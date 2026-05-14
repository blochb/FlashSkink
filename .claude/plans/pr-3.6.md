# PR 3.6 — Volume integration: WriteBulkAsync, RegisterTailAsync, upload/mirror lifecycle

**Branch:** pr/3.6-volume-integration
**Blueprint sections:** §11, §11.1, §13.1, §16.7
**Dev plan section:** phase-3 §3.6

## Scope

Promotes the §2.7 `FlashSkinkVolume` from a Phase-1-only commit surface into the full
Phase-3 integration point. Three changes:

1. **`WriteBulkAsync`** — public partial-failure-aware bulk-write method that iterates
   `BulkWriteItem`s under the existing volume gate, returning a `BulkWriteReceipt` with
   one `Result<WriteReceipt>` per item (cross-cutting decision 1 of Phase 2 still holds:
   single-writer serialisation).
2. **`RegisterTailAsync`** — `internal` admin entry that inserts a `Providers` row
   (idempotent on conflict), registers the `IStorageProvider` instance in the volume's
   `InMemoryProviderRegistry`, and pulses the upload wakeup. Phase 4 replaces with the
   public `AddTailAsync` once OAuth flow + `BrainBackedProviderRegistry` arrive.
3. **Lifecycle wiring** — `CreateAsync` / `OpenAsync` construct and `Start`
   `UploadQueueService` (§3.4) and `BrainMirrorService` (§3.5); `WriteFileAsync` and
   `WriteBulkAsync` pulse the wakeup signal and notify the mirror service on each
   successful commit; `DisposeAsync` drains the mirror service (final mirror under
   `CancellationToken.None`) **before** the upload-queue service per the ordering
   rationale spelled out in dev plan §3.6.

No new `ErrorCode` values are added (cross-cutting decision 2). No public
`AddTailAsync`. No backfill of `TailUploads` rows for files written before
registration — `RegisterTailAsync` is the §3.6 internal seam; the §11.1 backfill
semantics belong to Phase 4's public `AddTailAsync`.

## Files to create

- `src/FlashSkink.Core.Abstractions/Models/BulkWriteItem.cs` — public sealed record
  with `Stream Source`, `string VirtualPath`, `IDisposable? OwnedSource`. ~25 lines.
- `src/FlashSkink.Core.Abstractions/Models/BulkItemResult.cs` — public sealed record
  with `string VirtualPath`, `Result<WriteReceipt> Outcome`. ~15 lines.
- `src/FlashSkink.Core.Abstractions/Models/BulkWriteReceipt.cs` — public sealed record
  with `IReadOnlyList<BulkItemResult> Items`. ~15 lines.
- `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs` — xUnit
  integration tests covering RegisterTailAsync + WriteFileAsync, WriteBulkAsync,
  brain-mirror lifecycle, disposal-mid-upload, and WAL invariant. ~750 lines.

## Files to modify

- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — add `WriteBulkAsync`
  public method; add `internal RegisterTailAsync`; add upload/mirror service fields
  and wakeup signal; wire `Start` in factory methods; pulse signal after successful
  writes; new `DisposeAsync` ordering (mirror first, then queue, then existing chain).
  Net add ~230 lines.
- `src/FlashSkink.Core/Orchestration/VolumeCreationOptions.cs` — add three optional
  injected services: `IProviderRegistry? ProviderRegistry`, `INetworkAvailabilityMonitor?
  NetworkMonitor`, `IClock? Clock`. Net add ~25 lines.

## Dependencies

- NuGet: none new.
- Project references: none new.

## Public API surface

### `FlashSkink.Core.Abstractions.Models.BulkWriteItem` (sealed record)

Summary intent: one item in a `WriteBulkAsync` request — a plaintext source stream and
its target virtual path. The optional `OwnedSource` lets callers hand ownership of a
disposable resource (e.g., a `FileStream`) to the bulk method; the volume disposes it
after the per-item commit completes (regardless of success). When `null`, the caller
retains ownership of `Source` and is responsible for disposal.

```csharp
public sealed record BulkWriteItem
{
    public required Stream Source { get; init; }
    public required string VirtualPath { get; init; }
    public IDisposable? OwnedSource { get; init; }
}
```

### `FlashSkink.Core.Abstractions.Models.BulkItemResult` (sealed record)

Summary intent: per-item outcome inside a `BulkWriteReceipt` — pairs the requested
virtual path with the `Result<WriteReceipt>` returned by the underlying single-item
write.

```csharp
public sealed record BulkItemResult
{
    public required string VirtualPath { get; init; }
    public required Result<WriteReceipt> Outcome { get; init; }
}
```

### `FlashSkink.Core.Abstractions.Models.BulkWriteReceipt` (sealed record)

Summary intent: aggregate result of `WriteBulkAsync` — one `BulkItemResult` per
submitted item, in submission order. `WriteBulkAsync` always returns a non-failed
`Result<BulkWriteReceipt>`; individual item failures live inside the receipt
(Principle 1's "core never throws", combined with the §11.1 "bulk operations are
partial-failure-aware" contract).

```csharp
public sealed record BulkWriteReceipt
{
    public required IReadOnlyList<BulkItemResult> Items { get; init; }
}
```

### `FlashSkink.Core.Orchestration.VolumeCreationOptions` (additions)

Three new optional `init` properties. All three default to internally-constructed
sensible defaults so existing callers (including the `FlashSkinkVolumeTests` suite
from §2.7) keep compiling.

```csharp
/// <summary>
/// Provider registry shared between the upload queue, brain mirror, and
/// RegisterTailAsync. Null → the factory creates a fresh InMemoryProviderRegistry.
/// </summary>
public IProviderRegistry? ProviderRegistry { get; init; }

/// <summary>
/// Network-availability signal consumed by UploadQueueService. Null → the factory
/// creates an AlwaysOnlineNetworkMonitor (Phase 5 wires the real OS monitor).
/// </summary>
public INetworkAvailabilityMonitor? NetworkMonitor { get; init; }

/// <summary>
/// Clock for retry-backoff scheduling, orchestrator idle waits, and the
/// brain-mirror debounce/timer. Null → SystemClock.Instance.
/// </summary>
public IClock? Clock { get; init; }
```

### `FlashSkink.Core.Orchestration.FlashSkinkVolume.WriteBulkAsync` (new public method)

```csharp
/// <summary>
/// Writes a batch of items sequentially under the volume gate. Per Phase 2's
/// single-writer serialisation rule (cross-cutting decision 1), items commit one
/// at a time — each is an independent Phase-1 commit. Returns a
/// <see cref="BulkWriteReceipt"/> whose <c>Items</c> list contains one
/// <see cref="BulkItemResult"/> per input item, in submission order.
/// </summary>
/// <remarks>
/// The bulk operation is not transactional across items by design (§11.1) — a
/// 10,000-file backup that fails on file 7,500 must not throw away 7,499 successes.
/// Each item is committed independently; the brain row, blob, and per-tail upload
/// rows are durable before the method advances to the next item. Cancellation is
/// observed between items; an item already in flight at cancellation time finishes
/// or fails naturally (the per-pipeline ct still flows in).
///
/// If a <see cref="BulkWriteItem.OwnedSource"/> is provided, it is disposed in a
/// finally block after each item's pipeline call — both on success and on failure —
/// using <see cref="CancellationToken.None"/> semantics (no extra cancellation,
/// Principle 17 in spirit).
///
/// Throws <see cref="ObjectDisposedException"/> if called after
/// <see cref="DisposeAsync"/>.
/// </remarks>
public Task<Result<BulkWriteReceipt>> WriteBulkAsync(
    IReadOnlyList<BulkWriteItem> items,
    CancellationToken ct = default);
```

Why `Result<BulkWriteReceipt>` and not `BulkWriteReceipt`: the outer `Result` carries
gate-acquisition cancellation, argument validation failure (e.g., `items is null`),
and the `_disposed` short-circuit. Per-item failures live inside the receipt.

### `FlashSkink.Core.Orchestration.FlashSkinkVolume.RegisterTailAsync` (new internal method)

```csharp
/// <summary>
/// Internal admin entry used by tests (and, in Phase 3, only by tests). Inserts a
/// row into <c>Providers</c> with no OAuth credentials (token/secret columns stay
/// NULL — Phase 4 wires the OAuth flow), registers <paramref name="provider"/> in
/// the volume's <see cref="IProviderRegistry"/>, and pulses the upload wakeup
/// signal so the orchestrator picks the new tail up on its next tick.
/// </summary>
/// <remarks>
/// Idempotent on the brain row: if a <c>Providers</c> row with the same
/// <paramref name="providerId"/> already exists, the insert is skipped and
/// <see cref="Result.Ok"/> is returned — the <see cref="IStorageProvider"/>
/// instance is registered in the in-memory registry regardless (the registry is
/// in-memory and was lost on the prior volume close; re-registration on open is
/// the expected path for already-existing tails). Pre-existing
/// <c>TailUploads</c> rows are not mutated; Phase 4's <c>AddTailAsync</c> will
/// add the §11.1 "queue every existing file" backfill semantics.
/// </remarks>
internal Task<Result> RegisterTailAsync(
    string providerId,
    string providerType,
    string displayName,
    string? providerConfigJson,
    IStorageProvider provider,
    CancellationToken ct = default);
```

`InternalsVisibleTo("FlashSkink.Tests")` is already declared in the Core csproj for
the §1.5 `KeyVault` test seam and is reused here verbatim (no new
`InternalsVisibleTo` attribute is needed — verified by reading
`src/FlashSkink.Core/FlashSkink.Core.csproj`).

## Internal types

### `FlashSkinkVolume` — new private fields

```csharp
private readonly IProviderRegistry _providerRegistry;
private readonly INetworkAvailabilityMonitor _networkMonitor;
private readonly IClock _clock;
private readonly UploadWakeupSignal _wakeupSignal;
private readonly UploadQueueService _uploadQueueService;
private readonly BrainMirrorService _brainMirrorService;
private readonly CancellationTokenSource _volumeCts;
```

All seven are constructed inside `BuildVolumeFromSession` (or a new helper —
`BuildAndStartBackgroundServices` — for legibility) and owned by the volume.
Disposal order is spelled out in **Method-body contracts** below.

### `FlashSkinkVolume.WriteBulkAsync` — control flow

Pseudocode:

```
ThrowIfDisposed();
if items is null → Result<BulkWriteReceipt>.Fail(InvalidArgument)
try { await _gate.WaitAsync(ct); }
catch (OperationCanceledException ex) → Result<BulkWriteReceipt>.Fail(Cancelled)
var results = new List<BulkItemResult>(items.Count);
try
{
    bool sawSuccess = false;
    foreach (var item in items)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _writePipeline.ExecuteAsync(
                item.Source, item.VirtualPath, _context, ct);
            results.Add(new BulkItemResult { VirtualPath = item.VirtualPath, Outcome = outcome });
            if (outcome.Success) { sawSuccess = true; }
        }
        catch (OperationCanceledException ocex)
        {
            results.Add(new BulkItemResult
            {
                VirtualPath = item.VirtualPath,
                Outcome = Result<WriteReceipt>.Fail(ErrorCode.Cancelled,
                    "Bulk write cancelled.", ocex),
            });
            // Stop iterating; remaining items will not be attempted.
            break;
        }
        finally
        {
            item.OwnedSource?.Dispose();
        }
    }
    // Pulse once if any commit succeeded — debounce on the consumer side coalesces
    // multiple commits within the bulk into a single mirror cycle.
    if (sawSuccess)
    {
        _wakeupSignal.Pulse();
        _brainMirrorService.NotifyWriteCommitted();
    }
    return Result<BulkWriteReceipt>.Ok(new BulkWriteReceipt { Items = results });
}
finally
{
    _gate.Release();
}
```

Notes:
- The per-item `try/catch` for `OperationCanceledException` records the cancellation
  on the in-flight item then breaks out of the loop (no further items attempted).
  The outer method still returns `Result.Ok(receipt)` so the caller sees the partial
  receipt — cancellation in a bulk is not a failure of the bulk method itself.
- Each `item.OwnedSource?.Dispose()` is in a `finally` so it always runs, regardless
  of how the per-item branch exited (success, failure result, or cancellation
  exception observed inside the pipeline).
- The single `Pulse()` / `NotifyWriteCommitted()` call at the end of the bulk is
  intentional. The upload-queue wakeup is coalesced by the channel (capacity-1,
  drop-write); the brain-mirror debounce window absorbs multiple commits anyway.
  N pulses for N items is no better than one and adds work in the orchestrator.

### `FlashSkinkVolume.RegisterTailAsync` — control flow

```
ThrowIfDisposed();
if providerId/providerType/displayName/provider is null or empty → Fail(InvalidArgument)
try { await _gate.WaitAsync(ct); }
catch (OperationCanceledException ex) → Fail(Cancelled)
try
{
    var existsResult = await connection.QuerySingleOrDefaultAsync<string?>(
        "SELECT ProviderID FROM Providers WHERE ProviderID = @ProviderId",
        new { ProviderId = providerId }, ct);
    if (!existsResult exists / null)
    {
        await connection.ExecuteAsync(
            "INSERT INTO Providers (ProviderID, ProviderType, DisplayName, " +
            " ProviderConfig, HealthStatus, AddedUtc, IsActive) " +
            "VALUES (@ProviderId, @ProviderType, @DisplayName, " +
            "        @ProviderConfig, 'Healthy', @AddedUtc, 1)",
            new {
                ProviderId = providerId,
                ProviderType = providerType,
                DisplayName = displayName,
                ProviderConfig = providerConfigJson, // may be null
                AddedUtc = DateTime.UtcNow.ToString("O"),
            }, ct);
    }
    // Always register the in-process instance — the in-memory registry is rebuilt
    // every volume open.
    if (_providerRegistry is InMemoryProviderRegistry inMem)
    {
        inMem.Register(providerId, provider);
    }
    else
    {
        // Future BrainBackedProviderRegistry doesn't have a Register method on
        // the IProviderRegistry contract (Principle 23 — frozen); Phase 4 will
        // wire its own registration path inside the OAuth completion. Phase 3
        // requires the InMemoryProviderRegistry, but if a custom registry was
        // passed in, surface a clear failure rather than silently dropping.
        return Result.Fail(ErrorCode.InvalidArgument,
            "RegisterTailAsync requires an InMemoryProviderRegistry-backed volume.");
    }
    _wakeupSignal.Pulse();
    return Result.Ok();
}
catch (OperationCanceledException ex) → Fail(Cancelled)
catch (SqliteException ex when unique constraint) → Fail(PathConflict, "...")
catch (SqliteException ex) → Fail(DatabaseWriteFailed)
catch (Exception ex) → Fail(Unknown)
finally { _gate.Release(); }
```

The `HealthStatus = 'Healthy'` literal is the §16.2 enum default — Phase 5's
`HealthMonitorService` updates it once health is being monitored. The `IsActive = 1`
literal is the §16.2 default — Phase 4's `RemoveTailAsync` flips to 0.

## Method-body contracts

### `WriteFileAsync` — modified

After the existing pipeline call, before releasing the gate:

```csharp
var result = await _writePipeline.ExecuteAsync(source, virtualPath, _context, ct);
if (result.Success)
{
    _wakeupSignal.Pulse();
    _brainMirrorService.NotifyWriteCommitted();
}
return result;
```

`Pulse()` and `NotifyWriteCommitted()` are documented as never-throw (per §3.4 and
§3.5 plans) — no try/catch around them. They execute before `_gate.Release()` runs
in the `finally`; the order doesn't matter for correctness (the signals are
asynchronous), but keeping them above `finally` makes the data flow legible.

### `WriteBulkAsync` — see pseudocode above

Cancellation rules:
- Pre-acquire: `OperationCanceledException` → `Result.Fail(Cancelled)`.
- Mid-bulk: observed at the top of each per-item iteration; the in-flight pipeline
  also observes `ct`. The item carrying the cancellation is recorded as
  `ErrorCode.Cancelled`; the loop breaks. Items already committed remain committed
  (their `BulkItemResult` is preserved); items not yet attempted are absent from
  the result list. **The bulk method itself returns `Result.Ok(receipt)`** —
  cancellation in the middle of a partial-failure-aware operation is not a failure
  of the operation.

Notification: `WriteBulkAsync` does not publish any extra notifications. Per-item
pipeline failures publish through `WritePipeline.LogAndPublishAsync` as today.

### `RegisterTailAsync` — see pseudocode above

The unique-constraint catch (`SqliteException.IsUniqueConstraintViolation()` extension
from §1.5) is defensive — the pre-check above should already guarantee no conflict —
but if two concurrent `RegisterTailAsync` calls race past the pre-check, the second
returns `ErrorCode.PathConflict` with a useful message. (No new code added; reusing
the existing `PathConflict` value.)

### `CreateAsync` / `OpenAsync` — modified

Inside `BuildVolumeFromSession`, after the existing `VolumeContext` and pipeline
construction and **before** the `new FlashSkinkVolume(...)` return:

```csharp
var registry = options.ProviderRegistry
    ?? new InMemoryProviderRegistry(loggerFactory.CreateLogger<InMemoryProviderRegistry>());
var netMonitor = options.NetworkMonitor ?? new AlwaysOnlineNetworkMonitor();
var clock = options.Clock ?? SystemClock.Instance;

var uploadQueueRepo = new UploadQueueRepository(
    connection, loggerFactory.CreateLogger<UploadQueueRepository>());
var retryPolicy = new RetryPolicy();
var rangeUploader = new RangeUploader(
    uploadQueueRepo, clock, retryPolicy,
    loggerFactory.CreateLogger<RangeUploader>());

var wakeupSignal = new UploadWakeupSignal();

var uploadQueueService = new UploadQueueService(
    uploadQueueRepo, blobs, files, activityLog,
    registry, netMonitor, notificationBus,
    rangeUploader, retryPolicy, clock, wakeupSignal,
    loggerFactory.CreateLogger<UploadQueueService>());

var brainMirrorService = new BrainMirrorService(
    connection, dek, skinkRoot, registry,
    notificationBus, clock,
    loggerFactory.CreateLogger<BrainMirrorService>());

var volumeCts = new CancellationTokenSource();

var queueStartResult = uploadQueueService.Start(volumeCts.Token);
if (!queueStartResult.Success)
{
    // Fail the volume open: dispose what we constructed, return the error.
    await uploadQueueService.DisposeAsync().ConfigureAwait(false);
    await brainMirrorService.DisposeAsync().ConfigureAwait(false);
    volumeCts.Dispose();
    context.Dispose();
    await session.DisposeAsync().ConfigureAwait(false);
    // Caller bubbles this back up; CreateAsync's finally is already past the
    // "ownership transferred" point — see step-flow in Notes.
    throw new InvalidOperationException(
        $"Upload queue service failed to start: {queueStartResult.Error!.Code}");
}
var mirrorStartResult = brainMirrorService.Start(volumeCts.Token);
if (!mirrorStartResult.Success)
{
    await uploadQueueService.DisposeAsync().ConfigureAwait(false);
    await brainMirrorService.DisposeAsync().ConfigureAwait(false);
    volumeCts.Dispose();
    context.Dispose();
    await session.DisposeAsync().ConfigureAwait(false);
    throw new InvalidOperationException(
        $"Brain mirror service failed to start: {mirrorStartResult.Error!.Code}");
}
```

The `throw` inside `BuildVolumeFromSession` is caught by `CreateAsync`/`OpenAsync`'s
existing outer `catch (Exception ex) → Result.Fail(ErrorCode.Unknown, ...)`. Principle
1's "Core never throws across the public API" is upheld — the throw is internal,
inside a private builder. (Alternative considered: return `Result<FlashSkinkVolume>`
from `BuildVolumeFromSession`. Rejected as a bigger refactor; the existing factory
methods already use throw-and-catch for `IncrementalHash.Dispose()` ordering today.)

**Why not use cross-cutting `ErrorCode.UploadFailed` here:** `UploadFailed` is for
per-blob upload outcomes; a startup-time service-init failure is not an upload. The
only available reusable codes are `Unknown`, `InvalidArgument`, or
`DatabaseWriteFailed`. `Unknown` is the right one — the failure mode is a
defensive-catch path that §3.4 / §3.5 plans document as "should never fire."

### `DisposeAsync` — modified

The new ordering replaces the existing two-step (`_context.Dispose()` →
`_session.DisposeAsync()`) with a four-step chain inside the gate:

```csharp
if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
try
{
    // Step 1 — cancel the volume CTS. Workers and timers observe this and exit
    // their loops at the next ct check. The brain mirror's RunOneCycleAsync uses
    // CancellationToken.None internally (Principle 17), so cancellation alone
    // does not stop the final mirror — it only stops the background timer.
    _volumeCts.Cancel();

    // Step 2 — brain mirror first (dev plan §3.6 rationale). DisposeAsync runs
    // the final mirror under CancellationToken.None through every active tail,
    // then awaits the timer + debounce tasks (5 s budget each). Running this
    // before the upload queue stops avoids per-provider HTTP/connection-pool
    // contention with in-flight worker UploadRangeAsync calls and lets the
    // mirror grab clean provider state.
    await _brainMirrorService.DisposeAsync().ConfigureAwait(false);

    // Step 3 — upload queue. DisposeAsync signals workers, waits for clean
    // shutdown with a 10 s per-worker budget (§3.4); any worker mid-range will
    // be cancelled and the UploadSessions row is preserved (the §15.3 step 7c
    // brain transaction only fires on successful finalisation).
    await _uploadQueueService.DisposeAsync().ConfigureAwait(false);

    // Step 4 — existing teardown: dispose VolumeContext (IncrementalHash,
    // CompressionService), then VolumeSession (zero DEK, dispose connection).
    _context.Dispose();
    await _session.DisposeAsync().ConfigureAwait(false);

    _volumeCts.Dispose();
}
finally
{
    _gate.Release();
}
```

Why the cancellation comes first but the mirror is awaited before cancellation has
"taken effect": `_volumeCts.Cancel()` is observed at the next `ct` check inside the
timer / debounce loops (which exits them quickly); the brain mirror service's own
`DisposeAsync` is what produces the final mirror, not the timer. The order
"cancel-then-dispose" stops new work from starting; the awaits then drain the
in-flight work.

**Why the gate is held across all four steps**: the gate prevents new
`WriteFileAsync` / `WriteBulkAsync` calls from racing into a half-torn-down volume.
A new call sees `_disposed != 0` via `ThrowIfDisposed` and throws
`ObjectDisposedException` before ever reaching the gate — but the gate guards
against the race where the call passed `ThrowIfDisposed` a microsecond before
`Interlocked.Exchange(ref _disposed, 1) != 0` flipped (same rationale as the
existing §2.7 disposal). The brain mirror final-mirror call itself **does not need
the gate** (it reads the brain via `SqliteConnection.BackupDatabase`, which does
not contend with writers on the same connection), but holding the gate across the
whole disposal block keeps the invariant uniform.

## Integration points

```csharp
// UploadQueueService (§3.4)
public sealed class UploadQueueService : IAsyncDisposable
{
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
    public Result Start(CancellationToken volumeToken);
    public ValueTask DisposeAsync();
}

// BrainMirrorService (§3.5)
public sealed class BrainMirrorService : IAsyncDisposable
{
    public BrainMirrorService(
        SqliteConnection brainConnection,
        ReadOnlyMemory<byte> dek,
        string skinkRoot,
        IProviderRegistry providerRegistry,
        INotificationBus notificationBus,
        IClock clock,
        ILogger<BrainMirrorService> logger);
    public Result Start(CancellationToken volumeToken);
    public void NotifyWriteCommitted();
    public ValueTask<Result> TriggerMirrorAsync(CancellationToken ct);
    public ValueTask DisposeAsync();
}

// InMemoryProviderRegistry (§3.1)
public sealed class InMemoryProviderRegistry : IProviderRegistry
{
    public InMemoryProviderRegistry(ILogger<InMemoryProviderRegistry> logger);
    public void Register(string providerId, IStorageProvider provider);
    public bool Remove(string providerId);
    public ValueTask<Result<IStorageProvider>> GetAsync(string providerId, CancellationToken ct);
    public ValueTask<Result<IReadOnlyList<string>>> ListActiveProviderIdsAsync(CancellationToken ct);
}

// AlwaysOnlineNetworkMonitor (§3.1)
public sealed class AlwaysOnlineNetworkMonitor : INetworkAvailabilityMonitor
{
    public bool IsAvailable => true;
    public event EventHandler<bool>? AvailabilityChanged; // never raised
}

// UploadQueueRepository (§1.6 + §3.4 LookupSessionAsync addition)
public sealed class UploadQueueRepository
{
    public UploadQueueRepository(SqliteConnection connection, ILogger<UploadQueueRepository> logger);
    // existing methods + new in §3.4: LookupSessionAsync
}

// RangeUploader (§3.3)
public sealed class RangeUploader
{
    public RangeUploader(
        UploadQueueRepository uploadQueueRepository,
        IClock clock,
        RetryPolicy retryPolicy,
        ILogger<RangeUploader> logger);
}

// SystemClock (§3.2)
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; }
}

// FileSystemProvider (§3.1) — used by tests
public sealed class FileSystemProvider : IStorageProvider, ISupportsRemoteHashCheck
{
    public FileSystemProvider(string rootPath, ILogger<FileSystemProvider> logger);
    // (uses FileSystemProviderConfig internally)
}

// WritePipeline INSERT-SELECT into TailUploads is already in place (verified at
// src/FlashSkink.Core/Engine/WritePipeline.cs:547-549) — it INSERTs one
// TailUploads row per active Providers row on every commit. No changes needed.
```

## Principles touched

- **Principle 1** — `WriteBulkAsync` and `RegisterTailAsync` return `Result` /
  `Result<T>`; never throw across the boundary. The `throw` inside
  `BuildVolumeFromSession` is internal (private builder), caught by the factory
  methods' existing outer catch.
- **Principle 2** — every successful commit fans out to every active tail (the
  WritePipeline INSERT-SELECT into `TailUploads` was already this shape; Phase 3
  is what *processes* those rows).
- **Principle 3** — `WriteBulkAsync` and the upload services all read from the
  skink; tails are touched only for upload + mirror.
- **Principle 4** — Phase 1 commit (the inner `WritePipeline.ExecuteAsync`) is
  synchronous and transactional, unchanged. The wakeup pulse and mirror
  notification fire *after* the commit returns success — never gating Phase 1.
- **Principle 5** — `UploadSessions` rows persist; the disposal ordering
  guarantees an in-flight upload's session row is preserved when the volume
  closes mid-upload (verified by `DisposeAsync_MidUpload_PreservesSessionRow`).
- **Principle 13** — every new public method takes `CancellationToken ct` as its
  last parameter (`ct = default` for ergonomics).
- **Principle 14** — `WriteBulkAsync` and `RegisterTailAsync` catch
  `OperationCanceledException` first; the bulk per-item iteration too.
- **Principle 15** — `WriteBulkAsync` catches `OperationCanceledException` then
  `Exception`; `RegisterTailAsync` catches `OperationCanceledException`,
  `SqliteException` (with `IsUniqueConstraintViolation()` filter), `SqliteException`,
  `Exception` in that order.
- **Principle 16** — `_volumeCts`, `_wakeupSignal`, `_uploadQueueService`, and
  `_brainMirrorService` are all disposed in `DisposeAsync`; the per-item
  `OwnedSource` is disposed in a `finally` per iteration; failed Start cleans up
  every partially-constructed service before throwing.
- **Principle 17** — `DisposeAsync` uses `CancellationToken.None` as a literal at
  the gate-acquire; the brain mirror's final-mirror call internally uses
  `CancellationToken.None` (per §3.5 plan); the `OwnedSource.Dispose()` is
  synchronous and cancellation-free.
- **Principle 25** — no appliance vocabulary in `WriteBulkAsync` /
  `RegisterTailAsync` error messages. "Bulk write cancelled.", "Could not register
  tail.", etc. — user vocabulary throughout. Notifications continue to flow from
  `WritePipeline.LogAndPublishAsync` and the §3.4 / §3.5 services unchanged.
- **Principle 31** — `_volumeCts` is disposed after the session (which zeros
  the DEK); the brain-mirror service's `_dek` view is dropped when its DisposeAsync
  returns; no new key material is introduced.

## Test spec

### `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs`

Test class: `FlashSkinkVolumeUploadIntegrationTests`

All tests use a per-test `_skinkRoot` and a per-test `_tailRoot` (two distinct temp
directories, both deleted in `DisposeAsync`). A shared helper
`CreateVolumeAndRegisterFileSystemTail` constructs a fresh volume, builds a
`FileSystemProvider` rooted at `_tailRoot`, and calls `RegisterTailAsync` — most
tests start from this helper.

A second helper `WaitForUploadAsync(volume, fileId, providerId, timeout)` polls
`TailUploads` until `Status = 'UPLOADED'` or the timeout elapses; backs the
"end-to-end" assertions where the real upload-queue worker drives the lifecycle.
Default timeout 10 s; tests use a `TestNetworkAvailabilityMonitor` + `FakeClock`
where they want determinism.

#### Bulk write

- `WriteBulkAsync_FiveDistinctItems_AllSucceed` — pass 5 `BulkWriteItem`s with
  unique paths; assert `Items.Count == 5`, every `Outcome.Success` is true, and
  reading each path back via `ReadFileAsync` reproduces the original bytes.
- `WriteBulkAsync_PartialFailure_ReturnsMixedReceipt` — item 3's source is a stream
  that throws `IOException` on `Read`; assert `Items.Count == 5`, 4 outcomes
  succeed, item 3's `Outcome.Success` is false with non-null `Error`, and the 4
  successful items are readable back. (Acceptance line 373.)
- `WriteBulkAsync_OwnedSourceIsDisposed_OnSuccessAndFailure` — pass two items, one
  with a success-path source and one with a throwing source, both with non-null
  `OwnedSource = trackingDisposable`; assert both `trackingDisposable.Disposed`
  flags are set after the call returns.
- `WriteBulkAsync_Cancellation_MidLoop_StopsAndReturnsPartialReceipt` — submit 10
  items wrapped over `CancellingStream`s that flip a `cts` after item 3 begins;
  assert returned receipt has at most 4 entries and the cancellation appears as
  `ErrorCode.Cancelled` on one item.
- `WriteBulkAsync_AfterDispose_ThrowsObjectDisposed` — `DisposeAsync` then call;
  asserts `ObjectDisposedException` (same convention as §2.7 disposal tests).

#### RegisterTailAsync

- `RegisterTail_InsertsProvidersRow` — call `RegisterTailAsync` for a new
  providerId; open a raw SQLite query against `Providers`; assert one row with
  `ProviderType`, `DisplayName`, `ProviderConfig`, `HealthStatus='Healthy'`,
  `IsActive=1`.
- `RegisterTail_IdempotentOnDuplicate` — call twice with the same providerId;
  asserts both return success and only one `Providers` row exists.
- `RegisterTail_AfterRegister_ListActiveProviderIdsContains_Provider` — call
  `RegisterTailAsync`, then call `volume`-internal access to the registry
  (via the same `InMemoryProviderRegistry` instance passed in
  `VolumeCreationOptions`), assert `ListActiveProviderIdsAsync` returns the
  registered providerId.
- `RegisterTail_NullProvider_ReturnsInvalidArgument` — pass `provider: null!`;
  asserts `ErrorCode.InvalidArgument`.

#### End-to-end upload (FileSystemProvider real driver)

These tests use **real time** (not `FakeClock`) because they exercise the §3.4
worker loop end-to-end and the FileSystemProvider has no expiring sessions; they
poll via `WaitForUploadAsync` with a 10 s budget.

- `RegisterTailThenWriteOneFile_BlobLandsAtTail_Decrypted` — register tail, write
  a 10 MB file; wait for `TailUploads.Status = 'UPLOADED'`; assert the on-disk
  file at the sharded tail path exists and decrypts to the original plaintext
  with the volume's DEK. (Acceptance line 363.)
- `WriteFiveDistinctFiles_AllLandAtTail_AllUploaded_SessionsEmpty` — register
  tail, write 5 distinct files; wait for all 5 `TailUploads` rows to reach
  `UPLOADED`; assert (a) all 5 files exist at the configured tail root with
  sharded paths, (b) `UploadSessions` table is empty (§21.3 invariant —
  acceptance lines 364 and 376). (Acceptance line 364.)

#### Brain mirror lifecycle

These use `FakeClock` so the 10 s debounce window and 15-minute timer are
deterministic.

- `BrainMirror_AfterCommit_AppearsAtTail_AfterDebounce` — write one file; advance
  `FakeClock` by 11 s; assert exactly one `_brain/{timestamp}.bin` file appears at
  the tail root and decrypts to a SQLite database that contains the freshly-written
  `Files` row. (Acceptance line 371.)
- `BrainMirror_OnCleanShutdown_RunsOneFinalMirror` — register tail, write file,
  immediately call `volume.DisposeAsync()` (before the debounce window elapses);
  after disposal, assert exactly one `_brain/{timestamp}.bin` file landed at the
  tail. (Acceptance line 373.)

#### Disposal mid-upload

- `Dispose_MidUpload_PreservesSessionRowAndReturnsWithin10s` — register a
  `FaultInjectingStorageProvider`-wrapped FileSystem tail whose
  `SetRangeLatency(TimeSpan.FromSeconds(30))` makes uploads visibly slow; write a
  20 MB file; wait briefly (~1 s) until at least one range has been confirmed
  uploaded (poll `UploadSessions.BytesUploaded > 0`); call `volume.DisposeAsync()`
  and time it; assert the disposal returns within 10 s and the `UploadSessions`
  row still exists with the last-confirmed `BytesUploaded`. (Acceptance line 370.)
- `Dispose_MidUpload_NextOpenResumesNotRestarts` — same setup; after dispose,
  re-open the same skink root **with a fresh FileSystemProvider over the same tail
  root** (re-registered via `RegisterTailAsync`); poll until upload completes;
  assert the final blob bytes match the original and (crucially) the second-open
  `LastActivityUtc` shows the upload picked up from a non-zero offset (assert by
  reading the `Files` table's `BlobId`, downloading the tail blob, decrypting,
  comparing; the resume itself is a property of the session-row preservation
  asserted in the prior test). (Acceptance line 370 sequel.)

#### Network gating

- `NetworkUnavailable_GatesUploads_RestoresOnAvailable` — use a
  `TestNetworkAvailabilityMonitor` set to `IsAvailable = false`; register tail,
  write file; advance time briefly (5 s); assert the file is **not** at the tail
  yet; flip `monitor.SetAvailable(true)` (which raises `AvailabilityChanged`);
  wait for upload completion within 10 s. (Acceptance line 374.)

#### WAL invariant

- `WalInvariant_AfterUpload_NoSessionRowForUploaded` — register tail, write 3
  files, wait for all 3 to reach `UPLOADED`; query `UploadSessions` and assert
  zero rows for any of those `(FileID, ProviderID)` pairs. (§21.3 — acceptance
  line 376.)

#### Helper types (in same file or `_TestSupport/`)

- `private sealed class CancellingStream : Stream` — reused from §2.7 plan; first
  `Read` triggers `cts.Cancel()`.
- `private sealed class ThrowingStream : Stream` — `Read` throws `IOException`;
  used for partial-failure bulk-write test.
- `private sealed class TrackingDisposable : IDisposable` — exposes
  `bool Disposed { get; }`; used for OwnedSource verification.

A `FaultInjectingStorageProvider` already exists in `tests/FlashSkink.Tests/Providers/`
from §3.1 and is reused.

A `TestNetworkAvailabilityMonitor` already exists in `tests/FlashSkink.Tests/_TestSupport/`
from §3.4 (see pr-3.4.md "Internal types" section) and is reused.

A `FakeClock` already exists in `tests/FlashSkink.Tests/_TestSupport/` from §3.2
and is reused.

## Acceptance criteria

- [ ] Builds with zero warnings on all targets.
- [ ] All new tests pass.
- [ ] No existing tests break (FlashSkinkVolumeTests from §2.7 must still pass —
  the additive options properties default in for them).
- [ ] `WriteBulkAsync` returns a `BulkWriteReceipt` with per-item `Result<WriteReceipt>`
  in submission order; one failure does not block subsequent items (Phase 3 dev
  plan acceptance line 373).
- [ ] `WriteBulkAsync`'s `OwnedSource` is disposed for every item (success and
  failure), in a finally block.
- [ ] `RegisterTailAsync` inserts a `Providers` row with NULL OAuth fields and
  `HealthStatus='Healthy'`, `IsActive=1`.
- [ ] `RegisterTailAsync` is idempotent on duplicate `ProviderID` (no second row,
  returns `Result.Ok`).
- [ ] End-to-end against `FileSystemProvider`: `RegisterTailAsync` + `WriteFileAsync`
  of a 10 MB file → `TailUploads.Status` reaches `'UPLOADED'`, the blob lives at the
  sharded tail path, and it decrypts to the original plaintext (dev plan
  acceptance line 363).
- [ ] 5 distinct files round-trip — all 5 `TailUploads` rows reach `UPLOADED`,
  `UploadSessions` is empty after completion (acceptance line 364).
- [ ] Brain mirror after commit: one `_brain/{ts}.bin` lands on the tail after
  the 10 s debounce; decrypts to a SQLite DB containing the freshly-written file
  (acceptance line 371).
- [ ] Brain mirror on clean shutdown: one final `_brain/{ts}.bin` lands before
  `DisposeAsync` returns (acceptance line 373).
- [ ] `volume.DisposeAsync()` mid-upload returns within 10 s; `UploadSessions`
  row preserved; second open with same tail resumes the upload (acceptance line
  370).
- [ ] Network gating: `IsAvailable = false` blocks uploads; flipping to true and
  raising `AvailabilityChanged` resumes uploads within one tick (acceptance
  line 374).
- [ ] WAL invariant: for every `TailUploads` row at `'UPLOADED'`, no
  `UploadSessions` row exists for that `(FileID, ProviderID)` (acceptance
  line 376).
- [ ] No new `ErrorCode` values added (cross-cutting decision 2 — verified by
  diffing `ErrorCode.cs` against `main`).

## Line-of-code budget

- `src/FlashSkink.Core.Abstractions/Models/BulkWriteItem.cs` — ~25 lines
- `src/FlashSkink.Core.Abstractions/Models/BulkItemResult.cs` — ~15 lines
- `src/FlashSkink.Core.Abstractions/Models/BulkWriteReceipt.cs` — ~15 lines
- `src/FlashSkink.Core/Orchestration/VolumeCreationOptions.cs` — net add ~25 lines
- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — net add ~230 lines
  (fields ~10; `WriteBulkAsync` ~80; `RegisterTailAsync` ~70; modified
  WriteFileAsync ~5; modified BuildVolumeFromSession ~40; modified DisposeAsync
  ~25)
- `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeUploadIntegrationTests.cs` —
  ~750 lines (15+ tests; the bulk-write helper streams, the tail-poll helper,
  and the FakeClock-driven mirror tests dominate)
- **Total non-test: ~310 lines; test: ~750 lines.**

## Non-goals

- Do **not** add public `AddTailAsync` — Phase 4.
- Do **not** implement `BrainBackedProviderRegistry` — Phase 4.
- Do **not** add `RemoveTailAsync` — Phase 4.
- Do **not** implement the §11.1 "queue every existing file" backfill on
  registration — Phase 4 (paired with the public `AddTailAsync`).
- Do **not** add WAL recovery sweep for orphaned `UPLOADING` rows on startup —
  Phase 5 (dev plan §3.4 documents the dependency).
- Do **not** add network-availability OS signal — Phase 5 (Phase 3 keeps
  `AlwaysOnlineNetworkMonitor`; tests inject `TestNetworkAvailabilityMonitor`).
- Do **not** introduce a DI container; factory methods continue to construct
  the graph directly.
- Do **not** parallelise `WriteBulkAsync` across items — single-writer
  serialisation (cross-cutting decision 1 of Phase 2).
- Do **not** raise the `UsbRemoved` / `UsbReinserted` / `TailStatusChanged`
  events from this PR — raisers remain Phase 4/5/6 as declared in §2.7.
- Do **not** modify `WritePipeline` (no post-commit `event` added there — the
  pulse/notify happens in the volume after the pipeline returns success;
  a faithful reading of dev plan §3.6 step 6, "WritePipeline → UploadQueueService
  wakeup wiring," and avoids touching a stable Phase 2 service).
- Do **not** modify `ErrorCode.cs` — verified at PR submission.

## Drift notes

**Drift Note 1 — Service start failure surface.** Dev plan §3.6 lifecycle wiring
shows `await uploadQueueService.StartAsync(volumeCts.Token)`; the §3.4 plan
landed `Start` as sync-returning `Result`. If `Start` returns a failed
`Result`, the factory method's only ergonomic exit is to throw internally and
let the existing outer `catch (Exception ex)` in `CreateAsync` / `OpenAsync`
convert to `Result.Fail(ErrorCode.Unknown)`. This is consistent with the §3.4 /
§3.5 plans documenting that Start should never fail outside of "already-disposed"
states. Recorded here so a future reader does not interpret the throw as a
Principle 1 violation.

**Drift Note 2 — `RegisterTailAsync` requires `InMemoryProviderRegistry`.** The
`IProviderRegistry` interface has no `Register` method (Principle 23 — the
registry contract is the read-only seam consumed by orchestrator + mirror; the
write side is implementation-specific). When `VolumeCreationOptions` is given a
custom `IProviderRegistry`, `RegisterTailAsync` returns
`Result.Fail(ErrorCode.InvalidArgument, ...)`. Phase 4's
`BrainBackedProviderRegistry` will have its own wiring (the OAuth completion
path constructs and registers the cloud adapter), so this restriction does not
leak into the V1 product surface — only into the §3.6 internal seam.

**Drift Note 3 — `WriteReceipt` / `WriteStatus` relocated to `Core.Abstractions.Models`.**
The plan placed `BulkItemResult` in `Core.Abstractions/Models/` with `Result<WriteReceipt>` as
the `Outcome` field, but `WriteReceipt` lived in `Core.Engine` — `Core.Abstractions` has no
project reference to `Core`, so the plan as written would not compile. Resolution (approved
inline mid-implementation): both `WriteReceipt` and `WriteStatus` were moved from
`src/FlashSkink.Core/Engine/` to `src/FlashSkink.Core.Abstractions/Models/` and their
namespace changed from `FlashSkink.Core.Engine` to `FlashSkink.Core.Abstractions.Models`.
This aligns them with the other public-API DTOs already living in Abstractions (`VolumeFile`,
`BlobRecord`, `BackgroundFailure`, `WriteReceipt`'s parent in §11's signatures). Five files
had to add `using FlashSkink.Core.Abstractions.Models;`: `WritePipeline.cs`,
`WritePipelineTests.cs`, `FlashSkinkVolumeTests.cs` (the third — `FlashSkinkVolume.cs` —
already imported the namespace for other types). Pure namespace move; zero behavioural
change.

**Drift Note 4 — Dispose order reversed: queue-first, mirror-second.** The dev plan §3.6
specified "brain mirror runs **before** the upload queue stops" with rationale (#1, #2)
about per-provider HTTP / connection-pool contention. That rationale is a cloud-provider
concern surfacing in Phase 4. In Phase 3 the practical race is on the shared brain
`SqliteConnection` — `Microsoft.Data.Sqlite` connections are explicitly not thread-safe,
and the mirror's `BackupDatabase` call running concurrently with an upload-queue worker
still mid-`MarkUploaded` / `DeleteSession` corrupts the connection state. The mirror
service's own `BackupDatabase` happens on a `Task.Run`, the worker on its own task — both
on the same connection. Reversing to queue-first / mirror-second drains the workers
before the snapshot snapshots; the final mirror then runs uncontended. Resolution
discovered empirically during the Step 6 test loop (`BrainMirror_OnCleanShutdown` failed
because the snapshot raced). When Phase 4 ships cloud providers with HTTP contention
concerns, the right answer is per-provider serialization inside the provider adapter
(not re-reversing the dispose order), so this drift is forward-compatible.

**Drift Note 5 — `BrainMirrorService.SnapshotAsync` SQLCipher fix.** The §3.5 `SnapshotAsync`
implementation opened the destination `SqliteConnection` without setting the SQLCipher key,
which works only when the source brain is plain SQLite. In production the brain is
SQLCipher-encrypted with a key derived from the DEK, so the destination must be opened
with the same `PRAGMA key` — otherwise `BackupDatabase` faults with a `SqliteException`
mapped to `ErrorCode.DatabaseReadFailed` ("Brain snapshot failed."). The §3.5 unit tests
masked this because they used a plain in-memory connection.

Fix landed here (a cross-PR scope deviation, authorized inline): added a
`DeriveBrainPragma` helper to `BrainMirrorService` that derives the brain key from
`_dek` and produces the `PRAGMA key` statement; `SnapshotAsync` runs the pragma on the
destination connection immediately after `Open()`, before `BackupDatabase`. The
key-derivation happens on the calling thread (the stackalloc'd span never crosses the
`await` boundary — Principle 20); only the resulting hex-encoded string crosses into
the `Task.Run` lambda. `BrainMirrorServiceTests` was updated to use a SQLCipher-keyed
in-memory source (via `CreateKeyedInMemoryConnection`) matching production; the
`TriggerMirrorAsync_MirrorDecryptsBackToBrainContent` test was updated to set the same
key on the recovered destination before querying.

**Drift Note 6 — Single pulse per bulk.** Dev plan §3.6 does not explicitly
prescribe pulse cardinality for `WriteBulkAsync`; the natural reading is "one
pulse per commit" matching `WriteFileAsync`. This plan pulses **once** at the
end of the bulk because (a) the wakeup channel coalesces to capacity 1 anyway,
(b) `BrainMirrorService.NotifyWriteCommitted` debounces with a 10 s window so N
notifications collapse to one mirror, and (c) one pulse per item would generate
N orchestrator wakeups that all dequeue zero new rows after the first (the
bulk is serialised under the gate; each per-item commit is already visible in
`TailUploads` before the next iteration). Documented so a future reviewer who
expects per-item pulses understands the rationale.

## Notes

**`InternalsVisibleTo("FlashSkink.Tests")` is already present.** Confirmed by
reading `src/FlashSkink.Core/FlashSkink.Core.csproj` during plan research —
added in §1.5 for `KeyVault` test access. No new attribute is needed for
`RegisterTailAsync`.

**Test data construction discipline.** Per CLAUDE.md "tests author their own
test data inline" — the integration tests construct `BulkWriteItem`,
`FileSystemProvider`, `TestNetworkAvailabilityMonitor`, and `FakeClock`
inline; assertions on `_brain/{timestamp}.bin` filename format use a regex
literal (not a reference to any production constant) so a typo in the
production format would surface.

**Windows file-handle hygiene.** Same convention as §2.7's tests —
`SqliteConnection.ClearAllPools()` before `Directory.Delete(_skinkRoot,
recursive: true)` in `DisposeAsync`. The new `_tailRoot` is plain filesystem
and does not need pool-clearing; a `try/finally` around `Directory.Delete` is
enough.

**Why no new test class for `WriteBulkAsync`.** The bulk-write tests are
*integration* tests (they round-trip writes through the real pipeline + upload
queue), not unit tests of a standalone type. Co-locating them with the upload
integration tests keeps the volume-level story in one file (~750 lines is well
within the convention for an integration suite — `FlashSkinkVolumeTests.cs`
from §2.7 is ~850).
