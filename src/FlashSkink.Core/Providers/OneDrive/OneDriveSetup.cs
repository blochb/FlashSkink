using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.Setup;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers.OneDrive;

/// <summary>
/// <see cref="IProviderSetup"/> for OneDrive: drives BYOC OAuth 2.0 authorization-code + PKCE against
/// the Microsoft identity platform v2.0 endpoints, exchanges the code for a refresh token, and builds
/// the runtime <see cref="OneDriveProvider"/>. Raw HTTP (no MSAL).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Public vs. confidential app.</strong> The <c>client_secret</c> is included in the token
/// exchange only when the BYOC registration supplies one; native/public registrations rely on PKCE
/// alone (<c>codeVerifier</c>). Validation therefore does not require a secret.
/// </para>
/// <para>
/// <strong>Path model.</strong> OneDrive is path-addressed; <see cref="CreateProviderAsync"/> needs no
/// network round-trip — it constructs the provider directly from the persisted root path.
/// </para>
/// </remarks>
internal sealed class OneDriveSetup : IProviderSetup, IDisposable
{
    private const string AuthorizeEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string Scope = "Files.ReadWrite offline_access";

    private readonly IOneDriveClientFactory _clientFactory;
    private readonly HttpClient _tokenExchangeClient;
    private readonly bool _ownsTokenClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OneDriveSetup> _logger;

    /// <summary>Production constructor: owns its own token-exchange <see cref="HttpClient"/>.</summary>
    public OneDriveSetup(ILoggerFactory loggerFactory)
        : this(new OneDriveClientFactory(), new HttpClient(), ownsTokenClient: true, loggerFactory)
    {
    }

    /// <summary>Test/registry constructor: caller owns the injected <see cref="HttpClient"/> and factory.</summary>
    internal OneDriveSetup(IOneDriveClientFactory clientFactory, HttpClient tokenExchangeClient, ILoggerFactory loggerFactory)
        : this(clientFactory, tokenExchangeClient, ownsTokenClient: false, loggerFactory)
    {
    }

    private OneDriveSetup(
        IOneDriveClientFactory clientFactory,
        HttpClient tokenExchangeClient,
        bool ownsTokenClient,
        ILoggerFactory loggerFactory)
    {
        _clientFactory = clientFactory;
        _tokenExchangeClient = tokenExchangeClient;
        _ownsTokenClient = ownsTokenClient;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OneDriveSetup>();
    }

    /// <inheritdoc/>
    public string ProviderType => "onedrive";

    /// <inheritdoc/>
    public string DisplayName => "OneDrive";

    /// <inheritdoc/>
    public ProviderSetupKind SetupKind => ProviderSetupKind.OAuth;

    /// <inheritdoc/>
    public Task<Result<Uri>> GetAuthorizationUriAsync(
        string redirectUri, string codeChallenge, ProviderCredentials credentials, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(redirectUri))
        {
            return Task.FromResult(Result<Uri>.Fail(ErrorCode.InvalidArgument, "redirectUri must not be empty."));
        }
        if (string.IsNullOrEmpty(codeChallenge))
        {
            return Task.FromResult(Result<Uri>.Fail(ErrorCode.InvalidArgument, "codeChallenge must not be empty."));
        }
        if (string.IsNullOrEmpty(credentials.ClientId))
        {
            return Task.FromResult(Result<Uri>.Fail(ErrorCode.InvalidArgument, "An application identifier is required to begin sign-in."));
        }

        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(Result<Uri>.Fail(ErrorCode.Cancelled, "GetAuthorizationUriAsync cancelled."));
        }

        var sb = new StringBuilder(AuthorizeEndpoint);
        sb.Append("?response_type=code");
        sb.Append("&client_id=").Append(Uri.EscapeDataString(credentials.ClientId));
        sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        sb.Append("&scope=").Append(Uri.EscapeDataString(Scope));
        sb.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
        sb.Append("&code_challenge_method=S256");
        sb.Append("&response_mode=query");

        return Task.FromResult(Result<Uri>.Ok(new Uri(sb.ToString())));
    }

    /// <inheritdoc/>
    public async Task<Result<byte[]>> ExchangeCodeAsync(
        string code, string codeVerifier, string redirectUri, ProviderCredentials credentials, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code))
        {
            return Result<byte[]>.Fail(ErrorCode.InvalidArgument, "code must not be empty.");
        }
        if (string.IsNullOrEmpty(codeVerifier))
        {
            return Result<byte[]>.Fail(ErrorCode.InvalidArgument, "codeVerifier must not be empty.");
        }
        if (string.IsNullOrEmpty(redirectUri))
        {
            return Result<byte[]>.Fail(ErrorCode.InvalidArgument, "redirectUri must not be empty.");
        }
        if (string.IsNullOrEmpty(credentials.ClientId))
        {
            return Result<byte[]>.Fail(ErrorCode.InvalidArgument, "An application identifier is required to complete sign-in.");
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var form = new List<KeyValuePair<string, string>>
            {
                new("client_id", credentials.ClientId),
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", redirectUri),
                new("code_verifier", codeVerifier),
                new("scope", Scope),
            };
            if (!string.IsNullOrEmpty(credentials.ClientSecret))
            {
                form.Add(new KeyValuePair<string, string>("client_secret", credentials.ClientSecret));
            }

            using var content = new FormUrlEncodedContent(form);
            using HttpResponseMessage response = await _tokenExchangeClient
                .PostAsync(TokenEndpoint, content, ct)
                .ConfigureAwait(false);

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var tag = TryParseErrorTag(body);
                _logger.LogWarning("OneDrive token exchange failed with status {Status} ({Tag}).", (int)response.StatusCode, tag ?? "<none>");
                return Result<byte[]>.Fail(ErrorCode.TokenRefreshFailed, "Could not complete sign-in with the tail.");
            }

            TokenResponseShape? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(body, OneDriveTokenJsonContext.Default.TokenResponseShape);
            }
            catch (JsonException ex)
            {
                return Result<byte[]>.Fail(ErrorCode.ProviderApiChanged, "The tail returned an unrecognised sign-in response.", ex);
            }

            if (parsed is null || string.IsNullOrEmpty(parsed.RefreshToken))
            {
                return Result<byte[]>.Fail(ErrorCode.ProviderApiChanged, "The tail did not return a durable sign-in.");
            }

            byte[] envelope = ProviderTokenCrypto.Encrypt(parsed.RefreshToken, dek);
            return Result<byte[]>.Ok(envelope);
        }
        catch (OperationCanceledException ex)
        {
            return Result<byte[]>.Fail(ErrorCode.Cancelled, "ExchangeCodeAsync cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            return Result<byte[]>.Fail(ErrorCode.ProviderUnreachable, "Could not reach the tail to complete sign-in.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during OneDrive token exchange.");
            return Result<byte[]>.Fail(ErrorCode.Unknown, "Unexpected error completing sign-in.", ex);
        }
    }

    /// <inheritdoc/>
    public Task<Result<ValidationResult>> ValidatePathAsync(string path, string skinkRoot, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(Result<ValidationResult>.Fail(ErrorCode.Cancelled, "ValidatePathAsync cancelled."));
        }

        // Empty path is permitted; the tail defaults to the standard backup root.
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(Result<ValidationResult>.Ok(ValidationResult.Valid));
        }

        ReadOnlySpan<char> illegal = ['\\', ':', '*', '?', '"', '<', '>', '|'];
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                return Task.FromResult(Result<ValidationResult>.Ok(
                    ValidationResult.Invalid("The folder path may not contain '.' or '..' segments.")));
            }
            foreach (var c in segment)
            {
                if (illegal.Contains(c) || char.IsControl(c))
                {
                    return Task.FromResult(Result<ValidationResult>.Ok(
                        ValidationResult.Invalid($"The folder name '{segment}' contains a character that is not allowed.")));
                }
            }
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                return Task.FromResult(Result<ValidationResult>.Ok(
                    ValidationResult.Invalid($"The folder name '{segment}' may not end with a space or a dot.")));
            }
        }

        return Task.FromResult(Result<ValidationResult>.Ok(ValidationResult.Valid));
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
            return Result<IStorageProvider>.Fail(ErrorCode.InvalidArgument, "providerId must not be empty.");
        }
        if (string.IsNullOrEmpty(credentials.ClientId))
        {
            return Result<IStorageProvider>.Fail(ErrorCode.InvalidArgument, "An application identifier is required to connect to the tail.");
        }
        if (encryptedToken is null || encryptedToken.Length == 0)
        {
            return Result<IStorageProvider>.Fail(ErrorCode.InvalidArgument, "A stored sign-in is required to connect to the tail.");
        }

        if (!ProviderTokenCrypto.TryDecrypt(encryptedToken, dek, out var refreshToken))
        {
            return Result<IStorageProvider>.Fail(ErrorCode.TokenRevoked, "The stored sign-in for this tail could not be read.");
        }

        string? rootPath = null;
        if (!string.IsNullOrEmpty(providerConfigJson))
        {
            try
            {
                var config = JsonSerializer.Deserialize(providerConfigJson, OneDriveProviderConfigJsonContext.Default.OneDriveProviderConfig);
                rootPath = config?.RootPath;
            }
            catch (JsonException ex)
            {
                return Result<IStorageProvider>.Fail(ErrorCode.InvalidArgument, "The stored tail configuration is unreadable.", ex);
            }
        }

        return await CreateProviderFromConfigAsync(
            providerId, displayName, credentials.ClientId, credentials.ClientSecret ?? string.Empty, refreshToken, rootPath, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Constructs the runtime provider from already-decrypted credentials. No network round-trip:
    /// OneDrive is path-addressed, so the root path is resolved locally (default backup root when the
    /// persisted config omits it).
    /// </summary>
    internal Task<Result<IStorageProvider>> CreateProviderFromConfigAsync(
        string providerId,
        string displayName,
        string clientId,
        string clientSecret,
        string refreshToken,
        string? rootPath,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(Result<IStorageProvider>.Fail(ErrorCode.Cancelled, "CreateProviderFromConfigAsync cancelled."));
        }

        var resolvedRoot = !string.IsNullOrEmpty(rootPath) ? rootPath : "/" + ProviderConstants.CloudRootFolderName;

        var bundleResult = _clientFactory.Create(clientId, clientSecret, refreshToken, _loggerFactory);
        if (!bundleResult.Success)
        {
            return Task.FromResult(Result<IStorageProvider>.Fail(bundleResult.Error));
        }

        var bundle = bundleResult.Value;
        try
        {
            var provider = new OneDriveProvider(
                providerId,
                displayName,
                bundle,
                resolvedRoot,
                _loggerFactory.CreateLogger<OneDriveProvider>());
            return Task.FromResult(Result<IStorageProvider>.Ok(provider));
        }
        catch (Exception ex)
        {
            bundle.Dispose();
            _logger.LogError(ex, "Unexpected error constructing OneDrive provider for {ProviderId}.", providerId);
            return Task.FromResult(Result<IStorageProvider>.Fail(ErrorCode.Unknown, "Unexpected error connecting to the tail.", ex));
        }
    }

    /// <summary>Extracts the OAuth <c>error</c> code only — never the <c>error_description</c> (principle 26).</summary>
    private static string? TryParseErrorTag(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            var shape = JsonSerializer.Deserialize(body, OneDriveTokenJsonContext.Default.TokenErrorShape);
            return shape?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsTokenClient)
        {
            _tokenExchangeClient.Dispose();
        }
    }
}

internal sealed record TokenResponseShape
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
}

internal sealed record TokenErrorShape
{
    [JsonPropertyName("error")] public string? Error { get; init; }
}

[JsonSerializable(typeof(TokenResponseShape))]
[JsonSerializable(typeof(TokenErrorShape))]
internal sealed partial class OneDriveTokenJsonContext : JsonSerializerContext;
