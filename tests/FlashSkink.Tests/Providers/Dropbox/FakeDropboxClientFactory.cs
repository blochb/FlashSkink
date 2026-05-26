using Dropbox.Api;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.Dropbox;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Tests.Providers.Dropbox;

/// <summary>
/// Test-support <see cref="IDropboxClientFactory"/> that constructs a real
/// <see cref="DropboxClient"/> bound to a <see cref="RecordingHttpMessageHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// The DropboxClient is constructed via the <c>(oauth2AccessToken, DropboxClientConfig)</c>
/// overload — single-access-token mode, no SDK-internal /oauth2/token refresh. This keeps tests
/// focused on a single recorded request per provider call without needing to first stub a token
/// refresh round-trip. The recorded BYOC credentials (appKey/appSecret/refreshToken) are still
/// captured for assertions but are not used to drive the SDK's authentication path in tests.
/// </para>
/// </remarks>
internal sealed class FakeDropboxClientFactory : IDropboxClientFactory
{
    public RecordingHttpMessageHandler Handler { get; } = new();

    /// <summary>If set, <see cref="Create"/> returns this result instead of constructing a bundle.</summary>
    public Result<DropboxClientBundle>? CreateResultOverride { get; set; }

    public int CreateCallCount { get; private set; }
    public string? LastAppKey { get; private set; }
    public string? LastAppSecret { get; private set; }
    public string? LastRefreshToken { get; private set; }

    public Result<DropboxClientBundle> Create(
        string appKey, string appSecret, string refreshToken, ILoggerFactory loggerFactory)
    {
        CreateCallCount++;
        LastAppKey = appKey;
        LastAppSecret = appSecret;
        LastRefreshToken = refreshToken;

        if (CreateResultOverride is not null)
        {
            return CreateResultOverride.Value;
        }

        var http = new HttpClient(Handler, disposeHandler: false);
        var config = new DropboxClientConfig("FlashSkink-Test") { HttpClient = http };
        // Single-access-token mode — no refresh chatter. Token value is opaque to the SDK and
        // the recorder; we never assert on it.
        var client = new DropboxClient("fake-access-token", config);

        return Result<DropboxClientBundle>.Ok(new DropboxClientBundle
        {
            Client = client,
            HttpClient = http,
        });
    }
}
