using System.Buffers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Buffers;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Engine;
using Xunit;

namespace FlashSkink.Tests.Engine;

public class CompressionServiceTests
{
    // ── Round-trip tests ──────────────────────────────────────────────────────

    [Fact]
    public void TryCompress_SmallCompressiblePayload_ReturnsLz4AndRoundTrips()
    {
        using var svc = new CompressionService();
        byte[] input = new byte[100 * 1024];
        Array.Fill(input, (byte)0xAB);

        bool compressed = svc.TryCompress(input, out var output, out BlobFlags flags, out int writtenBytes);

        Assert.True(compressed);
        Assert.NotNull(output);
        Assert.Equal(BlobFlags.CompressedLz4, flags);
        Assert.True(writtenBytes > 0 && writtenBytes < input.Length);
        Assert.False(output!.Memory.Span[..writtenBytes].SequenceEqual(input)); // compressed != plaintext

        using var destination = MemoryPool<byte>.Shared.Rent(input.Length);
        var result = svc.Decompress(output.Memory[..writtenBytes], flags, input.Length, destination, out int decompressedBytes);
        output.Dispose();

        Assert.True(result.Success);
        Assert.Equal(input.Length, decompressedBytes);
        Assert.True(destination.Memory.Span[..decompressedBytes].SequenceEqual(input));
    }

    [Fact]
    public void TryCompress_LargeCompressiblePayload_ReturnsZstdAndRoundTrips()
    {
        using var svc = new CompressionService();
        byte[] input = new byte[1 * 1024 * 1024];
        Array.Fill(input, (byte)0xCD);

        bool compressed = svc.TryCompress(input, out var output, out BlobFlags flags, out int writtenBytes);

        Assert.True(compressed);
        Assert.NotNull(output);
        Assert.Equal(BlobFlags.CompressedZstd, flags);
        Assert.True(writtenBytes > 0 && writtenBytes < input.Length);

        using var destination = MemoryPool<byte>.Shared.Rent(input.Length);
        var result = svc.Decompress(output!.Memory[..writtenBytes], flags, input.Length, destination, out int decompressedBytes);
        output.Dispose();

        Assert.True(result.Success);
        Assert.Equal(input.Length, decompressedBytes);
        Assert.True(destination.Memory.Span[..decompressedBytes].SequenceEqual(input));
    }

    [Fact]
    public void TryCompress_HighEntropyPayload_ReturnsFalse()
    {
        using var svc = new CompressionService();
        byte[] input = new byte[200 * 1024];
        new Random(42).NextBytes(input);

        bool compressed = svc.TryCompress(input, out var output, out BlobFlags flags, out int writtenBytes);

        Assert.False(compressed);
        Assert.Null(output);
        Assert.Equal(BlobFlags.None, flags);
        Assert.Equal(0, writtenBytes);
    }

    [Fact]
    public void Decompress_BlobFlagsNone_CopiesInputToDestination()
    {
        using var svc = new CompressionService();
        byte[] input = new byte[1024];
        new Random(0).NextBytes(input);
        using var destination = MemoryPool<byte>.Shared.Rent(input.Length);

        var result = svc.Decompress(input, BlobFlags.None, input.Length, destination, out int writtenBytes);

        Assert.True(result.Success);
        Assert.Equal(input.Length, writtenBytes);
        Assert.True(destination.Memory.Span[..writtenBytes].SequenceEqual(input));
    }

    // ── Threshold boundary tests ──────────────────────────────────────────────

    [Fact]
    public void TryCompress_OneByteBelowLz4Threshold_UsesLz4()
    {
        using var svc = new CompressionService();
        byte[] input = new byte[CompressionService.Lz4ThresholdBytes - 1];
        Array.Fill(input, (byte)0xAB);

        bool compressed = svc.TryCompress(input, out var output, out BlobFlags flags, out _);
        output?.Dispose();

        Assert.True(compressed);
        Assert.Equal(BlobFlags.CompressedLz4, flags);
    }

    [Fact]
    public void TryCompress_AtLz4Threshold_UsesZstd()
    {
        // Threshold uses '<' semantics: input.Length < Lz4ThresholdBytes → LZ4.
        // Exactly at the threshold → Zstd.
        using var svc = new CompressionService();
        byte[] input = new byte[CompressionService.Lz4ThresholdBytes];
        Array.Fill(input, (byte)0xAB);

        bool compressed = svc.TryCompress(input, out var output, out BlobFlags flags, out _);
        output?.Dispose();

        Assert.True(compressed);
        Assert.Equal(BlobFlags.CompressedZstd, flags);
    }

    // ── Error-path tests ──────────────────────────────────────────────────────

    [Fact]
    public void Decompress_BothFlagsSet_ReturnsBlobCorrupt()
    {
        using var svc = new CompressionService();
        byte[] data = new byte[64];
        using var destination = MemoryPool<byte>.Shared.Rent(64);
        BlobFlags bothSet = BlobFlags.CompressedLz4 | BlobFlags.CompressedZstd;

        var result = svc.Decompress(data, bothSet, data.Length, destination, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.BlobCorrupt, result.Error!.Code);
    }

    [Fact]
    public void Decompress_PlaintextSizeExceedsMax_ReturnsFileTooLong()
    {
        using var svc = new CompressionService();
        byte[] data = new byte[100];
        using var destination = MemoryPool<byte>.Shared.Rent(data.Length);

        // One above the cap → rejected
        var rejected = svc.Decompress(
            data, BlobFlags.None, CompressionService.MaxPlaintextBytes + 1L, destination, out _);
        Assert.False(rejected.Success);
        Assert.Equal(ErrorCode.FileTooLong, rejected.Error!.Code);

        // Exactly at the cap → accepted when plaintextSize matches payload length.
        var accepted = svc.Decompress(
            data, BlobFlags.None, data.Length, destination, out int written);
        Assert.True(accepted.Success);
        Assert.Equal(data.Length, written);
    }

    [Fact]
    public void Decompress_NegativePlaintextSize_ReturnsBlobCorrupt()
    {
        using var svc = new CompressionService();
        byte[] data = new byte[64];
        using var destination = MemoryPool<byte>.Shared.Rent(64);

        var result = svc.Decompress(data, BlobFlags.None, plaintextSize: -1L, destination, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.BlobCorrupt, result.Error!.Code);
    }

    [Fact]
    public void Decompress_BlobFlagsNone_PlaintextSizeMismatch_ReturnsBlobCorrupt()
    {
        using var svc = new CompressionService();
        byte[] data = new byte[100];
        using var destination = MemoryPool<byte>.Shared.Rent(data.Length);

        // plaintextSize claims 99 bytes but compressed payload is 100 bytes → corrupted header
        var result = svc.Decompress(data, BlobFlags.None, plaintextSize: 99L, destination, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.BlobCorrupt, result.Error!.Code);
    }

    // ── Dispose / zero tests ──────────────────────────────────────────────────

    [Fact]
    public void ClearOnDisposeOwner_DisposeZeroesBuffer()
    {
        // RecordingMemoryPool exposes the backing byte[] directly so we can read it after
        // dispose and verify CryptographicOperations.ZeroMemory was actually called.
        // ClearOnDisposeOwner is internal; accessible because AssemblyInfo.cs declares
        // [InternalsVisibleTo("FlashSkink.Tests")].
        var pool = new RecordingMemoryPool();
        var owner = ClearOnDisposeOwner.Rent(64, pool);

        owner.Memory.Span.Fill(0xFF);

        owner.Dispose();

        Assert.NotNull(pool.LastRented);
        Assert.All(pool.LastRented!, b => Assert.Equal((byte)0x00, b));
    }

    [Fact]
    public void ClearOnDisposeOwner_DoubleDispose_DoesNotThrow()
    {
        using var svc = new CompressionService();
        byte[] input = new byte[1024];
        Array.Fill(input, (byte)0xAB);

        svc.TryCompress(input, out var output, out _, out _);
        Assert.NotNull(output);

        output!.Dispose();
        var ex = Record.Exception(() => output.Dispose());
        Assert.Null(ex);
    }

    // ── Test helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Allocates a plain <see cref="byte"/> array per <see cref="Rent"/> call and exposes it
    /// via <see cref="LastRented"/> so tests can read the backing memory after dispose and
    /// verify that <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory"/>
    /// was actually called.
    /// </summary>
    private sealed class RecordingMemoryPool : MemoryPool<byte>
    {
        public byte[]? LastRented { get; private set; }

        public override int MaxBufferSize => int.MaxValue;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            var array = new byte[Math.Max(minBufferSize, 1)];
            LastRented = array;
            return new ArrayOwner(array);
        }

        protected override void Dispose(bool disposing) { }

        private sealed class ArrayOwner : IMemoryOwner<byte>
        {
            private readonly byte[] _array;
            internal ArrayOwner(byte[] array) => _array = array;
            public Memory<byte> Memory => _array;
            public void Dispose() { }
        }
    }
}
