# FlashSkink — Technical Blueprint v1.0

> **The tail regenerates your data.**
>
> **Status:** V1 design baseline. Mirror architecture, resumable per-tail uploads, transactional USB commit with asynchronous cross-host upload queue.
>
> **Document role:** Architectural and product reference. Describes *what* FlashSkink is and *why* its design is what it is. Implementation sequencing lives in `DEV_PLAN.md`. Workflow rules live in `CLAUDE.md`.
>
> **Supersedes:** FlashRaid Blueprint v2.7 (RAID-5 design, superseded by mirror model).

---

## Table of Contents

1. Project Overview
2. Glossary
3. Core Design Principles
4. Solution Architecture
5. The Mirror Model
6. Result Pattern and Error Handling
7. Logging Architecture
8. Notification Bus
9. Memory Management
10. Provider Interfaces
11. Core Public API
12. Presentation Layer Contracts
13. Storage Architecture
14. Data Processing Pipeline
15. Resumable Upload Sessions
16. Metadata Management (The Brain)
17. File Type Detection
18. Security and Key Management
19. USB Resilience
20. Integrity and Self-Healing
21. Error Handling and Crash Recovery
22. Provider Health Monitoring
23. CLI Reference
24. Setup CLI (Provider Automation)
25. GUI Surface
26. Portable Execution Model
27. Built-in Providers
28. Tech Stack
29. Decision Records
30. Out of Scope (V1)
31. Post-V1 Direction

---

## 1. Project Overview

FlashSkink is a portable, nomadic backup system that distributes complete encrypted replicas of a user's data across a local USB flash drive ("the skink") and one or more cloud storage providers ("the tails"). The skink is the body the user carries; each tail is a full, independently recoverable copy that grows behind it.

The entire application runs directly from the USB. Nothing is installed on the host machine, no host state is written, no traces remain after unplugging. Users interact through a small GUI or a CLI. Cloud providers are a configuration detail they touch only at setup time.

The product's single, unambiguous guarantee:

> **Any single surviving part — the USB skink, or any one cloud tail — plus the 24-word recovery phrase, regenerates everything.**

Lose the USB? Any tail regenerates the volume. Cloud account suspended? The skink or another tail regenerates the volume. Provider shuts down? Other tails are untouched. You need exactly one surviving part. Nothing more.

---

## 2. Glossary

| Term | Meaning |
|---|---|
| **Skink** | The USB flash drive that holds the local copy of all data and the brain. The body the user carries. |
| **Tail** | A configured cloud storage provider holding a complete, encrypted replica of all data on the skink. |
| **Brain** | The encrypted SQLite metadata database on the skink. Authoritative state. Holds the file tree, blob index, upload queue, OAuth tokens, and audit logs. |
| **Blob** | A single encrypted file payload. One blob per file. The atomic unit of storage and upload. |
| **Recovery phrase** | The 24-word BIP-39 mnemonic from which all keys are derivable. The single durable secret protecting the volume. |
| **DEK** | Data Encryption Key. Random 256-bit key that encrypts blobs and provider tokens. Stored encrypted in the vault on the skink. |
| **KEK** | Key Encryption Key. Derived from the user's password (or recovery phrase). Unwraps the DEK. |
| **Vault** | The on-skink file holding the DEK encrypted by the KEK. |
| **Volume** | A complete FlashSkink installation — one skink plus its configured tails. A user may operate multiple volumes (each on its own USB) independently. |
| **Phase 1 commit** | The synchronous, transactional write of an encrypted blob to the skink. The user-visible success point of a write. |
| **Phase 2 commit** | The asynchronous, resumable upload of a blob to a tail. May span sessions and hosts. |
| **Upload session** | A provider-specific resumable upload context (Google's session URI, Dropbox's session ID, OneDrive's upload URL) persisted on the skink so uploads resume across disconnects. |
| **Grace period** | The window after a delete or overwrite during which the prior blob is retained for accidental-action recovery. Default 30 days. |

---

## 3. Core Design Principles

| Principle | Implication |
|---|---|
| **Single survivor recovers everything** | Mirror, not stripe. Every tail is a complete, independent replica of the skink. Recovery from any one part is sufficient. |
| **The skink is the brain, not a cache** | The USB holds authoritative state and hosts the application. Tails are redundant copies; they are not consulted during normal operation. |
| **Two distinct commit boundaries** | Phase 1 (write to skink) is synchronous and transactional. Phase 2 (upload to tails) is asynchronous, resumable, best-effort across sessions and hosts. |
| **Cross-host resumability** | Upload session state lives on the skink. The user may unplug from one machine, plug into another tomorrow, and uploads resume exactly where they left off. |
| **Zero-knowledge at every external boundary** | All file content and all metadata leave the skink encrypted. No plaintext data, no raw keys, ever cross a provider boundary. |
| **Zero trust in the host** | No installation. No host state. No traces after unplugging. Staging on host temp is forbidden in the normal write path. |
| **Zero trust in the application** | No servers, no accounts, no telemetry. All keys derived locally. The user holds the only durable secret (the recovery phrase). |
| **Explicit recovery path** | Every failure scenario has a documented, tested recovery procedure. |
| **OS-agnostic** | No platform-specific APIs, paths, or assumptions. Identical behaviour on Windows, macOS (Intel and Apple Silicon), and Linux. |
| **Core / UI separation** | All storage, crypto, and engine logic in `FlashSkink.Core`. No business logic in any UI layer. |
| **UI-agnostic ViewModels** | `FlashSkink.Presentation` has zero dependency on any UI framework. ViewModel tests run without a GUI. |
| **Explicit error handling** | Core never leaks exceptions across its public API boundary. All public APIs return `Result` or `Result<T>`. |
| **No silent background failures** | Every background failure is logged, persisted, and surfaced to the user on next launch if not yet seen. |
| **Allocation-conscious hot paths** | Pipeline and upload loops use pooled buffers, `Span<T>`, `stackalloc`, and value types to minimise GC pressure. |
| **Stable provider contract** | `IStorageProvider`, `IProviderSetup`, `UploadSession`, and Result types are frozen before V1 ships. Implementations may be added; signatures may not change. |
| **Appliance positioning** | All product surfaces — CLI, GUI, copy, error messages — are written for users who think of FlashSkink as "a USB stick that backs itself up." Internal vocabulary (stripe, blob, WAL, OAuth) does not appear in user-visible surfaces. |

---

## 4. Solution Architecture

### 4.1 Project Layout

```
FlashSkink/
├── FlashSkink.sln
├── Directory.Build.props                ← shared MSBuild settings (TFM, nullable, analyzers)
├── Directory.Packages.props             ← Central Package Management (all PackageVersion here)
├── .editorconfig
├── global.json                          ← pins .NET SDK version
│
├── src/
│   ├── FlashSkink.Core.Abstractions/
│   │   ├── FlashSkink.Core.Abstractions.csproj
│   │   ├── Results/
│   │   │   ├── Result.cs
│   │   │   ├── Result{T}.cs
│   │   │   ├── ErrorContext.cs
│   │   │   └── ErrorCode.cs
│   │   ├── Providers/
│   │   │   ├── IStorageProvider.cs
│   │   │   ├── IProviderSetup.cs
│   │   │   ├── UploadSession.cs
│   │   │   └── ProviderHealth.cs
│   │   ├── Notifications/            ← INotificationBus, INotificationHandler,
│   │   │                                Notification, NotificationSeverity
│   │   └── Models/
│   │       ├── VolumeFile.cs
│   │       ├── WriteReceipt.cs
│   │       ├── HealthReport.cs
│   │       └── RecoveryOptions.cs
│   │
│   ├── FlashSkink.Core/
│   │   ├── FlashSkink.Core.csproj
│   │   ├── Engine/                   ← WritePipeline, ReadPipeline, FileTypeService,
│   │   │                                EntropyDetector, HashingService,
│   │   │                                PersistenceNotificationHandler
│   │   ├── Crypto/                   ← CryptoPipeline, KeyVault, MnemonicService,
│   │   │                                BlobHeader
│   │   ├── Compression/              ← CompressionService (Zstd / LZ4)
│   │   ├── Metadata/                 ← BrainConnectionFactory, FileRepository,
│   │   │   │                            BlobRepository, UploadQueueRepository,
│   │   │   │                            WalRepository, ActivityLogRepository,
│   │   │   │                            BackgroundFailureRepository
│   │   │   ├── Migrations/           ← Schema migration scripts
│   │   │   ├── Queries/              ← Dapper queries (general)
│   │   │   └── HotPath/              ← Raw SqliteDataReader readers (hot paths)
│   │   ├── Providers/
│   │   │   ├── FileSystemProvider.cs
│   │   │   ├── GoogleDriveProvider.cs
│   │   │   ├── DropboxProvider.cs
│   │   │   └── OneDriveProvider.cs
│   │   ├── Setup/                    ← Provider setup automation (Google, Dropbox)
│   │   ├── Upload/                   ← UploadQueueService, UploadSessionManager,
│   │   │                                RangeUploader, RetryPolicy
│   │   ├── Healing/                  ← AuditService, SelfHealingService,
│   │   │                                IntegrityVerifier
│   │   ├── Usb/                      ← UsbMonitorService, VolumeLocator,
│   │   │                                SingleInstanceLock
│   │   └── Orchestration/            ← FlashSkinkVolume (the public API surface)
│   │
│   ├── FlashSkink.Presentation/
│   │   ├── FlashSkink.Presentation.csproj
│   │   ├── ViewModels/
│   │   ├── Interfaces/               ← INavigationService, IDialogService,
│   │   │                                IFilePickerService, IBrowserService,
│   │   │                                IClipboardService
│   │   ├── Notifications/            ← NotificationBus, NotificationDispatcher,
│   │   │                                UiNotificationHandler
│   │   └── Services/
│   │
│   ├── FlashSkink.UI.Avalonia/
│   │   ├── FlashSkink.UI.Avalonia.csproj
│   │   ├── Views/
│   │   ├── Services/
│   │   └── Program.cs
│   │
│   └── FlashSkink.CLI/
│       ├── FlashSkink.CLI.csproj
│       ├── Commands/
│       │   ├── SetupCommand.cs       ← Provider automation
│       │   ├── UnlockCommand.cs
│       │   ├── InfoCommand.cs
│       │   ├── VerifyCommand.cs
│       │   ├── RestoreCommand.cs
│       │   ├── ExportCommand.cs
│       │   ├── RecoverCommand.cs
│       │   ├── ActivityCommand.cs
│       │   └── SupportBundleCommand.cs
│       └── Program.cs
│
└── tests/
    └── FlashSkink.Tests/
        ├── FlashSkink.Tests.csproj
        ├── Engine/
        ├── Crypto/
        ├── Metadata/
        ├── Providers/
        ├── Upload/
        ├── Healing/
        ├── CrashConsistency/         ← Property-based invariant tests
        └── Presentation/
```

**Rationale for the `src/` + `tests/` split.** This is the prevailing convention across modern .NET open-source repositories (`dotnet/runtime`, `dotnet/aspnetcore`, `JamesNK/Newtonsoft.Json`, most Microsoft-authored libraries). Practical effects:

- Top-level clutter stays minimal — `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `README.md`, `LICENSE`, `.editorconfig`, `.github/`, and the two source folders.
- MSBuild property files (`Directory.Build.props`, `Directory.Packages.props`) at the repo root apply to every project automatically, then can be scoped (a different `Directory.Build.props` inside `tests/` can relax warnings-as-errors or add test-only references without duplicating configuration across projects).
- Scripts and CI steps can target `src/**/*.csproj` vs `tests/**/*.csproj` cleanly — `dotnet test tests/**/*.csproj` for the test pass, `dotnet publish src/FlashSkink.UI.Avalonia/...` for release builds.
- New contributors find what they expect without being told where to look.

**Note on FlashSkink.Tests as a single project.** V1 keeps all tests in one project (`tests/FlashSkink.Tests/`) with subfolders per area. This minimises project-file overhead while tests are still growing. If any test area becomes large enough to justify its own assembly (separate test runner invocation, different TFM, different dependencies), splitting into `tests/FlashSkink.Core.Tests/`, `tests/FlashSkink.Presentation.Tests/`, etc. is a trivial restructure — no source changes, just project-file moves. PR 0.2 will revisit if needed.

### 4.2 Dependency Graph

```
FlashSkink.Core.Abstractions
        ▲
        │
FlashSkink.Core
        ▲                   ▲
        │                   │
FlashSkink.Presentation    FlashSkink.CLI
        ▲
        │
FlashSkink.UI.Avalonia

FlashSkink.Tests → Core.Abstractions, Core, Presentation
```

Rules enforced at the assembly level (verified in CI):

- **`FlashSkink.Presentation`** has no reference to Avalonia or any UI framework.
- **`FlashSkink.UI.Avalonia`** has no direct reference to `FlashSkink.Core` (except `Program.cs` for DI wiring).
- **`FlashSkink.CLI`** has no reference to `FlashSkink.Presentation`.
- **`FlashSkink.Core.Abstractions`** has no project references at all (foundation).
- **`FlashSkink.Core`** depends only on `FlashSkink.Core.Abstractions`.
- **`FlashSkink.Core` does not reference `FlashSkink.Presentation`.** The dependency arrow points the other way: `Presentation → Core`. Any contract that a Core service needs to call (e.g., `INotificationBus.PublishAsync` from `WritePipeline`) lives in `FlashSkink.Core.Abstractions`. Implementations of those contracts may still live in `Presentation` — the contract/implementation split is what keeps the layering clean. This rule is the reason `Result`, `ErrorContext`, `IStorageProvider`, `INotificationBus`, and `INotificationHandler` all live in `Abstractions`.

### 4.3 Naming Conventions

| Concern | Convention |
|---|---|
| Project namespace | `FlashSkink.{Layer}` (e.g. `FlashSkink.Core.Engine`) |
| `CancellationToken` parameter | Always named `ct`, always last parameter |
| Async methods | Suffix `Async` |
| Result-returning sync methods | No suffix; return `Result` or `Result<T>` |
| Interface prefix | `I` (e.g. `IStorageProvider`) |
| Test class names | `{ClassUnderTest}Tests` |
| Test method names | `Method_State_ExpectedBehavior` |

---

## 5. The Mirror Model

### 5.1 What Mirroring Means Here

Every tail holds a complete, independently recoverable encrypted replica of every file on the skink. There is no parity. There is no striping. There is no dependence between tails. A user with three tails has three full copies of their data in the cloud, plus the local copy on the skink — four total replicas.

The storage cost is N × data for N tails. This is the explicit tradeoff that buys:

- The "any single surviving part" guarantee, in its strongest form.
- A trivially simple recovery procedure.
- A trivially simple mental model for the user.
- An architecture without parity computation, stripe planning, two-phase commit complexity, or quorum logic.

For the target user (personal backup volumes, typically tens of GB), N × storage cost is acceptable and the mental simplicity is dispositive.

### 5.2 The Two Commit Boundaries

The mirror model rests on a sharp distinction between two operations that are conflated in most backup tools.

**Phase 1 — Write to skink.** Synchronous. Transactional. User-blocking. The user drags a file onto the skink; the skink compresses, encrypts, and writes the blob locally; a row is inserted in the brain; the operation returns success. The user may unplug the moment Phase 1 returns.

**Phase 2 — Upload to tails.** Asynchronous. Resumable. Best-effort. A background queue worker picks up newly-committed files and uploads them to each configured tail. Uploads use each provider's resumable upload protocol with session state persisted on the skink. The skink may be unplugged at any point and reinserted on a different host; uploads resume exactly where they left off.

The two phases are decoupled. The user's interaction model is "drag and forget" — Phase 1 confirms safety on the skink instantly, Phase 2 catches up over time without user involvement.

### 5.3 What "Mirror" Implies for Behaviour

The mirror model commits to specific behaviours:

- **Writes always succeed locally before tails are involved.** A tail being offline, slow, or full does not block writes. The user is never told "wait while we upload."
- **Deletes propagate to all tails.** When the user deletes a file from the skink, after the soft-delete grace period elapses, the file is removed from every tail. Tails track the skink's current state.
- **Tails are read for recovery, not for normal operation.** Reading a file on a healthy skink reads from the local blob, never from a tail. Tails are touched only when (a) uploading, (b) verifying integrity, (c) recovering from skink loss, or (d) self-healing a corrupted tail.
- **A new tail starts empty and catches up.** Adding a tail to an existing volume queues every existing file for upload to the new tail. No restripe, no rebalance — just a stream of uploads.
- **A removed tail is forgotten cleanly.** Removing a tail deletes its OAuth token and queue rows. The data on the provider is not deleted automatically (the user can do that manually if they wish — the data is encrypted and useless without the volume's keys).

### 5.4 What Mirroring Does Not Include

- **Versioning.** Overwrites are overwrites. The soft-delete grace period is a safety net, not a feature surface. There is no "previous versions" UI.
- **Snapshots.** Each upload is a current-state mirror. There are no point-in-time snapshots of the volume.
- **Multi-device sync.** A volume is single-user, single-skink. Two USBs are two volumes.
- **Selective sync.** All files on the skink go to all tails. The user does not pick which files go where.

These are not deficiencies but deliberate scope decisions. A snapshot product, if built, is a separate product (see §31 Post-V1 Direction).

---

## 6. Result Pattern and Error Handling

### 6.1 Design Goals

- **Core never throws across its public API boundary.** Absolute rule. Every `public` method on every type in `FlashSkink.Core` and `FlashSkink.Core.Abstractions`, synchronous or asynchronous, trivial-looking or complex, returns a `Result` or `Result<T>`. A `public SqliteConnection Create(...)` is not exempt — it returns `Result<SqliteConnection>`.
- **`ErrorContext` carries enough information for the caller to log in detail without re-throwing.** The raw `Exception` never escapes Core — type name, message, and stack trace are captured as strings.
- **Exception information is preserved with granularity.** A lone `catch (Exception ex)` that maps every failure to `ErrorCode.Unknown` is a defect. Distinct exception types map to distinct `ErrorCode` values whenever the caller can act on the difference.
- **Cancellation is a first-class concern.** `OperationCanceledException` is never folded into a generic failure. It is always caught separately and mapped to `ErrorCode.Cancelled`.
- **Some operations must not be cancellable.** Compensation, cleanup, zeroization, and sweep paths pass `CancellationToken.None` to their inner calls explicitly — see §6.7.
- **Plugin contracts also return Results.** `IStorageProvider`, `IProviderSetup`, and any future plugin-loadable contract use the same `Result` types, requiring plugin authors to handle errors explicitly.

### 6.2 Result Types

```csharp
namespace FlashSkink.Core.Abstractions.Results;

public readonly record struct Result
{
    public bool Success { get; }
    public ErrorContext? Error { get; }

    private Result(bool success, ErrorContext? error)
    {
        Success = success;
        Error = error;
    }

    public static Result Ok() => new(true, null);
    public static Result Fail(ErrorCode code, string message, Exception? exception = null)
        => new(false, ErrorContext.From(code, message, exception));
    public static Result Fail(ErrorContext error) => new(false, error);
}

public readonly record struct Result<T>
{
    public bool Success { get; }
    public T? Value { get; }
    public ErrorContext? Error { get; }

    private Result(bool success, T? value, ErrorContext? error)
    {
        Success = success;
        Value = value;
        Error = error;
    }

    public static Result<T> Ok(T value) => new(true, value, null);
    public static Result<T> Fail(ErrorCode code, string message, Exception? exception = null)
        => new(false, default, ErrorContext.From(code, message, exception));
    public static Result<T> Fail(ErrorContext error) => new(false, default, error);
}
```

### 6.3 ErrorContext

```csharp
public sealed record ErrorContext
{
    public required ErrorCode Code { get; init; }
    public required string Message { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? StackTrace { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public static ErrorContext From(ErrorCode code, string message, Exception? exception)
        => new()
        {
            Code = code,
            Message = message,
            ExceptionType = exception?.GetType().FullName,
            ExceptionMessage = exception?.Message,
            StackTrace = exception?.StackTrace
        };
}
```

The raw exception object is never retained; only its string-shaped diagnostic information.

### 6.4 ErrorCode

```csharp
public enum ErrorCode
{
    Unknown = 0,

    // Execution (control-flow outcomes; not domain failures)
    Cancelled,
    Timeout,

    // Volume
    VolumeNotFound, VolumeAlreadyOpen, VolumeCorrupt, VolumeReadOnly,
    VolumeAlreadyExists, VolumeIncompatibleVersion,

    // Auth
    InvalidPassword, InvalidMnemonic, KeyDerivationFailed,
    TokenExpired, TokenRefreshFailed, TokenRevoked,

    // Providers
    ProviderUnreachable, ProviderAuthFailed, ProviderQuotaExceeded,
    ProviderRateLimited, ProviderApiChanged,
    LocalNetworkOffline,
    UploadFailed, UploadSessionExpired, DownloadFailed,
    BlobNotFound, BlobCorrupt,

    // Pipeline
    CompressionFailed, EncryptionFailed, DecryptionFailed,
    IntegrityCheckFailed, ChecksumMismatch,

    // Metadata
    DatabaseCorrupt, DatabaseLocked, DatabaseWriteFailed,
    DatabaseMigrationFailed, WalRecoveryFailed,
    SchemaVersionMismatch,

    // USB / IO
    UsbRemoved, UsbNotFound, UsbReadOnly, UsbFull,
    StagingFailed, InsufficientDiskSpace,
    SingleInstanceLockHeld,

    // File operations
    FileChangedDuringRead, FileTooLong, PathConflict,
    CyclicMoveDetected, ConfirmationRequired,
    UnsupportedFileType, FileNotFound,

    // Recovery
    RecoveryFailed, NoBackupFound, BackupDecryptFailed,

    // Healing
    HealingFailed, NoSurvivorsAvailable
}
```

### 6.5 Exception-to-Result Translation Rules

When a public Core method wraps operations that can throw, exceptions are translated to `Result` with maximum preserved granularity.

**Rule 1 — A bare `catch (Exception ex)` is never the only catch block.** A single generic catch erases information the caller needs and is a code-review rejection.

**Rule 2 — Catch specific types first, in this fixed order:**

1. `OperationCanceledException` (which includes `TaskCanceledException`) → `ErrorCode.Cancelled`. Mandatory first catch on any method that accepts a `CancellationToken` or awaits something that can be cancelled.
2. Type-specific exceptions that map to distinct `ErrorCode` values — `SqliteException`, `IOException`, `UnauthorizedAccessException`, `CryptographicException`, `HttpRequestException`, `AuthenticationException`, etc. Each distinct failure mode the caller might handle differently gets its own catch.
3. `Exception` — only as a final fallback, mapped to `ErrorCode.Unknown`. Its presence is expected; its role is to prevent a thrown escape while signalling *"this case was not anticipated."*

**Rule 3 — Always pass the exception to `ErrorContext`.** This captures the type name, message, and stack trace as strings. The raw exception object never escapes Core.

**Rule 4 — Include diagnostic metadata, never secrets.** Connection-level context (without passwords), file paths, blob IDs, provider IDs, SQLite error codes, HTTP status codes — anything that helps the caller diagnose without re-throwing. Never tokens, passwords, keys, or mnemonics (see §7.6).

**Rule 5 — Sub-codes within a single exception type.** Some exceptions carry structured error codes that further discriminate the failure. `SqliteException.SqliteErrorCode` is used with `when` filters to distinguish `DatabaseLocked` from `DatabaseCorrupt` from generic `DatabaseWriteFailed`. `HttpRequestException.StatusCode` is used similarly for provider calls.

**Rule 6 — Dispose or clean up on every failure path.** If a method partially constructs a disposable resource before throwing, every catch block disposes it before returning the `Result.Fail`. `using` or `using var` is preferred when the shape of the method allows it; when the resource must be returned on the success path, nullable-tracking with explicit `Dispose` in each catch is the pattern.

### 6.6 Cancellation Handling

**Rule 1 — Every async method that performs I/O, network, or CPU-intensive work accepts `CancellationToken ct` as its final parameter.** The parameter name is consistently `ct` throughout the project.

**Rule 2 — `ct` is passed to every async call inside the method.** An async method that accepts `ct` but internally calls `stream.ReadAsync()` or `connection.OpenAsync()` without forwarding it is broken.

**Rule 3 — `OperationCanceledException` is always caught separately.** Never merged into a generic exception catch. It returns `Result.Fail(ErrorCode.Cancelled, "<operation> cancelled.", ex)`.

**Rule 4 — Cancellation is not an error-level log event.** Log at `Information` or `Debug`. It is a cooperative outcome, not a fault. Error-level logs on cancellation trigger false-positive alerts.

**Rule 5 — Pre-check cancellation at the top of long-running methods.** Before entering a loop that processes many rows or ranges, call `ct.ThrowIfCancellationRequested()` (inside the try) or check `ct.IsCancellationRequested` and return `Result.Fail(ErrorCode.Cancelled, ...)` directly.

**Rule 6 — Synchronous methods without `ct` still follow the exception-granularity rules.** If a synchronous method is invoked inside an async cancellable context and its inner calls can throw `OperationCanceledException`, the exception still maps to `ErrorCode.Cancelled`.

### 6.7 Uncancellable Operations: Sweeping and Compensation

Some operations must not be cancelled mid-flight — cancelling halfway would leave the system in an inconsistent state harder to recover from than the condition cancellation was trying to avoid.

**Operations in this category:**

- WAL recovery compensation (deleting orphaned blobs, marking records `FAILED`)
- Skink-side staging cleanup after a failed Phase 1 commit
- DEK / KEK / password buffer zeroization on volume close
- Final brain DB mirror on clean shutdown
- Self-healing finalisation once reconstruction has begun
- Post-upload verification and `TailUploads` transition to `UPLOADED`
- Token nonce rotation after successful refresh
- Soft-delete grace-period sweeper (when actually deleting)

**Pattern — observe cancellation at the boundary, then switch to `None`:**

The method still takes `ct` in its signature because callers expect it. Cancellation is observed *before* the critical section. Inside the critical section, every inner call receives `CancellationToken.None` **explicitly, as a literal** — this makes the intent visually conspicuous at code review time and grep-able.

```csharp
public async Task<Result> CompensateBlobAsync(
    string blobId,
    string stagingPath,
    CancellationToken ct)
{
    // Observe cancellation BEFORE the critical section begins.
    if (ct.IsCancellationRequested)
        return Result.Fail(ErrorCode.Cancelled,
            $"Blob {blobId} compensation not started.");

    // Inside the critical section: CancellationToken.None on every inner call.
    try
    {
        await _walRepository.MarkFailedAsync(blobId, CancellationToken.None);
        await _fileSystem.DeleteIfExistsAsync(stagingPath, CancellationToken.None);
        return Result.Ok();
    }
    catch (SqliteException ex)
    {
        return Result.Fail(ErrorCode.DatabaseWriteFailed,
            $"Failed to mark WAL blob {blobId} as failed during compensation.", ex);
    }
    catch (IOException ex)
    {
        return Result.Fail(ErrorCode.StagingFailed,
            $"Failed to clean staging file for blob {blobId}.", ex);
    }
}
```

**Rule — `CancellationToken.None` must be a literal, not a local.** Writing `var none = CancellationToken.None; await Foo(none);` defeats the readability goal. The compensation pattern is recognisable by `CancellationToken.None` appearing at every await site inside the critical section.

**Rule — Compensation methods never wrap an inner cancellable call in a try/catch-for-cancellation.** Since they pass `None`, `OperationCanceledException` should not occur; if it does, it is a bug that deserves to surface, not silent suppression.

### 6.8 Reference Implementation: BrainConnectionFactory

Three iterations of the same method, demonstrating the enforced pattern.

**Iteration 1 — Violates the Core contract.** Throws across the public API.

```csharp
// ❌ WRONG — throws across the Core public API boundary.
public SqliteConnection Create(string connectionString)
{
    var connection = new SqliteConnection(connectionString);
    connection.Open();
    ApplyPragmas(connection);
    return connection;
}
```

**Iteration 2 — Returns a Result, but still wrong.** Single generic catch erases the distinction between locked, corrupt, and unrelated failures.

```csharp
// ❌ WRONG — single Exception catch, no granularity, no cancellation.
public Result<SqliteConnection> Create(string connectionString)
{
    try
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ApplyPragmas(connection);
        return Result<SqliteConnection>.Ok(connection);
    }
    catch (Exception ex)
    {
        return Result<SqliteConnection>.Fail(ErrorCode.Unknown, "Failed.", ex);
    }
}
```

**Iteration 3 — Correct.** Granular exception mapping; disposal on every failure path. Async variant with `OperationCanceledException` as the first catch.

```csharp
public async Task<Result<SqliteConnection>> CreateAsync(
    string connectionString,
    CancellationToken ct)
{
    SqliteConnection? connection = null;
    try
    {
        connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await ApplyPragmasAsync(connection, ct);
        return Result<SqliteConnection>.Ok(connection);
    }
    catch (OperationCanceledException ex)               // MUST be first.
    {
        connection?.Dispose();
        return Result<SqliteConnection>.Fail(ErrorCode.Cancelled,
            "Brain connection open cancelled.", ex);
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 11 /* SQLITE_CORRUPT */)
    {
        connection?.Dispose();
        return Result<SqliteConnection>.Fail(ErrorCode.DatabaseCorrupt,
            "SQLite reports the brain database file is corrupt.", ex);
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6)
    {
        connection?.Dispose();
        return Result<SqliteConnection>.Fail(ErrorCode.DatabaseLocked,
            "SQLite reports the brain database is busy or locked.", ex);
    }
    catch (SqliteException ex)
    {
        connection?.Dispose();
        return Result<SqliteConnection>.Fail(ErrorCode.DatabaseWriteFailed,
            $"SQLite failed to open brain. SqliteErrorCode={ex.SqliteErrorCode}.", ex);
    }
    catch (UnauthorizedAccessException ex)
    {
        connection?.Dispose();
        return Result<SqliteConnection>.Fail(ErrorCode.DatabaseWriteFailed,
            "Access denied opening brain database file.", ex);
    }
    catch (IOException ex)
    {
        connection?.Dispose();
        return Result<SqliteConnection>.Fail(ErrorCode.DatabaseWriteFailed,
            "I/O error opening brain database file.", ex);
    }
    catch (Exception ex)
    {
        connection?.Dispose();
        return Result<SqliteConnection>.Fail(ErrorCode.Unknown,
            "Unexpected error opening brain connection.", ex);
    }
}
```

**Points enforced by this implementation:**

- Returns `Result<SqliteConnection>` — never raw `SqliteConnection`.
- `OperationCanceledException` is the first catch.
- `SqliteException` is filtered by `SqliteErrorCode` into three distinct codes.
- Distinct file-access exceptions map under `DatabaseWriteFailed` with type and message preserved.
- Generic `Exception` catch is present and last, mapped to `ErrorCode.Unknown`.
- Partially constructed connection is disposed on every failure path.
- Every `Result.Fail` passes the exception to `ErrorContext`.

---

## 7. Logging Architecture

### 7.1 Design

```
FlashSkink.Core, Presentation  → Microsoft.Extensions.Logging.Abstractions (ILogger<T> only)
FlashSkink.UI.Avalonia, CLI    → Serilog + Serilog.Extensions.Logging (wired in Program.cs)
FlashSkink.Tests               → MEL InMemory or Xunit sink
```

`FlashSkink.Core` and `FlashSkink.Presentation` depend only on the MEL abstractions. The host (UI or CLI) wires Serilog. This allows tests to substitute a no-op or in-memory logger without dragging Serilog into the test process.

### 7.2 Result Context vs. Log Context

| | `ErrorContext` in Result | Logging |
|---|---|---|
| **Purpose** | Tell the caller what went wrong | Record what happened during execution |
| **Audience** | ViewModels, CLI handlers, tests | Log files, debugging, support bundles |
| **When written** | At the point of failure | Throughout execution |
| **Who writes** | Core internals | Core internals + Presentation on Result handling |

Core logs internally and returns a failure Result. The caller logs the `ErrorContext` when handling it. The same event is never logged twice.

### 7.3 Log File Location and Rotation

```
[USB_ROOT]/.flashskink/logs/flashskink-yyyy-MM-dd.log
```

- Rolling: daily + on 50 MB size limit
- Retention: 14 files
- Format: Compact JSON (structured)
- Minimum level: `Information` in release, `Debug` in development (configurable via `config.json`)

```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("App", "FlashSkink")
    .Enrich.WithProperty("Version", AppVersion.Current)
    .WriteTo.File(
        path: Path.Combine(SkinkRoot, ".flashskink", "logs", "flashskink-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        formatter: new CompactJsonFormatter())
    .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Warning) // CLI only
    .CreateLogger();
```

### 7.4 Structured Logging Properties

```csharp
using (_logger.BeginScope(new Dictionary<string, object>
{
    ["OperationId"] = operationId,
    ["FileID"]      = fileId,
    ["BlobID"]      = blobId
}))
{
    _logger.LogInformation("Phase 1 commit started. Size={Size}", sizeBytes);
    _logger.LogInformation("Phase 1 committed. ElapsedMs={ElapsedMs}", sw.ElapsedMilliseconds);
}
```

Standard properties: `OperationId` (Guid; correlates all entries for one user action), `FileID`, `BlobID`, `ProviderID`, `Phase`, `AttemptNumber`, `BytesUploaded`, `ElapsedMs`, `ErrorCode`.

### 7.5 Log Levels

| Level | Used For |
|---|---|
| `Trace` | Range-upload progress, compression ratios, nonce generation |
| `Debug` | Phase transitions, retry attempts, provider selection, queue scheduling |
| `Information` | Volume opened, file written, upload completed, audit completed |
| `Warning` | Provider degraded, WAL record recovered, retry succeeded, USB removed |
| `Error` | Upload failed after retries, integrity check failed, blob corrupt |
| `Critical` | Brain corrupt, vault decryption failed, all tails unreachable |

### 7.6 What Is Never Logged

DEK, KEK, OAuth tokens, passwords, mnemonics, recovery phrases, encrypted blob bytes, file content. `ErrorContext.Metadata` must never contain properties named `*Token`, `*Key`, `*Password`, `*Secret`, `*Mnemonic`, or `*Phrase`. Enforced by code review and a CI lint that scans for these patterns.

---

## 8. Notification Bus

### 8.1 Problem

Background services (`UploadQueueService`, `AuditService`, `SelfHealingService`, `UsbMonitorService`, `HealthMonitorService`, brain mirror task) run without a user-initiated call stack. When they fail, there is no awaiting ViewModel to receive a `Result`. Without an explicit mechanism, failures disappear silently.

### 8.2 Design

`Channel<T>` (`System.Threading.Channels`) is the implementation primitive. It is built-in, allocation-efficient, and designed for the producer/consumer pattern — multiple background services writing, one dispatcher reading.

`BoundedChannel` with `DropOldest` ensures a runaway service cannot queue unbounded notifications. Capacity of 100 is sufficient; a flood from a single source indicates a systemic issue better described by one notification with a count, not 100 individual ones.

A single `ChannelReader<Notification>` is consumed by a `NotificationDispatcher` that fans out to registered `INotificationHandler` implementations. The GUI registers one handler. The persistence layer registers another. The CLI registers a stderr writer.

`IObservable<T>` is explicitly not used. Broadcast semantics are achieved via the fan-out dispatcher without a reactive dependency.

### 8.3 Interfaces and Implementation

**Where the types live.** The contracts (`INotificationBus`, `INotificationHandler`, `Notification`, `NotificationSeverity`) live in `FlashSkink.Core.Abstractions.Notifications`. The implementations (`NotificationBus`, `NotificationDispatcher`) live in `FlashSkink.Presentation.Notifications`. The persistence handler (`PersistenceNotificationHandler`) lives in `FlashSkink.Core/Engine/`. Rationale: every background service that needs to publish — `WritePipeline`, `ReadPipeline`, `UploadQueueService`, `AuditService`, `SelfHealingService`, `UsbMonitorService`, `HealthMonitorService`, the brain mirror task — lives in `FlashSkink.Core`. Per §4.2, `FlashSkink.Core` references only `FlashSkink.Core.Abstractions`. Putting the contracts in `Abstractions` is the only way Core publishers can call `INotificationBus.PublishAsync` and Core handlers (the persistence one) can implement `INotificationHandler` without violating the dependency graph. The same logic applies that puts `Result`, `ErrorContext`, and the model records in `Abstractions` — these are cross-layer contracts, not Presentation concerns. Implementations stay in Presentation because the dispatcher's fan-out is consumer-facing infrastructure: GUI handlers, CLI handlers, dedup-and-summary policy.

```csharp
// In FlashSkink.Core.Abstractions
namespace FlashSkink.Core.Abstractions.Notifications;

public interface INotificationBus
{
    ValueTask PublishAsync(Notification notification, CancellationToken ct = default);
}

public interface INotificationHandler
{
    ValueTask HandleAsync(Notification notification, CancellationToken ct);
}

public sealed class Notification
{
    public required string               Source     { get; init; }
    public required NotificationSeverity Severity   { get; init; }
    public required string               Title      { get; init; }
    public required string               Message    { get; init; }
    public ErrorContext?                 Error      { get; init; }
    public DateTime                      OccurredUtc { get; init; } = DateTime.UtcNow;
    public bool                          RequiresUserAction { get; init; }
}

public enum NotificationSeverity { Info, Warning, Error, Critical }
```

```csharp
// In FlashSkink.Presentation
namespace FlashSkink.Presentation.Notifications;

public sealed class NotificationBus : INotificationBus, IAsyncDisposable
{
    private readonly Channel<Notification> _channel;
    private readonly NotificationDispatcher _dispatcher;
    private readonly Task _dispatchLoop;

    public NotificationBus(NotificationDispatcher dispatcher, ILogger<NotificationBus> logger)
    {
        _dispatcher = dispatcher;
        _channel = Channel.CreateBounded<Notification>(new BoundedChannelOptions(100)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _dispatchLoop = DispatchLoopAsync();
    }

    public async ValueTask PublishAsync(Notification notification, CancellationToken ct = default)
        => await _channel.Writer.WriteAsync(notification, ct);

    private async Task DispatchLoopAsync()
    {
        await foreach (var notification in _channel.Reader.ReadAllAsync())
        {
            try { await _dispatcher.DispatchAsync(notification, CancellationToken.None); }
            catch { /* Defence in depth — dispatcher already swallows handler exceptions */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        await _dispatchLoop;
        await _dispatcher.DisposeAsync();
    }
}
```

`NotificationDispatcher` (also in `FlashSkink.Presentation.Notifications`) owns the registered handler list, the `(Source, ErrorCode)` deduplication state, and the periodic flush of suppressed-count summaries (§8.4). Handler exceptions inside `DispatchAsync` are caught and logged at `Warning` — a misbehaving handler must not interrupt fan-out.

**Visibility — `public sealed` for both classes.** `NotificationBus` and `NotificationDispatcher` are `public sealed`, not `internal`. They are the concrete types host projects construct in `Program.cs` for DI registration, and they are the types tests need to instantiate directly. `internal` would force either `[InternalsVisibleTo]` (which the test convention in CLAUDE.md discourages) or a public factory façade with no encapsulation benefit — these are behavior containers, not data carriers, so there is nothing to hide. The interfaces in `Core.Abstractions.Notifications` remain the registration target; the concrete classes are just normal public infrastructure.

### 8.4 Deduplication

A flapping provider can generate many `ProviderUnreachable` notifications in quick succession. The dispatcher deduplicates by `Source + ErrorCode` within a configurable cooldown window (default: 60 seconds). The first notification dispatches immediately; subsequent identical ones within the window update a counter but are not re-dispatched. When the window expires, a single "X occurrences" notification is dispatched if the count exceeded 1.

### 8.5 Background Failure Persistence

Background failures must survive until the user sees them — including cases where they occur while the UI is not focused, the CLI has already exited, or the app is restarted. A `BackgroundFailures` table in the brain provides this persistence.

**Write path:** A `PersistenceNotificationHandler` — implemented in `FlashSkink.Core/Engine/`, registered on the bus alongside the UI handler — writes every `Error` and `Critical` notification to `BackgroundFailures` immediately via `BackgroundFailureRepository`. `Info` and `Warning` are not persisted — they are transient. The handler lives in Core (not Presentation) because it implements `INotificationHandler` (an `Abstractions` contract per §8.3) and consumes `BackgroundFailureRepository` (a Core type); both are reachable from Core directly.

**Read path on launch:** Before any user operations, query for unacknowledged rows ordered by `OccurredUtc DESC`. Surface as a notification summary: *"3 background failures since your last session."* Each is dismissible; dismissal sets `Acknowledged = 1`.

**Edge case:** If the `BackgroundFailures` write itself fails (USB removed, brain locked), the failure is logged to the Serilog file sink only. You cannot store what you cannot store.

### 8.6 End-to-End Flow

```
Background service encounters failure
    │
    ├──► Result.Fail(...) returned internally
    ├──► ILogger.LogError(...)            → USB log file
    ├──► INotificationBus.PublishAsync   → Channel<Notification>
    │         │
    │         └──► NotificationDispatcher (fan-out):
    │                   ├── PersistenceHandler  → BackgroundFailures (Error/Critical only)
    │                   ├── UiHandler           → GUI status indicator
    │                   └── CliHandler          → stderr
    │
On next launch (if not yet acknowledged):
    └──► BackgroundFailures query → unacknowledged rows → surfaced on startup
```

---

## 9. Memory Management

### 9.1 Where GC Pressure Originates

The write pipeline and the upload queue are the hot paths. A 10 GB file processed naively allocates 10 GB through `MemoryStream` growth and per-buffer copies. Pressure points:

- **Pipeline stages** (compress, encrypt, hash) copying to a new `byte[]` per pass.
- **Upload range loop** materialising each range into a fresh `byte[]`.
- **Audit and self-healing** reading blob metadata in tight loops, allocating a class instance per row.
- **Intermediate streams** (`MemoryStream` backing arrays growing and never releasing).
- **Crypto scratch buffers** (nonces, GCM tags, hash digests) allocated on heap unnecessarily.

Lower-frequency operations (OAuth flows, brain schema operations, UI bindings, folder navigation) are not hot paths and are not subject to these constraints.

### 9.2 Pipeline Buffer Strategy: ArrayPool and MemoryPool

All pipeline stages pass buffers as `Memory<byte>` or `ReadOnlyMemory<byte>`. **Ownership is explicit:** the caller rents the buffer and is responsible for returning it.

**Ownership rule (the canonical pattern):** Methods that produce a buffer return `IMemoryOwner<byte>`. The caller disposes the owner when the buffer is no longer needed. This is the single ownership pattern across the codebase. Methods do not return `Memory<byte>` views over pooled buffers — the caller has no way to return the buffer in that case.

```csharp
// Producer
public async Task<Result<IMemoryOwner<byte>>> CompressAsync(
    ReadOnlyMemory<byte> input,
    CancellationToken ct)
{
    var owner = MemoryPool<byte>.Shared.Rent(estimatedCompressedSize);
    try
    {
        int written = _zstd.Compress(input.Span, owner.Memory.Span);
        return Result<IMemoryOwner<byte>>.Ok(new SlicedOwner(owner, 0, written));
    }
    catch
    {
        owner.Dispose();
        throw; // caught by the public boundary translator
    }
}

// Consumer
using var compressed = await _compressor.CompressAsync(plaintext, ct);
if (!compressed.Success) return Result.Fail(compressed.Error!);
await _crypto.EncryptAsync(compressed.Value!.Memory, ..., ct);
// owner disposes when 'using' scope exits
```

The `SlicedOwner` wrapper carries the original `IMemoryOwner<byte>` and a length, so the consumer sees a `Memory<byte>` of exactly the meaningful bytes.

### 9.3 Span for Synchronous In-Place Operations

`Span<byte>` and `ReadOnlySpan<byte>` for all synchronous stack-safe operations:

**Magic-byte and entropy detection** — `ReadOnlySpan<byte>` over a 16-byte header rented once at pipeline entry, shared between `FileTypeService` and `EntropyDetector`:

```csharp
private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
private static bool IsZip(ReadOnlySpan<byte> header) => header.StartsWith(ZipMagic);
```

**XXHash64 computation** — `System.IO.Hashing.XxHash64.Hash(ReadOnlySpan<byte>)` operates directly on the span.

**SHA-256 computation** — `SHA256.HashData(ReadOnlySpan<byte>, Span<byte>)` for one-shot hashing; `IncrementalHash` reused across ranges for streamed hashing.

### 9.4 stackalloc for Small Crypto Buffers

Nonces, GCM tags, hash digests, and blob headers are small and short-lived. `stackalloc` keeps them off the heap entirely:

```csharp
Span<byte> nonce  = stackalloc byte[12];
Span<byte> tag    = stackalloc byte[16];
Span<byte> digest = stackalloc byte[32];
Span<byte> header = stackalloc byte[BlobHeader.SizeBytes];

RandomNumberGenerator.Fill(nonce);
aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);
SHA256.HashData(plaintext, digest);
```

`stackalloc` is safe here because these buffers are consumed synchronously before any `await`. Never use `stackalloc` across an `await` boundary — the compiler rejects it; the rule is worth understanding.

### 9.5 Streaming Reads from USB Blobs

When uploading a blob in ranges, a single pooled buffer is used for the entire upload regardless of file size:

```csharp
const int RangeSize = 4 * 1024 * 1024; // 4 MB

await using var blobStream = File.OpenRead(blobPath);
blobStream.Seek(session.BytesUploaded, SeekOrigin.Begin);

byte[] buffer = ArrayPool<byte>.Shared.Rent(RangeSize);
try
{
    while (session.BytesUploaded < session.TotalBytes)
    {
        int read = await blobStream.ReadAsync(buffer.AsMemory(0, RangeSize), ct);
        if (read == 0) break;

        var rangeResult = await _provider.UploadRangeAsync(
            session, session.BytesUploaded,
            buffer.AsMemory(0, read), ct);

        if (!rangeResult.Success)
            return rangeResult;

        session = session with { BytesUploaded = session.BytesUploaded + read };
        await _sessionRepository.UpdateAsync(session, ct);
    }
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
}
```

One buffer, reused for the entire upload. A 10 GB file uses 4 MB of RAM for upload, not 10 GB.

### 9.6 RecyclableMemoryStream

Replace all `new MemoryStream()` with `RecyclableMemoryStream` (`Microsoft.IO.RecyclableMemoryStream`). `MemoryStream` doubles its backing array on growth and never releases until GC. `RecyclableMemoryStream` is backed by pooled buffers and returns them on `Dispose`.

A single `RecyclableMemoryStreamManager` is registered as a singleton in the DI container:

```csharp
services.AddSingleton<RecyclableMemoryStreamManager>();

// Usage
using var ms = _streamManager.GetStream("compression-output");
await _compressor.CompressAsync(inputMemory, ms, ct);
```

### 9.7 Hot-Path SQLite Reads: Raw SqliteDataReader

Dapper maps query results via reflection and property setters or constructors. For `record struct` with `init`-only properties this breaks — Dapper cannot set `init` properties after construction. `readonly record struct` is unsupported entirely.

**Strategy:** Dapper for general, low-frequency queries (file listing, folder navigation, settings reads). Raw `SqliteDataReader` for allocation-sensitive hot paths.

```csharp
// General — Dapper, convenience over allocation
public async Task<IReadOnlyList<VolumeFile>> ListChildrenAsync(string? parentId)
    => (await _connection.QueryAsync<VolumeFile>(
        @"SELECT * FROM Files
          WHERE (ParentID = @pid OR (@pid IS NULL AND ParentID IS NULL))
          ORDER BY IsFolder DESC, Name ASC",
        new { pid = parentId })).AsList();

// Hot path — raw reader, record struct, zero heap allocation per row
public async IAsyncEnumerable<TailUploadStatus> ReadPendingUploadsAsync(
    [EnumeratorCancellation] CancellationToken ct)
{
    await using var cmd = _connection.CreateCommand();
    cmd.CommandText =
        "SELECT FileID, ProviderID, Status FROM TailUploads WHERE Status != 'UPLOADED'";

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        yield return new TailUploadStatus(
            FileId:     reader.GetString(0),
            ProviderId: reader.GetString(1),
            Status:     reader.GetString(2));
    }
}

public readonly record struct TailUploadStatus(
    string FileId,
    string ProviderId,
    string Status);
```

Hot paths using raw readers:
- Upload queue scanning (`UploadQueueService`)
- Audit reads (`AuditService`)
- Self-healing scans (`SelfHealingService`)
- Provider health aggregation (`HealthMonitorService`)

### 9.8 Object Reuse for Hot-Path Instances

`AesGcm`, `IncrementalHash`, and compression codec instances are expensive to construct. They are held as scoped singletons per pipeline operation, not instantiated per file:

```csharp
public sealed class CryptoPipeline : IDisposable
{
    private readonly AesGcm _aes;

    public CryptoPipeline(ReadOnlySpan<byte> dek)
        => _aes = new AesGcm(dek, tagSizeInBytes: 16);

    public void Dispose() => _aes.Dispose();
}
```

`CryptoPipeline` lives for the duration of the volume session. Each encrypt/decrypt call uses the same `AesGcm` instance with a fresh nonce.

### 9.9 Where These Rules Do Not Apply

Optimisations are concentrated in: `WritePipeline`, `ReadPipeline`, `CryptoPipeline`, `CompressionService`, `UploadQueueService`, `RangeUploader`, `AuditService`, `SelfHealingService`, `FileTypeService`, and the brain hot-path readers. They are explicitly **not** applied to:

- OAuth flows
- Brain schema operations
- Folder tree navigation
- Setup / teardown paths
- UI binding
- Configuration reads
- Activity log writes

The complexity cost of pooled buffers and value-type readers outweighs any benefit in those areas.

---

## 10. Provider Interfaces

The provider abstraction must support resumable uploads as a first-class concept (see §15). Upload is not a single `UploadAsync(Stream)` call — it is a session lifecycle.

### 10.1 IStorageProvider

```csharp
namespace FlashSkink.Core.Abstractions.Providers;

public interface IStorageProvider
{
    string ProviderID   { get; }
    string DisplayName  { get; }

    // Resumable upload session lifecycle
    Task<Result<UploadSession>> BeginUploadAsync(
        string remoteName,
        long totalBytes,
        CancellationToken ct);

    Task<Result<long>> GetUploadedBytesAsync(
        UploadSession session,
        CancellationToken ct);

    Task<Result> UploadRangeAsync(
        UploadSession session,
        long offset,
        ReadOnlyMemory<byte> data,
        CancellationToken ct);

    Task<Result<string>> FinaliseUploadAsync(
        UploadSession session,
        CancellationToken ct);  // returns provider-side RemoteId on success

    Task<Result> AbortUploadAsync(
        UploadSession session,
        CancellationToken ct);

    // Single-call download (provider SDKs handle streaming internally)
    Task<Result<Stream>> DownloadAsync(
        string remoteId,
        CancellationToken ct);

    // Direct operations on completed remote objects
    Task<Result> DeleteAsync(string remoteId, CancellationToken ct);
    Task<Result<bool>> ExistsAsync(string remoteId, CancellationToken ct);

    // Health and capacity
    Task<Result<ProviderHealth>> CheckHealthAsync(CancellationToken ct);
    Task<Result<long>> GetUsedBytesAsync(CancellationToken ct);
    Task<Result<long?>> GetQuotaBytesAsync(CancellationToken ct); // null if unknown/unlimited
}
```

### 10.2 UploadSession

```csharp
public sealed record UploadSession
{
    public required string FileID            { get; init; }
    public required string ProviderID        { get; init; }
    public required string SessionUri        { get; init; }    // provider-returned URI or session ID
    public required DateTimeOffset ExpiresAt { get; init; }
    public required long BytesUploaded       { get; init; }
    public required long TotalBytes          { get; init; }
    public DateTimeOffset LastActivityUtc    { get; init; } = DateTimeOffset.UtcNow;
}
```

`UploadSession` is a value type the provider uses to identify an in-flight upload. The `UploadQueueService` persists it in `UploadSessions` and passes it back on each `UploadRangeAsync` call.

### 10.3 IProviderSetup

```csharp
public interface IProviderSetup
{
    string ProviderType        { get; }    // "google-drive", "dropbox", "onedrive", "filesystem"
    string DisplayName         { get; }
    ProviderSetupKind SetupKind { get; }

    Task<Result<Uri>> GetAuthorizationUriAsync(
        string redirectUri,
        ProviderCredentials credentials);   // BYOC clientId + clientSecret

    Task<Result<byte[]>> ExchangeCodeAsync(
        string code,
        string redirectUri,
        ProviderCredentials credentials,
        byte[] dek);

    Task<Result<ValidationResult>> ValidatePathAsync(string path);

    Task<Result<IStorageProvider>> CreateProviderAsync(
        byte[] encryptedToken,
        ProviderCredentials credentials,
        byte[] dek);
}

public enum ProviderSetupKind { OAuth, ApiKey, LocalPath }

public sealed record ProviderCredentials
{
    public string? ClientId     { get; init; }
    public string? ClientSecret { get; init; }
}
```

For BYOC, `ProviderCredentials` carries the user-supplied OAuth app credentials. For `LocalPath` providers (FileSystem), credentials are null.

### 10.4 ProviderHealth

```csharp
public sealed record ProviderHealth
{
    public required ProviderHealthStatus Status        { get; init; }
    public required DateTimeOffset CheckedAt           { get; init; }
    public TimeSpan? RoundTripLatency                  { get; init; }
    public string? Detail                              { get; init; }
}

public enum ProviderHealthStatus
{
    Healthy,        // Last check succeeded
    Degraded,       // Recent failures, retries in progress
    Unreachable,    // Sustained failure
    AuthFailed,     // Token expired or revoked
    QuotaExceeded   // Provider reports out of space
}
```

### 10.5 Stability Promise

The contracts in this section are frozen before V1 ships. New providers may be added; method signatures may not change. Additive evolution (new optional methods, new enum values) is permitted in minor versions; breaking changes require a major version.

---

## 11. Core Public API

The single entry point for all volume operations.

```csharp
namespace FlashSkink.Core.Orchestration;

public sealed class FlashSkinkVolume : IAsyncDisposable
{
    // Volume lifecycle
    public static Task<Result<FlashSkinkVolume>> CreateAsync(VolumeCreationOptions options);
    public static Task<Result<FlashSkinkVolume>> OpenAsync(string skinkRoot, string password);
    public static Task<Result<FlashSkinkVolume>> RecoverAsync(RecoveryOptions options);

    // File operations (return at Phase 1 commit; uploads queue in background)
    public Task<Result<WriteReceipt>> WriteFileAsync(
        Stream source, string virtualPath, CancellationToken ct);

    public Task<Result> ReadFileAsync(
        string virtualPath, Stream destination, CancellationToken ct);

    public Task<Result> DeleteFileAsync(
        string virtualPath, CancellationToken ct);

    // Folder operations
    public Task<Result<VolumeFile>> CreateFolderAsync(
        string name, string? parentId, CancellationToken ct);

    public Task<Result> DeleteFolderAsync(
        string folderId, bool confirmed, CancellationToken ct);

    public Task<Result> RenameFolderAsync(
        string folderId, string newName, CancellationToken ct);

    public Task<Result> MoveAsync(
        string fileId, string? newParentId, CancellationToken ct);

    // Listing
    public Task<Result<IReadOnlyList<VolumeFile>>> ListChildrenAsync(
        string? parentId, CancellationToken ct);

    public Task<Result<IReadOnlyList<VolumeFile>>> ListFilesAsync(
        string virtualPath, CancellationToken ct);

    // Bulk operations (partial-failure-aware)
    public Task<Result<BulkWriteReceipt>> WriteBulkAsync(
        IReadOnlyList<BulkWriteItem> items, CancellationToken ct);

    // Restore (within the volume; not full recovery)
    public Task<Result> RestoreFromGracePeriodAsync(
        string fileId, DateTimeOffset deletedAtOrLater, CancellationToken ct);

    // Tail management
    public Task<Result<TailInfo>> AddTailAsync(
        TailConfiguration config, CancellationToken ct);

    public Task<Result> RemoveTailAsync(
        string providerId, CancellationToken ct);

    public Task<Result<IReadOnlyList<TailInfo>>> ListTailsAsync(
        CancellationToken ct);

    // Health
    public Task<Result<HealthReport>> CheckHealthAsync(CancellationToken ct);
    public Task<Result<VerificationReport>> VerifyAsync(CancellationToken ct);

    // Export
    public Task<Result> ExportAsync(
        string targetDirectory, IProgress<ExportProgress>? progress, CancellationToken ct);

    // Activity log
    public Task<Result<IReadOnlyList<ActivityLogEntry>>> GetActivityAsync(
        DateTimeOffset since, CancellationToken ct);

    // Recovery phrase re-display (requires password + waiting period)
    public Task<Result<string>> RevealRecoveryPhraseAsync(
        string password, CancellationToken ct);

    // Password change
    public Task<Result> ChangePasswordAsync(
        string oldPassword, string newPassword, CancellationToken ct);

    public Task<Result> ResetPasswordAsync(
        string mnemonic, string newPassword, CancellationToken ct);

    // Events
    public event EventHandler<UsbRemovedEventArgs>?      UsbRemoved;
    public event EventHandler?                           UsbReinserted;
    public event EventHandler<TailStatusChangedEventArgs>? TailStatusChanged;
}
```

### 11.1 Behavioural Notes

- **`WriteFileAsync` returns at Phase 1 commit.** The file is on the skink, encrypted and integrity-checked. The `WriteReceipt` includes a `FileID` and a notification token the caller can subscribe to for upload-completion events. Tail uploads happen asynchronously.
- **`DeleteFolderAsync(confirmed: false)` on a non-empty folder** returns `Result.Fail(ErrorCode.ConfirmationRequired)` with `ErrorContext.Metadata["ChildCount"]` populated. The ViewModel uses this to build the confirmation dialog before retrying with `confirmed: true`.
- **`MoveAsync` accepts `newParentId = null`** to move an item to root.
- **`ListChildrenAsync(parentId: null)`** returns all root-level items.
- **`AddTailAsync`** queues every existing file for upload to the new tail. Returns immediately with the tail info; uploads proceed in the background.
- **`RemoveTailAsync`** deletes the OAuth token, queue rows, and session rows for the tail. Does not delete data on the provider side (user must do this manually if desired).
- **`RevealRecoveryPhraseAsync`** requires the password and enforces a 60-second countdown before returning the phrase (Decision A16-d). The countdown is server-side in Core, not just a UI affectation — a malicious caller cannot skip it.
- **`VerifyAsync`** walks every blob and every tail, confirming integrity. Long-running; respects cancellation.
- **`ExportAsync`** writes every file in its original directory structure to the target. Used for migration out of FlashSkink (Decision A15-a).

---

## 12. Presentation Layer Contracts

The Presentation layer holds ViewModels and platform-agnostic interfaces. UI projects implement these interfaces; Core never references them directly.

```csharp
namespace FlashSkink.Presentation.Interfaces;

public interface INavigationService
{
    Task NavigateToAsync<TViewModel>() where TViewModel : BaseViewModel;
    Task NavigateBackAsync();
}

public interface IDialogService
{
    Task ShowErrorAsync(string title, string message);
    Task ShowInfoAsync(string title, string message);
    Task<bool> ShowConfirmAsync(string title, string message);
    Task<string?> ShowInputAsync(string title, string prompt, bool masked = false);
}

public interface IFilePickerService
{
    Task<string?> PickFileAsync(string title, IEnumerable<string> allowedExtensions);
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickSaveLocationAsync(string title, string defaultFileName);
    Task<IReadOnlyList<string>> PickFilesAsync(string title);
}

public interface IBrowserService
{
    Task OpenAsync(Uri uri);
}

public interface IClipboardService
{
    Task CopyToClipboardAsync(string text);
}
```

These interfaces are deliberately small. Adding methods is a deliberate decision; each new method is a new dependency every UI implementation must satisfy.

---

## 13. Storage Architecture

### 13.1 The Nomadic Volume

A FlashSkink volume consists of:

- **The skink (USB):** The brain (encrypted SQLite), the vault, configuration, logs, executables, and the local blob store.
- **The tails (N providers):** Encrypted blobs and an encrypted brain mirror.

The skink is authoritative. Tails are catch-up replicas.

### 13.2 The Mirror Model

Each tail holds:

- A complete copy of every encrypted blob from the skink.
- A periodic encrypted mirror of the brain (for recovery scenarios).

Writes go to the skink first (Phase 1, transactional). Uploads to tails happen asynchronously (Phase 2, resumable). Reads happen from the skink directly. Tails are touched only for upload, integrity verification, recovery, and self-healing.

### 13.3 No Staging on Host Temp

The original RAID-5 design used host temp for stripe staging. The mirror model has no staging requirement: encryption happens during the Phase 1 write directly to the skink, and uploads stream from the skink blob to each tail.

`Path.GetTempPath()` is **not used** in the normal write or upload paths. Eliminates host-side debris and the "different host on resume" problem.

The single legitimate use of temp space is during recovery (where blobs from a tail are downloaded and decrypted to reconstruct a new skink). Even then, the temp directory is on the new skink itself, not the host.

### 13.4 Atomic File-Level Writes

The Phase 1 commit must be durable across power loss and USB removal. The write protocol:

1. Generate a `BlobID` (UUID).
2. Compute the destination path: `[USB]/.flashskink/blobs/{BlobID[0:2]}/{BlobID[2:4]}/{BlobID}.bin`
3. Write the encrypted blob to a temp path: `[USB]/.flashskink/staging/{BlobID}.tmp`
4. `fsync` the temp file.
5. Atomic rename to the destination path.
6. `fsync` the destination directory (to persist the rename).
7. Insert/update the brain row inside a transaction.
8. Commit the transaction.
9. Brain `fsync` is implicit via SQLite's `synchronous=EXTRA`.

If power is lost before step 5, the temp file is orphaned and cleaned by WAL recovery. If lost after step 5 but before step 8, the blob exists but no brain row references it; orphaned and cleaned by WAL recovery. If lost after step 8, the write is durable.

**FAT32 caveat:** FAT32 (the default format on most consumer USB drives) does not support atomic rename in the POSIX sense. The blueprint assumes the skink is formatted exFAT or NTFS (Windows), HFS+/APFS (macOS), or ext4 (Linux). Setup detects FAT32 and warns the user with a recommendation to reformat. A future version may add a "FAT32 compatibility mode" with weaker guarantees.

### 13.5 Blob Storage Layout

```
[USB]/.flashskink/blobs/
    ab/
        cd/
            abcd1234-....bin
            abcdef56-....bin
        cf/
            ...
    bf/
        ...
```

Two-level sharding by the first 4 hex chars of the BlobID UUID. 65,536 leaf directories. Prevents single-directory slowness at scale (FAT32 struggles past ~65K files per directory; even modern filesystems benefit from sharding for fast directory operations).

### 13.6 Blob Format

Each blob file on disk is a single concatenated structure with a small versioned header (Decision B4-c):

```
Offset  Size   Field
------  ----   -----
0       4      Magic ("FSBL" — FlashSkink BLob)
4       2      Version (uint16, currently 1)
6       2      Flags (uint16; see below)
8       12     Nonce (random per blob)
20      N      Ciphertext (AES-256-GCM, AAD per "AAD construction" below)
20+N    16     GCM Tag
```

**Flags (bit positions):**
- Bit 0: Compressed with LZ4
- Bit 1: Compressed with Zstd
- Bit 2: Reserved
- Bits 3-15: Reserved (must be zero in V1)

**AAD construction:**

The Additional Authenticated Data fed to `AesGcm.Encrypt` / `AesGcm.Decrypt` is a fixed 48-byte buffer constructed in this exact byte order:

```
Offset  Size   Field
------  ----   -----
0       16     BlobID — raw GUID bytes (RFC 4122 binary form, .NET's Guid.TryWriteBytes)
16      32     PlaintextSHA256 — raw 32-byte digest from SHA256.HashData
Total:  48     bytes
```

The string forms — the 36-character canonical UUID (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`) and the 64-character lowercase hex digest — are **never** used as AAD input. The fixed 48-byte size is `stackalloc`-friendly and avoids any UTF-8 encoding decisions on the hot path.

This pin matters operationally: every blob ever written carries the AAD baked into its GCM authentication tag. Changing the encoding later (string ↔ raw, byte order, length) is a wire-format break that invalidates every existing blob's authentication, with no migration path short of decrypt-and-re-encrypt every blob in the volume. Implementations that accidentally feed the string form will fail decryption against blobs written by a correct implementation, and vice versa — the failure mode is `ErrorCode.CryptoFailed` on every read, which will look like total volume corruption.

The AAD's two components ensure two distinct properties:
- **BlobID component** — binds the ciphertext to its specific BlobID, preventing blob-substitution attacks where one blob's bytes are swapped onto another row within the same volume.
- **PlaintextSHA256 component** — binds the ciphertext to the declared plaintext hash recorded in `Blobs.PlaintextSHA256`. A tampered brain row (e.g., a SHA-256 changed to match different plaintext) will fail GCM authentication on read.

**Versioning policy:**
- New flag bits are additive and don't increment Version.
- Adding new fields after the existing ones requires Version increment and corresponding read code that handles both versions.
- Removing or changing existing fields is a breaking change requiring a major-version migration.
- The AAD format is part of the wire format. Changes to the AAD construction are a major-version migration even if no on-disk byte layout changes.

The 20-byte header gives forward compatibility for future cipher changes, KDF parameter updates, or compression algorithm additions.

### 13.7 Blob Integrity

Each blob has three integrity guarantees:

1. **GCM authentication tag** — detects ciphertext tampering. Verified on every decrypt.
2. **Plaintext SHA-256** — stored in the brain's `Blobs` table. Verified after decryption to detect any pipeline failure.
3. **XXHash64 of ciphertext** — stored in the brain's `Blobs` table. Used by background audit to detect bit-rot on the skink without performing decryption (cheaper).

---

## 14. Data Processing Pipeline

### 14.1 Write Pipeline (Phase 1)

```
INPUT: Stream from user filesystem, virtualPath
  │
[0] FILE TYPE DETECTION  — Read 16-byte header (stackalloc).
                            FileTypeService → (Extension, MimeType)
                            EntropyDetector → IsCompressible
                            Stream rewound to position 0.
[1] CONTENT HASH         — IncrementalHash<SHA256> over plaintext stream
                            (computed in parallel with compression).
[2] COMPRESSION          — If !IsCompressible: skip.
                            Else: LZ4 if size < 512 KB, Zstd level 3 otherwise.
                            Output: pooled IMemoryOwner<byte>.
[3] ENCRYPTION           — AES-256-GCM, stackalloc nonce (12B), stackalloc tag (16B).
                            AAD: BlobID || PlaintextSHA256
                            Output: pooled IMemoryOwner<byte>.
[4] BLOB ASSEMBLY        — Header (8B) || Nonce (12B) || Ciphertext || Tag (16B).
[5] DURABLE WRITE        — Write to staging temp on USB, fsync, atomic rename, fsync dir.
[6] INTEGRITY HASH       — XXHash64 of final blob bytes for bit-rot detection.
[7] BRAIN COMMIT         — Transaction:
                              INSERT INTO Blobs (...)
                              INSERT INTO Files (...)
                              INSERT INTO TailUploads (...) per active tail
                              INSERT INTO ActivityLog (...)
                            Commit.
[8] CHANGE-DETECTION     — If a Blob with same PlaintextSHA256 already exists at the
                            same VirtualPath: short-circuit at step [2], no-op.
                            Returns Result.Ok(WriteReceipt with Status=Unchanged).

OUTPUT: Result<WriteReceipt> with FileID, BlobID, Status.
```

The pipeline returns success only when step 7 commits. Tail uploads are queued by the same transaction (step 7) but proceed asynchronously (Phase 2).

### 14.2 Read Pipeline

```
INPUT: virtualPath, destination Stream
  │
[1] BRAIN LOOKUP         — Files row → BlobID → Blobs row.
[2] BLOB OPEN            — Open .bin file at sharded path.
[3] HEADER PARSE         — Read 8-byte header. Verify magic, version, flags.
[4] DECRYPT              — Read nonce, ciphertext, tag.
                            AES-256-GCM decrypt.
                            AAD: BlobID || PlaintextSHA256 (from brain).
                            Verify GCM tag.
[5] DECOMPRESS           — Per Flags: LZ4 / Zstd / none.
[6] HASH VERIFY          — SHA-256 of decompressed plaintext.
                            Compare to Blobs.PlaintextSHA256.
                            Mismatch → ErrorCode.ChecksumMismatch.
[7] COPY TO DESTINATION  — Stream plaintext to destination.

OUTPUT: Result.Ok() on success.
```

Streaming is supported throughout: the read pipeline never materialises the full plaintext in memory (except for hash verification, which uses incremental hashing).

### 14.3 Bulk Write

When `WriteBulkAsync` is called with N items, each item is processed independently. The result is a `BulkWriteReceipt`:

```csharp
public sealed record BulkWriteReceipt
{
    public required int TotalItems { get; init; }
    public required int Succeeded  { get; init; }
    public required int Failed     { get; init; }
    public required IReadOnlyList<BulkWriteResult> Results { get; init; }
}

public sealed record BulkWriteResult
{
    public required string  VirtualPath { get; init; }
    public required bool    Success     { get; init; }
    public WriteReceipt?    Receipt     { get; init; }
    public ErrorContext?    Error       { get; init; }
}
```

Per Decision A24-a, partial failures do not abort the bulk operation. The caller receives a complete summary.

### 14.4 Pipeline Buffer Lifetime

All buffers used in the pipeline are explicitly owned via `IMemoryOwner<byte>`. The owner is disposed when the buffer is no longer needed (typically at the end of the using block in the calling stage). No buffer is shared across pipeline stages without ownership transfer.

---

## 15. Resumable Upload Sessions

### 15.1 Why Resumable Uploads Are First-Class

Each cloud provider exposes a resumable upload protocol. FlashSkink uses these protocols deliberately, persisting session state on the skink so uploads survive disconnection and resume on a different host.

This delivers two essential properties:

- **Cross-host resumability.** A user uploads 6 GB of a 10 GB file at home, unplugs the skink, brings it to work, plugs it in. The upload resumes from byte 6 GB on the new host with no manual intervention.
- **Bounded memory.** A single 4 MB pooled buffer handles uploads of arbitrary file size. A 10 GB upload uses 4 MB of RAM, not 10 GB.

### 15.2 Provider Protocol Mapping

| Provider | Protocol | Session validity | Session ID storage |
|---|---|---|---|
| Google Drive | Resumable Upload | 1 week | URI returned by initiate request |
| Dropbox | Upload Sessions | 48 hours from last append | Session ID returned by `upload_session/start` |
| OneDrive | createUploadSession | Time-bounded (varies, typically days) | URL returned by createUploadSession |
| FileSystem | N/A (atomic write per range with offset tracking) | Indefinite | Internal — tracked via `BytesUploaded` only |

The `IStorageProvider` abstraction (§10) reduces these to a uniform set of operations: `BeginUploadAsync`, `GetUploadedBytesAsync`, `UploadRangeAsync`, `FinaliseUploadAsync`, `AbortUploadAsync`.

### 15.3 Session Lifecycle

```
1. Worker selects (FileID, ProviderID) from TailUploads where Status != UPLOADED.
2. Look up UploadSessions row for (FileID, ProviderID).
3. If row exists and not expired:
     a. Call provider.GetUploadedBytesAsync(session)
     b. Reconcile reported offset with stored BytesUploaded
     c. Resume from confirmed offset
4. If row exists and expired:
     a. Discard session (call AbortUploadAsync best-effort)
     b. Delete UploadSessions row
     c. Fall through to step 5
5. If no row:
     a. Open blob file on USB
     b. Call provider.BeginUploadAsync(remoteName, blobSize)
     c. Insert UploadSessions row with returned session URI
6. Loop:
     a. Read up to RangeSize (4 MB) from blob at BytesUploaded offset
     b. Call provider.UploadRangeAsync(session, offset, range)
     c. On success: increment BytesUploaded, update UploadSessions row
     d. On failure: backoff and retry from same offset
     e. On expired-session error mid-upload: goto 4
7. When BytesUploaded == TotalBytes:
     a. Call provider.FinaliseUploadAsync(session) → returns RemoteId
     b. Verify finalisation (provider-specific: ETag, hash check, etc.)
     c. Transaction:
          UPDATE TailUploads SET Status='UPLOADED', RemoteId=...
          DELETE FROM UploadSessions WHERE FileID=... AND ProviderID=...
8. On unrecoverable failure:
     a. Transaction:
          UPDATE TailUploads SET Status='FAILED', LastError=...
          DELETE FROM UploadSessions WHERE FileID=... AND ProviderID=...
     b. Publish notification to bus
```

### 15.4 Session Expiry Handling

A session expires when:
- Provider's TTL elapses without activity
- Provider explicitly invalidates the session (rare)
- The blob being uploaded has been deleted from the skink (user changed their mind)

On expiry detected mid-upload (provider returns 4xx with a specific error), the worker:
1. Marks the session as discarded
2. Calls `AbortUploadAsync` best-effort (failures here are logged but ignored)
3. Restarts from byte 0 with a fresh `BeginUploadAsync`

This is wasteful (redo work) but bounded: only files whose upload spans longer than the session validity window encounter this. For most files (small enough to upload in one session) it never happens.

### 15.5 Range Size

Range size is fixed at **4 MB** (Decision B3-b). Rationale:

- Matches Google Drive's recommended chunk size
- Aligns with the pooled buffer ArrayPool would naturally grant
- A failed range re-uploads quickly (4 MB at 1 MB/s is 4 seconds)
- DB writes for progress tracking happen per range — 4 MB granularity means roughly one DB write per second on a typical broadband connection
- Larger sizes risk hitting per-range timeouts on slow connections

Range size is not user-configurable. If V2+ identifies a need for adaptive sizing, it can be revisited.

### 15.6 Concurrency

Within a single tail, ranges of the same blob upload sequentially. Different blobs may upload concurrently to the same tail (provider permitting). Different tails always upload concurrently.

V1 default: 2 concurrent uploads per tail, configurable per tail in advanced config but not exposed in UI. Total upload concurrency is capped at `2 × tailCount` to avoid saturating the user's upload bandwidth with too many parallel streams.

### 15.7 Verification After Finalisation

After `FinaliseUploadAsync` returns success, the worker performs provider-appropriate verification:

| Provider | Verification |
|---|---|
| Google Drive | Compare blob's XXHash64 to the `md5Checksum` Drive returns (after computing MD5 over the local blob — done lazily, only at this point) |
| Dropbox | Compare blob size to Dropbox's reported size; Dropbox provides a content-hash for additional verification |
| OneDrive | Compare hash returned in metadata response |
| FileSystem | Re-read the file and compute XXHash64; compare to local blob's XXHash64 |

Verification failure → `ErrorCode.ChecksumMismatch`, blob is left in `FAILED` state, notification published. The next worker cycle will retry from scratch (delete remote object, restart upload).

### 15.8 Per-Tail Upload Worker

The upload queue is implemented as one long-running worker task per tail, plus one orchestration loop:

```
OrchestratorLoop:
  while not shutdown:
    foreach tail in active tails:
      if tail.Status != Healthy: continue
      ensure UploadWorker(tail) is running
    await wakeupSignal or timeout(30s)

UploadWorker(tail):
  while not shutdown and not paused:
    next = TailUploadsRepository.GetNextPending(tail.ProviderID)
    if next is null:
      await wakeupSignal or timeout(60s)
      continue
    result = await UploadFileAsync(next.FileID, tail, ct)
    HandleResult(result)
```

Workers wake on:
- A new file being committed to the brain (orchestrator publishes a wakeup signal)
- A timer (60s polling, in case wakeup signals are missed)
- Tail health transition from Degraded back to Healthy

---

## 16. Metadata Management (The Brain)

### 16.1 SQLite Configuration

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous  = EXTRA;
PRAGMA foreign_keys = ON;
PRAGMA temp_store   = MEMORY;
```

The brain database file is encrypted at rest using SQLCipher (Decision B9-a). The encryption key is derived from the DEK on volume open and zeroed on volume close.

### 16.2 Schema

```sql
-- ===== Schema versioning =====
CREATE TABLE SchemaVersions (
    Version       INTEGER PRIMARY KEY,
    AppliedUtc    TEXT NOT NULL,
    Description   TEXT NOT NULL
);

-- ===== Files: the user-facing tree =====
-- One row per file or folder. Tree structure via ParentID.
CREATE TABLE Files (
    FileID        TEXT PRIMARY KEY,
    ParentID      TEXT REFERENCES Files(FileID),    -- NULL = root
    IsFolder      INTEGER NOT NULL DEFAULT 0,
    IsSymlink     INTEGER NOT NULL DEFAULT 0,
    SymlinkTarget TEXT,                              -- non-NULL iff IsSymlink=1
    Name          TEXT NOT NULL,                     -- UTF-8 NFC-normalized
    Extension     TEXT,                              -- ".jpg", lowercase, dot-prefixed; NULL for folders/symlinks
    MimeType      TEXT,                              -- "image/jpeg"; NULL if undetectable
    VirtualPath   TEXT NOT NULL,                     -- denormalised full path; consistent with ParentID
    SizeBytes     INTEGER NOT NULL DEFAULT 0,        -- 0 for folders/symlinks
    CreatedUtc    TEXT NOT NULL,                     -- preserved from source filesystem
    ModifiedUtc   TEXT NOT NULL,                     -- preserved from source filesystem
    AddedUtc      TEXT NOT NULL,                     -- when added to FlashSkink
    BlobID        TEXT REFERENCES Blobs(BlobID)      -- NULL for folders and symlinks
);

-- Unique name per parent, enforced over current (non-soft-deleted) files.
CREATE UNIQUE INDEX IX_Files_Parent_Name
    ON Files (COALESCE(ParentID, ''), Name);

CREATE INDEX IX_Files_BlobID ON Files (BlobID) WHERE BlobID IS NOT NULL;
CREATE INDEX IX_Files_ParentID ON Files (ParentID);

-- ===== Blobs: encrypted file payloads =====
-- One row per encrypted blob on disk. Files reference Blobs by BlobID.
-- V1 enforces 1:1 between Files and Blobs (no dedup). Schema separation
-- preserves the option to add dedup in V2 without migration.
CREATE TABLE Blobs (
    BlobID            TEXT PRIMARY KEY,
    EncryptedSize     INTEGER NOT NULL,
    PlaintextSize     INTEGER NOT NULL,
    PlaintextSHA256   TEXT NOT NULL,
    EncryptedXXHash   TEXT NOT NULL,                 -- bit-rot check on USB blob
    Compression       TEXT,                          -- NULL | LZ4 | ZSTD
    BlobPath          TEXT NOT NULL,                 -- relative path on USB
    CreatedUtc        TEXT NOT NULL,
    SoftDeletedUtc    TEXT,                          -- NULL = active
    PurgeAfterUtc     TEXT                           -- when sweeper hard-deletes
);

CREATE INDEX IX_Blobs_PlaintextSHA256 ON Blobs (PlaintextSHA256);
CREATE INDEX IX_Blobs_PurgeAfterUtc ON Blobs (PurgeAfterUtc) WHERE PurgeAfterUtc IS NOT NULL;

-- ===== Providers (tails) =====
CREATE TABLE Providers (
    ProviderID         TEXT PRIMARY KEY,
    ProviderType       TEXT NOT NULL,                -- "google-drive" | "dropbox" | "onedrive" | "filesystem"
    DisplayName        TEXT NOT NULL,
    EncryptedToken     BLOB,                         -- DEK-encrypted OAuth refresh token
    TokenNonce         TEXT,
    EncryptedClientSecret BLOB,                      -- DEK-encrypted OAuth client secret (BYOC)
    ClientSecretNonce  TEXT,
    ClientId           TEXT,                         -- OAuth client ID (not secret)
    ProviderConfig     TEXT,                         -- JSON; provider-specific (e.g. {"rootPath":"/mnt/nas"})
    HealthStatus       TEXT NOT NULL,                -- Healthy | Degraded | Unreachable | AuthFailed | QuotaExceeded
    LastHealthCheckUtc TEXT,
    AddedUtc           TEXT NOT NULL,
    IsActive           INTEGER NOT NULL DEFAULT 1
);

-- ===== TailUploads: per-file per-tail upload status =====
CREATE TABLE TailUploads (
    FileID        TEXT NOT NULL REFERENCES Files(FileID) ON DELETE CASCADE,
    ProviderID    TEXT NOT NULL REFERENCES Providers(ProviderID),
    Status        TEXT NOT NULL,                    -- PENDING | UPLOADING | UPLOADED | FAILED
    RemoteId      TEXT,                              -- provider-side object ID, NULL until UPLOADED
    QueuedUtc     TEXT NOT NULL,
    UploadedUtc   TEXT,
    LastAttemptUtc TEXT,
    AttemptCount  INTEGER NOT NULL DEFAULT 0,
    LastError     TEXT,                              -- last ErrorCode + message
    PRIMARY KEY (FileID, ProviderID)
);

CREATE INDEX IX_TailUploads_PendingByProvider
    ON TailUploads (ProviderID, Status)
    WHERE Status != 'UPLOADED';

-- ===== UploadSessions: in-flight resumable upload state =====
-- Deleted on finalisation or abort. Survives disconnect/reconnect/host-change.
CREATE TABLE UploadSessions (
    FileID              TEXT NOT NULL REFERENCES Files(FileID),
    ProviderID          TEXT NOT NULL REFERENCES Providers(ProviderID),
    SessionUri          TEXT NOT NULL,
    SessionExpiresUtc   TEXT NOT NULL,
    BytesUploaded       INTEGER NOT NULL,
    TotalBytes          INTEGER NOT NULL,
    LastActivityUtc     TEXT NOT NULL,
    PRIMARY KEY (FileID, ProviderID)
);

-- ===== WAL: crash-recovery state machine =====
CREATE TABLE WAL (
    WALID         TEXT PRIMARY KEY,
    Operation     TEXT NOT NULL,                    -- WRITE | DELETE | CASCADE_DELETE | TAIL_DELETE | PURGE
    Phase         TEXT NOT NULL,                    -- PREPARE | COMMITTED | FAILED
    StartedUtc    TEXT NOT NULL,
    UpdatedUtc    TEXT NOT NULL,
    Payload       TEXT NOT NULL                     -- JSON: operation-specific context
);

-- ===== BackgroundFailures: persisted notification queue =====
CREATE TABLE BackgroundFailures (
    FailureID     TEXT PRIMARY KEY,
    OccurredUtc   TEXT NOT NULL,
    Source        TEXT NOT NULL,
    ErrorCode     TEXT NOT NULL,
    Message       TEXT NOT NULL,
    Metadata      TEXT,                              -- JSON
    Acknowledged  INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IX_BackgroundFailures_Unacked
    ON BackgroundFailures (OccurredUtc DESC)
    WHERE Acknowledged = 0;

-- ===== ActivityLog: user-facing audit trail =====
CREATE TABLE ActivityLog (
    EntryID       TEXT PRIMARY KEY,
    OccurredUtc   TEXT NOT NULL,
    Category      TEXT NOT NULL,                    -- WRITE | DELETE | RESTORE | TAIL_ADDED | TAIL_REMOVED | RECOVERY | EXPORT | VERIFY
    Summary       TEXT NOT NULL,                    -- short human-readable
    Detail        TEXT                               -- JSON, optional structured detail
);

CREATE INDEX IX_ActivityLog_OccurredUtc ON ActivityLog (OccurredUtc DESC);

-- ===== DeleteLog: append-only audit trail for hard-deletes =====
CREATE TABLE DeleteLog (
    LogID         TEXT PRIMARY KEY,
    DeletedAt     TEXT NOT NULL,
    FileID        TEXT NOT NULL,                    -- informational; row already gone
    Name          TEXT NOT NULL,
    VirtualPath   TEXT NOT NULL,
    IsFolder      INTEGER NOT NULL,
    Trigger       TEXT NOT NULL                     -- USER_ACTION | CASCADE | SWEEP
);

-- ===== Settings: key-value config =====
CREATE TABLE Settings (
    Key           TEXT PRIMARY KEY,
    Value         TEXT NOT NULL
);

-- Initial settings (set at volume creation):
--   "GracePeriodDays" = "30"
--   "AuditIntervalHours" = "24"
--   "VolumeCreatedUtc" = "..."
--   "AppVersion" = "1.0.0"
```

### 16.3 Schema Migration

Migrations are versioned scripts embedded in the binary. The `MigrationRunner` runs at volume open:

```
1. Open brain (with SQLCipher key).
2. Read Settings["AppVersion"] and SchemaVersions max(Version).
3. If schema version < expected:
     a. Mirror the brain DB to .flashskink/brain.db.pre-migration-{timestamp}
     b. For each missing version N:
        - Begin transaction
        - Execute migration script for version N
        - INSERT INTO SchemaVersions (Version=N, AppliedUtc=..., Description=...)
        - Commit
     c. On any failure: restore from pre-migration mirror, return Result.Fail
4. If schema version > expected:
     - Return Result.Fail(VolumeIncompatibleVersion)
     - User must update FlashSkink to a version that understands this schema
```

Migrations are forward-only and automatic (Decision A7-b, with the pre-migration backup safeguard). The `.pre-migration-{timestamp}` mirror is retained for one week then cleaned by the sweeper.

### 16.4 Folder Tree Queries

**List immediate children:**
```sql
SELECT * FROM Files
WHERE (ParentID = @parentId OR (@parentId IS NULL AND ParentID IS NULL))
ORDER BY IsFolder DESC, Name ASC;  -- folders first, then files, both alphabetical
```

**Recursive descendants (for cascade delete and path listing):**
```sql
WITH RECURSIVE descendants AS (
    SELECT * FROM Files WHERE FileID = @rootId
    UNION ALL
    SELECT f.* FROM Files f
    INNER JOIN descendants d ON f.ParentID = d.FileID
)
SELECT * FROM descendants;
```

**Ancestor chain (for cycle detection before move):**
```sql
WITH RECURSIVE ancestors AS (
    SELECT FileID, ParentID FROM Files WHERE FileID = @newParentId
    UNION ALL
    SELECT f.FileID, f.ParentID FROM Files f
    INNER JOIN ancestors a ON f.FileID = a.ParentID
)
SELECT FileID FROM ancestors WHERE FileID = @movingId;
-- If this returns a row, the move is cyclic — reject with ErrorCode.CyclicMoveDetected
```

**Recursive VirtualPath update on rename/move:**
```sql
WITH RECURSIVE descendants AS (
    SELECT FileID, VirtualPath FROM Files WHERE FileID = @folderId
    UNION ALL
    SELECT f.FileID, f.VirtualPath FROM Files f
    INNER JOIN descendants d ON f.ParentID = d.FileID
)
UPDATE Files SET
    VirtualPath = @newPrefix || SUBSTR(VirtualPath, @oldPrefixLength + 1),
    ModifiedUtc = @now
WHERE FileID IN (SELECT FileID FROM descendants);
```

### 16.5 Folder Behavioural Rules

| Rule | Decision |
|---|---|
| **Delete non-empty folder** | Requires `confirmed = true`. `DeleteFolderAsync(confirmed: false)` returns `ConfirmationRequired` with child count in metadata. The ViewModel calls `IDialogService.ShowConfirmAsync` then retries. |
| **Upload to non-existent parent** | Auto-creates all missing intermediate folder rows via `FileRepository.EnsureFolderPathAsync`. Idempotent and transactional. If a path segment exists as a file (not a folder), returns `PathConflict`. |
| **Root contents** | `ParentID = NULL` is valid for both files and folders. Root items are freely mixed. |
| **Name uniqueness** | Unique per parent enforced by `IX_Files_Parent_Name`. Constraint violation mapped to `PathConflict`. |
| **Rename folder** | Cascades `VirtualPath` update to all descendants in a single transaction. Pre-checks for name conflict. |
| **Move file or folder** | `MoveAsync` supports any item to any parent (including root via `newParentId = null`). Cycle detection runs before the update via ancestor CTE. |
| **Delete semantics** | Hard-delete with grace period: `Files` row removed immediately; the underlying `Blob` is marked `SoftDeletedUtc=now, PurgeAfterUtc=now+grace`. The blob remains on USB and tails until the sweeper hard-deletes after the grace period. |
| **DeleteLog** | Written for every removal, in the same transaction as the row deletion. |

### 16.6 FileRepository Methods (selected)

| Method | Notes |
|---|---|
| `InsertAsync(VolumeFile file, BlobID? blobId)` | Inserts file or folder row. Maps constraint violation → `PathConflict`. |
| `GetByIdAsync(FileID)` | Single row. |
| `ListChildrenAsync(parentId?)` | Immediate children. `null` = root. Folders-first ordering. |
| `ListFilesAsync(virtualPath)` | All descendants under a path prefix. Dapper. |
| `EnsureFolderPathAsync(virtualPath)` | Walks segments, creates missing folders. Returns leaf `ParentID`. Idempotent. |
| `CountChildrenAsync(folderId)` | For confirmation dialog. |
| `GetDescendantsAsync(folderId)` | Recursive CTE. |
| `DeleteFolderCascadeAsync(folderId)` | Cascades hard-delete of folder rows; soft-deletes referenced blobs. WAL `CASCADE_DELETE`. |
| `DeleteFileAsync(fileId)` | Removes Files row; soft-deletes blob. WAL `DELETE`. |
| `RenameFolderAsync(folderId, newName)` | Updates `Name` + cascades `VirtualPath`. Single transaction. |
| `MoveAsync(fileId, newParentId?)` | Updates `ParentID` + `VirtualPath`. For folders: cascades. Cycle check first. |
| `RestoreFromGracePeriodAsync(blobId, virtualPath)` | Re-inserts a Files row pointing at a soft-deleted blob, clears soft-delete fields. |

### 16.7 Brain Backup (Mirror to Tails)

The brain database is mirrored as an AES-256-GCM encrypted vault to all active tails after every write commit, every 15 minutes (timer-driven), and on clean shutdown. Three backups retained per tail (rolling). On recovery, the most recent valid mirror is used.

The mirror process:
1. SQLite `BACKUP TO` produces a consistent snapshot (no need to lock the live DB).
2. The snapshot file is encrypted with the DEK (AES-256-GCM, fresh nonce).
3. The encrypted blob is uploaded as a special object: `_brain/{timestamp}.bin` on each tail.
4. After successful upload, older mirrors beyond the 3-rolling limit are deleted.

---

## 17. File Type Detection

### 17.1 Design Goals

File type identification must be reliable across Windows, macOS, and Linux. The three available signals have different reliability profiles:

| Signal | Windows | Linux | macOS |
|---|---|---|---|
| File extension | High | Low (often absent) | High |
| Magic bytes | High | High | High |
| OS API (UTI, libmagic) | Medium | High | High |

No OS API is called — it would couple detection to the host platform and violate the OS-agnostic principle. Both extension and magic bytes are captured at write time and stored in the `Files` table. The MIME type derived from magic bytes is the canonical type signal; the extension is preserved as-is from the original filename.

### 17.2 FileTypeService

Lives in `FlashSkink.Core/Engine/FileTypeService.cs`.

```csharp
public sealed class FileTypeService
{
    /// <summary>
    /// Detects file type from filename and first content bytes.
    /// Never throws. Returns null fields if the respective signal is absent
    /// or ambiguous. Called once at write time before the pipeline begins.
    /// </summary>
    public FileTypeResult Detect(string fileName, ReadOnlySpan<byte> header);
}

public sealed record FileTypeResult(
    string? Extension,    // lower-case, dot-prefixed: ".jpg" | null
    string? MimeType);    // "image/jpeg" | null if undetectable
```

**Detection logic:**

```
Extension  ← Path.GetExtension(fileName).ToLowerInvariant()
             null if empty string

MimeType   ← MagicBytes table lookup against header
             if found → magic-byte MIME
             else if extension in KnownExtensions table → extension MIME
             else → null

Conflict resolution:
  If magic-byte MIME disagrees with extension MIME:
    → trust magic bytes (user may have renamed the file)
    → keep original extension
```

### 17.3 Magic Byte Table

`FileTypeService` and `EntropyDetector` share a single static `MagicBytes` class. The 16-byte header read happens once at pipeline entry — one `stackalloc`, passed to both consumers.

| Signature | MIME type |
|---|---|
| `FF D8 FF` | `image/jpeg` |
| `89 50 4E 47` | `image/png` |
| `47 49 46 38` | `image/gif` |
| `52 49 46 46 ?? ?? ?? ?? 57 45 42 50` | `image/webp` |
| `25 50 44 46` | `application/pdf` |
| `50 4B 03 04` | `application/zip` (see disambiguation) |
| `D0 CF 11 E0` | `application/msword` (legacy .doc/.xls) |
| `1F 8B` | `application/gzip` |
| `42 5A 68` | `application/x-bzip2` |
| `37 7A BC AF 27 1C` | `application/x-7z-compressed` |
| `52 61 72 21` | `application/x-rar-compressed` |
| `66 74 79 70` at offset 4 | `video/mp4` |
| `1A 45 DF A3` | `video/webm` |
| `49 44 33` | `audio/mpeg` |
| `4F 67 67 53` | `audio/ogg` |
| `66 4C 61 43` | `audio/flac` |

**ZIP disambiguation** — `.docx`, `.xlsx`, `.pptx`, `.jar`, `.epub` share the ZIP signature. Resolved by extension:

```csharp
if (mimeType == "application/zip" && extension is not null)
{
    mimeType = extension switch
    {
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".jar"  => "application/java-archive",
        ".epub" => "application/epub+zip",
        _       => "application/zip"
    };
}
```

### 17.4 Pipeline Integration

```csharp
// PERF: read only 16 bytes — do not buffer the full file.
// stackalloc keeps the header off the heap.
// Stream.Position is reset to 0 before the rest of the pipeline runs.
Span<byte> header = stackalloc byte[16];
int read = await source.ReadAsync(header, ct);
source.Position = 0;

var fileType = _fileTypeService.Detect(virtualPath, header[..read]);
bool isCompressible = _entropyDetector.IsCompressible(
    Path.GetExtension(virtualPath), header[..read]);
```

### 17.5 EntropyDetector

Determines whether a file is worth compressing. Returns `false` for files whose magic bytes or extension indicate already-compressed content:

- Image formats: JPEG, PNG, GIF, WebP
- Compressed archives: ZIP, GZIP, BZIP2, 7Z, RAR
- Compressed video/audio: MP4, WebM, MP3, OGG, FLAC
- PDF (often contains compressed streams internally)

For uncertain cases, the pipeline attempts compression and rejects the result if the compressed size exceeds 95% of the original (no meaningful gain).

---

## 18. Security and Key Management

### 18.1 Key Hierarchy

```
User Password + Salt
        │ Argon2id (m=19456 KB, t=2, p=1)   ← OWASP 2024 baseline
   KEK (256-bit)  ◄── re-derivable from BIP-39 24-word Mnemonic
        │ AES-256-GCM unwrap
   DEK (256-bit, random)
        ├──► Encrypts file blobs (AAD = BlobID || PlaintextSHA256)
        ├──► Encrypts brain DB (via SQLCipher, key = HKDF(DEK, "brain"))
        ├──► Encrypts brain mirror uploaded to tails
        └──► Encrypts provider OAuth refresh tokens and client secrets
```

### 18.2 Argon2id Parameters

Per Decision B8-b, V1 uses OWASP 2024 baseline:

- Memory cost: 19,456 KB (~19 MB)
- Time cost: 2 iterations
- Parallelism: 1
- Salt: 32 bytes random per volume
- Output: 32 bytes (256-bit KEK)

Approximate unlock time: 100 ms on a 2024-era laptop. Acceptable for a operation that happens once per session.

Parameters are stored in the vault header (see §18.4) so future versions can change defaults without breaking existing volumes — old volumes use the parameters they were created with.

### 18.3 BIP-39 Recovery Mnemonic

24-word mnemonic, 256-bit entropy. The single durable secret. Setup flow:

1. Generate 32 bytes from `RandomNumberGenerator.GetBytes`.
2. Convert to 24-word BIP-39 mnemonic (using English wordlist).
3. Display to user once. Require user to type back 4 randomly-selected words to confirm.
4. Derive KEK seed from mnemonic (BIP-39 seed function with empty passphrase).
5. Use KEK seed + Argon2id salt to derive KEK.

Recovery: Mnemonic → KEK seed → KEK → unwrap DEK → decrypt brain mirror from any tail → reconstruct full volume.

The mnemonic is shown exactly once at setup. After that, it can be re-displayed via `RevealRecoveryPhraseAsync` which requires the password and a 60-second waiting period (Decision A16-d).

### 18.4 DEK Vault

Stored at `[USB]/.flashskink/vault.bin`:

```
Offset  Size   Field
------  ----   -----
0       4      Magic ("FSVT" — FlashSkink VaulT)
4       2      Version (uint16, currently 1)
6       2      Argon2id parameters (encoded: 8 bits memory log2, 4 bits iterations, 4 bits parallelism)
8       32     Argon2id salt
40      12     AES-GCM nonce
52      32     Wrapped DEK ciphertext (AES-256-GCM(KEK, DEK, AAD="DEK_VAULT"))
84      16     GCM tag
Total: 100 bytes
```

The vault is rewritten on password change (re-wraps DEK with new KEK). Old vault is overwritten in place after fsync of the new content.

### 18.5 Brain Encryption (SQLCipher)

The brain database is encrypted at rest using SQLCipher. The encryption key is derived via HKDF from the DEK with context label "brain":

```csharp
Span<byte> brainKey = stackalloc byte[32];
HKDF.DeriveKey(HashAlgorithmName.SHA256, dek, brainKey, salt: null, info: "brain"u8);
sqliteConnection.Execute($"PRAGMA key = \"x'{Convert.ToHexString(brainKey)}'\";");
CryptographicOperations.ZeroMemory(brainKey);
```

The brain key is derived once per volume open and zeroed when the volume closes. The DEK itself is held in a `byte[]` that is also zeroed at close.

SQLCipher uses AES-256-CBC with HMAC-SHA512 page-level integrity. Page size is 4096 bytes (default). Key derivation iterations: 256000 PBKDF2-SHA512 (the SQLCipher default; FlashSkink's KDF is at the password layer, not the brain layer).

### 18.6 Session Key and USB Removal Policy

- **USB removed:** DEK retained in memory. Operations paused. Notification published.
- **USB reinserted (same session):** `integrity_check`. Pass → resume. Fail → repair flow.
- **USB reinserted (different host):** Same as above; DEK cannot transfer between hosts because it lives only in process memory.
- **App closed / explicit lock:** `CryptographicOperations.ZeroMemory` on DEK, KEK, brain key, password buffer.
- **Process crash:** Memory is cleared by the OS. No persistent unencrypted state.

### 18.7 Provider Token Security

OAuth refresh tokens and BYOC client secrets are encrypted with the DEK using AES-256-GCM, fresh nonce per encryption. Stored in `Providers.EncryptedToken` and `Providers.EncryptedClientSecret`. Decrypted in memory only when needed for a provider call; the decrypted form is held in a `byte[]` zeroed immediately after use.

Token rotation: when a provider returns a new refresh token (some providers do this on every use), the old encrypted token is overwritten in the brain in a single transaction.

### 18.8 What Is Never Persisted Outside the Skink

- DEK (held only in process memory while volume is open)
- KEK (held only during operations that need it; zeroed immediately after)
- Password (zeroed immediately after KEK derivation)
- Mnemonic (shown to user once; not persisted by FlashSkink)
- Brain key (zeroed when SQLCipher connection closes)
- Decrypted OAuth tokens (zeroed immediately after each provider call)

The skink itself holds:
- The encrypted vault (DEK wrapped by KEK)
- The encrypted brain (containing encrypted OAuth tokens)
- Encrypted blobs

Everything that crosses a boundary outside the skink (cloud provider, log file, notification) is either encrypted or contains no key material.

---

## 19. USB Resilience

### 19.1 fsync Compliance

`PRAGMA synchronous = EXTRA` plus explicit `fsync` after blob writes narrows the window of unflushed writes. Does not eliminate risk on USB drives with caching that lies about flushing. Mitigated by:

- `integrity_check` on every mount (cheap, catches corruption from incomplete writes)
- Brain mirroring to tails after every commit (provides recovery path)
- Documentation recommending quality-brand drives

**Setup-time recommendation in the GUI and README:** Samsung, SanDisk, Kingston, Crucial. USB 3.x preferred. Avoid unbranded or unusually cheap drives.

### 19.2 USB Removal Detection

```
Primary:  FileSystemWatcher on [USB_ROOT]/.flashskink/
Fallback: DriveInfo.GetDrives() polling every 2 seconds
```

On removal:
- Operations suspended. In-flight uploads pause (their `UploadSessions` rows preserve state).
- DEK retained in memory.
- Notification published: "USB removed. Reinsert to continue."
- UI banner shown.

On reinsertion:
- `PRAGMA integrity_check` on the brain.
- Pass → resume operations, upload queue continues.
- Fail → enter repair flow, offer cloud-mirror restore.

### 19.3 Integrity Check on Mount

Run on every volume open:

```sql
PRAGMA integrity_check;
```

Output other than `ok` triggers the recovery-prompt UI: "The skink's brain reports corruption. FlashSkink can restore from the most recent cloud mirror. The corrupt brain will be archived first (not deleted) so you can attempt manual recovery if needed."

Recovery is non-destructive — the corrupt brain is moved to `[USB]/.flashskink/brain.db.corrupt-{timestamp}` rather than overwritten.

### 19.4 USB Full Behavior

Disk space monitored continuously. Per Decision A8-c:

| USB usage | Behaviour |
|---|---|
| < 80% | Normal operation, no UI indication |
| 80–89% | Status indicator informs ("Skink is filling up") |
| 90–99% | Warning notification ("Skink is nearly full") |
| ≥ 100% (or insufficient space for new write) | Reject writes with `ErrorCode.UsbFull`. UI surfaces clear explanation and suggests freeing space or moving to a larger drive. |

A 1% reservation is held for brain WAL/journal operations to prevent a completely-full skink from breaking metadata writes. New blobs are rejected when the available free space is less than max(blob size, 1% of capacity).

### 19.5 Single-Instance Lock

Per Decision A18-b, FlashSkink holds an exclusive file lock on `[USB]/.flashskink/instance.lock` while open. Second-instance launch detects the lock and exits with a clear error: "FlashSkink is already running on this volume from another process or host. Close the other instance and try again."

The lock is released on clean shutdown and on process exit (the OS releases file locks held by terminated processes).

---

## 20. Integrity and Self-Healing

### 20.1 Hash Layers

| Layer | Algorithm | Computed On | Detects |
|---|---|---|---|
| Plaintext | SHA-256 | Original file content | Pipeline failure (any stage) |
| Ciphertext (USB) | XXHash64 | Encrypted blob bytes | Bit-rot on USB |
| GCM Tag | AES-GCM | Ciphertext + AAD | Tampering, decryption failure |
| Tail object | Provider hash (MD5 / content-hash) | Uploaded blob | Transport corruption |

### 20.2 Background Audit

`AuditService` runs every 24 hours (configurable via Settings). For each blob:

1. Read encrypted blob from USB.
2. Compute XXHash64.
3. Compare to `Blobs.EncryptedXXHash`.
4. Mismatch → mark blob as `CORRUPT` in Blobs.Status (new state), publish notification.

For each tail per blob:
1. Sample-check tail integrity periodically (not every audit — too expensive).
2. If a sampled blob fails verification, mark the tail copy as corrupt, queue re-upload from skink.

### 20.3 Self-Healing

Blob detected as corrupt on USB:

1. Identify a healthy tail holding this blob (any tail with `TailUploads.Status = UPLOADED`).
2. Download the blob from the tail.
3. Verify ciphertext XXHash64 against `Blobs.EncryptedXXHash`.
4. If matches: replace the corrupt USB blob (atomic temp-write + rename). Mark blob as healthy.
5. If no healthy tail has this blob: blob is unrecoverable — surface as critical notification.

Blob detected as corrupt on a tail:

1. Re-upload from skink (the skink is authoritative; if its blob is healthy, that's the source of truth).
2. Delete the corrupt remote object after successful re-upload.

All self-healing operations write to ActivityLog so users can see what was repaired.

### 20.4 Verification Command

`flashskink-cli verify` (Decision A14-b) runs a comprehensive on-demand check:

1. For every blob: read from USB, compute XXHash64, verify.
2. For every (file, tail) in `TailUploads`: query provider for object existence and hash.
3. Report summary: blobs checked, tail entries checked, mismatches found, recommended actions.

This is the command users run when something feels wrong. Designed to complete in minutes for typical volumes (fast hash check, parallel tail queries with rate-limit awareness).

---

## 21. Error Handling and Crash Recovery

### 21.1 Provider Failure During Upload

Within an upload range:
- 3 retries with exponential backoff (1s, 4s, 16s)
- Each retry is for the same range from the same offset
- After 3 failures: increment `TailUploads.AttemptCount`, schedule retry after a longer backoff (5 min, 30 min, 2 hours, 12 hours)
- After 5 retry cycles: mark tail entry as `FAILED`, publish notification

The tail itself transitions to `Degraded` status after 2 consecutive upload failures across different files; `Unreachable` after 5. Other tails continue operating normally.

### 21.2 WAL Recovery on Startup

The WAL table records intent for operations that span multiple steps. On startup, after `integrity_check` passes, the recovery procedure runs:

```
For each WAL row where Phase != COMMITTED and != FAILED:
    switch Operation:
        case WRITE:
            // Phase 1 was in progress. Did the blob make it to USB?
            if blob file exists and matches Blobs row:
                mark Phase=COMMITTED  (recovery success)
            else:
                delete orphan blob if any, delete Files row,
                delete TailUploads rows, mark Phase=FAILED
        case DELETE:
            // Re-execute idempotently
            ensure Files row gone, ensure Blob marked SoftDeleted
            mark Phase=COMMITTED
        case CASCADE_DELETE:
            // Re-execute idempotently for any unfinished descendants
            re-run cascade for the folder, mark Phase=COMMITTED
        case TAIL_DELETE:
            // Re-execute remote delete idempotently
            for each (FileID, ProviderID) in payload not yet deleted:
                attempt provider.DeleteAsync(remoteId)
                on success or NotFound: mark deleted in TailUploads
            mark Phase=COMMITTED
        case PURGE:
            // Sweeper was deleting expired soft-deleted blobs
            for each blob in payload:
                attempt to delete from each tail (if not already)
                if all tails confirm: delete Blobs row, delete USB blob file
            mark Phase=COMMITTED
```

All recovery outcomes are logged to ActivityLog so users see what happened on startup if anything was recovered.

### 21.3 The Crash-Consistency Invariant

The single global invariant the system maintains across all crashes:

> For every `Files` row, either (a) its `BlobID` references an existing `Blobs` row with an existing on-disk blob file, or (b) `BlobID` is NULL (folder/symlink). For every `Blobs` row, the on-disk blob file exists. For every `UploadSessions` row, its `(FileID, ProviderID)` exists in `TailUploads` with `Status != UPLOADED`. For every `WAL` row with `Phase != COMMITTED and != FAILED`, recovery is required to restore the invariant.

WAL recovery's job is to restore this invariant. Property-based tests (under `FlashSkink.Tests/CrashConsistency/`) verify the invariant after every interleaving of "crash at line N of operation X."

### 21.4 USB Loss Recovery

When the user has lost their USB and wants to recover from a tail:

```bash
flashskink-cli recover \
  --mnemonic "word1 word2 ... word24" \
  --provider google-drive \
  --client-id "..." --client-secret "..." \
  --output /media/user/NEWUSB
```

OAuth device flow: URL printed to stdout, user pastes callback code. The CLI then:

1. Authenticates to the provider with user-supplied BYOC credentials and the callback code.
2. Locates the brain mirror on the tail (most recent of `_brain/{timestamp}.bin`).
3. Decrypts the brain mirror using a KEK derived from the mnemonic.
4. Reconstructs a new brain on the output USB.
5. For each blob referenced in the brain: downloads from the tail, verifies, writes to the new USB blob store.
6. Sets up the new USB as a fully-functional skink with the recovered tail already configured.
7. Other tails (if any): the user can add them through the normal `flashskink-cli setup` flow; their blobs are already present remotely and will be re-linked on first contact (compared by hash).

GUI recovery follows the same flow with a wizard-style UI (Decision B11-b).

---

## 22. Provider Health Monitoring

Health is observed at two layers: per-tail probes (§22.1) detect provider-specific problems; a passive local-network signal (§22.2) detects when the host machine has no network at all. A dispatcher rule (§22.3) infers connectivity issues that neither layer catches alone. Section 22.4 specifies how the upload queue and probe scheduler behave when the host is offline.

### 22.1 Per-tail health probes

`HealthMonitorService` polls each tail every 5 minutes (when healthy) or every 30 seconds (when degraded). Each check:

1. Upload a 1 KB test object to a `_health/` path on the provider.
2. Download the same object and verify content.
3. Delete the test object.
4. On success: status `Healthy`, latency recorded.
5. On failure: increment failure counter.
   - 2 consecutive failures: status `Degraded`
   - 5 consecutive failures: status `Unreachable`
6. Status changes published to notification bus.

Health status affects upload queue behaviour:
- `Healthy`: uploads proceed normally
- `Degraded`: uploads continue but with longer backoff between retries
- `Unreachable`: uploads paused for this tail; periodic re-check
- `AuthFailed`: uploads paused; user notification "tail needs reauthorization"
- `QuotaExceeded`: uploads paused; user notification "tail is full"

Health status does **not** block writes to the skink. The mirror model means writes are always accepted locally; tail health only affects upload progress.

### 22.2 Local network availability

`NetworkAvailabilityMonitor` (in `FlashSkink.Core/Engine/`) subscribes to `System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged` and exposes a current state of `Online` or `Offline`. The signal is OS-mediated (Windows NLM, macOS `SCNetworkReachability`, Linux netlink) and **passive** — no packets are sent. This preserves the no-telemetry discipline (DR-B12, DR-B13).

Initial state on volume open is determined synchronously via `NetworkInterface.GetIsNetworkAvailable()`, before `HealthMonitorService` and `UploadQueueService` start. They observe the initial state and start in the appropriate condition rather than briefly running and immediately pausing.

State transitions publish notifications:

| Notification | Trigger | Severity | Persisted to `BackgroundFailures` |
|---|---|---|---|
| `LocalNetworkOffline` | `IsAvailable` transitions to `false` | Warning | No |
| `LocalNetworkRestored` | `IsAvailable` transitions to `true` | Information | No |

Both follow the dispatcher's standard 60-second `Source + ErrorCode` deduplication (§8.4). Neither is persisted because the state is transient and the user does not need to acknowledge it on next launch — by then either the network is back, or the per-tail probes will have surfaced something actionable.

**What this detects:** all interfaces down — wifi off, ethernet unplugged, airplane mode, suspended laptop, VPN dropped to no fallback.

**What this does not detect:** captive portals, ISP outages, DNS failures, MITM filters blocking specific providers. For these, the per-tail probes in §22.1 remain the source of truth. The OS signal is additive; it catches the obvious case cheaply.

### 22.3 Inferred connectivity issues

When ≥ 2 tails transition to `Unreachable` within a 30-second window **and** `NetworkAvailabilityMonitor.CurrentState == Online`, the dispatcher publishes a single `ProbableConnectivityIssue` notification rather than letting N independent per-tail notifications surface in parallel. This catches the captive-portal and DNS-failure cases that §22.2 cannot detect — using only signals already produced by §22.1, with no new outbound traffic.

Thresholds (`N` and `W`) live in `config.json` and are not exposed in the GUI.

### 22.4 Behaviour during offline windows

When `NetworkAvailabilityMonitor.CurrentState == Offline`:

- `HealthMonitorService` probes are paused. Without this, every probe fails, every failure increments counters, and every tail eventually transitions to `Unreachable` for the wrong reason — polluting `Tails.HealthStatus` history with state changes attributable to a known external cause.
- `UploadQueueService` retry scheduling is paused. In-flight range uploads already in motion are not cancelled mid-byte; they fail naturally at the next socket operation, and the existing retry path handles the bookkeeping (§21.1). New retries are not scheduled until the network returns.
- `UploadSessions` rows are preserved unchanged. Cross-host resumability is unaffected.

When the state transitions back to `Online`:

- Both services resume.
- An immediate health probe is triggered on every tail. The user wants confirmation that things work; waiting for the standard 5-minute timer is the wrong UX immediately after reconnection.
- After all probes return, the system reverts to the §22.1 cadence.

---

## 23. CLI Reference

### 23.1 Top-Level Commands

```
flashskink-cli unlock      --skink <path> [--password <pwd>]
flashskink-cli info        --skink <path>
flashskink-cli setup       --skink <path> --provider <type> [...]
flashskink-cli verify      --skink <path>
flashskink-cli restore     --skink <path> --file <virtualPath> --output <path>
                           [--since <timestamp>]   # restore from grace period
flashskink-cli export      --skink <path> --output <directory>
flashskink-cli activity    --skink <path> [--since <timestamp>] [--category <cat>]
flashskink-cli recover     --mnemonic <words> --provider <type> --client-id <id>
                           --client-secret <secret> --output <newSkinkPath>
flashskink-cli reveal-phrase --skink <path>     # password-required, 60s wait
flashskink-cli change-password --skink <path>
flashskink-cli reset-password --skink <path> --mnemonic <words>
flashskink-cli config get|set <key> [<value>]
flashskink-cli support-bundle --skink <path> --output <bundleFile>
flashskink-cli pause       --skink <path> [--tail <providerId>]
flashskink-cli resume      --skink <path> [--tail <providerId>]
```

### 23.2 V1 Scope

All commands above are V1. CLI write operations (uploading new files via CLI) are V2 — V1 CLI is read, audit, restore, recovery, configuration, and setup automation only. The GUI is the primary write surface for V1.

### 23.3 Output Format

All commands support `--json` for machine-readable output. Default is human-readable. Errors return non-zero exit codes and write `ErrorContext.Code` and `Message` to stderr.

---

## 24. Setup CLI (Provider Automation)

### 24.1 Purpose

Automate the BYOC setup flow that would otherwise require 15-25 manual steps in each provider's developer console. Reduces setup to a guided 5-minute flow with two browser interactions per provider.

### 24.2 V1 Coverage

Per Decision B10-b (refined): Google Drive and Dropbox are automated in V1. OneDrive is manual with a detailed step-by-step guide (automation deferred to V1.1 or V2).

### 24.3 Google Drive Setup Flow

```
flashskink-cli setup --skink <path> --provider google-drive

Steps performed automatically by the CLI:
  1. Open browser to Google authentication (gcloud-style device-code flow)
  2. Authenticate user to Google with the necessary admin scopes
  3. Create Google Cloud project "FlashSkink-Backup-{user}-{shortid}"
  4. Enable Google Drive API on the project
  5. Configure OAuth consent screen (Production mode, single-user app)
  6. Create OAuth 2.0 desktop application credentials
  7. Open browser for Drive scope consent
  8. Exchange consent code for refresh token
  9. Encrypt and store credentials + token in brain

Output to user:
  ✓ Google Drive tail configured.
  Your encrypted backups will begin uploading next time the skink is connected.
```

### 24.4 Dropbox Setup Flow

Similar shape: authenticate to Dropbox, create app via App Console API, configure permissions, generate OAuth credentials, exchange consent code, store encrypted credentials.

### 24.5 OneDrive Setup (V1: Manual with Guide)

The CLI presents a step-by-step guide:

```
flashskink-cli setup --skink <path> --provider onedrive

Setting up OneDrive requires creating an app registration in Azure AD.
Follow these steps (or visit https://docs.flashskink.app/onedrive for screenshots):

  1. Go to https://portal.azure.com/...
  2. Sign in with your Microsoft account
  3. Click "App registrations" → "New registration"
  4. Name: "FlashSkink-Backup"
  5. Supported account types: "Personal Microsoft accounts only"
  6. Redirect URI: select "Public client/native (mobile & desktop)"
  7. Click "Register"
  8. Copy the "Application (client) ID" and paste here:

    > [user pastes]

  9. Go to "API permissions" → "Add a permission"
  ...

When complete, the CLI exchanges credentials and stores them encrypted on the skink.
```

Once the user pastes the client ID, the CLI handles OAuth consent flow programmatically (the manual part is only the app registration in Azure).

### 24.6 FileSystem Setup

Trivial: the user provides a path. The CLI verifies the path exists, is writable, and isn't a subdirectory of the skink (which would create a backup loop). No browser interaction.

```
flashskink-cli setup --skink <path> --provider filesystem --path /mnt/nas/backups
```

### 24.7 Setup CLI Programmatic Surface

The setup logic lives in `FlashSkink.Core.Setup` so the GUI setup wizard calls the same code paths. The GUI wraps the CLI commands in a wizard UI; the underlying logic is identical.

---

## 25. GUI Surface

### 25.1 V1 Scope (Decision B11-b)

The GUI is built with Avalonia (cross-platform). V1 includes:

- **Setup wizard**: password, recovery phrase display + confirmation, add tails (calling setup CLI logic), done.
- **File manager**: browse skink contents (folders + files), drag-and-drop to write, right-click delete/rename/move.
- **Status indicator**: single visible element showing tail sync state (all current / catching up / needs attention). Click to expand into per-tail detail panel (Decision A12-c).
- **Recovery wizard**: full recovery flow (mnemonic + tail credentials → new skink).
- **Minimal restore UI** (Decision A13-b): select files/folders, choose destination, restore. Used for normal restores when the skink is intact.

### 25.2 Out of V1 GUI

- Verify button (CLI only in V1)
- Export UI (CLI only in V1)
- Activity log view (CLI only in V1)
- Settings UI beyond essential (most settings via CLI)
- Per-file upload progress (status indicator only shows aggregate)

### 25.3 ViewModel Layer

All GUI logic lives in `FlashSkink.Presentation.ViewModels`. Avalonia views bind to ViewModels via `CommunityToolkit.Mvvm`. ViewModels never reference Avalonia.

ViewModels handle every `Result` explicitly: success and failure paths each have their own behaviour. A `Result` returning `Failure` is never silently ignored.

### 25.4 Appliance UX Discipline

- Internal vocabulary (stripe, blob, WAL, OAuth) does not appear in any user-visible string.
- The user's vocabulary is: skink, tail, recovery phrase, file, folder.
- Error messages are sentences in plain language with one suggested action.
- The status indicator is the only persistent UI element when the file manager is not in focus.
- No progress bars for individual operations unless they exceed 5 seconds.
- No notifications for routine successes (uploads completing, etc.) — only for things requiring attention.

### 25.5 Recovery Phrase Display

When the recovery phrase is shown (at setup or via reveal command):

- Full-screen, modal, all other UI obscured.
- Phrase displayed in a 6×4 grid, each word numbered.
- "Write this down. Without it, your data cannot be recovered." in a prominent position.
- "Continue" button disabled until the user completes a confirmation step (type 4 randomly-selected words back).
- No "copy to clipboard" affordance (deliberately — clipboard is a bad place for the recovery phrase).
- After confirmation, the phrase is cleared from memory and never displayed again until explicitly requested via reveal command.

---

## 26. Portable Execution Model

### 26.1 No-Installation Design

Self-contained single-file executables. Targets: `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`.

Published with:
```
dotnet publish --self-contained -r <RID> -p:PublishSingleFile=true -p:PublishTrimmed=false
```

Trimming is disabled for V1 because several dependencies (SQLCipher, reflection-based serialisation paths) are not trim-safe. V2 may revisit with trim-safe alternatives.

### 26.2 Skink Directory Layout

```
[USB_ROOT]/
├── FlashSkink.exe / FlashSkink                    ← GUI executable
├── flashskink-cli.exe / flashskink-cli            ← CLI executable
├── launch.sh / launch.command                     ← macOS/Linux launchers
├── README.txt                                      ← Quick-start for first-time users
└── .flashskink/
    ├── brain.db + brain.db-wal + brain.db-shm    ← SQLCipher-encrypted SQLite
    ├── vault.bin                                  ← DEK vault
    ├── instance.lock                              ← Single-instance lock
    ├── config.json                                ← Log level, audit interval, grace period
    ├── blobs/                                     ← Sharded encrypted blobs
    │   └── ab/cd/<uuid>.bin
    ├── staging/                                   ← Temporary files during Phase 1 commit
    └── logs/
        └── flashskink-yyyy-MM-dd.log
```

### 26.3 Platform Launch Notes

**Linux** — `launch.sh`:
```bash
#!/usr/bin/env bash
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
chmod +x "$DIR/FlashSkink" "$DIR/flashskink-cli"
"$DIR/FlashSkink" "$@"
```

**macOS** — `launch.command`:
```bash
#!/usr/bin/env bash
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
xattr -d com.apple.quarantine "$DIR/FlashSkink" "$DIR/flashskink-cli" 2>/dev/null
"$DIR/FlashSkink" "$@"
```

**Windows** — no launcher needed; `FlashSkink.exe` is directly executable. Code-signed to avoid SmartScreen warnings.

### 26.4 Host Temp and Host State

**None.** The blueprint's write and upload paths do not use `Path.GetTempPath()`. All temporary state (staging files during Phase 1 commit) lives in `.flashskink/staging/` on the skink itself. This guarantees zero host-side persistence.

The one exception: OS-level caches (filesystem cache, DNS cache) are outside FlashSkink's control. On a shared host, administrators may want to use FlashSkink only on trusted machines for this reason.

### 26.5 Portable Publish Validation (PR 0.1)

Before any implementation work, the build pipeline must prove that a published binary runs on a fresh machine for each RID with no additional installation. Native dependencies (SQLCipher, Konscious.Argon2, ZstdNet, Google.Apis, Dropbox.Api, Microsoft.Graph) must all package correctly. Validation:

1. `dotnet publish` for each RID.
2. Copy the published output to a USB drive.
3. Transport the USB to a freshly-imaged host for each platform.
4. Run from the USB. Observe successful launch.
5. Execute a trivial volume creation and blob write.

Any native dependency that fails this validation is a PR 0.1 blocker.

---

## 27. Built-in Providers (V1)

| Provider | Type | SDK | V1 Status |
|---|---|---|---|
| **FileSystem** | LocalPath | `System.IO` | Shipped. Also used as cloud-provider test double. |
| **Google Drive** | OAuth | `Google.Apis.Drive.v3` | Shipped with setup automation. |
| **Dropbox** | OAuth | `Dropbox.Api` | Shipped with setup automation. |
| **OneDrive** | OAuth | `Microsoft.Graph` | Shipped with manual setup guide. |

### 27.1 FileSystem Provider Notes

- `rootPath` configured at setup (e.g., `/mnt/nas/flashskink-backup` or `D:\Backups\FlashSkink`).
- Uploads write to `{rootPath}/{first-2-of-remoteName}/{remoteName}.bin` using atomic rename.
- Resumable sessions are internal: the `SessionUri` is the target path; `GetUploadedBytesAsync` returns the current file size; `UploadRangeAsync` writes at the given offset; `FinaliseUploadAsync` verifies total size and hash.
- Used as the cloud-provider test double throughout the test suite — deterministic, fast, no network.

### 27.2 Google Drive Notes

- Files uploaded to a dedicated folder (configurable at setup, default `"FlashSkink Backup"`).
- Brain mirror uploaded to `"FlashSkink Backup/_brain/"`.
- Uses resumable upload for all blobs.
- Token refresh automatic; failed refresh → `TokenRefreshFailed` notification.

### 27.3 Dropbox Notes

- Files uploaded to `/FlashSkink Backup/` (provider-root, not app-root — user sees them in their Dropbox).
- Uses upload sessions (`upload_session/start` through `upload_session/finish`).
- Session TTL is 48h; worker handles expiry by restarting.

### 27.4 OneDrive Notes

- Files uploaded to `/FlashSkink Backup/` in the user's OneDrive.
- Uses Graph `createUploadSession` + PUT range.
- Session TTL varies; expiry handled by restarting.

---

## 28. Tech Stack

| Concern | Package | Project |
|---|---|---|
| Language | C# 14 / .NET 10 | All |
| GUI | `Avalonia` | `UI.Avalonia` |
| MVVM | `CommunityToolkit.Mvvm` | `Presentation` |
| CLI | `System.CommandLine` | `CLI` |
| Notification channel | `System.Threading.Channels` | `Presentation` (built-in) |
| Logging abstractions | `Microsoft.Extensions.Logging.Abstractions` | `Core`, `Presentation` |
| Logging implementation | `Serilog` + `Serilog.Extensions.Logging` + `Serilog.Sinks.File` + `Serilog.Formatting.Compact` | `UI.Avalonia`, `CLI` |
| Database (general) | `Microsoft.Data.Sqlite` + `Dapper` | `Core` |
| Database (hot paths) | `Microsoft.Data.Sqlite` raw `SqliteDataReader` | `Core` |
| Database encryption | `SQLCipher` (via `Microsoft.Data.Sqlite` with native SQLCipher build) | `Core` |
| Stream pooling | `Microsoft.IO.RecyclableMemoryStream` | `Core` |
| Google Drive | `Google.Apis.Drive.v3` | `Core` |
| Dropbox | `Dropbox.Api` | `Core` |
| OneDrive | `Microsoft.Graph` | `Core` |
| FileSystem | `System.IO` | `Core` |
| Encryption | `System.Security.Cryptography` (`AesGcm`, `HKDF`) | `Core` |
| Key derivation | `Konscious.Security.Cryptography.Argon2` | `Core` |
| Compression | `ZstdNet` + `K4os.Compression.LZ4` | `Core` |
| Hashing | `System.IO.Hashing` (XxHash64) + `System.Security.Cryptography` (SHA-256) | `Core` |
| BIP-39 | Focused BIP-39 library (e.g. `dotnetstandard-bip39`) | `Core` |
| Memory zeroing | `CryptographicOperations.ZeroMemory` | `Core` |
| Testing | `xUnit` + `Moq` + `FsCheck` (property-based for crash consistency) | `Tests` |
| Publish | `dotnet publish --self-contained -r <RID> -p:PublishSingleFile=true` | GUI + CLI per RID |
| Code signing | `signtool` (Windows Authenticode) + `codesign` (macOS Developer ID) | Build pipeline |

**Native dependency notes:**

- **SQLCipher** requires the SQLCipher-enabled build of `Microsoft.Data.Sqlite.Core` plus native SQLCipher binaries per RID. Validated in PR 0.1.
- **ZstdNet** requires native libzstd per RID. Validated in PR 0.1.
- **Konscious.Argon2** is pure managed C# — no native dependency.

**Rejected libraries:**

- `NBitcoin` for BIP-39 — overkill. Drags in Bitcoin protocol code unused by FlashSkink. A focused library is preferred.

---

## 29. Decision Records

This section preserves every architectural and product decision with options, selection, and rationale. Reading this section alone should explain why FlashSkink is built the way it is.

### 29.1 Architectural decisions

**DR-1: Mirror, not RAID-5.**
- Rejected options: RAID-5 with parity across legs; full secret-sharing distribution.
- Chosen: Full mirroring; each tail is a complete independent replica.
- Rationale: RAID-5's N-1 survivor requirement weakens the product promise. Mirroring delivers a clean "any single survivor" guarantee at the cost of N × storage, which is acceptable for personal backup volumes. Architecture simplifies dramatically (no parity, no stripe planning, no quorum).

**DR-2: No chunking.**
- Rejected options: Fixed-size chunking (required by RAID-5); content-defined chunking (for delta uploads).
- Chosen: File is the atomic unit for encryption, storage, and transport.
- Rationale: Chunking was load-bearing only for parity. Provider SDKs handle resumable uploads at the HTTP layer. Content-defined chunking would reintroduce the complexity we eliminated; delta uploads are out of scope.

**DR-3: Two commit boundaries.**
- Chosen: Phase 1 (skink write) synchronous and transactional; Phase 2 (tail upload) asynchronous and resumable.
- Rationale: Matches how users expect USB drives to work (drag and unplug). Decouples local durability from network availability. Enables cross-host resumability.

**DR-4: Resumable uploads are first-class.**
- Rejected: Let SDKs handle transparency; rely on library-level automatic retries.
- Chosen: Upload sessions persisted on skink; explicit `IStorageProvider` lifecycle; range-by-range progress tracking.
- Rationale: Required for cross-host resumability. Bounds memory to a single 4 MB buffer regardless of file size. Gives product-level control over retry and resume policy.

**DR-5: SQLCipher for brain encryption.**
- Rejected: Application-level encrypt at shutdown; per-column encryption; no encryption.
- Chosen: SQLCipher integrated at connection level.
- Rationale: Only way to make the zero-knowledge promise extend to metadata. Drop-in solution, compatible with Dapper and Microsoft.Data.Sqlite. Page-level AES-256-CBC + HMAC-SHA512 is strong.

**DR-6: BYOC OAuth for V1.**
- Rejected for V1: Shipped registered app.
- Chosen: Users register their own OAuth apps; FlashSkink stores credentials encrypted.
- Rationale: Avoids Google CASA verification timeline (multi-month blocker). No shared client secret to leak. Per-user app means a ban is per-user. Rclone proves this model at scale. Registered app may be added in V2 based on feedback.

**DR-7: Setup CLI automates BYOC.**
- Rejected: Pure manual console setup with documentation.
- Chosen: CLI automates Google Cloud project creation, Dropbox App Console, Azure AD registration to the extent possible.
- Rationale: Reduces BYOC friction from 15-25 manual steps to ~5-minute guided flow. Critical for appliance positioning.

**DR-8: Soft-delete grace period instead of versioning.**
- Rejected: Full snapshot / versioning; hard delete with no grace; user-selectable versioning per file.
- Chosen: Universal grace period (default 30 days, CLI-configurable). No UI surface.
- Rationale: Protects against accidental overwrite/delete (the tax-return scenario) without becoming a versioning product. Preserves appliance mental model. Snapshot/versioning is a separate post-V1 product.

**DR-9: Session protocol enforced by CI.**
- Rejected options: Trust solo-dev discipline; enforce via code review alone; document the protocol without mechanical checks.
- Chosen: CI workflows (`plan-check`, `principle-audit`, `pr-review.yml`, `pr-review-crypto.yml`, `pr-review-recovery.yml`) make the session protocol's Gate 1 and Gate 2 discipline mechanical.
- Rationale: A solo-dev project cannot rely on social pressure or peer review to follow a protocol. Without automated enforcement, the protocol degrades quietly — missed plan files, scope creep, principle violations accumulate until the system no longer provides the safety the protocol was meant to provide. The CI layer makes deviations visible within minutes of a PR being opened. The `plan-check` job is a hard gate (blocks merge); the Claude-powered review jobs are comment-only so false positives don't stall work but still surface for acknowledgement. Public-repo GitHub Actions minutes are ample; this is essentially free enforcement.
- Scope note: DR-9 covers the existence and role of the CI layer. Specific workflow details (triggers, paths, models, prompts) are operational and live in `CLAUDE.md` § "CI and automation" — editable without a blueprint revision.

**DR-10: Local network availability detected via passive OS signal.**
- Rejected options: Active probe of a neutral endpoint (Cloudflare, Google DNS, etc.) — violates DR-B12/DR-B13 and the "no network chatter" discipline; rely solely on per-tail probe failures — produces N parallel "tail unreachable" notifications when the cause is local, pollutes `Tails.HealthStatus` history during offline windows, and forces the user to infer "I'm offline" from simultaneity.
- Chosen: `System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged` as the primary signal, with a dispatcher-level inference rule (≥ 2 tails `Unreachable` within 30 s while OS reports `Online`) covering captive-portal and DNS-failure cases. Distinct `ErrorCode.LocalNetworkOffline`. Probe scheduling and upload-retry scheduling pause during offline windows; in-flight uploads are not cancelled. State is reasserted synchronously at volume open via `GetIsNetworkAvailable()`.
- Rationale: The OS signal is passive — it makes no outbound traffic — so it preserves the zero-telemetry posture without adding a "neutral endpoint" exception. It fires correctly on Windows, macOS (Intel and Apple Silicon), and Linux through a single BCL API, satisfying the OS-agnostic principle without per-platform code. Pausing probes and retries during offline windows prevents the state-history pollution that would otherwise misattribute a local cause to specific providers, and preserves `TailUploads.AttemptCount` for genuine provider failures. Captive-portal and DNS-failure cases (where the OS reports `Online` but nothing works) are caught by the inference rule using existing per-tail signals — no new mechanism, no new traffic.
- Scope note: §22.2 specifies the monitor and its initial-state behaviour; §22.3 specifies the inference rule; §22.4 specifies the upload-queue and probe-scheduler behaviour during offline windows. Threshold values for the inference rule (`N` tails, `W` seconds) live in `config.json` and are tunable without a blueprint revision.

### 29.2 Settled questionnaire decisions

| Decision | Choice | Rationale |
|---|---|---|
| A1 — Metadata preservation | (b) Name + created/modified timestamps | Cross-platform lowest common denominator; matches user expectation |
| A2 — Symlinks / special files | (b) Preserve symlinks as metadata; reject other special files | Standard backup behaviour; cheap to implement |
| A3 — Mid-backup file change | (b) Detect via size/mtime, reject with error | Torn files worse than no file |
| A4 — Delete semantics | (c) Propagate to all tails after grace period | Matches mirror mental model |
| A5 — Grace-period UI | (a) No UI; CLI-only for power users | Preserves "not a versioning system" framing |
| A6 — First-run detection | (b) Valid / empty / invalid three-state | Pragmatic minimum |
| A7 — Migration strategy | (b) Automatic with pre-migration DB backup | Safety with rollback path |
| A8 — USB full thresholds | (c) 80% inform, 90% warn, 100% reject | Balance alarm vs action time |
| A9 — Provider quota exceeded | (a) Halt that tail; others continue | Mirror-model redundancy intact |
| A10 — Network failure retry | (b) Exponential backoff | No log spam, prompt recovery |
| A11 — USB reinsertion | (b) Integrity check then resume | Already-decided from §19 |
| A12 — Progress visibility | (c) Status indicator + expandable detail panel | Appliance with escape hatch |
| A13 — V1 Restore UX | (b) Minimal restore GUI | Paired with core GUI surface |
| A14 — Integrity verification | (b) Automatic + `verify` CLI | High-trust low-cost |
| A15 — Export | (a) `flashskink-cli export` | No lock-in; trust feature |
| A16 — Mnemonic re-display | (d) Password + 60s countdown | Stronger friction, deliberate |
| A17 — Password reset via mnemonic | (a) Fully supported | Natural consequence of key hierarchy |
| A18 — Concurrent skink access | (b) Single-instance lock | Cheap insurance |
| A19 — Multiple skinks per user | (a) Fully independent | No coordination needed |
| A20 — User-facing audit log | (b) CLI-accessible activity log | Trust feature; minimal UI |
| A21 — Empty files/folders | (a) Stored as metadata rows | Valid user data |
| A22 — Filename encoding | (a) UTF-8 NFC-normalized | Standard; cross-platform |
| A23 — Path length | (b) Accept any; resolve conflicts on restore | Honest |
| A24 — Bulk partial failures | (a) Continue past failures, summary | User-respecting |
| B1 — Grace period default | (b) 30 days | Already decided |
| B2 — Grace period config | (b) CLI only | Appliance simplicity |
| B3 — Upload range size | (b) 4 MB | Matches Google recommendation |
| B4 — Blob format | (c) Versioned header (magic + version + flags) | Future-proofing at trivial cost |
| B5 — Blob storage layout | (b) Two-level sharded | Prevents directory slowness |
| B6 — Compression threshold | (c) Skip already-compressed via entropy detection | Free given architecture |
| B7 — Compression algorithm | (c) LZ4 small, Zstd larger | Balanced |
| B8 — Argon2id parameters | (b) OWASP 2024 baseline (m=19456, t=2, p=1) | Current best practice |
| B9 — Brain encryption | (a) SQLCipher | Standard answer |
| B10 — Setup CLI scope | Google Drive + Dropbox automated; OneDrive manual guide | Realistic V1 scope |
| B11 — GUI/CLI scope | (b) Core GUI; advanced via CLI | Appliance with power-user escape |
| B12 — Update check | (a) No automatic checks | Zero-network discipline |
| B13 — Telemetry | (a) None ever | Matches positioning |
| B14 — Support bundle | (b) Logs + config + schema + recent activity | Useful, safe to share |

### 29.3 Post-V1 directions recorded

| Decision | Direction |
|---|---|
| C1 — Registered OAuth app | (c) Decide in V2 based on V1 feedback |
| C2 — Snapshot product | (c) Separate product sharing infrastructure |
| C3 — Plugin architecture | (a) V2+ |
| C4 — Multi-device concurrent | (b) V2+ |
| C5 — Dedup | (a) V2+ if storage pressure reported |
| C6 — Mobile | Post-V1: viewer/restorer communicating with skink via local transport (Bluetooth / WiFi Direct / local network); phone never holds cloud credentials or decryption keys |
| C7 — Hosted service | (b) Only static content (releases) on CDN |
| C8 — Hardware | (a) Small pilot 3-6 months after V1 ships |

### 29.4 Standard conventions

| Item | Choice |
|---|---|
| License | MIT (simpler) or Apache 2.0 (explicit patent grant) — pick one before first public release |
| Public release model | GitHub, public from day one |
| Versioning | Semantic versioning (major.minor.patch) |
| Code signing | Windows Authenticode + macOS Developer ID at V1 launch |
| Documentation | Single-page-per-topic markdown on GitHub Pages |
| Privacy policy | Minimal "we collect nothing" policy |
| Issue triage SLA | 7-day response (not fix) |

---

## 30. Out of Scope (V1)

The following are deliberately excluded from V1. Some are planned for V2+ (with direction recorded in §29.3); others are deliberately never planned.

**Deferred to V2+:**
- Registered OAuth app (Google / Dropbox / Microsoft-owned)
- Runtime plugin architecture for custom providers
- Dynamic tail addition with restriping (mirror model makes this trivial; not a problem to solve)
- Dedup across files (schema preserves the option)
- Multi-device concurrent access
- CLI write operations (add/overwrite files via CLI)
- GUI surface for verify / export / activity / full settings
- Mobile clients (defined: viewer/restorer only, through the skink, never direct to cloud)
- Automatic update checks
- Hardware product (pre-loaded skink USB)

**Never planned (out of product scope):**
- File versioning with user-selectable restore points (that's the snapshot product)
- Delta uploads / content-defined chunking
- Multi-device real-time sync
- Collaboration features (shared volumes)
- Server-side components (no accounts, no coordination)
- Telemetry of any kind
- Snapshot product built into FlashSkink itself (will be a separate product if built at all)

---

## 31. Post-V1 Direction

### 31.1 V1.1 (first patch release after V1)

Candidate contents, to be decided based on V1 feedback:
- OneDrive setup automation
- Additional provider adapters (Backblaze B2, AWS S3) if users request
- Minor GUI polish (activity view, verify button)
- Performance tuning based on real-world usage data

### 31.2 V2 (major)

Candidate contents:
- Registered OAuth app option (alongside BYOC)
- Plugin architecture for custom providers
- Dedup within a volume
- Full GUI surface (remove CLI-only restrictions from V1)
- Configurable advanced settings UI

### 31.3 Snapshot Product (separate)

If built, shares:
- Provider adapters
- Crypto pipeline
- Brain infrastructure (with different schema)
- Setup CLI

Differs in:
- Content-addressed storage with dedup
- Volume manifests and snapshot retention
- Explicit backup trigger UX
- Restore from specific point-in-time

Target: technical users currently using Borg / Restic / Arq who want the FlashSkink-style portable-USB twist.

### 31.4 Hardware Pilot

Scope: 50-100 pre-loaded USB drives sold 3-6 months after V1 ships. Decision criteria to proceed:
- V1 software has > 1000 users
- Setup-related support volume is manageable
- Real demand signal (requests, willingness to pay) present

Pilot design:
- Single SKU, single capacity (64 GB), single drive model
- Minimalist packaging (Kraft box, printed quick-start card)
- $19-25 retail
- Flat international shipping
- All-sales-final policy

### 31.5 Mobile Client Direction

Recorded intent (Decision C6): When mobile is introduced post-V1, it operates as a viewer/restorer that communicates with the skink through a local transport (Bluetooth, WiFi Direct, or local network). The skink is always the intermediary to cloud providers; the phone never holds OAuth tokens or decryption keys.

Specific transport and feature scope to be decided based on V1+ feedback and whether dedicated FlashSkink hardware (with a radio) becomes part of the roadmap.

---

## End of Blueprint

*This blueprint is authoritative for V1. Changes require a new blueprint version and review of downstream documents (DEV_PLAN.md, CLAUDE.md).*

*Last updated: April 2026 — added DR-9 (CI-enforced session protocol). Additive amendment; V1 design baseline unchanged.*
