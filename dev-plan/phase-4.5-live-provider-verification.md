# Phase 4.5 — Live provider verification

**Status marker:** This phase follows the standard session protocol defined in `CLAUDE.md`. Each section below (§4.5.1 through §4.5.3) maps to one PR, executed via `read section 4.5.X of the dev plan and perform`. Gate 1 (plan approval) and Gate 2 (implementation approval) are required for every section. Sections must be executed in order — §4.5.1 builds the shared harness that §4.5.2 and §4.5.3 reuse.

**Numbering note.** `§4.5` (two-part) is the OneDrive provider section *of Phase 4*. **Phase 4.5** uses three-part section numbers — `§4.5.1`, `§4.5.2`, `§4.5.3` — exactly as **Phase 3.5** used `§3.5.1/.2/.3` alongside Phase 3's own `§3.5`/`§3.6`. The three-part number disambiguates from the two-part section. Plan files are `.claude/plans/pr-4.5.X.md`; branches are `pr/4.5.X-<description>` (the `pr/X.Y.Z` form the CI `plan-check` job already accepts, per the Phase 3.5 precedent).

**Sequencing:** Phase 4.5 lands after Phase 4 closes (§4.6b merged). Phase 5 (healing, cross-tail verification, health monitoring, recovery) begins after §4.5.3 — and benefits directly from this phase, because Phase 5's `HealthMonitorService` and recovery paths lean on `CheckHealthAsync`, `ListAsync`, `GetUsedBytesAsync`, and `GetQuotaBytesAsync`, all of which §4.5 exercises against the real APIs for the first time.

**Why this is a phase, not a `chore`.** A live test that finds a real adapter defect (a wrong hash comparison, a resume offset off-by-one, a mishandled session-expiry response) does not produce test code alone — it produces a **source fix**. Phase status gives each fix a properly Gate-reviewed PR and, per cross-cutting decision 6, a regression guard added to the CI-safe recorded-fake suite. The deliverable of this phase is *confidence that each provider works against reality*, plus whatever source corrections that confidence requires.

---

## Goal

After Phase 4.5:

- A developer holding their own BYOC OAuth credentials for Google Drive, Dropbox, and OneDrive can run `dotnet test --filter Category=LiveProvider` locally and confirm, against the real provider APIs, that **each** provider:
  1. completes the OAuth setup dance and yields a usable `IStorageProvider` (the **setup-with-secrets** building block);
  2. uploads a multi-range encrypted blob and downloads it byte-identical (the **upload** and **download** building blocks);
  3. resumes a partially-uploaded blob from the provider-confirmed offset and finishes it intact (the **upload-resume** building block);
  4. passes its provider-specific remote hash-verification check, and fails it for a deliberately mismatched blob;
  5. reports correct `Exists`/`Delete`/`List` behaviour and sane `CheckHealth`/`GetUsedBytes`/`GetQuotaBytes` values.
- These tests **self-skip** when their provider credentials are absent from the environment, so they run (and pass as "skipped") in CI without secrets, never gate a merge, and never expose secrets to fork-PR runners.
- Any defect a live test reveals is fixed in the **same section's PR**, and a regression test is added to the recorded-SDK-fake suite so CI guards it from then on (cross-cutting decision 6).
- A short README under the live-test folder documents the one-time setup: which environment variables to set, how the one-time browser consent works, and where the local token cache lives.
- `CLAUDE.md`'s Testing convention documents the `LiveProvider` category, its self-skip behaviour, and that it never gates CI.

---

## Cross-cutting decisions

These span all three sections. Each section references back rather than restating.

**1. Self-skipping xUnit tests — not a console app, never CI-gated.**
The harness is xUnit tests, not a standalone executable, so it reuses the existing test project, assertion helpers, and runner, and fits the repo layout with no exception. xUnit in this repo is **v2 (`xunit` 2.9.3)**, which has no native `Assert.Skip`. The harness therefore ships a custom `[LiveProviderFact]` attribute — a subclass of `FactAttribute` whose constructor checks for the provider's credentials in the environment and sets `base.Skip = "<reason>"` when they are absent. (The `Xunit.SkippableFact` package is the alternative if richer mid-test skip semantics are ever needed; the custom attribute is preferred here to add no new dependency.) A sibling **`[LiveProviderTheory]`** (subclass of `TheoryAttribute` with identical credential-presence skip logic) is provided so parameterized live tests — multiple blob sizes, resume offsets, good/mismatch hash cases — are not blocked later. Because xUnit enumerates `[Theory]` data at **discovery** time even for skipped theories, live-theory data sources must be static and cheap (`[InlineData]` constants or static `[MemberData]`) — never network- or credential-dependent. Every live test also carries `[Trait("Category", "LiveProvider")]` so the whole set is selectable with `--filter Category=LiveProvider` and excludable elsewhere.

**2. Location — `tests/FlashSkink.Tests/Providers/Live/`, inside the single test project.**
No new test project. `CLAUDE.md` records "single test project is the V1 decision; split only when justified (separate TFM, separate runner invocation)." A self-skipping category selected by `--filter` does not require a separate project, so the live tests are a subfolder of the existing one. This also means the live tests **build** in CI (harmless) and **skip** at run time there (no credentials present).

**3. Credentials are BYOC, sourced from the environment / a gitignored `.env` — never the repo.**
Each provider reads two variables: `FLASHSKINK_LIVE_<KEY>_CLIENTID` and `FLASHSKINK_LIVE_<KEY>_CLIENTSECRET`, where `<KEY>` is `GDRIVE`, `DROPBOX`, or `ONEDRIVE`. These come from the developer's shell environment or a `.gitignore`d `.env` at the repo root (the same mechanism `CLAUDE.md` § "Secrets hygiene" already mandates for developer test accounts). The harness resolves them once; a test whose variables are unset is skipped by its `[LiveProviderFact]`. No live credential is ever committed.

**4. OAuth consent happens once; the resulting token is cached locally in a gitignored file.**
A `*_Setup_*` test performs the real browser consent via the existing public `LoopbackOAuthCapture` + the provider's `IProviderSetup`, then persists the DEK-encrypted refresh-token envelope (the `byte[]` returned by `ExchangeCodeAsync`) together with the throwaway 32-byte DEK used to encrypt it, to a per-provider file under a gitignored cache directory. Subsequent runs — and the upload/download/resume/surface tests — load the cache and call `CreateProviderAsync` headlessly; they require no browser. If the cache is absent, those tests skip with a message directing the developer to run the setup test first. The throwaway DEK is a **test-only** key with no relationship to any real volume's key hierarchy; it exists only to round-trip the envelope within the cache. **The cache file is exactly as sensitive as a plaintext refresh token** (it holds the token and the key that decrypts it side by side) — it is gitignored, local-only, and the developer treats it with the same hygiene as `.env`.

**5. Every live test cleans up after itself; the startup sweep is age-bounded and run-scoped — safe under concurrent runners and shared accounts.**
Live tests run against the developer's real cloud account, which may be hit *at the same time* by another terminal window or another developer. Cleanup must therefore never delete another runner's in-flight data. Three rules:
- **Per-test quarantine.** Every test writes under `_livetest/{runId}/{testName}-{guid}/…`, where `runId = {unixSeconds}-{guid}` is generated once per process. No two tests — and no two concurrent runs — ever share a path, so overlapping executions cannot collide (this is also what makes parallel execution safe, decision 10).
- **Teardown deletes only the test's own prefix** in a `finally`, including on failure.
- **The startup sweep is age-bounded, never unconditional.** The frozen `IStorageProvider.ListAsync` returns only remote IDs — no timestamps — and adding metadata retrieval would violate Principle 23, so the run folder *encodes* its creation time in its name (the leading `{unixSeconds}` of `runId`). The sweep lists `_livetest/`, parses that timestamp from each top-level run folder, and deletes only folders **older than a threshold (default 2 h)**. Folders younger than the threshold, or whose name has no parseable timestamp, are left untouched — so a sweep can never delete a concurrent runner's active data. This age-bounded reap of stale leftovers replaces any "delete the whole prefix" behaviour.
The `_livetest/` root is deliberately distinct from the production roots (blobs, `_brain/`, `_witness/`) so no sweep can ever touch real backup data. The threshold is a harness constant, overridable by an env var for developers who want a tighter or looser reap window.

**6. The live test is the discovery mechanism; the recorded-fake test is the CI regression guard.**
This is the load-bearing rule that makes Phase 4.5 a phase. When a live test surfaces a real adapter bug, the fix lands in the same section's PR **and** is accompanied by a new or adjusted test in the existing recorded-SDK-fake suite (`tests/FlashSkink.Tests/Providers/…`, the `RecordingHttpMessageHandler` / canned-response tests from §4.3–4.5) that reproduces the bug deterministically and runs in CI. The live test proves the fix against reality once; the fake test keeps it fixed on every PR. A live-only fix with no CI-side guard is a Gate 2 rejection for that section.

**7. The provider contract stays frozen (Principle 23).**
Phase 4.5 *consumes* `IStorageProvider`, `IProviderSetup`, `UploadSession`, `ProviderCredentials`, `ProviderSetupKind`, and `ProviderHealth`; it does not modify them. If a live test reveals that an adapter needs a hook the surface does not provide, the sanctioned path is an **additive** capability interface (analogous to `ISupportsRemoteHashCheck`), never a signature change.

**8. Real-network flakiness is a local signal, not CI's problem.**
Because these tests never gate a merge, a transient provider `5xx`, throttle, or latency spike is a local annoyance, not a broken build. The harness does not add retry/backoff beyond whatever the production adapter already implements — a live failure is surfaced verbatim so the developer can judge "real defect" vs "transient." No live test is ever promoted to a required CI status check.

**9. No secret ever reaches test output or logs (Principle 26).**
The harness, and every live test, must never print or log a refresh token, client secret, authorization code, PKCE verifier, or decrypted token. Assertion messages reference object names, byte counts, and offsets only. This mirrors `LoopbackOAuthCapture`'s own Principle-26 posture.

**10. Parallel-execution posture is explicit; correctness rests on prefix quarantine, not on serialization.**
xUnit v2 runs distinct test classes (collections) in parallel by default. The three provider classes talk to three distinct vendor accounts, hold no shared mutable static state (each builds its own provider from its own cache entry), and write under fully-quarantined per-test prefixes (decision 5) — so cross-class parallel execution is correct, and is left enabled because it also keeps the suite fast. Within a class, xUnit runs methods sequentially; we do **not** enable in-class parallelization. A `[Collection("LiveProvider")]` marker is applied to every live class so the whole set can be serialized as an opt-in lever when a developer is rate-limited or deliberately points more than one class at a single shared account — serialization is a performance/throttling lever, never a correctness requirement. (Note: assigning all three classes to one collection disables their mutual parallelism; the default keeps them in separate collections. The plan ships them in separate collections and documents the single-collection switch in the README.)

---

## Section index

| Section | Title | Deliverables |
|---|---|---|
| §4.5.1 | Live-verification harness + Google Drive | `LiveProviderFactAttribute`, credential resolver, gitignored token cache, the shared building-block exercises (setup / upload-download / resume / hash-check / surface sweep), `Providers/Live/README.md`, `.gitignore` + `CLAUDE.md` testing-convention updates, and the Google Drive live test class. Any Google-Drive adapter fixes + their recorded-fake regression guards. |
| §4.5.2 | Dropbox live verification | `DropboxLiveTests` wiring the harness to `DropboxSetup`/`DropboxProvider` (content-hash verification, `upload_session/start`→`append_v2`→`finish`, 48 h session TTL). Any Dropbox adapter fixes + recorded-fake regression guards. |
| §4.5.3 | OneDrive live verification | `OneDriveLiveTests` wiring the harness to `OneDriveSetup`/`OneDriveProvider` (quickXorHash/SHA1 verification, `createUploadSession` + PUT-range, session expiry). Any OneDrive adapter fixes + recorded-fake regression guards. |

Full implementation detail for each section lives in `.claude/plans/pr-4.5.X.md`, written at Gate 1 of the corresponding session.

---

## Section notes

### §4.5.1 — Live-verification harness + Google Drive

**Blueprint sections to read:** §10.1–10.4 (the frozen provider/setup contract), §15.1–15.3 (resumable upload lifecycle — begin/range/finalise and session persistence), §15.7 (per-provider verification table — Google Drive's `md5Checksum`), §24.3 (BYOC setup flow), §24.5 (local-loopback OAuth capture), §27.2 (Google Drive provider notes). Also re-read `CLAUDE.md` § "Testing" and § "Secrets hygiene".

**Scope summary:** New `tests/FlashSkink.Tests/Providers/Live/` folder containing the shared harness plus the first concrete provider test class (Google Drive). The harness is built *alongside* Google Drive — not as a standalone PR — so it is proven end-to-end against a real provider rather than in the abstract (the same "fold the shared piece in with its first consumer" rationale Phase 3.5 used for `VolumeState` in §3.5.1). One `.gitignore` edit, one `CLAUDE.md` edit, one README. Source changes to `src/` occur **only** if a Google Drive defect is found; the expected source delta is zero.

#### Files to create

**`tests/FlashSkink.Tests/Providers/Live/LiveProviderFactAttribute.cs`** (~50 lines)
`sealed class LiveProviderFactAttribute : FactAttribute`. Constructor takes the provider key (`"GDRIVE"`, etc.); checks `Environment.GetEnvironmentVariable("FLASHSKINK_LIVE_<KEY>_CLIENTID"/"_CLIENTSECRET")` (after `.env` load); if either is missing, sets `Skip = "Set FLASHSKINK_LIVE_<KEY>_CLIENTID/_CLIENTSECRET to run live <key> tests."`. Also applies `[Trait("Category","LiveProvider")]` (or tests apply the trait directly).

**`tests/FlashSkink.Tests/Providers/Live/LiveProviderTheoryAttribute.cs`** (~50 lines)
`sealed class LiveProviderTheoryAttribute : TheoryAttribute` — same constructor signature and credential-presence skip logic as `[LiveProviderFact]`, for the resume and hash-verification building blocks where parameterized inputs (resume offsets, blob sizes, good-vs-mismatch) read best as `[InlineData]` rows. Data must be static/cheap (decision 1 — discovery runs even when skipped).

**`tests/FlashSkink.Tests/Providers/Live/LiveProviderCredentials.cs`** (~60 lines)
Loads `.env` if present (minimal hand-rolled `KEY=VALUE` parser — no new package), resolves a `(ProviderCredentials Credentials, bool Present)` per provider key. Never logs the secret.

**`tests/FlashSkink.Tests/Providers/Live/LiveTokenCache.cs`** (~90 lines)
Per-provider load/save of `{ encryptedTokenBase64, dekBase64 }` to a gitignored cache directory (repo-root `.livetest-cache/<key>.json`). Provides `TryLoad(key, out cached)` and `Save(key, encryptedToken, dek)`. Documents in an XML comment that the file is credential-sensitive and gitignored.

**`tests/FlashSkink.Tests/Providers/Live/LiveProviderHarness.cs`** (~250 lines)
The reusable exercises, parameterised over an `IProviderSetup` + provider key:
- `RunSetupAsync` — if the token cache is empty: `LoopbackOAuthCapture.Prepare()` → `setup.GetAuthorizationUriAsync(...)` → `AwaitAuthorizationCodeAsync(...)` (real browser) → `setup.ExchangeCodeAsync(...)` → cache the envelope + throwaway DEK. Then `setup.CreateProviderAsync(...)` from the cache and assert `CheckHealthAsync` is `Healthy`.
- `BuildProviderFromCacheAsync` — headless `CreateProviderAsync` from the cached envelope; used by every non-setup test.
- `UploadDownloadRoundTripAsync` — generate >4 MiB random bytes, `BeginUploadAsync`→ multi-range `UploadRangeAsync` → `FinaliseUploadAsync`, then `DownloadAsync` and assert byte-identical; `DeleteAsync` in `finally`.
- `ResumeFromPartialAsync(int totalRanges, int resumeAfterRanges)` — `BeginUploadAsync`, upload the first `resumeAfterRanges` ranges, discard the `UploadSession` object, re-query `GetUploadedBytesAsync` against the same `SessionUri`, resume the remaining ranges, `FinaliseUploadAsync`, download-verify; `DeleteAsync` in `finally`. Parameterized so a `[LiveProviderTheory]` can cover several offsets (including a partial final range).
- `RemoteHashCheckAsync(bool corrupt)` — for `ISupportsRemoteHashCheck` providers; with `corrupt: false` the remote-hash comparison passes, with `corrupt: true` it fails. Parameterized for a `[LiveProviderTheory]`.
- `SurfaceSweepAsync` — `ExistsAsync` true after finalise / false after delete; `ListAsync(prefix)` returns the object; `DeleteAsync` idempotent; `GetUsedBytesAsync` ≥ 0; `GetQuotaBytesAsync` null-or-positive.
- `ReapStaleRunsAsync(TimeSpan threshold)` — age-bounded `_livetest/` cleanup at run start: lists run folders, parses the `{unixSeconds}` prefix from each, deletes only those older than `threshold` (decision 5). Never deletes unparseable or recent folders.
- Helpers for the per-test prefix (`_livetest/{runId}/{testName}-{guid}/`) and the process-wide `runId`.

**`tests/FlashSkink.Tests/Providers/Live/GoogleDriveLiveTests.cs`** (~150 lines)
Concrete class binding the harness to `GoogleDriveSetup` + the Drive provider, one `[LiveProviderFact("GDRIVE")]` per building block (test names below).

**`tests/FlashSkink.Tests/Providers/Live/README.md`** (~60 lines)
How to run: required env vars per provider, the one-time consent step, where the cache lives, `dotnet test --filter Category=LiveProvider`, and the "these never run in CI / never gate a merge" note.

#### Files to modify

**`.gitignore`** — add `.livetest-cache/` (and confirm `.env` is already ignored — it is). 
**`CLAUDE.md`** (§ "Testing") — add a short note: the `LiveProvider` category, that it self-skips when credentials are absent, that it is run locally via `--filter Category=LiveProvider`, and that it is never a required CI check. Ships in this PR per the `CLAUDE.md` update policy (causal-link rule).

#### Test spec (the building-block set, reused verbatim in §4.5.2/§4.5.3 with the provider prefix)

`tests/FlashSkink.Tests/Providers/Live/GoogleDriveLiveTests.cs` — class `GoogleDriveLiveTests`, `[Collection("LiveProvider")]`:
- `GoogleDrive_Setup_RealConsent_ProducesUsableProvider` — `[LiveProviderFact("GDRIVE")]`
- `GoogleDrive_UploadDownload_RoundTrips` — `[LiveProviderFact]`
- `GoogleDrive_Resume_FromPartialUpload_Completes` — `[LiveProviderTheory]` with `[InlineData]` over `(totalRanges, resumeAfterRanges)`, e.g. `(3,1)`, `(3,2)`, `(4,3)` (last includes a partial final range)
- `GoogleDrive_RemoteHashCheck_BehavesCorrectly` — `[LiveProviderTheory]` with `[InlineData(false)]` (passes) and `[InlineData(true)]` (fails)
- `GoogleDrive_ExistsDeleteList_BehaveCorrectly` — `[LiveProviderFact]`
- `GoogleDrive_HealthQuotaUsed_ReturnSaneValues` — `[LiveProviderFact]`

#### Line-of-code budget

| File | Lines |
|---|---|
| `Live/LiveProviderFactAttribute.cs` | ~50 |
| `Live/LiveProviderTheoryAttribute.cs` | ~50 |
| `Live/LiveProviderCredentials.cs` | ~60 |
| `Live/LiveTokenCache.cs` | ~90 |
| `Live/LiveProviderHarness.cs` | ~270 |
| `Live/GoogleDriveLiveTests.cs` | ~150 |
| `Live/README.md` | ~70 |
| `.gitignore` (delta) | ~2 |
| `CLAUDE.md` (delta) | ~8 |
| **Total** | **~750** (all test/docs; src delta ~0 unless a Drive bug is fixed) |

#### Principles touched
- **P23** (provider contract frozen) — the harness consumes the contract; no signature changes. Any fix is behind the surface or an additive capability interface.
- **P26** (no secrets in logs/output) — the harness and tests never print tokens, secrets, codes, or PKCE verifiers.
- **P12** (OS-agnostic) — the harness is platform-neutral; the only OS-branching (browser launch) is inside the existing `SystemBrowserLauncher`, not the harness.
- **P1, P13, P14, P15, P16** — apply only to any `src/` fix a live test triggers; the test code itself is exempt, but a fix must conform.

#### Non-goals for §4.5.1
- Do NOT build a console-app probe; do NOT add a new test project.
- Do NOT wire any live test into CI as a required check, and do NOT add a workflow that supplies real credentials.
- Do NOT add Dropbox or OneDrive live tests (those are §4.5.2 / §4.5.3).
- Do NOT modify the provider contract (Principle 23).
- Do NOT commit any credential, token, or the `.livetest-cache/` directory.
- Do NOT store the client secret in the token cache — read it from the environment each run; cache only the encrypted token envelope + the throwaway DEK.

---

### §4.5.2 — Dropbox live verification

**Blueprint sections to read:** §15.4 (Dropbox `upload_session` lifecycle mapping), §15.7 (Dropbox content-hash verification), §27.3 (Dropbox provider notes), plus §4.5.1's cross-cutting decisions.

**Scope summary:** New `DropboxLiveTests` reusing `LiveProviderHarness`, bound to `DropboxSetup`/`DropboxProvider`. Credentials: `FLASHSKINK_LIVE_DROPBOX_CLIENTID/_CLIENTSECRET`. Verification building block asserts Dropbox's content-hash path; the resume building block exercises `upload_session/start`→`append_v2`→`finish` and confirms `GetUploadedBytesAsync` reports the server-confirmed offset; note Dropbox's 48 h session TTL as a known boundary the resume test stays well within. Any Dropbox adapter fix lands here with a recorded-fake regression guard (decision 6). Harness changes, if any, must be minimal and backward-compatible for §4.5.3.

**Test spec:** `DropboxLiveTests` with the six `Dropbox_*` building-block tests (same shape as §4.5.1).

**LOC budget:** `Live/DropboxLiveTests.cs` ~150; harness delta ~0–30; src delta ~0 unless a Dropbox bug is fixed.

**Principles touched:** P23, P26 (as §4.5.1); P1/P13/P14/P15/P16 for any fix.

**Non-goals:** Do NOT refactor the harness beyond the minimal extension Dropbox needs; do NOT modify the contract; do NOT add OneDrive tests; do NOT commit credentials.

---

### §4.5.3 — OneDrive live verification

**Blueprint sections to read:** §15.5 (OneDrive `createUploadSession` + PUT-range lifecycle), §15.7 (OneDrive quickXorHash / SHA1 verification), §27.4 (OneDrive provider notes), plus §4.5.1's cross-cutting decisions.

**Scope summary:** New `OneDriveLiveTests` reusing `LiveProviderHarness`, bound to `OneDriveSetup`/`OneDriveProvider`. Credentials: `FLASHSKINK_LIVE_ONEDRIVE_CLIENTID/_CLIENTSECRET`. Verification building block asserts the quickXorHash/SHA1 path from Graph's metadata; the resume building block exercises `createUploadSession` + ranged `PUT` and confirms resume from the `nextExpectedRanges`/`GetUploadedBytesAsync` offset; OneDrive session expiry is noted as a known boundary. Any OneDrive adapter fix lands here with a recorded-fake regression guard (decision 6).

**Test spec:** `OneDriveLiveTests` with the six `OneDrive_*` building-block tests (same shape as §4.5.1).

**LOC budget:** `Live/OneDriveLiveTests.cs` ~150; harness delta ~0–30; src delta ~0 unless a OneDrive bug is fixed.

**Principles touched:** P23, P26 (as §4.5.1); P1/P13/P14/P15/P16 for any fix.

**Non-goals:** Do NOT modify the harness beyond OneDrive's minimal needs; do NOT modify the contract; do NOT commit credentials.

---

## Acceptance criteria for the full phase

- [ ] `dotnet build` clean (`--warnaserror`) on all supported RIDs after each PR.
- [ ] With **no** live env vars set, `dotnet test` runs green and every `LiveProvider`-category test reports **Skipped** (the CI-equivalent run — confirms the self-skip path).
- [ ] With credentials set, each of the three providers passes all six building-block tests against the real API (verified locally/manually by the developer).
- [ ] No new test project is added; all live tests live under `tests/FlashSkink.Tests/Providers/Live/`.
- [ ] No change to `IStorageProvider`, `IProviderSetup`, `UploadSession`, `ProviderCredentials`, `ProviderSetupKind`, or `ProviderHealth` (Principle 23). Any new hook is an additive capability interface.
- [ ] Every adapter defect found is fixed **and** guarded by a recorded-fake regression test that runs in CI (cross-cutting decision 6).
- [ ] No secret (refresh token, client secret, authorization code, PKCE verifier) appears in any test output or log (Principle 26).
- [ ] `.env` and `.livetest-cache/` are gitignored; no credential, token, or cache file is committed.
- [ ] `CLAUDE.md` § "Testing" documents the `LiveProvider` category (self-skipping, local-only, never a required CI check).
- [ ] Cleanup is concurrency-safe: every object is written under `_livetest/{runId}/…`; teardown deletes only the test's own prefix; the startup reap deletes **only** run folders older than the threshold (verified by a fast, non-live unit test of the timestamp-parse + age-filter logic, which itself runs in CI).
- [ ] `[LiveProviderTheory]` exists and is used for the resume and hash-verification building blocks; theory data is static (`[InlineData]`), so discovery never requires credentials or network.

## Non-goals for the phase as a whole

- Do NOT build a standalone console-app probe (superseded by the self-skipping test design).
- Do NOT run live tests in CI with real credentials, make any live test a required status check, or expose secrets to fork-PR runners.
- Do NOT implement multi-account or multi-folder configurations per provider (post-V1, per Phase 4's non-goals).
- Do NOT implement automated provider-console setup (paid layer, Blueprint §24.1).
- Do NOT add new provider types.
- Do NOT fold these tests into the `crash-consistency` or `nightly` suites, and do NOT add a `workflow_dispatch` live-credential job in this phase (that remains a separately-decided option, not part of Phase 4.5).
- Do NOT modify the frozen provider contract (Principle 23).
