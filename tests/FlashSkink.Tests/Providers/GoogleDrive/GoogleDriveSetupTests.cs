using System.Net;
using System.Text;
using System.Text.Json;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers;
using FlashSkink.Core.Providers.GoogleDrive;
using FlashSkink.Core.Providers.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers.GoogleDrive;

/// <summary>
/// Unit tests for <see cref="GoogleDriveSetup"/>. PR §4.3.
/// All HTTP traffic is intercepted; no real Google calls.
/// </summary>
public sealed class GoogleDriveSetupTests
{
    private static readonly byte[] _dek = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static (GoogleDriveSetup Setup, FakeGoogleDriveClientFactory Factory, RecordingHttpMessageHandler TokenHandler) Build()
    {
        var factory = new FakeGoogleDriveClientFactory();
        var tokenHandler = new RecordingHttpMessageHandler();
        var tokenClient = new HttpClient(tokenHandler, disposeHandler: false);
        var setup = new GoogleDriveSetup(factory, tokenClient, NullLoggerFactory.Instance);
        return (setup, factory, tokenHandler);
    }

    // ── Metadata ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Metadata_HasExpectedValues()
    {
        var (setup, _, _) = Build();
        Assert.Equal("google-drive", setup.ProviderType);
        Assert.Equal("Google Drive", setup.DisplayName);
        Assert.Equal(ProviderSetupKind.OAuth, setup.SetupKind);
    }

    // ── GetAuthorizationUriAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAuthorizationUriAsync_BuildsCorrectUrl()
    {
        var (setup, _, _) = Build();
        var creds = new ProviderCredentials { ClientId = "test-client-id.apps.googleusercontent.com" };

        var result = await setup.GetAuthorizationUriAsync(
            "http://127.0.0.1:12345/oauth-callback/", "challenge-xyz", creds, CancellationToken.None);

        Assert.True(result.Success);
        var uri = result.Value!;
        var query = uri.Query;
        Assert.Contains("response_type=code", query);
        Assert.Contains("client_id=test-client-id.apps.googleusercontent.com", query);
        Assert.Contains("code_challenge=challenge-xyz", query);
        Assert.Contains("code_challenge_method=S256", query);
        Assert.Contains("access_type=offline", query);
        Assert.Contains("prompt=consent", query);
        Assert.Contains("scope=", query);
        // Scope URL-encoded — colons become %3A, slashes become %2F.
        Assert.Contains("googleapis.com%2Fauth%2Fdrive.file", query);
    }

    [Fact]
    public async Task GetAuthorizationUriAsync_MissingClientId_ReturnsInvalidArgument()
    {
        var (setup, _, _) = Build();
        var creds = new ProviderCredentials { ClientId = "" };

        var result = await setup.GetAuthorizationUriAsync(
            "http://127.0.0.1:12345/oauth-callback/", "challenge", creds, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task GetAuthorizationUriAsync_MissingRedirectUri_ReturnsInvalidArgument()
    {
        var (setup, _, _) = Build();
        var creds = new ProviderCredentials { ClientId = "cid" };

        var result = await setup.GetAuthorizationUriAsync(
            "", "challenge", creds, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task GetAuthorizationUriAsync_MissingCodeChallenge_ReturnsInvalidArgument()
    {
        var (setup, _, _) = Build();
        var creds = new ProviderCredentials { ClientId = "cid" };

        var result = await setup.GetAuthorizationUriAsync(
            "http://127.0.0.1/cb/", "", creds, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task GetAuthorizationUriAsync_UrlEncodesRedirectUri()
    {
        var (setup, _, _) = Build();
        var creds = new ProviderCredentials { ClientId = "cid" };

        var result = await setup.GetAuthorizationUriAsync(
            "http://127.0.0.1:12345/oauth-callback/", "ch", creds, CancellationToken.None);

        Assert.True(result.Success);
        // Colon, slash, and forward-slash become percent-encoded in the query.
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A12345%2Foauth-callback%2F", result.Value!.Query);
    }

    // ── ExchangeCodeAsync ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExchangeCodeAsync_HappyPath_ReturnsEncryptedRefreshToken_Decryptable()
    {
        var (setup, _, tokenHandler) = Build();

        tokenHandler.Setup(HttpMethod.Post,
            "https://oauth2.googleapis.com/token",
            _ => CannedResponses.WithJson(200,
                "{\"access_token\":\"at-xyz\",\"refresh_token\":\"rt-xyz\",\"expires_in\":3600,\"token_type\":\"Bearer\",\"scope\":\"https://www.googleapis.com/auth/drive.file\"}"));

        var creds = new ProviderCredentials { ClientId = "cid", ClientSecret = "csec" };
        var result = await setup.ExchangeCodeAsync(
            "auth-code", "verifier-xyz", "http://127.0.0.1:12345/oauth-callback/",
            creds, _dek, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotEmpty(result.Value!);
        Assert.True(ProviderTokenCrypto.TryDecrypt(result.Value, _dek, out var plain));
        Assert.Equal("rt-xyz", plain);
    }

    [Fact]
    public async Task ExchangeCodeAsync_RequestBodyContainsAllExpectedFields()
    {
        var (setup, _, tokenHandler) = Build();

        tokenHandler.Setup(HttpMethod.Post,
            "https://oauth2.googleapis.com/token",
            _ => CannedResponses.WithJson(200,
                "{\"access_token\":\"at\",\"refresh_token\":\"rt\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"));

        var creds = new ProviderCredentials { ClientId = "cid-val", ClientSecret = "csec-val" };
        await setup.ExchangeCodeAsync(
            "auth-code-val", "verifier-val", "http://127.0.0.1:12345/oauth-callback/",
            creds, _dek, CancellationToken.None);

        var req = tokenHandler.ReceivedRequests.Single();
        Assert.Contains("code=auth-code-val", req.BodyString);
        Assert.Contains("code_verifier=verifier-val", req.BodyString);
        Assert.Contains("client_id=cid-val", req.BodyString);
        Assert.Contains("client_secret=csec-val", req.BodyString);
        Assert.Contains("grant_type=authorization_code", req.BodyString);
        Assert.Contains("redirect_uri=", req.BodyString);
    }

    [Fact]
    public async Task ExchangeCodeAsync_NetworkFailure_ReturnsProviderUnreachable()
    {
        var (setup, _, tokenHandler) = Build();

        tokenHandler.Setup(HttpMethod.Post,
            "https://oauth2.googleapis.com/token",
            _ => throw new HttpRequestException("network down"));

        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "s" };
        var result = await setup.ExchangeCodeAsync(
            "code", "ver", "http://127.0.0.1/", creds, _dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderUnreachable, result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCodeAsync_InvalidGrant_ReturnsProviderAuthFailed()
    {
        var (setup, _, tokenHandler) = Build();

        tokenHandler.Setup(HttpMethod.Post,
            "https://oauth2.googleapis.com/token",
            _ => CannedResponses.WithJson(400, "{\"error\":\"invalid_grant\"}"));

        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "s" };
        var result = await setup.ExchangeCodeAsync(
            "code", "ver", "http://127.0.0.1/", creds, _dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderAuthFailed, result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCodeAsync_InvalidClient_ReturnsProviderAuthFailed()
    {
        var (setup, _, tokenHandler) = Build();

        tokenHandler.Setup(HttpMethod.Post,
            "https://oauth2.googleapis.com/token",
            _ => CannedResponses.WithJson(401, "{\"error\":\"invalid_client\"}"));

        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "s" };
        var result = await setup.ExchangeCodeAsync(
            "code", "ver", "http://127.0.0.1/", creds, _dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderAuthFailed, result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCodeAsync_MissingRefreshToken_ReturnsProviderApiChanged()
    {
        var (setup, _, tokenHandler) = Build();

        tokenHandler.Setup(HttpMethod.Post,
            "https://oauth2.googleapis.com/token",
            _ => CannedResponses.WithJson(200,
                "{\"access_token\":\"at\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"));

        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "s" };
        var result = await setup.ExchangeCodeAsync(
            "code", "ver", "http://127.0.0.1/", creds, _dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderApiChanged, result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCodeAsync_MalformedJson_ReturnsProviderApiChanged()
    {
        var (setup, _, tokenHandler) = Build();

        tokenHandler.Setup(HttpMethod.Post,
            "https://oauth2.googleapis.com/token",
            _ => CannedResponses.WithJson(200, "{not valid json"));

        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "s" };
        var result = await setup.ExchangeCodeAsync(
            "code", "ver", "http://127.0.0.1/", creds, _dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderApiChanged, result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCodeAsync_MissingClientId_ReturnsInvalidArgument()
    {
        var (setup, _, _) = Build();
        var creds = new ProviderCredentials { ClientId = "", ClientSecret = "s" };

        var result = await setup.ExchangeCodeAsync(
            "code", "ver", "http://127.0.0.1/", creds, _dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCodeAsync_MissingClientSecret_ReturnsInvalidArgument()
    {
        var (setup, _, _) = Build();
        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "" };

        var result = await setup.ExchangeCodeAsync(
            "code", "ver", "http://127.0.0.1/", creds, _dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task ExchangeCodeAsync_Cancelled_ReturnsCancelled()
    {
        var (setup, _, _) = Build();
        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "s" };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await setup.ExchangeCodeAsync(
            "code", "ver", "http://127.0.0.1/", creds, _dek, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    // ── ValidatePathAsync ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidatePathAsync_ReturnsInvalid()
    {
        var (setup, _, _) = Build();

        var result = await setup.ValidatePathAsync("/tmp/x", "/tmp/skink", CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value!.IsValid);
    }

    // ── CreateProviderAsync ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProviderAsync_BadEnvelope_ReturnsTokenRevoked()
    {
        var (setup, _, _) = Build();
        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "s" };

        // Garbage envelope (does not decrypt under _dek).
        var bogus = new byte[64];

        var result = await setup.CreateProviderAsync(
            "p1", "Drive", bogus, creds, providerConfigJson: null, _dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.TokenRevoked, result.Error!.Code);
    }

    [Fact]
    public async Task CreateProviderAsync_MissingClientId_ReturnsInvalidArgument()
    {
        var (setup, _, _) = Build();
        var creds = new ProviderCredentials { ClientId = "", ClientSecret = "s" };
        var token = ProviderTokenCrypto.Encrypt("rt", _dek);

        var result = await setup.CreateProviderAsync(
            "p1", "Drive", token, creds, providerConfigJson: null, _dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task CreateProviderAsync_MalformedProviderConfig_ReturnsInvalidArgument()
    {
        var (setup, _, _) = Build();
        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "s" };
        var token = ProviderTokenCrypto.Encrypt("rt", _dek);

        var result = await setup.CreateProviderAsync(
            "p1", "Drive", token, creds, providerConfigJson: "{not json", dek: _dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task CreateProviderAsync_WithPersistedConfig_UsesPersistedFolderId_NoLookupCall()
    {
        var (setup, factory, _) = Build();
        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "s" };
        var token = ProviderTokenCrypto.Encrypt("rt", _dek);
        var cfg = "{\"folderId\":\"persisted-folder-id\",\"folderName\":\"FlashSkink Backup\"}";

        var result = await setup.CreateProviderAsync(
            "p1", "Drive", token, creds, cfg, _dek, CancellationToken.None);

        Assert.True(result.Success);
        var driveProvider = Assert.IsType<GoogleDriveProvider>(result.Value);
        Assert.Equal("persisted-folder-id", driveProvider.FolderId);

        // No lookup or create call should have been made.
        Assert.Empty(factory.SdkHandler.ReceivedRequests);

        await driveProvider.DisposeAsync();
    }

    [Fact]
    public async Task CreateProviderAsync_WithoutConfig_LooksUpExistingFolder_ReturnsItsId()
    {
        var (setup, factory, _) = Build();
        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "s" };
        var token = ProviderTokenCrypto.Encrypt("rt", _dek);

        // SDK list returns one existing folder.
        factory.SdkHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/files",
            _ => CannedResponses.WithJson(200,
                "{\"files\":[{\"id\":\"existing-fid\",\"name\":\"FlashSkink Backup\"}]}"));

        var result = await setup.CreateProviderAsync(
            "p1", "Drive", token, creds, providerConfigJson: null, _dek, CancellationToken.None);

        Assert.True(result.Success);
        var driveProvider = Assert.IsType<GoogleDriveProvider>(result.Value);
        Assert.Equal("existing-fid", driveProvider.FolderId);

        // Exactly one call: the list. No create.
        Assert.Single(factory.SdkHandler.ReceivedRequests);
        Assert.Equal(HttpMethod.Get, factory.SdkHandler.ReceivedRequests[0].Method);

        await driveProvider.DisposeAsync();
    }

    [Fact]
    public async Task CreateProviderAsync_WithoutConfig_NoExistingFolder_CreatesIt()
    {
        var (setup, factory, _) = Build();
        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "s" };
        var token = ProviderTokenCrypto.Encrypt("rt", _dek);

        // List returns empty.
        factory.SdkHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/files",
            _ => CannedResponses.WithJson(200, "{\"files\":[]}"));

        // Create returns new id.
        factory.SdkHandler.Setup(HttpMethod.Post,
            "https://www.googleapis.com/drive/v3/files",
            _ => CannedResponses.WithJson(200, "{\"id\":\"new-fid\",\"name\":\"FlashSkink Backup\"}"));

        var result = await setup.CreateProviderAsync(
            "p1", "Drive", token, creds, providerConfigJson: null, _dek, CancellationToken.None);

        Assert.True(result.Success);
        var driveProvider = Assert.IsType<GoogleDriveProvider>(result.Value);
        Assert.Equal("new-fid", driveProvider.FolderId);

        Assert.Equal(2, factory.SdkHandler.ReceivedRequests.Count);
        Assert.Contains(factory.SdkHandler.ReceivedRequests, r => r.Method == HttpMethod.Get);
        Assert.Contains(factory.SdkHandler.ReceivedRequests, r => r.Method == HttpMethod.Post);

        await driveProvider.DisposeAsync();
    }

    [Fact]
    public async Task CreateProviderAsync_FactoryFails_PropagatesError()
    {
        var (setup, factory, _) = Build();
        factory.CreateResultOverride = Result<GoogleDriveClientBundle>.Fail(
            ErrorCode.Unknown, "factory boom");

        var creds = new ProviderCredentials { ClientId = "c", ClientSecret = "s" };
        var token = ProviderTokenCrypto.Encrypt("rt", _dek);

        var result = await setup.CreateProviderAsync(
            "p1", "Drive", token, creds, providerConfigJson: null, _dek, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Unknown, result.Error!.Code);
    }
}
