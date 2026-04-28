# PR 1.5 — Brain connection, schema, and migrations

**Branch:** pr/1.5-brain-connection-schema-migrations
**Blueprint sections:** §16.1, §16.2, §16.3, §18.5, §6.8
**Dev plan section:** phase-1 §1.5

## Scope

Delivers three types and one embedded SQL resource in `FlashSkink.Core/Metadata/`:

`BrainConnectionFactory` opens an encrypted SQLCipher brain connection from a DEK: derives
the 32-byte brain key via HKDF, issues `PRAGMA key`, applies the required pragmas (WAL,
EXTRA sync, foreign keys, temp store in memory), runs `integrity_check`, then returns the
open `SqliteConnection`. Follows the reference implementation in blueprint §6.8 exactly.

`MigrationRunner` applies embedded schema migration scripts at volume open: reads the
current `SchemaVersions` max, compares to the expected version, runs each missing script
in its own transaction, records the applied version. Returns `Result`.

`V001_InitialSchema.sql` is the complete V1 brain schema from blueprint §16.2 as an
embedded resource.

`VolumeLifecycle` (in `FlashSkink.Core/Crypto/VolumeSession.cs`) is updated: its
constructor receives `BrainConnectionFactory`, `MigrationRunner`, and
`ILogger<VolumeLifecycle>`; `OpenAsync` is rewired to open a real brain connection and
run migrations instead of the §1.3 stub. The `KeyDerivationService` dependency is removed
(it moves into `BrainConnectionFactory`).

The NuGet packages `SQLitePCLRaw.bundle_e_sqlcipher` and `Dapper` are added to
`FlashSkink.Core.csproj` (both are pre-pinned in `Directory.Packages.props`).

## Files to create

- `src/FlashSkink.Core/Metadata/BrainConnectionFactory.cs` — `BrainConnectionFactory` sealed class, ~170 lines
- `src/FlashSkink.Core/Metadata/MigrationRunner.cs` — `MigrationRunner` sealed class, ~140 lines
- `src/FlashSkink.Core/Metadata/Migrations/V001_InitialSchema.sql` — full V1 schema from §16.2, ~90 lines
- `tests/FlashSkink.Tests/Metadata/BrainConnectionFactoryTests.cs` — ~160 lines
- `tests/FlashSkink.Tests/Metadata/MigrationRunnerTests.cs` — ~180 lines

## Files to modify

- `src/FlashSkink.Core/FlashSkink.Core.csproj` — add `PackageReference` for
  `SQLitePCLRaw.bundle_e_sqlcipher` and `Dapper`; add `EmbeddedResource` for the SQL file
- `src/FlashSkink.Core/Crypto/VolumeSession.cs` — update `VolumeLifecycle` constructor and
  `OpenAsync` to wire in `BrainConnectionFactory`, `MigrationRunner`, and
  `ILogger<VolumeLifecycle>`; remove `_kdf` field

## Dependencies

- NuGet: `SQLitePCLRaw.bundle_e_sqlcipher` `2.1.10` (confirmed in `Directory.Packages.props`);
  `Dapper` `2.1.35` (confirmed in `Directory.Packages.props`). No version bumps needed.
- `Microsoft.Data.Sqlite` already in `FlashSkink.Core.csproj`; no change needed.
- Project references: none new.

## Public API surface

### `FlashSkink.Core.Metadata.BrainConnectionFactory` (sealed class)

Summary intent: opens an encrypted SQLCipher brain connection from a DEK, applies all
required pragmas, validates integrity, and returns the ready-to-use connection. The caller
owns the returned connection and must dispose it.

Constructor: `BrainConnectionFactory(KeyDerivationService kdf, ILogger<BrainConnectionFactory> logger)`

- `Task<Result<SqliteConnection>> CreateAsync(string brainPath, ReadOnlyMemory<byte> dek, CancellationToken ct)`

  Derives the 32-byte brain key via HKDF before any `await`. Opens a `SqliteConnection`
  to `brainPath` (SQLite creates the file if it does not exist). Issues
  `PRAGMA key = "x'<hex>'"` to activate SQLCipher encryption. Applies
  `PRAGMA journal_mode = WAL`, `PRAGMA synchronous = EXTRA`,
  `PRAGMA foreign_keys = ON`, `PRAGMA temp_store = MEMORY`. Runs
  `PRAGMA integrity_check` and maps a non-"ok" result to `ErrorCode.DatabaseCorrupt`.
  Zeros the brain key bytes (`stackalloc`) before the first `await`. Returns
  `Result<SqliteConnection>.Ok(connection)`. On any failure, disposes the partially-
  constructed connection before returning.

  Catch ordering (§6.8 + SQLITE_NOTADB extension):
  1. `OperationCanceledException` → `ErrorCode.Cancelled`
  2. `SqliteException when SqliteErrorCode == 11` (SQLITE_CORRUPT) → `ErrorCode.DatabaseCorrupt`
  3. `SqliteException when SqliteErrorCode == 26` (SQLITE_NOTADB — wrong key, not a DB) → `ErrorCode.DatabaseCorrupt`
  4. `SqliteException when SqliteErrorCode == 5 || SqliteErrorCode == 6` (BUSY/LOCKED) → `ErrorCode.DatabaseLocked`
  5. `SqliteException` (other) → `ErrorCode.DatabaseWriteFailed`
  6. `UnauthorizedAccessException` → `ErrorCode.DatabaseWriteFailed`
  7. `IOException` → `ErrorCode.DatabaseWriteFailed`
  8. `Exception` → `ErrorCode.Unknown`

  On any failure: `connection?.Dispose()`.

---

### `FlashSkink.Core.Metadata.MigrationRunner` (sealed class)

Summary intent: applies versioned embedded SQL migration scripts to bring the brain schema
to the current expected version. Each migration runs in its own transaction. Validates that
all embedded resources exist at construction time.

Constructor: `MigrationRunner(ILogger<MigrationRunner> logger)`

Static constructor validates that every `MigrationEntry.ResourceName` resolves against the
assembly manifest, throwing `InvalidOperationException` at startup if a resource is missing.

- `Task<Result> RunAsync(SqliteConnection connection, CancellationToken ct)`

  Reads the current applied schema version from `SchemaVersions` (catches `SqliteException`
  for a fresh DB where the table does not yet exist, defaulting to version 0). If current
  version > `CurrentSchemaVersion` (= 1) returns
  `Result.Fail(ErrorCode.VolumeIncompatibleVersion, ...)`. If current version <
  `CurrentSchemaVersion`, runs each missing migration script in `Version` order; each script
  runs inside a `BEGIN TRANSACTION … COMMIT` block followed by an INSERT into
  `SchemaVersions`. On any script failure, rolls back the failing transaction, logs at
  `LogError` (version number and description only — never script contents), and returns
  `Result.Fail(ErrorCode.DatabaseMigrationFailed, ...)`.

  Catch ordering:
  1. `OperationCanceledException` → `ErrorCode.Cancelled`
  2. `SqliteException` → `ErrorCode.DatabaseMigrationFailed`
  3. `Exception` → `ErrorCode.Unknown`

---

### `FlashSkink.Core.Crypto.VolumeLifecycle` — updated constructor and OpenAsync

The existing public type is modified. No new public API is added; the constructor changes.

**Before (§1.3 stub):**
```csharp
VolumeLifecycle(KeyVault vault, KeyDerivationService kdf)
```

**After (§1.5):**
```csharp
VolumeLifecycle(KeyVault vault, BrainConnectionFactory brainFactory,
                MigrationRunner migrationRunner, ILogger<VolumeLifecycle> logger)
```

`OpenAsync(string skinkRoot, ReadOnlyMemory<byte> password, CancellationToken ct)` — now:
1. Unlocks vault at `[skinkRoot]/.flashskink/vault.bin`.
2. Calls `BrainConnectionFactory.CreateAsync(brainPath, dek, ct)` where
   `brainPath = [skinkRoot]/.flashskink/brain.db`.
3. On brain-open failure: zeros `dek`, returns the failure.
4. Calls `MigrationRunner.RunAsync(connection, ct)`.
5. On migration failure: disposes connection, zeros `dek`, returns the failure.
6. Returns `Result<VolumeSession>.Ok(new VolumeSession(dek, connection))`.
7. Logs at `LogError` when propagating any failure from steps 2–5 (Principle 27).

**`VolumeSession` dispose note (post-§1.5):** The brain key is fully derived and zeroed
inside `BrainConnectionFactory.CreateAsync` and never reaches `VolumeSession`. Therefore
`VolumeSession.DisposeAsync` zeroes only the **DEK** and disposes the **connection**.
The dev plan §1.3 phrasing "zeroes DEK + brain key" is stale; only the DEK is in scope
for `VolumeSession` from §1.5 onward. The `VolumeSession` type itself is not changed.

## Internal types

### `MigrationEntry` (private readonly record struct inside `MigrationRunner`)

- `int Version`
- `string ResourceName` — fully qualified embedded resource name
- `string Description` — short label for the SchemaVersions insert

Defined as a `private static readonly MigrationEntry[]` inside `MigrationRunner`:
```csharp
private static readonly MigrationEntry[] Migrations =
[
    new(1, "FlashSkink.Core.Metadata.Migrations.V001_InitialSchema.sql", "Initial schema")
];
```

The static constructor asserts `Migrations` is monotonically increasing on `Version` (via
`Debug.Assert` or a startup exception) and validates every `ResourceName` exists in the
assembly manifest (`Assembly.GetManifestResourceStream(name) != null`). This catches a
missing `EmbeddedResource` element at startup rather than at first vault open.

## Method-body contracts

### `BrainConnectionFactory.CreateAsync`

```
1.  ct.ThrowIfCancellationRequested() — early check before any allocation.

2.  Derive brain key (before any await — Principle 20):
      Span<byte> brainKeySpan = stackalloc byte[32];
      var brainKeyResult = _kdf.DeriveBrainKey(dek.Span, brainKeySpan);
      if (!brainKeyResult.Success) return Result<SqliteConnection>.Fail(brainKeyResult.Error!);

3.  Build PRAGMA key string (heap allocation, ephemeral — see Drift Note 6):
      var pragmaKey = $"PRAGMA key = \"x'{Convert.ToHexString(brainKeySpan)}'\"";

4.  Zero brain key bytes:
      CryptographicOperations.ZeroMemory(brainKeySpan);
    (Stackalloc zeroed before first await — Principle 20 + 31.)

5.  Open connection:
      connection = new SqliteConnection($"Data Source={brainPath}");
      await connection.OpenAsync(ct);

6.  Issue PRAGMA key — must not be cancelled mid-flight (Principle 17):
      using var keyCmd = connection.CreateCommand();
      keyCmd.CommandText = pragmaKey;
      await keyCmd.ExecuteNonQueryAsync(CancellationToken.None);

7.  Apply remaining pragmas (each via connection.CreateCommand(), awaited with ct):
      PRAGMA journal_mode = WAL
      PRAGMA synchronous = EXTRA
      PRAGMA foreign_keys = ON
      PRAGMA temp_store = MEMORY

8.  Run integrity_check:
      using var integrityCmd = connection.CreateCommand();
      integrityCmd.CommandText = "PRAGMA integrity_check";
      var integrityResult = (string?)(await integrityCmd.ExecuteScalarAsync(ct));
      if (integrityResult != "ok")
      {
          _logger.LogError("PRAGMA integrity_check returned {Result} for {BrainPath}",
              integrityResult, brainPath);  // no key material in log
          connection.Dispose();
          return Result<SqliteConnection>.Fail(ErrorCode.DatabaseCorrupt,
              $"PRAGMA integrity_check returned '{integrityResult}'.");
      }

9.  return Result<SqliteConnection>.Ok(connection);
```

Catch wraps steps 5–8. On every catch: `connection?.Dispose()`, `_logger.LogError(...)`.
The PRAGMA key string (`pragmaKey`) is set to a local variable; it is never logged,
never assigned to a field, and becomes GC-eligible after step 6 (Principle 26).

### `MigrationRunner.RunAsync`

```
1.  ct.ThrowIfCancellationRequested().

2.  Get current version (fresh DB has no SchemaVersions table):
      int currentVersion;
      try
      {
          using var cmd = connection.CreateCommand();
          cmd.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersions";
          // COALESCE guarantees non-null; the ! suppression is safe.
          currentVersion = (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
      }
      catch (SqliteException) { currentVersion = 0; }

3.  If currentVersion > CurrentSchemaVersion:
      return Result.Fail(ErrorCode.VolumeIncompatibleVersion,
          $"Brain schema v{currentVersion} is newer than this build (v{CurrentSchemaVersion}).");

4.  For each MigrationEntry in Migrations where entry.Version > currentVersion
    (array is monotonically ordered by Version — asserted in static constructor):
      a.  Load SQL:
            using var stream = typeof(MigrationRunner).Assembly
                .GetManifestResourceStream(entry.ResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded migration not found: {entry.ResourceName}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);
      b.  Begin transaction:
            using var tx = connection.BeginTransaction();
      c.  Execute the SQL script:
            using var scriptCmd = connection.CreateCommand();
            scriptCmd.Transaction = tx;
            scriptCmd.CommandText = sql;
            await scriptCmd.ExecuteNonQueryAsync(ct);
      d.  Insert SchemaVersions row:
            using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText =
                "INSERT INTO SchemaVersions (Version, AppliedUtc, Description) " +
                "VALUES (@v, @ts, @desc)";
            insertCmd.Parameters.AddWithValue("@v", entry.Version);
            insertCmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
            insertCmd.Parameters.AddWithValue("@desc", entry.Description);
            await insertCmd.ExecuteNonQueryAsync(ct);
      e.  Commit: tx.Commit();
      f.  Log success: _logger.LogInformation(
              "Applied brain migration v{Version} ({Description})",
              entry.Version, entry.Description);
    On any exception in 4a–4e:
      tx.Rollback();  // passed CancellationToken.None — compensation must not be cancelled
      _logger.LogError("Migration v{Version} ({Description}) failed", entry.Version, entry.Description);
      return Result.Fail(ErrorCode.DatabaseMigrationFailed, $"Migration v{entry.Version} failed.", ex);

5.  return Result.Ok().
```

Outer catch (wraps the entire method):
1. `OperationCanceledException` → `ErrorCode.Cancelled`, logged at `LogInformation`
2. `SqliteException` → `ErrorCode.DatabaseMigrationFailed`, logged at `LogError`
3. `Exception` → `ErrorCode.Unknown`, logged at `LogError`

## Integration points

From prior PRs:
- `FlashSkink.Core.Crypto.KeyDerivationService.DeriveBrainKey(ReadOnlySpan<byte> dek, Span<byte> destination) → Result`
- `FlashSkink.Core.Crypto.KeyVault.UnlockAsync(string vaultPath, ReadOnlyMemory<byte> password, CancellationToken ct) → Task<Result<byte[]>>`
- `FlashSkink.Core.Abstractions.Results.Result`, `Result<T>`, `ErrorCode`, `ErrorContext`
- `FlashSkink.Core.Crypto.VolumeSession` (internal constructor: `(byte[] dek, SqliteConnection? brainConnection)`)

BCL / NuGet:
- `Microsoft.Data.Sqlite.SqliteConnection` — `new SqliteConnection(string)`, `OpenAsync(ct)`, `CreateCommand()`, `BeginTransaction()`
- `System.Security.Cryptography.CryptographicOperations.ZeroMemory(Span<byte>)`
- `Microsoft.Extensions.Logging.ILogger<T>` — logging only; no Serilog in Core (Principle 28)
- `System.Reflection.Assembly.GetManifestResourceStream(string)` — loading SQL scripts
- `Dapper` — registered here for §1.6 use; not used in §1.5 code

## Principles touched

- Principle 1 (Core never throws across its public API)
- Principle 13 (`CancellationToken ct` last, always present on async methods)
- Principle 14 (`OperationCanceledException` first catch on all async methods)
- Principle 15 (no bare single `catch (Exception)` — granular hierarchy per §6.8)
- Principle 16 (every failure path disposes — `SqliteConnection` disposed in every catch)
- Principle 17 (`CancellationToken.None` literal — PRAGMA key step; `tx.Rollback()` compensation in `MigrationRunner`)
- Principle 20 (`stackalloc` never crosses `await` — brain key `stackalloc` zeroed before first `await`)
- Principle 26 (logging never contains secrets — PRAGMA key string never logged; brain key bytes never in ErrorContext)
- Principle 27 (Core logs internally at `Result.Fail` construction sites; `VolumeLifecycle` logs the propagated ErrorContext)
- Principle 28 (Core depends only on `Microsoft.Extensions.Logging.Abstractions`)
- Principle 31 (keys zeroed — `brainKeySpan` zeroed via `CryptographicOperations.ZeroMemory` before first `await`)

## Test spec

### `tests/FlashSkink.Tests/Metadata/BrainConnectionFactoryTests.cs`

**Class: `BrainConnectionFactoryTests`**

Uses a `TempDirectory` helper (same pattern as `KeyVaultTests`). A 32-byte all-zeros `dek`
is the standard test DEK. All tests require `SQLitePCLRaw.bundle_e_sqlcipher` (available
transitively via the `FlashSkink.Core` project reference).

- `CreateAsync_WithValidDek_ReturnsOpenConnection` — creates a temp brain.db path, calls
  `CreateAsync` with 32-byte zero DEK; asserts `Result.Success == true` and
  `result.Value!.State == ConnectionState.Open`.
- `CreateAsync_WithValidDek_IntegrityCheckPasses` — after `CreateAsync`, run
  `PRAGMA integrity_check` on the returned connection; assert result == "ok".
- `CreateAsync_Cancelled_ReturnsCancelled` — pass an already-cancelled `CancellationToken`;
  assert `ErrorCode.Cancelled`.
- `CreateAsync_WithCorruptFile_ReturnsDatabaseCorrupt` — write 512 bytes of random noise
  to the brain.db path, call `CreateAsync`; assert `ErrorCode.DatabaseCorrupt`.
  (SQLCipher returns SQLITE_NOTADB (26) or SQLITE_CORRUPT (11) on unreadable files;
  both map to `DatabaseCorrupt` via the §1.5 catch ladder.)
- `CreateAsync_ThenReopen_WithSameDek_Succeeds` — create a brain, dispose connection,
  call `CreateAsync` again with the same DEK; assert success. Verifies that a previously
  encrypted database can be reopened.
- `CreateAsync_ThenReopen_WithDifferentDek_ReturnsDatabaseCorrupt` — create brain with
  DEK-A (all zeros), dispose, try to open with DEK-B (all ones); assert
  `ErrorCode.DatabaseCorrupt`. (Wrong key → SQLITE_NOTADB (26) → `DatabaseCorrupt`.)
- `[Trait("Category","Integration")] CreateAsync_EncryptionIsActive_FileBytesAreNotPlaintext`
  — create brain with valid DEK, insert a row via the connection, dispose, read raw file
  bytes, assert that the bytes do NOT begin with the plain SQLite header magic
  `"SQLite format 3\000"`. Verifies SQLCipher encryption is active.

---

### `tests/FlashSkink.Tests/Metadata/MigrationRunnerTests.cs`

**Class: `MigrationRunnerTests`**

Tests use a plain (unencrypted) in-process `SqliteConnection` for speed — no SQLCipher
needed. `MigrationRunner` receives an already-open connection and doesn't know about
encryption.

Setup helper: `CreateInMemoryConnection()` — returns an open
`SqliteConnection("Data Source=:memory:")` with foreign keys enabled.

- `RunAsync_OnFreshDatabase_CreatesAllTables` — call `RunAsync` on a fresh in-memory DB;
  assert each V1 table exists in `sqlite_master`. Tables to check: `SchemaVersions`,
  `Files`, `Blobs`, `Providers`, `TailUploads`, `UploadSessions`, `WAL`,
  `BackgroundFailures`, `ActivityLog`, `DeleteLog`, `Settings`.
- `RunAsync_OnFreshDatabase_SetsSchemaVersionToOne` — after `RunAsync`, query
  `SELECT MAX(Version) FROM SchemaVersions`; assert == 1.
- `RunAsync_IsIdempotent` — call `RunAsync` twice on the same in-memory DB; assert both
  return `Result.Success == true` and `MAX(Version)` remains 1 (second call is a no-op).
- `RunAsync_Cancelled_ReturnsCancelled` — pass already-cancelled `CancellationToken`;
  assert `ErrorCode.Cancelled`.
- `RunAsync_OnSchemaNewerThanBuild_ReturnsVolumeIncompatibleVersion` — manually insert a
  `SchemaVersions` row with `Version = 999` into the in-memory DB, then call `RunAsync`;
  assert `ErrorCode.VolumeIncompatibleVersion`.
- `RunAsync_CreatesForeignKeyConstraints` — after migration, enable foreign keys
  (`PRAGMA foreign_keys = ON` on the in-memory connection), then attempt to INSERT a
  `Files` row referencing a non-existent `ParentID`; assert the INSERT throws
  `SqliteException` (verifies foreign key constraints were created).
- `RunAsync_UniqueIndexOnFilesParentName_IsEnforced` — after migration, insert two `Files`
  rows with the same `ParentID` (NULL) and the same `Name`; assert the second insert throws
  `SqliteException` (verifies the `IX_Files_Parent_Name` unique index).
- `EmbeddedResource_V001_IsFoundInAssemblyManifest` — calls
  `typeof(MigrationRunner).Assembly.GetManifestResourceNames()`, asserts that
  `"FlashSkink.Core.Metadata.Migrations.V001_InitialSchema.sql"` is present. Catches the
  class of `.csproj` misconfiguration (missing `EmbeddedResource` element) as a unit test
  failure rather than a runtime mystery.

---

### Existing tests

`VolumeSessionTests.cs` and `KeyVaultTests.cs` do not construct `VolumeLifecycle` directly
and require no changes. All existing tests must remain green.

## Acceptance criteria

- [ ] Builds with zero warnings on all targets (`dotnet build --warnaserror`)
- [ ] All new tests pass
- [ ] No existing tests break
- [ ] `integrity_check` is run inside `CreateAsync`; non-"ok" maps to `ErrorCode.DatabaseCorrupt`
- [ ] `SQLITE_NOTADB` (26) is in the catch ladder and maps to `ErrorCode.DatabaseCorrupt`
- [ ] `brainKeySpan` (stackalloc) zeroed via `CryptographicOperations.ZeroMemory` before the first `await` in `CreateAsync`
- [ ] PRAGMA key string is never logged or placed in `ErrorContext`
- [ ] All async methods have `CancellationToken ct` as last parameter
- [ ] `OperationCanceledException` is the first catch on all async methods
- [ ] `SqliteConnection` is disposed on every failure path in `CreateAsync`
- [ ] `MigrationRunner` static constructor validates all embedded resources exist
- [ ] `MigrationRunner.RunAsync` is idempotent (second call on already-migrated DB returns `Result.Ok()`)
- [ ] `V001_InitialSchema.sql` is registered as an `EmbeddedResource` in `FlashSkink.Core.csproj`
- [ ] `VolumeLifecycle.OpenAsync` uses the real `BrainConnectionFactory` and `MigrationRunner`

## Line-of-code budget

- `src/FlashSkink.Core/Metadata/BrainConnectionFactory.cs` — ~170 lines
- `src/FlashSkink.Core/Metadata/MigrationRunner.cs` — ~140 lines
- `src/FlashSkink.Core/Metadata/Migrations/V001_InitialSchema.sql` — ~90 lines
- `src/FlashSkink.Core/Crypto/VolumeSession.cs` (modifications only) — net delta ~+20 lines
- `tests/FlashSkink.Tests/Metadata/BrainConnectionFactoryTests.cs` — ~160 lines
- `tests/FlashSkink.Tests/Metadata/MigrationRunnerTests.cs` — ~180 lines
- Total: ~420 lines non-test, ~340 lines test

## Non-goals

- Do NOT implement the pre-migration backup file copy (§16.3 step 3a). See Drift Note 5.
- Do NOT add any brain repository methods — those are §1.6.
- Do NOT use Dapper in `BrainConnectionFactory` or `MigrationRunner` — Dapper is registered here for §1.6 use; these two types use raw `SqliteCommand` only.
- Do NOT insert initial Settings rows (`GracePeriodDays`, `AuditIntervalHours`, `AppVersion`, `VolumeCreatedUtc`) in V001. See Drift Note 4.
- Do NOT implement single-instance locking, USB removal handling, or `VolumeLifecycle.CreateAsync` — all Phase 2+.
- Do NOT update `docs/error-handling.md` — deferred to §1.6 once the full Phase 1 picture is complete.

## Drift notes

1. **Schema-version source: `SchemaVersions` table vs `Settings["SchemaVersion"]`.**
   Dev plan §1.5 says *"reads `Settings["SchemaVersion"]` (defaulting to 0)"*. Blueprint
   §16.3 says *"read Settings["AppVersion"] **and** SchemaVersions max(Version)"* and
   §16.2 defines no `SchemaVersion` Settings row. This PR correctly reads
   `SchemaVersions`. Blueprint wins per CLAUDE.md authority ordering.

2. **Migration failure error code: `DatabaseMigrationFailed` vs `DatabaseWriteFailed`.**
   Dev plan §1.5 says `ErrorCode.DatabaseWriteFailed` on migration failure. This PR uses
   `ErrorCode.DatabaseMigrationFailed`, which is in the §1.1 enum, is a more specific
   discriminant, and allows callers to distinguish a migration failure from a general write
   failure. Defensible deviation.

3. **NuGet package name: `bundle_e_sqlcipher` vs `bundle_sqlcipher`.**
   Dev plan §1.5 says `SQLitePCLRaw.bundle_sqlcipher`. `Directory.Packages.props` pins
   `SQLitePCLRaw.bundle_e_sqlcipher` (the enhanced-extensions build, version 2.1.10).
   The plan uses the `_e_` variant as pinned. Dev plan is stale.

4. **Initial Settings rows deferred from V001.**
   Dev plan §1.5 says V001 includes *"initial `Settings` rows"*. Blueprint §16.2 comments
   indicate those rows are *"set at volume creation"* (a runtime event, not schema creation).
   The static rows (`GracePeriodDays`, `AuditIntervalHours`, `AppVersion`) could be in V001
   without runtime input, but `VolumeCreatedUtc` requires a runtime timestamp. To keep V001
   purely structural and avoid special-casing, all initial Settings inserts are deferred to
   the Phase 2 `VolumeLifecycle.CreateAsync`. The `MigrationRunner` does not read any
   Settings value, so no downstream breakage.

5. **Pre-migration backup deferred.**
   Dev plan §1.5 says *"the pre-migration backup step (§16.3) is the caller's
   responsibility at the volume-open level (implemented in `VolumeLifecycle.OpenAsync`,
   §1.3)"*. This PR defers the backup entirely. Justification: V0→V1 always runs on a
   freshly created database with no user data; there is nothing to back up. The backup
   becomes critical at V1→V2+. It will be added in the first PR that introduces a V2
   migration script.

6. **PRAGMA key hex string cannot be zeroed in managed C#.**
   Dev plan §1.5 says *"The hex string is cleared (overwritten with zeros) immediately
   after the pragma executes."* Blueprint §18.5 reference implementation also implies this.
   In managed C#, `Convert.ToHexString` returns an immutable `string`; it cannot be zeroed
   in place without unsafe code. This PR mitigates by: (a) zeroing the source
   `brainKeySpan` (stackalloc) before the first await (Principles 20 + 31); (b) never
   logging or storing the PRAGMA string in any field, `ErrorContext`, or log message
   (Principle 26); (c) treating the PRAGMA `CommandText` string as an ephemeral heap
   allocation that becomes GC-eligible immediately after step 6. The blueprint's reference
   implementation accepts this same constraint.
