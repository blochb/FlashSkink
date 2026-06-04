# PR 4.5.1 — Live-verification harness + Google Drive

**Branch:** pr/4.5.1-live-harness-google-drive
**Blueprint sections:** §10.1–10.4 (frozen provider/setup contract), §15.1–15.3 (resumable upload lifecycle), §15.7 (per-provider verification), §24.3 (BYOC setup), §24.5 (loopback OAuth)
**Dev plan section:** phase-4.5 §4.5.1

## Scope
Build the reusable live-verification harness (self-skipping `[LiveProviderFact]`/`[LiveProviderTheory]`, BYOC credential resolver, gitignored token cache, the building-block exercises) and the first concrete provider test class — Google Drive — all under `tests/FlashSkink.Tests/Providers/Live/`. The harness is proven end-to-end against a real provider here rather than in the abstract; §4.5.2/§4.5.3 reuse it unchanged. Plus `.gitignore` + `CLAUDE.md` edits and a README. Expected `src/` delta is **zero** unless a live run reveals a Drive defect (which would be fixed here per cross-cutting decision 6).

---

## ⚠️ Deviations from the phase doc (REQUIRE APPROVAL)

Two items in the merged phase doc (`dev-plan/phase-4.5-live-provider-verification.md`) over-specify relative to the **frozen** `IStorageProvider` contract. I propose correcting both **in this PR** (editing the phase doc too, per the causal-link rule), because §4.5.1 is where the harness contract is fixed for all three providers.

### D1 — Remote-hash-check building block does not apply to Google Drive
`GoogleDriveProvider` deliberately does **not** implement `ISupportsRemoteHashCheck` (its class XML says so), and its `md5Checksum` is only *logged* at finalisation — there is no contract surface to query a remote hash. So a Drive "remote hash check" test is not implementable through the frozen contract.

**Proposed resolution:**
- The harness still ships a **capability-gated** `RemoteHashCheckAsync` exercise: `if (provider is ISupportsRemoteHashCheck hc) { compare hc.GetRemoteXxHash64Async(remoteId) to the local XXHash64 }`. It is reusable by any future provider/`FileSystemProvider` that implements the interface.
- For **Google Drive**, the integrity building block is the **upload→download→byte-identical round-trip** (what the contract actually guarantees — the bytes come back intact). The dedicated hash theory is replaced by one cheap, non-network fact `GoogleDrive_RemoteHashCheck_NotSupported_ByDesign` asserting `provider is not ISupportsRemoteHashCheck`, which documents the contract reality and guards against an accidental future change.
- Phase-doc edit: building block #4 becomes "for providers that implement `ISupportsRemoteHashCheck`; cloud providers rely on the GCM tag + round-trip integrity" (and the harness method stays capability-gated).

### D2 — Age-bounded startup sweep is not implementable; replace with self-scoped deletion
Decision 5 specifies a startup reap that parses `{unixSeconds}` from each run-folder **name**. But `IStorageProvider.ListAsync` returns **opaque remote IDs, not names** (`GoogleDriveProvider.ListAsync` returns Drive file IDs), so age cannot be derived from a list result through the contract.

**Proposed resolution (strictly safer than the age reap):**
- **Default cleanup is self-scoped:** each test tracks the remote IDs it created and deletes only those, in a `finally`. Self-scoped deletion can **never** touch another runner's data, regardless of timing — this satisfies the original concurrency-safety intent more robustly than an age threshold.
- **No automatic cross-run reap.** Stale leftovers from a crashed run are handled by an **explicit, opt-in** maintenance test `GoogleDrive_PurgeAllLiveTestObjects`, gated on `[LiveProviderFact("GDRIVE")]` **plus** an extra env flag `FLASHSKINK_LIVE_PURGE=1`, which lists `_livetest/` and deletes every returned ID. Documented "run only when no concurrent run is active."
- Per-test prefix stays `_livetest/{runId}/{testName}-{guid}/` (`runId = {unixSeconds}-{guid}`) for human-readable grouping; the unixSeconds is cosmetic now, not functional.
- Phase-doc edits: decision 5 (replace the age reap with self-scoped + opt-in purge) and the matching full-phase acceptance bullet (drop the "timestamp-parse + age-filter unit test"; replace with "self-scoped deletion; opt-in purge").

If you'd rather keep the phase doc as-is and absorb these only in the per-section plans, say so — but I recommend the doc edits so §4.5.2/§4.5.3 inherit the corrected contract.

---

## Files to create
- `tests/FlashSkink.Tests/Providers/Live/LiveProviderFactAttribute.cs` — `sealed class LiveProviderFactAttribute : FactAttribute`; ctor `(string providerKey, bool requiresCache = true, string? requiresEnvVar = null)`; sets `Skip` (discovery-time) when creds absent, or when `requiresCache` and the token-cache file is missing, or when `requiresEnvVar` is set and that env var is empty. ~70 lines.
- `tests/FlashSkink.Tests/Providers/Live/LiveProviderTheoryAttribute.cs` — `sealed class LiveProviderTheoryAttribute : TheoryAttribute`; same ctor + skip logic, factored through a shared `LiveSkip.Reason(...)` helper so the two attributes don't duplicate it. ~25 lines (+ shared helper ~50). ~75 lines total across both.
- `tests/FlashSkink.Tests/Providers/Live/LiveProviderCredentials.cs` — `.env` loader (hand-rolled `KEY=VALUE`) + per-key resolver returning `(ProviderCredentials Credentials, bool Present)`; repo-root locator (walk up for `FlashSkink.sln`). Never logs secrets. ~80 lines.
- `tests/FlashSkink.Tests/Providers/Live/LiveTokenCache.cs` — load/save `{ encryptedTokenBase64, dekBase64 }` JSON at `<repoRoot>/.livetest-cache/<key>.json`. ~90 lines.
- `tests/FlashSkink.Tests/Providers/Live/LiveProviderHarness.cs` — the reusable exercises (see contracts below). ~280 lines.
- `tests/FlashSkink.Tests/Providers/Live/GoogleDriveLiveTests.cs` — Drive test class binding the harness to the production `GoogleDriveSetup`. ~160 lines.
- `tests/FlashSkink.Tests/Providers/Live/README.md` — run instructions, env vars, one-time consent, cache location, purge flag, "never gates CI". ~75 lines.

## Files to modify
- `.gitignore` — add `.livetest-cache/` (`.env`/`.env.local` already ignored).
- `CLAUDE.md` (§ "Testing") — document the `LiveProvider` category (self-skipping, local-only, `--filter Category=LiveProvider`, never a required CI check). ~8 lines.
- `dev-plan/phase-4.5-live-provider-verification.md` — apply the D1 and D2 corrections (pending approval).

## Dependencies
- NuGet: **none** (xUnit v2 2.9.3 already present; no `DotNetEnv`, no `Xunit.SkippableFact`). The Drive SDK (`Google.Apis.Drive.v3`) is already referenced transitively via `FlashSkink.Core`.
- The test project already references `FlashSkink.Core` and `FlashSkink.Core.Abstractions`, so `GoogleDriveSetup`, `LoopbackOAuthCapture`, `ProviderCredentials`, `IStorageProvider`, `UploadSession`, `UploadConstants`, `ISupportsRemoteHashCheck` are all reachable.

## Public API surface (test assembly — types are `internal`)
All types are `internal sealed` in namespace `FlashSkink.Tests.Providers.Live` (test assembly; no public surface added to `src/`).

### `LiveProviderFactAttribute : Xunit.FactAttribute`
- `public LiveProviderFactAttribute(string providerKey, bool requiresCache = true, string? requiresEnvVar = null)` — at discovery (after `.env` load): (1) if `FLASHSKINK_LIVE_{KEY}_CLIENTID`/`_CLIENTSECRET` absent → `Skip = "Set FLASHSKINK_LIVE_{KEY}_CLIENTID/_CLIENTSECRET to run live {key} tests."`; (2) else if `requiresCache` and `<repoRoot>/.livetest-cache/{key.ToLowerInvariant()}.json` is missing → `Skip = "Run the {key} setup test first to populate the token cache."`; (3) else if `requiresEnvVar` is non-null and that variable is empty → `Skip = "Set {requiresEnvVar}=1 to run this maintenance test."`. Produces a true **Skipped** status (no false-positive pass).

### `LiveProviderTheoryAttribute : Xunit.TheoryAttribute`
- `public LiveProviderTheoryAttribute(string providerKey, bool requiresCache = true, string? requiresEnvVar = null)` — identical skip logic via the shared `LiveSkip.Reason(providerKey, requiresCache, requiresEnvVar)` helper.

### `LiveSkip` (static helper)
- `static string? Reason(string providerKey, bool requiresCache, string? requiresEnvVar)` — the single source of the three skip checks above; returns `null` when the test may run. Both attributes set `Skip = LiveSkip.Reason(...)`.

### `LiveProviderCredentials` (static)
- `static (ProviderCredentials Credentials, bool Present) Resolve(string providerKey)`
- `static string RepoRoot()` — walks up from `AppContext.BaseDirectory` to the dir containing `FlashSkink.sln`.
- `.env` parsed once (lazy, thread-safe); values populate the process env only if not already set.

### `LiveTokenCache` (static)
- `static bool TryLoad(string providerKey, out byte[] encryptedToken, out byte[] dek)`
- `static void Save(string providerKey, byte[] encryptedToken, ReadOnlyMemory<byte> dek)`
- Cache dir created on first `Save`; file is `<repoRoot>/.livetest-cache/<key>.json`.

### `LiveProviderHarness`
Constructed per test class with the provider's `IProviderSetup`, the provider key, a display name, and an `ILoggerFactory` (`NullLoggerFactory` is fine).
- `async Task<IStorageProvider> RunSetupAsync(CancellationToken ct)` — see contract.
- `async Task<IStorageProvider> BuildProviderFromCacheAsync(CancellationToken ct)`
- `async Task UploadDownloadRoundTripAsync(IStorageProvider p, CancellationToken ct)`
- `async Task ResumeFromPartialAsync(IStorageProvider p, int totalRanges, int resumeAfterRanges, CancellationToken ct)`
- `async Task SurfaceSweepAsync(IStorageProvider p, CancellationToken ct)`
- `async Task PurgeAllAsync(IStorageProvider p, CancellationToken ct)` — lists `_livetest/`, deletes every ID (opt-in maintenance).
- helpers: `string NewObjectName(string testName)` → `_livetest/{runId}/{testName}-{guid}/blob.bin`; process-wide `runId`.
- `static async Task DisposeProviderAsync(IStorageProvider p)` — `if (p is IAsyncDisposable d) await d.DisposeAsync();`

## Method-body contracts
- **`RunSetupAsync`**: if `LiveTokenCache.TryLoad` succeeds → delegate to `BuildProviderFromCacheAsync`. Else: generate a random 32-byte DEK; `new LoopbackOAuthCapture(loggerFactory)`; `Prepare()`; `setup.GetAuthorizationUriAsync(ctx.RedirectUri, ctx.CodeChallenge, creds, ct)`; `AwaitAuthorizationCodeAsync(ctx, uri, ct)` (opens real browser); `setup.ExchangeCodeAsync(code, ctx.CodeVerifier, ctx.RedirectUri, creds, dek, ct)` → envelope; `LiveTokenCache.Save(key, envelope, dek)`; then `setup.CreateProviderAsync(providerId, displayName, envelope, creds, providerConfigJson: null, dek, ct)`. Assert each `Result.Success` (via `ResultAssertions`). Assert `CheckHealthAsync` returns `ProviderHealthStatus.Healthy`. Returns the provider.
- **`BuildProviderFromCacheAsync`**: cache presence is guaranteed by `[LiveProviderFact(requiresCache: true)]` at discovery, so this method `TryLoad`s and — defensively — `Assert.True(loaded, "...run the setup test first")` if a concurrent run deleted the cache mid-flight, then `CreateProviderAsync(providerId, displayName, encryptedToken, credsFromEnv, providerConfigJson: null, dek, ct)` and returns.
- **`UploadDownloadRoundTripAsync`**: random `3 * UploadConstants.RangeSize` bytes (3 ranges). `BeginUploadAsync(name, total, ct)`; loop `UploadRangeAsync(session, offset, chunk, ct)` in `RangeSize` chunks; `FinaliseUploadAsync` → remoteId. `DownloadAsync(remoteId)` → read fully → `Assert.Equal` byte arrays. `finally`: `DeleteAsync(remoteId)`.
- **`ResumeFromPartialAsync(total, resumeAfter)`**: build a blob spanning `total` ranges (last partial when `total==4`). `BeginUploadAsync`; upload `resumeAfter` ranges; **discard** the `UploadSession`; rebuild a new `UploadSession { SessionUri = saved, ExpiresAt, BytesUploaded = 0, TotalBytes }`; `GetUploadedBytesAsync(rebuilt)` → assert `== resumeAfter * RangeSize`; resume remaining ranges from that offset; `FinaliseUploadAsync`; download-verify byte-identical. `finally`: delete.
- **`SurfaceSweepAsync`**: upload+finalise one small blob; `ExistsAsync(id)`→true; `ListAsync("_livetest/")` contains `id`; `DeleteAsync(id)`; `ExistsAsync(id)`→false; `DeleteAsync(id)` again → still `Ok` (idempotent). `GetUsedBytesAsync` ≥ 0; `GetQuotaBytesAsync` is null or > 0.
- All harness methods: `OperationCanceledException` is not caught (tests fail fast); secrets never appear in assertion messages (Principle 26).

## Integration points (signatures already verified in src)
- `new GoogleDriveSetup(ILoggerFactory)` (public production ctor).
- `IProviderSetup.GetAuthorizationUriAsync / ExchangeCodeAsync / CreateProviderAsync` (see contract above; `CreateProviderAsync` requires non-empty `ClientId` **and** `ClientSecret` + non-empty `encryptedToken`).
- `new LoopbackOAuthCapture(ILoggerFactory)`; `Result<OAuthCaptureContext> Prepare()`; `Task<Result<string>> AwaitAuthorizationCodeAsync(ctx, Uri, ct)`.
- `IStorageProvider`: `BeginUploadAsync(string,long,ct)`, `GetUploadedBytesAsync(UploadSession,ct)`, `UploadRangeAsync(UploadSession,long,ReadOnlyMemory<byte>,ct)`, `FinaliseUploadAsync(UploadSession,ct)→remoteId`, `DownloadAsync(string,ct)→Stream`, `DeleteAsync`, `ExistsAsync`, `ListAsync(prefix,ct)→IReadOnlyList<string>` (**IDs**), `CheckHealthAsync`, `GetUsedBytesAsync`, `GetQuotaBytesAsync`.
- `UploadConstants.RangeSize` = **5 MiB** (phase doc said ">4 MiB"; using the real constant).
- `UploadSession` is a public `record` with `required SessionUri/ExpiresAt/BytesUploaded/TotalBytes` — rebuildable for the resume test.
- `ResultAssertions` (existing test helper) for `.Success` assertions.

## Principles touched
- **P23** (provider contract frozen) — harness consumes the contract only; no signature changes. D1/D2 keep us inside the contract.
- **P26** (no secrets in output/logs) — no token/secret/code/verifier in assertions or logs.
- **P12** (OS-agnostic) — harness is platform-neutral; browser launch lives in `SystemBrowserLauncher`.
- **P1/P13/P14/P15/P16** — apply only if a Drive defect requires a `src/` fix.

## Test spec
`tests/FlashSkink.Tests/Providers/Live/GoogleDriveLiveTests.cs` — class `GoogleDriveLiveTests` (class-level `[Trait("Category","LiveProvider")]`; no shared `[Collection]`):
- `GoogleDrive_Setup_RealConsent_ProducesUsableProvider` — `[LiveProviderFact("GDRIVE", requiresCache: false)]`; `RunSetupAsync` then health == Healthy. (Cache **not** required — this test creates it.)
- `GoogleDrive_UploadDownload_RoundTrips` — `[LiveProviderFact("GDRIVE")]` (requiresCache default true).
- `GoogleDrive_Resume_FromPartialUpload_Completes` — `[LiveProviderTheory("GDRIVE")]` `[InlineData(3,1)]` `[InlineData(3,2)]` `[InlineData(4,3)]`.
- `GoogleDrive_RemoteHashCheck_NotSupported_ByDesign` — `[LiveProviderFact("GDRIVE")]` (builds a provider from cache, then asserts `provider is not ISupportsRemoteHashCheck`). *(replaces the Drive hash theory — D1)*
- `GoogleDrive_ExistsDeleteList_BehaveCorrectly` — `[LiveProviderFact("GDRIVE")]`.
- `GoogleDrive_HealthQuotaUsed_ReturnSaneValues` — `[LiveProviderFact("GDRIVE")]`.
- `GoogleDrive_PurgeAllLiveTestObjects` — `[LiveProviderFact("GDRIVE", requiresEnvVar: "FLASHSKINK_LIVE_PURGE")]`. *(opt-in maintenance — D2; Skipped unless the flag is set)*

Each non-setup test calls `BuildProviderFromCacheAsync` and disposes the provider in a `finally`.

## Acceptance criteria
- [ ] Builds with zero warnings on all targets.
- [ ] With **no** live env vars set, `dotnet test` is green and every `LiveProvider` test reports **Skipped** (the CI-equivalent path).
- [ ] No new test project; all files under `tests/FlashSkink.Tests/Providers/Live/`.
- [ ] No `src/` change (unless a Drive defect is found — then fixed + a recorded-fake guard added under `Providers/GoogleDrive/`, decision 6).
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] `.gitignore` ignores `.livetest-cache/`; no credential/token/cache committed.
- [ ] `CLAUDE.md` documents the `LiveProvider` category.
- [ ] (manual, local) With Drive credentials set, all `GoogleDrive_*` tests pass against the real API.

## Resolved (Gate 1) — attribute-based cache/flag checking
xUnit v2 has no `Assert.Skip`, but we get true **Skipped** status with zero dependencies by doing **all** skip decisions at discovery time in the attribute ctor: credentials-present, `requiresCache` (token-cache file exists), and `requiresEnvVar` (opt-in flag). A non-setup test with creds present but no cache shows as **Skipped** ("run the setup test first"), not a false-positive pass. The runtime guard in `BuildProviderFromCacheAsync` is purely defensive (concurrent cache deletion). This supersedes the earlier "pass-with-message" option.

## Line-of-code budget
| File | Lines |
|---|---|
| `Live/LiveProviderFactAttribute.cs` | ~45 |
| `Live/LiveProviderTheoryAttribute.cs` | ~45 |
| `Live/LiveProviderCredentials.cs` | ~80 |
| `Live/LiveTokenCache.cs` | ~90 |
| `Live/LiveProviderHarness.cs` | ~280 |
| `Live/GoogleDriveLiveTests.cs` | ~160 |
| `Live/README.md` | ~75 |
| `.gitignore` / `CLAUDE.md` / phase-doc (deltas) | ~30 |
| **Total** | **~805** (all test/docs; src delta ~0) |

## Non-goals
- Do NOT build a console-app probe; do NOT add a new test project.
- Do NOT wire any live test into CI as a required check; do NOT add a workflow supplying real credentials.
- Do NOT add Dropbox/OneDrive live tests (§4.5.2/§4.5.3).
- Do NOT modify `IStorageProvider`/`IProviderSetup`/`UploadSession`/`ProviderCredentials`/`ProviderSetupKind`/`ProviderHealth` (Principle 23).
- Do NOT store the client secret in the token cache — read it from env each run; cache only the encrypted envelope + throwaway DEK.
- Do NOT reach into Drive's SDK directly from the harness — consume only the `IStorageProvider`/`IProviderSetup` contract (keeps the harness provider-agnostic for §4.5.2/§4.5.3).
