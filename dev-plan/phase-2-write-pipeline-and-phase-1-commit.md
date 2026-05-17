# Phase 2 — Write Pipeline and Phase 1 Commit

**Status marker:** This phase follows the standard session protocol defined in `CLAUDE.md`. Each section below (§2.1 through §2.7) maps to one PR, executed via `read section 2.X of the dev plan and perform`. Gate 1 (plan approval) and Gate 2 (implementation approval) are required for every section. Sections must be executed in order — each one depends on the types established by the section before it.

**Terminology note:** "Phase 1 commit" in this document refers to the **write-pipeline phase** defined by the blueprint (§5.2, §14.1) — the synchronous, transactional commit of an encrypted blob to the skink. It is not the same as the project's *Phase 1*. Tail uploads ("Phase 2 upload") are queued by Phase 2 of this dev plan but executed in dev-plan Phase 3.

---

## Goal

After Phase 2:

- A FlashSkink volume can write a file end-to-end: detect type, hash, optionally compress, encrypt, atomically commit to the skink, and record the result in the brain.
- A FlashSkink volume can read a file end-to-end: brain lookup, blob open, header parse, decrypt, decompress, plaintext-hash verification, and stream to the caller.
- Every write is wrapped in a WAL `WRITE` operation scope (PREPARE → COMMITTED), preserving the crash-consistency invariant from §21.3 across every interleaving the write pipeline can produce.
- `INotificationBus` exists in `FlashSkink.Core.Abstractions.Notifications` (callable from any Core service) with concrete `NotificationBus` and `NotificationDispatcher` in `FlashSkink.Presentation.Notifications` and the `PersistenceNotificationHandler` in `FlashSkink.Core/Engine/`. Background services (added in later phases) have a working sink the moment they need one.
- `FlashSkinkVolume` exposes the file and folder operations a user can perform without tails configured: `WriteFileAsync`, `ReadFileAsync`, `DeleteFileAsync`, `CreateFolderAsync`, `DeleteFolderAsync`, `RenameFolderAsync`, `MoveAsync`, `ListChildrenAsync`, `ListFilesAsync`, `ChangePasswordAsync`, `RestoreFromGracePeriodAsync`.
- Tail-upload rows (`TailUploads.Status = PENDING`) are queued by every write but never dequeued — the upload queue service in Phase 3 picks them up.
- Phase 3 (tails, providers, upload queue) can start with a single prompt.

---

## Cross-cutting decisions

Four decisions span multiple PRs in this phase. Recording them here keeps the rule in one place; each section below references back.

**1. Concurrency model — single-writer serialization.** `FlashSkinkVolume` holds an internal `SemaphoreSlim(1, 1)`. Every public volume method acquires it for the duration of the operation. This is the rule that makes the rest of the design work: the shared `SqliteConnection`, the volume-scoped `IncrementalHash`, the volume-scoped `CryptoPipeline`, and the volume-scoped LZ4 / Zstd codec instances are all single-threaded by virtue of this gate. Concurrent calls into the volume are not a goal in V1 — a portable backup tool whose normal access pattern is "the user clicks one thing at a time" doesn't pay for fine-grained concurrency. A reader-writer split is post-V1 if profiling later demands it.

**2. Maximum plaintext size — 4 GiB per file.** Exposed as `VolumeContext.MaxPlaintextBytes = 4L * 1024 * 1024 * 1024`. Enforced at three sites: `WritePipeline` stage 1 rejects oversized sources via the bounded buffer cap; `ReadPipeline` rejects up-front when `Blobs.PlaintextSize` exceeds the cap; `CompressionService.Decompress` asserts the caller-supplied `plaintextSize`. Returns `ErrorCode.FileTooLong`. The streaming-chunked variant in a post-V1 PR removes the cap.

**3. AAD format — `BlobID (16-byte raw GUID) || PlaintextSHA256 (32-byte raw digest)` = 48 bytes total.** Fixed-size and `stackalloc`-friendly. **Not** the 36-character canonical UUID string and **not** the 64-character lowercase hex digest. This pins what the §1.4 plan left ambiguous. Blueprint §13.6 should be updated to spell this out in the same PR that introduces it (§2.5). Wire-format changes after Phase 2 are expensive — every blob ever written carries this AAD baked into its GCM tag.

**4. ErrorCode reuse in this phase — no new values.** Both codes needed by Phase 2 already exist in the §1.1 enum: `BlobCorrupt` (declared for download-time integrity failures; its semantic scope is extended here to cover local blob-format parse errors — illegal `BlobFlags` combinations, header magic mismatches — which are structurally the same failure class) and `FileTooLong` (declared for oversized files; the 4 GiB cap from cross-cutting decision 2 is enforced under this code). The §1.1 "declare the full enum up-front" posture holds: **no new `ErrorCode` values are added in §2.2 through §2.7.** Alternatives considered and rejected: `VaultCorrupt` for blob-format errors (wrong layer — it implies the vault structures are unreadable, not an individual blob); `UsbFull` for the size cap (wrong cause — the drive is not full, the file is oversized). `BlobCorrupt` and `FileTooLong` are the right existing codes.

---

## Section index

| Section | Title | Deliverables |
|---|---|---|
| §2.1 | File type and entropy detection | `FileTypeService`, `EntropyDetector`, `MagicBytes` table, `KnownExtensions` map; tests |
| §2.2 | Compression service | `CompressionService` (LZ4 < 512 KB, Zstd ≥ 512 KB, no-gain rejection), `BlobFlags`; round-trip and rejection tests |
| §2.3 | Notification bus | `INotificationBus`, `INotificationHandler`, `Notification`, `NotificationSeverity`, `NotificationBus`, `NotificationDispatcher`, `PersistenceNotificationHandler`; tests |
| §2.4 | Atomic blob writer and WAL operation scope | `AtomicBlobWriter` (staging → fsync → rename → directory fsync), `WriteWalScope` (PREPARE/COMMITTED/FAILED wrapper); crash-interleaving tests |
| §2.5 | Write pipeline and Phase 1 commit | `WritePipeline`, `WriteReceipt`, `WriteStatus`; change-detection short-circuit; brain transaction inserts (`Blobs`, `Files`, `TailUploads` PENDING per active tail, `ActivityLog`); tests |
| §2.6 | Read pipeline | `ReadPipeline` implementing §14.2 stages 1–7; tamper-detection tests at every layer (header, GCM, decompression, SHA-256) |
| §2.7 | Volume public API — file and folder operations | `FlashSkinkVolume` grown from the §1.3 skeleton: `CreateAsync`, `OpenAsync`, file/folder methods listed in Goal; `IAsyncDisposable`; tests |

Full implementation detail for each section lives in `.claude/plans/pr-2.X.md`, written at Gate 1 of the corresponding session. The notes below summarise the blueprint sections each PR must read and the NuGet packages it introduces.

---

## Section notes

### §2.1 — File type and entropy detection

**Blueprint sections to read:** §17 (all subsections), §9.3, §9.4.

**Scope summary:** Three artefacts in `FlashSkink.Core/Engine/`.

`FileTypeService` (sealed) — `Detect(string fileName, ReadOnlySpan<byte> header) → FileTypeResult` returning `(string? Extension, string? MimeType)`. Never throws. Magic-byte MIME wins over extension MIME on conflict; extension is preserved as-given (lower-cased, dot-prefixed). ZIP disambiguation by extension per §17.3. The four output combinations:

| Extension | MimeType | Meaning |
|---|---|---|
| set | set | Both signals present and (typically) agree. The common case. |
| `null` | set | No file extension; magic bytes recognised. Common on Linux for executables and renamed files. |
| set | `null` | Extension known but magic bytes either absent (small file) or unrecognised. Pipeline falls back to `KnownExtensions` map; if still no MIME, the field stays null. |
| `null` | `null` | No extension and no recognised magic. Treated as opaque content; `WriteReceipt` carries both nulls through to the caller. |

`EntropyDetector` (sealed) — `IsCompressible(string? extension, ReadOnlySpan<byte> header) → bool`. Returns `false` for already-compressed formats listed in §17.5. **Default for unknown extension AND unrecognised magic: `true`** — the pipeline attempts compression and the no-gain rule in §2.2 catches actual incompressibles. Refusing to compress unknown content would penalise plain text files with no extension, which are common on Linux.

`MagicBytes` (static) — owns the byte signatures from §17.3 and the `KnownExtensions` map (extension → MIME). Both `FileTypeService` and `EntropyDetector` read from this single source.

**NuGet:** None new.

**Key constraints:**
- Magic byte signatures are `static readonly byte[]` declared once; lookups use `ReadOnlySpan<byte>.StartsWith` (Principle 18, §9.3).
- `KnownExtensions` is a `static readonly FrozenDictionary<string, string>` initialised once at type load.
- `FileTypeService.Detect` is a pure function — no I/O, no allocation beyond the returned record.
- The 16-byte header is read once at pipeline entry (in §2.5) via `stackalloc byte[16]` and shared between both consumers; neither service reads the source stream itself (Principle 20).
- The MP4 signature (`66 74 79 70` at offset 4) is a rare offset-non-zero case — encode it explicitly in `MagicBytes`.

---

### §2.2 — Compression service

**Blueprint sections to read:** §14.1 (step 2), §9.2, §9.6, §13.6 (flags), §28.

**Scope summary:** One service plus a flags enum in `FlashSkink.Core/Engine/`.

`BlobFlags` ([Flags] enum, `ushort`-backed) — `None = 0`, `Lz4 = 1`, `Zstd = 2`. Same bit layout as the blob header flags from §13.6. Lives here because §2.2 is the first consumer; §2.5 and §2.6 reference it via this declaration.

`CompressionService` (sealed) — owns LZ4 and Zstd codecs as scoped instances (Principle 18, §9.8):
- `TryCompress(ReadOnlyMemory<byte> input, out IMemoryOwner<byte>? output, out BlobFlags flags, out int writtenBytes)` — selects LZ4 if `input.Length < 512 KB`, Zstd level 3 otherwise. Returns `false` (with `output = null`, `flags = None`) when the compressed size exceeds 95% of input — caller writes the plaintext with no compression flag set.
- `Decompress(ReadOnlyMemory<byte> compressed, BlobFlags flags, int plaintextSize, IMemoryOwner<byte> destination, out int writtenBytes)` — dispatches on `flags`. `BlobFlags.None` is a copy. Returns `Result`.

A `RecyclableMemoryStreamManager` is registered as a DI singleton in this PR (Principle 21). `CompressionService` consumes it for any place that would otherwise have used `new MemoryStream()`.

This PR uses two existing `ErrorCode` values per cross-cutting decision 4: `BlobCorrupt` (semantic scope extended to local blob-format parse errors) and `FileTooLong` (4 GiB cap enforcement). Neither is added to `Core.Abstractions/Results/ErrorCode.cs` — both were declared in §1.1.

**NuGet introduced:** `K4os.Compression.LZ4`, `ZstdNet`, `Microsoft.IO.RecyclableMemoryStream`. Verify versions are current and pin in `Directory.Packages.props`. `ZstdNet` is the canonical choice per §28 (rejected: `ZstdSharp.Port`); `Microsoft.IO.RecyclableMemoryStream` is added here rather than in §2.5 because `CompressionService` is the first stage that needs a pooled stream — the singleton registration rides with its first consumer. Native libzstd RID assets are validated by Phase 0's `nightly.yml` publish matrix (§28).

**Key constraints:**
- Output buffers are rented via `MemoryPool<byte>.Shared.Rent`, returned as `IMemoryOwner<byte>`. The default `MemoryPool<byte>.Shared` is backed by `ArrayPool<byte>.Shared` but does not zero on dispose; this PR introduces a `ClearOnDispose` `IMemoryOwner<byte>` wrapper for buffers that hold plaintext or ciphertext, so the zero-on-return guarantee from §2.5 is honoured uniformly across both pipelines (Principle 19).
- The 512 KB threshold and the 95% no-gain rule are constants exposed on `CompressionService` so tests can pin them and §2.5 can document them.
- Decompression never trusts `compressed.Length` for sizing; it trusts the `Blobs.PlaintextSize` value the caller passes in (which comes from the brain, not the blob). Per cross-cutting decision 2, `Decompress` rejects `plaintextSize > VolumeContext.MaxPlaintextBytes` with `ErrorCode.FileTooLong` *before* allocating the destination buffer — avoids OOM on a brain row whose `PlaintextSize` has been corrupted to a pathological value.
- Flags `Lz4` and `Zstd` are mutually exclusive; setting both is a defect. `Decompress` returns `ErrorCode.BlobCorrupt` on illegal flag combinations and on header magic mismatches surfaced by §1.4 `BlobHeader.Parse`.

---

### §2.3 — Notification bus

**Blueprint sections to read:** §8 (all subsections), §4.2 (dependency graph), Principle 24, Principle 8 (Core → Presentation prohibition).

**Scope summary:** Four contracts in `FlashSkink.Core.Abstractions/Notifications/`, two implementations in `FlashSkink.Presentation/Notifications/`, and one handler in `FlashSkink.Core/Engine/`.

**Contracts (in `FlashSkink.Core.Abstractions.Notifications`)** — these live in `Abstractions` because every Core publisher (`WritePipeline`, `ReadPipeline`, `UploadQueueService`, `AuditService`, etc.) and every Core handler (`PersistenceNotificationHandler`) must reference them, and per §4.2 / Principle 8 Core does not reference Presentation:
- `INotificationBus` and `INotificationHandler` (public interfaces)
- `Notification` (sealed class, properties from blueprint §8.3 — note `OccurredUtc` (`DateTime`, UTC) rather than `OccurredAt` (`DateTimeOffset`) so persistence to `BackgroundFailures.OccurredUtc` is a 1:1 mapping)
- `NotificationSeverity` (enum: `Info`, `Warning`, `Error`, `Critical`)

**Implementations (in `FlashSkink.Presentation.Notifications`)** — consumer-facing infrastructure:
- `NotificationBus` (`public sealed`, `IAsyncDisposable`) — `Channel<Notification>` of capacity 100 with `BoundedChannelFullMode.DropOldest`, single-reader/multi-writer; the dispatch loop reads the channel and delegates each notification to the dispatcher.
- `NotificationDispatcher` (`public sealed`, `IAsyncDisposable`) — fans out to registered handlers; deduplicates by `(Source, ErrorCode)` within a 60-second window per §8.4; runs a periodic flush task (default 5-second cadence) that emits suppressed-count summaries when a window expires.

**Handler (in `FlashSkink.Core/Engine/`)**:
- `PersistenceNotificationHandler` implements `INotificationHandler` (an `Abstractions` contract) and consumes `BackgroundFailureRepository` from §1.6. Writes every `Error` and `Critical` notification to `BackgroundFailures`. `Info` and `Warning` are not persisted.

**NuGet:** None new (`System.Threading.Channels` is BCL).

**Key constraints:**
- Contracts in `Abstractions`, implementations in `Presentation`, persistence handler in Core/Engine — see scope summary above for the rationale (Principle 8). A new architecture test (`Core_DoesNotReference_Presentation`) is added in this PR alongside the existing assembly-layering checks.
- `NotificationBus` and `NotificationDispatcher` are `public sealed` per blueprint §8.3 — they are constructed by host `Program.cs` for DI registration and by the test project directly; the interfaces in `Core.Abstractions.Notifications` are the DI registration target.
- Handler exceptions in the dispatch loop are caught and logged at `Warning` — a misbehaving handler must not interrupt fan-out (§8.3).
- If the `BackgroundFailures` insert itself fails, log to `ILogger` only; never publish to the bus from inside a handler (would loop).
- Deduplication window state is in-memory only; restart resets it. This is intentional — the persistence path captures anything that mattered.
- `NotificationBus` is registered as a singleton in DI by host projects (Phase 6). `PersistenceNotificationHandler` is registered as an `INotificationHandler` and added to the dispatcher at startup.
- Phase 2 registers no UI or CLI handler — those land in Phase 6. The bus must function correctly with only the persistence handler registered (verified in tests).
- Notification messages obey Principle 25 (appliance vocabulary discipline) — no mention of "blob", "WAL", "stripe", "DEK", or "PRAGMA" in `Title` or `Message`.
- **Channel-full drop logging.** `BoundedChannelFullMode.DropOldest` means a true unbounded flood (after `Source + ErrorCode` deduplication has done its work) silently evicts the oldest queued notifications. The persistence handler will not see them. The bus tracks a per-window drop counter and logs at `Warning` via `ILogger<NotificationBus>` whenever a publish causes an eviction, with the count of drops since the last successful drain. The dropped *content* is gone, but the fact-of-drop reaches the file sink. V1 acceptable loss (§8.2 already accepts that "a flood from a single source indicates a systemic issue better described by one notification with a count, not 100 individual ones") — this constraint records the visibility tradeoff explicitly.

---

### §2.4 — Atomic blob writer and WAL operation scope

**Blueprint sections to read:** §13.4, §13.5, §16.6 (WAL methods recap), §21.2 (WRITE recovery case), §21.3, Principles 17, 29, 30.

**Scope summary:** Two types in `FlashSkink.Core/Storage/`.

`AtomicBlobWriter` (sealed) — owns the on-disk write protocol from §13.4:
- `WriteAsync(string skinkRoot, string blobId, ReadOnlyMemory<byte> blobBytes, CancellationToken ct) → Result<string>`. Computes the sharded destination path `[skinkRoot]/.flashskink/blobs/{blobId[0:2]}/{blobId[2:4]}/{blobId}.bin`. Writes to `[skinkRoot]/.flashskink/staging/{blobId}.tmp`. `fsync`s the temp file via `RandomAccess.FlushToDisk(SafeFileHandle)`. Atomically renames to the destination path. `fsync`s the destination directory. Returns the final path.
- `DeleteStagingAsync(string skinkRoot, string blobId, CancellationToken ct) → Result` — compensation entry-point used by `WriteWalScope` on rollback.

`WriteWalScope` (sealed, `IAsyncDisposable`) — wraps a write in the WAL state machine:
- `OpenAsync(WalRepository wal, string fileId, string blobId, string virtualPath, CancellationToken ct) → Result<WriteWalScope>` inserts a WAL row (`Operation = "WRITE"`, `Phase = "PREPARE"`, `Payload = JSON{FileID, BlobID, VirtualPath}`).
- `CompleteAsync(CancellationToken ct = default)` transitions PREPARE → COMMITTED. The body uses `CancellationToken.None` as a literal at every internal await site (Principle 17) — once the brain transaction is durable, the WAL row's COMMITTED transition must not be cancellable mid-flight.
- `DisposeAsync` — if the scope reaches dispose without `CompleteAsync` having run, transition to FAILED with `CancellationToken.None`. If even the FAILED transition fails, log to `ILogger` and swallow (the row will be picked up by Phase 5 recovery and resolved idempotently).

**NuGet:** None new.

**Key constraints:**
- **Directory creation.** The two-level shard `[skinkRoot]/.flashskink/blobs/{xx}/{yy}/` is created on demand via `Directory.CreateDirectory` (idempotent) inside `WriteAsync` immediately before the rename. After creating a previously-absent shard directory, the *parent* of the new directory is fsync'd — on extN/HFS+/APFS, `mkdir` itself is not durable until the parent directory is synced. Once a shard exists, future writes to that shard skip the create and the parent fsync. The staging directory `.flashskink/staging/` is created once at volume open by `FlashSkinkVolume.OpenAsync` / `CreateAsync` (§2.7) and is fsync'd then.
- **Directory fsync after rename — platform notes.** On Linux/macOS, `fsync` on a directory file descriptor persists the rename (§13.4 step 6). On Windows, the equivalence is *not* literal: opening a directory handle for `FlushFileBuffers` requires `FILE_FLAG_BACKUP_SEMANTICS` and Microsoft's documentation does not guarantee POSIX `fsync(dirfd)` semantics. Phase 2's working assumption is that NTFS metadata journaling makes the rename durable in practice once `MoveFileEx` returns. The PR plan **measures** this rather than asserting it: a crash-injection test (forced power-off via VM snapshot, or its CI-friendly proxy) confirms the rename survives. If it does not, fall back to opening the directory handle with `FILE_FLAG_BACKUP_SEMANTICS` and calling `FlushFileBuffers`. Either way, the cross-platform helper in `AtomicBlobWriter` is the single site that encodes the policy.
- **BlobID lifetime and retry semantics.** Each call into `WritePipeline.ExecuteAsync` generates a fresh BlobID. A retry after a brain-transaction failure (i.e. a fresh user-level write attempt) generates a *new* BlobID and writes a *new* blob; the previous attempt's blob is orphaned and cleaned up in two passes — `WriteWalScope.DisposeAsync` deletes the staging file (if rename did not complete) or the renamed destination file (if rename did complete; the BlobID is in the WAL `Payload`), and Phase 5 audit's "blob with no `Files` row referencing it" sweep catches any survivors. Consequence: an existing file at the destination at rename time is **never** an in-flight retry — it is either a UUID collision (astronomical) or a Phase 5 recovery path that this code did not coordinate with. Both reduce to "this is a bug" and return `ErrorCode.PathConflict`. Reusing the same BlobID across retries (which would make rename idempotent) was rejected because the staging-vs-destination cleanup branching becomes load-bearing on whether the prior attempt's rename succeeded — an extra state machine for a case that fresh BlobIDs eliminate.
- Rename uses `File.Move(staging, dest, overwrite: false)`. See above for what the `overwrite: false` rejection means.
- Staging files are cleaned in the failure path of `WriteWalScope.DisposeAsync` via `AtomicBlobWriter.DeleteStagingAsync` — also with `CancellationToken.None`. When the WAL `Payload` indicates rename had completed (i.e. `WriteAsync` returned a destination path before the brain transaction failed), `DisposeAsync` also deletes the destination file via `AtomicBlobWriter.DeleteDestinationAsync(skinkRoot, blobId, ct)` — added to the type for this purpose.
- The FAT32 detection-and-warning flow lives in setup (Phase 6); §2.4 does not detect FS type but does document the assumption in an XML comment.
- Property-based crash-consistency tests live in `tests/FlashSkink.Tests/CrashConsistency/`. This PR seeds the folder with the first test class; FsCheck iteration count per PR is 200 (default is 100, but crash-interleaving tests benefit disproportionately from higher case counts because the bug surface is combinatorial). The 5000-case run is gated to `nightly.yml`.

---

### §2.5 — Write pipeline and Phase 1 commit

**Blueprint sections to read:** §14.1 (full), §13.6, §16.5, §16.6, §16.7 (deferred mirror — note in scope), §9.2, §9.4, §9.8, Principles 18–22, 29, 30.

**Scope summary:** One pipeline type, one receipt record, one status enum in `FlashSkink.Core/Engine/`.

`WriteStatus` (enum) — `Written`, `Unchanged`.

`WriteReceipt` (sealed record) — `FileID`, `BlobID`, `Status`, `PlaintextSize`, `EncryptedSize`, `MimeType?`, `Extension?`.

`WritePipeline` (sealed) — orchestrates §14.1 stages 0–7:

```csharp
public Task<Result<WriteReceipt>> ExecuteAsync(
    Stream source, string virtualPath,
    VolumeContext context, CancellationToken ct);
```

Stage flow:
0. **Type detection** — `stackalloc byte[16]` header read; `FileTypeService.Detect` + `EntropyDetector.IsCompressible`; rewind to position 0.
1. **Plaintext SHA-256 + buffered read** — stream into a single pooled `IMemoryOwner<byte>` (the `ClearOnDispose` wrapper from §2.2) bounded by `VolumeContext.MaxPlaintextBytes` (cross-cutting decision 2), while incrementally hashing via the volume-scoped `IncrementalHash`. If the source produces more bytes than the cap, return `ErrorCode.FileTooLong`. V1 read-once-into-buffer model; chunked streaming through compress/encrypt is deferred to a later optimisation PR if profiling justifies it.
2. **Change-detection short-circuit** — `BlobRepository.GetByPlaintextHashAsync(sha256)` lookup; if a `Blobs` row exists AND a `Files` row at the same `VirtualPath` already references it, return `Result.Ok(WriteReceipt(Status = Unchanged))` without writing. No dedup across paths in V1.
3. **Compression (conditional)** — `CompressionService.TryCompress` if `IsCompressible`; honour the no-gain rejection (resulting `BlobFlags.None` is a first-class outcome, not an error).
4. **Encryption** — `CryptoPipeline.Encrypt` (from §1.4) with AAD per cross-cutting decision 3 (16-byte raw GUID `||` 32-byte raw SHA-256 digest, total 48 bytes, `stackalloc`); fresh `stackalloc` nonce; GCM tag.
5. **Blob assembly** — header (20 B) + ciphertext + tag, into a pooled output `IMemoryOwner<byte>` (`ClearOnDispose` wrapper).
6. **Durable write** — `AtomicBlobWriter.WriteAsync`.
7. **Encrypted XXHash64** — `XxHash64.Hash` over the final blob bytes for bit-rot detection (Principle 18, §13.7). Stored in the `Blobs.EncryptedXXHash` column as a 16-character lowercase hex string in stage 8. **Not verified during reads** — see §2.6. The Phase 5 `AuditService` is the consumer that re-computes and compares it to detect bit-rot on the skink without paying decryption cost.
8. **Brain commit** — single SQLite transaction:
   - `INSERT INTO Blobs (...)`
   - `EnsureFolderPathAsync` for missing intermediate folders (§16.5)
   - `INSERT INTO Files (...)`
   - `INSERT INTO TailUploads (FileID, ProviderID, Status='PENDING', ...)` per active provider in `Providers` (zero rows in Phase 2, since no providers are configured yet — Phase 3/4 wires them)
   - `INSERT INTO ActivityLog (Category='WRITE', ...)`
   - `WriteWalScope.CompleteAsync(CancellationToken.None)` inside the same transaction
   - Commit

The pipeline returns `Result.Ok(WriteReceipt)` only when the brain transaction commits (DR-3, Principle 4). Failure between stages 5 and 7 is recovered by `WriteWalScope.DisposeAsync` (the staging file is removed and the WAL row marked FAILED).

`VolumeContext` is the parameter object that carries `SqliteConnection`, DEK reference (`ReadOnlyMemory<byte>` view, owned by the volume session), `RecyclableMemoryStreamManager`, `INotificationBus`, repositories, and the skink root path. It is constructed by §2.7 from `VolumeSession` (§1.3).

**NuGet introduced:** `System.IO.Hashing`. XXHash64 is first consumed here (§14.1 step 7) to hash the assembled blob bytes for bit-rot detection. §2.4 references the design rationale but carries no call site; the dependency belongs with the first usage.

**Key constraints:**
- All pooled buffers come from `MemoryPool<byte>.Shared` via `IMemoryOwner<byte>` (Principle 19). Buffers carrying plaintext or ciphertext use the `ClearOnDispose` wrapper introduced in §2.2 — pool buffers must not leak content between callers; raw `ArrayPool<byte>.Shared` with `clearArray: true` is **not** used here, so the ownership model is uniform across §2.2, §2.5, and §2.6.
- AAD format is pinned by cross-cutting decision 3: 48-byte fixed-size buffer (`stackalloc byte[48]`), 16 bytes raw GUID then 32 bytes raw SHA-256 digest. The string forms (canonical UUID, lowercase hex) are *never* used as AAD input.
- `IncrementalHash` is scoped per volume (one instance lives on `FlashSkinkVolume` for the duration of the session) and reused across writes. Volume serialization (cross-cutting decision 1) is what makes this safe — `IncrementalHash` is not thread-safe.
- The brain transaction inserts are issued via Dapper (not raw reader) — these are general queries, not hot-path scans (Principle 22). `TailUploads` inserts in Phase 2 always insert zero rows (no providers); the SQL is correct from day one and will exercise immediately when Phase 3 adds the first provider.
- Notification on write failure: `Source = "WritePipeline"`, severity `Error`, `Title` user-facing ("Could not save file"), `Message` includes `virtualPath`, `Error` carries the `ErrorContext`.
- Notification on success: none — this is a routine operation; activity-log entry is the audit trail (Principle 24 covers *failures* needing a sink, not successes).

---

### §2.6 — Read pipeline

**Blueprint sections to read:** §14.2, §13.6, §13.7, §9.5, §9.8, Principle 18.

**Scope summary:** One pipeline type in `FlashSkink.Core/Engine/`.

`ReadPipeline` (sealed):

```csharp
public Task<Result> ExecuteAsync(
    string virtualPath, Stream destination,
    VolumeContext context, CancellationToken ct);
```

Stage flow per §14.2:
1. **Brain lookup** — `FileRepository` resolves `virtualPath` to a `Files` row; `BlobRepository.GetByIdAsync(BlobID)` produces the `Blobs` row. Reject up-front with `ErrorCode.FileTooLong` if `Blobs.PlaintextSize > VolumeContext.MaxPlaintextBytes` (cross-cutting decision 2) — fails before any allocation.
2. **Blob open** — `File.OpenRead` at the sharded path (computed from BlobID).
3. **Header parse** — `BlobHeader.Parse` (from §1.4); reject on bad magic / unknown version.
4. **Decrypt** — `CryptoPipeline.Decrypt` with AAD constructed per cross-cutting decision 3 (48 bytes raw, BlobID GUID + raw SHA-256 from the `Blobs` row); GCM tag validated.
5. **Decompress** — `CompressionService.Decompress` per `BlobFlags` into a pooled `IMemoryOwner<byte>` (`ClearOnDispose`). `BlobFlags.None` is a memory copy.
6. **Hash verify** — `IncrementalHash.GetHashAndReset` over decompressed plaintext compared to `Blobs.PlaintextSHA256`. Mismatch → `ErrorCode.ChecksumMismatch` (§14.2 step 6, §13.7).
7. **Copy to destination** — stream plaintext to `destination`.

`Blobs.EncryptedXXHash` is **not** verified on this path. It is the bit-rot detection signal owned by the Phase 5 `AuditService`, which scans blobs cheaply without decrypting. Adding XXHash verification to the read path would duplicate work (the GCM tag in stage 4 already detects ciphertext tampering) without catching anything new.

**NuGet:** None new.

**Key constraints:**
- Streaming buffer of 4 MB pooled (§9.5 pattern) for stages 2–4; stage 5/6 produces a single decompressed `IMemoryOwner<byte>` capped at `VolumeContext.MaxPlaintextBytes`.
- The destination stream is *not* truncated or seeked; the caller owns it. The pipeline writes only the plaintext bytes.
- A `ChecksumMismatch` publishes a `Critical` notification (`Source = "ReadPipeline"`, `RequiresUserAction = true`) and persists via the bus — bit-rot or tampering on the skink is the user's signal to invoke recovery from a tail (Phase 5 surface).
- A `DecryptionFailed` from GCM tag mismatch publishes `Critical` similarly.
- No partial plaintext is ever written to `destination` on a verification failure: stage 5/6 buffer plaintext entirely before stage 7. (For very large files this trades memory for safety in V1; combined with the 4 GiB cap from cross-cutting decision 2, the trade is bounded. A streaming-verify variant is a post-V1 optimisation.)
- `IncrementalHash` is the *same* volume-scoped instance used by `WritePipeline` (cross-cutting decision 1 makes this safe via volume serialization) — `GetHashAndReset` returns it to a clean state after each use.
- `CryptoPipeline` instance is also volume-scoped (one `AesGcm` for the volume's lifetime, per §1.4); same safety story.
- Read pipeline never touches tails — local blob only (Principle 3).

---

### §2.7 — Volume public API — file and folder operations

**Blueprint sections to read:** §11, §11.1, §16.5, §16.6, §13.1.

**Scope summary:** Promote the §1.3 `VolumeLifecycle` skeleton into the full `FlashSkinkVolume` public API for the file/folder axis. Lives in `FlashSkink.Core/Orchestration/`.

Public methods exposed by this PR:

| Method | Behaviour |
|---|---|
| `CreateAsync(VolumeCreationOptions options)` | Generate mnemonic, create vault (`KeyVault.CreateAsync`), open brain, run migrations, write initial `Settings` rows (`GracePeriodDays`, `AuditIntervalHours`, `VolumeCreatedUtc`, `AppVersion`), seed `SchemaVersions` with V1. Returns `Result<FlashSkinkVolume>`. |
| `OpenAsync(string skinkRoot, string password)` | Unlock vault → open brain → run migrations → return volume. |
| `WriteFileAsync(Stream source, string virtualPath, CancellationToken ct)` | Delegate to `WritePipeline.ExecuteAsync`. |
| `ReadFileAsync(string virtualPath, Stream destination, CancellationToken ct)` | Delegate to `ReadPipeline.ExecuteAsync`. |
| `DeleteFileAsync(string virtualPath, CancellationToken ct)` | WAL `DELETE` scope + `FileRepository.DeleteFileAsync` + `BlobRepository.SoftDeleteAsync` + `DeleteLog` row, all in one transaction. |
| `CreateFolderAsync(string name, string? parentId, CancellationToken ct)` | `FileRepository.InsertAsync` for a folder row; constraint violation → `PathConflict`. |
| `DeleteFolderAsync(string folderId, bool confirmed, CancellationToken ct)` | If non-empty and `!confirmed`, return `ConfirmationRequired` with child count in metadata (§16.5). Otherwise WAL `CASCADE_DELETE` scope + `FileRepository.DeleteFolderCascadeAsync`. |
| `RenameFolderAsync(string folderId, string newName, CancellationToken ct)` | Delegates to `FileRepository.RenameFolderAsync`; cascading `VirtualPath` update inside one transaction. |
| `MoveAsync(string fileId, string? newParentId, CancellationToken ct)` | Cycle check (§16.4 ancestor CTE) → `FileRepository.MoveAsync`. `newParentId = null` is valid (root). |
| `ListChildrenAsync(string? parentId, CancellationToken ct)` | `FileRepository.ListChildrenAsync`. Folders-first ordering (§16.4). |
| `ListFilesAsync(string virtualPath, CancellationToken ct)` | `FileRepository.ListFilesAsync`. |
| `ChangePasswordAsync(string oldPassword, string newPassword, CancellationToken ct)` | `KeyVault.ChangePasswordAsync`; brain key is unaffected (HKDF from same DEK). |
| `RestoreFromGracePeriodAsync(string fileId, DateTimeOffset deletedAtOrLater, CancellationToken ct)` | `FileRepository.RestoreFromGracePeriodAsync`. |
| `DisposeAsync()` | Zero DEK, dispose `CryptoPipeline`, dispose `SqliteConnection`, dispose notification bus subscription. |

The `UsbRemoved` / `UsbReinserted` / `TailStatusChanged` events from §11 are declared on the type but not raised — the corresponding monitor services land in later phases. The full rationale for declaring §11 events while leaving §11 deferred methods undeclared lives in the top-level "What Phase 2 does NOT do" entry on the §11 contract divergence; this PR is where that policy actually lands in code.

**NuGet:** None new.

**Key constraints:**
- Every public method returns `Result` or `Result<T>` (Principle 1). Every async method takes `CancellationToken ct` last (Principle 13).
- **Single-writer serialization (cross-cutting decision 1).** The volume holds a `SemaphoreSlim(1, 1)` (`_gate`). Every public method's first action is `await _gate.WaitAsync(ct)`; every method releases in `finally`. This is the load-bearing invariant that makes the shared `SqliteConnection`, the volume-scoped `IncrementalHash`, the volume-scoped `CryptoPipeline`, and the codec instances safe. Concurrent calls into one volume are not a goal; if a caller wants parallelism across files, they open multiple volumes (post-V1).
- `FlashSkinkVolume` is `IAsyncDisposable`. Disposal is idempotent and zeroes all key material (Principle 31). Disposal acquires `_gate` before tearing down to avoid racing in-flight operations.
- The volume holds exactly one open `SqliteConnection` for its lifetime — repository instances share it. Safe under the gate above.
- **Path-vs-ID addressing rule.** Methods take `VirtualPath` when the target is named by user input that may not yet correspond to a tree node (`WriteFileAsync`, `ReadFileAsync`, `DeleteFileAsync`, `ListFilesAsync`). Methods take `FileID` / `ParentID` when the target is a tree node the caller has already navigated to (`DeleteFolderAsync`, `RenameFolderAsync`, `MoveAsync`, `ListChildrenAsync`, `RestoreFromGracePeriodAsync`). `CreateFolderAsync(name, parentId?)` is the hybrid: the new folder's name is user-supplied, its parent is navigated. The rule is "user-typed input → path; navigated-to-node → id"; record it here so future additions don't drift.
- The `Providers` table is empty in Phase 2 (no `AddTailAsync` yet); writes still issue the per-provider `INSERT INTO TailUploads` query, which inserts zero rows. This avoids a behavioural divergence between Phase 2 and Phase 3 code paths.
- The §11 methods deferred to later phases (`WriteBulkAsync`, `AddTailAsync`, `RemoveTailAsync`, `ListTailsAsync`, `CheckHealthAsync`, `VerifyAsync`, `ExportAsync`, `RecoverAsync`, `GetActivityAsync`, `ResetPasswordAsync`) are *not declared* on the type in this PR — see the top-level "What Phase 2 does NOT do" entry for why undeclared beats stubbed. The §11 events *are* declared, never raised; same entry has the rationale.

---

## What Phase 2 does NOT do

- **No upload to tails.** `UploadQueueService`, `RangeUploader`, and `RetryPolicy` are Phase 3.
- **No brain mirror to tails.** §16.7 mirror-to-tails depends on the upload queue and lands in Phase 3.
- **No WAL recovery sweep on startup.** `WalRepository.ListIncompleteAsync` is implemented; the recovery procedure that calls it (§21.2) lives in Phase 5.
- **No `IStorageProvider` implementations.** `FileSystemProvider`, `GoogleDriveProvider`, `DropboxProvider`, `OneDriveProvider` are Phase 4.
- **No `AuditService`, `SelfHealingService`, `HealthMonitorService`, `UsbMonitorService`.** Phase 5.
- **No GUI or CLI commands.** Phase 6.
- **`FlashSkinkVolume` does not match the full §11 public API — and the gap is asymmetric by design.** Phase 2 declares the §11 *events* (`UsbRemoved`, `UsbReinserted`, `TailStatusChanged`) as zero-cost shells that never fire until later phases wire the monitor services. Phase 2 does *not* declare the §11 *methods* deferred to later phases: `WriteBulkAsync` (Phase 3), `AddTailAsync` / `RemoveTailAsync` / `ListTailsAsync` / `CheckHealthAsync` (Phase 4), `VerifyAsync` / `ExportAsync` / `RecoverAsync` / `GetActivityAsync` (Phase 5), and `ResetPasswordAsync` (Phase 5). The asymmetry is the point. **A method added in a later phase is purely additive — no existing caller changes.** Stubbing one to return `Result.Fail(NotImplemented)` would advertise a working surface that doesn't work, and the user-facing error string from such a stub would misrepresent feature availability rather than absence; leaving the method undeclared makes "this isn't here yet" a compile-time signal. **An event added in a later phase is not additive in the same way.** Subscribers wire up at volume construction; an event added later means every consumer built against the earlier surface silently misses firings until it re-binds. Declaring all §11 events from day 1 — even unraised — buys a stable subscription contract across phases.
- **No streaming chunk-through compress/encrypt path.** V1 reads the plaintext into a pooled buffer; the streaming variant is a later profiling-driven optimisation PR.
- **No FAT32 detection or compatibility mode.** Setup-time detection lands in Phase 6; the compatibility mode is post-V1.

---

## Acceptance — Phase 2 is complete when

- [ ] All files listed in §2.1 through §2.7 exist and are committed on squash-merged PRs in `main`.
- [ ] `dotnet build` succeeds with zero warnings on `ubuntu-latest` and `windows-latest`.
- [ ] `dotnet test` is fully green: all Phase 0 and Phase 1 tests still pass; all Phase 2 tests pass.
- [ ] The following scenarios pass as integration tests or are demonstrated in test output:
  - [ ] `FileTypeService.Detect` on a JPEG header returns `(".jpg", "image/jpeg")`.
  - [ ] `FileTypeService.Detect` on a `.docx` ZIP-signature file returns the OOXML MIME type.
  - [ ] `EntropyDetector.IsCompressible` returns `false` for JPEG, PNG, MP4, ZIP; `true` for plain text.
  - [ ] `CompressionService.TryCompress` of a 100 KB highly compressible payload returns `BlobFlags.Lz4`; round-trip via `Decompress` matches input bytes.
  - [ ] `CompressionService.TryCompress` of a 1 MB payload uses `BlobFlags.Zstd`.
  - [ ] `CompressionService.TryCompress` of a high-entropy payload (random bytes) returns `false` (no-gain rejection).
  - [ ] `NotificationBus.PublishAsync` of an `Error` notification results in a `BackgroundFailures` row via `PersistenceNotificationHandler`.
  - [ ] `NotificationBus.PublishAsync` of an `Info` notification does NOT write to `BackgroundFailures`.
  - [ ] Deduplication: 10 identical `Error` notifications within 60 s produce 1 `BackgroundFailures` row.
  - [ ] `AtomicBlobWriter.WriteAsync` produces a file at the sharded path; the staging file is gone; the destination is byte-identical to input.
  - [ ] `WriteWalScope` opened then disposed without `CompleteAsync` leaves the WAL row in `Phase = "FAILED"`.
  - [ ] `WritePipeline.ExecuteAsync` of a small text file produces a `Files` row, `Blobs` row, `ActivityLog` row, and (with no providers configured) zero `TailUploads` rows.
  - [ ] `WritePipeline.ExecuteAsync` of the same content twice at the same path returns `WriteStatus.Unchanged` on the second call (change-detection short-circuit).
  - [ ] `ReadPipeline.ExecuteAsync` of a written file streams identical plaintext bytes to the destination.
  - [ ] `ReadPipeline.ExecuteAsync` against a tampered blob (one ciphertext byte flipped) returns `ErrorCode.DecryptionFailed` and writes nothing to the destination.
  - [ ] `ReadPipeline.ExecuteAsync` against a blob whose plaintext SHA-256 has been corrupted in-flight (simulated) returns `ErrorCode.ChecksumMismatch`.
  - [ ] `FlashSkinkVolume.WriteFileAsync` → `ReadFileAsync` round-trips a 5 MB random-bytes file with byte-equality.
  - [ ] **End-to-end LZ4 branch** — write a 100 KB highly compressible file via `FlashSkinkVolume.WriteFileAsync`, read back via `ReadFileAsync`, byte-identical. Inspect `Blobs.Compression = 'LZ4'` in the brain.
  - [ ] **End-to-end no-gain branch** — write a 1 MB random-bytes file (incompressible). Inspect `Blobs.Compression IS NULL`. Round-trip succeeds.
  - [ ] **End-to-end Zstd branch** — write a 1 MB highly compressible file. Inspect `Blobs.Compression = 'ZSTD'`. Round-trip succeeds.
  - [ ] **Concurrent ReadFileAsync serialization** — two `Task`s call `volume.ReadFileAsync` against the same volume in parallel; both succeed without `IncrementalHash` corruption or `SqliteConnection` reentrancy errors. The `SemaphoreSlim` gate is the mechanism; this test is the witness.
  - [ ] **Cancellation honoured — write path** — start a 100 MB write, cancel mid-flight; `WriteFileAsync` returns `ErrorCode.Cancelled` and the WAL row reaches `Phase = "FAILED"` with no orphaned blob on disk.
  - [ ] **Cancellation honoured — read path** — start a 100 MB read, cancel mid-flight; `ReadFileAsync` returns `ErrorCode.Cancelled` and the destination stream is left in a partial state but no exception escapes Core.
  - [ ] **Brain-transaction failure reaches FAILED** — fault-inject a `SqliteException` at the `INSERT INTO Files` step inside the brain commit; verify `WriteWalScope.DisposeAsync` transitions the WAL row to FAILED, the staging file is gone, the destination file (if rename completed) is gone, and the §21.3 invariant holds.
  - [ ] **Skink disk full during staging** — fault-inject `IOException` with `HResult = ERROR_DISK_FULL` (Windows) or `ENOSPC` (Linux) during the staging write inside `AtomicBlobWriter`; verify `WriteFileAsync` returns `ErrorCode.UsbFull`, no `Files` / `Blobs` row is created, no orphan staging file remains.
  - [ ] **`FileTooLong` enforcement** — attempt to write a synthetic >4 GiB stream (using a counting wrapper, not real bytes); pipeline rejects with `ErrorCode.FileTooLong` before allocating the buffer.
  - [ ] `FlashSkinkVolume.DeleteFolderAsync(confirmed: false)` on a non-empty folder returns `ErrorCode.ConfirmationRequired` with `Metadata["ChildCount"]` populated.
  - [ ] `FlashSkinkVolume.MoveAsync` of a folder under one of its own descendants returns `ErrorCode.CyclicMoveDetected`.
  - [ ] `FlashSkinkVolume.ChangePasswordAsync` followed by close-and-reopen with the new password succeeds.
  - [ ] `tests/FlashSkink.Tests/CrashConsistency/WriteCrashConsistencyTests.cs` runs FsCheck across crash-at-step-N interleavings of `WritePipeline` and verifies the §21.3 invariant after each (200 cases per PR — bumped from FsCheck's default 100 because crash-interleaving bug surface is combinatorial; nightly extends to 5000).
- [ ] CI `plan-check` job passes for all seven PRs (each `.claude/plans/pr-2.X.md` exists, contains all required headings, cites at least one `§` blueprint reference).
- [ ] **`ErrorCode` enum: zero new values added in Phase 2** (per cross-cutting decision 4). All codes used — `BlobCorrupt`, `FileTooLong`, `DecryptionFailed`, `ChecksumMismatch`, `PathConflict`, `CyclicMoveDetected`, `ConfirmationRequired`, `StagingFailed`, `UsbFull`, `Cancelled`, `Unknown` — were declared in §1.1. `ErrorCode.cs` is not modified by any Phase 2 PR.
- [ ] `docs/error-handling.md` is updated with the `WritePipeline` failure-rollback worked example (a natural sequel to the §1.5 `BrainConnectionFactory` example).

---

## Principles exercised in Phase 2

Every PR in this phase touches the principles listed below; Gate 2 checks each one explicitly.

- **Principle 1** (Core never throws across its public API) — `WritePipeline`, `ReadPipeline`, `CompressionService`, `AtomicBlobWriter`, `WriteWalScope`, and every method on `FlashSkinkVolume` returns `Result` or `Result<T>`.
- **Principle 3** (skink is authoritative; tails are catch-up replicas) — `ReadPipeline` reads the local blob, never the tail.
- **Principle 4** (two commit boundaries stay sharp) — `WritePipeline` returns success only when the brain transaction commits; per-tail rows are queued PENDING and never block.
- **Principle 6** (zero-knowledge at every external boundary) — every blob written to disk is encrypted; the AAD-binding (`BlobID || PlaintextSHA256`) prevents blob-substitution attacks.
- **Principle 7** (zero trust in the host) — `AtomicBlobWriter` stages on the skink (`.flashskink/staging/`); `Path.GetTempPath()` is not referenced anywhere in Phase 2.
- **Principle 13** (`CancellationToken ct` always last, always present) — every async pipeline method, every volume method, every scope method.
- **Principle 14** (`OperationCanceledException` caught first, mapped to `ErrorCode.Cancelled`, logged at `Information`) — every `try/catch` in the pipelines.
- **Principle 15** (no bare `catch (Exception)`) — granular handling for `IOException`, `UnauthorizedAccessException`, `SqliteException` with `SqliteErrorCode` filters, `CryptographicException`, then `Exception` last.
- **Principle 16** (dispose on every failure path) — `IMemoryOwner<byte>` and `SqliteTransaction` disposal in `WritePipeline`; staging-file cleanup in `WriteWalScope`.
- **Principle 17** (`CancellationToken.None` literal in compensation paths) — every WAL `CompleteAsync`/`FAILED` transition; every staging cleanup; every brain-transaction rollback.
- **Principle 18** (allocation-conscious hot paths) — `WritePipeline`, `ReadPipeline`, `CryptoPipeline`, `CompressionService`, `FileTypeService` all use pooled buffers, `Span<T>`, `stackalloc`, and value types where applicable.
- **Principle 19** (`IMemoryOwner<byte>` ownership) — every method that produces a buffer returns `IMemoryOwner<byte>`; the caller disposes.
- **Principle 20** (`stackalloc` never crosses `await`) — 16-byte header read, 12-byte nonce, 16-byte GCM tag, 32-byte hash digest, 20-byte blob header — all `stackalloc`, all consumed before any `await`.
- **Principle 21** (`RecyclableMemoryStream` replaces `new MemoryStream()`) — registered as singleton in §2.2.
- **Principle 22** (raw reader for hot paths; Dapper for general queries) — Phase 2's brain transactions are general queries (Dapper). Phase 2 introduces no new hot-path readers; the one declared in §1.6 (`UploadQueueRepository.DequeueNextBatchAsync`) waits for Phase 3 to be exercised.
- **Principle 24** (no background failure is silent) — every failure in `WritePipeline`, `ReadPipeline`, and `WriteWalScope` is logged via `ILogger`, published via `INotificationBus`, and (for Error/Critical) persisted via `PersistenceNotificationHandler`.
- **Principle 25** (appliance vocabulary discipline) — every notification `Title` and `Message` uses user vocabulary ("file", "folder") not appliance vocabulary ("blob", "WAL", "stripe", "DEK").
- **Principle 26** (logging never contains secrets) — plaintext bytes, ciphertext bytes, DEK bytes, AAD bytes, and the password buffer are excluded from every log call.
- **Principle 27** (Core logs internally; callers log the `Result`) — pipelines log at the `Result.Fail` site; `FlashSkinkVolume` logs the returned `ErrorContext` on its way back to the caller.
- **Principle 29** (atomic file-level writes on the skink) — `AtomicBlobWriter` is the canonical implementation.
- **Principle 30** (crash-consistency invariant preserved across every interleaving) — `WriteWalScope` + property-based tests verify the invariant for `WRITE`, `DELETE`, `CASCADE_DELETE` operations.
- **Principle 31** (keys zeroed on close) — `FlashSkinkVolume.DisposeAsync` zeroes DEK, brain key, and any password buffer it received.
- **Principle 32** (no telemetry, no update checks) — pipelines and volume make zero outbound network calls in Phase 2 (no providers exist yet).

---

## Post-Phase-2 hand-off

After Phase 2, the session protocol continues unchanged. Phase 3 begins with:

> `read section 3.1 of the dev plan and perform`

The plan for §3.1 will read `dev-plan/phase-3-providers-and-upload-queue.md` (not yet written) and the committed `.claude/plans/pr-2.*.md` files to discover the final public API surface of `WritePipeline`, `INotificationBus`, `WriteWalScope`, and `FlashSkinkVolume` before wiring the upload queue and brain mirror against them.

---

*Phase 2 is the first phase that produces user-visible behaviour: a file written to the skink can be read back from the skink, with full crash-consistency guarantees. Tails remain absent — and that is by design. Every line of code that an upload eventually relies on rests on the commit boundary established here.*
