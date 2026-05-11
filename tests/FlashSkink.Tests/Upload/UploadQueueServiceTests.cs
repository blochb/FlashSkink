using System.Globalization;
using System.IO.Hashing;
using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Providers;
using FlashSkink.Core.Upload;
using FlashSkink.Tests._TestSupport;
using FlashSkink.Tests.Engine;
using FlashSkink.Tests.Metadata;
using FlashSkink.Tests.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Upload;

public sealed class UploadQueueServiceTests : IAsyncLifetime, IDisposable
{
    private const string FileId = "file-001";
    private const string ProviderId = "provider-001";

    private readonly string _skinkRoot;
    private readonly string _tailRoot;
    private readonly SqliteConnection _connection;
    private readonly UploadQueueRepository _queueRepo;
    private readonly BlobRepository _blobRepo;
    private readonly FileRepository _fileRepo;
    private readonly ActivityLogRepository _activityRepo;
    private readonly InMemoryProviderRegistry _registry;
    private readonly TestNetworkAvailabilityMonitor _network;
    private readonly RecordingNotificationBus _bus;
    private readonly FakeClock _clock;
    private readonly UploadWakeupSignal _signal;
    private readonly RangeUploader _rangeUploader;
    private readonly FileSystemProvider _fsProvider;
    private readonly RecordingLogger<UploadQueueService> _serviceLogger;

    public UploadQueueServiceTests()
    {
        _skinkRoot = Path.Combine(Path.GetTempPath(), "flashskink-tests",
            "skink-" + Guid.NewGuid().ToString("N"));
        _tailRoot = Path.Combine(Path.GetTempPath(), "flashskink-tests",
            "tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_skinkRoot);
        Directory.CreateDirectory(_tailRoot);

        _connection = BrainTestHelper.CreateInMemoryConnection();
        _queueRepo = new UploadQueueRepository(_connection,
            NullLogger<UploadQueueRepository>.Instance);
        _blobRepo = new BlobRepository(_connection, NullLogger<BlobRepository>.Instance);
        var walRepo = new WalRepository(_connection, NullLogger<WalRepository>.Instance);
        _fileRepo = new FileRepository(_connection, walRepo, NullLogger<FileRepository>.Instance);
        _activityRepo = new ActivityLogRepository(_connection,
            NullLogger<ActivityLogRepository>.Instance);
        _registry = new InMemoryProviderRegistry(
            NullLogger<InMemoryProviderRegistry>.Instance);
        _network = new TestNetworkAvailabilityMonitor();
        _bus = new RecordingNotificationBus();
        _clock = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _signal = new UploadWakeupSignal();
        _rangeUploader = new RangeUploader(_queueRepo, _clock, RetryPolicy.Default,
            NullLogger<RangeUploader>.Instance);
        _fsProvider = new FileSystemProvider(ProviderId, "Test Tail", _tailRoot,
            NullLogger<FileSystemProvider>.Instance);
        _serviceLogger = new RecordingLogger<UploadQueueService>();
    }

    public async Task InitializeAsync()
    {
        await BrainTestHelper.ApplySchemaAsync(_connection);
        BrainTestHelper.InsertTestProvider(_connection, ProviderId);
        BrainTestHelper.InsertTestFile(_connection, FileId);
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

    private UploadQueueService CreateService(SqliteConnection? connection = null)
    {
        return new UploadQueueService(
            _queueRepo, _blobRepo, _fileRepo, _activityRepo,
            _registry, _network, _bus, _rangeUploader, RetryPolicy.Default,
            _clock, _signal, connection ?? _connection, _skinkRoot, _serviceLogger);
    }

    private async Task<(string BlobId, byte[] Bytes)> CreateLocalBlobAsync(int sizeBytes)
    {
        string blobId = Guid.NewGuid().ToString("N");
        var bytes = new byte[sizeBytes];
        for (int i = 0; i < sizeBytes; i++)
        {
            bytes[i] = (byte)((i * 31) % 251);
        }
        string relPath = Path.Combine("blobs", blobId[..2], blobId[2..4], blobId + ".bin");
        string absPath = Path.Combine(_skinkRoot, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
        await File.WriteAllBytesAsync(absPath, bytes);

        ulong xxhash = XxHash64.HashToUInt64(bytes);
        await _connection.ExecuteAsync(
            """
            INSERT INTO Blobs
                (BlobID, EncryptedSize, PlaintextSize, PlaintextSHA256, EncryptedXXHash,
                 BlobPath, CreatedUtc)
            VALUES
                (@BlobId, @Size, @Size, @Sha, @Xx, @Path, @Now)
            """,
            new
            {
                BlobId = blobId,
                Size = sizeBytes,
                Sha = new string('a', 64),
                Xx = xxhash.ToString("x16", CultureInfo.InvariantCulture),
                Path = relPath,
                Now = DateTime.UtcNow.ToString("O"),
            });

        await _connection.ExecuteAsync(
            "UPDATE Files SET BlobID = @BlobId WHERE FileID = @FileId",
            new { BlobId = blobId, FileId });

        return (blobId, bytes);
    }

    private async Task EnqueueAsync(string fileId = FileId, string providerId = ProviderId)
    {
        var result = await _queueRepo.EnqueueAsync(fileId, providerId, CancellationToken.None);
        Assert.True(result.Success);
    }

    private async Task<string> ReadStatusAsync(string fileId = FileId, string providerId = ProviderId)
    {
        return await _connection.QuerySingleAsync<string>(
            "SELECT Status FROM TailUploads WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId = fileId, ProviderId = providerId });
    }

    private async Task<int> ReadAttemptCountAsync()
    {
        return await _connection.QuerySingleAsync<int>(
            "SELECT AttemptCount FROM TailUploads WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId, ProviderId });
    }

    private async Task<int> ReadSessionCountAsync()
    {
        return await _connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM UploadSessions WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId, ProviderId });
    }

    /// <summary>
    /// Polls <paramref name="predicate"/> until it returns true or the wall-clock budget expires.
    /// Advances <see cref="_clock"/> by <paramref name="advanceChunk"/> whenever pending delays
    /// are observed, so worker-side <c>IClock.Delay</c> calls can release deterministically.
    /// </summary>
    private async Task PumpUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan? budget = null,
        TimeSpan? advanceChunk = null)
    {
        // Default budget is generous (30 s) because CI runners are slower than dev hardware
        // and cross-tail scenarios run multiple workers in parallel on a shared SQLite
        // connection. Locally these tests typically complete in < 500 ms; the wall-clock cap
        // is there to fail loudly on a real regression rather than to bound steady-state runtime.
        TimeSpan wallBudget = budget ?? TimeSpan.FromSeconds(30);
        TimeSpan chunk = advanceChunk ?? TimeSpan.FromHours(13);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.Elapsed < wallBudget)
        {
            if (await predicate())
            {
                return;
            }

            if (_clock.PendingDelayCount > 0)
            {
                _clock.Advance(chunk);
            }
            _signal.Pulse();
            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Predicate did not become true within {wallBudget}.");
    }

    // ── Construction & lifecycle ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_FirstCall_ReturnsOk()
    {
        await using var sut = CreateService();
        var result = sut.Start(CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Start_SecondCall_ReturnsOkWithoutSideEffect()
    {
        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        var second = sut.Start(CancellationToken.None);
        Assert.True(second.Success);
    }

    [Fact]
    public async Task Start_AfterDispose_ReturnsFail()
    {
        var sut = CreateService();
        await sut.DisposeAsync();

        var result = sut.Start(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeAlreadyOpen, result.Error!.Code);
    }

    [Fact]
    public async Task DisposeAsync_BeforeStart_ReturnsCleanly()
    {
        var sut = CreateService();
        await sut.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_AfterStart_StopsOrchestratorWithinBudget()
    {
        var sut = CreateService();
        sut.Start(CancellationToken.None);
        await Task.Delay(50);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.DisposeAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var sut = CreateService();
        sut.Start(CancellationToken.None);
        await sut.DisposeAsync();
        await sut.DisposeAsync();
    }

    // ── Happy path ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_PicksUpPendingRow_UploadsAndMarksUploaded()
    {
        await CreateLocalBlobAsync(1024 * 1024);
        _registry.Register(ProviderId, _fsProvider);
        await EnqueueAsync();

        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        _signal.Pulse();

        await PumpUntilAsync(async () => await ReadStatusAsync() == "UPLOADED");

        Assert.Equal("UPLOADED", await ReadStatusAsync());
        Assert.Equal(0, await ReadSessionCountAsync());

        var remoteId = await _connection.QuerySingleAsync<string>(
            "SELECT RemoteId FROM TailUploads WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId, ProviderId });
        Assert.NotNull(remoteId);

        var activityCount = await _connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM ActivityLog WHERE Category = 'UPLOADED'");
        Assert.Equal(1, activityCount);
    }

    [Fact]
    public async Task Worker_MultiRangeBlob_UploadsCompleteFile()
    {
        var (_, bytes) = await CreateLocalBlobAsync(8 * 1024 * 1024 + 1024);
        _registry.Register(ProviderId, _fsProvider);
        await EnqueueAsync();

        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        _signal.Pulse();

        await PumpUntilAsync(async () => await ReadStatusAsync() == "UPLOADED");

        var remoteId = await _connection.QuerySingleAsync<string>(
            "SELECT RemoteId FROM TailUploads WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId, ProviderId });
        var remoteBytes = await File.ReadAllBytesAsync(Path.Combine(_tailRoot, remoteId));
        Assert.Equal(bytes, remoteBytes);
    }

    // ── Per-range retry inside a cycle ───────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_TransientFailureOnce_RetriesAndCompletes()
    {
        await CreateLocalBlobAsync(1024 * 1024);
        var faulty = new FaultInjectingStorageProvider(_fsProvider);
        faulty.FailNextRange();
        _registry.Register(ProviderId, faulty);
        await EnqueueAsync();

        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        _signal.Pulse();

        await PumpUntilAsync(async () => await ReadStatusAsync() == "UPLOADED");

        Assert.Equal("UPLOADED", await ReadStatusAsync());
        Assert.Equal(1, await ReadAttemptCountAsync());
    }

    // ── Cycle escalation ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_FiveCyclesAllFail_PromotesToFailedAndNotifies()
    {
        await CreateLocalBlobAsync(1024 * 1024);
        var faulty = new FaultInjectingStorageProvider(_fsProvider);
        // Schedule enough failures to exhaust 5 cycles × 4 in-range attempts = 20 range failures.
        for (int i = 0; i < 50; i++)
        {
            faulty.FailNextRangeWith(ErrorCode.ProviderUnreachable);
        }
        _registry.Register(ProviderId, faulty);
        await EnqueueAsync();

        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        _signal.Pulse();

        await PumpUntilAsync(
            async () => await ReadStatusAsync() == "FAILED" && _bus.Published.Count > 0,
            budget: TimeSpan.FromSeconds(20));

        Assert.Equal("FAILED", await ReadStatusAsync());

        var lastError = await _connection.QuerySingleAsync<string?>(
            "SELECT LastError FROM TailUploads WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId, ProviderId });
        Assert.NotNull(lastError);
        Assert.Contains("ProviderUnreachable", lastError);

        Assert.Equal(0, await ReadSessionCountAsync());

        var notification = Assert.Single(_bus.Published);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.Equal("Could not back up file", notification.Title);
        Assert.Contains("Test Tail", notification.Message);

        var activityCount = await _connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM ActivityLog WHERE Category = 'UPLOAD_FAILED'");
        Assert.True(activityCount >= 1);
    }

    [Fact]
    public async Task Worker_PermanentFailure_MarksFailedImmediately()
    {
        await CreateLocalBlobAsync(1024 * 1024);
        var faulty = new FaultInjectingStorageProvider(_fsProvider);
        faulty.FailNextRangeWith(ErrorCode.ProviderAuthFailed);
        _registry.Register(ProviderId, faulty);
        await EnqueueAsync();

        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        _signal.Pulse();

        await PumpUntilAsync(async () => await ReadStatusAsync() == "FAILED");

        Assert.Equal("FAILED", await ReadStatusAsync());
        // AttemptCount is bumped to the §21.1 cycle cap (5) by MarkTerminallyFailedAsync so the
        // DequeueNextBatchAsync filter excludes this row from any further cycle attempts.
        Assert.Equal(5, await ReadAttemptCountAsync());
        Assert.Equal(0, await ReadSessionCountAsync());

        var notification = Assert.Single(_bus.Published);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.Equal(ErrorCode.ProviderAuthFailed, notification.Error!.Code);
    }

    // ── Network availability gating ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NetworkOffline_NewRow_DoesNotUpload()
    {
        await CreateLocalBlobAsync(1024 * 1024);
        _registry.Register(ProviderId, _fsProvider);
        await EnqueueAsync();

        _network.SetAvailable(false);

        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        _signal.Pulse();

        // Pump for a bit; status should remain PENDING.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (_clock.PendingDelayCount > 0)
            {
                _clock.Advance(TimeSpan.FromHours(1));
            }
            _signal.Pulse();
            await Task.Delay(20);
        }

        Assert.Equal("PENDING", await ReadStatusAsync());
    }

    [Fact]
    public async Task NetworkRestored_UploadProceeds()
    {
        await CreateLocalBlobAsync(1024 * 1024);
        _registry.Register(ProviderId, _fsProvider);
        await EnqueueAsync();

        _network.SetAvailable(false);

        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        _signal.Pulse();
        await Task.Delay(100);
        Assert.Equal("PENDING", await ReadStatusAsync());

        _network.SetAvailable(true);

        await PumpUntilAsync(async () => await ReadStatusAsync() == "UPLOADED");
    }

    // ── Per-tail isolation ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CrossTail_FaultyProviderDoesNotBlockHealthyProvider()
    {
        // Set up a second provider B with its own tail root and TailUploads row.
        const string ProviderIdB = "provider-002";
        string tailRootB = Path.Combine(Path.GetTempPath(), "flashskink-tests",
            "tailB-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tailRootB);
        try
        {
            BrainTestHelper.InsertTestProvider(_connection, ProviderIdB);

            await CreateLocalBlobAsync(1024 * 1024);

            // A is a fault-injecting provider that fails permanently.
            var faultyA = new FaultInjectingStorageProvider(_fsProvider);
            faultyA.FailNextRangeWith(ErrorCode.ProviderAuthFailed);
            _registry.Register(ProviderId, faultyA);

            // B is a clean FileSystemProvider on its own root.
            var providerB = new FileSystemProvider(ProviderIdB, "Test Tail B", tailRootB,
                NullLogger<FileSystemProvider>.Instance);
            _registry.Register(ProviderIdB, providerB);

            await EnqueueAsync(FileId, ProviderId);
            await EnqueueAsync(FileId, ProviderIdB);

            await using var sut = CreateService();
            sut.Start(CancellationToken.None);
            _signal.Pulse();

            await PumpUntilAsync(async () =>
                await ReadStatusAsync(FileId, ProviderId) == "FAILED" &&
                await ReadStatusAsync(FileId, ProviderIdB) == "UPLOADED");

            Assert.Equal("FAILED", await ReadStatusAsync(FileId, ProviderId));
            Assert.Equal("UPLOADED", await ReadStatusAsync(FileId, ProviderIdB));
        }
        finally
        {
            if (Directory.Exists(tailRootB))
            {
                try { Directory.Delete(tailRootB, recursive: true); } catch { }
            }
        }
    }

    // ── Resume ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_RestartWithExistingSession_ResumesFromBytesUploaded()
    {
        // Write a 12 MiB blob in 3 ranges. Pre-stage 8 MiB into the tail's .partial so that
        // the resume path picks it up via GetUploadedBytesAsync, and pre-insert a matching
        // UploadSessions row.
        var (blobId, bytes) = await CreateLocalBlobAsync(12 * 1024 * 1024);
        _registry.Register(ProviderId, _fsProvider);
        await EnqueueAsync();

        // Pre-stage: open a session via the provider to get a SessionUri, write 8 MiB partial.
        var begin = await _fsProvider.BeginUploadAsync(blobId + ".bin", 12 * 1024 * 1024,
            CancellationToken.None);
        Assert.True(begin.Success);
        var session = begin.Value!;

        // Persist the session row at BytesUploaded = 8 MiB.
        var expires = session.ExpiresAt == DateTimeOffset.MaxValue
            ? DateTime.MaxValue
            : session.ExpiresAt.UtcDateTime;
        await _queueRepo.GetOrCreateSessionAsync(FileId, ProviderId,
            session.SessionUri, expires, 12 * 1024 * 1024, CancellationToken.None);

        // Pre-write first 8 MiB into the partial (two ranges).
        var firstRange = bytes.AsMemory(0, 4 * 1024 * 1024);
        var secondRange = bytes.AsMemory(4 * 1024 * 1024, 4 * 1024 * 1024);
        await _fsProvider.UploadRangeAsync(session, 0, firstRange, CancellationToken.None);
        await _fsProvider.UploadRangeAsync(session, 4 * 1024 * 1024, secondRange,
            CancellationToken.None);
        await _queueRepo.UpdateSessionProgressAsync(FileId, ProviderId, 8 * 1024 * 1024,
            CancellationToken.None);

        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        _signal.Pulse();

        await PumpUntilAsync(async () => await ReadStatusAsync() == "UPLOADED");

        // Verify final tail content matches source bytes.
        var remoteId = await _connection.QuerySingleAsync<string>(
            "SELECT RemoteId FROM TailUploads WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId, ProviderId });
        var remoteBytes = await File.ReadAllBytesAsync(Path.Combine(_tailRoot, remoteId));
        Assert.Equal(bytes, remoteBytes);
    }

    // ── Crash-consistency invariants ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Invariant_AfterCompletedUpload_UploadSessionsIsEmpty()
    {
        await CreateLocalBlobAsync(1024 * 1024);
        _registry.Register(ProviderId, _fsProvider);
        await EnqueueAsync();

        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        _signal.Pulse();

        await PumpUntilAsync(async () => await ReadStatusAsync() == "UPLOADED");

        Assert.Equal(0, await ReadSessionCountAsync());
    }

    [Fact]
    public async Task Invariant_AfterPermanentFailure_UploadSessionsIsEmpty()
    {
        await CreateLocalBlobAsync(1024 * 1024);
        var faulty = new FaultInjectingStorageProvider(_fsProvider);
        faulty.FailNextRangeWith(ErrorCode.ProviderAuthFailed);
        _registry.Register(ProviderId, faulty);
        await EnqueueAsync();

        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        _signal.Pulse();

        await PumpUntilAsync(async () => await ReadStatusAsync() == "FAILED");

        Assert.Equal(0, await ReadSessionCountAsync());
    }

    // ── Notification vocabulary discipline (Principle 25) ────────────────────────────────────

    [Fact]
    public async Task PublishedNotifications_ContainNoApplianceVocabulary()
    {
        await CreateLocalBlobAsync(1024 * 1024);
        var faulty = new FaultInjectingStorageProvider(_fsProvider);
        faulty.FailNextRangeWith(ErrorCode.ProviderAuthFailed);
        _registry.Register(ProviderId, faulty);
        await EnqueueAsync();

        await using var sut = CreateService();
        sut.Start(CancellationToken.None);
        _signal.Pulse();

        await PumpUntilAsync(async () => _bus.Published.Count > 0);

        string[] forbidden = ["WAL", "OAuth", "DEK", "KEK", "AAD"];
        foreach (var n in _bus.Published)
        {
            foreach (var word in forbidden)
            {
                Assert.DoesNotContain(word, n.Title, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, n.Message, StringComparison.OrdinalIgnoreCase);
            }
            // The standalone words "tail"/"blob"/"session"/"range" must not appear as whole
            // words in user-facing strings. The provider's DisplayName ("Test Tail") embeds
            // "Tail" — that is a configured display string, not appliance vocabulary, and
            // tests on it would create false positives. Verify the *Title* (which is fully
            // owned by the service) is free of every appliance term.
            string[] strictForbidden = ["tail", "blob", "session", "range"];
            foreach (var word in strictForbidden)
            {
                Assert.DoesNotContain(word, n.Title, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ── Worker lifecycle on registry mutation ────────────────────────────────────────────────

    [Fact]
    public async Task Orchestrator_ProviderRegisteredAfterStart_WorkerSpawns()
    {
        await CreateLocalBlobAsync(1024 * 1024);
        await EnqueueAsync();

        await using var sut = CreateService();
        sut.Start(CancellationToken.None);

        // No provider yet — no worker, no upload.
        await Task.Delay(100);
        Assert.Equal("PENDING", await ReadStatusAsync());

        _registry.Register(ProviderId, _fsProvider);
        _signal.Pulse();

        await PumpUntilAsync(async () => await ReadStatusAsync() == "UPLOADED");
        Assert.True(_serviceLogger.HasEntry(LogLevel.Information, "Worker started"));
    }
}
