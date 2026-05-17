using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Engine;
using FlashSkink.Core.Providers;
using FlashSkink.Core.Upload;
using FlashSkink.Tests._TestSupport;
using FlashSkink.Tests.Metadata;
using FlashSkink.Tests.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Engine;

public sealed class BrainMirrorServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _skinkRoot;
    private readonly string _tailRoot;
    private readonly SqliteConnection _connection;
    private readonly byte[] _dek;
    private readonly InMemoryProviderRegistry _registry;
    private readonly RecordingNotificationBus _bus;
    private readonly FakeClock _clock;
    private readonly FileSystemProvider _fsProvider;
    private readonly RecordingLogger<BrainMirrorService> _serviceLogger;

    private const string ProviderId = "tail-1";

    public BrainMirrorServiceTests()
    {
        _skinkRoot = Path.Combine(Path.GetTempPath(), "flashskink-tests",
            "skink-" + Guid.NewGuid().ToString("N"));
        _tailRoot = Path.Combine(Path.GetTempPath(), "flashskink-tests",
            "tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_skinkRoot);
        Directory.CreateDirectory(_tailRoot);

        _dek = RandomNumberGenerator.GetBytes(32);
        // Production opens the brain with SQLCipher keyed off the DEK; the mirror's
        // snapshot path now requires source-and-dest keys to match (see BrainMirrorService
        // SnapshotAsync). The in-memory connection must therefore be keyed the same way
        // as production. Plain-SQLite was insufficient and masked the SQLCipher mismatch
        // until §3.6 surfaced it.
        _connection = CreateKeyedInMemoryConnection(_dek);
        _registry = new InMemoryProviderRegistry(NullLogger<InMemoryProviderRegistry>.Instance);
        _bus = new RecordingNotificationBus();
        _clock = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _fsProvider = new FileSystemProvider(ProviderId, "Test Tail", _tailRoot,
            NullLogger<FileSystemProvider>.Instance);
        _serviceLogger = new RecordingLogger<BrainMirrorService>();
    }

    public async Task InitializeAsync()
    {
        await BrainTestHelper.ApplySchemaAsync(_connection);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _connection.Dispose();
        _clock.Dispose();
        if (Directory.Exists(_skinkRoot))
        {
            try { Directory.Delete(_skinkRoot, recursive: true); } catch { }
        }
        if (Directory.Exists(_tailRoot))
        {
            try { Directory.Delete(_tailRoot, recursive: true); } catch { }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static SqliteConnection CreateKeyedInMemoryConnection(byte[] dek)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        Span<byte> brainKey = stackalloc byte[32];
        new KeyDerivationService().DeriveBrainKey(dek, brainKey);
        using (var keyCmd = connection.CreateCommand())
        {
            keyCmd.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(brainKey)}'\"";
            keyCmd.ExecuteNonQuery();
        }
        CryptographicOperations.ZeroMemory(brainKey);

        using var fk = connection.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys = ON";
        fk.ExecuteNonQuery();
        return connection;
    }

    private BrainMirrorService CreateService(long? maxBytes = null)
    {
        if (maxBytes is null)
        {
            return new BrainMirrorService(_connection, _dek, _skinkRoot,
                _registry, _bus, _clock, _serviceLogger);
        }
        return new BrainMirrorService(_connection, _dek, _skinkRoot,
            _registry, _bus, _clock, _serviceLogger, maxBytes.Value);
    }

    private string TailBrainDir() => Path.Combine(_tailRoot, "_brain");

    private string[] TailBrainEntries()
    {
        var dir = TailBrainDir();
        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }
        var files = Directory.GetFiles(dir, "*.bin");
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    /// <summary>
    /// Waits for the production log entry signalling that a brain mirror cycle ran to
    /// completion (emitted by <c>RunOneCycleAsync</c> after the per-tail loop). Returns
    /// the instant the log fires — cadence-free, so it does not become flaky under CI
    /// thread-pool starvation. Callers then assert on tail file-system state, which has
    /// already been committed by the time the log line is emitted.
    /// </summary>
    private Task<LogEntry> WaitForCycleCompletedAsync(TimeSpan? budget = null) =>
        _serviceLogger.WaitForAsync(
            e => e.Level == LogLevel.Information
              && e.Message.StartsWith("Brain mirror cycle completed", StringComparison.Ordinal),
            budget ?? TimeSpan.FromSeconds(10));

    /// <summary>
    /// Background <c>IClock</c> pump for tests whose worker loops idle on <c>_clock.Delay</c>
    /// (the timer cycle: 15-minute interval; the debounce cycle: 10-second sliding window).
    /// While alive, releases any pending <see cref="FakeClock.PendingDelayCount"/>. Construct
    /// inside an <c>await using</c> block for the duration of <see cref="WaitForCycleCompletedAsync"/>.
    /// </summary>
    private sealed class ClockPump : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pumpTask;

        public ClockPump(FakeClock clock, TimeSpan advanceChunk)
        {
            CancellationToken ct = _cts.Token;
            _pumpTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    if (clock.PendingDelayCount > 0)
                    {
                        clock.Advance(advanceChunk);
                    }
                    try
                    {
                        await Task.Delay(20, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, ct);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on graceful exit.
            }
            catch (Exception)
            {
                // Pump faulted — swallow so the underlying test failure propagates out of
                // the `await using` block instead of being masked by cleanup. The pump is
                // test infrastructure, not the SUT.
            }
            _cts.Dispose();
        }
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_FirstCall_ReturnsOk()
    {
        await using var sut = CreateService();
        var result = sut.Start(CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Start_SecondCall_IsIdempotent()
    {
        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        var second = sut.Start(CancellationToken.None);
        Assert.True(second.Success);
    }

    [Fact]
    public async Task Start_AfterDispose_ReturnsObjectDisposed()
    {
        var sut = CreateService();
        await sut.DisposeAsync();

        var result = sut.Start(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ObjectDisposed, result.Error!.Code);
    }

    [Fact]
    public async Task DisposeAsync_BeforeStart_ReturnsCleanly()
    {
        var sut = CreateService();
        await sut.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var sut = CreateService();
        sut.Start(CancellationToken.None);
        await sut.DisposeAsync();
        await sut.DisposeAsync();
    }

    [Fact]
    public void NotifyWriteCommitted_BeforeStart_IsNoOp()
    {
        var sut = CreateService();
        // Must not throw, must not pulse anything externally observable.
        sut.NotifyWriteCommitted();
    }

    // ── TriggerMirrorAsync direct path ───────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerMirrorAsync_NoProviders_ReturnsOkWithoutMirror()
    {
        await using var sut = CreateService();

        var result = await sut.TriggerMirrorAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(TailBrainEntries());
    }

    [Fact]
    public async Task TriggerMirrorAsync_OneProvider_WritesOneBrainObject()
    {
        _registry.Register(ProviderId, _fsProvider);
        await using var sut = CreateService();

        var result = await sut.TriggerMirrorAsync(CancellationToken.None);

        Assert.True(result.Success);
        var entries = TailBrainEntries();
        Assert.Single(entries);
        Assert.EndsWith(".bin", entries[0]);
        Assert.StartsWith("2026", Path.GetFileName(entries[0]));
    }

    [Fact]
    public async Task TriggerMirrorAsync_MirrorHasCorrectMagicAndVersion()
    {
        _registry.Register(ProviderId, _fsProvider);
        await using var sut = CreateService();

        await sut.TriggerMirrorAsync(CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(TailBrainEntries()[0]);
        Assert.True(bytes.Length > BrainMirrorHeader.Size);
        // "FSBM" little-endian
        Assert.Equal(0x46, bytes[0]);
        Assert.Equal(0x53, bytes[1]);
        Assert.Equal(0x42, bytes[2]);
        Assert.Equal(0x4D, bytes[3]);
        Assert.Equal(0x01, bytes[4]); // version LE
        Assert.Equal(0x00, bytes[5]);
    }

    [Fact]
    public async Task TriggerMirrorAsync_MirrorDecryptsBackToBrainContent()
    {
        // Seed an identifying row so we can verify content survived round-trip.
        BrainTestHelper.InsertTestFile(_connection, "round-trip-file", name: "round-trip-marker");

        _registry.Register(ProviderId, _fsProvider);
        await using var sut = CreateService();

        var trigger = await sut.TriggerMirrorAsync(CancellationToken.None);
        Assert.True(trigger.Success);

        var entries = TailBrainEntries();
        Assert.Single(entries);
        var payload = await File.ReadAllBytesAsync(entries[0]);

        // Layout: 16B header || 12B nonce || ciphertext || 16B tag.
        const int NonceSize = 12;
        const int TagSize = 16;
        int headerEnd = BrainMirrorHeader.Size;
        int nonceEnd = headerEnd + NonceSize;
        int ciphertextLen = payload.Length - nonceEnd - TagSize;
        Assert.True(ciphertextLen > 0);

        var header = payload.AsSpan(0, BrainMirrorHeader.Size);
        var nonce = payload.AsSpan(headerEnd, NonceSize);
        var ciphertext = payload.AsSpan(nonceEnd, ciphertextLen);
        var tag = payload.AsSpan(nonceEnd + ciphertextLen, TagSize);

        var plaintext = new byte[ciphertextLen];
        using (var aes = new AesGcm(_dek, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData: header);
        }

        // Write plaintext to disk and open as a SQLite database to verify the marker.
        var recoveredPath = Path.Combine(_skinkRoot, "recovered.db");
        await File.WriteAllBytesAsync(recoveredPath, plaintext);

        var csb = new SqliteConnectionStringBuilder { DataSource = recoveredPath, Pooling = false };
        using var recovered = new SqliteConnection(csb.ConnectionString);
        recovered.Open();
        // The recovered DB is SQLCipher-encrypted with the brain key (the source brain was
        // keyed by the test fixture; SnapshotAsync requires source/dest keys to match).
        // Set the same brain key here so the SELECT can read the encrypted page.
        Span<byte> recoveredBrainKey = stackalloc byte[32];
        new KeyDerivationService().DeriveBrainKey(_dek, recoveredBrainKey);
        using (var keyCmd = recovered.CreateCommand())
        {
            keyCmd.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(recoveredBrainKey)}'\"";
            keyCmd.ExecuteNonQuery();
        }
        CryptographicOperations.ZeroMemory(recoveredBrainKey);

        var name = await recovered.QuerySingleOrDefaultAsync<string>(
            "SELECT Name FROM Files WHERE FileID = 'round-trip-file'");
        Assert.Equal("round-trip-marker", name);
    }

    [Fact]
    public async Task TriggerMirrorAsync_AadTimestampTampering_FailsDecryption()
    {
        _registry.Register(ProviderId, _fsProvider);
        await using var sut = CreateService();
        await sut.TriggerMirrorAsync(CancellationToken.None);

        var path = TailBrainEntries()[0];
        var payload = await File.ReadAllBytesAsync(path);

        // Flip a byte in the AAD timestamp portion (bytes 8..15 of the header).
        payload[10] ^= 0xFF;

        const int NonceSize = 12;
        const int TagSize = 16;
        int headerEnd = BrainMirrorHeader.Size;
        int nonceEnd = headerEnd + NonceSize;
        int ciphertextLen = payload.Length - nonceEnd - TagSize;
        var header = payload.AsSpan(0, BrainMirrorHeader.Size);
        var nonce = payload.AsSpan(headerEnd, NonceSize);
        var ciphertext = payload.AsSpan(nonceEnd, ciphertextLen);
        var tag = payload.AsSpan(nonceEnd + ciphertextLen, TagSize);
        var plaintext = new byte[ciphertextLen];

        using var aes = new AesGcm(_dek, TagSize);
        bool threw = false;
        try
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData: header);
        }
        catch (CryptographicException)
        {
            threw = true;
        }
        Assert.True(threw, "Decryption should fail when the AAD timestamp is tampered.");
    }

    [Fact]
    public async Task TriggerMirrorAsync_FreshNoncePerCycle()
    {
        _registry.Register(ProviderId, _fsProvider);
        await using var sut = CreateService();

        // Generate 3 mirrors at distinct timestamps.
        await sut.TriggerMirrorAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await sut.TriggerMirrorAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await sut.TriggerMirrorAsync(CancellationToken.None);

        var entries = TailBrainEntries();
        Assert.Equal(3, entries.Length);

        var nonces = new HashSet<string>();
        foreach (var path in entries)
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var nonce = bytes.AsSpan(BrainMirrorHeader.Size, 12).ToArray();
            Assert.True(nonces.Add(Convert.ToHexString(nonce)),
                "Nonce collision across cycles — RNG misuse.");
        }
    }

    // ── Retention ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerMirrorAsync_FiveCycles_OnlyThreeMostRecentRetained()
    {
        _registry.Register(ProviderId, _fsProvider);
        await using var sut = CreateService();

        var timestamps = new List<DateTime>();
        for (int i = 0; i < 5; i++)
        {
            timestamps.Add(_clock.UtcNow);
            var r = await sut.TriggerMirrorAsync(CancellationToken.None);
            Assert.True(r.Success);
            _clock.Advance(TimeSpan.FromMinutes(16));
        }

        var entries = TailBrainEntries();
        Assert.Equal(UploadConstants.BrainMirrorRollingCount, entries.Length);

        // The remaining filenames should be the 3 most-recent timestamps.
        var expectedSuffixes = timestamps
            .TakeLast(UploadConstants.BrainMirrorRollingCount)
            .Select(t => t.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + ".bin")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        var actualNames = entries
            .Select(Path.GetFileName)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedSuffixes, actualNames);
    }

    // ── Trigger: debounce + timer + dispose ──────────────────────────────────────────────────

    [Fact]
    public async Task NotifyWriteCommitted_OnceThenWindowQuiet_RunsOneMirror()
    {
        _registry.Register(ProviderId, _fsProvider);
        await using var sut = CreateService();
        sut.Start(CancellationToken.None);

        sut.NotifyWriteCommitted();

        // The debounce loop awaits _clock.Delay(10s) for the sliding-quiet window before
        // running the cycle; the pump releases it. Chunk is 11s so a single advance is
        // enough to satisfy any single quiet window.
        await using (new ClockPump(_clock, TimeSpan.FromSeconds(11)))
        {
            await WaitForCycleCompletedAsync();
        }

        Assert.Single(TailBrainEntries());
    }

    [Fact]
    public async Task NotifyWriteCommitted_Burst_CoalescesIntoOneMirror()
    {
        _registry.Register(ProviderId, _fsProvider);
        await using var sut = CreateService();
        sut.Start(CancellationToken.None);

        for (int i = 0; i < 5; i++)
        {
            sut.NotifyWriteCommitted();
        }

        await using (new ClockPump(_clock, TimeSpan.FromSeconds(11)))
        {
            await WaitForCycleCompletedAsync();
        }

        // Give the loop a brief extra moment to (incorrectly) produce a second mirror.
        await Task.Delay(100);
        Assert.Single(TailBrainEntries());
    }

    [Fact]
    public async Task TimerAdvance_PastInterval_RunsMirror()
    {
        _registry.Register(ProviderId, _fsProvider);
        await using var sut = CreateService();
        sut.Start(CancellationToken.None);

        // The timer loop awaits _clock.Delay(15min); the pump releases it. Chunk is 16min
        // so a single advance fires the timer.
        await using (new ClockPump(_clock, TimeSpan.FromMinutes(16)))
        {
            await WaitForCycleCompletedAsync();
        }

        Assert.True(TailBrainEntries().Length >= 1);
    }

    [Fact]
    public async Task DisposeAsync_RunsFinalMirror()
    {
        _registry.Register(ProviderId, _fsProvider);
        var sut = CreateService();
        sut.Start(CancellationToken.None);

        // No prior triggers, no clock advance — the only mirror that lands is the dispose
        // final-mirror.
        await sut.DisposeAsync();

        Assert.Single(TailBrainEntries());
    }

    // ── Multi-tail isolation ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MultipleTails_OneFails_OthersSucceed()
    {
        // Tail A — clean.
        var tailARoot = Path.Combine(_tailRoot, "..", "tail-a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tailARoot);
        var tailA = new FileSystemProvider("tail-a", "Tail A", tailARoot,
            NullLogger<FileSystemProvider>.Instance);

        // Tail B — fault-injecting, fails the first range.
        var tailBRoot = Path.Combine(_tailRoot, "..", "tail-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tailBRoot);
        var tailBInner = new FileSystemProvider("tail-b", "Tail B", tailBRoot,
            NullLogger<FileSystemProvider>.Instance);
        var tailBFaulty = new FaultInjectingStorageProvider(tailBInner);
        tailBFaulty.FailNextRange();

        _registry.Register("tail-a", tailA);
        _registry.Register("tail-b", tailBFaulty);

        await using var sut = CreateService();
        var result = await sut.TriggerMirrorAsync(CancellationToken.None);

        try
        {
            Assert.True(result.Success);

            var tailABrain = Path.Combine(tailARoot, "_brain");
            var tailBBrain = Path.Combine(tailBRoot, "_brain");
            Assert.True(Directory.Exists(tailABrain) && Directory.GetFiles(tailABrain, "*.bin").Length == 1,
                "Tail A should have exactly one mirror.");
            Assert.False(Directory.Exists(tailBBrain) && Directory.GetFiles(tailBBrain, "*.bin").Length > 0,
                "Tail B should have no mirror after failure.");

            // Failure on B should have produced an Error notification mentioning the display name.
            Assert.Contains(_bus.Published, n =>
                n.Severity == FlashSkink.Core.Abstractions.Notifications.NotificationSeverity.Error
                && n.Message.Contains("Tail B", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tailARoot)) { try { Directory.Delete(tailARoot, true); } catch { } }
            if (Directory.Exists(tailBRoot)) { try { Directory.Delete(tailBRoot, true); } catch { } }
        }
    }

    // ── Size cap ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerMirrorAsync_SnapshotExceedsCap_FailsAndNotifies()
    {
        _registry.Register(ProviderId, _fsProvider);
        // Cap is 1 byte — guaranteed to be smaller than the (non-empty, schema-bearing)
        // SQLite snapshot.
        await using var sut = CreateService(maxBytes: 1);

        var result = await sut.TriggerMirrorAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.FileTooLong, result.Error!.Code);
        Assert.Contains(_bus.Published, n =>
            n.Error?.Code == ErrorCode.FileTooLong);
        // No mirror should have landed on the tail.
        Assert.Empty(TailBrainEntries());
    }

    // ── Cancellation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerMirrorAsync_PreCancelledToken_ReturnsCancelledAndCleansStaging()
    {
        _registry.Register(ProviderId, _fsProvider);
        await using var sut = CreateService();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await sut.TriggerMirrorAsync(cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);

        // No staging snapshot left behind.
        var stagingDir = Path.Combine(_skinkRoot, ".flashskink", "staging");
        if (Directory.Exists(stagingDir))
        {
            var leftover = Directory.GetFiles(stagingDir, "brain-mirror-*.db");
            Assert.Empty(leftover);
        }

        // No mirror on the tail.
        Assert.Empty(TailBrainEntries());
    }

    // ── Staging cleanup ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerMirrorAsync_AfterSuccess_NoStagingFileLeft()
    {
        _registry.Register(ProviderId, _fsProvider);
        await using var sut = CreateService();

        await sut.TriggerMirrorAsync(CancellationToken.None);

        var stagingDir = Path.Combine(_skinkRoot, ".flashskink", "staging");
        if (Directory.Exists(stagingDir))
        {
            var leftover = Directory.GetFiles(stagingDir, "brain-mirror-*.db");
            Assert.Empty(leftover);
        }
    }

    // ── Notifications: appliance-vocabulary discipline ───────────────────────────────────────

    [Fact]
    public async Task FailureNotification_UsesUserVocabulary()
    {
        // Force a failure by registering a tail whose provider fails BeginUploadAsync.
        var faulty = new FaultInjectingStorageProvider(_fsProvider);
        faulty.FailNextBeginWith(ErrorCode.ProviderUnreachable);
        _registry.Register(ProviderId, faulty);

        await using var sut = CreateService();
        await sut.TriggerMirrorAsync(CancellationToken.None);

        var notification = _bus.Published.SingleOrDefault(n =>
            n.Severity == FlashSkink.Core.Abstractions.Notifications.NotificationSeverity.Error);
        Assert.NotNull(notification);

        // No appliance vocabulary.
        foreach (var forbidden in new[] { "blob", "mirror", "WAL", "AAD", "GCM", "session", "range" })
        {
            Assert.DoesNotContain(forbidden, notification!.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, notification!.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
