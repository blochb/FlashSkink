# PR 3.5 — Brain mirror service

**Branch:** `pr/3.5-brain-mirror-service`
**Blueprint sections:** §16.7 (brain backup/mirror), §18.5 (DEK-derived brain key — note: the SQLCipher brain *file* key is HKDF(DEK,"brain"); the *mirror payload* is encrypted with the raw DEK directly, fresh nonce), §21.4 (USB-loss recovery — the consumer of the mirror), §15 (uploads via `IStorageProvider`), §10.1 (`IStorageProvider` surface).
**Dev plan section:** `dev-plan/phase-3-Upload-queue-and-resumable-uploads.md` §3.5

## Scope

Phase 3 §3.5 ships `BrainMirrorService` — the per-volume background service that:

1. Takes a consistent SQLite snapshot of the brain database via `SqliteConnection.BackupDatabase`.
2. Encrypts the snapshot with AES-256-GCM using the volume's DEK and an authenticated header (4-byte `"FSBM"` magic, 2-byte version, 2 bytes reserved, 8-byte timestamp), then writes the encrypted mirror as `_brain/{timestampUtc:yyyyMMddTHHmmssZ}.bin` on every active tail via the existing `IStorageProvider` lifecycle (`BeginUploadAsync` → `UploadRangeAsync` → `FinaliseUploadAsync`).
3. Prunes each tail's `_brain/` listing to the most recent 3 entries (best-effort; failures are logged but do not fail the mirror).
4. Fires on three triggers:
    - **Commit-driven (debounced).** `NotifyWriteCommitted()` is called by §3.6's post-commit hook; an internal 10-second debounce coalesces bursts into one mirror.
    - **Timer (15 minutes).** Every `UploadConstants.BrainMirrorIntervalMinutes` minutes when the service is running.
    - **Clean shutdown.** `DisposeAsync` runs one final mirror synchronously (`CancellationToken.None`) before tearing down.

No volume integration is performed in this PR; §3.6 will construct `BrainMirrorService` and wire its `NotifyWriteCommitted` to `WritePipeline`'s post-commit event. Tests drive the trigger surface directly.

After this PR merges:

- A `BrainMirrorService` can be constructed against any open `SqliteConnection`, DEK, skink root, `IProviderRegistry`, and `IClock`.
- `Start(volumeToken)` enrols the 15-minute timer task; the debounce activates on first `NotifyWriteCommitted`.
- One commit-then-debounce produces exactly one mirror per active healthy tail.
- A 16-minute `FakeClock` advance produces exactly one timer-driven mirror per active tail.
- `DisposeAsync` produces exactly one final mirror and stops the timer.
- After 5 mirrors over time, the third-from-last is the oldest retained — older `_brain/` entries on each tail are deleted.
- A failure on tail A does not prevent the mirror landing on tail B (per-tail isolation, like §3.4 worker isolation).

## Files to create

### `src/FlashSkink.Core/Engine/`

- `BrainMirrorService.cs` — `public sealed class : IAsyncDisposable`. Owns the timer task, the debounce state machine, the per-mirror snapshot/encrypt/upload/prune flow, the volume-scoped `CancellationTokenSource`, and the run-serialization semaphore. ~520 LOC including XML docs and the catch ladders.
- `BrainMirrorHeader.cs` — `internal static class` with constants and `Write(Span<byte> dest, DateTime utcTimestamp)` + `TryParse(ReadOnlySpan<byte> src, out ushort version, out DateTime utcTimestamp)`. ~70 LOC. Encapsulates the 16-byte mirror header format so Phase 5's recovery code reuses the same parser.

### `tests/FlashSkink.Tests/Engine/`

- `BrainMirrorServiceTests.cs` — full coverage of triggers, retention, encryption round-trip, per-tail isolation, and dispose semantics. ~700 LOC. Uses `FakeClock`, `TestNetworkAvailabilityMonitor` (not consumed — see "Open questions"), `FileSystemProvider` rooted in per-test temp dirs, and `InMemoryProviderRegistry`.
- `BrainMirrorHeaderTests.cs` — round-trip, version mismatch, magic mismatch, truncated input. ~90 LOC.

### `tests/FlashSkink.Tests/_TestSupport/`

No new shim. `FakeClock` (§3.2) and `TestNetworkAvailabilityMonitor` (§3.4) already exist; `BrainMirrorService` does **not** depend on `INetworkAvailabilityMonitor` in V1 (see "Open questions, item 4" — mirror is best-effort and a probe failure on an offline host returns `ProviderUnreachable` naturally through the upload calls).

## Files to modify

- `src/FlashSkink.Core.Abstractions/Results/ErrorCode.cs` — add one new enum value, `ObjectDisposed`, in the "Control flow" section (after `Timeout`). Used by `BrainMirrorService.Start` when called on a disposed service. **This overrides cross-cutting decision 2** ("no new `ErrorCode` values in Phase 3"); see the resolution of Open Question 1 below for the rationale. Net delta: +4 LOC (XML doc + enum value).
- `src/FlashSkink.Core/Upload/UploadQueueService.cs` — one-line change. The §3.4 plan reused `VolumeAlreadyOpen` for the "service has been disposed" Result on `Start` as a precedent for §3.5. Now that `ObjectDisposed` exists, migrate the §3.4 call site to use the precise code. The message stays the same; only the code changes. Net delta: ~+0 / -0 LOC (one-token edit). Rationale: leaving `VolumeAlreadyOpen` in §3.4 would be a known wart the moment `ObjectDisposed` lands.
- *(Test surface)* `tests/FlashSkink.Tests/Upload/UploadQueueServiceTests.cs` — update the one test that asserts on `ErrorCode.VolumeAlreadyOpen` for the dispose-then-start path (if such a test exists; the §3.4 plan listed `Start_AfterDispose_*`). Implementer verifies and migrates the assertion if present.
- `src/FlashSkink.Core/Providers/FileSystemProvider.cs` — implement the `_brain/` namespace co-existence that dev plan §3.1 stated but never wired. Currently `BeginUploadAsync("_brain/{ts}.bin", ...)` sanitises the slash to an underscore and shards under `blobs/_b/ra/_brain_{ts}.bin`; `ListAsync("_brain", ct)` then finds nothing because no `_brain/` directory exists, so brain-mirror retention silently no-ops. Fix: in `ComputeRemotePath`, special-case names whose sanitised form begins with `"_brain_"` and route them to `{rootPath}/_brain/{nameAfterPrefix}` (unsharded). `ComputeRemoteId` continues to return `Path.GetRelativePath(_rootPath, full)` which naturally yields `"_brain/{nameAfterPrefix}"` for these entries. `FullPath` (used by `Download`/`Delete`/`Exists`/hash check) is unchanged — it already joins `{rootPath}` with whatever relative path it receives. Data blobs continue to flow through the existing `blobs/{xx}/{yy}/` sharding. Net delta: ~+10 LOC inside `ComputeRemotePath`. No public API change.
- *(Test surface)* `tests/FlashSkink.Tests/Providers/FileSystemProviderTests.cs` — add three small tests covering the new `_brain/` routing: `BeginUploadAsync_BrainPrefix_FinalisesAtBrainSubdir`, `ListAsync_BrainPrefix_ReturnsBrainEntries`, `Roundtrip_BrainObject_DownloadMatchesUpload`. Existing data-blob tests must remain green (the `blobs/` path is unchanged).

`IStorageProvider`, `IProviderRegistry`, `UploadConstants`, `IClock`, `VolumeContext`, `WritePipeline`, and every brain schema are unchanged.

## Dependencies

- **NuGet:** none new. `Microsoft.Data.Sqlite` (already in `Core`) exposes `SqliteConnection.BackupDatabase`. `System.IO.Hashing` (added in §2.5) is not used here.
- **Project references:** none added. `Core/Engine/BrainMirrorService.cs` references `Core.Abstractions` (`IStorageProvider`, `IProviderRegistry`, `UploadSession`, `INotificationBus`, `Notification`, `NotificationSeverity`, `Result`, `ErrorCode`, `IClock`), `Core.Crypto` (no public symbols imported — mirror encryption is bespoke; see "Why we don't reuse `CryptoPipeline`" below), `Core.Storage.AtomicWriteHelper` (internal — same assembly), and `Microsoft.Extensions.Logging.Abstractions`.

## Public API surface

### `FlashSkink.Core.Engine.BrainMirrorService` (sealed class, `IAsyncDisposable`)

Summary intent: per-volume background service that mirrors the brain DB to every active tail on commit (debounced), on a 15-minute timer, and on clean shutdown. Owns the SQLite snapshot path, the encryption with `DEK + AAD(FSBM-header)`, the upload through `IStorageProvider`, and the 3-rolling retention pass.

Constructor:

```csharp
public BrainMirrorService(
    SqliteConnection brainConnection,
    ReadOnlyMemory<byte> dek,
    string skinkRoot,
    IProviderRegistry providerRegistry,
    INotificationBus notificationBus,
    IClock clock,
    ILogger<BrainMirrorService> logger);
```

7 dependencies. `brainConnection` is borrowed (the volume owns its lifetime — same ownership rule as `VolumeContext`). `dek` is a `ReadOnlyMemory<byte>` view of the volume's DEK, never copied to a longer-lived buffer (Principle 31). `skinkRoot` is needed for the staging path (`{skinkRoot}/.flashskink/staging/brain-mirror-{ticks}.db` — Principle 7 says staging lives on the skink, never `Path.GetTempPath()`). `providerRegistry` is the live `IProviderRegistry` whose snapshot is consulted at the top of each mirror run.

Public surface:

```csharp
/// <summary>
/// Enrols the 15-minute timer task and arms the debounce machinery. Idempotent: a second call
/// returns Ok without effect. Returns Ok immediately; does not wait for the first mirror.
/// Subsequent <see cref="NotifyWriteCommitted"/> calls schedule a debounced mirror.
/// </summary>
public Result Start(CancellationToken volumeToken);

/// <summary>
/// Signals that <c>WritePipeline</c> just committed a write. Resets the 10-second debounce
/// window; when it elapses without further commits, one mirror runs against every active tail.
/// Safe to call from any thread; never blocks; never throws. No-op when not started or
/// already disposed.
/// </summary>
public void NotifyWriteCommitted();

/// <summary>
/// Runs one mirror cycle synchronously and waits for it to complete. Used by
/// <see cref="DisposeAsync"/> for the clean-shutdown final mirror and exposed for tests.
/// Acquires the per-service run lock; if a cycle is already running, waits for it to finish
/// then runs another. Cancellation is honoured between per-tail uploads, not mid-upload
/// (Principle 17 within a single tail's lifecycle).
/// </summary>
public ValueTask<Result> TriggerMirrorAsync(CancellationToken ct);

/// <summary>
/// Cancels the timer and debounce tasks, runs one final mirror cycle with
/// <see cref="CancellationToken.None"/>, awaits the background tasks (5 s budget each), and
/// disposes internal state. Idempotent.
/// </summary>
public ValueTask DisposeAsync();
```

**Why `Start` is sync-returning, not `StartAsync`:** mirrors §3.4's `UploadQueueService.Start` rationale — no `await` is needed to enrol the timer task; `Task.Factory.StartNew(..., LongRunning)` plus a `CompareExchange` guard. Principle 13's "ct last on async methods" still applies (and is satisfied: `Start` takes one `volumeToken` parameter, used solely for the internal linked-source construction).

**Why `NotifyWriteCommitted` is sync `void`:** the call site is `WritePipeline`'s post-commit branch (wired in §3.6). It must not block the commit path. The method's body is `Interlocked.Exchange(ref _lastCommitTicks, _clock.UtcNow.Ticks)` plus a single `_debouncePulse.Pulse()` — a few nanoseconds.

**Why `TriggerMirrorAsync` is public:** the dev plan §3.5 specifies "Clean shutdown — `DisposeAsync` runs one final mirror synchronously before tearing down." Exposing the trigger method lets `DisposeAsync` call it deterministically (rather than reproducing the cycle inside the dispose path), and lets tests verify the mirror flow without waiting on real timers. The contract is "run *one* full cycle"; concurrent callers serialize on the internal `_runLock`.

### `FlashSkink.Core.Engine.BrainMirrorHeader` (internal static class)

Summary intent: encapsulates the 16-byte mirror header format used as AES-GCM AAD. Phase 5 recovery reuses `TryParse` to validate downloaded mirrors before decryption (rollback-attack defence — see "Mirror authenticity" in dev plan §3.5).

```csharp
namespace FlashSkink.Core.Engine;

internal static class BrainMirrorHeader
{
    /// <summary>4-byte ASCII magic <c>"FSBM"</c>.</summary>
    public const uint Magic = 0x4D425346; // little-endian "FSBM"

    /// <summary>Current header version.</summary>
    public const ushort Version = 1;

    /// <summary>Total header size in bytes.</summary>
    public const int Size = 16;

    /// <summary>Layout: 4B magic, 2B version, 2B reserved (zero), 8B <see cref="DateTime.ToBinary"/>.</summary>
    public static void Write(Span<byte> dest, DateTime utcTimestamp);

    public static bool TryParse(
        ReadOnlySpan<byte> src,
        out ushort version,
        out DateTime utcTimestamp);
}
```

`Write` is the producer side; `TryParse` is the consumer side (Phase 5 + the §3.5 tests' round-trip assertion). Both are pure functions over their input spans.

**Why a separate `internal` type and not inline literals:** the header is the AAD; a typo in the producer that matches a typo in the consumer would silently accept malformed mirrors. Centralising in one type with a typed `Write`/`TryParse` pair is the same defence as `BlobHeader` in §2.4. The CLAUDE.md convention "tests author their own data inline" still applies — `BrainMirrorHeaderTests` will assert the binary bytes by literal `[0x46, 0x53, 0x42, 0x4D, ...]` rather than reading from `BrainMirrorHeader.Magic`.

## Internal types and members

### Inside `BrainMirrorService`:

State fields:

```csharp
private readonly SqliteConnection _brain;
private readonly ReadOnlyMemory<byte> _dek;
private readonly string _skinkRoot;
private readonly IProviderRegistry _registry;
private readonly INotificationBus _bus;
private readonly IClock _clock;
private readonly ILogger<BrainMirrorService> _logger;

private CancellationTokenSource? _serviceCts;
private Task? _timerTask;
private Task? _debounceTask;
private readonly SemaphoreSlim _runLock = new(1, 1);
private long _lastCommitTicks;          // Interlocked-updated; 0 means "no commit since last debounce fire"
private readonly UploadWakeupSignal _debouncePulse = new();

private int _started;                   // Interlocked guard
private int _disposed;                  // Interlocked guard

private const int DebounceWindowSeconds = 10;
private const int StagingFileNamePrefix = ".flashskink/staging/brain-mirror-";
private const long MaxInMemoryMirrorBytes = 1L * 1024 * 1024 * 1024;  // 1 GiB
```

Private methods (each spelled out in **Method-body contracts** below):

- `private async Task TimerLoopAsync(CancellationToken ct)` — runs `clock.Delay(15 min)` in a loop, calling `TriggerMirrorAsync(ct)` on each tick.
- `private async Task DebounceLoopAsync(CancellationToken ct)` — awaits `_debouncePulse.WaitAsync(ct)`, then `clock.Delay(10 s, ct)`, then resets `_lastCommitTicks` (if no new commit landed in the window, fires `TriggerMirrorAsync(ct)`; if a new commit landed, the loop iterates and the window restarts).
- `private async Task<Result> RunOneCycleAsync(CancellationToken ct)` — the real work: snapshot → encrypt → upload-to-each-tail → prune-each-tail → cleanup.
- `private async Task<Result<string>> SnapshotAsync(DateTime nowUtc, CancellationToken ct)` — produces the on-disk staging snapshot file at `{skinkRoot}/.flashskink/staging/brain-mirror-{ticks}.db`. Returns the staging path.
- `private async Task<Result<byte[]>> ReadAndEncryptAsync(string stagingPath, DateTime headerTimestamp, CancellationToken ct)` — reads, encrypts, returns the on-the-wire bytes (header || nonce || ciphertext || tag).
- `private async Task<Result<string>> UploadToOneTailAsync(IStorageProvider provider, byte[] payload, DateTime headerTimestamp, CancellationToken ct)` — runs the §10.1 lifecycle for one mirror. Returns the `remoteId`.
- `private async Task<Result> PruneOneTailAsync(IStorageProvider provider, CancellationToken ct)` — lists `_brain/`, sorts descending by name (ISO-8601 ⇒ chronological), deletes everything beyond index 3.
- `private async Task PublishMirrorFailureAsync(string tailDisplayName, ErrorCode code, string message, CancellationToken ct)` — single source of truth for Principle-25 notification text.

## Method-body contracts

### `BrainMirrorService.Start(CancellationToken volumeToken)`

**Pre:** Service is not yet started; not disposed.

**Steps:**
1. If `Interlocked.CompareExchange(ref _started, 1, 0) != 0`: return `Result.Ok()` (idempotent).
2. If `_disposed == 1`: return `Result.Fail(ErrorCode.ObjectDisposed, "Brain mirror service has been disposed.")`. Uses the new `ObjectDisposed` code added in this PR (see Files to modify and the resolution of Open Question 1).
3. `_serviceCts = CancellationTokenSource.CreateLinkedTokenSource(volumeToken)`.
4. `_timerTask = Task.Factory.StartNew(() => TimerLoopAsync(_serviceCts.Token), CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap()`.
5. `_debounceTask = Task.Factory.StartNew(() => DebounceLoopAsync(_serviceCts.Token), CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap()`.
6. Return `Result.Ok()`.

**Errors:** outer `catch (Exception ex)` → `Result.Fail(Unknown, ..., ex)`. Defensive only — none of steps 1–5 should fault.

### `BrainMirrorService.NotifyWriteCommitted()`

```csharp
public void NotifyWriteCommitted()
{
    if (_started == 0 || _disposed == 1) { return; }
    Interlocked.Exchange(ref _lastCommitTicks, _clock.UtcNow.Ticks);
    _debouncePulse.Pulse();
}
```

Strictly non-throwing, non-blocking. Pulses the debounce signal; `DebounceLoopAsync` picks it up.

### `BrainMirrorService.TimerLoopAsync(CancellationToken ct)`

```
while (!ct.IsCancellationRequested):
    try:
        await _clock.Delay(TimeSpan.FromMinutes(UploadConstants.BrainMirrorIntervalMinutes), ct)
        var r = await TriggerMirrorAsync(ct)
        if (!r.Success && r.Error.Code != Cancelled):
            _logger.LogWarning("Timer-driven brain mirror cycle failed: {Code}", r.Error.Code)
    catch (OperationCanceledException): break
    catch (Exception ex):
        _logger.LogError(ex, "Brain mirror timer loop faulted; continuing.")
        try { await _clock.Delay(TimeSpan.FromMinutes(1), ct); }
        catch (OperationCanceledException) { break; }
```

Defence-in-depth `catch (Exception)` mirror's §3.4's orchestrator: the service must not silently die (Principle 24).

### `BrainMirrorService.DebounceLoopAsync(CancellationToken ct)`

```
while (!ct.IsCancellationRequested):
    try:
        // Wait for the first commit.
        await _debouncePulse.WaitAsync(ct)
        if (ct.IsCancellationRequested) break

        // Debounce: keep sliding the window while commits keep arriving.
        while (true) {
            long commitTicksAtWindowStart = Interlocked.Read(ref _lastCommitTicks)
            await _clock.Delay(TimeSpan.FromSeconds(DebounceWindowSeconds), ct)
            long commitTicksAtWindowEnd = Interlocked.Read(ref _lastCommitTicks)
            if (commitTicksAtWindowEnd == commitTicksAtWindowStart) break  // window quiet — fire
            // Else: another commit landed during the window; loop and wait another full window.
        }

        // Window quiet: clear the marker so next round starts fresh.
        Interlocked.Exchange(ref _lastCommitTicks, 0)
        var r = await TriggerMirrorAsync(ct)
        if (!r.Success && r.Error.Code != Cancelled):
            _logger.LogWarning("Commit-driven brain mirror cycle failed: {Code}", r.Error.Code)
    catch (OperationCanceledException): break
    catch (Exception ex):
        _logger.LogError(ex, "Brain mirror debounce loop faulted; continuing.")
        try { await _clock.Delay(TimeSpan.FromSeconds(1), ct); }
        catch (OperationCanceledException) { break; }
```

**Why this debounce shape and not a simpler "wait 10 s after each pulse":** the simpler shape would coalesce N commits over 10 s into one mirror — but if commit 1 happens at t=0 and commit 2 at t=9.9 s, the simple shape fires at t=10 s with the second commit only 0.1 s old, which arguably misses the user's expectation that the mirror captures *settled* state. The sliding-window shape ensures the window is quiet for the full 10 s before firing. This is the "stable-input debounce" pattern, idiomatic in UI frameworks (e.g. ReactiveX's `Throttle`/`Debounce` distinction).

### `BrainMirrorService.TriggerMirrorAsync(CancellationToken ct)`

```
await _runLock.WaitAsync(ct)
try {
    return await RunOneCycleAsync(ct)
} finally {
    _runLock.Release()
}
```

Serializes concurrent triggers (timer-while-shutdown-runs-final, dispose-while-debounce-fires) without losing them — the second caller waits on the lock and then runs a clean second cycle.

### `BrainMirrorService.RunOneCycleAsync(CancellationToken ct)`

```
ct.ThrowIfCancellationRequested()
var nowUtc = _clock.UtcNow
string? stagingPath = null
byte[]? payload = null
try {
    // 1. Snapshot.
    var snapResult = await SnapshotAsync(nowUtc, ct)
    if (!snapResult.Success) return Result.Fail(snapResult.Error)
    stagingPath = snapResult.Value

    // Check size cap.
    var snapSize = new FileInfo(stagingPath).Length
    if (snapSize > MaxInMemoryMirrorBytes) {
        _logger.LogError("Brain snapshot size {Size} exceeds {Cap} bytes; aborting cycle.", snapSize, MaxInMemoryMirrorBytes)
        await PublishMirrorFailureAsync(
            tailDisplayName: "(all tails)",
            code: ErrorCode.FileTooLong,
            message: $"Brain catalogue copy is too large for this version ({snapSize} bytes).",
            ct: ct)
        var meta = new Dictionary<string, string> {
            ["SnapshotSizeBytes"] = snapSize.ToString(CultureInfo.InvariantCulture),
            ["CapBytes"] = MaxInMemoryMirrorBytes.ToString(CultureInfo.InvariantCulture),
        }
        return Result.Fail(new ErrorContext {
            Code = ErrorCode.FileTooLong,
            Message = "Brain mirror snapshot exceeds the supported in-memory size.",
            Metadata = meta,
        })
    }

    // 2. Encrypt.
    var encryptResult = await ReadAndEncryptAsync(stagingPath, nowUtc, ct)
    if (!encryptResult.Success) return Result.Fail(encryptResult.Error)
    payload = encryptResult.Value

    // 3. List active providers.
    var listResult = await _registry.ListActiveProviderIdsAsync(ct)
    if (!listResult.Success) {
        _logger.LogWarning("Cannot list providers for brain mirror: {Code}", listResult.Error.Code)
        return Result.Fail(listResult.Error)
    }

    // 4. Per-tail upload + prune. Per-tail isolation: failure on tail A does not affect tail B.
    foreach (var providerId in listResult.Value) {
        ct.ThrowIfCancellationRequested()
        var providerResult = await _registry.GetAsync(providerId, ct)
        if (!providerResult.Success) {
            _logger.LogWarning("Provider {Id} not available for brain mirror: {Code}", providerId, providerResult.Error.Code)
            continue
        }
        var provider = providerResult.Value

        var upResult = await UploadToOneTailAsync(provider, payload, nowUtc, ct)
        if (!upResult.Success) {
            await PublishMirrorFailureAsync(provider.DisplayName, upResult.Error.Code, upResult.Error.Message, ct)
            // No prune; the prune pass would race a half-uploaded mirror. Move on.
            continue
        }

        // Prune. Best-effort: failures here don't fail the cycle.
        var pruneResult = await PruneOneTailAsync(provider, ct)
        if (!pruneResult.Success) {
            _logger.LogWarning("Brain mirror retention prune failed for {Tail}: {Code}",
                provider.DisplayName, pruneResult.Error.Code)
        }
    }

    return Result.Ok()
}
catch (OperationCanceledException ex) {
    _logger.LogInformation("Brain mirror cycle cancelled.")
    return Result.Fail(ErrorCode.Cancelled, "Brain mirror cycle cancelled.", ex)
}
catch (Exception ex) {
    _logger.LogError(ex, "Brain mirror cycle faulted unexpectedly.")
    return Result.Fail(ErrorCode.Unknown, "Brain mirror cycle faulted.", ex)
}
finally {
    // 5. Cleanup staging snapshot — Principle 17 (compensation uses CancellationToken.None).
    if (stagingPath is not null) {
        try { File.Delete(stagingPath); }
        catch (IOException ex) { _logger.LogWarning(ex, "Could not delete staging snapshot {Path}", stagingPath); }
        catch (UnauthorizedAccessException ex) { _logger.LogWarning(ex, "Access denied deleting staging snapshot {Path}", stagingPath); }
    }
    // Zero the encrypted-payload buffer? — payload is ciphertext (not a secret) and is a managed byte[].
    // No explicit zeroing required (ciphertext is not a secret; Principle 31 covers keys, not output).
    payload = null
}
```

**On the size cap:** the dev plan §3.5 documents a 1 GiB hard cap with a streaming-fallback "stretch goal" if profiling reveals real-world brains over the cap. V1 implements the in-memory path; over-cap is a `Result.Fail` plus a user notification ("Brain catalogue copy is too large for this version"). Streaming variant is explicitly a non-goal.

### `BrainMirrorService.SnapshotAsync(DateTime nowUtc, CancellationToken ct)`

```
ct.ThrowIfCancellationRequested()
var stagingDir = Path.Combine(_skinkRoot, ".flashskink", "staging")
Directory.CreateDirectory(stagingDir)
AtomicWriteHelper.FsyncDirectory(stagingDir)

var stagingPath = Path.Combine(stagingDir, $"brain-mirror-{nowUtc.Ticks}.db")

try {
    // SqliteConnection.BackupDatabase is synchronous; offload to a worker thread so the
    // service's awaiter doesn't block the calling sync-context. (BackupDatabase internally
    // uses sqlite3_backup_step in a loop; the SQLite docs guarantee it's safe alongside
    // concurrent readers/writers on the source — that's the whole point.)
    await Task.Run(() => {
        // Connection string Pooling=false (mirrors BrainConnectionFactory): ensures the
        // destination's WAL is fully flushed when the inner Dispose runs.
        var csb = new SqliteConnectionStringBuilder { DataSource = stagingPath, Pooling = false }
        using var dest = new SqliteConnection(csb.ConnectionString)
        dest.Open()
        _brain.BackupDatabase(dest)    // sqlite3_backup_init + step + finish
        dest.Close()                   // explicit; flushes destination WAL
    }, ct).ConfigureAwait(false)

    // fsync the staging file via a one-shot read-write-flush — Microsoft.Data.Sqlite does
    // not expose sqlite3_db_sync, so a Stream-level fsync is the substitute. Open with
    // FileAccess.ReadWrite so flush-to-disk works; do not truncate (no Mode.Create).
    using (var fs = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
        RandomAccess.FlushToDisk(fs.SafeFileHandle)
    }
    AtomicWriteHelper.FsyncDirectory(stagingDir)

    return Result<string>.Ok(stagingPath)
}
catch (OperationCanceledException ex) {
    try { File.Delete(stagingPath); } catch { }
    return Result<string>.Fail(ErrorCode.Cancelled, "Snapshot cancelled.", ex)
}
catch (SqliteException ex) {
    try { File.Delete(stagingPath); } catch { }
    return Result<string>.Fail(ErrorCode.DatabaseReadFailed, "Brain snapshot failed.", ex)
}
catch (IOException ex) {
    try { File.Delete(stagingPath); } catch { }
    return Result<string>.Fail(ErrorCode.Unknown, "I/O failure during brain snapshot.", ex)
}
catch (Exception ex) {
    try { File.Delete(stagingPath); } catch { }
    return Result<string>.Fail(ErrorCode.Unknown, "Unexpected error during brain snapshot.", ex)
}
```

**Important:** `SqliteConnection.BackupDatabase(otherConnection)` is the canonical SQLite online-backup API (wraps `sqlite3_backup_init`/`step`/`finish`). It produces a consistent snapshot without locking the source — exactly what blueprint §16.7 step 1 specifies.

**ErrorCode reminder:** cross-cutting decision 2 forbids new codes. `DatabaseReadFailed` is the existing code in `ErrorCode.cs` (verified at Gate 1) used for read-side SQLite failures elsewhere. If it does *not* exist (verification turns out negative), fall back to `Unknown` and document. See "Open questions, item 1."

### `BrainMirrorService.ReadAndEncryptAsync(string stagingPath, DateTime headerTimestamp, CancellationToken ct)`

```
ct.ThrowIfCancellationRequested()
long size = new FileInfo(stagingPath).Length
if (size > MaxInMemoryMirrorBytes) {
    // Defence in depth: RunOneCycleAsync already checked, but the file could have grown
    // between FileInfo.Length there and FileInfo.Length here (highly unlikely; this branch
    // is for completeness).
    return Result<byte[]>.Fail(ErrorCode.FileTooLong, "Brain snapshot exceeds in-memory cap.")
}
// MaxInMemoryMirrorBytes is 1 GiB ≤ int.MaxValue (~2.1 GiB), so (int)size is safe.
int plaintextLen = (int)size

byte[] plaintext = new byte[plaintextLen]
using (var fs = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true)) {
    int read = 0
    while (read < plaintextLen) {
        int n = await fs.ReadAsync(plaintext.AsMemory(read, plaintextLen - read), ct).ConfigureAwait(false)
        if (n == 0) throw new IOException("Unexpected EOF reading brain snapshot.")
        read += n
    }
}

// Output layout: header(16) || nonce(12) || ciphertext(plaintextLen) || tag(16)
const int HeaderSize = 16
const int NonceSize = 12
const int TagSize = 16
byte[] payload = new byte[HeaderSize + NonceSize + plaintextLen + TagSize]

// Header (also serves as AAD).
BrainMirrorHeader.Write(payload.AsSpan(0, HeaderSize), headerTimestamp)

// Nonce — fresh random per mirror.
Span<byte> nonce = stackalloc byte[NonceSize]
RandomNumberGenerator.Fill(nonce)
nonce.CopyTo(payload.AsSpan(HeaderSize, NonceSize))

// Encrypt.
Span<byte> ciphertext = payload.AsSpan(HeaderSize + NonceSize, plaintextLen)
Span<byte> tag = payload.AsSpan(HeaderSize + NonceSize + plaintextLen, TagSize)
try {
    using var aes = new AesGcm(_dek.Span, TagSize)
    aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData: payload.AsSpan(0, HeaderSize))
}
catch (CryptographicException ex) {
    return Result<byte[]>.Fail(ErrorCode.EncryptionFailed, "Brain mirror AES-GCM failed.", ex)
}

// Plaintext was a snapshot of the brain (which contains encrypted OAuth tokens but also
// metadata, paths, sizes, timestamps — *not* file content, but still secret enough that the
// in-memory copy is wiped after encryption per defence-in-depth, even though Principle 31's
// strict scope is keys, not metadata. The cost is one CryptographicOperations.ZeroMemory call.)
CryptographicOperations.ZeroMemory(plaintext)

return Result<byte[]>.Ok(payload)
```

**Why we don't reuse `CryptoPipeline`:** `CryptoPipeline.Encrypt` is purpose-built for the file-blob layout (20-byte `BlobHeader` with magic `FSBL`/flags/nonce, plus caller-supplied AAD). The mirror format is different — the AAD is the mirror's own typed header (`BrainMirrorHeader`, magic `FSBM`), and there's no compression-flag field on a mirror (the brain DB has its own compression characteristics). Forcing `CryptoPipeline` to serve both would either pollute its signature with mirror-specific options or require an awkward wrapper. The encryption primitive — `AesGcm.Encrypt` with a 12-byte nonce and 16-byte tag — is invoked directly here. The total mirror-specific cryptography surface is ~10 lines and fits within `ReadAndEncryptAsync`; pulling it into a shared helper would *cost* clarity rather than save duplication.

### `BrainMirrorService.UploadToOneTailAsync(IStorageProvider provider, byte[] payload, DateTime headerTimestamp, CancellationToken ct)`

```
string remoteName = $"_brain/{headerTimestamp:yyyyMMddTHHmmssZ}.bin"

// 1. Open session.
var sessionResult = await provider.BeginUploadAsync(remoteName, payload.LongLength, ct).ConfigureAwait(false)
if (!sessionResult.Success) return Result<string>.Fail(sessionResult.Error)
var session = sessionResult.Value

// 2. Range loop — RangeSize chunks (4 MiB).
int offset = 0
while (offset < payload.Length) {
    ct.ThrowIfCancellationRequested()
    int chunkLen = Math.Min(UploadConstants.RangeSize, payload.Length - offset)
    var rangeMem = payload.AsMemory(offset, chunkLen)
    var rangeResult = await provider.UploadRangeAsync(session, offset, rangeMem, ct).ConfigureAwait(false)
    if (!rangeResult.Success) {
        // Best-effort abort; surface the original failure.
        await provider.AbortUploadAsync(session, CancellationToken.None).ConfigureAwait(false)
        return Result<string>.Fail(rangeResult.Error)
    }
    offset += chunkLen
}

// 3. Finalise.
var finResult = await provider.FinaliseUploadAsync(session, ct).ConfigureAwait(false)
if (!finResult.Success) {
    await provider.AbortUploadAsync(session, CancellationToken.None).ConfigureAwait(false)
    return Result<string>.Fail(finResult.Error)
}

return Result<string>.Ok(finResult.Value)
```

The mirror's upload **does not** go through `RangeUploader`/`UploadQueueService` for two reasons:

1. **No `UploadSessions` row** — `RangeUploader` is built around the `UploadSessions` table for resume-across-process-restart of *data blob* uploads. A mirror snapshot is per-cycle; the next cycle will produce a fresh mirror anyway, so cross-restart resume is unnecessary.
2. **No `TailUploads` row** — the brain mirror is not a "file" with a brain row. The §3.4 worker queries `TailUploads`; threading the mirror through it would require either a sentinel row or a special-case branch in the worker. The simpler answer is the one specified by dev plan §3.5 step 4: "Reuse `IStorageProvider.BeginUploadAsync` / `UploadRangeAsync` / `FinaliseUploadAsync` — the mirror is a normal blob from the provider's perspective."

Retry semantics: a single per-range failure aborts the mirror for this tail and the next cycle starts fresh. No §21.1 ladder for mirrors (the dev plan does not specify one, and the next timer-driven or commit-driven cycle is the natural retry path). Recorded as **non-goal**.

### `BrainMirrorService.PruneOneTailAsync(IStorageProvider provider, CancellationToken ct)`

```
var listResult = await provider.ListAsync("_brain", ct).ConfigureAwait(false)
if (!listResult.Success) return Result.Fail(listResult.Error)

// remoteIds are sortable by lex order — yyyyMMddTHHmmssZ encoding makes lex order = chronological.
// Sort descending so [0..3) are the most recent we keep.
var sorted = listResult.Value
    .Where(id => id.StartsWith("_brain/", StringComparison.Ordinal) && id.EndsWith(".bin", StringComparison.Ordinal))
    .OrderByDescending(id => id, StringComparer.Ordinal)
    .ToList()

if (sorted.Count <= UploadConstants.BrainMirrorRollingCount) return Result.Ok()

// Delete everything beyond the 3 most recent. Best-effort per item — log on failure, continue.
foreach (var stale in sorted.Skip(UploadConstants.BrainMirrorRollingCount)) {
    var delResult = await provider.DeleteAsync(stale, ct).ConfigureAwait(false)
    if (!delResult.Success) {
        _logger.LogWarning("Could not prune stale mirror {RemoteId} from {Tail}: {Code}",
            stale, provider.DisplayName, delResult.Error.Code)
    }
}

return Result.Ok()
```

The filter on `_brain/` prefix and `.bin` suffix is defensive — `ListAsync("_brain", ct)` should already return only mirror entries, but a stray probe file (`.flashskink-staging` analogue) would otherwise be deletion-eligible. Belt and braces.

### `BrainMirrorService.PublishMirrorFailureAsync(string tailDisplayName, ErrorCode code, string message, CancellationToken ct)`

```
await _bus.PublishAsync(new Notification {
    Source = "BrainMirrorService",
    Severity = NotificationSeverity.Error,
    Title = "Could not save the catalogue copy",          // dev plan §3.5 Principle 25 wording
    Message = $"Could not save the catalogue copy to '{tailDisplayName}'.",
    Error = new ErrorContext { Code = code, Message = message },
    OccurredUtc = _clock.UtcNow,
    RequiresUserAction = false,
}, CancellationToken.None).ConfigureAwait(false)
```

`CancellationToken.None` for publish per Principle 17 — notification publication is bookkeeping that must not be cancelled mid-flight.

Vocabulary check: "catalogue copy" is the user-facing rendering of "brain mirror" per dev plan §3.5 Principle 25 quote. No "brain", "mirror", "tail", "blob", "session" in user-visible text. The `Error.Code` carries the technical detail for handlers that want it.

### `BrainMirrorService.DisposeAsync()`

```
if (Interlocked.Exchange(ref _disposed, 1) != 0) return

// 1. Cancel background tasks. Final mirror runs *under CancellationToken.None* so the
//    cancellation we just raised doesn't trip it.
try { _serviceCts?.Cancel(); } catch { /* CTS already disposed */ }

// 2. Final mirror (Principle 17 — compensation must complete).
try {
    var final = await TriggerMirrorAsync(CancellationToken.None).ConfigureAwait(false)
    if (!final.Success && final.Error.Code != ErrorCode.Cancelled) {
        _logger.LogWarning("Final brain mirror on shutdown failed: {Code} {Message}",
            final.Error.Code, final.Error.Message)
    }
}
catch (Exception ex) {
    _logger.LogError(ex, "Final brain mirror on shutdown faulted.")
}

// 3. Complete the debounce signal so the loop exits cleanly.
_debouncePulse.Complete()       // requires friend access — see Open questions item 2

// 4. Await background tasks (5 s budget each).
async Task AwaitWithBudget(Task? t, string name) {
    if (t is null) return
    try { await t.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false) }
    catch (TimeoutException) { _logger.LogWarning("{Task} did not stop within 5 s.", name) }
    catch (OperationCanceledException) { /* expected */ }
}
await AwaitWithBudget(_timerTask, "Timer loop")
await AwaitWithBudget(_debounceTask, "Debounce loop")

// 5. Dispose internal state.
_serviceCts?.Dispose()
_runLock.Dispose()
```

**About `_debouncePulse.Complete()`:** `UploadWakeupSignal.Complete` is `internal` (per §3.4 plan). Since `BrainMirrorService` is in `Core/Engine/` (same assembly as `Upload/UploadWakeupSignal.cs`), the `internal` accessor is reachable. No accessibility change needed. Verified at Gate 1.

**Order rationale:** final mirror *before* cancelling background tasks would leave the timer free to fire during the final mirror, and `TriggerMirrorAsync` would queue behind `RunOneCycleAsync` on the run lock — fine, but wasteful. Cancelling the timer first ensures only the explicit final mirror runs.

## Integration points

This PR consumes only existing types; it does not call into any volume-level method.

- `FlashSkink.Core.Abstractions.Providers.IStorageProvider` — `BeginUploadAsync` / `UploadRangeAsync` / `FinaliseUploadAsync` / `AbortUploadAsync` / `ListAsync` / `DeleteAsync`. Frozen contract from §3.1.
- `FlashSkink.Core.Abstractions.Providers.IProviderRegistry` — `GetAsync`, `ListActiveProviderIdsAsync`. Frozen from §3.1.
- `FlashSkink.Core.Abstractions.Notifications.INotificationBus` + `Notification` + `NotificationSeverity` — publish path.
- `FlashSkink.Core.Abstractions.Time.IClock` — `UtcNow` + `Delay` (timer + debounce).
- `FlashSkink.Core.Abstractions.Results.Result`, `Result<T>`, `ErrorContext`, `ErrorCode`.
- `FlashSkink.Core.Upload.UploadConstants` — `BrainMirrorIntervalMinutes`, `BrainMirrorRollingCount`, `RangeSize`.
- `FlashSkink.Core.Upload.UploadWakeupSignal` — reused for the debounce pulse primitive. Same accessibility (sealed class, public surface, internal `Complete`).
- `FlashSkink.Core.Storage.AtomicWriteHelper.FsyncDirectory` — internal helper for the staging dir.
- `Microsoft.Data.Sqlite.SqliteConnection.BackupDatabase` — the SQLite online-backup API.
- `System.Security.Cryptography.AesGcm`, `RandomNumberGenerator`, `CryptographicOperations.ZeroMemory`.
- `Microsoft.Extensions.Logging.Abstractions.ILogger<T>`.

This PR does **not** modify or consume:

- `WritePipeline` — §3.6 wires `NotifyWriteCommitted` to its post-commit branch.
- `FlashSkinkVolume` — does not yet exist (§3.6 creates).
- `UploadQueueService`, `RangeUploader`, `RetryPolicy` — orthogonal to the mirror flow.
- `VolumeContext` — the mirror gets its dependencies wired directly by §3.6's lifecycle code.
- Any brain schema or migration.

## Principles touched

- **Principle 1** — every public method on `BrainMirrorService` returns `Result` / `Result<T>` or `ValueTask` thereof. `NotifyWriteCommitted` is a sanctioned non-Result method: it cannot fail (no I/O, no allocation that could OOM, returns void). XML doc states "Never throws; no-op when not started or already disposed." `DisposeAsync` is `ValueTask` per `IAsyncDisposable` (same shape as `UploadQueueService.DisposeAsync` in §3.4).
- **Principle 6** — every byte that leaves the skink (the mirror payload uploaded to each tail) is AES-256-GCM encrypted with the DEK. The unencrypted staging snapshot lives on the skink (`.flashskink/staging/`) and is deleted in the `finally` of `RunOneCycleAsync`. Zero-knowledge holds.
- **Principle 7** — staging is on the skink at `{skinkRoot}/.flashskink/staging/brain-mirror-{ticks}.db`. Never `Path.GetTempPath()`.
- **Principle 8** — `BrainMirrorService` lives in `Core/Engine/`; references no UI/Presentation. `BrainMirrorHeader` is `internal` in `Core/Engine/`. Both reference only `Core.Abstractions`, `System.IO`, `System.Security.Cryptography`, `Microsoft.Data.Sqlite`, and `Microsoft.Extensions.Logging.Abstractions`.
- **Principle 12** — OS-agnostic. `SqliteConnection.BackupDatabase`, `AesGcm`, `RandomNumberGenerator`, file I/O are all cross-platform. `AtomicWriteHelper.FsyncDirectory` already handles Windows-vs-POSIX.
- **Principle 13** — every async method has `CancellationToken ct` as final parameter. `NotifyWriteCommitted()` is sync and takes no token (the call site cannot be cancelled — it's an event publication on the commit path).
- **Principle 14** — `OperationCanceledException` is first catch in every loop (`TimerLoopAsync`, `DebounceLoopAsync`, `RunOneCycleAsync`, `SnapshotAsync`). It maps to `ErrorCode.Cancelled`. Logged at `Information`, never `Error`.
- **Principle 15** — narrow-to-broad catches: `OperationCanceledException` → `SqliteException` → `IOException` / `UnauthorizedAccessException` → `CryptographicException` → `Exception` last.
- **Principle 16** — every `using` / `await using`: `SqliteConnection` for the snapshot destination, `FileStream` for the read, `AesGcm` for encryption. The `byte[] plaintext` is zeroed before falling out of scope. The `byte[] payload` is ciphertext (not a secret) — set to null in `finally` for GC.
- **Principle 17** — `CancellationToken.None` is a *literal* (per the principle's text) at:
  - The `provider.AbortUploadAsync(...)` calls inside the upload retry path.
  - The notification publish in `PublishMirrorFailureAsync`.
  - The staging-file deletion in the `finally` of `RunOneCycleAsync` (well — `File.Delete` is sync and has no token; the principle applies to async compensation).
  - The final mirror call inside `DisposeAsync`.
  - Each `WaitAsync(TimeSpan, CancellationToken.None)` in `AwaitWithBudget`.
- **Principle 18** — `BrainMirrorHeader.Write` uses `Span<byte>` (no allocation). The mirror payload is one `byte[]` allocated once per cycle (one large allocation per mirror, not per range). The 12-byte nonce is `stackalloc`. The 16-byte AAD header is written in place into the payload buffer.
- **Principle 19** — N/A (no `IMemoryOwner<byte>` produced by this type). The single per-cycle `byte[]` is fully owned by `RunOneCycleAsync`'s local scope.
- **Principle 20** — the 12-byte `stackalloc` nonce is consumed by `aes.Encrypt` *before* any `await`. It's also copied into the payload before the encryption call (the copy is synchronous; no `await` between alloc and use).
- **Principle 21** — no `new MemoryStream()` anywhere. Snapshot writes to disk; the in-memory mirror is a flat `byte[]`.
- **Principle 23** — adds no new abstraction. Reuses `IStorageProvider`, `IProviderRegistry`, `IClock`, `INotificationBus` — all frozen by Principle 23 from §3.1–§3.2.
- **Principle 24** — every failure path that crosses a tail boundary publishes via `INotificationBus.PublishAsync(Severity=Error, ...)`. `Critical`-severity is reserved for the snapshot/cap failure (the whole cycle fails); per-tail failures are `Error`. Existing `PersistenceNotificationHandler` captures both into `BackgroundFailures`.
- **Principle 25** — every user-visible string uses user vocabulary: "catalogue copy", "save", file name display strings. No "brain", "mirror", "tail", "blob", "session", "WAL", "AAD", "GCM" in `Title`/`Message`. Logger messages may use appliance vocabulary (governed by Principle 27).
- **Principle 26** — DEK is held as `ReadOnlyMemory<byte>` borrowed from the volume; never copied; never logged. The plaintext snapshot is zeroed after encryption. No `*Token` / `*Secret` / `*Mnemonic` keys in `ErrorContext.Metadata`.
- **Principle 27** — every `Result.Fail` site logs once. The orchestrating loops log warnings on a *returned* `Result.Fail` only when not already logged at the `Fail` site (the inner methods log; the loop log is a one-liner summary at `Warning`).
- **Principle 28** — `ILogger<BrainMirrorService>` from `Microsoft.Extensions.Logging.Abstractions`.
- **Principle 29** — staging dir is `Directory.CreateDirectory`'d and `AtomicWriteHelper.FsyncDirectory`'d before the snapshot lands. The snapshot file itself is `fsync`'d via `RandomAccess.FlushToDisk` after `BackupDatabase` returns. The staging directory is fsync'd again after the file fsync to flush the dirent. (Order: write data → fsync data → fsync containing directory — same shape as `AtomicBlobWriter`.)
- **Principle 30** — N/A in V1. The mirror has no `WAL` interaction; Phase 5's recovery is the *consumer* side, not Phase 3's concern.
- **Principle 31** — DEK is borrowed via `ReadOnlyMemory<byte>`; never copied into a long-lived buffer. The mirror plaintext (which contains encrypted-OAuth-tokens but not raw keys) is zeroed after encryption as defence-in-depth.
- **Principle 32** — every outbound network call is to a configured tail provider, dispatched via `IStorageProvider`. No telemetry, no update checks.

## Test spec

All test files target `tests/FlashSkink.Tests/Engine/` and `tests/FlashSkink.Tests/Engine/Mirror/` (subfolder for the header tests if cleaner — implementer's call). Naming: `Method_State_ExpectedBehavior`. Temp-dir-per-test pattern from `AtomicBlobWriterTests` (constructor creates a unique temp dir, `Dispose` recursive-deletes). `FakeClock` from §3.2 drives all time-dependent assertions; no `Task.Delay` against the system clock except for the one "wall-clock-bounded escape hatch" pattern used in `FakeClockTests` (a 5 s xUnit-timeout safety net so a stuck test fails loudly).

### `BrainMirrorServiceTests.cs`

**Fixture setup per test:**

- Per-test temp dir for the skink root.
- An open in-memory SQLite connection (`DataSource=:memory:`) seeded with one tiny `Files` row + one tiny `Blobs` row so the snapshot has content to encrypt and the decryption check can verify a non-trivial row count.
    - **Decision: in-memory vs. on-disk brain for tests.** `SqliteConnection.BackupDatabase` works against both; in-memory is faster, on-disk matches production. Tests use **in-memory** unless the test specifically validates fsync semantics. Documented inline in the test fixture.
- A `RandomNumberGenerator.GetBytes(32)` for the DEK (test-local, never reused across tests).
- An `InMemoryProviderRegistry` with one or more `FileSystemProvider` instances, each rooted at its own per-test subdirectory.
- A `FakeClock` started at `2025-01-01T00:00:00Z`.
- A `RecordingNotificationBus` from `_TestSupport/` (re-used; this fixture already exists from Phase 2 — implementer verifies; if not, a minimal `INotificationBus` capture stub is added inline to the test class as a private helper since "test data lives inline" — see "Open questions, item 3").

**Trigger tests:**

- `Start_Twice_IsIdempotent`
- `Start_AfterDispose_ReturnsVolumeAlreadyOpen` *(reuse of code; see "Open questions item 1")*
- `NotifyWriteCommitted_BeforeStart_IsNoOp` — does not pulse, does not run a mirror after a clock advance.
- `NotifyWriteCommitted_OnceThenAdvanceTen_RunsOneMirror` — `Start`, register one tail, `NotifyWriteCommitted()`, `clock.Advance(11 s)`; assert exactly one `_brain/*.bin` exists on the tail; assert exactly one notification (or zero — see "Open questions item 3").
- `NotifyWriteCommitted_BurstThenAdvanceTen_RunsOneMirror` — pulse 5 times in quick succession, `clock.Advance(11 s)`; exactly one mirror file appears.
- `NotifyWriteCommitted_BurstSlidingWindow_FiresAfterWindowQuiet` — pulse at t=0, advance 9 s, pulse again, advance 9 s, pulse again, advance 11 s; the window restarts each pulse; exactly one mirror appears at the end.
- `Timer_AdvancePast15Minutes_RunsOneMirror` — `Start`, no commits, `clock.Advance(16 min)`; one mirror on the tail.
- `Timer_AdvancePast30Minutes_RunsTwoMirrors` — `clock.Advance(31 min)`; two mirror files (the second supersedes the first in retention, but with `BrainMirrorRollingCount=3` both remain).
- `Dispose_RunsFinalMirror_BeforeStopping` — `Start`, no commits, `DisposeAsync`; exactly one mirror appears (the final one).

**Encryption / round-trip tests:**

- `Mirror_RoundTripsThroughDek_DecryptsToCorrectBrainContent` — produce one mirror, locate it on the tail, read its bytes, parse the `BrainMirrorHeader` to extract the timestamp, AES-GCM-decrypt with the DEK + header-as-AAD, write the plaintext to a temp file, open it as a SQLite connection, query the seeded `Files` row, assert the row is present and equal.
- `Mirror_AAD_TimestampMismatch_FailsDecryption` — produce a mirror, copy its bytes, *overwrite* the timestamp bytes in the header (simulating rollback attack), attempt decryption; the AES-GCM tag check fails. This validates Principle-6 defence-in-depth.
- `Mirror_NoncesUniqueAcrossCycles` — produce N mirrors (advance clock 16 min × N), inspect the 12-byte nonce slice of each, assert all distinct. `N=5`. Catches catastrophic RNG misuse.
- `Mirror_Header_HasCorrectMagicAndVersion` — produce a mirror, inspect the first 4 bytes (`F S B M`), bytes 4-5 (version = 1 LE), bytes 6-7 (reserved = 0).

**Retention tests:**

- `Retention_AfterFiveCycles_OnlyThreeMostRecentRetained` — 5 timer-driven mirrors (advance 16 min × 5), assert `provider.ListAsync("_brain")` returns exactly 3 entries and their timestamps are the 3 most recent.
- `Retention_ProviderListFailure_DoesNotFailCycle` — wrap `FileSystemProvider` with `FaultInjectingStorageProvider`; fail `ListAsync` once; assert the mirror still uploads; assert a warning log.
- `Retention_ProviderDeleteFailure_DoesNotFailCycle` — fail the first prune `DeleteAsync`; assert the mirror uploads, the prune logs a warning at `Warning`, and the next cycle's prune succeeds (idempotent).

**Per-tail isolation tests:**

- `MultipleTails_OneFails_OthersSucceed` — register tails A (clean) and B (`FaultInjectingStorageProvider` failing `UploadRangeAsync`); trigger a mirror; assert A has exactly one `_brain/*.bin` and B has none; assert one `Error`-severity notification mentioning B's display name; A is untouched.
- `MultipleTails_AllFail_NotificationPerTail` — A and B both fault; assert two notifications.
- `MultipleTails_AllSucceed_OneNotificationEach_OrNone` — see "Open questions item 3" — V1 publishes no success notification, so this test asserts zero notifications across both tails.

**Cancellation tests:**

- `Cycle_CancelledMidUpload_AbortsAndReturnsCancelled` — fail-injector blocks on `UploadRangeAsync`; cancel the volume CTS; assert `TriggerMirrorAsync(volumeCtsToken)` returns `Result.Fail(Cancelled)`; assert `AbortUploadAsync` was called.
- `DisposeAsync_AfterStart_RunsFinalMirror_DespiteCancellation` — cancel the volume CTS, then call `DisposeAsync`; the final mirror still runs (it uses `CancellationToken.None`); assert one mirror lands on the tail.

**Size-cap tests:**

- `Cycle_SnapshotExceedsCap_FailsWithNotification` — seed the brain DB with a `BLOB` column carrying more than 1 GiB of dummy data; trigger a mirror; assert `Result.Fail(Unknown)` with a "too large" message and a notification. *(May be slow; mark `[Trait("Category","Slow")]` and gate the seed size at 100 MB in CI with a smaller-than-real cap injected via a `BrainMirrorService` test-only constructor overload — see "Open questions item 5".)*

**Staging-cleanup tests:**

- `Cycle_CompletesSuccessfully_StagingFileDeleted` — `.flashskink/staging/brain-mirror-*.db` does not exist after the cycle.
- `Cycle_FailsDuringUpload_StagingFileStillDeleted` — fail-injector causes the upload to fail; staging file is still cleaned in the `finally` block.
- `Cycle_FailsDuringSnapshot_NoStagingFileLeft` — close the source connection before triggering; `BackupDatabase` throws; assert no orphaned staging file.

### `BrainMirrorHeaderTests.cs`

- `Write_ProducesExpectedBytes` — write a known timestamp (`new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)`), assert the 16-byte buffer equals `[0x46, 0x53, 0x42, 0x4D, 0x01, 0x00, 0x00, 0x00, ...8 bytes of ToBinary()...]`.
- `TryParse_KnownGoodBytes_ReturnsTrueAndValues` — round-trip.
- `TryParse_WrongMagic_ReturnsFalse` — flip first byte.
- `TryParse_WrongVersion_ReturnsFalse` *(or `out version != 1`; see "Open questions item 6")* — bump version byte to 2.
- `TryParse_Truncated_ReturnsFalse` — pass 15 bytes.
- `Write_RequiresExactSize_ThrowsOnTooSmall` — pass `Span<byte>` of length 15; the implementation should throw `ArgumentException` (this is a precondition violation inside an internal helper — sanctioned exception per Principle 1's exception for argument validation in private helpers).

### Existing tests

No existing tests modified. The full Phase 0–2 + §3.1–§3.4 suite must remain green.

## Acceptance criteria

- [ ] All listed files exist, build clean on `ubuntu-latest` and `windows-latest` with `--warnaserror`.
- [ ] `dotnet test` green: all Phase 0–2 + §3.1–§3.4 tests still pass; all §3.5 tests pass.
- [ ] `dotnet format --verify-no-changes` reports clean.
- [ ] `BrainMirrorService` is `public sealed`, `IAsyncDisposable`, lives in `FlashSkink.Core.Engine`, and has the public surface listed above.
- [ ] `BrainMirrorHeader` is `internal static` and lives in `FlashSkink.Core.Engine`.
- [ ] `ErrorCode.cs` adds exactly one new value, `ObjectDisposed`, in the "Control flow" section after `Timeout`. No other enum values are added, renamed, or reordered. **This is a deliberate override of cross-cutting decision 2** (see Open Question 1 resolution).
- [ ] `UploadQueueService.Start`'s disposed-state `Result.Fail` migrates from `VolumeAlreadyOpen` to `ObjectDisposed`. Any §3.4 test asserting on that code is updated.
- [ ] `IStorageProvider`, `IProviderRegistry`, `UploadSession`, `INotificationBus`, `Notification`, `UploadConstants`, `IClock`, `UploadWakeupSignal` are unchanged.
- [ ] `Core.Abstractions` references no UI/Presentation/Core projects (assembly-layering test in CI passes).
- [ ] The round-trip integration test `Mirror_RoundTripsThroughDek_DecryptsToCorrectBrainContent` passes.
- [ ] The retention integration test `Retention_AfterFiveCycles_OnlyThreeMostRecentRetained` passes.
- [ ] Per-tail isolation test `MultipleTails_OneFails_OthersSucceed` passes.
- [ ] All Principle-17 `CancellationToken.None` literals are spelled out (not aliased) at every site listed in the principle audit.
- [ ] Notification text contains no appliance vocabulary (`brain`, `mirror`, `tail`, `blob`, `session`, `WAL`, `AAD`, `GCM`).

## Line-of-code budget

| File | Approx LOC |
|---|---|
| `src/FlashSkink.Core/Engine/BrainMirrorService.cs` | 520 |
| `src/FlashSkink.Core/Engine/BrainMirrorHeader.cs` | 70 |
| **src subtotal** | **~590** |
| `tests/FlashSkink.Tests/Engine/BrainMirrorServiceTests.cs` | 700 |
| `tests/FlashSkink.Tests/Engine/BrainMirrorHeaderTests.cs` | 90 |
| **tests subtotal** | **~790** |
| **Total** | **~1,380** |

Comparable to §3.4's ~1,400 LOC and §3.3's similar footprint. The test surface is large because the trigger matrix (commit-debounce, timer, dispose) crossed with the failure matrix (snapshot, encrypt, upload, prune) crossed with multi-tail isolation produces a wide grid. None of the rows are skippable: each is a specified acceptance criterion in the dev plan.

## Non-goals

- **No streaming brain mirror.** §3.5's hard cap is 1 GiB in-memory; over-cap fails with a notification. Streaming variant is a Phase 3 stretch goal, explicitly deferred unless profiling reveals real-world brains over the cap. Documented in dev plan §3.5 Key constraints.
- **No retry ladder.** The mirror does not consult `RetryPolicy`. A failed cycle simply waits for the next trigger (debounce or timer). Adding a retry ladder is a V2+ consideration if real-world failure patterns motivate it.
- **No integration with `UploadQueueService`.** The mirror does not enqueue `TailUploads` rows. It calls `IStorageProvider` directly. Future V2 routing through the queue (for shared backoff) is recorded as a possibility in dev plan §3.6 Key constraints.
- **No `INetworkAvailabilityMonitor` gating.** V1's `AlwaysOnlineNetworkMonitor` makes the gate trivially true. Phase 5's real monitor will integrate via the same registry refresh / upload-call-fails-naturally path — a per-call provider failure on an offline host returns `ProviderUnreachable` and the cycle moves on. (Plan-level decision; see "Open questions item 4".)
- **No `IProviderSetup` / OAuth integration.** Phase 4. The mirror does not care which provider it talks to.
- **No `BrainBackedProviderRegistry`.** Phase 4.
- **No `RecoverAsync` / consumer of the mirror.** Phase 5. This PR only *writes* the mirror; the read path is Phase 5.
- **No CLI command for "mirror now".** Phase 6.
- **No volume integration.** §3.6 wires the lifecycle (`Start`, `NotifyWriteCommitted`, `DisposeAsync`) into `FlashSkinkVolume`.
- **No new `ErrorCode` values *beyond* `ObjectDisposed`.** This PR adds exactly one new value (overriding cross-cutting decision 2 with explicit user approval; see Open Question 1 resolution). No other codes are added.
- **No modifications to `WritePipeline`.** §3.6 adds the post-commit hook.

## Open questions for Gate 1

These six items need a steer before I lock the plan or proceed to implementation.

### 1. ErrorCode reuse for "disposed/already-started" and "snapshot too large" — RESOLVED

**Decision (Gate 1):**

- **Disposed-service-on-Start.** Add a new `ErrorCode.ObjectDisposed` to `ErrorCode.cs`. This is a deliberate override of cross-cutting decision 2 ("no new codes in Phase 3"). Rationale: `Unknown` is reserved for unanticipated failures per its XML doc; `VolumeAlreadyOpen` (the §3.4 precedent) is a stretch — the enum value's stated meaning is "second concurrent volume open attempted", not "service disposed". `ObjectDisposed` is the precise concept (matches BCL `ObjectDisposedException` semantics). The cost of overriding decision 2 is bounded: one enum value, one XML doc line, two call sites (this PR's `BrainMirrorService.Start` + the §3.4 migration). No CLI / docs cascade in Phase 3 because no user-visible code path surfaces this error — it's a programming-error path for the volume lifecycle.

- **Over-cap snapshot.** Reuse the existing `ErrorCode.FileTooLong` ("The file exceeds the maximum supported size."). The brain mirror snapshot is, semantically, a file (the `.db` on the staging path) that exceeds the supported size. The XML doc's wording is general and matches the situation precisely. The size is surfaced via `ErrorContext.Metadata["SnapshotSizeBytes"]` and `["CapBytes"]` for handlers; the user-visible notification text is "Brain catalogue copy is too large for this version".

**Side effect:** the §3.4 `UploadQueueService.Start` disposed-state branch is migrated from `VolumeAlreadyOpen` to `ObjectDisposed` in this PR (one-token edit + test assertion update). Leaving the §3.4 site on `VolumeAlreadyOpen` once a precise code exists would be a known wart.

### 2. `UploadWakeupSignal.Complete` accessibility

§3.4's plan documents `Complete()` as `internal`. `BrainMirrorService` is in `Core/Engine/` — same assembly as `UploadWakeupSignal` in `Core/Upload/` — so `internal` is reachable. But this creates a second consumer of the `internal` surface, which slightly weakens the §3.4 plan's "only `UploadQueueService` calls `Complete`" statement.

**Options:**

- **A. Keep `Complete` `internal`; document the second consumer.** Plan default. Adds a one-line XML comment to `UploadWakeupSignal.Complete` noting both call sites.
- **B. Promote `Complete` to `public`.** Mirrors `Channel<T>.Writer.Complete()` shape. Risks accidental external completion of a long-lived signal.
- **C. Give `BrainMirrorService` its own `UploadWakeupSignal` instance and never call `Complete` on it — let the GC reclaim it.** Channels don't need explicit completion to be GC'd; the cost is a single suppressed warning if a worker is awaiting a non-completed channel during dispose, but `_serviceCts.Cancel()` already short-circuits that.

**Recommendation:** A. The dev plan §3.5 doesn't forbid multiple call sites; the §3.4 plan's "only `UploadQueueService` calls `Complete`" was about *its* signal, not the abstraction.

### 3. Success-side notifications

`PublishMirrorFailureAsync` publishes on failure. Should there be a success-side notification ("Catalogue copy saved to '{TailDisplayName}'") at `Information` severity? Pros: visibility — users see mirrors landing. Cons: notification volume — 4 tails × every commit × debounced bursts could surface 100+ notifications/day, with `PersistenceNotificationHandler` capturing none of them (Information not persisted) but the dispatcher publishing each.

**Recommendation:** **no success notifications in V1.** Activity log (Phase 5 integration if we want it) is the right surface for routine successes. This matches §3.4's "success is routine, Principle 24 covers failures only" stance. The acceptance test `MultipleTails_AllSucceed_OneNotificationEach_OrNone` therefore asserts zero notifications.

### 4. Should `BrainMirrorService` consult `INetworkAvailabilityMonitor`?

Dev plan §3.5 does not require it. The mirror's upload calls naturally fail with `ProviderUnreachable` on an offline host; the cycle moves on. Adding the gate would mean an extra constructor parameter and a check at the top of `RunOneCycleAsync`.

**Recommendation:** **do not consume the monitor in V1.** The fail-natural-and-retry-next-cycle behaviour is already correct, and the dev plan calls the monitor out for `UploadQueueService` only. Phase 5 may add it if profiling shows useful early-bailout savings.

### 5. Size-cap testing — real 1 GiB vs. injected smaller cap

Seeding a 1 GiB brain DB for the over-cap test would blow CI runtime. Two options:

- **A. Mark the test `[Trait("Category", "Slow")]` and only run in nightly.** Plan default.
- **B. Add a test-only internal constructor overload `internal BrainMirrorService(..., long maxInMemoryMirrorBytes)`.** Tests pass a 1 MiB cap and seed a 2 MiB brain. The production code path is unchanged at runtime; the test exercises the cap branch deterministically.

**Recommendation:** B. The principle-1 sanctioned-pure-function for "argument validation" is not relevant; this is just dependency injection of a constant for test ergonomics. Documented inline with an XML comment.

### 6. `TryParse` semantics for wrong version

`TryParse(ReadOnlySpan<byte> src, out ushort version, out DateTime utcTimestamp) → bool`. Two interpretations:

- **A. Wrong magic returns false; wrong version returns true with the parsed version (caller decides if it can handle).** Mirrors `BlobHeader.Parse` shape.
- **B. Wrong magic and wrong version both return false.** Simpler caller contract; harder to surface "your brain mirror is from a newer FlashSkink, please upgrade".

**Recommendation:** A. Phase 5's recovery path will check `version != BrainMirrorHeader.Version` and surface a user-facing "upgrade required" message; the parser itself is non-judgemental.

---

*Plan ready for review. Stop at Gate 1 per `CLAUDE.md` step 3.*
