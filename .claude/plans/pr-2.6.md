# PR 2.6 — Read pipeline

**Branch:** pr/2.6-read-pipeline
**Blueprint sections:** §14.2, §13.6, §13.7, §9.5, §9.8
**Dev plan section:** phase-2 §2.6

## Scope

Delivers the `ReadPipeline` end-to-end: brain lookup → blob open → full-blob read → AES-GCM
decrypt (header parse is internal to `CryptoPipeline.Decrypt` per §1.4) → optional decompress
→ plaintext SHA-256 verify → copy to caller's destination stream. Returns `Result` and never
throws across the boundary (Principle 1). The caller (§2.7 `FlashSkinkVolume.ReadFileAsync`)
holds the volume serialization gate before invoking, so the volume-scoped `IncrementalHash`,
`CryptoPipeline`, and `CompressionService` on `VolumeContext` are single-threaded for the
duration.

Stage flow (§14.2):

1. **Brain lookup** — resolve `virtualPath` → `Files` row via `FileRepository`; resolve
   `BlobID` → `Blobs` row via `BlobRepository`. Reject up-front when the file row is missing
   (`FileNotFound`), the file is a folder (`FileNotFound` — folders cannot be read as files),
   the blob row is missing (`BlobCorrupt` — extended scope per cross-cutting decision 4), or
   `PlaintextSize > VolumeContext.MaxPlaintextBytes` (`FileTooLong`).
2. **Blob open** — open the sharded path computed via
   `AtomicBlobWriter.ComputeDestinationPath(skinkRoot, BlobID)`. Missing file →
   `BlobCorrupt`. Other I/O → `Unknown`.
3. **Full-blob read** — read the entire encrypted blob into a single pooled
   `ClearOnDisposeOwner` sized exactly to `Blobs.EncryptedSize`, in 4 MB chunks (§9.5). The
   header is part of the read; `CryptoPipeline.Decrypt` parses it internally (§13.6).
4. **Decrypt** — `CryptoPipeline.Decrypt` with AAD per cross-cutting decision 3 (16-byte raw
   GUID `||` 32-byte raw SHA-256 digest, 48 bytes, in a managed `byte[48]`). Output:
   ciphertext-length-sized pooled buffer (the post-compression payload, or the plaintext
   directly when `BlobFlags.None`). Failures: `DecryptionFailed` (GCM tag mismatch),
   `VolumeCorrupt` (header magic / unknown flag bits), `VolumeIncompatibleVersion`.
5. **Decompress (conditional)** — when flags are non-`None`, allocate a pooled
   plaintext-sized buffer and call `CompressionService.Decompress(payload, flags,
   plaintextSize=Blobs.PlaintextSize, destination, out written)`. Failures:
   `BlobCorrupt` (illegal flag combination, decoded-length mismatch, codec error),
   `FileTooLong` (cap re-checked inside `Decompress` before allocation).
6. **Hash verify** — `IncrementalHash` (volume-scoped) over the decompressed plaintext;
   `TryGetHashAndReset` into a 32-byte managed buffer; lowercase hex compare against
   `Blobs.PlaintextSHA256`. Mismatch → `ChecksumMismatch`.
7. **Copy to destination** — `destination.WriteAsync(plaintext, ct)` once, in the chosen
   chunk size, only after stage 6 has succeeded. No partial writes on verification failure.

`Blobs.EncryptedXXHash` is **not** verified on this path (dev-plan §2.6 explicit). It is the
audit signal owned by the future Phase 5 `AuditService`; the GCM tag in stage 4 already
detects ciphertext tampering.

The PR also adds one repository method to land the brain-lookup contract that §2.6 needs and
that §2.7 will reuse:

1. `FileRepository.GetByVirtualPathAsync(string virtualPath, CancellationToken ct)` —
   resolves `virtualPath` to a `Files` row or `null` on no-match. Mirrors `GetByIdAsync`
   in shape and catch ladder. Additive; no existing call site changes. (Drift Note 1.)

## Files to create

### Production — `src/FlashSkink.Core/Engine/`
- `ReadPipeline.cs` — sealed class; the §14.2 orchestrator; ~330 lines.

### Tests — `tests/FlashSkink.Tests/Engine/`
- `ReadPipelineTests.cs` — round-trips against a real `WritePipeline`-produced blob;
  tamper tests at every layer (header magic, GCM tag, ciphertext, decompressed plaintext);
  cancellation; FileTooLong; FileNotFound; missing-blob-on-disk; folder-as-file;
  notification-severity audits. ~600 lines.

## Files to modify

- `src/FlashSkink.Core/Metadata/FileRepository.cs` — add `GetByVirtualPathAsync`
  (~30 lines added). XML doc, full catch ladder, mirrors `GetByIdAsync`.
- `tests/FlashSkink.Tests/Metadata/FileRepositoryTests.cs` — add four tests for
  `GetByVirtualPathAsync` (~80 lines added).

## Dependencies

- NuGet: none new. (`System.Security.Cryptography.IncrementalHash` and
  `System.Buffers.MemoryPool<byte>` are BCL; everything else is consumed unchanged from
  prior PRs.)
- Project references: none new.

## Drift notes

**Drift Note 1 — `FileRepository.GetByVirtualPathAsync` is added in §2.6.** The §2.5 plan
(stage 2 change-detection short-circuit) anticipated this method but used an inline Dapper
query because §2.7 was nominally where it landed. §2.6's brain lookup is the first stage
that *requires* a clean `VirtualPath → VolumeFile` resolution; adding the method here keeps
`ReadPipeline` at the repository boundary (Principle 22 says general queries → Dapper, which
this method dispatches to internally) and gives §2.7 a method already on disk. The
inline query in `WritePipeline.ExecuteAsync` is left as-is (different shape — it joins
`VirtualPath` and `BlobID`); it can be migrated in §2.7 if desired but is not in scope here.

**Drift Note 2 — Folder-target read returns `FileNotFound`, not a new code.** When the
caller passes a `virtualPath` that resolves to a folder (`Files.IsFolder = 1`), the pipeline
returns `Result.Fail(ErrorCode.FileNotFound, "...")`. Cross-cutting decision 4 forbids new
codes; `FileNotFound` is the closest existing code that fits "the file you asked for is not
a file at this path." The user-facing message disambiguates ("That path is a folder, not a
file."). An alternative — `UnsupportedFileType` — was rejected because that code is
reserved for pipeline-side rejections of file content; a folder is a brain-shape mismatch,
not a content mismatch.

**Drift Note 3 — Missing-on-disk and unknown-flag-bits both map to `BlobCorrupt`.** Per
cross-cutting decision 4, `BlobCorrupt`'s scope is extended to local blob-format errors.
`BlobHeader.Parse` already returns `VolumeCorrupt` for unknown flag bits and bad magic; the
read pipeline propagates that code unchanged (the PR does not relabel parser failures). For
*missing* on-disk blobs (Files row references a BlobID whose `.bin` file is absent) the
pipeline returns `BlobCorrupt`. The two failures are reachable by callers and are
*intentionally* distinct: `VolumeCorrupt` says the wrapper is malformed (parse failure);
`BlobCorrupt` says the brain row promises a blob the disk can no longer produce. Phase 5
self-healing distinguishes them.

## Public API surface

### `FlashSkink.Core.Engine.ReadPipeline` (public sealed class)

Summary intent: orchestrates §14.2 stages 1–7 for a single read. Returns `Result` (no
typed value — the plaintext is streamed to the caller's destination). Never throws across
the boundary (Principle 1).

```csharp
namespace FlashSkink.Core.Engine;

public sealed class ReadPipeline
{
    public ReadPipeline(ILoggerFactory loggerFactory);

    public Task<Result> ExecuteAsync(
        string virtualPath,
        Stream destination,
        VolumeContext context,
        CancellationToken ct);
}
```

Constructor takes only a logger factory; all other dependencies (repositories, crypto,
compression, skink root, DEK view, hasher) come from the `VolumeContext` parameter on each
call. This matches `WritePipeline`'s shape.

### Modified — `FlashSkink.Core.Metadata.FileRepository`

Adds one method:

```csharp
/// <summary>
/// Returns the <see cref="VolumeFile"/> at the given virtual path, or a successful result
/// with <see langword="null"/> value when no matching row exists.
/// </summary>
public Task<Result<VolumeFile?>> GetByVirtualPathAsync(string virtualPath, CancellationToken ct);
```

Mirrors `GetByIdAsync` exactly: same `try`/`catch (OperationCanceledException ex)` /
`catch (SqliteException ex)` / `catch (Exception ex)` ladder; same error codes
(`Cancelled`, `DatabaseReadFailed`, `Unknown`); same `MapFile` helper. SQL:

```sql
SELECT FileID, ParentID, IsFolder, IsSymlink, SymlinkTarget, Name, Extension,
       MimeType, VirtualPath, SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc, BlobID
FROM Files
WHERE VirtualPath = @VirtualPath
```

`VirtualPath` is unique-indexed in the `Files` table (§16.4); the query naturally returns
zero or one row. No `LIMIT 1` needed (kept for parity with the existing
`GetByPlaintextHashAsync` style, but optional).

## Internal types

### `ReadPipeline` private constants

- `private const string SourceTag = "ReadPipeline";` — `Notification.Source` value.
- `private const int IoChunkSize = 4 * 1024 * 1024;` — 4 MB read chunk for blob open
  (§9.5). Used by the blob read loop, not by the destination write (which writes the
  buffered plaintext in one call).

### `ReadPipeline` private helpers

- `private static void BuildAad(byte[] aad48, Guid blobId, ReadOnlySpan<byte> plaintextSha256)` —
  identical to `WritePipeline.BuildAad`. Writes 16 raw GUID bytes followed by 32 raw
  SHA-256 bytes into a 48-byte managed array. (Duplicating the helper is acceptable — both
  pipelines own their AAD construction; centralising it would create a third type whose
  sole purpose is two callers, which the project guidance explicitly rejects.)
- `private static Guid TryParseBlobGuid(string blobId)` — wraps `Guid.ParseExact(blobId, "N")`;
  returns `Guid.Empty` on failure (a defensive belt-and-braces — `BlobID` always comes from
  `Guid.NewGuid().ToString("N")` upstream, so a parse failure indicates brain corruption).
  Caller treats `Guid.Empty` as a `BlobCorrupt` result.
- `private static byte[] HexDecodeSha256(string hex)` — `Convert.FromHexString(hex)` with a
  guard for the expected 64-char input; returns a 32-byte array. Failure (wrong length /
  non-hex) → throw `InvalidDataException`, caught by the pipeline's outer fall-through and
  mapped to `BlobCorrupt`. Used to convert `Blobs.PlaintextSHA256` (stored hex) into the
  raw bytes the AAD requires.
- `private async ValueTask<Result<IMemoryOwner<byte>>> ReadBlobAsync(string blobPath, long expectedSize, CancellationToken ct)` —
  opens the file, asserts `fs.Length == expectedSize` (corruption check; mismatch →
  `BlobCorrupt`), reads in `IoChunkSize` chunks into a `ClearOnDisposeOwner` of size
  `expectedSize`. Returns `BlobCorrupt` on `FileNotFoundException` /
  `DirectoryNotFoundException`, `Unknown` on other `IOException`,
  `Cancelled` on `OperationCanceledException`. Disposes the rented owner on any failure.

## Method-body contracts

### `ReadPipeline.ExecuteAsync` — full stage flow

Outer structure: one `try { ... }` covering stages 1–7, with the same catch ladder as
`WritePipeline.ExecuteAsync`:

1. `catch (OperationCanceledException ex)` → `Result.Fail(ErrorCode.Cancelled, "The read was cancelled.", ex)`.
   Logged at `LogInformation`. NOT published (Principle 14).
2. `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)` →
   `Result.Fail(ErrorCode.Unknown, "An I/O error occurred while reading from the skink.", ex)`.
   Published at `Severity = Error` via `LogAndPublishAsync(..., severity: Error)`.
3. `catch (Exception ex)` → `Result.Fail(ErrorCode.Unknown, "An unexpected error occurred while reading the file.", ex)`.
   Published at `Severity = Error`.

```text
0. ct.ThrowIfCancellationRequested();

1. STAGE 1 — brain lookup:
   var fileResult = await context.Files.GetByVirtualPathAsync(virtualPath, ct);
   if (!fileResult.Success)
       return await FailWithLogAndPublishAsync(context, virtualPath, fileResult.Error!, severity: Error, ct);
   if (fileResult.Value is not { } fileRow)
       return await FailWithLogAndPublishAsync(
           context, virtualPath,
           new ErrorContext { Code = FileNotFound, Message = "No file exists at that path." },
           severity: Error, ct);
   if (fileRow.IsFolder)
       return await FailWithLogAndPublishAsync(
           context, virtualPath,
           new ErrorContext { Code = FileNotFound, Message = "That path is a folder, not a file." },
           severity: Error, ct);
   if (fileRow.BlobId is null)
       return await FailWithLogAndPublishAsync(
           context, virtualPath,
           new ErrorContext { Code = BlobCorrupt, Message = "The file has no blob reference." },
           severity: Error, ct);

   var blobResult = await context.Blobs.GetByIdAsync(fileRow.BlobId, ct);
   if (!blobResult.Success)
       return await FailWithLogAndPublishAsync(context, virtualPath, blobResult.Error!, severity: Error, ct);
   if (blobResult.Value is not { } blobRow)
       return await FailWithLogAndPublishAsync(
           context, virtualPath,
           new ErrorContext { Code = BlobCorrupt, Message = "The brain references a blob that no longer exists." },
           severity: Error, ct);

   if (blobRow.PlaintextSize > VolumeContext.MaxPlaintextBytes)
       return await FailWithLogAndPublishAsync(
           context, virtualPath,
           new ErrorContext { Code = FileTooLong,
               Message = $"The file is too large; the maximum supported size is {VolumeContext.MaxPlaintextBytes} bytes." },
           severity: Error, ct);

2. STAGE 2-3 — blob open + full read:
   string blobPath = AtomicBlobWriter.ComputeDestinationPath(context.SkinkRoot, blobRow.BlobId);
   var blobReadResult = await ReadBlobAsync(blobPath, blobRow.EncryptedSize, ct);
   if (!blobReadResult.Success)
       return await FailWithLogAndPublishAsync(context, virtualPath, blobReadResult.Error!, severity: Error, ct);
   using var encryptedBuffer = blobReadResult.Value!;

3. STAGE 4 — decrypt:
   // Build AAD: 16-byte raw GUID || 32-byte raw SHA-256 digest. The GUID is parsed from
   // the BlobID hex string; the SHA-256 is hex-decoded from the brain row.
   Guid blobGuid = TryParseBlobGuid(blobRow.BlobId);
   if (blobGuid == Guid.Empty)
       return await FailWithLogAndPublishAsync(
           context, virtualPath,
           new ErrorContext { Code = BlobCorrupt, Message = "The blob identifier is malformed." },
           severity: Error, ct);

   byte[] expectedSha256;
   try { expectedSha256 = HexDecodeSha256(blobRow.PlaintextSha256); }
   catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
   {
       return await FailWithLogAndPublishAsync(
           context, virtualPath,
           new ErrorContext { Code = BlobCorrupt,
               Message = "The brain's plaintext digest is malformed.",
               ExceptionType = ex.GetType().FullName, ExceptionMessage = ex.Message },
           severity: Error, ct);
   }

   byte[] aadArray = new byte[48];
   BuildAad(aadArray, blobGuid, expectedSha256);

   // Output buffer for Decrypt is the post-compression payload — sized as ciphertext length.
   int ciphertextLength = checked((int)blobRow.EncryptedSize - BlobHeader.HeaderSize - BlobHeader.TagSize);
   if (ciphertextLength < 0)
       return await FailWithLogAndPublishAsync(
           context, virtualPath,
           new ErrorContext { Code = BlobCorrupt, Message = "The blob is too small to contain a header and tag." },
           severity: Error, ct);

   using var payloadBuffer = ClearOnDisposeOwner.Rent(ciphertextLength == 0 ? 1 : ciphertextLength);
   var decryptResult = context.Crypto.Decrypt(
       encryptedBuffer.Memory.Span,
       context.Dek.Span,
       aadArray,
       payloadBuffer,
       out BlobFlags flags,
       out int payloadLen);

   if (!decryptResult.Success)
   {
       // GCM tag mismatch — Critical, RequiresUserAction = true (dev plan §2.6).
       // Header parse failures (VolumeCorrupt / VolumeIncompatibleVersion) — Error.
       NotificationSeverity sev = decryptResult.Error!.Code == ErrorCode.DecryptionFailed
           ? NotificationSeverity.Critical
           : NotificationSeverity.Error;
       bool requiresAction = decryptResult.Error.Code == ErrorCode.DecryptionFailed;
       return await FailWithLogAndPublishAsync(
           context, virtualPath, decryptResult.Error!,
           severity: sev, requiresUserAction: requiresAction, ct);
   }

4. STAGE 5 — decompress (conditional):
   if (blobRow.PlaintextSize > VolumeContext.MaxPlaintextBytes)
       /* re-checked above; cannot reach here */;

   IMemoryOwner<byte> plaintextOwner;
   int plaintextWritten;
   if (flags == BlobFlags.None)
   {
       // Direct path — payload IS the plaintext. Verify size matches Blobs.PlaintextSize.
       if (payloadLen != blobRow.PlaintextSize)
           return await FailWithLogAndPublishAsync(
               context, virtualPath,
               new ErrorContext { Code = BlobCorrupt,
                   Message = $"Plaintext length {payloadLen} does not match recorded size {blobRow.PlaintextSize}." },
               severity: Error, ct);
       plaintextOwner = payloadBuffer.MoveOwnership();   // see "Internal types" → MoveOwnership note below
       plaintextWritten = payloadLen;
   }
   else
   {
       // Compressed — decompress into a freshly rented plaintext-sized buffer.
       int plaintextSizeInt = checked((int)blobRow.PlaintextSize);
       var plaintext = ClearOnDisposeOwner.Rent(plaintextSizeInt == 0 ? 1 : plaintextSizeInt);
       try
       {
           var decompressResult = context.Compression.Decompress(
               payloadBuffer.Memory[..payloadLen],
               flags,
               blobRow.PlaintextSize,
               plaintext,
               out plaintextWritten);
           if (!decompressResult.Success)
           {
               plaintext.Dispose();
               return await FailWithLogAndPublishAsync(
                   context, virtualPath, decompressResult.Error!, severity: Error, ct);
           }
           if (plaintextWritten != plaintextSizeInt)
           {
               plaintext.Dispose();
               return await FailWithLogAndPublishAsync(
                   context, virtualPath,
                   new ErrorContext { Code = BlobCorrupt,
                       Message = $"Decompressed length {plaintextWritten} does not match recorded size {plaintextSizeInt}." },
                   severity: Error, ct);
           }
           plaintextOwner = plaintext;
       }
       catch
       {
           plaintext.Dispose();
           throw;
       }
   }

   using (plaintextOwner)
   {
5. STAGE 6 — hash verify:
       _ = context.Sha256.GetHashAndReset();   // defensive reset
       context.Sha256.AppendData(plaintextOwner.Memory.Span[..plaintextWritten]);
       byte[] computed = new byte[32];
       if (!context.Sha256.TryGetHashAndReset(computed, out int hashWritten) || hashWritten != 32)
           return await FailWithLogAndPublishAsync(
               context, virtualPath,
               new ErrorContext { Code = ErrorCode.Unknown, Message = "SHA-256 hash read failed." },
               severity: Error, ct);

       if (!CryptographicOperations.FixedTimeEquals(computed, expectedSha256))
       {
           return await FailWithLogAndPublishAsync(
               context, virtualPath,
               new ErrorContext { Code = ErrorCode.ChecksumMismatch,
                   Message = "The file's contents do not match its recorded fingerprint. The skink may be damaged." },
               severity: NotificationSeverity.Critical, requiresUserAction: true, ct);
       }

6. STAGE 7 — copy to destination (only after hash verify passed):
       await destination.WriteAsync(plaintextOwner.Memory[..plaintextWritten], ct).ConfigureAwait(false);
   }   // end using (plaintextOwner) — ClearOnDispose zeros plaintext

   return Result.Ok();
```

### `ReadPipeline.ReadBlobAsync` — body sketch

```csharp
private async ValueTask<Result<IMemoryOwner<byte>>> ReadBlobAsync(
    string blobPath, long expectedSize, CancellationToken ct)
{
    if (expectedSize < 0 || expectedSize > VolumeContext.MaxPlaintextBytes + BlobHeader.HeaderSize + BlobHeader.TagSize)
        return Result<IMemoryOwner<byte>>.Fail(ErrorCode.BlobCorrupt,
            $"Recorded encrypted size {expectedSize} is out of range.");

    int sizeInt = checked((int)expectedSize);
    var owner = ClearOnDisposeOwner.Rent(sizeInt == 0 ? 1 : sizeInt);
    try
    {
        await using var fs = new FileStream(
            blobPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan,
            });

        if (fs.Length != expectedSize)
        {
            owner.Dispose();
            return Result<IMemoryOwner<byte>>.Fail(ErrorCode.BlobCorrupt,
                $"On-disk blob size {fs.Length} does not match recorded size {expectedSize}.");
        }

        int total = 0;
        while (total < sizeInt)
        {
            int request = Math.Min(IoChunkSize, sizeInt - total);
            int read = await fs.ReadAsync(owner.Memory.Slice(total, request), ct).ConfigureAwait(false);
            if (read == 0) { break; }
            total += read;
        }

        if (total != sizeInt)
        {
            owner.Dispose();
            return Result<IMemoryOwner<byte>>.Fail(ErrorCode.BlobCorrupt,
                $"Read {total} bytes but expected {sizeInt}.");
        }

        return Result<IMemoryOwner<byte>>.Ok(owner);
    }
    catch (OperationCanceledException ex)
    {
        owner.Dispose();
        return Result<IMemoryOwner<byte>>.Fail(ErrorCode.Cancelled, "Blob read cancelled.", ex);
    }
    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
    {
        owner.Dispose();
        return Result<IMemoryOwner<byte>>.Fail(ErrorCode.BlobCorrupt,
            "The blob file is missing from the skink.", ex);
    }
    catch (IOException ex)
    {
        owner.Dispose();
        return Result<IMemoryOwner<byte>>.Fail(ErrorCode.Unknown,
            "I/O error reading the blob from the skink.", ex);
    }
    catch (UnauthorizedAccessException ex)
    {
        owner.Dispose();
        return Result<IMemoryOwner<byte>>.Fail(ErrorCode.Unknown,
            "Access denied reading the blob from the skink.", ex);
    }
    catch (Exception ex)
    {
        owner.Dispose();
        return Result<IMemoryOwner<byte>>.Fail(ErrorCode.Unknown,
            "Unexpected error reading the blob from the skink.", ex);
    }
}
```

### `ReadPipeline.LogAndPublishAsync` — analogue of §2.5's helper

Identical shape to `WritePipeline.LogAndPublishAsync`, with two additions:

```csharp
private async ValueTask LogAndPublishAsync(
    VolumeContext ctx, string virtualPath, ErrorContext err,
    NotificationSeverity severity, bool requiresUserAction, CancellationToken ct)
{
    if (err.Code == ErrorCode.Cancelled) { /* log Information; do not publish */ return; }
    _logger.LogError(
        "Read of '{VirtualPath}' failed: {Code} — {Message}", virtualPath, err.Code, err.Message);

    var notification = new Notification
    {
        Source = SourceTag,
        Severity = severity,
        Title = severity == NotificationSeverity.Critical
            ? "File integrity check failed"
            : "Could not open file",
        Message = severity == NotificationSeverity.Critical
            ? $"FlashSkink could not verify the contents of '{virtualPath}'. The skink may be damaged. Try restoring this file from a backup tail."
            : $"FlashSkink could not open '{virtualPath}'.",
        Error = err,
        RequiresUserAction = requiresUserAction,
    };

    try { await ctx.NotificationBus.PublishAsync(notification, CancellationToken.None).ConfigureAwait(false); }
    catch (Exception pubEx)
    {
        _logger.LogWarning(pubEx,
            "Notification publish failed after read error for '{VirtualPath}'.", virtualPath);
    }
}
```

A small private convenience wrapper `FailWithLogAndPublishAsync(...)` calls
`LogAndPublishAsync` and returns `Result.Fail(err)` — keeps stage code shape compact.

### `MoveOwnership` note

`ClearOnDisposeOwner` does not currently expose a public ownership-transfer method; the
"plaintext = payload" branch needs to either:

(a) **Decision (chosen):** copy the payload bytes into a fresh `ClearOnDisposeOwner` of
exactly `plaintextSize` bytes, then dispose the payload buffer. The duplicate copy is
bounded by `MaxPlaintextBytes` and is the cleanest model — no internal API change to
`ClearOnDisposeOwner` (which is `internal sealed`). The hot-path cost is one buffer copy
on the no-compression path; given the read pipeline will already have decrypted into
`payloadBuffer`, the second copy is acceptable for V1 simplicity.

(b) Rejected: add a `MoveOwnership()` method to `ClearOnDisposeOwner`. Touches §1.4-era
code for one caller; the cleanup-of-state mechanics (resetting `_inner` and the length)
are subtle. Defer to a profiling-driven optimisation PR.

The plan's stage 5 sketch above is updated to reflect (a):

```csharp
if (flags == BlobFlags.None)
{
    if (payloadLen != blobRow.PlaintextSize)
        /* BlobCorrupt — see above */;
    int plaintextSizeInt = checked((int)blobRow.PlaintextSize);
    var copy = ClearOnDisposeOwner.Rent(plaintextSizeInt == 0 ? 1 : plaintextSizeInt);
    payloadBuffer.Memory[..plaintextSizeInt].CopyTo(copy.Memory);
    plaintextOwner = copy;
    plaintextWritten = plaintextSizeInt;
}
```

`payloadBuffer` is still disposed via the surrounding `using` declaration.

## Integration points

From prior PRs (consumed unchanged unless listed in "Files to modify"):

- `FlashSkink.Core.Abstractions.Results.{Result, Result<T>, ErrorCode, ErrorContext}` (§1.1).
  Codes used: `Cancelled`, `FileNotFound`, `FileTooLong`, `BlobCorrupt`, `VolumeCorrupt`,
  `VolumeIncompatibleVersion`, `DecryptionFailed`, `ChecksumMismatch`,
  `DatabaseReadFailed`, `Unknown`. No new codes (cross-cutting decision 4).
- `FlashSkink.Core.Abstractions.Notifications.{INotificationBus, Notification, NotificationSeverity}` (§2.3).
  `NotificationSeverity.Critical` is consumed for the first time in this PR (the §2.3 plan
  declared it; §2.5 only used `Error`).
- `FlashSkink.Core.Abstractions.Models.{VolumeFile, BlobRecord}` (§1.6).
- `FlashSkink.Core.Buffers.ClearOnDisposeOwner` (§2.2; internal — same assembly, OK).
- `FlashSkink.Core.Crypto.{BlobHeader, BlobFlags, CryptoPipeline}` (§1.4 + crypto-fix PR).
  `CryptoPipeline.Decrypt` signature consumed:
  `Result Decrypt(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> dek, ReadOnlySpan<byte> aad,
   IMemoryOwner<byte> outputOwner, out BlobFlags flags, out int bytesWritten)`.
- `FlashSkink.Core.Engine.{VolumeContext, CompressionService}` (§2.5, §2.2).
  `CompressionService.Decompress` consumed unchanged.
- `FlashSkink.Core.Storage.AtomicBlobWriter.ComputeDestinationPath` (public static) (§2.4).
- `FlashSkink.Core.Metadata.{FileRepository, BlobRepository}` (§1.6;
  `FileRepository.GetByVirtualPathAsync` added in this PR — see "Files to modify").
- `System.Security.Cryptography.{IncrementalHash, CryptographicOperations}` — BCL.
- `Microsoft.Extensions.Logging.{ILogger<T>, ILoggerFactory}` — Abstractions only (Principle 28).

## Principles touched

- **Principle 1** — `ReadPipeline.ExecuteAsync` returns `Result`; never throws across the
  public boundary. The outer catch ladder maps every exception to a `Result.Fail`.
- **Principle 3** — `ReadPipeline` reads only the local blob from the skink; no tail
  access. Verified by the test `ExecuteAsync_DoesNotTouchTailFolders` (asserts no I/O
  outside `[skinkRoot]/.flashskink/blobs/...`).
- **Principle 6** — AAD-bound decryption ensures any tampered brain row (BlobID swap,
  PlaintextSHA256 swap) fails GCM authentication.
- **Principle 7** — no `Path.GetTempPath()` in the pipeline; only the test harness uses
  it (test isolation).
- **Principle 12** — OS-agnostic — no platform-specific paths or APIs in the pipeline.
- **Principle 13** — `ExecuteAsync` takes `CancellationToken ct` last.
- **Principle 14** — `OperationCanceledException` is the first catch in `ExecuteAsync`
  and `ReadBlobAsync`; mapped to `ErrorCode.Cancelled`; logged at `Information`; NOT
  published.
- **Principle 15** — granular catch ladder: `OperationCanceledException`, then
  `IOException`/`UnauthorizedAccessException` (where applicable), then `Exception`.
- **Principle 16** — every failure path disposes: `using var encryptedBuffer`,
  `using var payloadBuffer`, `using (plaintextOwner)`. `ReadBlobAsync` disposes its
  rented `owner` on every catch. The `flags == None` branch's freshly-rented `copy` is
  disposed via the outer `using (plaintextOwner)`.
- **Principle 17** — `CancellationToken.None` literal at every compensation site:
  `LogAndPublishAsync`'s `PublishAsync` call, the dispose-on-failure cleanups (which
  don't accept a token).
- **Principle 18** — pooled buffers everywhere; `byte[48]` for AAD (managed because it
  is consumed across an `await` boundary into `Decrypt`, which is synchronous but is
  reached from an async outer context — the safer pattern is the managed array as in
  `WritePipeline`); `byte[32]` for hash digest (same reason). `IoChunkSize = 4 MB`
  follows §9.5. Allocations are bounded by `MaxPlaintextBytes`.
- **Principle 19** — `IMemoryOwner<byte>` ownership is explicit at every stage; the
  caller of `ReadBlobAsync` disposes the returned owner via `using`.
- **Principle 20** — no `stackalloc` crosses an `await` boundary in this PR. The AAD
  and hash digest buffers are managed `byte[]` arrays, intentionally — the pipeline is
  async and the buffers must survive across awaits (the buffer for SHA-256 verification
  spans the `destination.WriteAsync` await; the AAD spans the file-open await).
- **Principle 21** — no `new MemoryStream()` in the pipeline (the read path doesn't need
  growing streams; `ClearOnDisposeOwner` is sized exactly).
- **Principle 22** — `FileRepository.GetByVirtualPathAsync` uses Dapper (general
  query, not hot-path scan).
- **Principle 24** — every failure path (excluding cancellation) logs via
  `ILogger<ReadPipeline>` and publishes a `Notification` to the bus. Severity is
  `Critical` for `DecryptionFailed` and `ChecksumMismatch` (with
  `RequiresUserAction = true`); `Error` for everything else.
- **Principle 25** — every notification `Title` and `Message` uses user vocabulary —
  "file", "skink", "backup", "fingerprint" — and avoids "blob", "WAL", "DEK", "AAD",
  "GCM", "stripe", "PRAGMA". Verified by a test that scans every published
  `Notification.Title` and `Notification.Message` in the test suite for forbidden words.
- **Principle 26** — DEK bytes are never logged or placed in `ErrorContext.Metadata`.
  Plaintext bytes are never logged. The hex-decoded SHA-256 (`expectedSha256`) is
  compared in constant time via `CryptographicOperations.FixedTimeEquals` and never
  logged. No `Metadata` keys matching `*Token`, `*Key`, `*Password`, `*Secret`,
  `*Mnemonic`, or `*Phrase` are added.
- **Principle 27** — `ReadPipeline` logs once at the `Result.Fail` construction site;
  the future §2.7 `FlashSkinkVolume.ReadFileAsync` is responsible for any further
  logging of the returned `ErrorContext`.
- **Principle 32** — pipeline makes zero outbound network calls.

## Test spec

Test data ownership: tests author their own byte arrays inline. No `[InternalsVisibleTo]`
added. The `RecordingNotificationBus` test double from §2.5's
`WritePipelineTests.cs` is reused by extracting it into a small shared file
`tests/FlashSkink.Tests/Engine/RecordingNotificationBus.cs` (move + minor visibility
adjustment from `internal` to `internal` in the new file — same class, two consumers).

### `tests/FlashSkink.Tests/Engine/ReadPipelineTests.cs`

**Class: `ReadPipelineTests`** (`IAsyncLifetime` + `IDisposable`)

Setup mirrors `WritePipelineTests.cs`:
- per-test temp directory under `Path.GetTempPath()` (test-only — Principle 7 honoured by
  production code, not tests),
- in-memory SQLite via `BrainTestHelper.CreateInMemoryConnection()` +
  `BrainTestHelper.ApplySchemaAsync()`,
- 32-byte all-zeros DEK,
- a real `WritePipeline` and a real `ReadPipeline` (so round-trip tests exercise the
  on-disk format produced by the canonical write path),
- `RecordingNotificationBus`, `NullLogger<T>.Instance`,
- pre-creates `[skinkRoot]/.flashskink/staging` and `[skinkRoot]/.flashskink/blobs`.

Each test typically writes a payload via `WritePipeline.ExecuteAsync`, then reads back via
`ReadPipeline.ExecuteAsync` into a `MemoryStream`, then asserts.

**Happy-path round-trip tests:**

- `ExecuteAsync_SmallTextFile_RoundTripsBytes` — write 200-byte ASCII to
  `/notes/hello.txt`; read into a `MemoryStream`; assert the destination buffer matches
  the original input byte-for-byte; assert `Result.Success`.

- `ExecuteAsync_5MBRandomBytes_RoundTripsBytes` — write 5 MB random bytes (seeded
  `Random(42)`); read back; byte-equality. Audits the multi-chunk read loop in
  `ReadBlobAsync`.

- `ExecuteAsync_LargeMostlyZeros_RoundTripsBytes` — write 2 MB of all-zeros (Zstd path);
  read back; byte-equality. Audits the Zstd decompression branch.

- `ExecuteAsync_HighlyCompressible100KB_RoundTripsBytes_ViaLz4` — write 100 KB of
  `0xAB`; read back; byte-equality. Audits LZ4 branch.

- `ExecuteAsync_NoGainPayload_RoundTripsBytes` — write 1 MB random bytes (no-gain
  rejection); read back; byte-equality. Audits `BlobFlags.None` branch.

- `ExecuteAsync_EmptyFile_RoundTripsZeroBytes` — write zero bytes; read into
  `MemoryStream`; assert `result.Success` and `destination.Length == 0`.

- `ExecuteAsync_RootLevelFile_ReadByPath` — write to `/root.txt`; read by virtual path;
  byte-equality.

- `ExecuteAsync_NestedPath_ResolvesCorrectly` — write to `/a/b/c/file.txt`; read by
  same path; byte-equality.

**Brain-shape errors:**

- `ExecuteAsync_VirtualPathNotFound_ReturnsFileNotFound` — read a path that was never
  written. Assert `ErrorCode.FileNotFound`, `Severity = Error`, and the published
  message contains the virtual path but does not contain the word "blob" or "WAL".

- `ExecuteAsync_VirtualPathIsFolder_ReturnsFileNotFound` — write a file at
  `/a/b/file.txt` (creates folders `/a` and `/a/b`); attempt to read `/a/b`; assert
  `ErrorCode.FileNotFound` and the message says "folder".

- `ExecuteAsync_FileWithoutBlob_ReturnsBlobCorrupt` — manually insert a Files row whose
  `BlobID` is `NULL`; read by path; assert `ErrorCode.BlobCorrupt`. (Edge case — V1
  doesn't normally produce these, but the read pipeline guards against the brain
  shape.)

- `ExecuteAsync_BlobIdNotInBlobsTable_ReturnsBlobCorrupt` — manually insert a Files
  row pointing at a BlobID that has no matching `Blobs` row; read; assert
  `BlobCorrupt`.

- `ExecuteAsync_PlaintextSizeExceedsCap_ReturnsFileTooLong` — write a small file, then
  manually `UPDATE Blobs SET PlaintextSize = @cap + 1`; read; assert `FileTooLong`.

**On-disk corruption tests:**

- `ExecuteAsync_BlobFileMissing_ReturnsBlobCorrupt` — write a file; delete the on-disk
  blob file; read by path; assert `ErrorCode.BlobCorrupt` and the published
  notification severity is `Error`.

- `ExecuteAsync_BlobFileShorterThanRecorded_ReturnsBlobCorrupt` — write a file; truncate
  the blob to half its size; read; assert `BlobCorrupt`.

- `ExecuteAsync_TamperedHeaderMagic_ReturnsVolumeCorrupt` — write a file; flip the
  first byte of the on-disk blob (corrupts magic `FSBL` → `GSBL`); read; assert
  `VolumeCorrupt`.

- `ExecuteAsync_TamperedHeaderUnknownVersion_ReturnsVolumeIncompatibleVersion` — write a
  file; set blob bytes 4-5 (version field) to `2` (LE); read; assert
  `VolumeIncompatibleVersion`.

- `ExecuteAsync_TamperedHeaderUnknownFlags_ReturnsVolumeCorrupt` — write a file; set
  the high byte of the flags field (offset 7) to `0xFF`; read; assert
  `VolumeCorrupt` (`BlobHeader.Parse` rejects unknown flag bits — fix-blob-header-auth).

- `ExecuteAsync_TamperedCiphertextOneByte_ReturnsDecryptionFailed_AndCriticalNotification` —
  write a file; flip one byte in the ciphertext region (anywhere between offset 20 and
  `EncryptedSize - 16`); read; assert `ErrorCode.DecryptionFailed`, no plaintext written
  to destination, and exactly one published notification with
  `Severity = Critical`, `RequiresUserAction = true`, `Source = "ReadPipeline"`.

- `ExecuteAsync_TamperedTag_ReturnsDecryptionFailed` — flip a byte in the last 16 bytes
  of the blob (the GCM tag); assert `DecryptionFailed`.

- `ExecuteAsync_TamperedBrainSha256_ReturnsDecryptionFailed` — write a file; manually
  `UPDATE Blobs SET PlaintextSHA256 = '<different valid hex>'`; read; assert
  `DecryptionFailed` (AAD mismatch — the SHA-256 is bound into AAD).

- `ExecuteAsync_TamperedBrainBlobId_ReturnsDecryptionFailed_OrRecordsBlobCorrupt` —
  write two files; swap their BlobIDs in the brain (so file A's row points at file B's
  blob); read file A; assert `DecryptionFailed` (the AAD's BlobID component now
  doesn't match the blob's encoded AAD).

- `ExecuteAsync_PlaintextSizeMismatch_AfterCorruption_ReturnsBlobCorrupt` — write a file;
  manually `UPDATE Blobs SET PlaintextSize = PlaintextSize + 1`; read; assert
  `BlobCorrupt` (decompression / length mismatch caught before destination write).

**Hash-verify tests (the hardest path to reach without bypassing GCM):**

- `ExecuteAsync_HashMismatch_AfterDecompressionTampering_ReturnsChecksumMismatch_AndCriticalNotification` —
  the only way to reach `ChecksumMismatch` without first hitting `DecryptionFailed` is
  to construct a blob whose ciphertext-and-AAD pair authenticates correctly but whose
  *decompressed plaintext* does not match the recorded SHA-256. This is achievable in
  practice only by directly constructing such a blob, which means bypassing
  `WritePipeline`. The test:
  1. Writes a 200-byte file via `WritePipeline`.
  2. Reads the brain to get the canonical Plaintext SHA-256 for `payload-A`.
  3. Builds a *new* on-disk blob byte-for-byte using a fresh `CryptoPipeline.Encrypt`
     of `payload-B` (a different 200-byte payload with the same PlaintextSize, e.g. ASCII `B`),
     using the same DEK and AAD constructed from the *original* file's BlobID + the
     SHA-256 of `payload-A` (deliberately wrong — this is the construction).
     The GCM tag will authenticate against this AAD because `Encrypt` builds AAD for what
     the caller passes in, not what the brain says. The result is a forged blob whose
     decryption succeeds but whose plaintext SHA-256 doesn't match the brain row.
  4. Overwrites the on-disk blob with the forged bytes.
  5. Reads via `ReadPipeline`.

  Assert `ErrorCode.ChecksumMismatch`; assert `destination.Length == 0` (no partial
  plaintext written — Principle: stage 6 buffers entirely before stage 7); assert one
  `Severity = Critical`, `RequiresUserAction = true` notification.

  This test is the only one that exercises `FixedTimeEquals` returning `false`. It
  documents the failure surface that PhDr Phase 5 self-healing must handle.

**Cancellation tests:**

- `ExecuteAsync_CancelledBeforeStart_ReturnsCancelled_AndPublishesNothing` — pre-cancelled
  token; assert `ErrorCode.Cancelled`; assert `destination.Length == 0`; assert
  `_bus.Published.Count == 0` (Principle 14).

- `ExecuteAsync_CancelledDuringBlobRead_ReturnsCancelled` — write a 5 MB file; read
  with a `CancellationTokenSource` that fires after the first chunk; assert
  `Cancelled`; assert no published notification.

- `ExecuteAsync_CancelledDuringDestinationWrite_ReturnsCancelled` — write a small
  file; read with a destination stream whose `WriteAsync` triggers `cts.Cancel()` on
  invocation; assert `Cancelled`; the destination receives no further bytes.

**Notification audit tests:**

- `ExecuteAsync_AllPublishedNotifications_AvoidApplianceVocabulary` — runs each error
  path that publishes (`FileNotFound`, `BlobCorrupt`, `DecryptionFailed`,
  `ChecksumMismatch`, `VolumeCorrupt`, `FileTooLong`); collects every published
  `Notification.Title + Message`; asserts the concatenation does not contain
  (case-insensitive) any of: `"blob"`, `"wal"`, `"dek"`, `"aad"`, `"gcm"`, `"stripe"`,
  `"pragma"`, `"sha-256"`, `"sha256"`. (Principle 25 audit.) The user-visible word
  `"fingerprint"` is permitted and substitutes for `"hash"` / `"checksum"` /
  `"sha-256"` in the `ChecksumMismatch` message.

- `ExecuteAsync_DecryptionFailed_PublishesCriticalWithRequiresUserAction` — single-shot
  audit: tamper one byte; assert exactly one notification with
  `Severity == Critical && RequiresUserAction == true`.

- `ExecuteAsync_FailingNotificationBus_StillReturnsOriginalFailure` —
  `RecordingNotificationBus.ThrowOnPublish = true`; cause a `FileNotFound`; assert the
  returned `Result.Error.Code == FileNotFound` (not `Unknown`); assert the test
  logger captured a warning about the failed publish.

**Concurrency-readiness witness:**

- `ExecuteAsync_TwoSequentialReadsOnSameVolume_BothSucceed` — two back-to-back reads
  of the same file via the same `_pipeline` and `_context`; both succeed; both produce
  identical bytes. Audits `IncrementalHash` reset between calls — the dev plan's
  acceptance criterion ("Concurrent ReadFileAsync serialization") requires the §2.7
  semaphore for true concurrency; for §2.6 the witness is sequential.

### `tests/FlashSkink.Tests/Metadata/FileRepositoryTests.cs` (additions)

Append four tests:

- `GetByVirtualPathAsync_ExistingPath_ReturnsRow` — insert a file at `/x/y.txt`;
  query; assert non-null result with `FileId`, `VirtualPath = "/x/y.txt"`,
  `IsFolder = false`.
- `GetByVirtualPathAsync_NonexistentPath_ReturnsNullSuccess` — query a path that
  isn't present; assert `Result.Success == true && Result.Value is null`.
- `GetByVirtualPathAsync_FolderPath_ReturnsFolderRow` — insert a folder at `/a`;
  query; assert non-null result with `IsFolder = true`. (The pipeline rejects folder
  reads at a higher layer — the repository simply returns whatever matches.)
- `GetByVirtualPathAsync_Cancelled_ReturnsCancelled` — pre-cancelled token; assert
  `ErrorCode.Cancelled`.

### `tests/FlashSkink.Tests/Engine/RecordingNotificationBus.cs`

New shared file (extracted verbatim from `WritePipelineTests.cs`). The `internal sealed
class RecordingNotificationBus : INotificationBus` declaration moves here; the
`WritePipelineTests.cs` declaration is removed (one-line edit). Both `WritePipelineTests`
and `ReadPipelineTests` then reference the same type — eliminates a copy-paste twin.

## Acceptance criteria

- [ ] Builds with zero warnings on `ubuntu-latest` and `windows-latest`
- [ ] All new tests pass; all existing Phase 0 / Phase 1 / §2.1–§2.5 tests still pass
- [ ] `dotnet format --verify-no-changes` clean
- [ ] `ReadPipeline.ExecuteAsync` exists in `FlashSkink.Core.Engine` with the signature
      listed above
- [ ] `FileRepository.GetByVirtualPathAsync` exists with the signature listed above
      and the catch ladder matching `GetByIdAsync`
- [ ] `WritePipeline` → `ReadPipeline` round-trip is byte-identical for: 200-byte
      ASCII; 100 KB highly compressible (LZ4 branch); 1 MB highly compressible
      (Zstd branch); 1 MB random bytes (no-gain branch); zero-byte file; 5 MB random
      bytes (multi-chunk read loop)
- [ ] Tampering one byte of ciphertext anywhere between offset 20 and `EncryptedSize - 16`
      → `ErrorCode.DecryptionFailed`, `Severity = Critical`,
      `RequiresUserAction = true`, no plaintext written to destination
- [ ] Tampering the GCM tag → `DecryptionFailed`
- [ ] Tampering the magic in the on-disk header → `VolumeCorrupt`
- [ ] Tampering the version → `VolumeIncompatibleVersion`
- [ ] Tampering an unknown flag bit in the header → `VolumeCorrupt`
- [ ] Tampering `Blobs.PlaintextSHA256` in the brain → `DecryptionFailed`
      (AAD mismatch)
- [ ] Constructing a blob whose plaintext SHA-256 does not match the brain row
      (forged via direct `CryptoPipeline.Encrypt`) → `ChecksumMismatch`,
      `Severity = Critical`, `RequiresUserAction = true`, no plaintext written
- [ ] Missing on-disk blob → `BlobCorrupt`
- [ ] Truncated on-disk blob → `BlobCorrupt`
- [ ] Path resolves to a folder → `FileNotFound`
- [ ] Path doesn't exist → `FileNotFound`
- [ ] `Blobs.PlaintextSize > MaxPlaintextBytes` → `FileTooLong`
- [ ] Cancellation → `ErrorCode.Cancelled`; no notification published
- [ ] Failing notification bus does not mask the original `Result.Error`
- [ ] No published `Notification.Title` or `Notification.Message` contains
      "blob", "wal", "dek", "aad", "gcm", "stripe", "pragma", "sha-256", "sha256"
      (Principle 25 audit test)
- [ ] No new `ErrorCode` values added (cross-cutting decision 4)
- [ ] `Path.GetTempPath()` is not referenced in any file under
      `src/FlashSkink.Core/Engine/ReadPipeline.cs` (Principle 7)
- [ ] `dotnet test` is green
- [ ] CI `plan-check` passes (this file exists with all required headings and `§`
      blueprint citations)

## Line-of-code budget

- `src/FlashSkink.Core/Engine/ReadPipeline.cs` — ~330 lines
- `src/FlashSkink.Core/Metadata/FileRepository.cs` — ~30 lines added
- `tests/FlashSkink.Tests/Engine/ReadPipelineTests.cs` — ~600 lines
- `tests/FlashSkink.Tests/Engine/RecordingNotificationBus.cs` — ~30 lines (extracted)
- `tests/FlashSkink.Tests/Metadata/FileRepositoryTests.cs` — ~80 lines added
- Total: ~360 lines non-test, ~700 lines test

## Non-goals

- Do NOT verify `Blobs.EncryptedXXHash` on the read path. That signal is owned by the
  Phase 5 `AuditService` (dev-plan §2.6 explicit). Adding it here duplicates work the
  GCM tag already does.
- Do NOT introduce a streaming-decrypt variant. AES-GCM cannot decrypt-and-stream
  safely (the tag must verify before any plaintext is released); the V1 cap of
  `Array.MaxLength` (~2 GiB) makes the in-memory path acceptable.
- Do NOT wire `ReadPipeline` into `FlashSkinkVolume`. That is §2.7.
- Do NOT add a `MoveOwnership` method to `ClearOnDisposeOwner`. The
  no-compression-branch copy is bounded and acceptable for V1.
- Do NOT migrate `WritePipeline`'s inline `VirtualPath + BlobID` Dapper query to use
  `GetByVirtualPathAsync` — different shape; out of scope.
- Do NOT add a recovery-from-tail flow on `ChecksumMismatch` / `DecryptionFailed`.
  Phase 5 self-healing handles that; for §2.6 the user receives a Critical
  notification and the read fails.
- Do NOT add new `ErrorCode` values (cross-cutting decision 4).
