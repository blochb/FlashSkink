using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers;

public sealed class FileSystemProviderTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemProvider _sut;

    public FileSystemProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "flashskink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sut = new FileSystemProvider("test-provider", "Test Tail", _root, NullLogger<FileSystemProvider>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static byte[] MakeBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 256);
        }

        return bytes;
    }

    // ── Create (static factory) ───────────────────────────────────────────────────────────────

    [Fact]
    public void Create_RootPathDoesNotExist_ReturnsProviderUnreachable()
    {
        var missing = Path.Combine(Path.GetTempPath(), "flashskink-tests", "no-such-dir-" + Guid.NewGuid().ToString("N"));

        var result = FileSystemProvider.Create(
            "p1", "Test", missing, NullLogger<FileSystemProvider>.Instance);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderUnreachable, result.Error!.Code);
    }

    [Fact]
    public void Create_RootPathExistsAndWritable_ReturnsProvider()
    {
        var result = FileSystemProvider.Create(
            "p1", "Test", _root, NullLogger<FileSystemProvider>.Instance);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
    }

    // ── BeginUploadAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BeginUploadAsync_NewRemote_CreatesStagingDirAndSidecar()
    {
        var result = await _sut.BeginUploadAsync("abc123def456.bin", 1024, CancellationToken.None);

        Assert.True(result.Success);
        var stagingDir = Path.Combine(_root, ".flashskink-staging");
        Assert.True(Directory.Exists(stagingDir));
        Assert.True(File.Exists(Path.Combine(stagingDir, "abc123def456.bin.session")));
    }

    [Fact]
    public async Task BeginUploadAsync_ReturnsSessionWithMaxValueExpiry()
    {
        var result = await _sut.BeginUploadAsync("abcd1234.bin", 512, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(DateTimeOffset.MaxValue, result.Value!.ExpiresAt);
        Assert.Equal(0L, result.Value.BytesUploaded);
        Assert.Equal(512L, result.Value.TotalBytes);
    }

    // ── GetUploadedBytesAsync ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUploadedBytesAsync_NoPartialFile_ReturnsZero()
    {
        var session = new UploadSession
        {
            SessionUri = "abcd1234.bin",
            ExpiresAt = DateTimeOffset.MaxValue,
            BytesUploaded = 0,
            TotalBytes = 1024,
        };

        var result = await _sut.GetUploadedBytesAsync(session, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0L, result.Value);
    }

    [Fact]
    public async Task GetUploadedBytesAsync_AfterRangeUpload_ReturnsRangeLength()
    {
        const int totalBytes = 4096;
        var data = MakeBytes(totalBytes);
        var beginResult = await _sut.BeginUploadAsync("abcd1234.bin", totalBytes, CancellationToken.None);
        var session = beginResult.Value!;
        await _sut.UploadRangeAsync(session, 0, data, CancellationToken.None);

        var result = await _sut.GetUploadedBytesAsync(session, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(totalBytes, result.Value);
    }

    // ── UploadRangeAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadRangeAsync_AppendsAtOffset_BytesPersistAcrossOpens()
    {
        const int total = 8192;
        const int half = total / 2;
        var firstHalf = MakeBytes(half);
        var secondHalf = MakeBytes(half);

        var beginResult = await _sut.BeginUploadAsync("abcd1234.bin", total, CancellationToken.None);
        var session = beginResult.Value!;

        await _sut.UploadRangeAsync(session, 0, firstHalf, CancellationToken.None);

        // Simulate process restart: re-query bytes.
        var bytesResult = await _sut.GetUploadedBytesAsync(session, CancellationToken.None);
        Assert.Equal(half, bytesResult.Value);

        await _sut.UploadRangeAsync(session, half, secondHalf, CancellationToken.None);

        var bytesAfter = await _sut.GetUploadedBytesAsync(session, CancellationToken.None);
        Assert.Equal(total, bytesAfter.Value);
    }

    [Fact]
    public async Task UploadRangeAsync_OverlappingOffset_OverwritesPriorContent()
    {
        var data1 = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA };
        var data2 = new byte[] { 0xBB, 0xBB };

        var beginResult = await _sut.BeginUploadAsync("abcd5678.bin", 4, CancellationToken.None);
        var session = beginResult.Value!;
        await _sut.UploadRangeAsync(session, 0, data1, CancellationToken.None);
        await _sut.UploadRangeAsync(session, 0, data2, CancellationToken.None);

        var partialPath = Path.Combine(_root, ".flashskink-staging", "abcd5678.bin.partial");
        var contents = await File.ReadAllBytesAsync(partialPath);
        Assert.Equal(new byte[] { 0xBB, 0xBB, 0xAA, 0xAA }, contents);
    }

    [Fact]
    public async Task UploadRangeAsync_Cancelled_ReturnsCancelled()
    {
        var beginResult = await _sut.BeginUploadAsync("abcd9999.bin", 1024, CancellationToken.None);
        var session = beginResult.Value!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _sut.UploadRangeAsync(session, 0, MakeBytes(1024), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    // ── FinaliseUploadAsync ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FinaliseUploadAsync_Complete_AtomicallyMovesToShardedDestination()
    {
        const int total = 512;
        var data = MakeBytes(total);
        var remote = "abcdef1234567890.bin";

        var beginResult = await _sut.BeginUploadAsync(remote, total, CancellationToken.None);
        var session = beginResult.Value!;
        await _sut.UploadRangeAsync(session, 0, data, CancellationToken.None);

        var finalResult = await _sut.FinaliseUploadAsync(session, CancellationToken.None);

        Assert.True(finalResult.Success);
        var expectedDest = Path.Combine(_root, "blobs", remote[..2], remote[2..4], remote);
        Assert.True(File.Exists(expectedDest));
    }

    [Fact]
    public async Task FinaliseUploadAsync_Cancelled_ReturnsCancelled()
    {
        const int total = 64;
        var remote = "aabb998877001122.bin";
        var session = (await _sut.BeginUploadAsync(remote, total, CancellationToken.None)).Value!;
        await _sut.UploadRangeAsync(session, 0, MakeBytes(total), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _sut.FinaliseUploadAsync(session, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task FinaliseUploadAsync_SizeMismatch_ReturnsUploadFailed()
    {
        const int total = 1024;
        var data = MakeBytes(512); // only half

        var beginResult = await _sut.BeginUploadAsync("abcdef001122.bin", total, CancellationToken.None);
        var session = beginResult.Value!;
        await _sut.UploadRangeAsync(session, 0, data, CancellationToken.None);

        var finalResult = await _sut.FinaliseUploadAsync(session, CancellationToken.None);

        Assert.False(finalResult.Success);
        Assert.Equal(ErrorCode.UploadFailed, finalResult.Error!.Code);
    }

    [Fact]
    public async Task FinaliseUploadAsync_DestinationExists_ReturnsUploadFailed()
    {
        const int total = 128;
        var data = MakeBytes(total);
        var remote = "aabbccdd11223344.bin";

        // First upload — succeed.
        var s1 = (await _sut.BeginUploadAsync(remote, total, CancellationToken.None)).Value!;
        await _sut.UploadRangeAsync(s1, 0, data, CancellationToken.None);
        await _sut.FinaliseUploadAsync(s1, CancellationToken.None);

        // Second upload to the same remote name — destination already exists.
        var s2 = (await _sut.BeginUploadAsync(remote, total, CancellationToken.None)).Value!;
        await _sut.UploadRangeAsync(s2, 0, data, CancellationToken.None);
        var finalResult = await _sut.FinaliseUploadAsync(s2, CancellationToken.None);

        Assert.False(finalResult.Success);
        Assert.Equal(ErrorCode.UploadFailed, finalResult.Error!.Code);
    }

    [Fact]
    public async Task FinaliseUploadAsync_DeletesSidecarFile()
    {
        const int total = 64;
        var remote = "abcdef990011aabb.bin";

        var session = (await _sut.BeginUploadAsync(remote, total, CancellationToken.None)).Value!;
        await _sut.UploadRangeAsync(session, 0, MakeBytes(total), CancellationToken.None);
        await _sut.FinaliseUploadAsync(session, CancellationToken.None);

        var sidecar = Path.Combine(_root, ".flashskink-staging", remote + ".session");
        Assert.False(File.Exists(sidecar));
    }

    // ── AbortUploadAsync ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AbortUploadAsync_DeletesPartialAndSidecar_Idempotent()
    {
        var remote = "abcdef1122334455.bin";
        var session = (await _sut.BeginUploadAsync(remote, 256, CancellationToken.None)).Value!;
        await _sut.UploadRangeAsync(session, 0, MakeBytes(128), CancellationToken.None);

        await _sut.AbortUploadAsync(session, CancellationToken.None);
        await _sut.AbortUploadAsync(session, CancellationToken.None); // idempotent

        Assert.False(File.Exists(Path.Combine(_root, ".flashskink-staging", remote + ".partial")));
        Assert.False(File.Exists(Path.Combine(_root, ".flashskink-staging", remote + ".session")));
    }

    // ── DownloadAsync ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_ReturnsStreamOverFinalisedBlob()
    {
        const int total = 256;
        var data = MakeBytes(total);
        var remote = "aabbcc001122ffee.bin";

        var session = (await _sut.BeginUploadAsync(remote, total, CancellationToken.None)).Value!;
        await _sut.UploadRangeAsync(session, 0, data, CancellationToken.None);
        var finalResult = await _sut.FinaliseUploadAsync(session, CancellationToken.None);
        var remoteId = finalResult.Value!;

        var downloadResult = await _sut.DownloadAsync(remoteId, CancellationToken.None);

        Assert.True(downloadResult.Success);
        await using var stream = downloadResult.Value!;
        var buffer = new byte[total];
        await stream.ReadExactlyAsync(buffer, CancellationToken.None);
        Assert.Equal(data, buffer);
    }

    [Fact]
    public async Task DownloadAsync_MissingRemote_ReturnsBlobNotFound()
    {
        var result = await _sut.DownloadAsync("blobs/no/su/no-such-object.bin", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.BlobNotFound, result.Error!.Code);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesFile_Idempotent()
    {
        const int total = 64;
        var remote = "aabb112233445566.bin";
        var session = (await _sut.BeginUploadAsync(remote, total, CancellationToken.None)).Value!;
        await _sut.UploadRangeAsync(session, 0, MakeBytes(total), CancellationToken.None);
        var finalResult = await _sut.FinaliseUploadAsync(session, CancellationToken.None);
        var remoteId = finalResult.Value!;

        await _sut.DeleteAsync(remoteId, CancellationToken.None);
        await _sut.DeleteAsync(remoteId, CancellationToken.None); // idempotent

        var existsResult = await _sut.ExistsAsync(remoteId, CancellationToken.None);
        Assert.False(existsResult.Value);
    }

    // ── ExistsAsync ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistsAsync_AfterFinalise_ReturnsTrue()
    {
        const int total = 64;
        var remote = "aabb998877665544.bin";
        var session = (await _sut.BeginUploadAsync(remote, total, CancellationToken.None)).Value!;
        await _sut.UploadRangeAsync(session, 0, MakeBytes(total), CancellationToken.None);
        var finalResult = await _sut.FinaliseUploadAsync(session, CancellationToken.None);

        var existsResult = await _sut.ExistsAsync(finalResult.Value!, CancellationToken.None);

        Assert.True(existsResult.Value);
    }

    // ── ListAsync ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ReturnsRelativePathsBelowPrefix()
    {
        const int total = 32;
        var remote1 = "aabb001122334455.bin";
        var remote2 = "aabb556677889900.bin";

        foreach (var remote in new[] { remote1, remote2 })
        {
            var session = (await _sut.BeginUploadAsync(remote, total, CancellationToken.None)).Value!;
            await _sut.UploadRangeAsync(session, 0, MakeBytes(total), CancellationToken.None);
            await _sut.FinaliseUploadAsync(session, CancellationToken.None);
        }

        var listResult = await _sut.ListAsync("blobs", CancellationToken.None);

        Assert.True(listResult.Success);
        Assert.Equal(2, listResult.Value!.Count);
        Assert.All(listResult.Value, id => Assert.StartsWith("blobs/", id));
    }

    [Fact]
    public async Task ListAsync_EmptyPrefix_ReturnsAllObjects()
    {
        const int total = 32;
        var remote = "ccdd001122334455.bin";
        var session = (await _sut.BeginUploadAsync(remote, total, CancellationToken.None)).Value!;
        await _sut.UploadRangeAsync(session, 0, MakeBytes(total), CancellationToken.None);
        await _sut.FinaliseUploadAsync(session, CancellationToken.None);

        var listResult = await _sut.ListAsync("", CancellationToken.None);

        Assert.True(listResult.Success);
        Assert.NotEmpty(listResult.Value!);
    }

    [Fact]
    public async Task ListAsync_NonExistentPrefix_ReturnsEmpty()
    {
        var listResult = await _sut.ListAsync("_brain", CancellationToken.None);

        Assert.True(listResult.Success);
        Assert.Empty(listResult.Value!);
    }

    // ── CheckHealthAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckHealthAsync_WritableRoot_ReturnsHealthyWithLatency()
    {
        var result = await _sut.CheckHealthAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(ProviderHealthStatus.Healthy, result.Value!.Status);
        Assert.NotNull(result.Value.RoundTripLatency);
    }

    // ── GetUsedBytesAsync / GetQuotaBytesAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetUsedBytesAsync_ReturnsNonNegative()
    {
        var result = await _sut.GetUsedBytesAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Value >= 0);
    }

    [Fact]
    public async Task GetQuotaBytesAsync_ReturnsDriveSize()
    {
        var result = await _sut.GetQuotaBytesAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.True(result.Value > 0);
    }

    // ── Round-trip integration ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_50RangesOf4MiB_FinalBlobMatchesSourceBytes()
    {
        const int rangeSize = 4 * 1024 * 1024;
        const int rangeCount = 50;
        const long totalBytes = (long)rangeSize * rangeCount;
        var remote = "aabbccddeeff0011.bin";

        // Build source data (200 MB) in a lazy pattern to avoid large allocations.
        var sourceData = new byte[rangeSize];
        for (var i = 0; i < rangeSize; i++)
        {
            sourceData[i] = (byte)(i % 251); // prime modulus for variety
        }

        var session = (await _sut.BeginUploadAsync(remote, totalBytes, CancellationToken.None)).Value!;

        for (var i = 0; i < rangeCount; i++)
        {
            var offset = (long)i * rangeSize;
            var rangeResult = await _sut.UploadRangeAsync(session, offset, sourceData, CancellationToken.None);
            Assert.True(rangeResult.Success, $"Range {i} failed: {rangeResult.Error?.Message}");
        }

        var finalResult = await _sut.FinaliseUploadAsync(session, CancellationToken.None);
        Assert.True(finalResult.Success, finalResult.Error?.Message);

        var remoteId = finalResult.Value!;
        var downloadResult = await _sut.DownloadAsync(remoteId, CancellationToken.None);
        Assert.True(downloadResult.Success);

        await using var stream = downloadResult.Value!;
        var readBuffer = new byte[rangeSize];
        for (var i = 0; i < rangeCount; i++)
        {
            await stream.ReadExactlyAsync(readBuffer, CancellationToken.None);
            Assert.Equal(sourceData, readBuffer);
        }
    }

    // ── ISupportsRemoteHashCheck (§3.3) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Provider_ImplementsISupportsRemoteHashCheck_AndComputesHashOnFinalisedBlob()
    {
        // FileSystemProvider declares the capability interface.
        Assert.IsAssignableFrom<ISupportsRemoteHashCheck>(_sut);

        // Upload a small blob, finalise it, then ask for the remote XXHash64.
        var data = MakeBytes(2048);
        var begin = await _sut.BeginUploadAsync("abcd1234.bin", data.Length, CancellationToken.None);
        Assert.True(begin.Success);
        var session = begin.Value!;

        var rangeResult = await _sut.UploadRangeAsync(session, 0, data, CancellationToken.None);
        Assert.True(rangeResult.Success);

        var finalise = await _sut.FinaliseUploadAsync(session, CancellationToken.None);
        Assert.True(finalise.Success);
        var remoteId = finalise.Value!;

        ISupportsRemoteHashCheck hashCheck = _sut;
        var hashResult = await hashCheck.GetRemoteXxHash64Async(remoteId, CancellationToken.None);

        Assert.True(hashResult.Success);
        ulong expected = System.IO.Hashing.XxHash64.HashToUInt64(data);
        Assert.Equal(expected, hashResult.Value);
    }

    // ── _brain/ namespace routing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task BeginUploadAsync_BrainPrefix_FinalisesAtBrainSubdir()
    {
        var data = MakeBytes(512);
        var remote = "_brain/20260101T000000Z.bin";

        var begin = await _sut.BeginUploadAsync(remote, data.Length, CancellationToken.None);
        Assert.True(begin.Success);
        var session = begin.Value!;
        await _sut.UploadRangeAsync(session, 0, data, CancellationToken.None);
        var finalise = await _sut.FinaliseUploadAsync(session, CancellationToken.None);
        Assert.True(finalise.Success);

        Assert.Equal("_brain/20260101T000000Z.bin", finalise.Value);
        var destPath = Path.Combine(_root, "_brain", "20260101T000000Z.bin");
        Assert.True(File.Exists(destPath), $"Expected brain mirror at {destPath}");
    }

    [Fact]
    public async Task ListAsync_BrainPrefix_ReturnsBrainEntries()
    {
        var data = MakeBytes(128);
        foreach (var name in new[]
        {
            "_brain/20260101T000000Z.bin",
            "_brain/20260102T000000Z.bin",
        })
        {
            var begin = (await _sut.BeginUploadAsync(name, data.Length, CancellationToken.None)).Value!;
            await _sut.UploadRangeAsync(begin, 0, data, CancellationToken.None);
            await _sut.FinaliseUploadAsync(begin, CancellationToken.None);
        }

        var listResult = await _sut.ListAsync("_brain", CancellationToken.None);

        Assert.True(listResult.Success);
        var entries = listResult.Value!;
        Assert.Equal(2, entries.Count);
        Assert.Contains("_brain/20260101T000000Z.bin", entries);
        Assert.Contains("_brain/20260102T000000Z.bin", entries);
    }

    [Fact]
    public async Task RoundTrip_BrainObject_DownloadMatchesUpload()
    {
        var data = MakeBytes(2048);
        var remote = "_brain/20260101T000000Z.bin";

        var session = (await _sut.BeginUploadAsync(remote, data.Length, CancellationToken.None)).Value!;
        await _sut.UploadRangeAsync(session, 0, data, CancellationToken.None);
        var finalise = await _sut.FinaliseUploadAsync(session, CancellationToken.None);
        var remoteId = finalise.Value!;

        var download = await _sut.DownloadAsync(remoteId, CancellationToken.None);
        Assert.True(download.Success);
        var buffer = new byte[data.Length];
        await using (var stream = download.Value!)
        {
            await stream.ReadExactlyAsync(buffer, CancellationToken.None);
        }
        Assert.Equal(data, buffer);

        // ExistsAsync + DeleteAsync round-trip. Stream is disposed above so the file handle is
        // released — Windows cannot delete a file with an open handle.
        Assert.True((await _sut.ExistsAsync(remoteId, CancellationToken.None)).Value);
        Assert.True((await _sut.DeleteAsync(remoteId, CancellationToken.None)).Success);
        Assert.False((await _sut.ExistsAsync(remoteId, CancellationToken.None)).Value);
    }
}
