using Google.Apis.Drive.v3;

namespace FlashSkink.Core.Providers.GoogleDrive;

/// <summary>
/// Disposable container for the per-volume Google Drive SDK plumbing. Owns both the SDK's
/// <see cref="DriveService"/> and a companion <see cref="HttpClient"/> used for the resumable-upload
/// PUTs that bypass the SDK's <c>MediaUpload</c> helper (which insists on running the full upload
/// as one operation rather than handing single ranges back to <c>RangeUploader</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lifetime.</strong> Constructed by <see cref="IGoogleDriveClientFactory.Create"/>; held
/// by exactly one <see cref="GoogleDriveProvider"/>; disposed by the provider's
/// <see cref="IAsyncDisposable.DisposeAsync"/> when the volume's
/// <c>BrainBackedProviderRegistry</c> tears down.
/// </para>
/// <para>
/// <strong>Auth.</strong> Both clients share the same auth context (the SDK's
/// <c>UserCredential</c>); the companion <see cref="ResumableUploadClient"/> injects
/// <c>Authorization: Bearer {access_token}</c> on every request via a delegating handler that asks
/// the user credential for a fresh token (refreshing if needed). Refresh failure propagates as a
/// <see cref="System.Net.Http.HttpRequestException"/> from the call site.
/// </para>
/// </remarks>
internal sealed class GoogleDriveClientBundle : IDisposable
{
    /// <summary>SDK service for metadata operations: <c>Files.List</c>, <c>Files.Get</c>, <c>Files.Delete</c>, <c>About.Get</c>.</summary>
    public required DriveService DriveService { get; init; }

    /// <summary>HTTP client carrying the auth-injecting delegating handler. Used for raw resumable-upload PUTs against the session URI returned by <c>files?uploadType=resumable</c>.</summary>
    public required HttpClient ResumableUploadClient { get; init; }

    private int _disposed;

    /// <summary>Idempotently disposes both the SDK service and the companion HTTP client.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Best-effort, in defined order: companion HTTP first (cancels any in-flight PUTs), then SDK.
        try { ResumableUploadClient.Dispose(); } catch { /* swallow */ }
        try { DriveService.Dispose(); } catch { /* swallow */ }
    }
}
