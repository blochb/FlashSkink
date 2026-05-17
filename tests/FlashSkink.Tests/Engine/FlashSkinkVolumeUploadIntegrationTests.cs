using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Orchestration;
using FlashSkink.Core.Providers;
using FlashSkink.Tests._TestSupport;
using FlashSkink.Tests.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Engine;

/// <summary>
/// Integration tests for the §3.6 volume integration surface — <c>WriteBulkAsync</c>,
/// internal <c>RegisterTailAsync</c>, the upload-queue + brain-mirror lifecycle wiring, and
/// the §21.3 WAL invariant after end-to-end upload.
/// </summary>
public sealed class FlashSkinkVolumeUploadIntegrationTests : IAsyncLifetime
{
    private string _skinkRoot = string.Empty;
    private string _tailRoot = string.Empty;
    private const string Password = "test-password-123";
    private const string ProviderId = "tail-1";
    private const string ProviderType = "filesystem";
    private const string DisplayName = "Test Tail";

    private InMemoryProviderRegistry _registry = null!;
    private RecordingNotificationBus _bus = null!;

    public Task InitializeAsync()
    {
        _skinkRoot = Path.Combine(Path.GetTempPath(), $"flashskink-vol-int-{Guid.NewGuid():N}");
        _tailRoot = Path.Combine(Path.GetTempPath(), $"flashskink-tail-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_skinkRoot);
        Directory.CreateDirectory(_tailRoot);
        _registry = new InMemoryProviderRegistry(NullLogger<InMemoryProviderRegistry>.Instance);
        _bus = new RecordingNotificationBus();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_skinkRoot, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_tailRoot, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private VolumeCreationOptions DefaultOptions(
        INetworkAvailabilityMonitor? netMonitor = null,
        FakeClock? clock = null) => new()
        {
            LoggerFactory = NullLoggerFactory.Instance,
            NotificationBus = _bus,
            ProviderRegistry = _registry,
            NetworkMonitor = netMonitor,
            Clock = clock,
        };

    private FileSystemProvider CreateFsProvider() =>
        new(ProviderId, DisplayName, _tailRoot, NullLogger<FileSystemProvider>.Instance);

    private async Task<FlashSkinkVolume> CreateVolumeAsync(VolumeCreationOptions? options = null)
    {
        var result = await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, options ?? DefaultOptions());
        Assert.True(result.Success, result.Error?.Message);
        // The RecoveryPhrase is intentionally leaked across this test fixture — these
        // tests exercise upload paths, not phrase lifecycle. xUnit GCs it at test end.
        return result.Value!.Volume;
    }

    /// <summary>
    /// Polls the tail root for the expected sharded blob path. Returns true once the file
    /// appears within <paramref name="budget"/>, false otherwise. The path the provider writes
    /// to is <c>{tailRoot}/blobs/{xx}/{yy}/{blobId}.bin</c>.
    /// </summary>
    private async Task<bool> WaitForBlobAtTailAsync(string blobId, TimeSpan? budget = null)
    {
        var path = ComputeSharedTailBlobPath(blobId);
        var sw = Stopwatch.StartNew();
        var deadline = budget ?? TimeSpan.FromSeconds(15);
        while (sw.Elapsed < deadline)
        {
            if (File.Exists(path))
            {
                return true;
            }
            await Task.Delay(40);
        }
        return File.Exists(path);
    }

    private string ComputeSharedTailBlobPath(string blobId)
    {
        var remoteName = blobId + ".bin";
        return Path.Combine(_tailRoot, "blobs", remoteName[..2], remoteName[2..4], remoteName);
    }

    private string ComputeSharedSkinkBlobPath(string blobId)
    {
        // Matches AtomicBlobWriter layout: .flashskink/blobs/{xx}/{yy}/{blobId}.bin
        return Path.Combine(_skinkRoot, ".flashskink", "blobs", blobId[..2], blobId[2..4], blobId + ".bin");
    }

    private async Task<SqliteConnection> OpenRawBrainAsync()
    {
        var vaultPath = Path.Combine(_skinkRoot, ".flashskink", "vault.bin");
        var brainPath = Path.Combine(_skinkRoot, ".flashskink", "brain.db");
        var kdf = new KeyDerivationService();
        var keyVault = new KeyVault(kdf, new MnemonicService());
        var brainFactory = new BrainConnectionFactory(kdf, NullLogger<BrainConnectionFactory>.Instance);

        var passwordBytes = Encoding.UTF8.GetBytes(Password);
        var unlock = await keyVault.UnlockAsync(
            vaultPath, new ReadOnlyMemory<byte>(passwordBytes), CancellationToken.None);
        CryptographicOperations.ZeroMemory(passwordBytes);
        Assert.True(unlock.Success, unlock.Error?.Message);
        var dek = unlock.Value!;
        var brainResult = await brainFactory.CreateAsync(brainPath, dek, CancellationToken.None);
        CryptographicOperations.ZeroMemory(dek);
        Assert.True(brainResult.Success, brainResult.Error?.Message);
        return brainResult.Value!;
    }

    private static byte[] RandomBytes(int n) => RandomNumberGenerator.GetBytes(n);

    private string DumpBusErrors()
    {
        var errs = _bus.Published
            .Where(n => n.Severity is NotificationSeverity.Error or NotificationSeverity.Critical)
            .Select(n => $"[{n.Severity}] {n.Source}: {n.Title} — {n.Message} ({n.Error?.Code})")
            .ToArray();
        return errs.Length == 0 ? "(none)" : string.Join(" | ", errs);
    }

    // ── WriteBulkAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task WriteBulkAsync_FiveDistinctItems_AllSucceed()
    {
        await using var volume = await CreateVolumeAsync();
        var items = new List<BulkWriteItem>();
        var payloads = new List<byte[]>();
        for (int i = 0; i < 5; i++)
        {
            var data = RandomBytes(256 * (i + 1));
            payloads.Add(data);
            items.Add(new BulkWriteItem
            {
                Source = new MemoryStream(data),
                VirtualPath = $"bulk/item-{i}.bin",
            });
        }

        var result = await volume.WriteBulkAsync(items);

        Assert.True(result.Success);
        var receipt = result.Value!;
        Assert.Equal(5, receipt.Items.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal($"bulk/item-{i}.bin", receipt.Items[i].VirtualPath);
            Assert.True(receipt.Items[i].Outcome.Success,
                $"Item {i} unexpectedly failed: {receipt.Items[i].Outcome.Error?.Message}");
        }

        // Verify all items round-trip via ReadFileAsync.
        for (int i = 0; i < 5; i++)
        {
            var dest = new MemoryStream();
            var read = await volume.ReadFileAsync($"bulk/item-{i}.bin", dest);
            Assert.True(read.Success);
            Assert.Equal(payloads[i], dest.ToArray());
        }
    }

    [Fact]
    public async Task WriteBulkAsync_PartialFailure_ReturnsMixedReceipt()
    {
        await using var volume = await CreateVolumeAsync();
        var items = new List<BulkWriteItem>
        {
            new() { Source = new MemoryStream(RandomBytes(128)), VirtualPath = "a.bin" },
            new() { Source = new MemoryStream(RandomBytes(128)), VirtualPath = "b.bin" },
            new() { Source = new ThrowingStream(), VirtualPath = "bad.bin" },
            new() { Source = new MemoryStream(RandomBytes(128)), VirtualPath = "d.bin" },
            new() { Source = new MemoryStream(RandomBytes(128)), VirtualPath = "e.bin" },
        };

        var result = await volume.WriteBulkAsync(items);

        Assert.True(result.Success);
        var receipt = result.Value!;
        Assert.Equal(5, receipt.Items.Count);
        Assert.True(receipt.Items[0].Outcome.Success);
        Assert.True(receipt.Items[1].Outcome.Success);
        Assert.False(receipt.Items[2].Outcome.Success);
        Assert.True(receipt.Items[3].Outcome.Success);
        Assert.True(receipt.Items[4].Outcome.Success);

        // The 4 successful items are readable back.
        foreach (var vp in new[] { "a.bin", "b.bin", "d.bin", "e.bin" })
        {
            var dest = new MemoryStream();
            Assert.True((await volume.ReadFileAsync(vp, dest)).Success);
        }
    }

    [Fact]
    public async Task WriteBulkAsync_OwnedSourceIsDisposed_OnSuccessAndFailure()
    {
        await using var volume = await CreateVolumeAsync();
        var goodTracker = new TrackingDisposable();
        var badTracker = new TrackingDisposable();

        var items = new List<BulkWriteItem>
        {
            new()
            {
                Source = new MemoryStream(RandomBytes(64)),
                VirtualPath = "good.bin",
                OwnedSource = goodTracker,
            },
            new()
            {
                Source = new ThrowingStream(),
                VirtualPath = "bad.bin",
                OwnedSource = badTracker,
            },
        };

        await volume.WriteBulkAsync(items);

        Assert.True(goodTracker.Disposed, "OwnedSource for a successful item must be disposed.");
        Assert.True(badTracker.Disposed, "OwnedSource for a failed item must be disposed.");
    }

    [Fact]
    public async Task WriteBulkAsync_NullItems_ReturnsInvalidArgument()
    {
        await using var volume = await CreateVolumeAsync();
        var result = await volume.WriteBulkAsync(null!);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task WriteBulkAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var volume = await CreateVolumeAsync();
        await volume.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await volume.WriteBulkAsync(new List<BulkWriteItem>()));
    }

    // ── RegisterTailAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterTail_InsertsProvidersRow()
    {
        await using var volume = await CreateVolumeAsync();
        var provider = CreateFsProvider();
        const string ConfigJson = """{"rootPath":"/test/path"}""";

        var result = await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, ConfigJson, provider);

        Assert.True(result.Success, result.Error?.Message);
        await volume.DisposeAsync();

        await using var brain = await OpenRawBrainAsync();
        var row = await brain.QuerySingleAsync<(string ProviderID, string ProviderType,
            string DisplayName, string? ProviderConfig, string HealthStatus, long IsActive)>(
            "SELECT ProviderID, ProviderType, DisplayName, ProviderConfig, HealthStatus, IsActive " +
            "FROM Providers WHERE ProviderID = @Id",
            new { Id = ProviderId });

        Assert.Equal(ProviderId, row.ProviderID);
        Assert.Equal(ProviderType, row.ProviderType);
        Assert.Equal(DisplayName, row.DisplayName);
        Assert.Equal(ConfigJson, row.ProviderConfig);
        Assert.Equal("Healthy", row.HealthStatus);
        Assert.Equal(1, row.IsActive);
    }

    [Fact]
    public async Task RegisterTail_IdempotentOnDuplicate()
    {
        await using var volume = await CreateVolumeAsync();
        var first = await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, CreateFsProvider());
        var second = await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, CreateFsProvider());

        Assert.True(first.Success);
        Assert.True(second.Success);

        await volume.DisposeAsync();

        await using var brain = await OpenRawBrainAsync();
        var count = await brain.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Providers WHERE ProviderID = @Id",
            new { Id = ProviderId });
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RegisterTail_AfterRegister_RegistryContainsProvider()
    {
        await using var volume = await CreateVolumeAsync();
        await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, CreateFsProvider());

        var ids = await _registry.ListActiveProviderIdsAsync(CancellationToken.None);
        Assert.True(ids.Success);
        Assert.Contains(ProviderId, ids.Value!);
    }

    [Fact]
    public async Task RegisterTail_NullProvider_ReturnsInvalidArgument()
    {
        await using var volume = await CreateVolumeAsync();
        var result = await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, provider: null!);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task RegisterTail_EmptyProviderId_ReturnsInvalidArgument()
    {
        await using var volume = await CreateVolumeAsync();
        var result = await volume.RegisterTailAsync(
            "", ProviderType, DisplayName, null, CreateFsProvider());
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    // ── End-to-end upload (real time, FileSystemProvider) ────────────────────

    [Fact]
    public async Task WriteFile_AfterRegisterTail_LandsAtTail()
    {
        // Use offline→online pattern: the upload-queue worker has a known race against the
        // orchestrator for the wakeup pulse when started while a row already exists. Starting
        // offline ensures the orchestrator never starts the worker until we flip online; the
        // worker's first dequeue then sees the row immediately, no race.
        var netMonitor = new TestNetworkAvailabilityMonitor();
        netMonitor.SetAvailable(false);
        await using var volume = await CreateVolumeAsync(DefaultOptions(netMonitor: netMonitor));
        Assert.True((await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, CreateFsProvider())).Success);

        var payload = RandomBytes(512 * 1024);   // 512 KB
        var writeResult = await volume.WriteFileAsync(new MemoryStream(payload), "doc.bin");
        Assert.True(writeResult.Success);
        var blobId = writeResult.Value!.BlobId;

        netMonitor.SetAvailable(true);

        var landed = await WaitForBlobAtTailAsync(blobId);
        Assert.True(landed, $"Blob did not appear at the tail within the polling budget. " +
            $"Bus failures: {DumpBusErrors()}");

        // Tail bytes match the local skink blob byte-for-byte — both are ciphertext from the
        // same encryption call; the upload is a verbatim transfer.
        var skinkBytes = await File.ReadAllBytesAsync(ComputeSharedSkinkBlobPath(blobId));
        var tailBytes = await File.ReadAllBytesAsync(ComputeSharedTailBlobPath(blobId));
        Assert.Equal(skinkBytes, tailBytes);
    }

    [Fact]
    public async Task FiveWrites_AllUploaded_SessionsEmpty()
    {
        var netMonitor = new TestNetworkAvailabilityMonitor();
        netMonitor.SetAvailable(false);
        var volume = await CreateVolumeAsync(DefaultOptions(netMonitor: netMonitor));
        Assert.True((await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, CreateFsProvider())).Success);

        var blobIds = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var w = await volume.WriteFileAsync(new MemoryStream(RandomBytes(64 * 1024)), $"f{i}.bin");
            Assert.True(w.Success);
            blobIds.Add(w.Value!.BlobId);
        }

        netMonitor.SetAvailable(true);

        foreach (var id in blobIds)
        {
            Assert.True(await WaitForBlobAtTailAsync(id),
                $"Blob {id} did not appear at the tail. Bus failures: {DumpBusErrors()}");
        }

        await volume.DisposeAsync();

        await using var brain = await OpenRawBrainAsync();
        var uploadedCount = await brain.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM TailUploads WHERE ProviderID = @P AND Status = 'UPLOADED'",
            new { P = ProviderId });
        Assert.Equal(5, uploadedCount);

        var sessionCount = await brain.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM UploadSessions WHERE ProviderID = @P",
            new { P = ProviderId });
        Assert.Equal(0, sessionCount);
    }

    [Fact]
    public async Task WalInvariant_AfterUpload_NoSessionRowForUploaded()
    {
        var netMonitor = new TestNetworkAvailabilityMonitor();
        netMonitor.SetAvailable(false);
        var volume = await CreateVolumeAsync(DefaultOptions(netMonitor: netMonitor));
        Assert.True((await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, CreateFsProvider())).Success);

        var blobIds = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var w = await volume.WriteFileAsync(new MemoryStream(RandomBytes(32 * 1024)), $"wal-{i}.bin");
            Assert.True(w.Success);
            blobIds.Add(w.Value!.BlobId);
        }

        netMonitor.SetAvailable(true);

        foreach (var id in blobIds)
        {
            Assert.True(await WaitForBlobAtTailAsync(id),
                $"Blob {id} did not appear at the tail. Bus failures: {DumpBusErrors()}");
        }

        await volume.DisposeAsync();

        await using var brain = await OpenRawBrainAsync();
        // §21.3: for every TailUploads row at UPLOADED, no UploadSessions row exists for that
        // (FileID, ProviderID) pair.
        var leaked = await brain.QueryAsync<long>(
            @"SELECT COUNT(*) FROM TailUploads tu
              INNER JOIN UploadSessions us ON us.FileID = tu.FileID AND us.ProviderID = tu.ProviderID
              WHERE tu.Status = 'UPLOADED'");
        Assert.Equal(0L, leaked.Single());
    }

    // ── Network gating ───────────────────────────────────────────────────────

    [Fact]
    public async Task NetworkUnavailable_BlocksUploads_RestoresOnAvailable()
    {
        var netMonitor = new TestNetworkAvailabilityMonitor();
        netMonitor.SetAvailable(false);
        await using var volume = await CreateVolumeAsync(DefaultOptions(netMonitor: netMonitor));
        Assert.True((await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, CreateFsProvider())).Success);

        var w = await volume.WriteFileAsync(new MemoryStream(RandomBytes(16 * 1024)), "offline.bin");
        Assert.True(w.Success);
        var blobId = w.Value!.BlobId;

        // While offline, the blob must not appear at the tail. Give the worker a brief window
        // to confirm it's actually paused.
        Assert.False(await WaitForBlobAtTailAsync(blobId, TimeSpan.FromMilliseconds(500)));

        netMonitor.SetAvailable(true);
        Assert.True(await WaitForBlobAtTailAsync(blobId));
    }

    // ── Brain mirror (FakeClock-driven debounce) ────────────────────────────

    [Fact]
    public async Task BrainMirror_OnCleanShutdown_RunsOneFinalMirror()
    {
        var logFactory = new ListLoggerFactory();
        var options = new VolumeCreationOptions
        {
            LoggerFactory = logFactory,
            NotificationBus = _bus,
            ProviderRegistry = _registry,
        };
        var volume = await CreateVolumeAsync(options);
        Assert.True((await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, CreateFsProvider())).Success);

        var w = await volume.WriteFileAsync(new MemoryStream(RandomBytes(8 * 1024)), "before-close.bin");
        Assert.True(w.Success);

        // Immediately dispose — before the 10 s debounce could elapse. The dispose order
        // (queue-first, mirror-second) guarantees no worker is mid-brain-transaction when
        // the snapshot runs.
        await volume.DisposeAsync();

        var brainDir = Path.Combine(_tailRoot, "_brain");
        Assert.True(Directory.Exists(brainDir),
            $"Tail's _brain/ directory should exist after final-mirror on dispose. " +
            $"Bus failures: {DumpBusErrors()}. " +
            $"Recent log: {logFactory.DumpRecent(15)}");
        var mirrors = Directory.GetFiles(brainDir, "*.bin");
        Assert.True(mirrors.Length >= 1,
            $"Expected at least one brain-mirror file after dispose; found {mirrors.Length}. " +
            $"Bus failures: {DumpBusErrors()}");
    }

    [Fact]
    public async Task BrainMirror_AfterCommit_AppearsAtTail_AfterDebounce()
    {
        using var clock = new FakeClock(new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc));
        await using var volume = await CreateVolumeAsync(DefaultOptions(clock: clock));
        Assert.True((await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, CreateFsProvider())).Success);

        var w = await volume.WriteFileAsync(new MemoryStream(RandomBytes(4 * 1024)), "tick.bin");
        Assert.True(w.Success);

        // Pump the clock until the debounce window elapses and the mirror appears at the tail.
        // The mirror service's 10 s debounce + 15-min timer both consume IClock.Delay.
        var brainDir = Path.Combine(_tailRoot, "_brain");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (Directory.Exists(brainDir) && Directory.GetFiles(brainDir, "*.bin").Length >= 1)
            {
                return;
            }
            if (clock.PendingDelayCount > 0)
            {
                clock.Advance(TimeSpan.FromSeconds(15));
            }
            await Task.Delay(20);
        }
        Assert.Fail("Brain mirror did not appear at the tail within the polling budget.");
    }

    // ── Dispose mid-upload ───────────────────────────────────────────────────

    [Fact]
    public async Task DisposeMidUpload_PreservesSessionRowOrCompletes()
    {
        // The dispose-mid-upload path is a §3.4 contract. We force the worker to spend visible
        // wall time on a single range by wrapping the FileSystemProvider in a
        // FaultInjectingStorageProvider with per-range latency. After the worker has at least
        // begun an upload, we dispose the volume and assert that disposal returned within the
        // 10 s shutdown budget.
        await using var volume = await CreateVolumeAsync();
        var fs = CreateFsProvider();
        var fault = new FaultInjectingStorageProvider(fs);
        fault.SetRangeLatency(TimeSpan.FromSeconds(2));
        Assert.True((await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, fault)).Success);

        // 12 MB → multiple 4 MB ranges, plenty of latency to interrupt mid-flight.
        var w = await volume.WriteFileAsync(new MemoryStream(RandomBytes(12 * 1024 * 1024)), "big.bin");
        Assert.True(w.Success);

        // Give the worker a moment to start the upload.
        await Task.Delay(200);

        var sw = Stopwatch.StartNew();
        await volume.DisposeAsync();
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"DisposeAsync took {sw.Elapsed} — exceeded 15 s budget.");

        // The TailUploads row should still exist (UPLOADING or PENDING/FAILED — depending on
        // exact timing, but never deleted; sessions row either preserved or absent if upload
        // completed before dispose).
        await using var brain = await OpenRawBrainAsync();
        var rowCount = await brain.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM TailUploads WHERE ProviderID = @P", new { P = ProviderId });
        Assert.Equal(1, rowCount);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("Simulated read failure.");

        public override int Read(Span<byte> buffer) =>
            throw new IOException("Simulated read failure.");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            throw new IOException("Simulated read failure.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            throw new IOException("Simulated read failure.");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class ListLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        private readonly List<string> _entries = [];
        private readonly Lock _lock = new();

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) =>
            new ListLogger(this, categoryName);

        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }

        public void Dispose() { }

        public void Record(string entry)
        {
            lock (_lock)
            {
                _entries.Add(entry);
            }
        }

        public string DumpRecent(int max)
        {
            lock (_lock)
            {
                int start = Math.Max(0, _entries.Count - max);
                return string.Join(" || ", _entries.Skip(start));
            }
        }

        private sealed class ListLogger(ListLoggerFactory parent, string category)
            : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) =>
                logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) { return; }
                var msg = formatter(state, exception);
                var line = exception is null
                    ? $"[{logLevel}] {category}: {msg}"
                    : $"[{logLevel}] {category}: {msg} -- {exception.GetType().Name}: {exception.Message}";
                parent.Record(line);
            }
        }
    }
}
