using System.Buffers;
using System.Globalization;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Abstractions.Time;
using FlashSkink.Core.Metadata;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Upload;

/// <summary>
/// Single-blob upload state machine. Walks one <c>(FileID, ProviderID)</c> through the
/// blueprint §15.3 lifecycle: resume-or-open session → range loop with §21.1 in-range retry →
/// finalise → §15.7 verify → return outcome.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Caller contract.</strong> The caller (<c>UploadQueueService</c>, §3.4) is responsible for:
/// <list type="bullet">
///   <item>calling <c>MarkUploadingAsync</c> before invoking <see cref="UploadAsync"/>;</item>
///   <item>resolving <c>blobAbsolutePath</c> from <c>blob.BlobPath</c> and the skink root;</item>
///   <item>looking up <c>existingSession</c> from <c>UploadSessions</c> (or passing <see langword="null"/>);</item>
///   <item>writing the brain transaction on <see cref="UploadOutcomeStatus.Completed"/>
///   (<c>MarkUploadedAsync</c> + <c>DeleteSessionAsync</c>) — §15.3 step 7c requires a single
///   transaction across the status flip and the session delete;</item>
///   <item>writing the brain transaction on <see cref="UploadOutcomeStatus.PermanentFailure"/>;</item>
///   <item>consulting <see cref="RetryPolicy.NextCycleAttempt"/> on <see cref="UploadOutcomeStatus.RetryableFailure"/>.</item>
/// </list>
/// </para>
/// <para>
/// <strong>RangeUploader's own surface</strong> is the §21.1 in-range ladder and the
/// <c>UploadSessions</c> bookkeeping: it inserts/upserts the session row at open
/// (<c>GetOrCreateSessionAsync</c>), advances <c>BytesUploaded</c> on each provider-confirmed
/// range (<c>UpdateSessionProgressAsync</c>), and deletes the row when restarting from
/// session-expired (<c>DeleteSessionAsync</c>). It never advances the row past what the
/// provider has confirmed (Principle 30 — crash-consistency invariant).
/// </para>
/// </remarks>
public sealed class RangeUploader
{
    private readonly UploadQueueRepository _uploadQueueRepository;
    private readonly IClock _clock;
    private readonly RetryPolicy _retryPolicy;
    private readonly ILogger<RangeUploader> _logger;

    /// <summary>Creates a <see cref="RangeUploader"/>. Stateless beyond its dependencies; safe to share across calls.</summary>
    public RangeUploader(
        UploadQueueRepository uploadQueueRepository,
        IClock clock,
        RetryPolicy retryPolicy,
        ILogger<RangeUploader> logger)
    {
        _uploadQueueRepository = uploadQueueRepository;
        _clock = clock;
        _retryPolicy = retryPolicy;
        _logger = logger;
    }

    /// <summary>
    /// Uploads <paramref name="blob"/>'s ciphertext from <paramref name="blobAbsolutePath"/> to
    /// <paramref name="provider"/>, resuming from <paramref name="existingSession"/> when present.
    /// Returns an <see cref="UploadOutcome"/> describing the per-blob result.
    /// </summary>
    /// <returns>
    /// <list type="bullet">
    ///   <item><see cref="Result{T}.Ok"/> with <see cref="UploadOutcomeStatus.Completed"/>:
    ///   the remote object is finalised and (when supported) verified;</item>
    ///   <item><see cref="Result{T}.Ok"/> with <see cref="UploadOutcomeStatus.RetryableFailure"/>:
    ///   the in-range retry ladder escalated; the caller should consult
    ///   <see cref="RetryPolicy.NextCycleAttempt"/>;</item>
    ///   <item><see cref="Result{T}.Ok"/> with <see cref="UploadOutcomeStatus.PermanentFailure"/>:
    ///   an unrecoverable failure (auth, quota, hash mismatch, local blob corrupt);</item>
    ///   <item><see cref="Result{T}.Fail"/>: cancellation, brain bookkeeping failure, or an
    ///   unhandled I/O exception. The caller treats this as a transient failure of the call
    ///   itself, not as a per-blob outcome — the <c>UploadSessions</c> row is preserved so the
    ///   next call can resume.</item>
    /// </list>
    /// </returns>
    public async Task<Result<UploadOutcome>> UploadAsync(
        string fileId,
        string providerId,
        IStorageProvider provider,
        BlobRecord blob,
        string blobAbsolutePath,
        UploadSessionRow? existingSession,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // ── Step 1 — Resume or open session ──────────────────────────────────────────────
            var resumeResult = await ResumeOrOpenSessionAsync(
                fileId, providerId, provider, blob, existingSession, ct).ConfigureAwait(false);
            if (!resumeResult.Success)
            {
                return Result<UploadOutcome>.Fail(resumeResult.Error!);
            }

            var session = resumeResult.Value.Session;
            long bytesUploaded = resumeResult.Value.StartOffset;

            // Resumed exactly at completion (crash between provider's last range ack and our finalise).
            if (bytesUploaded >= blob.EncryptedSize)
            {
                return await FinaliseAndVerifyAsync(fileId, providerId, provider, blob, session, ct)
                    .ConfigureAwait(false);
            }

            // ── Step 2 — Open the local blob file ────────────────────────────────────────────
            await using var stream = new FileStream(
                blobAbsolutePath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });

            if (stream.Length != blob.EncryptedSize)
            {
                _logger.LogError(
                    "Local blob {BlobId} length mismatch: expected {Expected}, got {Actual}",
                    blob.BlobId, blob.EncryptedSize, stream.Length);
                return Result<UploadOutcome>.Ok(UploadOutcome.Permanent(
                    ErrorCode.BlobCorrupt,
                    "Local blob length differs from Blobs.EncryptedSize."));
            }

            stream.Seek(bytesUploaded, SeekOrigin.Begin);

            // ── Step 3 — Range loop with per-range retry ─────────────────────────────────────
            // Buffer holds ciphertext (already encrypted on disk per Phase 2). Content-clearing
            // on dispose is NOT required because ciphertext is not a secret — MemoryPool<byte>.Shared
            // (without ClearOnDispose) is the right pool here.
            using var bufferOwner = MemoryPool<byte>.Shared.Rent(UploadConstants.RangeSize);
            var fullBuffer = bufferOwner.Memory[..UploadConstants.RangeSize];

            while (bytesUploaded < blob.EncryptedSize)
            {
                ct.ThrowIfCancellationRequested();

                int rangeLen = (int)Math.Min(blob.EncryptedSize - bytesUploaded, UploadConstants.RangeSize);
                var rangeMemory = fullBuffer[..rangeLen];

                int totalRead = 0;
                while (totalRead < rangeLen)
                {
                    int read = await stream
                        .ReadAsync(rangeMemory.Slice(totalRead, rangeLen - totalRead), ct)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        return Result<UploadOutcome>.Ok(UploadOutcome.Permanent(
                            ErrorCode.BlobCorrupt,
                            "Local blob ended before EncryptedSize was reached."));
                    }
                    totalRead += read;
                }

                var step = await UploadOneRangeWithRetryAsync(
                    fileId, providerId, provider, session, bytesUploaded, rangeMemory, ct)
                    .ConfigureAwait(false);

                switch (step.Kind)
                {
                    case RangeStepKind.Success:
                        bytesUploaded += rangeLen;
                        var update = await _uploadQueueRepository
                            .UpdateSessionProgressAsync(fileId, providerId, bytesUploaded, ct)
                            .ConfigureAwait(false);
                        if (!update.Success)
                        {
                            return Result<UploadOutcome>.Fail(update.Error!);
                        }
                        session = session with { BytesUploaded = bytesUploaded };
                        break;

                    case RangeStepKind.SessionExpired:
                        // Best-effort cleanup uses CancellationToken.None — Principle 17.
                        await provider.AbortUploadAsync(session, CancellationToken.None).ConfigureAwait(false);
                        await _uploadQueueRepository
                            .DeleteSessionAsync(fileId, providerId, CancellationToken.None)
                            .ConfigureAwait(false);

                        var fresh = await OpenFreshSessionAsync(fileId, providerId, provider, blob, ct)
                            .ConfigureAwait(false);
                        if (!fresh.Success)
                        {
                            return Result<UploadOutcome>.Fail(fresh.Error!);
                        }

                        session = fresh.Value!;
                        bytesUploaded = 0;
                        stream.Seek(0, SeekOrigin.Begin);
                        break;

                    case RangeStepKind.PermanentFailure:
                    case RangeStepKind.RetryableFailureExhausted:
                        return Result<UploadOutcome>.Ok(step.FailureOutcome!);
                }
            }

            // ── Steps 4–5 — Finalise + verify ────────────────────────────────────────────────
            return await FinaliseAndVerifyAsync(fileId, providerId, provider, blob, session, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("Range upload cancelled for {FileId} on {ProviderId}", fileId, providerId);
            return Result<UploadOutcome>.Fail(
                ErrorCode.Cancelled, "Range upload was cancelled.", ex);
        }
        catch (FileNotFoundException ex)
        {
            // A missing local blob is permanent — every retry would hit the same exception and
            // burn through the cycle ladder for no reason. Return a permanent outcome so the
            // caller marks the row FAILED on the first attempt.
            _logger.LogError(ex, "Local blob file missing: {BlobPath}", blobAbsolutePath);
            return Result<UploadOutcome>.Ok(UploadOutcome.Permanent(
                ErrorCode.BlobCorrupt, "Local blob file is missing."));
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogError(ex, "Local blob directory missing: {BlobPath}", blobAbsolutePath);
            return Result<UploadOutcome>.Ok(UploadOutcome.Permanent(
                ErrorCode.BlobCorrupt, "Local blob directory is missing."));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied during range upload of {FileId}", fileId);
            return Result<UploadOutcome>.Fail(
                ErrorCode.ProviderUnreachable, "Access denied during range upload.", ex);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "I/O error during range upload of {FileId}", fileId);
            return Result<UploadOutcome>.Fail(
                ErrorCode.UploadFailed, "I/O error during range upload.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during range upload of {FileId}", fileId);
            return Result<UploadOutcome>.Fail(
                ErrorCode.Unknown, "Unexpected error during range upload.", ex);
        }
    }

    // ── Step 1 helper ────────────────────────────────────────────────────────────────────────

    private async Task<Result<SessionResume>> ResumeOrOpenSessionAsync(
        string fileId,
        string providerId,
        IStorageProvider provider,
        BlobRecord blob,
        UploadSessionRow? existingSession,
        CancellationToken ct)
    {
        if (existingSession is null)
        {
            var fresh = await OpenFreshSessionAsync(fileId, providerId, provider, blob, ct)
                .ConfigureAwait(false);
            return fresh.Success
                ? Result<SessionResume>.Ok(new SessionResume(fresh.Value!, 0))
                : Result<SessionResume>.Fail(fresh.Error!);
        }

        if (existingSession.SessionExpiresUtc <= _clock.UtcNow)
        {
            // Expired — best-effort cleanup using CancellationToken.None (Principle 17), then open fresh.
            var existingValue = ReconstructSession(existingSession);
            await provider.AbortUploadAsync(existingValue, CancellationToken.None).ConfigureAwait(false);
            await _uploadQueueRepository
                .DeleteSessionAsync(fileId, providerId, CancellationToken.None)
                .ConfigureAwait(false);

            var fresh = await OpenFreshSessionAsync(fileId, providerId, provider, blob, ct)
                .ConfigureAwait(false);
            return fresh.Success
                ? Result<SessionResume>.Ok(new SessionResume(fresh.Value!, 0))
                : Result<SessionResume>.Fail(fresh.Error!);
        }

        // Resume — reconcile against provider-reported bytes.
        var session = ReconstructSession(existingSession);
        var probe = await provider.GetUploadedBytesAsync(session, ct).ConfigureAwait(false);
        if (!probe.Success)
        {
            return Result<SessionResume>.Fail(probe.Error!);
        }

        long providerReported = probe.Value;
        long reconciled = Math.Min(providerReported, existingSession.BytesUploaded);

        if (reconciled != existingSession.BytesUploaded)
        {
            var update = await _uploadQueueRepository
                .UpdateSessionProgressAsync(fileId, providerId, reconciled, ct)
                .ConfigureAwait(false);
            if (!update.Success)
            {
                return Result<SessionResume>.Fail(update.Error!);
            }
        }

        var resumed = session with { BytesUploaded = reconciled };
        return Result<SessionResume>.Ok(new SessionResume(resumed, reconciled));
    }

    private async Task<Result<UploadSession>> OpenFreshSessionAsync(
        string fileId,
        string providerId,
        IStorageProvider provider,
        BlobRecord blob,
        CancellationToken ct)
    {
        string remoteName = blob.BlobId + ".bin";

        var begin = await provider.BeginUploadAsync(remoteName, blob.EncryptedSize, ct)
            .ConfigureAwait(false);
        if (!begin.Success)
        {
            return Result<UploadSession>.Fail(begin.Error!);
        }

        var session = begin.Value! with
        {
            FileID = fileId,
            ProviderID = providerId,
        };

        // SessionExpiresUtc storage is DateTime; UploadSession.ExpiresAt is DateTimeOffset.
        // DateTimeOffset.MaxValue → DateTime.MaxValue (the FileSystemProvider "never expires" sentinel).
        DateTime expiresUtc = session.ExpiresAt == DateTimeOffset.MaxValue
            ? DateTime.MaxValue
            : session.ExpiresAt.UtcDateTime;

        // BeginUploadAsync has committed a session at the provider; the row write must complete
        // even if ct is already signalled — losing the session URI makes the upload non-resumable.
        // CancellationToken.None per §6.7 / Principle 17.
        var persist = await _uploadQueueRepository
            .GetOrCreateSessionAsync(fileId, providerId, session.SessionUri, expiresUtc, session.TotalBytes, CancellationToken.None)
            .ConfigureAwait(false);
        if (!persist.Success)
        {
            return Result<UploadSession>.Fail(persist.Error!);
        }

        return Result<UploadSession>.Ok(session);
    }

    // ── Step 3 helper — per-range retry ladder (§21.1) ──────────────────────────────────────

    private async Task<RangeStepResult> UploadOneRangeWithRetryAsync(
        string fileId,
        string providerId,
        IStorageProvider provider,
        UploadSession session,
        long offset,
        ReadOnlyMemory<byte> rangeBytes,
        CancellationToken ct)
    {
        int rangeAttempt = 1;
        while (true)
        {
            var result = await provider.UploadRangeAsync(session, offset, rangeBytes, ct)
                .ConfigureAwait(false);

            if (result.Success)
            {
                return new RangeStepResult(RangeStepKind.Success, FailureOutcome: null);
            }

            var error = result.Error!;
            var code = error.Code;

            if (code == ErrorCode.UploadSessionExpired)
            {
                _logger.LogInformation(
                    "Provider session expired for {FileId} on {ProviderId} at offset {Offset}; restarting upload.",
                    fileId, providerId, offset);
                return new RangeStepResult(RangeStepKind.SessionExpired, FailureOutcome: null);
            }

            if (IsPermanent(code))
            {
                _logger.LogError(
                    "Permanent failure for {FileId} on {ProviderId} at offset {Offset}: {Code}",
                    fileId, providerId, offset, code);
                return new RangeStepResult(
                    RangeStepKind.PermanentFailure,
                    FailureOutcome: UploadOutcome.Permanent(code, error.Message));
            }

            // Retryable: consult the §21.1 in-range ladder.
            var decision = _retryPolicy.NextRangeAttempt(rangeAttempt);
            if (decision.Outcome == RetryOutcome.EscalateCycle)
            {
                _logger.LogWarning(
                    "In-range retry budget exhausted for {FileId} on {ProviderId} at offset {Offset}: {Code}. Escalating to cycle.",
                    fileId, providerId, offset, code);
                return new RangeStepResult(
                    RangeStepKind.RetryableFailureExhausted,
                    FailureOutcome: UploadOutcome.Retryable(code, error.Message));
            }

            _logger.LogWarning(
                "Range attempt {Attempt} failed for {FileId} on {ProviderId} at offset {Offset}: {Code}. Retrying after {Delay}.",
                rangeAttempt, fileId, providerId, offset, code, decision.Delay);

            await _clock.Delay(decision.Delay, ct).ConfigureAwait(false);
            rangeAttempt++;
        }
    }

    // ── Steps 4–5 helper — finalise + verify ────────────────────────────────────────────────

    private async Task<Result<UploadOutcome>> FinaliseAndVerifyAsync(
        string fileId,
        string providerId,
        IStorageProvider provider,
        BlobRecord blob,
        UploadSession session,
        CancellationToken ct)
    {
        var finalise = await provider.FinaliseUploadAsync(session, ct).ConfigureAwait(false);
        if (!finalise.Success)
        {
            var error = finalise.Error!;
            if (IsPermanent(error.Code))
            {
                return Result<UploadOutcome>.Ok(UploadOutcome.Permanent(error.Code, error.Message));
            }
            return Result<UploadOutcome>.Ok(UploadOutcome.Retryable(error.Code, error.Message));
        }

        string remoteId = finalise.Value!;

        // §15.7 verification — hash check if the provider supports it; otherwise trust the GCM tag.
        if (provider is ISupportsRemoteHashCheck hashCheck)
        {
            var hashResult = await hashCheck.GetRemoteXxHash64Async(remoteId, ct).ConfigureAwait(false);
            if (!hashResult.Success)
            {
                // Verification I/O failure — treat as retryable (the next cycle re-attempts).
                var error = hashResult.Error!;
                _logger.LogWarning(
                    "Remote hash check failed for {FileId} on {ProviderId} ({RemoteId}): {Code}",
                    fileId, providerId, remoteId, error.Code);
                return Result<UploadOutcome>.Ok(UploadOutcome.Retryable(error.Code, error.Message));
            }

            ulong remoteHash = hashResult.Value;
            ulong localHash = ParseEncryptedXxHash(blob.EncryptedXxHash);

            if (remoteHash != localHash)
            {
                _logger.LogError(
                    "Remote XXHash64 mismatch for {FileId} on {ProviderId}: local={Local:X16}, remote={Remote:X16}",
                    fileId, providerId, localHash, remoteHash);
                return Result<UploadOutcome>.Ok(UploadOutcome.Permanent(
                    ErrorCode.ChecksumMismatch,
                    "Remote XXHash64 does not match the local blob's recorded hash."));
            }
        }
        else
        {
            _logger.LogInformation(
                "Skipping remote-hash verification for {ProviderId}; provider does not support hash check.",
                providerId);
        }

        _logger.LogInformation(
            "Upload completed for {FileId} on {ProviderId}: {BytesUploaded} bytes → {RemoteId}",
            fileId, providerId, blob.EncryptedSize, remoteId);

        return Result<UploadOutcome>.Ok(UploadOutcome.Completed(remoteId, blob.EncryptedSize));
    }

    // ── Static helpers ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Codes the in-range retry ladder treats as permanent (no retry, no cycle escalation).
    /// All other failure codes returned by <see cref="IStorageProvider.UploadRangeAsync"/> are
    /// treated as retryable and consulted against <see cref="RetryPolicy.NextRangeAttempt"/>.
    /// </summary>
    private static bool IsPermanent(ErrorCode code) => code is
        ErrorCode.ProviderAuthFailed or
        ErrorCode.ProviderQuotaExceeded or
        ErrorCode.TokenRevoked or
        ErrorCode.ChecksumMismatch;

    /// <summary>
    /// Builds an <see cref="UploadSession"/> from a persisted <see cref="UploadSessionRow"/>.
    /// Session timestamps stored as <see cref="DateTime"/> are wrapped as
    /// <see cref="DateTimeOffset"/> with a <see cref="TimeSpan.Zero"/> offset (rows are written
    /// as UTC ISO-8601 by the repository).
    /// </summary>
    private static UploadSession ReconstructSession(UploadSessionRow row) => new()
    {
        FileID = row.FileId,
        ProviderID = row.ProviderId,
        SessionUri = row.SessionUri,
        ExpiresAt = ToDateTimeOffsetUtc(row.SessionExpiresUtc),
        BytesUploaded = row.BytesUploaded,
        TotalBytes = row.TotalBytes,
        LastActivityUtc = ToDateTimeOffsetUtc(row.LastActivityUtc),
    };

    private static DateTimeOffset ToDateTimeOffsetUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
        DateTimeKind.Unspecified => new DateTimeOffset(value, TimeSpan.Zero),
        _ => new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero),
    };

    /// <summary>
    /// Parses the 16-character lowercase hexadecimal XXHash64 string written by
    /// <c>WritePipeline</c> via <c>xxhash.ToString("x16")</c>.
    /// </summary>
    private static ulong ParseEncryptedXxHash(string hex) =>
        ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    // ── Internal types ───────────────────────────────────────────────────────────────────────

    private readonly record struct SessionResume(UploadSession Session, long StartOffset);

    private enum RangeStepKind
    {
        Success,
        SessionExpired,
        RetryableFailureExhausted,
        PermanentFailure,
    }

    private readonly record struct RangeStepResult(
        RangeStepKind Kind,
        UploadOutcome? FailureOutcome);
}
