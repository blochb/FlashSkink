namespace FlashSkink.Core.Abstractions.Providers;

/// <summary>
/// Classifies how an <see cref="IProviderSetup"/> implementation obtains the credential material
/// the brain persists for a tail. Blueprint §10.3.
/// </summary>
/// <remarks>
/// The kind determines which <see cref="IProviderSetup"/> methods are meaningful for a given
/// provider:
/// <list type="bullet">
/// <item><description><see cref="OAuth"/> — <see cref="IProviderSetup.GetAuthorizationUriAsync"/>
/// and <see cref="IProviderSetup.ExchangeCodeAsync"/> are required;
/// <see cref="IProviderSetup.ValidatePathAsync"/> returns
/// <see cref="ValidationResult.Invalid(string)"/>.</description></item>
/// <item><description><see cref="LocalPath"/> — only
/// <see cref="IProviderSetup.ValidatePathAsync"/> is required; the OAuth methods return
/// <see cref="Results.ErrorCode.InvalidArgument"/>.</description></item>
/// <item><description><see cref="ApiKey"/> — reserved for V2+; no V1 provider uses this kind.</description></item>
/// </list>
/// Values are ordered with <see cref="OAuth"/> = 0 so the default of an uninitialised field maps
/// to the most common case across V1 providers.
/// </remarks>
public enum ProviderSetupKind
{
    /// <summary>Provider uses BYOC OAuth (Google Drive, Dropbox, OneDrive).</summary>
    OAuth = 0,

    /// <summary>Provider uses a single API key. Reserved for V2+.</summary>
    ApiKey = 1,

    /// <summary>Provider is configured by a local-or-NAS filesystem path (FileSystem).</summary>
    LocalPath = 2,
}
