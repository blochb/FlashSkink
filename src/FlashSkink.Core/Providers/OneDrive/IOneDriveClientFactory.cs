using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers.OneDrive;

/// <summary>Builds an <see cref="OneDriveClientBundle"/> from Microsoft identity credentials.</summary>
internal interface IOneDriveClientFactory
{
    /// <summary>
    /// Creates an authed + plain <see cref="HttpClient"/> pair for a OneDrive tail. The
    /// <paramref name="clientSecret"/> may be empty for public (PKCE-only) app registrations.
    /// </summary>
    Result<OneDriveClientBundle> Create(string clientId, string clientSecret, string refreshToken, ILoggerFactory loggerFactory);
}
