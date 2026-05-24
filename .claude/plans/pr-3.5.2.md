# PR 3.5.2 — Session-begin handshake and fenced state

**Branch:** `pr/3.5.2-session-begin-handshake`
**Blueprint sections:** §8.5–8.6 (BackgroundFailures persistence; Principle 24), §19.5 (single-instance lock — the handshake fires in the same window, after lock acquisition), §31.1 (VolumeID — used to validate the witness against the volume we think we opened). New sections §19.6 and §19.7 ship in this PR.
**Dev plan section:** [phase-3.5 §3.5.2](../../dev-plan/phase-3.5-volume-identity-and-clone-safety.md) — Session-begin handshake and fenced state.

---

## Scope

Wires the witness primitives from §3.5.1 into the volume-open lifecycle. After this PR:

- Every `OpenAsync` runs a session-begin **witness handshake** against every reachable tail before `BuildVolumeFromSessionAsync` returns the volume.
- A detected conflict — either a tail whose witness `Epoch >= newEpoch` (epoch comparison) or a tail whose witness carries `ConflictObserved = true` from a prior winner (conflict marker) — puts the volume into **fenced state**: `Settings["VolumeState"] = "Fenced"` is persisted, a `Critical` notification publishes, a `BackgroundFailures` row records `SplitBrainDetected`, and `UploadQueueService` idles all workers.
- Fenced state survives close/reopen via the persisted `Settings["VolumeState"]` row read by `BackfillAndStampOnOpenAsync` (§3.5.1).
- The **two-sided fence** propagates: whenever the volume is fenced (fresh detection or persisted), every handshake writes `ConflictObserved = true` to every accessible tail so the other skink's next handshake picks up the marker even if its own epoch is lower.
- `RegisterTailAsync` writes an **initial witness** to a freshly-registered tail so the witness contract holds from the first session, not just from the second open after registration.
- A new `FlashSkinkVolume.State` property exposes the live `VolumeState`.
- An **auto-downgrade** path handles the crash-recovery case where a `PromoteAsync` (§3.5.3) wrote fresh witnesses to all tails but crashed before persisting `Settings["VolumeState"] = "Normal"`: when the brain says Fenced but the handshake observes no conflict signals AND at least one tail was read, the persisted row is treated as stale and downgraded.
- BLUEPRINT.md gains §19.6 (witness protocol) and §19.7 (fenced state).

Out of scope for this PR — they land in §3.5.3:
- `FlashSkinkVolume.PromoteAsync` (resolution API)
- BLUEPRINT §19.8 (resolution flow doc)
- The `ErrorCode.VolumeFenced` return path from `PromoteAsync` race-guards (the enum value exists from §3.5.1; nothing returns it yet)

---

## Files to create

- `src/FlashSkink.Core/Identity/WitnessHandshake.cs` — ~210 lines. `internal sealed class` plus an `internal enum ConflictTrigger` and an `internal readonly record struct WitnessHandshakeOutcome`. Constructor takes `WitnessStore store, ILogger<WitnessHandshake> logger`. Primary entry point `RunAsync` implements the read-compare-write algorithm.
- `tests/FlashSkink.Tests/Identity/WitnessHandshakeTests.cs` — ~280 lines. Unit tests against `WitnessHandshake.RunAsync` using `FileSystemProvider` instances in temp dirs (plus `FaultInjectingStorageProvider` for unreachable-tail scenarios). One file, one test class.
- `tests/FlashSkink.Tests/Orchestration/SplitBrainIntegrationTests.cs` — ~430 lines. End-to-end tests via `FlashSkinkVolume.CreateAsync`/`OpenAsync`/`WriteFileAsync`/`RegisterTailAsync`, asserting the public observable behaviour (fenced `State`, blocked uploads, persisted state, notifications, BackgroundFailures rows). Uses `FileSystemProvider` registered via `RegisterTailAsync`.

## Files to modify

- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — five additive changes:
  1. New `public VolumeState State { get; private set; }` property; constructor accepts an `initiallyFenced` parameter and a `WitnessStore witnessStore` field reference (for §3.5.3's `PromoteAsync`).
  2. `OpenAsync`: after `BackfillAndStampOnOpenAsync` returns `(state, newEpoch)`, call the new `private static async Task<Result<VolumeState>> RunWitnessHandshakeAsync(...)` and forward the returned `VolumeState` into `BuildVolumeFromSessionAsync`.
  3. New `RunWitnessHandshakeAsync` static method (~120 lines including XML doc): reads `VolumeID` + active providers from the brain, resolves providers from the registry, runs `WitnessHandshake.RunAsync`, applies the four-state transition table (Normal+NoConflict, Normal+Fresh, Fenced+Fresh, Fenced+NoConflict-auto-downgrade), persists `Settings["VolumeState"]`, publishes notifications, and writes `BackgroundFailures` rows on initial detection.
  4. `BuildVolumeFromSessionAsync` signature widens with `bool initiallyFenced` (and constructs a `WitnessStore` + an `ILogger<WitnessHandshake>` for storage on the volume); passes `initiallyFenced` into the `UploadQueueService` constructor.
  5. `RegisterTailAsync`: after the brain insert + in-memory `Register` succeed and BEFORE the wakeup pulse, read the current `VolumeID`/`VolumeEpoch`/`VolumeState` from the brain, construct a fresh `WitnessPayload` (with `ConflictObserved` matching the current `State`), and call `_witnessStore.WriteAsync(provider, _session.Dek, payload, CancellationToken.None)`. A write failure logs at Warning and continues — the brain row is committed, the registry has the provider, and the next handshake retries.

- `src/FlashSkink.Core/Upload/UploadQueueService.cs` — two additive changes:
  1. Constructor gains a new `bool initiallyFenced` parameter (slotted between `skinkRoot` and `logger`) and a `private volatile int _fenced` field, initialised in the constructor body.
  2. New `internal void SetFenced(bool fenced) => Interlocked.Exchange(ref _fenced, fenced ? 1 : 0);` plus a fenced-guard `if (_fenced != 0) { … continue; }` block at the top of the active body of both `OrchestratorAsync` and `WorkerAsync` (after the `_networkMonitor.IsAvailable` check, before any brain or provider work). The guard's idle uses `IdleAsync(OrchestratorIdle/WorkerIdle, ct)` so it wakes on a pulse — `PromoteAsync` (§3.5.3) pulses the wakeup signal so workers resume promptly when the fence clears.

- `BLUEPRINT.md` — add two subsections under §19, between §19.5 and the §20 header (insert at line 2219, before the `---` separator):
  - **§19.6 — Witness protocol** (~50 lines)
  - **§19.7 — Fenced state** (~40 lines)

## Dependencies

- NuGet: none added.
- Project references: none added.

---

## Public API surface

One new public property on `FlashSkinkVolume`:

```csharp
/// <summary>
/// The current operational state of this volume. <see cref="VolumeState.Fenced"/> indicates
/// that a split-brain conflict was detected during the session-begin handshake; Phase 2
/// uploads are blocked until resolved by <c>PromoteAsync</c> (dev plan §3.5.3). Read-only
/// to callers; transitions to <see cref="VolumeState.Fenced"/> happen inside
/// <c>OpenAsync</c> when the handshake detects a fresh conflict, and the value persists
/// across close/reopen via <c>Settings["VolumeState"]</c>. (Blueprint §19.7.)
/// </summary>
public VolumeState State { get; private set; }
```

No new public types. `WitnessHandshake`, `WitnessHandshakeOutcome`, and `ConflictTrigger` are all `internal` per Phase 3.5 scope (the §3.5.1 PR's tests use `[InternalsVisibleTo]` to reach the existing internals, and `SplitBrainIntegrationTests` does the same).

`UploadQueueService`'s new constructor parameter (`bool initiallyFenced`) is a public API change to a public type. It does not break callers — `FlashSkinkVolume.BuildVolumeFromSessionAsync` is the sole construction site in the codebase. The new `SetFenced` method is `internal`.

---

## Internal types

### `FlashSkink.Core.Identity.ConflictTrigger` (internal enum)

```csharp
internal enum ConflictTrigger
{
    None = 0,
    EpochComparison = 1,
    ConflictMarker = 2,
}
```

XML doc explains the two parallel detection paths and that `None` paired with `ConflictDetected = false` is the no-conflict outcome.

### `FlashSkink.Core.Identity.WitnessHandshakeOutcome` (internal readonly record struct)

```csharp
internal readonly record struct WitnessHandshakeOutcome(
    bool ConflictDetected,
    ConflictTrigger Trigger,
    string? ConflictingProviderId,
    WitnessPayload? ConflictWitness,
    int TailsRead,
    int TailsWritten);
```

`ConflictDetected` reflects ONLY the fresh-conflict signal from this handshake (`Trigger != None`). The caller composes that with the persisted `currentState` to decide whether to enter, remain in, or downgrade from fenced state. `TailsRead` counts tails whose `WitnessStore.TryReadAsync` returned `Ok(...)` (regardless of payload nullness); `TailsWritten` counts tails where the witness write succeeded. The auto-downgrade decision in the caller uses `TailsRead > 0` as the "positive evidence" gate.

### `FlashSkink.Core.Identity.WitnessHandshake` (internal sealed class)

```csharp
internal sealed class WitnessHandshake
{
    internal WitnessHandshake(WitnessStore store, ILogger<WitnessHandshake> logger);

    internal Task<Result<WitnessHandshakeOutcome>> RunAsync(
        IReadOnlyList<(string ProviderId, IStorageProvider Provider)> tails,
        string volumeId,
        long newEpoch,
        ReadOnlyMemory<byte> dek,
        bool alreadyFenced,
        CancellationToken ct);
}
```

---

## Method-body contracts

### `WitnessHandshake.RunAsync(tails, volumeId, newEpoch, dek, alreadyFenced, ct)`

**Preconditions:** `tails` non-null (may be empty — handshake returns `Ok(no-conflict, no-writes)` without iterating); `volumeId` non-null/non-empty; `newEpoch >= 1`; `dek.Length == 32`.

**Algorithm:**

1. `ct.ThrowIfCancellationRequested()`.
2. Initialise locals: `bool conflictDetected = false; ConflictTrigger trigger = ConflictTrigger.None; string? conflictingProviderId = null; WitnessPayload? conflictWitness = null; int tailsRead = 0;`
3. **Read phase.** Iterate `tails` sequentially. For each `(providerId, provider)`:
   - `var readResult = await _store.TryReadAsync(provider, dek, ct);`
   - If `!readResult.Success`: log at Debug ("Witness read failed on tail {ProviderId}: {Code}"); skip to next tail (do NOT increment `tailsRead`). Errors here can only be `Cancelled` (propagate via re-throw of the original exception) or `Unknown` (skip — this tail provides no information).
     - Cancellation handling: the outer try/catch at method scope catches `OperationCanceledException` and returns `Fail(Cancelled)`. The `Unknown` mapping here is silent-skip plus Debug log; counted as "not read" for auto-downgrade evidence purposes.
   - Increment `tailsRead++`.
   - `var payload = readResult.Value;`
   - If `payload is null`: this tail has no information. Continue.
   - **Volume-ID validation:** if `payload.Value.VolumeId != volumeId`: log at Warning ("Witness on tail {ProviderId} carries VolumeId={WitnessVolumeId}, expected {ExpectedVolumeId}; treating as no information."); continue. The ciphertext-binding-to-key guarantee makes this near-impossible in practice (a mismatched DEK would have failed decrypt at the store layer), but the defensive check matches Blueprint §19.6's documented behaviour.
   - **Trigger A — epoch comparison:** if `payload.Value.Epoch >= newEpoch` AND `!conflictDetected`:
     - `conflictDetected = true; trigger = ConflictTrigger.EpochComparison; conflictingProviderId = providerId; conflictWitness = payload;`
   - **Trigger B — conflict marker:** else if `payload.Value.Epoch < newEpoch` AND `payload.Value.ConflictObserved` AND `!conflictDetected`:
     - `conflictDetected = true; trigger = ConflictTrigger.ConflictMarker; conflictingProviderId = providerId; conflictWitness = payload;`
   - Note: the `&& !conflictDetected` clauses implement the documented "first-trigger-wins" rule for the `ConflictingProviderId` / `ConflictWitness` fields. The loop does NOT break early — every tail is still read so `tailsRead` reflects the full reachable set (needed by the auto-downgrade gate downstream).
4. **Write phase.** Decide the payload's `ConflictObserved` value:
   - If `conflictDetected || alreadyFenced` → `ConflictObserved = true`.
   - Else → `ConflictObserved = false`.

   Build a single payload via `WitnessPayload.ForNewSession(volumeId, newEpoch, appVersion)` followed by `payload = payload with { ConflictObserved = … }` to encode the value. (`appVersion` is sourced from the same `GetAppInformationalVersion()` helper used by `FlashSkinkVolume` — the handshake gets the value passed in. **Refinement:** add `string appVersion` as an additional parameter to `RunAsync`, slotted between `newEpoch` and `dek`. The caller has the value from `BackfillAndStampOnOpenAsync`'s context; this avoids the handshake type taking a hard reference to the volume's private helper.)
5. Write the payload to every tail via `await _store.WriteAsync(provider, dek, payload, CancellationToken.None)` (Principle 17 — once we've decided to write, cancellation no longer applies; the witness write must complete). Count successes into `int tailsWritten`. A write failure is logged at Warning (`"Witness write failed on tail {ProviderId}: {Code}"`) and counted separately; it is NOT a split-brain — it is an upload problem, and the volume proceeds normally.
6. Return `Result<WitnessHandshakeOutcome>.Ok(new WitnessHandshakeOutcome(conflictDetected, trigger, conflictingProviderId, conflictWitness, tailsRead, tailsWritten))`.

**Catch ordering:**
- `OperationCanceledException ex → Result.Fail(ErrorCode.Cancelled, "Witness handshake was cancelled.", ex)` — first.
- `Exception ex → Result.Fail(ErrorCode.Unknown, "Unexpected error during witness handshake.", ex)` — last; logged at Warning at the construction site.

**Principle 1 — never throws across the public API.** All exceptions are caught and converted to `Result.Fail`. The internal-only nature of this type makes the boundary discipline a matter of consistency, not a strict requirement.

### `FlashSkinkVolume.RunWitnessHandshakeAsync(session, skinkRoot, options, newEpoch, currentState, ct)` (new private static)

**Signature:**

```csharp
private static async Task<Result<VolumeState>> RunWitnessHandshakeAsync(
    VolumeSession session,
    string skinkRoot,
    VolumeCreationOptions options,
    long newEpoch,
    VolumeState currentState,
    CancellationToken ct)
```

**Behaviour:**

1. `ct.ThrowIfCancellationRequested()`.
2. **Resolve registry.** If `options.ProviderRegistry` is null, this open path will use a fresh registry inside `BuildVolumeFromSessionAsync` — but for the handshake itself, an unconfigured registry has no tails, so the handshake observes zero tails and returns no-conflict. Mirror the same fallback locally: `var registry = options.ProviderRegistry ?? new InMemoryProviderRegistry(options.LoggerFactory.CreateLogger<InMemoryProviderRegistry>());`. (Cross-cutting decision 8: in Phase 3.5 tests a registry MUST be passed via options; in production Phase 4's `BrainBackedProviderRegistry` populates from the brain. The local fallback exists for parity with `BuildVolumeFromSessionAsync` so the open succeeds against a brand-new volume with no registered tails.)
3. **Read VolumeID and active providers from the brain.** Use Dapper through `session.Brain.LockAsync(ct)` (Principle 36):
   ```csharp
   string volumeId;
   IReadOnlyList<string> activeProviderIds;
   using (var scope = await session.Brain!.LockAsync(ct).ConfigureAwait(false))
   {
       volumeId = await scope.Connection.QuerySingleAsync<string>(
           new CommandDefinition(
               "SELECT Value FROM Settings WHERE Key = 'VolumeID'",
               cancellationToken: ct)).ConfigureAwait(false);
       var ids = await scope.Connection.QueryAsync<string>(
           new CommandDefinition(
               "SELECT ProviderID FROM Providers WHERE IsActive = 1",
               cancellationToken: ct)).ConfigureAwait(false);
       activeProviderIds = ids.AsList();
   }
   ```
   Both reads are not hot-path (Principle 22 — Dapper here is fine).
4. **Resolve providers.** For each `providerId` in `activeProviderIds`, call `await registry.GetAsync(providerId, ct)`. Providers that return `Fail(ProviderUnreachable)` (not registered in the in-memory registry — normal for tests that haven't called `RegisterTailAsync` yet, and normal for Phase 4 brain-backed registries that may fail to reconstruct a cloud adapter) are skipped with a Debug log. Build `List<(string, IStorageProvider)> tails` from the successful resolutions.
5. **Construct ephemeral handshake.** `var store = new WitnessStore(options.LoggerFactory.CreateLogger<WitnessStore>()); var handshake = new WitnessHandshake(store, options.LoggerFactory.CreateLogger<WitnessHandshake>());`
6. **Invoke handshake.** `var outcome = await handshake.RunAsync(tails, volumeId, newEpoch, GetAppInformationalVersion(), new ReadOnlyMemory<byte>(session.Dek!), alreadyFenced: currentState == VolumeState.Fenced, ct).ConfigureAwait(false);` If `!outcome.Success`, propagate via `Result<VolumeState>.Fail(outcome.Error!)`.
7. **Transition table.** Combine `outcome.Value.ConflictDetected` with `currentState`:

   - **(Fresh conflict, currently Normal) — enter Fenced.**
     - Persist `Settings["VolumeState"] = "Fenced"` inside a `session.Brain.LockAsync` scope using `INSERT OR REPLACE INTO Settings (Key, Value) VALUES ('VolumeState', 'Fenced')`. Use `CancellationToken.None` for the upsert (Principle 17 — once we've decided to fence, the persist must complete).
     - Log at Error (Principle 27 — log at the construction site of the Fail-equivalent state transition; this is a state transition not a `Result.Fail`, but the spirit applies): `_logger.LogError("Split-brain detected on tail {ProviderId} via {Trigger}; volume entering fenced state. WitnessEpoch={WitnessEpoch}, LocalEpoch={LocalEpoch}.", outcome.Value.ConflictingProviderId, outcome.Value.Trigger, outcome.Value.ConflictWitness?.Epoch, newEpoch);` — **but** `RunWitnessHandshakeAsync` is `static` and has no logger field. **Resolution:** the static method receives `options.LoggerFactory` already; construct an inline logger: `var logger = options.LoggerFactory.CreateLogger(typeof(FlashSkinkVolume));`.
     - Publish a `Critical` notification (user-vocabulary only — Principle 25):
       ```csharp
       await options.NotificationBus.PublishAsync(new Notification
       {
           Source = "FlashSkinkVolume",
           Severity = NotificationSeverity.Critical,
           Title = "Conflicting copy detected",
           Message = "FlashSkink found a conflicting copy of this skink on one of your tails. Uploads are paused until you resolve the conflict.",
           Error = new ErrorContext
           {
               Code = ErrorCode.SplitBrainDetected,
               Message = $"Conflict detected on tail '{outcome.Value.ConflictingProviderId}'. {outcome.Value.ConflictWitness?.ToDisplayString()}",
               Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
               {
                   ["TailProviderID"] = outcome.Value.ConflictingProviderId ?? string.Empty,
                   ["ConflictTrigger"] = outcome.Value.Trigger.ToString(),
                   ["WitnessEpoch"] = outcome.Value.ConflictWitness?.Epoch.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                   ["LocalEpoch"] = newEpoch.ToString(CultureInfo.InvariantCulture),
                   ["WitnessHost"] = outcome.Value.ConflictWitness?.Host ?? string.Empty,
                   ["WitnessSessionId"] = outcome.Value.ConflictWitness?.SessionId ?? string.Empty,
                   ["ConflictObserved"] = outcome.Value.ConflictWitness?.ConflictObserved.ToString() ?? string.Empty,
               },
           },
           OccurredUtc = DateTime.UtcNow,
           RequiresUserAction = true,
       }, CancellationToken.None).ConfigureAwait(false);
       ```
     - Write a `BackgroundFailures` row via an ephemeral `BackgroundFailureRepository(session.Brain, options.LoggerFactory.CreateLogger<BackgroundFailureRepository>())` (the repository is `public sealed class`, no project-reference concerns — it already lives in `FlashSkink.Core.Metadata`). The row's `Source = "FlashSkinkVolume"`, `ErrorCode = nameof(ErrorCode.SplitBrainDetected)`, `Message` uses the same user-vocabulary message as the notification, `Metadata` is null (the structured metadata above is on the notification — `BackgroundFailures.Metadata` is a free-form JSON string, not the same shape).
     - Return `Result<VolumeState>.Ok(VolumeState.Fenced)`.

   - **(Fresh conflict, currently Fenced) — remain Fenced, no spam.**
     - No `Settings` write (already Fenced). No notification. No `BackgroundFailures` row. Log at Warning for diagnostics: `_logger.LogWarning("Volume already fenced; handshake observed another fresh trigger on tail {ProviderId} via {Trigger}.", ...);`
     - Return `Result<VolumeState>.Ok(VolumeState.Fenced)`.

   - **(No fresh conflict, currently Fenced) — auto-downgrade check.**
     - If `outcome.Value.TailsRead > 0`: positive evidence that the witnesses are clean. Persist `Settings["VolumeState"] = "Normal"` (same Dapper upsert pattern with `CancellationToken.None`). Log at Information: `_logger.LogInformation("Auto-downgraded stale Fenced state — handshake observed {TailsRead} tail(s) with no conflict signal. A prior promote likely completed the tail writes before persisting the local state change.", outcome.Value.TailsRead);`. Publish an `Info` notification: `Title = "Conflict resolved", Message = "FlashSkink finished resolving a prior conflict; uploads are resuming."` (user vocabulary). No `BackgroundFailures` row (Info severity is not persisted — see Blueprint §8.5). Return `Result<VolumeState>.Ok(VolumeState.Normal)`.
     - Else (`TailsRead == 0`): no reachable tails to confirm against; keep Fenced. Log at Debug. Return `Result<VolumeState>.Ok(VolumeState.Fenced)`.

   - **(No fresh conflict, currently Normal) — ordinary path.**
     - No state change. No notification. No `BackgroundFailures` row. Return `Result<VolumeState>.Ok(VolumeState.Normal)`.

**Catch ordering:**
- `OperationCanceledException ex → Result<VolumeState>.Fail(ErrorCode.Cancelled, "Witness handshake was cancelled.", ex)` — first.
- `SqliteException ex → Result<VolumeState>.Fail(ErrorCode.DatabaseWriteFailed, "Failed to persist volume state during witness handshake.", ex)` (specific catch for the brain-write failures above).
- `Exception ex → Result<VolumeState>.Fail(ErrorCode.Unknown, "Unexpected error during witness handshake.", ex)` — last.

### `FlashSkinkVolume.RegisterTailAsync` (modified — witness write addition)

After the existing brain-insert path commits the `Providers` row and `inMem.Register(providerId, provider)` runs successfully, BEFORE `_wakeupSignal.Pulse()`:

1. Read the current `VolumeID`, `VolumeEpoch`, `VolumeState` from the brain — a single Dapper query inside a brain scope:
   ```csharp
   string volumeId;
   long currentEpoch;
   VolumeState currentState;
   using (var scope = await _context.Brain.LockAsync(ct).ConfigureAwait(false))
   {
       volumeId = await scope.Connection.QuerySingleAsync<string>(
           new CommandDefinition(
               "SELECT Value FROM Settings WHERE Key = 'VolumeID'",
               cancellationToken: ct)).ConfigureAwait(false);
       var epochRaw = await scope.Connection.QuerySingleAsync<string>(
           new CommandDefinition(
               "SELECT Value FROM Settings WHERE Key = 'VolumeEpoch'",
               cancellationToken: ct)).ConfigureAwait(false);
       currentEpoch = long.Parse(epochRaw, CultureInfo.InvariantCulture);
       var stateRaw = await scope.Connection.QuerySingleOrDefaultAsync<string?>(
           new CommandDefinition(
               "SELECT Value FROM Settings WHERE Key = 'VolumeState'",
               cancellationToken: ct)).ConfigureAwait(false);
       currentState = stateRaw is not null
           && Enum.TryParse<VolumeState>(stateRaw, ignoreCase: false, out var parsed)
               ? parsed
               : VolumeState.Normal;
   }
   ```
2. Build the witness payload and write it (`CancellationToken.None` per Principle 17 — once the brain row is committed and the registry has the adapter, the witness write is the atomic-commit step):
   ```csharp
   var payload = WitnessPayload.ForNewSession(volumeId, currentEpoch, GetAppInformationalVersion())
       with { ConflictObserved = currentState == VolumeState.Fenced };
   var writeResult = await _witnessStore.WriteAsync(
       provider, _session.Dek, payload, CancellationToken.None).ConfigureAwait(false);
   if (!writeResult.Success)
   {
       _registerTailLogger.LogWarning(
           "Initial witness write to newly-registered tail {ProviderId} failed ({Code}); the next session-begin handshake will retry.",
           providerId, writeResult.Error!.Code);
   }
   ```
   Note: there is no existing `_logger` field on `FlashSkinkVolume`. Either thread a new `_logger` field through the constructor (preferred — multiple consumers in this PR), or use the `options.LoggerFactory`-derived logger via a new private field. **Decision:** add `private readonly ILogger<FlashSkinkVolume> _logger;` constructor parameter (it's a trivial threading), pulled from `loggerFactory.CreateLogger<FlashSkinkVolume>()` in `BuildVolumeFromSessionAsync`. Used for the `RegisterTailAsync` witness-write warning and any other instance-level logging this PR introduces.
3. Continue with `_wakeupSignal.Pulse()` and `return Result.Ok();` exactly as before. The witness write does NOT change the return value of `RegisterTailAsync` — a `Warning` log is the entire failure surface.

The witness write happens INSIDE the gate (the existing method already holds `_gate`), which is correct: it is a small synchronous-ish step that piggybacks on the brain-insert critical section.

### `UploadQueueService.SetFenced(bool fenced)` and worker guards

```csharp
internal void SetFenced(bool fenced)
    => Interlocked.Exchange(ref _fenced, fenced ? 1 : 0);
```

In `OrchestratorAsync`, after the `_networkMonitor.IsAvailable` check that calls `IdleAsync(OrchestratorIdle, ct)`, add:

```csharp
if (_fenced != 0)
{
    // Volume is fenced — skip all upload activity until PromoteAsync clears the flag.
    // No notification per tick; the fenced notification was published on the session
    // open that detected the conflict (Principle 24). PromoteAsync (§3.5.3) pulses
    // the wakeup signal so we exit IdleAsync promptly when the fence clears.
    await IdleAsync(OrchestratorIdle, ct).ConfigureAwait(false);
    continue;
}
```

In `WorkerAsync`, the same block (using `WorkerIdle`) is added at the analogous position. The fenced guard runs BEFORE `_providerRegistry.GetAsync` and `_uploadQueueRepository.DequeueNextBatchAsync`, so a fenced volume issues zero brain reads and zero provider calls per tick.

`IdleAsync` (rather than `_clock.Delay`) is used so a `_wakeupSignal.Pulse()` from `PromoteAsync` wakes the loop immediately — without that, an unfenced volume would wait up to a full poll interval before resuming.

---

## Integration points

- **`FlashSkinkVolume.OpenAsync`** — after `BackfillAndStampOnOpenAsync`, call `RunWitnessHandshakeAsync`. The handshake runs BEFORE `BuildVolumeFromSessionAsync` so a fenced state is known before any background service starts and `UploadQueueService` receives the correct `initiallyFenced` value in its constructor. The `session` variable is still owned by the finally block at this point — a handshake failure cleanly disposes the session and releases the lock via the existing finally.
- **`FlashSkinkVolume.CreateAsync`** — does NOT run the handshake. Rationale: a brand-new volume has zero registered tails (the `Providers` table is empty until `RegisterTailAsync` is called), so the handshake has nothing to do. `RegisterTailAsync` writes the first witness to each tail as it is registered, establishing the contract from the first session onward.
- **`BuildVolumeFromSessionAsync`** — gains `bool initiallyFenced` parameter; constructs `WitnessStore` (passes to `FlashSkinkVolume` constructor for §3.5.3); passes `initiallyFenced` to the `UploadQueueService` constructor.
- **`FlashSkinkVolume.RegisterTailAsync`** — adds a witness write between the in-memory `Register` and the wakeup pulse. The write reads the current `VolumeID`/`VolumeEpoch`/`VolumeState` from the brain (Dapper inside a brain scope).
- **`UploadQueueService`** — constructor change + fenced-guard block in both orchestrator and worker loops.
- **`IStorageProvider`** — no changes. Witness I/O uses the existing upload-session triplet and `ListAsync`/`DownloadAsync`/`DeleteAsync`.
- **`BackgroundFailureRepository`** — used as-is. `RunWitnessHandshakeAsync` constructs an ephemeral instance and calls `AppendAsync`.
- **`INotificationBus`** — used as-is. `RunWitnessHandshakeAsync` publishes `Critical` (for fresh detection) and `Info` (for auto-downgrade) notifications.

---

## Principles touched

- **Principle 1** (Core never throws across the public API) — `WitnessHandshake.RunAsync` returns `Result<WitnessHandshakeOutcome>`; `RunWitnessHandshakeAsync` returns `Result<VolumeState>`. Both methods catch `OperationCanceledException` first and `Exception` last. The new public `State` property is a plain getter — cannot throw.
- **Principle 13** (`CancellationToken ct` last) — both new methods end with `CancellationToken ct`.
- **Principle 14** (`OperationCanceledException` first catch) — both new methods catch it first and map to `ErrorCode.Cancelled` logged at Debug/Information (not Error — cancellation is not a fault).
- **Principle 15** (no bare `catch (Exception)` as the only catch) — `RunWitnessHandshakeAsync` catches `OperationCanceledException`, `SqliteException`, and `Exception` (in that order); `WitnessHandshake.RunAsync` catches `OperationCanceledException` then `Exception`.
- **Principle 16** (dispose partially-constructed resources) — n/a (no new resource acquisition in either method beyond what `WitnessStore.WriteAsync` already handles via its own `AbortUploadAsync` finally).
- **Principle 17** (`CancellationToken.None` literal in compensation paths) — the witness writes in step 5 of `WitnessHandshake.RunAsync`, the `Settings["VolumeState"]` upsert in `RunWitnessHandshakeAsync`, the notification publish, the `BackgroundFailures` write, and the witness write in `RegisterTailAsync` all use `CancellationToken.None` spelled out as a literal. Tests assert the witness write is observable after pre-cancelling the outer `ct`.
- **Principle 22** (Dapper outside hot paths) — `RunWitnessHandshakeAsync` reads `Settings["VolumeID"]` and the `Providers` table via Dapper; `RegisterTailAsync` reads `VolumeID`/`VolumeEpoch`/`VolumeState` via Dapper. Both are session-open scope, not on the upload hot path.
- **Principle 24** (no silent background failures) — fresh-conflict detection publishes a `Critical` notification AND writes a `BackgroundFailures` row. The combination guarantees user-visibility on next launch even if the session crashes before any UI displays the notification. Subsequent re-detections while already Fenced do NOT re-publish (avoid spam; the user has been told once). Auto-downgrade publishes an `Info` notification (not persisted per Blueprint §8.5).
- **Principle 25** (appliance vocabulary) — every user-facing string in this PR uses skink / tail / copy / conflict / resolve / paused / resume vocabulary only. The strings "epoch", "witness", "split-brain", "split brain", "volume", "fence", "fenced", "stripe", "WAL", "DEK", "AAD" do NOT appear in any `Notification.Title`, `Notification.Message`, or `BackgroundFailure.Message`. The `ErrorCode.SplitBrainDetected` enum value is internal-vocabulary and acceptable (it does not appear in user-visible strings).
- **Principle 26** (no secrets in logs) — `ErrorContext.Metadata` keys are `TailProviderID`, `ConflictTrigger`, `WitnessEpoch`, `LocalEpoch`, `WitnessHost`, `WitnessSessionId`, `ConflictObserved`. None match `*Token`, `*Key`, `*Password`, `*Secret`, `*Mnemonic`, `*Phrase`. No DEK bytes, no envelope bytes, no plaintext bytes are logged anywhere.
- **Principle 27** (Core logs at the construction site of the Fail) — `RunWitnessHandshakeAsync` logs at Error when transitioning Normal→Fenced (state-transition equivalent of constructing a Fail); `UploadQueueService`'s fenced-guard skip is silent per tick (only the initial detection logs); `WitnessHandshake.RunAsync` logs at Debug/Warning per tail as appropriate.
- **Principle 28** (Core depends only on MEL abstractions) — all new logging uses `ILogger<T>` from `Microsoft.Extensions.Logging.Abstractions`. No Serilog reference.
- **Principle 31** (keys zeroed on volume close) — the DEK passed to `WitnessHandshake.RunAsync` and `WitnessStore.WriteAsync` is a per-call `ReadOnlyMemory<byte>` view over `session.Dek`. No long-lived key references are held by `WitnessHandshake` or `WitnessHandshakeOutcome`. `_session.Dek` continues to be the single authoritative storage; existing volume teardown zeros it.
- **Principle 33** (Volume identity is a random GUID, never derived from recovery-phrase material) — the handshake reads `Settings["VolumeID"]` (a Guid written by `CreateAsync`/`BackfillAndStampOnOpenAsync`) and passes it through to the witness payload's `VolumeId` field for validation. No new derivation paths introduced.
- **Principle 35** (Single-instance per volume) — the handshake runs INSIDE the single-instance lock window (between `InstanceLock.AcquireAsync` and `BuildVolumeFromSessionAsync`). A second concurrent open against the same skink root would have failed at `InstanceLock` already, so the handshake is single-writer for its duration on a given host. The cross-host network-mounted USB race is documented as a known limitation in Blueprint §19.6.
- **Principle 36** (Brain SQL flows through `IBrainAccess`) — `RunWitnessHandshakeAsync` and `RegisterTailAsync` both use `session.Brain.LockAsync(ct)` / `_context.Brain.LockAsync(ct)` to acquire scopes; raw `SqliteConnection` is never injected into `WitnessHandshake`.

---

## Test spec

### `tests/FlashSkink.Tests/Identity/WitnessHandshakeTests.cs` — class `WitnessHandshakeTests : IDisposable`

Constructor creates a unique temp dir per test; `Dispose` recursively deletes it. Uses real `WitnessStore` instances backed by one or more `FileSystemProvider`s rooted at sub-directories of the temp dir (plus optional `FaultInjectingStorageProvider` wrappers). DEK is 32 random bytes per test; `volumeId` is a fresh Guid per test; `appVersion` is the literal string `"test-1.0.0"`.

Helper: `private async Task SeedWitnessAsync(IStorageProvider provider, string volumeId, long epoch, bool conflictObserved)` — uses a `WitnessStore` to pre-populate a tail with a payload of the requested shape.

- `RunAsync_NoTails_ReturnsNoConflictAndNoWrites` — empty `tails` list; outcome `ConflictDetected = false`, `TailsRead = 0`, `TailsWritten = 0`.
- `RunAsync_NoPriorWitness_WritesWitness_ReturnsNoConflict` — one fresh tail; outcome `ConflictDetected = false`, `TailsRead = 1`, `TailsWritten = 1`; reading the tail's witness back shows `Epoch == newEpoch`, `ConflictObserved == false`.
- `RunAsync_WitnessEpochLower_ReturnsNoConflict_WritesNewEpoch` — seed witness with `Epoch = 3`; pass `newEpoch = 5`; outcome `ConflictDetected = false`; tail now carries `Epoch = 5`, `ConflictObserved = false`.
- `RunAsync_WitnessEpochEqual_ReturnsConflict` — seed `Epoch = 5`; pass `newEpoch = 5`; outcome `ConflictDetected = true`, `Trigger = EpochComparison`; tail now carries `Epoch = 5` (the handshake's epoch), `ConflictObserved = true`.
- `RunAsync_WitnessEpochHigher_ReturnsConflict` — seed `Epoch = 7`; pass `newEpoch = 5`; outcome `ConflictDetected = true`, `Trigger = EpochComparison`.
- `RunAsync_ConflictMarkerLowerEpoch_ReturnsConflict` — seed `Epoch = 2, ConflictObserved = true`; pass `newEpoch = 5`; outcome `ConflictDetected = true`, `Trigger = ConflictMarker`. **This is the meta-plan item 12 direct test — the lagger detection half of the two-sided fence.** Without this trigger, the lagger would never learn it was the lagger.
- `RunAsync_ConflictDetected_WritesWitnessWithConflictObservedTrue` — after any conflict-detecting run, every reachable tail's witness has `ConflictObserved = true`.
- `RunAsync_AlreadyFenced_NoConflictOnTail_WritesConflictObservedTrue` — seed `Epoch = 1, ConflictObserved = false`; pass `newEpoch = 5, alreadyFenced = true`; outcome `ConflictDetected = false, Trigger = None`; tail now carries `Epoch = 5, ConflictObserved = true`.
- `RunAsync_AlreadyFenced_NoTails_WritesNothing` — empty `tails`, `alreadyFenced = true`; outcome `ConflictDetected = false`, `TailsWritten = 0`.
- `RunAsync_UnreachableTail_SkipsAndReturnsNoConflict` — `FaultInjectingStorageProvider.FailNextListWith(ProviderUnreachable)`; outcome `ConflictDetected = false`, `TailsRead = 0`, `TailsWritten = 0` (write would also fail — the FaultInjectingStorageProvider's fault knobs are single-shot per call, so configure the write to succeed and assert `TailsWritten = 1` instead; OR configure both read AND write to fail and assert `TailsRead = 0, TailsWritten = 0`). **Decision:** test ONLY the read-fail path and let the write succeed (simpler assertion); a second test below covers write-fail.
- `RunAsync_WriteFailureToOneTail_LoggedNotFatal` — two tails, one with a `FaultInjectingStorageProvider` configured to fail `BeginUploadAsync`. Outcome `ConflictDetected = false`, `TailsRead = 2`, `TailsWritten = 1`.
- `RunAsync_PartiallyUnreachable_ConflictsOnReachableTail_ReturnsConflict` — two tails: one unreachable (List fails), one with `Epoch = 99`. Outcome `ConflictDetected = true, Trigger = EpochComparison, ConflictingProviderId = (the reachable tail's id)`.
- `RunAsync_FirstTriggerWins_DeterministicConflictAttribution` — three tails, all with `Epoch = 99`. Outcome `ConflictingProviderId = tails[0].ProviderId`. Re-run with reversed `tails` order: `ConflictingProviderId = (the new tails[0])`. The point of the test is that the iteration order alone determines attribution.
- `RunAsync_VolumeIdMismatch_SkipsTail_NoConflict` — seed witness with a DIFFERENT `volumeId` (random Guid, distinct from the handshake's `volumeId`); outcome `ConflictDetected = false, TailsRead = 1, TailsWritten = 1` (the tail's witness is still rewritten with the correct VolumeId).
- `RunAsync_CancelledBeforeStart_ReturnsCancelled` — pre-cancelled CTS; outcome `Fail(Cancelled)`.
- `RunAsync_CancellationDuringReads_ReturnsCancelled` — Use a `FaultInjectingStorageProvider` whose first `ListAsync` blocks until a CTS is cancelled (need to add `SetListLatency(TimeSpan)` to the fault provider? OR simpler — use an in-test `FailNextListWith(ErrorCode.Cancelled)` to simulate the inner provider observing cancellation, then verify outcome `Fail(Cancelled)`). **Decision:** keep it simple — pre-cancel the CTS and verify the entry-point check fires. Drop this test as redundant with the previous one.

### `tests/FlashSkink.Tests/Orchestration/SplitBrainIntegrationTests.cs` — class `SplitBrainIntegrationTests : IDisposable`

Constructor creates a unique skink root in `Path.GetTempPath()` and a sibling tail-root directory; `Dispose` recursively deletes both. Constructs a single `InMemoryProviderRegistry` and passes it via `VolumeCreationOptions.ProviderRegistry` to every `CreateAsync`/`OpenAsync` call (per cross-cutting decision 8). Notification capture: a small in-test handler that subscribes to `INotificationBus` and records every published `Notification` into a thread-safe list.

Password: `"test-password-12345"` (matching `VolumeIdentityTests`).

Helper: `CreateRegisteredVolumeAsync` — `CreateAsync` → `RegisterTailAsync(tailProviderId, "filesystem", "Test Tail", null, fileSystemProvider)` → returns the open volume and the `FileSystemProvider`. The registered tail's initial witness is written by `RegisterTailAsync` (the new behaviour in this PR).

Helper: `WriteFreshWitnessToTailAsync(provider, dek, volumeId, epoch, conflictObserved)` — direct `WitnessStore` write, used to set up split-brain test conditions.

Tests:

- `RegisterTailAsync_WritesInitialWitness_OnRegistration` — register a tail; without reopening, manually read `_witness/current.enc` from the tail's filesystem (or via `WitnessStore.TryReadAsync` on a fresh DEK derived from the receipt's vault). Assert: a witness exists, `Epoch` equals `Settings["VolumeEpoch"]` of the brain, `ConflictObserved == false`.
- `OpenAsync_NoConflict_State_IsNormal` — create, register, write a file, dispose, reopen. Assert `volume.State == VolumeState.Normal` after reopen.
- `OpenAsync_DetectsEpochConflict_VolumeFenced` — create + register + dispose. Manually overwrite the tail's `_witness/current.enc` with a witness carrying `Epoch = 99`. Reopen. Assert `volume.State == VolumeState.Fenced`.
- `OpenAsync_DetectsConflictMarker_VolumeFenced` — create + register + dispose (epoch 2, witness written). Manually overwrite the tail's witness with `Epoch = 1, ConflictObserved = true` (the lagger-detection scenario). Reopen (epoch increments to 3). Assert `volume.State == VolumeState.Fenced`. (Tests Trigger B end-to-end.)
- `OpenAsync_SplitBrain_PublishesCriticalNotification` — same setup as the epoch-conflict test; assert the notification list contains one `Critical` notification with `Source = "FlashSkinkVolume"`, `Error.Code = ErrorCode.SplitBrainDetected`, `Error.Metadata["TailProviderID"]` matching the registered tail.
- `OpenAsync_SplitBrain_WritesBackgroundFailureRow` — same setup; after reopen, query the `BackgroundFailures` table directly (Dapper through a freshly-acquired brain scope on a test-controlled side connection — or call `BackgroundFailureRepository.ListUnacknowledgedAsync` from within a fresh `IBrainAccess` constructed for the test). Assert exactly one row with `Source = "FlashSkinkVolume"`, `ErrorCode = nameof(ErrorCode.SplitBrainDetected)`, message contains "conflicting copy".
- `OpenAsync_FencedState_PersistedAcrossReopen` — open conflict-triggering scenario → assert Fenced → dispose → reopen WITHOUT touching the tail's witness again → assert `State == Fenced`; assert NO second `Critical` notification (per Principle 24 — single detection).
- `OpenAsync_FencedState_UploadsAreBlocked` — open a fenced volume → write a file via `WriteFileAsync` (Phase 1, must succeed) → wait briefly (1–2 seconds via test clock or real `Task.Delay`) → assert the tail's filesystem contains NO blob file (other than `_witness/`, `_brain/`). The fenced guard in `UploadQueueService` prevents any upload activity. **Implementation note:** the test relies on `FileSystemProvider` writing blobs to a deterministic path; if blobs land in a sharded `blobs/xx/yy/` layout, assert no `blobs/` directory was created on the tail.
- `OpenAsync_FencedState_Phase1WritesSucceed` — open a fenced volume → `WriteFileAsync` → assert `Result.Success`. Confirms Phase 1 is unaffected by the fence (only Phase 2 uploads are blocked).
- `Notification_SplitBrainDetected_UsesUserVocabularyOnly` — capture the published `Critical` notification. Assert `notification.Title` and `notification.Message` (concatenated) contain at least one of "skink"/"tail"/"copy" AND do NOT contain any of (case-insensitive): "epoch", "witness", "split-brain", "split brain", "volume", "fence", "fenced", "WAL", "blob", "stripe", "DEK", "AAD". Apply the same negative assertion to the `BackgroundFailures` row's `Message` column.
- `AutoDowngrade_StaleFencedBrainRow_WitnessShowsNoConflict_DowngradesToNormal` — create + register; manually upsert `Settings["VolumeState"] = "Fenced"` via a direct Dapper write to the brain (use a helper that opens a fresh `IBrainAccess` against the same brain file with the test's DEK). Write a witness with `Epoch = 2, ConflictObserved = false` to the tail. Reopen with `newEpoch = 5` (advance epoch by closing+reopening enough times — or set up the test so the increment is just one). Assert: `State == Normal`; `Settings["VolumeState"]` row is now `"Normal"`; a single `Info` notification was published with the resolution message.
- `AutoDowngrade_NoTailsReachable_KeepsFencedState` — same brain-state setup, but configure the tail's provider to fail `ListAsync` (use a `FaultInjectingStorageProvider` wrapper). Assert: `State == Fenced` after reopen; `Settings["VolumeState"]` row is still `"Fenced"`; no auto-downgrade notification published.
- `FreshConflictWhileAlreadyFenced_DoesNotRePublishNotification` — set up a fenced volume (one `Critical` notification observed); close; while closed, the OTHER skink advances the witness even further (simulate by overwriting with a yet-higher epoch); reopen → assert `State == Fenced` (still); assert NO additional `Critical` notification was published (the count in the captured list is exactly 1); assert NO additional `BackgroundFailures` row was written (count remains 1).
- `WitnessWriteFailureOnRegistration_DoesNotFailRegisterTail` — register a tail whose `BeginUploadAsync` is fault-injected to fail. Assert `RegisterTailAsync` returns `Result.Ok`; assert the in-memory registry contains the provider; assert the `Providers` brain row was inserted. (A subsequent handshake on the next open will retry the witness write.)

---

## Acceptance criteria

- [ ] `dotnet build` clean on Windows and Linux with `--warnaserror`.
- [ ] `dotnet test` green: all new tests pass, no existing tests regress.
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] `WitnessHandshake.RunAsync` returns the documented outcome for all 14+ scenarios above.
- [ ] `OpenAsync` runs the handshake before `BuildVolumeFromSessionAsync`; a handshake failure cleanly releases the lock and disposes the session.
- [ ] `FlashSkinkVolume.State` is publicly readable and reflects the post-handshake state.
- [ ] Fenced state persists across close/reopen via `Settings["VolumeState"]`.
- [ ] The two-sided fence works end-to-end: a winner stamps `ConflictObserved = true`, a subsequent lagger open with a lower local epoch still enters Fenced.
- [ ] Auto-downgrade works when the brain says Fenced but the witnesses are clean AND at least one tail is reachable; does NOT downgrade when no tails are reachable.
- [ ] `UploadQueueService` workers idle when `_fenced != 0`; no provider calls, no brain reads beyond the guard check.
- [ ] `RegisterTailAsync` writes an initial witness; a failure logs at Warning but does not fail registration.
- [ ] User-vocabulary discipline (Principle 25) holds in every notification message and `BackgroundFailures` row.
- [ ] `ErrorContext.Metadata` keys contain no token/key/password/secret/mnemonic/phrase substrings (Principle 26).
- [ ] BLUEPRINT.md contains §19.6 and §19.7 with the content sketched in the dev plan.

---

## Line-of-code budget

| File | Lines |
|---|---|
| `src/FlashSkink.Core/Identity/WitnessHandshake.cs` | ~210 |
| `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` (delta) | ~170 |
| `src/FlashSkink.Core/Upload/UploadQueueService.cs` (delta) | ~35 |
| `BLUEPRINT.md` (delta) | ~90 |
| **Total src delta** | **~505** |
| `tests/.../Identity/WitnessHandshakeTests.cs` | ~280 |
| `tests/.../Orchestration/SplitBrainIntegrationTests.cs` | ~430 |
| **Total test delta** | **~710** |

Dev-plan budget headline was ~470 src / ~550 test; this plan is higher because (a) the dev-plan budget didn't account for the new `_logger` field threading through `FlashSkinkVolume`'s constructor, the `RegisterTailAsync` `VolumeID`/`VolumeEpoch`/`VolumeState` read block, or the ephemeral logger construction inside the static `RunWitnessHandshakeAsync`; (b) the integration test count is one larger than the dev-plan list (the `RegisterTailAsync_WritesInitialWitness_OnRegistration` test was implied but not enumerated, and the `WitnessWriteFailureOnRegistration_DoesNotFailRegisterTail` test is an additional defensive case). Both are intentional.

---

## Non-goals

- Do NOT add `FlashSkinkVolume.PromoteAsync` — §3.5.3.
- Do NOT add BLUEPRINT §19.8 (resolution flow) — §3.5.3.
- Do NOT return `ErrorCode.VolumeFenced` from any code path in this PR — the enum value exists from §3.5.1; §3.5.3 wires the `PromoteAsync` race-guard that returns it.
- Do NOT add per-tail `LastWitnessSessionId` tracking for the simultaneous-cross-host case — documented as a known limitation in §19.6.
- Do NOT modify `BrainMirrorService` — the brain mirror runs normally even while fenced (it is infrastructure, not a Phase 2 upload of user data).
- Do NOT modify `CreateAsync` — a brand-new volume has no tails, so there is nothing to handshake against; `RegisterTailAsync`'s initial-witness write covers the first-session contract.
- Do NOT add the fenced-guard to `RangeUploader` or to `UploadQueueRepository` — the guard belongs at the `UploadQueueService` worker boundary; the layers below it should not need to know about fenced state.
- Do NOT add a CLI surface for `State` or `BackgroundFailures` listing — that lands in Phase 4.
- Do NOT introduce `IWitnessStore` as an interface — `WitnessStore` is a concrete `internal sealed class`. §3.5.3's `PromoteAsync` uses the concrete type; an interface adds no benefit until there's a second implementation, which is not on the roadmap.
