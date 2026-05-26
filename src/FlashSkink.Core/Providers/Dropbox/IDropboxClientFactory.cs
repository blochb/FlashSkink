using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers.Dropbox;

/// <summary>
/// Construction seam for the per-volume Dropbox SDK client. Production binds to
/// <see cref="DropboxClientFactory"/>; tests bind to a fake that returns a
/// <see cref="DropboxClientBundle"/> whose HTTP client is backed by a recording handler.
/// Cross-cutting decision 4 of phase-4-providers (per-volume SDK clients, disposed by the registry).
/// </summary>
/// <remarks>
/// <para>
/// Construction never performs network I/O — it builds the in-memory plumbing. The first call
/// against the returned <see cref="DropboxClientBundle.Client"/> triggers the SDK's access-token
/// refresh flow against the user's BYOC app, which is where authentication failures surface as
/// <see cref="ErrorCode.TokenRefreshFailed"/>.
/// </para>
/// </remarks>
internal interface IDropboxClientFactory
{
    /// <summary>
    /// Constructs a <see cref="DropboxClientBundle"/> from the user's BYOC Dropbox app credentials
    /// plus the freshly-decrypted refresh token.
    /// </summary>
    /// <param name="appKey">Dropbox app key (non-null, non-empty). Equivalent to OAuth "ClientId".</param>
    /// <param name="appSecret">Dropbox app secret (non-null, non-empty; cleartext — must not be logged).</param>
    /// <param name="refreshToken">OAuth refresh token (cleartext — must not be logged).</param>
    /// <param name="loggerFactory">Logger factory for the bundle's diagnostics.</param>
    Result<DropboxClientBundle> Create(
        string appKey,
        string appSecret,
        string refreshToken,
        ILoggerFactory loggerFactory);
}
