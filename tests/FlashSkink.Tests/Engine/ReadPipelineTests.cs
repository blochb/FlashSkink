using System.Buffers;
using System.Security.Cryptography;
using System.Text;
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

public sealed class ReadPipelineTests : IAsyncLifetime, IDisposable
{
    private static readonly byte[] Dek = new byte[32]; // all-zeros DEK for tests

    private readonly SqliteConnection _connection;
    private readonly string _skinkRoot;
    private readonly RecordingNotificationBus _bus;
    private readonly WritePipeline _writePipeline;
    private readonly ReadPipeline _readPipeline;
    private readonly VolumeContext _context;

    public ReadPipelineTests()
    {
        _connection = BrainTestHelper.CreateInMemoryConnection();
        _skinkRoot = Path.Combine(Path.GetTempPath(), "flashskink-read-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(_skinkRoot, ".flashskink", "staging"));
        Directory.CreateDirectory(Path.Combine(_skinkRoot, ".flashskink", "blobs"));

        _bus = new RecordingNotificationBus();

        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
        _writePipeline = new WritePipeline(new FileTypeService(), new EntropyDetector(), loggerFactory);
        _readPipeline = new ReadPipeline(loggerFactory);

        var wal = new WalRepository(_connection, loggerFactory.CreateLogger<WalRepository>());
        var blobs = new BlobRepository(_connection, loggerFactory.CreateLogger<BlobRepository>());
        var files = new FileRepository(_connection, wal, loggerFactory.CreateLogger<FileRepository>());
        var activity = new ActivityLogRepository(_connection, loggerFactory.CreateLogger<ActivityLogRepository>());

        _context = new VolumeContext(
            brainConnection: _connection,
            dek: Dek.AsMemory(),
            skinkRoot: _skinkRoot,
            sha256: IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            crypto: new CryptoPipeline(),
            compression: new CompressionService(),
            blobWriter: new AtomicBlobWriter(loggerFactory.CreateLogger<AtomicBlobWriter>()),
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> WriteAndGetBlobIdAsync(string virtualPath, byte[] content)
    {
        var result = await _writePipeline.ExecuteAsync(
            new MemoryStream(content), virtualPath, _context, CancellationToken.None);
        Assert.True(result.Success, $"Write failed: {result.Error?.Message}");
        return result.Value!.BlobId;
    }

    private string GetBlobPath(string blobId) =>
        AtomicBlobWriter.ComputeDestinationPath(_skinkRoot, blobId);

    private async Task<(Result result, byte[] content)> ReadFileAsync(
        string virtualPath, CancellationToken ct = default)
    {
        var dest = new MemoryStream();
        var result = await _readPipeline.ExecuteAsync(virtualPath, dest, _context, ct);
        return (result, dest.ToArray());
    }

    // ── Happy-path round-trip tests ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SmallTextFile_RoundTripsBytes()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('A', 200));
        await WriteAndGetBlobIdAsync("/notes/hello.txt", payload);

        var (result, content) = await ReadFileAsync("/notes/hello.txt");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(payload, content);
    }

    [Fact]
    public async Task ExecuteAsync_5MBRandomBytes_RoundTripsBytes()
    {
        var payload = new byte[5 * 1024 * 1024];
        new Random(42).NextBytes(payload);
        await WriteAndGetBlobIdAsync("/data/large.bin", payload);

        var (result, content) = await ReadFileAsync("/data/large.bin");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(payload, content);
    }

    [Fact]
    public async Task ExecuteAsync_LargeMostlyZeros_RoundTripsBytes()
    {
        // 2 MB of all-zeros — highly compressible (Zstd branch exercised)
        var payload = new byte[2 * 1024 * 1024];
        await WriteAndGetBlobIdAsync("/data/zeros.bin", payload);

        var (result, content) = await ReadFileAsync("/data/zeros.bin");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(payload, content);
    }

    [Fact]
    public async Task ExecuteAsync_HighlyCompressible100KB_RoundTripsBytes()
    {
        // 100 KB of repeated byte — high compression ratio (LZ4 branch exercised)
        var payload = new byte[100 * 1024];
        Array.Fill(payload, (byte)0xAB);
        await WriteAndGetBlobIdAsync("/data/repetitive.bin", payload);

        var (result, content) = await ReadFileAsync("/data/repetitive.bin");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(payload, content);
    }

    [Fact]
    public async Task ExecuteAsync_NoGainPayload_RoundTripsBytes()
    {
        // 1 MB random — compression gain rejected, BlobFlags.None branch exercised
        var payload = new byte[1024 * 1024];
        new Random(123).NextBytes(payload);
        await WriteAndGetBlobIdAsync("/data/random.bin", payload);

        var (result, content) = await ReadFileAsync("/data/random.bin");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(payload, content);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyFile_RoundTripsZeroBytes()
    {
        await WriteAndGetBlobIdAsync("/empty.bin", []);

        var (result, content) = await ReadFileAsync("/empty.bin");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(content);
    }

    [Fact]
    public async Task ExecuteAsync_RootLevelFile_ReadByPath()
    {
        byte[] payload = Encoding.UTF8.GetBytes("root content");
        await WriteAndGetBlobIdAsync("/root.txt", payload);

        var (result, content) = await ReadFileAsync("/root.txt");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(payload, content);
    }

    [Fact]
    public async Task ExecuteAsync_NestedPath_ResolvesCorrectly()
    {
        byte[] payload = Encoding.UTF8.GetBytes("nested");
        await WriteAndGetBlobIdAsync("/a/b/c/file.txt", payload);

        var (result, content) = await ReadFileAsync("/a/b/c/file.txt");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(payload, content);
    }

    // ── Brain-shape errors ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_VirtualPathNotFound_ReturnsFileNotFound()
    {
        var (result, _) = await ReadFileAsync("/no/such/file.txt");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.FileNotFound, result.Error!.Code);
        Assert.Single(_bus.Published);
        // Message must reference the path but not contain forbidden appliance vocabulary.
        Assert.DoesNotContain("blob", _bus.Published[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_VirtualPathIsFolder_ReturnsFileNotFound()
    {
        // Insert a folder row directly so VirtualPath matches what ReadPipeline receives.
        var now = DateTime.UtcNow;
        await _connection.ExecuteAsync(
            "INSERT INTO Files " +
            "(FileID, ParentID, IsFolder, IsSymlink, SymlinkTarget, Name, Extension, MimeType, " +
            "VirtualPath, SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc, BlobID) " +
            "VALUES (@Id, NULL, 1, 0, NULL, 'myfolder', NULL, NULL, '/myfolder', 0, @Now, @Now, @Now, NULL)",
            new { Id = Guid.NewGuid().ToString(), Now = now.ToString("O") });

        var (result, _) = await ReadFileAsync("/myfolder");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.FileNotFound, result.Error!.Code);
        Assert.Contains("folder", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_FileWithoutBlob_ReturnsBlobCorrupt()
    {
        var now = DateTime.UtcNow;
        await _connection.ExecuteAsync(
            "INSERT INTO Files " +
            "(FileID, ParentID, IsFolder, IsSymlink, SymlinkTarget, Name, Extension, MimeType, " +
            "VirtualPath, SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc, BlobID) " +
            "VALUES (@Id, NULL, 0, 0, NULL, 'orphan.txt', NULL, NULL, '/orphan.txt', 0, @Now, @Now, @Now, NULL)",
            new { Id = Guid.NewGuid().ToString(), Now = now.ToString("O") });

        var (result, _) = await ReadFileAsync("/orphan.txt");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.BlobCorrupt, result.Error!.Code);
    }

    [Fact]
    public async Task ExecuteAsync_BlobIdNotInBlobsTable_ReturnsBlobCorrupt()
    {
        // Files row references a BlobID that has no matching Blobs row.
        // PRAGMA foreign_keys = OFF is a no-op inside a transaction, so we use SqliteCommand
        // directly (synchronous) to toggle FK enforcement — Dapper's ExecuteAsync may implicitly
        // wrap statements in a transaction on some SQLite driver builds.
        string fakeBlobId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        using (var offCmd = _connection.CreateCommand())
        {
            offCmd.CommandText = "PRAGMA foreign_keys = OFF";
            offCmd.ExecuteNonQuery();
        }

        await _connection.ExecuteAsync(
            "INSERT INTO Files " +
            "(FileID, ParentID, IsFolder, IsSymlink, SymlinkTarget, Name, Extension, MimeType, " +
            "VirtualPath, SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc, BlobID) " +
            "VALUES (@Id, NULL, 0, 0, NULL, 'missing.txt', NULL, NULL, '/missing.txt', 0, @Now, @Now, @Now, @BlobId)",
            new { Id = Guid.NewGuid().ToString(), BlobId = fakeBlobId, Now = now.ToString("O") });

        using (var onCmd = _connection.CreateCommand())
        {
            onCmd.CommandText = "PRAGMA foreign_keys = ON";
            onCmd.ExecuteNonQuery();
        }

        var (result, _) = await ReadFileAsync("/missing.txt");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.BlobCorrupt, result.Error!.Code);
    }

    [Fact]
    public async Task ExecuteAsync_PlaintextSizeExceedsCap_ReturnsFileTooLong()
    {
        await WriteAndGetBlobIdAsync("/capped.bin", new byte[100]);
        await _connection.ExecuteAsync(
            "UPDATE Blobs SET PlaintextSize = @Cap",
            new { Cap = VolumeContext.MaxPlaintextBytes + 1 });

        var (result, _) = await ReadFileAsync("/capped.bin");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.FileTooLong, result.Error!.Code);
    }

    // ── On-disk corruption tests ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_BlobFileMissing_ReturnsBlobCorrupt()
    {
        string blobId = await WriteAndGetBlobIdAsync("/present.bin", new byte[64]);
        File.Delete(GetBlobPath(blobId));

        var (result, _) = await ReadFileAsync("/present.bin");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.BlobCorrupt, result.Error!.Code);
        Assert.Single(_bus.Published);
        Assert.Equal(NotificationSeverity.Error, _bus.Published[0].Severity);
    }

    [Fact]
    public async Task ExecuteAsync_BlobFileShorterThanRecorded_ReturnsBlobCorrupt()
    {
        var payload = new byte[256];
        new Random(7).NextBytes(payload);
        string blobId = await WriteAndGetBlobIdAsync("/short.bin", payload);

        // Truncate the blob to half its size.
        string blobPath = GetBlobPath(blobId);
        byte[] original = File.ReadAllBytes(blobPath);
        File.WriteAllBytes(blobPath, original[..(original.Length / 2)]);

        var (result, _) = await ReadFileAsync("/short.bin");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.BlobCorrupt, result.Error!.Code);
    }

    [Fact]
    public async Task ExecuteAsync_BlobFileLongerThanRecorded_ReturnsBlobCorrupt()
    {
        // Symmetric counterpart to BlobFileShorterThanRecorded — appending bytes exercises the
        // fs.Length != expectedSize guard from the other direction.
        var payload = new byte[256];
        new Random(8).NextBytes(payload);
        string blobId = await WriteAndGetBlobIdAsync("/long.bin", payload);
        string blobPath = GetBlobPath(blobId);

        await using (var fs = new FileStream(blobPath, FileMode.Append))
        {
            fs.Write(new byte[64]); // append 64 garbage bytes
        }

        var (result, _) = await ReadFileAsync("/long.bin");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.BlobCorrupt, result.Error!.Code);
    }

    [Fact]
    public async Task ExecuteAsync_TamperedHeaderMagic_ReturnsVolumeCorrupt()
    {
        // Header layout: [magic 4B][version 2B][flags 2B][nonce 12B] = 20 bytes
        string blobId = await WriteAndGetBlobIdAsync("/magic.bin", new byte[64]);
        string blobPath = GetBlobPath(blobId);
        byte[] blob = File.ReadAllBytes(blobPath);

        blob[0] ^= 0xFF; // corrupt the first magic byte
        File.WriteAllBytes(blobPath, blob);

        var (result, _) = await ReadFileAsync("/magic.bin");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeCorrupt, result.Error!.Code);
    }

    [Fact]
    public async Task ExecuteAsync_TamperedHeaderUnknownVersion_ReturnsVolumeIncompatibleVersion()
    {
        // Version field is bytes 4–5 (LE uint16). Set version to 2.
        string blobId = await WriteAndGetBlobIdAsync("/version.bin", new byte[64]);
        string blobPath = GetBlobPath(blobId);
        byte[] blob = File.ReadAllBytes(blobPath);

        blob[4] = 0x02;
        blob[5] = 0x00;
        File.WriteAllBytes(blobPath, blob);

        var (result, _) = await ReadFileAsync("/version.bin");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeIncompatibleVersion, result.Error!.Code);
    }

    [Fact]
    public async Task ExecuteAsync_TamperedHeaderUnknownFlags_ReturnsVolumeCorrupt()
    {
        // Flags field is bytes 6–7 (LE uint16). Set high byte to 0xFF → unknown bits.
        string blobId = await WriteAndGetBlobIdAsync("/flags.bin", new byte[64]);
        string blobPath = GetBlobPath(blobId);
        byte[] blob = File.ReadAllBytes(blobPath);

        blob[7] = 0xFF; // high byte of flags — bits 8–15 are not defined
        File.WriteAllBytes(blobPath, blob);

        var (result, _) = await ReadFileAsync("/flags.bin");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeCorrupt, result.Error!.Code);
    }

    [Fact]
    public async Task ExecuteAsync_TamperedCiphertextOneByte_ReturnsDecryptionFailed_AndCriticalNotification()
    {
        var payload = new byte[64];
        new Random(11).NextBytes(payload);
        string blobId = await WriteAndGetBlobIdAsync("/cipher.bin", payload);
        string blobPath = GetBlobPath(blobId);
        byte[] blob = File.ReadAllBytes(blobPath);

        blob[20] ^= 0x01; // byte 20 = first byte of ciphertext (after 20-byte header)
        File.WriteAllBytes(blobPath, blob);

        var dest = new MemoryStream();
        var result = await _readPipeline.ExecuteAsync("/cipher.bin", dest, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.DecryptionFailed, result.Error!.Code);
        Assert.Equal(0, dest.Length); // no plaintext written
        var notification = Assert.Single(_bus.Published);
        Assert.Equal(NotificationSeverity.Critical, notification.Severity);
        Assert.True(notification.RequiresUserAction);
        Assert.Equal("ReadPipeline", notification.Source);
    }

    [Fact]
    public async Task ExecuteAsync_TamperedTag_ReturnsDecryptionFailed()
    {
        var payload = new byte[64];
        new Random(22).NextBytes(payload);
        string blobId = await WriteAndGetBlobIdAsync("/tag.bin", payload);
        string blobPath = GetBlobPath(blobId);
        byte[] blob = File.ReadAllBytes(blobPath);

        blob[^1] ^= 0x01; // last byte = last byte of GCM tag (final 16 bytes)
        File.WriteAllBytes(blobPath, blob);

        var (result, _) = await ReadFileAsync("/tag.bin");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.DecryptionFailed, result.Error!.Code);
    }

    [Fact]
    public async Task ExecuteAsync_TamperedBrainSha256_ReturnsDecryptionFailed()
    {
        // SHA-256 is bound into AAD; changing it in the brain causes GCM auth failure.
        await WriteAndGetBlobIdAsync("/sha.bin", new byte[64]);
        await _connection.ExecuteAsync(
            "UPDATE Blobs SET PlaintextSHA256 = @AltHash",
            new { AltHash = new string('a', 64) }); // valid hex but wrong hash

        var (result, _) = await ReadFileAsync("/sha.bin");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.DecryptionFailed, result.Error!.Code);
    }

    [Fact]
    public async Task ExecuteAsync_SwappedOnDiskBlobs_ReturnsDecryptionFailed()
    {
        // Swap the on-disk blob files (NOT the brain rows) — file A's path now holds blob B's bytes.
        // Decryption uses file A's AAD but finds blob B's ciphertext → GCM tag mismatch.
        var rng = new Random(33);
        var payloadA = new byte[256];
        var payloadB = new byte[256];
        rng.NextBytes(payloadA);
        rng.NextBytes(payloadB); // both random, same size → same EncryptedSize, BlobFlags.None

        string blobIdA = await WriteAndGetBlobIdAsync("/swapA.bin", payloadA);
        string blobIdB = await WriteAndGetBlobIdAsync("/swapB.bin", payloadB);

        string pathA = GetBlobPath(blobIdA);
        string pathB = GetBlobPath(blobIdB);
        string temp = pathA + ".swap";
        File.Move(pathA, temp);
        File.Move(pathB, pathA);
        File.Move(temp, pathB);

        var (result, _) = await ReadFileAsync("/swapA.bin");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.DecryptionFailed, result.Error!.Code);
    }

    [Fact]
    public async Task ExecuteAsync_PlaintextSizeMismatch_AfterCorruption_ReturnsBlobCorrupt()
    {
        // Increment PlaintextSize by 1 without changing the actual blob.
        // The pipeline decrypts successfully (AAD unchanged), then finds payloadLen ≠ PlaintextSize.
        var payload = new byte[200];
        new Random(44).NextBytes(payload);
        await WriteAndGetBlobIdAsync("/mismatch.bin", payload);
        await _connection.ExecuteAsync(
            "UPDATE Blobs SET PlaintextSize = PlaintextSize + 1");

        var (result, _) = await ReadFileAsync("/mismatch.bin");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.BlobCorrupt, result.Error!.Code);
    }

    // ── ChecksumMismatch test (stage 6) ──────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HashMismatch_AfterDecompressionTampering_ReturnsChecksumMismatch_AndCriticalNotification()
    {
        // Construct a forged blob: encrypted payload B using AAD bound to payload A's metadata.
        // Decryption succeeds (AAD matches) but SHA-256(B) ≠ brain's PlaintextSHA256(A) → ChecksumMismatch.

        // Use incompressible payloads so BlobFlags.None is guaranteed → same EncryptedSize formula.
        var rng = new Random(55);
        var payloadA = new byte[200];
        var payloadB = new byte[200];
        rng.NextBytes(payloadA);
        rng.NextBytes(payloadB);

        string blobId = await WriteAndGetBlobIdAsync("/forged.bin", payloadA);

        // Read the brain's recorded SHA-256 for payload A.
        string sha256HexA = await _connection.QuerySingleAsync<string>(
            "SELECT PlaintextSHA256 FROM Blobs WHERE BlobID = @BlobId", new { BlobId = blobId });
        byte[] sha256A = Convert.FromHexString(sha256HexA);

        // Build the same AAD that ReadPipeline will use for this blob.
        Guid blobGuid = Guid.ParseExact(blobId, "N");
        byte[] aadArray = new byte[48];
        blobGuid.TryWriteBytes(aadArray.AsSpan(0, 16));
        sha256A.AsSpan(0, 32).CopyTo(aadArray.AsSpan(16, 32));

        // Encrypt payload B with that AAD — forged blob authenticates but contains wrong plaintext.
        const int encLen = 20 + 200 + 16; // HeaderSize(20) + plaintext(200) + TagSize(16)
        using var outputOwner = MemoryPool<byte>.Shared.Rent(encLen);
        var crypto = new CryptoPipeline();
        var encResult = crypto.Encrypt(payloadB, Dek, aadArray, BlobFlags.None, outputOwner, out int written);
        Assert.True(encResult.Success, encResult.Error?.Message);

        // Overwrite the on-disk blob with the forged bytes.
        File.WriteAllBytes(GetBlobPath(blobId), outputOwner.Memory.Span[..written].ToArray());

        var dest = new MemoryStream();
        var result = await _readPipeline.ExecuteAsync("/forged.bin", dest, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ChecksumMismatch, result.Error!.Code);
        Assert.Equal(0, dest.Length); // no plaintext written — stage 7 never runs
        var notification = Assert.Single(_bus.Published);
        Assert.Equal(NotificationSeverity.Critical, notification.Severity);
        Assert.True(notification.RequiresUserAction);
        Assert.Equal("ReadPipeline", notification.Source);
    }

    // ── Cancellation tests ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CancelledBeforeStart_ReturnsCancelled_AndPublishesNothing()
    {
        await WriteAndGetBlobIdAsync("/cancel.bin", new byte[64]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (result, content) = await ReadFileAsync("/cancel.bin", cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        Assert.Empty(content);
        Assert.Empty(_bus.Published); // Principle 14 — cancellation is not published
    }

    [Fact]
    public async Task ExecuteAsync_CancelledMidRead_ReturnsCancelled_AndPublishesNothing()
    {
        // Write a 5 MB file then immediately cancel the token — ReadBlobAsync propagates Cancelled.
        var payload = new byte[5 * 1024 * 1024];
        new Random(66).NextBytes(payload);
        await WriteAndGetBlobIdAsync("/bigcancel.bin", payload);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel so the first ReadAsync inside ReadBlobAsync throws

        var (result, _) = await ReadFileAsync("/bigcancel.bin", cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        Assert.Empty(_bus.Published);
    }

    // ── Notification audit tests ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DecryptionFailed_PublishesCriticalWithRequiresUserAction()
    {
        var payload = new byte[64];
        new Random(77).NextBytes(payload);
        string blobId = await WriteAndGetBlobIdAsync("/crit.bin", payload);
        byte[] blob = File.ReadAllBytes(GetBlobPath(blobId));
        blob[20] ^= 0xFF;
        File.WriteAllBytes(GetBlobPath(blobId), blob);

        await ReadFileAsync("/crit.bin");

        var notification = Assert.Single(_bus.Published);
        Assert.Equal(NotificationSeverity.Critical, notification.Severity);
        Assert.True(notification.RequiresUserAction);
    }

    [Fact]
    public async Task ExecuteAsync_AllPublishedNotifications_AvoidApplianceVocabulary()
    {
        // FileNotFound path
        await ReadFileAsync("/vocab-test-missing.bin");

        // BlobCorrupt path — missing on-disk file
        string blobId = await WriteAndGetBlobIdAsync("/vocab-test-corrupt.bin", new byte[32]);
        File.Delete(GetBlobPath(blobId));
        await ReadFileAsync("/vocab-test-corrupt.bin");

        // DecryptionFailed path — Critical severity; produces different Title/Message strings
        // ("File integrity check failed", "The skink may be damaged…") not reached by Error paths.
        string tamperBlobId = await WriteAndGetBlobIdAsync("/vocab-test-tamper.bin", new byte[64]);
        byte[] tamperBlob = File.ReadAllBytes(GetBlobPath(tamperBlobId));
        tamperBlob[20] ^= 0xFF; // flip one ciphertext byte → GCM tag mismatch
        File.WriteAllBytes(GetBlobPath(tamperBlobId), tamperBlob);
        await ReadFileAsync("/vocab-test-tamper.bin");

        // Collect every notification title + message published so far.
        string allText = string.Join(" ", _bus.Published.Select(n => n.Title + " " + n.Message));

        string[] forbidden = ["blob", "wal", "dek", "aad", "gcm", "stripe", "pragma", "sha-256", "sha256"];
        foreach (var word in forbidden)
        {
            Assert.False(allText.Contains(word, StringComparison.OrdinalIgnoreCase),
                $"Found forbidden word '{word}' in notification text: {allText}");
        }
    }

    [Fact]
    public async Task ExecuteAsync_FailingNotificationBus_StillReturnsOriginalFailure()
    {
        _bus.ThrowOnPublish = true;

        var (result, _) = await ReadFileAsync("/not-here.bin");

        // The original failure (FileNotFound) must be returned — bus failure must not mask it.
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.FileNotFound, result.Error!.Code);
    }

    // ── Sequential read witness ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TwoSequentialReadsOnSameVolume_BothSucceed()
    {
        // Verifies IncrementalHash state is properly reset between calls (§14.2 stage 6).
        byte[] payload = Encoding.UTF8.GetBytes("shared pipeline content");
        await WriteAndGetBlobIdAsync("/seq.txt", payload);

        var (r1, c1) = await ReadFileAsync("/seq.txt");
        var (r2, c2) = await ReadFileAsync("/seq.txt");

        Assert.True(r1.Success, r1.Error?.Message);
        Assert.True(r2.Success, r2.Error?.Message);
        Assert.Equal(payload, c1);
        Assert.Equal(payload, c2);
        Assert.Equal(c1, c2);
    }
}
