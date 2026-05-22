# Fix — brain SqliteConnection thread-safety (universal gate)

**Branch:** `fix/brain-connection-thread-safety`
**Blueprint sections:** §6.6 (cancellation), §6.7 (Principle 17 compensation), §9.7 (raw-reader hot paths), §13.4 (atomic skink writes), §15.3 (upload step 7c brain transitions), §16 (brain schema and connection), §19 (volume lifecycle), §21.3 (crash-consistency invariant)
**CLAUDE.md principles touched:** 1, 17, 22, **and a proposed new principle 36** (see Principles section).
**Spike origin:** `docs/spike-findings.md` § "2026-05-20 — SQLite dispose-time NRE on Windows CI". The previously-proposed cancellation-based fix (`fix/upload-queue-dispose-race`, plan at `.claude/plans/fix-upload-queue-dispose-race.md`) was empirically falsified and is not the basis of this PR.

## Scope

`Microsoft.Data.Sqlite.SqliteConnection` is not thread-safe — its internal
`_commands` collection is an unsynchronized `List<T>`. The brain
`SqliteConnection` is currently shared by ~9 components (`WritePipeline`,
`UploadQueueRepository`, `BlobRepository`, `FileRepository`,
`ActivityLogRepository`, `BackgroundFailureRepository`, `WalRepository`,
`UploadQueueService` transaction blocks, `BrainMirrorService.BackupDatabase`,
and `FlashSkinkVolume.RegisterTailAsync` Dapper calls). Any two of these
running concurrently can corrupt `_commands`; the corruption later surfaces as
`NullReferenceException` inside `SqliteConnection.Close()` — the CI flake
documented in PR #67's commit message and PR #75's run 26158829111.

Fix shape: introduce `IBrainAccess` — a thin wrapper that owns the brain
`SqliteConnection` and exposes it only through a disposable scope that
acquires a single per-volume `SemaphoreSlim`. Every existing SQL call site is
migrated to acquire the scope before issuing SQL. Methods that take a
`SqliteTransaction` parameter (the cross-component transaction pattern) do
**not** re-acquire the gate — the caller is responsible for holding the scope
around the entire transaction block. After the fix, no raw `SqliteConnection`
flows into any downstream component.

## Files to create

- `src/FlashSkink.Core/Metadata/BrainAccess.cs` — `IBrainAccess`, `BrainScope`,
  `BrainAccess`. ~120 lines including XML docs.
- `tests/FlashSkink.Tests/Metadata/BrainAccessTests.cs` — unit tests for
  `BrainAccess`: acquire serialises two concurrent callers, scope dispose
  releases the gate, dispose cancels pending acquires, double-dispose is safe,
  exception inside scope still releases the gate. ~200 lines.
- `tests/FlashSkink.Tests/CrashConsistency/BrainAccessConcurrencyTests.cs` —
  regression test adapted from the spike's `SqliteConcurrentAccessSpike`. 20
  concurrent SQL operations on one connection: **without** the wrapper (using
  a raw `SqliteConnection`) the test demonstrates failures (kept as a
  documented "this is the bug we fixed" assertion against a future
  regression); **with** `BrainAccess` the same workload runs cleanly across
  500 iterations. ~180 lines.

## Files to modify

### Wrapper integration (lifetime owners)

- `src/FlashSkink.Core/Crypto/VolumeSession.cs` — replace
  `SqliteConnection? _brainConnection` with `IBrainAccess? _brainAccess`.
  `DisposeAsync` now disposes the `BrainAccess` (which disposes the
  connection internally). Public `BrainConnection` property is **removed**;
  new public `Brain` (returns `IBrainAccess?`).
- `src/FlashSkink.Core/Crypto/VolumeSession.cs` (`VolumeLifecycle.OpenAsync`)
  — after `BrainConnectionFactory.CreateAsync` returns the raw connection
  and `MigrationRunner.RunAsync` completes (these are single-threaded
  startup paths that legitimately need raw access), wrap the connection in
  `new BrainAccess(connection, …)` and store *that* on `VolumeSession`.
- `src/FlashSkink.Core/Engine/VolumeContext.cs` — constructor parameter
  `SqliteConnection brainConnection` becomes `IBrainAccess brain`. Public
  `BrainConnection` property is **removed**; new public `Brain` (returns
  `IBrainAccess`). XML comments updated to reflect new ownership.

### Repository migrations (constructor + every public method)

Each repository constructor takes `IBrainAccess brain` instead of
`SqliteConnection connection`. Each public method that issues SQL wraps the
SQL block in `using var scope = await _brain.LockAsync(ct);` and replaces
`_connection` with `scope.Connection`. Overloads that accept a
`SqliteTransaction? transaction` parameter do **not** acquire the gate —
their XML doc gains a "*caller must hold the brain scope*" note.

- `src/FlashSkink.Core/Metadata/WalRepository.cs`
- `src/FlashSkink.Core/Metadata/BlobRepository.cs`
- `src/FlashSkink.Core/Metadata/FileRepository.cs`
- `src/FlashSkink.Core/Metadata/ActivityLogRepository.cs`
- `src/FlashSkink.Core/Metadata/BackgroundFailureRepository.cs`
- `src/FlashSkink.Core/Metadata/UploadQueueRepository.cs` — special case:
  `DequeueNextBatchAsync` is an `async IAsyncEnumerable` iterator. The scope
  must be held for the lifetime of the iteration; the `using var scope`
  declaration at the top of the iterator body achieves this naturally — the
  scope is disposed when the consumer's `await foreach` finishes or
  throws.

### Service migrations (constructor + transaction blocks)

- `src/FlashSkink.Core/Upload/UploadQueueService.cs` — constructor parameter
  `SqliteConnection connection` becomes `IBrainAccess brain`.
  `ApplyCompletedAsync` and `ApplyPermanentAsync` acquire `using var scope =
  await _brain.LockAsync(ct)` around the entire `await using (var tx =
  scope.Connection.BeginTransactionAsync(…))` block. Repository calls inside
  the transaction continue to pass `tx`; per the contract above, those
  overloads do not re-acquire the gate.
- `src/FlashSkink.Core/Engine/BrainMirrorService.cs` — constructor parameter
  `SqliteConnection brainConnection` becomes `IBrainAccess brain`.
  `BackupAsync` (the `Task.Run(() => _brain.BackupDatabase(dest))` site at
  line 562–578) wraps the entire `Task.Run` body in `using var scope = await
  _brain.LockAsync(ct)` acquired on the calling task before the `Task.Run`,
  with `scope.Connection.BackupDatabase(dest)` inside. Holding the gate
  across the full backup is correct — `BackupDatabase` reads pages
  sequentially and is the only operation that should access the connection
  during the cycle.

### Write pipeline migration

- `src/FlashSkink.Core/Engine/WritePipeline.cs` — five direct uses of
  `context.BrainConnection` (lines 141, 503, 506, 527, 549, 562). The Phase 1
  commit at line 503 (`tx = ctx.BrainConnection.BeginTransaction()`) is a
  cross-table transaction that already serialises with itself through the
  volume's `_gate`; under the new contract, this block acquires `using var
  scope = await ctx.Brain.LockAsync(ct)` once at the top of the commit and
  uses `scope.Connection.BeginTransaction()` and `scope.Connection.ExecuteAsync(tx)`
  inside. The pre-commit existing-file lookup at line 141 acquires its own
  short-lived scope.

### Volume orchestration

- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs`:
  - `BuildVolumeFromSessionAsync` (line ~990) — read `session.Brain` instead
    of `session.BrainConnection`. Pass `IBrainAccess` to every repository and
    service constructor and to `VolumeContext`. (The volume's `_gate` is
    retained — it serves a different purpose, gating *volume-level*
    operations such as `RegisterTailAsync` against `DisposeAsync` regardless
    of brain SQL.)
  - `RegisterTailAsync` (line ~808) — the two Dapper calls
    (`QuerySingleOrDefaultAsync` SELECT and `ExecuteAsync` INSERT) are wrapped
    in a single `using var scope = await _brain.LockAsync(ct)` so the
    select-then-insert sequence is atomic against any worker SQL.
  - The volume's `_gate` continues to wrap RegisterTailAsync as today;
    BrainAccess is acquired *inside* the volume gate. The two gates compose
    correctly: the volume gate prevents concurrent register/dispose, the
    brain gate prevents concurrent SQL.
  - `DisposeAsync` — no change. `_uploadQueueService.DisposeAsync()` drains
    workers; `_brainMirrorService.DisposeAsync()` drains mirror; `_context.Dispose()`
    runs; `_session.DisposeAsync()` now disposes the `BrainAccess` which
    disposes the connection. The existing ordering is correct.

### Migration / factory paths (no gate needed — single-threaded by construction)

- `src/FlashSkink.Core/Metadata/BrainConnectionFactory.cs` — unchanged. Runs
  once during volume open; no concurrent access possible.
- `src/FlashSkink.Core/Metadata/MigrationRunner.cs` — unchanged. Runs once
  before any worker exists; no concurrent access possible. Takes raw
  `SqliteConnection`; this is the one *sanctioned* place raw access remains
  (XML doc updated to note this).

### Documentation

- `CLAUDE.md` — add Principle 36 (text in Principles section below).
- `docs/architecture.md` — short paragraph under "Brain layer" describing the
  thread-safety invariant and `IBrainAccess`.

## Public API surface

No external API change. `FlashSkinkVolume` and its public methods retain
their signatures. The change is internal to Core:

- **New**: `FlashSkink.Core.Metadata.IBrainAccess` (public).
- **New**: `FlashSkink.Core.Metadata.BrainScope` (public, readonly struct,
  `IDisposable`).
- **New**: `FlashSkink.Core.Metadata.BrainAccess : IBrainAccess,
  IAsyncDisposable` (public).
- **Renamed/replaced**: `VolumeSession.BrainConnection` → `VolumeSession.Brain`
  (returns `IBrainAccess?`). `VolumeContext.BrainConnection` →
  `VolumeContext.Brain` (returns `IBrainAccess`).
- **Constructor signature changes** (internal-facing) on every repository
  and on `UploadQueueService`, `BrainMirrorService`, `VolumeContext`,
  `VolumeSession`.

### `IBrainAccess` (interface)

```csharp
public interface IBrainAccess
{
    /// <summary>
    /// Acquires the brain serialisation gate and returns a scope exposing the
    /// underlying connection. Dispose the scope to release the gate. Throws
    /// <see cref="OperationCanceledException"/> if <paramref name="ct"/> cancels
    /// before the gate is acquired. Throws <see cref="ObjectDisposedException"/>
    /// after the owning <see cref="BrainAccess"/> has been disposed.
    /// </summary>
    ValueTask<BrainScope> LockAsync(CancellationToken ct);
}
```

### `BrainScope` (public readonly struct, `IDisposable`)

```csharp
public readonly struct BrainScope : IDisposable
{
    public SqliteConnection Connection { get; }
    /// <summary>Releases the brain gate. Idempotent.</summary>
    public void Dispose();
}
```

`Dispose` is synchronous (`SemaphoreSlim.Release` is synchronous). Callers use
`using var scope = await brain.LockAsync(ct);` — the `await` is on
`LockAsync`, the `using` declares the scope, dispose is sync at end of block.

### `BrainAccess` (public sealed class)

```csharp
public sealed class BrainAccess : IBrainAccess, IAsyncDisposable
{
    public BrainAccess(SqliteConnection connection, ILogger<BrainAccess> logger);
    public ValueTask<BrainScope> LockAsync(CancellationToken ct);
    public ValueTask DisposeAsync();
}
```

`DisposeAsync` acquires the gate with `CancellationToken.None` (Principle 17 —
dispose is compensation, must not be cancellable), then disposes the
connection, then releases and disposes the semaphore. Idempotent.

## Internal types

None new beyond the three above.

## Method-body contracts

### `BrainAccess.LockAsync`

1. `if (_disposed != 0) throw new ObjectDisposedException(nameof(BrainAccess))`.
2. `await _gate.WaitAsync(ct).ConfigureAwait(false)` — propagates OCE.
3. Return `new BrainScope(_connection, _gate)`.

### `BrainAccess.DisposeAsync`

1. `if (Interlocked.Exchange(ref _disposed, 1) != 0) return`.
2. `await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false)` —
   Principle 17 literal.
3. `try { _connection.Dispose(); } finally { _gate.Release(); _gate.Dispose(); }`.

The `await _gate.WaitAsync` ensures any in-flight scope completes before we
dispose the connection. **This is the structural fix for the original CI
flake**: dispose cannot race with an in-flight SQL operation.

### `BrainScope.Dispose`

1. `_gate?.Release()` — null check guards against default-constructed scopes
   in error paths.

### Repository method contracts (representative — same pattern everywhere)

For methods that **don't** take a `SqliteTransaction` parameter (the standalone
entry points):

```csharp
public async Task<Result> XAsync(args, CancellationToken ct)
{
    try
    {
        ct.ThrowIfCancellationRequested();
        using var scope = await _brain.LockAsync(ct).ConfigureAwait(false);
        // existing SQL, with scope.Connection in place of _connection
        ...
    }
    catch (OperationCanceledException ex) { return Result.Fail(Cancelled, ..., ex); }
    catch (SqliteException ex) { ... }
    catch (Exception ex) { return Result.Fail(Unknown, ..., ex); }
}
```

For methods that **do** take a `SqliteTransaction` parameter (transaction
participants):

```csharp
/// <remarks>The caller must hold a <see cref="BrainScope"/> for the duration
/// of <paramref name="transaction"/>; this method does not re-acquire the
/// brain gate.</remarks>
public async Task<Result> XAsync(args, SqliteTransaction? transaction, CancellationToken ct)
{
    // No gate acquisition. Use _brain.UnsafeConnection-style accessor? No —
    // we pass the connection through the transaction. Dapper's CommandDefinition
    // accepts a Transaction; the connection used is the transaction's connection.
    try
    {
        ct.ThrowIfCancellationRequested();
        var connection = transaction?.Connection ?? throw new InvalidOperationException(
            "transaction-aware overload requires a non-null transaction");
        // ... existing SQL on `connection` ...
    }
    catch ... { ... }
}
```

For the standalone overload that delegates to the transaction-aware one
(`MarkUploadedAsync(..., CancellationToken)` → `MarkUploadedAsync(..., null,
CancellationToken)`), the standalone overload acquires its own scope before
delegating. Sketch:

```csharp
public async Task<Result> MarkUploadedAsync(args, CancellationToken ct)
{
    using var scope = await _brain.LockAsync(ct).ConfigureAwait(false);
    return await MarkUploadedAsyncWithinScope(args, scope.Connection, transaction: null, ct);
}
```

This avoids the "null tx means acquire gate; non-null tx means caller owns
gate" ambiguity by splitting into two clear shapes. Plan keeps the existing
public overloads; renames the private worker.

## Integration points

- `UploadQueueService.ApplyCompletedAsync` / `ApplyPermanentAsync` —
  acquires the scope around the entire `BeginTransactionAsync` → `CommitAsync`
  block. Transaction-aware repository calls inside use `scope.Connection`
  implicitly (via the `tx.Connection` accessor in the transaction-aware
  overloads).
- `BrainMirrorService.RunOneCycleAsync` — acquires the scope before the
  `Task.Run` and holds it for the duration of `BackupDatabase`. The
  destination connection (a separate `SqliteConnection` opened solely for
  the backup destination) is unrelated to the brain gate.
- `WritePipeline` Phase 1 commit — acquires the scope, opens
  `BeginTransaction`, performs all five `ExecuteAsync` calls, commits,
  releases scope.
- `FlashSkinkVolume.RegisterTailAsync` — already gated by volume `_gate`;
  also acquires brain scope for the Dapper SELECT + INSERT.

## Principles touched

- **Principle 1** (Core never throws across the public API): preserved.
  `LockAsync` throws `OperationCanceledException` and `ObjectDisposedException`
  — both are internal to Core and never reach a Core public boundary; the
  callers (repositories, services) catch them in their existing catch
  ladders. The `BrainScope.Dispose()` cannot throw under normal use. The
  iterator carve-out in `DequeueNextBatchAsync` is unchanged.
- **Principle 17** (`CancellationToken.None` in compensation paths): applied
  inside `BrainAccess.DisposeAsync` (gate wait under `None`) and inside
  every `Apply*` transaction block already (carried over from PR #67).
- **Principle 22** (raw `SqliteDataReader` in brain hot paths): preserved.
  `DequeueNextBatchAsync` still uses the raw reader; it just runs inside a
  scope.

### Proposed new Principle 36 (requires approval)

> **36. Every brain SQL operation flows through `IBrainAccess`; raw
> `SqliteConnection` is never injected into a downstream component.**
> `Microsoft.Data.Sqlite.SqliteConnection` is not thread-safe (internal
> `_commands` is an unsynchronized `List<T>`; CI evidence in
> `docs/spike-findings.md` § "2026-05-20"). The single sanctioned exception
> is the volume-open path: `BrainConnectionFactory.CreateAsync` and
> `MigrationRunner.RunAsync` run before any background service exists and
> may take a raw `SqliteConnection`. All other components — repositories,
> services, the write pipeline, volume orchestration — receive
> `IBrainAccess` and acquire a `BrainScope` for every SQL block. (Blueprint
> §16, spike findings 2026-05-20.)

Decision: add to `CLAUDE.md` in this PR alongside the code change (CLAUDE.md
update policy: "Updates to CLAUDE.md ship in the same PR as the change they
document. Not in a separate housekeeping PR. Causal link matters.")

## Test spec

### `tests/FlashSkink.Tests/Metadata/BrainAccessTests.cs`

- `LockAsync_Acquired_ReturnsScope_WithConnection` — basic happy path.
- `LockAsync_TwoCallers_Serialise` — Task A acquires + holds 50ms; Task B's
  `LockAsync` does not return until A's scope is disposed.
- `LockAsync_CancellationBeforeAcquire_ThrowsOce` — already-cancelled token
  throws OCE without acquiring.
- `LockAsync_CancellationWhileWaiting_ThrowsOce` — Task A holds; Task B
  awaits with a token; cancelling B's token throws OCE without acquiring.
- `Dispose_AfterScopeDisposed_DisposesConnection` — after the scope is
  released and BrainAccess is disposed, the connection is closed.
- `Dispose_WhileScopeHeld_BlocksUntilReleased` — Task A holds the scope;
  `DisposeAsync` started in Task B does not complete until A releases. (5s
  budget; verifies the Principle-17 dispose-waits-for-in-flight contract.)
- `LockAsync_AfterDispose_ThrowsObjectDisposed` — after DisposeAsync,
  LockAsync throws.
- `DoubleDispose_IsSafe` — idempotent.
- `ScopeDispose_OnExceptionInBody_StillReleasesGate` — `using` statement
  releases on exception path.

### `tests/FlashSkink.Tests/CrashConsistency/BrainAccessConcurrencyTests.cs`

- `ConcurrentSql_ThroughBrainAccess_NoFailures` — 500 iterations, each with
  20 concurrent SQL operations on one `BrainAccess`. Asserts zero
  exceptions. This is the direct counter-test to the spike's
  `SqliteConcurrentAccessSpike.TwoTasks_ConcurrentSql_OnOneConnection_NreAtClose`
  which produced 4.2% failures on a raw `SqliteConnection`.
- `VolumeLifecycle_StressRegisterTail_NoNre` — 100 iterations of
  `CreateAsync → RegisterTail × 3 → DisposeAsync` on a real
  `FlashSkinkVolume`. Asserts zero exceptions and no `Error`/`Critical`
  notifications. This is the integration-level regression test for the
  original CI flake.

### Existing tests that must remain green

- All `FlashSkinkVolumeUploadIntegrationTests` — `RegisterTail_*`,
  `WriteFile_AfterRegisterTail_LandsAtTail`,
  `FiveWrites_AllUploaded_SessionsEmpty` (PR #67 regression). The fix
  preserves transaction semantics and per-tail isolation, so all should
  pass with no test change.
- All repository unit tests under `tests/FlashSkink.Tests/Metadata/`,
  `Upload/`, `Engine/` — these construct repositories with a raw
  `SqliteConnection` today. Each will need a one-line change: wrap the
  test's raw connection in `new BrainAccess(connection, NullLogger)` and
  pass that. ~30 test files affected, mechanically.

## Acceptance criteria

- [ ] `dotnet build` zero warnings on Windows + Linux.
- [ ] All existing tests pass after the mechanical "wrap connection in
      BrainAccess" update.
- [ ] `BrainAccessTests` (9 cases) pass.
- [ ] `BrainAccessConcurrencyTests.ConcurrentSql_ThroughBrainAccess_NoFailures`
      passes 500 iterations × 20 concurrent calls with zero failures.
- [ ] `BrainAccessConcurrencyTests.VolumeLifecycle_StressRegisterTail_NoNre`
      passes 100 iterations cleanly.
- [ ] Grep verification: no `: SqliteConnection` constructor parameter
      anywhere in `src/FlashSkink.Core/` outside `BrainConnectionFactory`,
      `MigrationRunner`, `BrainAccess`, and the sanctioned destination
      connection in `BrainMirrorService.BackupAsync`. The new principle 36 is
      machine-checkable via this grep.
- [ ] CLAUDE.md updated with Principle 36 (subject to approval at Gate 1).
- [ ] `docs/architecture.md` updated with a short brain-layer paragraph.

## Line-of-code budget

| File | Δ LoC | Note |
|---|---|---|
| `src/FlashSkink.Core/Metadata/BrainAccess.cs` (new) | +120 | wrapper + docs |
| `src/FlashSkink.Core/Crypto/VolumeSession.cs` | +10 / -5 | property rename, dispose |
| `src/FlashSkink.Core/Engine/VolumeContext.cs` | +10 / -5 | constructor param + prop |
| `src/FlashSkink.Core/Metadata/WalRepository.cs` | +15 | wrap each method |
| `src/FlashSkink.Core/Metadata/BlobRepository.cs` | +25 | wrap each method |
| `src/FlashSkink.Core/Metadata/FileRepository.cs` | +50 | many methods |
| `src/FlashSkink.Core/Metadata/ActivityLogRepository.cs` | +15 | |
| `src/FlashSkink.Core/Metadata/BackgroundFailureRepository.cs` | +15 | |
| `src/FlashSkink.Core/Metadata/UploadQueueRepository.cs` | +30 | iterator + tx-aware |
| `src/FlashSkink.Core/Upload/UploadQueueService.cs` | +20 | 2 tx blocks, ctor |
| `src/FlashSkink.Core/Engine/BrainMirrorService.cs` | +15 | BackupAsync, ctor |
| `src/FlashSkink.Core/Engine/WritePipeline.cs` | +30 | 5 SQL sites |
| `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` | +25 | RegisterTail + wiring |
| `tests/FlashSkink.Tests/Metadata/BrainAccessTests.cs` (new) | +200 | 9 tests |
| `tests/FlashSkink.Tests/CrashConsistency/BrainAccessConcurrencyTests.cs` (new) | +180 | 2 stress tests |
| Existing tests (mechanical update) | +60 | ~30 sites × 2 lines each |
| `CLAUDE.md` | +12 | Principle 36 |
| `docs/architecture.md` | +15 | brain-layer paragraph |
| **Total** | **~850 lines** | larger PR, justified by structural fix |

## Non-goals

- Do **not** change the connection's `Pooling = false` setting. The Windows
  WAL-handle concern that motivated it is real; revisiting is a separate
  spike (see `docs/spike-findings.md` § "Decisions deferred").
- Do **not** introduce a connection pool, `Cache=Shared`, or per-operation
  connections. Those alternatives are documented in spike findings as
  deferred for post-V1 reconsideration.
- Do **not** add reentrancy to `BrainAccess`. A single thread that already
  holds the scope and recursively calls `LockAsync` will deadlock — by
  design. The transaction pattern (gate held by caller, repository methods
  with `transaction` parameter skip acquisition) is the explicit alternative
  to reentrancy.
- Do **not** introduce `AsyncLocal<BrainScope>` ambient context as a
  shortcut. Explicit pass-through (the transaction parameter, or the
  `scope.Connection` parameter) is more readable and avoids the well-known
  AsyncLocal-vs-Task-flow footguns.
- Do **not** widen the volume-level `_gate` to cover brain SQL.
  `_gate` serialises volume-level operations (RegisterTail vs DisposeAsync);
  the new brain gate serialises SQL. They are two different invariants and
  should remain separate to keep their contracts crisp.
- Do **not** change `MigrationRunner` or `BrainConnectionFactory` to take
  `IBrainAccess`. They run before any concurrent component exists.
- Do **not** delete `.claude/plans/fix-upload-queue-dispose-race.md` or the
  spike branch. Both are records of the disconfirmed hypothesis.
- Do **not** ship in pieces (e.g., gate workers first, write pipeline later).
  A half-finished migration leaves the bug class open on the
  unmigrated paths and creates an awkward principle ("some components use
  IBrainAccess, others don't"). All-or-nothing.

## Verification plan

1. Implement BrainAccess + tests; verify the 9 unit tests pass.
2. Implement the regression test
   (`BrainAccessConcurrencyTests.ConcurrentSql_ThroughBrainAccess_NoFailures`)
   with BrainAccess *not yet wired into the repositories* — it should pass
   (BrainAccess in isolation works).
3. Migrate repositories one at a time, running the affected test suite
   after each. Mechanical changes; failures should only be compile errors
   from the constructor signature change.
4. Migrate services (UploadQueueService, BrainMirrorService).
5. Migrate WritePipeline.
6. Migrate FlashSkinkVolume wiring.
7. Run full test suite; verify zero existing-test regressions.
8. Run `BrainAccessConcurrencyTests.VolumeLifecycle_StressRegisterTail_NoNre`
   — should pass cleanly.
9. Manual local stress: bump
   `ConcurrentSql_ThroughBrainAccess_NoFailures` to 5000 iterations once
   before PR open. Document the local-only run in the PR body's "Drift
   notes."
10. Open PR; let CI run the matrix.
