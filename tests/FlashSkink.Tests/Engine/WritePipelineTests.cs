using System.Buffers;
using System.IO.Hashing;
using System.Security.Cryptography;
using Dapper;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Engine;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Storage;
using FlashSkink.Tests.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IO;
using Xunit;

namespace FlashSkink.Tests.Engine;

// ── Test doubles ─────────────────────────────────────────────────────────────

/// <summary>
/// Captures all published notifications for assertion in tests. Set
/// <see cref="ThrowOnPublish"/> to verify that a failing bus does not mask the original error.
/// </summary>
internal sealed class RecordingNotificationBus : INotificationBus
{
    private readonly List<Notification> _notifications = [];

    public IReadOnlyList<Notification> Published => _notifications;

    public bool ThrowOnPublish { get; set; }

    public ValueTask PublishAsync(Notification notification, CancellationToken ct = default)
    {
        if (ThrowOnPublish)
        {
            throw new InvalidOperationException("Test bus configured to throw.");
        }

        _notifications.Add(notification);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A <see cref="Stream"/> that returns a fixed content and signals a
/// <see cref="CancellationTokenSource"/> after a given number of bytes have been read.
/// Used to test mid-stream cancellation.
/// </summary>
internal sealed class CancelAfterNBytesStream(byte[] content, int cancelAfterBytes) : Stream
{
    private int _position;
    private CancellationTokenSource? _cts;

    public void SetCts(CancellationTokenSource cts) => _cts = cts;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= content.Length)
        {
            return 0;
        }

        int toRead = Math.Min(buffer.Length, content.Length - _position);
        content.AsSpan(_position, toRead).CopyTo(buffer.Span);
        _position += toRead;

        if (_position >= cancelAfterBytes && _cts is not null)
        {
            _cts.Cancel();
        }

        await Task.Yield(); // yield so cancellation can propagate
        cancellationToken.ThrowIfCancellationRequested();
        return toRead;
    }
}

/// <summary>A seekable stream whose reported <see cref="Length"/> is larger than actual content.</summary>
internal sealed class LyingSeekableStream(byte[] content, long reportedLength) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => reportedLength;
    public override long Position { get => _position; set => _position = (int)value; }
    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= content.Length)
        {
            return 0;
        }

        int toRead = Math.Min(count, content.Length - _position);
        Array.Copy(content, _position, buffer, offset, toRead);
        _position += toRead;
        return toRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => (int)offset,
            SeekOrigin.Current => _position + (int)offset,
            SeekOrigin.End => content.Length + (int)offset,
            _ => _position,
        };
        return _position;
    }
}

// ── Test class ────────────────────────────────────────────────────────────────

public sealed class WritePipelineTests : IAsyncLifetime, IDisposable
{
    private static readonly byte[] Dek = new byte[32]; // all-zeros DEK for tests

    private readonly SqliteConnection _connection;
    private readonly string _skinkRoot;
    private readonly RecordingNotificationBus _bus;
    private readonly WritePipeline _pipeline;
    private readonly VolumeContext _context;

    public WritePipelineTests()
    {
        _connection = BrainTestHelper.CreateInMemoryConnection();
        _skinkRoot = Path.Combine(Path.GetTempPath(), "flashskink-pipe-tests", Guid.NewGuid().ToString("N"));

        // Create required skink directories.
        Directory.CreateDirectory(Path.Combine(_skinkRoot, ".flashskink", "staging"));
        Directory.CreateDirectory(Path.Combine(_skinkRoot, ".flashskink", "blobs"));

        _bus = new RecordingNotificationBus();

        var loggerFactory = NullLoggerFactory.Instance;
        _pipeline = new WritePipeline(
            new FileTypeService(),
            new EntropyDetector(),
            loggerFactory);

        var wal = new WalRepository(_connection, NullLoggerFactory.Instance.CreateLogger<WalRepository>());
        var blobs = new BlobRepository(_connection, NullLoggerFactory.Instance.CreateLogger<BlobRepository>());
        var files = new FileRepository(_connection, wal, NullLoggerFactory.Instance.CreateLogger<FileRepository>());
        var activity = new ActivityLogRepository(_connection, NullLoggerFactory.Instance.CreateLogger<ActivityLogRepository>());

        _context = new VolumeContext(
            brainConnection: _connection,
            dek: Dek.AsMemory(),
            skinkRoot: _skinkRoot,
            sha256: IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            crypto: new CryptoPipeline(),
            compression: new CompressionService(),
            blobWriter: new AtomicBlobWriter(NullLoggerFactory.Instance.CreateLogger<AtomicBlobWriter>()),
            streamManager: new RecyclableMemoryStreamManager(),
            notificationBus: _bus,
            blobs: blobs,
            files: files,
            wal: wal,
            activityLog: activity);
    }

    public async Task InitializeAsync() => await BrainTestHelper.ApplySchemaAsync(_connection);

    public Task DisposeAsync()
    {
        _context.Dispose();
        _connection.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_skinkRoot))
        {
            Directory.Delete(_skinkRoot, recursive: true);
        }
    }

    private static Stream TextStream(string content) =>
        new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

    private static Stream BytesStream(byte[] content) => new MemoryStream(content);

    // ── Happy-path round-trip tests ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SmallTextFile_ProducesFilesBlobsAndActivityLogRows()
    {
        byte[] payload = new byte[200];
        Array.Fill(payload, (byte)'A');
        const string virtualPath = "/notes/hello.txt";

        var result = await _pipeline.ExecuteAsync(
            BytesStream(payload), virtualPath, _context, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var receipt = result.Value!;

        Assert.Equal(WriteStatus.Written, receipt.Status);
        Assert.False(string.IsNullOrEmpty(receipt.FileId));
        Assert.False(string.IsNullOrEmpty(receipt.BlobId));
        Assert.Equal(200L, receipt.PlaintextSize);
        // EncryptedSize = HeaderSize(20) + ciphertext(200) + Tag(16) = 236 bytes (uncompressed small text)
        // or less if compressed. Either way EncryptedSize > 0.
        Assert.True(receipt.EncryptedSize > 0);
        Assert.Equal(".txt", receipt.Extension);

        var filesCount = _connection.QuerySingle<int>("SELECT COUNT(*) FROM Files WHERE IsFolder = 0");
        Assert.Equal(1, filesCount);

        var blobsCount = _connection.QuerySingle<int>("SELECT COUNT(*) FROM Blobs");
        Assert.Equal(1, blobsCount);

        var activityCount = _connection.QuerySingle<int>("SELECT COUNT(*) FROM ActivityLog");
        Assert.Equal(1, activityCount);

        var tailCount = _connection.QuerySingle<int>("SELECT COUNT(*) FROM TailUploads");
        Assert.Equal(0, tailCount); // no providers configured

        // Blob file must exist on disk.
        var destPath = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, receipt.BlobId);
        Assert.True(File.Exists(destPath), $"Blob file not found at: {destPath}");
    }

    [Fact]
    public async Task ExecuteAsync_AutoCreatesIntermediateFolders_IdempotentOnRepeatedWrites()
    {
        byte[] content = [0x01, 0x02];
        await _pipeline.ExecuteAsync(BytesStream(content), "/a/b/c/file.txt", _context, CancellationToken.None);
        await _pipeline.ExecuteAsync(BytesStream(content), "/a/b/d/file2.txt", _context, CancellationToken.None);

        // Folders: a, b, c, d (4 folders)
        var folders = _connection.QuerySingle<int>("SELECT COUNT(*) FROM Files WHERE IsFolder = 1");
        Assert.Equal(4, folders);

        // Files: file.txt, file2.txt (2 files); same content → second write hits change-detection
        // and short-circuits at Unchanged, but file.txt exists and path differs so file2.txt is a new blob.
        // Actually for this test content is the same. For /a/b/d/file2.txt the path differs so a new blob is written.
        var files = _connection.QuerySingle<int>("SELECT COUNT(*) FROM Files WHERE IsFolder = 0");
        Assert.Equal(2, files);

        // Verify folder hierarchy
        var aFolder = _connection.QuerySingleOrDefault<string>(
            "SELECT FileID FROM Files WHERE Name = 'a' AND IsFolder = 1");
        Assert.NotNull(aFolder);

        var bFolder = _connection.QuerySingleOrDefault<string>(
            "SELECT FileID FROM Files WHERE Name = 'b' AND IsFolder = 1");
        Assert.NotNull(bFolder);
    }

    [Fact]
    public async Task ExecuteAsync_RootLevelFile_ParentIdIsNull()
    {
        var result = await _pipeline.ExecuteAsync(
            BytesStream([0xAB]), "/root.txt", _context, CancellationToken.None);

        Assert.True(result.Success);
        var parentId = _connection.QuerySingleOrDefault<string>(
            "SELECT ParentID FROM Files WHERE FileID = @Id",
            new { Id = result.Value!.FileId });
        Assert.Null(parentId);
    }

    // ── Compression branch tests ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HighlyCompressible100KB_BlobsCompressionIsLz4OrZstd()
    {
        // 100 KB of the same byte — extremely compressible
        byte[] payload = new byte[100 * 1024];
        Array.Fill(payload, (byte)0xAB);

        var result = await _pipeline.ExecuteAsync(
            BytesStream(payload), "/data/compressible.bin", _context, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var compression = _connection.QuerySingleOrDefault<string?>(
            "SELECT Compression FROM Blobs WHERE BlobID = @Id",
            new { Id = result.Value!.BlobId });

        // LZ4 is used below 512 KB; Zstd above
        Assert.True(compression is "LZ4" or "ZSTD",
            $"Expected LZ4 or ZSTD compression; got: {compression}");
        // Encrypted blob should be smaller than plaintext + header + tag
        Assert.True(result.Value!.EncryptedSize < payload.Length + 20 + 16,
            "Expected compressed blob to be smaller than uncompressed.");
    }

    [Fact]
    public async Task ExecuteAsync_RandomBytes1MB_NoGain_BlobsCompressionIsNull()
    {
        // 1 MB of seeded random bytes — incompressible
        var rng = new Random(42);
        byte[] payload = new byte[1024 * 1024];
        rng.NextBytes(payload);

        var result = await _pipeline.ExecuteAsync(
            BytesStream(payload), "/data/random.bin", _context, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var compression = _connection.QuerySingleOrDefault<string?>(
            "SELECT Compression FROM Blobs WHERE BlobID = @Id",
            new { Id = result.Value!.BlobId });

        Assert.Null(compression);
        // Uncompressed: EncryptedSize = 20 + 1048576 + 16 = 1048612
        Assert.Equal(payload.Length + 20 + 16, result.Value!.EncryptedSize);
    }

    [Fact]
    public async Task ExecuteAsync_BlobHeaderFlagsMatchCompression_OnDiskRoundTrip()
    {
        // 100 KB of zeros → highly compressible → LZ4 (below 512 KB threshold)
        byte[] payload = new byte[100 * 1024];

        var result = await _pipeline.ExecuteAsync(
            BytesStream(payload), "/data/zeros.bin", _context, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var dest = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, result.Value!.BlobId);
        var onDisk = await File.ReadAllBytesAsync(dest);

        var parseResult = BlobHeader.Parse(onDisk, out BlobFlags flags, out _);
        Assert.True(parseResult.Success);

        var compression = _connection.QuerySingle<string?>(
            "SELECT Compression FROM Blobs WHERE BlobID = @Id",
            new { Id = result.Value!.BlobId });

        // The on-disk header flags must agree with the Blobs.Compression column.
        if (compression == "LZ4")
        {
            Assert.Equal(BlobFlags.CompressedLz4, flags);
        }
        else if (compression == "ZSTD")
        {
            Assert.Equal(BlobFlags.CompressedZstd, flags);
        }
        else
        {
            Assert.Equal(BlobFlags.None, flags);
        }
    }

    // ── Change-detection short-circuit tests ──────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SameContentSamePath_SecondCallReturnsUnchanged()
    {
        byte[] content = new byte[200];
        Array.Fill(content, (byte)'Z');
        const string path = "/docs/file.txt";

        var first = await _pipeline.ExecuteAsync(BytesStream(content), path, _context, CancellationToken.None);
        Assert.True(first.Success);
        Assert.Equal(WriteStatus.Written, first.Value!.Status);

        var second = await _pipeline.ExecuteAsync(BytesStream(content), path, _context, CancellationToken.None);
        Assert.True(second.Success);
        Assert.Equal(WriteStatus.Unchanged, second.Value!.Status);
        Assert.Equal(first.Value!.BlobId, second.Value!.BlobId);

        var blobCount = _connection.QuerySingle<int>("SELECT COUNT(*) FROM Blobs");
        Assert.Equal(1, blobCount); // no new blob

        var fileCount = _connection.QuerySingle<int>("SELECT COUNT(*) FROM Files WHERE IsFolder = 0");
        Assert.Equal(1, fileCount); // no new file row

        var activityCount = _connection.QuerySingle<int>("SELECT COUNT(*) FROM ActivityLog");
        Assert.Equal(1, activityCount); // no activity log on Unchanged path
    }

    [Fact]
    public async Task ExecuteAsync_SameContentDifferentPath_WritesNewBlobAndFile()
    {
        byte[] content = new byte[200];
        Array.Fill(content, (byte)'X');

        var a = await _pipeline.ExecuteAsync(BytesStream(content), "/a.txt", _context, CancellationToken.None);
        var b = await _pipeline.ExecuteAsync(BytesStream(content), "/b.txt", _context, CancellationToken.None);

        Assert.True(a.Success);
        Assert.True(b.Success);

        // V1: no cross-path dedup — each path gets its own blob
        var blobCount = _connection.QuerySingle<int>("SELECT COUNT(*) FROM Blobs");
        Assert.Equal(2, blobCount);
        Assert.NotEqual(a.Value!.BlobId, b.Value!.BlobId);

        var fileCount = _connection.QuerySingle<int>("SELECT COUNT(*) FROM Files WHERE IsFolder = 0");
        Assert.Equal(2, fileCount);
    }

    [Fact]
    public async Task ExecuteAsync_DifferentContentSamePath_ReturnsPathConflict()
    {
        // Write file.txt with content A.
        await _pipeline.ExecuteAsync(
            BytesStream([0x01, 0x02, 0x03]),
            "/file.txt", _context, CancellationToken.None);

        // Write different content to the same path — should hit UNIQUE constraint.
        var second = await _pipeline.ExecuteAsync(
            BytesStream([0x04, 0x05, 0x06]),
            "/file.txt", _context, CancellationToken.None);

        Assert.False(second.Success);
        Assert.Equal(ErrorCode.PathConflict, second.Error!.Code);
    }

    // ── Cancellation tests ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CancelledBeforeStart_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _pipeline.ExecuteAsync(
            BytesStream([0x01]), "/file.bin", _context, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);

        var blobsCount = _connection.QuerySingle<int>("SELECT COUNT(*) FROM Blobs");
        Assert.Equal(0, blobsCount);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledMidLargeWrite_LeavesNoOrphans()
    {
        byte[] payload = new byte[100 * 1024]; // 100 KB
        Array.Fill(payload, (byte)0xFF);

        using var cts = new CancellationTokenSource();
        // Cancel after reading ~50 KB (halfway through the non-seekable stream).
        var cancelStream = new CancelAfterNBytesStream(payload, cancelAfterBytes: 50 * 1024);
        cancelStream.SetCts(cts);

        var result = await _pipeline.ExecuteAsync(cancelStream, "/large.bin", _context, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);

        // No orphan staging file.
        var stagingDir = Path.Combine(_skinkRoot, ".flashskink", "staging");
        var stagingFiles = Directory.GetFiles(stagingDir);
        Assert.Empty(stagingFiles);

        // No brain rows.
        Assert.Equal(0, _connection.QuerySingle<int>("SELECT COUNT(*) FROM Files"));
        Assert.Equal(0, _connection.QuerySingle<int>("SELECT COUNT(*) FROM Blobs"));
    }

    // ── FileTooLong enforcement ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SeekableSourceLongerThanCap_ReturnsFileTooLong_QuickReturn()
    {
        // Stream that lies about its length (cap + 1) but yields no real bytes.
        long cap = VolumeContext.MaxPlaintextBytes;
        using var tooLong = new LyingSeekableStream([], cap + 1);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _pipeline.ExecuteAsync(tooLong, "/huge.bin", _context, CancellationToken.None);
        sw.Stop();

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.FileTooLong, result.Error!.Code);
        // Must return quickly without trying to allocate 4+ GiB.
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Expected quick return for seekable FileTooLong check, took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task ExecuteAsync_NonSeekableSourceExceedsCap_ReturnsFileTooLong()
    {
        // A non-seekable stream that synthesizes MaxPlaintextBytes + 1 bytes in 64 KB chunks.
        long cap = VolumeContext.MaxPlaintextBytes;
        using var tooLong = new SyntheticNonSeekableStream(cap + 1);

        var result = await _pipeline.ExecuteAsync(tooLong, "/huge.bin", _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.FileTooLong, result.Error!.Code);
        Assert.NotEqual(ErrorCode.Cancelled, result.Error!.Code);
    }

    // ── Brain-tx-failure / fault injection ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_PathConflictDuringCommit_RollsBackAndMarksWalFailed()
    {
        // First write succeeds.
        var first = await _pipeline.ExecuteAsync(
            BytesStream([0x01, 0x02, 0x03]),
            "/conflict.txt", _context, CancellationToken.None);
        Assert.True(first.Success);

        // Second write — different content, same path → hits UNIQUE constraint on Files.
        var second = await _pipeline.ExecuteAsync(
            BytesStream([0xAA, 0xBB, 0xCC]),
            "/conflict.txt", _context, CancellationToken.None);

        Assert.False(second.Success);
        Assert.Equal(ErrorCode.PathConflict, second.Error!.Code);

        // No orphan staging file.
        var stagingDir = Path.Combine(_skinkRoot, ".flashskink", "staging");
        Assert.Empty(Directory.GetFiles(stagingDir));

        // The on-disk destination blob from the failed write must be gone.
        // (Can't check by BlobId easily since the write failed — check total Blobs count instead.)
        var blobCount = _connection.QuerySingle<int>("SELECT COUNT(*) FROM Blobs");
        Assert.Equal(1, blobCount); // only the first write's blob

        // WAL row from the second write must be FAILED; no incomplete (PREPARE) rows remain.
        var incompleteWalRows = _connection.Query<string>(
            "SELECT Phase FROM WAL WHERE Phase NOT IN ('COMMITTED', 'FAILED')").ToList();
        Assert.Empty(incompleteWalRows); // no rows stuck in PREPARE or other transitional states

        var walFailed = _connection.Query<string>(
            "SELECT Phase FROM WAL WHERE Phase = 'FAILED'").ToList();
        Assert.Single(walFailed); // one FAILED row from the second write
    }

    [Fact]
    public async Task ExecuteAsync_PathConflictDuringCommit_PublishesNotification()
    {
        await _pipeline.ExecuteAsync(
            BytesStream([0x01]),
            "/notify.txt", _context, CancellationToken.None);

        await _pipeline.ExecuteAsync(
            BytesStream([0x02]),
            "/notify.txt", _context, CancellationToken.None);

        var notifications = _bus.Published;
        Assert.Single(notifications);
        var n = notifications[0];
        Assert.Equal("WritePipeline", n.Source);
        Assert.Equal(NotificationSeverity.Error, n.Severity);
        Assert.Equal("Could not save file", n.Title);
        Assert.Equal(ErrorCode.PathConflict, n.Error!.Code);
        Assert.Contains("/notify.txt", n.Message);

        // Principle 25 + 26: no appliance vocabulary or DEK references in the message.
        Assert.DoesNotContain("blob", n.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WAL", n.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DEK", n.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Notification-not-published-on-cancellation test ───────────────────────

    [Fact]
    public async Task ExecuteAsync_Cancellation_DoesNotPublishNotification()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await _pipeline.ExecuteAsync(BytesStream([0x01]), "/cancel.txt", _context, cts.Token);

        Assert.Empty(_bus.Published);
    }

    // ── XXHash64 sanity ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_BlobsEncryptedXxHashMatchesXxHashOfOnDiskBlob()
    {
        byte[] content = new byte[512];
        Array.Fill(content, (byte)0x55);

        var result = await _pipeline.ExecuteAsync(
            BytesStream(content), "/checksum.bin", _context, CancellationToken.None);

        Assert.True(result.Success);
        var blobId = result.Value!.BlobId;

        var dest = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId);
        byte[] onDisk = await File.ReadAllBytesAsync(dest);

        ulong expectedHash = XxHash64.HashToUInt64(onDisk);
        string expectedHex = expectedHash.ToString("x16");

        var storedHash = _connection.QuerySingle<string>(
            "SELECT EncryptedXXHash FROM Blobs WHERE BlobID = @Id", new { Id = blobId });

        Assert.Equal(expectedHex, storedHash);
    }

    // ── Failing notification bus test ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FailingNotificationBus_StillReturnsOriginalFailure()
    {
        _bus.ThrowOnPublish = true;

        // First write succeeds; second hits PathConflict and publishes (which throws).
        await _pipeline.ExecuteAsync(BytesStream([0x01]), "/bus-test.txt", _context, CancellationToken.None);

        var result = await _pipeline.ExecuteAsync(
            BytesStream([0x02]), "/bus-test.txt", _context, CancellationToken.None);

        // Even though the bus threw, the original failure is returned.
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.PathConflict, result.Error!.Code);
    }

    // ── AAD construction test ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AadOrderCorrect_WrongOrderFailsDecryption()
    {
        // This is an indirect test: write a file and decrypt with wrong AAD ordering.
        // The decrypt should fail with DecryptionFailed, proving the AAD is order-sensitive.
        byte[] payload = new byte[100];
        Array.Fill(payload, (byte)0xCC);

        var result = await _pipeline.ExecuteAsync(
            BytesStream(payload), "/aad-test.bin", _context, CancellationToken.None);
        Assert.True(result.Success);

        var blobId = result.Value!.BlobId;
        var sha256Hex = _connection.QuerySingle<string>(
            "SELECT PlaintextSHA256 FROM Blobs WHERE BlobID = @Id", new { Id = blobId });

        // Reconstruct correct AAD: 16 raw GUID bytes || 32 raw SHA-256 bytes
        var blobGuid = Guid.ParseExact(blobId, "N");
        byte[] correctAad = new byte[48];
        blobGuid.TryWriteBytes(correctAad.AsSpan(0, 16));
        Convert.FromHexString(sha256Hex).AsSpan(0, 32).CopyTo(correctAad.AsSpan(16));

        // Wrong ordering: SHA-256 first, then GUID
        byte[] wrongOrderAad = new byte[48];
        Convert.FromHexString(sha256Hex).AsSpan(0, 32).CopyTo(wrongOrderAad.AsSpan(0, 32));
        blobGuid.TryWriteBytes(wrongOrderAad.AsSpan(32, 16));

        var dest = AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId);
        byte[] onDisk = await File.ReadAllBytesAsync(dest);

        using var decOut = new ArrayMemoryOwner<byte>(payload.Length + 100);
        var decCorrect = _context.Crypto.Decrypt(onDisk, Dek, correctAad, decOut, out _, out _);
        Assert.True(decCorrect.Success, "Decryption with correct AAD must succeed.");

        var decWrong = _context.Crypto.Decrypt(onDisk, Dek, wrongOrderAad, decOut, out _, out _);
        Assert.False(decWrong.Success, "Decryption with wrong-order AAD must fail.");
        Assert.Equal(ErrorCode.DecryptionFailed, decWrong.Error!.Code);
    }
}

// ── Test helper streams ───────────────────────────────────────────────────────

/// <summary>Non-seekable stream that synthesizes <paramref name="totalBytes"/> bytes of zeros in 64 KB chunks.</summary>
file sealed class SyntheticNonSeekableStream(long totalBytes) : Stream
{
    private long _remaining = totalBytes;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        int toRead = (int)Math.Min(count, Math.Min(_remaining, 65536));
        Array.Clear(buffer, offset, toRead);
        _remaining -= toRead;
        return toRead;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Minimal <see cref="IMemoryOwner{T}"/> backed by a plain array — for decrypt output in tests.</summary>
file sealed class ArrayMemoryOwner<T>(int length) : IMemoryOwner<T>
{
    private readonly T[] _array = new T[length];
    public Memory<T> Memory => _array.AsMemory();
    public void Dispose() { }
}
