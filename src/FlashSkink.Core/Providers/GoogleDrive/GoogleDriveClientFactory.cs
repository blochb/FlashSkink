using FlashSkink.Core.Abstractions.Results;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers.GoogleDrive;

/// <summary>
/// Production <see cref="IGoogleDriveClientFactory"/>. Wires the Google.Apis SDK's
/// <see cref="GoogleAuthorizationCodeFlow"/> + <see cref="UserCredential"/> into a
/// <see cref="DriveService"/> and a companion <see cref="HttpClient"/> with a matching
/// auth-injecting delegating handler.
/// </summary>
internal sealed class GoogleDriveClientFactory : IGoogleDriveClientFactory
{
    private const string ApplicationName = "FlashSkink";

    /// <inheritdoc/>
    public Result<GoogleDriveClientBundle> Create(
        string clientId,
        string clientSecret,
        string refreshToken,
        ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return Result<GoogleDriveClientBundle>.Fail(
                ErrorCode.InvalidArgument, "Google Drive clientId is required.");
        }
        if (string.IsNullOrEmpty(clientSecret))
        {
            return Result<GoogleDriveClientBundle>.Fail(
                ErrorCode.InvalidArgument, "Google Drive clientSecret is required.");
        }
        if (string.IsNullOrEmpty(refreshToken))
        {
            return Result<GoogleDriveClientBundle>.Fail(
                ErrorCode.InvalidArgument, "Google Drive refreshToken is required.");
        }

        DriveService? driveService = null;
        HttpClient? resumableClient = null;

        try
        {
            // 1. The SDK's OAuth2 flow encapsulates "given a refresh token, give me a current
            //    access token, refreshing if needed". We instantiate it with the BYOC client
            //    secrets and the drive.file scope (least-privilege; can only see files our app
            //    created — see PR plan §6 (resolution)).
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = clientId,
                    ClientSecret = clientSecret,
                },
                Scopes = [DriveService.ScopeConstants.DriveFile],
            });

            // 2. Seed the UserCredential with just the refresh token; the access token starts null
            //    and is fetched lazily on first use.
            var tokenResponse = new TokenResponse { RefreshToken = refreshToken };
            var userCred = new UserCredential(flow, /*userId*/ "user", tokenResponse);

            // 3. Build the DriveService for SDK-style metadata operations.
            driveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = userCred,
                ApplicationName = ApplicationName,
            });

            // 4. Build the companion HttpClient with our own delegating handler that injects the
            //    same UserCredential's access token on every request. We use this for raw resumable
            //    PUT calls, which Drive's resumable protocol expects to target the per-upload
            //    session URI directly.
            var resumableHandler = new GoogleAuthDelegatingHandler(userCred, loggerFactory.CreateLogger<GoogleAuthDelegatingHandler>())
            {
                InnerHandler = new HttpClientHandler(),
            };
            resumableClient = new HttpClient(resumableHandler);

            return Result<GoogleDriveClientBundle>.Ok(new GoogleDriveClientBundle
            {
                DriveService = driveService,
                ResumableUploadClient = resumableClient,
            });
        }
        catch (Exception ex)
        {
            // Dispose anything that managed to construct before the failure.
            try { driveService?.Dispose(); } catch { /* swallow */ }
            try { resumableClient?.Dispose(); } catch { /* swallow */ }

            return Result<GoogleDriveClientBundle>.Fail(
                ErrorCode.Unknown, "Failed to construct Google Drive SDK clients.", ex);
        }
    }
}

/// <summary>
/// Delegating handler that fetches a fresh access token from the supplied
/// <see cref="UserCredential"/> on every outbound request, and refreshes once on a single 401
/// response before giving up.
/// </summary>
internal sealed class GoogleAuthDelegatingHandler : DelegatingHandler
{
    private readonly UserCredential _userCred;
    private readonly ILogger<GoogleAuthDelegatingHandler> _logger;

    public GoogleAuthDelegatingHandler(UserCredential userCred, ILogger<GoogleAuthDelegatingHandler> logger)
    {
        _userCred = userCred;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // First attempt — current access token (refreshed by the SDK lazily if needed).
        await ApplyAuthHeaderAsync(request, cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // 401 path: dispose the failed response, force a refresh, retry once.
        _logger.LogDebug("Drive responded 401; forcing token refresh and retrying once.");
        response.Dispose();

        try
        {
            // RefreshTokenAsync requests a fresh access token unconditionally.
            await _userCred.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TokenResponseException tre)
        {
            // Refresh-token-itself failure: surface as a fresh 401 so the caller can map to
            // TokenRefreshFailed. We construct an HttpResponseMessage rather than throw so the
            // calling provider's HttpRequestException catch doesn't fire — the provider has a
            // separate code path for the second-401 case.
            _logger.LogWarning("Drive refresh-token call failed: {Error}", tre.Error?.Error);
            return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                ReasonPhrase = "Refresh failed",
            };
        }

        // Retry the request once with the new access token. We must clone the request because a
        // single HttpRequestMessage cannot be sent twice.
        var retry = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
        await ApplyAuthHeaderAsync(retry, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAuthHeaderAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _userCred.GetAccessTokenForRequestAsync(authUri: null, cancellationToken: ct).ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content is not null)
        {
            // Buffer the content so we can read it twice. For our resumable PUTs the content is a
            // ByteArrayContent over a 4 MiB span which is small enough to buffer cheaply.
            var bytes = await original.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var newContent = new ByteArrayContent(bytes);
            foreach (var header in original.Content.Headers)
            {
                newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            clone.Content = newContent;
        }

        return clone;
    }
}
