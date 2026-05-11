# PR 3.4 — Upload queue service

**Branch:** `pr/3.4-upload-queue-service`
**Blueprint sections:** §15.8 (orchestrator + worker pseudocode), §21.1 (retry escalation — both ladders), §22.4 (offline gating via `INetworkAvailabilityMonitor`), §16.6 (`UploadQueueRepository` surface), §8 (`INotificationBus`, severities, `BackgroundFailures` persistence).
**Dev plan section:** `dev-plan/phase-3-Upload-queue-and-resumable-uploads.md` §3.4.

## Scope

Phase 3 §3.4 ships the orchestrator that takes the seam (§3.1), the clock + retry policy (§3.2), and the per-blob state machine (§3.3) and turns them into a live background service: one `UploadQueueService` per volume, owning an orchestrator task that ensures one `WorkerAsync` per registered provider is running, with each worker dequeuing `TailUploads` rows for its tail, calling `RangeUploader.UploadAsync`, and applying the per-cycle §21.1 ladder + the brain transitions on `TailUploads` + the §8 notification/activity-log surface. A small `UploadWakeupSignal` primitive (channel-of-capacity-1 with `DropWrite`) is added so the post-commit hook on `WritePipeline` (wired in §3.6) can kick the workers.

After this PR merges:

- A volume opened with an `IProviderRegistry` containing one or more registered providers runs one worker per provider, each dequeuing and uploading its tail's `PENDING`/`FAILED` rows.
- Per-blob outcomes from `RangeUploader` translate into a single brain transaction per outcome (Completed → `MarkUploaded` + `DeleteSession`; PermanentFailure → `MarkFailed` + `DeleteSession`; RetryableFailure → `MarkFailed` then a `RetryPolicy.NextCycleAttempt`-delayed re-dequeue).
- A failure on tail A never blocks tail B (per-tail worker isolation).
- `INetworkAvailabilityMonitor.IsAvailable == false` pauses all scheduling on the next tick; flipping back to `true` (event raised) wakes every worker via the shared signal.
- Volume shutdown cancels the volume CTS, awaits the orchestrator and each worker with a 10 s per-worker budget, and preserves any in-flight `UploadSessions` rows for the next session to resume from.

No volume integration (`FlashSkinkVolume.RegisterTailAsync` / `WriteBulkAsync` / `WritePipeline → UploadQueueService` wiring). §3.6 owns those.
No brain mirror. §3.5.
No new providers, no `IClock` host-side wiring, no `BrainBackedProviderRegistry`, no WAL recovery sweep.

## Files to create

### `src/FlashSkink.Core/Upload/`

- `UploadWakeupSignal.cs` — `public sealed class` wrapping a `Channel<byte>` of capacity 1, `BoundedChannelFullMode.DropWrite`, `SingleReader = false` (multiple workers read), `SingleWriter = false` (orchestrator and producer-side `WritePipeline` both write). Surface: `void Pulse()`, `ValueTask WaitAsync(CancellationToken ct)`. ~75 LOC including XML docs.
- `UploadQueueService.cs` — `public sealed class : IAsyncDisposable`. Owns the orchestrator task, the per-tail worker tasks, the volume-scoped `CancellationTokenSource`, and the shared `UploadWakeupSignal` reference. ~520 LOC including XML docs and the catch ladders.

### `tests/FlashSkink.Tests/Upload/`

- `UploadWakeupSignalTests.cs` — single-pulse / coalesce / multi-waiter / cancellation. ~140 LOC.
- `UploadQueueServiceTests.cs` — orchestrator and worker scenarios end-to-end against `FileSystemProvider` + `FaultInjectingStorageProvider` + an in-memory brain + `FakeClock`. ~720 LOC.

### `tests/FlashSkink.Tests/_TestSupport/`

- `TestNetworkAvailabilityMonitor.cs` — `internal sealed class : INetworkAvailabilityMonitor`. Exposes `void SetAvailable(bool)` that mutates the backing field and raises `AvailabilityChanged` when the value changes. Used by both the online-gating tests and the recovery-on-availability test. ~50 LOC.

## Files to modify

- `src/FlashSkink.Core/Metadata/UploadQueueRepository.cs` — add one **read-only** Dapper method:
  ```csharp
  public async Task<Result<UploadSessionRow?>> LookupSessionAsync(
      string fileId, string providerId, CancellationToken ct);
  ```
  Mirrors the existing `GetOrCreateSessionAsync` row-mapping logic verbatim (it already contains the SELECT-and-map block — extract a private helper if cleaner, but **do not** change the existing `GetOrCreateSessionAsync` public behaviour). Net delta: ~+45 LOC. No INSERT, no UPDATE; pure read.

No other production files modified. `IStorageProvider`, `IProviderRegistry`, `UploadSession`, `INetworkAvailabilityMonitor`, `RangeUploader`, `RetryPolicy`, `IClock`, `UploadConstants`, `ErrorCode`, and every brain schema are unchanged.

## Dependencies

- **NuGet:** none new. `System.Threading.Channels` is in the BCL.
- **Project references:** none added. `Core/Upload/UploadQueueService.cs` references `Core.Abstractions` (`IStorageProvider`, `IProviderRegistry`, `INetworkAvailabilityMonitor`, `INotificationBus`, `Notification`, `NotificationSeverity`, `Result`, `ErrorCode`, `BlobRecord`, `VolumeFile`, `ActivityLogEntry`, `IClock`), `Core.Metadata` (`UploadQueueRepository`, `BlobRepository`, `FileRepository`, `ActivityLogRepository`), and `Microsoft.Extensions.Logging.Abstractions`.

## Public API surface

### `FlashSkink.Core.Upload.UploadWakeupSignal` (sealed class)

Summary intent: edge-triggered, coalescing wakeup channel shared between the publisher (`WritePipeline` post-commit hook, the orchestrator on availability transitions) and consumers (orchestrator idle wait, each worker idle wait). Capacity-1 channel with `DropWrite` means a backed-up pulse is the same as a pending pulse: there is no benefit to coalescing more than one (the consumer re-queries the queue anyway).

```csharp
namespace FlashSkink.Core.Upload;

public sealed class UploadWakeupSignal
{
    public UploadWakeupSignal();

    /// <summary>
    /// Writes a single wakeup token. Idempotent: if a token is already buffered, the channel's
    /// <see cref="BoundedChannelFullMode.DropWrite"/> policy silently drops the new write. Never blocks.
    /// </summary>
    public void Pulse();

    /// <summary>
    /// Asynchronously reads one wakeup token. Completes when <see cref="Pulse"/> is called, when
    /// <paramref name="ct"/> is cancelled (throws <see cref="OperationCanceledException"/>), or
    /// when the channel is completed during dispose (returns; caller checks <paramref name="ct"/>
    /// and exits its loop).
    /// </summary>
    public ValueTask WaitAsync(CancellationToken ct);

    /// <summary>Completes the channel so all pending and future <see cref="WaitAsync"/> calls return.</summary>
    internal void Complete();
}
```

`Complete()` is `internal` because only `UploadQueueService.DisposeAsync` (same assembly) calls it. No public `IDisposable` — the signal is a value-shaped primitive; `Complete()` semantics live with the owner.

Why `ValueTask` (not `Task<Result>`): same reason as `IClock.Delay` (§3.2 Plan, "Why `Delay` doesn't return Result"). Cancellation flows as `OperationCanceledException`; the caller's catch-first ladder maps it. Wrapping in `Result` here would force boilerplate at every idle-wait site.

### `FlashSkink.Core.Upload.UploadQueueService` (sealed class, `IAsyncDisposable`)

Summary intent: per-volume background service that orchestrates one upload worker per registered tail. Owns the volume's upload `CancellationTokenSource`, the wakeup signal subscription on `INetworkAvailabilityMonitor.AvailabilityChanged`, and the brain transactions that close out each per-blob outcome. Returns a `Result` from `StartAsync` (so the public API stays Principle-1-compliant); never throws across its public surface.

Constructor:

```csharp
public UploadQueueService(
    UploadQueueRepository uploadQueueRepository,
    BlobRepository blobRepository,
    FileRepository fileRepository,
    ActivityLogRepository activityLogRepository,
    IProviderRegistry providerRegistry,
    INetworkAvailabilityMonitor networkMonitor,
    INotificationBus notificationBus,
    RangeUploader rangeUploader,
    RetryPolicy retryPolicy,
    IClock clock,
    UploadWakeupSignal wakeupSignal,
    ILogger<UploadQueueService> logger);
```

11 dependencies — large but each is genuinely needed (queue/brain reads + writes, file metadata for user-vocabulary notifications, activity log, registry, network signal, bus, the §3.3 uploader, the §3.2 policy, the §3.2 clock, the signal, the logger). No grouping into a settings/context record in V1; the dev plan's §3.6 example constructor in the lifecycle wiring uses the same flat-args shape.

Public surface:

```csharp
/// <summary>
/// Starts the orchestrator background task. Idempotent: a second call returns Ok without
/// effect. Returns Ok once the orchestrator task is enrolled; does not wait for the first
/// dequeue. Errors only on duplicate start with a different volume token or on disposed state.
/// </summary>
public Result Start(CancellationToken volumeToken);

public ValueTask DisposeAsync();
```

`Start` is **synchronous-returning**, not async: the dev plan's example pseudocode (`StartAsync(VolumeContext)`) is followed in spirit, but no `await` is needed to enrol the orchestrator (just allocate a `Task.Run` or, preferred, `Task.Factory.StartNew(..., LongRunning)`). Naming `Start` rather than `StartAsync` because there is no awaited work — Principle 13's "`ct` last on async methods" still applies (and is satisfied: `Start` takes one parameter, the volume CTS token, used solely for the internal linked-source construction). `DisposeAsync` is `ValueTask`-returning per `IAsyncDisposable`.

### `FlashSkink.Core.Metadata.UploadQueueRepository.LookupSessionAsync` (new public method)

```csharp
/// <summary>
/// Returns the persisted <see cref="UploadSessionRow"/> for the given (fileId, providerId), or
/// <see langword="null"/> when no in-flight session exists. Used by the upload worker on resume
/// (§3.4) to seed <see cref="RangeUploader.UploadAsync"/>'s <c>existingSession</c> parameter.
/// Read-only — does not insert, update, or delete. Dapper.
/// </summary>
public Task<Result<UploadSessionRow?>> LookupSessionAsync(
    string fileId, string providerId, CancellationToken ct);
```

Why this is additive to §1.6: the §1.6 surface declared `GetOrCreateSessionAsync` (read-with-upsert), `UpdateSessionProgressAsync`, and `DeleteSessionAsync`. The worker's resume path needs a pure read — calling `GetOrCreateSessionAsync` would upsert a fresh row (resetting `BytesUploaded = 0`) when no row exists, which is the wrong semantics for resume. Principle 23 frozenness applies to **provider** contracts, not internal repositories; `LookupSessionAsync` is the natural surface addition. Dev plan §3.4 calls this out explicitly: *"LookupSessionAsync is not in §1.6's surface — this PR adds it."*

## Internal types

### Inside `UploadQueueService`:

- `private readonly struct ProcessOutcome` — Bundles `MarkUploaded` vs `MarkFailed` decision data without allocating; consumed only by `ApplyOutcomeAsync`. Fields: `UploadOutcomeStatus Status`, `string? RemoteId`, `ErrorCode FailureCode`, `string FailureMessage`. Pure carrier; no behaviour.
- `private async Task OrchestratorAsync(CancellationToken ct)` — the §15.8 outer loop.
- `private async Task WorkerAsync(string providerId, CancellationToken ct)` — the §15.8 inner loop.
- `private async Task<Result> ProcessOneAsync(TailUploadRow row, IStorageProvider provider, CancellationToken ct)` — the per-blob pipeline (MarkUploading → lookup session → RangeUploader.UploadAsync → ApplyOutcomeAsync).
- `private async Task ApplyOutcomeAsync(TailUploadRow row, UploadOutcome outcome, CancellationToken ct)` — the brain-transaction surface; emits notifications and activity-log entries.
- `private async Task PublishFailureAsync(TailUploadRow row, VolumeFile? file, IStorageProvider provider, ErrorCode code, string message, NotificationSeverity severity, CancellationToken ct)` — single source of truth for the user-vocabulary notification text.
- `private async Task EnsureWorkerRunningAsync(string providerId, CancellationToken ct)` — adds a worker if one isn't already tracked.
- `private async Task StopWorkerAsync(string providerId)` — cancels and awaits a worker (10 s budget) when a provider is removed from the registry.

All workers tracked in `private readonly Dictionary<string, (Task Task, CancellationTokenSource Cts)> _workers = new();` guarded by `private readonly SemaphoreSlim _workersLock = new(1, 1);` (single-writer; orchestrator is the only mutator).

Why `Dictionary` + `SemaphoreSlim` rather than `ConcurrentDictionary`: the operations the orchestrator performs on the dictionary (ensure-running, stop-removed) are compound — *read the keys, compare against the active set, mutate*. `ConcurrentDictionary` would still need a lock to make the compare-and-mutate atomic, so a plain dictionary + `SemaphoreSlim` is simpler and avoids the false impression that concurrent access is safe without coordination.

### `TestNetworkAvailabilityMonitor` (test-only)

```csharp
internal sealed class TestNetworkAvailabilityMonitor : INetworkAvailabilityMonitor
{
    public bool IsAvailable { get; private set; } = true;
    public event EventHandler<bool>? AvailabilityChanged;
    public void SetAvailable(bool value);
}
```

`SetAvailable` no-ops on unchanged value; raises `AvailabilityChanged(this, value)` synchronously when changed.

## Method-body contracts

### `UploadWakeupSignal.Pulse`

```csharp
public void Pulse() => _channel.Writer.TryWrite(0);
```

`TryWrite` returns `false` when the channel is full or completed — both are non-events for the caller. No log, no throw. (`DropWrite` policy means a full channel is the expected steady state.)

### `UploadWakeupSignal.WaitAsync`

```csharp
public async ValueTask WaitAsync(CancellationToken ct)
{
    try
    {
        _ = await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
    }
    catch (ChannelClosedException)
    {
        // Completed during dispose; caller checks ct and exits.
    }
}
```

The `ChannelClosedException` swallow is **deliberate** and the only one in this PR. The expected flow on shutdown is: `Complete()` → reader-side `ReadAsync` faults with `ChannelClosedException` → the worker's outer `while (!ct.IsCancellationRequested)` check exits the loop. Catching `OperationCanceledException` here would defeat the cancellation contract (Principle 14) — we let it propagate. Principle 15's "narrow before broad" is satisfied: `ChannelClosedException` is specific.

### `UploadQueueRepository.LookupSessionAsync`

Catch ladder identical to existing methods. SQL:

```sql
SELECT FileID, ProviderID, SessionUri, SessionExpiresUtc,
       BytesUploaded, TotalBytes, LastActivityUtc
FROM UploadSessions
WHERE FileID = @FileId AND ProviderID = @ProviderId
```

Row mapping: same `DateTime.Parse(..., RoundtripKind)` shape used in `GetOrCreateSessionAsync` and `DequeueNextBatchAsync`. Implementer is free to extract a private static `MapSessionRow(dynamic)` helper and call it from both `GetOrCreateSessionAsync` and `LookupSessionAsync` to avoid duplication, **as long as `GetOrCreateSessionAsync`'s observable behaviour does not change**. Tests for `GetOrCreateSessionAsync` from §1.6 must remain green.

### `UploadQueueService.Start(CancellationToken volumeToken)`

**Pre:**
- Service is not yet started; not disposed.

**Steps:**
1. If `_started == 1` (`Interlocked.CompareExchange`): return `Result.Ok()` (idempotent re-Start).
2. If `_disposed == 1`: return `Result.Fail(ErrorCode.VolumeAlreadyOpen, "Upload queue service has been disposed.")`. Reuses an existing code (cross-cutting decision 2 — no new codes). Match is loose; the message is the actionable signal.
3. Construct `_serviceCts = CancellationTokenSource.CreateLinkedTokenSource(volumeToken)`; store. The volume's CTS is the parent; tearing down the volume cancels the service.
4. Subscribe `networkMonitor.AvailabilityChanged += OnAvailabilityChanged`. Handler signature: `(object?, bool isAvailable) => { if (isAvailable) wakeupSignal.Pulse(); }` — flipping to available pulses everyone; flipping to unavailable is observed at the next tick (workers will fall through to the idle wait).
5. `_orchestratorTask = Task.Factory.StartNew(() => OrchestratorAsync(_serviceCts.Token), CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();` — `LongRunning` hint because this task runs for the life of the volume; `CancellationToken.None` on `StartNew` because cancellation is observed inside the loop, not used to refuse to start.
6. Return `Result.Ok()`.

**Errors:** none expected. `Interlocked.CompareExchange` cannot fail; `CreateLinkedTokenSource` cannot fail on a valid token; `Task.Factory.StartNew` cannot fail. The outer try is `catch (Exception ex)` → `Result.Fail(ErrorCode.Unknown, ..., ex)` as defence (Principle 15: always a final fallback catch).

### `OrchestratorAsync(CancellationToken ct)`

Pseudocode mirror of §15.8, augmented for §22.4:

```
while (!ct.IsCancellationRequested) {
    try {
        if (!networkMonitor.IsAvailable) {
            // Paused — log Trace, idle wait. Network-restored event will Pulse() us.
            await Task.WhenAny(wakeup.WaitAsync(ct), clock.Delay(OrchestratorIdle, ct));
            continue;
        }

        var activeListResult = await registry.ListActiveProviderIdsAsync(ct);
        if (!activeListResult.Success) {
            logger.LogWarning("Could not list active providers: {Code}", activeListResult.Error!.Code);
            await clock.Delay(OrchestratorIdle, ct);
            continue;
        }

        var active = new HashSet<string>(activeListResult.Value!, StringComparer.Ordinal);

        // Start missing workers; stop departed workers (worker-set diff).
        await EnsureAllRunningAndPruneDepartedAsync(active, ct);

        await Task.WhenAny(wakeup.WaitAsync(ct), clock.Delay(OrchestratorIdle, ct));
    }
    catch (OperationCanceledException) { break; }  // shutdown — exit loop
    catch (Exception ex) {
        logger.LogError(ex, "Orchestrator loop iteration faulted; continuing.");
        // Defence in depth (Principle 24 — service must not silently die);
        // small fixed sleep to avoid hot loop.
        try { await clock.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { break; }
    }
}
```

The `Task.WhenAny` between `wakeup.WaitAsync` and `clock.Delay(OrchestratorIdle, ct)` is the canonical "wake on signal OR poll cap" pattern. Both inner tasks observe `ct`; cancellation cascades through `Task.WhenAny`. The wakeup's `WaitAsync` swallows `ChannelClosedException` so disposal cleanly returns; cancellation propagates as `OperationCanceledException`.

`OrchestratorIdle` = `TimeSpan.FromSeconds(UploadConstants.OrchestratorIdlePollSeconds)`. The constant was added in §3.1.

### `EnsureAllRunningAndPruneDepartedAsync(HashSet<string> active, CancellationToken ct)`

```
await _workersLock.WaitAsync(ct);
try {
    foreach (var id in active) {
        if (!_workers.ContainsKey(id)) {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCts.Token);
            var task = Task.Factory.StartNew(() => WorkerAsync(id, cts.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();
            _workers[id] = (task, cts);
            logger.LogInformation("Worker started for provider {ProviderId}", id);
        }
    }

    var departed = _workers.Keys.Where(k => !active.Contains(k)).ToList();
    foreach (var id in departed) {
        await StopWorkerLocked(id);
    }
} finally {
    _workersLock.Release();
}
```

`StopWorkerLocked(id)`:
- Cancel the worker's CTS.
- `await Task.WhenAny(workerTask, Task.Delay(TimeSpan.FromSeconds(10)))` — 10 s budget per dev plan §3.4 "Key constraints".
- If still running, log `Warning` and **leave the task** — calling `Wait` would block dispose indefinitely (Principle 24's "must not silently die" applies to the *failure surface*; the orchestrator continues).
- `_workers.Remove(id)`; dispose the CTS.

### `WorkerAsync(string providerId, CancellationToken ct)`

```
const string Source = "UploadQueueService";  // notification Source

while (!ct.IsCancellationRequested) {
    try {
        if (!networkMonitor.IsAvailable) {
            await Task.WhenAny(wakeup.WaitAsync(ct), clock.Delay(WorkerIdle, ct));
            continue;
        }

        var providerResult = await registry.GetAsync(providerId, ct);
        if (!providerResult.Success) {
            // Provider removed mid-flight; orchestrator will prune us on next tick.
            await clock.Delay(WorkerIdle, ct);
            continue;
        }
        var provider = providerResult.Value!;

        bool processedAny = false;
        await foreach (var row in uploadQueueRepository.DequeueNextBatchAsync(providerId, batchSize: 1, ct)) {
            processedAny = true;
            var processResult = await ProcessOneAsync(row, provider, ct);
            if (!processResult.Success) {
                // Brain bookkeeping itself faulted — log; the row is in an indeterminate
                // state (UPLOADING with no terminal flip). Phase 5 WAL recovery sweeps it back
                // to PENDING. For now, fall through to the idle wait so we don't spin.
                logger.LogError(
                    "Brain bookkeeping faulted for file {FileId} on {ProviderId}: {Code}",
                    row.FileId, providerId, processResult.Error!.Code);
                break;
            }
        }

        if (!processedAny) {
            await Task.WhenAny(wakeup.WaitAsync(ct), clock.Delay(WorkerIdle, ct));
        }
    }
    catch (OperationCanceledException) { break; }
    catch (SqliteException ex) {
        // Principle 1 carve-out — DequeueNextBatchAsync is the sanctioned IAsyncEnumerable
        // path; SqliteException propagates. We catch here at the worker boundary.
        logger.LogError(ex, "SQLite error in worker for {ProviderId}; idling and continuing.", providerId);
        try { await clock.Delay(WorkerIdle, ct); } catch (OperationCanceledException) { break; }
    }
    catch (Exception ex) {
        logger.LogError(ex, "Worker loop iteration faulted for {ProviderId}; idling and continuing.", providerId);
        try { await clock.Delay(WorkerIdle, ct); } catch (OperationCanceledException) { break; }
    }
}
```

`WorkerIdle` = `TimeSpan.FromSeconds(UploadConstants.WorkerIdlePollSeconds)` (60 s per §3.1).

The catch ladder explicitly includes `SqliteException` because `DequeueNextBatchAsync` is the §9.7 raw-reader carve-out method (Principle 1 sanctioned exception). The worker must absorb its exceptions or it would silently die on a single transient SQLite error.

### `ProcessOneAsync(TailUploadRow row, IStorageProvider provider, CancellationToken ct)`

```
// 1. Look up the blob.
var blobResult = await blobRepository.GetByIdAsync(/* via Files row's BlobID */, ct);
//   ─ but first we need the file's BlobID:
var fileResult = await fileRepository.GetByIdAsync(row.FileId, ct);
if (!fileResult.Success) return Result.Fail(fileResult.Error!);
if (fileResult.Value is null || fileResult.Value.BlobId is null) {
    logger.LogError("File {FileId} missing or has no BlobID; marking failed.", row.FileId);
    var mark = await uploadQueueRepository.MarkFailedAsync(
        row.FileId, row.ProviderId, $"{ErrorCode.FileNotFound}: file row missing or no blob.", ct);
    if (!mark.Success) return Result.Fail(mark.Error!);
    // No notification — this is a programming/data error, not a background failure the user can act on;
    // log path is sufficient. (Optional Gate-1 callout — see Open questions.)
    return Result.Ok();
}
var blobResult = await blobRepository.GetByIdAsync(fileResult.Value.BlobId, ct);
if (!blobResult.Success) return Result.Fail(blobResult.Error!);
if (blobResult.Value is null) {
    logger.LogError("Blob {BlobId} missing for file {FileId}; marking failed.", fileResult.Value.BlobId, row.FileId);
    var mark = await uploadQueueRepository.MarkFailedAsync(row.FileId, row.ProviderId, "Blob row missing", ct);
    if (!mark.Success) return Result.Fail(mark.Error!);
    return Result.Ok();
}

// 2. Flip to UPLOADING (increments AttemptCount, which is the cycle counter).
var markUploading = await uploadQueueRepository.MarkUploadingAsync(row.FileId, row.ProviderId, ct);
if (!markUploading.Success) return Result.Fail(markUploading.Error!);

// 3. Look up resumable session (read-only — does not insert).
var sessionResult = await uploadQueueRepository.LookupSessionAsync(row.FileId, row.ProviderId, ct);
if (!sessionResult.Success) return Result.Fail(sessionResult.Error!);
var existingSession = sessionResult.Value;

// 4. Resolve absolute path from skink root + blob.BlobPath. The volume's SkinkRoot is
//    not on this service; it is on VolumeContext (§3.6 wires it). For Phase 3, we accept
//    skinkRoot as a constructor parameter — see Open questions, item 1.
var blobAbsolutePath = Path.Combine(_skinkRoot, blobResult.Value!.BlobPath);

// 5. Delegate to RangeUploader.
var uploadResult = await rangeUploader.UploadAsync(
    row.FileId, row.ProviderId, provider, blobResult.Value!, blobAbsolutePath, existingSession, ct);
if (!uploadResult.Success) {
    // Cancellation or brain bookkeeping fault propagated from RangeUploader.
    // Cancellation: preserve UploadSessions row, exit (Principle 14).
    // Other Result.Fail: log; the row is UPLOADING; Phase 5 WAL sweep recovers.
    if (uploadResult.Error!.Code == ErrorCode.Cancelled) return Result.Fail(uploadResult.Error!);
    logger.LogError(
        "RangeUploader returned Result.Fail for {FileId}: {Code} {Message}",
        row.FileId, uploadResult.Error.Code, uploadResult.Error.Message);
    return Result.Fail(uploadResult.Error!);
}

// 6. Apply the per-blob outcome — single brain transaction inside ApplyOutcomeAsync.
await ApplyOutcomeAsync(row, fileResult.Value!, provider, uploadResult.Value!, ct);
return Result.Ok();
```

### `ApplyOutcomeAsync(TailUploadRow row, VolumeFile file, IStorageProvider provider, UploadOutcome outcome, CancellationToken ct)`

This is the §15.3 step 7c surface — the brain transitions that close out a per-blob upload. The dev plan §3.4 specifies "single brain transaction per case". V1 implementation:

> **Transactional behaviour.** `MarkUploadedAsync`, `MarkFailedAsync`, `DeleteSessionAsync`, and `ActivityLogRepository.AppendAsync` are each individual `SqliteConnection.ExecuteAsync` calls in V1, not wrapped in an explicit `SqliteTransaction`. SQLite-with-WAL semantics already make each `UPDATE` atomic, and the failure-recovery surface (Phase 5 WAL sweep) is the same whether or not we wrap. **However**, to match the dev plan's "single transaction" requirement on §15.3 step 7c (status flip + session delete), this implementation opens an explicit `SqliteTransaction` for the Completed and PermanentFailure cases that covers both the status flip and the session delete. The activity-log append uses `CancellationToken.None` (Principle 17) and runs *after* the transaction commits — log entries are a user-facing artefact, not part of the consistency invariant. If the log append fails after the upload-marking transaction commits, the row state is correct and only the audit trail is short by one row; logged at `Warning`.

Case logic:

```
switch (outcome.Status) {
    case UploadOutcomeStatus.Completed:
        await using (var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct))
        {
            // The repositories share the volume's _connection; the active transaction binds.
            var mu = await uploadQueueRepository.MarkUploadedAsync(row.FileId, row.ProviderId, outcome.RemoteId!, ct);
            if (!mu.Success) { await tx.RollbackAsync(CancellationToken.None); /* propagate by throwing? — see note */ }
            var ds = await uploadQueueRepository.DeleteSessionAsync(row.FileId, row.ProviderId, ct);
            if (!ds.Success) { await tx.RollbackAsync(CancellationToken.None); }
            await tx.CommitAsync(ct);
        }
        await activityLogRepository.AppendAsync(new ActivityLogEntry {
            EntryId = Guid.NewGuid().ToString(),
            OccurredUtc = clock.UtcNow,
            Category = "UPLOADED",
            Summary = $"Uploaded '{file.VirtualPath}' to '{provider.DisplayName}'.",
        }, CancellationToken.None);
        // No notification (success is routine, Principle 24 covers failures only).
        break;

    case UploadOutcomeStatus.RetryableFailure:
        // The in-range ladder escalated. Mark FAILED with the pending code, then consult cycle ladder.
        var lastError = $"{outcome.FailureCode}: {outcome.FailureMessage}";
        await uploadQueueRepository.MarkFailedAsync(row.FileId, row.ProviderId, lastError, ct);
        // The cycle counter is TailUploads.AttemptCount, which was incremented inside
        // MarkUploadingAsync at the top of ProcessOneAsync. row.AttemptCount + 1 is the
        // attempt number this cycle just represented.
        int cycleNumber = row.AttemptCount + 1;
        var decision = retryPolicy.NextCycleAttempt(cycleNumber);
        if (decision.Outcome == RetryOutcome.MarkFailed) {
            // 5th cycle exhausted — promote to permanent.
            await PublishFailureAsync(row, file, provider,
                outcome.FailureCode!.Value, outcome.FailureMessage ?? "Upload failed.",
                NotificationSeverity.Error, ct);
            // (Row is already FAILED from MarkFailedAsync above; no further write needed.)
            // Best-effort session delete — failed cycles leave a stale session if the provider
            // already rejected it; cleanup is part of the "MarkFailed" semantics.
            await uploadQueueRepository.DeleteSessionAsync(row.FileId, row.ProviderId, CancellationToken.None);
            await activityLogRepository.AppendAsync(new ActivityLogEntry {
                EntryId = Guid.NewGuid().ToString(),
                OccurredUtc = clock.UtcNow,
                Category = "UPLOAD_FAILED",
                Summary = $"Stopped retrying upload of '{file.VirtualPath}' to '{provider.DisplayName}'.",
            }, CancellationToken.None);
        } else {
            // Schedule next cycle: idle this worker for decision.Delay, then re-dequeue.
            // The next DequeueNextBatchAsync will see this row at FAILED (which is included by the
            // filter) and reprocess. AttemptCount remains > 0; on next entry, MarkUploadingAsync
            // increments it again, advancing the cycle.
            logger.LogInformation(
                "Cycle {Cycle} failed for {FileId} on {ProviderId}; next attempt after {Delay}",
                cycleNumber, row.FileId, row.ProviderId, decision.Delay);
            await clock.Delay(decision.Delay, ct);
        }
        break;

    case UploadOutcomeStatus.PermanentFailure:
        await using (var tx2 = (SqliteTransaction)await _connection.BeginTransactionAsync(ct))
        {
            var mf = await uploadQueueRepository.MarkFailedAsync(
                row.FileId, row.ProviderId, $"{outcome.FailureCode}: {outcome.FailureMessage}", ct);
            if (!mf.Success) { await tx2.RollbackAsync(CancellationToken.None); }
            var ds2 = await uploadQueueRepository.DeleteSessionAsync(row.FileId, row.ProviderId, ct);
            if (!ds2.Success) { await tx2.RollbackAsync(CancellationToken.None); }
            await tx2.CommitAsync(ct);
        }
        await PublishFailureAsync(row, file, provider,
            outcome.FailureCode!.Value, outcome.FailureMessage ?? "Upload failed.",
            NotificationSeverity.Error, ct);
        await activityLogRepository.AppendAsync(new ActivityLogEntry {
            EntryId = Guid.NewGuid().ToString(),
            OccurredUtc = clock.UtcNow,
            Category = "UPLOAD_FAILED",
            Summary = $"Could not upload '{file.VirtualPath}' to '{provider.DisplayName}'.",
        }, CancellationToken.None);
        break;
}
```

**The transaction wrapper question.** The current repositories take `SqliteConnection` only, not `SqliteTransaction`. To make the §15.3-step-7c "single transaction" promise real, **this PR adds an overload-friendly pattern**: instead of changing the existing repository methods, `ApplyOutcomeAsync` calls them sequentially under an explicit `tx = await _connection.BeginTransactionAsync(ct)` and relies on `Microsoft.Data.Sqlite`'s connection-binds-active-transaction semantics — every `ExecuteAsync` on the shared connection participates in the active transaction. This **does** work in `Microsoft.Data.Sqlite` (Dapper's `CommandDefinition` constructor used without an explicit transaction picks up the active one), but it is fragile against a future implementer who calls `_connection.BeginTransaction` somewhere else. **See Open questions, item 2** — flagged so Gate 1 can pick a clear path.

The `RetryableFailure` case does **not** wrap in a single transaction: it does `MarkFailed` (one statement) and then either schedules retry or promotes to permanent. There is nothing to make atomic with the `MarkFailed`.

### `PublishFailureAsync(...)`

```
var bus = notificationBus;
var title = "Could not back up file";  // Principle 25: user vocabulary
var message = file is null
    ? $"Could not upload one of your files to '{provider.DisplayName}'."
    : $"Could not upload '{file.VirtualPath}' to '{provider.DisplayName}'.";

await bus.PublishAsync(new Notification {
    Source = "UploadQueueService",
    Severity = severity,
    Title = title,
    Message = message,
    Error = new ErrorContext { Code = code, Message = errorMessage },
    OccurredUtc = clock.UtcNow,
    RequiresUserAction = false,
}, CancellationToken.None);  // Principle 17 — notification publish is bookkeeping; not cancellable mid-flight
```

Notification text deliberately avoids "tail", "blob", "session", "range", "WAL". The dev plan §3.4 quotes the canonical title: *"Could not back up file `{virtualPath}` to `{tailDisplayName}`"* — the implementation uses "upload" rather than "back up" because that matches the file-level Phase 1 vocabulary the user already sees in the Activity Log ("Uploaded …"). Either is acceptable per Principle 25; consistency wins. **Open question 3** lists the choice.

### `DisposeAsync`

```
if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

// Cancel the service CTS — orchestrator and workers observe this on the next await.
try { _serviceCts?.Cancel(); } catch { /* already disposed */ }

// Unsubscribe before waiting to avoid late event re-pulses.
networkMonitor.AvailabilityChanged -= OnAvailabilityChanged;

// Wait for the orchestrator first (it owns worker references).
if (_orchestratorTask is not null) {
    try { await _orchestratorTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
    catch (TimeoutException) { logger.LogWarning("Orchestrator did not stop within 10s."); }
    catch (OperationCanceledException) { /* expected */ }
}

// Stop and await each worker (10s budget per worker, parallelised).
await _workersLock.WaitAsync(CancellationToken.None);
try {
    var workerTasks = new List<Task>();
    foreach (var (id, entry) in _workers) {
        try { entry.Cts.Cancel(); } catch { }
        workerTasks.Add(WaitWithBudget(entry.Task, TimeSpan.FromSeconds(10), id));
    }
    await Task.WhenAll(workerTasks);
    foreach (var entry in _workers.Values) {
        entry.Cts.Dispose();
    }
    _workers.Clear();
} finally {
    _workersLock.Release();
}

// Complete the wakeup signal so any straggler returns.
wakeupSignal.Complete();

_serviceCts?.Dispose();
_workersLock.Dispose();

async Task WaitWithBudget(Task task, TimeSpan budget, string providerId) {
    try { await task.WaitAsync(budget, CancellationToken.None); }
    catch (TimeoutException) {
        logger.LogWarning("Worker for {ProviderId} did not stop within {Budget}.", providerId, budget);
    }
    catch (OperationCanceledException) { /* expected on graceful exit */ }
}
```

Per-worker awaits run in parallel (`Task.WhenAll`), so a single misbehaving worker can't stretch volume close to *N × 10s*; total close is bounded by the slowest single worker plus orchestrator wait, ~20 s worst case.

`wakeupSignal.Complete()` is called after the workers are awaited because completing the channel could cause `WaitAsync` to fault before the worker had a chance to observe its own CTS — the order avoids spurious shutdown logs.

## Integration points

This PR consumes:

- `FlashSkink.Core.Abstractions.Providers.IStorageProvider` — through `provider` resolved via the registry; never instantiated here.
- `FlashSkink.Core.Abstractions.Providers.IProviderRegistry` — `GetAsync`, `ListActiveProviderIdsAsync`.
- `FlashSkink.Core.Abstractions.Providers.INetworkAvailabilityMonitor` — `IsAvailable` + `AvailabilityChanged`.
- `FlashSkink.Core.Abstractions.Time.IClock` — `Delay`, `UtcNow`.
- `FlashSkink.Core.Abstractions.Notifications.INotificationBus` + `Notification` + `NotificationSeverity` — publish path.
- `FlashSkink.Core.Abstractions.Models.VolumeFile` + `BlobRecord` + `ActivityLogEntry` + `ErrorContext` + `ErrorCode`.
- `FlashSkink.Core.Upload.RangeUploader.UploadAsync` (§3.3).
- `FlashSkink.Core.Upload.RetryPolicy.NextCycleAttempt` (§3.2).
- `FlashSkink.Core.Upload.UploadConstants.OrchestratorIdlePollSeconds` and `WorkerIdlePollSeconds` (§3.1).
- `FlashSkink.Core.Metadata.UploadQueueRepository` (`DequeueNextBatchAsync`, `MarkUploadingAsync`, `MarkUploadedAsync`, `MarkFailedAsync`, `LookupSessionAsync` (new), `DeleteSessionAsync`).
- `FlashSkink.Core.Metadata.BlobRepository.GetByIdAsync`.
- `FlashSkink.Core.Metadata.FileRepository.GetByIdAsync`.
- `FlashSkink.Core.Metadata.ActivityLogRepository.AppendAsync`.
- `Microsoft.Data.Sqlite.SqliteConnection.BeginTransactionAsync` — for the two cases that need atomicity.

Not consumed: `WritePipeline`, `FlashSkinkVolume`, `BackgroundFailureRepository` (the existing `PersistenceNotificationHandler` is the bus subscriber that writes there — `UploadQueueService` publishes through the bus and never touches the persistence path directly). `BrainMirrorService` does not exist yet (§3.5).

## Principles touched

- **Principle 1** — every public method on `UploadQueueService` (`Start`, `DisposeAsync`) returns `Result` or is `IAsyncDisposable.DisposeAsync`. `UploadWakeupSignal.Pulse` returns `void` because it is a sanctioned pure side-effect: `TryWrite` cannot fail in any caller-actionable way. `UploadWakeupSignal.WaitAsync` returns `ValueTask` for the same reason as `IClock.Delay` (cancellation as `OperationCanceledException`, wrap-at-consumer-boundary). The XML comment on `WaitAsync` cites the §3.2 precedent.
- **Principle 2** — per-tail isolation explicitly enforced. A failed tail produces a `MarkFailed` row + notification; other tails' workers are untouched.
- **Principle 3** — the worker reads the blob from `Path.Combine(_skinkRoot, blob.BlobPath)`; never from a tail.
- **Principle 4** — `UploadQueueService` runs entirely after Phase 1 commit. Tail uploads cannot block writes.
- **Principle 5** — the `UploadSessions` row is preserved across `OperationCanceledException` (`ProcessOneAsync` returns `Result.Fail(Cancelled)` and the row stays in place; next session resumes via `LookupSessionAsync`).
- **Principle 6** — every byte passed to `provider.UploadRangeAsync` is ciphertext from the skink; `UploadQueueService` does no plaintext touching.
- **Principle 8** — all new types live in `Core/Upload/` and `Core/Metadata/`. No UI reference. No Presentation reference. (The `INotificationBus` interface lives in `Core.Abstractions.Notifications` per §8.3; the concrete `NotificationBus` lives in Presentation, but `UploadQueueService` only depends on the interface.)
- **Principle 13** — every async method takes `CancellationToken ct` last.
- **Principle 14** — `OperationCanceledException` is the first catch in `OrchestratorAsync`, `WorkerAsync`, `ProcessOneAsync`, and `Start`. In `WaitAsync` it is allowed to propagate (no catch by design — the consumer ladder maps).
- **Principle 15** — `WorkerAsync` has `SqliteException` (narrow) before `Exception` (broad). `OrchestratorAsync` has `OperationCanceledException` before `Exception`.
- **Principle 16** — `_serviceCts`, every per-worker `CancellationTokenSource`, `_workersLock`, and the shared `wakeupSignal` are disposed in `DisposeAsync`. The CTSes are disposed *after* `Task.WhenAll` returns so a still-running worker can't reference a disposed token.
- **Principle 17** — `CancellationToken.None` literal at:
  - `ActivityLogRepository.AppendAsync` in `ApplyOutcomeAsync` (post-commit bookkeeping must not be cancellable);
  - `INotificationBus.PublishAsync` in `PublishFailureAsync`;
  - Best-effort `DeleteSessionAsync` after a permanent / cycle-exhausted failure;
  - Every `await` site inside `DisposeAsync`;
  - The cancellation-cleanup callback registered with `CancellationTokenRegistration`.
  Every site is a *literal* `CancellationToken.None`, not a local variable (Principle 17).
- **Principle 18** — no hot-path allocation in the steady-state loop. `OrchestratorAsync` allocates one `HashSet<string>` per tick; that's tens of bytes once every 30 s. The `worker → ProcessOneAsync` path's allocation is dominated by the brain reads (which themselves use Dapper for non-hot paths per Principle 22 — single-row reads of one file and one blob). The wakeup channel is allocation-free in steady state. `Notification` instances are allocated only on failure — not steady-state path.
- **Principle 22** — `DequeueNextBatchAsync` is the §9.7 raw-reader path (consumed here for the first time as the hot path). All other queue/session reads use Dapper. Hand-rolled SQL only inside repositories.
- **Principle 23** — `IStorageProvider`, `UploadSession`, `IProviderRegistry`, `INetworkAvailabilityMonitor` are not modified. The new `UploadQueueRepository.LookupSessionAsync` is an internal-repository addition, not a frozen-contract change.
- **Principle 24** — every `MarkFailed` outcome path: (a) logs via `ILogger<UploadQueueService>` at `Error` (cycle-exhausted) or `Warning` (mid-cycle); (b) publishes a `NotificationSeverity.Error` notification; (c) the existing `PersistenceNotificationHandler` (Phase 2) writes the row to `BackgroundFailures`. No new persistence wiring here — the existing handler picks up the publish.
- **Principle 25** — notification `Title` is "Could not back up file"; `Message` is `"Could not upload '{virtualPath}' to '{providerDisplayName}'."` Activity log Summary uses the same vocabulary. **No** appliance words ("tail", "blob", "session", "range", "cycle", "WAL", "OAuth") in any user-facing string. `ErrorContext.Code` carries the `ErrorCode` for handlers that want it; it does not appear in the user-facing strings.
- **Principle 26** — no secrets logged. `ErrorContext.Metadata` is never populated by this service; tokens, DEK, mnemonics never traverse `UploadQueueService`. Logger fields are `FileId` (UUID), `ProviderId` (display-ID string), `BytesUploaded` (long), `Cycle` (int), `Delay` (`TimeSpan`), `ErrorCode` (enum name).
- **Principle 27** — every `Result.Fail` site in `UploadQueueService` logs once at the construction site; callers (volume integration in §3.6) log the returned `ErrorContext`. The same event is not double-logged.
- **Principle 28** — `ILogger<UploadQueueService>` from `Microsoft.Extensions.Logging.Abstractions`. No Serilog reference in Core.
- **Principle 30** — the worker preserves the §21.3 invariant for `UploadSessions` / `TailUploads`: on every brain mutation site, either the session row is preserved (mid-cycle, cancellation) or it is deleted in the same logical transaction as the terminal flip (Completed, PermanentFailure). The test spec includes an `Invariant_AfterCompletedUpload_UploadSessionsIsEmpty` assertion across each happy-path case.
- **Principle 32** — every outbound call is via the resolved `IStorageProvider`. No direct HTTP. No telemetry.

## Test spec

All tests in `tests/FlashSkink.Tests/Upload/`. Each test constructs a fresh in-memory brain via `BrainTestHelper.CreateInMemoryConnection` + `ApplySchemaAsync`, a per-test temp tail-root, a `FileSystemProvider` rooted there (optionally wrapped in `FaultInjectingStorageProvider`), an `InMemoryProviderRegistry`, a `TestNetworkAvailabilityMonitor`, a `FakeClock`, a `RangeUploader` over the same dependencies, a `RecordingNotificationBus` (existing test fixture), and the `UploadQueueService` under test.

### `tests/FlashSkink.Tests/Upload/UploadWakeupSignalTests.cs`

- `Pulse_BeforeWait_WaitCompletesImmediately`
- `Pulse_DuringWait_WaitCompletes`
- `PulseMultiple_OnlyOneWaitCompletes` — second `WaitAsync` blocks until next `Pulse` (DropWrite coalescing).
- `Wait_TokenPreCancelled_ThrowsOperationCanceled`
- `Wait_TokenCancelledMidWait_ThrowsOperationCanceled`
- `Complete_PendingWait_ReturnsWithoutThrow` — `WaitAsync` swallows `ChannelClosedException`.
- `Pulse_AfterComplete_NoOp`
- `MultipleWaiters_OnePulse_OneCompletes` — verifies `SingleReader = false` semantics: two concurrent waiters; one Pulse releases exactly one.

### `tests/FlashSkink.Tests/Upload/UploadQueueServiceTests.cs`

**Construction & lifecycle:**
- `Start_FirstCall_BeginsOrchestrator`
- `Start_SecondCall_ReturnsOkWithoutSideEffect`
- `Start_AfterDispose_ReturnsFail`
- `DisposeAsync_BeforeStart_ReturnsCleanly`
- `DisposeAsync_AfterStart_StopsOrchestratorWithinBudget`
- `DisposeAsync_Idempotent`

**Happy path:**
- `Worker_PicksUpPendingRow_UploadsAndMarksUploaded` — enqueue one `TailUploads` row; write a 1 MiB blob to the skink; register a `FileSystemProvider`; `Start`; `Pulse`; advance `FakeClock` to drain any retry delays; await completion (poll the row status with a wall-clock timeout); assert `Status == 'UPLOADED'`, `RemoteId` is set, `UploadSessions` row deleted, `ActivityLog` has one `UPLOADED` row.
- `Worker_MultipleRows_ProcessesInQueueOrder` — three rows queued at different `QueuedUtc` values; assert order via the order of `UPLOADED` entries in `ActivityLog`.

**Per-tail isolation:**
- `CrossTail_FaultyProviderDoesNotBlockHealthyProvider` — register two providers: A wraps `FailNextBeginWith(ProviderUnreachable)` repeatedly (will end up cycle-failing); B is clean. Enqueue one row per provider for the same logical file. Assert B completes; A enters cycle ladder. Verify B's completion does not require advancing the FakeClock through A's 5/30/120/720-minute waits.

**Resume across "host change":**
- `Worker_RestartWithExistingSession_ResumesFromBytesUploaded` — write a 12 MiB blob; pre-insert an `UploadSessions` row at `BytesUploaded = 8 MiB`; pre-write 8 MiB into the tail's `.partial`. Start service; assert only the last 4 MiB is uploaded (verify via `FaultInjectingStorageProvider`'s range counter) and final blob equals source bytes.

**Session expiry:**
- `Worker_ProviderRejectsSessionExpired_RestartsAndCompletes` — `FaultInjectingStorageProvider.ForceSessionExpiryAfter(2)`; assert final blob completes correctly; observe `UPLOADED` row at end.

**Per-range retry inside a single cycle:**
- `Worker_TransientFailureOnce_RangeUploaderRetriesAndCompletes` — `FailNextRangeWith(ProviderUnreachable)` once; advance `FakeClock` by 1 s; assert blob completes in one cycle (`AttemptCount == 1`).

**Cycle escalation:**
- `Worker_InRangeBudgetExhaustedOnce_MarksFailedThenRescheduleAfterCycleDelay` — `FailNextRange` four times to exhaust the in-range ladder, then clear faults. Assert `MarkFailed` happens, the worker idles via `clock.Delay(5min, ct)`, advancing `FakeClock` 5 minutes releases the next dequeue, the row goes through a fresh `UPLOADING` flip (`AttemptCount == 2`), and on second cycle completes — final status `UPLOADED`.
- `Worker_FiveCyclesAllFail_PromotesToFailedAndNotifies` — make every range fail. Advance `FakeClock` through 1s/4s/16s × 5 cycles plus 5m/30m/2h/12h between. After the 5th cycle, assert: `Status == 'FAILED'`, `LastError` populated, `UploadSessions` row deleted, exactly one `Error`-severity `Notification` published (Title "Could not back up file"), one `UPLOAD_FAILED` row in `ActivityLog`, and `BackgroundFailures` (via the live `PersistenceNotificationHandler` registered on the test bus) has the row.

**Permanent failures:**
- `Worker_ProviderAuthFailed_PromotesToFailedImmediately_NoRetry` — `[Theory]` over `ProviderAuthFailed`, `ProviderQuotaExceeded`, `TokenRevoked`, `ChecksumMismatch`. Assert `AttemptCount == 1` (single MarkUploading flip), `Status == 'FAILED'`, notification published, no `FakeClock` retry delays observed.

**Cancellation:**
- `DisposeAsync_MidRangeUpload_PreservesSessionRow` — start, enqueue a 20 MiB blob, observe a partial upload in progress (use a `FaultInjectingStorageProvider.SetRangeLatency(TimeSpan.FromSeconds(30))` on real time, **not** `FakeClock` — this test needs real cancellation through an in-flight HTTP-equivalent). `DisposeAsync` returns within 10s budget. After dispose, the `UploadSessions` row is preserved with `BytesUploaded` reflecting the last confirmed range.

**Network availability gating:**
- `NetworkOffline_NewRow_DoesNotUpload` — set monitor to offline before `Start`; enqueue; `Pulse`; advance `FakeClock` 5 minutes — assert row remains `PENDING`.
- `NetworkRestored_UploadProceedsWithinOneTick` — start offline as above; flip to online (raises `AvailabilityChanged`); assert upload begins within one orchestrator idle period.

**Orchestrator worker management:**
- `Orchestrator_ProviderRegisteredAfterStart_WorkerSpawns` — start with empty registry; register a provider mid-flight; pulse; assert a worker eventually picks up an enqueued row.
- `Orchestrator_ProviderRemoved_WorkerShutsDown` — register provider, start, observe worker running; remove from registry; advance orchestrator tick; assert worker task completes within budget. (Inspect via test-only hook: a public `int ActiveWorkerCount` debug property gated by `[Conditional("DEBUG")]` or via the existing log output captured by `RecordingLogger`.) **Open question 4** — pick the introspection mechanism.

**Crash-consistency invariants:**
- `Invariant_AfterCompletedUpload_UploadSessionsIsEmpty`
- `Invariant_AfterFailedFinal_UploadSessionsIsEmpty`
- `Invariant_AfterCancellation_UploadSessionsPreservedWithLastConfirmedBytes`
- `Invariant_TailUploadsUploadedRowsHaveNoMatchingUploadSessions` — for every row at `UPLOADED`, no `UploadSessions` row exists with the same `(FileID, ProviderID)`.

The FsCheck-driven property test from dev plan §3.4 acceptance criteria ("crash-at-line-N" interleavings) is **deferred to a follow-up PR** — see Open questions, item 5.

### `tests/FlashSkink.Tests/Metadata/UploadQueueRepositoryTests.cs` (additions)

- `LookupSessionAsync_NoRow_ReturnsNull`
- `LookupSessionAsync_ExistingRow_ReturnsSnapshot` — insert via `GetOrCreateSessionAsync`, lookup, assert all fields match.
- `LookupSessionAsync_DoesNotMutate` — call lookup three times; assert `LastActivityUtc` and `BytesUploaded` unchanged across calls (no UPDATE happens).
- `LookupSessionAsync_Cancellation_ReturnsCancelled`

### Existing tests

All Phase 0–2 + §3.1 + §3.2 + §3.3 tests must remain green. `RangeUploaderTests` is unaffected — `RangeUploader` is unchanged. `UploadQueueRepositoryTests` gains the four new methods listed above and existing tests stay green.

## Acceptance criteria

- [ ] All listed files exist; build clean on `ubuntu-latest` and `windows-latest` with `--warnaserror`.
- [ ] `dotnet test` green: all Phase 0–2 + §3.1 + §3.2 + §3.3 tests still pass; all new §3.4 tests pass.
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] `UploadQueueService`, `UploadWakeupSignal` are public, documented, live in `FlashSkink.Core.Upload`.
- [ ] `UploadQueueRepository.LookupSessionAsync` is public, documented.
- [ ] `IStorageProvider`, `UploadSession`, `IProviderRegistry`, `INetworkAvailabilityMonitor`, `IClock`, `RangeUploader`, `RetryPolicy` unmodified (`git diff` clean on those files).
- [ ] `ErrorCode.cs` unmodified (cross-cutting decision 2).
- [ ] Brain schema unmodified.
- [ ] `WritePipeline`, `FlashSkinkVolume`, `BackgroundFailureRepository`, `PersistenceNotificationHandler` unmodified.
- [ ] Every public method on `UploadQueueService` returns `Result` or implements `IAsyncDisposable.DisposeAsync`. No exceptions cross the public boundary.
- [ ] Notification `Title` and `Message` strings in `UploadQueueService` source contain none of: `"tail"`, `"blob"`, `"session"`, `"range"`, `"WAL"`, `"OAuth"`, `"DEK"`, `"KEK"`, `"AAD"`. (Mechanical: a `dotnet test` assertion that greps the assembly's string constants.) `"Tail"` in `tailDisplayName`-style identifier names is fine; this rule is for user-facing strings only — Tests check the rendered `Notification.Title` and `Notification.Message` against the prohibited token list.
- [ ] Every `Result.Fail` site in `UploadQueueService` is preceded by exactly one `_logger.Log{Information|Warning|Error}` call (Principle 27 — log-once at construction).
- [ ] `Five-cycle escalation` integration test verifies the §21.1 number sequence end-to-end against `FakeClock`.

## Line-of-code budget

| File | Approx LOC |
|---|---|
| `Core/Upload/UploadWakeupSignal.cs` | 75 |
| `Core/Upload/UploadQueueService.cs` | 520 |
| `Core/Metadata/UploadQueueRepository.cs` (delta) | +45 |
| **src subtotal** | **~640** |
| `tests/Upload/UploadWakeupSignalTests.cs` | 140 |
| `tests/Upload/UploadQueueServiceTests.cs` | 720 |
| `tests/Metadata/UploadQueueRepositoryTests.cs` (delta) | +90 |
| `tests/_TestSupport/TestNetworkAvailabilityMonitor.cs` | 50 |
| **tests subtotal** | **~1000** |
| **Total** | **~1640** |

The largest PR in Phase 3 so far. Splitting would be artificial: a service-without-tests PR is not a meaningful checkpoint, and the cycle-ladder coverage is exactly what makes the orchestration testable. The dev plan §3.4 explicitly scopes this as one PR.

## Non-goals

- **No `FlashSkinkVolume` integration / `RegisterTailAsync` / `WriteBulkAsync` / `WritePipeline → Pulse()` wiring.** §3.6.
- **No `BrainMirrorService`.** §3.5. (Notification bus / activity log are touched here but only as consumers; the brain-mirror service is not yet constructed.)
- **No WAL recovery sweep on startup.** Phase 5. Orphaned `UPLOADING` rows from a crash mid-upload are documented as a known-limitation: until Phase 5's startup sweep ships, a crash during `RangeUploader.UploadAsync` leaves the row at `UPLOADING` and the next session does not re-dequeue it. Mitigation: until Phase 5, the user can manually re-enqueue by `MarkFailed`-ing the row through a CLI utility (out of scope for this PR). The acceptance test `DisposeAsync_MidRangeUpload_PreservesSessionRow` covers the **clean shutdown** path; the **crash** path is unaddressed here by design.
- **No `HealthMonitorService` integration.** Phase 5. `RetryPolicy` remains health-blind; the orchestrator's only gating signal is `INetworkAvailabilityMonitor`.
- **No real `NetworkAvailabilityMonitor`.** Phase 5. Tests use `TestNetworkAvailabilityMonitor`; production runs with the §3.1 `AlwaysOnlineNetworkMonitor` until Phase 5.
- **No `IClock` host-side DI registration.** Phase 6.
- **No new `ErrorCode` values.** Cross-cutting decision 2.
- **No notification deduplication tuning.** The existing `NotificationDispatcher` 60-second `Source + ErrorCode` dedup window (§8.4) governs; no override here.
- **No GUI surface / CLI surface for upload status / `pause` / `resume`.** Phase 6.
- **No FsCheck "crash at line N of `RangeUploader.UploadAsync`" property test.** Dev plan acceptance criterion lists it; it is **deferred to a follow-up PR within Phase 3** (open question 5 below). The deterministic per-scenario integration tests in this PR cover every documented branch.
- **No `BrainBackedProviderRegistry`.** Phase 4. The registry stays `InMemoryProviderRegistry`.
- **No `IProviderSetup` / OAuth.** Phase 4.
- **No changes to `RangeUploader`, `RetryPolicy`, `IClock`, `FileSystemProvider`, `FaultInjectingStorageProvider`, or any brain repository other than `UploadQueueRepository`'s additive `LookupSessionAsync`.**

## Open questions for Gate 1

### 1. `_skinkRoot` source — constructor parameter or `VolumeContext` reference?

`ProcessOneAsync` needs to compute `blobAbsolutePath = Path.Combine(skinkRoot, blob.BlobPath)`. Three options:

- **A. Add `string skinkRoot` to `UploadQueueService`'s constructor.** Plan default. The single string is held privately; §3.6 passes `VolumeContext.SkinkRoot` when constructing the service.
- **B. Take `VolumeContext context` in the constructor.** Carries everything (`SkinkRoot`, repositories, the bus). Reduces 11-arg ctor to 3 args (`context, registry, networkMonitor`). But `VolumeContext` is currently a write-path carrier and would gain ownership of the upload-side repositories too — semantic bloat.
- **C. Resolve `blobAbsolutePath` through a small `IBlobPathResolver` interface.** Over-engineering for V1 — only one implementation.

**My recommendation:** A. Single `string skinkRoot` is the smallest, most testable shape. §3.6 wires `VolumeContext.SkinkRoot` directly.

### 2. The §15.3 step 7c "single transaction" wrapper

The dev plan §3.4 specifies "single brain transaction per case" for Completed/PermanentFailure (status flip + session delete). The repositories take `SqliteConnection` only. Three implementation options:

- **A. Explicit `await using SqliteTransaction tx = ...; … await tx.CommitAsync(ct);` around the two repository calls** (plan default). Relies on `Microsoft.Data.Sqlite`'s "active transaction binds on shared connection" behaviour. Works today; fragile if a future implementer interposes their own transaction in `MarkUploadedAsync`.
- **B. Add overloads to the repository methods** that take an `SqliteTransaction? transaction = null` parameter. Cleanest dependency direction; ~30 LOC of repository delta added on top of `LookupSessionAsync`.
- **C. Skip the explicit transaction.** Each `UPDATE` is already individually atomic on SQLite-WAL. The visible semantics are *eventual* consistency between `Status = 'UPLOADED'` and `UploadSessions` row absence, which Phase 5 WAL recovery cleans up if a crash interleaves. The dev plan's "single transaction" wording becomes a recovery-equivalence statement rather than a literal transaction.

**My recommendation:** B. The `+transaction` overload is small and removes the spooky-action-at-a-distance of relying on Sqlite's ambient-transaction binding through Dapper. Adds ~30 LOC to the modified-file delta (so total ~+75 LOC on `UploadQueueRepository`). If the user prefers A or C to minimise this PR's surface, both are defensible — flag.

### 3. Notification verb — "back up" or "upload"?

The dev plan §3.4 specifies the title *"Could not back up file `{virtualPath}` to `{tailDisplayName}`"*. The activity-log entries the user already sees from Phase 2 use "Uploaded" / "Wrote". `WritePipeline`'s Activity Log Summary (existing) uses "Wrote `{path}`" (matching the WRITE category).

- **A. Use "back up"** — matches the dev plan verbatim; aligns with the product framing ("backup system").
- **B. Use "upload"** — matches the activity-log vocabulary already in flight.
- **C. Use both** — Title "Could not back up file"; Activity log Summary "Uploaded …" / "Could not upload …". Mixed but each is correct in its own surface.

**My recommendation:** C. Notifications are aspirational and product-framed; the activity log is mechanical and event-framed. Mixing matches each medium's voice. Principle 25 is satisfied either way (none of "tail", "blob", "session", "range" appear).

### 4. Test introspection of orchestrator worker state

Two existing-codebase-friendly options:

- **A. `internal int ActiveWorkerCount`** read-only, gated by `[InternalsVisibleTo("FlashSkink.Tests")]` (already set on Core). Plan default. ~3 LOC.
- **B. Log-scraping via `RecordingLogger<UploadQueueService>`.** "Worker started for {ProviderId}" and "Worker stopped" are logged at Information today; tests assert on the captured log entries. Zero production surface added.

**My recommendation:** B. Avoids a production-surface field that exists only for tests, and the existing `RecordingLogger` pattern is already used across the test suite (see `RangeUploaderTests`).

### 5. FsCheck "crash at line N" property test — this PR or follow-up?

Dev plan §3.4 acceptance criterion: *"Property-based test in `tests/FlashSkink.Tests/CrashConsistency/UploadCrashConsistencyTests.cs` runs FsCheck across 'crash at line N of `RangeUploader.UploadAsync`' interleavings…"* The §21.3 invariant is testable; writing the harness involves intercepting `RangeUploader` at well-defined await points and asserting the invariant after each interleaving.

- **A. Ship the property test in this PR.** Faithful to the dev plan acceptance list. Adds ~250 LOC of test + harness; tests cover `RangeUploader`'s **internal** state machine, not `UploadQueueService`'s. Arguably the property test belongs in §3.3 by topic (it tests `RangeUploader`'s crash-consistency), and was deferred from §3.3 only because Phase 3 has multiple opportunities to add it.
- **B. Defer to a small follow-up PR** (e.g. `pr/3.4a-upload-crash-consistency-property-test`) after §3.4 lands. Keeps §3.4's surface tight; the property test is independent of the volume integration in §3.6.

**My recommendation:** B. The deterministic per-scenario tests in this PR cover every documented branch and exercise the §21.3 invariant via the dedicated `Invariant_*` tests above. The FsCheck harness is a quality multiplier, not a correctness gate, and is better authored against the *full* upload pipeline (RangeUploader + UploadQueueService) which is what `pr/3.4a` would have. If the user wants the strict dev-plan acceptance list, this PR's budget grows by ~250 LOC + harness.

---

*Plan ready for review. Stop at Gate 1 per `CLAUDE.md` step 3.*
