namespace FlashSkink.Core.Abstractions.Models;

/// <summary>
/// Describes a tail to add via <c>FlashSkinkVolume.AddTailAsync</c>. Carries the provider type plus
/// the credential material the volume needs to run the provider's setup flow. Blueprint §11.
/// </summary>
/// <remarks>
/// <para>
/// For local-folder tails (<c>ProviderType == "filesystem"</c>) only <see cref="LocalPath"/> is
/// required; all OAuth fields stay <see langword="null"/>.
/// </para>
/// <para>
/// For cloud tails the caller (the CLI) first runs the loopback OAuth dance and supplies
/// <see cref="ClientId"/>, <see cref="ClientSecret"/>, <see cref="AuthorizationCode"/>,
/// <see cref="CodeVerifier"/>, and <see cref="RedirectUri"/>. The plaintext
/// <see cref="ClientSecret"/> lives on this record only for the duration of the
/// <c>AddTailAsync</c> call (phase-4 cross-cutting decision 2); the OAuth refresh token never
/// appears here — <c>AddTailAsync</c> derives and encrypts it internally from
/// <see cref="AuthorizationCode"/>.
/// </para>
/// </remarks>
public sealed record TailConfiguration
{
    /// <summary>Provider type token: <c>"filesystem"</c> | <c>"google-drive"</c> | <c>"dropbox"</c> | <c>"onedrive"</c>.</summary>
    public required string ProviderType { get; init; }

    /// <summary>
    /// Stable provider id (brain primary key). When <see langword="null"/>, <c>AddTailAsync</c> uses
    /// <see cref="ProviderType"/> (V1 allows one tail per provider type).
    /// </summary>
    public string? ProviderId { get; init; }

    /// <summary>Display name shown to the user. When <see langword="null"/>, the provider setup's own display name is used.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Local folder for <c>ProviderSetupKind.LocalPath</c> providers (filesystem). <see langword="null"/> for OAuth providers.</summary>
    public string? LocalPath { get; init; }

    /// <summary>BYOC OAuth client id (not secret). Required for OAuth providers.</summary>
    public string? ClientId { get; init; }

    /// <summary>BYOC OAuth client secret (plaintext). Required for OAuth providers; encrypted before persisting.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>OAuth authorization code captured via the loopback flow. When set, <c>AddTailAsync</c> runs the token exchange.</summary>
    public string? AuthorizationCode { get; init; }

    /// <summary>PKCE verifier paired with the challenge used to obtain <see cref="AuthorizationCode"/>.</summary>
    public string? CodeVerifier { get; init; }

    /// <summary>Loopback redirect URI used during the OAuth dance.</summary>
    public string? RedirectUri { get; init; }
}
