# PR 4.5.3 — OneDrive live verification

**Branch:** pr/4.5.3-onedrive-live-verification
**Blueprint sections:** §15.5 (OneDrive `createUploadSession` + ranged `PUT` lifecycle), §15.7 (per-provider verification — quickXorHash/SHA1), §27.4 (OneDrive provider notes), plus §10.1–10.4 (frozen provider/setup contract reused from §4.5.1)
**Dev plan section:** phase-4.5 §4.5.3

## Scope
Add `OneDriveLiveTests`, the third and final live-verification provider class, binding the **unchanged** `LiveProviderHarness` (built in §4.5.1, refined in §4.5.2) to the production `OneDriveSetup`/`OneDriveProvider`. Credentials: `FLASHSKINK_LIVE_ONEDRIVE_CLIENTID/_CLIENTSECRET`. The class self-skips at discovery exactly like the Drive/Dropbox classes, so it runs and reports **Skipped** in CI without secrets and never gates a merge. Expected `src/` delta is **zero** and harness delta is **zero** — unless a live run reveals a OneDrive adapter defect, which would be fixed here with a recorded-fake regression guard per cross-cutting decision 6. This closes Phase 4.5.

---

## ⚠️ Deviation from the phase doc (REQUIRES APPROVAL)

### D1-OneDrive — OneDrive has no `ISupportsRemoteHashCheck` surface; the "quickXorHash/SHA1 path" building block is not implementable through the frozen contract
The §4.5.3 section note (phase doc line 190) says the verification building block "asserts the quickXorHash/SHA1 path from Graph's metadata." But `OneDriveProvider` **deliberately does not implement a remote-hash-check capability** (`OneDriveProvider.cs` lines 31–34): "OneDrive's QuickXorHash is implemented in `OneDriveQuickXorHash` for future use but is not wired here; the AES-GCM authentication tag on the encrypted blob is the cryptographic authenticator." There is no contract surface (`ISupportsRemoteHashCheck`) to query a remote hash. This is the identical situation §4.5.1 resolved for Google Drive (**D1**) and §4.5.2 for Dropbox (**D1-Dropbox**).

**Proposed resolution (mirrors §4.5.1/§4.5.2 exactly):**
- The OneDrive integrity building block is the **upload→download→byte-identical round-trip** (GCM-tag-backed).
- The dedicated hash test is the cheap, non-network fact `OneDrive_RemoteHashCheck_NotSupported_ByDesign` asserting `provider is not ISupportsRemoteHashCheck`.
- **Harness reused verbatim** (zero delta) — it already ships no hash exercise.
- **Phase-doc edit (causal-link rule):** in the §4.5.3 section note, replace "asserts the quickXorHash/SHA1 path from Graph's metadata" with a note that OneDrive does not implement `ISupportsRemoteHashCheck` (QuickXorHash is computed but unwired), so integrity rests on the GCM tag + round-trip — same posture as Drive/Dropbox. Adjust the "six `OneDrive_*` tests" line to the realised §4.5.1 shape (seven methods incl. the opt-in purge + the resume theory).

If you'd rather leave the phase doc untouched and absorb this only here, say so — but I recommend the edit so the doc stops asserting a hash path the frozen contract cannot express.

---

## Verified up front (no fixed-port fix needed, unlike §4.5.2)
- **OneDrive does NOT need `IRequiresFixedRedirectPort`.** Microsoft identity special-cases loopback (`127.0.0.1`) redirect URIs and **ignores the port at runtime** (Microsoft Learn, "Redirect URI best practices"; RFC 8252 §7.3). So FlashSkink's ephemeral-port loopback (`http://127.0.0.1:{port}/oauth-callback/`) works; the developer registers `http://127.0.0.1/oauth-callback/` once (port-agnostic) under the app's *Mobile and desktop applications* platform. `OneDriveSetup` correctly does **not** implement the capability — the default `Prepare(null)` ephemeral path is right.
- **OneDrive resume is compatible with the post-§4.5.2 harness.** `OneDriveProvider.GetUploadedBytesAsync` queries the server (`GET uploadUrl` → `nextExpectedRanges` → `NextOffsetFromRanges`), returning the true confirmed offset — like Google Drive. The harness now passes `BytesUploaded = offset`, and the server confirms the same value, so `Assert.Equal(offset, confirmed)` holds. No harness change.
- **A client secret is expected.** The CLI `setup add` requires `--client-id` and `--client-secret` for every cloud provider, and the harness skip-gate requires both `FLASHSKINK_LIVE_ONEDRIVE_CLIENTID` and `_CLIENTSECRET`. So the developer registers an Entra app **with** a client secret (the OneDrive adapter also supports PKCE-only public clients, but V1's BYOC path uses a secret). Documented in the README.

---

## Files to create
- `tests/FlashSkink.Tests/Providers/Live/OneDriveLiveTests.cs` — OneDrive live test class binding the harness to the production `OneDriveSetup`. Near-verbatim structural clone of `DropboxLiveTests.cs`/`GoogleDriveLiveTests.cs` (key `"ONEDRIVE"`, display name `"OneDrive (live test)"`, `OneDrive_*` method names). ~150 lines.

## Files to modify
- `tests/FlashSkink.Tests/Providers/Live/README.md` — add a OneDrive subsection to the existing "Redirect URI registration (provider-specific)" block: register `http://127.0.0.1/oauth-callback/` (loopback, http, port-ignored; added under *Mobile and desktop applications* / via the `replyUrlsWithType` manifest entry), scopes `Files.ReadWrite offline_access`, and that a client secret is required. ~10 lines.
- `dev-plan/phase-4.5-live-provider-verification.md` — apply the D1-OneDrive correction to the §4.5.3 section note + test-spec line (pending approval).

(No `.gitignore` / `CLAUDE.md` edits — done in §4.5.1, already provider-agnostic. No harness edit — reused verbatim.)

## Dependencies
- NuGet: **none** new. OneDrive uses raw HTTP (no Graph SDK/MSAL); the test project already references `FlashSkink.Core` + `FlashSkink.Core.Abstractions`, so `OneDriveSetup`, `IStorageProvider`, `ISupportsRemoteHashCheck`, `LiveProviderHarness`, `LiveProviderFact/Theory` are all reachable.
- No new project references.

## Public API surface (test assembly)
No `src/` public surface is added. One new test type:

### `FlashSkink.Tests.Providers.Live.OneDriveLiveTests` (`public sealed class`)
Class-level `[Trait("Category","LiveProvider")]`; **no** shared `[Collection]` (parallel by default — decision 10). Mirrors `DropboxLiveTests`:
- `private const string Key = "ONEDRIVE";`
- `private static LiveProviderHarness CreateHarness()` → `new(new OneDriveSetup(NullLoggerFactory.Instance), Key, "OneDrive (live test)", NullLoggerFactory.Instance)`
- Seven test methods (signatures below).

No changes to `LiveProviderHarness`, `LiveProviderFactAttribute`, `LiveProviderTheoryAttribute`, `LiveProviderCredentials`, or `LiveTokenCache` — all consumed unchanged.

## Method-body contracts
Each method constructs the harness, builds/sets-up a provider, runs one harness exercise in a `try`, and disposes the provider in a `finally` via `LiveProviderHarness.DisposeProviderAsync` — identical structure to the Drive/Dropbox classes:
- `OneDrive_Setup_RealConsent_ProducesUsableProvider` — `[LiveProviderFact(Key, requiresCache: false)]`; `RunSetupAsync` (real browser consent once → caches the DEK-encrypted envelope under `.livetest-cache/onedrive.json` → asserts `CheckHealthAsync == Healthy`); `finally` dispose.
- `OneDrive_UploadDownload_RoundTrips` — `[LiveProviderFact(Key)]`; `BuildProviderFromCacheAsync` → `UploadDownloadRoundTripAsync` (3×`RangeSize`, `createUploadSession` → ranged `PUT`s → final-chunk DriveItem, download, byte-identical, delete in `finally`).
- `OneDrive_Resume_FromPartialUpload_Completes` — `[LiveProviderTheory(Key)]` `[InlineData(3,1)]` `[InlineData(3,2)]` `[InlineData(4,3)]`; `ResumeFromPartialAsync` (rebuild session from persisted `SessionUri`+offset, `GetUploadedBytesAsync` → server `nextExpectedRanges` offset, resume, finalise, download-verify).
- `OneDrive_RemoteHashCheck_NotSupported_ByDesign` — `[LiveProviderFact(Key)]`; asserts `provider is not ISupportsRemoteHashCheck` (D1-OneDrive).
- `OneDrive_ExistsDeleteList_BehaveCorrectly` — `[LiveProviderFact(Key)]`; `SurfaceSweepAsync`.
- `OneDrive_HealthQuotaUsed_ReturnSaneValues` — `[LiveProviderFact(Key)]`; `CheckHealthAsync == Healthy`, `GetUsedBytesAsync ≥ 0`, `GetQuotaBytesAsync` null-or-positive.
- `OneDrive_PurgeAllLiveTestObjects` — `[LiveProviderFact(Key, requiresEnvVar: "FLASHSKINK_LIVE_PURGE")]`; opt-in maintenance — `PurgeAllAsync`. Skipped unless the flag is set.

`OperationCanceledException` is never caught (tests fail fast); no secret ever appears in an assertion message (Principle 26).

## Integration points (verified in src)
- `new OneDriveSetup(ILoggerFactory)` — public production ctor (`OneDriveSetup.cs` line 40), mirrors `GoogleDriveSetup`/`DropboxSetup`.
- `OneDriveProvider : IStorageProvider, IAsyncDisposable` (`OneDriveProvider.cs` line 36) — `DisposeProviderAsync` already prefers `IAsyncDisposable`.
- `OneDriveSetup : IProviderSetup` — `GetAuthorizationUriAsync`/`ExchangeCodeAsync`/`CreateProviderAsync` consumed only through the frozen `IProviderSetup` contract by the harness; no OneDrive-specific calls from the test class.
- `LiveProviderHarness` public surface (unchanged): `RunSetupAsync`, `BuildProviderFromCacheAsync`, `UploadDownloadRoundTripAsync`, `ResumeFromPartialAsync(p, totalRanges, resumeAfterRanges, ct)`, `SurfaceSweepAsync`, `PurgeAllAsync`, `static DisposeProviderAsync`.
- `ISupportsRemoteHashCheck` (`FlashSkink.Core.Abstractions.Providers`) — referenced only for the negative `is not` assertion.

## Principles touched
- **P23** (provider contract frozen) — the test class consumes the contract; no signature changes. D1-OneDrive keeps us inside the contract (no new surface). No capability interface needed (OneDrive needs neither hash-check nor fixed-port).
- **P26** (no secrets in output/logs) — no token/secret/code/verifier in any assertion message.
- **P12** (OS-agnostic) — the test class is platform-neutral; browser launch lives in `SystemBrowserLauncher`.
- **P1/P13/P14/P15/P16** — apply only if a OneDrive defect requires a `src/` fix (then fixed + recorded-fake guard under `Providers/OneDrive/`, decision 6).

## Test spec
`tests/FlashSkink.Tests/Providers/Live/OneDriveLiveTests.cs` — class `OneDriveLiveTests` (class-level `[Trait("Category","LiveProvider")]`; no shared `[Collection]`):
- `OneDrive_Setup_RealConsent_ProducesUsableProvider` — `[LiveProviderFact("ONEDRIVE", requiresCache: false)]`
- `OneDrive_UploadDownload_RoundTrips` — `[LiveProviderFact("ONEDRIVE")]`
- `OneDrive_Resume_FromPartialUpload_Completes` — `[LiveProviderTheory("ONEDRIVE")]` `[InlineData(3,1)]` `[InlineData(3,2)]` `[InlineData(4,3)]`
- `OneDrive_RemoteHashCheck_NotSupported_ByDesign` — `[LiveProviderFact("ONEDRIVE")]` (asserts `provider is not ISupportsRemoteHashCheck`)
- `OneDrive_ExistsDeleteList_BehaveCorrectly` — `[LiveProviderFact("ONEDRIVE")]`
- `OneDrive_HealthQuotaUsed_ReturnSaneValues` — `[LiveProviderFact("ONEDRIVE")]`
- `OneDrive_PurgeAllLiveTestObjects` — `[LiveProviderFact("ONEDRIVE", requiresEnvVar: "FLASHSKINK_LIVE_PURGE")]`

Each non-setup test calls `BuildProviderFromCacheAsync` and disposes the provider in a `finally`.

## Acceptance criteria
- [ ] Builds with zero warnings on all targets (`--warnaserror`).
- [ ] With **no** live env vars set, `dotnet test` is green and every `LiveProvider` test (Drive + Dropbox + OneDrive) reports **Skipped** (the CI-equivalent path).
- [ ] No new test project; the new file is under `tests/FlashSkink.Tests/Providers/Live/`.
- [ ] No `src/` change (unless a OneDrive defect is found — then fixed + a recorded-fake guard added under `Providers/OneDrive/`, decision 6).
- [ ] No change to `LiveProviderHarness` or the other §4.5.1 harness files (backward-compatible reuse — Phase 4.5 closes with the harness stable).
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] No credential/token/cache committed (`.env`, `.livetest-cache/` already gitignored).
- [ ] (manual, local) With OneDrive credentials set **and** `http://127.0.0.1/oauth-callback/` registered, all `OneDrive_*` tests pass against the real OneDrive API.

## Line-of-code budget
| File | Lines |
|---|---|
| `Live/OneDriveLiveTests.cs` | ~150 |
| `Live/README.md` (delta) | ~10 |
| `dev-plan/phase-4.5-…md` (delta) | ~6 |
| **Total** | **~166** (all test/docs; src delta ~0, harness delta 0) |

## Non-goals
- Do NOT modify `LiveProviderHarness` or any other §4.5.1 harness file (OneDrive needs no extension; resume + setup work through the existing contract path).
- Do NOT add a fixed-port capability for OneDrive (verified unnecessary — Microsoft identity ignores the loopback port).
- Do NOT modify `IStorageProvider`/`IProviderSetup`/`UploadSession`/`ProviderCredentials`/`ProviderSetupKind`/`ProviderHealth` (Principle 23).
- Do NOT wire any live test into CI as a required check; do NOT add a workflow supplying real credentials.
- Do NOT store the client secret in the token cache — read it from env each run; cache only the encrypted envelope + throwaway DEK.
- Do NOT reach into Graph/HTTP directly from the test class — consume only `OneDriveSetup` (via `IProviderSetup`) and the `IStorageProvider` contract through the harness.
- Do NOT re-edit `.gitignore` or `CLAUDE.md` (done in §4.5.1).
