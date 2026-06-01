using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.OneDrive;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Tests.Providers.OneDrive;

/// <summary>
/// Test <see cref="IOneDriveClientFactory"/> that wires both the authed and plain clients onto a
/// single shared <see cref="RecordingHttpMessageHandler"/>, so a test can script and assert every
/// Graph, token-endpoint, and pre-signed range request through one recorder. The authed client is
/// wrapped in the real <see cref="MicrosoftAuthDelegatingHandler"/> so the 401-refresh-retry path is
/// exercised end-to-end; the plain client talks to the recorder directly (no auth handler), matching
/// production's pre-signed range PUTs.
/// </summary>
internal sealed class FakeOneDriveClientFactory : IOneDriveClientFactory
{
    private readonly RecordingHttpMessageHandler _handler;

    public FakeOneDriveClientFactory(RecordingHttpMessageHandler handler)
    {
        _handler = handler;
    }

    public Result<OneDriveClientBundle> Create(string clientId, string clientSecret, string refreshToken, ILoggerFactory loggerFactory)
    {
        var authHandler = new MicrosoftAuthDelegatingHandler(
            clientId,
            clientSecret,
            refreshToken,
            loggerFactory.CreateLogger<MicrosoftAuthDelegatingHandler>())
        {
            InnerHandler = _handler,
        };

        // disposeHandler:false — the test owns the shared recorder's lifetime; neither client may
        // dispose it (it is referenced by both the authed pipeline and the plain client).
        var authed = new HttpClient(authHandler, disposeHandler: false);
        var plain = new HttpClient(_handler, disposeHandler: false);

        return Result<OneDriveClientBundle>.Ok(new OneDriveClientBundle
        {
            AuthedClient = authed,
            PlainClient = plain,
        });
    }
}
