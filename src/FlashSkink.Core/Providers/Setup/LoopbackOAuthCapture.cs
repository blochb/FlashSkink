using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers.Setup;

/// <summary>
/// Production <see cref="IOAuthCaptureFlow"/> — runs the RFC 8252 local-loopback OAuth dance
/// against a real <see cref="HttpListener"/> bound to <c>http://127.0.0.1:{random-port}/oauth-callback/</c>.
/// Blueprint §24.5; cross-cutting decision 3 of phase-4-providers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lifecycle.</strong> Each setup flow is two calls: <see cref="Prepare"/> allocates a
/// free loopback port, starts the listener, and generates a fresh PKCE verifier+challenge pair.
/// <see cref="AwaitAuthorizationCodeAsync"/> then launches the system browser to the provider's
/// authorisation URL and awaits the redirect, with a 5-minute hard timeout (Blueprint §24.5).
/// The instance may handle multiple concurrent flows (one per distinct <c>RedirectUri</c>); each
/// flow is one-shot — the context is consumed by the await call.
/// </para>
/// <para>
/// <strong>Security.</strong> The listener binds only to <see cref="IPAddress.Loopback"/>, not to
/// any external interface — an attacker on the same LAN cannot intercept the redirect. PKCE
/// (RFC 7636) defends against a malicious local process racing to claim the redirect: only the
/// flow that holds the verifier can complete the token exchange.
/// </para>
/// <para>
/// <strong>Threading.</strong> <see cref="Prepare"/>, <see cref="AwaitAuthorizationCodeAsync"/>,
/// and <see cref="Dispose"/> are safe to call concurrently. Listeners are tracked in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by the full redirect URI.
/// </para>
/// <para>
/// Principle 26: this type never logs the PKCE verifier, the authorisation code, the full
/// authorisation URI, or any provider <c>error_description</c> value (providers sometimes echo
/// user-supplied parameters there).
/// </para>
/// </remarks>
public sealed class LoopbackOAuthCapture : IOAuthCaptureFlow, IDisposable
{
    /// <summary>Suffix appended to the loopback URI. <see cref="HttpListener"/> requires the trailing slash.</summary>
    private const string RedirectPath = "/oauth-callback/";

    /// <summary>Entropy bytes for the PKCE verifier (RFC 7636 §4.1 — 43-128 char base64url is valid).</summary>
    private const int VerifierEntropyBytes = 32;

    private const string SuccessHtml =
        "<html><body style=\"font-family:sans-serif;padding:2em;\">"
        + "<h2>FlashSkink &mdash; sign-in complete</h2>"
        + "<p>You can close this browser tab and return to the terminal.</p>"
        + "</body></html>";

    private const string ErrorHtmlTemplate =
        "<html><body style=\"font-family:sans-serif;padding:2em;\">"
        + "<h2>FlashSkink &mdash; sign-in did not complete</h2>"
        + "<p>{0}</p>"
        + "<p>Return to the terminal for next steps.</p>"
        + "</body></html>";

    private readonly IBrowserLauncher _browserLauncher;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;
    private readonly ILogger<LoopbackOAuthCapture> _logger;

    // Active flows keyed by full RedirectUri string ("http://127.0.0.1:54321/oauth-callback/").
    // Listeners stay tracked here until Dispose — we deliberately do NOT stop them at the end
    // of a successful await, because http.sys delivers the response asynchronously and an
    // immediate listener.Stop() races against the kernel flush (observed as the client seeing
    // RST mid-response). Disposal is the authoritative cleanup point. Concurrent because tests
    // may exercise parallel flows; production OAuth setup is single-flow.
    private readonly ConcurrentDictionary<string, FlowState> _flows = new(StringComparer.Ordinal);

    // 0 = live, 1 = disposed. Interlocked.Exchange / Volatile.Read so the XML doc's
    // "safe to call concurrently" contract actually holds (a torn read on ARM or a double-entry
    // race on x86 would otherwise let two Dispose calls both run the teardown loop).
    private int _disposed;

    private sealed class FlowState
    {
        public required HttpListener Listener { get; init; }
        public bool Consumed;
    }

    /// <summary>
    /// Production constructor — uses <see cref="SystemBrowserLauncher"/>,
    /// <see cref="TimeProvider.System"/>, and the 5-minute timeout from Blueprint §24.5.
    /// </summary>
    public LoopbackOAuthCapture(ILoggerFactory loggerFactory)
        : this(new SystemBrowserLauncher(), loggerFactory, TimeProvider.System, TimeSpan.FromMinutes(5))
    {
    }

    /// <summary>
    /// Test constructor — lets the integration tests inject a recording browser launcher, a
    /// custom <see cref="TimeProvider"/>, and a short timeout. <c>internal</c> so production
    /// callers cannot bypass the OS launcher or the 5-minute budget.
    /// </summary>
    internal LoopbackOAuthCapture(
        IBrowserLauncher browserLauncher,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(browserLauncher);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _browserLauncher = browserLauncher;
        _timeProvider = timeProvider;
        _timeout = timeout;
        _logger = loggerFactory.CreateLogger<LoopbackOAuthCapture>();
    }

    /// <inheritdoc/>
    public Result<OAuthCaptureContext> Prepare(int? preferredPort = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(LoopbackOAuthCapture));
        }

        if (preferredPort is { } requested && requested is < 1 or > 65535)
        {
            return Result<OAuthCaptureContext>.Fail(
                ErrorCode.InvalidArgument,
                $"preferredPort must be in 1..65535; got {requested}.");
        }

        HttpListener? listener = null;
        try
        {
            // A fixed port is for providers that require an exactly-registered redirect URI
            // (IRequiresFixedRedirectPort, e.g. Dropbox); null keeps the RFC 8252 ephemeral port.
            var port = preferredPort ?? AllocateFreeLoopbackPort();
            var redirectUri = $"http://127.0.0.1:{port}{RedirectPath}";

            listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            var (verifier, challenge) = GeneratePkcePair();

            // On the extraordinarily rare port-reuse-within-process case, dispose the prior
            // entry so we don't leak.
            if (_flows.TryGetValue(redirectUri, out var existing))
            {
                try { existing.Listener.Stop(); } catch (ObjectDisposedException) { }
                try { ((IDisposable)existing.Listener).Dispose(); } catch (ObjectDisposedException) { }
            }
            _flows[redirectUri] = new FlowState { Listener = listener };

            return Result<OAuthCaptureContext>.Ok(new OAuthCaptureContext(
                RedirectUri: redirectUri,
                CodeChallenge: challenge,
                CodeVerifier: verifier));
        }
        catch (SocketException ex)
        {
            CleanupListenerOnFailure(listener);
            _logger.LogWarning(ex, "Failed to allocate loopback port for OAuth capture.");
            return Result<OAuthCaptureContext>.Fail(
                ErrorCode.Unknown,
                $"Failed to allocate loopback port for OAuth capture: {ex.Message}",
                ex);
        }
        catch (HttpListenerException ex)
        {
            CleanupListenerOnFailure(listener);
            _logger.LogWarning(ex, "Failed to start loopback HTTP listener for OAuth capture.");
            return Result<OAuthCaptureContext>.Fail(
                ErrorCode.Unknown,
                $"Failed to start loopback HTTP listener for OAuth capture: {ex.Message}",
                ex);
        }
        catch (Exception ex)
        {
            CleanupListenerOnFailure(listener);
            _logger.LogWarning(ex, "Unexpected failure preparing OAuth capture.");
            return Result<OAuthCaptureContext>.Fail(
                ErrorCode.Unknown,
                $"Unexpected failure preparing OAuth capture: {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<string>> AwaitAuthorizationCodeAsync(
        OAuthCaptureContext context,
        Uri authorizationUri,
        CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(LoopbackOAuthCapture));
        }
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizationUri);

        try
        {
            ct.ThrowIfCancellationRequested();

            // Atomically claim the flow exactly once. The listener stays in _flows for Dispose
            // to clean up; we never stop or dispose it here, even on success — http.sys
            // delivers the response asynchronously and Stop() races the kernel flush.
            if (!_flows.TryGetValue(context.RedirectUri, out var flow))
            {
                return Result<string>.Fail(
                    ErrorCode.InvalidArgument,
                    "Context was not produced by Prepare() on this instance.");
            }
            lock (flow)
            {
                if (flow.Consumed)
                {
                    return Result<string>.Fail(
                        ErrorCode.InvalidArgument,
                        "Context has already been consumed.");
                }
                flow.Consumed = true;
            }

            var listener = flow.Listener;

            // Launch the browser first; on failure the caller learns the OS browser is broken
            // before they wait 5 minutes.
            try
            {
                _browserLauncher.Launch(authorizationUri);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to launch system browser for OAuth capture.");
                return Result<string>.Fail(
                    ErrorCode.Unknown,
                    $"Failed to launch the system browser: {ex.Message}",
                    ex);
            }

            using var timeoutCts = new CancellationTokenSource(_timeout, _timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // Stop the listener when cancellation or timeout fires so GetContextAsync unblocks.
            await using var _ = linkedCts.Token.UnsafeRegister(static state =>
            {
                var l = (HttpListener)state!;
                try { l.Stop(); }
                catch (ObjectDisposedException) { /* already disposed */ }
                catch (HttpListenerException) { /* already stopped */ }
            }, listener).ConfigureAwait(false);

            HttpListenerContext httpContext;
            try
            {
                httpContext = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (linkedCts.Token.IsCancellationRequested)
            {
                return WakeupSignalToResult(ct, timeoutCts.Token);
            }
            catch (ObjectDisposedException) when (linkedCts.Token.IsCancellationRequested)
            {
                return WakeupSignalToResult(ct, timeoutCts.Token);
            }

            var query = ParseQuery(httpContext.Request.Url?.Query);

            if (query.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
            {
                // Principle 26: we log the provider's error code (a fixed vocabulary like
                // "access_denied" — not a secret) but NOT error_description (free-form,
                // sometimes echoes user-supplied state).
                _logger.LogWarning("OAuth provider returned error code: {OAuthError}", error);
                WriteResponse(
                    httpContext.Response,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        ErrorHtmlTemplate,
                        $"The sign-in provider reported: {System.Net.WebUtility.HtmlEncode(error)}."));
                return Result<string>.Fail(
                    ErrorCode.ProviderAuthFailed,
                    $"OAuth provider returned error: {error}.");
            }

            if (!query.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            {
                _logger.LogWarning("OAuth callback did not contain an authorisation code.");
                WriteResponse(
                    httpContext.Response,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        ErrorHtmlTemplate,
                        "The callback did not contain an authorisation code."));
                return Result<string>.Fail(
                    ErrorCode.ProviderAuthFailed,
                    "Callback did not contain ?code=...");
            }

            WriteResponse(httpContext.Response, SuccessHtml);
            return Result<string>.Ok(code);
        }
        catch (OperationCanceledException ex)
        {
            return Result<string>.Fail(
                ErrorCode.Cancelled, "OAuth capture was cancelled.", ex);
        }
        catch (HttpListenerException ex)
        {
            _logger.LogWarning(ex, "Loopback HTTP listener failed during OAuth capture.");
            return Result<string>.Fail(
                ErrorCode.ProviderUnreachable,
                $"Loopback HTTP listener failed: {ex.Message}",
                ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected failure awaiting OAuth callback.");
            return Result<string>.Fail(
                ErrorCode.Unknown,
                $"Unexpected failure awaiting OAuth callback: {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Exchange returns the *previous* value; if it was already 1, another caller is already
        // running (or has already run) the teardown loop — bail out so we don't double-stop.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (var kvp in _flows)
        {
            try { kvp.Value.Listener.Stop(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Listener stop failed during dispose."); }

            try { ((IDisposable)kvp.Value.Listener).Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Listener dispose failed during dispose."); }
        }
        _flows.Clear();
    }

    /// <summary>
    /// Standard "allocate a free port" idiom: bind a probe <see cref="TcpListener"/> on port 0,
    /// read the assigned port from the local endpoint, then stop the probe. The brief TOCTOU
    /// window between probe.Stop() and the subsequent HttpListener.Start() is the well-known
    /// cost of this pattern; the kernel re-allocates a fresh port long before any third party
    /// could bind it.
    /// </summary>
    private static int AllocateFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>
    /// RFC 7636 PKCE: random 32-byte verifier (base64url-encoded), challenge =
    /// base64url(SHA256(ASCII(verifier))). The verifier is treated as ASCII bytes before
    /// hashing per RFC 7636 §4.2.
    /// </summary>
    private static (string Verifier, string Challenge) GeneratePkcePair()
    {
        Span<byte> verifierBytes = stackalloc byte[VerifierEntropyBytes];
        RandomNumberGenerator.Fill(verifierBytes);
        var verifier = Base64Url.EncodeToString(verifierBytes);

        Span<byte> challengeHash = stackalloc byte[SHA256.HashSizeInBytes];
        // verifier is ASCII by construction (base64url alphabet), so Encoding.ASCII is correct.
        var verifierAscii = Encoding.ASCII.GetBytes(verifier);
        SHA256.HashData(verifierAscii, challengeHash);
        var challenge = Base64Url.EncodeToString(challengeHash);

        return (verifier, challenge);
    }

    private void CleanupListenerOnFailure(HttpListener? listener)
    {
        if (listener is null)
        {
            return;
        }
        try
        {
            if (listener.IsListening)
            {
                listener.Stop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort listener stop after Prepare failure.");
        }
        try
        {
            ((IDisposable)listener).Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort listener dispose after Prepare failure.");
        }
    }

    /// <summary>
    /// Translate a "listener was woken up by cancellation" exception into the right Result.
    /// Cancellation takes priority over timeout when both fired.
    /// </summary>
    private Result<string> WakeupSignalToResult(CancellationToken ct, CancellationToken timeoutToken)
    {
        if (ct.IsCancellationRequested)
        {
            return Result<string>.Fail(ErrorCode.Cancelled, "OAuth capture was cancelled.");
        }
        if (timeoutToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "OAuth capture timed out after {TimeoutSeconds}s.", _timeout.TotalSeconds);
            return Result<string>.Fail(
                ErrorCode.Timeout,
                $"OAuth capture timed out after {_timeout}.");
        }
        // Defensive: a wakeup signal arrived without a known cause.
        return Result<string>.Fail(
            ErrorCode.Unknown,
            "OAuth capture listener stopped unexpectedly.");
    }

    /// <summary>
    /// Writes <paramref name="html"/> as the response body and closes the response. Synchronous
    /// because <see cref="HttpListenerResponse.Close()"/> queues the bytes to http.sys and
    /// returns immediately; an async wrapper would add no value. <c>KeepAlive = false</c> tells
    /// http.sys to send a <c>Connection: close</c> header so the client knows the response is
    /// terminal — this combined with leaving the listener alive (instead of stopping it
    /// immediately) avoids the kernel-flush race that resets the client connection mid-read.
    /// </summary>
    private static void WriteResponse(HttpListenerResponse response, string html)
    {
        try
        {
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.KeepAlive = false;
            var bytes = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = bytes.LongLength;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }
        catch (HttpListenerException) { /* client closed the connection — nothing to do */ }
        catch (ObjectDisposedException) { /* response already disposed */ }
    }

    /// <summary>
    /// Minimal query-string parser — extracts the <c>code</c> and <c>error</c> keys we care
    /// about. Returns a dictionary keyed by URL-decoded key, with URL-decoded values.
    /// </summary>
    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        var span = query.AsSpan();
        if (span.Length > 0 && span[0] == '?')
        {
            span = span[1..];
        }

        foreach (var pairRange in span.Split('&'))
        {
            var pair = span[pairRange];
            if (pair.Length == 0)
            {
                continue;
            }

            var eqIndex = pair.IndexOf('=');
            if (eqIndex < 0)
            {
                result[Uri.UnescapeDataString(pair.ToString())] = string.Empty;
            }
            else
            {
                var key = Uri.UnescapeDataString(pair[..eqIndex].ToString());
                var value = Uri.UnescapeDataString(pair[(eqIndex + 1)..].ToString());
                result[key] = value;
            }
        }

        return result;
    }
}
