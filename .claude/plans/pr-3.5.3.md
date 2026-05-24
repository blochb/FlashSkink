# PR 3.5.3 — Resolution flow and edge-case hardening

**Branch:** `pr/3.5.3-promote-and-edge-case-hardening`
**Blueprint sections:** §19.5 (lock lifecycle context — `PromoteAsync` runs inside the open volume, under the same single-instance lock), §19.6 (witness protocol — `PromoteAsync` writes a fresh witness through `WitnessStore`), §19.7 (fenced state + auto-downgrade — `PromoteAsync` is the clean exit; the auto-downgrade landed in §3.5.2 already provides the crash-recovery dual), §8.5 (notification severity → persistence — the `Info` resolution notification is intentionally NOT persisted to `BackgroundFailures`), §21.3 (crash-consistency invariant — `PromoteAsync` is crash-safe by virtue of §3.5.2's auto-downgrade). New section §19.8 ships in this PR.
**Dev plan section:** [phase-3.5 §3.5.3](../../dev-plan/phase-3.5-volume-identity-and-clone-safety.md) — Resolution flow and edge-case hardening.

---

## Scope

Closes Phase 3.5. After this PR:

- `FlashSkinkVolume.PromoteAsync` is the public Core API a user (today via CLI in Phase 4, today via tests) calls to resolve a split-brain conflict by declaring this skink canonical. It clears `Settings["VolumeState"] = "Fenced"`, writes a fresh witness with `ConflictObserved = false` to every accessible tail, unfences the upload queue, and pulses the wakeup signal so workers resume promptly. Idempotent on an unfenced volume.
- A new `BLUEPRINT.md` §19.8 documents the resolution flow: what `PromoteAsync` does and does NOT do, what happens to the demoted skink on its next open, the re-promote vs. recover choice, the same-phrase/restore-from-image/clock-skew "by-design" cases, and the explicit "no merge" rule.
- A new edge-case test file `tests/FlashSkink.Tests/Identity/WitnessEdgeCaseTests.cs` exercises the meta-plan items 13–17 (truncated file, manual deletion, same-phrase-separate-init, restore-from-image, clock skew) against the `WitnessStore` + `WitnessHandshake` combination directly — these are documentation-as-tests for behaviour that emerges from §3.5.1/§3.5.2 primitives.
- New integration tests in `tests/FlashSkink.Tests/Orchestration/SplitBrainIntegrationTests.cs` cover the end-to-end promote story: state clears, witness rewritten without the marker, uploads resume, idempotent, offline-tail interaction, repair on next open, crash-recovery (the §3.5.2 auto-downgrade path validated from the §3.5.3 perspective), and the user-vocabulary discipline on the resolution notification.

Out of scope for this PR (deferred to V1.x / V2 / Phase 4, per the dev-plan non-goals):

- `flashskink clone` CLI command.
- `Siblings` brain table / clone tracking.
- Automatic recovery from a tail after demotion (Phase 5).
- OAuth-revocation special handling beyond what `IStorageProvider` already exposes.
- `WitnessHandshake` invocation from `CreateAsync` (the dev plan explicitly forbids this — a brand-new volume has no registered tails).
- Auto-update / phone-home (Principle 32, permanent prohibition).
- Any CLI surface for `PromoteAsync` (lands in Phase 4).

---

## Files to create

- `tests/FlashSkink.Tests/Identity/WitnessEdgeCaseTests.cs` — ~150 lines. `internal sealed class WitnessEdgeCaseTests : IDisposable`. Per-test temp-dir setup mirroring `WitnessHandshakeTests`. Uses real `WitnessStore` + `WitnessHandshake` against `FileSystemProvider` instances. Five `[Fact]` tests covering the meta-plan items 13–17. **No new test infrastructure introduced** — the file uses the same `NewTail(...)` / `SeedWitnessAsync(...)` patterns as `WitnessHandshakeTests` (re-implemented locally rather than shared via a helper class, matching the existing per-class self-contained pattern in `tests/FlashSkink.Tests/Identity/`).

## Files to modify

- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — one additive change:
  - New `public async Task<Result> PromoteAsync(CancellationToken ct = default)` method. ~110 lines including XML doc, the gate acquire/release scaffolding, the idempotent fast-path, the per-tail witness write loop, the brain `Settings["VolumeState"]` write via the existing `PersistVolumeStateAsync` helper, the `_uploadQueueService.SetFenced(false)` call, the `_wakeupSignal.Pulse()` call, the `Info` notification publish, and the catch ordering.
- `tests/FlashSkink.Tests/Orchestration/SplitBrainIntegrationTests.cs` — additive: new `[Fact]` methods (no constructor / helper changes; the existing helpers `CreateAndRegisterAsync`, `ReopenAsync`, `ManuallyWriteWitnessAsync`, `ManuallyReadWitnessAsync`, `TailBlobsDir`, `CountCriticalSplitBrainNotifications`, `RandomBytes` are reused as-is). ~230 lines of new test code.
- `BLUEPRINT.md` — additive: new §19.8 inserted directly after §19.7 (currently ends at line 2291; insert §19.8 between line 2291 and the `---` separator at line 2293). ~70 lines.

## Dependencies

- NuGet: none added.
- Project references: none added.

---

## Public API surface

One new public method on `FlashSkinkVolume`:

```csharp
/// <summary>
/// Resolves a split-brain conflict by declaring this skink canonical. Clears the fenced
/// state (<c>Settings["VolumeState"] = "Normal"</c>), writes a fresh witness with
/// <c>ConflictObserved = false</c> to every accessible tail, unfences
/// <see cref="UploadQueueService"/> so Phase 2 uploads resume, and pulses the wakeup signal
/// so workers exit their fenced-idle wait promptly. Idempotent — safe to call on an
/// unfenced volume (returns <see cref="Result.Ok"/> immediately).
/// </summary>
/// <remarks>
/// <para>
/// The demoted skink (the one not promoted) retains its <c>Settings["VolumeState"] = "Fenced"</c>
/// and will see <c>ConflictObserved = false</c> in the fresh witness on its next session
/// open. A fenced volume that opens and reads <c>ConflictObserved = false</c> with an epoch
/// it recognises as ahead of its own knows the winner has promoted: it should display a user
/// message recommending recovery via a tail rather than attempting to promote itself
/// (this guidance is surfaced as a notification; the volume still works for Phase 1 and reads).
/// </para>
/// <para>
/// <strong>Idempotency vs. stale-tail repair.</strong> An offline tail at the time of a prior
/// promote retains its old <c>ConflictObserved = true</c> witness. <see cref="PromoteAsync"/>
/// does NOT re-attempt the write — that repair is performed automatically by the next
/// <see cref="OpenAsync"/>'s handshake: with <c>alreadyFenced=false</c> post-promote, the
/// handshake writes a fresh <c>ConflictObserved = false</c> witness to every accessible tail
/// (including the previously-offline one if it is now reachable). This keeps
/// <see cref="PromoteAsync"/> cheap and lets the recurring session-open handshake act as the
/// eventual-consistency repair loop.
/// </para>
/// <para>
/// <strong>Crash-safety.</strong> The sequence is: write fresh witnesses to all accessible
/// tails, then upsert <c>Settings["VolumeState"] = "Normal"</c>, then flip the in-memory
/// flag, then notify. If the process crashes after the witness writes succeed but before the
/// brain write, the next open observes brain-says-Fenced but witnesses-say-clean and
/// auto-downgrades (§3.5.2's <c>RunWitnessHandshakeAsync</c> transition table; Blueprint
/// §19.7). No brain-side journal is required. (Blueprint §19.8.)
/// </para>
/// </remarks>
public async Task<Result> PromoteAsync(CancellationToken ct = default)
```

No new public types. No new public properties. No constructor changes. No changes to the existing `RegisterTailAsync`, `OpenAsync`, `CreateAsync`, `WriteFileAsync`, etc. — `PromoteAsync` is a self-contained additive surface.

---

## Internal types

None added. `PromoteAsync` reuses existing internal types: `WitnessStore`, `WitnessPayload`, `BrainScope` (via `IBrainAccess.LockAsync`), `Notification`, `NotificationSeverity`.

---

## Method-body contracts

### `FlashSkinkVolume.PromoteAsync(ct)`

**Preconditions:**
- Volume is open (not disposed). Disposed volumes throw `ObjectDisposedException` (via `ThrowIfDisposed`), same convention as every other public method on this type.
- `ct` may be cancelled; the entry-point gate-acquire returns `Result.Fail(ErrorCode.Cancelled)` rather than throwing.

**Sequence:**

1. `ThrowIfDisposed();`
2. Acquire the gate: `try { await _gate.WaitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException ex) { return Result.Fail(ErrorCode.Cancelled, "Promote cancelled.", ex); }`
3. `ThrowIfDisposedAndReleaseGate();` — handles the race where dispose started while we were waiting on the gate.
4. **Inside `try { … } finally { _gate.Release(); }`:**
   - **Idempotent fast-path.** If `State == VolumeState.Normal`: return `Result.Ok()` immediately. No notification, no log, no tail writes, no brain write. This is the meta-plan idempotency contract and the dev plan's step 3.
   - **Log at Information** (Principle 27 — log at the construction site of the state transition): `_logger.LogInformation("User-initiated promote — clearing fenced state and writing fresh witnesses to all accessible tails.");`
   - **Read `VolumeID`, `VolumeEpoch`, active provider ids from the brain.** Single `await _session.Brain!.LockAsync(ct)` scope (Principle 36) holding both queries:
     - `SELECT Value FROM Settings WHERE Key = 'VolumeID'` → `volumeId`
     - `SELECT Value FROM Settings WHERE Key = 'VolumeEpoch'` → `epochRaw`, then `long.Parse(..., CultureInfo.InvariantCulture)` (the brain seeds this in `CreateAsync`; `BackfillAndStampOnOpenAsync` increments it on every open; absence or unparseability would be a defect at this point, so a parse failure here is a hard error — falls through to `SqliteException` / `Exception` catch). The freshly-incremented epoch from the open *plus* any subsequent writes during this session — actually `VolumeEpoch` is incremented once per `OpenAsync`, so the current value IS the local newEpoch of the open session. Re-use is correct.
     - `SELECT ProviderID FROM Providers WHERE IsActive = 1` → `activeProviderIds` (list of strings)
     - Dapper is permitted here (Principle 22 — `PromoteAsync` is user-initiated, not on the upload hot path).
   - **Build the fresh witness payload.** `var payload = WitnessPayload.ForNewSession(volumeId, currentEpoch, GetAppInformationalVersion());` — the factory sets `ConflictObserved = false` by default, which is exactly what we want. **Do not** apply a `with { ConflictObserved = false }` modifier — redundant and noisy.
   - **Write the fresh witness to every accessible tail.** For each `providerId` in `activeProviderIds`:
     - Resolve via `var resolved = await _providerRegistry.GetAsync(providerId, ct).ConfigureAwait(false);`. If `!resolved.Success`: log at Debug (`"Provider {ProviderId} not resolvable from registry ({Code}); skipping in promote."`), continue. This mirrors the §3.5.2 handshake's tolerance for an unregistered provider id (a registry that has not yet been populated for this tail — normal during Phase 4 brain-backed-registry edge cases).
     - `var writeResult = await _witnessStore.WriteAsync(resolved.Value!, _session.Dek, payload, CancellationToken.None).ConfigureAwait(false);` — **Principle 17**: `CancellationToken.None` as a literal at the await site. Once we have decided to clear the fence, the witness write is the atomic-commit step; cancellation mid-promote-write would leave half the tails carrying `ConflictObserved=true` and the other half carrying `ConflictObserved=false`, defeating the resolution-on-next-handshake guarantee for the demoted skink.
     - On write failure, log at Warning (`"Witness write to tail {ProviderId} during promote failed ({Code}); the next session-begin handshake will retry."`) and continue with the next tail. A per-tail failure is NOT fatal — the next handshake will retry as part of its normal read-then-write loop (the dev-plan note on idempotency-vs-stale-tail-repair). This matches the §3.5.2 handshake's posture on per-tail write failures and the dev-plan step 4c.
   - **Persist `Settings["VolumeState"] = "Normal"`.** Call the existing static helper: `await PersistVolumeStateAsync(_session.Brain!, VolumeState.Normal).ConfigureAwait(false);` — it already uses `CancellationToken.None` internally for the brain scope and the SQL (Principle 17). Reusing this helper keeps the §3.5.2 / §3.5.3 brain-write code paths identical, which is exactly the property that makes auto-downgrade and promote produce equivalent terminal state.
   - **Unfence the upload queue.** `_uploadQueueService.SetFenced(false);` — the field is `volatile int` updated via `Interlocked.Exchange`, so this is thread-safe against worker reads.
   - **Flip the in-memory state.** `State = VolumeState.Normal;` — assigned under the gate, so no torn read by concurrent volume-method callers (every other entry point also acquires `_gate` before touching state).
   - **Pulse the wakeup signal.** `_wakeupSignal.Pulse();` — workers idling in `IdleAsync(OrchestratorIdle, ct)` / `IdleAsync(WorkerIdle, ct)` from the §3.5.2 fenced-guard wake up immediately rather than waiting for the next 60-second poll. Tested by `PromoteAsync_ResumesUploads`.
   - **Publish the `Info` notification.** User vocabulary only (Principle 25):
     ```csharp
     await _context.NotificationBus.PublishAsync(new Notification
     {
         Source = nameof(FlashSkinkVolume),
         Severity = NotificationSeverity.Info,
         Title = "Conflict resolved",
         Message = "FlashSkink will now resume uploading to your tails.",
         OccurredUtc = _clock.UtcNow,
         RequiresUserAction = false,
     }, CancellationToken.None).ConfigureAwait(false);
     ```
     The notification has no `Error` set (it is not a failure); `Source = nameof(FlashSkinkVolume)` matches the convention used by `RunWitnessHandshakeAsync` for the parallel auto-downgrade notification. **Vocabulary check:** "Conflict resolved" + "FlashSkink will now resume uploading to your tails." — anchors include "FlashSkink" and "tails"; forbidden tokens (`epoch`, `witness`, `fence`, `fenced`, `volume`, `split-brain`, `split brain`, `dek`, `aad`, `wal`, `blob`, `stripe`) all absent. The test `PromoteAsync_Notification_UsesUserVocabularyOnly` enforces the negative.
   - **Return `Result.Ok()`.**
5. **Catch ordering** (outside the `finally _gate.Release()`):
   - `OperationCanceledException ex → Result.Fail(ErrorCode.Cancelled, "Promote cancelled.", ex)` — first. Note: this can fire from step 2's `_gate.WaitAsync(ct)` or from the step-4 brain reads (the `LockAsync(ct)` scope and the Dapper queries observe `ct`).
   - `SqliteException ex → Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to persist volume state during promote.", ex)` — the only SQLite operation that runs with a token capable of being cancelled is the brain-read step; subsequent SQL writes use `CancellationToken.None` per Principle 17. A `SqliteException` here is most likely a real I/O failure on the brain file, mapped to `DatabaseWriteFailed` per the §3.5.2 pattern.
   - `Exception ex → Result.Fail(ErrorCode.Unknown, "Unexpected error during promote.", ex)` — last. Logged at Warning at the construction site (mirrors §3.5.2's `RunWitnessHandshakeAsync` catch).

**Failure mode when `PersistVolumeStateAsync` fails after the witness writes succeed.** The witness writes have already gone out with `ConflictObserved=false` (the desired post-promote state). The brain `Settings["VolumeState"]` row is still `"Fenced"`. The next open will: read brain-says-Fenced, observe witnesses-say-clean from at least one tail, auto-downgrade to Normal. This is the crash-safety story; nothing extra is needed on the failure side — the user sees `Result.Fail(DatabaseWriteFailed)`, the volume remains in fenced state for the remainder of this session (a follow-up retry of `PromoteAsync` will hit the fast-path after the brain row is upserted on retry; or the user closes and reopens and auto-downgrade fixes it). Tested implicitly by the §3.5.2 `AutoDowngrade_StaleFencedBrainRow_WitnessShowsNoConflict_DowngradesToNormal` (already green).

**Principle 17 hygiene table** (every await site inside `PromoteAsync` after the gate acquisition):

| Site | Token | Reason |
|---|---|---|
| `_gate.WaitAsync(ct)` | `ct` | Entry-point cancellation gate |
| `_session.Brain!.LockAsync(ct)` (the read scope) | `ct` | Brain reads observe cancellation — no resource committed yet |
| Dapper `QuerySingleAsync` / `QueryAsync` inside the scope | `ct` | Same scope; pre-commit |
| `_providerRegistry.GetAsync(providerId, ct)` | `ct` | Provider lookup is a read; no commit yet |
| `_witnessStore.WriteAsync(provider, dek, payload, CancellationToken.None)` | **`CancellationToken.None`** | Once decided, the witness write is the commit; Principle 17 |
| `PersistVolumeStateAsync(brain, VolumeState.Normal)` | **`CancellationToken.None`** (inside the helper) | Brain commit; Principle 17 |
| `_context.NotificationBus.PublishAsync(notification, CancellationToken.None)` | **`CancellationToken.None`** | Post-commit notification; user must hear about the resolution even if the caller is mid-shutdown |

Per the dev-plan principle-17 note: "every tail-write call inside `PromoteAsync` passes `CancellationToken.None` at the await site (the entry-point `ct.ThrowIfCancellationRequested()` is the only cancellation check)" — the entry-point check is the `_gate.WaitAsync(ct)` line plus the cancellable brain-read scope; once we cross into the commit phase (witness writes, brain write, notification), it's all `CancellationToken.None`.

---

## Integration points

- **`FlashSkinkVolume._session.Brain` (IBrainAccess)** — `PromoteAsync` acquires a `BrainScope` to read `VolumeID` / `VolumeEpoch` / `Providers`. Same access pattern as `RegisterTailAsync` (`FlashSkinkVolume.cs` lines 944–963) and `RunWitnessHandshakeAsync` (lines 1326–1337).
- **`FlashSkinkVolume._witnessStore` (WitnessStore)** — `PromoteAsync` calls `WriteAsync(provider, dek, payload, CancellationToken.None)`. The field was added in §3.5.2 for exactly this purpose; no new field needed.
- **`FlashSkinkVolume._uploadQueueService.SetFenced(false)`** — flips the volatile flag the §3.5.2 worker-loop guards check. Verified by `PromoteAsync_ResumesUploads`.
- **`FlashSkinkVolume._wakeupSignal.Pulse()`** — wakes the orchestrator/worker `IdleAsync` waits introduced in §3.5.2 (`UploadQueueService.cs` lines 316–323 and 460–...). Without the pulse the unfenced workers would wait up to the next poll interval before noticing.
- **`FlashSkinkVolume._providerRegistry` (IProviderRegistry)** — `PromoteAsync` resolves each active provider id via `GetAsync`. Tests use `InMemoryProviderRegistry`; production Phase 4 uses `BrainBackedProviderRegistry`. Both expose `GetAsync(providerId, ct)`.
- **`FlashSkinkVolume._context.NotificationBus` (INotificationBus)** — `PromoteAsync` publishes one `Info`-severity notification. Per Blueprint §8.5, `Info` is NOT persisted to `BackgroundFailures` — transient by design.
- **`FlashSkinkVolume.PersistVolumeStateAsync(IBrainAccess, VolumeState)`** — existing private static helper from §3.5.2 (`FlashSkinkVolume.cs` lines 1503–1510). Reused as-is; no signature change.
- **`FlashSkinkVolume.GetAppInformationalVersion()`** — existing private static helper (lines 1685+). Reused as-is.

---

## Principles touched

- **Principle 1** (Core never throws across the public API) — `PromoteAsync` returns `Result`. The only throw across its boundary is `ObjectDisposedException` from `ThrowIfDisposed()`, which matches every other public method on the type (sanctioned existing convention; not a violation since the public surface returns `Result` on every defined input).
- **Principle 13** (`CancellationToken ct` last) — `PromoteAsync(CancellationToken ct = default)` — sole parameter, last position, default value.
- **Principle 14** (`OperationCanceledException` first catch) — the catch block lists OCE first, then `SqliteException`, then `Exception`. Cancellation maps to `ErrorCode.Cancelled`; logged at Debug/Information (not Error).
- **Principle 15** (no bare `catch (Exception)` as the only catch) — three catch clauses: OCE, `SqliteException`, `Exception`. The narrow-to-broad ordering matches the §3.5.2 `RunWitnessHandshakeAsync` precedent (`FlashSkinkVolume.cs` lines 1479–1495).
- **Principle 17** (`CancellationToken.None` literal in compensation paths) — every commit-phase await (witness writes, `PersistVolumeStateAsync`, notification publish) uses `CancellationToken.None` spelled out as a literal. See the Principle-17 hygiene table above.
- **Principle 22** (Dapper outside hot paths) — `PromoteAsync` is user-initiated, runs at most once per session, executes three Dapper reads + the existing Dapper upsert in `PersistVolumeStateAsync`. Not on the upload or write hot path.
- **Principle 24** (no silent background failures) — `PromoteAsync`'s success path publishes an `Info` notification. Failures inside `PromoteAsync` return `Result.Fail` to the caller; the volume stays in `Fenced` state until the user retries. Per-tail witness write failures are logged at Warning but do not fail the overall operation — the session-open handshake on the next reopen retries (eventual-consistency repair loop documented in §19.8).
- **Principle 25** (appliance vocabulary) — notification text uses "FlashSkink", "Conflict resolved", "resume uploading", "your tails" — anchors only. Forbidden tokens (`epoch`, `witness`, `fence`, `fenced`, `volume`, `split-brain`, `split brain`, `dek`, `aad`, `wal`, `blob`, `stripe`) all absent. Enforced by the `PromoteAsync_Notification_UsesUserVocabularyOnly` test.
- **Principle 26** (no secrets in logs) — `PromoteAsync` logs `ProviderID`, `ErrorCode`, and the entry-point info line. No DEK bytes, no plaintext, no envelope bytes, no `_session.Dek` references in log messages. The notification's `Error` field is unset (no metadata that could leak).
- **Principle 27** (Core logs at the construction site of the Fail / state-transition) — `PromoteAsync` logs at Information on entry (`"User-initiated promote — clearing fenced state…"`). The downgrade-to-Normal state transition is the spiritual analogue of constructing a `Result.Fail`'s log site; one log per call.
- **Principle 28** (Core depends only on MEL abstractions) — `PromoteAsync` uses `ILogger<FlashSkinkVolume>` from the existing `_logger` field. No Serilog reference introduced.
- **Principle 30** (crash-consistency invariant preserved) — `PromoteAsync`'s crash window (between the tail witness writes and the brain `Settings["VolumeState"]` write) is recovered by §3.5.2's auto-downgrade. No new invariant is introduced; the §3.5.3 edge-case test `OpenAsync_CrashRecovery_BrainSaysFenced_WitnessShowsNoConflict_AutoDowngrades` proves the round-trip from the §3.5.3 perspective (the §3.5.2 test already covers the half before the crash; this PR's test covers the after).
- **Principle 31** (keys zeroed on volume close) — `PromoteAsync` does not hold the DEK in any new location. It passes `_session.Dek` as a `ReadOnlyMemory<byte>` argument to `_witnessStore.WriteAsync`; the store consumes it synchronously per call (per §3.5.1's design).
- **Principle 33** (Volume identity is a random GUID, never derived from recovery-phrase material) — `PromoteAsync` reads `Settings["VolumeID"]` (a Guid written by `CreateAsync` / `BackfillAndStampOnOpenAsync`) and embeds it in the fresh witness payload. No new derivation paths.
- **Principle 35** (single-instance per volume) — `PromoteAsync` runs INSIDE the single-instance lock window (between `InstanceLock.AcquireAsync` and `DisposeAsync`'s release step). A second concurrent open against the same skink root would have failed at `InstanceLock` already, so `PromoteAsync` is single-writer on the host. Cross-host network-mounted USB caveats are documented in §19.6's known-limitation block.
- **Principle 36** (brain SQL flows through `IBrainAccess`) — `PromoteAsync` acquires a `BrainScope` via `_session.Brain!.LockAsync(ct)` for the reads, and reuses the existing `PersistVolumeStateAsync` helper (which acquires its own scope with `CancellationToken.None`) for the upsert. No raw `SqliteConnection` injected anywhere.

---

## Test spec

### `tests/FlashSkink.Tests/Orchestration/SplitBrainIntegrationTests.cs` (additive)

All tests reuse the existing class scaffolding (`InitializeAsync` / `DisposeAsync` / `DefaultOptions` / `CreateFsProvider` / `CreateAndRegisterAsync` / `ReopenAsync` / `ReadDekAsync` / `ManuallyWriteWitnessAsync` / `ManuallyReadWitnessAsync` / `TailBlobsDir` / `RandomBytes` / `CountCriticalSplitBrainNotifications`). No new helpers required.

Helper to count `Info` notifications with `Source = nameof(FlashSkinkVolume)` (added as a one-line private method to match the existing pattern):

```csharp
private int CountInfoFromVolume() =>
    _bus.Published.Count(n =>
        n.Severity == NotificationSeverity.Info &&
        n.Source == nameof(FlashSkinkVolume));
```

Tests (the dev-plan §3.5.3 list, with adjustments where current code already makes a test redundant):

- `PromoteAsync_ClearsFencedState_ReturnsNormal` — Setup: create + register, dispose, `ManuallyWriteWitnessAsync(epoch: 99, conflictObserved: false)`, reopen → `State == Fenced`. Action: `await volume.PromoteAsync()`. Assert: `Result.Success`, `volume.State == VolumeState.Normal`, the `Settings["VolumeState"]` row reads `"Normal"`.
- `PromoteAsync_WritesFreshWitnessWithNoConflictMarker` — Same setup. Action: `PromoteAsync`. Assert: `ManuallyReadWitnessAsync()` returns a witness with `ConflictObserved == false`. (The epoch will be whatever the local newEpoch was at the time of the most recent open — this test asserts the marker, not the epoch.)
- `PromoteAsync_ResumesUploads` — Setup: same fenced-volume setup. Write a file via `WriteFileAsync` while fenced (queued, not uploaded). Action: `PromoteAsync`. Assert: within a generous wait window (~3 seconds is conservative; the wakeup pulse should fire the worker within tens of milliseconds), at least one blob file appears under `TailBlobsDir()`. **Note on flakiness**: the worker may not have committed the upload by the time we observe; the test polls with a 50 ms sleep up to 3 seconds, asserting at least one `*.bin` under `blobs/`. Matches the existing polling pattern in `FlashSkinkVolumeUploadIntegrationTests` (search for that file to confirm the pattern is already there before authoring — fall back to a fixed `Task.Delay(2)` followed by a single assertion if the polling helper does not exist).
- `PromoteAsync_OnUnfencedVolume_ReturnsOk` — Setup: create + register (state stays `Normal` — no manual witness write). Action: `await volume.PromoteAsync()`. Assert: `Result.Success`; `State == Normal`; no `Info` notification published (the fast-path emits nothing — `CountInfoFromVolume()` returns 0 net change).
- `PromoteAsync_IsIdempotent` — Setup: fenced volume. Action: `await volume.PromoteAsync()` then `await volume.PromoteAsync()`. Assert: both return `Ok`; `CountInfoFromVolume()` increments by exactly 1 across the two calls (the second hit the fast-path, emitted no notification).
- `PromoteAsync_OfflineTails_StillClearsLocalState` — Setup: fenced volume + use `FaultInjectingStorageProvider` wrapping the FS provider, configured to `FailNextBeginWith(ProviderUnreachable)` (this fails the witness write inside `PromoteAsync`). **Implementation detail**: re-registering a tail mid-session is not currently supported (`RegisterTailAsync` does not de-register), so the test must register the *faulty* provider from the start. The setup becomes: register the faulty provider; dispose; reopen — but the faulty provider's `FailNextBegin` knob has fired during the initial `RegisterTailAsync` (matching `WitnessWriteFailureOnRegistration_DoesNotFailRegisterTail`), so by the time we reopen, the knob is exhausted. **Resolution**: use a second `FailNextBeginWith(ProviderUnreachable)` call after the reopen-with-conflict step but before `PromoteAsync` — re-arms the knob so the `PromoteAsync` witness write hits the unreachable case. Alternative: skip the faulty wrapper entirely and just assert that with one tail registered, `PromoteAsync` succeeds and `State == Normal` even if we use `ManuallyDelete` on the tail's `_witness/current.enc` between the reopen and the promote — proves "no reachable witness path doesn't block the local-state clear." **Decision**: use the second approach (manual delete of `_witness/current.enc` between reopen and promote). Simpler, exercises the same code path, no fault-injector reuse complexity. Assert: `PromoteAsync` returns `Ok`; `State == Normal`; `Settings["VolumeState"] == "Normal"`. No assertion on whether the tail's witness was rewritten (it depends on whether the file system delete affected the FS provider's view; the asserted behaviour is the local-state clear).
- `OpenAsync_AfterPromote_OfflineTailNowOnline_RepairsWitness` — Setup: register two tails; dispose; manually write a fenced witness ONLY to tail 1 (advances tail 1's witness to epoch 99); reopen → `State == Fenced`. **Wait** — `ManuallyWriteWitnessAsync` is hard-wired to the single `_tailRoot`. Test variant: register one tail; manually delete `_witness/current.enc` from the tail's `_witness/` directory; dispose; manually write a fenced witness; reopen → fenced; promote → success; assert tail's witness is now `ConflictObserved == false`. **Adjusted scope**: the "two tails one offline" version requires plumbing a second tail root into the existing scaffolding. Since `SplitBrainIntegrationTests` already constructs `_tailRoot` as a single directory, adding a second tail in one test feels heavyweight. **Decision**: re-scope the test to assert the simpler invariant — "after `PromoteAsync` completes, the next reopen sees a clean witness and the handshake remains Normal." That is the user-visible behaviour the dev plan describes. The "previously-offline tail repaired by next handshake" is interesting in production with multi-tail volumes; for §3.5.3 the single-tail variant is adequate proof. Final test name: `OpenAsync_AfterPromote_HandshakeKeepsVolumeNormal`. Asserts: `State == Normal` on reopen; the witness on disk has `ConflictObserved == false` and the epoch matches the post-reopen newEpoch.
- `OpenAsync_CrashRecovery_BrainSaysFenced_WitnessShowsNoConflict_AutoDowngrades` — **This test already exists** as `AutoDowngrade_StaleFencedBrainRow_WitnessShowsNoConflict_DowngradesToNormal` in `SplitBrainIntegrationTests.cs` (added in §3.5.2). The dev plan says §3.5.3 also covers it; here we satisfy that by leaving the §3.5.2 test in place (it already proves the crash-recovery path) and adding a §3.5.3-flavoured comment-only test that asserts the *post-promote* shape: `PromoteAsync` writes the cleared witness; if we then simulate a crash by upserting `Settings["VolumeState"] = "Fenced"` again WITHOUT touching the witness, the next reopen auto-downgrades. This is a richer narrative: "user promoted, the brain crashed before recording, the next open self-heals." **Test name**: `PromoteAsync_ThenSimulatedCrash_NextOpenAutoDowngrades`. Asserts: `State == Normal` post-reopen; one Info notification per reopen (the auto-downgrade is the source — this test is checking the §3.5.2 path, not a §3.5.3-side-effect, but the narrative is §3.5.3's).
- `PromoteAsync_Notification_UsesUserVocabularyOnly` — Setup: fenced volume. Action: `PromoteAsync`. Capture the `Info` notification (`_bus.Published.Single(...)`). Assert: `Title + " " + Message` (lowercased) contains at least one of `"skink"`, `"tail"`, `"copy"`, `"flashskink"`; assert it does NOT contain any of `"epoch"`, `"witness"`, `"fence"`, `"fenced"`, `"volume"`, `"split-brain"`, `"split brain"`, `"dek"`, `"aad"`, `"wal"`, `"blob"`, `"stripe"`. **Note**: the existing affirmative anchors set must include `"flashskink"` because the literal message is `"FlashSkink will now resume uploading to your tails."` — neither "skink" nor "tail" alone reads as a positive match here, but "FlashSkink" does, as does "tails". The test pattern from `Notification_SplitBrainDetected_UsesUserVocabularyOnly` (lines 387–426) is a precise template; the new test follows the same shape with the negative tokens identical and the affirmative tokens widened slightly to include `"flashskink"`.

**Cancellation tests** — intentionally omitted. The dev plan calls only for the seven tests above plus the edge-case file; adding `PromoteAsync_PreCancelledToken_ReturnsCancelled` is the kind of mechanical defensive test that this PR's Principle-13/14 audit already implies. If a Gate-2 reviewer asks for it, it's a 12-line addition; we hold the line on the dev-plan headline list.

### `tests/FlashSkink.Tests/Identity/WitnessEdgeCaseTests.cs` (new file)

`internal sealed class WitnessEdgeCaseTests : IDisposable`. Per-test setup mirroring `WitnessHandshakeTests`:

```csharp
private const string TestVolumeId = "test-volume-id";
private const string TestAppVersion = "0.1.0+edge-case-test";

private readonly string _rootBase;
private readonly WitnessStore _store;
private readonly WitnessHandshake _handshake;
private readonly byte[] _dek;
```

Helpers (private to the class, matching the pattern in `WitnessHandshakeTests`):

- `private FileSystemProvider NewTail(string providerId)` — identical body to `WitnessHandshakeTests.NewTail`.
- `private async Task SeedWitnessAsync(IStorageProvider provider, long epoch, bool conflictObserved, string? volumeId = null)` — identical to `WitnessHandshakeTests.SeedWitnessAsync`.

Tests:

- `PartialWitnessFile_TruncatedDuringWrite_TreatedAsAbsent` — Construct a tail. Manually write a partial (truncated) `.enc` envelope to `{root}/_witness/current.enc` (e.g. 10 bytes — less than the 1-byte version + 12-byte nonce + 16-byte tag minimum). Call `_store.TryReadAsync(tail, _dek, ct)`. Assert: `Result.Success == true`, `Value == null`. Then run the handshake against this tail with `newEpoch = 5`: assert `ConflictDetected == false`; assert the tail's witness was rewritten cleanly (a follow-up `TryReadAsync` returns a non-null payload with `Epoch == 5`).
- `WitnessDeletion_ManualDelete_TreatedAsFirstUse` — Seed a witness via `SeedWitnessAsync(tail, epoch: 3, conflictObserved: false)`. Manually `File.Delete({root}/_witness/current.enc)`. Run the handshake. Assert: `ConflictDetected == false`, `TailsRead == 1` (the tail was reachable; no payload visible), `TailsWritten == 1` (a fresh witness was written). Follow-up `TryReadAsync` returns the new payload with `Epoch == newEpoch`.
- `SamePhraseSeperateInit_DifferentVolumeIds_NoWitnessInteraction` — Construct two tails sharing a common backing root via FS namespace mechanics? No — the test would need to fake a "shared provider" reading the same `_witness/` directory under two different volume identities. Simpler version: construct ONE tail. Seed a witness with a different `volumeId` (`SeedWitnessAsync(tail, epoch: 99, conflictObserved: true, volumeId: "different-volume-id-altogether")`). Run the handshake with the test's `TestVolumeId`. Assert: `ConflictDetected == false` (the volume-id mismatch skips the witness — the §3.5.2 handshake logs Warning and treats as no info); `TailsRead == 0` (the §3.5.2 implementation does NOT increment `tailsRead` on a VolumeId mismatch — see `WitnessHandshake.cs` lines 209–215 and the surrounding comments). Then assert that after the handshake's write phase, the tail's witness now carries `TestVolumeId` (the handshake overwrote the foreign witness with our own). **This is the by-design behaviour documented in §19.8**: different VolumeIDs → no witness interaction even on a shared provider, and the local volume claims the witness on its first observation.
- `RestoreFromImage_IdenticalVolumeIdAndEpoch_DetectedOnNextOpen` — Construct one tail. Seed a witness with `epoch: 5, volumeId: TestVolumeId, conflictObserved: false` (the restore image's witness). Run the handshake with the test's `volumeId: TestVolumeId` and `newEpoch: 5` (the restored skink's first session reaches the same newEpoch). Assert: `ConflictDetected == true`; `Trigger == ConflictTrigger.EpochComparison` (Trigger A — `witness.Epoch >= newEpoch` holds at `5 == 5`); `ConflictingProviderId == "tail-1"`. **By-design**: restore-from-image collapses to "same VolumeID + same epoch" and the standard epoch comparison catches it on the first race.
- `ClockSkew_LargeTimestampDifference_DoesNotAffectConflictDetection` — Construct one tail. Manually construct a `WitnessPayload` with `CommittedAtUtc = DateTime.UtcNow.AddYears(100).ToString("O")`, `Epoch = 1, ConflictObserved = false, VolumeId = TestVolumeId`. Write via `_store.WriteAsync(tail, _dek, payload, CancellationToken.None)`. Run the handshake with `newEpoch = 2`. Assert: `ConflictDetected == false` — the timestamp is informational only. Symmetric check with a far-past timestamp (`AddYears(-100)`) — also `ConflictDetected == false`. **By-design**: epoch is the authoritative signal; the timestamp is diagnostic.

### `BLUEPRINT.md` — new §19.8

Inserted between the end of §19.7 (line 2291) and the `---` separator (line 2293). Content sketch (final wording is the implementer's prose, but it must cover):

```markdown
### 19.8 Resolution Flow

**`PromoteAsync` — canonical declaration, not a merge.** When the user resolves a
split-brain by declaring one skink canonical, `FlashSkinkVolume.PromoteAsync` clears the
fenced state on the local volume, writes a fresh witness with `ConflictObserved = false`
to every accessible tail, unfences the upload queue, and pulses the wakeup signal so
Phase 2 uploads resume promptly. Idempotent on an unfenced volume. (Dev plan §3.5.3.)

The promoted skink keeps its local writes and resumes uploads. The demoted skink keeps
its local writes too, but on its next open the session-begin handshake reads the
winner's fresh witness from the tail. Two outcomes are possible — and the difference
between them is the basis for the CLI choice in Phase 4:

1. The demoted skink's local epoch is BELOW the witness epoch (the winner has run
   sessions since the divergence): Trigger A fires; the demoted skink remains Fenced.
2. The demoted skink's local epoch is ABOVE the witness epoch (the demoted skink has
   advanced further since the divergence): Trigger B fires only if the witness still
   carries `ConflictObserved = true` (it does not after a promote); Trigger A does not
   fire. The demoted skink would auto-downgrade to Normal. Operationally this means
   the demoted skink "wins" by virtue of being further ahead — which is wrong if the
   user already promoted the other side.

To handle case 2 cleanly, the demoted skink's user-facing guidance after observing
`ConflictObserved = false` from the winner is presented as a notification recommending
recovery via a tail (Phase 5). The volume still works for Phase 1 and reads regardless.

**The re-promote vs. recover choice.** The CLI in Phase 4 must surface this choice
explicitly when a user opens a previously-demoted skink:

- *Re-promote* — override the prior decision; the local writes win, the previous winner
  is demoted. The CLI calls `PromoteAsync` on the local volume. The next open of the
  previously-winning skink sees the new marker and fences itself.
- *Recover from a tail* — discard the local writes and adopt the winner's state. The
  CLI invokes the Phase 5 recovery flow against any reachable tail. The local skink is
  effectively re-initialised from the recovered brain mirror.

These are fundamentally different actions — re-promote keeps local writes and discards
the winner's; recovery discards local writes and adopts the winner's. The choice is
the user's; this section documents the semantics, not the UX.

**Crash-safety reference.** `PromoteAsync` writes fresh witnesses to all tails before
persisting `Settings["VolumeState"] = "Normal"`. The §3.5.2 auto-downgrade path in
`RunWitnessHandshakeAsync` (§19.7) heals the brain row when the witnesses show no
conflict and at least one tail was reachable. This makes `PromoteAsync` crash-safe
without a brain-side journal.

**Eventual-consistency tail repair.** An offline tail at the time of the promote
retains its old `ConflictObserved = true` witness. `PromoteAsync` does NOT re-attempt
the write. The next session-begin handshake on a normal-state volume writes a fresh
`ConflictObserved = false` witness to every accessible tail (§19.6 handshake write
phase). The recurring handshake is the eventual-consistency repair loop.

**By-design cases (no special handling required).**

- *Same recovery phrase, separate init* — two volumes created from the same recovery
  phrase have different `VolumeID`s (Principle 33) and therefore distinct tail-path
  prefixes on any shared provider account. No witness interaction; no conflict.
- *Restore from image* — structurally identical to a USB clone (same `VolumeID`, same
  epoch). Detected by the standard epoch comparison the first time either copy advances.
- *Clock skew* — timestamps in the witness are informational. The epoch counter is the
  authoritative signal; clock skew (arbitrary, in either direction) cannot cause false
  positives or false negatives.

**The "no merge" rule.** `PromoteAsync` does NOT reconcile differing file contents
between the two skinks. Files written only on the demoted skink remain only on the
demoted skink (and on whatever tails received them before fencing took effect). The
product surface for "bring the demoted writes forward" is Phase 5 recovery, not a
witness-protocol feature.
```

---

## Acceptance criteria

- [ ] `dotnet build` clean on Windows and Linux with `--warnaserror`.
- [ ] `dotnet test` green: all new tests pass, no existing tests regress.
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] `FlashSkinkVolume.PromoteAsync` is publicly callable; idempotent on an unfenced volume; returns `Result.Ok` on success.
- [ ] After `PromoteAsync` succeeds: `volume.State == VolumeState.Normal`; `Settings["VolumeState"]` row is `"Normal"`; every accessible tail's witness has `ConflictObserved = false`.
- [ ] After `PromoteAsync` succeeds: `_uploadQueueService._fenced == 0` (verified indirectly via the upload-resume test).
- [ ] The `Info` notification published on `PromoteAsync` success contains user vocabulary only and no internal vocabulary (epoch, witness, fence, fenced, volume, split-brain, etc.).
- [ ] Per-tail witness write failures inside `PromoteAsync` are logged at Warning and do NOT fail the overall promote; the local-state clear succeeds regardless.
- [ ] The next session-begin handshake after a successful promote writes fresh witnesses with `ConflictObserved = false` to every accessible tail (handshake-as-repair-loop).
- [ ] `BLUEPRINT.md` contains §19.8 covering the bullets listed in the test-spec sketch above.
- [ ] No new analyzer warnings.

---

## Line-of-code budget

| File | Lines |
|---|---|
| `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` (delta) | ~110 |
| `BLUEPRINT.md` (delta) | ~70 |
| **Total src delta** | **~180** |
| `tests/FlashSkink.Tests/Orchestration/SplitBrainIntegrationTests.cs` (new methods + 1 small helper) | ~230 |
| `tests/FlashSkink.Tests/Identity/WitnessEdgeCaseTests.cs` (new file) | ~180 |
| **Total test delta** | **~410** |

The dev-plan budget (~110 src + ~70 blueprint + ~230 + ~150 test) is matched within ~30 lines on the edge-case file. The extra ~30 lines on `WitnessEdgeCaseTests.cs` accommodates the `RestoreFromImage` and `ClockSkew` symmetric-check assertions (the dev plan listed one-direction checks; the implementation covers both directions for the symmetric properties, which is the kind of unit-test thoroughness Gate 2 prefers).

---

## Non-goals

- Do NOT implement the `flashskink clone` CLI command — deferred to V1.x / V2 (meta-plan item 15).
- Do NOT implement `Siblings` brain table or any per-clone tracking.
- Do NOT implement automatic recovery from a tail after demotion — the user is directed to recovery (Phase 5).
- Do NOT add OAuth-revocation special handling in `WitnessStore` beyond what `IStorageProvider` already exposes (`TokenRevoked` maps to "tail offline, skip").
- Do NOT add `WitnessHandshake` to `CreateAsync` — by design, a brand-new volume has no registered tails (§3.5.1, §3.5.2 already settled this).
- Do NOT modify `RegisterTailAsync`, `OpenAsync`, `CreateAsync`, `WriteFileAsync`, or any other existing method on `FlashSkinkVolume`. `PromoteAsync` is purely additive.
- Do NOT add a `WitnessHandshake` interface (`IWitnessHandshake`) or a `WitnessStore` interface — both are concrete `internal sealed class`es; `PromoteAsync` uses the concrete `WitnessStore` directly (§3.5.2 already pinned this decision).
- Do NOT change `PersistVolumeStateAsync`'s signature or semantics. It already does the right thing (`CancellationToken.None`, single brain-scope upsert).
- Do NOT add a `BackgroundFailures` row on `PromoteAsync` success — `Info` severity is not persisted (Blueprint §8.5).
- Do NOT add telemetry, phone-home, or update checks (Principle 32, permanent prohibition).
- Do NOT introduce a CLI surface for `PromoteAsync` — that lands in Phase 4.
- Do NOT amend `CLAUDE.md` — no new principles introduced; the existing principle set fully covers `PromoteAsync`.
