namespace FlashSkink.Core.Abstractions.Providers;

/// <summary>
/// Optional capability interface declaring that an <see cref="IProviderSetup"/>'s OAuth flow needs
/// the local-loopback redirect URI bound to a <em>fixed</em> port, because the provider does not
/// honor RFC 8252 §7.3 variable-port loopback matching and instead requires the <c>redirect_uri</c>
/// to exactly match a pre-registered value (port included).
/// </summary>
/// <remarks>
/// <para>
/// Capability-interface evolution is the approved mechanism for extending provider/setup behaviour
/// without modifying the frozen <see cref="IProviderSetup"/> contract (Principle 23) — the same
/// pattern as <see cref="ISupportsRemoteHashCheck"/>.
/// </para>
/// <para>
/// The caller (the CLI <c>setup add</c> flow and the live-verification harness) passes
/// <see cref="RedirectPort"/> to <see cref="IOAuthCaptureFlow.Prepare"/> so the loopback listener
/// binds that exact port, and the developer registers
/// <c>http://127.0.0.1:{RedirectPort}/oauth-callback/</c> in the provider's app console. Providers
/// that honor dynamic loopback ports (e.g. Google Drive) do <i>not</i> implement this interface and
/// keep using an ephemeral port. Implemented in V1 by <c>DropboxSetup</c>.
/// </para>
/// </remarks>
public interface IRequiresFixedRedirectPort
{
    /// <summary>
    /// The fixed loopback port to bind for this provider's OAuth redirect. The developer registers
    /// <c>http://127.0.0.1:{RedirectPort}/oauth-callback/</c> as an authorized redirect URI. Pure
    /// accessor — never throws.
    /// </summary>
    int RedirectPort { get; }
}
