using Dropbox.Api;

namespace FlashSkink.Core.Providers.Dropbox;

/// <summary>
/// Disposable wrapper carrying the SDK's <see cref="DropboxClient"/> and the per-volume
/// <see cref="HttpClient"/> we configured the SDK with. Owns both. The bundle becomes the
/// property of the constructed <see cref="DropboxProvider"/>, which disposes it on
/// <see cref="IAsyncDisposable.DisposeAsync"/>.
/// </summary>
internal sealed class DropboxClientBundle : IDisposable
{
    public required DropboxClient Client { get; init; }

    /// <summary>The HTTP client supplied to <c>DropboxClientConfig.HttpClient</c>. Disposed by this bundle.</summary>
    public required HttpClient HttpClient { get; init; }

    public void Dispose()
    {
        try
        {
            Client.Dispose();
        }
        catch
        {
            // Best-effort dispose; swallow any SDK-internal disposal noise.
        }
        try
        {
            HttpClient.Dispose();
        }
        catch
        {
            // Best-effort dispose.
        }
    }
}
