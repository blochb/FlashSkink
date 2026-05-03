# PR 2.4 — Atomic blob writer and WAL operation scope

**Branch:** pr/2.4-atomic-blob-writer-and-wal-scope
**Blueprint sections:** §13.4, §13.5, §16.6, §21.2, §21.3
**Dev plan section:** phase-2 §2.4

## Scope

Delivers two types in `src/FlashSkink.Core/Storage/` (a folder created by this PR) that together implement the durable, crash-consistent file-write protocol from blueprint §13.4 and the WAL state-machine scope from §21.2:

1. **`AtomicBlobWriter`** — owns the on-disk write protocol: stage → fsync file → atomic rename → fsync destination directory. Cross-platform; Windows uses NTFS metadata-journal as the directory-fsync equivalent (rationale below). Computes the sharded destination path from a BlobID. Provides compensation entry-points (`DeleteStagingAsync`, `DeleteDestinationAsync`) used by `WriteWalScope` on failure.

2. **`WriteWalScope`** — wraps a single user-level write attempt in a WAL row whose phase machine is PREPARE → COMMITTED on success, or PREPARE → FAILED on failure. Implements `IAsyncDisposable`; `DisposeAsync` is the only place WAL `FAILED` transitions are issued and the only place orphaned staging / destination files are cleaned. Uses `CancellationToken.None` literal at every internal compensation site (Principle 17).

This PR also seeds the property-based crash-consistency test folder at `tests/FlashSkink.Tests/CrashConsistency/` with the first test class — `WriteCrashConsistencyTests` — that uses FsCheck to iterate 200 "crash at step N" interleavings against the §21.3 invariant for `WRITE` operations (FsCheck default of 100 is doubled because crash-interleaving bug surface is combinatorial; nightly extends to 5000 in §2.4's acceptance criteria entry).

The PR adds **no NuGet packages** and **no `ErrorCode` values** (cross-cutting decision 4 — `BlobCorrupt`, `FileTooLong`, `StagingFailed`, `UsbFull`, `Cancelled`, `Unknown`, `DatabaseWriteFailed` are all already in the §1.1 enum). It does **not** wire either type into `WritePipeline` — that lands in §2.5.

## Files to create

### Production — `src/FlashSkink.Core/Storage/`
- `AtomicBlobWriter.cs` — sealed; the §13.4 write protocol, sharded path computation, compensation entry-points, cross-platform directory-fsync helper. ~280 lines.
- `WriteWalScope.cs` — sealed `IAsyncDisposable`; WAL state-machine wrapper around one user-level write attempt. ~210 lines.

### Tests — `tests/FlashSkink.Tests/Storage/`
- `AtomicBlobWriterTests.cs` — exercises the full §13.4 protocol against a real on-disk staging/destination tree under an `xUnit` per-test temp directory. ~280 lines.
- `WriteWalScopeTests.cs` — verifies PREPARE → COMMITTED / PREPARE → FAILED transitions, `DisposeAsync` idempotency, staging cleanup, destination cleanup, and the `CancellationToken.None` literal usage on compensation paths (the latter via a behavioural witness — see test spec). Uses `BrainTestHelper.CreateInMemoryConnection` + `BrainTestHelper.ApplySchemaAsync` for the WAL backing store. ~290 lines.

### Tests — `tests/FlashSkink.Tests/CrashConsistency/`
- `WriteCrashConsistencyTests.cs` — first FsCheck-driven property test class. Models a write as a 4-step sequence (stage write → file fsync → rename → directory fsync → brain commit) and exercises crash-at-step-N for N ∈ [1..5], asserting the §21.3 invariant after each. 200 iterations per property (overrides FsCheck default of 100). ~250 lines.

## Files to modify

None. No `.csproj` edits required (implicit globbing picks up new subfolders), no shared helper changes, no documentation edits this PR.

## Dependencies

- NuGet: none new. (`FsCheck.Xunit` 2.16.6 was already pinned in `Directory.Packages.props` and referenced by `tests/FlashSkink.Tests/FlashSkink.Tests.csproj` — see §0 Phase 0 bootstrap. This PR is the first user.)
- Project references: none new.

## Public API surface

### `FlashSkink.Core.Storage.AtomicBlobWriter` (public sealed class)

Summary intent: implements blueprint §13.4 file-level atomic write to the skink, including staging, file fsync, atomic rename, and destination-directory fsync. Compensation entry-points (`DeleteStagingAsync`, `DeleteDestinationAsync`) are used by `WriteWalScope` on rollback.

Constructor: `AtomicBlobWriter(ILogger<AtomicBlobWriter> logger)`

- `Task<Result<string>> WriteAsync(string skinkRoot, string blobId, ReadOnlyMemory<byte> blobBytes, CancellationToken ct)`
  Computes `dest = [skinkRoot]/.flashskink/blobs/{blobId[0..2]}/{blobId[2..4]}/{blobId}.bin` and `staging = [skinkRoot]/.flashskink/staging/{blobId}.tmp`.
  Behaviour:
  1. Create the two-level shard directory `[skinkRoot]/.flashskink/blobs/{xx}/{yy}/` via `Directory.CreateDirectory` (idempotent). If the leaf directory was newly created, fsync the leaf directory's *parent* once (see Method-body contracts).
  2. Open the staging path for write (create-or-truncate), write `blobBytes`, fsync the file via `RandomAccess.FlushToDisk` on the underlying `SafeFileHandle`.
  3. `File.Move(staging, dest, overwrite: false)`. UNIQUE-destination collisions return `ErrorCode.PathConflict` (the BlobID-collision / Phase-5-race case noted in the dev-plan §2.4 key-constraints).
  4. Fsync the destination directory via the cross-platform `FsyncDirectory` helper.
  5. Return `Result<string>.Ok(dest)`.

  Failure mapping:
  - `OperationCanceledException` → `ErrorCode.Cancelled`. Caller's `WriteWalScope.DisposeAsync` will clean any partial staging file via `DeleteStagingAsync(... CancellationToken.None)`.
  - `IOException` whose `HResult` indicates disk-full → `ErrorCode.UsbFull` (`HResult == unchecked((int)0x80070070)` for `ERROR_DISK_FULL` on Windows; `HResult` matching `ENOSPC == 28` mapped via `(HResult & 0xFFFF) == 28` for Unix). Helper `IsDiskFull(IOException)` lives next to `WriteAsync` as a `private static`.
  - `IOException` with destination-already-exists semantics (`File.Move(... overwrite: false)` throws `IOException` when destination exists; check `HResult == unchecked((int)0x80070050)` `ERROR_FILE_EXISTS` on Windows, `(HResult & 0xFFFF) == 17` `EEXIST` on Unix) → `ErrorCode.PathConflict`.
  - Other `IOException` → `ErrorCode.StagingFailed`.
  - `UnauthorizedAccessException` → `ErrorCode.StagingFailed`.
  - `Exception` (last) → `ErrorCode.Unknown`.

  Cleanup obligation: on any failure path, the method makes a best-effort `try { File.Delete(staging) } catch { }` to avoid leaving orphans when `DisposeAsync` is not the rollback site (e.g. when `WriteAsync` fails *before* the WAL row was created — caller hasn't constructed a `WriteWalScope` yet). When the call site *did* construct a scope (the §2.5 happy path), the scope's `DisposeAsync` will also call `DeleteStagingAsync` — both calls are idempotent.

- `Task<Result> DeleteStagingAsync(string skinkRoot, string blobId, CancellationToken ct)`
  Compensation entry-point invoked by `WriteWalScope.DisposeAsync`. Computes the staging path and best-effort deletes it. Missing file is success (idempotent). Returns `Result.Ok()` on success, `Result.Fail(ErrorCode.StagingFailed, ...)` on `IOException` other than file-not-found. Catches `Exception` last → `ErrorCode.Unknown`.

- `Task<Result> DeleteDestinationAsync(string skinkRoot, string blobId, CancellationToken ct)`
  Compensation entry-point invoked by `WriteWalScope.DisposeAsync` when the WAL payload indicates rename completed but the brain transaction failed. Computes the sharded destination path and best-effort deletes it. Missing file is success (idempotent). Same error mapping as `DeleteStagingAsync`.

- `static string ComputeDestinationPath(string skinkRoot, string blobId)`
  Pure helper; returns `Path.Combine(skinkRoot, ".flashskink", "blobs", blobId[..2], blobId[2..4], blobId + ".bin")`. Public-static so `WriteWalScope` and §2.5 read the same path-derivation rule. Never throws (Principle 1 pure-function carve-out — XML doc states "Never throws"). `blobId.Length < 4` is enforced by the caller (§2.5 generates `Guid.NewGuid().ToString("N")`, 32 chars); a `Debug.Assert` guards the precondition.

- `static string ComputeStagingPath(string skinkRoot, string blobId)`
  Same shape as `ComputeDestinationPath`; returns `Path.Combine(skinkRoot, ".flashskink", "staging", blobId + ".tmp")`. Pure; never throws.

### `FlashSkink.Core.Storage.WriteWalScope` (public sealed class : IAsyncDisposable)

Summary intent: wraps one user-level write attempt in a WAL row whose phase transitions PREPARE → COMMITTED on success or PREPARE → FAILED on dispose-without-complete. Centralises the §21.3 invariant-restoring rollback logic; §2.5's `WritePipeline` constructs one of these per call.

Construction is via the factory `OpenAsync` only. The constructor itself is `private`.

- `static Task<Result<WriteWalScope>> OpenAsync(WalRepository wal, AtomicBlobWriter blobWriter, string skinkRoot, string fileId, string blobId, string virtualPath, ILogger<WriteWalScope> logger, CancellationToken ct)`
  1. Build the JSON payload `{"FileID":"...", "BlobID":"...", "VirtualPath":"...", "SkinkRoot":"..."}` via `System.Text.Json.JsonSerializer.Serialize` of an internal `WriteWalPayload` record.
  2. Build a `WalRow` with `WalId = Guid.NewGuid().ToString("N")`, `Operation = "WRITE"`, `Phase = "PREPARE"`, `StartedUtc = UpdatedUtc = DateTime.UtcNow`, `Payload = json`.
  3. Call `wal.InsertAsync(walRow, transaction: null, ct)`. On failure, propagate the `ErrorContext` as `Result<WriteWalScope>.Fail(...)` — no scope is constructed, so there is nothing to dispose.
  4. On success, construct and return a fresh `WriteWalScope` carrying `(wal, blobWriter, skinkRoot, fileId, blobId, virtualPath, walRow.WalId, logger)`.

- `void MarkRenamed()`
  Called by §2.5 after `AtomicBlobWriter.WriteAsync` returns success and *before* the brain transaction begins. Sets an internal `_renameCompleted = true` flag so `DisposeAsync` knows to also delete the destination file (not just staging) on rollback. Synchronous: no I/O, no allocations.

- `Task<Result> CompleteAsync(CancellationToken ct = default)`
  Transitions the WAL row PREPARE → COMMITTED. Behaviour:
  1. Guard: if `_completed` is already `true`, return `Result.Ok()` (idempotent — calling twice is not an error; useful for §2.5 ergonomics).
  2. Call `_wal.TransitionAsync(_walId, "COMMITTED", CancellationToken.None)` (Principle 17 — once the brain transaction has committed, the WAL row's COMMITTED transition must not be cancellable mid-flight; the `ct` parameter exists for symmetry and future use but is **not** forwarded).
  3. On success, set `_completed = true`, return `Result.Ok()`.
  4. On failure, log at `Error` and return the failed `Result` — the WAL row is now in a state where Phase 5 recovery will see PREPARE and re-evaluate by inspecting on-disk vs brain state.
  5. The `ct` parameter is intentionally accepted for API symmetry across the volume's await call sites (Principle 13: every async public method accepts `ct`); `[SuppressMessage("Usage", "CA1801")]` is **not** applied because the parameter is documented as observed-but-not-forwarded; XML doc states this explicitly.

- `ValueTask DisposeAsync()`
  Idempotent. Behaviour:
  1. Guard: if `_disposed` is already `true`, return.
  2. Set `_disposed = true`.
  3. If `_completed` is `true`, return — the WAL row is already in COMMITTED, and no on-disk cleanup is required.
  4. Otherwise (the failure path):
     a. Best-effort delete staging: `await _blobWriter.DeleteStagingAsync(_skinkRoot, _blobId, CancellationToken.None)`.
     b. If `_renameCompleted` is `true`: also delete destination: `await _blobWriter.DeleteDestinationAsync(_skinkRoot, _blobId, CancellationToken.None)`.
     c. Transition WAL: `await _wal.TransitionAsync(_walId, "FAILED", CancellationToken.None)`.
     d. If the WAL transition itself returns a failed `Result`: log at `Error` (`_logger`) and swallow — the row will be picked up by Phase 5 recovery and resolved idempotently per blueprint §21.2 WRITE recovery case.
     e. Any exception escaping a/b/c is caught at `Exception` and logged at `Error`; never rethrown — `DisposeAsync` must not throw.

### `FlashSkink.Core.Storage.WriteWalPayload` (internal sealed record)

Summary intent: the JSON shape persisted in `WAL.Payload` for `Operation = "WRITE"` rows. Internal because no consumer outside the storage namespace reads or writes it in this PR; Phase 5 recovery (Phase 5) will move it to `Core.Abstractions` if cross-assembly consumption appears.

```
string FileID
string BlobID
string VirtualPath
string SkinkRoot
```

Serialised via `System.Text.Json.JsonSerializerOptions` set to `PropertyNamingPolicy = null` (preserve PascalCase so the on-disk JSON matches the field names verbatim, easing manual recovery diagnostics).

## Internal types

### `AtomicBlobWriter.IsDiskFull(IOException ex)` (private static)
Single check. On Windows, `(uint)ex.HResult == 0x80070070` (HRESULT for `ERROR_DISK_FULL`). On Unix, `(ex.HResult & 0xFFFF) == 28` (`ENOSPC`). Returns `true` if either matches; `false` otherwise. Centralises the platform-conditional in one helper so the catch-block stays linear.

### `AtomicBlobWriter.IsFileExists(IOException ex)` (private static)
Single check. On Windows, `(uint)ex.HResult == 0x80070050` (`ERROR_FILE_EXISTS`) or `0x800700B7` (`ERROR_ALREADY_EXISTS`). On Unix, `(ex.HResult & 0xFFFF) == 17` (`EEXIST`).

### `AtomicBlobWriter.FsyncDirectory(string directoryPath)` (private static)
Cross-platform directory-flush. On Linux/macOS: opens the directory file descriptor via `File.Open(directoryPath, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Options = FileOptions.None })`, then calls `RandomAccess.FlushToDisk(handle)`. On Windows: this PR does **not** open a handle (NTFS metadata-journaling makes the rename durable in practice once `File.Move` returns); the helper is a no-op on `OperatingSystem.IsWindows()`. The crash-consistency test class includes a `[Fact(Skip = "manual VM-snapshot test — see XML")]` placeholder method documenting the future power-off proxy that would force a fall-back to `FILE_FLAG_BACKUP_SEMANTICS` if NTFS proves insufficient. The skip is intentional and called out in `WriteCrashConsistencyTests.cs` XML.

### `WriteWalScope` private fields
- `WalRepository _wal`
- `AtomicBlobWriter _blobWriter`
- `string _skinkRoot`
- `string _fileId`
- `string _blobId`
- `string _virtualPath`
- `string _walId`
- `ILogger<WriteWalScope> _logger`
- `bool _renameCompleted` — set by `MarkRenamed()`
- `bool _completed` — set by `CompleteAsync()`
- `bool _disposed` — set by `DisposeAsync()`

All `bool` fields are written-then-read on the single thread that owns the scope (volume-serialization gate from cross-cutting decision 1 makes this safe in §2.5; tests construct scopes in single-threaded test bodies).

## Method-body contracts

### `AtomicBlobWriter.WriteAsync` — directory-creation and parent-fsync rule

```
1. var dest = ComputeDestinationPath(skinkRoot, blobId);
   var staging = ComputeStagingPath(skinkRoot, blobId);
   var leafDir = Path.GetDirectoryName(dest)!;
   var midDir  = Path.GetDirectoryName(leafDir)!;   // [skinkRoot]/.flashskink/blobs/{xx}
   var rootDir = Path.GetDirectoryName(midDir)!;    // [skinkRoot]/.flashskink/blobs

2. var leafExisted = Directory.Exists(leafDir);
   Directory.CreateDirectory(leafDir);              // idempotent; creates intermediates

3. if (!leafExisted) { FsyncDirectory(midDir); }    // parent fsync once on first
                                                     // creation per cross-cutting note in
                                                     // dev-plan §2.4 (Linux/macOS only)

4. ct.ThrowIfCancellationRequested();

5. // Write + fsync staging:
   await using (var fs = new FileStream(staging,
       new FileStreamOptions
       {
           Mode = FileMode.Create,
           Access = FileAccess.Write,
           Share = FileShare.None,
           Options = FileOptions.None,
           PreallocationSize = blobBytes.Length,
       }))
   {
       await fs.WriteAsync(blobBytes, ct).ConfigureAwait(false);
       await fs.FlushAsync(ct).ConfigureAwait(false);
       RandomAccess.FlushToDisk(fs.SafeFileHandle);
   }

6. ct.ThrowIfCancellationRequested();

7. File.Move(staging, dest, overwrite: false);

8. FsyncDirectory(leafDir);

9. return Result<string>.Ok(dest);
```

Note: `Directory.CreateDirectory` is idempotent and returns the existing directory if present — no race condition risk between concurrent volumes (V1 single-volume model rules that out anyway). The `leafExisted` check is best-effort; the worst case if a parallel process created the directory between the `Exists` and `CreateDirectory` is one redundant parent fsync.

### `AtomicBlobWriter.WriteAsync` — full catch ordering

```
catch (OperationCanceledException ex)
{
    TryDeleteStaging(staging);   // best-effort, swallow IOException
    return Result<string>.Fail(ErrorCode.Cancelled, "Atomic blob write cancelled.", ex);
}
catch (IOException ex) when (IsDiskFull(ex))
{
    TryDeleteStaging(staging);
    _logger.LogError(ex, "Skink full while writing blob {BlobId}", blobId);
    return Result<string>.Fail(ErrorCode.UsbFull,
        "The skink is full; cannot write the file.", ex);
}
catch (IOException ex) when (IsFileExists(ex))
{
    // Rename collided — see dev-plan §2.4 key-constraints "BlobID lifetime and retry semantics".
    TryDeleteStaging(staging);
    _logger.LogError(ex, "Destination file already exists for blob {BlobId}", blobId);
    return Result<string>.Fail(ErrorCode.PathConflict,
        "A file already exists at the target path.", ex);
}
catch (IOException ex)
{
    TryDeleteStaging(staging);
    _logger.LogError(ex, "I/O error writing blob {BlobId} at {Dest}", blobId, dest);
    return Result<string>.Fail(ErrorCode.StagingFailed,
        "I/O error during atomic blob write.", ex);
}
catch (UnauthorizedAccessException ex)
{
    TryDeleteStaging(staging);
    _logger.LogError(ex, "Access denied writing blob {BlobId} at {Dest}", blobId, dest);
    return Result<string>.Fail(ErrorCode.StagingFailed,
        "Access denied during atomic blob write.", ex);
}
catch (Exception ex)
{
    TryDeleteStaging(staging);
    _logger.LogError(ex, "Unexpected error writing blob {BlobId}", blobId);
    return Result<string>.Fail(ErrorCode.Unknown,
        "Unexpected error during atomic blob write.", ex);
}
```

`TryDeleteStaging(string)` is a `private static` helper: `try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }`. Used on the failure path to keep the staging directory clean even when no `WriteWalScope` exists yet (e.g., the WAL insert failed before `WriteAsync` was called — but in that case we never entered `WriteAsync`; the realistic case is a failure inside `WriteAsync` itself between the staging write and the rename).

### `AtomicBlobWriter.DeleteStagingAsync` and `DeleteDestinationAsync` — body shape

Both methods follow this shape:

```
public Task<Result> DeleteStagingAsync(string skinkRoot, string blobId, CancellationToken ct)
{
    try
    {
        ct.ThrowIfCancellationRequested();
        var path = ComputeStagingPath(skinkRoot, blobId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.FromResult(Result.Ok());
    }
    catch (OperationCanceledException ex)
    {
        return Task.FromResult(Result.Fail(ErrorCode.Cancelled,
            "Staging delete cancelled.", ex));
    }
    catch (IOException ex)
    {
        _logger.LogWarning(ex, "Could not delete staging file for blob {BlobId}", blobId);
        return Task.FromResult(Result.Fail(ErrorCode.StagingFailed,
            "Could not delete staging file.", ex));
    }
    catch (UnauthorizedAccessException ex)
    {
        _logger.LogWarning(ex, "Access denied deleting staging file for blob {BlobId}", blobId);
        return Task.FromResult(Result.Fail(ErrorCode.StagingFailed,
            "Access denied deleting staging file.", ex));
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Unexpected error deleting staging file for blob {BlobId}", blobId);
        return Task.FromResult(Result.Fail(ErrorCode.Unknown,
            "Unexpected error deleting staging file.", ex));
    }
}
```

Synchronous I/O wrapped in `Task.FromResult` is acceptable here because `File.Delete` has no async overload and the operation is fast (single inode unlink); the method signature is `Task<Result>` for symmetry with the rest of the class.

### `WriteWalScope.OpenAsync` — failure path

If `WalRepository.InsertAsync` fails (DB locked, disk full, etc.), `OpenAsync` returns the propagated `ErrorContext` and constructs *no* scope. The caller (§2.5) will not have a `WriteWalScope` to dispose, which is correct — there is no on-disk staging file yet (the AtomicBlobWriter call happens *after* `OpenAsync`), and no WAL row to roll back.

### `WriteWalScope.DisposeAsync` — exception isolation

```
public async ValueTask DisposeAsync()
{
    if (_disposed) return;
    _disposed = true;
    if (_completed) return;

    try
    {
        var deleteStaging = await _blobWriter
            .DeleteStagingAsync(_skinkRoot, _blobId, CancellationToken.None)
            .ConfigureAwait(false);
        if (!deleteStaging.Success)
        {
            _logger.LogError(
                "Failed to delete staging file during WAL rollback for blob {BlobId}: {Code} {Message}",
                _blobId, deleteStaging.Error?.Code, deleteStaging.Error?.Message);
        }

        if (_renameCompleted)
        {
            var deleteDest = await _blobWriter
                .DeleteDestinationAsync(_skinkRoot, _blobId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!deleteDest.Success)
            {
                _logger.LogError(
                    "Failed to delete destination file during WAL rollback for blob {BlobId}: {Code} {Message}",
                    _blobId, deleteDest.Error?.Code, deleteDest.Error?.Message);
            }
        }

        var transition = await _wal
            .TransitionAsync(_walId, "FAILED", CancellationToken.None)
            .ConfigureAwait(false);
        if (!transition.Success)
        {
            _logger.LogError(
                "Failed to transition WAL row {WalId} to FAILED: {Code} {Message}. " +
                "Phase 5 recovery will reconcile.",
                _walId, transition.Error?.Code, transition.Error?.Message);
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex,
            "Unexpected error during WriteWalScope rollback for WAL {WalId}, blob {BlobId}",
            _walId, _blobId);
    }
}
```

Three observations:
1. Every internal `await` site uses the `CancellationToken.None` literal — Principle 17 demands the literal, not a stored field.
2. Failed-result branches log but do not throw; `DisposeAsync` swallows.
3. The outer `catch (Exception)` is defence-in-depth in case `_blobWriter` or `_wal` itself throws (they shouldn't — both return `Result` per Principle 1 — but `IDisposable` API throwing is non-recoverable for the caller).

## Integration points

From prior PRs (consumed unchanged):
- `FlashSkink.Core.Abstractions.Results.Result`, `Result<T>`, `ErrorCode` (§1.1) — `Cancelled`, `UsbFull`, `StagingFailed`, `PathConflict`, `Unknown`, `DatabaseWriteFailed`.
- `FlashSkink.Core.Metadata.WalRepository.InsertAsync(WalRow, SqliteTransaction?, CancellationToken)` and `TransitionAsync(string, string, CancellationToken)` (§1.6).
- `FlashSkink.Core.Metadata.WalRow` (§1.6).
- `Microsoft.Extensions.Logging.ILogger<T>` (Abstractions only — Principle 28).

For tests:
- `tests/FlashSkink.Tests/Metadata/BrainTestHelper.cs` — `CreateInMemoryConnection`, `ApplySchemaAsync`. Used by `WriteWalScopeTests` and `WriteCrashConsistencyTests`.
- `tests/FlashSkink.Tests/_TestSupport/RecordingLogger.cs` — captures log entries; used to verify `DisposeAsync` logs at `Error` when WAL transition fails.

No call site outside this PR exists yet — §2.5 (`WritePipeline`) is the first consumer.

## Principles touched

- **Principle 1** (Core never throws across its public API) — every public method on `AtomicBlobWriter` and `WriteWalScope` returns `Result`/`Result<T>` or is a documented pure helper (`ComputeDestinationPath`, `ComputeStagingPath`, `MarkRenamed`).
- **Principle 7** (zero trust in the host) — staging is rooted at `[skinkRoot]/.flashskink/staging/`; no `Path.GetTempPath()` reference anywhere in this PR.
- **Principle 12** (OS-agnostic by default) — `FsyncDirectory` branches on `OperatingSystem.IsWindows()`; both branches are exercised by tests on the matrix.
- **Principle 13** (`CancellationToken ct` last) — every async method.
- **Principle 14** (`OperationCanceledException` first catch) — `WriteAsync`, `DeleteStagingAsync`, `DeleteDestinationAsync`.
- **Principle 15** (no bare `catch (Exception)` as the only catch) — `WriteAsync` distinguishes disk-full vs file-exists vs general I/O before falling through to `Exception`.
- **Principle 16** (dispose on every failure path) — `WriteAsync` uses `await using` for the staging `FileStream`; failure paths invoke `TryDeleteStaging`.
- **Principle 17** (`CancellationToken.None` literal in compensation paths) — every internal await site in `WriteWalScope.DisposeAsync` and `WriteWalScope.CompleteAsync` uses the literal `CancellationToken.None`, not a stored field; verified by reviewer-readable code and by the `Compensation_UsesCancellationTokenNone_Literal` source-grep test (see test spec).
- **Principle 24** (no background failure is silent) — `WriteWalScope.DisposeAsync`'s WAL-transition-failure branch logs at `Error` with the inner `ErrorContext`. (No notification bus publish here — §2.5 publishes the user-visible notification when the surrounding write returns `Fail`.)
- **Principle 27** (Core logs internally; callers log the `Result`) — every `Result.Fail` site logs once via `ILogger<T>`.
- **Principle 28** (Core depends only on `Microsoft.Extensions.Logging.Abstractions`) — only the abstractions assembly is referenced.
- **Principle 29** (atomic file-level writes on the skink) — `AtomicBlobWriter` is the canonical implementation per blueprint §13.4.
- **Principle 30** (crash-consistency invariant preserved across every failure interleaving) — `WriteWalScope` + `WriteCrashConsistencyTests` verify the §21.3 invariant for the WRITE path.

## Test spec

### `tests/FlashSkink.Tests/Storage/AtomicBlobWriterTests.cs`

**Class: `AtomicBlobWriterTests`** (`IDisposable` — creates a per-test temp directory under `Path.GetTempPath()` for the *test fixture only*, never for production code; the temp directory simulates the skink root and is removed on dispose).

Test root convention: `_skinkRoot = Path.Combine(Path.GetTempPath(), "flashskink-tests", Guid.NewGuid().ToString("N"))`. Created in constructor; recursively deleted in `Dispose`.

Logger: `NullLogger<AtomicBlobWriter>.Instance`.

- `WriteAsync_NewBlob_CreatesShardedDestination` — write 1 KiB; assert `[root]/.flashskink/blobs/{xx}/{yy}/{blobId}.bin` exists; assert byte-equality to input; assert no staging file remains.
- `WriteAsync_ReturnsDestinationPath` — assert the returned `Result<string>.Value` equals the expected sharded path.
- `WriteAsync_TwoBlobs_DifferentShards_BothPersist` — write blobs whose first 4 hex chars differ; assert both files exist in different shard subdirectories.
- `WriteAsync_TwoBlobs_SameShard_BothPersist` — write blobs whose first 4 hex chars match; assert both files exist in the same shard subdirectory and the directory is fsync'd only on the first creation (verified by directory-mtime equality after the second write — best-effort; documented as such).
- `WriteAsync_DestinationExists_ReturnsPathConflict` — pre-create the destination file; call `WriteAsync` with the same BlobID; assert `Result.Error.Code == PathConflict`; assert no staging file remains.
- `WriteAsync_CancelledMidStage_ReturnsCancelledAndCleansStaging` — start a write with a token cancelled before `ct.ThrowIfCancellationRequested()`; assert `Cancelled` and that `[staging]/{blobId}.tmp` does not exist.
- `WriteAsync_ZeroByteBlob_Succeeds` — write `ReadOnlyMemory<byte>.Empty`; assert the destination file exists with length 0.
- `WriteAsync_LargeBlob_Succeeds` — write 4 MiB; assert byte-equality (sanity check on the staging-write loop).
- `DeleteStagingAsync_Existing_Removes` — create a staging file directly on disk; call `DeleteStagingAsync`; assert file gone, `Result.Success == true`.
- `DeleteStagingAsync_Missing_ReturnsOk` — call without creating the file; assert `Result.Success == true` (idempotent).
- `DeleteDestinationAsync_Existing_Removes` — write a blob then call `DeleteDestinationAsync`; assert file gone, `Result.Success == true`.
- `DeleteDestinationAsync_Missing_ReturnsOk` — assert idempotent on missing file.
- `ComputeDestinationPath_KnownBlobId_ReturnsExpectedShardedPath` — pure-function unit test on a hardcoded BlobID.
- `ComputeStagingPath_KnownBlobId_ReturnsExpectedStagingPath` — pure-function unit test.

**Disk-full simulation note:** simulating real disk-full inside CI is impractical without a loop-mounted small filesystem. This PR does *not* attempt that test here; the §2.5 acceptance criterion "Skink disk full during staging" calls for fault injection at the `WritePipeline` layer using a wrapper around `AtomicBlobWriter`. Within §2.4's tests we cover the `IsDiskFull` helper via direct unit test:

- `IsDiskFull_WindowsHResult_ReturnsTrue` — construct an `IOException` via `new IOException("disk full", unchecked((int)0x80070070))`; assert `IsDiskFull` returns `true`. Implemented as a `[Theory]` with both Windows and Unix HResults; the helper is a `private static`, accessed via `[InternalsVisibleTo("FlashSkink.Tests")]` *or* via reflection — preferred approach: **reflection** (no `InternalsVisibleTo` is added, in keeping with the §1.6 carry-forward rule that production internals stay internal; the reflection block is one helper method in the test file).

### `tests/FlashSkink.Tests/Storage/WriteWalScopeTests.cs`

**Class: `WriteWalScopeTests`** (`IAsyncLifetime` + `IDisposable`).

Setup: in-memory SQLite via `BrainTestHelper.CreateInMemoryConnection` + `ApplySchemaAsync`. A real `WalRepository` and a real `AtomicBlobWriter` (skinkRoot under per-test temp).

- `OpenAsync_InsertsWalRow_InPreparePhase` — call `OpenAsync`; assert the WAL table now has one row with `Operation = "WRITE"`, `Phase = "PREPARE"`, `Payload` JSON containing `FileID`, `BlobID`, `VirtualPath`, `SkinkRoot`.
- `OpenAsync_WalInsertFails_ReturnsFailNoScope` — pass a closed connection to `WalRepository`; assert `OpenAsync` returns `Result.Fail` with `Code == DatabaseWriteFailed`; assert no scope to dispose. (The closed-connection trick mirrors the pattern used by `PersistenceNotificationHandlerTests`.)
- `CompleteAsync_TransitionsToCommitted` — open, complete; assert WAL row `Phase = "COMMITTED"`.
- `CompleteAsync_CalledTwice_IsIdempotent` — open, complete twice; both return `Result.Success`; WAL row stays at `COMMITTED` and only one transition was issued (verify via `UpdatedUtc` not changing on the second call — within the timestamp resolution of `DateTime.UtcNow`, this is checked by capturing the timestamp after the first call and asserting the second call did not advance it; if flaky, replace with a `wal-call-counter` test double).
- `DisposeAsync_AfterComplete_DoesNothing` — open, complete, dispose; assert WAL row stays at `COMMITTED` (no transition to `FAILED`).
- `DisposeAsync_WithoutComplete_TransitionsToFailedAndCleansStaging` — open; create a staging file directly; dispose without complete; assert WAL row is `FAILED` and staging file is gone.
- `DisposeAsync_WithMarkRenamed_AlsoDeletesDestination` — open; call `MarkRenamed`; create both staging and destination files directly; dispose; assert both files gone and WAL row is `FAILED`.
- `DisposeAsync_WithoutMarkRenamed_LeavesDestinationUntouched` — open; create a destination file directly (simulating an external file at the path — should not be touched); dispose; assert destination file still exists, WAL row is `FAILED`. (Documents the precondition: only `MarkRenamed` enables destination cleanup.)
- `DisposeAsync_IsIdempotent` — dispose twice; second call is a no-op (no WAL transition issued; `_disposed` guards the path).
- `DisposeAsync_WalTransitionFails_LogsAndSwallows` — use a `RecordingLogger<WriteWalScope>`; close the SQLite connection between `OpenAsync` and `DisposeAsync`; dispose; assert no exception escapes; assert the recording logger captured an `Error` log mentioning the WAL ID and "FAILED".
- `Compensation_UsesCancellationTokenNone_Literal` — source-grep test. Reads `src/FlashSkink.Core/Storage/WriteWalScope.cs` from the repo root via `Path.Combine(Helpers.RepoRoot, ...)` and asserts that every line containing `_blobWriter.DeleteStagingAsync` or `_blobWriter.DeleteDestinationAsync` or `_wal.TransitionAsync` also contains `CancellationToken.None`. Mechanises Principle 17. `Helpers.RepoRoot` is a small `static class` in `_TestSupport/` that walks up from `AppContext.BaseDirectory` looking for `FlashSkink.sln`. Implemented in this PR if absent.

### `tests/FlashSkink.Tests/CrashConsistency/WriteCrashConsistencyTests.cs`

**Class: `WriteCrashConsistencyTests`** (`IAsyncLifetime` + `IDisposable`).

Uses FsCheck.Xunit's `[Property]` attribute. The property: for every `crashAtStep ∈ [1..5]` and every random BlobID, after the simulated crash and one cycle of "WAL recovery" (a stub modelled in-test that replays §21.2 WRITE recovery: delete orphan blob, mark WAL FAILED), the §21.3 invariant holds.

A `FaultyAtomicBlobWriter` test double (in the test file) wraps the real writer and throws an `IOException("simulated crash")` after step `crashAtStep` reached. Steps modelled:

| Step | Action | Crash effect |
|---|---|---|
| 1 | Before staging write | No staging file, no WAL `MarkRenamed`, no destination, no brain row |
| 2 | After staging write, before fsync | Staging file exists, no rename, no destination |
| 3 | After staging fsync, before rename | Same as step 2 |
| 4 | After rename, before directory fsync | Destination exists, staging gone, no `MarkRenamed` flagged |
| 5 | After successful WriteAsync, before brain commit | Destination exists, staging gone, scope has `MarkRenamed`, no brain row |

After each crash, the test:
1. Disposes the scope (this is what the §21.2 recovery sweep would also trigger).
2. Asserts the WAL row is now `FAILED`.
3. Asserts no orphan files exist on disk for the BlobID.
4. Asserts no `Files` row exists (vacuously true — no brain-commit step in §2.4 yet; the §2.5 PR extends this property test to seed a `Files` row at the appropriate point).

Property body:

```csharp
[Property(MaxTest = 200)]
public async Task WriteCrash_AtAnyStep_PreservesInvariant(
    PositiveInt crashAtStepBoxed)
{
    var crashAtStep = ((crashAtStepBoxed.Get - 1) % 5) + 1;  // ∈ [1..5]
    // ... arrange ScopeUnderTest, FaultyAtomicBlobWriter ...
    // ... act: try WriteAsync, catch crash, await scope.DisposeAsync ...
    // ... assert: invariant holds
}
```

Iteration cap: `MaxTest = 200`. The `nightly.yml` workflow extends to 5000 via the existing per-test override mechanism (FsCheck respects an environment variable; if absent, default to 200; nightly sets it).

A second `[Fact(Skip = "manual VM-snapshot test — see XML")]` documents the future power-off proxy:

```csharp
[Fact(Skip = "Manual crash-injection test — requires VM snapshot or USB unplug. " +
             "Run before V1 ship to validate the §13.4 step-6 directory-fsync " +
             "assumption on Windows. If the rename does not survive, fall back to " +
             "FILE_FLAG_BACKUP_SEMANTICS in AtomicBlobWriter.FsyncDirectory.")]
public Task WriteAsync_PowerOff_AfterRenameBeforeBrainCommit_RenamesSurvives() =>
    throw new NotImplementedException("See attribute Skip message.");
```

This is **not** a working test; it is a discovery beacon for the future. The `[Fact(Skip = ...)]` keeps it in the test report as a skipped row, which is exactly the visibility we want.

## Acceptance criteria

- [ ] Builds with zero warnings on `ubuntu-latest` and `windows-latest`.
- [ ] All new tests pass; existing tests still pass.
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] `src/FlashSkink.Core/Storage/AtomicBlobWriter.cs` exists with the public API listed above.
- [ ] `src/FlashSkink.Core/Storage/WriteWalScope.cs` exists with the public API listed above.
- [ ] `tests/FlashSkink.Tests/Storage/` exists with both test files.
- [ ] `tests/FlashSkink.Tests/CrashConsistency/` exists with the FsCheck test class and the skipped power-off beacon.
- [ ] No new `ErrorCode` values added.
- [ ] No new NuGet packages added.
- [ ] `WriteWalScope` is `IAsyncDisposable`; `DisposeAsync` is idempotent.
- [ ] `WriteWalScope.DisposeAsync` uses `CancellationToken.None` as a literal at every internal await site (verified by `Compensation_UsesCancellationTokenNone_Literal` source-grep test).
- [ ] `AtomicBlobWriter.WriteAsync` performs the §13.4 sequence: directory create → staging write → file fsync → rename → directory fsync.
- [ ] Disk-full `IOException` maps to `ErrorCode.UsbFull` on both Windows (HRESULT `0x80070070`) and Unix (`ENOSPC == 28`).
- [ ] Destination-exists `IOException` maps to `ErrorCode.PathConflict`.
- [ ] FsCheck property runs at `MaxTest = 200`; the skipped power-off beacon is present in the test report.
- [ ] Every public async method on `AtomicBlobWriter` and `WriteWalScope` takes `CancellationToken ct` last.
- [ ] `OperationCanceledException` is the first catch on every async method.
- [ ] `Path.GetTempPath()` is **not** referenced in `src/FlashSkink.Core/Storage/` (Principle 7) — only in test code.

## Line-of-code budget

### Non-test
- `AtomicBlobWriter.cs` — ~280 lines
- `WriteWalScope.cs` — ~210 lines
- **Total non-test: ~490 lines**

### Test
- `AtomicBlobWriterTests.cs` — ~280 lines
- `WriteWalScopeTests.cs` — ~290 lines
- `WriteCrashConsistencyTests.cs` — ~250 lines
- (`Helpers/RepoRoot.cs` if absent: ~20 lines)
- **Total test: ~840 lines**

## Non-goals

- Do NOT call `AtomicBlobWriter` from `WritePipeline` — that is §2.5.
- Do NOT implement the `WRITE` recovery sweep — `WalRepository.ListIncompleteAsync` is implemented in §1.6, but the sweep that calls it is Phase 5.
- Do NOT add `IStorageProvider`, tail uploads, or anything beyond the local skink — Phase 3 / 4.
- Do NOT add a notification-bus publish from inside `AtomicBlobWriter` or `WriteWalScope` — the user-visible notification is published by `WritePipeline` in §2.5 when its `Result` is `Fail`. (Per Principle 24, the *write pipeline* failure path is the publish site, not the per-stage primitives.)
- Do NOT detect or warn on FAT32 — Phase 6 setup work; XML doc on `AtomicBlobWriter` *mentions* the FAT32 caveat per blueprint §13.4 but does not act on it.
- Do NOT add a streaming-chunked variant — V1 reads the blob into a single buffer (§2.5 will pass a `ReadOnlyMemory<byte>`); the streaming variant is a post-V1 optimisation.
- Do NOT reuse BlobIDs across retries — every `WritePipeline.ExecuteAsync` call generates a fresh BlobID (cross-cutting decision, §2.4 dev-plan key constraints).
- Do NOT add `[InternalsVisibleTo]` for the test project — production internals stay internal; tests use reflection for `IsDiskFull` and `IsFileExists` (CLAUDE.md §"Testing" rule, carried forward from §1.6).
- Do NOT update `BLUEPRINT.md`, `CLAUDE.md`, or `dev-plan/` in this PR — no doc drift expected.
- Do NOT update `docs/error-handling.md` with the `WritePipeline` worked example — that lands with §2.5 (cited in §2.5 acceptance criteria).
