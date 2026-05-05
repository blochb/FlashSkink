# PR 2.5 — Write pipeline and Phase 1 commit

**Branch:** pr/2.5-write-pipeline-phase1-commit
**Blueprint sections:** §14.1, §13.6, §13.7, §16.2, §16.5, §16.6, §16.7, §9.2, §9.4, §9.6, §9.8
**Dev plan section:** phase-2 §2.5

## Scope

Delivers the Phase 1 (write-pipeline) commit boundary. `WritePipeline.ExecuteAsync` ingests one
source `Stream` at a `virtualPath`, runs the §14.1 stage flow (type detect → SHA-256 + buffered
read → change-detection short-circuit → compress → encrypt → blob assemble → atomic write →
XXHash64 → brain transaction), and returns `Result<WriteReceipt>`. The brain transaction
inserts `Blobs`, `Files`, `TailUploads` (zero rows in Phase 2 — no providers configured), and
`ActivityLog` rows, then transitions the WAL row to `COMMITTED`, then commits the SQLite
transaction.

`VolumeContext` is introduced as the per-volume parameter object: it owns the volume-scoped
`SqliteConnection`, `IncrementalHash` (SHA-256), `CryptoPipeline`, `CompressionService`,
`AtomicBlobWriter`, `RecyclableMemoryStreamManager`, `INotificationBus`, all four repositories
that this PR consumes (`BlobRepository`, `FileRepository`, `WalRepository`,
`ActivityLogRepository`), and the skink root path. The DEK is borrowed via
`ReadOnlyMemory<byte>` from `VolumeSession` (lifetime owned by the session, not the context).
`VolumeContext.MaxPlaintextBytes` re-exports the plaintext cap (originally cross-cutting decision 2; corrected post-review to `Array.MaxLength` ~2 GiB to match the single-buffer allocation constraint). §2.7
will construct one inside `FlashSkinkVolume`; this PR exercises it from tests by constructing
it directly against an in-memory brain plus a per-test temp skink root.

The PR also makes three small additive changes to land §2.5's contract dependencies:

1. `CryptoPipeline.Encrypt` gains a `BlobFlags flags` parameter so the compression flags ride
   into the blob header (the §1.4 implementation hardcoded `BlobFlags.None`). Existing §1.4
   tests are updated to pass `BlobFlags.None`.
2. `WalRepository.TransitionAsync` gains an optional `SqliteTransaction? transaction = null`
   parameter (additive, mirrors `InsertAsync`'s signature) so the WAL `COMMITTED` transition
   can ride inside the brain commit transaction (cross-cutting note in §2.5 stage 8).
3. `WriteWalScope.CompleteAsync` gains an optional `SqliteTransaction? transaction = null`
   parameter that it forwards to `WalRepository.TransitionAsync`. Existing call sites continue
   to work unchanged (the parameter is optional).

`System.IO.Hashing` is introduced as a new NuGet for `XxHash64` (§14.1 step 7, §13.7).
`Microsoft.IO.RecyclableMemoryStream` (already pinned in `Directory.Packages.props`) is added
as a `PackageReference` to `FlashSkink.Core.csproj` (deferred from §2.2 per its drift note 3 —
this PR is the first consumer).

`docs/error-handling.md` is extended with the `WritePipeline` failure-rollback worked example
(per phase-2 acceptance criteria).

## Files to create

### Production — `src/FlashSkink.Core/Engine/`
- `VolumeContext.cs` — sealed class; per-volume parameter object; `IDisposable`. ~140 lines.
- `WriteStatus.cs` — `public enum WriteStatus { Written, Unchanged }`. ~12 lines.
- `WriteReceipt.cs` — `public sealed record WriteReceipt`. ~25 lines.
- `WritePipeline.cs` — sealed class; the §14.1 orchestrator; ~480 lines.

### Documentation
- `docs/error-handling.md` — append a "WritePipeline failure rollback" section that walks
  through the Phase-1 brain-transaction failure path: WAL row in PREPARE → brain insert
  fails → exception caught → `tx.Rollback()` → `WriteWalScope.DisposeAsync` deletes staging
  + (if `MarkRenamed` was set) destination → WAL row transitions to FAILED → `Result.Fail`
  returned → `INotificationBus` publishes a user-facing `Error` notification → §21.3
  invariant intact. ~+60 lines.

### Tests — `tests/FlashSkink.Tests/Engine/`
- `WritePipelineTests.cs` — round-trip writes against a real in-memory brain and a per-test
  temp skink-root directory. Verifies brain rows after each write. Exercises LZ4, Zstd,
  no-gain, change-detection, cancellation, FileTooLong, brain-tx-failure. ~620 lines.
- `VolumeContextTests.cs` — disposal idempotency, ownership semantics. ~80 lines.

### Tests — `tests/FlashSkink.Tests/CrashConsistency/` (existing folder)
- Extends `WriteCrashConsistencyTests.cs` with a new `[Property]` —
  `WritePipelineCrash_AtAnyStep_PreservesInvariant` — that wraps the §2.4 5-step model with
  a brain seeded by the actual `WritePipeline` and asserts §21.3 still holds across crash
  interleavings, including the new "brain insert fails" interleaving. The §2.4 file is
  modified (not replaced); the new `[Property]` adds ~140 lines.

## Files to modify

- `Directory.Packages.props` — add `System.IO.Hashing` (version `10.0.0`, aligned with
  the `Microsoft.Extensions.Logging.Abstractions 10.0.7` / `Microsoft.Data.Sqlite 10.0.7`
  baseline already in the file).
- `src/FlashSkink.Core/FlashSkink.Core.csproj` — add `<PackageReference Include="System.IO.Hashing" />`
  and `<PackageReference Include="Microsoft.IO.RecyclableMemoryStream" />`.
- `src/FlashSkink.Core/Crypto/CryptoPipeline.cs` — add `BlobFlags flags` parameter to
  `Encrypt`. Update `BlobHeader.Write(...)` call site to pass `flags` instead of the hardcoded
  `BlobFlags.None`. XML doc updated.
- `src/FlashSkink.Core/Metadata/WalRepository.cs` — add optional
  `SqliteTransaction? transaction = null` parameter to `TransitionAsync`; forward to the
  `CommandDefinition`.
- `src/FlashSkink.Core/Storage/WriteWalScope.cs` — add optional
  `SqliteTransaction? transaction = null` parameter to `CompleteAsync`; forward to
  `_wal.TransitionAsync`. XML doc updated to note the parameter and the still-honoured
  Principle 17 rule (`ct` is not forwarded; `transaction` is optional and only used by §2.5
  to keep the COMMITTED transition inside the brain commit tx).
- `tests/FlashSkink.Tests/Crypto/CryptoPipelineTests.cs` — update each `Encrypt` call to pass
  `BlobFlags.None` (positional-argument addition; ~+1 token per call).
- `tests/FlashSkink.Tests/Storage/WriteWalScopeTests.cs` — add one regression test
  (`CompleteAsync_WithTransaction_TransitionParticipatesInTx`) verifying that when a
  transaction is passed and rolled back, the WAL row stays in PREPARE.
- `tests/FlashSkink.Tests/Metadata/WalRepositoryTests.cs` — add one regression test
  (`TransitionAsync_WithTransaction_RolledBack_LeavesPhaseUnchanged`).
- `tests/FlashSkink.Tests/CrashConsistency/WriteCrashConsistencyTests.cs` — add the new
  `[Property]` per the test spec below.
- `docs/error-handling.md` — append the worked example (see "Files to create").

## Dependencies

- NuGet **introduced**: `System.IO.Hashing` 10.0.0 (XXHash64 for §14.1 step 7).
- NuGet **first-consumed by Core**: `Microsoft.IO.RecyclableMemoryStream` 3.0.1 (already in
  `Directory.Packages.props`; new `PackageReference` in `FlashSkink.Core.csproj`).
- Project references: none new.

## Public API surface

### `FlashSkink.Core.Engine.WriteStatus` (enum)
Summary intent: outcome of a `WritePipeline.ExecuteAsync` call.

```csharp
public enum WriteStatus
{
    Written = 0,
    Unchanged = 1,
}
```

### `FlashSkink.Core.Engine.WriteReceipt` (sealed record)
Summary intent: the result a successful Phase 1 commit returns to the caller.

```csharp
public sealed record WriteReceipt
{
    public required string FileId { get; init; }
    public required string BlobId { get; init; }
    public required WriteStatus Status { get; init; }
    public required long PlaintextSize { get; init; }
    public required long EncryptedSize { get; init; }
    public string? MimeType { get; init; }
    public string? Extension { get; init; }
}
```

`MimeType` and `Extension` are nullable per §2.1 `FileTypeResult` semantics. `BlobId` is the
existing blob id on the `Unchanged` path (no new blob written) and the freshly generated id on
the `Written` path. `PlaintextSize` is the byte count of the source stream as measured by
stage 1.

### `FlashSkink.Core.Engine.VolumeContext` (public sealed class : IDisposable)
Summary intent: per-volume parameter object that carries the live brain connection, DEK view,
volume-scoped pipeline instances, repositories, and infrastructure that pipelines and
`FlashSkinkVolume` consume. Disposal disposes the volume-scoped `IncrementalHash` and
`CompressionService`. The DEK is **not** owned here (`VolumeSession` zeros it); the
`SqliteConnection` is **not** owned here either (the session disposes it).

```csharp
public sealed class VolumeContext : IDisposable
{
    /// <summary>Maximum plaintext bytes per file (Array.MaxLength, ~2 GiB). Aliases CompressionService.MaxPlaintextBytes.</summary>
    public const long MaxPlaintextBytes = CompressionService.MaxPlaintextBytes;

    /// <summary>Open encrypted brain connection, lifetime owned by VolumeSession.</summary>
    public SqliteConnection BrainConnection { get; }

    /// <summary>Borrowed view of the live DEK, lifetime owned by VolumeSession.</summary>
    public ReadOnlyMemory<byte> Dek { get; }

    /// <summary>Skink root path, e.g. "E:\" or "/mnt/usb".</summary>
    public string SkinkRoot { get; }

    /// <summary>Volume-scoped SHA-256 incremental hasher; reused across writes and reads.</summary>
    public IncrementalHash Sha256 { get; }

    /// <summary>Volume-scoped crypto pipeline (one AesGcm allocated per Encrypt/Decrypt call internally).</summary>
    public CryptoPipeline Crypto { get; }

    /// <summary>Volume-scoped compression service; native Zstd codec handles reused.</summary>
    public CompressionService Compression { get; }

    /// <summary>Atomic blob writer; stateless beyond the injected logger.</summary>
    public AtomicBlobWriter BlobWriter { get; }

    /// <summary>Singleton stream manager for any pipeline stage that needs a growing memory stream.</summary>
    public RecyclableMemoryStreamManager StreamManager { get; }

    /// <summary>The notification bus pipelines publish failure events to.</summary>
    public INotificationBus NotificationBus { get; }

    public BlobRepository Blobs { get; }
    public FileRepository Files { get; }
    public WalRepository Wal { get; }
    public ActivityLogRepository ActivityLog { get; }

    /// <summary>
    /// Constructs a context. The caller transfers ownership of <paramref name="sha256"/> and
    /// <paramref name="compression"/> to this instance — they are disposed by <see cref="Dispose"/>.
    /// All other parameters retain their prior owners.
    /// </summary>
    public VolumeContext(
        SqliteConnection brainConnection,
        ReadOnlyMemory<byte> dek,
        string skinkRoot,
        IncrementalHash sha256,
        CryptoPipeline crypto,
        CompressionService compression,
        AtomicBlobWriter blobWriter,
        RecyclableMemoryStreamManager streamManager,
        INotificationBus notificationBus,
        BlobRepository blobs,
        FileRepository files,
        WalRepository wal,
        ActivityLogRepository activityLog);

    /// <summary>Disposes the volume-scoped IncrementalHash and CompressionService. Idempotent.</summary>
    public void Dispose();
}
```

Disposal is idempotent (guarded by an `Interlocked.Exchange` on a `_disposed` int flag — same
pattern as `VolumeSession.DisposeAsync`).

### `FlashSkink.Core.Engine.WritePipeline` (public sealed class)
Summary intent: orchestrates §14.1 stages 0–8 for a single write. The single public entry
point returns `Result<WriteReceipt>` and never throws across the boundary (Principle 1).

Constructor: `WritePipeline(FileTypeService fileTypeService, EntropyDetector entropyDetector, ILogger<WritePipeline> logger)`

(`FileTypeService` and `EntropyDetector` are the two §2.1 services; constructed once at host
startup and injected. They are stateless and may be shared across volumes — they are *not* on
`VolumeContext`.)

- `Task<Result<WriteReceipt>> ExecuteAsync(Stream source, string virtualPath, VolumeContext context, CancellationToken ct)`

  Runs the §14.1 stage flow. See "Method-body contracts" for the full sequence and failure
  mapping. Returns `Result<WriteReceipt>` only on a successfully committed brain transaction
  (Principle 4). Cancellation maps to `ErrorCode.Cancelled`. The caller (§2.7
  `FlashSkinkVolume.WriteFileAsync`) holds the volume serialization gate (cross-cutting
  decision 1) before invoking, so the volume-scoped instances on `VolumeContext` are
  guaranteed to be single-threaded for the duration of this call.

### Modified — `FlashSkink.Core.Crypto.CryptoPipeline.Encrypt`

Signature change (additive parameter):

```csharp
public Result Encrypt(
    ReadOnlySpan<byte> plaintext,
    ReadOnlySpan<byte> dek,
    ReadOnlySpan<byte> aad,
    BlobFlags flags,                   // NEW — was hardcoded to BlobFlags.None
    IMemoryOwner<byte> outputOwner,
    out int bytesWritten);
```

`Decrypt` is unchanged (it already returns `out BlobFlags flags` from the parsed header).

### Modified — `FlashSkink.Core.Metadata.WalRepository.TransitionAsync`

Signature change (additive optional parameter):

```csharp
public async Task<Result> TransitionAsync(
    string walId,
    string newPhase,
    CancellationToken ct,
    SqliteTransaction? transaction = null);
```

When `transaction` is non-null, it is forwarded to the `CommandDefinition` constructor so the
UPDATE participates in the caller's transaction. When null, the UPDATE auto-commits as before.
All existing call sites (`WriteWalScope.CompleteAsync`, `WriteWalScope.DisposeAsync`,
`FileRepository.DeleteFileAsync` etc.) continue to work because the parameter is optional and
defaults to null.

### Modified — `FlashSkink.Core.Storage.WriteWalScope.CompleteAsync`

Signature change (additive optional parameter):

```csharp
public async Task<Result> CompleteAsync(
    SqliteTransaction? transaction = null,
    CancellationToken ct = default);
```

When `transaction` is non-null, it is forwarded to `WalRepository.TransitionAsync` so the
COMMITTED transition rides inside the caller's transaction. The XML doc is updated to note
this and to retain the existing Principle 17 phrasing about the `ct` parameter being accepted
for symmetry but not forwarded.

## Internal types

### `WritePipeline` private helpers

- `private static long? TryGetSourceLength(Stream source)` — returns `source.Length` when
  `source.CanSeek && source.Length >= 0`, else `null`. Used by stage 1 to short-circuit the
  `MaxPlaintextBytes` cap check before allocation.
- `private async ValueTask<Result<IMemoryOwner<byte>>> ReadIntoBufferAsync(Stream source, long? knownLength, IncrementalHash hasher, CancellationToken ct)` —
  reads `source` to the end into a single `ClearOnDisposeOwner` while feeding `hasher`. When
  `knownLength` is non-null and within the cap, rents exactly that size up-front. Otherwise
  uses a `RecyclableMemoryStream` from `context.StreamManager` to grow, then copies into a
  sized `ClearOnDisposeOwner` once the total is known and validated against the cap. Returns
  `ErrorCode.FileTooLong` when the cumulative count exceeds `VolumeContext.MaxPlaintextBytes`,
  disposing all rented buffers first.
- `private static void BuildAad(Span<byte> aad48, Guid blobId, ReadOnlySpan<byte> plaintextSha256)` —
  writes the 48-byte AAD per cross-cutting decision 3: 16 bytes raw GUID
  (`blobId.TryWriteBytes(aad48[..16])`) followed by 32 bytes raw SHA-256 digest. `aad48` is
  always a `stackalloc byte[48]` at the call site; never allocated on the heap.
- `private async Task<Result> CommitBrainAsync(VolumeContext ctx, BrainCommitArgs args, WriteWalScope scope, CancellationToken ct)` —
  opens the SQLite transaction, issues raw Dapper inserts for `Blobs`, `Files`, `TailUploads`,
  `ActivityLog`, calls `scope.CompleteAsync(transaction: tx)`, commits, and returns
  `Result.Ok()`. On any thrown exception or non-success Result inside the try, rolls back
  and returns the propagated failure (the surrounding `WriteWalScope.DisposeAsync` then runs
  the staging/destination cleanup and the WAL FAILED transition).
- `private readonly record struct BrainCommitArgs(string FileId, string BlobId, string? ParentId, string Name, string? Extension, string? MimeType, string VirtualPath, long PlaintextSize, long EncryptedSize, string PlaintextSha256, string EncryptedXxHash, string? Compression, string BlobPath, DateTime NowUtc, string FilenameForActivity)` —
  positional struct; just a parameter bag for `CommitBrainAsync` to keep its arity reasonable.

### `WritePipeline` private constants

- `private const string ActivityCategory = "WRITE";`
- `private const string SourceTag = "WritePipeline";` — the `Notification.Source` value.

## Method-body contracts

### `WritePipeline.ExecuteAsync` — full stage flow

```
0. ct.ThrowIfCancellationRequested().

1. STAGE 0 — type detection:
   Span<byte> headerBuf = stackalloc byte[16];
   int headerRead = source.CanSeek
       ? ReadHeaderSeekable(source, headerBuf)   // private helper; reads, then Seek(0)
       : await ReadHeaderNonSeekableAsync(source, headerBuf, ct);  // reads + buffers — but
                                                                    // see comment below
   FileTypeResult typeResult = _fileTypeService.Detect(virtualPath, headerBuf[..headerRead]);
   bool isCompressible = _entropyDetector.IsCompressible(typeResult.Extension, headerBuf[..headerRead]);

   For non-seekable streams the 16-byte header read is *also* fed into IncrementalHash and
   into the front of the read buffer, so Stage 1 does not have to re-read those bytes. The
   bookkeeping is internal to ReadIntoBufferAsync; the seekable path Seek(0)s and then runs
   the same loop.

2. STAGE 1 — plaintext SHA-256 + buffered read:
   long? knownLength = TryGetSourceLength(source);
   if (knownLength is { } len && len > VolumeContext.MaxPlaintextBytes)
       return Result<WriteReceipt>.Fail(ErrorCode.FileTooLong, ...);

   context.Sha256 is reset before use (defensive — even though the volume gate makes this
   safe, IncrementalHash retains state from the prior call):
       context.Sha256.GetHashAndReset();   // discard return; we need a clean slate

   var bufferResult = await ReadIntoBufferAsync(source, knownLength, context.Sha256, ct);
   if (!bufferResult.Success) return Result<WriteReceipt>.Fail(bufferResult.Error!);
   using var plaintextBuffer = bufferResult.Value!;
   long plaintextSize = plaintextBuffer.Memory.Length;

   Span<byte> sha256 = stackalloc byte[32];
   if (!context.Sha256.TryGetHashAndReset(sha256, out int hashWritten) || hashWritten != 32)
       return Result<WriteReceipt>.Fail(ErrorCode.Unknown, "Hash read failed");

3. STAGE 2 — change-detection short-circuit:
   string sha256Hex = Convert.ToHexString(sha256).ToLowerInvariant();
   var existingResult = await context.Blobs.GetByPlaintextHashAsync(sha256Hex, ct);
   if (!existingResult.Success) { LogAndPublish(...); return Result<WriteReceipt>.Fail(existingResult.Error!); }
   if (existingResult.Value is { } existingBlob)
   {
       // Look up Files row at the exact virtualPath. We do not have a "GetByPath" repo
       // method yet — Phase 2 §2.7 adds one. For §2.5 we issue an inline Dapper query:
       var existingFile = await context.BrainConnection.QuerySingleOrDefaultAsync<dynamic>(
           "SELECT FileID FROM Files WHERE VirtualPath = @P AND BlobID = @B",
           new { P = virtualPath, B = existingBlob.BlobId });
       if (existingFile is not null)
       {
           return Result<WriteReceipt>.Ok(new WriteReceipt {
               FileId = (string)existingFile.FileID,
               BlobId = existingBlob.BlobId,
               Status = WriteStatus.Unchanged,
               PlaintextSize = plaintextSize,
               EncryptedSize = existingBlob.EncryptedSize,
               MimeType = typeResult.MimeType,
               Extension = typeResult.Extension,
           });
       }
       // Hash matches but path differs — V1 has no cross-path dedup; treat as a fresh blob.
       // Fall through to the encryption path with a new BlobID.
   }

4. STAGE 3 — compression:
   IMemoryOwner<byte>? compressed = null;
   ReadOnlyMemory<byte> payload;
   BlobFlags flags;
   try
   {
       if (isCompressible &&
           context.Compression.TryCompress(plaintextBuffer.Memory, out compressed, out flags, out int compWritten))
       {
           payload = compressed!.Memory;
       }
       else
       {
           flags = BlobFlags.None;
           payload = plaintextBuffer.Memory;
       }

       // STAGE 4 — encryption:
       Guid newBlobGuid = Guid.NewGuid();
       string newBlobId = newBlobGuid.ToString("N");

       // AAD construction (cross-cutting decision 3 — never the string forms):
       Span<byte> aad = stackalloc byte[48];
       BuildAad(aad, newBlobGuid, sha256);

       int encryptedLen = BlobHeader.HeaderSize + payload.Length + BlobHeader.TagSize;
       using var encrypted = ClearOnDisposeOwner.Rent(encryptedLen);
       var encResult = context.Crypto.Encrypt(payload.Span, context.Dek.Span, aad, flags, encrypted, out int encWritten);
       if (!encResult.Success) { LogAndPublish(...); return Result<WriteReceipt>.Fail(encResult.Error!); }

       // STAGE 5 — blob assembly: encrypted owner already holds [Header || Ciphertext || Tag].

       // STAGE 6 — open WAL scope BEFORE the on-disk write so a crash between WriteAsync and
       // the brain commit has a recovery row. (FileID is generated here too so it can be
       // included in the WAL Payload.)
       string newFileId = Guid.NewGuid().ToString();
       var scopeResult = await WriteWalScope.OpenAsync(
           context.Wal, context.BlobWriter, context.SkinkRoot,
           newFileId, newBlobId, virtualPath,
           _walScopeLogger, ct);
       if (!scopeResult.Success) { LogAndPublish(...); return Result<WriteReceipt>.Fail(scopeResult.Error!); }
       await using var scope = scopeResult.Value!;

       // STAGE 7 — durable write:
       var writeResult = await context.BlobWriter.WriteAsync(
           context.SkinkRoot, newBlobId, encrypted.Memory[..encWritten], ct);
       if (!writeResult.Success) { LogAndPublish(...); return Result<WriteReceipt>.Fail(writeResult.Error!); }
       string blobPath = writeResult.Value!;
       scope.MarkRenamed();

       // STAGE 8 — XXHash64 over the final blob bytes (bit-rot detection signal):
       ulong xxhash = XxHash64.HashToUInt64(encrypted.Memory.Span[..encWritten]);
       string encryptedXxHashHex = xxhash.ToString("x16");

       // STAGE 9 — ensure folder path BEFORE opening the brain transaction.
       // Idempotent; orphan folders from a failed file insert are harmless.
       var (parentId, name) = SplitVirtualPath(virtualPath);
       string? parentFileId = null;
       if (parentId is { Length: > 0 })
       {
           var ensureResult = await context.Files.EnsureFolderPathAsync(parentId, ct);
           if (!ensureResult.Success) { LogAndPublish(...); return Result<WriteReceipt>.Fail(ensureResult.Error!); }
           parentFileId = ensureResult.Value;
       }

       // STAGE 10 — brain commit (single SQLite transaction; raw Dapper inside).
       var args = new BrainCommitArgs(
           FileId: newFileId, BlobId: newBlobId, ParentId: parentFileId,
           Name: name, Extension: typeResult.Extension, MimeType: typeResult.MimeType,
           VirtualPath: virtualPath, PlaintextSize: plaintextSize, EncryptedSize: encWritten,
           PlaintextSha256: sha256Hex, EncryptedXxHash: encryptedXxHashHex,
           Compression: flags == BlobFlags.CompressedLz4 ? "LZ4"
                       : flags == BlobFlags.CompressedZstd ? "ZSTD" : null,
           BlobPath: ToRelativeBlobPath(blobPath, context.SkinkRoot),
           NowUtc: DateTime.UtcNow,
           FilenameForActivity: name);

       var commitResult = await CommitBrainAsync(context, args, scope, ct);
       if (!commitResult.Success) { LogAndPublish(...); return Result<WriteReceipt>.Fail(commitResult.Error!); }

       // Success. scope is now COMMITTED; DisposeAsync becomes a no-op.
       return Result<WriteReceipt>.Ok(new WriteReceipt {
           FileId = newFileId, BlobId = newBlobId, Status = WriteStatus.Written,
           PlaintextSize = plaintextSize, EncryptedSize = encWritten,
           MimeType = typeResult.MimeType, Extension = typeResult.Extension,
       });
   }
   finally
   {
       compressed?.Dispose();
   }

X. Outer catch ordering (wraps stages 1–10):
   1. catch (OperationCanceledException ex) → ErrorCode.Cancelled
   2. catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) → ErrorCode.StagingFailed
   3. catch (Exception ex) → ErrorCode.Unknown
```

Notes:

- All `Result.Fail` returns through the outer catch are *also* logged via `_logger.LogError`
  and published via `context.NotificationBus.PublishAsync` (Severity = Error, Source =
  "WritePipeline", Title = "Could not save file", Message includes `virtualPath` but never
  blob bytes / DEK / AAD — Principle 26). The `LogAndPublish` private helper centralises
  this; it never throws. `OperationCanceledException` is logged at `LogInformation` and *not*
  published (Principle 14 — cancellation is not a fault).
- The `await using var scope` ensures rollback runs whether we exit via return, exception, or
  cancellation. If `commitResult.Success` is true, `scope.CompleteAsync` (called inside
  `CommitBrainAsync`) flips `_completed = true`, making `DisposeAsync` a no-op.
- `_walScopeLogger` is a `ILogger<WriteWalScope>` field on `WritePipeline`; injected via the
  constructor (revised: actually injected as a separate parameter to `ExecuteAsync` would be
  awkward; the simpler design is for `WritePipeline` to receive an `ILoggerFactory` once and
  derive `ILogger<WriteWalScope>` from it). **Decision:** add `ILoggerFactory loggerFactory`
  to the `WritePipeline` constructor; derive `_walScopeLogger = loggerFactory.CreateLogger<WriteWalScope>()`
  in the constructor. Tests pass `NullLoggerFactory.Instance`.

### `WritePipeline.CommitBrainAsync` — brain transaction body

```
1. SqliteTransaction tx = ctx.BrainConnection.BeginTransaction();
   try
   {
       // INSERT INTO Blobs
       await ctx.BrainConnection.ExecuteAsync(new CommandDefinition(
           "INSERT INTO Blobs (BlobID, EncryptedSize, PlaintextSize, PlaintextSHA256, " +
           "EncryptedXXHash, Compression, BlobPath, CreatedUtc, SoftDeletedUtc, PurgeAfterUtc) " +
           "VALUES (@BlobId, @EncryptedSize, @PlaintextSize, @PlaintextSha256, " +
           "@EncryptedXxHash, @Compression, @BlobPath, @CreatedUtc, NULL, NULL)",
           new { args.BlobId, args.EncryptedSize, args.PlaintextSize, args.PlaintextSha256,
                 args.EncryptedXxHash, args.Compression, args.BlobPath,
                 CreatedUtc = args.NowUtc.ToString("O") },
           tx, cancellationToken: ct));

       // INSERT INTO Files
       await ctx.BrainConnection.ExecuteAsync(new CommandDefinition(
           "INSERT INTO Files (FileID, ParentID, IsFolder, IsSymlink, SymlinkTarget, Name, " +
           "Extension, MimeType, VirtualPath, SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc, BlobID) " +
           "VALUES (@FileId, @ParentId, 0, 0, NULL, @Name, @Extension, @MimeType, @VirtualPath, " +
           "@SizeBytes, @Now, @Now, @Now, @BlobId)",
           new { args.FileId, args.ParentId, args.Name, args.Extension, args.MimeType,
                 args.VirtualPath, SizeBytes = args.PlaintextSize,
                 Now = args.NowUtc.ToString("O"), args.BlobId },
           tx, cancellationToken: ct));

       // INSERT INTO TailUploads — INSERT-SELECT against active providers (zero rows in Phase 2)
       await ctx.BrainConnection.ExecuteAsync(new CommandDefinition(
           "INSERT INTO TailUploads (FileID, ProviderID, Status, QueuedUtc, AttemptCount) " +
           "SELECT @FileId, ProviderID, 'PENDING', @Now, 0 " +
           "FROM Providers WHERE IsActive = 1",
           new { args.FileId, Now = args.NowUtc.ToString("O") },
           tx, cancellationToken: ct));

       // INSERT INTO ActivityLog
       await ctx.BrainConnection.ExecuteAsync(new CommandDefinition(
           "INSERT INTO ActivityLog (EntryID, OccurredUtc, Category, Summary, Detail) " +
           "VALUES (@EntryId, @Now, @Category, @Summary, NULL)",
           new { EntryId = Guid.NewGuid().ToString("N"), Now = args.NowUtc.ToString("O"),
                 Category = ActivityCategory,
                 Summary = $"Saved file '{args.FilenameForActivity}'" },  // Principle 25 — no
                                                                          // appliance vocabulary
           tx, cancellationToken: ct));

       // WAL transition COMMITTED — inside the same tx, via WriteWalScope.CompleteAsync(transaction: tx)
       var transitionResult = await scope.CompleteAsync(transaction: tx, ct: CancellationToken.None);
       // Principle 17 — ct passed as the literal CancellationToken.None to CompleteAsync;
       // CompleteAsync further does not forward ct anywhere internally. Documented at the
       // call site.
       if (!transitionResult.Success)
       {
           tx.Rollback();
           return transitionResult;
       }

       tx.Commit();
       return Result.Ok();
   }
   catch (OperationCanceledException ex)
   {
       tx.Rollback();
       _logger.LogInformation("Brain commit cancelled for file {VirtualPath}", args.VirtualPath);
       return Result.Fail(ErrorCode.Cancelled, "Write cancelled before brain commit completed.", ex);
   }
   catch (SqliteException ex) when (ex.IsUniqueConstraintViolation())
   {
       tx.Rollback();
       _logger.LogError(ex, "Path conflict committing write of {VirtualPath}", args.VirtualPath);
       return Result.Fail(ErrorCode.PathConflict,
           $"A file or folder named '{args.Name}' already exists at this location.", ex);
   }
   catch (SqliteException ex)
   {
       tx.Rollback();
       _logger.LogError(ex, "Database error committing write of {VirtualPath}", args.VirtualPath);
       return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to commit write to brain.", ex);
   }
   catch (Exception ex)
   {
       tx.Rollback();
       _logger.LogError(ex, "Unexpected error committing write of {VirtualPath}", args.VirtualPath);
       return Result.Fail(ErrorCode.Unknown, "Unexpected error committing write to brain.", ex);
   }
   finally
   {
       tx.Dispose();
   }
```

### Notification publish — `LogAndPublish` helper

```csharp
private async ValueTask LogAndPublish(VolumeContext ctx, string virtualPath, ErrorContext err)
{
    if (err.Code == ErrorCode.Cancelled) { return; } // Principle 14
    _logger.LogError("Write of '{VirtualPath}' failed: {Code} {Message}",
        virtualPath, err.Code, err.Message);
    var n = new Notification {
        Source = SourceTag,
        Severity = NotificationSeverity.Error,
        Title = "Could not save file",
        Message = $"FlashSkink could not save '{virtualPath}'.",
        Error = err,
        RequiresUserAction = false,
    };
    try { await ctx.NotificationBus.PublishAsync(n, CancellationToken.None); }
    catch (Exception pubEx) { _logger.LogWarning(pubEx, "Notification publish failed."); }
    // CancellationToken.None — once we are publishing the failure, do not abort it because
    // the original operation was cancelled (Principle 17). And we never propagate a publish
    // exception — a failed bus must not mask the underlying write failure.
}
```

`virtualPath` is user-supplied content. It is included in the user-facing `Message` and in
`ErrorContext.Metadata` only via existing `ErrorContext` fields; this PR adds no
`Metadata["BlobId"]` / `Metadata["DEK"]` / etc. — Principle 26 lint rule passes by
construction.

### `WalRepository.TransitionAsync` — modified body

The change is purely the addition of the optional parameter and forwarding it to the
`CommandDefinition` constructor. No behavioural change when the parameter is null. The
existing catch ordering is preserved.

### `WriteWalScope.CompleteAsync` — modified body

```csharp
public async Task<Result> CompleteAsync(
    SqliteTransaction? transaction = null,
    CancellationToken ct = default)
{
    if (_completed) { return Result.Ok(); }

    var transition = await _wal.TransitionAsync(
        _walId, "COMMITTED", CancellationToken.None, transaction)
        .ConfigureAwait(false);
    if (!transition.Success)
    {
        _logger.LogError(
            "Failed to transition WAL row {WalId} to COMMITTED: {Code} {Message}. " +
            "Phase 5 recovery will reconcile.",
            _walId, transition.Error?.Code, transition.Error?.Message);
        return transition;
    }

    _completed = true;
    return Result.Ok();
}
```

`ct` is still not forwarded — Principle 17 stands. `transaction` is forwarded as the new
fourth parameter.

## Integration points

From prior PRs (consumed unchanged unless listed in "Files to modify"):

- `FlashSkink.Core.Abstractions.Results.Result`, `Result<T>`, `ErrorCode`, `ErrorContext`
  (§1.1) — codes used by §2.5: `Cancelled`, `FileTooLong`, `PathConflict`, `BlobCorrupt`,
  `DatabaseWriteFailed`, `EncryptionFailed`, `UsbFull`, `StagingFailed`, `Unknown`.
- `FlashSkink.Core.Abstractions.Notifications.{INotificationBus, Notification, NotificationSeverity}`
  (§2.3).
- `FlashSkink.Core.Buffers.ClearOnDisposeOwner` (§2.2; internal — same assembly, OK).
- `FlashSkink.Core.Crypto.{BlobHeader, BlobFlags, CryptoPipeline}` (§1.4; `CryptoPipeline.Encrypt`
  signature change in this PR — see "Files to modify").
- `FlashSkink.Core.Engine.{FileTypeService, EntropyDetector, FileTypeResult, CompressionService}`
  (§2.1, §2.2).
- `FlashSkink.Core.Storage.{AtomicBlobWriter, WriteWalScope}` (§2.4; `WriteWalScope.CompleteAsync`
  signature change in this PR — see "Files to modify").
- `FlashSkink.Core.Metadata.{BlobRepository, FileRepository, WalRepository, ActivityLogRepository,
  WalRow}` (§1.6; `WalRepository.TransitionAsync` signature change in this PR — see "Files to
  modify"). Used signatures consumed by `WritePipeline`:
  - `BlobRepository.GetByPlaintextHashAsync(string plaintextSha256, CancellationToken ct)`
  - `FileRepository.EnsureFolderPathAsync(string virtualPath, CancellationToken ct)`
- `Microsoft.Data.Sqlite.SqliteConnection`, `SqliteTransaction`, `SqliteException` — BCL.
- `Dapper.SqlMapper.ExecuteAsync` extension method on `SqliteConnection`.
- `Microsoft.IO.RecyclableMemoryStream.RecyclableMemoryStreamManager` — new NuGet here.
- `System.IO.Hashing.XxHash64.HashToUInt64(ReadOnlySpan<byte>)` — new NuGet here.
- `System.Security.Cryptography.IncrementalHash` — BCL.
- `Microsoft.Extensions.Logging.{ILogger<T>, ILoggerFactory}` (Abstractions only — Principle 28).

## Principles touched

- **Principle 1** — `WritePipeline.ExecuteAsync` returns `Result<WriteReceipt>`; never throws.
- **Principle 4** — Phase 1 commit returns success only when the brain transaction commits;
  per-tail rows are queued PENDING (zero rows in Phase 2; the SQL still runs against
  `Providers` so Phase 3 wires up without a code path change).
- **Principle 6** — every blob written carries the AAD-bound encryption (`BlobID || PlaintextSHA256`)
  per cross-cutting decision 3.
- **Principle 7** — staging is rooted at `[skinkRoot]/.flashskink/staging/` via
  `AtomicBlobWriter`; `Path.GetTempPath()` is not referenced by `WritePipeline` or
  `VolumeContext`. (Used only in *test* code.)
- **Principle 13** — every async public method takes `CancellationToken ct` last.
  `CompleteAsync`'s second parameter is `ct = default`; the new `transaction` parameter is
  *third* in `WalRepository.TransitionAsync` (after `ct`) — see Drift Note 1.
- **Principle 14** — `OperationCanceledException` is the first catch in `ExecuteAsync` and
  `CommitBrainAsync`; mapped to `ErrorCode.Cancelled` and logged at `Information`.
- **Principle 15** — granular catch ladder: `OperationCanceledException`, then `IOException`/
  `UnauthorizedAccessException` (via `when` clauses where helpful), then `SqliteException`
  (with `IsUniqueConstraintViolation()` filter for `PathConflict`), then `Exception`.
- **Principle 16** — every failure path disposes: `compressed?.Dispose()` in `finally`;
  `using var encrypted`; `using var plaintextBuffer`; `await using var scope` (with
  `DisposeAsync` running rollback when not completed); `tx.Dispose()` in `CommitBrainAsync`'s
  `finally`.
- **Principle 17** — `CancellationToken.None` literal at every compensation site:
  `LogAndPublish`'s `PublishAsync` call, `CompleteAsync(transaction: tx, ct: CancellationToken.None)`,
  the existing `WriteWalScope.DisposeAsync` calls (unchanged from §2.4).
- **Principle 18** — pooled `ClearOnDisposeOwner` for plaintext, compressed, and encrypted
  buffers; `stackalloc byte[16]` for header; `stackalloc byte[32]` for SHA-256 digest;
  `stackalloc byte[48]` for AAD; raw `byte[]` slices via `Span<byte>` throughout. No
  `new MemoryStream()` in the hot path (the `RecyclableMemoryStream` is only used for
  unknown-length sources — tested explicitly).
- **Principle 19** — every method that produces a buffer returns `IMemoryOwner<byte>`; the
  caller disposes via `using`/`await using`.
- **Principle 20** — `stackalloc` spans are consumed synchronously; never crossed across
  `await`. Specifically: header read uses a private synchronous helper for the seekable case;
  for the non-seekable case the span is copied into `headerBufArray = headerBuf.ToArray()`
  before any await (a tiny 16-byte allocation that the C# compiler would otherwise reject for
  the cross-await use). The AAD `stackalloc byte[48]` is used inside the synchronous
  `Crypto.Encrypt` call before any await; same for the `stackalloc byte[32]` digest.
- **Principle 21** — `RecyclableMemoryStream` is the only growing-stream type used; no
  `new MemoryStream()` anywhere in this PR.
- **Principle 22** — brain inserts are issued via Dapper (`_connection.ExecuteAsync(new CommandDefinition(...))`)
  per the §2.5 dev plan note ("the brain transaction inserts are issued via Dapper (not raw
  reader) — these are general queries, not hot-path scans"). The hash lookup in stage 2 also
  uses Dapper via the existing `BlobRepository.GetByPlaintextHashAsync`.
- **Principle 24** — every failure path in `WritePipeline.ExecuteAsync` (excluding
  cancellation) logs via `ILogger<WritePipeline>` and publishes a `Notification` with
  `Severity = Error`; `PersistenceNotificationHandler` (§2.3) persists to
  `BackgroundFailures` for `Error` and `Critical` severities.
- **Principle 25** — every user-facing string in this PR (`Title = "Could not save file"`,
  `Message = $"FlashSkink could not save '{virtualPath}'."`,
  `Summary = $"Saved file '{name}'"`) avoids appliance vocabulary. No "blob"/"WAL"/"DEK"/
  "stripe" in any user-visible output.
- **Principle 26** — DEK bytes are never logged or placed in `ErrorContext.Metadata`. The
  `virtualPath` used in messages is user content (intentional). `Notification.Error` carries
  `ErrorContext` from inner calls; this PR adds no metadata keys matching the forbidden
  list.
- **Principle 27** — `WritePipeline` logs once at the `Result.Fail` construction site; the
  caller (§2.7 `FlashSkinkVolume.WriteFileAsync`, future) is responsible for any further
  logging of the returned `ErrorContext`.
- **Principle 29** — atomic file-level write delegated to `AtomicBlobWriter` (§2.4). No new
  on-disk-write protocol introduced here.
- **Principle 30** — `WriteWalScope` (§2.4) plus `CommitBrainAsync`'s rollback semantics
  preserve the §21.3 invariant for the new "brain insert fails" interleaving. The new
  `[Property]` test in `WriteCrashConsistencyTests.cs` asserts this across crash-at-step-N
  for N ∈ [1..6] (the §2.4 5-step model is extended with step 6 = "brain insert fails after
  WriteAsync succeeded").
- **Principle 32** — no telemetry, no update checks; `WritePipeline` is purely local.

## Test spec

Test data ownership: tests author their own byte arrays inline; no `[InternalsVisibleTo]`
added. Compression-result and hash-equality assertions reference `BlobFlags.None` /
`BlobFlags.CompressedLz4` / `BlobFlags.CompressedZstd` directly (these are public).

### `tests/FlashSkink.Tests/Engine/VolumeContextTests.cs`

**Class: `VolumeContextTests`**

- `Construct_ExposesAllInjectedFields` — assert every getter returns the injected value.
- `Dispose_DisposesIncrementalHashAndCompressionService` — pass spies; after
  `context.Dispose()`, both spies report disposed.
- `Dispose_IsIdempotent` — dispose twice; spies report exactly one dispose each.
- `Dispose_DoesNotDisposeBrainConnection` — pass an open `SqliteConnection`; after
  `context.Dispose()`, the connection is still open. (Lifetime owned by `VolumeSession`.)
- `MaxPlaintextBytes_EqualsArrayMaxLength` — assert `VolumeContext.MaxPlaintextBytes == (long)Array.MaxLength`. (Re-export sanity check.)

### `tests/FlashSkink.Tests/Engine/WritePipelineTests.cs`

**Class: `WritePipelineTests`** (`IAsyncLifetime` + `IDisposable`)

Setup: per-test temp directory under `Path.GetTempPath()` (test-only — production never uses
host temp); in-memory SQLite via `BrainTestHelper.CreateInMemoryConnection()` +
`BrainTestHelper.ApplySchemaAsync()`; a 32-byte all-zeros DEK; a real `WritePipeline`,
`AtomicBlobWriter`, `CompressionService`, `CryptoPipeline`, `IncrementalHash`,
`RecyclableMemoryStreamManager`. The `INotificationBus` is a recording test double
(`RecordingNotificationBus`) defined inline in the test file: implements `INotificationBus`,
captures published `Notification`s in a `List<Notification>`. Loggers are
`NullLogger<T>.Instance`.

Skink layout helper: each test creates the `[skinkRoot]/.flashskink/staging` and
`[skinkRoot]/.flashskink/blobs` directories before invoking the pipeline. Cleanup via
`Directory.Delete(skinkRoot, recursive: true)` in `Dispose`.

**Happy-path round-trip tests:**

- `ExecuteAsync_SmallTextFile_ProducesFilesBlobsAndActivityLogRows` — write a 200-byte
  in-memory ASCII payload to `/notes/hello.txt`. Assert the returned receipt has
  `Status = Written`, non-empty `FileId` and `BlobId`, `PlaintextSize = 200`,
  `EncryptedSize = 200 + 20 + 16 = 236` (header + ciphertext + tag), `Extension = ".txt"`,
  `MimeType = "text/plain"`. Assert exactly one row in `Files`, exactly one row in `Blobs`,
  exactly one row in `ActivityLog`, zero rows in `TailUploads`. Assert the blob file exists
  on disk at the sharded path.

- `ExecuteAsync_AutoCreatesIntermediateFolders_IdempotentOnRepeatedWrites` — first write to
  `/a/b/c/file.txt`, then write `/a/b/d/file2.txt`. Assert `Files` contains rows for `a`,
  `b`, `c`, `d`, `file.txt`, `file2.txt` with `IsFolder` set correctly.

- `ExecuteAsync_RootLevelFile_ParentIdIsNull` — write `/root.txt`. Assert the file's
  `ParentID` is `NULL`.

**Compression branch tests:**

- `ExecuteAsync_HighlyCompressible100KB_UsesLz4_BlobsCompressionEqualsLZ4` — write 100 KB
  of `0xAB`. Assert `Blobs.Compression == "LZ4"` and `EncryptedSize < PlaintextSize +
  HeaderSize + TagSize`. Assert the blob file's first 6 bytes contain `BlobFlags.CompressedLz4`
  in the flags field of the on-disk header.

- `ExecuteAsync_HighlyCompressible1MB_UsesZstd_BlobsCompressionEqualsZSTD` — write 1 MB of
  `0xCD`. Assert `Blobs.Compression == "ZSTD"`.

- `ExecuteAsync_RandomBytes1MB_NoGain_BlobsCompressionIsNull` — write 1 MB seeded
  `Random(42).NextBytes(...)`. Assert `Blobs.Compression IS NULL` and `EncryptedSize ==
  PlaintextSize + HeaderSize + TagSize`.

- `ExecuteAsync_BlobHeaderFlagsMatchCompression_OnDiskRoundTrip` — write a 100 KB
  compressible payload, read the on-disk blob bytes, parse the header via
  `BlobHeader.Parse`, assert `flags == BlobFlags.CompressedLz4`. (Audits the
  `CryptoPipeline.Encrypt(flags)` change.)

**Change-detection short-circuit tests:**

- `ExecuteAsync_SameContentSamePath_SecondCallReturnsUnchanged` — write the same 200-byte
  payload twice to the same path. Assert the second call returns
  `Status = WriteStatus.Unchanged` with the same `BlobId` as the first call. Assert no new
  `Blobs` row was created (still 1 row); no second `Files` row; no new `ActivityLog` row
  written by the short-circuit (the dev plan explicitly skips the brain commit on the
  Unchanged path).

- `ExecuteAsync_SameContentDifferentPath_WritesNewBlobAndFile` — write 200-byte payload to
  `/a.txt`, then to `/b.txt`. Assert both `Files` rows exist; `Blobs` count is 2 (V1: no
  cross-path dedup); both `BlobId`s differ.

- `ExecuteAsync_DifferentContentSamePath_WritesNewBlobAndOverwritesFile` — for V1 the
  contract is "writes a new blob with a new BlobId; the old Files row is left in place
  unless the caller explicitly deletes first" (no in-place update in §2.5). The acceptance
  criterion to verify: a second `Files` row insert at the same VirtualPath should hit the
  unique-name-per-parent constraint and return `ErrorCode.PathConflict`. This test asserts
  that. **This documents an expected edge case**: in §2.7 the volume API may add an
  overwrite mode; for §2.5 the pipeline does not.

**Cancellation tests:**

- `ExecuteAsync_CancelledBeforeStart_ReturnsCancelled` — pre-cancelled token; assert
  `ErrorCode.Cancelled`; assert no on-disk blob, no `Files` / `Blobs` / `WAL` row.

- `ExecuteAsync_CancelledMidLargeWrite_LeavesNoOrphans` — cancel mid-stream with a 100 KB
  payload using a `CancellationTokenSource` that fires when stage 1 has read 50 KB
  (mechanism: a custom `Stream` wrapper that signals the CTS after N bytes have been read
  via `ReadAsync`). Assert `ErrorCode.Cancelled`; assert no staging file, no destination
  blob, no `Files` row, and the WAL row (if one was created) is in `FAILED` phase.

**FileTooLong enforcement:**

- `ExecuteAsync_SeekableSourceLongerThanCap_ReturnsFileTooLong_BeforeAllocation` — pass a
  `Stream` whose `CanSeek = true` and `Length = MaxPlaintextBytes + 1`. (Implementation: a
  custom `Stream` that lies about Length and never returns bytes from `Read`.) Assert
  `ErrorCode.FileTooLong`, no allocation observed via a recording `MemoryPool<byte>` test
  pool — actually the BCL's `MemoryPool<byte>.Shared` is opaque; instead assert the call
  returns within (e.g.) 100 ms wall-clock, which would be impossible if the pipeline tried
  to materialise 4 GiB in memory. Pragmatic and sufficient for the criterion.

- `ExecuteAsync_NonSeekableSourceExceedsCap_ReturnsFileTooLong_DisposesBuffer` — a
  non-seekable counting `Stream` that returns `MaxPlaintextBytes + 1` bytes total via many
  `ReadAsync` chunks. Assert `ErrorCode.Cancelled` is *not* returned; `FileTooLong` is
  returned; assert the test's recording wrapper around `RecyclableMemoryStreamManager`
  observes the growing stream was disposed before return.

**Brain-tx-failure / fault injection:**

- `ExecuteAsync_BrainConnectionClosedDuringCommit_RollsBackAndMarksWalFailed` — open the
  pipeline successfully through the AtomicBlobWriter call (the `MarkRenamed` step), then
  close the brain `SqliteConnection` (the test owns the connection and can reach in via the
  context); invoke a *second* write whose brain commit will fail. Assert
  `Result.Success == false` with `Code == DatabaseWriteFailed`. Assert no orphan staging
  file. Assert the WAL row (`WALID` from the second write's payload) is in phase
  `'FAILED'`. Assert the destination blob file from the second write was deleted (because
  `MarkRenamed` had been set when the failure occurred). The on-disk blob from the first
  write is left intact (its commit succeeded).

  Implementation note: closing a live connection mid-test is awkward. A cleaner mechanism:
  inject a *test-only* `WritePipeline` constructor overload (or a tx callback) that lets
  the test trigger a `SqliteException` after `tx.Commit()` would have been called. The
  simplest portable approach: use a `Stream` source whose path conflicts with an existing
  row (creates a UNIQUE constraint violation at the `INSERT INTO Files` step). This
  organically produces a `SqliteException` inside the brain tx and exercises the rollback
  path without requiring fault-injection plumbing. **Decision:** use the
  unique-constraint-collision approach. The test name becomes
  `ExecuteAsync_PathConflictDuringCommit_RollsBackAndMarksWalFailed`.

- `ExecuteAsync_PathConflictDuringCommit_PublishesNotification` — same setup as the above
  test; use a `RecordingNotificationBus`; assert exactly one `Notification` was published
  with `Source == "WritePipeline"`, `Severity == Error`, `Title == "Could not save file"`,
  `Error.Code == ErrorCode.PathConflict`. Assert the message contains the virtual path
  string but does NOT contain the blob ID, the SHA-256, or the word "blob"/"WAL"/"DEK"
  (Principle 25 + 26 audit).

**Notification-not-published-on-cancellation test:**

- `ExecuteAsync_Cancellation_DoesNotPublishNotification` — pre-cancel the token; verify the
  `RecordingNotificationBus` captured zero notifications.

**XXHash64 sanity:**

- `ExecuteAsync_BlobsEncryptedXxHashMatchesXxHashOfOnDiskBlob` — write a payload, then
  recompute `XxHash64.HashToUInt64` over the on-disk blob bytes and assert
  `Blobs.EncryptedXXHash == that.ToString("x16")`. (Audits stage 8 — the bit-rot signal is
  consistent.)

**`LogAndPublish` doesn't propagate publish failures:**

- `ExecuteAsync_FailingNotificationBus_StillReturnsOriginalFailure` — `RecordingNotificationBus`
  with `ThrowOnPublish = true`; cause a `PathConflict`. Assert the returned `Result`'s code
  is still `PathConflict` (not `Unknown` or `BusFailed`); assert the test logger captured
  the warning about the failed publish.

### `tests/FlashSkink.Tests/Crypto/CryptoPipelineTests.cs` (modifications)

Each existing `Encrypt(plaintext, dek, aad, owner, out written)` call gets a
`BlobFlags.None` argument inserted in the new fourth position. Add three new tests:

- `Encrypt_WithCompressedLz4Flag_HeaderEncodesFlag` — encrypt with `BlobFlags.CompressedLz4`,
  parse the on-disk header via `BlobHeader.Parse`, assert returned flags == `CompressedLz4`.
- `Encrypt_WithCompressedZstdFlag_HeaderEncodesFlag` — same with `CompressedZstd`.
- `EncryptThenDecrypt_FlagsRoundTrip` — encrypt with `CompressedLz4`, decrypt, assert
  `Decrypt`'s `out flags` == `CompressedLz4`.

### `tests/FlashSkink.Tests/Storage/WriteWalScopeTests.cs` (one new test)

- `CompleteAsync_WithTransaction_TransitionParticipatesInTx` — open scope; begin a SQLite
  transaction; call `CompleteAsync(transaction: tx)`; rollback the tx; query the WAL row;
  assert `Phase == "PREPARE"` (the COMMITTED transition was rolled back). Without the new
  parameter, the transition would have auto-committed independently of the test's tx.

### `tests/FlashSkink.Tests/Metadata/WalRepositoryTests.cs` (one new test)

- `TransitionAsync_WithTransaction_RolledBack_LeavesPhaseUnchanged` — insert WAL row in
  PREPARE; begin tx; call `TransitionAsync(walId, "COMMITTED", ct, transaction: tx)`;
  rollback; query the row; assert `Phase == "PREPARE"`.

### `tests/FlashSkink.Tests/CrashConsistency/WriteCrashConsistencyTests.cs` (one new property)

- `WritePipelineCrash_AtAnyStep_PreservesInvariant` — extends the §2.4 5-step model to 6
  steps where step 6 is "brain INSERT INTO Files fails (UNIQUE constraint)". For each step
  N ∈ [1..6], simulate a crash at step N, dispose the scope, and assert the §21.3
  invariant still holds: every `Files` row references an existing `Blobs` row whose blob
  exists on disk; every WAL row whose phase is not COMMITTED has a recovery path that
  restores the invariant. `MaxTest = 200` per the §2.4 convention.

  Implementation re-uses the §2.4 in-test `FaultyAtomicBlobWriter`, plus a new
  `FaultyWritePipeline` that pre-seeds a colliding `Files` row before invoking
  `WritePipeline.ExecuteAsync` (forcing step 6 to throw a UNIQUE-constraint
  `SqliteException`).

## Acceptance criteria

- [ ] Builds with zero warnings on `ubuntu-latest` and `windows-latest`
- [ ] All new tests pass; all existing Phase 0 / Phase 1 / §2.1–§2.4 tests still pass
- [ ] `dotnet format --verify-no-changes` clean
- [ ] `WritePipeline.ExecuteAsync` exists in `FlashSkink.Core.Engine` with the signature
      listed above
- [ ] `VolumeContext` exists in `FlashSkink.Core.Engine` with the public surface listed
      above; is `IDisposable`; disposes only the volume-scoped instances it constructed
- [ ] `WriteReceipt` and `WriteStatus` exist with the shapes listed above
- [ ] `CryptoPipeline.Encrypt` accepts `BlobFlags flags`; on-disk header encodes the flag
      (round-trip via `BlobHeader.Parse`)
- [ ] `WalRepository.TransitionAsync` accepts an optional `SqliteTransaction? transaction`
- [ ] `WriteWalScope.CompleteAsync` accepts an optional `SqliteTransaction? transaction`
      and forwards it
- [ ] On a successful write: exactly one new row each in `Blobs`, `Files`, `ActivityLog`;
      zero new rows in `TailUploads` (no providers configured in Phase 2); WAL row
      transitioned to `COMMITTED`
- [ ] On a write whose brain commit raises a UNIQUE-constraint violation: WAL row in
      `FAILED`, staging file absent, destination blob file absent
- [ ] Change-detection short-circuit returns `WriteStatus.Unchanged` on identical second
      write to the same path; no second `Blobs` row, no new `ActivityLog` row
- [ ] `Blobs.Compression` is `"LZ4"` for the LZ4 branch, `"ZSTD"` for the Zstd branch,
      `NULL` for the no-gain branch
- [ ] `Blobs.EncryptedXXHash` matches `XxHash64.HashToUInt64` over the on-disk blob bytes
- [ ] AAD passed to `CryptoPipeline.Encrypt` is exactly 48 bytes — 16-byte raw GUID
      followed by 32-byte raw SHA-256 digest (cross-cutting decision 3) — verified by a
      test that flips the AAD ordering and asserts the resulting blob fails decryption
- [ ] `FileTooLong` returned when the source advertises (`CanSeek` path) or produces
      (non-seekable path) more than 4 GiB
- [ ] Cancellation returns `ErrorCode.Cancelled`; no notification published on cancellation
- [ ] Failure notifications obey Principle 25 (no `blob`, `WAL`, `DEK`, `stripe`,
      `PRAGMA` in `Title` / `Message`)
- [ ] No new `ErrorCode` values added (cross-cutting decision 4)
- [ ] `Path.GetTempPath()` is not referenced in any file under `src/FlashSkink.Core/Engine/`
      (Principle 7) — only in test code
- [ ] `docs/error-handling.md` updated with the `WritePipeline` failure-rollback worked
      example
- [ ] CI `plan-check` passes (this file exists with all required headings and `§` blueprint
      citations)

## Line-of-code budget

### Non-test
- `src/FlashSkink.Core/Engine/VolumeContext.cs` — ~140 lines
- `src/FlashSkink.Core/Engine/WriteReceipt.cs` — ~25 lines
- `src/FlashSkink.Core/Engine/WriteStatus.cs` — ~12 lines
- `src/FlashSkink.Core/Engine/WritePipeline.cs` — ~480 lines
- `src/FlashSkink.Core/Crypto/CryptoPipeline.cs` modifications — net ~+5 lines
- `src/FlashSkink.Core/Metadata/WalRepository.cs` modifications — net ~+3 lines
- `src/FlashSkink.Core/Storage/WriteWalScope.cs` modifications — net ~+5 lines
- `src/FlashSkink.Core/FlashSkink.Core.csproj` — net ~+2 lines
- `Directory.Packages.props` — net ~+1 line
- **Total non-test: ~670 lines** (mostly in `WritePipeline.cs`)

### Test
- `tests/FlashSkink.Tests/Engine/WritePipelineTests.cs` — ~620 lines
- `tests/FlashSkink.Tests/Engine/VolumeContextTests.cs` — ~80 lines
- `tests/FlashSkink.Tests/Crypto/CryptoPipelineTests.cs` modifications — net ~+50 lines
- `tests/FlashSkink.Tests/Storage/WriteWalScopeTests.cs` modifications — ~+40 lines
- `tests/FlashSkink.Tests/Metadata/WalRepositoryTests.cs` modifications — ~+25 lines
- `tests/FlashSkink.Tests/CrashConsistency/WriteCrashConsistencyTests.cs` modifications —
  ~+140 lines
- **Total test: ~955 lines**

### Docs
- `docs/error-handling.md` — net ~+60 lines

## Drift notes

1. **`WalRepository.TransitionAsync` parameter ordering.** Principle 13 says
   `CancellationToken ct` is *always last*. The new `SqliteTransaction? transaction = null`
   parameter is placed *after* `ct` in this PR (`TransitionAsync(walId, newPhase, ct,
   transaction: null)`). Rationale: putting `transaction` before `ct` would force every
   existing caller to either insert `transaction: null,` positionally or switch to named
   arguments. Both are noisy. Putting `transaction` *after* `ct` keeps every existing
   call-site signature working unchanged and matches the precedent set by
   `WalRepository.InsertAsync(WalRow row, SqliteTransaction? transaction = null,
   CancellationToken ct = default)` — which already has `transaction` *before* `ct`.
   Principle 13's strict reading is violated; the spirit (every async public method accepts
   `ct`) is preserved. **Reviewer sign-off requested at Gate 1.** If the reviewer prefers
   the strict ordering, this PR will instead reorder `InsertAsync`'s parameters to put `ct`
   last (a one-line edit per call-site, no behavioural change).

2. **`WriteWalScope.CompleteAsync` parameter ordering.** The new `SqliteTransaction?
   transaction = null` parameter is placed *first*, ahead of `ct = default`. This *does*
   honour Principle 13 strictly. Existing call sites
   (`WriteWalScopeTests.CompleteAsync_TransitionsToCommitted` etc.) pass no arguments at all
   (`await scope.CompleteAsync()`); the addition is purely additive. Documenting the
   asymmetry with drift note 1 because a reader will notice the ordering differs across the
   two changed APIs.

3. **`EnsureFolderPathAsync` runs *outside* the brain commit transaction.** Dev plan §2.5
   stage 8 lists `EnsureFolderPathAsync` as a step inside the single SQLite transaction
   alongside the Blobs / Files / TailUploads / ActivityLog inserts. The on-disk
   `FileRepository.EnsureFolderPathAsync` does *not* accept a transaction parameter, and
   adding one would cascade through several internal Dapper call sites within a method
   that's already complex. This PR runs `EnsureFolderPathAsync` *before* opening the brain
   tx. Folder creation is idempotent; an orphaned folder from a failed file-insert is
   harmless (a future write to the same folder reuses it; cleanup is unnecessary). The §2.7
   `DeleteFolderAsync(confirmed: false)` will still complain about an empty folder, but
   that's already the existing semantic. The acceptance criterion "auto-creates intermediate
   folders" is preserved by this drift; the only observable difference is that a brain-tx
   rollback does not roll back the folder creates. Documented here so a future reader
   understands why the tx body is what it is. **Alternative considered and rejected:** add a
   `transaction:` parameter to `EnsureFolderPathAsync` in a §1.6 modification — out of
   §2.5's scope by the principle of "additive-only modifications to upstream PRs".

4. **`WriteWalScope.CompleteAsync` is called *inside* the brain tx, but the existing scope
   construction also issues a separate WAL `INSERT` outside any tx.** The §2.4
   `WriteWalScope.OpenAsync` calls `wal.InsertAsync(walRow, transaction: null, ct)`, so the
   PREPARE row is auto-committed before the on-disk write happens. This is intentional and
   unchanged: the PREPARE row exists on disk before the staging write so a crash between
   open and commit produces a recoverable WAL row. The COMMITTED transition rides inside
   the brain tx; if the tx rolls back, the WAL row stays in PREPARE, and §21.2 recovery
   inspects on-disk + brain state to decide whether to mark FAILED or COMMITTED. This is
   the §13.4 / §21 design exactly. No drift here — calling out the asymmetry for clarity.

5. **`docs/error-handling.md` worked example replaces, not extends, the §1.5 one.** The
   §1.5 / §1.6 worked example covered `BrainConnectionFactory.CreateAsync`. This PR appends
   a sibling section for `WritePipeline.ExecuteAsync`'s rollback path; the §1.5 section is
   left in place. The phase-2 acceptance bullet ("`docs/error-handling.md` is updated with
   the `WritePipeline` failure-rollback worked example (a natural sequel to the §1.5
   `BrainConnectionFactory` example)") is satisfied as a sibling example, not a
   replacement.

## Non-goals

- Do NOT implement `ReadPipeline` — that is §2.6.
- Do NOT implement `FlashSkinkVolume.WriteFileAsync` or any other volume-public API — those
  are §2.7. This PR's tests construct `WritePipeline` and `VolumeContext` directly.
- Do NOT register any service in DI — Phase 6 wires DI in host projects.
- Do NOT implement the streaming chunk-through compress/encrypt path — V1 reads the
  plaintext into a single buffer (deferred per the dev plan).
- Do NOT add a `GetByVirtualPathAsync` to `FileRepository` — the change-detection
  short-circuit uses an inline Dapper one-liner. §2.7 may add it when `FlashSkinkVolume`
  needs it.
- Do NOT implement brain-mirror-to-tails (§16.7) — Phase 3.
- Do NOT enqueue or dequeue tail uploads — Phase 3. Phase 2 only inserts `TailUploads` rows
  with `Status = 'PENDING'` (zero rows when no providers exist).
- Do NOT add new `ErrorCode` values (cross-cutting decision 4).
- Do NOT add `[InternalsVisibleTo]` for the test project. `ClearOnDisposeOwner` is reachable
  from `WritePipeline` because both live in `FlashSkink.Core` (same assembly); tests do not
  reference `ClearOnDisposeOwner` directly.
- Do NOT touch `BLUEPRINT.md` or `CLAUDE.md`. Cross-cutting decision 3 already pinned the
  AAD format in the dev plan and asks for a §13.6 update "in the same PR that introduces
  it" — but on inspection the blueprint §13.6 *already* documents the 48-byte raw AAD
  (lines 1429–1443 of the file), so no edit is required. Listed here to record the check.
- Do NOT modify `INotificationBus` or `INotificationHandler` — consumed unchanged from §2.3.
- Do NOT add per-provider TailUploads inserts iteratively — the single
  `INSERT … SELECT … FROM Providers WHERE IsActive = 1` covers Phase 2 (zero matching rows)
  *and* Phase 3+ (one row per active provider) without any code-path change.
