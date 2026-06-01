using System.Threading;

namespace FlashSkink.Core.Providers.OneDrive;

/// <summary>
/// Owns the two <see cref="HttpClient"/> instances a OneDrive provider needs: one that injects and
/// refreshes the Microsoft identity bearer token, and a plain one for pre-signed range PUTs.
/// </summary>
internal sealed class OneDriveClientBundle : IDisposable
{
    /// <summary>Client that applies the Microsoft bearer token and refreshes it on 401.</summary>
    public required HttpClient AuthedClient { get; init; }

    /// <summary>Client with no auth handler, used for pre-authorised upload-session range PUTs.</summary>
    public required HttpClient PlainClient { get; init; }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            AuthedClient.Dispose();
        }
        catch
        {
            // Best-effort disposal; nothing actionable on failure.
        }

        try
        {
            PlainClient.Dispose();
        }
        catch
        {
            // Best-effort disposal; nothing actionable on failure.
        }
    }
}
