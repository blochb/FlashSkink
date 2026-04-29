# PR 1.6 — Brain repositories

**Branch:** pr/1.6-brain-repositories
**Blueprint sections:** §16.2, §16.4, §16.5, §16.6
**Dev plan section:** phase-1 §1.6

## Scope

Delivers six repository classes in `FlashSkink.Core/Metadata/` and the domain-model types
they depend on. Each repository takes a `SqliteConnection` (from `VolumeSession.BrainConnection`)
and uses Dapper for general queries and raw `SqliteDataReader` only for the single hot path
(`UploadQueueRepository.DequeueNextBatchAsync`).

Also delivers the domain model records that cross the Core→Presentation boundary
(`VolumeFile`, `BlobRecord`, `ActivityLogEntry`, `BackgroundFailure`) in
`FlashSkink.Core.Abstractions/Models/`, and internal repository DTOs
(`TailUploadRow`, `UploadSessionRow`, `WalRow`) in `FlashSkink.Core/Metadata/`.

Implements the carry-forward obligation from §1.3: the skipped
`UnlockFromMnemonicAsync_RoundTrip_Succeeds` test in `KeyVaultTests.cs`.

Updates `docs/error-handling.md` with the `BrainConnectionFactory.CreateAsync` worked example
(deferred from §1.5).

## Files to create

### Models — Core.Abstractions
- `src/FlashSkink.Core.Abstractions/Models/VolumeFile.cs` — sealed record mapping the `Files` table, ~55 lines
- `src/FlashSkink.Core.Abstractions/Models/BlobRecord.cs` — sealed record mapping the `Blobs` table, ~30 lines
- `src/FlashSkink.Core.Abstractions/Models/ActivityLogEntry.cs` — sealed record mapping `ActivityLog`, ~22 lines
- `src/FlashSkink.Core.Abstractions/Models/BackgroundFailure.cs` — sealed record mapping `BackgroundFailures`, ~25 lines

### Internal DTOs — Core/Metadata
- `src/FlashSkink.Core/Metadata/TailUploadRow.cs` — `readonly record struct` for `DequeueNextBatchAsync` hot path, ~22 lines
- `src/FlashSkink.Core/Metadata/UploadSessionRow.cs` — sealed record mapping `UploadSessions`, ~20 lines
- `src/FlashSkink.Core/Metadata/WalRow.cs` — sealed record mapping `WAL`, ~18 lines

### Repositories — Core/Metadata
- `src/FlashSkink.Core/Metadata/FileRepository.cs` — 12-method file-tree repository, ~330 lines
- `src/FlashSkink.Core/Metadata/BlobRepository.cs` — 7-method blob CRUD, ~185 lines
- `src/FlashSkink.Core/Metadata/UploadQueueRepository.cs` — 8-method upload queue + session, ~210 lines
- `src/FlashSkink.Core/Metadata/WalRepository.cs` — 4-method WAL state machine, ~130 lines
- `src/FlashSkink.Core/Metadata/ActivityLogRepository.cs` — 3-method audit log, ~95 lines
- `src/FlashSkink.Core/Metadata/BackgroundFailureRepository.cs` — 4-method failure queue, ~115 lines

### Tests — tests/FlashSkink.Tests/Metadata
- `tests/FlashSkink.Tests/Metadata/FileRepositoryTests.cs` — ~290 lines
- `tests/FlashSkink.Tests/Metadata/BlobRepositoryTests.cs` — ~185 lines
- `tests/FlashSkink.Tests/Metadata/UploadQueueRepositoryTests.cs` — ~175 lines
- `tests/FlashSkink.Tests/Metadata/WalRepositoryTests.cs` — ~130 lines
- `tests/FlashSkink.Tests/Metadata/ActivityLogRepositoryTests.cs` — ~85 lines
- `tests/FlashSkink.Tests/Metadata/BackgroundFailureRepositoryTests.cs` — ~105 lines

## Files to modify

- `src/FlashSkink.Core.Abstractions/FlashSkink.Core.Abstractions.csproj` — no NuGet changes;
  the new `Models/` files are picked up automatically (no `<Compile>` entries needed with
  implicit globbing)
- `tests/FlashSkink.Tests/Crypto/KeyVaultTests.cs` — replace the `[Fact(Skip = ...)]`
  placeholder at line 288 with a real implementation of
  `UnlockFromMnemonicAsync_RoundTrip_Succeeds`
- `docs/error-handling.md` — add the `BrainConnectionFactory.CreateAsync` worked example
  (deferred from §1.5 non-goals)
- `CLAUDE.md` — add a carve-out sub-bullet under Principle 1 documenting that
  `IAsyncEnumerable<readonly record struct>` brain hot-path readers are the one sanctioned
  exception to the "all public methods return `Result`" rule (see §9.7 and issue 2 below)

## Dependencies

- NuGet: none new (Dapper and SQLitePCLRaw were registered in §1.5)
- Project references: none new

## Public API surface

### `FlashSkink.Core.Abstractions.Models.VolumeFile` (sealed record)

Summary intent: domain model for one row in the `Files` brain table — represents either a
file or a folder node in the user's virtual tree.

```
string  FileId          // TEXT PRIMARY KEY
string? ParentId        // NULL = root
bool    IsFolder
bool    IsSymlink
string? SymlinkTarget   // non-null iff IsSymlink
string  Name            // UTF-8 NFC-normalised
string? Extension       // ".jpg", lowercase, dot-prefixed; null for folders/symlinks
string? MimeType        // "image/jpeg"; null if undetectable
string  VirtualPath     // denormalised full path
long    SizeBytes       // 0 for folders/symlinks
DateTime CreatedUtc
DateTime ModifiedUtc
DateTime AddedUtc
string? BlobId          // null for folders and symlinks
```

---

### `FlashSkink.Core.Abstractions.Models.BlobRecord` (sealed record)

Summary intent: domain model for one row in the `Blobs` table.

```
string   BlobId
long     EncryptedSize
long     PlaintextSize
string   PlaintextSha256
string   EncryptedXxHash
string?  Compression         // null | "LZ4" | "ZSTD"
string   BlobPath            // relative path on USB
DateTime CreatedUtc
DateTime? SoftDeletedUtc     // null = active
DateTime? PurgeAfterUtc
```

---

### `FlashSkink.Core.Abstractions.Models.ActivityLogEntry` (sealed record)

Summary intent: one row from the `ActivityLog` table.

```
string   EntryId
DateTime OccurredUtc
string   Category     // WRITE | DELETE | RESTORE | TAIL_ADDED | TAIL_REMOVED | RECOVERY | EXPORT | VERIFY
string   Summary
string?  Detail       // JSON optional
```

---

### `FlashSkink.Core.Abstractions.Models.BackgroundFailure` (sealed record)

Summary intent: one row from the `BackgroundFailures` table.

```
string   FailureId
DateTime OccurredUtc
string   Source
string   ErrorCode
string   Message
string?  Metadata      // JSON optional
bool     Acknowledged
```

---

### `FlashSkink.Core.Metadata.TailUploadRow` (readonly record struct)

Summary intent: hot-path DTO yielded by `UploadQueueRepository.DequeueNextBatchAsync`; one
pending upload task per row.

```
string  FileId
string  ProviderId
string  Status
string? RemoteId
DateTime QueuedUtc
int     AttemptCount
```

---

### `FlashSkink.Core.Metadata.UploadSessionRow` (sealed record)

Summary intent: in-memory representation of one row in `UploadSessions`.

```
string   FileId
string   ProviderId
string   SessionUri
DateTime SessionExpiresUtc
long     BytesUploaded
long     TotalBytes
DateTime LastActivityUtc
```

---

### `FlashSkink.Core.Metadata.WalRow` (sealed record)

Summary intent: one WAL row, used by recovery logic and `ListIncompleteAsync`.

```
string   WalId
string   Operation   // WRITE | DELETE | CASCADE_DELETE | TAIL_DELETE | PURGE
string   Phase       // PREPARE | COMMITTED | FAILED
DateTime StartedUtc
DateTime UpdatedUtc
string   Payload     // JSON
```

---

### `FlashSkink.Core.Metadata.FileRepository` (sealed class)

Summary intent: all file-tree and folder-tree operations against the `Files` (and `DeleteLog`)
tables, using recursive CTEs for tree traversal and WAL journalling for multi-step mutations.

Constructor: `FileRepository(SqliteConnection connection, WalRepository wal, ILogger<FileRepository> logger)`

- `Task<Result> InsertAsync(VolumeFile file, CancellationToken ct)`
  Inserts one `Files` row using all fields from `file` (including `file.BlobId`).
  Maps `SqliteException` with UNIQUE constraint violation (`SqliteErrorCode == 19`) to
  `ErrorCode.PathConflict`. Note: `BlobId` lives on `VolumeFile`; no separate `blobId`
  parameter is needed.

- `Task<Result<VolumeFile?>> GetByIdAsync(string fileId, CancellationToken ct)`
  Single-row fetch via Dapper. Returns `null` value (not `ErrorCode.FileNotFound`) when no
  row exists; the `Result` is still success. Callers distinguish "not found" from null.

- `Task<Result<IReadOnlyList<VolumeFile>>> ListChildrenAsync(string? parentId, CancellationToken ct)`
  Immediate children of `parentId` (null = root). Folders first, then files, both
  alphabetical (§16.4 list-children query). Via Dapper.

- `Task<Result<IReadOnlyList<VolumeFile>>> ListFilesAsync(string virtualPathPrefix, CancellationToken ct)`
  All descendants whose `VirtualPath` starts with `virtualPathPrefix`. Via Dapper with
  `WHERE VirtualPath LIKE @prefix || '%'` (prefix search, not recursive CTE).

- `Task<Result<string?>> EnsureFolderPathAsync(string virtualPath, CancellationToken ct)`
  Walks each path segment of `virtualPath` (split on `/`). For each segment: finds or
  creates the folder row. Returns the `FileId` of the deepest (leaf) folder, or `null` if
  `virtualPath` is empty/root. Returns `ErrorCode.PathConflict` if any segment resolves to
  an existing *file* (not folder).

- `Task<Result<int>> CountChildrenAsync(string folderId, CancellationToken ct)`
  `SELECT COUNT(*) FROM Files WHERE ParentID = @folderId`. Via Dapper.

- `Task<Result<IReadOnlyList<VolumeFile>>> GetDescendantsAsync(string folderId, CancellationToken ct)`
  Recursive-descendants CTE from §16.4. Via Dapper with the CTE query.

- `Task<Result> DeleteFolderCascadeAsync(string folderId, bool confirmed, CancellationToken ct)`
  If `!confirmed`: counts children; if > 0, returns `ErrorCode.ConfirmationRequired` with
  `Metadata["ChildCount"]` set. If `confirmed`: opens an explicit transaction; inserts WAL
  row (operation = `CASCADE_DELETE`, phase = `PREPARE`); fetches all descendants; deletes
  their `Files` rows; soft-deletes all referenced Blob rows; writes `DeleteLog` entries;
  transitions WAL to `COMMITTED`; commits. Compensation path (WAL transition on failure)
  uses `CancellationToken.None` literal (Principle 17).

- `Task<Result> DeleteFileAsync(string fileId, CancellationToken ct)`
  Removes the `Files` row; soft-deletes the referenced `Blobs` row (if any) with
  `PurgeAfterUtc = DateTime.UtcNow + GracePeriodDays`; writes a `DeleteLog` entry with
  `Trigger = 'USER_ACTION'` — all in one explicit transaction with a WAL row
  (operation = `DELETE`). Grace period days are read via Dapper inline:
  `await _connection.QuerySingleOrDefaultAsync<string>("SELECT Value FROM Settings WHERE Key = 'GracePeriodDays'")`,
  parsed to `int` with a fallback of 30. Compensation path uses `CancellationToken.None` literal.

- `Task<Result> RenameFolderAsync(string folderId, string newName, CancellationToken ct)`
  Pre-checks for name conflict via `IX_Files_Parent_Name`. Runs the recursive
  `VirtualPath` update CTE from §16.4 inside one transaction. Returns
  `ErrorCode.PathConflict` on unique-index violation.

- `Task<Result> MoveAsync(string fileId, string? newParentId, CancellationToken ct)`
  Runs the ancestor-chain cycle-detection CTE (§16.4) first; returns
  `ErrorCode.CyclicMoveDetected` if a cycle is found. Updates `ParentID` and `VirtualPath`.
  If `fileId` is a folder: runs the recursive `VirtualPath` update CTE for descendants. All
  in one transaction. Does not rename the item; to move-and-rename, callers invoke `MoveAsync`
  then `RenameFolderAsync` as two separate transactions. A crash between them leaves the item
  moved-but-not-renamed; the WAL row from each call gives recovery enough context.

- `Task<Result> RestoreFromGracePeriodAsync(string blobId, string virtualPath, CancellationToken ct)`
  Fetches the `Blobs` row first; returns `ErrorCode.BlobNotFound` if it does not exist (i.e.
  the sweeper already hard-deleted it). Otherwise clears `SoftDeletedUtc` and `PurgeAfterUtc`
  on the `Blobs` row and re-inserts a `Files` row pointing at that blob, all in one transaction.
  Note: `FileRepository` directly updates the `Blobs` table here (and in `DeleteFileAsync` /
  `DeleteFolderCascadeAsync`) rather than delegating to `BlobRepository`. This is deliberate —
  it keeps the soft-delete inside the same transaction as the `Files` DML — and is the one place
  `FileRepository` has direct knowledge of the `Blobs` schema.

---

### `FlashSkink.Core.Metadata.BlobRepository` (sealed class)

Summary intent: CRUD on the `Blobs` table; no WAL involvement.

Constructor: `BlobRepository(SqliteConnection connection, ILogger<BlobRepository> logger)`

- `Task<Result> InsertAsync(BlobRecord blob, CancellationToken ct)`
- `Task<Result<BlobRecord?>> GetByIdAsync(string blobId, CancellationToken ct)`
- `Task<Result<BlobRecord?>> GetByPlaintextHashAsync(string plaintextSha256, CancellationToken ct)`
  Returns the active (non-soft-deleted) blob matching the hash. Used in Phase 2 to
  short-circuit re-encryption of unchanged files.
- `Task<Result> SoftDeleteAsync(string blobId, DateTime purgeAfterUtc, CancellationToken ct)`
  Sets `SoftDeletedUtc = now, PurgeAfterUtc = @purgeAfterUtc`.
- `Task<Result> MarkCorruptAsync(string blobId, CancellationToken ct)`
  Sets `SoftDeletedUtc = now, PurgeAfterUtc = now` (immediate purge eligibility). The healing
  service (Phase 5) re-downloads from a tail and re-inserts a fresh `Blobs` row.
- `Task<Result<IReadOnlyList<BlobRecord>>> ListPendingPurgeAsync(CancellationToken ct)`
  Returns all rows where `PurgeAfterUtc <= DateTime.UtcNow`. Via Dapper.
- `Task<Result> HardDeleteAsync(string blobId, CancellationToken ct)`
  Deletes the `Blobs` row. Called by the sweeper after verifying the on-disk blob is gone.

---

### `FlashSkink.Core.Metadata.UploadQueueRepository` (sealed class)

Summary intent: manages `TailUploads` (upload queue) and `UploadSessions` (resumable-upload
state) tables.

Constructor: `UploadQueueRepository(SqliteConnection connection, ILogger<UploadQueueRepository> logger)`

- `Task<Result> EnqueueAsync(string fileId, string providerId, CancellationToken ct)`
  Inserts a `TailUploads` row with `Status = PENDING`, `QueuedUtc = now`,
  `AttemptCount = 0`. Maps UNIQUE violation to `ErrorCode.PathConflict` (duplicate enqueue).

- `async IAsyncEnumerable<TailUploadRow> DequeueNextBatchAsync(string providerId, int batchSize, [EnumeratorCancellation] CancellationToken ct)`
  Raw `SqliteDataReader` hot path (Principle 22). Queries `TailUploads` where
  `ProviderID = @providerId AND Status IN ('PENDING','FAILED')` ordered by `QueuedUtc ASC`
  with `LIMIT @batchSize`. Yields `TailUploadRow` structs. The `async` modifier and
  `[EnumeratorCancellation]` attribute (`System.Runtime.CompilerServices`) are required so
  that `ct` flows correctly when the caller uses `.WithCancellation()` on the enumerable.
  Throws on SQLite error; caller (Phase 3 `UploadQueueService`) handles exceptions during
  iteration. Returns `IAsyncEnumerable<TailUploadRow>` rather than
  `Result<IReadOnlyList<TailUploadRow>>` per the dev-plan §1.6 explicit specification and
  Principle 22; this is the one sanctioned deviation from Principle 1 in Phase 1, documented
  as a carve-out in `CLAUDE.md` Principle 1.

- `Task<Result> MarkUploadingAsync(string fileId, string providerId, CancellationToken ct)`
  Sets `Status = UPLOADING, LastAttemptUtc = now, AttemptCount += 1`.

- `Task<Result> MarkUploadedAsync(string fileId, string providerId, string remoteId, CancellationToken ct)`
  Sets `Status = UPLOADED, RemoteId = @remoteId, UploadedUtc = now`.

- `Task<Result> MarkFailedAsync(string fileId, string providerId, string lastError, CancellationToken ct)`
  Sets `Status = FAILED, LastError = @lastError, LastAttemptUtc = now`.

- `Task<Result<UploadSessionRow?>> GetOrCreateSessionAsync(string fileId, string providerId, string sessionUri, DateTime sessionExpiresUtc, long totalBytes, CancellationToken ct)`
  Upserts an `UploadSessions` row: INSERT OR REPLACE with the given values and
  `BytesUploaded = 0, LastActivityUtc = now`. Returns the current row (or the newly created
  one) via Dapper `QuerySingleOrDefaultAsync`.

- `Task<Result> UpdateSessionProgressAsync(string fileId, string providerId, long bytesUploaded, CancellationToken ct)`
  Sets `BytesUploaded = @bytesUploaded, LastActivityUtc = now`.

- `Task<Result> DeleteSessionAsync(string fileId, string providerId, CancellationToken ct)`
  Deletes the `UploadSessions` row.

---

### `FlashSkink.Core.Metadata.WalRepository` (sealed class)

Summary intent: manages the `WAL` crash-recovery state machine. Accepts an optional
`SqliteTransaction` on `InsertAsync` so callers can embed WAL inserts in an existing
transaction (used by `FileRepository.DeleteFolderCascadeAsync` and `MoveAsync`).

Constructor: `WalRepository(SqliteConnection connection, ILogger<WalRepository> logger)`

- `Task<Result> InsertAsync(WalRow row, SqliteTransaction? transaction = null, CancellationToken ct = default)`
  Inserts the WAL row. If `transaction` is non-null, assigns it to the command; otherwise
  the INSERT is auto-committed.

- `Task<Result> TransitionAsync(string walId, string newPhase, CancellationToken ct)`
  Updates `Phase = @newPhase, UpdatedUtc = now`. Called with `CancellationToken.None`
  literal on compensation paths (Principle 17).

- `Task<Result<IReadOnlyList<WalRow>>> ListIncompleteAsync(CancellationToken ct)`
  Returns all WAL rows where `Phase NOT IN ('COMMITTED', 'FAILED')`. Used by the Phase 5
  recovery sweep. Via Dapper.

- `Task<Result> DeleteAsync(string walId, CancellationToken ct)`
  Hard-deletes the WAL row after successful recovery.

---

### `FlashSkink.Core.Metadata.ActivityLogRepository` (sealed class)

Summary intent: append-only audit trail for user-visible events.

Constructor: `ActivityLogRepository(SqliteConnection connection, ILogger<ActivityLogRepository> logger)`

- `Task<Result> AppendAsync(ActivityLogEntry entry, CancellationToken ct)`
- `Task<Result<IReadOnlyList<ActivityLogEntry>>> ListRecentAsync(int limit, CancellationToken ct)`
  `ORDER BY OccurredUtc DESC LIMIT @limit`. Via Dapper.
- `Task<Result<IReadOnlyList<ActivityLogEntry>>> ListByCategoryAsync(string category, int limit, CancellationToken ct)`
  `WHERE Category = @category ORDER BY OccurredUtc DESC LIMIT @limit`. Via Dapper.

---

### `FlashSkink.Core.Metadata.BackgroundFailureRepository` (sealed class)

Summary intent: persisted queue for background-service failures that survive restarts
(Principle 24).

Constructor: `BackgroundFailureRepository(SqliteConnection connection, ILogger<BackgroundFailureRepository> logger)`

- `Task<Result> AppendAsync(BackgroundFailure failure, CancellationToken ct)`
- `Task<Result<IReadOnlyList<BackgroundFailure>>> ListUnacknowledgedAsync(CancellationToken ct)`
  `WHERE Acknowledged = 0 ORDER BY OccurredUtc DESC`. Via Dapper.
- `Task<Result> AcknowledgeAsync(string failureId, CancellationToken ct)`
  Sets `Acknowledged = 1` on the specified row.
- `Task<Result> AcknowledgeAllAsync(CancellationToken ct)`
  Sets `Acknowledged = 1` on all rows where `Acknowledged = 0`.

---

## Internal types

### `SqliteErrorCodes` (internal static class inside `FileRepository.cs` or a shared file)
Constants for SQLite error codes used in exception filters:
- `const int UniqueConstraintFailed = 19` — `SQLITE_CONSTRAINT_UNIQUE`
- `const int ForeignKeyViolation = 787` — `SQLITE_CONSTRAINT_FOREIGNKEY`

These are already used in spirit by §1.5; centralising them avoids magic numbers.

## Method-body contracts

### Catch ordering — all repository `async` methods

Every repository method follows this pattern (Principles 14, 15):
```
1. catch (OperationCanceledException)   → ErrorCode.Cancelled, logged at LogInformation
2. catch (SqliteException ex) when (ex.SqliteErrorCode == 19)  → ErrorCode.PathConflict (where applicable)
3. catch (SqliteException)              → ErrorCode.DatabaseWriteFailed, logged at LogError
4. catch (Exception)                   → ErrorCode.Unknown, logged at LogError
```
Read-only methods omit catch 2. Methods that return lists return empty lists (not failures) for
zero-row results.

### `FileRepository.DeleteFolderCascadeAsync` — transaction outline

```
1. If !confirmed:
     count = CountChildrenAsync(folderId, ct)
     if count > 0: return Result.Fail(ConfirmationRequired, ..., Metadata{"ChildCount":count})

2. Begin explicit transaction tx.

3. walRow = new WalRow(WalId=Guid.NewGuid().ToString(), Operation="CASCADE_DELETE",
                       Phase="PREPARE", StartedUtc=now, UpdatedUtc=now, Payload=folderId JSON)
   await _wal.InsertAsync(walRow, tx, CancellationToken.None);  // Principle 17 — must not cancel

4. descendants = await GetDescendantsAsync(folderId, ct) — executed on same connection inside tx

5. For each descendant with BlobID != null:
     set SoftDeletedUtc=now, PurgeAfterUtc=now+grace on Blobs row (inside tx)
     (grace period: Dapper query on Settings["GracePeriodDays"], default 30 days)

6. DELETE FROM Files WHERE FileID IN (descendant IDs)  — single parameterised batch

7. INSERT INTO DeleteLog (LogID, DeletedAt, FileID, Name, VirtualPath, IsFolder, Trigger)
   for each deleted row; Trigger = 'CASCADE' for all rows in this method

8. await _wal.TransitionAsync(walRow.WalId, "COMMITTED", CancellationToken.None);  // Principle 17

9. tx.Commit()
   return Result.Ok()

On any exception between step 2 and step 9:
   tx.Rollback()   (compensation — no cancel)
   await _wal.TransitionAsync(walRow.WalId, "FAILED", CancellationToken.None);
   return Result.Fail(ErrorCode.DatabaseWriteFailed, ...)
```

### `FileRepository.MoveAsync` — cycle detection and path update

```
1. Cycle check (only if moving a folder):
     run ancestor-chain CTE: if @fileId appears in ancestor chain of @newParentId
     → return ErrorCode.CyclicMoveDetected

2. Resolve new VirtualPath:
     if newParentId == null: newVirtualPath = "/" + name
     else: newVirtualPath = parent.VirtualPath + "/" + name

3. Begin tx.

4. UPDATE Files SET ParentID=@newParentId, VirtualPath=@newVirtualPath WHERE FileID=@fileId

5. If fileId is a folder:
     run recursive VirtualPath update CTE (§16.4) with @folderId=fileId,
     @newPrefix=newVirtualPath, @oldPrefixLength=oldVirtualPath.Length

6. tx.Commit()
   return Result.Ok()

On UNIQUE constraint violation: tx.Rollback(), return ErrorCode.PathConflict
On other exception: tx.Rollback(), return ErrorCode.DatabaseWriteFailed
```

### `UploadQueueRepository.DequeueNextBatchAsync` — raw reader

```csharp
public async IAsyncEnumerable<TailUploadRow> DequeueNextBatchAsync(
    string providerId, int batchSize,
    [EnumeratorCancellation] CancellationToken ct)
{
await using var cmd = _connection.CreateCommand();
cmd.CommandText = """
    SELECT FileID, ProviderID, Status, RemoteId, QueuedUtc, AttemptCount
    FROM TailUploads
    WHERE ProviderID = @providerId AND Status IN ('PENDING', 'FAILED')
    ORDER BY QueuedUtc ASC
    LIMIT @batchSize
    """;
cmd.Parameters.AddWithValue("@providerId", providerId);
cmd.Parameters.AddWithValue("@batchSize", batchSize);
ct.ThrowIfCancellationRequested();
await using var reader = await cmd.ExecuteReaderAsync(ct);
while (await reader.ReadAsync(ct))
{
    yield return new TailUploadRow(
        FileId: reader.GetString(0),
        ProviderId: reader.GetString(1),
        Status: reader.GetString(2),
        RemoteId: reader.IsDBNull(3) ? null : reader.GetString(3),
        QueuedUtc: DateTime.Parse(reader.GetString(4), null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        AttemptCount: reader.GetInt32(5));
}
} // end method
```

## Integration points

From prior PRs:
- `FlashSkink.Core.Abstractions.Results.Result`, `Result<T>`, `ErrorCode`, `ErrorContext`
- `Microsoft.Data.Sqlite.SqliteConnection`, `SqliteCommand`, `SqliteTransaction`,
  `SqliteDataReader`, `SqliteException`
- `Dapper` — `QueryAsync<T>`, `QuerySingleOrDefaultAsync<T>`, `ExecuteAsync`
  (all called as extension methods on `SqliteConnection`)
- `Microsoft.Extensions.Logging.ILogger<T>` (Abstractions only — Principle 28)

## Principles touched

- Principle 1 (Core never throws across its public API — all methods return `Result`/`Result<T>`
  except `DequeueNextBatchAsync`, which the dev plan explicitly specifies as `IAsyncEnumerable`)
- Principle 13 (`CancellationToken ct` last, always present)
- Principle 14 (`OperationCanceledException` first catch)
- Principle 15 (granular exception hierarchy — UNIQUE constraint caught before generic `SqliteException`)
- Principle 16 (dispose on failure — `SqliteTransaction` rolled back in every catch)
- Principle 17 (`CancellationToken.None` literal in WAL compensation paths — all transition and
  rollback calls in `DeleteFolderCascadeAsync`, `DeleteFileAsync`, `RenameFolderAsync`, `MoveAsync`)
- Principle 22 (raw `SqliteDataReader` for `DequeueNextBatchAsync`; Dapper for all other reads)
- Principle 26 (logging never contains secrets — repositories log operation names and error codes,
  never user data, file content, or key material)
- Principle 27 (Core logs at `Result.Fail` construction site; callers log the returned ErrorContext)
- Principle 28 (Core depends only on `Microsoft.Extensions.Logging.Abstractions`)

## Test spec

### Shared test helper — `BrainTestHelper`

A `static class BrainTestHelper` (in a shared file or inline in the first test class that
uses it) provides:
- `CreateInMemoryConnection()` — returns an open `SqliteConnection("Data Source=:memory:")`
  (note trailing colon — required for the in-memory engine; omitting it creates a file named
  `:memory`) with `PRAGMA foreign_keys = ON` applied.
- `ApplySchemaAsync(SqliteConnection conn)` — runs `MigrationRunner.RunAsync` on the
  connection so all V1 tables exist. All repository test classes call this in their
  constructor or a `[BeforeEach]` fixture.
- `InsertTestProvider(SqliteConnection conn, string providerId)` — inserts a minimal
  `Providers` row satisfying the `TailUploads.ProviderID` FK.
- `InsertTestFile(SqliteConnection conn, string fileId, string? parentId = null)` — inserts
  a minimal `Files` row satisfying the `TailUploads.FileID` and `UploadSessions.FileID` FKs.

---

### `tests/FlashSkink.Tests/Metadata/FileRepositoryTests.cs`

**Class: `FileRepositoryTests`** (implements `IAsyncLifetime` to create/dispose the in-memory DB)

Uses `Microsoft.Extensions.Logging.Abstractions.NullLogger<T>` for all loggers in tests
(available transitively via `FlashSkink.Core`'s `Microsoft.Extensions.Logging.Abstractions` reference).

- `InsertAsync_File_RoundTripsViaGetById` — insert a file VolumeFile, get by ID, assert all
  fields match.
- `InsertAsync_Folder_IsFound_AsFolder` — insert a folder row, get by ID, assert `IsFolder = true`.
- `InsertAsync_DuplicateName_SameParent_ReturnsPathConflict` — insert two files with same name
  and same parentId; second returns `ErrorCode.PathConflict`.
- `InsertAsync_DuplicateName_DifferentParent_Succeeds` — two files with same name but different
  parents both succeed.
- `ListChildrenAsync_RootWithTwoItems_ReturnsBothFolderFirst` — insert one file and one folder
  at root; list children of null; assert folder is index 0.
- `ListChildrenAsync_NullParent_ReturnsOnlyRootItems` — insert a nested file; list root children;
  assert the nested file is absent.
- `EnsureFolderPathAsync_NewPath_CreatesAllSegments` — call `EnsureFolderPathAsync("docs/reports")`
  on empty DB; assert both "docs" and "reports" folder rows exist; return value is "reports" FileId.
- `EnsureFolderPathAsync_ExistingPath_IsIdempotent` — call twice with same path; second call
  returns the same leaf FileId; no duplicate rows.
- `EnsureFolderPathAsync_FileAtSegment_ReturnsPathConflict` — insert a file named "docs" at root;
  call `EnsureFolderPathAsync("docs/reports")`; assert `ErrorCode.PathConflict`.
- `CountChildrenAsync_EmptyFolder_ReturnsZero` — insert folder, call `CountChildrenAsync`; assert 0.
- `CountChildrenAsync_TwoChildren_ReturnsTwo` — insert two children; assert 2.
- `GetDescendantsAsync_ThreeLevels_ReturnsAllDescendants` — three-level folder tree; assert
  `GetDescendantsAsync` of root returns all 3 levels.
- `DeleteFileAsync_RemovesFilesRow_SoftDeletesBlob` — insert blob and file, delete file; assert
  `Files` row gone and `Blobs.SoftDeletedUtc` is set and `PurgeAfterUtc` is set.
- `DeleteFolderCascadeAsync_WithChildren_RequiresConfirmation` — insert folder with one child
  file; call with `confirmed = false`; assert `ErrorCode.ConfirmationRequired` with
  `Metadata["ChildCount"] == "1"`.
- `DeleteFolderCascadeAsync_Confirmed_DeletesAll` — insert folder + child file; call with
  `confirmed = true`; assert both `Files` rows gone and `DeleteLog` has two entries.
- `RenameFolderAsync_UpdatesVirtualPathForDescendants` — folder "old" with child file;
  rename to "new"; assert child file's `VirtualPath` is updated.
- `RenameFolderAsync_NameConflict_ReturnsPathConflict` — sibling folder with target name exists;
  assert `ErrorCode.PathConflict`.
- `MoveAsync_FileToNewParent_UpdatesParentAndPath` — move file to another folder; assert
  `ParentID` and `VirtualPath` both updated.
- `MoveAsync_FolderToDescendant_ReturnsCyclicMoveDetected` — attempt to move a folder into one
  of its own descendants; assert `ErrorCode.CyclicMoveDetected`.
- `RestoreFromGracePeriodAsync_ClearsSoftDeleteFields` — soft-delete a blob then restore; assert
  `SoftDeletedUtc` and `PurgeAfterUtc` are null.

---

### `tests/FlashSkink.Tests/Metadata/BlobRepositoryTests.cs`

**Class: `BlobRepositoryTests`**

- `InsertAsync_RoundTripsViaGetById` — insert a `BlobRecord`, get by ID, assert all fields match.
- `GetByPlaintextHashAsync_ExistingHash_ReturnsBlobRecord` — insert blob, query by hash; assert match.
- `GetByPlaintextHashAsync_SoftDeletedBlob_ReturnsNull` — insert blob, soft-delete it, query by
  hash; assert returns null (active blobs only).
- `GetByPlaintextHashAsync_UnknownHash_ReturnsNull`.
- `SoftDeleteAsync_SetsTimestamps` — after `SoftDeleteAsync`, `GetById` shows non-null
  `SoftDeletedUtc` and `PurgeAfterUtc`.
- `MarkCorruptAsync_SetsPurgeToNow` — after `MarkCorruptAsync`, `PurgeAfterUtc <= DateTime.UtcNow`.
- `ListPendingPurgeAsync_ReturnsOnlyEligible` — insert one blob with `PurgeAfterUtc` in the past
  and one in the future; assert only the past one is returned.
- `HardDeleteAsync_RemovesRow` — insert and hard-delete; `GetById` returns null.

---

### `tests/FlashSkink.Tests/Metadata/UploadQueueRepositoryTests.cs`

**Class: `UploadQueueRepositoryTests`**

Requires both a `Providers` row and a `Files` row before inserting `TailUploads` or
`UploadSessions` (both have FK constraints on `FileID` and `ProviderID`). Uses
`BrainTestHelper.InsertTestProvider` and `BrainTestHelper.InsertTestFile` in test setup.

- `EnqueueAsync_InsertsRowInPendingState` — enqueue, then query raw; assert `Status = PENDING`.
- `EnqueueAsync_DuplicateFileAndProvider_ReturnsPathConflict`.
- `DequeueNextBatchAsync_ReturnsPendingRows` — enqueue 3 rows; dequeue batch of 2; assert 2
  rows returned.
- `DequeueNextBatchAsync_SkipsUploadedRows` — enqueue then `MarkUploadedAsync`; dequeue; assert
  0 rows returned.
- `MarkUploadingAsync_ChangesStatus` — enqueue, mark uploading; assert `Status = UPLOADING`
  and `AttemptCount = 1`.
- `MarkUploadedAsync_SetsRemoteId` — enqueue, mark uploaded with remoteId; assert row has
  `Status = UPLOADED` and `RemoteId` set.
- `MarkFailedAsync_SetsLastError` — enqueue, mark failed; assert `Status = FAILED` and
  `LastError` matches.
- `GetOrCreateSessionAsync_InsertsOnFirstCall_Returns` — call once; assert row exists with
  `BytesUploaded = 0`.
- `GetOrCreateSessionAsync_CalledTwice_ReplacesExisting` — call with different `SessionUri`;
  assert new uri is stored.
- `UpdateSessionProgressAsync_UpdatesBytesUploaded` — create session, update progress;
  assert `BytesUploaded` matches.
- `DeleteSessionAsync_RemovesRow` — create session, delete; assert row gone.

---

### `tests/FlashSkink.Tests/Metadata/WalRepositoryTests.cs`

**Class: `WalRepositoryTests`**

- `InsertAsync_InsertsRow_WithPreparePhase` — insert a WAL row; assert it appears in `ListIncompleteAsync`.
- `InsertAsync_WithExternalTransaction_CommitsWithCaller` — begin a transaction, insert WAL row
  inside it, commit manually; assert row exists.
- `TransitionAsync_UpdatesPhase` — insert row in PREPARE, transition to COMMITTED; assert phase updated.
- `ListIncompleteAsync_ExcludesCommittedAndFailed` — insert rows in PREPARE, COMMITTED, FAILED;
  assert only the PREPARE row is returned.
- `DeleteAsync_RemovesRow` — insert and delete; `ListIncompleteAsync` returns empty.

---

### `tests/FlashSkink.Tests/Metadata/ActivityLogRepositoryTests.cs`

**Class: `ActivityLogRepositoryTests`**

- `AppendAsync_RoundTripsViaListRecent` — append one entry; `ListRecentAsync(10)` returns it.
- `ListRecentAsync_RespectsLimit` — append 5 entries; `ListRecentAsync(3)` returns exactly 3
  (most recent first).
- `ListByCategoryAsync_FiltersCategory` — append WRITE and DELETE entries; query WRITE; assert
  only WRITE entries returned.

---

### `tests/FlashSkink.Tests/Metadata/BackgroundFailureRepositoryTests.cs`

**Class: `BackgroundFailureRepositoryTests`**

- `AppendAsync_RoundTripsViaListUnacknowledged` — append one failure; appears in
  `ListUnacknowledgedAsync`.
- `AcknowledgeAsync_SetsAcknowledgedFlag` — append, acknowledge, assert absent from
  `ListUnacknowledgedAsync`.
- `AcknowledgeAllAsync_ClearsAllUnacknowledged` — append 3 failures, `AcknowledgeAllAsync`,
  assert `ListUnacknowledgedAsync` returns empty.

---

### Carry-forward — `tests/FlashSkink.Tests/Crypto/KeyVaultTests.cs`

**Replace the placeholder at line 288:**

`UnlockFromMnemonicAsync_RoundTrip_Succeeds` — implements the deferred positive round-trip test.

Strategy: The vault must be created with the mnemonic-derived KEK as the wrapping key. Since
`KeyVault.CreateAsync(path, password, ct)` calls `DeriveKekFromPassword(password, salt)` and
`UnlockFromMnemonicAsync(path, words, ct)` calls `DeriveKek(seed, salt)`, and both delegate
to the same `RunArgon2(byte[], salt, ...)` internally, the KEKs are equal when
`password bytes == seed bytes`. The test body must include a comment referencing this invariant
(e.g. `// Invariant: DeriveKekFromPassword and DeriveKek both call RunArgon2 with identical
// parameters; passing the BIP-39 seed as the password bytes produces the same KEK.`)
so future contributors to §1.2's KDF don't unknowingly break this test without realising why.

```
1. words = MnemonicService.Generate()
2. seedResult = MnemonicService.ToSeed(words)          // 64-byte seed
3. createResult = KeyVault.CreateAsync(path, seed, ct) // creates vault using seed as password bytes
4. unlockResult = KeyVault.UnlockFromMnemonicAsync(path, words, ct)
5. Assert unlockResult.Success == true
6. Assert unlockResult.Value sequence-equals createResult.Value
7. ZeroMemory both DEKs
```

---

## Acceptance criteria

- [ ] Builds with zero warnings on all targets
- [ ] All new tests pass
- [ ] No existing tests break
- [ ] `UnlockFromMnemonicAsync_RoundTrip_Succeeds` is a real `[Fact]` (not `Skip`)
- [ ] `DequeueNextBatchAsync` uses raw `SqliteDataReader` (no Dapper), returns `IAsyncEnumerable<TailUploadRow>`
- [ ] All other repository reads use Dapper
- [ ] WAL compensation calls (`TransitionAsync` on failure, `tx.Rollback()`) use `CancellationToken.None` literals
- [ ] `DeleteFolderCascadeAsync` with unconfirmed non-empty folder returns `ErrorCode.ConfirmationRequired` with `Metadata["ChildCount"]`
- [ ] `MoveAsync` cycle detection runs the ancestor-chain CTE before any mutation
- [ ] All async methods have `CancellationToken ct` as last parameter
- [ ] `OperationCanceledException` is first catch on all async methods
- [ ] Unique-constraint violations (`SqliteErrorCode == 19`) are caught before generic `SqliteException`
- [ ] `docs/error-handling.md` updated with `BrainConnectionFactory.CreateAsync` worked example
- [ ] `CLAUDE.md` Principle 1 updated with `IAsyncEnumerable` hot-path carve-out
- [ ] `DequeueNextBatchAsync` declared `async IAsyncEnumerable<TailUploadRow>` with `[EnumeratorCancellation]`
- [ ] `DeleteLog.Trigger` is `CASCADE` in `DeleteFolderCascadeAsync`, `USER_ACTION` in `DeleteFileAsync`
- [ ] `Settings["GracePeriodDays"]` is read via Dapper (not raw `SqliteCommand`) in both delete methods
- [ ] `RestoreFromGracePeriodAsync` returns `ErrorCode.BlobNotFound` when the blob row is absent
- [ ] All model types are in `FlashSkink.Core.Abstractions/Models/` (shared with Presentation)
- [ ] `TailUploadRow`, `UploadSessionRow`, `WalRow` are in `FlashSkink.Core/Metadata/` (internal)

## Line-of-code budget

### Non-test
- Models (×4 in Abstractions): ~130 lines total
- Internal DTOs (×3 in Core): ~60 lines total
- `FileRepository.cs`: ~330 lines
- `BlobRepository.cs`: ~185 lines
- `UploadQueueRepository.cs`: ~210 lines
- `WalRepository.cs`: ~130 lines
- `ActivityLogRepository.cs`: ~95 lines
- `BackgroundFailureRepository.cs`: ~115 lines
- **Total non-test: ~1255 lines**

### Test
- `FileRepositoryTests.cs`: ~290 lines
- `BlobRepositoryTests.cs`: ~185 lines
- `UploadQueueRepositoryTests.cs`: ~175 lines
- `WalRepositoryTests.cs`: ~130 lines
- `ActivityLogRepositoryTests.cs`: ~85 lines
- `BackgroundFailureRepositoryTests.cs`: ~105 lines
- `KeyVaultTests.cs` modification: ~+30 lines
- **Total test: ~1000 lines**

## Non-goals

- Do NOT implement `UploadQueueService`, `RangeUploader`, or `RetryPolicy` — Phase 3.
- Do NOT implement WAL recovery execution (`ListIncompleteAsync` is implemented; the recovery
  sweep that calls it is Phase 5).
- Do NOT implement `AuditService` or `SelfHealingService` — Phase 5.
- Do NOT implement a `SettingsRepository` — the only `Settings` read in §1.6 (grace period
  for soft-delete) is done inline with a Dapper one-liner and a hardcoded fallback of 30 days.
- Do NOT implement brain mirroring to tails — Phase 3.
- Do NOT wire repositories into DI or any host project — Phase 2+ concern.
- Do NOT touch `DeleteLog` table beyond what `DeleteFileAsync` and `DeleteFolderCascadeAsync`
  write into it (no `DeleteLogRepository` needed in Phase 1).

## Drift notes

None anticipated. Blueprint §16.2–§16.6 and prior plan APIs are the primary inputs.
Any deviation discovered during implementation must be escalated per the session protocol.
