using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Abstractions.Providers;

/// <summary>
/// Abstraction over the RFC 8252 local-loopback OAuth dance used by all
/// <see cref="ProviderSetupKind.OAuth"/> providers. Blueprint §24.5, cross-cutting decision 3 of
/// phase-4-providers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two-method shape.</strong> The interface is split into a synchronous
/// <see cref="Prepare"/> step and an async <see cref="AwaitAuthorizationCodeAsync"/> step so the
/// caller can weave the PKCE challenge into the provider's authorisation URL before the listener
/// runs. A single-method "do everything" interface would force a URL-builder callback parameter
/// that is awkward to test.
/// </para>
/// <para>
/// <strong>Implementations.</strong> §4.2 ships <c>LoopbackOAuthCapture</c> binding a
/// real <c>HttpListener</c> on <c>http://127.0.0.1:{port}/oauth-callback</c>. Tests inject a
/// fake that returns canned outcomes without touching the network.
/// </para>
/// <para>
/// <strong>Lifecycle.</strong> The flow is one-shot: a fresh <see cref="OAuthCaptureContext"/> per
/// setup attempt. The implementation may hold a transient listener between <see cref="Prepare"/>
/// and <see cref="AwaitAuthorizationCodeAsync"/>; both methods together form a single logical
/// operation. Callers SHOULD invoke them in pairs and SHOULD NOT reuse a context across attempts.
/// </para>
/// </remarks>
public interface IOAuthCaptureFlow
{
    /// <summary>
    /// Allocates a free loopback port, builds the redirect URI, and generates a PKCE
    /// verifier+challenge pair. Returns the context the caller embeds in the provider's
    /// authorisation URL (via <see cref="IProviderSetup.GetAuthorizationUriAsync"/>) and feeds
    /// back to <see cref="AwaitAuthorizationCodeAsync"/>.
    /// </summary>
    /// <returns>
    /// <see cref="Result{T}.Ok(T)"/> with the prepared context on success.
    /// <see cref="ErrorCode.Unknown"/> on port-exhaustion or PKCE-generation failure (rare).
    /// </returns>
    Result<OAuthCaptureContext> Prepare();

    /// <summary>
    /// Launches the system browser to <paramref name="authorizationUri"/> and listens on the
    /// loopback redirect described by <paramref name="context"/> until the provider redirects
    /// with <c>?code=…</c>. Returns the authorisation code.
    /// </summary>
    /// <param name="context">Context returned by <see cref="Prepare"/>.</param>
    /// <param name="authorizationUri">URL built by
    /// <see cref="IProviderSetup.GetAuthorizationUriAsync"/> with the
    /// <see cref="OAuthCaptureContext.CodeChallenge"/> and
    /// <see cref="OAuthCaptureContext.RedirectUri"/> embedded.</param>
    /// <param name="ct">Cancellation token. Implementations enforce a 5-minute hard timeout
    /// (Blueprint §24.5) regardless of <paramref name="ct"/>; cancellation maps to
    /// <see cref="ErrorCode.Cancelled"/>, timeout maps to <see cref="ErrorCode.Timeout"/>.</param>
    /// <returns>The authorisation code from the redirect's query string.</returns>
    Task<Result<string>> AwaitAuthorizationCodeAsync(
        OAuthCaptureContext context,
        Uri authorizationUri,
        CancellationToken ct);
}

/// <summary>
/// Context produced by <see cref="IOAuthCaptureFlow.Prepare"/> and consumed by
/// <see cref="IOAuthCaptureFlow.AwaitAuthorizationCodeAsync"/> plus
/// <see cref="IProviderSetup.GetAuthorizationUriAsync"/> /
/// <see cref="IProviderSetup.ExchangeCodeAsync"/>.
/// </summary>
/// <param name="RedirectUri">
/// Full redirect URI bound by the listener (e.g. <c>"http://127.0.0.1:54321/oauth-callback"</c>).
/// Both the authorisation URL and the token-exchange request must include this exact value.
/// </param>
/// <param name="CodeChallenge">
/// Base64url-encoded SHA-256 hash of <see cref="CodeVerifier"/>. The caller embeds this in the
/// authorisation URL as <c>code_challenge=...&amp;code_challenge_method=S256</c>.
/// </param>
/// <param name="CodeVerifier">
/// High-entropy random string (RFC 7636 §4.1). The caller embeds this in the token-exchange
/// request as <c>code_verifier=...</c>. <strong>Never log this value.</strong>
/// </param>
public sealed record OAuthCaptureContext(
    string RedirectUri,
    string CodeChallenge,
    string CodeVerifier);
