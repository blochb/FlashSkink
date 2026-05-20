using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Identity;

/// <summary>
/// Reads and writes the encrypted witness file (<c>_witness/current.enc</c>) on a tail.
/// One instance per volume; constructed with an <see cref="ILogger{T}"/> only — the DEK
/// and the target <see cref="IStorageProvider"/> are per-call parameters so the same
/// <see cref="WitnessStore"/> serves every tail without holding key material
/// (Principle 31). Dev plan §3.5.1, Blueprint §19.6 (added in dev plan §3.5.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read semantics — every "no usable witness" condition maps to <c>Ok(null)</c>.</strong>
/// A tail that is offline, has no witness file, has a corrupted file, has a file
/// encrypted with a different DEK, or has a non-JSON plaintext — all return
/// <c>Result&lt;WitnessPayload?&gt;.Ok(null)</c>. The session-begin handshake in §3.5.2
/// treats every <c>null</c> result identically: this tail provides no conflict signal.
/// Only programming-level failures (<see cref="ErrorCode.Cancelled"/>,
/// <see cref="ErrorCode.Unknown"/>) escape as outer <c>Fail</c>.
/// </para>
/// <para>
/// <strong>Write semantics — Principle 17.</strong> The cancellation token <c>ct</c> is
/// observed exactly once, at method entry, via <see cref="CancellationToken.ThrowIfCancellationRequested"/>.
/// Every subsequent await inside <see cref="WriteAsync"/> uses <see cref="CancellationToken.None"/>
/// spelled out as a literal: once we decide to begin the upload session, abandoning it
/// mid-flight would leave the tail with a half-written witness whose ownership no future
/// session can claim. The <c>finally</c> block calls <c>AbortUploadAsync</c> on any session
/// the write opened but did not finalise, releasing the provider-side staging slot.
/// </para>
/// </remarks>
internal sealed class WitnessStore
{
    /// <summary>
    /// The well-known remote name passed to <see cref="IStorageProvider.BeginUploadAsync"/>
    /// when writing the witness. The corresponding <c>remoteId</c> returned by
    /// <see cref="IStorageProvider.FinaliseUploadAsync"/> is provider-specific (a relative
    /// path for <c>FileSystemProvider</c>, an opaque object id for cloud providers).
    /// Reads use <see cref="IStorageProvider.ListAsync"/> with the <c>"_witness/"</c>
    /// prefix and use the returned id, not this constant, for <see cref="IStorageProvider.DownloadAsync"/>.
    /// </summary>
    internal const string WitnessRemoteName = "_witness/current.enc";

    /// <summary>The <see cref="IStorageProvider.ListAsync"/> prefix used by <see cref="TryReadAsync"/>.</summary>
    private const string WitnessRemotePrefix = "_witness/";

    /// <summary>
    /// The set of provider-side error codes that <see cref="TryReadAsync"/> treats as
    /// "tail offline, no information available" rather than propagating up. The same set
    /// applies to both the list and the download step — a tail that goes offline between
    /// the two steps is handled identically.
    /// </summary>
    private static readonly HashSet<ErrorCode> OfflineErrorCodes =
    [
        ErrorCode.ProviderUnreachable,
        ErrorCode.ProviderAuthFailed,
        ErrorCode.TokenExpired,
        ErrorCode.TokenRevoked,
        ErrorCode.TokenRefreshFailed,
    ];

    private readonly ILogger<WitnessStore> _logger;

    /// <summary>
    /// Creates a <see cref="WitnessStore"/>. The logger is the only injected dependency;
    /// every other input — DEK, provider, payload — is per-call.
    /// </summary>
    internal WitnessStore(ILogger<WitnessStore> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempts to read and decrypt the witness file from <paramref name="provider"/>.
    /// </summary>
    /// <param name="provider">The tail to read from.</param>
    /// <param name="dek">The 32-byte DEK used to decrypt the envelope.</param>
    /// <param name="ct">Cancellation token; observed at entry and forwarded to the provider.</param>
    /// <returns>
    /// <see cref="Result{T}.Ok(T)"/> with the parsed payload on success, or
    /// <see cref="Result{T}.Ok(T)"/> with <see langword="null"/> when the tail has no
    /// usable witness (offline / absent / corrupted / wrong DEK / unparseable JSON).
    /// <see cref="Result{T}.Fail(ErrorContext)"/> only on cancellation or truly unexpected errors.
    /// </returns>
    internal async Task<Result<WitnessPayload?>> TryReadAsync(
        IStorageProvider provider,
        ReadOnlyMemory<byte> dek,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // Step 1 — locate the witness object on this tail. ListAsync returns
            // provider-assigned remote IDs (a relative path on FileSystemProvider, an
            // opaque object key on a cloud provider). Reading via List → Download avoids
            // baking any path convention into this layer.
            var listResult = await provider.ListAsync(WitnessRemotePrefix, ct).ConfigureAwait(false);
            if (!listResult.Success)
            {
                if (OfflineErrorCodes.Contains(listResult.Error!.Code))
                {
                    _logger.LogDebug(
                        "Tail {ProviderId} unavailable for witness read ({Code}); treating as no information.",
                        provider.ProviderID, listResult.Error.Code);
                    return Result<WitnessPayload?>.Ok(null);
                }
                return Result<WitnessPayload?>.Fail(listResult.Error);
            }

            var remoteIds = listResult.Value!;
            if (remoteIds.Count == 0)
            {
                // No witness file on this tail — first use, or witness was deleted. Either
                // way, no information is available; the handshake treats this as "no
                // conflict from this tail" rather than a fault.
                return Result<WitnessPayload?>.Ok(null);
            }

            if (remoteIds.Count > 1)
            {
                // Should not happen — WriteAsync deletes existing witnesses before writing
                // a new one. Defensive: take the first id and log so we know it occurred.
                _logger.LogWarning(
                    "Tail {ProviderId} has {Count} witness files; expected at most 1. Using first ({RemoteId}).",
                    provider.ProviderID, remoteIds.Count, remoteIds[0]);
            }

            var remoteId = remoteIds[0];

            // Step 2 — download the encrypted envelope. The witness is small (~250 bytes)
            // so the stream is fully read into memory; this is the cleanest pattern for a
            // single short read and avoids any partial-decrypt edge cases.
            var dlResult = await provider.DownloadAsync(remoteId, ct).ConfigureAwait(false);
            if (!dlResult.Success)
            {
                if (OfflineErrorCodes.Contains(dlResult.Error!.Code))
                {
                    _logger.LogDebug(
                        "Tail {ProviderId} unavailable for witness download ({Code}); treating as no information.",
                        provider.ProviderID, dlResult.Error.Code);
                    return Result<WitnessPayload?>.Ok(null);
                }
                // BlobNotFound between List and Download (rare race — witness deleted
                // mid-handshake): treat as absent.
                if (dlResult.Error.Code == ErrorCode.BlobNotFound)
                {
                    return Result<WitnessPayload?>.Ok(null);
                }
                return Result<WitnessPayload?>.Fail(dlResult.Error);
            }

            byte[] envelope;
            await using (var stream = dlResult.Value!)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
                envelope = ms.ToArray();
            }

            // Step 3 — decrypt. A failure here means a corrupted file, a partial write
            // (truncated envelope), a wrong DEK (same VolumeID space cannot occur with our
            // ciphertext-binding-by-key guarantee, but defensive), or a bad version byte.
            // All are "no usable witness" — the handshake proceeds without this tail's
            // information.
            if (!WitnessCrypto.TryDecrypt(envelope, dek.Span, out var plaintext))
            {
                _logger.LogWarning(
                    "Witness on tail {ProviderId} failed AES-GCM decryption; treating as no information.",
                    provider.ProviderID);
                return Result<WitnessPayload?>.Ok(null);
            }

            // Step 4 — parse JSON. Forgiving: missing fields default, unknown fields are
            // ignored. Only a hard JSON-shape failure returns false here.
            if (!WitnessPayload.TryParse(plaintext, out var payload))
            {
                _logger.LogWarning(
                    "Witness on tail {ProviderId} decrypted but the JSON did not parse; treating as no information.",
                    provider.ProviderID);
                return Result<WitnessPayload?>.Ok(null);
            }

            return Result<WitnessPayload?>.Ok(payload);
        }
        catch (OperationCanceledException ex)
        {
            return Result<WitnessPayload?>.Fail(
                ErrorCode.Cancelled, "Witness read was cancelled.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Unexpected error reading witness on tail {ProviderId}.", provider.ProviderID);
            return Result<WitnessPayload?>.Fail(
                ErrorCode.Unknown,
                $"Unexpected error reading witness on tail '{provider.ProviderID}'.", ex);
        }
    }

    /// <summary>
    /// Writes a fresh witness to <paramref name="provider"/>, replacing any existing
    /// <c>_witness/</c> objects. The write is the atomic-commit step of the session-begin
    /// handshake — see Principle 17 note in the type-level remarks.
    /// </summary>
    /// <param name="provider">The tail to write to.</param>
    /// <param name="dek">The 32-byte DEK used to encrypt the envelope.</param>
    /// <param name="payload">The witness payload to serialize and encrypt.</param>
    /// <param name="ct">Cancellation observed only at method entry; subsequent awaits use <see cref="CancellationToken.None"/>.</param>
    internal async Task<Result> WriteAsync(
        IStorageProvider provider,
        ReadOnlyMemory<byte> dek,
        WitnessPayload payload,
        CancellationToken ct)
    {
        UploadSession? session = null;
        bool finalised = false;

        try
        {
            ct.ThrowIfCancellationRequested();

            // Step 1 — best-effort cleanup of any pre-existing witnesses. Explicit deletes
            // keep the "at most one witness per tail" invariant on every provider type and
            // give the subsequent upload-session triplet a clean destination. Note that
            // FileSystemProvider.FinaliseUploadAsync refuses to overwrite an existing
            // destination file (it uses `File.Move(.., overwrite: false)` per §13.4 step 6
            // semantics), so a delete failure that leaves the old witness on disk WILL
            // surface as a later StagingFailed with InnerErrorCode=UploadFailed and the
            // message "Destination file already exists on tail." Cloud providers in Phase 4
            // will behave differently again — some accept overwrite as a new object, others
            // require the delete first. The cleanup is best-effort because (a) on the common
            // happy path it succeeds, and (b) the practical blast radius of a delete failure
            // is small: a process that cannot delete a file in this directory usually cannot
            // write to it either, so the upload would fail regardless. The log message at the
            // failure site below is intentionally not promising a successful overwrite.
            var listResult = await provider.ListAsync(
                WitnessRemotePrefix, CancellationToken.None).ConfigureAwait(false);
            if (listResult.Success)
            {
                foreach (var existingId in listResult.Value!)
                {
                    var deleteResult = await provider.DeleteAsync(
                        existingId, CancellationToken.None).ConfigureAwait(false);
                    if (!deleteResult.Success)
                    {
                        _logger.LogDebug(
                            "Best-effort delete of existing witness {RemoteId} on tail {ProviderId} failed ({Code}); subsequent upload may fail if the destination still exists.",
                            existingId, provider.ProviderID, deleteResult.Error!.Code);
                    }
                }
            }
            else
            {
                _logger.LogDebug(
                    "Pre-write list of witness prefix on tail {ProviderId} failed ({Code}); subsequent upload may fail if a stale witness still exists.",
                    provider.ProviderID, listResult.Error!.Code);
            }

            // Step 2 — encrypt the payload into the envelope. Synchronous, no I/O.
            var envelope = WitnessCrypto.Encrypt(payload.SerializeUtf8(), dek.Span);

            // Step 3 — open the upload session. Failure here means we never had a session
            // to abort; return immediately with a clear error.
            var beginResult = await provider.BeginUploadAsync(
                WitnessRemoteName, envelope.Length, CancellationToken.None).ConfigureAwait(false);
            if (!beginResult.Success)
            {
                _logger.LogWarning(
                    "Witness BeginUpload on tail {ProviderId} failed: {Code}.",
                    provider.ProviderID, beginResult.Error!.Code);
                return Result.Fail(new ErrorContext
                {
                    Code = ErrorCode.StagingFailed,
                    Message = $"Failed to begin witness upload on tail '{provider.ProviderID}'.",
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ProviderID"] = provider.ProviderID,
                        ["InnerErrorCode"] = beginResult.Error.Code.ToString(),
                    },
                });
            }
            session = beginResult.Value!;

            // Step 4 — upload the envelope as a single range (it fits in well under the
            // 4 MiB range size). Failure here triggers the finally-block abort below.
            var rangeResult = await provider.UploadRangeAsync(
                session, 0, envelope, CancellationToken.None).ConfigureAwait(false);
            if (!rangeResult.Success)
            {
                _logger.LogWarning(
                    "Witness UploadRange on tail {ProviderId} failed: {Code}.",
                    provider.ProviderID, rangeResult.Error!.Code);
                return Result.Fail(new ErrorContext
                {
                    Code = ErrorCode.StagingFailed,
                    Message = $"Failed to upload witness range on tail '{provider.ProviderID}'.",
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ProviderID"] = provider.ProviderID,
                        ["InnerErrorCode"] = rangeResult.Error.Code.ToString(),
                    },
                });
            }

            // Step 5 — finalise. After this returns Ok the session is closed on the
            // provider side; the finally block's abort becomes a no-op.
            var finResult = await provider.FinaliseUploadAsync(
                session, CancellationToken.None).ConfigureAwait(false);
            if (!finResult.Success)
            {
                _logger.LogWarning(
                    "Witness FinaliseUpload on tail {ProviderId} failed: {Code}.",
                    provider.ProviderID, finResult.Error!.Code);
                return Result.Fail(new ErrorContext
                {
                    Code = ErrorCode.StagingFailed,
                    Message = $"Failed to finalise witness upload on tail '{provider.ProviderID}'.",
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ProviderID"] = provider.ProviderID,
                        ["InnerErrorCode"] = finResult.Error.Code.ToString(),
                    },
                });
            }

            finalised = true;
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            // Can only fire from the entry-point ThrowIfCancellationRequested — every
            // other await above uses CancellationToken.None as a literal (Principle 17).
            return Result.Fail(ErrorCode.Cancelled, "Witness write was cancelled at entry.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Unexpected error writing witness on tail {ProviderId}.", provider.ProviderID);
            return Result.Fail(
                ErrorCode.Unknown,
                $"Unexpected error writing witness on tail '{provider.ProviderID}'.", ex);
        }
        finally
        {
            // Release any session we opened but did not finalise. Best-effort, never throws.
            if (session is not null && !finalised)
            {
                try
                {
                    var abortResult = await provider.AbortUploadAsync(
                        session, CancellationToken.None).ConfigureAwait(false);
                    if (!abortResult.Success)
                    {
                        _logger.LogDebug(
                            "Best-effort abort of orphaned witness session on tail {ProviderId} did not succeed ({Code}).",
                            provider.ProviderID, abortResult.Error!.Code);
                    }
                }
                catch (Exception abortEx)
                {
                    _logger.LogDebug(
                        abortEx, "Best-effort abort of orphaned witness session on tail {ProviderId} threw.",
                        provider.ProviderID);
                }
            }
        }
    }
}
