using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.Setup;
using Google;
using Microsoft.Extensions.Logging;
using GoogleDriveData = Google.Apis.Drive.v3.Data;

namespace FlashSkink.Core.Providers.GoogleDrive;

/// <summary>
/// <see cref="IProviderSetup"/> for Google Drive. <see cref="SetupKind"/> = <see cref="ProviderSetupKind.OAuth"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>OAuth flow.</strong> <see cref="GetAuthorizationUriAsync"/> constructs the consent URL
/// (with PKCE, <c>access_type=offline</c>, and <c>prompt=consent</c> per Gate 1 resolutions
/// #4–#6 of <c>.claude/plans/pr-4.3.md</c>). <see cref="ExchangeCodeAsync"/> POSTs to
/// <c>https://oauth2.googleapis.com/token</c> and returns the refresh token encrypted with the DEK
/// via <see cref="ProviderTokenCrypto"/>.
/// </para>
/// <para>
/// <strong>Construction.</strong> <see cref="CreateProviderAsync"/> decrypts the refresh token,
/// builds the SDK clients via <see cref="IGoogleDriveClientFactory"/>, resolves or creates the
/// <c>"FlashSkink Backup"</c> folder, and wraps the clients in a
/// <see cref="GoogleDriveProvider"/>. The resolved folder ID is exposed on the constructed
/// provider via <c>GoogleDriveProvider.FolderId</c> so §4.6 can persist it into
/// <c>Providers.ProviderConfig</c>.
/// </para>
/// <para>
/// <strong>Scope.</strong> <c>https://www.googleapis.com/auth/drive.file</c> — per-file scope,
/// least-privilege. Drive can only see files our app has created.
/// </para>
/// </remarks>
internal sealed partial class GoogleDriveSetup : IProviderSetup
{
    internal const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    internal const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    internal const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";

    private readonly IGoogleDriveClientFactory _clientFactory;
    private readonly HttpClient _tokenExchangeClient;
    private readonly bool _ownsTokenClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GoogleDriveSetup> _logger;

    /// <inheritdoc/>
    public string ProviderType => "google-drive";

    /// <inheritdoc/>
    public string DisplayName => "Google Drive";

    /// <inheritdoc/>
    public ProviderSetupKind SetupKind => ProviderSetupKind.OAuth;

    /// <summary>Production constructor — wires the default factory + a fresh <see cref="HttpClient"/> for token exchange.</summary>
    public GoogleDriveSetup(ILoggerFactory loggerFactory)
        : this(new GoogleDriveClientFactory(), new HttpClient(), ownsTokenClient: true, loggerFactory)
    {
    }

    /// <summary>Test constructor — accepts injected factory and HTTP client; does not dispose the supplied client.</summary>
    internal GoogleDriveSetup(
        IGoogleDriveClientFactory clientFactory,
        HttpClient tokenExchangeClient,
        ILoggerFactory loggerFactory)
        : this(clientFactory, tokenExchangeClient, ownsTokenClient: false, loggerFactory)
    {
    }

    private GoogleDriveSetup(
        IGoogleDriveClientFactory clientFactory,
        HttpClient tokenExchangeClient,
        bool ownsTokenClient,
        ILoggerFactory loggerFactory)
    {
        _clientFactory = clientFactory;
        _tokenExchangeClient = tokenExchangeClient;
        _ownsTokenClient = ownsTokenClient;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GoogleDriveSetup>();
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
                ErrorCode.InvalidArgument, "Google Drive setup requires a non-null ClientId."));
        }

        // Build the consent URL. Gate 1 resolutions: access_type=offline (#5) + prompt=consent (#4)
        // are non-negotiable for reliable refresh-token issuance.
        var sb = new StringBuilder(AuthorizationEndpoint);
        sb.Append("?response_type=code");
        sb.Append("&client_id=").Append(Uri.EscapeDataString(credentials.ClientId));
        sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        sb.Append("&scope=").Append(Uri.EscapeDataString(DriveFileScope));
        sb.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
        sb.Append("&code_challenge_method=S256");
        sb.Append("&access_type=offline");
        sb.Append("&prompt=consent");

        return Task.FromResult(Result<Uri>.Ok(new Uri(sb.ToString())));
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
                ErrorCode.InvalidArgument, "Google Drive token exchange requires both ClientId and ClientSecret.");
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("code_verifier", codeVerifier),
                new KeyValuePair<string, string>("redirect_uri", redirectUri),
                new KeyValuePair<string, string>("client_id", credentials.ClientId),
                new KeyValuePair<string, string>("client_secret", credentials.ClientSecret),
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
            });

            using var response = await _tokenExchangeClient
                .PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Body may include error/error_description per OAuth2 RFC. Never log the body
                // verbatim — it could echo our request fields. Parse the error string only.
                var errorTag = TryParseErrorTag(body);
                if (string.Equals(errorTag, "invalid_grant", StringComparison.Ordinal))
                {
                    _logger.LogWarning("Drive token exchange rejected: invalid_grant.");
                    return Result<byte[]>.Fail(
                        ErrorCode.ProviderAuthFailed,
                        "Google rejected the authorisation code (invalid_grant). It may have expired or been used.");
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    string.Equals(errorTag, "invalid_client", StringComparison.Ordinal))
                {
                    _logger.LogWarning("Drive token exchange rejected: invalid client credentials.");
                    return Result<byte[]>.Fail(
                        ErrorCode.ProviderAuthFailed,
                        "Google rejected the client credentials (invalid_client).");
                }

                _logger.LogWarning("Drive token exchange failed with HTTP {Status} ({Error}).",
                    (int)response.StatusCode, errorTag ?? "<no-error-tag>");
                return Result<byte[]>.Fail(
                    ErrorCode.Unknown,
                    $"Google token endpoint returned HTTP {(int)response.StatusCode}.");
            }

            TokenResponseShape? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(body, TokenJsonContext.Default.TokenResponseShape);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Drive token endpoint returned malformed JSON.");
                return Result<byte[]>.Fail(
                    ErrorCode.ProviderApiChanged, "Google token endpoint returned malformed JSON.", ex);
            }

            if (parsed is null || string.IsNullOrEmpty(parsed.RefreshToken))
            {
                _logger.LogWarning("Drive token endpoint did not return a refresh token.");
                return Result<byte[]>.Fail(
                    ErrorCode.ProviderApiChanged,
                    "Google token response did not include a refresh_token (was access_type=offline and prompt=consent both sent?).");
            }

            var envelope = ProviderTokenCrypto.Encrypt(parsed.RefreshToken, dek);
            return Result<byte[]>.Ok(envelope);
        }
        catch (OperationCanceledException ex)
        {
            return Result<byte[]>.Fail(ErrorCode.Cancelled, "ExchangeCodeAsync cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            return Result<byte[]>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure during Google token exchange.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Google token exchange.");
            return Result<byte[]>.Fail(
                ErrorCode.Unknown, "Unexpected error during Google token exchange.", ex);
        }
    }

    /// <inheritdoc/>
    public Task<Result<ValidationResult>> ValidatePathAsync(
        string path, string skinkRoot, CancellationToken ct)
    {
        return Task.FromResult(Result<ValidationResult>.Ok(
            ValidationResult.Invalid("Google Drive is an OAuth provider; no local path is required.")));
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
                "Google Drive CreateProviderAsync requires both ClientId and ClientSecret (the caller decrypts the client secret before calling).");
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
                "Could not decrypt Google Drive refresh token; the brain may be corrupt or the DEK is wrong.");
        }

        // Optional persisted folder ID.
        string? persistedFolderId = null;
        if (!string.IsNullOrEmpty(providerConfigJson))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize(
                    providerConfigJson, GoogleDriveProviderConfigJsonContext.Default.GoogleDriveProviderConfig);
                if (cfg is not null && !string.IsNullOrEmpty(cfg.FolderId))
                {
                    persistedFolderId = cfg.FolderId;
                }
            }
            catch (JsonException ex)
            {
                return Result<IStorageProvider>.Fail(
                    ErrorCode.InvalidArgument,
                    "Google Drive provider config is malformed JSON.",
                    ex);
            }
        }

        return await CreateProviderFromConfigAsync(
            providerId, displayName, credentials.ClientId, credentials.ClientSecret,
            refreshToken, persistedFolderId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Constructs a <see cref="GoogleDriveProvider"/> from already-decrypted credentials. Bypasses
    /// <see cref="CreateProviderAsync"/>'s DEK-decrypt step; used by
    /// <c>BrainBackedProviderRegistry</c> after it has decrypted both blobs.
    /// </summary>
    internal async Task<Result<IStorageProvider>> CreateProviderFromConfigAsync(
        string providerId,
        string displayName,
        string clientId,
        string clientSecret,
        string refreshToken,
        string? persistedFolderId,
        CancellationToken ct)
    {
        var bundleResult = _clientFactory.Create(clientId, clientSecret, refreshToken, _loggerFactory);
        if (!bundleResult.Success)
        {
            return Result<IStorageProvider>.Fail(bundleResult.Error);
        }
        var bundle = bundleResult.Value;

        try
        {
            ct.ThrowIfCancellationRequested();

            string folderId;
            if (!string.IsNullOrEmpty(persistedFolderId))
            {
                folderId = persistedFolderId;
            }
            else
            {
                var resolved = await ResolveOrCreateFolderAsync(bundle, ct).ConfigureAwait(false);
                if (!resolved.Success)
                {
                    bundle.Dispose();
                    return Result<IStorageProvider>.Fail(resolved.Error);
                }
                folderId = resolved.Value;
            }

            var provider = new GoogleDriveProvider(
                providerId, displayName, bundle, folderId,
                _loggerFactory.CreateLogger<GoogleDriveProvider>());
            return Result<IStorageProvider>.Ok(provider);
        }
        catch (OperationCanceledException ex)
        {
            bundle.Dispose();
            return Result<IStorageProvider>.Fail(
                ErrorCode.Cancelled, "Google Drive provider construction cancelled.", ex);
        }
        catch (Exception ex)
        {
            bundle.Dispose();
            _logger.LogError(ex, "Unexpected error during Google Drive provider construction.");
            return Result<IStorageProvider>.Fail(
                ErrorCode.Unknown, "Unexpected error during Google Drive provider construction.", ex);
        }
    }

    private async Task<Result<string>> ResolveOrCreateFolderAsync(
        GoogleDriveClientBundle bundle, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // Look up an existing folder with the canonical name under My Drive root.
            var list = bundle.DriveService.Files.List();
            list.Q = "mimeType = 'application/vnd.google-apps.folder' " +
                $"and name = '{ProviderConstants.CloudRootFolderName}' " +
                "and 'root' in parents " +
                "and trashed = false";
            list.Fields = "files(id, name)";
            list.PageSize = 1;

            var listResult = await list.ExecuteAsync(ct).ConfigureAwait(false);
            var existing = listResult.Files?.FirstOrDefault();
            if (existing is not null && !string.IsNullOrEmpty(existing.Id))
            {
                return Result<string>.Ok(existing.Id);
            }

            // Otherwise create it.
            var newFolder = await bundle.DriveService.Files.Create(new GoogleDriveData.File
            {
                Name = ProviderConstants.CloudRootFolderName,
                MimeType = "application/vnd.google-apps.folder",
            }).ExecuteAsync(ct).ConfigureAwait(false);

            if (newFolder is null || string.IsNullOrEmpty(newFolder.Id))
            {
                return Result<string>.Fail(
                    ErrorCode.ProviderApiChanged,
                    "Drive Files.Create did not return an id for the new folder.");
            }

            return Result<string>.Ok(newFolder.Id);
        }
        catch (OperationCanceledException ex)
        {
            return Result<string>.Fail(ErrorCode.Cancelled, "Folder resolution cancelled.", ex);
        }
        catch (GoogleApiException gex) when ((int)gex.HttpStatusCode == 401)
        {
            return Result<string>.Fail(
                ErrorCode.TokenRefreshFailed,
                "Drive rejected the access token during folder resolution; refresh failed.",
                gex);
        }
        catch (GoogleApiException gex)
        {
            _logger.LogWarning(gex, "Drive folder resolution failed with HTTP {Status}.", (int)gex.HttpStatusCode);
            return Result<string>.Fail(
                ErrorCode.ProviderUnreachable,
                $"Drive folder resolution failed with HTTP {(int)gex.HttpStatusCode}.",
                gex);
        }
        catch (HttpRequestException ex)
        {
            return Result<string>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure during folder resolution.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during folder resolution.");
            return Result<string>.Fail(
                ErrorCode.Unknown, "Unexpected error during folder resolution.", ex);
        }
    }

    /// <summary>
    /// Best-effort extraction of the OAuth2 <c>error</c> field from a token-endpoint error body.
    /// Returns <see langword="null"/> if the body is empty or unparseable. Never logs the body.
    /// </summary>
    private static string? TryParseErrorTag(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }
        try
        {
            var err = JsonSerializer.Deserialize(body, TokenJsonContext.Default.TokenErrorShape);
            return err?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>JSON shape of Google's token-endpoint success response (we project the fields we need).</summary>
    internal sealed record TokenResponseShape
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("token_type")] public string? TokenType { get; init; }
        [JsonPropertyName("scope")] public string? Scope { get; init; }
    }

    /// <summary>JSON shape of Google's token-endpoint error response (RFC 6749 §5.2).</summary>
    internal sealed record TokenErrorShape
    {
        [JsonPropertyName("error")] public string? Error { get; init; }

        // Intentionally not deserialised: error_description. May echo user-supplied parameters,
        // so we never read or log it (Principle 26).
    }

    [JsonSerializable(typeof(TokenResponseShape))]
    [JsonSerializable(typeof(TokenErrorShape))]
    internal sealed partial class TokenJsonContext : JsonSerializerContext;
}
