# Phase 1 — Crypto and Brain

**Status marker:** This phase follows the standard session protocol defined in `CLAUDE.md`. Each section below (§1.1 through §1.6) maps to one PR, executed via `read section 1.X of the dev plan and perform`. Gate 1 (plan approval) and Gate 2 (implementation approval) are required for every section. Sections must be executed in order — each one depends on the types established by the section before it.

---

## Goal

After Phase 1:

- The `Result`/`Result<T>`/`ErrorContext`/`ErrorCode` pattern is fully implemented in `FlashSkink.Core.Abstractions` and all public Core methods can use it from Phase 2 onward.
- A volume can be created from scratch: mnemonic generated, vault written, brain initialised with the full V1 schema.
- A volume can be unlocked (password → Argon2id → KEK → unwrap DEK → derive brain key → open SQLCipher connection) and locked (all key material zeroed).
- `CryptoPipeline` can encrypt and decrypt a byte payload with AES-256-GCM, with correct nonce, AAD, and GCM tag handling.
- `BlobHeader` can serialise and parse the versioned on-disk header format (§13.6).
- All six brain repositories are implemented and unit-tested against a real SQLCipher in-process database.
- Every test in `tests/FlashSkink.Tests/` passes on Ubuntu and Windows.
- Phase 2 (write pipeline) can start with a single prompt.

---

## Section index

| Section | Title | Deliverables |
|---|---|---|
| §1.1 | Result pattern and ErrorCode | `Result`, `Result<T>`, `ErrorContext`, `ErrorCode` in `Core.Abstractions/Results/`; comprehensive unit tests |
| §1.2 | Mnemonic service and key derivation | `MnemonicService` (BIP-39 generate/validate/recover); `KeyDerivationService` (Argon2id KDF, HKDF); tests |
| §1.3 | DEK vault and volume lifecycle | `KeyVault` (vault.bin read/write, DEK wrap/unwrap, password change, zeroing); `VolumeLifecycle` (unlock/lock skeleton); tests |
| §1.4 | Crypto pipeline and blob header | `CryptoPipeline` (AES-256-GCM encrypt/decrypt); `BlobHeader` (serialise/parse); tests including round-trip and tamper-detection |
| §1.5 | Brain connection, schema, and migrations | `BrainConnectionFactory` (SQLCipher, HKDF brain key, pragmas); `MigrationRunner`; embedded schema SQL; tests against in-process DB |
| §1.6 | Brain repositories | `FileRepository`, `BlobRepository`, `UploadQueueRepository`, `WalRepository`, `ActivityLogRepository`, `BackgroundFailureRepository`; tests |

Full implementation detail for each section lives in `.claude/plans/pr-1.X.md`, written at Gate 1 of the corresponding session. The tables below summarise the blueprint sections each PR must read and the NuGet packages it introduces — to ensure Claude reads the right source material and does not introduce unapproved dependencies.

---

## Section notes

### §1.1 — Result pattern and ErrorCode

**Blueprint sections to read:** §6 (all subsections).

**Scope summary:** Replaces the placeholder types created by Phase 0 with the full implementation. `Result` (non-generic, for void-returning operations) and `Result<T>` (generic, carrying a success value) are `readonly record struct` types. `ErrorContext` carries `Code`, `Message`, `ExceptionType`, `ExceptionMessage`, `StackTrace`, and optional string-keyed `Metadata` — the raw `Exception` object does not escape Core. `ErrorCode` is an `enum` covering every distinguishable failure mode referenced by Phase 1 through Phase 6 sections.

The plan must pre-declare the full `ErrorCode` enum from the blueprint rather than adding values piecemeal. Values needed by Phase 1 alone:

`Cancelled`, `Unknown`, `DatabaseCorrupt`, `DatabaseLocked`, `DatabaseWriteFailed`, `VaultNotFound`, `VaultCorrupt`, `VaultVersionUnsupported`, `InvalidPassword`, `InvalidMnemonic`, `CryptoFailed`, `ChecksumMismatch`, `UsbFull`, `StagingFailed`, `PathConflict`, `CyclicMoveDetected`, `ConfirmationRequired`, `VolumeIncompatibleVersion`, `SingleInstanceLockHeld`.

Later phases add to the enum in their own PRs without touching §1.1 files.

**NuGet:** None new (foundation types only).

**Key constraints:**
- Both `Result` and `Result<T>` expose a `static Fail(...)` factory that accepts `(ErrorCode, string message)` and `(ErrorCode, string message, Exception ex)` overloads. The exception overload extracts `ExceptionType`, `ExceptionMessage`, and `StackTrace` into `ErrorContext` and does not hold a reference to the `Exception`.
- `Result<T>.Ok(T value)` and `Result.Ok()` are the success factories.
- No implicit conversions. No `throw`-on-failure helpers. Callers pattern-match or use `.Success` / `.Value` / `.Error` directly.

---

### §1.2 — Mnemonic service and key derivation

**Blueprint sections to read:** §18.1, §18.2, §18.3.

**Scope summary:** Two services, both in `FlashSkink.Core/Crypto/`.

`MnemonicService` owns the BIP-39 lifecycle:
- `Generate()` — produces a 24-word mnemonic from 32 bytes of `RandomNumberGenerator.GetBytes`. Returns the words as `string[]`; the caller is responsible for displaying and clearing them.
- `Validate(string[] words)` — validates all 24 words exist in the English BIP-39 wordlist and that the checksum is correct. Returns `Result`.
- `ToSeed(string[] words)` — applies the BIP-39 seed derivation (PBKDF2-SHA512, passphrase = `""`, 2048 iterations) and returns the 64-byte seed as `byte[]`. Caller zeroes it.

`KeyDerivationService` owns the cryptographic KDF layer:
- `DeriveKek(byte[] seed, ReadOnlySpan<byte> argon2Salt, out byte[] kek)` — Argon2id with the parameters specified in §18.2 (m=19456, t=2, p=1). Takes either the mnemonic seed or a password-derived seed as input.
- `DeriveKekFromPassword(ReadOnlySpan<byte> passwordBytes, ReadOnlySpan<byte> argon2Salt, out byte[] kek)` — same KDF, password path.
- `DeriveBrainKey(ReadOnlySpan<byte> dek, Span<byte> destination)` — HKDF-SHA256 from DEK with info label `"brain"u8`. Used by `BrainConnectionFactory` (§1.5).

**NuGet introduced:** `Konscious.Security.Cryptography.Argon2` (Argon2id) — verify the version is current and matches `Directory.Packages.props`. The BIP-39 wordlist is an embedded resource, not a third-party library.

**Key constraints:**
- The BIP-39 wordlist (2048 entries) is embedded as a `txt` resource in `FlashSkink.Core`. It is loaded once into a `static readonly FrozenSet<string>` (or equivalent) at first access.
- `MnemonicService` never persists or logs words.
- All `byte[]` outputs that carry key material are documented as "caller must zero after use." The services do not zero their own outputs — the caller controls lifetime.
- `DeriveKekFromPassword` accepts `ReadOnlySpan<byte>` for the password so the caller can pass a stack-pinned or pooled buffer and zero it after the call without the service holding a reference.

---

### §1.3 — DEK vault and volume lifecycle

**Blueprint sections to read:** §18.4, §18.6, §18.8.

**Scope summary:** Two types in `FlashSkink.Core/Crypto/`.

`KeyVault` manages the on-disk vault file (`[USB]/.flashskink/vault.bin`). The binary format is defined in §18.4:

```
Offset  Size   Field
------  ----   -----
0       4      Magic ("FSVT")
4       2      Version (uint16 = 1)
6       2      Argon2id params (8-bit log2(memory), 4-bit iterations, 4-bit parallelism)
8       32     Argon2id salt
40      12     AES-GCM nonce
52      32     Wrapped DEK ciphertext
84      16     GCM tag
Total:  100    bytes
```

Public methods:
- `CreateAsync(string vaultPath, ReadOnlySpan<byte> password, CancellationToken ct)` — generates a random DEK, derives KEK via Argon2id, wraps DEK, writes vault. Returns `Result<byte[]>` (the unwrapped DEK, caller zeroes).
- `UnlockAsync(string vaultPath, ReadOnlySpan<byte> password, CancellationToken ct)` — reads vault, derives KEK, unwraps DEK. Returns `Result<byte[]>` (the DEK, caller zeroes). Returns `ErrorCode.InvalidPassword` on GCM authentication failure.
- `UnlockFromMnemonicAsync(string vaultPath, string[] words, CancellationToken ct)` — mnemonic path for recovery: BIP-39 seed → KDF → KEK → unwrap DEK.
- `ChangePasswordAsync(string vaultPath, ReadOnlySpan<byte> currentPassword, ReadOnlySpan<byte> newPassword, CancellationToken ct)` — unlocks to get DEK, re-wraps with new KEK, writes new vault (temp-write + fsync + rename — same atomic pattern as blob writes), zeroes both keys.

`VolumeLifecycle` is the thin orchestration type that will eventually grow into the full `FlashSkinkVolume` surface (§11). In Phase 1 it implements only the lock/unlock axis — enough to support repository tests in §1.6:
- `OpenAsync(string skinkRoot, ReadOnlySpan<byte> password, CancellationToken ct)` — unlock vault → derive brain key → open brain connection → run migrations → return `Result<VolumeSession>`.
- `VolumeSession` (sealed, `IAsyncDisposable`) — holds the decrypted DEK (as `byte[]`), the open `SqliteConnection`, and a `Close()` / `DisposeAsync()` that zeroes DEK + brain key and closes the connection.

**NuGet:** None new.

**Key constraints:**
- `KeyVault` never holds a `byte[]` field for DEK or KEK. It derives, uses, and returns/zeroes within method scope.
- `CryptographicOperations.ZeroMemory` is called on every intermediate key buffer — KEK, brain key, password bytes passed in — before the method returns on both success and failure paths (Principle 31).
- All GCM operations use `System.Security.Cryptography.AesGcm`. No third-party crypto library touches key material.
- Vault write is atomic: write to `.flashskink/vault.tmp`, `fsync`, rename to `vault.bin` (same principle as blob writes, blueprint §13.4).

---

### §1.4 — Crypto pipeline and blob header

**Blueprint sections to read:** §13.6, §14.1 (steps 2–4 only), §14.2 (step 4 only), §18.1.

**Scope summary:** Two types in `FlashSkink.Core/Crypto/`.

`BlobHeader` handles serialisation and parsing of the 20-byte on-disk preamble:

```
Offset  Size   Field
------  ----   -----
0       4      Magic ("FSBL")
4       2      Version (uint16 = 1)
6       2      Flags (uint16; Bit 0 = LZ4, Bit 1 = Zstd)
8       12     Nonce
```

The GCM tag (16 bytes) follows the ciphertext and is not part of the header struct, but `BlobHeader` exposes the constants `HeaderSize = 20`, `TagSize = 16`, `NonceSize = 12`, and `MagicSize = 4` so other pipeline stages use one source of truth.

Public methods:
- `Write(Span<byte> destination, BlobFlags flags, ReadOnlySpan<byte> nonce)` — writes 20 bytes to `destination[..HeaderSize]`.
- `Parse(ReadOnlySpan<byte> source, out BlobFlags flags, out ReadOnlySpan<byte> nonce)` — validates magic and version, returns `Result`. Returns `ErrorCode.VaultCorrupt` (reused as a blob-format error) on bad magic. Returns `ErrorCode.VaultVersionUnsupported` on unknown version.

`CryptoPipeline` performs AES-256-GCM encrypt and decrypt using the DEK:

- `Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> dek, ReadOnlySpan<byte> aad, IMemoryOwner<byte> outputOwner, out int bytesWritten)` — generates a fresh 12-byte nonce via `RandomNumberGenerator.GetBytes`, writes the blob header, then the ciphertext, then the 16-byte GCM tag into `outputOwner.Memory`. The output layout is: `[Header (20B)] || [Ciphertext (N B)] || [Tag (16B)]`. Returns `Result`.
- `Decrypt(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> dek, ReadOnlySpan<byte> aad, IMemoryOwner<byte> outputOwner, out int bytesWritten)` — parses the header, locates ciphertext and tag, decrypts, writes plaintext to `outputOwner.Memory`. Returns `ErrorCode.CryptoFailed` on GCM authentication failure. Returns `ErrorCode.VaultVersionUnsupported` on unknown blob version.

**NuGet:** None new (`AesGcm` is in `System.Security.Cryptography`; buffer pooling uses `ArrayPool<byte>` from `System.Buffers`).

**Key constraints:**
- The nonce is generated fresh inside `Encrypt` via `stackalloc byte[NonceSize]`, populated by `RandomNumberGenerator.Fill`, then written into the output buffer before the `AesGcm.Encrypt` call. The `stackalloc` never crosses an `await` boundary (Principle 20).
- The AAD for blob encryption is `BlobID || PlaintextSHA256` (each as UTF-8 bytes), concatenated by the caller. `CryptoPipeline` accepts AAD as `ReadOnlySpan<byte>` without knowing its composition.
- `IMemoryOwner<byte>` outputs are provided by the caller from `ArrayPool<byte>.Shared`; `CryptoPipeline` writes into them. The pipeline never allocates output buffers itself.
- `CryptoPipeline` has zero knowledge of files, blobs, repositories, or vault keys. It takes bytes in, returns bytes out.

---

### §1.5 — Brain connection, schema, and migrations

**Blueprint sections to read:** §16.1, §16.2 (full schema), §16.3, §18.5, §6.8 (reference implementation).

**Scope summary:** Three types plus schema resources in `FlashSkink.Core/Metadata/`.

`BrainConnectionFactory` owns the SQLCipher connection lifecycle:
- `CreateAsync(string brainPath, ReadOnlySpan<byte> dek, CancellationToken ct)` — derives the brain key via HKDF-SHA256 (`KeyDerivationService.DeriveBrainKey`), opens a `SqliteConnection`, issues the SQLCipher `PRAGMA key` to activate encryption, applies the required pragmas (`journal_mode=WAL`, `synchronous=EXTRA`, `foreign_keys=ON`, `integrity_check`), zeroes the brain key, returns `Result<SqliteConnection>`. Follows the reference implementation in blueprint §6.8 exactly: `OperationCanceledException` caught first, then `SqliteException` with `SqliteErrorCode` filters for `CORRUPT` (11), `BUSY`/`LOCKED` (5/6), then generic `SqliteException`, then `UnauthorizedAccessException`, then `Exception`.
- The returned `SqliteConnection` is the caller's responsibility to dispose.

`MigrationRunner` applies embedded schema migration scripts:
- `RunAsync(SqliteConnection connection, CancellationToken ct)` — reads `Settings["SchemaVersion"]` (defaulting to 0 for a fresh DB), compares to the latest embedded version, runs each missing script in version order inside individual transactions, records each applied version in `SchemaVersions`. Returns `Result`. On failure at any migration step, rolls back the failing transaction and returns `Result.Fail(ErrorCode.DatabaseWriteFailed, ...)` — the pre-migration backup step (§16.3) is the caller's responsibility at the volume-open level (implemented in `VolumeLifecycle.OpenAsync`, §1.3).

**Embedded resources:**
- `FlashSkink.Core/Metadata/Migrations/V001_InitialSchema.sql` — the complete V1 schema from §16.2, including all tables, indices, and initial `Settings` rows.

**NuGet introduced:** `Microsoft.Data.Sqlite` (the `SqliteConnection` base), `SQLitePCLRaw.bundle_sqlcipher` (SQLCipher encryption), `Dapper` (general queries — not used in §1.5 itself, but registered here since it is a `FlashSkink.Core` dependency).

**Key constraints:**
- `PRAGMA integrity_check` is run inside `CreateAsync` as part of opening. A non-`ok` result maps to `ErrorCode.DatabaseCorrupt` — the caller (`VolumeLifecycle`) enters the repair flow.
- The brain key is `stackalloc byte[32]`, derived, used in the `PRAGMA key` call, and zeroed with `CryptographicOperations.ZeroMemory` before `CreateAsync` returns — regardless of success or failure (Principle 31).
- `PRAGMA key` is issued as a string interpolation containing the hex-encoded key. The hex string is cleared (overwritten with zeros) immediately after the pragma executes. It is never logged.
- Tests in `tests/FlashSkink.Tests/Metadata/` use an in-process SQLite database (without SQLCipher encryption — `password = ""` or a test-fixed key) for speed. A separate integration test confirms that SQLCipher encryption is actually active by verifying that the file bytes are not plaintext. This integration test is gated behind a `[Trait("Category", "Integration")]` attribute so it can be run separately.

---

### §1.6 — Brain repositories

**Blueprint sections to read:** §16.2 (schema, for column names/types), §16.4 (folder tree queries), §16.5 (folder behavioural rules), §16.6 (`FileRepository` method list).

**Scope summary:** Six repository classes in `FlashSkink.Core/Metadata/`, plus their query infrastructure.

Each repository receives a `SqliteConnection` (or a factory/accessor — the plan will decide the DI pattern, likely a `IBrainAccessor` interface that `VolumeSession` implements). All repositories use Dapper for general queries and raw `SqliteDataReader` only for the hot paths identified in Principle 22.

**`FileRepository`** — all methods from §16.6:
`InsertAsync`, `GetByIdAsync`, `ListChildrenAsync`, `ListFilesAsync`, `EnsureFolderPathAsync`, `CountChildrenAsync`, `GetDescendantsAsync`, `DeleteFolderCascadeAsync`, `DeleteFileAsync`, `RenameFolderAsync`, `MoveAsync`, `RestoreFromGracePeriodAsync`.

The four folder-tree queries from §16.4 (list children, recursive descendants CTE, ancestor chain cycle check, recursive `VirtualPath` update) are implemented here.

**`BlobRepository`** — CRUD on the `Blobs` table:
`InsertAsync`, `GetByIdAsync`, `GetByPlaintextHashAsync` (for change-detection short-circuit in Phase 2), `SoftDeleteAsync`, `MarkCorruptAsync`, `ListPendingPurgeAsync` (blobs past `PurgeAfterUtc`), `HardDeleteAsync`.

**`UploadQueueRepository`** — manages `TailUploads` and `UploadSessions`:
`EnqueueAsync` (inserts a `TailUploads` row in PENDING state for a given `(FileID, ProviderID)`), `DequeueNextBatchAsync` (raw `SqliteDataReader`, hot path — returns a `readonly record struct` per row), `MarkUploadingAsync`, `MarkUploadedAsync`, `MarkFailedAsync`, `GetOrCreateSessionAsync` (upsert `UploadSessions`), `UpdateSessionProgressAsync`, `DeleteSessionAsync`.

**`WalRepository`** — manages the `WAL` table for crash recovery:
`InsertAsync` (PREPARE phase), `TransitionAsync` (PREPARE → COMMITTED or PREPARE/COMMITTED → FAILED), `ListIncompleteAsync` (returns all rows not in COMMITTED or FAILED — called at startup by the WAL recovery logic in Phase 5), `DeleteAsync`.

**`ActivityLogRepository`** — append and query on `ActivityLog`:
`AppendAsync`, `ListRecentAsync(int limit)`, `ListByCategoryAsync(string category, int limit)`.

**`BackgroundFailureRepository`** — manages `BackgroundFailures`:
`AppendAsync`, `ListUnacknowledgedAsync`, `AcknowledgeAsync(string failureId)`, `AcknowledgeAllAsync`.

**NuGet:** None new beyond what §1.5 introduced.

**Key constraints:**
- All repository methods that perform writes accept `CancellationToken ct` as their final parameter (Principle 13). Read methods that may be called on hot paths also accept `ct`.
- Compensation-path calls within any repository (WAL transitions, staging cleanup) pass `CancellationToken.None` as a literal (Principle 17).
- `FileRepository.DeleteFolderCascadeAsync` and `FileRepository.MoveAsync` use recursive CTEs from §16.4 and run inside explicit transactions. The WAL row for the operation is inserted before the main DML and committed in the same transaction.
- `UploadQueueRepository.DequeueNextBatchAsync` uses a raw `SqliteDataReader` (Principle 22) and returns `IAsyncEnumerable<TailUploadRow>` where `TailUploadRow` is a `readonly record struct`. This is the one hot-path reader in Phase 1.
- Constraint violations (`UNIQUE`, `FOREIGN KEY`) from SQLite are caught as `SqliteException` with the appropriate `SqliteErrorCode` and mapped to `ErrorCode.PathConflict` or `ErrorCode.DatabaseWriteFailed` as appropriate.

---

## What Phase 1 does NOT do

- **No `FileTypeService` or `EntropyDetector`.** These belong to the write pipeline (Phase 2, §2.1). Phase 1 establishes the crypto and brain; the content-type layer belongs with the pipeline that uses it.
- **No `CompressionService`.** LZ4 and Zstd compression are wired in Phase 2.
- **No `WritePipeline` or `ReadPipeline`.** Phase 2 connects the pieces Phase 1 assembles.
- **No upload queue logic** beyond the repository layer. `UploadQueueService`, `RangeUploader`, and `RetryPolicy` are Phase 3.
- **No `IStorageProvider` implementations.** `FileSystemProvider`, `GoogleDriveProvider`, `DropboxProvider`, `OneDriveProvider` are Phase 4.
- **No WAL recovery execution.** `WalRepository.ListIncompleteAsync` is implemented, but the recovery sweep that calls it lives in Phase 5.
- **No `AuditService` or `SelfHealingService`.** Phase 5.
- **No GUI or CLI commands.** Phase 6.
- **No brain mirroring to tails.** The mirror upload path (§16.7) is implemented in Phase 3 once the upload queue exists.
- **`VolumeLifecycle`** in §1.3 is a skeleton — only `OpenAsync` / `DisposeAsync` and the types they need. The full `FlashSkinkVolume` public API surface (§11) grows in Phase 2 and is complete in Phase 5.
- **No Notification bus.** `INotificationBus` and `NotificationBus` are declared in Phase 2 and wired fully in Phase 3. Phase 1 repositories accept `ILogger<T>` only.

---

## Acceptance — Phase 1 is complete when

- [ ] All files listed in §1.1 through §1.6 exist and are committed on squash-merged PRs in `main`.
- [ ] `dotnet build` succeeds with zero warnings on `ubuntu-latest` and `windows-latest` (the two CI matrix targets).
- [ ] `dotnet test` is fully green: all existing Phase 0 smoke tests still pass; all Phase 1 tests pass.
- [ ] The following scenarios pass as integration tests or demonstrated in test output:
  - [ ] `MnemonicService.Generate()` produces 24 valid BIP-39 words that pass `Validate()`.
  - [ ] `KeyVault.CreateAsync` then `KeyVault.UnlockAsync` (same password) returns the same DEK.
  - [ ] `KeyVault.UnlockAsync` with a wrong password returns `ErrorCode.InvalidPassword`.
  - [ ] `CryptoPipeline.Encrypt` → `CryptoPipeline.Decrypt` (same DEK, same AAD) round-trips to original bytes.
  - [ ] `CryptoPipeline.Decrypt` with a tampered ciphertext byte returns `ErrorCode.CryptoFailed`.
  - [ ] `MigrationRunner.RunAsync` on a fresh in-process SQLite creates all V1 tables and sets `SchemaVersion = 1`.
  - [ ] `FileRepository.InsertAsync` + `ListChildrenAsync` round-trip correctly via in-process DB.
  - [ ] `BlobRepository.GetByPlaintextHashAsync` returns the matching row after `InsertAsync`.
- [ ] CI `plan-check` job passes for all six PRs (each `.claude/plans/pr-1.X.md` exists, contains all required headings, cites at least one `§` blueprint reference).
- [ ] No `ErrorCode` values are left as `TODO` or placeholder stubs — the full Phase 1–6 enum is declared in §1.1 even if only Phase 1 values are exercised yet.
- [ ] `docs/error-handling.md` is updated with at least one worked example drawn from the Phase 1 implementation (the `BrainConnectionFactory.CreateAsync` pattern from §6.8 is the natural candidate).

---

## Principles exercised in Phase 1

Phase 1 is the first real exercise of the project's non-negotiable invariants. Every PR in this phase touches the principles listed below; Gate 2 checks each one explicitly.

- **Principle 1** (Core never throws across its public API) — every public method in `KeyVault`, `CryptoPipeline`, `BrainConnectionFactory`, `MigrationRunner`, and all repositories returns `Result` or `Result<T>`.
- **Principle 13** (`CancellationToken ct` always last, always present on async I/O) — all repository and factory methods.
- **Principle 14** (`OperationCanceledException` caught first, mapped to `ErrorCode.Cancelled`, logged at `Information`) — all async methods with `ct`.
- **Principle 15** (no bare single `catch (Exception)`) — granular exception handling is the central pattern of this phase; §6.8 is the reference.
- **Principle 16** (dispose on every failure path) — `SqliteConnection` disposal in `BrainConnectionFactory`; `IMemoryOwner<byte>` disposal in `CryptoPipeline`.
- **Principle 17** (`CancellationToken.None` as a literal in compensation paths) — `WalRepository.TransitionAsync` calls inside error-recovery branches; `KeyVault.ChangePasswordAsync`'s vault-write rollback path.
- **Principle 20** (`stackalloc` never crosses `await`) — nonce generation in `CryptoPipeline.Encrypt`; brain key derivation in `BrainConnectionFactory.CreateAsync`.
- **Principle 26** (logging never contains secrets) — `BrainConnectionFactory` and `KeyVault` are the highest-risk sites; the PRAGMA key string and all key buffers are explicitly excluded from any log call.
- **Principle 27** (Core logs internally; callers log the `Result`) — `BrainConnectionFactory` logs the `SqliteException` details at the point of constructing `Result.Fail`; `VolumeLifecycle` logs the returned error when surfacing it.
- **Principle 28** (Core depends only on `Microsoft.Extensions.Logging.Abstractions`) — no Serilog import in `FlashSkink.Core` or `FlashSkink.Core.Abstractions`.
- **Principle 31** (keys zeroed on close) — DEK, KEK, brain key, and password bytes are zeroed with `CryptographicOperations.ZeroMemory` on every exit path of every method in `KeyVault`, `KeyDerivationService`, and `BrainConnectionFactory`.

---

## Post-Phase-1 hand-off

After Phase 1 the session protocol continues unchanged. Phase 2 begins with:

> `read section 2.1 of the dev plan and perform`

The plan for §2.1 will read `dev-plan/phase-2-write-pipeline.md` (not yet written) and the committed `.claude/plans/pr-1.*.md` files to discover the final public API surface of `CryptoPipeline`, `BrainConnectionFactory`, and the repositories before wiring them into the write pipeline.

---

*Phase 1 is the cryptographic and data-persistence foundation. No application feature is user-visible at the end of this phase — but every line of code that a user eventually trusts with their data rests on what is built here.*
