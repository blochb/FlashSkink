namespace FlashSkink.Core.Abstractions.Providers;

/// <summary>
/// Carrier for the user-supplied BYOC OAuth app credentials that drive an
/// <see cref="IProviderSetup"/> flow. Blueprint §10.3, §24.3.
/// </summary>
/// <remarks>
/// <para>
/// BYOC ("Bring-Your-Own-Cloud") means FlashSkink ships no shared OAuth app credentials — each user
/// supplies their own <see cref="ClientId"/> and <see cref="ClientSecret"/> from the provider's
/// developer console. The CLI's <c>setup add</c> command collects them from arguments and passes
/// them to <see cref="IProviderSetup.GetAuthorizationUriAsync"/> /
/// <see cref="IProviderSetup.ExchangeCodeAsync"/>.
/// </para>
/// <para>
/// For <see cref="ProviderSetupKind.LocalPath"/> providers (FileSystem) the credentials object is
/// <see langword="null"/>; both fields are <see langword="null"/> on a freshly-defaulted instance.
/// Validation that "both fields are non-null for OAuth providers" is enforced inside each
/// <see cref="IProviderSetup"/> implementation via
/// <see cref="Results.ErrorCode.InvalidArgument"/>, not by this record's constructor — partial
/// state is legitimate during interactive setup.
/// </para>
/// <para>
/// <strong>Lifetime:</strong> the <see cref="ClientSecret"/> is plaintext-in-memory only inside
/// <see cref="IProviderSetup.ExchangeCodeAsync"/> and
/// <see cref="IProviderSetup.CreateProviderAsync"/>; thereafter only the DEK-encrypted form
/// (the <c>EncryptedClientSecret</c> column on the <c>Providers</c> brain row) is passed around.
/// Logging the secret is a Principle 26 violation.
/// </para>
/// </remarks>
public sealed record ProviderCredentials
{
    /// <summary>OAuth client ID (not a secret; safe to log).</summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// OAuth client secret. <strong>Never log this value.</strong> Persisted only after
    /// DEK encryption via <c>ProviderTokenCrypto</c>.
    /// </summary>
    public string? ClientSecret { get; init; }
}
