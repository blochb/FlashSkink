using System.Buffers;
using System.IO.Hashing;
using System.Security.Cryptography;
using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Buffers;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.IO;

namespace FlashSkink.Core.Engine;

/// <summary>
/// Orchestrates the §14.1 Phase 1 commit stage flow for a single write: type detection,
/// SHA-256 + buffered read, change-detection short-circuit, compress, encrypt, blob assembly,
/// atomic durable write, XXHash64, and brain transaction. Returns
/// <see cref="Result{T}"/> of <see cref="WriteReceipt"/> — never throws across the boundary
/// (Principle 1). The caller (<c>FlashSkinkVolume.WriteFileAsync</c>) must hold the
/// volume-scoped serialization gate before invoking (cross-cutting decision 1).
/// </summary>
public sealed class WritePipeline
{
    private const string ActivityCategory = "WRITE";
    private const string SourceTag = "WritePipeline";

    private readonly FileTypeService _fileTypeService;
    private readonly EntropyDetector _entropyDetector;
    private readonly ILogger<WritePipeline> _logger;
    private readonly ILogger<WriteWalScope> _walScopeLogger;

    /// <summary>
    /// Creates a <see cref="WritePipeline"/>. <paramref name="fileTypeService"/> and
    /// <paramref name="entropyDetector"/> are stateless and may be shared across volumes.
    /// </summary>
    public WritePipeline(
        FileTypeService fileTypeService,
        EntropyDetector entropyDetector,
        ILoggerFactory loggerFactory)
    {
        _fileTypeService = fileTypeService;
        _entropyDetector = entropyDetector;
        _logger = loggerFactory.CreateLogger<WritePipeline>();
        _walScopeLogger = loggerFactory.CreateLogger<WriteWalScope>();
    }

    /// <summary>
    /// Executes the §14.1 stage flow for <paramref name="source"/> at
    /// <paramref name="virtualPath"/>. Returns a successful <see cref="WriteReceipt"/> only
    /// when the brain transaction commits (Principle 4). Cancellation returns
    /// <see cref="ErrorCode.Cancelled"/>. Never throws.
    /// </summary>
    public async Task<Result<WriteReceipt>> ExecuteAsync(
        Stream source,
        string virtualPath,
        VolumeContext context,
        CancellationToken ct)
    {
        IMemoryOwner<byte>? compressed = null;
        try
        {
            // ── Stage 0 — pre-flight ─────────────────────────────────────────
            ct.ThrowIfCancellationRequested();

            // ── Stage 0 — type detection ─────────────────────────────────────
            // For seekable sources: read 16 bytes, remember position, seek back to 0.
            // For non-seekable: read 16 bytes into a fixed array that is then prepended
            // inside ReadIntoBufferAsync — no bytes are lost.
            byte[] headerBufArray = new byte[16];
            int headerRead;
            if (source.CanSeek)
            {
                headerRead = source.Read(headerBufArray, 0, 16);
                source.Seek(0, SeekOrigin.Begin);
            }
            else
            {
                // ReadAsync then await is safe here because headerBufArray is a managed
                // array (not stackalloc) — it does not cross the await boundary as a span.
                headerRead = await source.ReadAsync(headerBufArray.AsMemory(0, 16), ct)
                    .ConfigureAwait(false);
            }

            ReadOnlySpan<byte> headerSpan = headerBufArray.AsSpan(0, headerRead);
            FileTypeResult typeResult = _fileTypeService.Detect(virtualPath, headerSpan);
            bool isCompressible = _entropyDetector.IsCompressible(typeResult.Extension, headerSpan);

            // ── Stage 1 — plaintext SHA-256 + buffered read ──────────────────
            long? knownLength = TryGetSourceLength(source);
            if (knownLength is { } earlyLen && earlyLen > VolumeContext.MaxPlaintextBytes)
            {
                return Result<WriteReceipt>.Fail(ErrorCode.FileTooLong,
                    $"The file is too large; the maximum supported size is {VolumeContext.MaxPlaintextBytes} bytes.");
            }

            // Defensive reset — GetHashAndReset() returns the hash of any residual data and
            // returns the IncrementalHash to its initial state. The return value is discarded;
            // we only care about the reset so the subsequent AppendData calls start clean.
            _ = context.Sha256.GetHashAndReset();

            var bufferResult = await ReadIntoBufferAsync(
                source, knownLength, headerBufArray, headerRead, context, ct)
                .ConfigureAwait(false);
            if (!bufferResult.Success)
            {
                await LogAndPublishAsync(context, virtualPath, bufferResult.Error!, ct).ConfigureAwait(false);
                return Result<WriteReceipt>.Fail(bufferResult.Error!);
            }

            using var plaintextBuffer = bufferResult.Value!;
            long plaintextSize = plaintextBuffer.Memory.Length;

            // Use a managed byte[] rather than stackalloc because sha256Bytes is referenced
            // after several await points below (BuildAad, change-detection). Principle 20
            // prohibits stackalloc across await boundaries.
            byte[] sha256Bytes = new byte[32];
            if (!context.Sha256.TryGetHashAndReset(sha256Bytes, out int hashWritten) || hashWritten != 32)
            {
                var hashErr = new ErrorContext { Code = ErrorCode.Unknown, Message = "SHA-256 hash read failed." };
                await LogAndPublishAsync(context, virtualPath, hashErr, ct).ConfigureAwait(false);
                return Result<WriteReceipt>.Fail(hashErr);
            }

            // ── Stage 2 — change-detection short-circuit ─────────────────────
            string sha256Hex = Convert.ToHexString(sha256Bytes).ToLowerInvariant();
            var existingResult = await context.Blobs.GetByPlaintextHashAsync(sha256Hex, ct)
                .ConfigureAwait(false);
            if (!existingResult.Success)
            {
                await LogAndPublishAsync(context, virtualPath, existingResult.Error!, ct).ConfigureAwait(false);
                return Result<WriteReceipt>.Fail(existingResult.Error!);
            }

            if (existingResult.Value is { } existingBlob)
            {
                // Same content — check if the same virtual path already points at it.
                var existingFile = await context.BrainConnection.QuerySingleOrDefaultAsync<dynamic>(
                    new CommandDefinition(
                        "SELECT FileID FROM Files WHERE VirtualPath = @P AND BlobID = @B",
                        new { P = virtualPath, B = existingBlob.BlobId },
                        cancellationToken: ct))
                    .ConfigureAwait(false);

                if (existingFile is not null)
                {
                    return Result<WriteReceipt>.Ok(new WriteReceipt
                    {
                        FileId = (string)existingFile.FileID,
                        BlobId = existingBlob.BlobId,
                        Status = WriteStatus.Unchanged,
                        PlaintextSize = plaintextSize,
                        EncryptedSize = existingBlob.EncryptedSize,
                        MimeType = typeResult.MimeType,
                        Extension = typeResult.Extension,
                    });
                }
                // Hash matches but path differs — V1 has no cross-path dedup; fall through.
            }

            // ── Stage 3 — compression ────────────────────────────────────────
            ReadOnlyMemory<byte> payload;
            BlobFlags flags;
            if (isCompressible &&
                context.Compression.TryCompress(
                    plaintextBuffer.Memory, out compressed, out flags, out int compWritten))
            {
                payload = compressed!.Memory[..compWritten];
            }
            else
            {
                flags = BlobFlags.None;
                payload = plaintextBuffer.Memory;
            }

            // ── Stage 4 — encryption ─────────────────────────────────────────
            Guid newBlobGuid = Guid.NewGuid();
            string newBlobId = newBlobGuid.ToString("N");

            // AAD: 16-byte raw GUID || 32-byte raw SHA-256 digest.
            // Built synchronously before any await — stays in this stack frame (Principle 20).
            // The Encrypt call is also synchronous; no await crosses the stackalloc spans.
            byte[] aadArray = new byte[48];
            BuildAad(aadArray, newBlobGuid, sha256Bytes);

            int encryptedLen = BlobHeader.HeaderSize + payload.Length + BlobHeader.TagSize;
            using var encrypted = ClearOnDisposeOwner.Rent(encryptedLen);
            var encResult = context.Crypto.Encrypt(
                payload.Span, context.Dek.Span, aadArray, flags, encrypted, out int encWritten);
            if (!encResult.Success)
            {
                await LogAndPublishAsync(context, virtualPath, encResult.Error!, ct).ConfigureAwait(false);
                return Result<WriteReceipt>.Fail(encResult.Error!);
            }

            // ── Stage 5 — blob assembly (header already included in encrypted buffer) ──

            // ── Stage 6 — open WAL scope before the on-disk write ────────────
            // The FileID is generated here so it can be included in the WAL payload.
            string newFileId = Guid.NewGuid().ToString();
            var scopeResult = await WriteWalScope.OpenAsync(
                context.Wal, context.BlobWriter, context.SkinkRoot,
                newFileId, newBlobId, virtualPath,
                _walScopeLogger, ct)
                .ConfigureAwait(false);
            if (!scopeResult.Success)
            {
                await LogAndPublishAsync(context, virtualPath, scopeResult.Error!, ct).ConfigureAwait(false);
                return Result<WriteReceipt>.Fail(scopeResult.Error!);
            }

            await using var scope = scopeResult.Value!;

            // ── Stage 7 — durable atomic write ──────────────────────────────
            var writeResult = await context.BlobWriter.WriteAsync(
                context.SkinkRoot, newBlobId, encrypted.Memory[..encWritten], ct)
                .ConfigureAwait(false);
            if (!writeResult.Success)
            {
                await LogAndPublishAsync(context, virtualPath, writeResult.Error!, ct).ConfigureAwait(false);
                return Result<WriteReceipt>.Fail(writeResult.Error!);
            }

            string blobPath = writeResult.Value!;
            scope.MarkRenamed();

            // ── Stage 8 — XXHash64 over the final on-disk blob bytes ─────────
            ulong xxhash = XxHash64.HashToUInt64(encrypted.Memory.Span[..encWritten]);
            string encryptedXxHashHex = xxhash.ToString("x16");

            // ── Stage 9 — ensure folder path (outside brain tx per Drift Note 3) ──
            (string? parentVirtualPath, string leafName) = SplitVirtualPath(virtualPath);
            string? parentFileId = null;
            if (parentVirtualPath is { Length: > 0 })
            {
                var ensureResult = await context.Files.EnsureFolderPathAsync(parentVirtualPath, ct)
                    .ConfigureAwait(false);
                if (!ensureResult.Success)
                {
                    await LogAndPublishAsync(context, virtualPath, ensureResult.Error!, ct).ConfigureAwait(false);
                    return Result<WriteReceipt>.Fail(ensureResult.Error!);
                }

                parentFileId = ensureResult.Value;
            }

            // ── Stage 10 — brain commit ──────────────────────────────────────
            string? compressionLabel = flags == BlobFlags.CompressedLz4 ? "LZ4"
                : flags == BlobFlags.CompressedZstd ? "ZSTD"
                : null;

            string relativeBlobPath = ToRelativeBlobPath(blobPath, context.SkinkRoot);

            var commitArgs = new BrainCommitArgs(
                FileId: newFileId,
                BlobId: newBlobId,
                ParentId: parentFileId,
                Name: leafName,
                Extension: typeResult.Extension,
                MimeType: typeResult.MimeType,
                VirtualPath: virtualPath,
                PlaintextSize: plaintextSize,
                EncryptedSize: encWritten,
                PlaintextSha256: sha256Hex,
                EncryptedXxHash: encryptedXxHashHex,
                Compression: compressionLabel,
                BlobPath: relativeBlobPath,
                NowUtc: DateTime.UtcNow,
                FilenameForActivity: leafName);

            var commitResult = await CommitBrainAsync(context, commitArgs, scope, ct)
                .ConfigureAwait(false);
            if (!commitResult.Success)
            {
                await LogAndPublishAsync(context, virtualPath, commitResult.Error!, ct).ConfigureAwait(false);
                return Result<WriteReceipt>.Fail(commitResult.Error!);
            }

            // Success — scope is COMMITTED; DisposeAsync is now a no-op.
            return Result<WriteReceipt>.Ok(new WriteReceipt
            {
                FileId = newFileId,
                BlobId = newBlobId,
                Status = WriteStatus.Written,
                PlaintextSize = plaintextSize,
                EncryptedSize = encWritten,
                MimeType = typeResult.MimeType,
                Extension = typeResult.Extension,
            });
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("Write of '{VirtualPath}' was cancelled.", virtualPath);
            return Result<WriteReceipt>.Fail(ErrorCode.Cancelled, "The write was cancelled.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var err = new ErrorContext
            {
                Code = ErrorCode.StagingFailed,
                Message = "An I/O error occurred while writing to the skink.",
                ExceptionType = ex.GetType().FullName,
                ExceptionMessage = ex.Message,
                StackTrace = ex.StackTrace,
            };
            await LogAndPublishAsync(context, virtualPath, err, CancellationToken.None).ConfigureAwait(false);
            return Result<WriteReceipt>.Fail(err);
        }
        catch (Exception ex)
        {
            var err = new ErrorContext
            {
                Code = ErrorCode.Unknown,
                Message = "An unexpected error occurred while writing to the skink.",
                ExceptionType = ex.GetType().FullName,
                ExceptionMessage = ex.Message,
                StackTrace = ex.StackTrace,
            };
            await LogAndPublishAsync(context, virtualPath, err, CancellationToken.None).ConfigureAwait(false);
            return Result<WriteReceipt>.Fail(err);
        }
        finally
        {
            compressed?.Dispose();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>source.Length</c> when <c>source.CanSeek &amp;&amp; source.Length &gt;= 0</c>;
    /// otherwise <see langword="null"/>. Pure — no I/O, never throws.
    /// </summary>
    private static long? TryGetSourceLength(Stream source)
    {
        if (!source.CanSeek)
        {
            return null;
        }

        try
        {
            long len = source.Length;
            return len >= 0 ? len : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads <paramref name="source"/> to EOF into a single <see cref="ClearOnDisposeOwner"/>,
    /// feeding every byte into <see cref="VolumeContext.Sha256"/>. For seekable sources the
    /// known-length fast path is taken; for non-seekable sources the 16-byte header that was
    /// already read is prepended to the <see cref="RecyclableMemoryStreamManager"/> stream
    /// before the main read loop, so no header bytes are dropped. Returns
    /// <see cref="ErrorCode.FileTooLong"/> if the cumulative byte count exceeds
    /// <see cref="VolumeContext.MaxPlaintextBytes"/>.
    /// </summary>
    private static async ValueTask<Result<IMemoryOwner<byte>>> ReadIntoBufferAsync(
        Stream source,
        long? knownLength,
        byte[] prefixBytes,
        int prefixLength,
        VolumeContext context,
        CancellationToken ct)
    {
        const int chunkSize = 81920; // 80 KB — standard .NET CopyToAsync chunk

        if (knownLength.HasValue)
        {
            // Fast path — size known up-front and within cap.
            long len = knownLength.Value;
            var owner = ClearOnDisposeOwner.Rent((int)len);
            try
            {
                int totalRead = 0;
                Memory<byte> buf = owner.Memory;
                while (totalRead < len)
                {
                    int read = await source.ReadAsync(buf[totalRead..], ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    context.Sha256.AppendData(buf.Span.Slice(totalRead, read));
                    totalRead += read;
                }

                // Trim to actual bytes read (stream may have been shorter than Length).
                if (totalRead < (int)len)
                {
                    var trimmed = ClearOnDisposeOwner.Rent(totalRead);
                    buf.Slice(0, totalRead).CopyTo(trimmed.Memory);
                    owner.Dispose();
                    return Result<IMemoryOwner<byte>>.Ok(trimmed);
                }

                return Result<IMemoryOwner<byte>>.Ok(owner);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }
        else
        {
            // Non-seekable path — use RecyclableMemoryStream to grow.
            using var ms = context.StreamManager.GetStream("WritePipeline.ReadIntoBufferAsync");

            // Feed the already-read prefix bytes into the hash and staging stream.
            if (prefixLength > 0)
            {
                ReadOnlySpan<byte> prefix = prefixBytes.AsSpan(0, prefixLength);
                context.Sha256.AppendData(prefix);
                ms.Write(prefix);
            }

            var chunk = new byte[chunkSize];
            long totalBytes = prefixLength;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read = await source.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
                if (totalBytes > VolumeContext.MaxPlaintextBytes)
                {
                    return Result<IMemoryOwner<byte>>.Fail(ErrorCode.FileTooLong,
                        $"The file is too large; the maximum supported size is {VolumeContext.MaxPlaintextBytes} bytes.");
                }

                context.Sha256.AppendData(chunk.AsSpan(0, read));
                ms.Write(chunk.AsSpan(0, read));
            }

            // Copy from the RecyclableMemoryStream into a zeroing owner.
            int total = (int)ms.Length;
            var result = ClearOnDisposeOwner.Rent(total);
            try
            {
                ms.Seek(0, SeekOrigin.Begin);
                int copied = 0;
                while (copied < total)
                {
                    int n = ms.Read(result.Memory.Span[copied..]);
                    if (n == 0)
                    {
                        break;
                    }
                    copied += n;
                }

                return Result<IMemoryOwner<byte>>.Ok(result);
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Writes the 48-byte AAD per cross-cutting decision 3: 16 raw GUID bytes followed by
    /// 32 raw SHA-256 digest bytes. Uses a managed <c>byte[]</c> rather than
    /// <c>stackalloc</c> so the result can be passed to async callers without crossing an
    /// await boundary. The bytes are written directly into <paramref name="aad48"/>.
    /// </summary>
    private static void BuildAad(byte[] aad48, Guid blobId, ReadOnlySpan<byte> plaintextSha256)
    {
        // Raw little-endian GUID bytes — 16 bytes.
        blobId.TryWriteBytes(aad48.AsSpan(0, 16));
        // Raw SHA-256 digest — 32 bytes.
        plaintextSha256[..32].CopyTo(aad48.AsSpan(16, 32));
    }

    /// <summary>
    /// Executes the brain transaction: INSERT INTO Blobs, Files, TailUploads, ActivityLog;
    /// WAL COMMITTED transition inside the same tx; then commits. On any failure the
    /// transaction is rolled back and a failed <see cref="Result"/> is returned; the
    /// surrounding <c>await using var scope</c> then runs <see cref="WriteWalScope.DisposeAsync"/>
    /// to delete staging/destination files and transition the WAL row to FAILED.
    /// </summary>
    private async Task<Result> CommitBrainAsync(
        VolumeContext ctx,
        BrainCommitArgs args,
        WriteWalScope scope,
        CancellationToken ct)
    {
        SqliteTransaction? tx = null;
        try
        {
            tx = ctx.BrainConnection.BeginTransaction();

            // INSERT INTO Blobs
            await ctx.BrainConnection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO Blobs " +
                "(BlobID, EncryptedSize, PlaintextSize, PlaintextSHA256, " +
                "EncryptedXXHash, Compression, BlobPath, CreatedUtc, SoftDeletedUtc, PurgeAfterUtc) " +
                "VALUES (@BlobId, @EncryptedSize, @PlaintextSize, @PlaintextSha256, " +
                "@EncryptedXxHash, @Compression, @BlobPath, @CreatedUtc, NULL, NULL)",
                new
                {
                    args.BlobId,
                    args.EncryptedSize,
                    args.PlaintextSize,
                    args.PlaintextSha256,
                    args.EncryptedXxHash,
                    args.Compression,
                    args.BlobPath,
                    CreatedUtc = args.NowUtc.ToString("O"),
                },
                tx,
                cancellationToken: ct)).ConfigureAwait(false);

            // INSERT INTO Files
            await ctx.BrainConnection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO Files " +
                "(FileID, ParentID, IsFolder, IsSymlink, SymlinkTarget, Name, " +
                "Extension, MimeType, VirtualPath, SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc, BlobID) " +
                "VALUES (@FileId, @ParentId, 0, 0, NULL, @Name, @Extension, @MimeType, @VirtualPath, " +
                "@SizeBytes, @Now, @Now, @Now, @BlobId)",
                new
                {
                    args.FileId,
                    args.ParentId,
                    args.Name,
                    args.Extension,
                    args.MimeType,
                    args.VirtualPath,
                    SizeBytes = args.PlaintextSize,
                    Now = args.NowUtc.ToString("O"),
                    args.BlobId,
                },
                tx,
                cancellationToken: ct)).ConfigureAwait(false);

            // INSERT INTO TailUploads — INSERT-SELECT against active providers (zero rows in Phase 2)
            await ctx.BrainConnection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO TailUploads (FileID, ProviderID, Status, QueuedUtc, AttemptCount) " +
                "SELECT @FileId, ProviderID, 'PENDING', @Now, 0 " +
                "FROM Providers WHERE IsActive = 1",
                new
                {
                    args.FileId,
                    Now = args.NowUtc.ToString("O"),
                },
                tx,
                cancellationToken: ct)).ConfigureAwait(false);

            // INSERT INTO ActivityLog
            await ctx.BrainConnection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO ActivityLog (EntryID, OccurredUtc, Category, Summary, Detail) " +
                "VALUES (@EntryId, @Now, @Category, @Summary, NULL)",
                new
                {
                    EntryId = Guid.NewGuid().ToString("N"),
                    Now = args.NowUtc.ToString("O"),
                    Category = ActivityCategory,
                    Summary = $"Saved file '{args.FilenameForActivity}'",
                },
                tx,
                cancellationToken: ct)).ConfigureAwait(false);

            // WAL COMMITTED transition — inside the same tx (Drift Note 4 in pr-2.5.md).
            // ct is passed as CancellationToken.None — Principle 17: compensation must complete.
            var transitionResult = await scope.CompleteAsync(
                transaction: tx, ct: CancellationToken.None)
                .ConfigureAwait(false);
            if (!transitionResult.Success)
            {
                tx.Rollback();
                return transitionResult;
            }

            tx.Commit();
            scope.ConfirmCommitted(); // _completed only after the commit lands (§21.3)
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            tx?.Rollback();
            _logger.LogInformation(
                "Brain commit cancelled for file '{VirtualPath}'.", args.VirtualPath);
            return Result.Fail(ErrorCode.Cancelled, "Write cancelled before brain commit completed.", ex);
        }
        catch (SqliteException ex) when (ex.IsUniqueConstraintViolation())
        {
            tx?.Rollback();
            _logger.LogError(ex,
                "Path conflict committing write of '{VirtualPath}'.", args.VirtualPath);
            return Result.Fail(ErrorCode.PathConflict,
                $"A file or folder named '{args.Name}' already exists at this location.", ex);
        }
        catch (SqliteException ex)
        {
            tx?.Rollback();
            _logger.LogError(ex,
                "Database error committing write of '{VirtualPath}'.", args.VirtualPath);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to commit write to brain.", ex);
        }
        catch (Exception ex)
        {
            tx?.Rollback();
            _logger.LogError(ex,
                "Unexpected error committing write of '{VirtualPath}'.", args.VirtualPath);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error committing write to brain.", ex);
        }
        finally
        {
            tx?.Dispose();
        }
    }

    /// <summary>
    /// Logs the error at <see cref="LogLevel.Error"/> and publishes a user-facing
    /// <see cref="Notification"/> via <see cref="INotificationBus"/>. Cancellation errors are
    /// logged at <see cref="LogLevel.Information"/> and are NOT published to the bus
    /// (Principles 14 and 24). Never throws — a bus failure is swallowed and logged as a
    /// warning so it does not mask the original error.
    /// </summary>
    private async ValueTask LogAndPublishAsync(
        VolumeContext ctx,
        string virtualPath,
        ErrorContext err,
        CancellationToken ct)
    {
        if (err.Code == ErrorCode.Cancelled)
        {
            // Principle 14 — cancellation is not a fault; log at Information only.
            _logger.LogInformation(
                "Write of '{VirtualPath}' was cancelled: {Code}", virtualPath, err.Code);
            return;
        }

        _logger.LogError(
            "Write of '{VirtualPath}' failed: {Code} — {Message}", virtualPath, err.Code, err.Message);

        var notification = new Notification
        {
            Source = SourceTag,
            Severity = NotificationSeverity.Error,
            Title = "Could not save file",
            Message = $"FlashSkink could not save '{virtualPath}'.",
            Error = err,
            RequiresUserAction = false,
        };

        try
        {
            // Principle 17 — CancellationToken.None as a literal; never cancel a failure publish.
            await ctx.NotificationBus.PublishAsync(notification, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception pubEx)
        {
            _logger.LogWarning(pubEx,
                "Notification publish failed after write error for '{VirtualPath}'.", virtualPath);
        }
    }

    /// <summary>
    /// Splits a virtual path into its parent-folder segment and leaf name.
    /// <c>"/a/b/c.txt"</c> → <c>("a/b", "c.txt")</c>.
    /// <c>"root.txt"</c> → <c>(null, "root.txt")</c>.
    /// Never throws. Pure function.
    /// </summary>
    private static (string? parentPath, string name) SplitVirtualPath(string virtualPath)
    {
        string trimmed = virtualPath.TrimStart('/');
        int lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return (null, trimmed);
        }

        return (trimmed[..lastSlash], trimmed[(lastSlash + 1)..]);
    }

    /// <summary>
    /// Converts an absolute destination blob path back to a relative path under
    /// <paramref name="skinkRoot"/>. Uses forward-slash separators for portability.
    /// Never throws. Pure function.
    /// </summary>
    private static string ToRelativeBlobPath(string absolutePath, string skinkRoot)
    {
        string root = skinkRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rel = absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? absolutePath[(root.Length + 1)..]
            : absolutePath;

        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    // ── Internal record ───────────────────────────────────────────────────────

    /// <summary>Parameter bag for <see cref="CommitBrainAsync"/>.</summary>
    private readonly record struct BrainCommitArgs(
        string FileId,
        string BlobId,
        string? ParentId,
        string Name,
        string? Extension,
        string? MimeType,
        string VirtualPath,
        long PlaintextSize,
        long EncryptedSize,
        string PlaintextSha256,
        string EncryptedXxHash,
        string? Compression,
        string BlobPath,
        DateTime NowUtc,
        string FilenameForActivity);
}
