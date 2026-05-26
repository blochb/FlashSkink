using System.Text.Json;
using Dropbox.Api;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.Setup;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers.Dropbox;

/// <summary>
/// <see cref="IProviderSetup"/> for Dropbox. <see cref="SetupKind"/> = <see cref="ProviderSetupKind.OAuth"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>OAuth flow.</strong> <see cref="GetAuthorizationUriAsync"/> wraps
/// <see cref="DropboxOAuth2Helper.GetAuthorizeUri(OAuthResponseType, string, string, string, bool, bool, string, bool, TokenAccessType, string[], IncludeGrantedScopes, string)"/>
/// with PKCE (S256) and <see cref="TokenAccessType.Offline"/> — the latter is required to receive a
/// refresh token. <see cref="ExchangeCodeAsync"/> delegates to
/// <see cref="DropboxOAuth2Helper.ProcessCodeFlowAsync(string, string, string, string, HttpClient, string)"/>
/// and DEK-encrypts the resulting refresh token via <see cref="ProviderTokenCrypto"/>.
/// </para>
/// <para>
/// <strong>Construction.</strong> <see cref="CreateProviderAsync"/> decrypts the refresh token,
/// builds the SDK client via <see cref="IDropboxClientFactory"/>, and wraps the client in a
/// <see cref="DropboxProvider"/>. Unlike Google Drive there is no "find or create folder" step —
/// Dropbox creates intermediate path components implicitly on upload, so the root path is just a
/// string. The default root is <c>"/" + ProviderConstants.CloudRootFolderName</c>.
/// </para>
/// <para>
/// <strong>Scopes.</strong> <c>files.content.write</c>, <c>files.content.read</c>,
/// <c>account_info.read</c>. The third is needed by <see cref="DropboxProvider.CheckHealthAsync"/>
/// (<see cref="Users.Routes.UsersUserRoutes.GetCurrentAccountAsync"/>) and
/// <see cref="DropboxProvider.GetQuotaBytesAsync"/>
/// (<see cref="Users.Routes.UsersUserRoutes.GetSpaceUsageAsync"/>).
/// </para>
/// </remarks>
internal sealed class DropboxSetup : IProviderSetup, IDisposable
{
    /// <summary>OAuth scopes requested at consent time. See class remarks for rationale.</summary>
    private static readonly string[] Scopes =
    [
        "files.content.write",
        "files.content.read",
        "account_info.read",
    ];

    private readonly IDropboxClientFactory _clientFactory;
    private readonly HttpClient _oauthHttpClient;
    private readonly bool _ownsOauthClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DropboxSetup> _logger;

    /// <inheritdoc/>
    public string ProviderType => "dropbox";

    /// <inheritdoc/>
    public string DisplayName => "Dropbox";

    /// <inheritdoc/>
    public ProviderSetupKind SetupKind => ProviderSetupKind.OAuth;

    /// <summary>Production constructor — wires the default factory + a fresh <see cref="HttpClient"/> for token exchange.</summary>
    public DropboxSetup(ILoggerFactory loggerFactory)
        : this(new DropboxClientFactory(), new HttpClient(), ownsOauthClient: true, loggerFactory)
    {
    }

    /// <summary>Test constructor — accepts injected factory and HTTP client; does not dispose the supplied client.</summary>
    internal DropboxSetup(
        IDropboxClientFactory clientFactory,
        HttpClient oauthHttpClient,
        ILoggerFactory loggerFactory)
        : this(clientFactory, oauthHttpClient, ownsOauthClient: false, loggerFactory)
    {
    }

    private DropboxSetup(
        IDropboxClientFactory clientFactory,
        HttpClient oauthHttpClient,
        bool ownsOauthClient,
        ILoggerFactory loggerFactory)
    {
        _clientFactory = clientFactory;
        _oauthHttpClient = oauthHttpClient;
        _ownsOauthClient = ownsOauthClient;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DropboxSetup>();
    }

    /// <summary>
    /// Disposes the per-instance OAuth <see cref="HttpClient"/> when this instance owns it (i.e.
    /// was constructed via the production single-arg constructor). When the test constructor was
    /// used, the caller retains ownership and this is a no-op. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_ownsOauthClient)
        {
            try { _oauthHttpClient.Dispose(); } catch { /* swallow */ }
        }
    }

    /// <inheritdoc/>
    public Task<Result<Uri>> GetAuthorizationUriAsync(
        string redirectUri,
        string codeChallenge,
        ProviderCredentials credentials,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(redirectUri))
        {
            return Task.FromResult(Result<Uri>.Fail(
                ErrorCode.InvalidArgument, "redirectUri is required."));
        }
        if (string.IsNullOrEmpty(codeChallenge))
        {
            return Task.FromResult(Result<Uri>.Fail(
                ErrorCode.InvalidArgument, "codeChallenge is required."));
        }
        if (string.IsNullOrEmpty(credentials.ClientId))
        {
            return Task.FromResult(Result<Uri>.Fail(
                ErrorCode.InvalidArgument, "Dropbox setup requires a non-null ClientId."));
        }

        try
        {
            // The SDK helper builds the consent URL with PKCE + offline access. Token access
            // type Offline is non-negotiable for reliable refresh-token issuance.
            var uri = DropboxOAuth2Helper.GetAuthorizeUri(
                oauthResponseType: OAuthResponseType.Code,
                clientId: credentials.ClientId,
                redirectUri: redirectUri,
                state: null,
                forceReapprove: false,
                disableSignup: false,
                requireRole: null,
                forceReauthentication: false,
                tokenAccessType: TokenAccessType.Offline,
                scopeList: Scopes,
                includeGrantedScopes: IncludeGrantedScopes.None,
                codeChallenge: codeChallenge);

            return Task.FromResult(Result<Uri>.Ok(uri));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build Dropbox authorisation URI.");
            return Task.FromResult(Result<Uri>.Fail(
                ErrorCode.Unknown, "Failed to build Dropbox authorisation URI.", ex));
        }
    }

    /// <inheritdoc/>
    public async Task<Result<byte[]>> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        ProviderCredentials credentials,
        ReadOnlyMemory<byte> dek,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code))
        {
            return Result<byte[]>.Fail(ErrorCode.InvalidArgument, "code is required.");
        }
        if (string.IsNullOrEmpty(codeVerifier))
        {
            return Result<byte[]>.Fail(ErrorCode.InvalidArgument, "codeVerifier is required.");
        }
        if (string.IsNullOrEmpty(redirectUri))
        {
            return Result<byte[]>.Fail(ErrorCode.InvalidArgument, "redirectUri is required.");
        }
        if (string.IsNullOrEmpty(credentials.ClientId) || string.IsNullOrEmpty(credentials.ClientSecret))
        {
            return Result<byte[]>.Fail(
                ErrorCode.InvalidArgument,
                "Dropbox token exchange requires both ClientId (appKey) and ClientSecret (appSecret).");
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var response = await DropboxOAuth2Helper.ProcessCodeFlowAsync(
                code: code,
                appKey: credentials.ClientId,
                appSecret: credentials.ClientSecret,
                redirectUri: redirectUri,
                client: _oauthHttpClient,
                codeVerifier: codeVerifier).ConfigureAwait(false);

            if (response is null || string.IsNullOrEmpty(response.RefreshToken))
            {
                _logger.LogWarning(
                    "Dropbox token endpoint did not return a refresh token (was TokenAccessType.Offline sent?).");
                return Result<byte[]>.Fail(
                    ErrorCode.ProviderApiChanged,
                    "Dropbox token response did not include a refresh_token. Verify TokenAccessType.Offline was sent at authorisation time.");
            }

            var envelope = ProviderTokenCrypto.Encrypt(response.RefreshToken, dek);
            return Result<byte[]>.Ok(envelope);
        }
        catch (OperationCanceledException ex)
        {
            return Result<byte[]>.Fail(ErrorCode.Cancelled, "ExchangeCodeAsync cancelled.", ex);
        }
        catch (AuthException aex)
        {
            _logger.LogWarning("Dropbox rejected the OAuth code or client credentials.");
            return Result<byte[]>.Fail(
                ErrorCode.ProviderAuthFailed,
                "Dropbox rejected the OAuth code or client credentials.", aex);
        }
        catch (OAuth2Exception oex)
        {
            _logger.LogWarning("Dropbox OAuth2 exchange failed: {Error}", oex.ErrorDescription ?? oex.Message);
            return Result<byte[]>.Fail(
                ErrorCode.ProviderAuthFailed,
                "Dropbox OAuth2 token exchange failed.", oex);
        }
        catch (HttpException hex)
        {
            // SDK transport-level failure (5xx, etc.) — surface as ProviderUnreachable for retry.
            return Result<byte[]>.Fail(
                ErrorCode.ProviderUnreachable,
                $"Dropbox token endpoint returned HTTP {hex.StatusCode}.", hex);
        }
        catch (HttpRequestException ex)
        {
            return Result<byte[]>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure during Dropbox token exchange.", ex);
        }
        catch (IOException ex)
        {
            return Result<byte[]>.Fail(
                ErrorCode.ProviderUnreachable, "I/O failure during Dropbox token exchange.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Dropbox token exchange.");
            return Result<byte[]>.Fail(
                ErrorCode.Unknown, "Unexpected error during Dropbox token exchange.", ex);
        }
    }

    /// <inheritdoc/>
    public Task<Result<ValidationResult>> ValidatePathAsync(
        string path, string skinkRoot, CancellationToken ct)
    {
        // Dropbox is OAuth; LocalPath validation is not applicable.
        return Task.FromResult(Result<ValidationResult>.Ok(
            ValidationResult.Invalid("Dropbox is an OAuth provider; no local path is required.")));
    }

    /// <inheritdoc/>
    public async Task<Result<IStorageProvider>> CreateProviderAsync(
        string providerId,
        string displayName,
        byte[] encryptedToken,
        ProviderCredentials credentials,
        string? providerConfigJson,
        ReadOnlyMemory<byte> dek,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(providerId))
        {
            return Result<IStorageProvider>.Fail(
                ErrorCode.InvalidArgument, "providerId is required.");
        }
        if (string.IsNullOrEmpty(displayName))
        {
            return Result<IStorageProvider>.Fail(
                ErrorCode.InvalidArgument, "displayName is required.");
        }
        if (string.IsNullOrEmpty(credentials.ClientId) || string.IsNullOrEmpty(credentials.ClientSecret))
        {
            return Result<IStorageProvider>.Fail(
                ErrorCode.InvalidArgument,
                "Dropbox CreateProviderAsync requires both ClientId (appKey) and ClientSecret (appSecret).");
        }
        if (encryptedToken is null || encryptedToken.Length == 0)
        {
            return Result<IStorageProvider>.Fail(
                ErrorCode.InvalidArgument, "encryptedToken is required.");
        }

        if (!ProviderTokenCrypto.TryDecrypt(encryptedToken, dek, out var refreshToken))
        {
            return Result<IStorageProvider>.Fail(
                ErrorCode.TokenRevoked,
                "Could not decrypt Dropbox refresh token; the brain may be corrupt or the DEK is wrong.");
        }

        // Optional persisted root path.
        string? persistedRootPath = null;
        if (!string.IsNullOrEmpty(providerConfigJson))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize(
                    providerConfigJson, DropboxProviderConfigJsonContext.Default.DropboxProviderConfig);
                if (cfg is not null && !string.IsNullOrEmpty(cfg.RootPath))
                {
                    persistedRootPath = cfg.RootPath;
                }
            }
            catch (JsonException ex)
            {
                return Result<IStorageProvider>.Fail(
                    ErrorCode.InvalidArgument,
                    "Dropbox provider config is malformed JSON.", ex);
            }
        }

        return await CreateProviderFromConfigAsync(
            providerId, displayName, credentials.ClientId, credentials.ClientSecret,
            refreshToken, persistedRootPath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Constructs a <see cref="DropboxProvider"/> from already-decrypted credentials. Bypasses
    /// <see cref="CreateProviderAsync"/>'s DEK-decrypt step; used by
    /// <c>BrainBackedProviderRegistry</c> after it has decrypted both blobs.
    /// </summary>
    internal async Task<Result<IStorageProvider>> CreateProviderFromConfigAsync(
        string providerId,
        string displayName,
        string appKey,
        string appSecret,
        string refreshToken,
        string? persistedRootPath,
        CancellationToken ct)
    {
        var bundleResult = _clientFactory.Create(appKey, appSecret, refreshToken, _loggerFactory);
        if (!bundleResult.Success)
        {
            return Result<IStorageProvider>.Fail(bundleResult.Error);
        }
        var bundle = bundleResult.Value;

        try
        {
            ct.ThrowIfCancellationRequested();

            var rootPath = !string.IsNullOrEmpty(persistedRootPath)
                ? persistedRootPath!
                : "/" + ProviderConstants.CloudRootFolderName;

            var provider = new DropboxProvider(
                providerId, displayName, bundle, rootPath,
                _loggerFactory.CreateLogger<DropboxProvider>());

            return Result<IStorageProvider>.Ok(provider);
        }
        catch (OperationCanceledException ex)
        {
            bundle.Dispose();
            return Result<IStorageProvider>.Fail(
                ErrorCode.Cancelled, "Dropbox provider construction cancelled.", ex);
        }
        catch (Exception ex)
        {
            bundle.Dispose();
            _logger.LogError(ex, "Unexpected error during Dropbox provider construction.");
            return Result<IStorageProvider>.Fail(
                ErrorCode.Unknown, "Unexpected error during Dropbox provider construction.", ex);
        }
    }
}
