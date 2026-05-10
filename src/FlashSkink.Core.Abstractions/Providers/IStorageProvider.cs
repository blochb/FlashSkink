using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Abstractions.Providers;

/// <summary>
/// Provider-agnostic contract for resumable, session-based upload, download, and health-probing
/// of the encrypted blobs and brain-mirror objects FlashSkink replicates to a tail. Blueprint §10.
/// </summary>
/// <remarks>
/// <para>
/// This contract is <strong>frozen</strong> after V1 ships (Principle 23). New providers may be
/// added; method signatures may not change. Additive capability interfaces (e.g.
/// <c>ISupportsRemoteHashCheck</c>, §3.3) are the approved evolution mechanism.
/// </para>
/// <para>
/// Every method returns <see cref="Result"/> or <see cref="Result{T}"/>; no method throws across
/// this boundary (Principle 1). The caller is responsible for observing cancellation before
/// calling — implementations check <c>ct.ThrowIfCancellationRequested()</c> at entry and map
/// <see cref="OperationCanceledException"/> to <see cref="ErrorCode.Cancelled"/>.
/// </para>
/// </remarks>
public interface IStorageProvider
{
    /// <summary>Stable, opaque provider identifier matching the <c>Providers.ProviderID</c> brain row.</summary>
    string ProviderID { get; }

    /// <summary>Machine-readable provider type token, e.g. <c>"filesystem"</c>, <c>"google-drive"</c>.</summary>
    string ProviderType { get; }

    /// <summary>Human-readable display name, e.g. <c>"External drive (E:\\)"</c>.</summary>
    string DisplayName { get; }

    // ── Upload session lifecycle (blueprint §10.1, §15.3) ────────────────────────────────────

    /// <summary>
    /// Opens a new resumable upload session for an object named <paramref name="remoteName"/>
    /// of total encrypted size <paramref name="totalBytes"/>. Returns an <see cref="UploadSession"/>
    /// whose <see cref="UploadSession.SessionUri"/> is the provider-returned session identifier.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="UploadSession"/> has <see cref="UploadSession.FileID"/> and
    /// <see cref="UploadSession.ProviderID"/> set to <see cref="string.Empty"/>; the caller stamps
    /// them before persisting. <see cref="UploadSession.ExpiresAt"/> is <see cref="DateTimeOffset.MaxValue"/>
    /// for providers whose sessions never expire (e.g. <c>FileSystemProvider</c>).
    /// </remarks>
    /// <param name="remoteName">Provider-side object name; does not include shard path prefixes.</param>
    /// <param name="totalBytes">Total encrypted bytes the provider should expect.</param>
    /// <param name="ct">Cancellation token; maps to <see cref="ErrorCode.Cancelled"/> on cancellation.</param>
    Task<Result<UploadSession>> BeginUploadAsync(string remoteName, long totalBytes, CancellationToken ct);

    /// <summary>
    /// Returns the number of bytes the provider has confirmed receiving for the given
    /// <paramref name="session"/>. Returns <c>0</c> when the session's staging area no longer
    /// exists (e.g. the tail was cleaned). The caller interprets this as a restart signal.
    /// </summary>
    Task<Result<long>> GetUploadedBytesAsync(UploadSession session, CancellationToken ct);

    /// <summary>
    /// Uploads <paramref name="data"/> starting at <paramref name="offset"/> within the object.
    /// Returns <see cref="ErrorCode.UploadSessionExpired"/> when the provider signals the session
    /// is no longer valid; the caller must call <see cref="BeginUploadAsync"/> to restart.
    /// </summary>
    /// <param name="session">Active session returned by <see cref="BeginUploadAsync"/>.</param>
    /// <param name="offset">Byte offset within the object at which <paramref name="data"/> starts.</param>
    /// <param name="data">Up to <c>UploadConstants.RangeSize</c> (4 MiB) of encrypted bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result> UploadRangeAsync(UploadSession session, long offset, ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>
    /// Finalises the upload, committing the staged object at the provider side.
    /// Returns the stable <c>remoteId</c> string that identifies this object for
    /// <see cref="DownloadAsync"/>, <see cref="DeleteAsync"/>, and <see cref="ExistsAsync"/>.
    /// </summary>
    Task<Result<string>> FinaliseUploadAsync(UploadSession session, CancellationToken ct);

    /// <summary>
    /// Best-effort cancellation of an in-progress upload. Deletes any provider-side staging
    /// artefacts. Idempotent; returns <see cref="Result.Ok()"/> if the session is already gone.
    /// </summary>
    Task<Result> AbortUploadAsync(UploadSession session, CancellationToken ct);

    // ── Download / existence / delete ────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a read-only <see cref="Stream"/> over the object identified by <paramref name="remoteId"/>.
    /// The caller is responsible for disposing the returned stream.
    /// Returns <see cref="ErrorCode.BlobNotFound"/> when no object with that id exists.
    /// </summary>
    Task<Result<Stream>> DownloadAsync(string remoteId, CancellationToken ct);

    /// <summary>
    /// Deletes the object identified by <paramref name="remoteId"/>. Idempotent — missing object
    /// is treated as success.
    /// </summary>
    Task<Result> DeleteAsync(string remoteId, CancellationToken ct);

    /// <summary>Returns <see langword="true"/> when the object identified by <paramref name="remoteId"/> exists on the provider.</summary>
    Task<Result<bool>> ExistsAsync(string remoteId, CancellationToken ct);

    // ── Listing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the remote IDs of all objects whose remote name begins with <paramref name="prefix"/>.
    /// Used by <c>BrainMirrorService</c> (§3.5) to enumerate <c>_brain/</c> entries for rolling
    /// retention, and by recovery (Phase 5) to locate backups.
    /// </summary>
    /// <param name="prefix">Case-sensitive name prefix, e.g. <c>"_brain"</c> or <c>"blobs"</c>. Empty string lists everything.</param>
    Task<Result<IReadOnlyList<string>>> ListAsync(string prefix, CancellationToken ct);

    // ── Health and capacity ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs a live health probe — writes a probe object, reads it, deletes it — and returns
    /// the result. <c>HealthMonitorService</c> (Phase 5) drives the cadence; this method is
    /// the single integration point.
    /// </summary>
    Task<Result<ProviderHealth>> CheckHealthAsync(CancellationToken ct);

    /// <summary>Returns the number of bytes currently consumed on this provider, including all objects written by this and other volumes sharing the same account or path.</summary>
    Task<Result<long>> GetUsedBytesAsync(CancellationToken ct);

    /// <summary>Returns the total storage quota in bytes, or <see langword="null"/> when the quota is unknown or unlimited.</summary>
    Task<Result<long?>> GetQuotaBytesAsync(CancellationToken ct);
}
