using System.Diagnostics;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers;

public sealed class FaultInjectingStorageProviderTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemProvider _inner;
    private readonly FaultInjectingStorageProvider _sut;

    public FaultInjectingStorageProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "flashskink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _inner = new FileSystemProvider("fi", "Fault Injecting", _root, NullLogger<FileSystemProvider>.Instance);
        _sut = new FaultInjectingStorageProvider(_inner);
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

    private async Task<(FaultInjectingStorageProvider sut, FlashSkink.Core.Abstractions.Providers.UploadSession session)>
        OpenSessionAsync(string remote = "abcdef1234567890.bin", int total = 4096)
    {
        var session = (await _sut.BeginUploadAsync(remote, total, CancellationToken.None)).Value!;
        return (_sut, session);
    }

    [Fact]
    public async Task FailNextRange_NextRangeFails_ThirdSucceeds()
    {
        var (sut, session) = await OpenSessionAsync();
        var data = MakeBytes(4096);

        sut.FailNextRange();

        var first = await sut.UploadRangeAsync(session, 0, data, CancellationToken.None);
        Assert.False(first.Success);
        Assert.Equal(ErrorCode.UploadFailed, first.Error!.Code);

        // Second attempt succeeds (fault consumed).
        var second = await sut.UploadRangeAsync(session, 0, data, CancellationToken.None);
        Assert.True(second.Success);
    }

    [Fact]
    public async Task FailNextRangeWith_PropagatesSpecifiedErrorCode()
    {
        var (sut, session) = await OpenSessionAsync();
        sut.FailNextRangeWith(ErrorCode.ProviderRateLimited);

        var result = await sut.UploadRangeAsync(session, 0, MakeBytes(4096), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderRateLimited, result.Error!.Code);
    }

    [Fact]
    public async Task ForceSessionExpiryAfter_ReturnsUploadSessionExpiredOnNthRange()
    {
        var (sut, session) = await OpenSessionAsync(total: 8192);
        sut.ForceSessionExpiryAfter(1); // expire after 1 successful range

        // Range 0 — succeeds (rangesUploaded goes to 1).
        await sut.UploadRangeAsync(session, 0, MakeBytes(4096), CancellationToken.None);

        // Range 1 — should be expired (rangesUploaded == 1 == expiryAfter).
        var result = await sut.UploadRangeAsync(session, 4096, MakeBytes(4096), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadSessionExpired, result.Error!.Code);
    }

    [Fact]
    public async Task SetRangeLatency_AppliesDelay()
    {
        var (sut, session) = await OpenSessionAsync();
        sut.SetRangeLatency(TimeSpan.FromMilliseconds(100));

        var sw = Stopwatch.StartNew();
        await sut.UploadRangeAsync(session, 0, MakeBytes(4096), CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 90, $"Expected >= 90ms delay, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Reset_ClearsAllInjectedFaults()
    {
        var (sut, session) = await OpenSessionAsync();
        sut.FailNextRange();
        sut.SetRangeLatency(TimeSpan.FromSeconds(10));
        sut.ForceSessionExpiryAfter(0);

        sut.Reset();

        // After reset, range should succeed without delay or expiry.
        var sw = Stopwatch.StartNew();
        var result = await sut.UploadRangeAsync(session, 0, MakeBytes(4096), CancellationToken.None);
        sw.Stop();

        Assert.True(result.Success);
        Assert.True(sw.ElapsedMilliseconds < 5000, "Unexpected latency after reset");
    }
}
