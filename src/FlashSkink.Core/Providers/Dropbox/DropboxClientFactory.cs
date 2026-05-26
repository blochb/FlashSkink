using Dropbox.Api;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers.Dropbox;

/// <summary>
/// Production <see cref="IDropboxClientFactory"/>. Constructs a <see cref="DropboxClient"/> seeded
/// with the refresh token; the SDK manages access-token refresh internally.
/// </summary>
internal sealed class DropboxClientFactory : IDropboxClientFactory
{
    private const string UserAgent = "FlashSkink/1.0";

    // 5-minute per-call timeout — generous enough for a 4 MiB upload on a slow connection, tight
    // enough to surface a stuck network as Dropbox.Api.HttpException → ProviderUnreachable.
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(5);

    /// <inheritdoc/>
    public Result<DropboxClientBundle> Create(
        string appKey,
        string appSecret,
        string refreshToken,
        ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrEmpty(appKey))
        {
            return Result<DropboxClientBundle>.Fail(
                ErrorCode.InvalidArgument, "Dropbox appKey is required.");
        }
        if (string.IsNullOrEmpty(appSecret))
        {
            return Result<DropboxClientBundle>.Fail(
                ErrorCode.InvalidArgument, "Dropbox appSecret is required.");
        }
        if (string.IsNullOrEmpty(refreshToken))
        {
            return Result<DropboxClientBundle>.Fail(
                ErrorCode.InvalidArgument, "Dropbox refreshToken is required.");
        }

        HttpClient? http = null;
        DropboxClient? client = null;
        try
        {
            http = new HttpClient { Timeout = HttpTimeout };
            var config = new DropboxClientConfig(UserAgent) { HttpClient = http };
            client = new DropboxClient(refreshToken, appKey, appSecret, config);

            return Result<DropboxClientBundle>.Ok(new DropboxClientBundle
            {
                Client = client,
                HttpClient = http,
            });
        }
        catch (Exception ex)
        {
            try { client?.Dispose(); } catch { /* swallow */ }
            try { http?.Dispose(); } catch { /* swallow */ }

            return Result<DropboxClientBundle>.Fail(
                ErrorCode.Unknown, "Failed to construct Dropbox SDK client.", ex);
        }
    }
}
