using System.Buffers;
using System.Runtime.InteropServices;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Buffers;
using FlashSkink.Core.Crypto;
using K4os.Compression.LZ4;
using ZstdNet;

namespace FlashSkink.Core.Engine;

/// <summary>
/// Compresses and decompresses pipeline payloads using LZ4 or Zstd; enforces the no-gain rule
/// on compression and the 4 GiB plaintext cap on decompression.
/// </summary>
/// <remarks>
/// Holds <see cref="Compressor"/> and <see cref="Decompressor"/> as reusable fields to avoid
/// creating and destroying native codec handles on every call (blueprint §9.8, Principle 18).
/// The volume-wide <c>SemaphoreSlim(1,1)</c> (cross-cutting decision 1) ensures callers never
/// invoke this service concurrently, so no additional locking is needed here.
/// </remarks>
public sealed class CompressionService : IDisposable
{
    /// <summary>Files smaller than this threshold are compressed with LZ4.</summary>
    public const int Lz4ThresholdBytes = 512 * 1024;

    /// <summary>
    /// Compressed output exceeding this fraction of input size is rejected (no-gain rule).
    /// </summary>
    public const double NoGainThreshold = 0.95;

    /// <summary>Maximum plaintext size accepted by <see cref="Decompress"/>.</summary>
    public const long MaxPlaintextBytes = 4L * 1024 * 1024 * 1024;

    private readonly Compressor _compressor;
    private readonly Decompressor _decompressor;
    private bool _disposed;

    /// <summary>Creates a new instance; allocates native Zstd codec handles.</summary>
    public CompressionService()
    {
        _compressor = new Compressor(new CompressionOptions(3));
        _decompressor = new Decompressor();
    }

    /// <summary>Disposes the native Zstd compressor and decompressor handles.</summary>
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _compressor.Dispose();
        _decompressor.Dispose();
    }

    /// <summary>
    /// Attempts to compress <paramref name="input"/>. Selects LZ4 when
    /// <c>input.Length &lt; Lz4ThresholdBytes</c>, Zstd level 3 otherwise.
    /// Returns <see langword="false"/> (output = null, flags = None, writtenBytes = 0) when
    /// the compressed result exceeds <c>NoGainThreshold * input.Length</c> — the caller writes
    /// plaintext with <see cref="BlobFlags.None"/>. Compression failures also return
    /// <see langword="false"/> (incompressible-content semantics).
    /// </summary>
    public bool TryCompress(
        ReadOnlyMemory<byte> input,
        out IMemoryOwner<byte>? output,
        out BlobFlags flags,
        out int writtenBytes)
    {
        try
        {
            if (input.Length < Lz4ThresholdBytes)
            {
                return TryCompressLz4(input, out output, out flags, out writtenBytes);
            }
            else
            {
                return TryCompressZstd(input, out output, out flags, out writtenBytes);
            }
        }
        catch (ZstdException)
        {
            output = null; flags = BlobFlags.None; writtenBytes = 0;
            return false;
        }
        catch (Exception)
        {
            output = null; flags = BlobFlags.None; writtenBytes = 0;
            return false;
        }
    }

    /// <summary>
    /// Decompresses <paramref name="compressed"/> into <paramref name="destination"/>,
    /// dispatching on <paramref name="flags"/>. <see cref="BlobFlags.None"/> performs a plain
    /// copy. Rejects <paramref name="plaintextSize"/> &gt; <see cref="MaxPlaintextBytes"/> with
    /// <see cref="ErrorCode.FileTooLong"/> before any allocation. Returns
    /// <see cref="ErrorCode.BlobCorrupt"/> on illegal flag combinations (both Lz4 and Zstd set)
    /// or on decompressor failure.
    /// </summary>
    public Result Decompress(
        ReadOnlyMemory<byte> compressed,
        BlobFlags flags,
        long plaintextSize,
        IMemoryOwner<byte> destination,
        out int writtenBytes)
    {
        writtenBytes = 0;
        try
        {
            if (plaintextSize < 0)
            {
                return Result.Fail(ErrorCode.BlobCorrupt, "plaintextSize is negative");
            }

            if (plaintextSize > MaxPlaintextBytes)
            {
                return Result.Fail(ErrorCode.FileTooLong,
                    $"plaintextSize {plaintextSize} exceeds the 4 GiB maximum");
            }

            if ((flags & BlobFlags.CompressedLz4) != 0 && (flags & BlobFlags.CompressedZstd) != 0)
            {
                return Result.Fail(ErrorCode.BlobCorrupt, "Illegal BlobFlags combination");
            }

            if ((flags & BlobFlags.CompressedLz4) != 0)
            {
                int decoded = LZ4Codec.Decode(compressed.Span, destination.Memory.Span);
                if ((long)decoded != plaintextSize)
                {
                    return Result.Fail(ErrorCode.BlobCorrupt,
                        $"LZ4 decoded {decoded} bytes but expected {plaintextSize}");
                }
                writtenBytes = decoded;
            }
            else if ((flags & BlobFlags.CompressedZstd) != 0)
            {
                ArraySegment<byte> compressedSeg = GetArraySegment(compressed);
                byte[] decompressed = _decompressor.Unwrap(compressedSeg);
                if ((long)decompressed.Length != plaintextSize)
                {
                    return Result.Fail(ErrorCode.BlobCorrupt,
                        $"Zstd decompressed {decompressed.Length} bytes but expected {plaintextSize}");
                }
                decompressed.AsSpan().CopyTo(destination.Memory.Span);
                writtenBytes = decompressed.Length;
            }
            else
            {
                // BlobFlags.None — plain copy
                compressed.Span.CopyTo(destination.Memory.Span);
                writtenBytes = compressed.Length;
            }

            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "Operation cancelled", ex);
        }
        catch (ZstdException ex)
        {
            return Result.Fail(ErrorCode.BlobCorrupt, "Zstd decompression failed", ex);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCode.Unknown, "Unexpected error during decompression", ex);
        }
    }

    private bool TryCompressLz4(
        ReadOnlyMemory<byte> input,
        out IMemoryOwner<byte>? output,
        out BlobFlags flags,
        out int writtenBytes)
    {
        int maxSize = LZ4Codec.MaximumOutputSize(input.Length);
        IMemoryOwner<byte> raw = MemoryPool<byte>.Shared.Rent(maxSize);
        try
        {
            int written = LZ4Codec.Encode(input.Span, raw.Memory.Span[..maxSize]);
            if (written <= 0 || (double)written > NoGainThreshold * input.Length)
            {
                raw.Dispose();
                output = null; flags = BlobFlags.None; writtenBytes = 0;
                return false;
            }
            output = new ClearOnDisposeOwner(raw, written);
            flags = BlobFlags.CompressedLz4;
            writtenBytes = written;
            return true;
        }
        catch
        {
            raw.Dispose();
            throw;
        }
    }

    private bool TryCompressZstd(
        ReadOnlyMemory<byte> input,
        out IMemoryOwner<byte>? output,
        out BlobFlags flags,
        out int writtenBytes)
    {
        // ZstdNet.Compressor.Wrap returns a new byte[]; we copy into a pool buffer.
        // There is no buffer-overwriting overload in ZstdNet 1.4.5.
        ArraySegment<byte> inputSeg = GetArraySegment(input);
        byte[] zstdOutput = _compressor.Wrap(inputSeg);
        if ((double)zstdOutput.Length > NoGainThreshold * input.Length)
        {
            output = null; flags = BlobFlags.None; writtenBytes = 0;
            return false;
        }

        var owner = ClearOnDisposeOwner.Rent(zstdOutput.Length);
        zstdOutput.AsSpan().CopyTo(owner.Memory.Span);
        output = owner;
        flags = BlobFlags.CompressedZstd;
        writtenBytes = zstdOutput.Length;
        return true;
    }

    private static ArraySegment<byte> GetArraySegment(ReadOnlyMemory<byte> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> seg))
        {
            return seg;
        }
        return new ArraySegment<byte>(memory.ToArray());
    }
}
