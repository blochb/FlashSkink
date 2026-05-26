using System.Security.Cryptography;
using FlashSkink.Core.Providers.Dropbox;
using Xunit;

namespace FlashSkink.Tests.Providers.Dropbox;

/// <summary>
/// Unit tests for <see cref="DropboxContentHash"/>. PR §4.4.
/// </summary>
public sealed class DropboxContentHashTests
{
    private const string ShaOfEmpty =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public async Task ComputeAsync_EmptyStream_ReturnsSha256OfEmpty()
    {
        using var ms = new MemoryStream(Array.Empty<byte>());
        var hash = await DropboxContentHash.ComputeAsync(ms, CancellationToken.None);
        Assert.Equal(ShaOfEmpty, hash);
    }

    [Fact]
    public async Task ComputeAsync_SingleByte_MatchesReference()
    {
        var bytes = new byte[] { 0x42 };
        using var ms = new MemoryStream(bytes);
        var hash = await DropboxContentHash.ComputeAsync(ms, CancellationToken.None);
        Assert.Equal(ReferenceHash(bytes), hash);
    }

    [Fact]
    public async Task ComputeAsync_ExactBlockSize_MatchesReference()
    {
        var bytes = new byte[DropboxContentHash.BlockSize];
        Array.Fill(bytes, (byte)0xAB);
        using var ms = new MemoryStream(bytes);
        var hash = await DropboxContentHash.ComputeAsync(ms, CancellationToken.None);
        Assert.Equal(ReferenceHash(bytes), hash);
    }

    [Fact]
    public async Task ComputeAsync_OverOneBlock_MatchesReference()
    {
        var bytes = new byte[DropboxContentHash.BlockSize + 1];
        Array.Fill(bytes, (byte)0xCD);
        using var ms = new MemoryStream(bytes);
        var hash = await DropboxContentHash.ComputeAsync(ms, CancellationToken.None);
        Assert.Equal(ReferenceHash(bytes), hash);
    }

    [Fact]
    public async Task ComputeAsync_MultiBlock_MatchesReference()
    {
        var bytes = new byte[(DropboxContentHash.BlockSize * 3) + 17];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i & 0xFF);
        }
        using var ms = new MemoryStream(bytes);
        var hash = await DropboxContentHash.ComputeAsync(ms, CancellationToken.None);
        Assert.Equal(ReferenceHash(bytes), hash);
    }

    [Fact]
    public async Task ComputeAsync_PrecancelledToken_ReturnsEmpty()
    {
        using var ms = new MemoryStream(new byte[1024]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var hash = await DropboxContentHash.ComputeAsync(ms, cts.Token);
        Assert.Equal(string.Empty, hash);
    }

    [Fact]
    public async Task ComputeAsync_NonSeekableStream_Works()
    {
        var bytes = new byte[1000];
        for (var i = 0; i < bytes.Length; i++) { bytes[i] = (byte)(i & 0xFF); }
        using var inner = new MemoryStream(bytes);
        using var nonSeekable = new NonSeekableStream(inner);

        var hash = await DropboxContentHash.ComputeAsync(nonSeekable, CancellationToken.None);
        Assert.Equal(ReferenceHash(bytes), hash);
    }

    /// <summary>
    /// Reference implementation: SHA-256 of each 4 MiB block, concat the digests, SHA-256 the concat,
    /// hex-lowercase. Computed independently of <see cref="DropboxContentHash"/> so the assertion is
    /// not tautological — we apply the algorithm directly here from Dropbox's published description.
    /// </summary>
    private static string ReferenceHash(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return Convert.ToHexStringLower(SHA256.HashData(ReadOnlySpan<byte>.Empty));
        }

        const int blockSize = 4 * 1024 * 1024;
        using var concat = new MemoryStream();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var thisBlock = Math.Min(blockSize, bytes.Length - offset);
            var digest = SHA256.HashData(bytes.AsSpan(offset, thisBlock));
            concat.Write(digest);
            offset += thisBlock;
        }
        return Convert.ToHexStringLower(SHA256.HashData(concat.ToArray()));
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
