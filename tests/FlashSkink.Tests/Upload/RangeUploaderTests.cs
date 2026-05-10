using System.Globalization;
using System.IO.Hashing;
using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Providers;
using FlashSkink.Core.Upload;
using FlashSkink.Tests._TestSupport;
using FlashSkink.Tests.Metadata;
using FlashSkink.Tests.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Upload;

public sealed class RangeUploaderTests : IAsyncLifetime, IDisposable
{
    private const string FileId = "file-001";
    private const string ProviderId = "provider-001";
    private const int RangeSize = UploadConstants.RangeSize;

    private readonly string _skinkRoot;
    private readonly string _tailRoot;
    private readonly SqliteConnection _connection;
    private readonly UploadQueueRepository _queueRepo;
    private readonly FakeClock _clock;
    private readonly FileSystemProvider _fsProvider;
    private readonly RangeUploader _sut;

    public RangeUploaderTests()
    {
        _skinkRoot = Path.Combine(Path.GetTempPath(), "flashskink-tests", "skink-" + Guid.NewGuid().ToString("N"));
        _tailRoot = Path.Combine(Path.GetTempPath(), "flashskink-tests", "tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_skinkRoot);
        Directory.CreateDirectory(_tailRoot);

        _connection = BrainTestHelper.CreateInMemoryConnection();
        _queueRepo = new UploadQueueRepository(_connection, NullLogger<UploadQueueRepository>.Instance);
        _clock = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _fsProvider = new FileSystemProvider(
            ProviderId, "Test Tail", _tailRoot, NullLogger<FileSystemProvider>.Instance);
        _sut = new RangeUploader(_queueRepo, _clock, RetryPolicy.Default, NullLogger<RangeUploader>.Instance);
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
            Directory.Delete(_skinkRoot, recursive: true);
        }
        if (Directory.Exists(_tailRoot))
        {
            Directory.Delete(_tailRoot, recursive: true);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static byte[] MakeBytes(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) % 251);
        }
        return bytes;
    }

    private (string Path, BlobRecord Record, byte[] Bytes) CreateLocalBlob(int sizeBytes)
    {
        string blobId = Guid.NewGuid().ToString("N");
        byte[] bytes = MakeBytes(sizeBytes);
        string dir = Path.Combine(_skinkRoot, "blobs", blobId[..2], blobId[2..4]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, blobId + ".bin");
        File.WriteAllBytes(path, bytes);

        ulong xxhash = XxHash64.HashToUInt64(bytes);
        var record = new BlobRecord
        {
            BlobId = blobId,
            EncryptedSize = sizeBytes,
            PlaintextSize = sizeBytes,
            PlaintextSha256 = new string('a', 64),
            EncryptedXxHash = xxhash.ToString("x16", CultureInfo.InvariantCulture),
            BlobPath = Path.Combine("blobs", blobId[..2], blobId[2..4], blobId + ".bin"),
            CreatedUtc = DateTime.UtcNow,
        };

        return (path, record, bytes);
    }

    private async Task<UploadSessionRow?> ReadSessionRowAsync()
    {
        var row = await _connection.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT FileID, ProviderID, SessionUri, SessionExpiresUtc,
                   BytesUploaded, TotalBytes, LastActivityUtc
            FROM UploadSessions
            WHERE FileID = @FileId AND ProviderID = @ProviderId
            """,
            new { FileId, ProviderId });
        if (row is null)
        {
            return null;
        }

        return new UploadSessionRow(
            FileId: (string)row.FileID,
            ProviderId: (string)row.ProviderID,
            SessionUri: (string)row.SessionUri,
            SessionExpiresUtc: DateTime.Parse((string)row.SessionExpiresUtc, null, DateTimeStyles.RoundtripKind),
            BytesUploaded: (long)row.BytesUploaded,
            TotalBytes: (long)row.TotalBytes,
            LastActivityUtc: DateTime.Parse((string)row.LastActivityUtc, null, DateTimeStyles.RoundtripKind));
    }

    private static async Task SpinUntilAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Predicate did not become true within timeout.");
            }
            await Task.Delay(5);
        }
    }

    private byte[] ReadFinalisedBytes(string remoteId)
    {
        string fullPath = Path.Combine(_tailRoot, remoteId.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllBytes(fullPath);
    }

    // ── Happy path ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_FreshSessionSmallBlob_FinalisesAndReturnsCompleted()
    {
        var (path, record, bytes) = CreateLocalBlob(2048);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, _fsProvider, record, path,
            existingSession: null, ct: CancellationToken.None);

        Assert.True(result.Success);
        var outcome = result.Value!;
        Assert.Equal(UploadOutcomeStatus.Completed, outcome.Status);
        Assert.NotNull(outcome.RemoteId);
        Assert.Equal(2048, outcome.BytesUploaded);

        Assert.Equal(bytes, ReadFinalisedBytes(outcome.RemoteId!));

        // Session row remains; caller is responsible for deleting it on commit.
        var session = await ReadSessionRowAsync();
        Assert.NotNull(session);
        Assert.Equal(2048, session!.BytesUploaded);
    }

    [Fact]
    public async Task UploadAsync_FreshSessionMultiRangeBlob_AllBytesUploaded()
    {
        // 2.5 ranges of 4 MiB → 10 MiB.
        int size = (int)(RangeSize * 2.5);
        var (path, record, bytes) = CreateLocalBlob(size);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, _fsProvider, record, path, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.Completed, result.Value!.Status);
        Assert.Equal(size, result.Value.BytesUploaded);
        Assert.Equal(bytes, ReadFinalisedBytes(result.Value.RemoteId!));
    }

    [Fact]
    public async Task UploadAsync_ExactRangeBoundary_TwoFullRanges()
    {
        int size = RangeSize * 2;
        var (path, record, bytes) = CreateLocalBlob(size);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, _fsProvider, record, path, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.Completed, result.Value!.Status);
        Assert.Equal(bytes, ReadFinalisedBytes(result.Value.RemoteId!));
    }

    // ── Resume / reconcile ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_ResumeFromExistingSession_ContinuesFromBytesUploaded()
    {
        // First call: open session, upload one range, then simulate a crash before completion
        // by aborting via cancellation? Simpler: do a full upload to a fresh provider state, then
        // construct an UploadSessionRow that points to a partial state and resume from it.
        // For a cleaner test: do a partial upload using the provider directly, persist a row,
        // then call UploadAsync with that row.
        int size = RangeSize * 3;
        var (path, record, bytes) = CreateLocalBlob(size);

        // Drive the provider directly to upload the first range.
        string remoteName = record.BlobId + ".bin";
        var begin = await _fsProvider.BeginUploadAsync(remoteName, size, CancellationToken.None);
        Assert.True(begin.Success);
        var session = begin.Value!;
        await _fsProvider.UploadRangeAsync(session, 0, bytes.AsMemory(0, RangeSize), CancellationToken.None);

        // Persist a session row reflecting that one range has been uploaded.
        var persistResult = await _queueRepo.GetOrCreateSessionAsync(
            FileId, ProviderId, session.SessionUri, DateTime.MaxValue, size, CancellationToken.None);
        Assert.True(persistResult.Success);
        await _queueRepo.UpdateSessionProgressAsync(FileId, ProviderId, RangeSize, CancellationToken.None);

        var existingRow = (await ReadSessionRowAsync())!;

        // Now resume.
        var result = await _sut.UploadAsync(
            FileId, ProviderId, _fsProvider, record, path, existingRow, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.Completed, result.Value!.Status);
        Assert.Equal(bytes, ReadFinalisedBytes(result.Value.RemoteId!));
    }

    [Fact]
    public async Task UploadAsync_ResumeProviderReportsLessThanRow_ReconcilesDownAndContinues()
    {
        int size = RangeSize * 2;
        var (path, record, bytes) = CreateLocalBlob(size);

        // Drive provider to upload exactly one range (4 MiB).
        string remoteName = record.BlobId + ".bin";
        var begin = await _fsProvider.BeginUploadAsync(remoteName, size, CancellationToken.None);
        var session = begin.Value!;
        await _fsProvider.UploadRangeAsync(session, 0, bytes.AsMemory(0, RangeSize), CancellationToken.None);

        // Persist a row LYING about progress: claim 8 MiB uploaded, but provider has only 4 MiB.
        await _queueRepo.GetOrCreateSessionAsync(
            FileId, ProviderId, session.SessionUri, DateTime.MaxValue, size, CancellationToken.None);
        await _queueRepo.UpdateSessionProgressAsync(FileId, ProviderId, size, CancellationToken.None);

        var existingRow = (await ReadSessionRowAsync())!;
        Assert.Equal(size, existingRow.BytesUploaded);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, _fsProvider, record, path, existingRow, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.Completed, result.Value!.Status);

        // Final blob equals source (the second range was actually uploaded).
        Assert.Equal(bytes, ReadFinalisedBytes(result.Value.RemoteId!));
    }

    [Fact]
    public async Task UploadAsync_ResumeProviderAlreadyComplete_FinalisesImmediately()
    {
        int size = RangeSize;
        var (path, record, bytes) = CreateLocalBlob(size);

        // Drive provider to upload all bytes (without finalising).
        string remoteName = record.BlobId + ".bin";
        var begin = await _fsProvider.BeginUploadAsync(remoteName, size, CancellationToken.None);
        var session = begin.Value!;
        await _fsProvider.UploadRangeAsync(session, 0, bytes, CancellationToken.None);

        await _queueRepo.GetOrCreateSessionAsync(
            FileId, ProviderId, session.SessionUri, DateTime.MaxValue, size, CancellationToken.None);
        await _queueRepo.UpdateSessionProgressAsync(FileId, ProviderId, size, CancellationToken.None);

        var existingRow = (await ReadSessionRowAsync())!;

        var result = await _sut.UploadAsync(
            FileId, ProviderId, _fsProvider, record, path, existingRow, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.Completed, result.Value!.Status);
        Assert.Equal(bytes, ReadFinalisedBytes(result.Value.RemoteId!));
    }

    [Fact]
    public async Task UploadAsync_ResumeExpiredSession_AbortsAndOpensFresh()
    {
        int size = RangeSize;
        var (path, record, bytes) = CreateLocalBlob(size);

        // Construct a session row that "exists" with an expiry in the past.
        DateTime expired = _clock.UtcNow - TimeSpan.FromHours(1);
        await _queueRepo.GetOrCreateSessionAsync(
            FileId, ProviderId, "stale-session-uri", expired, size, CancellationToken.None);
        await _queueRepo.UpdateSessionProgressAsync(FileId, ProviderId, 1024, CancellationToken.None);

        var existingRow = (await ReadSessionRowAsync())!;
        Assert.True(existingRow.SessionExpiresUtc <= _clock.UtcNow);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, _fsProvider, record, path, existingRow, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.Completed, result.Value!.Status);
        Assert.Equal(bytes, ReadFinalisedBytes(result.Value.RemoteId!));

        // Session row was rewritten with a fresh URI (different from the stale one).
        var freshRow = await ReadSessionRowAsync();
        Assert.NotNull(freshRow);
        Assert.NotEqual("stale-session-uri", freshRow!.SessionUri);
    }

    // ── Session expiry mid-upload ────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_SessionExpiredOnRange3_RestartsFromByteZero()
    {
        int size = RangeSize * 3;
        var (path, record, bytes) = CreateLocalBlob(size);

        var fault = new FaultInjectingStorageProvider(_fsProvider);
        fault.ForceSessionExpiryAfter(2);  // ranges 1, 2 succeed; range 3 returns expired

        var result = await _sut.UploadAsync(
            FileId, ProviderId, fault, record, path, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.Completed, result.Value!.Status);
        Assert.Equal(size, result.Value.BytesUploaded);
        Assert.Equal(bytes, ReadFinalisedBytes(result.Value.RemoteId!));
    }

    // ── Per-range retry (§21.1 in-range ladder) ─────────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_TransientFailureOnce_RetriesAfter1sAndSucceeds()
    {
        int size = 1024;
        var (path, record, bytes) = CreateLocalBlob(size);

        var fault = new FaultInjectingStorageProvider(_fsProvider);
        fault.FailNextRangeWith(ErrorCode.UploadFailed);

        var uploadTask = Task.Run(() => _sut.UploadAsync(
            FileId, ProviderId, fault, record, path, null, CancellationToken.None));

        // Wait for the retry to register a delay.
        await SpinUntilAsync(() => _clock.PendingDelayCount == 1);

        _clock.Advance(TimeSpan.FromSeconds(1));

        var result = await uploadTask;

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.Completed, result.Value!.Status);
        Assert.Equal(bytes, ReadFinalisedBytes(result.Value.RemoteId!));
    }

    [Fact]
    public async Task UploadAsync_TransientFailureTwice_RetriesAfter1sAnd4s()
    {
        int size = 1024;
        var (path, record, bytes) = CreateLocalBlob(size);

        var fault = new FaultInjectingStorageProvider(_fsProvider);
        fault.FailNextRangeWith(ErrorCode.ProviderRateLimited);
        fault.FailNextRangeWith(ErrorCode.ProviderRateLimited);

        var uploadTask = Task.Run(() => _sut.UploadAsync(
            FileId, ProviderId, fault, record, path, null, CancellationToken.None));

        // First retry — 1s.
        await SpinUntilAsync(() => _clock.PendingDelayCount == 1);
        _clock.Advance(TimeSpan.FromSeconds(1));

        // Second retry — 4s.
        await SpinUntilAsync(() => _clock.PendingDelayCount == 1);
        _clock.Advance(TimeSpan.FromSeconds(4));

        var result = await uploadTask;

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.Completed, result.Value!.Status);
        Assert.Equal(bytes, ReadFinalisedBytes(result.Value.RemoteId!));
    }

    [Fact]
    public async Task UploadAsync_TransientFailureFourTimes_EscalatesAsRetryable()
    {
        int size = 1024;
        var (path, record, _) = CreateLocalBlob(size);

        var fault = new FaultInjectingStorageProvider(_fsProvider);
        fault.FailNextRangeWith(ErrorCode.UploadFailed);
        fault.FailNextRangeWith(ErrorCode.UploadFailed);
        fault.FailNextRangeWith(ErrorCode.UploadFailed);
        fault.FailNextRangeWith(ErrorCode.UploadFailed);

        var uploadTask = Task.Run(() => _sut.UploadAsync(
            FileId, ProviderId, fault, record, path, null, CancellationToken.None));

        // Drain the 1/4/16 second waits.
        foreach (var delay in new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(16) })
        {
            await SpinUntilAsync(() => _clock.PendingDelayCount == 1);
            _clock.Advance(delay);
        }

        var result = await uploadTask;

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.RetryableFailure, result.Value!.Status);
        Assert.Equal(ErrorCode.UploadFailed, result.Value.FailureCode);
    }

    // ── Permanent failure codes ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ErrorCode.ProviderAuthFailed)]
    [InlineData(ErrorCode.ProviderQuotaExceeded)]
    [InlineData(ErrorCode.TokenRevoked)]
    public async Task UploadAsync_PermanentFailureCode_ReturnsPermanentImmediately(ErrorCode code)
    {
        int size = 1024;
        var (path, record, _) = CreateLocalBlob(size);

        var fault = new FaultInjectingStorageProvider(_fsProvider);
        fault.FailNextRangeWith(code);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, fault, record, path, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.PermanentFailure, result.Value!.Status);
        Assert.Equal(code, result.Value.FailureCode);

        // Pending delay count must be zero — no retries happened.
        Assert.Equal(0, _clock.PendingDelayCount);
    }

    // ── Verification (ISupportsRemoteHashCheck) ──────────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_HashMismatch_ReturnsPermanentChecksumMismatch()
    {
        int size = 1024;
        var (path, record, _) = CreateLocalBlob(size);

        // Mutate the recorded hash so the remote (correct) hash will not match.
        var lyingRecord = record with { EncryptedXxHash = "deadbeefdeadbeef" };

        var result = await _sut.UploadAsync(
            FileId, ProviderId, _fsProvider, lyingRecord, path, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.PermanentFailure, result.Value!.Status);
        Assert.Equal(ErrorCode.ChecksumMismatch, result.Value.FailureCode);
    }

    [Fact]
    public async Task UploadAsync_HashCheckIoFails_ReturnsRetryable()
    {
        int size = 1024;
        var (path, record, _) = CreateLocalBlob(size);

        var fault = new FaultInjectingStorageProvider(_fsProvider);
        fault.FailNextHashCheckWith(ErrorCode.ProviderUnreachable);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, fault, record, path, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.RetryableFailure, result.Value!.Status);
        Assert.Equal(ErrorCode.ProviderUnreachable, result.Value.FailureCode);
    }

    [Fact]
    public async Task UploadAsync_ProviderWithoutHashCheckCapability_SkipsVerificationAndCompletes()
    {
        int size = 1024;
        var (path, record, bytes) = CreateLocalBlob(size);

        var stub = new NoHashCheckProviderStub(_fsProvider);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, stub, record, path, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.Completed, result.Value!.Status);
        Assert.Equal(bytes, ReadFinalisedBytes(result.Value.RemoteId!));
    }

    // ── Finalise failure paths ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_FinaliseFailsWithRetryableCode_ReturnsRetryable()
    {
        int size = 1024;
        var (path, record, _) = CreateLocalBlob(size);

        var fault = new FaultInjectingStorageProvider(_fsProvider);
        fault.FailNextFinaliseWith(ErrorCode.ProviderUnreachable);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, fault, record, path, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.RetryableFailure, result.Value!.Status);
        Assert.Equal(ErrorCode.ProviderUnreachable, result.Value.FailureCode);
    }

    [Fact]
    public async Task UploadAsync_FinaliseFailsWithPermanentCode_ReturnsPermanent()
    {
        int size = 1024;
        var (path, record, _) = CreateLocalBlob(size);

        var fault = new FaultInjectingStorageProvider(_fsProvider);
        fault.FailNextFinaliseWith(ErrorCode.ProviderQuotaExceeded);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, fault, record, path, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.PermanentFailure, result.Value!.Status);
        Assert.Equal(ErrorCode.ProviderQuotaExceeded, result.Value.FailureCode);
    }

    // ── BeginUploadAsync failure ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_BeginUploadFails_ReturnsResultFailWithProviderError()
    {
        int size = 1024;
        var (path, record, _) = CreateLocalBlob(size);

        var fault = new FaultInjectingStorageProvider(_fsProvider);
        fault.FailNextBeginWith(ErrorCode.ProviderUnreachable);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, fault, record, path, null, CancellationToken.None);

        // A BeginUploadAsync failure propagates as Result.Fail — the caller (UploadQueueService,
        // §3.4) treats this as a transient failure of the call itself; the next cycle re-enters
        // UploadAsync with no existing session and tries Begin again.
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderUnreachable, result.Error!.Code);

        // No session row was persisted because Begin failed before GetOrCreateSessionAsync.
        Assert.Null(await ReadSessionRowAsync());
    }

    // ── Local blob preconditions ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_LocalBlobMissing_ReturnsPermanentBlobCorrupt()
    {
        int size = 1024;
        var (path, record, _) = CreateLocalBlob(size);
        File.Delete(path);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, _fsProvider, record, path, null, CancellationToken.None);

        // A missing local blob is permanent — retrying every cycle would be futile.
        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.PermanentFailure, result.Value!.Status);
        Assert.Equal(ErrorCode.BlobCorrupt, result.Value.FailureCode);
    }

    [Fact]
    public async Task UploadAsync_LocalBlobDirectoryMissing_ReturnsPermanentBlobCorrupt()
    {
        int size = 1024;
        var (path, record, _) = CreateLocalBlob(size);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);

        var result = await _sut.UploadAsync(
            FileId, ProviderId, _fsProvider, record, path, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.PermanentFailure, result.Value!.Status);
        Assert.Equal(ErrorCode.BlobCorrupt, result.Value.FailureCode);
    }

    [Fact]
    public async Task UploadAsync_LocalBlobShorterThanEncryptedSize_ReturnsPermanentBlobCorrupt()
    {
        int actualSize = 1024;
        var (path, record, _) = CreateLocalBlob(actualSize);
        var lyingRecord = record with { EncryptedSize = actualSize * 2 };

        var result = await _sut.UploadAsync(
            FileId, ProviderId, _fsProvider, lyingRecord, path, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.PermanentFailure, result.Value!.Status);
        Assert.Equal(ErrorCode.BlobCorrupt, result.Value.FailureCode);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_CancelledBeforeFirstRange_ReturnsResultFailCancelled()
    {
        int size = 1024;
        var (path, record, _) = CreateLocalBlob(size);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _sut.UploadAsync(
            FileId, ProviderId, _fsProvider, record, path, null, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task UploadAsync_CancelledMidUpload_PreservesSessionRow()
    {
        int size = RangeSize * 3;
        var (path, record, _) = CreateLocalBlob(size);

        // Latency on each range gives the cancellation token room to fire mid-flight.
        // FaultInjectingStorageProvider does not add latency to BeginUploadAsync, so the
        // session row is written before the first range starts; cancellation fires inside
        // the latency-injected range delay.
        var fault = new FaultInjectingStorageProvider(_fsProvider);
        fault.SetRangeLatency(TimeSpan.FromMilliseconds(500));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await _sut.UploadAsync(
            FileId, ProviderId, fault, record, path, null, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);

        // Session row preserved — caller / next session can resume from it.
        Assert.NotNull(await ReadSessionRowAsync());
    }

    // ── Session row crash-consistency ────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_RangeBudgetExhausted_SessionBytesUploadedReflectsConfirmedOnly()
    {
        int size = RangeSize * 2;
        var (path, record, _) = CreateLocalBlob(size);

        // Four consecutive failures on range 1 exhaust the 1/4/16s in-range ladder and escalate.
        // The fault decorator returns the failure WITHOUT calling the inner provider, so no bytes
        // ever land on the tail. The session row's BytesUploaded must remain at 0.
        var fault = new FaultInjectingStorageProvider(_fsProvider);
        fault.FailNextRangeWith(ErrorCode.UploadFailed);
        fault.FailNextRangeWith(ErrorCode.UploadFailed);
        fault.FailNextRangeWith(ErrorCode.UploadFailed);
        fault.FailNextRangeWith(ErrorCode.UploadFailed);

        var uploadTask = Task.Run(() => _sut.UploadAsync(
            FileId, ProviderId, fault, record, path, null, CancellationToken.None));

        foreach (var delay in new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(16) })
        {
            await SpinUntilAsync(() => _clock.PendingDelayCount == 1);
            _clock.Advance(delay);
        }

        var result = await uploadTask;

        Assert.True(result.Success);
        Assert.Equal(UploadOutcomeStatus.RetryableFailure, result.Value!.Status);

        // Session row exists at offset 0 — no provider-confirmed bytes.
        var row = await ReadSessionRowAsync();
        Assert.NotNull(row);
        Assert.Equal(0, row!.BytesUploaded);
    }

    // ── Test stub: provider without ISupportsRemoteHashCheck ─────────────────────────────────

    private sealed class NoHashCheckProviderStub : IStorageProvider
    {
        private readonly IStorageProvider _inner;

        public NoHashCheckProviderStub(IStorageProvider inner) => _inner = inner;

        public string ProviderID => _inner.ProviderID;
        public string ProviderType => _inner.ProviderType;
        public string DisplayName => _inner.DisplayName;

        public Task<Result<UploadSession>> BeginUploadAsync(string remoteName, long totalBytes, CancellationToken ct) =>
            _inner.BeginUploadAsync(remoteName, totalBytes, ct);

        public Task<Result<long>> GetUploadedBytesAsync(UploadSession session, CancellationToken ct) =>
            _inner.GetUploadedBytesAsync(session, ct);

        public Task<Result> UploadRangeAsync(UploadSession session, long offset, ReadOnlyMemory<byte> data, CancellationToken ct) =>
            _inner.UploadRangeAsync(session, offset, data, ct);

        public Task<Result<string>> FinaliseUploadAsync(UploadSession session, CancellationToken ct) =>
            _inner.FinaliseUploadAsync(session, ct);

        public Task<Result> AbortUploadAsync(UploadSession session, CancellationToken ct) =>
            _inner.AbortUploadAsync(session, ct);

        public Task<Result<Stream>> DownloadAsync(string remoteId, CancellationToken ct) =>
            _inner.DownloadAsync(remoteId, ct);

        public Task<Result> DeleteAsync(string remoteId, CancellationToken ct) =>
            _inner.DeleteAsync(remoteId, ct);

        public Task<Result<bool>> ExistsAsync(string remoteId, CancellationToken ct) =>
            _inner.ExistsAsync(remoteId, ct);

        public Task<Result<IReadOnlyList<string>>> ListAsync(string prefix, CancellationToken ct) =>
            _inner.ListAsync(prefix, ct);

        public Task<Result<ProviderHealth>> CheckHealthAsync(CancellationToken ct) =>
            _inner.CheckHealthAsync(ct);

        public Task<Result<long>> GetUsedBytesAsync(CancellationToken ct) =>
            _inner.GetUsedBytesAsync(ct);

        public Task<Result<long?>> GetQuotaBytesAsync(CancellationToken ct) =>
            _inner.GetQuotaBytesAsync(ct);
    }
}
