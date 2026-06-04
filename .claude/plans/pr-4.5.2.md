# PR 4.5.2 — Dropbox live verification

**Branch:** pr/4.5.2-dropbox-live-verification
**Blueprint sections:** §15.4 (Dropbox `upload_session/start`→`append_v2`→`finish` lifecycle), §15.7 (per-provider verification — Dropbox content-hash), §27.3 (Dropbox provider notes), plus §10.1–10.4 (frozen provider/setup contract reused from §4.5.1)
**Dev plan section:** phase-4.5 §4.5.2

## Scope
Add `DropboxLiveTests`, the second concrete live-verification provider class, binding the **unchanged** `LiveProviderHarness` (built in §4.5.1) to the production `DropboxSetup`/`DropboxProvider`. Credentials come from `FLASHSKINK_LIVE_DROPBOX_CLIENTID/_CLIENTSECRET`. The class self-skips at discovery exactly like the Drive class, so it runs and reports **Skipped** in CI without secrets and never gates a merge. Expected `src/` delta is **zero** unless a live run reveals a Dropbox adapter defect — which would be fixed here with a recorded-fake regression guard per cross-cutting decision 6.

---

## ⚠️ Deviation from the phase doc (REQUIRES APPROVAL)

### D1-Dropbox — Dropbox has no `ISupportsRemoteHashCheck` surface; the "content-hash path" building block is not implementable through the frozen contract
The §4.5.2 section note (phase doc line 172) says the verification building block "asserts Dropbox's content-hash path." But `DropboxProvider` **deliberately does not implement `ISupportsRemoteHashCheck`** (`DropboxProvider.cs` lines 31–36): Dropbox's `ContentHash` is a SHA-256-based digest, structurally incompatible with the `ulong` XXHash64 the capability interface returns, so it is **logged at finalisation only** — there is no contract surface to query it. This is the identical situation §4.5.1 resolved for Google Drive as **D1** (Drive's `md5Checksum` is also log-only).

**Proposed resolution (mirrors §4.5.1 D1 exactly):**
- The Dropbox verification building block is the **upload→download→byte-identical round-trip** (what the contract guarantees: bytes come back intact; integrity rests on the encrypted blob's AES-GCM authentication tag, Principle 6).
- The dedicated hash test becomes one cheap, non-network fact `Dropbox_RemoteHashCheck_NotSupported_ByDesign` asserting `provider is not ISupportsRemoteHashCheck` — documenting the contract reality and guarding against an accidental future change.
- **Harness is reused verbatim** (zero delta): the §4.5.1 harness already ships no hash exercise (D1 removed it for Drive); Dropbox needs nothing more.
- **Phase-doc edit (causal-link rule):** in the §4.5.2 section note, replace "Verification building block asserts Dropbox's content-hash path" with a note that Dropbox does not implement `ISupportsRemoteHashCheck` (content-hash is SHA-256, log-only), so integrity rests on the GCM tag + round-trip — same posture as Drive. Also adjust the §4.5.2 test-spec line "six `Dropbox_*` … tests" to match the §4.5.1 shape (seven methods incl. the opt-in purge and the resume theory), since §4.5.1's realised set is the actual template.

If you'd rather leave the phase doc untouched and absorb this only here, say so — but I recommend the edit so the doc stops asserting a content-hash path that the frozen contract cannot express.

---

## Files to create
- `tests/FlashSkink.Tests/Providers/Live/DropboxLiveTests.cs` — Dropbox live test class binding the harness to the production `DropboxSetup`. Near-verbatim structural clone of `GoogleDriveLiveTests.cs` (key `"DROPBOX"`, display name `"Dropbox (live test)"`, `Dropbox_*` method names). ~150 lines.

## Files to modify
- `dev-plan/phase-4.5-live-provider-verification.md` — apply the D1-Dropbox correction to the §4.5.2 section note + test-spec line (pending approval).

(No `.gitignore` / `CLAUDE.md` edits — both were done in §4.5.1 and already cover this provider: `.livetest-cache/` is ignored; the `LiveProvider` testing-convention note is provider-agnostic.)

## Dependencies
- NuGet: **none** new. The Dropbox SDK (`Dropbox.Api`) is already referenced transitively via `FlashSkink.Core`; the test project already references `FlashSkink.Core` + `FlashSkink.Core.Abstractions`, so `DropboxSetup`, `IStorageProvider`, `ISupportsRemoteHashCheck`, `LiveProviderHarness`, `LiveProviderFact/Theory` are all reachable.
- No new project references.

## Public API surface (test assembly — types are `internal`/test-public)
No `src/` public surface is added. One new test type:

### `FlashSkink.Tests.Providers.Live.DropboxLiveTests` (`public sealed class`)
Class-level `[Trait("Category","LiveProvider")]`; **no** shared `[Collection]` (parallel by default — decision 10). Mirrors `GoogleDriveLiveTests`:
- `private const string Key = "DROPBOX";`
- `private static LiveProviderHarness CreateHarness()` → `new(new DropboxSetup(NullLoggerFactory.Instance), Key, "Dropbox (live test)", NullLoggerFactory.Instance)`
- Seven test methods (signatures below).

No changes to `LiveProviderHarness`, `LiveProviderFactAttribute`, `LiveProviderTheoryAttribute`, `LiveProviderCredentials`, or `LiveTokenCache` — all consumed unchanged.

## Method-body contracts
Each method constructs the harness, builds/sets-up a provider, runs one harness exercise in a `try`, and disposes the provider in a `finally` via `LiveProviderHarness.DisposeProviderAsync` — identical structure to the Drive class:
- `Dropbox_Setup_RealConsent_ProducesUsableProvider` — `[LiveProviderFact(Key, requiresCache: false)]`; `await harness.RunSetupAsync(...)` (real browser consent once, caches the DEK-encrypted envelope under `.livetest-cache/dropbox.json`, asserts `CheckHealthAsync == Healthy`); `finally` dispose.
- `Dropbox_UploadDownload_RoundTrips` — `[LiveProviderFact(Key)]`; `BuildProviderFromCacheAsync` → `UploadDownloadRoundTripAsync` (3×`RangeSize` random bytes, `upload_session/start`→`append_v2`×N→`finish`, download, assert byte-identical, delete in `finally`).
- `Dropbox_Resume_FromPartialUpload_Completes` — `[LiveProviderTheory(Key)]` `[InlineData(3,1)]` `[InlineData(3,2)]` `[InlineData(4,3)]`; `ResumeFromPartialAsync` (upload N ranges, discard session, rebuild from persisted `SessionUri`, `GetUploadedBytesAsync` asserts the server-confirmed offset, resume the rest, finalise, download-verify). Exercises Dropbox's 48 h session TTL boundary well within range.
- `Dropbox_RemoteHashCheck_NotSupported_ByDesign` — `[LiveProviderFact(Key)]`; asserts `provider is not ISupportsRemoteHashCheck` (D1-Dropbox).
- `Dropbox_ExistsDeleteList_BehaveCorrectly` — `[LiveProviderFact(Key)]`; `SurfaceSweepAsync` (exists→true, list contains id, delete, exists→false, idempotent re-delete, used≥0, quota null-or-positive).
- `Dropbox_HealthQuotaUsed_ReturnSaneValues` — `[LiveProviderFact(Key)]`; `CheckHealthAsync == Healthy`, `GetUsedBytesAsync ≥ 0`, `GetQuotaBytesAsync` null-or-positive.
- `Dropbox_PurgeAllLiveTestObjects` — `[LiveProviderFact(Key, requiresEnvVar: "FLASHSKINK_LIVE_PURGE")]`; opt-in maintenance — `PurgeAllAsync` lists `_livetest/` and deletes every returned id. Skipped unless the flag is set.

`OperationCanceledException` is never caught (tests fail fast); no secret ever appears in an assertion message (Principle 26).

## Integration points (signatures already verified in src)
- `new DropboxSetup(ILoggerFactory)` — public production ctor (`DropboxSetup.cs` line 63), mirrors `GoogleDriveSetup`.
- `DropboxProvider : IStorageProvider, IAsyncDisposable` (`DropboxProvider.cs` line 54) — `DisposeProviderAsync` already prefers `IAsyncDisposable`.
- `DropboxSetup : IProviderSetup` — `GetAuthorizationUriAsync` / `ExchangeCodeAsync` / `CreateProviderAsync` consumed only through the frozen `IProviderSetup` contract by the harness; no Dropbox-specific calls from the test class.
- `LiveProviderHarness` public surface (from `.claude/plans/pr-4.5.1.md`): `RunSetupAsync`, `BuildProviderFromCacheAsync`, `UploadDownloadRoundTripAsync`, `ResumeFromPartialAsync(p, totalRanges, resumeAfterRanges, ct)`, `SurfaceSweepAsync`, `PurgeAllAsync`, `static DisposeProviderAsync`.
- `ISupportsRemoteHashCheck` (`FlashSkink.Core.Abstractions.Providers`) — referenced only for the negative `is not` assertion.

## Principles touched
- **P23** (provider contract frozen) — the test class consumes the contract; no signature changes. D1-Dropbox keeps us inside the contract (no new surface).
- **P26** (no secrets in output/logs) — no token/secret/code/verifier in any assertion message.
- **P12** (OS-agnostic) — the test class is platform-neutral; browser launch lives in `SystemBrowserLauncher`.
- **P1/P13/P14/P15/P16** — apply only if a Dropbox defect requires a `src/` fix (then fixed + recorded-fake guard under `Providers/Dropbox/`, decision 6).

## Test spec
`tests/FlashSkink.Tests/Providers/Live/DropboxLiveTests.cs` — class `DropboxLiveTests` (class-level `[Trait("Category","LiveProvider")]`; no shared `[Collection]`):
- `Dropbox_Setup_RealConsent_ProducesUsableProvider` — `[LiveProviderFact("DROPBOX", requiresCache: false)]`
- `Dropbox_UploadDownload_RoundTrips` — `[LiveProviderFact("DROPBOX")]`
- `Dropbox_Resume_FromPartialUpload_Completes` — `[LiveProviderTheory("DROPBOX")]` `[InlineData(3,1)]` `[InlineData(3,2)]` `[InlineData(4,3)]`
- `Dropbox_RemoteHashCheck_NotSupported_ByDesign` — `[LiveProviderFact("DROPBOX")]` (asserts `provider is not ISupportsRemoteHashCheck`)
- `Dropbox_ExistsDeleteList_BehaveCorrectly` — `[LiveProviderFact("DROPBOX")]`
- `Dropbox_HealthQuotaUsed_ReturnSaneValues` — `[LiveProviderFact("DROPBOX")]`
- `Dropbox_PurgeAllLiveTestObjects` — `[LiveProviderFact("DROPBOX", requiresEnvVar: "FLASHSKINK_LIVE_PURGE")]`

Each non-setup test calls `BuildProviderFromCacheAsync` and disposes the provider in a `finally`.

## Acceptance criteria
- [ ] Builds with zero warnings on all targets (`--warnaserror`).
- [ ] With **no** live env vars set, `dotnet test` is green and every `LiveProvider` test (Drive + Dropbox) reports **Skipped** (the CI-equivalent path).
- [ ] No new test project; the new file is under `tests/FlashSkink.Tests/Providers/Live/`.
- [ ] No `src/` change (unless a Dropbox defect is found — then fixed + a recorded-fake guard added under `Providers/Dropbox/`, decision 6).
- [ ] No change to `LiveProviderHarness` or the other §4.5.1 harness files (backward-compatible reuse, so §4.5.3 inherits it unchanged).
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] No credential/token/cache committed (`.env`, `.livetest-cache/` already gitignored).
- [ ] (manual, local) With Dropbox credentials set, all `Dropbox_*` tests pass against the real Dropbox API.

## Line-of-code budget
| File | Lines |
|---|---|
| `Live/DropboxLiveTests.cs` | ~150 |
| `dev-plan/phase-4.5-…md` (delta) | ~6 |
| **Total** | **~156** (all test/docs; src delta ~0, harness delta 0) |

## Non-goals
- Do NOT modify `LiveProviderHarness` or any other §4.5.1 harness file (Dropbox needs no extension; the contract surface is identical to Drive). A harness change would be a scope-creep flag.
- Do NOT add OneDrive live tests (§4.5.3).
- Do NOT modify `IStorageProvider`/`IProviderSetup`/`UploadSession`/`ProviderCredentials`/`ProviderSetupKind`/`ProviderHealth` (Principle 23).
- Do NOT wire any live test into CI as a required check; do NOT add a workflow supplying real credentials.
- Do NOT store the client secret in the token cache — read it from env each run; cache only the encrypted envelope + throwaway DEK.
- Do NOT reach into the Dropbox SDK directly from the test class — consume only `DropboxSetup` (via `IProviderSetup`) and the `IStorageProvider` contract through the harness.
- Do NOT re-edit `.gitignore` or `CLAUDE.md` (done in §4.5.1; already provider-agnostic).
