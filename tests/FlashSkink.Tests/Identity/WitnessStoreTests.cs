using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Identity;
using FlashSkink.Core.Providers;
using FlashSkink.Tests.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Identity;

/// <summary>
/// Integration tests for <see cref="WitnessStore"/> against a real
/// <see cref="FileSystemProvider"/> rooted at a per-test temp dir, with
/// <see cref="FaultInjectingStorageProvider"/> wrapping for the offline-tail and
/// fault-injection scenarios. Dev plan §3.5.1.
/// </summary>
public sealed class WitnessStoreTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemProvider _provider;
    private readonly WitnessStore _store;
    private readonly byte[] _dek;

    public WitnessStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "flashskink-witness-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _provider = new FileSystemProvider(
            "test-witness-provider", "Witness Test Tail", _root,
            NullLogger<FileSystemProvider>.Instance);
        _store = new WitnessStore(NullLogger<WitnessStore>.Instance);
        _dek = RandomNumberGenerator.GetBytes(32);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static WitnessPayload SamplePayload(long epoch = 1L, bool conflict = false) =>
        new(
            VolumeId: "test-volume-id",
            Epoch: epoch,
            SessionId: Guid.NewGuid().ToString("D"),
            CommittedAtUtc: DateTime.UtcNow.ToString("O"),
            Host: "test-host",
            AppVersion: "0.1.0+test",
            ConflictObserved: conflict);

    // ── Read paths ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryReadAsync_NoWitnessOnTail_ReturnsOkNull()
    {
        var result = await _store.TryReadAsync(
            _provider, _dek, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task WriteAsync_ThenTryReadAsync_ReturnsWrittenPayload()
    {
        var payload = SamplePayload(epoch: 42L);

        var writeResult = await _store.WriteAsync(
            _provider, _dek, payload, CancellationToken.None);
        Assert.True(writeResult.Success);

        var readResult = await _store.TryReadAsync(
            _provider, _dek, CancellationToken.None);
        Assert.True(readResult.Success);
        Assert.NotNull(readResult.Value);

        var read = readResult.Value!.Value;
        Assert.Equal(payload.VolumeId, read.VolumeId);
        Assert.Equal(payload.Epoch, read.Epoch);
        Assert.Equal(payload.SessionId, read.SessionId);
        Assert.Equal(payload.Host, read.Host);
        Assert.Equal(payload.AppVersion, read.AppVersion);
        Assert.Equal(payload.ConflictObserved, read.ConflictObserved);
    }

    [Fact]
    public async Task WriteAsync_StoresFileAt_WitnessCurrentEncPath()
    {
        var payload = SamplePayload();
        var result = await _store.WriteAsync(_provider, _dek, payload, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_root, "_witness", "current.enc")));
    }

    [Fact]
    public async Task WriteAsync_Twice_SecondWriteReplacesFirst()
    {
        var first = SamplePayload(epoch: 1L);
        var second = SamplePayload(epoch: 2L);

        Assert.True((await _store.WriteAsync(_provider, _dek, first, CancellationToken.None)).Success);
        Assert.True((await _store.WriteAsync(_provider, _dek, second, CancellationToken.None)).Success);

        var readResult = await _store.TryReadAsync(_provider, _dek, CancellationToken.None);
        Assert.True(readResult.Success);
        Assert.Equal(2L, readResult.Value!.Value.Epoch);
    }

    [Fact]
    public async Task TryReadAsync_CorruptedFile_ReturnsOkNull()
    {
        // Write a valid witness so the _witness/current.enc path exists, then overwrite
        // with garbage. The store should treat this as "no information" — the §3.5.2
        // handshake's contract is that a corrupted witness file is equivalent to absence.
        Assert.True((await _store.WriteAsync(
            _provider, _dek, SamplePayload(), CancellationToken.None)).Success);

        var witnessFile = Path.Combine(_root, "_witness", "current.enc");
        await File.WriteAllBytesAsync(witnessFile, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var result = await _store.TryReadAsync(_provider, _dek, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task TryReadAsync_WrongDek_ReturnsOkNull()
    {
        var dekA = RandomNumberGenerator.GetBytes(32);
        var dekB = RandomNumberGenerator.GetBytes(32);

        Assert.True((await _store.WriteAsync(
            _provider, dekA, SamplePayload(), CancellationToken.None)).Success);

        var result = await _store.TryReadAsync(_provider, dekB, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task TryReadAsync_PreCancelledToken_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _store.TryReadAsync(_provider, _dek, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task WriteAsync_PreCancelledToken_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _store.WriteAsync(_provider, _dek, SamplePayload(), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task TryReadAsync_ProviderUnreachableOnList_ReturnsOkNull()
    {
        var faulty = new FaultInjectingStorageProvider(_provider);
        faulty.FailNextListWith(ErrorCode.ProviderUnreachable);

        var result = await _store.TryReadAsync(faulty, _dek, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task TryReadAsync_TokenRevokedOnList_ReturnsOkNull()
    {
        var faulty = new FaultInjectingStorageProvider(_provider);
        faulty.FailNextListWith(ErrorCode.TokenRevoked);

        var result = await _store.TryReadAsync(faulty, _dek, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task TryReadAsync_ProviderUnreachableOnDownload_ReturnsOkNull()
    {
        // Write a real witness so the list step finds it, then fault-inject the download.
        Assert.True((await _store.WriteAsync(
            _provider, _dek, SamplePayload(), CancellationToken.None)).Success);

        var faulty = new FaultInjectingStorageProvider(_provider);
        faulty.FailNextDownloadWith(ErrorCode.ProviderUnreachable);

        var result = await _store.TryReadAsync(faulty, _dek, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Value);
    }

    // ── Write fault-path: orphan-session cleanup ─────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_FailedBegin_ReturnsStagingFailed_NoStagingLeak()
    {
        var faulty = new FaultInjectingStorageProvider(_provider);
        faulty.FailNextBeginWith(ErrorCode.ProviderUnreachable);

        var result = await _store.WriteAsync(faulty, _dek, SamplePayload(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.StagingFailed, result.Error!.Code);

        // No session was opened, so there should be no staging artefacts on disk.
        var stagingDir = Path.Combine(_root, ".flashskink-staging");
        if (Directory.Exists(stagingDir))
        {
            Assert.Empty(Directory.EnumerateFiles(stagingDir));
        }
    }

    [Fact]
    public async Task WriteAsync_FailedRange_AbortsSession_NoStagingLeak()
    {
        var faulty = new FaultInjectingStorageProvider(_provider);
        faulty.FailNextRangeWith(ErrorCode.UploadFailed);

        var result = await _store.WriteAsync(faulty, _dek, SamplePayload(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.StagingFailed, result.Error!.Code);

        // AbortUploadAsync in the finally block should have cleared the staging slot.
        var stagingDir = Path.Combine(_root, ".flashskink-staging");
        if (Directory.Exists(stagingDir))
        {
            Assert.Empty(Directory.EnumerateFiles(stagingDir));
        }
    }

    [Fact]
    public async Task WriteAsync_FailedFinalise_AbortsSession()
    {
        var faulty = new FaultInjectingStorageProvider(_provider);
        faulty.FailNextFinaliseWith(ErrorCode.UploadFailed);

        var result = await _store.WriteAsync(faulty, _dek, SamplePayload(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.StagingFailed, result.Error!.Code);

        // The witness file should NOT exist because finalisation failed and the session
        // was aborted. Test passes if the file is missing OR present with no leakage.
        Assert.False(File.Exists(Path.Combine(_root, "_witness", "current.enc")));
    }
}
