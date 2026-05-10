# PR 3.3 — Range uploader

**Branch:** `pr/3.3-range-uploader`
**Blueprint sections:** §15 (full — resumable upload sessions), §13.6 (blob-on-disk format — the bytes the uploader streams), §13.7 (blob integrity — three guarantees), §10.1 (`IStorageProvider` contract — frozen consumer), §9.5 (4 MB pooled buffer pattern), §21.1 (in-range retry ladder — consumed via §3.2's `RetryPolicy`).
**Dev plan section:** `dev-plan/phase-3-Upload-queue-and-resumable-uploads.md` §3.3.

## Scope

Phase 3 §3.3 ships the single-blob upload state machine: `RangeUploader.UploadAsync` walks one `(FileID, ProviderID)` row through the §15.3 lifecycle — resume-or-open session → range loop with per-range retry → finalise → verify — and returns an `UploadOutcome` describing the result. The §3.4 `UploadQueueService` will own the cycle-level retry ladder, the brain transitions on `TailUploads`, and the notification surface. This PR is one type, one capability interface, two value types, and a small modification to `FileSystemProvider` to wire the new capability.

After this PR merges:

- The §3.4 PR can land `UploadQueueService` against frozen `RangeUploader` and `UploadOutcome` shapes.
- `FileSystemProvider` exposes its XXHash64 hash-check capability publicly via `ISupportsRemoteHashCheck`, completing the §15.7 verification path for the FileSystem row of the protocol-mapping table.
- The §21.1 in-range ladder (1 s / 4 s / 16 s, then escalate) is exercised end-to-end against the §3.2 `RetryPolicy` and the test `FakeClock`.

No upload orchestration. No cycle ladder. No `MarkUploadingAsync` / `MarkUploadedAsync` / `MarkFailedAsync` calls — those live in the caller (`UploadQueueService`, §3.4) so a single brain transaction can cover the status flip plus the `UploadSessions` delete (§15.3 step 7c). No notifications.

## Files to create

### `src/FlashSkink.Core.Abstractions/Providers/`

- `ISupportsRemoteHashCheck.cs` — capability interface (Principle 23 additive evolution). One method: `Task<Result<ulong>> GetRemoteXxHash64Async(string remoteId, CancellationToken ct)`. ~30 LOC including XML docs.

### `src/FlashSkink.Core/Upload/`

- `UploadOutcomeStatus.cs` — `public enum` with `Completed | RetryableFailure | PermanentFailure`. ~25 LOC including XML docs.
- `UploadOutcome.cs` — `public sealed record` carrying `Status`, optional `RemoteId` / `BytesUploaded` (Completed only), optional `FailureCode` / `FailureMessage` (failures only); plus three static factories `Completed`, `RetryableFailure`, `PermanentFailure`. ~90 LOC including XML docs.
- `RangeUploader.cs` — `public sealed class` implementing the §15.3 single-blob state machine. ~430 LOC including XML docs and the catch ladder.

### `tests/FlashSkink.Tests/Upload/`

- `RangeUploaderTests.cs` — exhaustive coverage of every branch in `UploadAsync`. Constructed against a real `FileSystemProvider` rooted at a per-test temp dir, optionally wrapped in `FaultInjectingStorageProvider` for failure scenarios. Uses `FakeClock` + `RetryPolicy.Default` + a real in-memory brain (SQLCipher off — see test infrastructure note). ~520 LOC.

### `tests/FlashSkink.Tests/_TestSupport/`

- `BrainTestHarness.cs` (if absent) — one-shot helper that creates a temp-dir-backed `SqliteConnection` with the brain schema applied, returns wired `BlobRepository` / `FileRepository` / `UploadQueueRepository` instances, and disposes them all on `IAsyncDisposable.DisposeAsync`. **Verify presence first** — `Phase 2` tests already create per-test brains; if a shared harness exists, reuse it; otherwise add this file. The implementer reads `tests/FlashSkink.Tests/Engine/WritePipelineTests.cs` (or wherever the existing per-test brain setup lives) and either lifts the helper into `_TestSupport/` or imports its name. Either path is fine; **the §3.3 tests must not duplicate the brain-bootstrap dance.** ~80 LOC if newly authored; 0 LOC if a harness already exists and is reused.

## Files to modify

- `src/FlashSkink.Core/Providers/FileSystemProvider.cs` — add `: ISupportsRemoteHashCheck` to the class declaration and change the existing `internal async Task<Result<ulong>> GetRemoteXxHash64Async(...)` (file [src/FlashSkink.Core/Providers/FileSystemProvider.cs:570](src/FlashSkink.Core/Providers/FileSystemProvider.cs:570)) to `public`. Body unchanged. Net delta: ~+3 lines (the interface name, a using directive, and a one-line XML cross-reference to the interface).

No other production files modified. No brain schema change. No `ErrorCode` change (cross-cutting decision 2 holds).

## Dependencies

- **NuGet:** none new. `System.IO.Hashing` (added in §2.5) is already referenced by `FileSystemProvider` for the existing `XxHash64` call site.
- **Project references:** none added. `Core/Upload/RangeUploader.cs` references `Core.Abstractions` (for `IStorageProvider`, `ISupportsRemoteHashCheck`, `UploadSession`, `Result`, `IClock`) and the existing `Core/Metadata/UploadQueueRepository`. `Core.Abstractions/Providers/ISupportsRemoteHashCheck.cs` references `Core.Abstractions/Results` only.

## Public API surface

### `FlashSkink.Core.Abstractions.Providers.ISupportsRemoteHashCheck` (interface)

Summary intent: optional capability interface declaring that an `IStorageProvider` can compute the XXHash64 of a finalised remote object server-side. `RangeUploader` casts to this interface during the §15.7 verification step; providers that do not implement it skip the check and rely on the GCM tag inside the encrypted blob (which already guards against ciphertext tampering — Principle 6).

```csharp
namespace FlashSkink.Core.Abstractions.Providers;

public interface ISupportsRemoteHashCheck
{
    /// <summary>
    /// Reads the finalised remote object identified by <paramref name="remoteId"/> and returns
    /// its XXHash64. The caller compares against the local <c>Blobs.EncryptedXXHash</c> stored
    /// at write time. Blueprint §15.7 (FileSystem row).
    /// </summary>
    /// <param name="remoteId">Provider-side identifier returned by <see cref="IStorageProvider.FinaliseUploadAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="ErrorCode.BlobNotFound"/> if no object exists for <paramref name="remoteId"/>;
    /// otherwise <see cref="ErrorCode.ProviderUnreachable"/> on transport failure or the hash on success.
    /// </returns>
    Task<Result<ulong>> GetRemoteXxHash64Async(string remoteId, CancellationToken ct);
}
```

Capability-interface evolution sanctioned by Principle 23: adding `: ISupportsRemoteHashCheck` to existing or future providers does not change the frozen `IStorageProvider` contract.

### `FlashSkink.Core.Upload.UploadOutcomeStatus` (enum)

```csharp
namespace FlashSkink.Core.Upload;

public enum UploadOutcomeStatus
{
    /// <summary>The blob was uploaded, finalised, and (if the provider supports it) verified.</summary>
    Completed = 0,

    /// <summary>The in-range retry ladder (§21.1) escalated for at least one range.
    /// The caller should consult <c>RetryPolicy.NextCycleAttempt</c> and re-enter later.</summary>
    RetryableFailure = 1,

    /// <summary>An unrecoverable condition was encountered (auth failed, quota exceeded,
    /// hash mismatch). The caller should mark the row <c>FAILED</c> immediately.</summary>
    PermanentFailure = 2,
}
```

### `FlashSkink.Core.Upload.UploadOutcome` (sealed record)

```csharp
namespace FlashSkink.Core.Upload;

public sealed record UploadOutcome
{
    public required UploadOutcomeStatus Status { get; init; }

    /// <summary>Provider-side identifier returned by <see cref="IStorageProvider.FinaliseUploadAsync"/>.
    /// Non-null only when <see cref="Status"/> is <see cref="UploadOutcomeStatus.Completed"/>.</summary>
    public string? RemoteId { get; init; }

    /// <summary>Total encrypted bytes uploaded on a successful call. <c>0</c> on failure.</summary>
    public long BytesUploaded { get; init; }

    /// <summary>Failure code on retryable or permanent failure; <see langword="null"/> on completion.</summary>
    public ErrorCode? FailureCode { get; init; }

    /// <summary>Human-readable failure message; <see langword="null"/> on completion.</summary>
    public string? FailureMessage { get; init; }

    public static UploadOutcome Completed(string remoteId, long bytesUploaded) =>
        new() { Status = UploadOutcomeStatus.Completed, RemoteId = remoteId, BytesUploaded = bytesUploaded };

    public static UploadOutcome Retryable(ErrorCode code, string message) =>
        new() { Status = UploadOutcomeStatus.RetryableFailure, FailureCode = code, FailureMessage = message };

    public static UploadOutcome Permanent(ErrorCode code, string message) =>
        new() { Status = UploadOutcomeStatus.PermanentFailure, FailureCode = code, FailureMessage = message };
}
```

`sealed record` (class) rather than `readonly record struct` — `UploadAsync` is called once per blob (not per range), so a one-allocation-per-call cost is irrelevant; the record's mutable-init semantics keep the factory shapes readable.

### `FlashSkink.Core.Upload.RangeUploader` (sealed class)

Summary intent: orchestrates the §15.3 single-blob upload state machine. Pure I/O orchestration — no `TailUploads` writes, no notification publishes, no cycle-level retries. Returns a `Result<UploadOutcome>` where `Result.Fail` indicates an error the *caller* must propagate (cancelled, brain-write failure during session bookkeeping); `Result.Ok(UploadOutcome.*)` carries the per-blob upload outcome.

Constructor:

```csharp
public RangeUploader(
    UploadQueueRepository uploadQueueRepository,
    IClock clock,
    RetryPolicy retryPolicy,
    ILogger<RangeUploader> logger);
```

Public surface:

```csharp
public Task<Result<UploadOutcome>> UploadAsync(
    string fileId,
    string providerId,
    IStorageProvider provider,
    BlobRecord blob,
    string blobAbsolutePath,
    UploadSessionRow? existingSession,
    CancellationToken ct);
```

**Caller contract:**

- The caller (`UploadQueueService`, §3.4) is responsible for:
  - Calling `MarkUploadingAsync` *before* invoking `UploadAsync`.
  - Resolving `blobAbsolutePath` from `blob.BlobPath` + `skinkRoot` (RangeUploader has no `VolumeContext` reference).
  - Looking up `existingSession` via `LookupSessionAsync` (added in §3.4) or passing `null` when no session row exists yet.
  - Performing the brain transaction on `Completed` (`MarkUploadedAsync` + `DeleteSessionAsync`) and on `PermanentFailure` (`MarkFailedAsync` + `DeleteSessionAsync`).
  - Consulting `RetryPolicy.NextCycleAttempt` on `RetryableFailure`.
- `RangeUploader` is responsible for:
  - The §21.1 in-range retry ladder (delegated to `RetryPolicy.NextRangeAttempt` + `IClock.Delay`).
  - Inserting / upserting the `UploadSessions` row at session open (`GetOrCreateSessionAsync`).
  - Updating `UploadSessions.BytesUploaded` after every successful range (`UpdateSessionProgressAsync`).
  - Deleting the `UploadSessions` row when restarting from session-expired (`DeleteSessionAsync` with `CancellationToken.None`).
  - Best-effort `provider.AbortUploadAsync` before deleting an expired session row.

The reason the *caller* deletes the session on `Completed` (rather than `RangeUploader` doing it) is that §15.3 step 7c requires the status flip and the session delete to happen in *one* brain transaction; that transaction belongs to the caller because it also writes the `RemoteId` and the `ActivityLog` row.

## Internal types

None. (The orchestration is one method on `RangeUploader`; private helpers are local methods or `static` private methods inside the class.)

## Method-body contracts

### `RangeUploader.UploadAsync(...)`

**Pre:**
- `fileId` and `providerId` are non-empty; `blob` is non-null.
- `blobAbsolutePath` exists and contains exactly `blob.EncryptedSize` bytes (the WAL invariant — Principle 30 — guarantees this for any blob whose `Blobs` row is committed).
- `existingSession` is either `null` or has matching `(FileId, ProviderId)`.
- `provider.ProviderID == providerId`.

**Postconditions on success (`Result.Ok(UploadOutcome.Completed)`):**
- The remote object exists at `provider`-side identifier `RemoteId`.
- The `UploadSessions` row may or may not be present (caller deletes it as part of its commit transaction).
- `blob.EncryptedXxHash` matches the remote XXHash64 if the provider implements `ISupportsRemoteHashCheck`; otherwise the verification was skipped and the GCM tag in the blob bytes is the only authenticator.

**Postconditions on `Result.Ok(UploadOutcome.Retryable)`:**
- The `UploadSessions` row reflects the highest provider-confirmed `BytesUploaded` reached during the attempt.
- No remote object was finalised (the §15.3 finalise step was either not reached or itself returned a retryable failure).

**Postconditions on `Result.Ok(UploadOutcome.Permanent)`:**
- The `UploadSessions` row may still exist (caller deletes it in its `MarkFailedAsync` transaction).
- For `ChecksumMismatch`: a remote object was finalised under `RemoteId`, but its bytes are not what we wrote. The caller is expected to delete that object (V1 leaves this to manual cleanup or to a future Phase 5 self-heal pass; it is **not** done here per dev plan §3.3 step 5).

**Stage flow** (literal mapping of dev plan §3.3 steps 1–6):

1. **Resume or open session.**
   - If `existingSession is null`: open a fresh session via `provider.BeginUploadAsync(remoteName, blob.EncryptedSize, ct)` where `remoteName = blob.BlobId + ".bin"`. Stamp the returned `UploadSession` with `FileID = fileId, ProviderID = providerId`. Persist via `uploadQueueRepository.GetOrCreateSessionAsync(...)`. `startOffset = 0`.
   - If `existingSession != null` and `existingSession.SessionExpiresUtc <= clock.UtcNow`: reconstruct an `UploadSession` from the row, best-effort `provider.AbortUploadAsync(reconstructed, CancellationToken.None)`, `uploadQueueRepository.DeleteSessionAsync(fileId, providerId, CancellationToken.None)`, then fall through to the open-fresh path above.
   - If `existingSession != null` and not expired: reconstruct an `UploadSession`, call `provider.GetUploadedBytesAsync(reconstructed, ct)`. The reconciled offset is `Math.Min(providerReported, existingSession.BytesUploaded)`. If the reconciled value is *less* than `existingSession.BytesUploaded`, also call `uploadQueueRepository.UpdateSessionProgressAsync` to bring the row down — never advance the row past what the provider has confirmed (see "Key constraints" §"Crash-consistency invariant" below). `startOffset = reconciled`.

2. **Open the blob file.** `File.OpenRead` at `blobAbsolutePath` with `FileShare.Read`. (Phase 5 self-healing reads the same blob; allowing concurrent readers is forward-compatible.) Validate `stream.Length == blob.EncryptedSize`; on mismatch return `Result.Ok(UploadOutcome.Permanent(BlobCorrupt, "Local blob length differs from Blobs.EncryptedSize."))` — **no upload is attempted** because §13.6 / §13.7 are violated and the brain is the source of truth. Seek to `startOffset`.

3. **Range loop with per-range retry** (the §21.1 in-range ladder lives here, not in `UploadQueueService`):

   ```csharp
   using var bufferOwner = MemoryPool<byte>.Shared.Rent(UploadConstants.RangeSize);
   var buffer = bufferOwner.Memory[..UploadConstants.RangeSize];
   long bytesUploaded = startOffset;

   while (bytesUploaded < blob.EncryptedSize)
   {
       ct.ThrowIfCancellationRequested();
       int rangeLen = (int)Math.Min(blob.EncryptedSize - bytesUploaded, UploadConstants.RangeSize);
       var rangeMemory = buffer[..rangeLen];

       int totalRead = 0;
       while (totalRead < rangeLen)
       {
           int read = await stream.ReadAsync(rangeMemory.Slice(totalRead, rangeLen - totalRead), ct);
           if (read == 0) return Result.Ok(UploadOutcome.Permanent(
               ErrorCode.BlobCorrupt, "Local blob ended before EncryptedSize was reached."));
           totalRead += read;
       }

       int rangeAttempt = 1;
       while (true)
       {
           var result = await provider.UploadRangeAsync(session, bytesUploaded, rangeMemory, ct);
           if (result.Success)
           {
               bytesUploaded += rangeLen;
               var update = await uploadQueueRepository.UpdateSessionProgressAsync(
                   fileId, providerId, bytesUploaded, ct);
               if (!update.Success) return Result<UploadOutcome>.Fail(update.Error!);
               session = session with { BytesUploaded = bytesUploaded };
               break;  // advance outer loop
           }

           var code = result.Error!.Code;

           if (code == ErrorCode.UploadSessionExpired)
           {
               // Restart from step 1 in fresh-session mode. Best-effort cleanup uses None.
               await provider.AbortUploadAsync(session, CancellationToken.None);
               await uploadQueueRepository.DeleteSessionAsync(fileId, providerId, CancellationToken.None);
               // Open fresh session; reset bytesUploaded; re-seek to 0.
               // (See "Session restart" helper below.)
               // After helper succeeds: bytesUploaded = 0; stream.Seek(0, SeekOrigin.Begin).
               // Then break out of inner-retry to the outer while; continue from new bytesUploaded.
               (session, var sessionResetResult) = await OpenFreshSessionAsync(...);
               if (!sessionResetResult.Success) return Result<UploadOutcome>.Fail(sessionResetResult.Error!);
               bytesUploaded = 0;
               stream.Seek(0, SeekOrigin.Begin);
               break;  // restart outer loop from offset 0
           }

           if (IsPermanent(code))
           {
               return Result.Ok(UploadOutcome.Permanent(code, result.Error!.Message));
           }

           // Retryable: consult RetryPolicy.
           var decision = retryPolicy.NextRangeAttempt(rangeAttempt);
           if (decision.Outcome == RetryOutcome.EscalateCycle)
           {
               return Result.Ok(UploadOutcome.Retryable(code, result.Error!.Message));
           }

           _logger.LogWarning(
               "Range attempt {Attempt} failed for {FileId} at offset {Offset}: {Code}. Retrying after {Delay}.",
               rangeAttempt, fileId, bytesUploaded, code, decision.Delay);
           await clock.Delay(decision.Delay, ct);
           rangeAttempt++;
       }
   }
   ```

   `IsPermanent(ErrorCode)`: `ProviderAuthFailed`, `ProviderQuotaExceeded`, `TokenRevoked`, `ChecksumMismatch`. (Static helper inside the class.)
   Retryable codes (the implicit complement, but for `UploadRangeAsync`-class failures): `ProviderUnreachable`, `ProviderRateLimited`, `Timeout`, `UploadFailed`, `Unknown`. The list is documented in an XML comment above `IsPermanent` so a reviewer doesn't have to derive it.
   `Cancelled` (`OperationCanceledException`): not handled in the inner branch — it propagates by re-throw inside the `catch` ladder to the outer public-method catch (Principle 14, dev plan §3.3 step 3 cancellation bullet).

4. **Finalise.** `await provider.FinaliseUploadAsync(session, ct)`. On retryable failure: return `UploadOutcome.Retryable`. On permanent failure: return `UploadOutcome.Permanent`. On success: `RemoteId = result.Value`.

5. **Verify.** If `provider is ISupportsRemoteHashCheck hashCheck`:
   - `var hashResult = await hashCheck.GetRemoteXxHash64Async(remoteId, ct)`.
   - On failure: return `UploadOutcome.Retryable(hashResult.Error.Code, ...)` — verification I/O failure is transient (the provider was up enough to finalise; a hash-fetch might just be transient).
   - Parse `blob.EncryptedXxHash` — 16-character lowercase hex, written by `WritePipeline` as `xxhash.ToString("x16")` ([src/FlashSkink.Core/Engine/WritePipeline.cs:230](src/FlashSkink.Core/Engine/WritePipeline.cs:230)) — using `ulong.Parse(blob.EncryptedXxHash, NumberStyles.HexNumber, CultureInfo.InvariantCulture)`.
   - Compare. On mismatch: log at `Error` and return `UploadOutcome.Permanent(ChecksumMismatch, ...)` per dev plan §3.3.
   - If the provider is *not* `ISupportsRemoteHashCheck`: log at `Information` ("Skipping remote-hash verification for {ProviderId}; provider does not support hash-check.") and proceed. The GCM tag in the blob already authenticates ciphertext (Principle 6). V1 cloud providers will get their own per-provider verify paths in Phase 4.

6. **Return `Completed`.** `return Result.Ok(UploadOutcome.Completed(remoteId, blob.EncryptedSize))`. The caller writes the brain transaction.

**Catch ladder** (at the public-method outer try):

1. `catch (OperationCanceledException ex) { return Result<UploadOutcome>.Fail(ErrorCode.Cancelled, "Range upload was cancelled.", ex); }` — first.
2. `catch (FileNotFoundException ex)` and `catch (DirectoryNotFoundException ex)` — the local blob is gone. Return `Result<UploadOutcome>.Fail(ErrorCode.BlobCorrupt, "Local blob file is missing.", ex)` — the WAL invariant says this can only happen if the brain row points at a path that was hand-deleted. The caller treats this as `Result.Fail`, not as an `UploadOutcome`, because the precondition is broken.
3. `catch (IOException ex) { return Result<UploadOutcome>.Fail(ErrorCode.UploadFailed, "I/O error during range upload.", ex); }`.
4. `catch (UnauthorizedAccessException ex) { return Result<UploadOutcome>.Fail(ErrorCode.ProviderUnreachable, "Access denied during range upload.", ex); }`.
5. `catch (Exception ex) { return Result<UploadOutcome>.Fail(ErrorCode.Unknown, "Unexpected error during range upload.", ex); }` — last.

The buffer owner is `using var`-disposed in every path including failure (Principle 16); the blob `FileStream` is `await using var`-disposed.

### `OpenFreshSessionAsync` (private local helper)

Encapsulates the "open a new session, persist the row, return the new `UploadSession`" sequence used in step 1 (cold open) and inside the range-loop on `UploadSessionExpired`. Returns `(UploadSession session, Result rowPersistOutcome)` — failures of the persist call propagate up as `Result.Fail`.

### `ReconstructSession` (private static helper)

Builds an `UploadSession` value from an `UploadSessionRow`. Used twice: once at resume, once at the abort-before-restart path on expiry detection.

```csharp
private static UploadSession ReconstructSession(UploadSessionRow row) => new()
{
    FileID = row.FileId,
    ProviderID = row.ProviderId,
    SessionUri = row.SessionUri,
    ExpiresAt = new DateTimeOffset(row.SessionExpiresUtc, TimeSpan.Zero),
    BytesUploaded = row.BytesUploaded,
    TotalBytes = row.TotalBytes,
    LastActivityUtc = new DateTimeOffset(row.LastActivityUtc, TimeSpan.Zero),
};
```

The `DateTimeKind` in brain rows is `Unspecified` (parsed via `RoundtripKind` from a `"O"`-formatted UTC string); the helper attaches `TimeSpan.Zero` to make the `DateTimeOffset` unambiguously UTC.

### `IsPermanent` (private static helper)

```csharp
private static bool IsPermanent(ErrorCode code) => code is
    ErrorCode.ProviderAuthFailed or
    ErrorCode.ProviderQuotaExceeded or
    ErrorCode.TokenRevoked or
    ErrorCode.ChecksumMismatch;
```

Single source of truth; tests assert the full list via `[Theory]`.

## Integration points

This PR consumes:

- `FlashSkink.Core.Abstractions.Providers.IStorageProvider` — the frozen contract; `RangeUploader` casts to `ISupportsRemoteHashCheck` for verification.
- `FlashSkink.Core.Abstractions.Providers.UploadSession` — the value passed to provider methods.
- `FlashSkink.Core.Abstractions.Time.IClock` — for retry waits (§3.2).
- `FlashSkink.Core.Upload.RetryPolicy` and `RetryDecision` / `RetryOutcome` (§3.2).
- `FlashSkink.Core.Upload.UploadConstants.RangeSize` (§3.1).
- `FlashSkink.Core.Metadata.UploadQueueRepository` — `GetOrCreateSessionAsync`, `UpdateSessionProgressAsync`, `DeleteSessionAsync` (§1.6).
- `FlashSkink.Core.Abstractions.Models.BlobRecord` — `BlobId`, `EncryptedSize`, `EncryptedXxHash`.
- `FlashSkink.Core.Metadata.UploadSessionRow` — input shape for `existingSession`.

The `Blobs.EncryptedXXHash` storage format: 16-character lowercase hex, written by `WritePipeline` via `xxhash.ToString("x16")` and read by parsing with `NumberStyles.HexNumber, CultureInfo.InvariantCulture`. The format is verified by the existing `WritePipelineTests` round-trip.

Not consumed: `WritePipeline`, `FlashSkinkVolume`, `INotificationBus`, `BackgroundFailureRepository`. Those are the §3.4 caller's responsibilities.

## Principles touched

- **Principle 1** — `RangeUploader.UploadAsync` returns `Task<Result<UploadOutcome>>`. Every failure path produces a `Result.Fail` or a `Result.Ok(UploadOutcome.Retryable|Permanent)`. No exceptions cross the public boundary.
- **Principle 3** — `RangeUploader` reads ciphertext from the local blob path; never from a tail.
- **Principle 4** — `RangeUploader` does **not** touch the Phase 1 commit boundary; it only mutates `UploadSessions` (Phase 2 bookkeeping).
- **Principle 5** — `UploadSessions` row persists across session expiry / reconnect; `RangeUploader` resumes from `Math.Min(providerReported, rowBytes)` on every entry.
- **Principle 6** — every byte sent to `provider.UploadRangeAsync` is ciphertext from `Blobs.BlobPath`; the caller never sees plaintext.
- **Principle 8** — `RangeUploader` lives in `Core/Upload/`; references `Core.Abstractions` only.
- **Principle 13** — `CancellationToken ct` is the final parameter on every async method.
- **Principle 14** — `OperationCanceledException` is the first catch in the public method's outer try.
- **Principle 15** — the catch ladder is `OperationCanceledException` → `FileNotFoundException` → `DirectoryNotFoundException` → `IOException` → `UnauthorizedAccessException` → `Exception`. Distinct codes per type.
- **Principle 16** — `using var bufferOwner` and `await using var stream` ensure disposal on every path including failure.
- **Principle 17** — best-effort `AbortUploadAsync` and `DeleteSessionAsync` on session-expired use the literal `CancellationToken.None`. Documented in code comments at each call site.
- **Principle 18** — one 4 MB pooled buffer per blob, reused across all ranges.
- **Principle 19** — `IMemoryOwner<byte>` returned by `MemoryPool<byte>.Shared.Rent`; disposed by the `using` scope.
- **Principle 23** — `IStorageProvider`, `UploadSession`, `IProviderRegistry`, `INetworkAvailabilityMonitor` are not modified. `ISupportsRemoteHashCheck` is a *new* additive capability interface — Principle 23 explicitly sanctions this evolution path.
- **Principle 26** — no secrets logged. `RangeUploader` logs `FileId`, `ProviderId`, `BytesUploaded`, `BlobID`, `Offset`, `RangeAttempt`, `Delay`, and `ErrorCode`. None of these are secret.
- **Principle 27** — every `Result.Fail` site logs once via `ILogger<RangeUploader>`. The caller (`UploadQueueService`) logs the returned `ErrorContext`/`UploadOutcome.Failure*` again at its own boundary.
- **Principle 28** — `ILogger<RangeUploader>` from `Microsoft.Extensions.Logging.Abstractions`.
- **Principle 30** — the crash-consistency invariant for `UploadSessions` is preserved: `BytesUploaded` is updated *after* the provider acknowledges, never before; on resume, the provider's reported value is the floor; on session-expired, the row is deleted before the new session row is upserted.

Note: Principle 24 (background failures: log + bus + persist) is **not** exercised by `RangeUploader` directly — the caller (`UploadQueueService`, §3.4) owns the notification / bus / persist surface. `RangeUploader` only logs.

## Test spec

All tests in `tests/FlashSkink.Tests/Upload/RangeUploaderTests.cs`. Naming `Method_State_ExpectedBehavior` per established convention. Each test constructs:

- A per-test temp dir (skink root + tail root).
- A real on-disk encrypted blob file written via `File.WriteAllBytes` of a deterministic ciphertext fixture (the test does not need to drive the full `WritePipeline`; it constructs `BlobRecord` and the matching ciphertext directly — XXHash64 computed inline).
- A real `FileSystemProvider` rooted at the tail temp dir, optionally wrapped in `FaultInjectingStorageProvider`.
- A real `UploadQueueRepository` over a temp-dir SQLite brain (test harness — see below).
- `FakeClock` and `RetryPolicy.Default`.

### Test cases

**Happy path:**
- `UploadAsync_FreshSessionSmallBlob_FinalisesAndReturnsCompleted` — 1 MiB blob, single range. Asserts `Status == Completed`, `RemoteId` not null, file exists at `{tailRoot}/blobs/{xx}/{yy}/{BlobID}.bin`, `UploadSessions` row was created and updated with `BytesUploaded == EncryptedSize`. (Caller deletes the row, not RangeUploader.)
- `UploadAsync_FreshSessionMultiRangeBlob_AllRangesUploadedInOrder` — 16 MiB blob → 4 ranges. Assert each range was sent in order.
- `UploadAsync_FreshSessionExactRangeBoundary_LastRangeIsExactlyRangeSize` — exactly 8 MiB → 2 ranges of 4 MiB each.
- `UploadAsync_FreshSessionPartialLastRange_LastRangeShorterThanRangeSize` — 5 MiB → range 1 = 4 MiB, range 2 = 1 MiB.

**Resume / reconcile:**
- `UploadAsync_ResumeFromExistingSessionInSync_ContinuesFromBytesUploaded` — `existingSession` has `BytesUploaded = 4 MiB`, provider reports 4 MiB; uploader continues from offset 4 MiB.
- `UploadAsync_ResumeProviderReportsLessThanRow_ReconcilesDownAndContinues` — row says 8 MiB, provider says 4 MiB; uploader reconciles to 4 MiB and `UpdateSessionProgressAsync` is called to update the row.
- `UploadAsync_ResumeProviderAlreadyComplete_FinalisesImmediately` — row says 16 MiB, provider says 16 MiB, total is 16 MiB; the range loop is a no-op and finalise is called once.
- `UploadAsync_ResumeExpiredSession_AbortsAndOpensFresh` — row's `SessionExpiresUtc` is in the past relative to `FakeClock.UtcNow`; uploader best-effort aborts, deletes the row, opens a fresh session, uploads from byte 0.

**Session expiry mid-upload:**
- `UploadAsync_SessionExpiredOnRange3_RestartsFromByteZero` — `FaultInjectingStorageProvider.ForceSessionExpiryAfter(2)` (so range 3 returns expired); uploader best-effort aborts old session, deletes session row, opens new session, restarts from byte 0; final blob arrives intact.
- `UploadAsync_SessionExpiredTwiceInOneCall_HandlesBothRestarts` — chained expiries on different ranges; eventually completes.

**Per-range retry (§21.1 in-range ladder):**
- `UploadAsync_TransientFailureOnce_RetriesAfter1sAndSucceeds` — `FailNextRange()` once with `UploadFailed`. Uploader consults `RetryPolicy.NextRangeAttempt(1)` → `Wait(1s)`. `FakeClock.Advance(1s)` releases the wait. Range succeeds on attempt 2.
- `UploadAsync_TransientFailureTwice_RetriesAfter1sThen4s` — `FailNextRangeWith(ProviderRateLimited)` twice. Asserts `FakeClock` advanced exactly 1 s + 4 s = 5 s of pending delay before the 3rd attempt succeeded.
- `UploadAsync_TransientFailureThreeTimes_RetriesAfter1s4s16s` — three failures. Asserts the full 1 s / 4 s / 16 s ladder was consulted.
- `UploadAsync_TransientFailureFourTimes_EscalatesAsRetryable` — four failures. Returns `UploadOutcome.Retryable` after the 4th attempt (no 5th retry per `RetryPolicy.NextRangeAttempt(4) == Escalate`).

**Permanent failure codes:**
- `UploadAsync_ProviderAuthFailed_ReturnsPermanent` `[Theory]` over `ProviderAuthFailed`, `ProviderQuotaExceeded`, `TokenRevoked` — all map to `UploadOutcome.Permanent` immediately, no retries.
- `UploadAsync_PermanentFailureOnRange1_NoRetriesAttempted` — uploader does **not** call `RetryPolicy.NextRangeAttempt` (verified by tracking provider call count: only 1 `UploadRangeAsync` invocation).

**Verification (`ISupportsRemoteHashCheck`):**
- `UploadAsync_FileSystemProviderHashMatches_ReturnsCompleted` — the default happy-path test; verification runs and matches.
- `UploadAsync_FileSystemProviderHashMismatches_ReturnsPermanentChecksumMismatch` — test fixture writes a blob whose `EncryptedXxHash` does **not** match the actual ciphertext bytes (mutate one byte in the brain row). Asserts `UploadOutcome.Permanent(ChecksumMismatch)` and the remote object **was finalised** (uploader does not delete it — that's V2 / Phase 5 self-heal).
- `UploadAsync_HashCheckIoFails_ReturnsRetryable` — wrap `FileSystemProvider` in a decorator that fails `GetRemoteXxHash64Async` once; assert `UploadOutcome.Retryable` is returned with the underlying error code.
- `UploadAsync_ProviderWithoutHashCheckCapability_SkipsVerificationAndReturnsCompleted` — a stub `IStorageProvider` that does **not** implement `ISupportsRemoteHashCheck`; asserts the upload completes without invoking any hash-check logic and an `Information`-level log records the skip.

**Local-blob preconditions:**
- `UploadAsync_LocalBlobMissing_ReturnsResultFailBlobCorrupt` — `blobAbsolutePath` does not exist; returns `Result.Fail(BlobCorrupt)` (not an `UploadOutcome`, because the precondition itself is broken).
- `UploadAsync_LocalBlobShorterThanEncryptedSize_ReturnsPermanentBlobCorrupt` — local file is 4 MiB but `blob.EncryptedSize` says 8 MiB; uploader detects mismatch on stream open and returns `UploadOutcome.Permanent(BlobCorrupt)`.

**Cancellation:**
- `UploadAsync_CancelledBeforeFirstRange_ReturnsResultFailCancelled` — pre-cancel `ct`. Uploader observes at the top of step 3.
- `UploadAsync_CancelledMidRangeUpload_PreservesSessionRow` — cancel after range 2 finishes. Asserts the session row's `BytesUploaded == 8 MiB` (the last confirmed value), `Result.Fail(Cancelled)` returned, no remote object finalised.
- `UploadAsync_CancelledDuringRetryWait_PreservesSessionRow` — fault-inject one failure, `FakeClock` is mid-`Delay(1s)`, cancel `ct`. Asserts cancellation propagates and the session row is preserved.

**Session row crash-consistency:**
- `UploadAsync_SessionUpdateFails_ReturnsResultFailDatabaseWriteFailed` — wrap `UploadQueueRepository` in a fault-injecting repository decorator (test-only helper) that fails `UpdateSessionProgressAsync`. Uploader returns `Result.Fail(DatabaseWriteFailed)` and the *next* call resumes correctly from the previous confirmed value.
- `UploadAsync_BytesUploadedNeverExceedsProviderConfirmed` — property-style assertion: at every point in the call the row's `BytesUploaded` is `<= the provider's accepted bytes`. (Implemented via interleaved test instrumentation rather than FsCheck — full FsCheck crash-consistency lives in §3.4 / Phase 5 per dev plan acceptance criteria.)

**Logging assertions:**
- `UploadAsync_RangeRetried_LogsWarningWithAttemptAndDelay` — uses a `RecordingLogger<RangeUploader>` and asserts a `Warning`-level entry containing the attempt number and the delay duration.
- `UploadAsync_HashCheckSkipped_LogsInformation` — asserts the "Skipping remote-hash verification" Information log.

### Existing tests

`FileSystemProviderTests`, `FaultInjectingStorageProviderTests`, `RetryPolicyTests`, `FakeClockTests`, all Phase 2 tests — must remain green. No regressions.

The change to `FileSystemProvider` (adding `: ISupportsRemoteHashCheck` and changing the existing method's visibility) does not change any tested behaviour; existing `FileSystemProviderTests` stay green. **Add one new test** in `FileSystemProviderTests.cs`: `Provider_ImplementsISupportsRemoteHashCheck_AndCanComputeHashOnFinalisedBlob`. ~30 LOC.

## Acceptance criteria

- [ ] All listed files exist; build clean on `ubuntu-latest` and `windows-latest` with `--warnaserror`.
- [ ] `dotnet test` green: every Phase 0–2 + §3.1 + §3.2 test still passes; every new §3.3 test passes.
- [ ] `dotnet format --verify-no-changes` reports clean.
- [ ] `ISupportsRemoteHashCheck` is public, documented, and lives in `FlashSkink.Core.Abstractions.Providers`.
- [ ] `UploadOutcome`, `UploadOutcomeStatus`, `RangeUploader` are public, documented, and live in `FlashSkink.Core.Upload`.
- [ ] `FileSystemProvider` implements `ISupportsRemoteHashCheck`; the method body is unchanged from §3.1.
- [ ] `IStorageProvider` and `UploadSession` are unmodified (Principle 23 verified by `git diff` on those two files).
- [ ] `ErrorCode.cs` is unmodified (cross-cutting decision 2).
- [ ] No public API outside the listed types is added or modified. `WritePipeline`, `FlashSkinkVolume`, `UploadQueueRepository` retain their existing public surfaces.
- [ ] `RangeUploader.UploadAsync` does **not** call `MarkUploadingAsync`, `MarkUploadedAsync`, or `MarkFailedAsync` (caller-responsibility — verified by code inspection at Gate 2).
- [ ] The 4 MiB buffer is rented exactly once per `UploadAsync` call (verified by a test that counts `MemoryPool<byte>.Shared.Rent` calls — implementation can use a wrapper `MemoryPool` for the test, or — simpler — assert behaviourally that wall-clock memory pressure does not grow with range count).

## Line-of-code budget

| File | Approx LOC |
|---|---|
| `Core.Abstractions/Providers/ISupportsRemoteHashCheck.cs` | 30 |
| `Core/Upload/UploadOutcomeStatus.cs` | 25 |
| `Core/Upload/UploadOutcome.cs` | 90 |
| `Core/Upload/RangeUploader.cs` | 430 |
| `Core/Providers/FileSystemProvider.cs` (delta) | +3 / −1 |
| **src subtotal** | **~580** |
| `tests/Upload/RangeUploaderTests.cs` | 520 |
| `tests/_TestSupport/BrainTestHarness.cs` (if newly authored) | 80 |
| `tests/Providers/FileSystemProviderTests.cs` (delta) | +30 |
| **tests subtotal** | **~630** |
| **Total** | **~1210** |

A medium-large PR by Phase 3 standards. The bulk is `RangeUploader.cs` (the state machine itself) and `RangeUploaderTests.cs` (the case-by-case coverage of every §15.3 / §21.1 branch). The dev plan §3.3 deliberately scopes this as a single PR — splitting would defer the verification path or the resume path to a follow-up, and both are needed for §3.4 to land cleanly.

## Non-goals

- **No `UploadQueueService` / orchestrator / per-tail worker.** §3.4.
- **No `UploadWakeupSignal`.** §3.4.
- **No `LookupSessionAsync` on `UploadQueueRepository`.** That repository method is added in §3.4 (the dev plan flags it explicitly). §3.3's `UploadAsync` takes the `UploadSessionRow?` from a parameter; the caller is responsible for the lookup.
- **No `MarkUploadingAsync` / `MarkUploadedAsync` / `MarkFailedAsync` calls.** §3.4 wraps `UploadAsync` in those.
- **No notification publishing.** §3.4 owns `INotificationBus`.
- **No `BackgroundFailures` writes.** §3.4 publishes; `PersistenceNotificationHandler` (existing from Phase 2) captures.
- **No cycle-level retry loop.** §3.4 owns `RetryPolicy.NextCycleAttempt`.
- **No remote-object cleanup on `ChecksumMismatch`.** Per dev plan §3.3 step 5, the V1 uploader leaves the bad remote object in place; manual cleanup or a future Phase 5 self-heal pass handles it. A test asserts this behaviour (`UploadAsync_FileSystemProviderHashMismatches_LeavesRemoteObjectInPlace`).
- **No `Verify*` paths for cloud providers.** §15.7 cloud-provider rows (Google Drive `md5Checksum`, Dropbox content-hash, OneDrive metadata hash) are Phase 4. `FileSystemProvider`'s XXHash64 path is the only verification implemented in §3.3.
- **No `BrainMirrorService`.** §3.5.
- **No `RegisterTailAsync` / `WriteBulkAsync` / volume integration.** §3.6.
- **No new `ErrorCode` values** — cross-cutting decision 2.
- **No FsCheck crash-consistency property test.** The full property test for §21.3 invariants on `UploadSessions` / `TailUploads` lives in §3.4 / Phase 5 per dev plan acceptance criteria. §3.3's tests are deterministic per-scenario.
- **No `SystemClock` DI registration in any host project.** Phase 6.
- **No swap of `FaultInjectingStorageProvider`'s latency injection from `Task.Delay` to `IClock.Delay`.** The §3.1 plan's forward-looking note about a "follow-up rev in §3.3" does not bind §3.3 — the dev plan §3.3 itself does not mention this fixture. Defer to whichever later PR genuinely needs deterministic latency in fault injection (likely §3.4 if any orchestrator test cares about the wall-clock cost of provider latency under load).

## Open questions for Gate 1

### 1. `BrainTestHarness` — new file vs. reuse-or-inline

Phase 2 tests already construct per-test SQLite brains. Three options:

- **A. Inline brain bootstrap in `RangeUploaderTests.cs`.** Each test fixture sets up its own connection + repos. Lowest abstraction; some duplication.
- **B. Add `tests/FlashSkink.Tests/_TestSupport/BrainTestHarness.cs`** as a shared helper for §3.3 and forward (likely needed by §3.4 `UploadQueueServiceTests` as well). One ~80-LOC file.
- **C. Reuse an existing harness** if one is hiding in Phase 2 tests. **Implementer to verify by inspecting `tests/FlashSkink.Tests/Engine/`** — if `WritePipelineTests.cs` or similar already lifts a brain factory into `_TestSupport/`, reuse it. If it inlines (likely), choose between A and B.

**My recommendation:** B if no harness exists; C if one does. A is acceptable but the `UploadQueueServiceTests` in §3.4 will need the same machinery, and lifting at §3.3 saves a duplicate later.

### 2. `UploadOutcome` shape — class record vs. readonly record struct

Dev plan says "sealed record". Two options:

- **A. Class record** (plan default). One allocation per `UploadAsync` call. Mutable-init factories read cleanly.
- **B. `readonly record struct`.** Allocation-free. Three nullable string properties (`RemoteId`, `FailureMessage`) and a nullable `ErrorCode?` make the struct ~24–32 bytes — larger than the typical struct but still fine.

**My recommendation:** A (class record). One alloc per blob upload is a non-issue (called per-file, not per-range); the record's `with`-expression and factory ergonomics are slightly nicer for a class record. The dev plan's "sealed record" wording also reads as class record by default.

### 3. Hash-check capability vs. "FileSystem-row only" verification

Dev plan §3.3 introduces the `ISupportsRemoteHashCheck` capability interface and has `FileSystemProvider` implement it. The blueprint §15.7 has different verification mechanisms per provider (Google → MD5 vs. XXHash64; Dropbox → content-hash; OneDrive → metadata hash). A single `GetRemoteXxHash64Async` interface fits FileSystem and any future native-XXHash64 cloud provider, but does not fit the V1 cloud providers' native hashes.

Two paths:

- **A. Single capability for XXHash64 (plan default).** Cloud providers implement their own per-provider verify paths in Phase 4 (separate code path, not via this interface). FileSystem and any future XXHash64-native provider use this interface. Simple; matches the dev plan literally.
- **B. Generic hash-check interface** with multiple algorithm support (`GetRemoteHashAsync(remoteId, HashAlgorithm, ct)`). Extensible but speculative; cloud providers' verification logic is more involved than just hash comparison (Google's MD5 is over plaintext, not ciphertext, requiring the local MD5 to be computed first — that doesn't fit a simple "compare hash" call).

**My recommendation:** A. The dev plan's design decision is sound — Phase 4 cloud providers will have richer per-provider verify methods that don't fit a one-size capability.

---

*Plan ready for review. Stop at Gate 1 per `CLAUDE.md` step 3.*
