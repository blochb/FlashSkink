# Refactor PR B — Single-instance lock

**Branch:** pr/refactor-b-single-instance-lock
**Blueprint sections:** §19.5 (Single-Instance Lock), §26.2 (Skink Directory Layout)
**Meta-plan:** `.claude/plans/agreed-clone-deferred-now-bright-popcorn.md`,
section "Refactor PR shape (confirmed)", item B.
**Items addressed (from meta-plan ranked table):** 2 (single-instance lock), 8 (blueprint §).

## Deviations from the meta-plan

Four divergences from the "Refactor PR B" specification in
`agreed-clone-deferred-now-bright-popcorn.md`, all explicitly confirmed with
the user before this plan was finalized:

1. **File path:** meta-plan says `.flashskink/.lock`; this plan uses
   `.flashskink/instance.lock`. Blueprint §19.5 and §26.2 already specify
   `instance.lock`; per CLAUDE.md the blueprint wins. *Deviation forced by
   existing authoritative spec.*
2. **Class name:** meta-plan says `SkinkLock`; this plan uses `InstanceLock`
   (with `InstanceLockManifest` for the JSON shape). Rationale: the file is
   `instance.lock`, "skink" is appliance vocabulary for the whole volume,
   and the lock represents *one running instance*, not the skink itself.
   *User-confirmed choice.*
3. **ErrorCode:** meta-plan adds `SkinkInUse`; this plan reuses
   `SingleInstanceLockHeld` (already present at `ErrorCode.cs` line 160 from
   earlier scaffolding). Adding `SkinkInUse` would duplicate intent.
   *Deviation forced by existing code.*
4. **Test layout:** meta-plan lists four tests in a single suite under
   `tests/FlashSkink.Tests/Storage/`; this plan splits into a unit suite
   (`Storage/InstanceLockTests.cs` against the primitive) and an integration
   suite (`Orchestration/SingleInstanceTests.cs` through `FlashSkinkVolume`).
   Test names are correspondingly renamed. The four meta-plan-named
   scenarios are all covered (acquire / release / stale recovery / manifest
   contents), distributed across the two suites. *User-confirmed choice.*

The other meta-plan requirements — `FileStream` with `FileShare.None`,
contents `{pid, hostname, started_at, app_version}`, acquired on open /
released on dispose, `--force` recovery seam without CLI wiring, blueprint §
on single-instance — are unchanged.

## Scope

Acquire an OS-level exclusive file lock on `[skinkRoot]/.flashskink/instance.lock`
for the entire lifetime of an open `FlashSkinkVolume`. A second concurrent
`CreateAsync` / `OpenAsync` on the same `skinkRoot` — same host or another host
sharing the same USB volume over a network share — returns
`ErrorCode.SingleInstanceLockHeld` with metadata identifying the holder (`Pid`,
`Host`, `StartedAtUtc`, `AppVersion`). On clean shutdown the lock file is
deleted; on crash the OS releases the file lock automatically but leaves the
file on disk — that stale file is recoverable via an opt-in `ForceUnlock`
flag on `VolumeCreationOptions` (CLI exposure as `--force` deferred to Phase 4).

The lock primitive lives next to the other low-level storage primitives in
`FlashSkink.Core.Storage`. It is `internal` — only `FlashSkinkVolume` consumes
it; no public API surface change.

This PR also expands blueprint §19.5 from a single paragraph to a full
sub-section (file contents, force-recovery semantics, dispose ordering) and
adds a new CLAUDE.md principle.

## Files to create

- `src/FlashSkink.Core/Storage/InstanceLock.cs` — ~180 lines.
  Internal `IAsyncDisposable` class wrapping the `FileStream`+`FileShare.None`
  primitive. Static `AcquireAsync` factory returning `Result<InstanceLock>`.
  Disposal deletes the lock file (best-effort) then closes the `FileStream`.
- `src/FlashSkink.Core/Storage/InstanceLockManifest.cs` — ~60 lines.
  Internal `readonly record struct InstanceLockManifest(int Pid, string Host, string StartedAtUtc, string AppVersion)`
  plus a hand-rolled tiny JSON serializer (4 fields, no `System.Text.Json`
  dependency adds — but actually MEL already brings STJ transitively; using
  STJ is fine and simpler). Used both to write the manifest at acquire time
  and to read it (with `FileShare.ReadWrite|Delete`) when surfacing the
  holder in the error message.
- `tests/FlashSkink.Tests/Storage/InstanceLockTests.cs` — ~250 lines.
  Unit tests for the primitive itself (independent of `FlashSkinkVolume`).
- `tests/FlashSkink.Tests/Orchestration/SingleInstanceTests.cs` — ~200 lines.
  Integration tests through `FlashSkinkVolume.CreateAsync` / `OpenAsync` /
  `DisposeAsync` confirming the lock-acquire-on-open / release-on-dispose /
  block-second-open contract end-to-end.

## Files to modify

- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs`
  - Add `_instanceLock` field (nullable, set in `CreateAsync`/`OpenAsync`,
    disposed last in `DisposeAsync`).
  - In `CreateAsync`: after `Directory.CreateDirectory(stagingPath)` and
    `FsyncDirectoryAsync(stagingPath)`, call
    `InstanceLock.AcquireAsync(skinkRoot, options.ForceUnlock, ct)`. On
    failure, return its `Result.Error` directly. Track ownership via a
    `lockHeld` bool the same way `vaultCreated`/`brainCreated` are tracked;
    dispose in `finally` on the failure path. On the success path, the lock
    is passed to `BuildVolumeFromSessionAsync` and stored on the volume.
  - In `OpenAsync`: same insertion right after `Directory.CreateDirectory(stagingPath)`.
  - In `DisposeAsync`: add a Step 5 *after* Step 4 (`_session.DisposeAsync`)
    that disposes `_instanceLock` (release the OS lock, delete the file).
    Last-step ordering is load-bearing — see Method-body contracts below.
  - Update `BuildVolumeFromSessionAsync` signature to accept an
    `InstanceLock` and thread it into the private constructor.
  - Update the private constructor's parameter list and field assignment.
- `src/FlashSkink.Core/Orchestration/VolumeCreationOptions.cs`
  - Add `public bool ForceUnlock { get; init; } = false;` with XML doc
    explicitly noting "intended for CLI `--force` stale-lock recovery; see
    blueprint §19.5". This is additive; no existing test breaks.
- `BLUEPRINT.md`
  - Expand §19.5 from one paragraph to a sub-section with:
    - the file path (already there),
    - the lock-acquisition primitive (`FileStream` + `FileShare.None`),
    - the lock manifest schema (`pid`, `host`, `startedAtUtc`, `appVersion`),
    - the second-instance error surface (`ErrorCode.SingleInstanceLockHeld`
      and the user-facing message),
    - the crash → stale-lock-file → `--force` recovery story,
    - the dispose ordering (lock released *after* session teardown).
- `CLAUDE.md`
  - Add Principle 35 (single-instance per volume), cross-referencing §19.5.
  - Adjust the "Expected growth" line if the file approaches 700 lines.

## Files NOT modified (by design)

- `src/FlashSkink.Core.Abstractions/Results/ErrorCode.cs` — `SingleInstanceLockHeld`
  already exists at line 160. No new value needed.
- `src/FlashSkink.Core/Crypto/VolumeSession.cs` — the lock is not a crypto/brain
  concern; storing it on `VolumeSession` would muddy the type's role. The lock
  is a `FlashSkinkVolume`-level concern.
- `src/FlashSkink.Core/Orchestration/VolumeLifecycle.cs` — same reasoning; the
  lifecycle type's job is vault → brain → migrations. The lock wraps the
  volume, not the brain.
- `src/FlashSkink.CLI/Program.cs` — the `--force` CLI surface lands in Phase 4's
  CLI bootstrap PR. The plumbing in `VolumeCreationOptions.ForceUnlock` is the
  seam; wiring it from `System.CommandLine` is later work.

## Dependencies

- No new NuGet packages. `System.Text.Json` is already pulled transitively for
  configuration / Dapper paths; using it for the 4-field manifest is fine.
  If verification shows it is not in fact transitive in
  `FlashSkink.Core.csproj`, the manifest will use a tiny hand-rolled writer
  (string interpolation) and reader (regex on four `"key":"value"` patterns) —
  the JSON is fully under our control so this is safe. The plan ships with
  STJ as the default and falls back only if the project graph rejects it.
- No project-reference changes.

## Public API surface

No new public types or members. The only public-surface change is:

### `FlashSkink.Core.Orchestration.VolumeCreationOptions` (existing record)

- `public bool ForceUnlock { get; init; } = false;` *(new)*
  Summary intent: opt-in stale-lock recovery — when `true`,
  `CreateAsync`/`OpenAsync` delete any pre-existing
  `.flashskink/instance.lock` before attempting to acquire. Intended for the
  Phase-4 CLI `--force` flag; production callers should leave this `false`.
  Setting `true` does **not** disable the lock — it simply ignores a stale
  lock file from a prior crashed process.

## Internal types

### `FlashSkink.Core.Storage.InstanceLock` (internal sealed, IAsyncDisposable)

- `internal static Task<Result<InstanceLock>> AcquireAsync(string skinkRoot, bool force, CancellationToken ct)`
- `internal string LockFilePath { get; }`
- `public ValueTask DisposeAsync()` — releases the OS lock; best-effort
  deletes the file; idempotent (uses `Interlocked.Exchange` on a `_disposed`
  int the same way `FlashSkinkVolume`/`VolumeSession` do).
- Private constructor takes the open `FileStream` and the path.

### `FlashSkink.Core.Storage.InstanceLockManifest` (internal readonly record struct)

- `int Pid`, `string Host`, `string StartedAtUtc`, `string AppVersion`.
- `internal static InstanceLockManifest Current(string appVersion)` — captures
  `Environment.ProcessId`, `Environment.MachineName`, `DateTime.UtcNow.ToString("O")`,
  and the supplied `appVersion` (which the caller pulls from
  `AssemblyInformationalVersionAttribute` — same source as Refactor PR A
  used for the brain Settings stamps).
- `internal string Serialize()` — emits a compact 4-field JSON object.
- `internal static bool TryParse(ReadOnlySpan<byte> utf8, out InstanceLockManifest manifest)` —
  forgiving parser; if the file is empty / non-JSON / mis-shaped, returns
  `false` and the caller surfaces "unknown holder" in the error message
  rather than throwing.

## Method-body contracts

### `InstanceLock.AcquireAsync(skinkRoot, force, ct)`

Preconditions: `skinkRoot` is non-null and `[skinkRoot]/.flashskink/` exists
(callers — `CreateAsync`/`OpenAsync` — both create it before this call, so
this is a true invariant rather than a runtime check).

Body:

1. Observe `ct.ThrowIfCancellationRequested()`.
2. Compose `lockFilePath = Path.Combine(skinkRoot, ".flashskink", "instance.lock")`.
3. If `force == true`, attempt `File.Delete(lockFilePath)` (best-effort —
   swallow `FileNotFoundException`; let `UnauthorizedAccessException` /
   `IOException` propagate so the caller sees that the lock cannot be
   forcibly cleared, which is the *opposite* of the intent).
4. Try to open the file with:
   ```csharp
   new FileStream(
       lockFilePath,
       FileMode.OpenOrCreate,
       FileAccess.ReadWrite,
       FileShare.None,
       bufferSize: 0,
       FileOptions.WriteThrough)
   ```
   - `FileMode.OpenOrCreate` so a stale file from a crashed process (where
     `force == true`) is reused if the delete in step 3 raced.
   - `FileShare.None` is the exclusion primitive — Windows enforces via the
     opportunistic lock, Linux/macOS via advisory `flock`-equivalent in
     .NET 6+ which closes the cross-platform gap.
5. On the `new FileStream(...)` call:
   - `OperationCanceledException` → caller path is sync, but if a future
     refactor introduces async helpers, still catch first per Principle 14.
   - `IOException` (Windows HRESULT 0x80070020 sharing violation; Linux
     EAGAIN/EWOULDBLOCK from advisory lock; macOS same): another process
     holds the lock. Read the manifest separately (see step 7) and return
     `Result.Fail(SingleInstanceLockHeld, message, metadata)`.
   - `UnauthorizedAccessException`: path is read-only or in a protected
     area — return `Result.Fail(StagingFailed, ...)` (this is a config
     error, not a lock conflict).
   - Bare `catch (Exception ex)` last → `Unknown`.
6. With the stream open and exclusively locked, truncate it (`stream.SetLength(0)`)
   and write `InstanceLockManifest.Current(GetAppInformationalVersion()).Serialize()`
   as UTF-8 bytes, then `stream.Flush(flushToDisk: true)` to make the
   manifest visible to any concurrent reader observing the holder.
7. (Failure-path reader): when the `new FileStream` in step 4 threw `IOException`,
   open the file again with `FileShare.ReadWrite | FileShare.Delete` for read,
   and `using var reader = new FileStream(...)` separately. Read up to ~256
   bytes, try `InstanceLockManifest.TryParse(...)`. If parse fails or the
   file is empty (the holder hasn't written yet — race), fall back to
   placeholder values ("unknown") in the `ErrorContext.Metadata`. This
   helper read uses no shared file handle and tolerates failure.
8. Return `Result<InstanceLock>.Ok(new InstanceLock(stream, lockFilePath))`.

Postconditions on Ok: the returned `InstanceLock` owns the `FileStream`; the
OS lock is held; the manifest is on disk.

Postconditions on Fail: no file handle is leaked; if a partial file was
created in step 4 (`OpenOrCreate` creating a 0-byte file before the lock
failure in a tight race — Windows doesn't but be defensive), the file
remains as-is (the holder's manifest is what matters, not ours).

Cancellation: observed at step 1 only. Once the `FileStream` is open we are
in the millisecond hot path; cancellation between open and manifest write
is unnecessary and would orphan the lock.

Error codes: `SingleInstanceLockHeld`, `StagingFailed`, `Cancelled`, `Unknown`.

### `InstanceLock.DisposeAsync()`

- Idempotent via `Interlocked.Exchange(ref _disposed, 1) != 0` early return.
- Close `_stream` (releases OS lock).
- Best-effort `File.Delete(_lockFilePath)`. Swallow `IOException` and
  `UnauthorizedAccessException` — a stale lock file on disk is harmless
  (the OS lock is gone; the next acquire creates/overwrites). Never throw
  from dispose.

### `FlashSkinkVolume.CreateAsync` — modified

Insert two new blocks. After the existing
`Directory.CreateDirectory(stagingPath); await FsyncDirectoryAsync(stagingPath);`
(lines 148–149):

```csharp
var lockResult = await InstanceLock.AcquireAsync(
    skinkRoot, options.ForceUnlock, ct).ConfigureAwait(false);
if (!lockResult.Success)
{
    return Result<VolumeCreationReceipt>.Fail(lockResult.Error!);
}
instanceLock = lockResult.Value!;
```

Add a `bool lockHeld = false;` and `InstanceLock? instanceLock = null;`
to the locals; set `lockHeld = true` after assignment. On the ownership
transfer near line 198 (`vaultCreated = false; brainCreated = false;`), also
set `lockHeld = false` and copy `instanceLock` into a local `ownedLock`.
Pass `ownedLock` to `BuildVolumeFromSessionAsync`. In the `finally` block
near lines 217-240, after the existing vault/brain cleanup, add:

```csharp
if (lockHeld && instanceLock is not null)
{
    await instanceLock.DisposeAsync().ConfigureAwait(false);
}
```

### `FlashSkinkVolume.OpenAsync` — modified

Same shape. After `Directory.CreateDirectory(stagingPath);` (line 267) and
*before* the existing vault unlock at line 269, insert the
`InstanceLock.AcquireAsync` block. Track ownership via a `lockHeld` bool and
release in `finally` (around line 312, before the `session.DisposeAsync`
call) if not yet transferred. The ownership transfer point is line 291
where `var ownedSession = session; session = null;` already lives —
add `var ownedLock = instanceLock; lockHeld = false;` immediately after.

### `FlashSkinkVolume.BuildVolumeFromSessionAsync` — modified

New parameter `InstanceLock instanceLock`. Threads to the private
constructor. Existing failure-recovery branches (the two `throw new
InvalidOperationException` blocks at queue-start-failure and
mirror-start-failure, lines 982-1003) dispose the new lock alongside the
queue, mirror, cts, context, and session — preserving the "no resource
leaked on any failure path" invariant (Principle 16).

### `FlashSkinkVolume.DisposeAsync` — modified

Insert Step 5 between line 881 (`await _session.DisposeAsync()`) and line
883 (`_volumeCts.Dispose()`):

```csharp
// Step 5 — release the single-instance lock LAST. Holding the lock past
// session teardown means a concurrent second-instance launch sees
// SingleInstanceLockHeld for the entire duration of our shutdown, not
// just up to the point where the brain connection closes. This keeps the
// "two processes never both think they own the volume" invariant clean
// across the full dispose interval. (Blueprint §19.5, Principle 35.)
if (_instanceLock is not null)
{
    await _instanceLock.DisposeAsync().ConfigureAwait(false);
}
```

The CTS dispose stays last per the existing structure.

Why last and not first? If lock release happened before queue/mirror/session
teardown, a second process could acquire the lock and begin manipulating
brain.db / staging while our workers are still draining. SQLite would
handle the file-level race (it has its own locking), but the user-visible
invariant — "single instance per volume" — would be violated for the
teardown window. Releasing last preserves the invariant cleanly.

Why not first-and-then-suppress? Because we cannot atomically tell another
process "I'm shutting down, don't acquire yet." The OS file lock is the
only honest signal we have.

## Integration points

- `FlashSkinkVolume.GetAppInformationalVersion()` (private, added in Refactor A
  at lines ~1180 of FlashSkinkVolume.cs) — `InstanceLockManifest.Current`
  needs the same value. Easiest: `InstanceLock` accepts the version string
  as a parameter from the caller, and `FlashSkinkVolume` passes
  `GetAppInformationalVersion()` in. Alternative: duplicate the helper in
  `InstanceLock`. **Decision: pass the value in as a parameter** — keeps
  `InstanceLock` self-contained for testing (the test passes a literal
  version string), and avoids `InternalsVisibleTo` gymnastics.
- `Directory.CreateDirectory(stagingPath)` already creates the parent
  `.flashskink/` folder as a side effect — the lock file lives in that
  parent. No directory-creation step needed inside `InstanceLock`.
- `FsyncDirectoryAsync` is the existing helper in `FlashSkinkVolume.cs` —
  not called by `InstanceLock` itself (the file's *content* matters
  much less than the file's *existence* for the OS lock; we are not
  protecting against torn writes here).

## Principles touched

- Principle 1 (Core never throws across its public API) — `InstanceLock`
  is internal, but `FlashSkinkVolume.CreateAsync`/`OpenAsync` still return
  `Result<T>`. `IOException` mapping inside `AcquireAsync` is the obligation.
- Principle 13 (`CancellationToken ct` last; named `ct`) — applies to
  `AcquireAsync`.
- Principle 14 (`OperationCanceledException` first catch) — applies in
  `AcquireAsync`.
- Principle 15 (specific catches before bare `Exception`) — `IOException`
  and `UnauthorizedAccessException` are specific; `Exception` is fallback.
- Principle 16 (dispose partial resources on every failure path) — the
  `FileStream` must be disposed if the manifest write throws.
- Principle 17 (`CancellationToken.None` literal in compensation paths) —
  `DisposeAsync` of `FlashSkinkVolume` already follows this; the new
  `_instanceLock.DisposeAsync()` is a synchronous-ish release, no `ct`
  involvement.
- Principle 24 (no silent background failure) — the `SingleInstanceLockHeld`
  Result is surfaced; not a background failure but logged at the construction
  site through `ILogger<FlashSkinkVolume>` (or, since `FlashSkinkVolume`
  has no logger field today, through the lifecycle's logger if exposed;
  see the open question below).
- Principle 25 (appliance vocabulary) — user-facing message says "FlashSkink
  is already running on this volume" not "the single-instance lock is held",
  per blueprint §19.5.
- Principle 26 (no secrets in logs) — `Pid`, `Host`, `StartedAtUtc`,
  `AppVersion` are all non-secret. The manifest never contains key material.
- Principle 30 (crash-consistency invariant) — the lock file does NOT
  participate in the invariant. Its existence after crash is expected and
  recoverable; deleting it does not affect brain/blob/upload-session
  consistency.

## Test spec

### `tests/FlashSkink.Tests/Storage/InstanceLockTests.cs`

Unit tests against the primitive directly. Each test uses a temp directory
under `Path.GetTempPath()` and cleans up in `IAsyncLifetime.DisposeAsync`.

- `AcquireAsync_FirstCall_ReturnsOk_AndCreatesLockFile` — verify the file
  exists at `[root]/.flashskink/instance.lock` and contains valid JSON
  matching the current process's pid/host/version.
- `AcquireAsync_SecondCall_WhileFirstHeld_ReturnsSingleInstanceLockHeld` —
  first lock acquired, second `AcquireAsync` returns
  `Result.Fail(SingleInstanceLockHeld)`. The error metadata includes
  `Pid` matching the first holder's pid (which is `Environment.ProcessId`
  in-process — verify equality).
- `AcquireAsync_AfterFirstDispose_SecondCallSucceeds` — round-trip.
- `AcquireAsync_WithForce_DeletesStaleLockFile_AndSucceeds` — simulate
  stale lock by writing the file directly (no `FileStream` held), then
  call `AcquireAsync(force: true)` and assert success.
- `AcquireAsync_WithoutForce_StaleLockFileOnDisk_StillSucceeds` —
  important: a stale *file* (no live `FileStream` on it) is NOT a held
  lock. `FileMode.OpenOrCreate` + `FileShare.None` succeeds because no
  other process holds it. The test asserts that and proves the `--force`
  flag is *only* relevant for the case where another process held it
  with `FileShare.None` but is gone — which is the same case (since the
  OS released the lock on process exit). So in practice `--force` is
  needed only when the manifest must be overwritten while another
  application is mistakenly holding the file open for reading (rare).
  This test pins the behavior so the user-facing semantics stay clear.
- `LockManifest_ContainsCurrentProcessInfo` — read the file back as JSON,
  assert pid==Environment.ProcessId, host==Environment.MachineName,
  startedAt is a parseable ISO 8601 UTC timestamp within the last 5
  seconds, appVersion matches the constructor argument.
- `DisposeAsync_IsIdempotent` — call twice; second call is a no-op.
- `AcquireAsync_WithCancelledToken_ReturnsCancelled` — pre-cancelled
  token; result is `Result.Fail(Cancelled)`; no file is created.

### `tests/FlashSkink.Tests/Orchestration/SingleInstanceTests.cs`

Integration tests through `FlashSkinkVolume`. Each uses
`flashskink-lock-test-{guid}` temp roots, same pattern as
`VolumeIdentityTests.cs` and `AppVersionStampTests.cs`. Uses
`OrchestrationTestHelper` from Refactor A.

- `CreateAsync_AcquiresLock_LockFileExistsOnDisk` — after CreateAsync
  (without disposing the receipt), `instance.lock` exists.
- `CreateAsync_WhileVolumeOpen_SecondCreate_ReturnsSingleInstanceLockHeld` —
  first `CreateAsync` succeeds, leaves the volume open; second
  `CreateAsync` on the *same* root (the second call would otherwise
  fail with `VolumeAlreadyExists` because vault.bin exists — so we must
  test against a freshly-emptied root where vault.bin has been deleted
  but the volume is still open... actually this is awkward. Better
  test: first `OpenAsync` succeeds and stays open; second `OpenAsync`
  returns `SingleInstanceLockHeld`).
- `OpenAsync_WhileVolumeOpen_SecondOpen_ReturnsSingleInstanceLockHeld` —
  the canonical case. First `OpenAsync` succeeds. Second `OpenAsync`
  returns the failure with metadata containing the first holder's pid.
- `OpenAsync_AfterFirstDispose_SucceedsOnSecondCall` — round-trip,
  proves DisposeAsync releases the lock.
- `OpenAsync_WithForceUnlock_RecoversFromStaleLockFile` — simulate
  a stale lock file (write to disk directly, no held handle), call
  `OpenAsync` with `options.ForceUnlock = true`, assert success.
- `DisposeAsync_ReleasesLock_LockFileIsDeleted` — after dispose, the
  `instance.lock` file no longer exists on disk.
- `Failed_OpenAsync_ReleasesLock_OnFailurePath` — feed `OpenAsync` an
  invalid password; the `Result` is `Fail(InvalidPassword)`; assert
  the lock file does not exist on disk afterward (proves the
  failure-path `finally` cleanup works).

## Acceptance criteria

- [ ] Builds with zero warnings on all targets (`dotnet build --warnaserror`).
- [ ] All new tests pass.
- [ ] No existing tests break.
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] Blueprint §19.5 expanded to a proper sub-section covering lock file
      contents, force-recovery, and dispose ordering.
- [ ] CLAUDE.md gains Principle 35 with the right cross-refs.
- [ ] `VolumeCreationOptions.ForceUnlock` documented with XML doc comment.
- [ ] `InstanceLock` and `InstanceLockManifest` are `internal` (verified
      by absence in the public assembly surface; existing
      `ArchitectureTests` will catch accidental `public` exposure).

## Line-of-code budget

- `src/FlashSkink.Core/Storage/InstanceLock.cs` — ~180 lines
- `src/FlashSkink.Core/Storage/InstanceLockManifest.cs` — ~60 lines
- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — ~+40 lines (delta)
- `src/FlashSkink.Core/Orchestration/VolumeCreationOptions.cs` — ~+8 lines (delta)
- `BLUEPRINT.md` — ~+30 lines (delta)
- `CLAUDE.md` — ~+8 lines (delta)
- `tests/FlashSkink.Tests/Storage/InstanceLockTests.cs` — ~250 lines
- `tests/FlashSkink.Tests/Orchestration/SingleInstanceTests.cs` — ~200 lines

Total: ~325 lines non-test, ~450 lines test, ~38 lines docs.

## Non-goals

- **No CLI `--force` flag.** The `VolumeCreationOptions.ForceUnlock` plumbing
  lands; the `System.CommandLine` wiring waits for Phase 4's CLI bootstrap PR.
- **No watchdog process / health check on the holder pid.** Reading the
  manifest's `Pid` and checking whether that process is still alive (to
  auto-recover from a stale lock without `--force`) would be cross-platform
  ugly and outside scope. The OS-level file lock release on process exit
  is the design's recovery mechanism; the file itself being stale is a
  cosmetic artifact, recoverable via `--force`.
- **No lock at create-time on `[skinkRoot]` itself.** The lock lives at
  `.flashskink/instance.lock`. The directory may not exist yet on a
  brand-new `CreateAsync`; the call sequence is "create .flashskink/staging
  → acquire lock → create vault → create brain → ...". Anything trying to
  race against the directory-create itself is outside the threat model
  (and would be `VolumeAlreadyExists` on the vault step anyway).
- **No tail-side lock.** Tails are not concurrent-mounted; the lock is a
  volume-scope concern on the skink. A second instance with the same
  recovery phrase mounting *its own* skink against the same tails is the
  "clone / split-brain" concern — that's Phase 3.5 work, not this PR.
- **No upgrade of `SingleInstanceLockHeld` semantics to identify
  cross-host holders by IP / hostname-resolution / etc.** The manifest
  records the `MachineName` of the holder; that is the entire user-facing
  story. A USB plugged into machine A and held open while plugged into
  machine B at the same time (network share scenario) shows "FlashSkink
  is running on machine A". Anything more is over-engineering.
- **No tests for crashing the process mid-lock-held.** Out-of-process
  crash simulation is brittle in xUnit; the OS-level file-lock-released-
  on-process-exit behavior is OS-provided contract, not our code.

## Sequencing within the PR

1. Add `InstanceLockManifest` + `InstanceLock` with unit tests, get to
   green. (Pure primitive, no integration.)
2. Add `VolumeCreationOptions.ForceUnlock`.
3. Wire `FlashSkinkVolume.CreateAsync` + `OpenAsync` + `DisposeAsync`,
   thread through `BuildVolumeFromSessionAsync`, update the private
   constructor.
4. Add `SingleInstanceTests` integration suite.
5. Update blueprint §19.5 and CLAUDE.md Principle 35.
6. Final `dotnet build --warnaserror` + `dotnet test` + `dotnet format`.

## Open questions for review

1. **Logger on `FlashSkinkVolume`?** Currently `FlashSkinkVolume` has no
   `ILogger<FlashSkinkVolume>` field. The `SingleInstanceLockHeld` Result
   carries the holder identity in its `ErrorContext.Metadata`; the caller
   (CLI handler in Phase 4) will log it. Per Principle 27 ("Core logs
   internally; callers log the Result"), the construction site of the
   `Result.Fail` should log once. Where? Two choices:
   (a) `InstanceLock.AcquireAsync` takes an `ILogger` parameter and logs.
   (b) `FlashSkinkVolume.CreateAsync`/`OpenAsync` log just before
       returning the failure. Recommendation: **(a)**, plumbed from the
       `LoggerFactory` in `VolumeCreationOptions`. Minor wiring; keeps
       logging next to where the failure is detected.
2. **STJ vs hand-rolled JSON?** Verify `System.Text.Json` is reachable from
   `FlashSkink.Core` without a new `<PackageReference>`. If yes, use it.
   If not, hand-roll. *Decision deferred to implementation*; the manifest
   shape is identical either way.
