using FlashSkink.Core.Providers.OneDrive;
using Xunit;

namespace FlashSkink.Tests.Providers.OneDrive;

/// <summary>
/// Tests for <see cref="OneDriveQuickXorHash"/>. The hash ships <em>unwired</em> in V1 (Phase-5
/// content-integrity is its consumer), so these assert the structural and self-consistency
/// properties that matter for a streaming digest — empty-state, streaming-equals-single-shot,
/// determinism, avalanche, and cancellation — rather than a hardcoded interop vector (no
/// authoritative published base64 vector was available to pin against; that lands when the hash is
/// wired and validated against a live OneDrive item).
/// </summary>
public sealed class OneDriveQuickXorHashTests
{
    [Fact]
    public async Task ComputeAsync_EmptyInput_ReturnsEmptyStateHash()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());

        var hash = await OneDriveQuickXorHash.ComputeAsync(stream, CancellationToken.None);

        // Empty input → zero state, zero length → 20 zero bytes.
        Assert.Equal(Convert.ToBase64String(new byte[20]), hash);
    }

    [Fact]
    public async Task ComputeAsync_NonEmptyInput_IsDeterministicAndCorrectlySized()
    {
        var data = new byte[1000];
        new Random(12345).NextBytes(data);

        var first = await OneDriveQuickXorHash.ComputeAsync(new MemoryStream(data), CancellationToken.None);
        var second = await OneDriveQuickXorHash.ComputeAsync(new MemoryStream(data), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(20, Convert.FromBase64String(first).Length);
        Assert.NotEqual(Convert.ToBase64String(new byte[20]), first);

        // Avalanche: a single-byte change yields a different digest.
        data[0] ^= 0xFF;
        var mutated = await OneDriveQuickXorHash.ComputeAsync(new MemoryStream(data), CancellationToken.None);
        Assert.NotEqual(first, mutated);
    }

    [Fact]
    public async Task ComputeAsync_StreamedInChunks_EqualsSingleShot()
    {
        var data = new byte[700 * 1024];
        new Random(98765).NextBytes(data);

        var singleShot = await OneDriveQuickXorHash.ComputeAsync(new MemoryStream(data), CancellationToken.None);
        var chunked = await OneDriveQuickXorHash.ComputeAsync(new SmallReadStream(data, 7), CancellationToken.None);

        Assert.Equal(singleShot, chunked);
    }

    [Fact]
    public async Task ComputeAsync_Cancelled_ReturnsEmptyString()
    {
        var data = new byte[1024];
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var hash = await OneDriveQuickXorHash.ComputeAsync(new MemoryStream(data), cts.Token);

        Assert.Equal(string.Empty, hash);
    }

    /// <summary>Read-only stream that returns at most <c>maxRead</c> bytes per read, forcing chunk boundaries.</summary>
    private sealed class SmallReadStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxRead;
        private int _pos;

        public SmallReadStream(byte[] data, int maxRead)
        {
            _data = data;
            _maxRead = maxRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int remaining = _data.Length - _pos;
            if (remaining <= 0)
            {
                return 0;
            }
            int n = Math.Min(Math.Min(count, _maxRead), remaining);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
