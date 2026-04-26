# PR 1.1 — Result pattern and ErrorCode

**Branch:** pr/1.1-result-pattern-and-errorcode
**Blueprint sections:** §6.1, §6.2, §6.3, §6.4, §6.5, §6.6, §6.7, §6.8
**Dev plan section:** phase-1 §1.1

## Scope

Replaces the placeholder `Placeholder.cs` in `FlashSkink.Core.Abstractions` with the full production implementation of the Result pattern. Delivers four types in `FlashSkink.Core.Abstractions/Results/`: `Result` (non-generic), `Result<T>` (generic), `ErrorContext` (error detail carrier), and `ErrorCode` (full V1-through-Phase-6 enum). No implicit conversions, no throw-on-failure helpers. All subsequent Core methods depend on these types from Phase 2 onward.

## Files to create

- `src/FlashSkink.Core.Abstractions/Results/ErrorCode.cs` — `ErrorCode` enum, full set covering Phase 1–6 failures, ~55 lines
- `src/FlashSkink.Core.Abstractions/Results/ErrorContext.cs` — `sealed record ErrorContext` with `From` factory, ~35 lines
- `src/FlashSkink.Core.Abstractions/Results/Result.cs` — `readonly record struct Result` (non-generic), ~30 lines
- `src/FlashSkink.Core.Abstractions/Results/Result{T}.cs` — `readonly record struct Result<T>` (generic), ~35 lines
- `tests/FlashSkink.Tests/Results/ResultTests.cs` — unit tests for all four types, ~220 lines

## Files to modify

- `src/FlashSkink.Core.Abstractions/Placeholder.cs` — delete content; replace with a redirect comment or remove the file entirely (the `Results/` folder replaces it)

## Dependencies

- NuGet: none new
- Project references: none new

## Public API surface

### `FlashSkink.Core.Abstractions.Results.ErrorCode` (enum)
Summary intent: Discriminated set of all failure modes the application can produce; callers switch on this to decide recovery strategy.

Values (grouped by domain):

```
Unknown = 0

// Control flow
Cancelled, Timeout

// Volume
VolumeNotFound, VolumeAlreadyOpen, VolumeCorrupt, VolumeReadOnly,
VolumeAlreadyExists, VolumeIncompatibleVersion

// Auth
InvalidPassword, InvalidMnemonic, KeyDerivationFailed,
TokenExpired, TokenRefreshFailed, TokenRevoked

// Providers
ProviderUnreachable, ProviderAuthFailed, ProviderQuotaExceeded,
ProviderRateLimited, ProviderApiChanged,
UploadFailed, UploadSessionExpired, DownloadFailed,
BlobNotFound, BlobCorrupt

// Pipeline
CompressionFailed, EncryptionFailed, DecryptionFailed,
IntegrityCheckFailed, ChecksumMismatch

// Metadata
DatabaseCorrupt, DatabaseLocked, DatabaseWriteFailed,
DatabaseMigrationFailed, WalRecoveryFailed, SchemaVersionMismatch

// USB / IO
UsbRemoved, UsbNotFound, UsbReadOnly, UsbFull,
StagingFailed, InsufficientDiskSpace, SingleInstanceLockHeld

// File operations
FileChangedDuringRead, FileTooLong, PathConflict,
CyclicMoveDetected, ConfirmationRequired,
UnsupportedFileType, FileNotFound

// Recovery
RecoveryFailed, NoBackupFound, BackupDecryptFailed

// Healing
HealingFailed, NoSurvivorsAvailable
```

---

### `FlashSkink.Core.Abstractions.Results.ErrorContext` (sealed record)
Summary intent: Carries all diagnostic information for a failed operation without retaining the raw Exception object.

- `ErrorCode Code { get; init; }`
- `string Message { get; init; }`
- `string? ExceptionType { get; init; }`
- `string? ExceptionMessage { get; init; }`
- `string? StackTrace { get; init; }`
- `IReadOnlyDictionary<string, string>? Metadata { get; init; }`
- `static ErrorContext From(ErrorCode code, string message, Exception? exception)` — extracts type name, message, and stack trace as strings; never retains the Exception reference

---

### `FlashSkink.Core.Abstractions.Results.Result` (readonly record struct)
Summary intent: Result type for operations that succeed with no value or fail with an ErrorContext.

- `bool Success { get; }`
- `ErrorContext? Error { get; }`
- `static Result Ok()`
- `static Result Fail(ErrorCode code, string message, Exception? exception = null)`
- `static Result Fail(ErrorContext error)` — for propagating an existing ErrorContext unchanged

---

### `FlashSkink.Core.Abstractions.Results.Result<T>` (readonly record struct)
Summary intent: Result type for operations that succeed with a value of type T, or fail with an ErrorContext.

- `bool Success { get; }`
- `T? Value { get; }`
- `ErrorContext? Error { get; }`
- `static Result<T> Ok(T value)`
- `static Result<T> Fail(ErrorCode code, string message, Exception? exception = null)`
- `static Result<T> Fail(ErrorContext error)` — for propagating an existing ErrorContext unchanged

## Internal types

None — all four types in this PR are public.

## Method-body contracts

### `ErrorContext.From`
- Precondition: `code` is a defined `ErrorCode` value; `message` is non-null.
- `exception` may be null (produces null `ExceptionType`, `ExceptionMessage`, `StackTrace`).
- Never retains a reference to `exception`. Extracts strings synchronously.
- `Metadata` is always null on contexts created via `From`; callers add metadata via `with { Metadata = ... }`.

### `Result.Fail(ErrorCode, string, Exception?)`
- Delegates to `ErrorContext.From`; returns a new `Result` with `Success = false`.
- The `exception` overload: `ExceptionType`, `ExceptionMessage`, `StackTrace` are captured strings; the `Exception` object is not stored.

### `Result<T>.Fail(ErrorCode, string, Exception?)`
- Same contract as `Result.Fail`; `Value` is `default(T?)`.

### `Result<T>.Ok(T value)`
- `Error` is null; `Value` is the provided value. `Success = true`.

## Integration points

None — this PR introduces the foundation types; nothing in the existing codebase calls them yet. The `Placeholder.cs` file is the only existing file touched.

## Principles touched

- Principle 1 (Core never throws across its public API — these types are the mechanism that enforces it)
- Principle 8 (Core holds no UI framework reference — `Core.Abstractions` must remain pure)
- Principle 9 (Presentation holds no UI framework reference — same assembly boundary)
- Principle 15 (A bare `catch (Exception ex)` is never the only catch — documented in `ErrorCode` XML comments as pattern guidance)
- Principle 26 (Logging never contains secrets — `ErrorContext.Metadata` key-name restriction documented on the type)

## Test spec

### `tests/FlashSkink.Tests/Results/ResultTests.cs`

**Class:** `ResultTests`

- `Result_Ok_HasSuccessTrue` — `Result.Ok()` produces `Success = true`, `Error = null`.
- `Result_Fail_WithCodeAndMessage_HasSuccessFalse` — `Result.Fail(ErrorCode.Unknown, "msg")` produces `Success = false`, `Error.Code = Unknown`, `Error.Message = "msg"`, `Error.ExceptionType = null`.
- `Result_Fail_WithException_CapturesExceptionStrings` — pass a real `Exception`; assert `ExceptionType` equals the full type name, `ExceptionMessage` equals the exception message, `StackTrace` is non-null.
- `Result_Fail_WithException_DoesNotRetainExceptionReference` — verifies `ErrorContext` has no `Exception`-typed property (reflection check or type-safety check).
- `Result_Fail_WithErrorContext_PropagatesContext` — `Result.Fail(existingContext)` carries the same `ErrorContext` instance reference.

**Class:** `ResultOfTTests`

- `ResultOfT_Ok_HasSuccessTrueAndValue` — `Result<int>.Ok(42)` produces `Success = true`, `Value = 42`, `Error = null`.
- `ResultOfT_Ok_WithReferenceType_HasValue` — `Result<string>.Ok("hello")` produces `Value = "hello"`.
- `ResultOfT_Fail_HasSuccessFalseAndDefaultValue` — `Result<int>.Fail(ErrorCode.Unknown, "msg")` produces `Success = false`, `Value = default`.
- `ResultOfT_Fail_WithException_CapturesExceptionStrings` — same exception-string assertions as `Result` variant.
- `ResultOfT_Fail_WithErrorContext_PropagatesContext` — same propagation check as `Result` variant.
- `ResultOfT_Ok_NullableReferenceType_Allowed` — `Result<string?>.Ok(null)` does not throw; `Success = true`.

**Class:** `ErrorContextTests`

- `ErrorContext_From_NullException_ProducesNullFields` — `ErrorContext.From(ErrorCode.Cancelled, "msg", null)` has null `ExceptionType`, `ExceptionMessage`, `StackTrace`.
- `ErrorContext_From_RealException_CapturesTypeNameAndMessage` — pass `new InvalidOperationException("boom")`; assert `ExceptionType = "System.InvalidOperationException"`, `ExceptionMessage = "boom"`.
- `ErrorContext_From_RealException_StackTraceNonNullAfterThrow` — throw and catch an exception, then pass it; assert `StackTrace` is non-null.
- `ErrorContext_From_RealException_StackTraceNullBeforeThrow` — pass `new Exception("x")` without throwing; assert `StackTrace` is null (CLR does not populate StackTrace until thrown).
- `ErrorContext_WithMetadata_CanBeAdded` — use `with { Metadata = new Dictionary<string,string>{"k","v"} }` and assert `Metadata["k"] == "v"`.
- `ErrorContext_Metadata_IsNullByDefault_FromFactory` — `ErrorContext.From(...)` always produces `Metadata = null`.

**Class:** `ErrorCodeTests`

- `ErrorCode_Unknown_IsZero` — `(int)ErrorCode.Unknown == 0`.
- `ErrorCode_AllPhase1Values_AreDefined` — asserts each of the 19 Phase 1 values listed in the dev plan exists in the enum by name (using `Enum.IsDefined`). Values: `Cancelled`, `Unknown`, `DatabaseCorrupt`, `DatabaseLocked`, `DatabaseWriteFailed`, `VaultNotFound`(*), `VaultCorrupt`(*), `VaultVersionUnsupported`(*), `InvalidPassword`, `InvalidMnemonic`, `CryptoFailed`(*), `ChecksumMismatch`, `UsbFull`, `StagingFailed`, `PathConflict`, `CyclicMoveDetected`, `ConfirmationRequired`, `VolumeIncompatibleVersion`, `SingleInstanceLockHeld`.
  (*) Note: the dev plan listed `VaultNotFound`, `VaultCorrupt`, `VaultVersionUnsupported`, `CryptoFailed` as Phase 1 names, but the blueprint §6.4 enum uses slightly different grouping. The test asserts the blueprint §6.4 names (which are authoritative): `VolumeCorrupt` covers vault-level corruption; `EncryptionFailed`/`DecryptionFailed` cover crypto. See drift note below.
- `ErrorCode_HasNoNegativeValues` — all enum values are >= 0.

## Acceptance criteria

- [ ] Builds with zero warnings on all targets (`dotnet build --warnaserror`)
- [ ] All new tests pass (`dotnet test`)
- [ ] No existing tests break (ArchitectureTests, SmokeTests all green)
- [ ] `Placeholder.cs` is removed or emptied; no `internal static class Placeholder` remains
- [ ] `ErrorCode` enum matches blueprint §6.4 exactly — no values added, none omitted
- [ ] `ErrorContext` has no property of type `Exception` or any subtype thereof
- [ ] `Result` and `Result<T>` constructors are private; only `Ok`/`Fail` factories are public
- [ ] File-scoped namespaces used throughout
- [ ] XML doc comments on all public types and members

## Line-of-code budget

- `src/FlashSkink.Core.Abstractions/Results/ErrorCode.cs` — ~55 lines
- `src/FlashSkink.Core.Abstractions/Results/ErrorContext.cs` — ~40 lines
- `src/FlashSkink.Core.Abstractions/Results/Result.cs` — ~35 lines
- `src/FlashSkink.Core.Abstractions/Results/Result{T}.cs` — ~40 lines
- `tests/FlashSkink.Tests/Results/ResultTests.cs` — ~220 lines
- Total: ~170 lines non-test, ~220 lines test

## Non-goals

- Do NOT wire `Result`/`Result<T>` into any existing Core or Presentation code in this PR — only the types themselves are introduced.
- Do NOT add convenience extension methods (`Map`, `Bind`, `Match`, `ThenAsync`, etc.) — these are not in the blueprint and are not needed by Phase 2.
- Do NOT implement implicit operators or `throw`-on-failure helpers.
- Do NOT add `IStorageProvider` or any other interface — those are separate PRs.
- Do NOT add `VaultNotFound`, `VaultCorrupt`, `VaultVersionUnsupported`, or `CryptoFailed` as enum values — the blueprint §6.4 enum (authoritative) uses `VolumeCorrupt`, `EncryptionFailed`, `DecryptionFailed` instead. The dev plan §1.1 names are an approximation; blueprint wins.

## Drift note

The dev plan §1.1 lists `VaultNotFound`, `VaultCorrupt`, `VaultVersionUnsupported`, `CryptoFailed` as Phase 1 enum values. Blueprint §6.4 (authoritative) does not include these names — vault-level failures map to `VolumeCorrupt`/`VolumeNotFound`/`VolumeIncompatibleVersion`, and crypto failures map to `EncryptionFailed`/`DecryptionFailed`. Blueprint wins per `CLAUDE.md` escalation rules. No `CLAUDE.md` update needed — this is a naming approximation in the dev plan, not a structural disagreement.
