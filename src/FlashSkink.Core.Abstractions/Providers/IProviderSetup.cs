using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Abstractions.Providers;

/// <summary>
/// Drives a brand-new tail through credential collection, validation, token exchange (for
/// <see cref="ProviderSetupKind.OAuth"/> providers), and construction of the runtime
/// <see cref="IStorageProvider"/>. Blueprint §10.3, §24.3.
/// </summary>
/// <remarks>
/// <para>
/// One implementation per provider type. Implementations live alongside their
/// <see cref="IStorageProvider"/> (e.g. <c>FileSystemProviderSetup</c> next to
/// <c>FileSystemProvider</c>; cloud setups arrive in §4.3–4.5).
/// </para>
/// <para>
/// <strong>Departures from Blueprint §10.3.</strong> Three parameters are <em>added</em> beyond
/// the literal Blueprint signature, all additive on the API surface:
/// <list type="bullet">
/// <item><description>PKCE (<c>codeChallenge</c> on
/// <see cref="GetAuthorizationUriAsync"/>, <c>codeVerifier</c> on
/// <see cref="ExchangeCodeAsync"/>) — RFC 8252 requires PKCE for native-app OAuth.</description></item>
/// <item><description><c>skinkRoot</c> on <see cref="ValidatePathAsync"/> — needed so
/// <c>FileSystemProviderSetup</c> can reject paths that are subdirectories of the skink (Blueprint
/// §24.3 — would create a backup loop).</description></item>
/// <item><description><c>providerId</c>, <c>displayName</c>, and <c>providerConfigJson</c> on
/// <see cref="CreateProviderAsync"/> — the runtime <see cref="IStorageProvider"/> needs all three
/// to be useful to the upload orchestrator.</description></item>
/// </list>
/// Every method also takes a final <see cref="CancellationToken"/> per the codebase convention
/// (Principle 13).
/// </para>
/// <para>
/// <strong>Principle 1.</strong> Every method returns <see cref="Result"/> or
/// <see cref="Result{T}"/>; no method throws across this boundary.
/// </para>
/// </remarks>
public interface IProviderSetup
{
    /// <summary>Machine-readable provider type token (e.g. <c>"filesystem"</c>, <c>"google-drive"</c>).</summary>
    string ProviderType { get; }

    /// <summary>Human-readable display name shown to the user in setup output.</summary>
    string DisplayName { get; }

    /// <summary>Setup-flow shape — determines which other methods are meaningful.</summary>
    ProviderSetupKind SetupKind { get; }

    /// <summary>
    /// Builds the authorisation URL the user is directed to in their browser. PKCE per RFC 7636.
    /// </summary>
    /// <param name="redirectUri">Loopback redirect URI prepared by <see cref="IOAuthCaptureFlow.Prepare"/>.</param>
    /// <param name="codeChallenge">PKCE code challenge (base64url of SHA-256 of the verifier).</param>
    /// <param name="credentials">User-supplied BYOC OAuth app credentials. Must have a non-null
    /// <see cref="ProviderCredentials.ClientId"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="ErrorCode.InvalidArgument"/> for <see cref="ProviderSetupKind.LocalPath"/>
    /// providers, or when required fields on <paramref name="credentials"/> are missing.
    /// </returns>
    Task<Result<Uri>> GetAuthorizationUriAsync(
        string redirectUri,
        string codeChallenge,
        ProviderCredentials credentials,
        CancellationToken ct);

    /// <summary>
    /// Exchanges an OAuth authorisation code for a refresh token and returns the
    /// DEK-encrypted refresh-token envelope ready to persist into <c>Providers.EncryptedToken</c>.
    /// </summary>
    /// <param name="code">The authorisation code captured by <see cref="IOAuthCaptureFlow"/>.</param>
    /// <param name="codeVerifier">PKCE verifier paired with the challenge used in
    /// <see cref="GetAuthorizationUriAsync"/>.</param>
    /// <param name="redirectUri">Same redirect URI used in <see cref="GetAuthorizationUriAsync"/>.</param>
    /// <param name="credentials">Same credentials used in <see cref="GetAuthorizationUriAsync"/>.</param>
    /// <param name="dek">32-byte DEK for envelope encryption. Held by reference; the implementation
    /// must not retain it beyond the call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success: the DEK-encrypted refresh-token envelope (<c>ProviderTokenCrypto</c> format).
    /// <see cref="ErrorCode.InvalidArgument"/> for <see cref="ProviderSetupKind.LocalPath"/>
    /// providers.
    /// </returns>
    Task<Result<byte[]>> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        ProviderCredentials credentials,
        ReadOnlyMemory<byte> dek,
        CancellationToken ct);

    /// <summary>
    /// Validates a candidate local path for a <see cref="ProviderSetupKind.LocalPath"/> provider.
    /// </summary>
    /// <param name="path">Candidate root path (e.g. <c>"/mnt/nas/myskink-tail"</c>).</param>
    /// <param name="skinkRoot">Absolute path to the skink root; the validator rejects
    /// <paramref name="path"/> when it equals or is a subdirectory of this value (Blueprint §24.3
    /// — would create a backup loop).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Ok(T)"/> wrapping a <see cref="ValidationResult"/> — the inner value
    /// expresses success-vs-invalid-input, while the outer <see cref="Result{T}"/> surfaces real
    /// I/O errors. Non-<see cref="ProviderSetupKind.LocalPath"/> implementations may return
    /// <see cref="ValidationResult.Invalid"/>.
    /// </returns>
    Task<Result<ValidationResult>> ValidatePathAsync(
        string path,
        string skinkRoot,
        CancellationToken ct);

    /// <summary>
    /// Constructs the runtime <see cref="IStorageProvider"/> from a freshly-completed setup or
    /// from a persisted <c>Providers</c> brain row (used by <c>BrainBackedProviderRegistry</c>).
    /// </summary>
    /// <param name="providerId">Stable provider identifier; becomes <see cref="IStorageProvider.ProviderID"/>.</param>
    /// <param name="displayName">Human-readable display name.</param>
    /// <param name="encryptedToken">DEK-encrypted refresh-token envelope for OAuth providers;
    /// empty array for <see cref="ProviderSetupKind.LocalPath"/>.</param>
    /// <param name="credentials">Client ID + decrypted client secret for OAuth providers;
    /// <see langword="null"/>-equivalent (empty fields) for <see cref="ProviderSetupKind.LocalPath"/>.</param>
    /// <param name="providerConfigJson">Provider-specific config JSON from
    /// <c>Providers.ProviderConfig</c>. For FileSystem, the deserialised
    /// <c>FileSystemProviderConfig</c> shape; <see langword="null"/> when the row has no config.</param>
    /// <param name="dek">32-byte DEK. Must not be retained beyond the call.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<IStorageProvider>> CreateProviderAsync(
        string providerId,
        string displayName,
        byte[] encryptedToken,
        ProviderCredentials credentials,
        string? providerConfigJson,
        ReadOnlyMemory<byte> dek,
        CancellationToken ct);
}
