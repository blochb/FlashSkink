using System.Reflection;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Storage;

public sealed class AtomicBlobWriterTests : IDisposable
{
    private readonly string _skinkRoot;
    private readonly AtomicBlobWriter _sut;

    public AtomicBlobWriterTests()
    {
        _skinkRoot = Path.Combine(Path.GetTempPath(), "flashskink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_skinkRoot);
        _sut = new AtomicBlobWriter(NullLogger<AtomicBlobWriter>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_skinkRoot))
        {
            Directory.Delete(_skinkRoot, recursive: true);
        }
    }

    private static string MakeBlobId() => Guid.NewGuid().ToString("N"); // 32 hex chars

    private static byte[] MakeBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 256);
        }
        return bytes;
    }

    // ── WriteAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_NewBlob_CreatesShardedDestination()
    {
        var blobId = MakeBlobId();
        var data = MakeBytes(1024);

        var result = await _sut.WriteAsync(_skinkRoot, blobId, data, CancellationToken.None);

        Assert.True(result.Success);
        var destPath = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId);
        Assert.True(File.Exists(destPath));
        Assert.Equal(data, File.ReadAllBytes(destPath));
        var stagingPath = AtomicBlobWriter.ComputeStagingPath(_skinkRoot, blobId);
        Assert.False(File.Exists(stagingPath));
    }

    [Fact]
    public async Task WriteAsync_ReturnsDestinationPath()
    {
        var blobId = MakeBlobId();
        var expected = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId);

        var result = await _sut.WriteAsync(_skinkRoot, blobId, MakeBytes(64), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public async Task WriteAsync_TwoBlobs_DifferentShards_BothPersist()
    {
        // Ensure the first 4 hex chars differ so they land in different shard directories.
        var blobId1 = "aa" + "bb" + new string('0', 28);
        var blobId2 = "cc" + "dd" + new string('0', 28);

        var r1 = await _sut.WriteAsync(_skinkRoot, blobId1, MakeBytes(16), CancellationToken.None);
        var r2 = await _sut.WriteAsync(_skinkRoot, blobId2, MakeBytes(16), CancellationToken.None);

        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.True(File.Exists(AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId1)));
        Assert.True(File.Exists(AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId2)));
        // Confirm they are in different shard directories.
        Assert.NotEqual(
            Path.GetDirectoryName(AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId1)),
            Path.GetDirectoryName(AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId2)));
    }

    [Fact]
    public async Task WriteAsync_TwoBlobs_SameShard_BothPersist()
    {
        // Same first 4 hex chars → same shard directory.
        var blobId1 = "aabb" + new string('1', 28);
        var blobId2 = "aabb" + new string('2', 28);

        var r1 = await _sut.WriteAsync(_skinkRoot, blobId1, MakeBytes(16), CancellationToken.None);
        var r2 = await _sut.WriteAsync(_skinkRoot, blobId2, MakeBytes(16), CancellationToken.None);

        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.True(File.Exists(AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId1)));
        Assert.True(File.Exists(AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId2)));
        Assert.Equal(
            Path.GetDirectoryName(AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId1)),
            Path.GetDirectoryName(AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId2)));
    }

    [Fact]
    public async Task WriteAsync_DestinationExists_ReturnsPathConflict()
    {
        var blobId = MakeBlobId();
        var destPath = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!); // ! safe: destPath is always a file path with a parent dir
        File.WriteAllBytes(destPath, [0xDE, 0xAD]);

        var result = await _sut.WriteAsync(_skinkRoot, blobId, MakeBytes(32), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.PathConflict, result.Error!.Code);
        var stagingPath = AtomicBlobWriter.ComputeStagingPath(_skinkRoot, blobId);
        Assert.False(File.Exists(stagingPath));
    }

    [Fact]
    public async Task WriteAsync_CancelledBeforeStaging_ReturnsCancelledAndNoStagingFile()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var blobId = MakeBlobId();

        var result = await _sut.WriteAsync(_skinkRoot, blobId, MakeBytes(32), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        var stagingPath = AtomicBlobWriter.ComputeStagingPath(_skinkRoot, blobId);
        Assert.False(File.Exists(stagingPath));
    }

    [Fact]
    public async Task WriteAsync_ZeroByteBlob_Succeeds()
    {
        var blobId = MakeBlobId();

        var result = await _sut.WriteAsync(_skinkRoot, blobId, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.True(result.Success);
        var destPath = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId);
        Assert.True(File.Exists(destPath));
        Assert.Equal(0, new FileInfo(destPath).Length);
    }

    [Fact]
    public async Task WriteAsync_LargeBlob_RoundTripsBytes()
    {
        var blobId = MakeBlobId();
        var data = MakeBytes(4 * 1024 * 1024); // 4 MiB

        var result = await _sut.WriteAsync(_skinkRoot, blobId, data, CancellationToken.None);

        Assert.True(result.Success);
        var written = File.ReadAllBytes(AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId));
        Assert.Equal(data, written);
    }

    // ── DeleteStagingAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteStagingAsync_Existing_RemovesFile()
    {
        var blobId = MakeBlobId();
        var stagingPath = AtomicBlobWriter.ComputeStagingPath(_skinkRoot, blobId);
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!); // ! safe: stagingPath is always a file path with a parent dir
        File.WriteAllBytes(stagingPath, [0x01]);

        var result = await _sut.DeleteStagingAsync(_skinkRoot, blobId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(stagingPath));
    }

    [Fact]
    public async Task DeleteStagingAsync_Missing_ReturnsOk()
    {
        var blobId = MakeBlobId();

        var result = await _sut.DeleteStagingAsync(_skinkRoot, blobId, CancellationToken.None);

        Assert.True(result.Success);
    }

    // ── DeleteDestinationAsync ────────────────────────────────────────────────

    [Fact]
    public async Task DeleteDestinationAsync_Existing_RemovesFile()
    {
        var blobId = MakeBlobId();
        await _sut.WriteAsync(_skinkRoot, blobId, MakeBytes(16), CancellationToken.None);
        var destPath = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId);
        Assert.True(File.Exists(destPath));

        var result = await _sut.DeleteDestinationAsync(_skinkRoot, blobId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(destPath));
    }

    [Fact]
    public async Task DeleteDestinationAsync_Missing_ReturnsOk()
    {
        var blobId = MakeBlobId();

        var result = await _sut.DeleteDestinationAsync(_skinkRoot, blobId, CancellationToken.None);

        Assert.True(result.Success);
    }

    // ── Pure helpers ──────────────────────────────────────────────────────────

    [Fact]
    public void ComputeDestinationPath_KnownBlobId_ReturnsExpectedShardedPath()
    {
        const string blobId = "aabbccddeeff00112233445566778899";
        var expected = Path.Combine(_skinkRoot, ".flashskink", "blobs", "aa", "bb", blobId + ".bin");

        var actual = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeStagingPath_KnownBlobId_ReturnsExpectedStagingPath()
    {
        const string blobId = "aabbccddeeff00112233445566778899";
        var expected = Path.Combine(_skinkRoot, ".flashskink", "staging", blobId + ".tmp");

        var actual = AtomicBlobWriter.ComputeStagingPath(_skinkRoot, blobId);

        Assert.Equal(expected, actual);
    }

    // ── Private helper unit tests via reflection ──────────────────────────────

    private static bool InvokeIsDiskFull(int hResult)
    {
        var method = typeof(AtomicBlobWriter)
            .GetMethod("IsDiskFull", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, new object[] { new IOException("test", hResult) })!;
    }

    private static bool InvokeIsFileExists(int hResult)
    {
        var method = typeof(AtomicBlobWriter)
            .GetMethod("IsFileExists", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, new object[] { new IOException("test", hResult) })!;
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070), true)]           // Windows ERROR_DISK_FULL
    [InlineData(unchecked((int)(0x80070000 | 28)), true)]    // Unix ENOSPC (low 16 bits = 28)
    [InlineData(unchecked((int)0x80070050), false)]          // ERROR_FILE_EXISTS — not disk full
    [InlineData(0, false)]                                    // no error — not disk full
    public void IsDiskFull_WithHResult_MatchesExpected(int hResult, bool expected)
    {
        Assert.Equal(expected, InvokeIsDiskFull(hResult));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070050), true)]           // Windows ERROR_FILE_EXISTS
    [InlineData(unchecked((int)0x800700B7), true)]           // Windows ERROR_ALREADY_EXISTS
    [InlineData(unchecked((int)(0x80070000 | 17)), true)]    // Unix EEXIST (low 16 bits = 17)
    [InlineData(unchecked((int)0x80070070), false)]          // ERROR_DISK_FULL — not file-exists
    [InlineData(0, false)]                                    // no error — not file-exists
    public void IsFileExists_WithHResult_MatchesExpected(int hResult, bool expected)
    {
        Assert.Equal(expected, InvokeIsFileExists(hResult));
    }
}
