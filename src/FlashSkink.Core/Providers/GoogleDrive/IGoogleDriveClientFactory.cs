using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers.GoogleDrive;

/// <summary>
/// Construction seam for the per-volume Google Drive client objects. Production binds to
/// <see cref="GoogleDriveClientFactory"/>; tests bind to a fake that routes through a recording
/// <see cref="HttpMessageHandler"/>. Cross-cutting decision 4 of phase-4-providers (per-volume SDK
/// clients, disposed by the registry).
/// </summary>
/// <remarks>
/// <para>
/// The returned <see cref="GoogleDriveClientBundle"/> owns both the SDK's <c>DriveService</c> and a
/// companion <see cref="HttpClient"/> used for the raw resumable-upload PUTs that bypass the SDK's
/// <c>MediaUpload</c> helper. Disposing the bundle disposes both. The bundle becomes the property
/// of the constructed <see cref="GoogleDriveProvider"/>, which disposes it on
/// <see cref="IAsyncDisposable.DisposeAsync"/>.
/// </para>
/// <para>
/// Construction never performs network I/O — it builds the in-memory plumbing. The first call
/// against the returned <c>DriveService</c> triggers the SDK's token-refresh flow, which is where
/// authentication failures surface.
/// </para>
/// </remarks>
internal interface IGoogleDriveClientFactory
{
    /// <summary>
    /// Constructs a <see cref="GoogleDriveClientBundle"/> from the user's BYOC OAuth client
    /// credentials plus the freshly-decrypted refresh token.
    /// </summary>
    /// <param name="clientId">BYOC OAuth client ID (non-null, non-empty).</param>
    /// <param name="clientSecret">BYOC OAuth client secret (non-null, non-empty; cleartext —
    /// must not be logged).</param>
    /// <param name="refreshToken">OAuth refresh token (cleartext — must not be logged).</param>
    /// <param name="loggerFactory">Logger factory for the bundle's own diagnostics.</param>
    Result<GoogleDriveClientBundle> Create(
        string clientId,
        string clientSecret,
        string refreshToken,
        ILoggerFactory loggerFactory);
}
