# Spike Findings

This document archives the conclusions of exploratory spikes. Each entry records
what we were investigating, what we found (especially hypothesis-falsifying
evidence), and the implication for production code. Code from spike branches is
discarded; only the decision survives here.

---

## 2026-05-20 — SQLite dispose-time NRE on Windows CI

**Branch:** `spike/sqlite-dispose-race` (discarded)
**Triggering symptom:** `System.NullReferenceException` inside
`Microsoft.Data.Sqlite.SqliteConnection.Close()` during
`FlashSkinkVolume.DisposeAsync` → `VolumeSession.DisposeAsync`. Observed once on
Windows CI in PR #75 (run 26158829111). Same shape reported in PR #67's commit
message for the `FiveWrites_AllUploaded_SessionsEmpty` test.

### Initial hypothesis (FALSIFIED)

The volume CTS cancels while an upload worker is mid-`DequeueNextBatchAsync`.
`ExecuteReaderAsync` / `ReadAsync` throw `OperationCanceledException`; the
iterator's `await using` cleanup of `SqliteDataReader` / `SqliteCommand` races
the imminent `SqliteConnection.Close()`. Suggested fix: apply Principle 17
(observe ct first, then `CancellationToken.None` inside) to
`DequeueNextBatchAsync`.

### Method

1. **Baseline rate** on `main` with the existing failing test in a 500-iteration
   in-process loop: **0 / 500 failures** (~100 ms / iter). Natural race window
   too narrow to hit locally.
2. **Widen the window**: injected `await Task.Delay(100, ct)` immediately after
   `ExecuteReaderAsync` so the worker is guaranteed mid-iterator when
   `DisposeAsync` fires. Added counters: 100 iterator entries / 100
   cancellations / 0 completions confirms the worker WAS cancelled mid-query
   every iteration. **Result: 0 / 100 NREs.** Strong disconfirmation of the
   cancellation hypothesis.
3. **New hypothesis — concurrent SQL on the shared connection.** Wrote a
   minimal harness: one `SqliteConnection`, N parallel `SELECT` commands via
   `Task.Run`, then `connection.Dispose()`. No volume, no upload service, no
   cancellation. Three controls:

| Variant | Parallel calls | Synchronization | Result |
|---|---|---|---|
| Negative control | 1 (sequential) | none | 0 / 500 failures |
| Test case | 20 parallel | none | **21 / 500 failures (4.2%)** |
| Positive control | 20 parallel | `SemaphoreSlim(1,1)` | 0 / 500 failures |

   First-failure stack trace in the test case is **byte-identical** to the CI
   stack: `NullReferenceException` in `SqliteConnection.Close()` →
   `Dispose(bool)`. Other failures in the same runs surface as
   `IndexOutOfRangeException` and `ArgumentOutOfRangeException` — all symptoms
   of an unsynchronized internal `List<WeakReference<SqliteCommand>> _commands`
   being mutated by one thread while another iterates.

### Root cause

`Microsoft.Data.Sqlite.SqliteConnection` is **not thread-safe**. The internal
`_commands` collection (added to in `SqliteCommand`'s ctor via
`_connection.AddCommand(this)`, removed from in `RemoveCommand` during command
disposal, walked by `Close()`) is a plain `List<T>` with no synchronization.
Two threads issuing SQL on one connection corrupts the list; the NRE manifests
the next time `Close()` (or the connection's component disposal path) walks the
corrupted list.

Confirmed against the `Microsoft.Data.Sqlite` source at
`dotnet/efcore` (`src/Microsoft.Data.Sqlite.Core/SqliteConnection.cs`):
`Close()` iterates `_commands` and calls `command.Dispose()`, which reenters
`RemoveCommand` on the same list. No locks, no `ConcurrentBag`, no
`ImmutableList`. Two historical reports of the same NRE pattern at dispose
time:
[aspnet/Microsoft.Data.Sqlite#466](https://github.com/aspnet/Microsoft.Data.Sqlite/issues/466)
(closed could-not-reproduce, repo archived 2018) and
[dotnet/efcore#20651](https://github.com/dotnet/efcore/issues/20651)
(closed not-planned, "suspected `_commands` corruption"). The MS Sqlite docs
have always implicitly assumed single-threaded access per connection; this is
not a regression, it's a baked-in invariant we were violating.

### Why this matches the CI flake

The brain `SqliteConnection` in a live `FlashSkinkVolume` is shared by many
components without a universal lock:

- `UploadQueueRepository` (upload workers, via `DequeueNextBatchAsync` and
  Dapper).
- `UploadQueueService` (transactions in `ApplyCompletedAsync` /
  `ApplyPermanentAsync`).
- `BrainMirrorService` (`_brain.BackupDatabase(dest)` on a `Task.Run` thread).
- `FlashSkinkVolume.RegisterTailAsync` and other gated volume operations
  (Dapper).
- All Dapper-backed repository methods (`BlobRepository`, `FileRepository`,
  `ActivityLogRepository`, `ProviderRepository`, …).

`_gate` in `FlashSkinkVolume` only serialises *volume-level* operations
(`RegisterTailAsync`, `DisposeAsync`, write pipeline acquire). Workers and the
mirror service touch the brain *without* taking `_gate`. Any moment a worker
issues SQL while `RegisterTailAsync`'s Dapper calls also run on the brain (or
while `BrainMirrorService`'s `BackupDatabase` runs on its thread-pool task),
`_commands` corruption is possible. The NRE surfaces at some later
`Close()` — often `DisposeAsync` — because that's when the corrupted list is
walked. The "happens at dispose" framing in the CI stack is misleading; the
**cause** is upstream and not local to dispose at all.

### Implication for the originally-proposed fix

The Principle-17 fix on `DequeueNextBatchAsync`
(`.claude/plans/fix-upload-queue-dispose-race.md`) **does not address the
root cause**. Cancelling the worker mid-iterator is empirically not the racer.
Shipping that fix would have left the bug class wide open and given the team
false confidence. Plan discarded.

### Recommended fix shape

Structural, not local. The minimum is a **brain-connection lock acquired by
every SQL-issuing site**:

1. Introduce a `SemaphoreSlim` (or equivalent) on `VolumeContext` (or a new
   `BrainConnection` wrapper) that owns the connection and gates every SQL
   operation.
2. Every repository (`UploadQueueRepository`, `BlobRepository`,
   `FileRepository`, `ActivityLogRepository`, `ProviderRepository`, …) takes
   the gate before any `_connection.CreateCommand()` / Dapper call.
3. `BrainMirrorService.BackupDatabase` takes the gate around the
   `Task.Run(() => _brain.BackupDatabase(dest))` block.
4. `FlashSkinkVolume.RegisterTailAsync` keeps its existing `_gate` use, but the
   inner Dapper calls also acquire the new brain gate (or unify the two).

Trade-off: a long-running write transaction blocks the upload worker from
checking the queue. In practice write transactions are short (Phase 1 commit
is bounded by file-system fsync, not by any IO that holds the lock for long).
Worth measuring before V1, not worth pre-optimising.

Alternative (rejected for V1): connection-per-component with WAL multi-reader.
SQLite WAL mode supports many readers + one writer, so each component could
own its own connection. Costs: more memory for DEK-derived state per
connection, lose cross-component transactions (we don't share them today,
mostly), and `BackupDatabase` from the mirror service still wants exclusive
read access. Larger refactor; defer past V1 if it's still needed.

### Verification approach for the real fix

The minimal reproducer in this spike — `SqliteConcurrentAccessSpike` — is the
right verification harness. The real fix should turn the test case (20 parallel
SELECTs, no synchronization) into the **failing case it should remain**, and
should turn the FlashSkink production code into one where every SQL-issuing
site acquires the brain gate. The stress test we'd add to
`FlashSkinkVolumeUploadIntegrationTests` then runs the full volume lifecycle
under load with no NREs.

### Artefacts (all discarded with the spike branch)

- `tests/FlashSkink.Tests/Spike/SqliteDisposeRaceSpike.cs` — falsified the
  cancellation hypothesis.
- `tests/FlashSkink.Tests/Spike/SqliteConcurrentAccessSpike.cs` — confirmed the
  concurrent-SQL root cause with negative + positive controls.
- `.claude/plans/fix-upload-queue-dispose-race.md` — discarded plan
  (cancellation-based fix). Kept in git history as a record of the wrong path.

### Lessons

1. The cancellation-during-iterator framing was a plausible but wrong
   hypothesis. The minimal reproducer (no volume, no upload service, no
   cancellation) was the correctness-clinching step. Future timing-race
   investigations should start with the minimal harness, not the failing test.
2. A 1-in-N CI flake doesn't mean "rare race we can't hit locally." A 4%
   failure rate locally with 20 parallel commands tells us CI just has worse
   thread-scheduling luck than a 16-core dev box. The bug isn't environmental;
   it's structural and shows up cleanly once you ask the right question.
3. Two historical reports of the same NRE pattern existed in the MS Sqlite
   issue tracker (#466, #20651), both closed unresolved. 30 minutes of
   issue-tracker reading would have surfaced the "_commands corruption"
   hypothesis directly. Do this step first on next race-condition spike.

### Decisions deferred

These alternatives were considered during the spike and explicitly rejected
**for V1**. Each is recorded with the conditions under which it should be
reconsidered, so a future maintainer doesn't have to re-derive the analysis.

**Custom connection pool (`ConcurrentBag<SqliteConnection>` of pre-keyed
connections).** Strict superset of built-in pooling: would give us true
parallel reads while a write transaction holds its connection. **Deferred
because:** SQLite WAL mode still serialises writers at the engine layer
(`SQLITE_BUSY` retry), so the pool only buys parallel *reads*, and FlashSkink
has no read-heavy hot path — workers poll every ~30 s, the brain is small,
`BackupDatabase` is the only longer read and it genuinely should serialise
against writes for snapshot consistency. Also: Argon2 brain-key derivation
runs once per pooled connection (N × ~250 ms at warm-up vs 1 × under the
gate), DEK-derived state lives in N connection internals (slightly worse for
Principle 31 zeroization), cross-repository transactions need `AmbientConnection`
plumbing, and the USB-yank-safety story (drain semantics at DisposeAsync) is
non-trivial. **Reconsider if:** post-V1 profiling shows the universal gate is
a measurable bottleneck — most likely sign is the brain-mirror `BackupDatabase`
blocking user-perceptible writes during normal activity.

**Per-operation connection + built-in pooling (`Pooling=true`,
`using var conn = new SqliteConnection(...)` per call).** Standard .NET idiom;
the pattern most SQLite samples assume. **Deferred because:** we explicitly
set `Pooling=False` in `BrainConnectionFactory` to prevent `.db-wal` /
`.db-shm` handles persisting past `Dispose()` on Windows — which matters
because the user can yank the USB at any time. The decision was made early
without a formal spike (single-line comment, no blueprint section). The
honest read: the WAL-handle concern is real, but the alternative
("`Pooling=true` + explicit `SqliteConnection.ClearAllPools()` at
`DisposeAsync`") is plausible if validated. **Reconsider if:** someone runs a
2–4 hour spike that (a) measures the actual WAL-handle window on Windows
after `ClearAllPools`, (b) confirms it closes before a realistic USB-yank
detection latency, and (c) measures Argon2 cost amortisation under our actual
workload.

**`Cache=Shared` (multiple connections to one file, shared in-memory page
cache).** Technically valid alternative to a custom pool. **Deferred
because:** the SQLite documentation
[recommends against it for new applications](https://www.sqlite.org/sharedcache.html)
— "Shared-cache mode is an obsolete feature… new applications should avoid
using it." Predates WAL and has its own thread-safety footnotes. Not worth
swimming against upstream guidance.

**`Mode=Memory` (pure in-memory database) and hybrid memory + flush
patterns** (e.g., Litestream-style memory working copy with periodic disk
flush). **Permanently rejected, not deferred:** the brain is the authoritative
state of the volume, and Phase 1 commit's "fsync the brain row before
returning success" (Principles 4, 29, 30) is meaningless if the brain isn't
durable on disk. Mentioned here only so this option doesn't re-surface in
future architectural reviews as a "did anyone consider…?".

### Structural principle that should outlive the chosen pattern

Whichever pattern we ultimately adopt (universal gate for V1; possibly
custom pool or per-op with pooling post-V1), the constraint to lock in is:
**no raw `SqliteConnection` is ever injected into a downstream component**.
Repositories, services, and the mirror task all consume the brain through a
single abstraction (`IBrainAccess` or a `BrainConnection` wrapper) that
*enforces* the chosen pattern. The current architecture violates this — we
pass `SqliteConnection` directly into `UploadQueueRepository`,
`BrainMirrorService`, etc., which is why the bug surfaced. Naming the
abstraction makes the safety property machine-checkable: if no Core code
references `SqliteConnection` outside the wrapper, the thread-safety
invariant holds regardless of which implementation lives behind the
abstraction.
