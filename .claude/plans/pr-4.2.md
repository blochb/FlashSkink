# PR 4.2 — OAuth loopback capture

**Branch:** `pr/4.2-oauth-loopback-capture`
**Blueprint sections:** §24.5 (Local-loopback OAuth capture — canonical description), §24.3 (two-stage CLI flow — what the helper enables), §3 (zero trust in the host — no host-side state from the OAuth dance).
**Dev plan section:** phase-4-providers §4.2.
**Phase-4 cross-cutting decisions touched:** 3 (single shared OAuth helper, all three cloud providers use this).

---

## Scope

Implement the production `IOAuthCaptureFlow` introduced in §4.1: a real local-loopback HTTP listener bound to `127.0.0.1:{random-port}/oauth-callback/` that captures the `?code=…` redirect from the user's browser after they consent on the provider's authorisation page.

Five new files:

1. **`LoopbackOAuthCapture`** (internal sealed, in `Core/Providers/Setup/`) — implements `IOAuthCaptureFlow`. `Prepare()` allocates a free loopback port, starts an `HttpListener` on that port, generates a fresh PKCE verifier+challenge pair, and returns the context. `AwaitAuthorizationCodeAsync(context, authorizationUri, ct)` launches the system browser to `authorizationUri`, awaits a redirect to the prepared loopback URI (5-minute hard timeout), parses `?code=…` from the query, writes a small HTML success page to the browser, and returns the code.
2. **`IBrowserLauncher`** (internal, in `Core/Providers/Setup/`) — single-method seam over the OS browser launch (`void Launch(Uri url)`). Production: `SystemBrowserLauncher`. Tests: a recording fake that captures the URL without spawning a real browser.
3. **`SystemBrowserLauncher`** (internal sealed, in `Core/Providers/Setup/`) — OS-specific launcher. Windows: `Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })`. macOS: `Process.Start("open", url)`. Linux: `Process.Start("xdg-open", url)`. Sanctioned platform-branching for the launcher only (cross-cutting decision 3, "OS-specific branching only for the launcher — the listener itself is identical on every OS", and Principle 12 carve-out for explicitly justified platform-specific code).
4. **Tests** for the fake (`FakeOAuthCaptureFlowTests`), the real helper (`LoopbackOAuthCaptureTests`), and the browser launcher (smoke test).

Plus one test-support file:

5. **`FakeOAuthCaptureFlow`** (internal sealed, in `tests/_TestSupport/`) — the canned-outcome double §4.3–4.6 will inject into provider-setup tests.

`LoopbackOAuthCapture` is `public sealed` (matching the `BrainBackedProviderRegistry` precedent from §4.1) so the CLI composition root (§4.6) can construct it directly without crossing a layering boundary via `[InternalsVisibleTo("FlashSkink.CLI")]`. `SystemBrowserLauncher` and `IBrowserLauncher` stay `internal` — they're implementation details of `LoopbackOAuthCapture`. The public constructor takes only `ILoggerFactory` and internally constructs a `SystemBrowserLauncher`; an `internal` test constructor takes the launcher seam, a `TimeProvider`, and a custom timeout.

`§3.5.2` test patterns continue to work unchanged — nothing in this PR touches `FlashSkinkVolume`, `BrainBackedProviderRegistry`, or any existing test infrastructure.

---

## Discrepancies surfaced against the dev-plan text

Two small drifts, resolved at Gate 1 and recorded here for the audit trail.

1. **`LoopbackOAuthCapture` is `public sealed`, not `internal`.** The dev plan says "The helper is `internal` to `FlashSkink.Core`". This PR makes it `public sealed` instead, matching `BrainBackedProviderRegistry` (§4.1). Rationale: keeping it `internal` would force the §4.6 CLI composition root to either reach across a layering boundary via `[InternalsVisibleTo("FlashSkink.CLI")]` or introduce a public extension-method shim. Both are anti-patterns next to the trivial alternative of letting the type be the public surface it already needs to be.

2. **`IBrowserLauncher` is a new interface.** The dev plan describes the launcher inline ("using `Process.Start` with `UseShellExecute = true`; OS-specific branching only for the launcher — the listener itself is identical on every OS"). It does not call out a separate interface. This PR introduces `IBrowserLauncher` as a test seam — without it, the integration test would have no way to drive the flow end-to-end without launching a real browser on the CI host. The interface is `internal` to `FlashSkink.Core` (no abstractions-layer concern), adds one method, and slots cleanly into standard DI patterns.

---

## Files to create

- `src/FlashSkink.Core/Providers/Setup/IBrowserLauncher.cs` — `internal interface IBrowserLauncher { void Launch(Uri url); }`. ~25 lines including XML doc.
- `src/FlashSkink.Core/Providers/Setup/SystemBrowserLauncher.cs` — OS-dispatched launcher. ~80 lines.
- `src/FlashSkink.Core/Providers/Setup/LoopbackOAuthCapture.cs` — the `IOAuthCaptureFlow` implementation. ~280 lines.
- `tests/FlashSkink.Tests/_TestSupport/FakeOAuthCaptureFlow.cs` — canned-outcome double for §4.3–4.6 setup tests. ~120 lines.
- `tests/FlashSkink.Tests/Providers/Setup/FakeOAuthCaptureFlowTests.cs` — sanity tests for the double itself. ~90 lines.
- `tests/FlashSkink.Tests/Providers/Setup/LoopbackOAuthCaptureTests.cs` — integration tests against the real helper. ~280 lines.
- `tests/FlashSkink.Tests/Providers/Setup/SystemBrowserLauncherTests.cs` — minimal smoke test that the launcher's `Launch(Uri)` succeeds on the running OS using a benign URL. Skips if no DISPLAY/launch capability is detected (CI on headless Linux). ~70 lines.

## Files to modify

None. §4.2 is purely additive — no edits to existing files.

## Dependencies

- **NuGet:** none new. `System.Net.HttpListener` (BCL), `System.Diagnostics.Process` (BCL), `System.Security.Cryptography.RandomNumberGenerator` (BCL), `System.Security.Cryptography.SHA256` (BCL), `System.Buffers.Text.Base64Url` (BCL, .NET 9+ — confirmed available on `net10.0`).
- **Project references:** none new.

---

## Public API surface

### `FlashSkink.Core.Providers.Setup.LoopbackOAuthCapture` (public sealed)

**XML intent:** Production `IOAuthCaptureFlow` implementation backed by a real local-loopback `HttpListener` on `127.0.0.1:{random-port}/oauth-callback/`. Blueprint §24.5; cross-cutting decision 3 of phase-4-providers.

Public surface:
- `LoopbackOAuthCapture(ILoggerFactory loggerFactory)` — production constructor; uses `SystemBrowserLauncher`, `TimeProvider.System`, and a 5-minute timeout per Blueprint §24.5.
- `Result<OAuthCaptureContext> Prepare()` — inherited from `IOAuthCaptureFlow`.
- `Task<Result<string>> AwaitAuthorizationCodeAsync(OAuthCaptureContext, Uri, CancellationToken)` — inherited from `IOAuthCaptureFlow`.
- `void Dispose()` — `IDisposable`; stops and closes every listener still tracked by the instance.

The full type body and the internal test constructor are documented under "Internal types" → "`LoopbackOAuthCapture`" below.

No other type in this PR is public. `IBrowserLauncher`, `SystemBrowserLauncher`, and `FakeOAuthCaptureFlow` are all `internal`.

---

## Internal types

### `FlashSkink.Core.Providers.Setup.IBrowserLauncher` (internal interface)

```csharp
internal interface IBrowserLauncher
{
    /// <summary>
    /// Opens <paramref name="url"/> in the user's default browser using the OS launcher.
    /// Non-blocking — returns as soon as the launcher process is started. Failure to launch
    /// surfaces as <see cref="System.ComponentModel.Win32Exception"/> / <see cref="System.IO.FileNotFoundException"/>
    /// / <see cref="PlatformNotSupportedException"/>; <see cref="LoopbackOAuthCapture"/> catches
    /// and maps to <see cref="ErrorCode.Unknown"/>.
    /// </summary>
    void Launch(Uri url);
}
```

### `FlashSkink.Core.Providers.Setup.SystemBrowserLauncher` (internal sealed)

```csharp
internal sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Launch(Uri url)
    {
        if (OperatingSystem.IsWindows())
        {
            // UseShellExecute=true asks the Windows shell to resolve the default browser.
            Process.Start(new ProcessStartInfo
            {
                FileName = url.ToString(),
                UseShellExecute = true,
            });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            // `open <url>` launches the default browser.
            Process.Start("open", url.ToString());
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            // `xdg-open <url>` is the freedesktop.org standard; available on every mainstream
            // distro (Ubuntu, Fedora, Arch, openSUSE, Debian).
            Process.Start("xdg-open", url.ToString());
            return;
        }

        throw new PlatformNotSupportedException(
            $"Browser launch is not supported on platform '{Environment.OSVersion.Platform}'.");
    }
}
```

`Process.Start` overloads:

- `(ProcessStartInfo)` — used on Windows because `UseShellExecute = true` is mandatory for URL-as-FileName dispatch.
- `(string fileName, string arguments)` — used on macOS/Linux; `UseShellExecute` defaults to `false`, which is correct for invoking a named binary.

### `FlashSkink.Core.Providers.Setup.LoopbackOAuthCapture` (public sealed) — full body

```csharp
public sealed class LoopbackOAuthCapture : IOAuthCaptureFlow, IDisposable
{
    private const string RedirectPath = "/oauth-callback/";   // HttpListener prefix requires trailing slash
    private const int VerifierEntropyBytes = 32;              // → ~43-char base64url verifier
    private const string SuccessHtml =
        "<html><body style=\"font-family:sans-serif;padding:2em;\">"
        + "<h2>FlashSkink — sign-in complete</h2>"
        + "<p>You can close this browser tab and return to the terminal.</p>"
        + "</body></html>";
    private const string ErrorHtmlTemplate =
        "<html><body style=\"font-family:sans-serif;padding:2em;\">"
        + "<h2>FlashSkink — sign-in did not complete</h2>"
        + "<p>{0}</p>"
        + "<p>Return to the terminal for next steps.</p>"
        + "</body></html>";

    private readonly IBrowserLauncher _browserLauncher;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;
    private readonly ILogger<LoopbackOAuthCapture> _logger;

    // Active listeners keyed by full RedirectUri string ("http://127.0.0.1:54321/oauth-callback").
    // Concurrent because tests may exercise parallel flows; production OAuth setup is
    // single-flow, so the dictionary's contention surface is essentially zero.
    private readonly ConcurrentDictionary<string, HttpListener> _listeners = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// Production constructor — uses the OS browser launcher, real clock, and the
    /// 5-minute timeout from Blueprint §24.5. Callable from outside the assembly
    /// (the CLI composition root in §4.6).
    /// </summary>
    public LoopbackOAuthCapture(ILoggerFactory loggerFactory)
        : this(new SystemBrowserLauncher(), loggerFactory, TimeProvider.System, TimeSpan.FromMinutes(5))
    {
    }

    /// <summary>Test constructor — lets the integration tests inject a fake browser
    /// launcher, a custom clock, and a short timeout. <c>internal</c> so production callers
    /// cannot bypass the OS launcher or the 5-minute budget.</summary>
    internal LoopbackOAuthCapture(
        IBrowserLauncher browserLauncher,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        TimeSpan timeout);

    public Result<OAuthCaptureContext> Prepare();

    public Task<Result<string>> AwaitAuthorizationCodeAsync(
        OAuthCaptureContext context,
        Uri authorizationUri,
        CancellationToken ct);

    public void Dispose();   // stops + closes every listener still in the dictionary
}
```

### `FlashSkink.Tests._TestSupport.FakeOAuthCaptureFlow` (internal sealed)

```csharp
internal sealed class FakeOAuthCaptureFlow : IOAuthCaptureFlow
{
    private readonly object _lock = new();
    private OAuthCaptureContext? _preparedContext;

    /// <summary>Records the URL that the production code would have launched the browser to.</summary>
    public Uri? CapturedAuthorizationUri { get; private set; }

    /// <summary>Increments on every Prepare() call.</summary>
    public int PrepareCallCount { get; private set; }

    /// <summary>Increments on every AwaitAuthorizationCodeAsync() call.</summary>
    public int AwaitCallCount { get; private set; }

    /// <summary>
    /// Result returned by <see cref="Prepare"/>. Defaults to a deterministic Ok context
    /// (RedirectUri = "http://127.0.0.1:65000/oauth-callback/", verifier/challenge = stable strings).
    /// </summary>
    public Result<OAuthCaptureContext> PrepareResult { get; set; }

    /// <summary>
    /// Result returned by <see cref="AwaitAuthorizationCodeAsync"/>. Defaults to
    /// Ok("test-authorization-code").
    /// </summary>
    public Result<string> AwaitResult { get; set; } = Result<string>.Ok("test-authorization-code");

    public FakeOAuthCaptureFlow();

    public Result<OAuthCaptureContext> Prepare();

    public Task<Result<string>> AwaitAuthorizationCodeAsync(
        OAuthCaptureContext context,
        Uri authorizationUri,
        CancellationToken ct);
}
```

The fake is `internal` to `tests/_TestSupport/` and visible to every test class via `namespace FlashSkink.Tests._TestSupport`. §4.3–§4.6 will reuse it.

---

## Method-body contracts

### `LoopbackOAuthCapture.Prepare()`

Synchronous; no I/O beyond `TcpListener` port allocation + `HttpListener` start.

1. Throw `ObjectDisposedException` if `_disposed`.
2. Allocate a free loopback port using the standard idiom:
   ```csharp
   var probe = new TcpListener(IPAddress.Loopback, 0);
   probe.Start();
   int port;
   try { port = ((IPEndPoint)probe.LocalEndpoint).Port; }
   finally { probe.Stop(); }
   ```
   The brief TOCTOU window between `Stop()` and the subsequent `HttpListener.Start()` is the well-known cost of this pattern; in practice the kernel re-allocates a fresh port long before any third party could bind it, and re-tries are not needed (the probe runs once per setup flow).
3. Build `redirectUri = $"http://127.0.0.1:{port}/oauth-callback/"` and start an `HttpListener` on it:
   ```csharp
   var listener = new HttpListener();
   listener.Prefixes.Add(redirectUri);
   listener.Start();
   ```
   The trailing slash on the prefix is required by `HttpListener` (the framework throws `ArgumentException` without it).
4. Generate the PKCE pair using `System.Buffers.Text.Base64Url`:
   ```csharp
   Span<byte> verifierBytes = stackalloc byte[VerifierEntropyBytes];
   RandomNumberGenerator.Fill(verifierBytes);
   var verifier = Base64Url.EncodeToString(verifierBytes);

   Span<byte> challengeHash = stackalloc byte[SHA256.HashSizeInBytes];
   SHA256.HashData(Encoding.ASCII.GetBytes(verifier), challengeHash);
   var challenge = Base64Url.EncodeToString(challengeHash);
   ```
   Per RFC 7636 §4.2: the `code_verifier` is treated as ASCII bytes before hashing; the resulting digest is base64url-encoded without padding (which `Base64Url.EncodeToString` already does).
5. Register the listener: `_listeners[redirectUri] = listener;` (using indexer; on duplicate key — i.e. extraordinarily rare port reuse within one process before the prior flow disposed — call `Stop()`+`Close()` on the existing listener to avoid leaks).
6. Return `Result<OAuthCaptureContext>.Ok(new(redirectUri, challenge, verifier))`.

Failure modes:

- `SocketException` from `TcpListener.Start()` or `HttpListenerException` from `HttpListener.Start()` → log at `Warning`, return `Result.Fail(Unknown, "Failed to bind loopback listener: <message>", ex)`. Cleanup: ensure the probe is stopped and the listener (if partially started) is closed.
- `OperationCanceledException` is not possible — the method is synchronous and accepts no `CancellationToken`. (Per principle 14, the catch is still present so the catch-block hierarchy remains regular; `Prepare()` has no awaits so OCE cannot reach it. Drop the catch — Principle 14 governs methods that *can* be cancelled.)
- `CryptographicException` from `SHA256.HashData` is a programming-error scenario only (the BCL `SHA256` cannot legitimately fail on a non-null span). Bubble up as `Result.Fail(Unknown, ..., ex)` via the generic catch.

Catch ordering: `SocketException` → `Unknown`; `HttpListenerException` → `Unknown`; `Exception` → `Unknown`. All map to `Unknown` because the failure modes are programming errors / environment problems with no separately-actionable recovery for the caller. Distinct catch types are kept (Principle 15) so the `ErrorContext.ExceptionType` field carries the original BCL type name.

### `LoopbackOAuthCapture.AwaitAuthorizationCodeAsync(context, authorizationUri, ct)`

1. Throw `ObjectDisposedException` if `_disposed`.
2. `ct.ThrowIfCancellationRequested()` at entry.
3. Look up `_listeners[context.RedirectUri]` — if not present, return `Result.Fail(InvalidArgument, "Context was not produced by Prepare() on this instance (or has already been consumed).")`. This catches sequencing bugs in callers.
4. Remove the listener from the dictionary so it's consumed exactly once: `_listeners.TryRemove(context.RedirectUri, out var listener)`. If the remove races against `Dispose`, treat the same as "not present" above.
5. Wrap the listener in a `try/finally` that ensures `listener.Close()` runs on every exit path.
6. Launch the browser via `_browserLauncher.Launch(authorizationUri)`. On failure (any exception), `Stop`/`Close` the listener and return `Result.Fail(Unknown, ..., ex)`.
7. Build a linked cancellation token for the 5-minute timeout:
   ```csharp
   using var timeoutCts = new CancellationTokenSource();
   timeoutCts.CancelAfter(_timeout, _timeProvider);   // .NET 8+ overload accepting TimeProvider
   using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
   ```
   Note: `TimeProvider`-aware `CancelAfter` overload landed in .NET 8 — `CancellationTokenSource.CancelAfter(TimeSpan, TimeProvider)`. Confirmed available on `net10.0`.
8. Register cancellation to stop the listener so the in-flight `GetContextAsync` unblocks:
   ```csharp
   await using var _ = linkedCts.Token.UnsafeRegister(static state =>
   {
       var l = (HttpListener)state!;
       try { l.Stop(); } catch (ObjectDisposedException) { /* already disposed */ }
   }, listener).ConfigureAwait(false);
   ```
9. `await listener.GetContextAsync().ConfigureAwait(false)` to receive the redirect.
10. Parse `?code=…` from the request URL's query string with a private helper `ParseQuery(string)` returning a `Dictionary<string, string>` (StringComparer.Ordinal). If the query contains `error=…`, write the error HTML page and return `Result.Fail(ProviderAuthFailed, $"OAuth provider returned error: {error}.", null)`. If `code` is missing or empty, write the error HTML page and return `Result.Fail(ProviderAuthFailed, "Callback did not contain ?code=...", null)`.
11. On success: write `SuccessHtml` to the response, close the response, return `Result<string>.Ok(code)`.

Catch ordering inside `AwaitAuthorizationCodeAsync`:

- `OperationCanceledException` first (Principle 14): if `ct.IsCancellationRequested` → `Cancelled`; otherwise the timeout fired → `Timeout`.
- `HttpListenerException` when `linkedCts.Token.IsCancellationRequested`: same translation (listener was Stopped by our cancellation registration; the exception is the wakeup signal). The `when` filter narrows so a "real" `HttpListenerException` (network breakage) still bubbles to the generic handler.
- `ObjectDisposedException` when `linkedCts.Token.IsCancellationRequested`: same — wakeup signal.
- `HttpListenerException` (no filter, e.g. listener died unexpectedly) → `ProviderUnreachable` mapping with the original exception attached.
- `Exception` (final, Principle 15) → `Unknown`.

The `finally` block on the outer try always: `listener.Stop()` (idempotent on already-stopped); `((IDisposable)listener).Dispose()` (idempotent on already-closed; `HttpListener` is `IDisposable` via explicit interface). Disposal failure is logged at `Debug` and swallowed.

#### Response-write helper

```csharp
private static async Task WriteResponseAsync(HttpListenerResponse response, string html, CancellationToken ct)
{
    response.StatusCode = 200;
    response.ContentType = "text/html; charset=utf-8";
    var bytes = Encoding.UTF8.GetBytes(html);
    response.ContentLength64 = bytes.LongLength;
    await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    response.OutputStream.Close();
}
```

Uses `CancellationToken.None` on the final write — the response is part of the success/error reply; cancelling mid-write would leave the user's browser in a confused state. Per Principle 17, the `CancellationToken.None` is a literal at the await site:

```csharp
await WriteResponseAsync(httpContext.Response, SuccessHtml, CancellationToken.None).ConfigureAwait(false);
```

#### Query parser

```csharp
private static Dictionary<string, string> ParseQuery(string? query)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    if (string.IsNullOrEmpty(query)) return result;

    var span = query.AsSpan();
    if (span.Length > 0 && span[0] == '?') span = span[1..];

    foreach (var pairRange in span.Split('&'))
    {
        var pair = span[pairRange];
        if (pair.Length == 0) continue;

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
```

Why hand-roll: `System.Web.HttpUtility.ParseQueryString` works but its `NameValueCollection` return type is awkward (and on some target frameworks emits a `SYSLIB0014`-class compile-time complaint when used with nullable annotations). A 15-line parser is clearer than wrestling with NVC; the parser only handles the `code`/`error` keys for our purposes.

### `LoopbackOAuthCapture.Dispose()`

Idempotent. Sets `_disposed = true`, then walks `_listeners.Values` and stops+closes each. Failures swallowed. Clears the dictionary. Per Principle 17 there is no `CancellationToken` parameter — disposal is non-cancellable by construction.

### `SystemBrowserLauncher.Launch(Uri url)`

See the type body above; no additional contract beyond what's spelled out.

### `FakeOAuthCaptureFlow.Prepare()`

- Returns the configured `PrepareResult`. If `PrepareResult.Success`, captures the value in `_preparedContext` so `AwaitAuthorizationCodeAsync` can validate the caller used the matching context.
- Increments `PrepareCallCount` under lock.

### `FakeOAuthCaptureFlow.AwaitAuthorizationCodeAsync(context, authorizationUri, ct)`

- Increments `AwaitCallCount` and records `CapturedAuthorizationUri = authorizationUri` under lock.
- If `ct.IsCancellationRequested`, returns `Result.Fail(Cancelled, "...")` synchronously via `Task.FromResult`.
- Otherwise returns `Task.FromResult(AwaitResult)`.
- Does not validate that `context` matches `_preparedContext` — test scenarios may want to bypass that. Tests that care can assert on `_preparedContext` themselves.

The fake never starts a real listener, never launches a browser, never touches the network. Tests that need to verify a provider setup wove the right `code_challenge` into its authorisation URL do so by capturing `CapturedAuthorizationUri` and parsing it.

---

## Integration points

- `IOAuthCaptureFlow` + `OAuthCaptureContext` (added in §4.1) — the interface this PR implements.
- `Result<T>` / `ErrorCode` (existing) — return shape and error vocabulary. No new `ErrorCode` values.
- `TimeProvider` (BCL, .NET 8+) — used for testable timeout via `CancellationTokenSource.CancelAfter(TimeSpan, TimeProvider)`. Default is `TimeProvider.System` for production.
- `[InternalsVisibleTo("FlashSkink.Tests")]` on `FlashSkink.Core` (already declared) — lets the integration test construct `LoopbackOAuthCapture` and the browser-launcher seam directly.
- The `_TestSupport/RecordingLogger<T>.cs` pattern (existing) — `LoopbackOAuthCaptureTests` uses it to assert `Warning`-level log entries on the failure paths.

---

## Principles touched

- **Principle 1** (Core never throws across the public API boundary) — every `IOAuthCaptureFlow` method returns `Result`/`Result<T>`. The internal `IBrowserLauncher.Launch` throws (it has no `Result` return because it's a void seam consumed only inside `LoopbackOAuthCapture`'s try/catch); the exception is caught and converted to `Result.Fail` by `AwaitAuthorizationCodeAsync`.
- **Principle 12** (OS-agnostic by default) — `SystemBrowserLauncher` is the *only* platform-branched type in §4.2. The branching is explicitly justified in cross-cutting decision 3 ("OS-specific branching only for the launcher"). `LoopbackOAuthCapture` itself is OS-agnostic: `HttpListener`, `TcpListener`, and `IPAddress.Loopback` all work identically on Windows/Linux/macOS in .NET 10.
- **Principle 13** (`CancellationToken ct` last) — every async method in the new code accepts `ct` as the final parameter. `Prepare()` is synchronous and correctly omits it.
- **Principle 14** (`OperationCanceledException` first catch) — `AwaitAuthorizationCodeAsync`'s catch hierarchy starts with `OperationCanceledException`. `Prepare()` is synchronous and is the sanctioned exception (no awaits, so OCE cannot reach it).
- **Principle 15** (no bare `catch (Exception)` as the only catch) — every catch block has at least one specific type before the generic fallback.
- **Principle 17** (`CancellationToken.None` literal in compensation) — the response-write step (`WriteResponseAsync`) uses `CancellationToken.None` as a literal at the await site, not via a local. Dispose paths similarly do not accept a `ct`.
- **Principle 22** (Dapper outside hot paths only) — N/A; no SQL in §4.2.
- **Principle 24** (no silent background failures) — `Prepare()` and `AwaitAuthorizationCodeAsync` log every failure mode at `Warning` through `ILogger<LoopbackOAuthCapture>` before returning `Result.Fail`. The notification-bus side of Principle 24 ("publish to `INotificationBus`") does NOT apply here: §4.2 is invoked synchronously from a CLI command (§4.6), not from a background service, so the CLI handler is responsible for surfacing the failure to the user — the bus is for fire-and-forget background work.
- **Principle 26** (no secrets in logs) — `LoopbackOAuthCapture` never logs `context.CodeVerifier`, never logs the authorisation code, never logs the authorisation URI's full content (which can include `state` or `nonce` parameters). The logger emits only: the redirect URI (a `127.0.0.1:port/oauth-callback` literal, not a secret), the timeout duration on timeout, and the OAuth provider's `error=` value (the provider's own choice to surface, not a secret of ours). The `error_description` field is intentionally NOT logged — providers sometimes echo user-supplied query parameters there.
- **Principle 28** (Core depends only on MEL abstractions) — `ILogger<LoopbackOAuthCapture>` is the only logging dependency, from `Microsoft.Extensions.Logging.Abstractions`. Already a Core dependency.
- **Principle 32** (no telemetry, no update checks, no network chatter) — the only network activity is the local-loopback listener on `127.0.0.1`. Not external. Not telemetry.

---

## Test spec

### `tests/FlashSkink.Tests/_TestSupport/FakeOAuthCaptureFlow.cs`

Production-quality test double; reused by §4.3–§4.6. Implementation details under "Internal types" above.

### `tests/FlashSkink.Tests/Providers/Setup/FakeOAuthCaptureFlowTests.cs` — class `FakeOAuthCaptureFlowTests`

- `Prepare_ReturnsDefaultContext_WhenNoOverride` — default `PrepareResult` returns an `Ok` with the canonical `127.0.0.1:65000` redirect.
- `Prepare_HonoursOverride_WhenSet` — setting `PrepareResult` to a custom `Result` returns it verbatim.
- `Prepare_IncrementsCallCount`
- `AwaitAuthorizationCodeAsync_ReturnsDefaultCode_WhenNoOverride` — default returns `Ok("test-authorization-code")`.
- `AwaitAuthorizationCodeAsync_HonoursOverride_WhenSet`
- `AwaitAuthorizationCodeAsync_CapturesAuthorizationUri` — pass a URI, assert `CapturedAuthorizationUri` reflects it.
- `AwaitAuthorizationCodeAsync_IncrementsCallCount`
- `AwaitAuthorizationCodeAsync_CancelledToken_ReturnsCancelled`

### `tests/FlashSkink.Tests/Providers/Setup/LoopbackOAuthCaptureTests.cs` — class `LoopbackOAuthCaptureTests`

Integration tests for the real helper. The browser launcher is a test double (`RecordingBrowserLauncher` defined nested in the test class) that records the URL it would have launched, then the test makes an HTTP GET to `context.RedirectUri` from a `System.Net.Http.HttpClient` running on the test thread — this simulates the user's browser following the provider's redirect.

Test cases:

- `Prepare_ReturnsContext_WithPort_PkceVerifierAndChallenge` — assert `RedirectUri.StartsWith("http://127.0.0.1:")` and ends with `/oauth-callback/`; assert `CodeVerifier.Length >= 43`; assert `CodeChallenge.Length == 43` (SHA-256 → 32 bytes → 43 base64url chars).
- `Prepare_TwoConsecutiveCalls_AllocateDistinctPorts` — call twice, assert different ports.
- `Prepare_PkceChallenge_IsSha256OfVerifier` — independently re-derive `Base64Url(SHA256(Encoding.ASCII.GetBytes(verifier)))` and assert equality.
- `Prepare_Verifier_Differs_Across_Calls` — two `Prepare()` calls produce different verifiers.
- `AwaitAuthorizationCodeAsync_HappyPath_ReturnsCode` — `Prepare()` → start `Await…` on a background task → use an `HttpClient` to GET `{context.RedirectUri}?code=auth-code-xyz` → assert the await task returns `Ok("auth-code-xyz")` and the response from the listener contains the success HTML.
- `AwaitAuthorizationCodeAsync_LaunchesBrowser_WithSuppliedAuthorizationUri` — assert `RecordingBrowserLauncher.LaunchedUri` equals the passed `authorizationUri`.
- `AwaitAuthorizationCodeAsync_QueryHasError_ReturnsProviderAuthFailed` — GET `…?error=access_denied`; assert `Fail(ProviderAuthFailed)` with the message containing `access_denied`, and the response HTML is the error template (not the success one).
- `AwaitAuthorizationCodeAsync_QueryMissingCode_ReturnsProviderAuthFailed` — GET with no `code` and no `error`; assert `Fail(ProviderAuthFailed)`.
- `AwaitAuthorizationCodeAsync_CancelledBeforeRedirect_ReturnsCancelled` — start the await with an early-cancelling `ct`; never hit the redirect URI; assert the await returns `Fail(Cancelled)` promptly (within 1s).
- `AwaitAuthorizationCodeAsync_TimeoutFires_ReturnsTimeout` — construct `LoopbackOAuthCapture` via the test constructor with `timeout = TimeSpan.FromMilliseconds(200)` and `TimeProvider.System`; do not GET the redirect; assert `Fail(Timeout)` within ~1s (timeout + jitter). Uses real `TimeProvider.System` because we want the actual `HttpListener.Stop()` wakeup-on-cancellation path to fire; testing with a `FakeTimeProvider` here would not exercise the listener's cancellation path because `CancelAfter` with a fake provider doesn't actually unblock `GetContextAsync`.
- `AwaitAuthorizationCodeAsync_ContextFromAnotherInstance_ReturnsInvalidArgument` — construct instance A and B; `A.Prepare()` produces context; call `B.AwaitAuthorizationCodeAsync(context, ...)` — returns `Fail(InvalidArgument)` because the context wasn't prepared on `B`.
- `AwaitAuthorizationCodeAsync_ContextConsumedTwice_SecondCallReturnsInvalidArgument` — successful await consumes the context; second await with the same context returns `Fail(InvalidArgument)`.
- `AwaitAuthorizationCodeAsync_BrowserLauncherThrows_ReturnsUnknown` — launcher throws `PlatformNotSupportedException`; assert `Fail(Unknown)` with the exception attached, and the listener is cleaned up (subsequent `Prepare()` succeeds).
- `Dispose_StopsActiveListeners` — call `Prepare()` to start a listener, then `Dispose()`. Attempt to GET the redirect URI; assert the connection is refused (the listener is closed).
- `AwaitAuthorizationCodeAsync_AfterDispose_Throws` — `Dispose()` then `Await…` on the disposed instance throws `ObjectDisposedException`.

#### `RecordingBrowserLauncher` (private nested test type)

```csharp
private sealed class RecordingBrowserLauncher : IBrowserLauncher
{
    public Uri? LaunchedUri { get; private set; }
    public Exception? ThrowOnLaunch { get; set; }
    public void Launch(Uri url)
    {
        LaunchedUri = url;
        if (ThrowOnLaunch is not null) throw ThrowOnLaunch;
    }
}
```

### `tests/FlashSkink.Tests/Providers/Setup/SystemBrowserLauncherTests.cs` — class `SystemBrowserLauncherTests`

One smoke test: confirm `SystemBrowserLauncher.Launch(new Uri("about:blank"))` returns without throwing for any reason other than a *known* environment-shaped failure mode.

The headless-CI risk is real: a Linux runner without `xdg-open` installed throws `Win32Exception` ("No such file or directory"); a runner on an exotic platform throws `PlatformNotSupportedException` from the implementation's final guard. Both are environmental, not bugs. The test treats them as expected and accepts them; any other exception is a real failure.

```csharp
[Fact]
public void Launch_OnRunningPlatform_DoesNotThrow_OrThrowsExpectedEnvironmentException()
{
    var launcher = new SystemBrowserLauncher();
    var ex = Record.Exception(() => launcher.Launch(new Uri("about:blank")));

    // null → launcher accepted the URI. Win32Exception → e.g. xdg-open missing on
    // headless Linux. PlatformNotSupportedException → the type's own guard fired on
    // an unsupported OS. Anything else is a real defect.
    Assert.True(
        ex is null or System.ComponentModel.Win32Exception or PlatformNotSupportedException,
        $"Unexpected exception from SystemBrowserLauncher.Launch: {ex}");
}
```

`about:blank` was chosen rather than `https://example.com` to avoid any actual network activity (no host-side state, Principle 7).

---

## Acceptance criteria

- [ ] Builds with zero warnings on all targets (Linux, Windows, macOS Intel, macOS ARM64).
- [ ] All Phase 0–4.1 tests continue to pass.
- [ ] All new tests pass.
- [ ] `LoopbackOAuthCapture`, `SystemBrowserLauncher`, `IBrowserLauncher` exist in `src/FlashSkink.Core/Providers/Setup/`.
- [ ] `FakeOAuthCaptureFlow` exists in `tests/FlashSkink.Tests/_TestSupport/`.
- [ ] `LoopbackOAuthCapture` uses `TimeProvider`-aware `CancelAfter` for the 5-minute timeout and accepts an injected `TimeProvider` in its test constructor.
- [ ] No new `ErrorCode` values added.
- [ ] No reference to any UI framework anywhere in the new code (Principles 8–11).
- [ ] `dotnet format --verify-no-changes` is clean.
- [ ] No string interpolation of secret-named fields in any logger call.

---

## Line-of-code budget

| File | Lines |
|---|---|
| `Core/Providers/Setup/IBrowserLauncher.cs` | ~25 |
| `Core/Providers/Setup/SystemBrowserLauncher.cs` | ~80 |
| `Core/Providers/Setup/LoopbackOAuthCapture.cs` | ~280 |
| **src delta** | **~385** |
| `tests/_TestSupport/FakeOAuthCaptureFlow.cs` | ~120 |
| `tests/.../Providers/Setup/FakeOAuthCaptureFlowTests.cs` | ~90 |
| `tests/.../Providers/Setup/LoopbackOAuthCaptureTests.cs` | ~280 |
| `tests/.../Providers/Setup/SystemBrowserLauncherTests.cs` | ~30 |
| **test delta** | **~520** |
| **Total delta** | **~905** |

---

## Non-goals for §4.2

- Do NOT make `LoopbackOAuthCapture` public. Visibility decision deferred to §4.6 (CLI composition root).
- Do NOT wire `LoopbackOAuthCapture` into any composition root. The CLI `Program.cs` is empty until §4.6; this PR leaves it untouched.
- Do NOT implement any provider-specific OAuth logic. Authorisation-URL construction, scope strings, token-exchange endpoints, refresh handling — all live in `GoogleDriveSetup` / `DropboxSetup` / `OneDriveSetup` (§4.3 / §4.4 / §4.5).
- Do NOT add HTTPS support to the loopback listener. RFC 8252 §7.3 explicitly endorses plain HTTP for the loopback redirect (the connection never leaves the host). Adding HTTPS would require self-signed certificates and OS keychain dancing for no security benefit.
- Do NOT extend `IOAuthCaptureFlow` (frozen in §4.1).
- Do NOT add an `INotificationBus` publish for failures. Failures are returned synchronously to the CLI command and are surfaced by the CLI handler (§4.6); no fire-and-forget path exists in this PR's call sites.
- Do NOT modify `ProviderTokenCrypto`, `FileSystemProviderSetup`, `BrainBackedProviderRegistry`, or any other §4.1 artefact.
- Do NOT add a `state` / `nonce` query-string parameter validation. PKCE alone is sufficient for the loopback flow (RFC 8252 §8.9 — `state` is recommended primarily to defend against CSRF in browser-redirect flows where the redirect URI is hosted by the relying party; here the redirect URI is a single-shot loopback bound by us, not a publicly-reachable endpoint). Cloud-provider setups in §4.3–§4.5 may still include `state` in their authorisation URLs — the loopback doesn't *forbid* it — but the loopback itself doesn't enforce or validate it.

---

## Gate 1 resolutions (open questions, now answered)

1. **`LoopbackOAuthCapture` visibility** → `public sealed`. Matches `BrainBackedProviderRegistry` precedent; CLI composition root (§4.6) constructs it directly without `[InternalsVisibleTo]`.
2. **`IBrowserLauncher` introduction** → accepted as an internal interface. Cleaner than a delegate parameter; supports XML doc on the exception contract; gives tests a typed seam.
3. **`state` parameter validation** → not validated by the loopback. Cloud-provider setups may include `state` in their authorisation URLs; the loopback ignores it. PKCE is the complete defence for the loopback-redirect-interception threat model (RFC 8252).
4. **`Base64Url`** → use BCL `System.Buffers.Text.Base64Url`. Native, allocation-free, supported on `net10.0`.
5. **`SystemBrowserLauncherTests` viability on CI** → defensive catch of `Win32Exception` and `PlatformNotSupportedException` in the test body (see test spec above). Avoids the CI debug loop on headless Linux.
