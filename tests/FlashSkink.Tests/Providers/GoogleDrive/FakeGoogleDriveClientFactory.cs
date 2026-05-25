using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.GoogleDrive;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Tests.Providers.GoogleDrive;

/// <summary>
/// Test-support <see cref="IGoogleDriveClientFactory"/> that constructs a real
/// <see cref="DriveService"/> bound to a recording <see cref="RecordingHttpMessageHandler"/>.
/// Two separate handlers: one for the SDK's HTTP client (metadata calls), one for the companion
/// HTTP client (raw resumable PUTs). Most tests only use one or the other.
/// </summary>
internal sealed class FakeGoogleDriveClientFactory : IGoogleDriveClientFactory
{
    /// <summary>Recording handler bound to the SDK's <c>DriveService.HttpClient</c>.</summary>
    public RecordingHttpMessageHandler SdkHandler { get; } = new();

    /// <summary>Recording handler bound to the companion HTTP client used for raw resumable PUTs.</summary>
    public RecordingHttpMessageHandler ResumableHandler { get; } = new();

    /// <summary>If set, <see cref="Create"/> returns this result instead of constructing a bundle.</summary>
    public Result<GoogleDriveClientBundle>? CreateResultOverride { get; set; }

    public int CreateCallCount { get; private set; }
    public string? LastClientId { get; private set; }
    public string? LastClientSecret { get; private set; }
    public string? LastRefreshToken { get; private set; }

    public Result<GoogleDriveClientBundle> Create(
        string clientId, string clientSecret, string refreshToken, ILoggerFactory loggerFactory)
    {
        CreateCallCount++;
        LastClientId = clientId;
        LastClientSecret = clientSecret;
        LastRefreshToken = refreshToken;

        if (CreateResultOverride is not null)
        {
            return CreateResultOverride.Value;
        }

        // Build a DriveService whose HTTP client is the SDK handler.
        var driveService = new DriveService(new BaseClientService.Initializer
        {
            HttpClientFactory = new StaticHttpClientFactory(SdkHandler),
            ApplicationName = "FlashSkink-Test",
        });

        // Companion HTTP client.
        var resumableClient = new HttpClient(ResumableHandler, disposeHandler: false);

        return Result<GoogleDriveClientBundle>.Ok(new GoogleDriveClientBundle
        {
            DriveService = driveService,
            ResumableUploadClient = resumableClient,
        });
    }

    /// <summary>
    /// Google.Apis IHttpClientFactory implementation that returns a ConfigurableHttpClient
    /// wrapping our recording handler. Lets the SDK route every call through the handler.
    /// </summary>
    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _innerHandler;

        public StaticHttpClientFactory(HttpMessageHandler innerHandler)
        {
            _innerHandler = innerHandler;
        }

        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
        {
            // ConfigurableMessageHandler wraps our recording handler so the SDK's auth /
            // retry / etc. machinery can interpose. In production the inner handler is
            // HttpClientHandler; in tests it's our recorder.
            var handler = new ConfigurableMessageHandler(_innerHandler);
            return new ConfigurableHttpClient(handler);
        }
    }
}
