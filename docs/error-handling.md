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
