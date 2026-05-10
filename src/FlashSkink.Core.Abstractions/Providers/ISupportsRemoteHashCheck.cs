using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Abstractions.Providers;

/// <summary>
/// Optional capability interface declaring that an <see cref="IStorageProvider"/> can compute
/// the XXHash64 of a finalised remote object server-side. The upload pipeline (<c>RangeUploader</c>,
/// §3.3) casts to this interface during the §15.7 verification step; providers that do not
/// implement it skip the post-finalisation hash check and rely on the GCM authentication tag
/// inside the encrypted blob (Principle 6) as the sole authenticator.
/// </summary>
/// <remarks>
/// <para>
/// Capability-interface evolution is the approved mechanism for extending provider behaviour
/// without modifying the frozen <see cref="IStorageProvider"/> contract (Principle 23).
/// </para>
/// <para>
/// Implemented in V1 by <c>FileSystemProvider</c> (which can re-read the finalised file and
/// compute XXHash64 directly). Cloud providers that arrive in Phase 4 — Google Drive, Dropbox,
/// OneDrive — use their own per-provider verification paths (e.g. comparing native MD5 or
/// content-hash) and do <i>not</i> implement this interface.
/// </para>
/// </remarks>
public interface ISupportsRemoteHashCheck
{
    /// <summary>
    /// Reads the finalised remote object identified by <paramref name="remoteId"/> and returns
    /// its XXHash64. The caller compares the returned value against the local
    /// <c>Blobs.EncryptedXXHash</c> recorded at write time.
    /// </summary>
    /// <param name="remoteId">Provider-side identifier returned by <see cref="IStorageProvider.FinaliseUploadAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="ErrorCode.BlobNotFound"/> when no object exists for <paramref name="remoteId"/>;
    /// <see cref="ErrorCode.ProviderUnreachable"/> on transient transport failures; the
    /// 64-bit XXHash64 of the remote bytes on success.
    /// </returns>
    Task<Result<ulong>> GetRemoteXxHash64Async(string remoteId, CancellationToken ct);
}
