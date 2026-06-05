# PR 4.5.2 — Dropbox live verification (+ the OAuth fixed-port fix it surfaced)

**Branch:** pr/4.5.2-dropbox-live-verification
**Blueprint sections:** §15.4 (Dropbox `upload_session/start`→`append_v2`→`finish`), §15.7 (per-provider verification), §27.3 (Dropbox provider notes), §24.3 (BYOC setup), §24.5 (loopback OAuth capture), §10.1–10.4 (frozen provider/setup contract)
**Dev plan section:** phase-4.5 §4.5.2

## Scope
Add `DropboxLiveTests` (the second concrete live-verification class, binding the unchanged `LiveProviderHarness` to `DropboxSetup`/`DropboxProvider`) **and** the production fix that the live work surfaced: the loopback OAuth capture allocates a random ephemeral port, but Dropbox requires the `redirect_uri` to exactly match a pre-registered value (it does not honor RFC 8252 variable loopback ports). This makes Dropbox OAuth setup impossible today — not only in the live test but in the real `skink setup add` CLI path, which uses the same `LoopbackOAuthCapture`. Per cross-cutting decision 6 ("the live test discovers; the fix lands in the same section's PR with a CI-side regression guard"), this PR fixes it by adding an **opt-in fixed loopback port**, surfaced through an **additive capability interface** that `DropboxSetup` implements, honored by both the CLI setup flow and the live harness, and documented in the Dropbox setup guide. Google Drive's random-port behaviour is unchanged (default path). OneDrive is out of scope (its need, if any, is a §4.5.3 question — Microsoft identity does support `http://localhost` dynamic ports).

---

## The finding (why this PR now changes `src/`)
- `LoopbackOAuthCapture.Prepare()` binds `new TcpListener(IPAddress.Loopback, 0)` → an OS-assigned **ephemeral** port; the redirect URI becomes `http://127.0.0.1:{random}/oauth-callback/`.
- **Google Drive** completes consent because Google's *Desktop-app* client honors RFC 8252 §7.3 (port-agnostic loopback match) — verified live in §4.5.1.
- **Dropbox** requires an **exact** `redirect_uri` match including the port and has not implemented variable-port loopback (verified against Dropbox community + OAuth guide). An unpredictable ephemeral port can never match a pre-registered URI, so `Dropbox_Setup` — and the real CLI `skink setup add --provider dropbox` — fail with a redirect mismatch.
- The fix is in production OAuth code (`IOAuthCaptureFlow`/`LoopbackOAuthCapture`), not the test. The deterministic CI guard (decision 6) is a set of `LoopbackOAuthCapture` + `SetupAddCommand` + `DropboxSetup` unit tests that exercise the fixed-port path with no network.

## D1-Dropbox (carried from the prior plan, still applies)
Dropbox does **not** implement `ISupportsRemoteHashCheck` (`ContentHash` is SHA-256, log-only — `DropboxProvider.cs` lines 31–36), identical to Drive's §4.5.1 D1. The integrity building block is the byte-identical round-trip (GCM-tag-backed); the hash test is the negative fact `Dropbox_RemoteHashCheck_NotSupported_ByDesign`. Harness reused verbatim. Phase-doc §4.5.2 note already corrected (committed earlier in this branch).

---

## Files to create
- `src/FlashSkink.Core.Abstractions/Providers/IRequiresFixedRedirectPort.cs` — new additive capability interface (the P23-sanctioned mechanism, mirroring `ISupportsRemoteHashCheck`). ~25 lines.
- `tests/FlashSkink.Tests/Providers/Live/DropboxLiveTests.cs` — **already created** earlier in this branch; unchanged by the fix (it drives the harness, which now honors the capability). ~150 lines.

## Files to modify
**Production (`src/`):**
- `src/FlashSkink.Core.Abstractions/Providers/IOAuthCaptureFlow.cs` — change `Result<OAuthCaptureContext> Prepare()` to `Result<OAuthCaptureContext> Prepare(int? preferredPort = null)`; document the new parameter (null = ephemeral, default; a value = bind that exact loopback port for providers that require a pre-registered redirect URI). `IOAuthCaptureFlow` is **not** in the frozen-contract set (P23), so this extension is allowed. ~6 lines.
- `src/FlashSkink.Core/Providers/Setup/LoopbackOAuthCapture.cs` — honor `preferredPort`: when non-null, validate `1..65535` (else `Result.Fail(InvalidArgument)`) and bind that exact port instead of `AllocateFreeLoopbackPort()`; a busy fixed port surfaces through the existing `HttpListenerException`/`SocketException` → `Result.Fail` path (message mentions the port so the user can free it). Default (null) path is byte-for-byte the current behaviour. ~15 lines.
- `src/FlashSkink.Core/Providers/Dropbox/DropboxSetup.cs` — implement `IRequiresFixedRedirectPort`; add `public const int FixedLoopbackPort = 53682;` (a fixed, arbitrary high port — the value users register) and `int IRequiresFixedRedirectPort.RedirectPort => FixedLoopbackPort;`. Update the class XML remark to note the fixed-port requirement + rationale. ~10 lines.
- `src/FlashSkink.CLI/Commands/Setup/SetupAddCommand.cs` — at the `Prepare()` call site (line 160), pass the capability's port when the selected `setup is IRequiresFixedRedirectPort fp`: `_oauthCapture.Prepare(fp.RedirectPort)`, else `_oauthCapture.Prepare()`. ~4 lines.
- The embedded Dropbox setup-guide resource (logical name `guides.dropbox`; physical file under `src/FlashSkink.CLI/Setup/guides/`) — add the now-mandatory step: register redirect URI `http://127.0.0.1:53682/oauth-callback/` in the Dropbox app console, and the `account_info.read`/`files.content.*` scopes. ~8 lines. (Exact path pinned at implementation.)

**Tests:**
- `tests/FlashSkink.Tests/_TestSupport/FakeOAuthCaptureFlow.cs` — match the new signature `Prepare(int? preferredPort = null)`; record `public int? LastPreparedPort { get; private set; }`. ~4 lines.
- `tests/FlashSkink.Tests/Providers/Setup/LoopbackOAuthCaptureTests.cs` — add the fixed-port regression guards (below). ~70 lines.
- `tests/FlashSkink.Tests/Providers/Dropbox/DropboxSetupTests.cs` — add a test asserting `DropboxSetup` implements `IRequiresFixedRedirectPort` with `RedirectPort == 53682`. ~15 lines.
- `tests/FlashSkink.Tests/Engine/SetupCommandsEndToEndTests.cs` — add a test asserting that a Dropbox-style setup (implementing `IRequiresFixedRedirectPort`) causes `SetupAddCommand` to call `Prepare(53682)` (via `FakeOAuthCaptureFlow.LastPreparedPort`); and that a non-capability setup still calls `Prepare(null)`. ~40 lines.
- `tests/FlashSkink.Tests/Providers/Live/LiveProviderHarness.cs` — `RunSetupAsync`: mirror the CLI — `var port = _setup is IRequiresFixedRedirectPort fp ? fp.RedirectPort : (int?)null; capture.Prepare(port)`. So the Dropbox live test exercises the real fixed-port path. ~3 lines.
- `tests/FlashSkink.Tests/Providers/Live/README.md` — document that Dropbox requires registering `http://127.0.0.1:53682/oauth-callback/` exactly (Google needs no redirect registration; Dropbox does). ~8 lines.

**Docs / phase:**
- `dev-plan/phase-4.5-live-provider-verification.md` — already corrected for D1-Dropbox earlier in this branch. Add a one-line note to the §4.5.2 section that this PR also lands the loopback fixed-port fix (decision 6 in action). ~2 lines.

## Dependencies
None new. All types reachable from existing references.

## Public API surface
### `FlashSkink.Core.Abstractions.Providers.IRequiresFixedRedirectPort` (new public interface)
Summary intent: additive capability declaring that an OAuth provider's setup needs the loopback redirect URI bound to a fixed, pre-registered port (because the provider does not honor RFC 8252 variable-port loopback). The P23-sanctioned extension mechanism — analogous to `ISupportsRemoteHashCheck`.
- `int RedirectPort { get; }` — the fixed loopback port to bind; the caller registers `http://127.0.0.1:{RedirectPort}/oauth-callback/`. Pure accessor; never fails (so no `Result`).

### `FlashSkink.Core.Abstractions.Providers.IOAuthCaptureFlow.Prepare` (signature change — non-frozen)
- `Result<OAuthCaptureContext> Prepare(int? preferredPort = null)` — `null` (default) preserves the current ephemeral-port behaviour; a value binds that exact loopback port. `InvalidArgument` when out of `1..65535`; `Unknown` when the port is unavailable or PKCE generation fails.

### `FlashSkink.Core.Providers.Dropbox.DropboxSetup` (implements new interface)
- `public const int FixedLoopbackPort = 53682;`
- `int IRequiresFixedRedirectPort.RedirectPort => FixedLoopbackPort;` (explicit interface impl).

## Method-body contracts
- **`LoopbackOAuthCapture.Prepare(int? preferredPort)`**: if `preferredPort is { } p` and `p < 1 or p > 65535` → `Result.Fail(InvalidArgument, "...")` before binding. Otherwise `var port = preferredPort ?? AllocateFreeLoopbackPort();` and proceed exactly as today (bind `http://127.0.0.1:{port}/oauth-callback/`, start listener, generate PKCE, register flow). The `SocketException`/`HttpListenerException` catches already map bind failures to `Result.Fail`; when `preferredPort` was set, the failure message names the port. Principle 26 unchanged (no secrets logged). No `await`, no `ct` (synchronous — P13 N/A).
- **`SetupAddCommand`**: `int? preferredPort = setup is IRequiresFixedRedirectPort fp ? fp.RedirectPort : null;` then `_oauthCapture.Prepare(preferredPort)`. No other behavioural change; error handling unchanged.
- **`LiveProviderHarness.RunSetupAsync`**: same capability check before `capture.Prepare(port)`.

## Integration points (verified in src)
- `SetupAddCommand.cs:160` `_oauthCapture.Prepare();` — call site, with `setup` (`IProviderSetup`) in scope.
- `LoopbackOAuthCapture.Prepare()` (lines 120–161); `AllocateFreeLoopbackPort()` (349–361); `RedirectPath = "/oauth-callback/"`.
- `IOAuthCaptureFlow.Prepare()` (line 42); implementers: `LoopbackOAuthCapture`, `FakeOAuthCaptureFlow`.
- `DropboxSetup` public ctor `(ILoggerFactory)`; `DropboxProvider : IStorageProvider, IAsyncDisposable`.
- `SetupCommandsEndToEndTests.BuildFactory(...)` injects `FakeOAuthCaptureFlow`; asserts `PrepareCallCount`.

## Principles touched
- **P23** (provider contract frozen) — `IProviderSetup` and the frozen family are untouched; the new behaviour rides an **additive capability interface** (`IRequiresFixedRedirectPort`), the sanctioned mechanism. `IOAuthCaptureFlow` is explicitly *not* in the frozen set, so its additive optional-parameter extension is permitted.
- **P1** (Core never throws across its public API) — `Prepare` still returns `Result`; the new validation returns `Result.Fail(InvalidArgument)` rather than throwing.
- **P12** (OS-agnostic) — fixed-port binding uses the same cross-platform `HttpListener`/`TcpListener` path; no platform branch.
- **P15/P16** (catch ordering / dispose on failure) — the existing catch ladder and `CleanupListenerOnFailure` are preserved; the fixed-port bind failure flows through them unchanged.
- **P26** (no secrets in logs/output) — unchanged; the port is not a secret; no token/secret/code/verifier is logged.
- **P13** — N/A to `Prepare` (synchronous, no I/O await, no `ct`).

## Test spec
**CI regression guards (decision 6 — deterministic, no network):**
`tests/FlashSkink.Tests/Providers/Setup/LoopbackOAuthCaptureTests.cs`:
- `Prepare_WithPreferredPort_BindsExactPort` — allocate+release a free port `p` (throwaway `TcpListener` on 0, read port, stop), call `Prepare(p)`, assert `RedirectUri == $"http://127.0.0.1:{p}/oauth-callback/"`.
- `Prepare_WithoutPreferredPort_StillBindsEphemeralLoopback` — `Prepare()` (and `Prepare(null)`) succeed and return a `http://127.0.0.1:{port}/oauth-callback/` URI with a positive port (default behaviour preserved).
- `Prepare_WithPreferredPortInUse_ReturnsFail` — bind a `TcpListener` on `p`, then `Prepare(p)` → `!Success` (no throw across the boundary).
- `Prepare_WithInvalidPort_ReturnsInvalidArgument` — `[Theory]` over `0`, `-1`, `70000` → `Error.Code == InvalidArgument`.

`tests/FlashSkink.Tests/Providers/Dropbox/DropboxSetupTests.cs`:
- `DropboxSetup_Requires_FixedRedirectPort_53682` — `new DropboxSetup(NullLoggerFactory.Instance) is IRequiresFixedRedirectPort fp && fp.RedirectPort == 53682`.

`tests/FlashSkink.Tests/Engine/SetupCommandsEndToEndTests.cs`:
- `SetupAdd_FixedPortProvider_PreparesWithThatPort` — a fake `IProviderSetup` implementing `IRequiresFixedRedirectPort{RedirectPort=53682}`; after `setup add`, assert `fakeOAuth.LastPreparedPort == 53682`.
- `SetupAdd_NonFixedPortProvider_PreparesWithNull` — a plain fake setup → `fakeOAuth.LastPreparedPort is null`.

**Live tests (manual, self-skipping — phase deliverable):**
`DropboxLiveTests` (already created) — 7 `Dropbox_*` methods in the §4.5.1 shape; now genuinely runnable once the fix lands and the developer registers the fixed redirect URI.

## Acceptance criteria
- [ ] Builds with zero warnings on all targets (`--warnaserror`).
- [ ] With no live env vars set, every `LiveProvider` test (Drive + Dropbox) reports **Skipped**.
- [ ] New CI regression tests pass: fixed-port binds, busy-port fails cleanly, invalid-port → InvalidArgument, default path unchanged, Dropbox declares the capability, SetupAddCommand honors it.
- [ ] No existing tests break (Drive's default ephemeral-port path is unchanged).
- [ ] No change to `IStorageProvider`/`IProviderSetup`/`UploadSession`/`ProviderCredentials`/`ProviderSetupKind`/`ProviderHealth`/Result family (P23); the new behaviour is an additive capability interface + a non-frozen-interface extension.
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] No credential/token/cache committed.
- [ ] (manual, local) With Dropbox credentials set **and** `http://127.0.0.1:53682/oauth-callback/` registered, all `Dropbox_*` tests pass against the real Dropbox API.

## Line-of-code budget
| File | Lines |
|---|---|
| `IRequiresFixedRedirectPort.cs` (new) | ~25 |
| `IOAuthCaptureFlow.cs` (delta) | ~6 |
| `LoopbackOAuthCapture.cs` (delta) | ~15 |
| `DropboxSetup.cs` (delta) | ~10 |
| `SetupAddCommand.cs` (delta) | ~4 |
| Dropbox guide resource (delta) | ~8 |
| `FakeOAuthCaptureFlow.cs` (delta) | ~4 |
| `LoopbackOAuthCaptureTests.cs` (delta) | ~70 |
| `DropboxSetupTests.cs` (delta) | ~15 |
| `SetupCommandsEndToEndTests.cs` (delta) | ~40 |
| `LiveProviderHarness.cs` (delta) | ~3 |
| `DropboxLiveTests.cs` (already created) | ~150 |
| `README.md` / phase-doc (deltas) | ~10 |
| **Total** | **~360** new/changed (incl. the already-created live class) |

## Non-goals
- Do NOT modify the frozen contract (`IProviderSetup` et al., P23) — use the capability interface.
- Do NOT add OneDrive live tests or assume OneDrive needs a fixed port (§4.5.3 decides; Microsoft identity supports `http://localhost` dynamic ports).
- Do NOT add a user-facing CLI option to configure the Dropbox redirect port — the fixed constant + guide registration is the V1 approach; a configurable port is post-V1.
- Do NOT change Google Drive's behaviour (it must keep using the ephemeral port — the default `Prepare(null)` path).
- Do NOT make any live test a required CI check or add a workflow supplying real credentials.
- Do NOT store the client secret in the token cache — read from env each run; cache only the encrypted envelope + throwaway DEK.
- Do NOT reach into the Dropbox SDK directly from the test class — consume the contract via the harness.
