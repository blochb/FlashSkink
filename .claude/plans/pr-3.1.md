# PR 3.1 — Provider seam, FileSystem provider, test infrastructure

**Branch:** `pr/3.1-provider-seam-and-filesystem-provider`
**Blueprint sections:** §10 (all subsections), §15.2 (FileSystem row), §15.7 (FileSystem verification), §22.2 (network monitor problem statement), §27 (provider taxonomy), §16.2 (Providers table)
**Dev plan section:** `dev-plan/phase-3-Upload-queue-and-resumable-uploads.md` §3.1

## Scope

Phase 3 begins by introducing the consumer-facing provider seam (`IStorageProvider`, `UploadSession`, `IProviderRegistry`), the network-availability seam (`INetworkAvailabilityMonitor` + `AlwaysOnlineNetworkMonitor` default), the first real `IStorageProvider` implementation (`FileSystemProvider` — backed by a configured local/NAS path, doubling as the cloud-provider test double per `CLAUDE.md`), the `UploadConstants` shared values, and a test-only `FaultInjectingStorageProvider` decorator. No upload orchestration, no retry policy, no range-uploader, no brain-mirror — those are §3.2–§3.5. After this PR, §3.2 (retry policy) can land against frozen contracts.

The PR also extracts `AtomicBlobWriter`'s currently-private directory-fsync helper into a small shared utility so `FileSystemProvider` can reuse the same fsync discipline (Principle 29) without duplicating the Windows-vs-POSIX branching.

## Files to create

### `src/FlashSkink.Core.Abstractions/`

- `Providers/IStorageProvider.cs` — provider contract (frozen by Principle 23 after this PR ships). ~120 LOC including XML docs.
- `Providers/UploadSession.cs` — `sealed record` returned by `BeginUploadAsync` and persisted (sans bookkeeping fields) in `UploadSessions`. ~40 LOC.
- `Providers/IProviderRegistry.cs` — orchestrator-facing lookup contract. ~30 LOC.
- `Providers/INetworkAvailabilityMonitor.cs` — passive online/offline signal. ~35 LOC.

### `src/FlashSkink.Core/`

- `Providers/InMemoryProviderRegistry.cs` — concurrent-dictionary registry; `Register`/`Remove` runtime surface used by §3.6 `RegisterTailAsync` and tests. ~90 LOC.
- `Providers/FileSystemProvider.cs` — real `IStorageProvider` whose "remote" is a configured filesystem path (NAS, external drive, local). Implements §15.2 over `.partial` + sidecar `.session` JSON + atomic rename. ~360 LOC.
- `Providers/FileSystemProviderConfig.cs` — `internal sealed record` for `{"rootPath":"..."}` JSON parse (System.Text.Json source-gen friendly). ~25 LOC.
- `Engine/AlwaysOnlineNetworkMonitor.cs` — V1 stub: `IsAvailable=true`, event never raised. Phase 5 replaces. ~30 LOC.
- `Upload/UploadConstants.cs` — `public static class` holding `RangeSize`, `MaxRangesInFlightPerTail`, `WorkerIdlePollSeconds`, `OrchestratorIdlePollSeconds`, `BrainMirrorIntervalMinutes`, `BrainMirrorRollingCount`. ~30 LOC.
- `Storage/AtomicWriteHelper.cs` — `internal static class` with `FsyncDirectory(string path)` and `WriteAndFsyncAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct)`. Wraps the Windows-no-op / POSIX `RandomAccess.FlushToDisk` logic that currently lives privately inside `AtomicBlobWriter`. ~70 LOC.

### `tests/FlashSkink.Tests/Providers/`

- `FaultInjectingStorageProvider.cs` — `internal sealed` decorator over any `IStorageProvider`. Surface: `FailNextRange()`, `FailNextRangeWith(ErrorCode)`, `ForceSessionExpiryAfter(int)`, `SetRangeLatency(TimeSpan)`. Latency injection delays via `Task.Delay` (no `IClock` dependency in §3.1; §3.2 introduces the clock and a follow-up rev of this fixture in §3.3 will swap to it). ~150 LOC.
- `FileSystemProviderTests.cs` — full coverage of every `IStorageProvider` method on `FileSystemProvider`. ~280 LOC.
- `InMemoryProviderRegistryTests.cs` — register, remove, list active, get by id (hit + miss), thread-safety smoke. ~120 LOC.
- `AlwaysOnlineNetworkMonitorTests.cs` — IsAvailable always true; subscribing to AvailabilityChanged never receives. ~25 LOC.
- `FaultInjectingStorageProviderTests.cs` — confirms each fault knob does what it says. ~120 LOC.

## Files to modify

- `src/FlashSkink.Core/Storage/AtomicBlobWriter.cs` — replace the private `FsyncDirectory` method body with a single call to `AtomicWriteHelper.FsyncDirectory`. The replacement keeps existing behaviour identical (verified by the existing `AtomicBlobWriterTests`). Net delta: ~−20 / +5 LOC.
- `src/FlashSkink.Core.Abstractions/AssemblyInfo.cs` — **create if absent**, add `[assembly: InternalsVisibleTo("FlashSkink.Tests")]`. (Existing on Core; verified absent on Core.Abstractions in exploration.) Needed because `FileSystemProviderConfig` is `internal` and tests construct it directly.

## Dependencies

- **NuGet:** none new. `System.IO.Hashing` (added in §2.5) is reused for the §3.3 hash-check — but that capability and its interface are §3.3, not this PR. `Microsoft.IO.RecyclableMemoryStream` (§2.2) is unused in this PR (FileSystemProvider streams via `FileStream` directly).
- **Project references:** none added. Layering unchanged: `Core.Abstractions/Providers/` types reference no external project; `Core/Providers/FileSystemProvider.cs` references `Core.Abstractions` only.

## Public API surface

All new public types live in `FlashSkink.Core.Abstractions.Providers` unless noted. XML doc summaries (one-line intent) shown; the implementer writes the prose.

### `IStorageProvider` (interface, `FlashSkink.Core.Abstractions.Providers`)

Summary intent: provider-agnostic contract for resumable, session-based blob upload, download, and integrity probing of the encrypted blobs and brain-mirror objects FlashSkink replicates to a tail. Frozen after this PR (Principle 23).

Members (all signatures match blueprint §10.1; cancellation per Principle 13):

```csharp
string ProviderID { get; }
string ProviderType { get; }
string DisplayName { get; }

Task<Result<UploadSession>> BeginUploadAsync(
    string remoteName, long totalBytes, CancellationToken ct);
Task<Result<long>> GetUploadedBytesAsync(
    UploadSession session, CancellationToken ct);
Task<Result> UploadRangeAsync(
    UploadSession session, long offset, ReadOnlyMemory<byte> data, CancellationToken ct);
Task<Result<string>> FinaliseUploadAsync(
    UploadSession session, CancellationToken ct);
Task<Result> AbortUploadAsync(
    UploadSession session, CancellationToken ct);

Task<Result<Stream>> DownloadAsync(string remoteId, CancellationToken ct);
Task<Result> DeleteAsync(string remoteId, CancellationToken ct);
Task<Result<bool>> ExistsAsync(string remoteId, CancellationToken ct);

Task<Result<IReadOnlyList<string>>> ListAsync(string prefix, CancellationToken ct);

Task<Result<ProviderHealth>> CheckHealthAsync(CancellationToken ct);
Task<Result<long>> GetUsedBytesAsync(CancellationToken ct);
Task<Result<long?>> GetQuotaBytesAsync(CancellationToken ct);   // null = unknown / unlimited
```

### `UploadSession` (`sealed record`)

Summary intent: in-memory representation of an open resumable upload. Provider-supplied fields populated by `BeginUploadAsync`; caller-supplied bookkeeping fields stamped by `UploadQueueRepository` before persistence.

```csharp
public sealed record UploadSession
{
    public string FileID { get; init; } = "";
    public string ProviderID { get; init; } = "";
    public required string SessionUri { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required long BytesUploaded { get; init; }
    public required long TotalBytes { get; init; }
    public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UtcNow;
}
```

Note on `required`: `FileID` and `ProviderID` are **not** marked `required` (they default to `""`) because `IStorageProvider.BeginUploadAsync` takes only `(remoteName, totalBytes, ct)` per blueprint §10.1 and cannot supply them. The caller (RangeUploader in §3.3) stamps both fields with a `with`-expression before persisting via `UploadQueueRepository.GetOrCreateSessionAsync`. Discrepancy with blueprint §10.2 quote (`required`) flagged in **Open questions** below.

### `IProviderRegistry` (interface)

```csharp
ValueTask<Result<IStorageProvider>> GetAsync(string providerId, CancellationToken ct);
ValueTask<Result<IReadOnlyList<string>>> ListActiveProviderIdsAsync(CancellationToken ct);
```

Summary intent: orchestrator-facing lookup so `UploadQueueService` (§3.4) does not see a static provider list at construction. Phase 4's `BrainBackedProviderRegistry` is an alternate implementation; this PR ships only `InMemoryProviderRegistry`.

### `INetworkAvailabilityMonitor` (interface)

```csharp
bool IsAvailable { get; }
event EventHandler<bool>? AvailabilityChanged;
```

Summary intent: synchronous best-effort online signal, consulted by background loops on every tick. No async surface in V1 — Phase 5's real implementation is OS-event-driven (§22.2).

### `InMemoryProviderRegistry` (`public sealed`, `Core/Providers/`)

Summary intent: in-process registry. `Register(string providerId, IStorageProvider)` and `Remove(string providerId)` are the runtime surface used by §3.6 `RegisterTailAsync` and by tests. Holds strong references; tests dispose/replace.

Members:

```csharp
public InMemoryProviderRegistry();
public void Register(string providerId, IStorageProvider provider);
public bool Remove(string providerId);
public ValueTask<Result<IStorageProvider>> GetAsync(string providerId, CancellationToken ct);
public ValueTask<Result<IReadOnlyList<string>>> ListActiveProviderIdsAsync(CancellationToken ct);
```

Backing store: `ConcurrentDictionary<string, IStorageProvider>`. `GetAsync` returns `Result.Fail(ProviderUnreachable, "Provider not registered")` on miss — the orchestrator interprets unregistered as offline, identical to the cloud case where the registry has no live adapter.

### `AlwaysOnlineNetworkMonitor` (`public sealed`, `Core/Engine/`)

Summary intent: V1 default. `IsAvailable` returns `true`; `AvailabilityChanged` is never raised. Phase 5 replaces with the OS-mediated `NetworkAvailabilityMonitor`.

### `FileSystemProvider` (`public sealed`, `Core/Providers/`)

Summary intent: real production `IStorageProvider` writing to a configured local/NAS root path. Implements §15.2's "atomic write per range with offset tracking" model; sessions are infinite (`ExpiresAt = DateTimeOffset.MaxValue`). Doubles as the cloud-provider test double.

Constructor:

```csharp
public FileSystemProvider(
    string providerId,
    string displayName,
    string rootPath,
    ILogger<FileSystemProvider> logger);

public static Result<FileSystemProvider> Create(
    string providerId,
    string displayName,
    string rootPath,
    ILogger<FileSystemProvider> logger);
```

The static factory validates `rootPath` exists and is writable (probe write-then-delete of a 1-byte file under a `_health/` subdir). Failure → `Result.Fail(ProviderUnreachable, "Configured root path is not accessible")`. The constructor exists for tests/DI but does no validation — callers should prefer `Create`.

`ProviderType = "filesystem"`.

### `UploadConstants` (`public static class`, `Core/Upload/`)

```csharp
public const int RangeSize = 4 * 1024 * 1024;
public const int MaxRangesInFlightPerTail = 1;
public const int WorkerIdlePollSeconds = 60;
public const int OrchestratorIdlePollSeconds = 30;
public const int BrainMirrorIntervalMinutes = 15;
public const int BrainMirrorRollingCount = 3;
```

Cross-cutting decisions 3 + 4 enforced by single source. `RangeSize` is `int` (not `long`) deliberately — every range is exactly 4 MiB and `int.MaxValue ≈ 2 GiB` provides headroom without forcing callers to cast.

## Internal types

### `AtomicWriteHelper` (`internal static`, `Core/Storage/`)

```csharp
internal static class AtomicWriteHelper
{
    public static void FsyncDirectory(string directoryPath);
    public static async Task WriteAndFsyncAsync(
        string filePath, ReadOnlyMemory<byte> data, CancellationToken ct);
}
```

`FsyncDirectory` is the body lifted from `AtomicBlobWriter.FsyncDirectory` (file [src/FlashSkink.Core/Storage/AtomicBlobWriter.cs:251](src/FlashSkink.Core/Storage/AtomicBlobWriter.cs:251)): no-op on Windows; on POSIX, `File.OpenHandle(...)` + `RandomAccess.FlushToDisk(handle)` swallowing `UnauthorizedAccessException`/`IOException`.

`WriteAndFsyncAsync` is the open-write-flush-flushtodisk sequence shared by `AtomicBlobWriter.WriteAsync` and `FileSystemProvider.UploadRangeAsync`'s pre-finalise checkpoint use case. Throws on failure (caller's catch ladder maps to `Result.Fail`).

`AtomicBlobWriter` modification: replace `FsyncDirectory(...)` private call sites with `AtomicWriteHelper.FsyncDirectory(...)`. Behaviour preserved; existing `AtomicBlobWriterTests` cover the regression surface.

### `FileSystemProviderConfig` (`internal sealed record`, `Core/Providers/`)

```csharp
internal sealed record FileSystemProviderConfig(string RootPath);
```

Carrier for the `Providers.ProviderConfig` JSON column. JSON shape: `{"rootPath":"..."}` per blueprint §16.2.

### `FaultInjectingStorageProvider` (`internal sealed`, `tests/FlashSkink.Tests/Providers/`)

Decorator. Constructor takes the inner `IStorageProvider`. Public knobs:

```csharp
internal sealed class FaultInjectingStorageProvider(IStorageProvider inner) : IStorageProvider
{
    public void FailNextRange();
    public void FailNextRangeWith(ErrorCode code);
    public void ForceSessionExpiryAfter(int rangesUploaded);
    public void SetRangeLatency(TimeSpan delay);
    public void Reset();
    // IStorageProvider members: delegate to `inner` after applying fault state
}
```

State is `internal` (not thread-static) — tests are single-threaded per scenario.

## Method-body contracts

### `FileSystemProvider.BeginUploadAsync(remoteName, totalBytes, ct)`

- **Pre:** `remoteName` is non-empty, doesn't start with `/` or `\`, contains no `..`. `totalBytes >= 0`.
- **Steps:**
  1. `ct.ThrowIfCancellationRequested()`.
  2. Compute sidecar path: `{rootPath}/.flashskink-staging/{SafeRemote(remoteName)}.session`. Compute partial path: `{rootPath}/.flashskink-staging/{SafeRemote(remoteName)}.partial`.
  3. Ensure `.flashskink-staging/` exists (idempotent `Directory.CreateDirectory`); fsync staging dir on first creation via `AtomicWriteHelper.FsyncDirectory`.
  4. Write a small JSON sidecar `{ "totalBytes": N, "createdUtc": "..." }` via `AtomicWriteHelper.WriteAndFsyncAsync`. (System.Text.Json source-gen.)
  5. Return `Result<UploadSession>.Ok(new UploadSession { SessionUri = SafeRemote(remoteName), ExpiresAt = DateTimeOffset.MaxValue, BytesUploaded = 0, TotalBytes = totalBytes })`. `FileID` and `ProviderID` are left at default `""`; the caller stamps them.
- **Errors:** `OperationCanceledException → Cancelled`. `IOException`/`UnauthorizedAccessException → ProviderUnreachable`. `Exception → Unknown`. (Catch ladder per §"Failure-path pattern" below.)
- **Note on `SafeRemote`:** sanitises remote names by replacing `/` and `\` with `_` (provider-internal staging filenames are flat). The original `remoteName` is preserved in the *final* sharded destination computed in `FinaliseUploadAsync`.

### `FileSystemProvider.GetUploadedBytesAsync(session, ct)`

- Read `FileInfo({rootPath}/.flashskink-staging/{session.SessionUri}.partial).Length`. Returns `0` if file is absent (per dev plan: sidecar disappearing means upload restarts, same as cloud expiry).
- Errors: as above; `FileNotFoundException` is **not** an error, returns 0.

### `FileSystemProvider.UploadRangeAsync(session, offset, data, ct)`

- **Pre:** `0 <= offset <= session.TotalBytes`. `data.Length > 0`. `offset + data.Length <= session.TotalBytes`.
- **Steps:**
  1. `ct.ThrowIfCancellationRequested()`.
  2. Open partial path with `FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read`, `useAsync: true`.
  3. `Seek` to `offset`; `WriteAsync(data, ct)`.
  4. `await stream.FlushAsync(ct)`; then `RandomAccess.FlushToDisk(stream.SafeFileHandle)`.
  5. Return `Result.Ok()`.
- **Errors:** `OperationCanceledException → Cancelled`; `IOException → UploadFailed`; `UnauthorizedAccessException → ProviderUnreachable`; `Exception → Unknown`.
- **Note:** Sidecar `.session` JSON is **not** rewritten per range — `GetUploadedBytesAsync` derives current bytes from `.partial` length, which advances atomically per `WriteAsync`+flush. Surviving the sidecar's deletion is intentional (blueprint says session is internal-only).

### `FileSystemProvider.FinaliseUploadAsync(session, ct)`

- **Steps:**
  1. `ct.ThrowIfCancellationRequested()`.
  2. Verify partial-file size equals `session.TotalBytes`; if not → `Result.Fail(UploadFailed, "Final size mismatch")`.
  3. Compute final destination: see **Sharded path layout** below.
  4. Create destination shard directories; fsync each newly-created directory via `AtomicWriteHelper.FsyncDirectory`.
  5. `File.Move(partialPath, destPath, overwrite: false)` — atomic rename. If destination already exists → `Result.Fail(UploadFailed, "Destination already exists")` (caller handles deduplication; provider does not).
  6. `AtomicWriteHelper.FsyncDirectory(destDirectory)`.
  7. Best-effort delete of the sidecar `.session` file (use `CancellationToken.None` per Principle 17 — bookkeeping must not be cancellable mid-flight).
  8. Return `Result<string>.Ok(remoteId)` where `remoteId` is the *relative path from `rootPath`* — e.g. `blobs/ab/cd/abcd...bin`. Stable, recoverable.
- **Errors:** as `UploadRangeAsync`.

### `FileSystemProvider.AbortUploadAsync(session, ct)`

- Best-effort delete of `.partial` and `.session` files. Idempotent. Errors swallowed and logged at `Warning` (the orchestrator already moved on).

### `FileSystemProvider.DownloadAsync(remoteId, ct)`

- `File.OpenRead({rootPath}/{remoteId})`. Wrap returned `FileStream` as `Stream` in `Result<Stream>.Ok(...)`. Caller disposes.
- Errors: `FileNotFoundException → BlobNotFound`; `IOException → ProviderUnreachable`.

### `FileSystemProvider.DeleteAsync(remoteId, ct)` / `ExistsAsync(remoteId, ct)`

- Direct `File.Delete` / `File.Exists` against `{rootPath}/{remoteId}`. `Delete` is idempotent (no-op on missing).

### `FileSystemProvider.ListAsync(prefix, ct)`

- `Directory.EnumerateFiles({rootPath}/{prefix}, "*", SearchOption.AllDirectories)`. Returns relative paths from `rootPath`. Used by §3.5 brain-mirror retention pass (`ListAsync("_brain", ct)`).

### `FileSystemProvider.CheckHealthAsync(ct)`

- Write a `_health/{Guid:N}.probe` file with 1-byte content; read it back; delete it. Latency captured via `Stopwatch`. Returns `ProviderHealth { Status = Healthy, RoundTripLatency = sw.Elapsed }` on success; `Status = Unreachable` on failure. (Phase 5's `HealthMonitorService` drives the cadence; this PR just exposes the method.)

### `FileSystemProvider.GetUsedBytesAsync(ct)` / `GetQuotaBytesAsync(ct)`

- `new DriveInfo(rootPath).TotalSize - DriveInfo.AvailableFreeSpace` for used; `new DriveInfo(rootPath).TotalSize` for quota. Errors → `ProviderUnreachable` / `null` quota.

### Sharded path layout

**Decision required at Gate 1.** The dev plan §3.1 says `{rootPath}/blobs/{xx}/{yy}/{remoteName}` (4-char shard, `/blobs/` prefix). Blueprint §27.1 says `{rootPath}/{first-2-of-remoteName}/{remoteName}.bin` (2-char shard, no prefix). See **Open questions** below. The plan's default — pending user choice — is the **dev plan layout** (`blobs/{xx}/{yy}/`) since (a) it matches the skink's own sharding for cognitive consistency, (b) `_brain/` and `_health/` already use leading-underscore prefixes that `/blobs/` complements, and (c) the dev plan was authored after the blueprint and is the more recent intent. If the user picks the blueprint layout, the only change is the `ComputeRemotePath` helper inside `FileSystemProvider`.

### `InMemoryProviderRegistry` methods

- `Register(id, provider)`: `_dict[id] = provider`; logs at `Information`.
- `Remove(id)`: `_dict.TryRemove(id, out _)`; returns the bool.
- `GetAsync(id, ct)`: `_dict.TryGetValue → Result.Ok` else `Result.Fail(ProviderUnreachable)`. `ValueTask` because no I/O.
- `ListActiveProviderIdsAsync(ct)`: snapshot `_dict.Keys.ToArray()`. `ValueTask`.

### Failure-path pattern (canonical)

Reference: `FileRepository.InsertAsync` at [src/FlashSkink.Core/Metadata/FileRepository.cs:101](src/FlashSkink.Core/Metadata/FileRepository.cs:101). Order in every public `FileSystemProvider` method:

1. `catch (OperationCanceledException ex) → Result.Fail(Cancelled, ..., ex)`. Log `Information`.
2. `catch (FileNotFoundException ex) → Result.Fail(BlobNotFound, ...)` *only on Download/Exists*. Otherwise no entry.
3. `catch (UnauthorizedAccessException ex) → Result.Fail(ProviderUnreachable, ...)`. Log `Error`.
4. `catch (IOException ex) → Result.Fail(UploadFailed | ProviderUnreachable, ...)` (code depends on operation). Log `Error`.
5. `catch (Exception ex) → Result.Fail(Unknown, ...)`. Log `Error`.

## Integration points

This PR consumes only existing types; it does not call any volume-level method.

- `FlashSkink.Core.Abstractions.Results.Result`, `Result<T>`, `ErrorContext`, `ErrorCode` — every `Result.Fail` call.
- `FlashSkink.Core.Abstractions.Models.ProviderHealth` (existing) — returned by `CheckHealthAsync`.
- `Microsoft.Extensions.Logging.ILogger<T>` — injected last constructor parameter.
- `System.IO.RandomAccess.FlushToDisk(SafeFileHandle)` — Phase 1 fsync discipline.
- `AtomicBlobWriter.ComputeStagingPath / ComputeDestinationPath` static helpers (existing) — *not* used directly (FileSystemProvider's path semantics differ — staging is under `.flashskink-staging/` in the **tail**'s rootPath, not the skink's), but the same code shape is mirrored.

This PR is **not** wired into `FlashSkinkVolume`, `WritePipeline`, or `UploadQueueRepository` — that's §3.4 / §3.6.

## Principles touched

- **Principle 1** — every public method on `FileSystemProvider`, `InMemoryProviderRegistry` returns `Result` or `Result<T>`. Static `Create` returns `Result<FileSystemProvider>`. `AlwaysOnlineNetworkMonitor.IsAvailable` is a sanctioned property (cannot fail).
- **Principle 8** — new types in `Core.Abstractions/Providers/` reference no UI/Presentation. New types in `Core/Providers/` reference only `Core.Abstractions` and `System.IO`/`Microsoft.Extensions.Logging.Abstractions`.
- **Principle 12** — OS-agnostic. Windows-vs-POSIX directory-fsync branching delegated to `AtomicWriteHelper` (already cross-platform-safe via existing `AtomicBlobWriter` test coverage).
- **Principle 13** — every async method has `CancellationToken ct` last.
- **Principle 14** — `OperationCanceledException` first catch in every method.
- **Principle 15** — narrow-to-broad catch ladder with specific `IOException` / `FileNotFoundException` / `UnauthorizedAccessException` before `Exception`.
- **Principle 16** — `FileStream` disposed via `using` in every path including failure.
- **Principle 17** — sidecar deletion in `FinaliseUploadAsync` uses literal `CancellationToken.None`.
- **Principle 23** — `IStorageProvider` and `UploadSession` are **introduced** by this PR. After merge they are frozen by Principle 23. The plan flags this explicitly so subsequent PRs (and reviewers) treat them as untouchable.
- **Principle 25** — `FileSystemProvider`'s few user-visible strings (logger messages can use appliance vocabulary, but `Result.Fail` messages may surface) avoid "session", "range", "blob" in user-message form. Logger messages use appliance vocabulary freely (Principle 27 governs).
- **Principle 26** — no secrets logged. `FileSystemProvider` has no credentials.
- **Principle 28** — `ILogger<T>` from `Microsoft.Extensions.Logging.Abstractions`.
- **Principle 29** — atomic-write discipline preserved via `AtomicWriteHelper` reuse.
- **Principle 32** — `FileSystemProvider` makes zero outbound network calls; `AlwaysOnlineNetworkMonitor` makes zero. Other than file I/O against the configured local path, nothing leaves the process.

## Test spec

All test files in `tests/FlashSkink.Tests/Providers/`. Naming: `MethodName_State_ExpectedBehavior` per `FileRepositoryTests`/`AtomicBlobWriterTests`. Temp-dir-per-test pattern from `AtomicBlobWriterTests` (constructor creates `Path.Combine(Path.GetTempPath(), "flashskink-tests", Guid.NewGuid().ToString("N"))`; `Dispose` recursive-deletes).

### `FileSystemProviderTests.cs`

- `Create_RootPathDoesNotExist_ReturnsProviderUnreachable`
- `Create_RootPathExistsAndWritable_ReturnsProvider`
- `BeginUploadAsync_NewRemote_CreatesStagingDirAndSidecar`
- `BeginUploadAsync_ReturnsSessionWithMaxValueExpiry`
- `GetUploadedBytesAsync_NoPartialFile_ReturnsZero`
- `GetUploadedBytesAsync_AfterRangeUpload_ReturnsRangeLength`
- `UploadRangeAsync_AppendsAtOffset_BytesPersistAcrossOpens`
- `UploadRangeAsync_OverlappingOffset_OverwritesPriorContent`
- `UploadRangeAsync_Cancelled_ReturnsCancelled`
- `FinaliseUploadAsync_Complete_AtomicallyMovesToShardedDestination`
- `FinaliseUploadAsync_SizeMismatch_ReturnsUploadFailed`
- `FinaliseUploadAsync_DestinationExists_ReturnsUploadFailed`
- `FinaliseUploadAsync_DeletesSidecarFile`
- `AbortUploadAsync_DeletesPartialAndSidecar_Idempotent`
- `DownloadAsync_ReturnsStreamOverFinalisedBlob`
- `DownloadAsync_MissingRemote_ReturnsBlobNotFound`
- `DeleteAsync_RemovesFile_Idempotent`
- `ExistsAsync_AfterFinalise_ReturnsTrue`
- `ListAsync_ReturnsRelativePathsBelowPrefix`
- `CheckHealthAsync_WritableRoot_ReturnsHealthyWithLatency`
- `CheckHealthAsync_ReadOnlyRoot_ReturnsUnreachable` *(skip on Windows if attribute can't be applied; document with `OperatingSystem.IsWindows` short-circuit)*
- `GetUsedBytesAsync_ReturnsNonNegative`
- `GetQuotaBytesAsync_ReturnsDriveSize`
- `RoundTrip_50RangesOf4MiB_FinalBlobMatchesSourceBytes` — the integration-style test; 5×16 MiB upload across 50 ranges; final file equals source bytes.

### `InMemoryProviderRegistryTests.cs`

- `Register_NewProvider_GetReturnsIt`
- `Register_OverwritesExistingId`
- `Remove_ReturnsTrueForKnown_FalseForUnknown`
- `GetAsync_UnknownId_ReturnsProviderUnreachable`
- `ListActiveProviderIdsAsync_ReflectsRegisterAndRemove`
- `ConcurrentRegisterRemove_NoTorn` — 100-task race; assert dictionary state remains coherent.

### `AlwaysOnlineNetworkMonitorTests.cs`

- `IsAvailable_AlwaysReturnsTrue`
- `Subscribing_NeverInvokesHandler_AfterArbitraryWait`

### `FaultInjectingStorageProviderTests.cs`

- `FailNextRange_NextRangeFails_ThirdSucceeds`
- `FailNextRangeWith_PropagatesSpecifiedErrorCode`
- `ForceSessionExpiryAfter_ReturnsUploadSessionExpiredOnNthRange`
- `SetRangeLatency_AppliesDelay` — uses `Stopwatch` rather than a clock abstraction (clock arrives in §3.2).
- `Reset_ClearsAllInjectedFaults`
- All tests construct over a `FileSystemProvider` rooted at the per-test temp dir.

### Existing tests

`AtomicBlobWriterTests` continues to pass unchanged after the `FsyncDirectory` extraction. No new asserts there.

## Acceptance criteria

- [ ] All listed files exist, build clean on `ubuntu-latest` and `windows-latest` with `--warnaserror`.
- [ ] `dotnet test` green: all Phase 0–2 tests still pass; all new Phase 3 §3.1 tests pass.
- [ ] `dotnet format --verify-no-changes` reports clean.
- [ ] `IStorageProvider`, `UploadSession`, `IProviderRegistry`, `INetworkAvailabilityMonitor` are public, documented, and live in `FlashSkink.Core.Abstractions.Providers`.
- [ ] `Core.Abstractions` references no UI/Presentation/Core projects (assembly-layering test in CI passes).
- [ ] `[InternalsVisibleTo("FlashSkink.Tests")]` present on Core.Abstractions if any internal types are introduced (currently `FileSystemProviderConfig` is `internal` in Core, not Core.Abstractions, so this attribute may not be strictly needed — the implementer verifies).
- [ ] `RoundTrip_50RangesOf4MiB_FinalBlobMatchesSourceBytes` integration test passes.
- [ ] `ErrorCode.cs` is **not** modified (cross-cutting decision 2).
- [ ] `AtomicBlobWriter.cs` modification preserves all existing test pass/fail signatures.

## Line-of-code budget

| File | Approx LOC |
|---|---|
| `Core.Abstractions/Providers/IStorageProvider.cs` | 120 (including XML) |
| `Core.Abstractions/Providers/UploadSession.cs` | 40 |
| `Core.Abstractions/Providers/IProviderRegistry.cs` | 30 |
| `Core.Abstractions/Providers/INetworkAvailabilityMonitor.cs` | 35 |
| `Core/Providers/InMemoryProviderRegistry.cs` | 90 |
| `Core/Providers/FileSystemProvider.cs` | 360 |
| `Core/Providers/FileSystemProviderConfig.cs` | 25 |
| `Core/Engine/AlwaysOnlineNetworkMonitor.cs` | 30 |
| `Core/Upload/UploadConstants.cs` | 30 |
| `Core/Storage/AtomicWriteHelper.cs` | 70 |
| `Core/Storage/AtomicBlobWriter.cs` (delta) | −20 / +5 |
| `Core.Abstractions/AssemblyInfo.cs` (if missing) | 5 |
| **src subtotal** | **~825** |
| `tests/Providers/FileSystemProviderTests.cs` | 280 |
| `tests/Providers/InMemoryProviderRegistryTests.cs` | 120 |
| `tests/Providers/AlwaysOnlineNetworkMonitorTests.cs` | 25 |
| `tests/Providers/FaultInjectingStorageProvider.cs` | 150 |
| `tests/Providers/FaultInjectingStorageProviderTests.cs` | 120 |
| **tests subtotal** | **~695** |
| **Total** | **~1520** |

This is a meaty PR — it introduces the entire provider seam and the first real provider in one PR per the dev plan's deliberate scoping. If the user prefers to split, a natural seam is: `pr/3.1a-provider-contracts` (interfaces + UploadSession + UploadConstants + InMemoryProviderRegistry + AlwaysOnlineNetworkMonitor + AtomicWriteHelper extraction; ~520 LOC src) followed by `pr/3.1b-filesystem-provider` (FileSystemProvider + FaultInjectingStorageProvider + tests; ~880 LOC src+tests). Flagged in **Open questions**.

## Non-goals

- **No `IProviderSetup`** — Phase 4 OAuth flow. `FileSystemProvider`'s setup is just a path; its construction takes the path directly.
- **No `IProviderSetup.ValidatePathAsync`** — Phase 4. `FileSystemProvider.Create` validates inline.
- **No `ISupportsRemoteHashCheck` / `GetRemoteXxHash64Async`** — §3.3 introduces the capability interface and adds the implementation to `FileSystemProvider`. This PR ships `FileSystemProvider` *without* hash-check capability; §3.3's PR will add `: ISupportsRemoteHashCheck` and the method body. Stated explicitly because dev plan §3.1 has an ambiguous sentence about it.
- **No `BrainBackedProviderRegistry`** — Phase 4. Phase 3 ships only `InMemoryProviderRegistry`.
- **No `RegisterTailAsync`** on `FlashSkinkVolume` — §3.6 wires that.
- **No real `NetworkAvailabilityMonitor`** — Phase 5.
- **No `IClock` / `SystemClock` / `FakeClock`** — §3.2. `FaultInjectingStorageProvider`'s latency injection uses `Task.Delay` directly in this PR; §3.3 will swap to `IClock` once it lands.
- **No upload orchestration, no retry policy, no range uploader, no brain mirror, no volume integration** — §3.2–§3.6.
- **No `RangeUploader.UploadAsync`-style consumer of these abstractions** — only the contracts and one provider land here.
- **No new `ErrorCode` values** — cross-cutting decision 2.
- **No modifications to `WritePipeline`, `FlashSkinkVolume`, brain schema, or `UploadQueueRepository`.**

## Open questions for Gate 1

These three need a steer before I lock the plan or proceed to implementation:

### 1. Sharded path layout for FileSystemProvider's final destination

Dev plan §3.1 says `{rootPath}/blobs/{xx}/{yy}/{remoteName}` (4-char shard, `/blobs/` prefix). Blueprint §27.1 says `{rootPath}/{first-2-of-remoteName}/{remoteName}.bin` (2-char shard, no prefix). Per `CLAUDE.md` "Blueprint wins"; per "blueprint sections to read" the dev plan cites §15.2 but not §27 for this detail. **My recommendation:** follow the **dev plan layout** because (a) it matches the skink's `.flashskink/blobs/{xx}/{yy}/` shape so cognitive load stays low, (b) `_brain/` and `_health/` namespacing is cleaner with a sibling `blobs/` rather than blob shards mixed into the root, and (c) the dev plan post-dates the blueprint and is the more recent intent. **But I want explicit user approval given the principle says blueprint wins.**

### 2. `UploadSession.FileID` and `UploadSession.ProviderID` — `required` or not?

Blueprint §10.2 quotes both as `required`. But `IStorageProvider.BeginUploadAsync(remoteName, totalBytes, ct)` cannot supply them. Three options:

- **A. Drop `required` from both** (default `""`); caller stamps via `with`-expression. (Plan default.)
- **B. Add `string fileId, string providerId` parameters to `BeginUploadAsync`.** Clean for the consumer; deviates further from blueprint signature.
- **C. Define an upstream `ProviderUploadSession` (record without FileID/ProviderID) returned by IStorageProvider, with `UploadSession` (with FileID/ProviderID) as a Core-side wrapper.** Most correct, most boilerplate.

**My recommendation:** A. Smallest deviation, easiest to revert if §3.3 implementation reveals a problem.

### 3. PR size / split

~825 src + ~695 test LOC is substantial. Split into 3.1a (contracts + registry + monitor + helper) and 3.1b (FileSystemProvider + fault injector)? Plan default is **single PR** per the dev plan's stated scope.

**My recommendation:** ship as one PR. The cohesion is high, the file count is reasonable, and §3.2 starts cleanly only after the contracts are frozen. Splitting would force §3.1a's tests to be near-trivial (no real provider to test against) and would add a merge cycle.

---

*Plan ready for review. Stop at Gate 1 per `CLAUDE.md` step 3.*
