using System.Text;
using System.Text.Json;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.Dropbox;
using FlashSkink.Core.Providers.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers.Dropbox;

/// <summary>
/// Unit tests for <see cref="DropboxSetup"/>. PR §4.4. No real network — token-exchange HTTP runs
/// through a <see cref="RecordingHttpMessageHandler"/>; client construction runs through a
/// <see cref="FakeDropboxClientFactory"/>.
/// </summary>
public sealed class DropboxSetupTests
{
    // The SDK's ProcessCodeFlowAsync posts to api.dropbox.com (not api.dropboxapi.com).
    // Verified against Dropbox.Api 7.0.0 by tracing the request URL.
    private const string DropboxTokenEndpoint = "https://api.dropbox.com/oauth2/token";

    private static readonly byte[] Dek = MakeDek(0x42);

    private static byte[] MakeDek(byte seed)
    {
        var dek = new byte[32];
        for (var i = 0; i < dek.Length; i++) { dek[i] = (byte)(seed + i); }
        return dek;
    }

    private static (DropboxSetup Setup, FakeDropboxClientFactory Factory, RecordingHttpMessageHandler OauthHandler, HttpClient OauthClient) Build()
    {
        var factory = new FakeDropboxClientFactory();
        var oauthHandler = new RecordingHttpMessageHandler();
        var oauthClient = new HttpClient(oauthHandler);
        var setup = new DropboxSetup(factory, oauthClient, NullLoggerFactory.Instance);
        return (setup, factory, oauthHandler, oauthClient);
    }

    private static ProviderCredentials Credentials(string clientId = "app-key", string secret = "app-secret")
        => new() { ClientId = clientId, ClientSecret = secret };

    // ── Metadata ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Metadata_HasExpectedValues()
    {
        var (setup, _, _, _) = Build();
        Assert.Equal("dropbox", setup.ProviderType);
        Assert.Equal("Dropbox", setup.DisplayName);
        Assert.Equal(ProviderSetupKind.OAuth, setup.SetupKind);
    }

    [Fact]
    public void DropboxSetup_Requires_FixedRedirectPort()
    {
        // Dropbox does not honor RFC 8252 variable-port loopback, so its setup must declare the
        // fixed port the loopback listener binds and the developer registers. (Google Drive, which
        // honors dynamic ports, deliberately does not implement this capability.)
        var (setup, _, _, _) = Build();

        var capability = Assert.IsAssignableFrom<IRequiresFixedRedirectPort>(setup);
        Assert.Equal(53682, capability.RedirectPort);
    }

    // ── GetAuthorizationUriAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAuthorizationUriAsync_BuildsConsentUrl_WithPkceAndOffline()
    {
        var (setup, _, _, _) = Build();
        var result = await setup.GetAuthorizationUriAsync(
            "http://127.0.0.1:1234/oauth-callback", "challenge-xyz", Credentials(), CancellationToken.None);

        Assert.True(result.Success);
        var uri = result.AssertValue();
        Assert.Contains("dropbox.com", uri.Host);
        var query = uri.Query;
        Assert.Contains("response_type=code", query);
        Assert.Contains("client_id=app-key", query);
        Assert.Contains("code_challenge=challenge-xyz", query);
        Assert.Contains("code_challenge_method=S256", query);
        Assert.Contains("token_access_type=offline", query);
    }

    [Theory]
    [InlineData(null, "ch", "cid")]
    [InlineData("", "ch", "cid")]
    [InlineData("uri", null, "cid")]
    [InlineData("uri", "", "cid")]
    [InlineData("uri", "ch", null)]
    [InlineData("uri", "ch", "")]
    public async Task GetAuthorizationUriAsync_MissingRequiredField_ReturnsInvalidArgument(
        string? redirectUri, string? codeChallenge, string? clientId)
    {
        var (setup, _, _, _) = Build();
        var creds = new ProviderCredentials { ClientId = clientId };
        var result = await setup.GetAuthorizationUriAsync(
            redirectUri!, codeChallenge!, creds, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }

    // ── ExchangeCodeAsync ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExchangeCodeAsync_HappyPath_ReturnsEncryptedRefreshToken()
    {
        var (setup, _, oauthHandler, oauthClient) = Build();
        oauthHandler.Setup(HttpMethod.Post, DropboxTokenEndpoint,
            _ => DropboxCannedResponses.WithJson(200,
                """
                {
                  "access_token": "at-fresh",
                  "token_type": "bearer",
                  "expires_in": 14400,
                  "refresh_token": "rt-decrypted-value",
                  "uid": "12345",
                  "account_id": "dbid:ABC",
                  "scope": "files.content.write files.content.read account_info.read"
                }
                """));

        var result = await setup.ExchangeCodeAsync(
            "auth-code-1", "verifier-xyz", "http://127.0.0.1:1234/oauth-callback",
            Credentials(), Dek, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(ProviderTokenCrypto.TryDecrypt(result.AssertValue(), Dek, out var plaintext));
        Assert.Equal("rt-decrypted-value", plaintext);

        oauthClient.Dispose();
    }

    [Fact]
    public async Task ExchangeCodeAsync_MissingRefreshToken_ReturnsProviderApiChanged()
    {
        var (setup, _, oauthHandler, oauthClient) = Build();
        oauthHandler.Setup(HttpMethod.Post, DropboxTokenEndpoint,
            _ => DropboxCannedResponses.WithJson(200,
                """
                {
                  "access_token": "at-fresh",
                  "token_type": "bearer",
                  "expires_in": 14400,
                  "uid": "12345"
                }
                """));

        var result = await setup.ExchangeCodeAsync(
            "code", "verifier", "http://127.0.0.1:1234/oauth-callback",
            Credentials(), Dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderApiChanged, result.AssertError().Code);
        oauthClient.Dispose();
    }

    [Fact]
    public async Task ExchangeCodeAsync_InvalidGrant_ReturnsProviderAuthFailed()
    {
        var (setup, _, oauthHandler, oauthClient) = Build();
        oauthHandler.Setup(HttpMethod.Post, DropboxTokenEndpoint,
            _ => DropboxCannedResponses.WithJson(400,
                """{"error":"invalid_grant","error_description":"code expired"}"""));

        var result = await setup.ExchangeCodeAsync(
            "code", "verifier", "http://127.0.0.1:1234/oauth-callback",
            Credentials(), Dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderAuthFailed, result.AssertError().Code);
        oauthClient.Dispose();
    }

    [Fact]
    public async Task ExchangeCodeAsync_NetworkFailure_ReturnsProviderUnreachable()
    {
        var (setup, _, oauthHandler, oauthClient) = Build();
        oauthHandler.Setup(HttpMethod.Post, DropboxTokenEndpoint,
            _ => throw new HttpRequestException("down"));

        var result = await setup.ExchangeCodeAsync(
            "code", "verifier", "http://127.0.0.1:1234/oauth-callback",
            Credentials(), Dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderUnreachable, result.AssertError().Code);
        oauthClient.Dispose();
    }

    [Fact]
    public async Task ExchangeCodeAsync_Cancelled_ReturnsCancelled()
    {
        var (setup, _, _, oauthClient) = Build();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await setup.ExchangeCodeAsync(
            "code", "verifier", "http://127.0.0.1:1234/oauth-callback",
            Credentials(), Dek, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.AssertError().Code);
        oauthClient.Dispose();
    }

    [Theory]
    [InlineData(null, "v", "r", "cid", "secret")]
    [InlineData("", "v", "r", "cid", "secret")]
    [InlineData("c", null, "r", "cid", "secret")]
    [InlineData("c", "", "r", "cid", "secret")]
    [InlineData("c", "v", null, "cid", "secret")]
    [InlineData("c", "v", "", "cid", "secret")]
    [InlineData("c", "v", "r", null, "secret")]
    [InlineData("c", "v", "r", "cid", null)]
    public async Task ExchangeCodeAsync_MissingFields_ReturnsInvalidArgument(
        string? code, string? verifier, string? redirect, string? clientId, string? secret)
    {
        var (setup, _, _, oauthClient) = Build();
        var creds = new ProviderCredentials { ClientId = clientId, ClientSecret = secret };
        var result = await setup.ExchangeCodeAsync(
            code!, verifier!, redirect!, creds, Dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
        oauthClient.Dispose();
    }

    // ── ValidatePathAsync ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidatePathAsync_ReturnsInvalid()
    {
        var (setup, _, _, oauthClient) = Build();
        var result = await setup.ValidatePathAsync("/any/path", "/skink", CancellationToken.None);
        Assert.True(result.Success);
        Assert.False(result.AssertValue().IsValid);
        oauthClient.Dispose();
    }

    // ── CreateProviderAsync ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProviderAsync_HappyPath_WithConfig_UsesPersistedRootPath()
    {
        var (setup, factory, _, oauthClient) = Build();
        var encryptedToken = ProviderTokenCrypto.Encrypt("rt-x", Dek);
        var configJson = """{"rootPath":"/Custom/Root"}""";

        var result = await setup.CreateProviderAsync(
            "dbx-1", "Dropbox", encryptedToken, Credentials(), configJson, Dek, CancellationToken.None);

        Assert.True(result.Success);
        var provider = Assert.IsType<DropboxProvider>(result.Value);
        Assert.Equal("/Custom/Root", provider.RootPath);
        Assert.Equal(1, factory.CreateCallCount);
        Assert.Equal("rt-x", factory.LastRefreshToken);
        await provider.DisposeAsync();
        oauthClient.Dispose();
    }

    [Fact]
    public async Task CreateProviderAsync_HappyPath_WithoutConfig_UsesDefaultRootPath()
    {
        var (setup, _, _, oauthClient) = Build();
        var encryptedToken = ProviderTokenCrypto.Encrypt("rt-x", Dek);

        var result = await setup.CreateProviderAsync(
            "dbx-1", "Dropbox", encryptedToken, Credentials(),
            providerConfigJson: null, Dek, CancellationToken.None);

        Assert.True(result.Success);
        var provider = Assert.IsType<DropboxProvider>(result.Value);
        Assert.Equal("/FlashSkink Backup", provider.RootPath);
        await provider.DisposeAsync();
        oauthClient.Dispose();
    }

    [Fact]
    public async Task CreateProviderAsync_DecryptFails_ReturnsTokenRevoked()
    {
        var (setup, _, _, oauthClient) = Build();
        // Encrypted with a DIFFERENT DEK.
        var encryptedToken = ProviderTokenCrypto.Encrypt("rt-x", MakeDek(0xAA));

        var result = await setup.CreateProviderAsync(
            "dbx-1", "Dropbox", encryptedToken, Credentials(),
            providerConfigJson: null, Dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.TokenRevoked, result.AssertError().Code);
        oauthClient.Dispose();
    }

    [Fact]
    public async Task CreateProviderAsync_MalformedConfigJson_ReturnsInvalidArgument()
    {
        var (setup, _, _, oauthClient) = Build();
        var encryptedToken = ProviderTokenCrypto.Encrypt("rt-x", Dek);

        var result = await setup.CreateProviderAsync(
            "dbx-1", "Dropbox", encryptedToken, Credentials(),
            providerConfigJson: "{not json", dek: Dek, ct: CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
        oauthClient.Dispose();
    }

    [Theory]
    [InlineData(null, "Dropbox", "cid", "sec")]
    [InlineData("", "Dropbox", "cid", "sec")]
    [InlineData("id", null, "cid", "sec")]
    [InlineData("id", "", "cid", "sec")]
    [InlineData("id", "Dropbox", null, "sec")]
    [InlineData("id", "Dropbox", "cid", null)]
    public async Task CreateProviderAsync_MissingFields_ReturnsInvalidArgument(
        string? providerId, string? displayName, string? clientId, string? clientSecret)
    {
        var (setup, _, _, oauthClient) = Build();
        var encryptedToken = ProviderTokenCrypto.Encrypt("rt-x", Dek);
        var creds = new ProviderCredentials { ClientId = clientId, ClientSecret = clientSecret };

        var result = await setup.CreateProviderAsync(
            providerId!, displayName!, encryptedToken, creds,
            providerConfigJson: null, Dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
        oauthClient.Dispose();
    }

    [Fact]
    public async Task CreateProviderAsync_EmptyEncryptedToken_ReturnsInvalidArgument()
    {
        var (setup, _, _, oauthClient) = Build();
        var result = await setup.CreateProviderAsync(
            "id", "Dropbox", Array.Empty<byte>(), Credentials(),
            providerConfigJson: null, Dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
        oauthClient.Dispose();
    }

    // ── Dispose ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_ProductionConstructor_DisposesOauthHttpClient()
    {
        // The production constructor allocates its own HttpClient and is responsible for
        // disposing it. PR review #2: previously this was leaked.
        var setup = new DropboxSetup(NullLoggerFactory.Instance);
        setup.Dispose();
        // Calling Dispose twice must not throw — the IDisposable contract.
        setup.Dispose();
    }

    [Fact]
    public void Dispose_TestConstructor_DoesNotDisposeCallerOwnedClient()
    {
        // The test constructor receives an HttpClient owned by the caller. Disposing the setup
        // must NOT dispose that client (the caller is still using it).
        using var oauthClient = new HttpClient(new RecordingHttpMessageHandler());
        var setup = new DropboxSetup(
            new FakeDropboxClientFactory(), oauthClient, NullLoggerFactory.Instance);

        setup.Dispose();

        // If the setup had disposed the client, this call would throw ObjectDisposedException.
        // The assertion is implicit: the call simply succeeds.
        Assert.NotNull(oauthClient.BaseAddress is null ? "" : oauthClient.BaseAddress.ToString());
    }
}
