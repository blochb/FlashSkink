# Phase 3 — Upload queue and resumable uploads

**Status marker:** This phase follows the standard session protocol defined in `CLAUDE.md`. Each section below (§3.1 through §3.6) maps to one PR, executed via `read section 3.X of the dev plan and perform`. Gate 1 (plan approval) and Gate 2 (implementation approval) are required for every section. Sections must be executed in order — each one depends on the types established by the section before it.

**Terminology note:** "Phase 2 upload" in this document refers to the **tail-upload phase** defined by the blueprint (§5.2, §15) — the asynchronous, resumable, best-effort replication of skink-resident blobs to each configured tail. It is not the same as the project's *Phase 2*. Phase 1 commit (the synchronous skink write) lives in dev-plan Phase 2; Phase 3 is the first phase where bytes leave the skink.

---

## Goal

After Phase 3:

- An `IStorageProvider`-shaped tail can be registered with a volume (via an internal seam that Phase 4 will replace with the public `AddTailAsync` OAuth flow), and a configured tail receives every committed file as an encrypted blob — resumably, idempotently, and across disconnect/host-change. `FileSystemProvider` ships in this phase as the first real provider implementation, suitable for NAS/external-drive tails and used as the deterministic test double for the cloud providers that arrive in Phase 4.
- The full §15.3 session lifecycle is implemented: `BeginUploadAsync` → loop of `UploadRangeAsync` calls in 4 MB chunks → `FinaliseUploadAsync` → post-finalisation verification → brain transaction marking the row UPLOADED and deleting the `UploadSessions` row.
- An `UploadQueueService` orchestrator runs one long-lived worker per active tail, woken by a wakeup signal from `WritePipeline` (committed file) and by a 60 s timer.
- A `RetryPolicy` implements §21.1 exactly: 3 in-range retries with 1 s / 4 s / 16 s backoff, then escalation to 5 min / 30 min / 2 h / 12 h between cycles, then `FAILED` after 5 cycles.
- An encrypted brain mirror (`_brain/{timestamp}.bin`) is uploaded to every active healthy tail after each write commit, on a 15-minute timer, and on volume close. Three rolling mirrors are retained per tail (§16.7).
- `FlashSkinkVolume.WriteBulkAsync` exists and returns a `BulkWriteReceipt` enumerating per-item success/failure (deferred from Phase 2's §11 divergence list).
- Phase 4 (cloud provider implementations and OAuth setup) can start with a single prompt, having a frozen `IStorageProvider` consumer surface to slot real cloud adapters into.

---

## Cross-cutting decisions

Five decisions span multiple PRs in this phase. Recording them here keeps the rule in one place; each section below references back.

**1. Provider seam — `IProviderRegistry`, not `IEnumerable<IStorageProvider>`.** The upload orchestrator does not see a static list of providers at construction time; it asks an `IProviderRegistry` for the live `IStorageProvider` instance corresponding to a given `Providers.ProviderID`. Phase 3 ships an `InMemoryProviderRegistry` (in `Core/Providers/`) that holds an in-process dictionary populated by `RegisterTailAsync` and tests. Phase 4 ships an alternative `BrainBackedProviderRegistry` for cloud providers, which reads `Providers` rows, decrypts the BYOC client secret + OAuth refresh token with the DEK, and constructs the appropriate cloud `IStorageProvider` adapter. This seam is what lets Phase 3 land before Phase 4 without inventing throwaway code: the registry contract is what Phase 4 implements.

**2. ErrorCode reuse in this phase — no new values.** Every code Phase 3 needs already exists in §1.1: `UploadFailed`, `UploadSessionExpired`, `ProviderUnreachable`, `ProviderAuthFailed`, `ProviderQuotaExceeded`, `ProviderRateLimited`, `ProviderApiChanged`, `TokenExpired`, `TokenRefreshFailed`, `TokenRevoked`, `BlobCorrupt`, `ChecksumMismatch`, `BlobNotFound`, `Cancelled`, `Timeout`, `Unknown`. The §1.1 "declare the full enum up-front" posture continues to hold: **no new `ErrorCode` values are added in §3.1 through §3.6.** `ErrorCode.cs` is not modified by any Phase 3 PR.

**3. Range size is a constant, not a configuration.** `UploadConstants.RangeSize = 4 * 1024 * 1024` per blueprint §15.5 (Decision B3-b). Adaptive sizing is post-V1. The constant lives in `Core/Upload/UploadConstants.cs` so the orchestrator, the `RangeUploader`, and the test double all reference one source of truth.

**4. Concurrency model — one worker per active tail.** §15.6 caps "in-flight ranges per tail" at 2 and total parallelism at `2 × tailCount`. Phase 3's V1 implementation simplifies to **one worker per tail with sequential range uploads** (`MaxRangesInFlightPerTail = 1`), exposed as a constant for V2+ to revisit. Justification: a single sequential range loop already pipelines upload bandwidth fully on consumer connections (4 MB at 1 MB/s = 4 s — the network is the bottleneck, not the loop overhead); per-tail concurrency only helps on multi-Mbps connections where the per-range overhead dominates, and that's a profiling-driven optimisation. Cross-tail parallelism (different tails uploading at the same time) **is** delivered in Phase 3 — the orchestrator runs one worker task per active tail. This decision is recorded so the constant is visible and Gate 2 doesn't reject the simpler implementation as "below blueprint."

**5. Network-availability gating is deferred to Phase 5.** Blueprint §22.4 says `UploadQueueService` retry scheduling is paused when `NetworkAvailabilityMonitor.CurrentState == Offline`. The monitor itself, the OS-mediated network signal, and the pause/resume integration land in Phase 5 alongside `HealthMonitorService`. Phase 3 ships an `INetworkAvailabilityMonitor` interface (in `Core.Abstractions`) with a `Online`-always default implementation, and the `UploadQueueService` consults it on every retry-schedule decision. Wiring the real OS monitor is a single-PR swap in Phase 5 with zero changes to the queue service. This avoids re-shaping the retry loop later and keeps the §21.1 backoff math testable today.

---

## Section index

| Section | Title | Deliverables |
|---|---|---|
| §3.1 | Provider seam, FileSystem provider, test infrastructure | `IProviderRegistry`, `InMemoryProviderRegistry`, `INetworkAvailabilityMonitor`, `AlwaysOnlineNetworkMonitor`, `FileSystemProvider` (src/), `FaultInjectingStorageProvider` (tests/), `UploadConstants`; tests |
| §3.2 | Retry policy and backoff | `RetryPolicy`, `RetryDecision`, `IClock` + `SystemClock` + `FakeClock` (tests/); per-range and per-cycle scheduling tests |
| §3.3 | Range uploader | `RangeUploader` implementing the §15.3 single-blob session lifecycle: open session → loop ranges → finalise → verify; expired-session restart; tests |
| §3.4 | Upload queue service | `UploadQueueService` (orchestrator + per-tail worker), wakeup signal, `MarkUploaded`/`MarkFailed`/`MarkUploading` brain transactions; tests |
| §3.5 | Brain mirror service | `BrainMirrorService` (§16.7): `BACKUP TO` snapshot → AES-256-GCM encrypt with DEK → upload as `_brain/{timestamp}.bin` → prune to 3 rolling per tail; after-write trigger + 15-min timer + clean-shutdown trigger; tests |
| §3.6 | Volume integration | `FlashSkinkVolume.WriteBulkAsync`, internal `RegisterTailAsync` admin method (Phase 4 replaces with public `AddTailAsync`), `WritePipeline` → `UploadQueueService` wakeup wiring, lifecycle (start/stop on `OpenAsync`/`DisposeAsync`); tests |

Full implementation detail for each section lives in `.claude/plans/pr-3.X.md`, written at Gate 1 of the corresponding session. The notes below summarise the blueprint sections each PR must read and the NuGet packages it introduces.

---

## Section notes

### §3.1 — Provider seam, FileSystem provider, test infrastructure

**Blueprint sections to read:** §10 (all subsections), §15.2 (FileSystem row of the protocol mapping table), §15.7 (FileSystem verification — re-read and XXHash64 compare), §22.2 (network monitor problem statement), §27 (provider taxonomy — what's in V1 and what's not).

**Scope summary:** Two interfaces in `FlashSkink.Core.Abstractions/Providers/`, one default network monitor in `FlashSkink.Core/Engine/`, one in-process registry and the `FileSystemProvider` adapter in `FlashSkink.Core/Providers/`, one constants file in `FlashSkink.Core/Upload/`, and a test-only fault-injection decorator in `tests/FlashSkink.Tests/Providers/`.

**Contracts (in `FlashSkink.Core.Abstractions.Providers`):**
- `IProviderRegistry` — `ValueTask<Result<IStorageProvider>> GetAsync(string providerId, CancellationToken ct)` and `ValueTask<Result<IReadOnlyList<string>>> ListActiveProviderIdsAsync(CancellationToken ct)`. The orchestrator queries the registry at the top of each scheduling tick and on every wakeup.
- `INetworkAvailabilityMonitor` — `bool IsAvailable { get; }`, `event EventHandler<bool>? AvailabilityChanged`. Synchronous because callers are background loops that snapshot it; the event is for transition-driven wakeups.

**Implementations:**
- `InMemoryProviderRegistry` (`Core/Providers/`, `public sealed`) — concurrent dictionary keyed by `ProviderID`. `Register(string, IStorageProvider)` + `Remove(string)` are the runtime surface used by `RegisterTailAsync` (§3.6) and tests. Phase 4 ships an alternative `BrainBackedProviderRegistry` for cloud providers that reads `Providers` rows and constructs adapters from decrypted refresh tokens; the interface is the only contract the orchestrator depends on.
- `AlwaysOnlineNetworkMonitor` (`Core/Engine/`, `public sealed`) — `IsAvailable` returns `true` always; `AvailabilityChanged` is never raised. Replaced in Phase 5.
- **`FileSystemProvider`** (`Core/Providers/`, `public sealed`) — real production `IStorageProvider` whose "remote" is a configured local-or-network filesystem path (the use case is a NAS mount, an external drive, or a local folder for users who want a redundant copy without a cloud account). Per §15.2 there is no native session protocol; the adapter implements one over atomic file operations:
    - `BeginUploadAsync(remoteName, totalBytes)` — creates `{rootPath}/.flashskink-staging/{remoteName}.session` (a small JSON file: `{"totalBytes": ..., "createdUtc": ...}`) and returns an `UploadSession` whose `SessionUri` is the staging filename and `ExpiresAt = DateTimeOffset.MaxValue` (FileSystem sessions never expire — §15.2 "Indefinite").
    - `GetUploadedBytesAsync(session)` — `FileInfo({rootPath}/.flashskink-staging/{remoteName}.partial).Length` (or 0 if absent).
    - `UploadRangeAsync(session, offset, data)` — opens the `.partial` file with `FileMode.OpenOrCreate, FileAccess.Write`, seeks to `offset`, writes, `FlushAsync(ct)` then `RandomAccess.FlushToDisk(handle)` (Principle 29 — the same fsync discipline as the skink's `AtomicBlobWriter`).
    - `FinaliseUploadAsync(session)` — atomically renames `.partial` to the final sharded path `{rootPath}/blobs/{xx}/{yy}/{remoteName}` (sharded the same way the skink shards), `fsync` the destination directory, deletes the `.session` file. Returns the relative path as `RemoteId`.
    - `AbortUploadAsync(session)` — deletes the `.partial` and `.session` files (best-effort, idempotent).
    - `DownloadAsync(remoteId)` — `File.OpenRead` at the sharded path; returned `Stream` is the caller's to dispose.
    - `DeleteAsync` / `ExistsAsync` — direct filesystem operations on the sharded path.
    - `CheckHealthAsync` — writes a `_health/{Guid}.probe` file, reads it, deletes it; latency captured. Returns `Healthy` / `Unreachable` (Phase 5's monitor service drives the cadence; Phase 3 just exposes the method).
    - `GetUsedBytesAsync` / `GetQuotaBytesAsync` — `DriveInfo` for the configured root.
    - **Hash-check capability**: implements the `ISupportsRemoteHashCheck` interface introduced in §3.3 — `GetRemoteXxHash64Async(remoteId, ct)` re-reads the file and computes XXHash64 (§15.7 FileSystem row).
- `FaultInjectingStorageProvider` (`tests/FlashSkink.Tests/Providers/`, `internal sealed`) — decorator that wraps any `IStorageProvider` and adds: deterministic per-call failures (`FailNextRange()`, `FailNextRangeWith(ErrorCode)`), session expiry injection (`ForceSessionExpiryAfter(int rangesUploaded)`), latency injection (`SetRangeLatency(TimeSpan)`). Tests construct one over a `FileSystemProvider` rooted at a per-test temp dir.

**`UploadConstants`** (in `FlashSkink.Core/Upload/`):
- `RangeSize = 4 * 1024 * 1024` (cross-cutting decision 3)
- `MaxRangesInFlightPerTail = 1` (cross-cutting decision 4)
- `WorkerIdlePollSeconds = 60` (§15.8)
- `OrchestratorIdlePollSeconds = 30` (§15.8)
- `BrainMirrorIntervalMinutes = 15` (§16.7)
- `BrainMirrorRollingCount = 3` (§16.7)

**NuGet:** None new (`Microsoft.IO.RecyclableMemoryStream` from §2.2 is reused for any FileSystemProvider buffering; `System.IO.Hashing` from §2.5 is reused for the hash-check capability).

**Key constraints:**
- `FileSystemProvider` honours the same atomic-write discipline as `AtomicBlobWriter` (Principle 29): write to `.partial`, `fsync`, atomic rename, `fsync` destination directory. The shared sequence is encapsulated in a small helper inside `Core/Providers/` to avoid duplicating the platform-specific directory-fsync logic from §2.4 — the helper wraps both call sites.
- FileSystemProvider's "session" is a metadata sidecar, not a server-side handle: the `.session` file's only purpose is letting `GetUploadedBytesAsync` survive process restart. The brain's `UploadSessions` row remains the source of truth; the sidecar is a redundant on-the-tail cache that lets the provider answer the "how many bytes do you have?" question without trusting the brain. If the sidecar disappears (manual cleanup, partial drive failure), `GetUploadedBytesAsync` returns 0 and the upload restarts — same as a cloud provider returning expired-session.
- The configured root path is provided via `Providers.ProviderConfig` (JSON, e.g. `{"rootPath":"/mnt/nas/myskink-tail"}` per §16.2). `FileSystemProvider` validates at construction that the path exists and is writable; failure to validate returns `Result.Fail(ProviderUnreachable)` from the construction factory. Phase 4 wires this validation into the `IProviderSetup.ValidatePathAsync` flow.
- The in-memory registry holds *strong* references to `IStorageProvider` instances; tests are responsible for disposing/replacing them. Phase 4's `BrainBackedProviderRegistry` for cloud providers will own a tighter lifecycle.
- Principle 23 (provider contract is frozen) constrains this PR: the `IStorageProvider` and `UploadSession` shapes from §1.1 / §10 are *not* edited here. The new `ISupportsRemoteHashCheck` is an *additive* capability interface (Principle 23 sanctions additive evolution), introduced in §3.3 and consumed by the verification step.
- `INetworkAvailabilityMonitor` has no `CheckNowAsync()` method in V1 — the OS signal is passive (§22.2) and synchronous; Phase 5's real implementation reads `NetworkInterface.GetIsNetworkAvailable()` once at construction and then subscribes to `NetworkChange.NetworkAvailabilityChanged` for further updates.

---

### §3.2 — Retry policy and backoff

**Blueprint sections to read:** §21.1 (full), §15.3 (step 6 failure handling), §22.1 (health affecting backoff).

**Scope summary:** One policy type, one decision record, and a clock abstraction in `FlashSkink.Core/Upload/`, plus a test-only `FakeClock` in `tests/FlashSkink.Tests/Upload/`.

`IClock` + `SystemClock` (`Core.Abstractions/Time/`) — `DateTime UtcNow { get; }`, `ValueTask Delay(TimeSpan, CancellationToken)`. The retry loop, the orchestrator's idle wait, and the brain-mirror timer all consume `IClock` so tests can advance time deterministically. `FakeClock` (test-only) exposes `Advance(TimeSpan)` and tracks pending delay tasks.

`RetryPolicy` (sealed) — pure logic; no I/O, no async:
- `RetryDecision NextRangeAttempt(int rangeAttemptNumber)` — returns `Retry(after: 1s/4s/16s)` for attempts 1/2/3, `EscalateCycle` for attempt 4.
- `RetryDecision NextCycleAttempt(int cycleNumber)` — returns `Retry(after: 5min/30min/2h/12h)` for cycles 1/2/3/4, `MarkFailed` for cycle 5.
- `RetryDecision` is a `readonly record struct` with `Outcome` (`Retry | EscalateCycle | MarkFailed`) and a `TimeSpan Delay`.

The policy itself never calls `IClock.Delay` — that's the caller's job. This keeps the policy a pure function (sanctioned-pure-function exception in Principle 1: a `RetryPolicy.Next*` method that takes an `int` and returns a value type cannot fail; XML doc comment states "Never throws").

**NuGet:** None new.

**Key constraints:**
- The §21.1 numbers — 1/4/16 s, 5/30/120/720 min, 5-cycle cap — are encoded as `static readonly TimeSpan[]` arrays inside `RetryPolicy`. Tests reference them by index, not by hard-coded literals — the policy is the single source.
- Health-status modulation (§22.1: "longer backoff when Degraded") is a Phase 5 concern. Phase 3's `RetryPolicy` is health-blind. The orchestrator queries health (Phase 5 service) and chooses *whether* to enter the loop; `RetryPolicy` only encodes the §21.1 progression once a loop is entered.
- `IClock.Delay` is the testable equivalent of `Task.Delay`. `FakeClock.Advance(TimeSpan)` completes any pending delay whose deadline is `<= UtcNow + advance`. The orchestrator and brain-mirror timer both go through `IClock.Delay` — tests don't sleep.

---

### §3.3 — Range uploader

**Blueprint sections to read:** §15 (entire section), §13.6 (blob-on-disk format — the uploader reads these bytes verbatim), §13.7 (XXHash64 verification on tails — see §15.7), §10.1 (`IStorageProvider` interface), §9.5 (4 MB pooled buffer pattern).

**Scope summary:** One service in `FlashSkink.Core/Upload/`.

`RangeUploader` (sealed) — owns the §15.3 single-blob upload state machine:

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

Returns `Result<UploadOutcome>` where `UploadOutcome` is a sealed record with `RemoteId`, `BytesUploaded`, and `Status` (`Completed | RetryableFailure(ErrorCode) | PermanentFailure(ErrorCode)`).

Stage flow per §15.3:

1. **Resume or open session.** If `existingSession` is non-null and not expired: call `provider.GetUploadedBytesAsync` to reconcile the offset, fall through to step 4. If null or expired (per `LastActivityUtc + provider TTL` — but the canonical signal is provider returning `UploadSessionExpired`): call `provider.BeginUploadAsync(remoteName, blob.EncryptedSize, ct)` and `UploadQueueRepository.GetOrCreateSessionAsync` to persist the returned session.
2. **Open the blob file** via `File.OpenRead` at the sharded path. Use `FileShare.Read` (Phase 5 self-healing may also need it).
3. **Range loop.** Rent one `IMemoryOwner<byte>` of `UploadConstants.RangeSize` from `MemoryPool<byte>.Shared` (Principle 19; reused across all ranges of this blob). For each offset in `[BytesUploaded, blob.EncryptedSize)`:
   - `Seek` and `ReadExactly` into the buffer.
   - `await provider.UploadRangeAsync(session, offset, buffer.Memory[..bytesRead], ct)`.
   - On success: `UpdateSessionProgressAsync(BytesUploaded += bytesRead)`.
   - On `UploadSessionExpired`: best-effort `AbortUploadAsync`, delete the `UploadSessions` row, restart from step 1 (which will open a fresh session).
4. **Finalise.** `await provider.FinaliseUploadAsync(session, ct)` → `RemoteId`.
5. **Verify.** Per §15.7: the only hash we control across all providers is `Blobs.EncryptedXXHash`. `RangeUploader` calls `provider.GetRemoteXxHash64Async(remoteId, ct)` if the provider implements the capability interface `ISupportsRemoteHashCheck` (introduced in this section's `Core.Abstractions.Providers/` namespace, additive per Principle 23). On match, success. On mismatch, return `Result.Fail(ChecksumMismatch)` and the caller (`UploadQueueService`) marks the row `FAILED` and publishes a Critical notification. Providers that do not implement the capability (e.g. providers whose native hash is MD5/content-hash, where comparing to XXHash64 requires per-provider logic) will be added in Phase 4 with their own `Verify*` paths; for those, the V1 fallback is "trust the upload" — the GCM tag inside the blob already guards against ciphertext tampering. `FileSystemProvider` implements the capability today (§3.1).
6. **Return** `UploadOutcome.Completed(remoteId)`. The caller (`UploadQueueService`) is responsible for the brain transaction that flips `TailUploads.Status` to `UPLOADED` and deletes the `UploadSessions` row — keeping the brain mutation outside this type lets a single transaction cover the status flip + session delete (§15.3 step 7c).

**NuGet:** None new.

**Key constraints:**
- The 4 MB buffer is rented **once per blob**, not per range. Reusing the same buffer across the loop avoids the 4 MB / range × thousand-range-blob churn the §9.5 pattern guards against.
- The buffer holds **ciphertext** (the blob bytes are already encrypted on disk per Phase 2) — content-clearing on dispose is **not** required because ciphertext is not a secret. `MemoryPool<byte>.Shared` (no `ClearOnDispose` wrapper from §2.5) is the right pool here. Document this in an XML comment so the next reader doesn't pattern-match on §2.5 and "fix" it.
- Cancellation observed before every `UploadRangeAsync` (`ct.ThrowIfCancellationRequested()`); mid-range cancellation is allowed (the in-flight HTTP request fails naturally and the next attempt resumes from the same offset because `BytesUploaded` was not advanced).
- The session row is **never** advanced past what the provider has confirmed. Order: `provider.UploadRangeAsync` returns success → `UpdateSessionProgressAsync`. If the process crashes between those two, the next resume queries `provider.GetUploadedBytesAsync` and reconciles down to the provider-reported value.
- The "remote name" passed to `BeginUploadAsync` is `BlobID + ".bin"` — pre-shard format. Tail-side sharding is provider-specific and lives in the adapter (Phase 4); the blueprint's `[USB]/.flashskink/blobs/{xx}/{yy}/...` shard layout is skink-specific and not echoed on tails.
- Logging: every range attempt logs at `Trace`; every range failure at `Warning`; every blob-level outcome (Completed / RetryableFailure / PermanentFailure) at `Information` with the per-blob duration and total bytes uploaded.
- Notification: `RangeUploader` does **not** publish notifications. The caller (`UploadQueueService`) decides — a single-range failure inside an active retry cycle is not worth a user notification; only the `RetryPolicy.MarkFailed` outcome is.

---

### §3.4 — Upload queue service

**Blueprint sections to read:** §15.8 (orchestrator + worker pseudocode), §21.1 (retry escalation), §22.4 (offline gating; consume `INetworkAvailabilityMonitor`), §16.6 (`UploadQueueRepository` methods), §8 (notification severity for `MarkFailed`).

**Scope summary:** One orchestrator and one worker, plus a wakeup primitive, in `FlashSkink.Core/Upload/`.

`UploadQueueService` (sealed, `IAsyncDisposable`) — owns the orchestrator loop and the per-tail worker tasks.

`UploadWakeupSignal` (sealed) — a `Channel<Unit>` of capacity 1 with `BoundedChannelFullMode.DropWrite`. `Pulse()` writes a single token (no-op if one is already buffered); `WaitAsync(ct)` reads. The orchestrator publishes a pulse from the post-commit hook in `WritePipeline` (wired in §3.6). DropWrite (not DropOldest) is intentional: a backed-up signal is the same as a pending signal — no benefit to coalescing more.

Orchestrator loop (§15.8):
```
StartAsync(VolumeContext): kick off OrchestratorAsync as a background Task,
                            store CTS for shutdown.

OrchestratorAsync(ct):
  while !ct.IsCancellationRequested:
    if !networkMonitor.IsAvailable:
      await Delay(OrchestratorIdlePollSeconds, ct); continue
    activeIds = await registry.ListActiveProviderIdsAsync(ct)
    foreach providerId in activeIds:
      EnsureWorkerRunning(providerId)
    foreach providerId in (runningWorkers \ activeIds):
      await StopWorkerAsync(providerId)
    await Task.WhenAny(wakeup.WaitAsync(ct),
                       Delay(OrchestratorIdlePollSeconds, ct))
```

Per-tail worker:
```
WorkerAsync(providerId, ct):
  while !ct.IsCancellationRequested:
    if !networkMonitor.IsAvailable:
      await Delay(WorkerIdlePollSeconds, ct); continue
    await foreach row in uploadQueueRepository
                          .DequeueNextBatchAsync(providerId, batchSize: 1, ct):
      result = await ProcessOneAsync(row, ct)
      ApplyOutcome(result)
    if no row was processed:
      await Task.WhenAny(wakeup.WaitAsync(ct),
                         Delay(WorkerIdlePollSeconds, ct))

ProcessOneAsync(row, ct):
  providerResult = await registry.GetAsync(row.ProviderId, ct)
  blob = await blobRepository.GetByIdAsync(file.BlobId, ct)
  await uploadQueueRepository.MarkUploadingAsync(row.FileId, row.ProviderId, ct)
  session = await uploadQueueRepository.LookupSessionAsync(row.FileId, row.ProviderId, ct)
  outcome = await rangeUploader.UploadAsync(...)
  return outcome
```

`ApplyOutcome` cases (single brain transaction per case):
- `Completed`: `MarkUploadedAsync` + `DeleteSessionAsync` + `ActivityLog (Category=UPLOADED)` — all in one transaction. No notification (success is routine, Principle 24 covers failures).
- `RetryableFailure`: drive the `RetryPolicy.NextRangeAttempt` / `NextCycleAttempt` ladder. Per-range retries happen *inside* `RangeUploader.UploadAsync` already (delay via `IClock.Delay`); cycle retries are the worker's responsibility — `MarkFailedAsync` with the pending error code, schedule a `Task.Delay` of the cycle backoff (consuming `IClock`), then loop. The per-row `AttemptCount` column on `TailUploads` is the cycle counter the worker reads on resume.
- `PermanentFailure(MarkFailed)`: `MarkFailedAsync` with the final error code, `INotificationBus.PublishAsync(Severity=Error, Source="UploadQueueService", Title="Could not back up file", ErrorContext=...)`, `ActivityLog (Category=UPLOAD_FAILED)`. Per Principle 24 the persistence handler captures it.

**NuGet:** None new.

**Key constraints:**
- The orchestrator and each worker have their own `CancellationTokenSource` linked to the volume's session token; `DisposeAsync` cancels and `await`s all of them in sequence (orchestrator first, then workers — orchestrator may be holding worker references). Eventual `Task.WhenAll` with a short timeout (10 s) before logging-and-abandoning, so a misbehaving provider can't hang volume close indefinitely.
- The wakeup signal is single-instance: one `UploadWakeupSignal` per volume, shared between the orchestrator and all workers. Worker wakeups happen via `Pulse()` after every commit; the orchestrator-level pulse is what wakes a worker that's currently idle on `WaitAsync`.
- "What did `WritePipeline` actually queue?" — the wakeup is a kick, not a payload. After waking, workers re-query `DequeueNextBatchAsync` for whatever's PENDING. Spurious wakeups (channel pulsed when nothing changed) are harmless; missed wakeups are why the 60-second timer exists.
- Per-row `MarkUploadingAsync` happens **before** `RangeUploader.UploadAsync`. If the process crashes in the upload, the row is `UPLOADING` on resume. The DequeueNextBatchAsync filter (`Status IN ('PENDING','FAILED')` per §1.6) does **not** include `UPLOADING` — so the next session won't pick it up. Phase 5 WAL recovery rescues it: a startup sweep transitions any orphaned `UPLOADING` row back to `PENDING` before the queue starts. This PR documents the dependency in `## What Phase 3 does NOT do` so Phase 5 wires the fix-up.
- `UploadQueueRepository.LookupSessionAsync` is **not** in §1.6's surface — Phase 1 declared `GetOrCreateSessionAsync` (which writes), `UpdateSessionProgressAsync`, `DeleteSessionAsync` only. This PR adds `LookupSessionAsync(string fileId, string providerId, CancellationToken ct) → Result<UploadSessionRow?>` (read-only, Dapper). The §1.6 surface was additive-only by design (Principle 23 applies to the **provider contract**, not internal repositories) and `LookupSessionAsync` is the natural addition for the worker's resume path.
- Per-tail worker isolation: a failure on tail A never blocks tail B. Each worker catches all exceptions inside its outer loop, logs at `Error`, and continues; only `OperationCanceledException` exits the loop (Principle 14).
- Notifications use **user vocabulary** (Principle 25): "Could not back up file `{virtualPath}` to `{tailDisplayName}`". No mention of "tail upload session", "range", "WAL", or `ErrorCode` strings in `Title`/`Message` (the `ErrorContext` carries the code for handlers that want it).

---

### §3.5 — Brain mirror service

**Blueprint sections to read:** §16.7 (full), §18.5 (DEK-derived brain key — note: brain *file* encryption uses HKDF(DEK,"brain"); the *mirror* is encrypted directly with the DEK, fresh nonce — the rationale is that the mirror is consumed by recovery, which has the DEK from the mnemonic and does not have the SQLCipher brain key context), §21.4 (recovery — the mirror is what step 2/3 reads), §15 (uploads via the same `IStorageProvider`).

**Scope summary:** One service in `FlashSkink.Core/Engine/`, plus a snapshot helper.

`BrainMirrorService` (sealed, `IAsyncDisposable`) — coordinates the §16.7 mirror flow:

1. **Trigger sources** (any of):
   - "After write" — subscribed to a `WritePipelineEvents.Committed` event the orchestrator already publishes for the upload-queue wakeup. Mirror **not** triggered per-write directly; instead a debounce: collect commits over a 10-second window, then mirror once. (§16.7 says "after every write commit" — debouncing is a faithful reading: the user-visible promise is "shortly after every commit there is a fresh mirror", not "one mirror per commit" which would saturate small-file workloads.)
   - 15-minute timer (`IClock.Delay`).
   - Clean shutdown — `DisposeAsync` runs one final mirror synchronously before tearing down.

2. **Snapshot**: `SQLiteConnection.BackupDatabase(target)` to `.flashskink/staging/brain-mirror-{timestamp}.db`. SQLite's online backup API produces a consistent snapshot without locking the live DB.

3. **Encrypt**: read the snapshot file into a single pooled `IMemoryOwner<byte>` (capped at the brain size, which for a typical V1 volume is < 50 MB; for outliers Phase 3 falls back to streaming — see constraint below). AES-256-GCM with the DEK (the volume already holds it in `VolumeContext`), fresh 12-byte `stackalloc` nonce, AAD = `"FSBM" || timestampUtc.ToBinary()` (16 bytes — fixed-size, `stackalloc`-friendly, version-prefixed for future format changes).

4. **Upload** to every active healthy tail. Reuse `IStorageProvider.BeginUploadAsync` / `UploadRangeAsync` / `FinaliseUploadAsync` — the mirror is a normal blob from the provider's perspective. Remote name: `_brain/{timestampUtc:yyyyMMddTHHmmssZ}.bin`. The leading underscore distinguishes brain mirrors from data blobs in the provider namespace (Phase 4 adapters will list the `_brain/` prefix when looking up existing mirrors during recovery).

5. **Prune** to 3 rolling per tail: list `_brain/` on the tail, sort by name (which sorts chronologically thanks to ISO-8601 lex order), delete all but the 3 most recent. Deletion uses `provider.DeleteAsync(remoteId)` and is best-effort — failures here log at `Warning` but do not fail the mirror (the next pass will retry).

6. **Cleanup**: delete the staging snapshot file. Use `CancellationToken.None` for cleanup (Principle 17 — compensation must not be cancellable mid-flight).

**NuGet:** None new (SQLite's `BackupDatabase` is in `Microsoft.Data.Sqlite`).

**Key constraints:**
- The mirror size scales with brain content — `Files`, `Blobs`, `TailUploads`, `ActivityLog`, etc. For a typical V1 volume it's < 50 MB; for a 4 GiB-files-times-many-thousands volume it could approach hundreds of MB. The "load entire mirror into a pooled buffer" approach has a hard cap at `1 GiB`; over that, the service falls back to streaming (read-encrypt-upload in 4 MB chunks via `CryptoStream`-style chunked GCM — note: AES-GCM is **not** safely chunkable with a single nonce, so chunked mode uses one nonce per chunk and a small chunk-index header). The cap and the streaming-fallback design are recorded here; the implementation lands the simple in-memory path in §3.5 and a follow-up "streaming brain mirror" PR is a Phase 3 stretch goal **only if** profiling reveals real-world brains over the cap. Most users will never hit it; the cap-and-fallback prevents OOM.
- Mirror authenticity: AAD includes the timestamp to prevent rollback attacks (an attacker cannot swap an old mirror under a fresh-looking name and have it validate). Recovery (Phase 5) validates the AAD against the timestamp parsed from the remote name.
- The mirror **does not** include `WAL` table content beyond what SQLite's backup naturally captures. Recovery from a mirror produces a brain that may have `WAL` rows from in-flight operations at snapshot time; Phase 5 WAL recovery handles them on first open of the recovered volume — same code path as a clean restart.
- Per Principle 25, mirror operations use no appliance vocabulary in user-visible strings: notifications about mirror failures say "Could not save the catalogue copy to `{tailDisplayName}`."
- Failure of the *mirror upload* to one tail does not block uploads to others. Per-tail concurrency is the same as data uploads (cross-cutting decision 4).
- The 3-rolling retention is per-tail, not global. If a tail is offline for an extended period and accumulates only one fresh mirror after coming back online, the older two on that tail are still retained — they're useful for recovery.

---

### §3.6 — Volume integration

**Blueprint sections to read:** §11 (full Core public API), §11.1 (bulk write semantics — partial-failure-aware), §13.1 (skink/tail authority), §16.7 (mirror lifecycle integration).

**Scope summary:** Promote the §2.7 `FlashSkinkVolume` to expose `WriteBulkAsync` and the internal `RegisterTailAsync` admin entry; wire `UploadQueueService` and `BrainMirrorService` lifecycles to volume open/close.

Public additions:

| Method | Behaviour |
|---|---|
| `WriteBulkAsync(IReadOnlyList<BulkWriteItem> items, CancellationToken ct) → Result<BulkWriteReceipt>` | Iterates items sequentially under the volume gate (cross-cutting decision 1 from Phase 2 still holds — single-writer serialization). For each item, calls `WritePipeline.ExecuteAsync` and collects the per-item `Result<WriteReceipt>` into a `BulkWriteReceipt`. Cancellation observed between items; mid-item cancellation lets the per-item pipeline finish or fail naturally. The bulk operation is **not** transactional across items — each is an independent commit, by design (a 10,000-file backup that fails on file 7,500 should not throw away 7,499 successes). |

Internal additions:

| Method | Behaviour |
|---|---|
| `internal Task<Result> RegisterTailAsync(string providerType, string displayName, IStorageProvider provider, CancellationToken ct)` | Inserts a `Providers` row (no token / no client secret yet — those fields stay NULL until Phase 4 wires the OAuth flow), registers `provider` in the `InMemoryProviderRegistry`, and pulses the upload wakeup. Used by Phase 3 tests; Phase 4's public `AddTailAsync` ultimately calls this from inside the OAuth completion path. |

**Lifecycle wiring** (in `OpenAsync` / `CreateAsync`):

```
After brain is open and DEK is loaded:
1. Construct UploadWakeupSignal
2. Construct UploadQueueService(uploadQueueRepository, registry, networkMonitor,
                                 retryPolicy, rangeUploader, clock, wakeup, bus, logger)
3. Construct BrainMirrorService(connection, registry, dek, clock, wakeup, bus, logger)
4. await uploadQueueService.StartAsync(volumeCts.Token)
5. await brainMirrorService.StartAsync(volumeCts.Token)
6. WritePipeline subscribes wakeup.Pulse() to its post-commit event
```

**Lifecycle wiring** (in `DisposeAsync`):

```
1. Cancel volumeCts
2. await brainMirrorService.DisposeAsync()
   (runs one final mirror to all healthy tails before stopping)
3. await uploadQueueService.DisposeAsync()
   (signals workers, waits for clean shutdown with 10s timeout per worker)
4. (existing) zero DEK, dispose CryptoPipeline, dispose SqliteConnection,
   dispose notification bus subscription
```

**NuGet:** None new.

**Key constraints:**
- `BulkWriteReceipt` and `BulkWriteItem` are added to `Core.Abstractions/Models/`. `BulkWriteItem`: `Stream Source`, `string VirtualPath`, `IDisposable? OwnedSource` (optional — for callers that want the bulk method to dispose each source after use). `BulkWriteReceipt`: `IReadOnlyList<BulkItemResult>` where `BulkItemResult = (string VirtualPath, Result<WriteReceipt> Outcome)`.
- The `RegisterTailAsync` admin method is **internal** + `[InternalsVisibleTo("FlashSkink.Tests")]` — the test convention from `CLAUDE.md` says tests should not lean on internals, but `RegisterTailAsync` is the test surface the §11 public API will replace; this is the same shape Phase 1 used for `KeyVault` test access. Phase 4 makes it `private` once `AddTailAsync` is the public path.
- `WriteBulkAsync` does **not** parallelise across items in V1. Cross-cutting decision 1 (single-writer serialization) governs. Parallelism is post-V1.
- The lifecycle is asymmetric on `DisposeAsync`: brain mirror runs **before** the upload queue stops, because the mirror itself is uploaded *via* the queue's primitives. If the queue is stopped first, the final mirror has nowhere to land. Order matters; document with an XML comment.
- Pre-existing volumes opened with `OpenAsync` must not have their existing `TailUploads` rows mutated by the registration path. `RegisterTailAsync` only inserts a `Providers` row if no row with the same `ProviderID` exists; if one exists, it returns `Result.Ok` (idempotent re-registration on volume open), and tests register the matching `IStorageProvider` instance into the registry separately.
- The `INotificationBus` registration of upload-related handlers is unchanged from Phase 2 — `PersistenceNotificationHandler` already captures `Error`/`Critical`. No new handler types in Phase 3.

---

## What Phase 3 does NOT do

- **No cloud `IStorageProvider` implementations.** `GoogleDriveProvider`, `DropboxProvider`, `OneDriveProvider` are Phase 4. Phase 3 ships `FileSystemProvider` (real, in `src/`) which doubles as the cloud-provider test double per `CLAUDE.md`'s testing convention.
- **No `IProviderSetup` OAuth flow.** Phase 4. The OAuth device flow, BYOC credential capture, and refresh-token encryption all live in Phase 4 alongside the cloud providers that need them. `FileSystemProvider` needs none of this — its setup is just a path.
- **No public `AddTailAsync`.** Phase 4. Phase 3 has the internal `RegisterTailAsync` admin entry only.
- **No `BrainBackedProviderRegistry`.** The DEK-decrypts-OAuth-token path is Phase 4. Phase 3 ships only `InMemoryProviderRegistry`. (FileSystemProvider has no encrypted credentials to decrypt, so Phase 3's tail-on-volume-open path constructs `FileSystemProvider` directly from `Providers.ProviderConfig` JSON.)
- **No `HealthMonitorService`.** Phase 5. Phase 3's orchestrator does not consult per-tail `HealthStatus` for backoff modulation; the `RetryPolicy` is health-blind. Phase 5 wires the modulation.
- **No `NetworkAvailabilityMonitor` (real OS signal).** Phase 5. Phase 3 ships `AlwaysOnlineNetworkMonitor`.
- **No WAL recovery sweep on startup.** §21.2 (WAL recovery) and the orphaned-`UPLOADING`-row fix-up land in Phase 5. Phase 3 documents the dependency: a process crash mid-upload leaves the row `UPLOADING`, which Phase 5's startup sweep transitions back to `PENDING`. Until Phase 5 ships, the recovery story is "restart picks up from `LastActivityUtc`-bounded session expiry, which forces a fresh upload" — bounded redo, not data loss.
- **No `AuditService`, `SelfHealingService`.** Phase 5.
- **No GUI or CLI commands for upload status.** Phase 6. Phase 3's user-visible signal is the existing `INotificationBus` + `BackgroundFailures` queue.
- **No `RecoverAsync` consumer of the brain mirror.** Phase 5. Phase 3 *writes* the mirror; Phase 5 *reads* it.
- **No `VerifyAsync` cross-tail consistency check.** Phase 5.
- **No streaming brain mirror.** §3.5 caps the mirror at 1 GiB and uses an in-memory buffer; the streaming variant is a follow-up if profiling reveals real-world brains over the cap.

---

## Acceptance — Phase 3 is complete when

- [ ] All files listed in §3.1 through §3.6 exist and are committed on squash-merged PRs in `main`.
- [ ] `dotnet build` succeeds with zero warnings on `ubuntu-latest` and `windows-latest`.
- [ ] `dotnet test` is fully green: all Phase 0–2 tests still pass; all Phase 3 tests pass.
- [ ] The following scenarios pass as integration tests:
  - [ ] `RegisterTailAsync` (with a `FileSystemProvider` rooted at a temp dir) + `WriteFileAsync` of a 10 MB file → wait until the worker reports completion → `TailUploads.Status` row reaches `UPLOADED`, `RemoteId` matches the remote object's relative path, and the on-disk file at the tail decrypts to the original plaintext using the volume's DEK.
  - [ ] **End-to-end against the FileSystemProvider** — write 5 distinct files via `WriteFileAsync`; assert all 5 land at the configured tail root with sharded paths matching the skink's, all 5 `TailUploads` rows are `UPLOADED`, and `UploadSessions` is empty.
  - [ ] **Resume across "host change"** — `FaultInjectingStorageProvider` over a `FileSystemProvider`; open volume, write a 20 MB file, fault-inject failure on range 3, dispose volume mid-upload (worker cancelled with session in-flight), re-open the volume against the same tail-root path, observe upload resumes from confirmed `BytesUploaded` (not from byte 0); blob arrives intact and matches the source plaintext after decryption.
  - [ ] **Session expiry mid-upload** — `FaultInjectingStorageProvider.ForceSessionExpiryAfter(5)`; on range 5 of 10 the provider returns `UploadSessionExpired`; uploader best-effort `AbortUploadAsync`, deletes the `UploadSessions` row, restarts with fresh `BeginUploadAsync`; final blob is correct.
  - [ ] **Per-range retry** — `FaultInjectingStorageProvider.FailNextRange()` invoked twice; range 3 fails with a transient error twice and succeeds on the 3rd attempt; `RetryPolicy` was consulted with `1s, 4s` delays (verified via `FakeClock`); blob arrives intact and the row is `UPLOADED`.
  - [ ] **Cycle escalation and `MarkFailed`** — `FaultInjectingStorageProvider` fails every range with `ProviderUnreachable`; after 5 cycles the row is `FAILED` with `LastError` populated; an `Error`-severity notification is published; `BackgroundFailures` has the row; activity log has `UPLOAD_FAILED`.
  - [ ] **Cross-tail isolation** — register tails A and B; A's provider is a fault-injecting wrapper that always fails, B's is a clean `FileSystemProvider`; a single `WriteFileAsync` produces `UPLOADED` on B and `FAILED` on A; A's failure does not delay B's upload (verified via `FakeClock` advancing only B's deadlines).
  - [ ] **Cancellation honoured — orchestrator** — `volume.DisposeAsync()` mid-upload returns within the 10 s shutdown timeout; in-flight upload's `UploadSessions` row is preserved; on next open with the same provider, the upload resumes (not restarted).
  - [ ] **Brain mirror after commit** — register a `FileSystemProvider` tail, write a file, advance `FakeClock` past the 10 s debounce; observe a single `_brain/{timestamp}.bin` file on the tail; decrypt with the DEK; assert the decrypted SQLite database contains the freshly written file.
  - [ ] **Brain mirror retention** — write 5 files spaced 16 minutes apart (advancing `FakeClock`); observe exactly 3 `_brain/` files on the tail with the 3 most recent timestamps.
  - [ ] **Brain mirror on clean shutdown** — write file, immediately `DisposeAsync` before the 10 s debounce — observe one final mirror lands on the tail.
  - [ ] **`WriteBulkAsync` partial failure** — submit 10 items where item 5's source stream throws on read; result is a `BulkWriteReceipt` with 9 `Ok` and 1 `Fail` entries; the 9 successful items are committed and queued for upload.
  - [ ] **Network unavailable gating** — set `INetworkAvailabilityMonitor.IsAvailable = false` (test double); write a file; observe nothing uploads; flip to `true` + raise `AvailabilityChanged`; observe upload completes within one orchestrator tick.
  - [ ] **WAL invariant after upload** — for every `TailUploads` row at status `UPLOADED`, no `UploadSessions` row exists for that `(FileID, ProviderID)` (§21.3 invariant for sessions).
- [ ] **`ErrorCode` enum: zero new values added in Phase 3** (per cross-cutting decision 2). All codes used were declared in §1.1. `ErrorCode.cs` is not modified by any Phase 3 PR.
- [ ] CI `plan-check` job passes for all six PRs (each `.claude/plans/pr-3.X.md` exists, contains all required headings, cites at least one `§` blueprint reference).
- [ ] `docs/error-handling.md` is updated with the `RangeUploader` failure-rollback worked example (a sequel to Phase 2's `WritePipeline` example).
- [ ] Property-based test in `tests/FlashSkink.Tests/CrashConsistency/UploadCrashConsistencyTests.cs` runs FsCheck across "crash at line N of `RangeUploader.UploadAsync`" interleavings and verifies after each crash that either (a) `BytesUploaded` reflects only confirmed-by-provider ranges, or (b) the `UploadSessions` row is absent (200 cases per PR; nightly extends to 5000 per Phase 2's pattern).

---

## Principles exercised in Phase 3

- **Principle 1** (Core never throws across its public API) — `UploadQueueService`, `RangeUploader`, `BrainMirrorService`, `RetryPolicy.Next*`, every method on `FlashSkinkVolume` returns `Result` or `Result<T>` (or is a sanctioned-pure-function for `RetryPolicy`).
- **Principle 2** (single-survivor recovery) — every committed file produces an upload to *every* configured tail; per-tail isolation means a failed tail does not poison the mirror to another tail.
- **Principle 3** (skink is authoritative) — `RangeUploader` reads ciphertext from the local blob path; never from a tail.
- **Principle 4** (two commit boundaries stay sharp) — Phase 1 commit (Phase 2 dev plan) is unaffected by Phase 3; tail uploads are best-effort, never block writes.
- **Principle 5** (upload session state lives on the skink) — `UploadSessions` rows persist across the entire upload, including disconnect and host-change; the resumption path is exercised by the cross-host integration test.
- **Principle 6** (zero-knowledge at every external boundary) — every byte written to a tail is encrypted (data blobs by Phase 2's pipeline; brain mirror by §3.5).
- **Principle 7** (zero trust in the host) — brain mirror staging is on the skink (`.flashskink/staging/`), never `Path.GetTempPath()`.
- **Principle 13** — every async method takes `CancellationToken ct` as final parameter.
- **Principle 14** — `OperationCanceledException` is the first catch in every loop and pipeline.
- **Principle 15** — granular handling: `HttpRequestException` filtered by `StatusCode`, `IOException` for blob read failures, `SqliteException` for brain transactions, `Exception` last.
- **Principle 16** — `IMemoryOwner<byte>` and `SqliteTransaction` disposal in every failure path.
- **Principle 17** — `CancellationToken.None` literal in: brain mirror cleanup, `MarkUploaded`/`MarkFailed`/`DeleteSession` brain transactions (once the upload's outcome is determined, the bookkeeping must not be cancellable mid-flight), `DisposeAsync` paths.
- **Principle 18** — `RangeUploader` reuses one 4 MB pooled buffer per blob; brain mirror uses `stackalloc` for nonces and AAD; `RetryPolicy` is allocation-free.
- **Principle 19** — `IMemoryOwner<byte>` returned everywhere a buffer is produced; consumed and disposed by the caller.
- **Principle 20** — nonces and AAD `stackalloc`'d, consumed before any `await`.
- **Principle 22** — `DequeueNextBatchAsync` (declared in §1.6) is exercised here for the first time as a hot-path raw reader; all other queue/session reads use Dapper.
- **Principle 23** — `IStorageProvider`, `UploadSession`, `IProviderSetup`, `ProviderHealth` are not modified; the new `IProviderRegistry` and `INetworkAvailabilityMonitor` are *new* abstractions, not changes to frozen ones.
- **Principle 24** — `MarkFailed` outcomes log via `ILogger`, publish via `INotificationBus` (Error severity), and `BackgroundFailures` captures (via the existing `PersistenceNotificationHandler`); `ActivityLog` records both successes and failures.
- **Principle 25** — every notification `Title` and `Message` uses user vocabulary ("file", "tail", "back up", "catalogue copy") not appliance vocabulary ("blob", "range", "session", "WAL").
- **Principle 26** — refresh tokens, session URIs, and DEK bytes are never logged. `ErrorContext.Metadata` is filtered for `*Token` / `*Secret` / `*Mnemonic` keys.
- **Principle 27** — per-PR Core logging at `Result.Fail` site only; volume callers log the returned `ErrorContext`.
- **Principle 30** — crash-consistency property test verifies the §21.3 invariant for `UploadSessions` and `TailUploads` after every interleaved crash position.
- **Principle 31** — DEK is held by `VolumeContext`; `BrainMirrorService` reads it via that reference and never copies into a longer-lived buffer; Phase 3 does not introduce any new key material.
- **Principle 32** — every outbound network call is to a configured tail provider, dispatched via `IStorageProvider`; no telemetry, no update checks.

---

## Post-Phase-3 hand-off

After Phase 3, the session protocol continues unchanged. Phase 4 begins with:

> `read section 4.1 of the dev plan and perform`

The plan for §4.1 will read `dev-plan/phase-4-cloud-providers-and-byoc-setup.md` (not yet written) and the committed `.claude/plans/pr-3.*.md` files to discover the final public consumer surface of `IProviderRegistry`, `RangeUploader`, `UploadQueueService`, and `BrainMirrorService` before slotting in `GoogleDriveProvider`, `DropboxProvider`, `OneDriveProvider`, the `BrainBackedProviderRegistry` that decrypts OAuth refresh tokens at construction, and the `IProviderSetup` OAuth device flow that drives the public `FlashSkinkVolume.AddTailAsync`. (`FileSystemProvider` already lives in the codebase from Phase 3 §3.1.)

---

*Phase 3 is the first phase where bytes leave the skink. Every guarantee from Phase 2 — the encrypted blob, the brain row, the WAL scope — is what Phase 3 turns into a durable, resumable, multi-tail replica. After Phase 3, the product promise (any single survivor recovers everything) becomes physically true: a real tail holds a real complete copy, mirror brain included.*
