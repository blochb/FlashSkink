# PR Refactor-A — Volume identity and version metadata

**Branch:** `pr/refactor-a-identity-and-versioning`
**Blueprint sections (impact map):** §3 (new principle row), §16.2 (Settings comment block — line 1789 lists the initial rows, needs `VolumeID` + version-key rename), §16.3 (line 1800 currently reads `Settings["AppVersion"]`; needs updating to `AppVersionCreatedWith`), §28 Tech Stack (add MinVer), new §31 "App identity and versioning" (or fold into a renumbered §29.4 standard conventions — see note)
**Dev plan section:** none — out-of-band refactor PR landing before Phase 4. Parent plan: `.claude/plans/agreed-clone-deferred-now-bright-popcorn.md` ("Recommended V1 scope, items 1/3/4/5/7/16").
**Base:** rebased onto `main` after PR #68 (recovery phrase fix) merged. `CreateAsync` now returns `Result<VolumeCreationReceipt>`, `SeedInitialSettingsAsync` is 2-arg and no longer writes `RecoveryPhrase`. Plan adapted accordingly.

---

## Scope

Add (a) a stable per-volume identity GUID written to the brain at create time and backfilled on open, (b) MinVer-driven SemVer-from-git-tags version metadata across all assemblies, (c) brain-level stamping of "version created with" and "version last opened" so audit logs and bug reports carry an unambiguous version reference, (d) confirmation tests that the existing schema-compat check (already coded in `MigrationRunner`) correctly refuses to open brains created by a future build, and (e) a new blueprint section documenting all of the above plus the explicit *no auto-update / no phone-home* rule that cross-refs Principle 32.

No new ErrorCodes. No new public types on the `Core` surface (volume identity exposure to callers is deferred to the new Phase 3.5 witness work — internal-only here). The existing `Settings` KV table absorbs the new rows with no schema migration. `SeedInitialSettingsAsync` and `OpenAsync` (both in `FlashSkinkVolume`) are the two integration points in production code; the rest is config and docs.

This PR's *only* runtime-visible behaviour change is: opening any volume now writes three Settings rows. Everything else is build-time metadata, documentation, or tests.

---

## Files to create

- `.claude/plans/pr-refactor-a-identity-and-versioning.md` — this plan.
- `tests/FlashSkink.Tests/Orchestration/VolumeIdentityTests.cs` — ~250 lines. New test class covering volume GUID generation, backfill, and stability.
- `tests/FlashSkink.Tests/Orchestration/AppVersionStampTests.cs` — ~200 lines. New test class covering version stamping on create and open.
- `tests/FlashSkink.Tests/Metadata/MigrationRunnerCompatTests.cs` — ~120 lines. New test class confirming the existing future-version check returns `VolumeIncompatibleVersion` (item 5 verification). If a `MigrationRunnerTests.cs` already exists, fold these in there instead — verify in implementation.

## Files to modify

- `Directory.Build.props` — add `<Product>`, `<Company>`, `<Description>`, `<Copyright>`, `<Deterministic>true</Deterministic>`, `<ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>`, `<EmbedUntrackedSources>true</EmbedUntrackedSources>`, `<PublishRepositoryUrl>true</PublishRepositoryUrl>`. Add a `MinVer` `PackageReference` so every project that inherits gets stamped (single point of wiring).
- `Directory.Packages.props` — add `<PackageVersion Include="MinVer" Version="6.0.0" />` (or current stable; verify on NuGet at implementation time per memory `feedback_verify_external_facts.md`).
- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — three changes (signature of `CreateAsync` is unchanged from current `Result<VolumeCreationReceipt>` post-#68; `OpenAsync` still returns `Result<FlashSkinkVolume>`):
  1. `SeedInitialSettingsAsync` (current sig `(SqliteConnection, CancellationToken)`) — refactor body to read `AssemblyInformationalVersionAttribute` instead of `Assembly.GetName().Version`. Rename the `AppVersion` upsert key to `AppVersionCreatedWith`. Add `VolumeID` upsert (random `Guid.NewGuid().ToString("D")`). Add `AppVersionLastOpened` and `AppVersionLastOpenedUtc` upserts (initially equal to the create-time version and `VolumeCreatedUtc`). The existing comment block at lines 1012–1014 about `RecoveryPhrase` not being persisted stays unchanged — that's PR #68's contract.
  2. `OpenAsync` — after `lifecycle.OpenAsync` succeeds and migrations have run, before `BuildVolumeFromSessionAsync`, call a new private helper `BackfillAndStampOnOpenAsync(ownedSession.BrainConnection!, ct)` that idempotently: backfills `VolumeID` if missing; backfills `AppVersionCreatedWith` from legacy `AppVersion` if present (and deletes the legacy key); writes/updates `AppVersionLastOpened` and `AppVersionLastOpenedUtc`. All four upserts and the optional legacy delete run inside a single short `SqliteTransaction`. On failure, dispose `ownedSession` and return the failure (parallel to the existing error-path pattern).
  3. Add a private static `GetAppInformationalVersion()` helper that reads `typeof(FlashSkinkVolume).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-unknown"`. Used by both `SeedInitialSettingsAsync` and `BackfillAndStampOnOpenAsync`.
- `BLUEPRINT.md` — four edits:
  1. **§16.2 Schema** — update the trailing comment block (lines 1787–1791 currently) to list the new initial Settings keys and the open-time-stamped keys. Drop `AppVersion` mention; replace with `AppVersionCreatedWith`. Add `VolumeID`, `AppVersionLastOpened`, `AppVersionLastOpenedUtc`. Add an inline note that `VolumeID` is a random GUID never derived from the recovery phrase. **Also fix the `AuditIntervalHours` drift at line 1789**: change `"AuditIntervalHours" = "24"` to `"AuditIntervalHours" = "168"` to match the actual seeded value. Picks weekly as canonical (decided out-of-band — see PR description for rationale).
  2. **§16.3 Schema Migration** — line 1800 currently reads `2. Read Settings["AppVersion"] and SchemaVersions max(Version).` Update to `2. Read Settings["AppVersionCreatedWith"] and SchemaVersions max(Version).` The reference is informational (used for migration logging context, not control flow), so the change is one token.
  3. **§28 Tech Stack** — add a `Versioning` row: package `MinVer`, project "All (via Directory.Build.props)". Add a sentence under the table noting that builds are deterministic with sources embedded (`<Deterministic>` + `<EmbedUntrackedSources>`).
  4. **New §31 "App identity and versioning"** — placed before the existing Post-V1 Direction section (which is renumbered §31 → §32 so that App Identity lands at §31 and Post-V1 Direction remains the last section). Covers: volume identity rule (random at init, never derived from recovery phrase, lives in `Settings["VolumeID"]`); version metadata sourcing (MinVer reads git tags; `AssemblyInformationalVersionAttribute` is the runtime canonical source); brain version stamping (three Settings keys, semantics); brain schema forward-compat (cross-ref §16.3, note that the check is implemented in `MigrationRunner.RunAsync` and returns `ErrorCode.VolumeIncompatibleVersion`); explicit *no auto-update / no phone-home* rule (cross-ref Principle 32 in CLAUDE.md and the corresponding "Zero trust in the application" row of §3).
- `CLAUDE.md` — two additions in the Principles section:
  - **Principle 33 (new)**: "Volume identity is a random GUID, never derived from recovery-phrase material. The skink stores it in `Settings["VolumeID"]` written at create time and backfilled at open time. Volume identity is independent of cryptographic key material so that two skinks initialised from the same recovery phrase are distinct *volumes* even though they share encryption keys. (Blueprint §31.)"
  - **Principle 34 (new)**: "Application version metadata is sourced from `AssemblyInformationalVersionAttribute`, populated by MinVer from git tags. Builds use `<Deterministic>true</Deterministic>` and `<EmbedUntrackedSources>true</EmbedUntrackedSources>` so binaries are reproducible from a tagged commit. The brain stamps the application version into `Settings["AppVersionCreatedWith"]` at create and `Settings["AppVersionLastOpened"]` / `Settings["AppVersionLastOpenedUtc"]` on every open. (Blueprint §31.)"
- `README.md` — no functional changes. Optional: add a single line under "For contributors" noting "Version: SemVer from git tags via MinVer." Defer unless reviewer asks.

---

## Dependencies

- **NuGet (new):** `MinVer` 6.0.0 (verify on nuget.org at implementation time; pin to current stable). Added as a `PackageVersion` in `Directory.Packages.props` and a `PackageReference` in `Directory.Build.props` so every project inherits.
- **Project references:** none added.
- **Git tag (manual, after merge):** the user tags `v0.1.0` (or whatever pre-V1 SemVer they choose) at the merge commit to seed MinVer. Until tagged, MinVer emits `0.0.0-alpha.0.<height>+<sha>` which is correct behaviour, not a defect.

---

## Public API surface

No new public types. No new public members on existing types.

The new `Settings` rows are an internal contract (the schema is internal-facing, not part of the published API). Document them in code via XML doc-comment on `SeedInitialSettingsAsync` and `BackfillAndStampOnOpenAsync` and in BLUEPRINT.md §16.2.

---

## Internal types

No new types. New private helpers on `FlashSkinkVolume`:

- `private static string GetAppInformationalVersion()` — reads `AssemblyInformationalVersionAttribute` from `typeof(FlashSkinkVolume).Assembly`; returns the attribute's `InformationalVersion` value, or `"0.0.0-unknown"` if the attribute is absent (defensive — shouldn't happen once MinVer is wired). Never throws.
- `private static async Task<Result> BackfillAndStampOnOpenAsync(SqliteConnection connection, CancellationToken ct)` — see method-body contract below.

---

## Method-body contracts

### `SeedInitialSettingsAsync` (body refactor, signature unchanged from post-#68 state)

**Current signature (unchanged):** `private static async Task<Result> SeedInitialSettingsAsync(SqliteConnection connection, CancellationToken ct)`.

**Preconditions:** `connection` is open; brain migrations have completed.

**Postconditions on success:** `Settings` contains:
- `GracePeriodDays = "30"`
- `AuditIntervalHours = "168"` *(keep existing value; do not "fix" to match blueprint's stale "24" in this PR — that drift is out of scope, flagged separately)*
- `VolumeCreatedUtc = <now ISO-8601 O-format>`
- `VolumeID = <new Guid.NewGuid().ToString("D")>` *(new)*
- `AppVersionCreatedWith = <GetAppInformationalVersion()>` *(replaces the existing `AppVersion` key)*
- `AppVersionLastOpened = <same value as AppVersionCreatedWith>` *(new)*
- `AppVersionLastOpenedUtc = <same value as VolumeCreatedUtc>` *(new)*

The `RecoveryPhrase` row was removed by PR #68 and is not reintroduced. The comment block citing §18.8 / Decision A16 stays.

**ErrorCodes:** `Cancelled` on cancellation; `DatabaseWriteFailed` on `SqliteException`; `Unknown` on anything else. Same shape as today.

**Cancellation:** observed before the first INSERT and propagates via the existing pattern.

### `BackfillAndStampOnOpenAsync` (new)

**Preconditions:** `connection` is open; migrations have completed.

**Postconditions on success:**
- `Settings["VolumeID"]` exists. If it was missing on entry (legacy brain), a fresh GUID is written.
- `Settings["AppVersionCreatedWith"]` exists. If it was missing on entry but legacy `Settings["AppVersion"]` was present, the legacy value is copied to `AppVersionCreatedWith` and `AppVersion` is deleted. If both were missing (corrupt or hand-edited), `AppVersionCreatedWith` is written as `"unknown-legacy"` (a sentinel different from `"0.0.0-unknown"` so the two cases are distinguishable in audit logs).
- `Settings["AppVersionLastOpened"]` = current `GetAppInformationalVersion()` (always overwritten).
- `Settings["AppVersionLastOpenedUtc"]` = current `DateTime.UtcNow.ToString("O")` (always overwritten).

**Transaction:** all four upserts and the optional legacy-`AppVersion` delete run inside a single `SqliteTransaction`. On any exception, the transaction is rolled back via `using var tx` disposal and a `Result.Fail` is returned.

**ErrorCodes:** `Cancelled` on `OperationCanceledException`; `DatabaseWriteFailed` on `SqliteException`; `Unknown` on anything else. First-catch ordering per Principle 14/15.

**Cancellation:** `ct.ThrowIfCancellationRequested()` before the BEGIN; cooperative cancellation between upserts; no compensation needed (transaction is unwound on rollback).

**Logging:** one `LogDebug` line per stamp (volume identity backfilled, legacy `AppVersion` migrated, last-opened updated). No secret-bearing values are logged.

### Call-site change in `OpenAsync`

After the existing `lifecycle.OpenAsync` call returns success, before the `BuildVolumeFromSessionAsync` call, call `BackfillAndStampOnOpenAsync(ownedSession.BrainConnection!, ct)`. If it returns `Result.Fail`, dispose `ownedSession` and return the failure. The existing `finally` block's null check still handles the dispose path correctly because we'd null `session` only on the successful path through `BuildVolumeFromSessionAsync`.

---

## Integration points

- `FlashSkinkVolume.CreateAsync` (PR 2.7) — already calls `SeedInitialSettingsAsync`. Signature unchanged; behaviour change is purely additive rows.
- `FlashSkinkVolume.OpenAsync` (PR 2.7) — gains a new step before `BuildVolumeFromSessionAsync`. Signature unchanged.
- `MigrationRunner.RunAsync` (PR 1.5) — *no change.* The forward-compat check it already implements (returning `VolumeIncompatibleVersion` when brain schema > app schema) is what the new tests verify.
- `Settings` table — unchanged DDL; new rows only.
- `BrainConnectionFactory` (PR 1.5) — no change.
- `VolumeLifecycle` (PR 2.7) — no change.

---

## Principles touched

- **Principle 1** (Core never throws across public API) — both new helpers return `Result`. No throwing changes.
- **Principle 14** (`OperationCanceledException` first) — both new helpers preserve the first-catch ordering.
- **Principle 15** (no bare `catch (Exception)` alone) — both new helpers follow the OCE → specific → general pattern.
- **Principle 26** (logging never contains secrets) — `VolumeID` is not a secret and is safe to log; `AppVersion*` values are not secrets. Recovery phrase handling is unchanged by this PR.
- **Principle 27** (Core logs internally; callers log Result) — new `LogDebug` calls follow the pattern.
- **Principle 28** (Core depends only on MEL abstractions) — MinVer is a build-time-only package; no runtime reference is added to Core.
- **Principle 32** (no telemetry, no update checks) — cross-referenced in the new blueprint §31; no implementation change.
- **Principles 33 and 34** (new, added by this PR) — see CLAUDE.md edits above.

---

## Test spec

### `tests/FlashSkink.Tests/Orchestration/VolumeIdentityTests.cs`

All tests use the existing `FlashSkinkVolume.CreateAsync` (returns `Result<VolumeCreationReceipt>` post-#68) and `OpenAsync` (returns `Result<FlashSkinkVolume>`) against a temp directory. Reuse the test scaffolding pattern from existing tests in `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeTests.cs` and `tests/FlashSkink.Tests/Crypto/RecoveryPhraseTests.cs` — those were updated by PR #68 and show the current post-#68 pattern (`var receipt = (await CreateAsync(...)).Value!; receipt.RecoveryPhrase.Dispose(); var volume = receipt.Volume;`). Each test must dispose `receipt.RecoveryPhrase` (which zeroes the word buffers) and `await using` the volume.

- `VolumeID_IsWrittenOnCreate_AsValidGuid` — create a volume; open the brain directly via a fresh `SqliteConnection` (with the DEK); read `Settings["VolumeID"]`; assert it parses as `Guid`.
- `VolumeID_IsStable_AcrossClose_AndReopen` — create, dispose, reopen, read `VolumeID`; assert it equals the value from the first open.
- `VolumeID_IsUnique_AcrossSeparateVolumes` — create two distinct volumes in two temp dirs; assert their `VolumeID` values differ. (Sanity check on `Guid.NewGuid()` distribution; cheap and catches accidental constant.)
- `VolumeID_IsBackfilledOnOpen_WhenMissingFromLegacyBrain` — create a volume, manually `DELETE FROM Settings WHERE Key='VolumeID'`, dispose, reopen; assert `Settings["VolumeID"]` is present after the open and is a valid GUID. (Simulates a brain created by a pre-this-PR build.)
- `VolumeID_OnceBackfilled_DoesNotChange_OnSubsequentOpen` — extends the previous test: after the backfill open, dispose, reopen again; assert the GUID is identical to the backfilled one. (Backfill must be idempotent.)

### `tests/FlashSkink.Tests/Orchestration/AppVersionStampTests.cs`

- `AppVersionCreatedWith_IsWrittenOnCreate_FromInformationalVersion` — create; read `Settings["AppVersionCreatedWith"]`; assert non-empty and matches the test-host's `AssemblyInformationalVersionAttribute` for the `FlashSkink.Core` assembly. (Test runs against the real attribute — MinVer-stamped in CI, manually-written in local dev.)
- `AppVersionCreatedWith_IsStable_AcrossReopens` — create with current build; dispose; reopen; assert `AppVersionCreatedWith` is unchanged. (Even if the open-time stamp differs — see next test — the created-with value must be immutable.)
- `AppVersionLastOpened_IsUpdatedOnEachOpen` — create; capture `AppVersionLastOpened` value; dispose; reopen; capture again; assert *equal* in the normal case (same build), then construct a degraded scenario by stubbing `GetAppInformationalVersion()` via reflection or by editing the row directly between opens, and assert that the next open writes the current value. (Adapt to whichever scaffolding is cleanest; the load-bearing assertion is "the open-time row reflects the opener, not the creator.")
- `AppVersionLastOpenedUtc_IsMonotonicAcrossOpens` — create; record `LastOpenedUtc`; dispose; await a small clock advance; reopen; assert new `LastOpenedUtc` > prior `LastOpenedUtc`. Uses `IClock` if injectable; otherwise a `Thread.Sleep(10)` between opens is acceptable.
- `LegacyAppVersionKey_IsMigratedTo_AppVersionCreatedWith_OnOpen` — create a volume; manually `DELETE FROM Settings WHERE Key='AppVersionCreatedWith'`; manually `INSERT INTO Settings (Key, Value) VALUES ('AppVersion', '0.0.7-legacy')`; dispose; reopen; assert `Settings["AppVersionCreatedWith"]` = `"0.0.7-legacy"` and `Settings["AppVersion"]` is absent (legacy key cleaned up).
- `BothVersionKeysMissing_BackfillsCreatedWith_AsSentinel` — create; delete both `AppVersion` and `AppVersionCreatedWith`; reopen; assert `Settings["AppVersionCreatedWith"]` = `"unknown-legacy"`.

### `tests/FlashSkink.Tests/Metadata/MigrationRunnerCompatTests.cs` (or fold into existing `MigrationRunnerTests.cs` if present)

- `RunAsync_ReturnsVolumeIncompatibleVersion_WhenBrainSchemaIsAhead` — open a fresh brain, insert a `SchemaVersions` row with `Version = MigrationRunner.CurrentSchemaVersion + 1`, call `RunAsync`, assert `Result.Success == false` and `Result.Error!.Code == ErrorCode.VolumeIncompatibleVersion`.
- `RunAsync_ReturnsOk_WhenBrainSchemaEqualsCurrent` — open a fresh brain, run migrations (advances to `CurrentSchemaVersion`), call `RunAsync` again, assert `Result.Success`.
- `RunAsync_ReturnsOk_WhenBrainSchemaIsBehind` — open a fresh brain, ensure `SchemaVersions` is empty (version 0), call `RunAsync`, assert success and that all migrations are applied.

If `MigrationRunnerTests.cs` already covers cases 2 and 3, only add case 1 (the actual gap). Verify at implementation time.

---

## Acceptance criteria

- [ ] `dotnet build` clean with `--warnaserror` on every project on both `ubuntu-latest` and `windows-latest`.
- [ ] `dotnet test` green on all existing tests plus the new test files.
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] Built `FlashSkink.Core.dll` and `FlashSkink.CLI.dll` (inspect via `dotnet-dll-properties` or PowerShell `(Get-Item …).VersionInfo`) show populated `Product`, `Company`, `FileVersion`, `ProductVersion`. `ProductVersion` matches the form `<MAJOR>.<MINOR>.<PATCH>[-<prerelease>]+<commit-sha>` once a git tag is in place; before the first tag it matches `0.0.0-alpha.0.<height>+<sha>`.
- [ ] Manually creating and opening a volume in a throwaway harness shows the three new Settings rows are present and the legacy `AppVersion` key is absent.
- [ ] `BLUEPRINT.md` builds with no broken cross-refs (visual inspection of the new §31 and the §16.2 / §28 edits).
- [ ] `CLAUDE.md` line count is still under the 900-line "something is wrong" threshold in §"`CLAUDE.md` update policy".

---

## Line-of-code budget

- `Directory.Build.props` — +10 lines.
- `Directory.Packages.props` — +1 line.
- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — +60 lines net (new helper, refactored seed, new open-time call site).
- `BLUEPRINT.md` — +80 lines net (new §31 + §16.2 / §28 edits).
- `CLAUDE.md` — +20 lines net (two new principles).
- `tests/FlashSkink.Tests/Orchestration/VolumeIdentityTests.cs` — ~250 lines.
- `tests/FlashSkink.Tests/Orchestration/AppVersionStampTests.cs` — ~200 lines.
- `tests/FlashSkink.Tests/Metadata/MigrationRunnerCompatTests.cs` — ~120 lines (or smaller if folding into existing).

**Total:** ~170 lines production + docs, ~570 lines tests.

---

## Non-goals

- **No CLI work.** No `flashskink --version` command. No `Program.cs` changes. CLI bootstrap waits for the first PR of Phase 4.
- **No `flashskink update` CLI command.** Per the meta-plan, app-update tooling is docs-only and lands in Phase 4 alongside CLI bootstrap.
- **No public `VolumeId` property on `FlashSkinkVolume`.** The GUID lives in `Settings` only. Public exposure waits for the new Phase 3.5 witness work, which is the actual consumer.
- *(Resolved by PR #68, no longer in scope for anything here)* `RecoveryPhrase` is no longer persisted in `Settings` — returned exactly once via `VolumeCreationReceipt.RecoveryPhrase`.
- *(In scope after all)* The `AuditIntervalHours` drift is now bundled: blueprint §16.2 line 1789 changes from `"24"` to `"168"` as a one-token fix in this PR. PR description calls out the decision (weekly canonical, picked for portable backup device — daily audits would be wasteful on a USB that isn't always plugged in).
- **No witness, no split-brain, no fenced state, no resolution flow.** Those are all the new Phase 3.5 dev-plan file, landing after Refactor PRs A and B.
- **No `flashskink clone` command.** Deferred to V1.x/V2 per agreed meta-plan.
- **No auto-update infrastructure.** Forbidden by Principle 32; documented as never in the new blueprint §31.
- **No changes to `MigrationRunner.RunAsync` body.** Only tests against its existing behaviour.
- **No new ErrorCodes.** `VolumeIncompatibleVersion` and `SingleInstanceLockHeld` already exist.

---

## Open question (small)

The new blueprint section can land in either of two places:

1. **A new top-level §31 "App identity and versioning,"** placed before the existing "Post-V1 Direction" (which becomes §32). Symmetric with how §19 "USB Resilience" hosts the single-instance subsection.
2. **Folded into §29.4 "Standard conventions"** as a new subsection.

Choice (1) is more visible and gives the rule its own home. Choice (2) keeps the conventions material in one place. Resolved during implementation: chose (1), with App Identity at §31 and Post-V1 Direction renumbered to §32 (per reviewer preference that Post-V1 Direction be the final section).

---

*End of plan.*
