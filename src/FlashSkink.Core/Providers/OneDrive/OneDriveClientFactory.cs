using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers.OneDrive;

/// <summary>
/// Default <see cref="IOneDriveClientFactory"/>: builds an authed client wrapping a
/// <see cref="MicrosoftAuthDelegatingHandler"/> (which injects and refreshes the bearer token) plus a
/// plain client for pre-signed range PUTs.
/// </summary>
internal sealed class OneDriveClientFactory : IOneDriveClientFactory
{
    public Result<OneDriveClientBundle> Create(string clientId, string clientSecret, string refreshToken, ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Result<OneDriveClientBundle>.Fail(ErrorCode.InvalidArgument, "A tail registration is missing its application identifier.");
        }

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Result<OneDriveClientBundle>.Fail(ErrorCode.InvalidArgument, "A tail registration is missing its stored sign-in.");
        }

        MicrosoftAuthDelegatingHandler? authHandler = null;
        HttpClient? authedClient = null;
        HttpClient? plainClient = null;
        try
        {
            authHandler = new MicrosoftAuthDelegatingHandler(
                clientId,
                clientSecret,
                refreshToken,
                loggerFactory.CreateLogger<MicrosoftAuthDelegatingHandler>())
            {
                InnerHandler = new HttpClientHandler(),
            };

            authedClient = new HttpClient(authHandler, disposeHandler: true);
            plainClient = new HttpClient();

            var bundle = new OneDriveClientBundle
            {
                AuthedClient = authedClient,
                PlainClient = plainClient,
            };

            return Result<OneDriveClientBundle>.Ok(bundle);
        }
        catch (Exception ex)
        {
            try
            {
                authedClient?.Dispose();
            }
            catch
            {
                // Best-effort cleanup.
            }

            try
            {
                plainClient?.Dispose();
            }
            catch
            {
                // Best-effort cleanup.
            }

            if (authedClient is null)
            {
                try
                {
                    authHandler?.Dispose();
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }

            return Result<OneDriveClientBundle>.Fail(ErrorCode.Unknown, "Failed to prepare the connection to this tail.", ex);
        }
    }
}

/// <summary>
/// Injects a Microsoft identity bearer token on every request and transparently refreshes it once on
/// a 401 using the stored refresh token (OAuth 2.0 refresh-token grant against the v2.0 endpoint).
/// </summary>
internal sealed class MicrosoftAuthDelegatingHandler : DelegatingHandler
{
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string Scope = "Files.ReadWrite offline_access";

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _refreshToken;
    private readonly ILogger<MicrosoftAuthDelegatingHandler> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;

    public MicrosoftAuthDelegatingHandler(string clientId, string clientSecret, string refreshToken, ILogger<MicrosoftAuthDelegatingHandler> logger)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _refreshToken = refreshToken;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ApplyAuthHeader(request);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();

        try
        {
            await RefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh the stored sign-in for a tail.");
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { ReasonPhrase = "Refresh failed" };
        }

        HttpRequestMessage retry = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
        ApplyAuthHeader(retry);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the cached bearer token if one is held. The token is acquired lazily: the first request
    /// goes out unauthenticated, the inevitable 401 triggers a single refresh, and the retry carries
    /// the new token. Subsequent requests reuse the cached token.
    /// </summary>
    private void ApplyAuthHeader(HttpRequestMessage request)
    {
        if (_accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        }
    }

    private async Task RefreshAccessTokenAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("client_id", _clientId),
                new("grant_type", "refresh_token"),
                new("refresh_token", _refreshToken),
                new("scope", Scope),
            };

            if (!string.IsNullOrEmpty(_clientSecret))
            {
                form.Add(new KeyValuePair<string, string>("client_secret", _clientSecret));
            }

            using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form),
            };

            // Route through the inner pipeline (base.SendAsync), NOT a fresh HttpClient: keeps the
            // token endpoint on the same transport as the Graph calls and bypasses this handler's
            // own 401-retry override (no recursion).
            using HttpResponseMessage response = await base.SendAsync(refreshRequest, ct).ConfigureAwait(false);

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Token endpoint returned {(int)response.StatusCode}.");
            }

            TokenRefreshShape? parsed = JsonSerializer.Deserialize(body, MicrosoftTokenJsonContext.Default.TokenRefreshShape);
            if (parsed is null || string.IsNullOrEmpty(parsed.AccessToken))
            {
                throw new InvalidOperationException("Token endpoint response did not contain an access token.");
            }

            _accessToken = parsed.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            byte[] buffered = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var newContent = new ByteArrayContent(buffered);
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
            {
                newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = newContent;
        }

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshLock.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed record TokenRefreshShape
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }
}

[JsonSerializable(typeof(TokenRefreshShape))]
internal sealed partial class MicrosoftTokenJsonContext : JsonSerializerContext;
