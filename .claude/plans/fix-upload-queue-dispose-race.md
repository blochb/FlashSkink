# Fix — UploadQueueService dispose race (NRE in SqliteConnection.Close)

**Branch:** `fix/upload-queue-dispose-race`
**Blueprint sections:** §6.6 (cancellation), §6.7 (Principle 17 compensation), §9.7 (raw-reader hot paths), §15.8 (worker wakeup), §19 (volume lifecycle), §21.3 (crash-consistency invariant)
**CLAUDE.md principles touched:** 14, 17, 22
**Not a dev-plan section:** standalone bug fix flagged in the task brief as a §3.4 / §3.6 concern surfaced by §3.5.x timing.

## Scope

Eliminate the intermittent `NullReferenceException` in `Microsoft.Data.Sqlite.SqliteConnection.Close()` observed on Windows CI when `FlashSkinkVolume.DisposeAsync` runs immediately after `RegisterTailAsync` (which pulses the upload wakeup signal). The race is between an in-flight upload-worker iterator (`UploadQueueRepository.DequeueNextBatchAsync`) — whose `SqliteDataReader` and `SqliteCommand` cleanup runs when the worker's `CancellationToken` cancels — and `VolumeSession.DisposeAsync` calling `SqliteConnection.Dispose()`. Apply CLAUDE.md Principle 17 ("`CancellationToken.None` in compensation paths is a literal, never a local") to the iterator's SQL primitives so that once a dequeue cycle starts, it runs to completion under `CancellationToken.None` and the worker observes cancellation between cycles, never mid-query.

## Files to modify

- `src/FlashSkink.Core/Metadata/UploadQueueRepository.cs` — make `DequeueNextBatchAsync` observe `ct` at entry, then pass `CancellationToken.None` to `ExecuteReaderAsync` and `ReadAsync`. Drop the `[EnumeratorCancellation]` use so a `.WithCancellation(...)` consumer can't reintroduce mid-query cancellation through the back door (the parameter stays named `ct` for the worker's caller side; we just route it to the pre-flight check, not the SQL).
- `src/FlashSkink.Core/Upload/UploadQueueService.cs` — `WorkerAsync` already drains the `await foreach` and then checks `ct` on the next iteration; add one `ct.ThrowIfCancellationRequested()` between dequeue and `ProcessOneAsync` to keep the cancellation point explicit now that the iterator no longer observes `ct` internally. Update the inline comment to reference Principle 17 and explain the new contract.

## Files to create

None (regression test added to an existing file — see Test spec).

## Files NOT changed (out of scope — guard against drift)

- `src/FlashSkink.Core/Crypto/VolumeSession.cs` — the NRE surfaces here but the bug is upstream; leaving alone preserves the simplest possible session teardown.
- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs::DisposeAsync` — the existing ordering (CTS cancel → upload-queue drain → brain mirror → context → session → lock) is correct once workers can no longer leak readers. Do **not** add a new gate, sleep, or pre-drain step here.
- `src/FlashSkink.Core/Engine/BrainMirrorService.cs` — final-mirror path already runs under `CancellationToken.None` (Principle 17); not implicated by the failure stack.
- PR #67's `ApplyCompletedAsync` Principle-17 fix in `UploadQueueService.cs` — must remain intact.

## Public API surface

No change. `UploadQueueRepository.DequeueNextBatchAsync` remains:

```csharp
public IAsyncEnumerable<TailUploadRow> DequeueNextBatchAsync(
    string providerId, int batchSize, CancellationToken ct);
```

The `[EnumeratorCancellation]` attribute is removed from the `ct` parameter — see Method-body contracts. This is an internal contract change (no source-breaking effect on existing callers in `UploadQueueService.WorkerAsync`), and the XML doc gains a Principle-17 note.

## Internal types

None added.

## Method-body contracts

### `UploadQueueRepository.DequeueNextBatchAsync(string providerId, int batchSize, CancellationToken ct)`

**New contract (Principle 17):**

1. Observe `ct` at entry with `ct.ThrowIfCancellationRequested()` (existing).
2. Open `SqliteCommand` via `_connection.CreateCommand()` inside an `await using` (existing).
3. Open `SqliteDataReader` via `ExecuteReaderAsync(CancellationToken.None)` — **change**: was `(ct)`.
4. Loop: `while (await reader.ReadAsync(CancellationToken.None))` — **change**: was `(ct)`. Yield rows.
5. The `[EnumeratorCancellation]` attribute is removed from `ct`. The parameter stays — it's how the entry-time check (step 1) is reachable — but the iterator no longer surfaces a consumer-side `.WithCancellation` token to the SQL primitives.

**Why:** A single dequeue cycle returns at most `batchSize` rows (worker calls with `batchSize: 1`); the cycle is bounded in cost. Letting it complete avoids leaving a `SqliteDataReader`/`SqliteCommand` to be torn down mid-disposal on a cancellation race. The worker re-checks `ct` between cycles (see WorkerAsync change), so shutdown latency remains bounded by one short dequeue.

**Failure modes unchanged:** Per Principle 1 carve-out in CLAUDE.md (the §9.7 sanctioned exception for `IAsyncEnumerable<readonly record struct>` raw-reader hot paths), `SqliteException` continues to propagate to the caller. Cancellation at entry continues to propagate as `OperationCanceledException`. Once inside the SQL section, cancellation is no longer observed — by design.

### `UploadQueueService.WorkerAsync(string providerId, CancellationToken ct)`

After the existing `await foreach` over `DequeueNextBatchAsync`, insert `ct.ThrowIfCancellationRequested()` immediately before the `if (dequeued is null)` branch — the iterator no longer throws OCE mid-cycle, so this is the explicit between-cycles cancellation point. The existing outer `catch (OperationCanceledException) { break; }` continues to catch the OCE thrown from the entry-time check inside the iterator or from this new check.

Update the existing two-line comment ("Drain the reader fully…") to add: "Per Principle 17, the iterator runs its SQL under `CancellationToken.None`; we observe `ct` here between cycles instead of mid-query, so the reader and command always finish disposing cleanly before the next iteration starts."

## Integration points

- `UploadQueueService.WorkerAsync` is the sole production caller of `DequeueNextBatchAsync`. Verified by `Grep` over `src/`. (Tests construct their own callers — see Test spec.)
- `FlashSkinkVolume.DisposeAsync`'s `await _uploadQueueService.DisposeAsync()` step continues to await `Task.WhenAll(workerTasks)` with the existing 10-second budget; the budget is now amply sufficient because workers exit on the next between-cycle ct check rather than potentially leaking iterator state.
- No interaction with `BrainMirrorService`, `VolumeSession`, or `VolumeContext` — those run after `_uploadQueueService.DisposeAsync()` returns, and are unaffected.

## Principles touched

- **Principle 14** ("`OperationCanceledException` is always the first catch on any method that accepts a `CancellationToken`") — unchanged; the iterator's entry-time `ThrowIfCancellationRequested` still surfaces OCE which the worker catches first.
- **Principle 17** ("`CancellationToken.None` in compensation paths is a literal, never a local") — extended to cover the iterator's SQL primitives. The bounded dequeue cycle is treated as a compensation-style critical section: observe ct before entering, then `CancellationToken.None` spelled out at every await site inside.
- **Principle 22** ("Brain hot paths use raw `SqliteDataReader`") — unchanged; the §9.7 sanctioned raw-reader pattern is preserved, and the §9.7 Principle-1 carve-out for `SqliteException` propagation continues to apply.
- Not touched: 13 (ct name/position unchanged), 15 (catch ordering unchanged at call sites), 16 (resource disposal pattern unchanged — `await using` does the work), 24 (background-failure surface unchanged), 26 (no new logging containing secrets).

## Test spec

### Update `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs`

Add one regression test that pulses the wakeup signal mid-dispose under stress:

- `RegisterTail_ThenDispose_Stress_NoSqliteNre` (10 iterations in CI; configurable up to 1000 locally via env-var-free constant `StressIterations = 10`)
  - Each iteration: `await using var volume = await CreateVolumeAsync();` then `RegisterTailAsync × 3` (each pulses the wakeup signal), then implicit `DisposeAsync` via `await using`.
  - Asserts: no exception propagates out of any iteration's dispose; over the run, no notification of severity `Error` or `Critical` posted to `_bus`.
  - Why 10 iterations in CI (not 1000): each iteration creates a brain + vault + SQLCipher key derivation, ~250 ms wall-time. 10 iterations × ~250 ms = ~2.5 s CI time, comfortably under the per-test budget. Local stress (manual) can bump to 1000.
  - Why "stress" rather than a precise interleaving: the failing race is a scheduler interleaving inside `Microsoft.Data.Sqlite` — there is no deterministic hook to force it. Re-running the create/register/dispose cycle 10× per CI run is the practical regression net.

### Keep passing

- All existing tests in `FlashSkinkVolumeUploadIntegrationTests.cs`, including:
  - `RegisterTail_IdempotentOnDuplicate` (the originally-flaky test)
  - `FiveWrites_AllUploaded_SessionsEmpty` (PR #67's regression test — verifies `ApplyCompletedAsync`'s Principle-17 fix still holds)
  - `WriteFile_AfterRegisterTail_LandsAtTail`
- All existing tests in `tests/FlashSkink.Tests/Upload/` that exercise `UploadQueueRepository.DequeueNextBatchAsync` directly (will verify by running `dotnet test --filter "FullyQualifiedName~UploadQueueRepository"` during Step 6).

## Acceptance criteria

- [ ] `dotnet build` zero warnings on Windows and Linux.
- [ ] All existing tests pass.
- [ ] `RegisterTail_ThenDispose_Stress_NoSqliteNre` passes 10 iterations consistently.
- [ ] `FiveWrites_AllUploaded_SessionsEmpty` continues to pass (PR #67 not regressed).
- [ ] No new public API surface; only internal contract change in `DequeueNextBatchAsync`.

## Line-of-code budget

- `src/FlashSkink.Core/Metadata/UploadQueueRepository.cs` — ~5 line delta (remove `[EnumeratorCancellation]`, swap two `ct`s for `CancellationToken.None`, extend doc comment).
- `src/FlashSkink.Core/Upload/UploadQueueService.cs` — ~6 line delta (one `ThrowIfCancellationRequested` + updated comment).
- `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs` — ~30 lines added.

## Non-goals

- Do **not** introduce a connection-level lock around all brain SQL. The existing single-connection-per-volume model is load-bearing and the dispose ordering in `FlashSkinkVolume.DisposeAsync` is correct once iterators stop leaking on cancellation.
- Do **not** add `[InternalsVisibleTo]` to expose the wakeup signal or worker tasks to the test project. The regression test exercises the public lifecycle (`CreateAsync` / `RegisterTailAsync` / `DisposeAsync`) — that's the layer where the bug surfaced.
- Do **not** widen Principle 17 to other repository methods preemptively. Only `DequeueNextBatchAsync` is implicated by the failure stack and is the only iterator method on a brain hot path; the Dapper-backed methods already handle ct cleanly via Dapper's standard cancellation plumbing.
- Do **not** change the 10-second `ShutdownBudget` constant. It remains a generous cap, not a primary correctness mechanism.
- Do **not** alter the `[EnumeratorCancellation]` attribute usage anywhere else (e.g., any future raw-reader iterator). This fix is scoped to the one method named in the failure stack.
- Do **not** rename, move, or re-namespace `UploadQueueRepository` or `UploadQueueService`.

## Verification plan

1. Read the change diff once before running tests.
2. Run `dotnet build` (Step 6, gate 3).
3. Run `dotnet test --filter "FullyQualifiedName~FlashSkinkVolumeUploadIntegrationTests"` — must be green.
4. Run `dotnet test --filter "FullyQualifiedName~UploadQueueRepository"` — must be green.
5. Run the full test suite — must be green.
6. Manually loop `RegisterTail_ThenDispose_Stress_NoSqliteNre` locally with `StressIterations = 1000` once before PR open, to confirm the race is closed. Document the local-only run in the PR body's "Drift notes" — do not commit a 1000-iteration default.
