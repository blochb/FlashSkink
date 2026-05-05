# Error Handling

FlashSkink uses the `Result` / `Result<T>` pattern throughout Core. No `public` method in
`FlashSkink.Core` or `FlashSkink.Core.Abstractions` throws across the public boundary; every
failure is returned as data.

---

## Result types

```csharp
// Non-generic — used when the operation has no meaningful return value on success.
Result result = Result.Ok();
Result result = Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to insert file.", ex);
Result result = Result.Fail(new ErrorContext { Code = ..., Message = ..., Metadata = ... });

// Generic — carries a value on success.
Result<VolumeFile?> result = Result<VolumeFile?>.Ok(file);
Result<VolumeFile?> result = Result<VolumeFile?>.Fail(ErrorCode.Cancelled, "Cancelled.", ex);
```

`ErrorContext` carries:
- `Code` — a value from `ErrorCode` (the primary dispatch key for callers)
- `Message` — human-readable description
- `ExceptionType` / `ExceptionMessage` / `StackTrace` — captured from the original exception, never the object itself
- `Metadata` — optional `Dictionary<string, string>` for structured extra data (e.g. `ChildCount`)

---

## Catch ordering

Every `async` method that can fail follows this pattern (Principles 14 and 15):

```csharp
catch (OperationCanceledException ex)
{
    _logger.LogInformation("Operation cancelled");                // LogInformation — cancellation is not a fault
    return Result.Fail(ErrorCode.Cancelled, "...", ex);
}
catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteErrorCodes.UniqueConstraintFailed)
{
    _logger.LogInformation("Unique constraint violation");        // narrow, expected case
    return Result.Fail(ErrorCode.PathConflict, "...", ex);
}
catch (SqliteException ex)
{
    _logger.LogError(ex, "Database error");                       // broad SQLite catch
    return Result.Fail(ErrorCode.DatabaseWriteFailed, "...", ex);
}
catch (Exception ex)
{
    _logger.LogError(ex, "Unexpected error");                     // always-last fallback
    return Result.Fail(ErrorCode.Unknown, "...", ex);
}
```

Rules:
1. `OperationCanceledException` is **always first**.
2. Specific, narrow exception types come before broad ones.
3. `catch (Exception ex)` is **always last** and **always present**.
4. Cancellation is logged at `Information` or `Debug`, never `Error`.

---

## Worked example — `BrainConnectionFactory.CreateAsync`

This is the reference implementation for the full pattern (blueprint §6.8). It demonstrates
all three layers: cancellation, specific SQLite error, and unknown fallback.

```csharp
public async Task<Result<SqliteConnection>> CreateAsync(
    string brainPath, byte[] dek, CancellationToken ct)
{
    SqliteConnection? connection = null;
    try
    {
        ct.ThrowIfCancellationRequested();

        connection = new SqliteConnection($"Data Source={brainPath}");
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Apply SQLCipher key derived from the DEK.
        var keyHex = Convert.ToHexString(dek).ToLowerInvariant();
        using var keyCmd = connection.CreateCommand();
        keyCmd.CommandText = $"PRAGMA key = \"x'{keyHex}'\"";
        await keyCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Run schema migrations so all tables exist.
        var migrationResult = await _migrationRunner.RunAsync(connection, ct).ConfigureAwait(false);
        if (!migrationResult.Success)
        {
            connection.Dispose();
            return Result<SqliteConnection>.Fail(migrationResult.Error!);
        }

        return Result<SqliteConnection>.Ok(connection);
    }
    catch (OperationCanceledException ex)
    {
        connection?.Dispose();
        _logger.LogInformation("CreateAsync cancelled for brain at {Path}", brainPath);
        return Result<SqliteConnection>.Fail(ErrorCode.Cancelled, "Brain connection cancelled.", ex);
    }
    catch (SqliteException ex)
    {
        connection?.Dispose();
        _logger.LogError(ex, "SQLite error opening brain at {Path}", brainPath);
        return Result<SqliteConnection>.Fail(ErrorCode.DatabaseWriteFailed,
            "Failed to open brain database.", ex);
    }
    catch (Exception ex)
    {
        connection?.Dispose();
        _logger.LogError(ex, "Unexpected error opening brain at {Path}", brainPath);
        return Result<SqliteConnection>.Fail(ErrorCode.Unknown,
            "Unexpected error opening brain database.", ex);
    }
}
```

Key points in this example:

| Point | Detail |
|---|---|
| Partial resource on failure | `connection` is disposed in every catch block (Principle 16) |
| Cancellation first | `ct.ThrowIfCancellationRequested()` at the top; `OperationCanceledException` caught first |
| Inner `Result` propagated | `migrationResult.Error!` re-wrapped without double-logging |
| No raw exception escaping | All three catches return `Result.Fail`; the exception object never crosses the boundary |
| Logging once | The site that calls `Result.Fail` logs once; the caller logs the returned `ErrorContext` |

---

## CancellationToken.None on compensation paths

When a method must run a cleanup or compensation step even after the main work was cancelled
or failed, pass `CancellationToken.None` **as a literal** at every `await` site in the
compensation block (Principle 17):

```csharp
catch (SqliteException ex)
{
    tx?.Rollback();
    // Compensation — must not be cancelled mid-flight.
    await _wal.TransitionAsync(walRow.WalId, "FAILED", CancellationToken.None).ConfigureAwait(false);
    _logger.LogError(ex, "Database error; WAL marked FAILED");
    return Result.Fail(ErrorCode.DatabaseWriteFailed, "...", ex);
}
```

Writing `var none = CancellationToken.None; await Foo(none)` defeats the readability goal
and is a Principle 17 violation. The literal must appear at the call site.

---

---

## Worked example — `WritePipeline.ExecuteAsync` failure rollback (§2.5)

This example walks the most complex failure interleaving: the atomic blob write succeeds (the
file is renamed into its sharded destination), but the subsequent brain transaction fails with a
UNIQUE-constraint violation. The §21.3 invariant must be restored — the on-disk blob must be
deleted and the WAL row must end in `FAILED`.

### Stage sequence

```
1. WriteWalScope.OpenAsync  → inserts WAL row in PREPARE (auto-committed, outside brain tx)
2. AtomicBlobWriter.WriteAsync → staging write + fsync + rename → blobPath
3. scope.MarkRenamed()          → sets _renameCompleted = true on the scope
4. CommitBrainAsync
   a. ctx.BrainConnection.BeginTransaction()
   b. INSERT INTO Blobs    ✓
   c. INSERT INTO Files    ✗ — UNIQUE constraint (VirtualPath already exists)
      → SqliteException thrown
   d. tx.Rollback()        — Blobs insert is rolled back
   e. return Result.Fail(ErrorCode.PathConflict, ...)
5. CommitBrainAsync returns a failed Result to ExecuteAsync
6. ExecuteAsync calls await LogAndPublishAsync(...)   — publishes Error notification to bus
7. ExecuteAsync returns Result.Fail(PathConflict) to caller
8. The `await using var scope` fires WriteWalScope.DisposeAsync():
   a. _completed == false  → rollback proceeds
   b. BlobWriter.DeleteStagingAsync(...)     — no-op (staging already renamed)
   c. _renameCompleted == true
      → BlobWriter.DeleteDestinationAsync(...)  — deletes the renamed blob file
   d. _wal.TransitionAsync(_walId, "FAILED", CancellationToken.None)
      — WAL row transitions PREPARE → FAILED
```

### Post-failure invariant state

| Resource | State |
|---|---|
| Staging file | Absent (renamed in step 2; deleted in step 8c) |
| Destination blob file | Absent (deleted in step 8c — `MarkRenamed` was set) |
| `Blobs` row | Absent (brain tx rolled back in step 4d) |
| `Files` row | Absent (constraint failed before insert) |
| WAL row | `FAILED` (step 8d) |
| `ActivityLog` row | Absent (brain tx rolled back) |

The §21.3 invariant holds: every `Files` row references an existing `Blobs` row with an
existing on-disk blob, or is NULL. There are no orphan rows or orphan files.

### Code sketch

```csharp
// In CommitBrainAsync — the tx failure path:
catch (SqliteException ex) when (ex.IsUniqueConstraintViolation())
{
    tx?.Rollback();                             // rolls back Blobs and partial Files inserts
    _logger.LogError(ex, "Path conflict ...");
    return Result.Fail(ErrorCode.PathConflict, "A file named '...' already exists.", ex);
}
// CommitBrainAsync returns to ExecuteAsync:
var commitResult = await CommitBrainAsync(context, args, scope, ct);
if (!commitResult.Success)
{
    await LogAndPublishAsync(context, virtualPath, commitResult.Error!, ct);
    return Result<WriteReceipt>.Fail(commitResult.Error!);
}
// The `await using var scope` in ExecuteAsync fires DisposeAsync on exit.
// Inside DisposeAsync (Principle 17 — CancellationToken.None at every site):
await _blobWriter.DeleteStagingAsync(skinkRoot, blobId, CancellationToken.None);
if (_renameCompleted)
{
    await _blobWriter.DeleteDestinationAsync(skinkRoot, blobId, CancellationToken.None);
}
await _wal.TransitionAsync(_walId, "FAILED", CancellationToken.None);
```

---

## Caller pattern

Callers (ViewModels, CLI command handlers) inspect `Result.Success` and log the
`ErrorContext` when handling a failure. The Core site that called `Result.Fail` already
logged; do not log again (Principle 27):

```csharp
var result = await _fileRepository.InsertAsync(file, ct);
if (!result.Success)
{
    _logger.LogWarning("File insert failed: {Code} — {Message}",
        result.Error!.Code, result.Error.Message);
    // surface to UI / return CLI exit code
    return;
}
```
