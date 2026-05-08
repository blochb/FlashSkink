using System.Buffers;
using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Buffers;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Storage;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Engine;

/// <summary>
/// Orchestrates §14.2 stages 1–7 for a single read: brain lookup → blob open → full-blob
/// read → AES-GCM decrypt → optional decompress → SHA-256 verify → copy to destination
/// stream. Returns <see cref="Result"/> — never throws across the boundary (Principle 1).
/// The caller (<c>FlashSkinkVolume.ReadFileAsync</c>) must hold the volume-scoped
/// serialization gate before invoking (cross-cutting decision 1).
/// </summary>
public sealed class ReadPipeline
{
    private const string SourceTag = "ReadPipeline";
    private const int IoChunkSize = 4 * 1024 * 1024;

    private readonly ILogger<ReadPipeline> _logger;

    /// <summary>Creates a <see cref="ReadPipeline"/>. All other dependencies come from the
    /// <see cref="VolumeContext"/> passed on each call.</summary>
    public ReadPipeline(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<ReadPipeline>();
    }

    /// <summary>
    /// Executes the §14.2 stage flow for <paramref name="virtualPath"/>, streaming the
    /// verified plaintext into <paramref name="destination"/>. Returns
    /// <see cref="Result.Ok()"/> only when all seven stages succeed. Cancellation returns
    /// <see cref="ErrorCode.Cancelled"/>. Never throws.
    /// </summary>
    public async Task<Result> ExecuteAsync(
        string virtualPath,
        Stream destination,
        VolumeContext context,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // ── Stage 1 — brain lookup ────────────────────────────────────────
            var fileResult = await context.Files.GetByVirtualPathAsync(virtualPath, ct).ConfigureAwait(false);
            if (!fileResult.Success)
            {
                return await FailWithLogAndPublishAsync(
                    context, virtualPath, fileResult.Error!, NotificationSeverity.Error).ConfigureAwait(false);
            }

            if (fileResult.Value is not { } fileRow)
            {
                return await FailWithLogAndPublishAsync(
                    context, virtualPath,
                    new ErrorContext { Code = ErrorCode.FileNotFound, Message = "No file exists at that path." },
                    NotificationSeverity.Error).ConfigureAwait(false);
            }

            if (fileRow.IsFolder)
            {
                return await FailWithLogAndPublishAsync(
                    context, virtualPath,
                    new ErrorContext { Code = ErrorCode.FileNotFound, Message = "That path is a folder, not a file." },
                    NotificationSeverity.Error).ConfigureAwait(false);
            }

            if (fileRow.BlobId is null)
            {
                return await FailWithLogAndPublishAsync(
                    context, virtualPath,
                    new ErrorContext { Code = ErrorCode.BlobCorrupt, Message = "The file has no blob reference." },
                    NotificationSeverity.Error).ConfigureAwait(false);
            }

            var blobResult = await context.Blobs.GetByIdAsync(fileRow.BlobId, ct).ConfigureAwait(false);
            if (!blobResult.Success)
            {
                return await FailWithLogAndPublishAsync(
                    context, virtualPath, blobResult.Error!, NotificationSeverity.Error).ConfigureAwait(false);
            }

            if (blobResult.Value is not { } blobRow)
            {
                return await FailWithLogAndPublishAsync(
                    context, virtualPath,
                    new ErrorContext { Code = ErrorCode.BlobCorrupt, Message = "The brain references a blob that no longer exists." },
                    NotificationSeverity.Error).ConfigureAwait(false);
            }

            if (blobRow.PlaintextSize > VolumeContext.MaxPlaintextBytes)
            {
                return await FailWithLogAndPublishAsync(
                    context, virtualPath,
                    new ErrorContext
                    {
                        Code = ErrorCode.FileTooLong,
                        Message = $"The file is too large; the maximum supported size is {VolumeContext.MaxPlaintextBytes} bytes.",
                    },
                    NotificationSeverity.Error).ConfigureAwait(false);
            }

            // ── Stage 2-3 — blob open + full read ────────────────────────────
            string blobPath = AtomicBlobWriter.ComputeDestinationPath(context.SkinkRoot, blobRow.BlobId);
            var blobReadResult = await ReadBlobAsync(blobPath, blobRow.EncryptedSize, ct).ConfigureAwait(false);
            if (!blobReadResult.Success)
            {
                return await FailWithLogAndPublishAsync(
                    context, virtualPath, blobReadResult.Error!, NotificationSeverity.Error).ConfigureAwait(false);
            }

            using var encryptedBuffer = blobReadResult.Value!;

            // ── Stage 4 — decrypt ─────────────────────────────────────────────
            Guid blobGuid = TryParseBlobGuid(blobRow.BlobId);
            if (blobGuid == Guid.Empty)
            {
                return await FailWithLogAndPublishAsync(
                    context, virtualPath,
                    new ErrorContext { Code = ErrorCode.BlobCorrupt, Message = "The blob identifier is malformed." },
                    NotificationSeverity.Error).ConfigureAwait(false);
            }

            byte[] expectedSha256;
            try
            {
                expectedSha256 = HexDecodeSha256(blobRow.PlaintextSha256);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
            {
                return await FailWithLogAndPublishAsync(
                    context, virtualPath,
                    new ErrorContext
                    {
                        Code = ErrorCode.BlobCorrupt,
                        Message = "The brain's plaintext digest is malformed.",
                        ExceptionType = ex.GetType().FullName,
                        ExceptionMessage = ex.Message,
                    },
                    NotificationSeverity.Error).ConfigureAwait(false);
            }

            // AAD: 16-byte raw GUID || 32-byte raw SHA-256 digest (cross-cutting decision 3).
            // Managed byte[] rather than stackalloc — survives async continuations (Principle 20).
            byte[] aadArray = new byte[48];
            BuildAad(aadArray, blobGuid, expectedSha256);

            int ciphertextLength = checked((int)(blobRow.EncryptedSize - BlobHeader.HeaderSize - BlobHeader.TagSize));
            if (ciphertextLength < 0)
            {
                return await FailWithLogAndPublishAsync(
                    context, virtualPath,
                    new ErrorContext { Code = ErrorCode.BlobCorrupt, Message = "The blob is too small to contain a header and tag." },
                    NotificationSeverity.Error).ConfigureAwait(false);
            }

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
                bool isTampered = decryptResult.Error!.Code == ErrorCode.DecryptionFailed;
                return await FailWithLogAndPublishAsync(
                    context, virtualPath, decryptResult.Error!,
                    isTampered ? NotificationSeverity.Critical : NotificationSeverity.Error,
                    requiresUserAction: isTampered).ConfigureAwait(false);
            }

            // ── Stage 5 — decompress (conditional) ───────────────────────────
            // plaintextOwner is always assigned in both branches before use, or we return early.
            IMemoryOwner<byte> plaintextOwner = null!; // assigned below in both branches
            int plaintextWritten = 0;

            if (flags == BlobFlags.None)
            {
                if (payloadLen != blobRow.PlaintextSize)
                {
                    return await FailWithLogAndPublishAsync(
                        context, virtualPath,
                        new ErrorContext
                        {
                            Code = ErrorCode.BlobCorrupt,
                            Message = $"Plaintext length {payloadLen} does not match recorded size {blobRow.PlaintextSize}.",
                        },
                        NotificationSeverity.Error).ConfigureAwait(false);
                }

                int plaintextSizeInt = checked((int)blobRow.PlaintextSize);
                var copy = ClearOnDisposeOwner.Rent(plaintextSizeInt == 0 ? 1 : plaintextSizeInt);
                try
                {
                    payloadBuffer.Memory[..plaintextSizeInt].CopyTo(copy.Memory);
                    plaintextOwner = copy;
                    plaintextWritten = plaintextSizeInt;
                }
                catch
                {
                    copy.Dispose();
                    throw;
                }
            }
            else
            {
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
                            context, virtualPath, decompressResult.Error!, NotificationSeverity.Error).ConfigureAwait(false);
                    }

                    if (plaintextWritten != plaintextSizeInt)
                    {
                        plaintext.Dispose();
                        return await FailWithLogAndPublishAsync(
                            context, virtualPath,
                            new ErrorContext
                            {
                                Code = ErrorCode.BlobCorrupt,
                                Message = $"Decompressed length {plaintextWritten} does not match recorded size {plaintextSizeInt}.",
                            },
                            NotificationSeverity.Error).ConfigureAwait(false);
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
                // ── Stage 6 — hash verify ─────────────────────────────────────
                // Discard any state left by a prior call before computing the new digest.
                // stackalloc is safe here — synchronous, no await between alloc and last use
                // (Principle 20). Avoids the byte[] allocation of GetHashAndReset() (Principle 18).
                Span<byte> discard = stackalloc byte[32];
                context.Sha256.TryGetHashAndReset(discard, out _);
                context.Sha256.AppendData(plaintextOwner.Memory.Span[..plaintextWritten]);
                byte[] computed = new byte[32];

                if (!context.Sha256.TryGetHashAndReset(computed, out int hashWritten) || hashWritten != 32)
                {
                    return await FailWithLogAndPublishAsync(
                        context, virtualPath,
                        new ErrorContext { Code = ErrorCode.Unknown, Message = "SHA-256 hash computation failed." },
                        NotificationSeverity.Error).ConfigureAwait(false);
                }

                if (!CryptographicOperations.FixedTimeEquals(computed, expectedSha256))
                {
                    return await FailWithLogAndPublishAsync(
                        context, virtualPath,
                        new ErrorContext
                        {
                            Code = ErrorCode.ChecksumMismatch,
                            Message = "The file's contents do not match its recorded fingerprint. The skink may be damaged.",
                        },
                        NotificationSeverity.Critical, requiresUserAction: true).ConfigureAwait(false);
                }

                // ── Stage 7 — copy to destination (only after verification) ──
                await destination.WriteAsync(plaintextOwner.Memory[..plaintextWritten], ct).ConfigureAwait(false);
            }

            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("Read of '{VirtualPath}' was cancelled.", virtualPath);
            return Result.Fail(ErrorCode.Cancelled, "The read was cancelled.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var err = new ErrorContext
            {
                Code = ErrorCode.Unknown,
                Message = "An I/O error occurred while reading from the skink.",
                ExceptionType = ex.GetType().FullName,
                ExceptionMessage = ex.Message,
                StackTrace = ex.StackTrace,
            };
            await LogAndPublishAsync(context, virtualPath, err, NotificationSeverity.Error, false).ConfigureAwait(false);
            return Result.Fail(err);
        }
        catch (Exception ex)
        {
            var err = new ErrorContext
            {
                Code = ErrorCode.Unknown,
                Message = "An unexpected error occurred while reading the file.",
                ExceptionType = ex.GetType().FullName,
                ExceptionMessage = ex.Message,
                StackTrace = ex.StackTrace,
            };
            await LogAndPublishAsync(context, virtualPath, err, NotificationSeverity.Error, false).ConfigureAwait(false);
            return Result.Fail(err);
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Opens and reads the full encrypted blob at <paramref name="blobPath"/> into a pooled
    /// buffer. Asserts the on-disk length matches <paramref name="expectedSize"/>. Disposes
    /// the rented buffer on any failure path.
    /// </summary>
    private async ValueTask<Result<IMemoryOwner<byte>>> ReadBlobAsync(
        string blobPath, long expectedSize, CancellationToken ct)
    {
        if (expectedSize < 0 || expectedSize > VolumeContext.MaxPlaintextBytes + BlobHeader.HeaderSize + BlobHeader.TagSize)
        {
            return Result<IMemoryOwner<byte>>.Fail(ErrorCode.BlobCorrupt,
                $"Recorded encrypted size {expectedSize} is out of range.");
        }

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
                if (read == 0)
                {
                    break;
                }

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

    /// <summary>
    /// Writes the 48-byte AAD per cross-cutting decision 3: 16 raw GUID bytes followed by
    /// 32 raw SHA-256 digest bytes. Mirrors <c>WritePipeline.BuildAad</c>.
    /// </summary>
    private static void BuildAad(byte[] aad48, Guid blobId, ReadOnlySpan<byte> plaintextSha256)
    {
        blobId.TryWriteBytes(aad48.AsSpan(0, 16));
        plaintextSha256[..32].CopyTo(aad48.AsSpan(16, 32));
    }

    private static Guid TryParseBlobGuid(string blobId)
    {
        return Guid.TryParseExact(blobId, "N", out Guid parsed) ? parsed : Guid.Empty;
    }

    private static byte[] HexDecodeSha256(string hex)
    {
        if (hex.Length != 64)
        {
            throw new InvalidDataException(
                $"Expected 64-character hex SHA-256 digest; got {hex.Length} characters.");
        }

        return Convert.FromHexString(hex);
    }

    /// <summary>
    /// Logs the error and publishes a user-facing <see cref="Notification"/>. Cancellation
    /// errors are logged at Information and NOT published (Principles 14 and 24). The sole
    /// async operation inside uses <see cref="CancellationToken.None"/> (Principle 17) — a
    /// <c>ct</c> parameter would be unused and misleading, so it is intentionally omitted.
    /// Never throws.
    /// </summary>
    private async ValueTask LogAndPublishAsync(
        VolumeContext ctx,
        string virtualPath,
        ErrorContext err,
        NotificationSeverity severity,
        bool requiresUserAction)
    {
        if (err.Code == ErrorCode.Cancelled)
        {
            _logger.LogInformation("Read of '{VirtualPath}' was cancelled: {Code}", virtualPath, err.Code);
            return;
        }

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

        try
        {
            // Principle 17 — CancellationToken.None literal; failure notifications must complete.
            await ctx.NotificationBus.PublishAsync(notification, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception pubEx)
        {
            _logger.LogWarning(pubEx,
                "Notification publish failed after read error for '{VirtualPath}'.", virtualPath);
        }
    }

    private async ValueTask<Result> FailWithLogAndPublishAsync(
        VolumeContext ctx,
        string virtualPath,
        ErrorContext err,
        NotificationSeverity severity,
        bool requiresUserAction = false)
    {
        await LogAndPublishAsync(ctx, virtualPath, err, severity, requiresUserAction).ConfigureAwait(false);
        return Result.Fail(err);
    }
}
