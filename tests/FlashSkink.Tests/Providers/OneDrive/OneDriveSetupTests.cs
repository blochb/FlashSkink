using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.OneDrive;
using FlashSkink.Core.Providers.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers.OneDrive;

/// <summary>
/// Unit tests for <see cref="OneDriveSetup"/>: the v2.0 authorization-code + PKCE flow, token
/// exchange, path validation, and runtime-provider construction. No network.
/// </summary>
public sealed class OneDriveSetupTests
{
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    private static byte[] Dek() => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public async Task GetAuthorizationUriAsync_BuildsV2UrlWithPkceAndOfflineAccess()
    {
        using var setup = new OneDriveSetup(
            new FakeOneDriveClientFactory(new RecordingHttpMessageHandler()),
            new HttpClient(new RecordingHttpMessageHandler()),
            NullLoggerFactory.Instance);

        var credentials = new ProviderCredentials { ClientId = "client-id" };
        var result = await setup.GetAuthorizationUriAsync(
            "http://localhost:7421/callback", "challenge-xyz", credentials, CancellationToken.None);

        var url = Uri.UnescapeDataString(result.AssertValue().AbsoluteUri);
        Assert.StartsWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("scope=Files.ReadWrite offline_access", url);
        Assert.Contains("code_challenge=challenge-xyz", url);
        Assert.Contains("code_challenge_method=S256", url);
    }

    [Fact]
    public async Task ExchangeCodeAsync_Success_EncryptsRefreshToken()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.Setup(HttpMethod.Post, TokenEndpoint, _ => OneDriveCannedResponses.Token("access-tok", "my-refresh-token"));
        using var setup = new OneDriveSetup(
            new FakeOneDriveClientFactory(new RecordingHttpMessageHandler()),
            new HttpClient(handler),
            NullLoggerFactory.Instance);

        var dek = Dek();
        var credentials = new ProviderCredentials { ClientId = "cid", ClientSecret = "secret" };
        var result = await setup.ExchangeCodeAsync(
            "auth-code", "verifier", "http://localhost/cb", credentials, dek, CancellationToken.None);

        Assert.True(ProviderTokenCrypto.TryDecrypt(result.AssertValue(), dek, out var recovered));
        Assert.Equal("my-refresh-token", recovered);
    }

    [Fact]
    public async Task ExchangeCodeAsync_WithClientSecret_IncludesSecretInForm()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.Setup(HttpMethod.Post, TokenEndpoint, _ => OneDriveCannedResponses.Token("a", "r"));
        using var setup = new OneDriveSetup(
            new FakeOneDriveClientFactory(new RecordingHttpMessageHandler()),
            new HttpClient(handler),
            NullLoggerFactory.Instance);

        var credentials = new ProviderCredentials { ClientId = "cid", ClientSecret = "the-secret" };
        await setup.ExchangeCodeAsync("code", "verifier", "http://localhost/cb", credentials, Dek(), CancellationToken.None);

        var post = handler.ReceivedRequests.Single(r => r.Method == HttpMethod.Post);
        Assert.Contains("client_secret=the-secret", post.BodyString);
    }

    [Fact]
    public async Task ExchangeCodeAsync_PublicApp_OmitsClientSecret()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.Setup(HttpMethod.Post, TokenEndpoint, _ => OneDriveCannedResponses.Token("a", "r"));
        using var setup = new OneDriveSetup(
            new FakeOneDriveClientFactory(new RecordingHttpMessageHandler()),
            new HttpClient(handler),
            NullLoggerFactory.Instance);

        var credentials = new ProviderCredentials { ClientId = "cid", ClientSecret = null };
        await setup.ExchangeCodeAsync("code", "verifier", "http://localhost/cb", credentials, Dek(), CancellationToken.None);

        var post = handler.ReceivedRequests.Single(r => r.Method == HttpMethod.Post);
        Assert.DoesNotContain("client_secret", post.BodyString);
    }

    [Fact]
    public async Task ExchangeCodeAsync_ErrorResponse_MapsWithoutLeakingErrorDescription()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.Setup(HttpMethod.Post, TokenEndpoint, _ => OneDriveCannedResponses.WithJson(
            400, "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS70008 the secret value leaked\"}"));
        using var setup = new OneDriveSetup(
            new FakeOneDriveClientFactory(new RecordingHttpMessageHandler()),
            new HttpClient(handler),
            NullLoggerFactory.Instance);

        var credentials = new ProviderCredentials { ClientId = "cid", ClientSecret = "secret" };
        var result = await setup.ExchangeCodeAsync(
            "code", "verifier", "http://localhost/cb", credentials, Dek(), CancellationToken.None);

        var error = result.AssertError();
        Assert.Equal(ErrorCode.TokenRefreshFailed, error.Code);
        Assert.DoesNotContain("AADSTS70008", error.Message);
        Assert.DoesNotContain("error_description", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData('\\')]
    [InlineData(':')]
    [InlineData('*')]
    [InlineData('?')]
    [InlineData('"')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('|')]
    public async Task ValidatePathAsync_RejectsIllegalSegmentChars(char illegal)
    {
        using var setup = new OneDriveSetup(
            new FakeOneDriveClientFactory(new RecordingHttpMessageHandler()),
            new HttpClient(new RecordingHttpMessageHandler()),
            NullLoggerFactory.Instance);

        var path = $"foo{illegal}bar";
        var result = await setup.ValidatePathAsync(path, "/skink/root", CancellationToken.None);

        var validation = result.AssertValue();
        Assert.False(validation.IsValid);
        Assert.NotNull(validation.Reason);
    }

    [Fact]
    public async Task ValidatePathAsync_EmptyPath_DefaultsToBackupRoot()
    {
        using var setup = new OneDriveSetup(
            new FakeOneDriveClientFactory(new RecordingHttpMessageHandler()),
            new HttpClient(new RecordingHttpMessageHandler()),
            NullLoggerFactory.Instance);

        var result = await setup.ValidatePathAsync(string.Empty, "/skink/root", CancellationToken.None);

        Assert.True(result.AssertValue().IsValid);
    }

    [Fact]
    public async Task CreateProviderAsync_DecryptsAndBuildsProvider()
    {
        var dek = Dek();
        var encryptedToken = ProviderTokenCrypto.Encrypt("refresh-token", dek);
        using var setup = new OneDriveSetup(
            new FakeOneDriveClientFactory(new RecordingHttpMessageHandler()),
            new HttpClient(new RecordingHttpMessageHandler()),
            NullLoggerFactory.Instance);

        var credentials = new ProviderCredentials { ClientId = "cid", ClientSecret = "secret" };
        var result = await setup.CreateProviderAsync(
            "provider-1", "OneDrive tail", encryptedToken, credentials,
            "{\"rootPath\":\"/FlashSkink Backup\"}", dek, CancellationToken.None);

        var provider = result.AssertValue();
        Assert.Equal("onedrive", provider.ProviderType);
        Assert.Equal("provider-1", provider.ProviderID);
        await ((IAsyncDisposable)provider).DisposeAsync();
    }

    [Fact]
    public async Task CreateProviderAsync_BadEnvelope_FailsCleanly()
    {
        var dek = Dek();
        using var setup = new OneDriveSetup(
            new FakeOneDriveClientFactory(new RecordingHttpMessageHandler()),
            new HttpClient(new RecordingHttpMessageHandler()),
            NullLoggerFactory.Instance);

        var credentials = new ProviderCredentials { ClientId = "cid", ClientSecret = "secret" };
        var result = await setup.CreateProviderAsync(
            "provider-1", "OneDrive tail", new byte[] { 1, 2, 3 }, credentials, null, dek, CancellationToken.None);

        Assert.Equal(ErrorCode.TokenRevoked, result.AssertError().Code);
    }
}
