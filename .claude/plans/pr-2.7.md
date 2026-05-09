# PR 2.7 — FlashSkinkVolume public API (file + folder operations)

**Branch:** pr/2.7-volume-read-file-async
**Blueprint sections:** §11, §11.1, §13.1, §16.5, §16.6
**Dev plan section:** phase-2 §2.7

## Scope

Promotes the §1.3 `VolumeLifecycle` skeleton into the full `FlashSkinkVolume` public
API.  Adds static factory methods `CreateAsync` / `OpenAsync` that assemble the entire
object graph (brain connection, crypto services, repositories) without a DI container.
Adds twelve public file/folder methods — delegating to `WritePipeline`, `ReadPipeline`,
and `FileRepository` — all serialised through a `SemaphoreSlim(1, 1)` gate.  Declares
three events (`UsbRemoved`, `UsbReinserted`, `TailStatusChanged`) whose raisers arrive
in later phases.  Two EventArgs types are created in `Core.Abstractions`.

## Files to create

- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — main public class; factory
  methods + twelve public methods + three events + disposal; ~560 lines
- `src/FlashSkink.Core/Orchestration/VolumeCreationOptions.cs` — parameter object
  carrying optional services for `CreateAsync` / `OpenAsync`; ~20 lines
- `src/FlashSkink.Core.Abstractions/Models/UsbRemovedEventArgs.cs` — EventArgs for USB
  removal notification; ~15 lines
- `src/FlashSkink.Core.Abstractions/Models/TailStatusChangedEventArgs.cs` — EventArgs
  for tail-health change notification; ~20 lines
- `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeTests.cs` — xUnit test class;
  ~850 lines

## Files to modify

None.  All repository methods needed by this PR exist in `FileRepository` already
(confirmed by reading the full source in §2.6 research).

## Dependencies

- NuGet: none new
- Project references: none new (`FlashSkink.Core` already references
  `FlashSkink.Core.Abstractions`)

## Public API surface

### `FlashSkink.Core.Abstractions.Models.UsbRemovedEventArgs` (sealed class : EventArgs)
Summary intent: EventArgs raised when the skink USB device is removed while a volume is
open; carries the skink root path so subscribers can surface a recovery prompt.

- `string SkinkRoot { get; }`
- `UsbRemovedEventArgs(string skinkRoot)`

---

### `FlashSkink.Core.Abstractions.Models.TailStatusChangedEventArgs` (sealed class : EventArgs)
Summary intent: EventArgs raised when a tail provider's health state changes; carries
provider ID and new health snapshot.

- `string ProviderId { get; }`
- `ProviderHealth Health { get; }`
- `TailStatusChangedEventArgs(string providerId, ProviderHealth health)`

---

### `FlashSkink.Core.Orchestration.VolumeCreationOptions` (sealed class)
Summary intent: Optional services injected into `FlashSkinkVolume` factory methods;
required for logging and notifications; defaults to NullLoggerFactory and a no-op bus
only in tests.

- `ILoggerFactory LoggerFactory { get; init; }`
- `INotificationBus NotificationBus { get; init; }`
- `RecyclableMemoryStreamManager? StreamManager { get; init; }` — null → factory
  creates one internally

---

### `FlashSkink.Core.Orchestration.FlashSkinkVolume` (sealed class : IAsyncDisposable)
Summary intent: Root public API for all file and folder operations on an open
FlashSkink volume; single-writer-serialised; constructed via static factory methods
only.

#### Events (declared; raisers arrive in later phases)

- `event EventHandler<UsbRemovedEventArgs>? UsbRemoved`
- `event EventHandler<UsbRemovedEventArgs>? UsbReinserted`
- `event EventHandler<TailStatusChangedEventArgs>? TailStatusChanged`

#### Static factory methods

```csharp
public static Task<Result<FlashSkinkVolume>> CreateAsync(
    string skinkRoot,
    string password,
    VolumeCreationOptions options,
    CancellationToken ct = default)
```
Summary intent: Creates a new volume at `skinkRoot` — initialises the brain schema,
generates a mnemonic, derives the KEK, creates the vault, opens a brain connection, and
returns an open `FlashSkinkVolume`.

```csharp
public static Task<Result<FlashSkinkVolume>> OpenAsync(
    string skinkRoot,
    string password,
    VolumeCreationOptions options,
    CancellationToken ct = default)
```
Summary intent: Opens an existing volume — unlocks the vault, derives the DEK, opens a
brain connection, and returns an open `FlashSkinkVolume`.

#### File and folder methods

Signatures match dev-plan §2.7 verbatim, including the path-vs-ID addressing rule
(line 297): user-typed input → `VirtualPath`; navigated-to-node → `FileID` /
`ParentID`; `CreateFolderAsync(name, parentId?)` is the hybrid.

```csharp
public Task<Result<WriteReceipt>> WriteFileAsync(
    Stream source,
    string virtualPath,
    CancellationToken ct = default)

public Task<Result> ReadFileAsync(
    string virtualPath,
    Stream destination,
    CancellationToken ct = default)

public Task<Result> DeleteFileAsync(
    string virtualPath,
    CancellationToken ct = default)

public Task<Result<string>> CreateFolderAsync(
    string name,
    string? parentId,
    CancellationToken ct = default)

public Task<Result> DeleteFolderAsync(
    string folderId,
    bool confirmed,
    CancellationToken ct = default)

public Task<Result> RenameFolderAsync(
    string folderId,
    string newName,
    CancellationToken ct = default)

public Task<Result> MoveAsync(
    string fileId,
    string? newParentId,
    CancellationToken ct = default)

public Task<Result<IReadOnlyList<VolumeFile>>> ListChildrenAsync(
    string? parentId,
    CancellationToken ct = default)

public Task<Result<IReadOnlyList<VolumeFile>>> ListFilesAsync(
    string virtualPathPrefix,
    CancellationToken ct = default)

public Task<Result> ChangePasswordAsync(
    string currentPassword,
    string newPassword,
    CancellationToken ct = default)

public Task<Result> RestoreFromGracePeriodAsync(
    string blobId,
    string virtualPath,
    CancellationToken ct = default)

public ValueTask DisposeAsync()
```

## Internal types

### `FlashSkink.Core.Orchestration.FlashSkinkVolume` — private members

- `SemaphoreSlim _gate` — `new SemaphoreSlim(1, 1)`; held by every public method for
  the duration of the call.
- `VolumeSession _session` — holds DEK (`byte[]`) and open `SqliteConnection`; owned by
  the volume; disposed in `DisposeAsync`.
- `VolumeContext _context` — parameter object passed to pipeline and repository calls;
  built from `_session` + injected services; `IDisposable`, disposed in `DisposeAsync`.
- `WritePipeline _writePipeline` — stateless; created once in factory methods.
- `ReadPipeline _readPipeline` — stateless; created once in factory methods.
- `VolumeLifecycle _lifecycle` — used by `OpenAsync` to chain vault-unlock →
  brain-open → migrations.
- `KeyVault _keyVault` — used by `CreateAsync` and `ChangePasswordAsync`.
- `string _vaultPath` — computed from `skinkRoot` in factory methods.
- `int _disposed` — `Interlocked.Exchange` flag for idempotent disposal.

## Method-body contracts

### `CreateAsync`

Preconditions: `skinkRoot` is a non-null path to an existing directory on the skink;
`password` non-null non-empty; `options` non-null.

`vaultPath = Path.Combine(skinkRoot, ".flashskink", "vault.bin")`;
`brainPath = Path.Combine(skinkRoot, ".flashskink", "brain.db")` — matches the layout
already used by `VolumeLifecycle.OpenAsync` (`VolumeSession.cs:92`).

Steps (each wrapped in `try/catch`, failures return `Result.Fail`):
1. `Directory.CreateDirectory(Path.Combine(skinkRoot, ".flashskink"))` (idempotent);
   also create `.flashskink/staging/` and fsync per dev-plan §2.5 line 162.
2. Convert `password` to UTF-8 bytes into a rented byte array; wrap as
   `ReadOnlyMemory<byte>`.
3. Call `_keyVault.CreateAsync(vaultPath, passwordMemory, ct)` → `Result<byte[]>` (DEK).
4. Call `_brainFactory.CreateAsync(brainPath, dek, ct)` → `Result<SqliteConnection>`.
5. Call `_migrationRunner.RunAsync(connection, ct)` — applies migration 001 and seeds
   the `SchemaVersions` row automatically (`MigrationRunner.cs:160`). The dev-plan
   "seed `SchemaVersions` with V1" requirement (line 273) is satisfied here, no
   manual insert.
6. Call `MnemonicService.Generate()` → `Result<string[]>`.
7. **Seed initial `Settings` rows** in a single transaction on the brain connection
   (dev-plan §2.7 line 273 — load-bearing because `FileRepository.ReadGracePeriodDaysAsync`
   already reads `GracePeriodDays`, `FileRepository.cs:54`). Rows: `GracePeriodDays = "30"`,
   `AuditIntervalHours = "168"`, `VolumeCreatedUtc = DateTime.UtcNow.ToString("O")`,
   `AppVersion = typeof(FlashSkinkVolume).Assembly.GetName().Version!.ToString()`,
   `RecoveryPhrase = string.Join(" ", words)`. Use `INSERT OR REPLACE INTO Settings
   (Key, Value) VALUES (@Key, @Value)`.
8. Construct repositories (`BlobRepository`, `WalRepository`, `ActivityLogRepository`,
   `FileRepository`), pipelines (`WritePipeline`, `ReadPipeline`), `VolumeContext`,
   `VolumeSession`.
9. Zero the UTF-8 password byte array (`CryptographicOperations.ZeroMemory`).
10. Return `Result.Ok(new FlashSkinkVolume(...))`.

Compensation paths use `CancellationToken.None` as a literal (Principle 17). On any
failure after step 3 (vault created) but before step 8: attempt
`File.Delete(vaultPath)` in the catch block. On any failure after step 4 (connection
open): dispose the connection. Always zero the UTF-8 password bytes in every catch
path (Principle 31).

Cancellation: `OperationCanceledException` caught first → `ErrorCode.Cancelled`.

### `OpenAsync`

Preconditions: `skinkRoot` is a non-null path; volume must already exist (vault and
brain files present).

Steps:
1. Convert `password` to UTF-8 bytes; wrap as `ReadOnlyMemory<byte>`.
2. Call `_lifecycle.OpenAsync(skinkRoot, passwordMemory, ct)` → `Result<VolumeSession>`
   (uses the existing `VolumeLifecycle` orchestrator at `VolumeSession.cs:64`, which
   already does vault-unlock → brain-open → migrations → return session).
3. Construct repositories, pipelines, `VolumeContext` from the session's connection
   and DEK.
4. Zero UTF-8 password bytes.
5. Return `Result.Ok(new FlashSkinkVolume(...))`.

On any failure after step 2: `await session.DisposeAsync()` (zeroes DEK, closes
connection). Always zero password bytes. `OperationCanceledException` caught first.

### `WriteFileAsync`

1. Acquire `_gate` via `WaitAsync(ct)`.
2. Delegate to `_writePipeline.ExecuteAsync(source, virtualPath, _context, ct)`.
3. Release gate in `finally`.
Returns `Result<WriteReceipt>`.

### `ReadFileAsync`

1. Acquire `_gate`.
2. Delegate to `_readPipeline.ExecuteAsync(virtualPath, destination, _context, ct)`.
3. Release gate in `finally`.
Returns `Result`.

### `DeleteFileAsync`

1. Acquire `_gate`.
2. Call `_context.Files.GetByVirtualPathAsync(virtualPath, ct)` → look up the file row.
3. If not found: return `Result.Fail(ErrorCode.FileNotFound, ...)` — matches the code
   used internally by `FileRepository.DeleteFileAsync` (`FileRepository.cs:483`).
4. Call `_context.Files.DeleteFileAsync(volumeFile.FileId, ct)`.
5. Release gate in `finally`.

### `CreateFolderAsync`

Single-segment folder creation under a navigated parent (path-vs-ID hybrid; dev-plan
line 297). Does **not** call `EnsureFolderPathAsync` — that is for path-based
walk-and-create and may walk multiple segments.

1. Acquire `_gate`.
2. Resolve `parentVirtualPath`: if `parentId is null` → empty; else call
   `_context.Files.GetByIdAsync(parentId, ct)` and read `Value.VirtualPath`.
3. Construct `VolumeFile` with `IsFolder = true`, `Name = name`, `ParentId = parentId`,
   `VirtualPath = parentVirtualPath.Length == 0 ? name : parentVirtualPath + "/" + name`,
   timestamps = `DateTime.UtcNow`, `FileId = Guid.NewGuid().ToString()`.
4. Call `_context.Files.InsertAsync(folder, ct)`. UNIQUE constraint violation surfaces
   as `ErrorCode.PathConflict` from the repository (dev-plan line 278).
5. Release gate in `finally`.
Returns `Result<string>` carrying the new folder's `FileId`.

### `DeleteFolderAsync`

1. Acquire `_gate`.
2. Delegate to `_context.Files.DeleteFolderCascadeAsync(folderId, confirmed, ct)`.
3. Release gate in `finally`.

`DeleteFolderCascadeAsync` internally returns `Result.Fail(ErrorCode.ConfirmationRequired)`
when `confirmed == false && childCount > 0`; volume surfaces this result unchanged.

### `RenameFolderAsync`

1. Acquire `_gate`.
2. Delegate to `_context.Files.RenameFolderAsync(folderId, newName, ct)`.
3. Release gate in `finally`.

### `MoveAsync`

1. Acquire `_gate`.
2. Delegate to `_context.Files.MoveAsync(fileId, newParentId, ct)`. The repository
   performs cycle detection and returns `ErrorCode.CyclicMoveDetected` when the
   target is a descendant (`FileRepository.cs:850`); `newParentId = null` is valid
   and means "move to root".
3. Release gate in `finally`.

### `ListChildrenAsync`

1. Acquire `_gate`. (The repository signature is
   `Task<Result<IReadOnlyList<VolumeFile>>>`, awaited normally — the earlier
   `IAsyncEnumerable` rationale was incorrect; gate works fine here.)
2. Delegate to `_context.Files.ListChildrenAsync(parentId, ct)`.
3. Release gate in `finally`.

### `ListFilesAsync`

1. Acquire `_gate`.
2. Delegate to `_context.Files.ListFilesAsync(virtualPathPrefix, ct)` — the repo uses
   a `LIKE prefix || '%'` scan, which already handles "all files under a path"
   (`FileRepository.cs:267`); no `recursive` parameter exists or is needed.
3. Release gate in `finally`.

### `ChangePasswordAsync`

1. Acquire `_gate`.
2. Convert `currentPassword` and `newPassword` to UTF-8 byte arrays.
3. Call `_keyVault.ChangePasswordAsync(_vaultPath, currentMem, newMem, ct)`.
4. Zero both byte arrays in `finally` (Principle 31).
5. Release gate in `finally`.

### `RestoreFromGracePeriodAsync`

1. Acquire `_gate`.
2. Delegate to `_context.Files.RestoreFromGracePeriodAsync(blobId, virtualPath, ct)`.
3. Release gate in `finally`.

### `DisposeAsync`

Acquires the gate before tearing down to avoid racing in-flight operations
(dev-plan §2.7 line 295).

1. If `Interlocked.Exchange(ref _disposed, 1) != 0` → return early (idempotent).
2. `await _gate.WaitAsync(CancellationToken.None)` — `CancellationToken.None`
   spelled out as a literal (Principle 17; disposal is a compensation path that
   must not be cancelled).
3. In `try` / `finally`:
    - Dispose `_context` (disposes `IncrementalHash`, `CompressionService`).
    - `await _session.DisposeAsync()` (zeroes DEK, disposes `SqliteConnection`).
    - In `finally`: `_gate.Release(); _gate.Dispose();`

## Integration points

```csharp
// VolumeLifecycle (§1.3, src/.../Crypto/VolumeSession.cs:64)
VolumeLifecycle(KeyVault vault, BrainConnectionFactory brainFactory,
    MigrationRunner migrationRunner, ILogger<VolumeLifecycle> logger)
Task<Result<VolumeSession>> OpenAsync(
    string skinkRoot, ReadOnlyMemory<byte> password, CancellationToken ct)

// BrainConnectionFactory (§1.3 / §2.3) — used directly inside CreateAsync
BrainConnectionFactory(KeyDerivationService kdf, ILogger<BrainConnectionFactory> logger)
Task<Result<SqliteConnection>> CreateAsync(
    string brainPath, ReadOnlyMemory<byte> dek, CancellationToken ct)

// MigrationRunner (§1.3) — runs migration 001 and seeds SchemaVersions automatically
MigrationRunner(ILogger<MigrationRunner> logger)
Task<Result> RunAsync(SqliteConnection connection, CancellationToken ct)

// KeyVault (§2.3)
KeyVault(KeyDerivationService kdf, MnemonicService mnemonic)
Task<Result<byte[]>> CreateAsync(
    string vaultPath, ReadOnlyMemory<byte> password, CancellationToken ct)
Task<Result<byte[]>> UnlockAsync(
    string vaultPath, ReadOnlyMemory<byte> password, CancellationToken ct)
Task<Result> ChangePasswordAsync(
    string vaultPath, ReadOnlyMemory<byte> current,
    ReadOnlyMemory<byte> newPassword, CancellationToken ct)

// VolumeSession (§1.3, src/.../Crypto/VolumeSession.cs:13)
internal VolumeSession(byte[] dek, SqliteConnection? brainConnection)
public byte[] Dek { get; }
public SqliteConnection? BrainConnection { get; }
ValueTask DisposeAsync()   // zeroes DEK, disposes connection

// VolumeContext (§2.5)
public VolumeContext(
    SqliteConnection brainConnection, ReadOnlyMemory<byte> dek,
    string skinkRoot, IncrementalHash sha256,
    CryptoPipeline crypto, CompressionService compression,
    AtomicBlobWriter blobWriter, RecyclableMemoryStreamManager streamManager,
    INotificationBus notificationBus, BlobRepository blobs,
    FileRepository files, WalRepository wal,
    ActivityLogRepository activityLog)
// IDisposable — disposes IncrementalHash and CompressionService

// WritePipeline (§2.5)
WritePipeline(FileTypeService, EntropyDetector, ILoggerFactory)
Task<Result<WriteReceipt>> ExecuteAsync(
    Stream source, string virtualPath,
    VolumeContext context, CancellationToken ct)

// ReadPipeline (§2.6)
ReadPipeline(ILoggerFactory loggerFactory)
Task<Result> ExecuteAsync(
    string virtualPath, Stream destination,
    VolumeContext context, CancellationToken ct)

// FileRepository — actual signatures (verified against
// src/FlashSkink.Core/Metadata/FileRepository.cs)
Task<Result<VolumeFile?>> GetByIdAsync(string fileId, CancellationToken ct)
Task<Result<VolumeFile?>> GetByVirtualPathAsync(string virtualPath, CancellationToken ct)
Task<Result> InsertAsync(VolumeFile file, CancellationToken ct)
Task<Result<string?>> EnsureFolderPathAsync(string virtualPath, CancellationToken ct)
Task<Result> DeleteFileAsync(string fileId, CancellationToken ct)   // returns FileNotFound, not NotFound
Task<Result> DeleteFolderCascadeAsync(
    string folderId, bool confirmed, CancellationToken ct)          // ConfirmationRequired carries Metadata["ChildCount"]
Task<Result> RenameFolderAsync(
    string folderId, string newName, CancellationToken ct)
Task<Result> MoveAsync(
    string fileId, string? newParentId, CancellationToken ct)       // newParentId == null means root
Task<Result<IReadOnlyList<VolumeFile>>> ListChildrenAsync(
    string? parentId, CancellationToken ct)                         // NOT IAsyncEnumerable
Task<Result<IReadOnlyList<VolumeFile>>> ListFilesAsync(
    string virtualPathPrefix, CancellationToken ct)                 // NOT (folderId, recursive)
Task<Result> RestoreFromGracePeriodAsync(
    string blobId, string virtualPath, CancellationToken ct)

// MnemonicService (§2.3)
Result<string[]> Generate()

// Repositories (constructors needed inside factory methods)
BlobRepository(SqliteConnection connection, ILogger<BlobRepository> logger)
WalRepository(SqliteConnection connection, ILogger<WalRepository> logger)
ActivityLogRepository(SqliteConnection connection, ILogger<ActivityLogRepository> logger)
FileRepository(SqliteConnection connection, WalRepository wal,
    ILogger<FileRepository> logger)
AtomicBlobWriter(string skinkRoot, RecyclableMemoryStreamManager streamManager,
    ILogger<AtomicBlobWriter> logger)
```

## Principles touched

- Principle 1 (Core never throws across its public API — all public methods return
  `Result` / `Result<T>`)
- Principle 8 (Core holds no UI framework reference)
- Principle 12 (OS-agnostic paths — use `Path.Combine` throughout)
- Principle 13 (`CancellationToken ct` always last)
- Principle 14 (`OperationCanceledException` caught first)
- Principle 15 (bare `catch (Exception ex)` always last)
- Principle 16 (every failure path disposes partially-constructed resources)
- Principle 17 (`CancellationToken.None` as a literal in compensation paths)
- Principle 31 (keys zeroed on volume close; UTF-8 password bytes zeroed on factory
  method exit and in every catch path)

## Test spec

### `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeTests.cs`

Test class: `FlashSkinkVolumeTests`

All tests use a per-test `_skinkRoot` temp directory created in `InitializeAsync` and
deleted in `DisposeAsync` (via `IAsyncLifetime`).  A single shared `VolumeCreationOptions`
uses `NullLoggerFactory.Instance` and a `new RecordingNotificationBus()`.

**Windows file-handle hygiene.** On Windows, SQLCipher / `Microsoft.Data.Sqlite` may
briefly hold the brain file after `SqliteConnection.Dispose()` due to connection
pooling. Test `DisposeAsync` calls `SqliteConnection.ClearAllPools()` before
`Directory.Delete(_skinkRoot, recursive: true)` to avoid flakes when the OS hasn't
released the handle yet.

#### Factory tests

- `CreateAsync_NewSkinkRoot_ReturnsOpenVolume` — calls `CreateAsync`; asserts
  `Result.Success`, volume not null, vault file exists on disk, brain file exists on
  disk.
- `CreateAsync_NewSkinkRoot_GeneratesRecoveryPhrase` — creates volume, disposes;
  test opens a raw `SqliteConnection` (with the same `KeyDerivationService`-derived
  brain key obtained by re-unlocking the vault — or simply via a second
  `OpenAsync` followed by reflection-based access to `VolumeSession.BrainConnection`)
  and runs `SELECT Value FROM Settings WHERE Key = 'RecoveryPhrase'`; asserts the
  stored value `Split(' ')` yields 24 words.
- `CreateAsync_NewSkinkRoot_SeedsInitialSettings` — after `CreateAsync`, asserts
  `Settings` contains rows for `GracePeriodDays`, `AuditIntervalHours`,
  `VolumeCreatedUtc`, `AppVersion`. (Witness for dev-plan §2.7 line 273.)
- `OpenAsync_ExistingVolume_ReturnsOpenVolume` — `CreateAsync` then `DisposeAsync` then
  `OpenAsync`; asserts second open succeeds.
- `OpenAsync_WrongPassword_ReturnsFailResult` — `CreateAsync` then `DisposeAsync` then
  `OpenAsync` with wrong password; asserts `!Result.Success`.
- `OpenAsync_MissingVaultFile_ReturnsFailResult` — skinkRoot with no vault; asserts
  fail.

#### WriteFileAsync / ReadFileAsync round-trip

- `WriteFile_ThenReadFile_ProducesOriginalContent` — writes a 512-byte random byte
  stream; reads back; asserts byte-for-byte equality.
- `WriteFile_ThenReadFile_LargeFile_ProducesOriginalContent` — 4 MB payload; same
  assertion.
- `WriteFile_SamePath_Twice_SecondWriteSucceeds` — overwrites; asserts both results
  succeed and read-back returns the second payload.
- `ReadFile_NonExistentPath_ReturnsFailResult` — asserts `ErrorCode.NotFound`.

#### DeleteFileAsync

- `DeleteFile_ExistingFile_Succeeds` — write then delete then attempt read; asserts
  delete succeeds and subsequent read returns `ErrorCode.NotFound`.
- `DeleteFile_NonExistentPath_ReturnsFailResult` — asserts fail.

#### Folder operations

- `CreateFolder_AtRoot_ReturnsId` — `CreateFolderAsync("docs", parentId: null, ct)`;
  asserts result has non-empty folder ID and the row's `VirtualPath == "docs"`.
- `CreateFolder_UnderExistingParent_NestsCorrectly` — create `docs` at root, then
  `CreateFolderAsync("ref", parentId: docsId, ct)`; assert nested row's
  `VirtualPath == "docs/ref"`.
- `CreateFolder_DuplicateNameUnderSameParent_ReturnsPathConflict` — second call
  with same `(name, parentId)`; asserts `ErrorCode.PathConflict`.
- `DeleteFolder_Empty_WithoutConfirmation_Succeeds` — zero-child folder; `confirmed:
  false`; asserts success.
- `DeleteFolder_NonEmpty_WithoutConfirmation_ReturnsConfirmationRequired` — write a
  file first; `confirmed: false`; asserts `ErrorCode.ConfirmationRequired` **and**
  `result.Error.Metadata["ChildCount"]` is populated and parses to a positive int.
  (Witness for dev-plan acceptance line 349.)
- `DeleteFolder_NonEmpty_WithConfirmation_Succeeds` — write a file first; `confirmed:
  true`; asserts success; subsequent `ReadFileAsync` of a child path returns
  `ErrorCode.FileNotFound`.
- `RenameFolder_ExistingFolder_Succeeds` — create folder, rename, list parent; asserts
  new name present.
- `MoveAsync_FileToFolder_Succeeds` — write file, create target folder, move by ID;
  asserts result success.
- `MoveAsync_FolderUnderItsOwnDescendant_ReturnsCyclicMoveDetected` — create folder
  `A`, then `B` under `A`; `MoveAsync(fileId: A, newParentId: B, ct)`; asserts
  `ErrorCode.CyclicMoveDetected`. (Witness for dev-plan acceptance line 350.)
- `MoveAsync_ToRoot_Succeeds` — create folder under a parent, then
  `MoveAsync(fileId, newParentId: null, ct)`; asserts success and the row's
  `ParentID` is null.

#### ListChildrenAsync / ListFilesAsync

- `ListChildren_PopulatedFolder_ReturnsExpectedItems` — create 2 subfolders + 2 files
  directly under root; `ListChildrenAsync(parentId: null, ct)`; asserts exactly 4
  items, folders before files (per `FileRepository.cs:222` ordering).
- `ListFiles_PrefixMatch_IncludesAllNestedFiles` — write `a.txt` at root,
  `docs/b.txt`, `docs/ref/c.txt`; call `ListFilesAsync("docs", ct)`; asserts the
  result includes both `docs/b.txt` and `docs/ref/c.txt` and excludes `a.txt`.
- `ListFiles_EmptyPrefix_IncludesAll` — same setup; `ListFilesAsync("", ct)`;
  asserts all three files returned.

#### ChangePasswordAsync

- `ChangePassword_ThenOpenWithNewPassword_Succeeds` — change password, dispose, open
  with new password; asserts success.
- `ChangePassword_WrongCurrentPassword_ReturnsFailResult` — asserts fail.

#### RestoreFromGracePeriodAsync

- `RestoreFromGracePeriod_ValidBlobId_Succeeds` — write file (capturing BlobId from
  `WriteReceipt`), delete file, restore by blobId + original virtualPath; asserts
  result success and subsequent read returns original content.

#### Compression branches (end-to-end via FlashSkinkVolume)

Witnesses for dev-plan acceptance lines 340–342. Each test writes via
`WriteFileAsync`, reads back via `ReadFileAsync`, asserts byte-equality, **and**
opens a raw `SqliteConnection` to query
`SELECT Compression FROM Blobs WHERE BlobID = @BlobId` (BlobId from the
`WriteReceipt`).

- `WriteThenRead_HighlyCompressible100KB_UsesLz4Branch` — 100 KB of repeating bytes;
  asserts `Compression == "LZ4"`.
- `WriteThenRead_HighlyCompressible1MB_UsesZstdBranch` — 1 MB of repeating bytes;
  asserts `Compression == "ZSTD"`.
- `WriteThenRead_RandomBytes1MB_UsesNoCompressionBranch` — 1 MB of
  `RandomNumberGenerator.GetBytes`; asserts `Compression IS NULL`.

#### Cancellation

Witnesses for dev-plan acceptance lines 344–345.

- `WriteFileAsync_CancelledMidFlight_ReturnsCancelled` — start a write of a 16 MB
  stream wrapped in a `CancellingStream` that calls `cts.Cancel()` after the first
  read; asserts result `ErrorCode.Cancelled`. No exception escapes.
- `ReadFileAsync_CancelledMidFlight_ReturnsCancelled` — write a 16 MB file, then
  start `ReadFileAsync` into a `CancellingMemoryStream` that calls `cts.Cancel()`
  on the first `WriteAsync`; asserts `ErrorCode.Cancelled`.

#### Concurrency

- `ConcurrentReads_SerializeCorrectly` — write a 4 MB file, then fire 4 parallel
  `ReadFileAsync` tasks against the same volume; asserts all four succeed and all
  four destination buffers byte-match the original input. Witnesses the
  `_gate` invariant from dev-plan acceptance line 343 (volume-scoped
  `IncrementalHash` and shared `SqliteConnection` would corrupt without
  serialization).
- `ConcurrentWrites_SerializeCorrectly` — fires 5 parallel `WriteFileAsync` calls to
  distinct paths; asserts all 5 succeed (semaphore prevents corruption, not progress).

#### Disposal

- `DisposeAsync_Idempotent_DoesNotThrow` — calls `DisposeAsync` twice; asserts no
  exception.
- `PublicMethod_AfterDispose_ThrowsObjectDisposedException` — `WriteFileAsync`
  after dispose; asserts `ObjectDisposedException` is thrown. (Cross-cutting
  decision 4 forbids new `ErrorCode` values in Phase 2, so a hypothetical
  `ErrorCode.Disposed` is unavailable. Use-after-dispose is a programming error
  per Principle 1's carve-out for caller misuse, and `ObjectDisposedException`
  is the .NET-standard signal — consistent with `VolumeSession.Dek`'s existing
  behavior at `VolumeSession.cs:27`.)

## Acceptance criteria

- [ ] Builds with zero warnings on all targets
- [ ] All new tests pass
- [ ] No existing tests break
- [ ] `FlashSkinkVolume.CreateAsync` on a fresh temp directory produces vault + brain
  files on disk
- [ ] `Settings` table contains `GracePeriodDays`, `AuditIntervalHours`,
  `VolumeCreatedUtc`, `AppVersion`, `RecoveryPhrase` rows after `CreateAsync`
- [ ] Round-trip `WriteFileAsync` → `ReadFileAsync` reproduces original bytes
- [ ] End-to-end LZ4, Zstd, and no-gain compression branches each produce the
  expected `Blobs.Compression` value when round-tripped via
  `FlashSkinkVolume.WriteFileAsync` / `ReadFileAsync`
- [ ] `WriteFileAsync` and `ReadFileAsync` honour mid-flight cancellation
  (`ErrorCode.Cancelled`, no exception escapes)
- [ ] Four parallel `ReadFileAsync` calls on one volume all succeed (gate witness)
- [ ] `DisposeAsync` called twice does not throw
- [ ] `DeleteFolderAsync` with `confirmed: false` on a non-empty folder returns
  `ErrorCode.ConfirmationRequired` with `Metadata["ChildCount"]` populated
- [ ] `MoveAsync` of a folder under one of its own descendants returns
  `ErrorCode.CyclicMoveDetected`
- [ ] No new values added to `ErrorCode` enum (cross-cutting decision 4)

## Line-of-code budget

- `src/FlashSkink.Core/Orchestration/FlashSkinkVolume.cs` — ~560 lines (Settings
  seeding adds ~30, gate-acquired DisposeAsync adds ~5)
- `src/FlashSkink.Core/Orchestration/VolumeCreationOptions.cs` — ~20 lines
- `src/FlashSkink.Core.Abstractions/Models/UsbRemovedEventArgs.cs` — ~15 lines
- `src/FlashSkink.Core.Abstractions/Models/TailStatusChangedEventArgs.cs` — ~20 lines
- `tests/FlashSkink.Tests/Engine/FlashSkinkVolumeTests.cs` — ~850 lines (cycle test,
  ChildCount metadata assertion, three compression-branch tests, two cancellation
  tests, concurrent-read test, settings-seeding test, ListFiles prefix tests, and
  helper `CancellingStream` / `CancellingMemoryStream` types add ~200 lines)
- **Total non-test: ~615 lines; test: ~850 lines**

## Non-goals

- Do NOT implement `UsbMonitorService` or anything that raises `UsbRemoved` /
  `UsbReinserted` / `TailStatusChanged` (planned Phase 6).
- Do NOT wire `FlashSkinkVolume` into the Avalonia UI or CLI (Phase 7+).
- Do NOT add `HealthMonitorService`, audit scheduling, or upload queue processing.
- Do NOT implement mnemonic-at-rest encryption (Phase 5); plaintext words in Settings
  is the intentional V1 placeholder.
- Do NOT change `FileRepository`, `WritePipeline`, or `ReadPipeline`.
- Do NOT introduce a DI container; factory methods construct the graph directly.

## Drift notes

**Drift Note 1 — Factory method signatures vs. blueprint §11**
Blueprint §11 shows `OpenAsync(string skinkRoot, string password)` with no
infrastructure parameters.  Phase 2 has no DI container, so `ILoggerFactory` and
`INotificationBus` must be supplied directly.  Both factory methods accept a
`VolumeCreationOptions options` parameter carrying these.  This deviation is
intentional for Phase 2 and will be revisited when DI is wired in a later phase.

**Drift Note 2 — `RestoreFromGracePeriodAsync` parameter shape**
Dev-plan §2.7 specifies `(string fileId, DateTimeOffset deletedAtOrLater)` for this
method.  However, `FileRepository.RestoreFromGracePeriodAsync` takes `(string blobId,
string virtualPath)`, and the `DeleteLog` table does not store `BlobID`.  Exposing the
repository signature directly at the volume level — `(string blobId, string virtualPath)`
— avoids a schema migration and fragile timestamp matching.  The volume method signature
uses `(string blobId, string virtualPath, CancellationToken ct)` in preference to the
dev plan.  The `WriteReceipt` already carries `BlobId`, so callers can obtain it at
write time.

## Notes

**Recovery phrase storage in `Settings`.** `CreateAsync` stores the 24 BIP-39 words as
plaintext in `Settings["RecoveryPhrase"]`. Mnemonic-at-rest encryption is deferred to
Phase 5 (see Non-goals). Principle 26 forbids logging secrets and bans
`*Token`/`*Key`/`*Password`/`*Secret`/`*Mnemonic`/`*Phrase` keys in
`ErrorContext.Metadata`; the lint scope is `ErrorContext.Metadata`, not arbitrary
brain rows, so storing the value in `Settings` does not violate the rule. The value
must never be logged or surfaced through `ErrorContext.Metadata`.
