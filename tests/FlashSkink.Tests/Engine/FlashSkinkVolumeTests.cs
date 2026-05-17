using System.Security.Cryptography;
using System.Text;
using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Orchestration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Engine;

public sealed class FlashSkinkVolumeTests : IAsyncLifetime
{
    private string _skinkRoot = string.Empty;
    private const string Password = "test-password-123";

    private static VolumeCreationOptions DefaultOptions => new()
    {
        LoggerFactory = NullLoggerFactory.Instance,
        NotificationBus = new RecordingNotificationBus(),
    };

    public Task InitializeAsync()
    {
        _skinkRoot = Path.Combine(Path.GetTempPath(), $"flashskink-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_skinkRoot);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_skinkRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ── Factory tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_NewSkinkRoot_ReturnsOpenVolume()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        Assert.True(File.Exists(Path.Combine(_skinkRoot, ".flashskink", "vault.bin")));
        Assert.True(File.Exists(Path.Combine(_skinkRoot, ".flashskink", "brain.db")));
        Assert.NotNull(volume);
    }

    [Fact]
    public async Task CreateAsync_NewSkinkRoot_GeneratesRecoveryPhrase()
    {
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try
        {
            await receipt.Volume.DisposeAsync();

            Assert.Equal(24, receipt.RecoveryPhrase.Count);
            var wordSet = MnemonicService.Wordlist.ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < 24; i++)
            {
                Assert.Contains(receipt.RecoveryPhrase[i].ToString(), wordSet);
            }
        }
        finally
        {
            receipt.RecoveryPhrase.Dispose();
        }
    }

    [Fact]
    public async Task CreateAsync_DoesNotPersistRecoveryPhrase()
    {
        // Asserts the negative: §18.8 and §29 Decision A16 require the phrase to be
        // returned via VolumeCreationReceipt exactly once and persisted nowhere — not
        // in Settings, not in any other brain table. Defends against a future
        // regression that re-adds the row "for convenience".
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try
        {
            await receipt.Volume.DisposeAsync();
        }
        finally
        {
            receipt.RecoveryPhrase.Dispose();
        }

        var persisted = await ReadBrainSettingAsync("RecoveryPhrase");
        Assert.Null(persisted);
    }

    [Fact]
    public async Task CreateAsync_NewSkinkRoot_SeedsInitialSettings()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;
        await volume.DisposeAsync();

        Assert.NotNull(await ReadBrainSettingAsync("GracePeriodDays"));
        Assert.NotNull(await ReadBrainSettingAsync("AuditIntervalHours"));
        Assert.NotNull(await ReadBrainSettingAsync("VolumeCreatedUtc"));
        Assert.NotNull(await ReadBrainSettingAsync("AppVersion"));
    }

    [Fact]
    public async Task OpenAsync_ExistingVolume_ReturnsOpenVolume()
    {
        var create = await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions);
        await create.Value!.Volume.DisposeAsync();

        var open = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(open.Success);
        await open.Value!.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_WrongPassword_ReturnsFailResult()
    {
        var create = await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions);
        await create.Value!.Volume.DisposeAsync();

        var open = await FlashSkinkVolume.OpenAsync(_skinkRoot, "wrong-password", DefaultOptions);
        Assert.False(open.Success);
    }

    [Fact]
    public async Task OpenAsync_MissingVaultFile_ReturnsFailResult()
    {
        var emptyRoot = Path.Combine(_skinkRoot, "empty");
        Directory.CreateDirectory(emptyRoot);
        var open = await FlashSkinkVolume.OpenAsync(emptyRoot, Password, DefaultOptions);
        Assert.False(open.Success);
    }

    [Fact]
    public async Task CreateAsync_StaleBrainDb_NoVault_FailsGracefullyAndCleansVault()
    {
        // Simulates the state left by a prior interrupted CreateAsync: brain.db contains
        // garbage (encrypted with an old DEK), vault.bin was already cleaned up.
        // CreateAsync must return a failure result (not throw) and must not leave
        // a vault.bin on disk (vault created in this attempt must be cleaned up).
        var flashskinkDir = Path.Combine(_skinkRoot, ".flashskink");
        Directory.CreateDirectory(flashskinkDir);
        await File.WriteAllBytesAsync(
            Path.Combine(flashskinkDir, "brain.db"),
            RandomNumberGenerator.GetBytes(64));

        var result = await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(flashskinkDir, "vault.bin")));
    }

    // ── WriteFileAsync / ReadFileAsync ────────────────────────────────────────

    [Fact]
    public async Task WriteFile_ThenReadFile_ProducesOriginalContent()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var payload = RandomNumberGenerator.GetBytes(512);
        var writeResult = await volume.WriteFileAsync(new MemoryStream(payload), "a.bin");
        Assert.True(writeResult.Success);

        var dest = new MemoryStream();
        var readResult = await volume.ReadFileAsync("a.bin", dest);
        Assert.True(readResult.Success);
        Assert.Equal(payload, dest.ToArray());
    }

    [Fact]
    public async Task WriteFile_ThenReadFile_LargeFile_ProducesOriginalContent()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var payload = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
        var writeResult = await volume.WriteFileAsync(new MemoryStream(payload), "big.bin");
        Assert.True(writeResult.Success);

        var dest = new MemoryStream();
        var readResult = await volume.ReadFileAsync("big.bin", dest);
        Assert.True(readResult.Success);
        Assert.Equal(payload, dest.ToArray());
    }

    [Fact]
    public async Task WriteFile_SamePath_SameContent_ReturnsUnchanged()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var payload = Encoding.UTF8.GetBytes("same content");
        await volume.WriteFileAsync(new MemoryStream(payload), "f.txt");
        var second = await volume.WriteFileAsync(new MemoryStream(payload), "f.txt");

        Assert.True(second.Success);
        Assert.Equal(WriteStatus.Unchanged, second.Value!.Status);
    }

    [Fact]
    public async Task WriteFile_SamePath_DifferentContent_ReturnsPathConflict()
    {
        // WritePipeline uses INSERT (not UPSERT) in the brain commit — overwriting with
        // different content hits the UNIQUE index and returns PathConflict. Callers must
        // delete the existing file before writing new content at the same path.
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var first = Encoding.UTF8.GetBytes("first content");
        var second = Encoding.UTF8.GetBytes("different content");
        await volume.WriteFileAsync(new MemoryStream(first), "g.txt");
        var overwrite = await volume.WriteFileAsync(new MemoryStream(second), "g.txt");

        Assert.Equal(ErrorCode.PathConflict, overwrite.Error!.Code);
    }

    [Fact]
    public async Task ReadFile_NonExistentPath_ReturnsFileNotFound()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var result = await volume.ReadFileAsync("no-such-file.txt", new MemoryStream());
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.FileNotFound, result.Error!.Code);
    }

    // ── DeleteFileAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteFile_ExistingFile_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var payload = RandomNumberGenerator.GetBytes(64);
        await volume.WriteFileAsync(new MemoryStream(payload), "del.bin");

        var delResult = await volume.DeleteFileAsync("del.bin");
        Assert.True(delResult.Success);

        var readResult = await volume.ReadFileAsync("del.bin", new MemoryStream());
        Assert.Equal(ErrorCode.FileNotFound, readResult.Error!.Code);
    }

    [Fact]
    public async Task DeleteFile_NonExistentPath_ReturnsFailResult()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var result = await volume.DeleteFileAsync("ghost.txt");
        Assert.False(result.Success);
    }

    // ── Folder operations ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateFolder_AtRoot_ReturnsId()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var result = await volume.CreateFolderAsync("docs", null);
        Assert.True(result.Success);
        Assert.NotEmpty(result.Value!);
    }

    [Fact]
    public async Task CreateFolder_UnderExistingParent_NestsCorrectly()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var docsResult = await volume.CreateFolderAsync("docs", null);
        var refResult = await volume.CreateFolderAsync("ref", docsResult.Value!);
        Assert.True(refResult.Success);

        var children = await volume.ListChildrenAsync(docsResult.Value!);
        Assert.True(children.Success);
        Assert.Single(children.Value!);
        Assert.Equal("docs/ref", children.Value![0].VirtualPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("sub/path")]
    public async Task CreateFolder_InvalidName_ReturnsInvalidArgument(string name)
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var result = await volume.CreateFolderAsync(name, null);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task CreateFolder_DuplicateNameUnderSameParent_ReturnsPathConflict()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        await volume.CreateFolderAsync("dup", null);
        var second = await volume.CreateFolderAsync("dup", null);
        Assert.Equal(ErrorCode.PathConflict, second.Error!.Code);
    }

    [Fact]
    public async Task DeleteFolder_Empty_WithoutConfirmation_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var folder = await volume.CreateFolderAsync("empty-folder", null);
        var result = await volume.DeleteFolderAsync(folder.Value!, confirmed: false);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task DeleteFolder_NonEmpty_WithoutConfirmation_ReturnsConfirmationRequired()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var folder = await volume.CreateFolderAsync("nonempty", null);
        await volume.WriteFileAsync(new MemoryStream([1, 2, 3]), "nonempty/child.txt");

        var result = await volume.DeleteFolderAsync(folder.Value!, confirmed: false);
        Assert.Equal(ErrorCode.ConfirmationRequired, result.Error!.Code);
        Assert.True(result.Error.Metadata!.ContainsKey("ChildCount"));
        Assert.True(int.TryParse(result.Error.Metadata["ChildCount"], out var count) && count > 0);
    }

    [Fact]
    public async Task DeleteFolder_NonEmpty_WithConfirmation_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var folder = await volume.CreateFolderAsync("cascade", null);
        await volume.WriteFileAsync(new MemoryStream([1, 2, 3]), "cascade/child.txt");

        var result = await volume.DeleteFolderAsync(folder.Value!, confirmed: true);
        Assert.True(result.Success);

        var read = await volume.ReadFileAsync("cascade/child.txt", new MemoryStream());
        Assert.Equal(ErrorCode.FileNotFound, read.Error!.Code);
    }

    [Fact]
    public async Task RenameFolder_ExistingFolder_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var folder = await volume.CreateFolderAsync("old-name", null);
        var rename = await volume.RenameFolderAsync(folder.Value!, "new-name");
        Assert.True(rename.Success);

        var children = await volume.ListChildrenAsync(null);
        Assert.Contains(children.Value!, f => f.Name == "new-name");
        Assert.DoesNotContain(children.Value!, f => f.Name == "old-name");
    }

    [Fact]
    public async Task MoveAsync_FileToFolder_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        await volume.WriteFileAsync(new MemoryStream([1, 2, 3]), "src.txt");
        var dest = await volume.CreateFolderAsync("dest-folder", null);

        var fileId = (await volume.ListChildrenAsync(null)).Value!
            .First(f => !f.IsFolder).FileId;
        var result = await volume.MoveAsync(fileId, dest.Value!);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task MoveAsync_FolderUnderItsOwnDescendant_ReturnsCyclicMoveDetected()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var a = await volume.CreateFolderAsync("A", null);
        var b = await volume.CreateFolderAsync("B", a.Value!);

        var result = await volume.MoveAsync(a.Value!, b.Value!);
        Assert.Equal(ErrorCode.CyclicMoveDetected, result.Error!.Code);
    }

    [Fact]
    public async Task MoveAsync_ToRoot_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var parent = await volume.CreateFolderAsync("parent", null);
        var child = await volume.CreateFolderAsync("child", parent.Value!);

        var result = await volume.MoveAsync(child.Value!, newParentId: null);
        Assert.True(result.Success);

        var rootChildren = await volume.ListChildrenAsync(null);
        Assert.Contains(rootChildren.Value!, f => f.FileId == child.Value);
    }

    // ── ListChildrenAsync / ListFilesAsync ────────────────────────────────────

    [Fact]
    public async Task ListChildren_PopulatedFolder_ReturnsExpectedItems()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        await volume.CreateFolderAsync("f1", null);
        await volume.CreateFolderAsync("f2", null);
        await volume.WriteFileAsync(new MemoryStream([1]), "a.txt");
        await volume.WriteFileAsync(new MemoryStream([2]), "b.txt");

        var result = await volume.ListChildrenAsync(null);
        Assert.True(result.Success);
        Assert.Equal(4, result.Value!.Count);
        Assert.True(result.Value[0].IsFolder);
        Assert.True(result.Value[1].IsFolder);
    }

    [Fact]
    public async Task ListFiles_PrefixMatch_IncludesAllNestedFiles()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        await volume.WriteFileAsync(new MemoryStream([1]), "a.txt");
        await volume.WriteFileAsync(new MemoryStream([2]), "docs/b.txt");
        await volume.WriteFileAsync(new MemoryStream([3]), "docs/ref/c.txt");

        var result = await volume.ListFilesAsync("docs");
        Assert.True(result.Success);
        Assert.Equal(2, result.Value!.Count(f => !f.IsFolder));
        Assert.DoesNotContain(result.Value!, f => f.Name == "a.txt");
    }

    [Fact]
    public async Task ListFiles_EmptyPrefix_IncludesAll()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        await volume.WriteFileAsync(new MemoryStream([1]), "a.txt");
        await volume.WriteFileAsync(new MemoryStream([2]), "docs/b.txt");
        await volume.WriteFileAsync(new MemoryStream([3]), "docs/ref/c.txt");

        var result = await volume.ListFilesAsync(string.Empty);
        Assert.True(result.Success);
        Assert.Equal(3, result.Value!.Count(f => !f.IsFolder));
    }

    // ── ChangePasswordAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_ThenOpenWithNewPassword_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;
        var changeResult = await volume.ChangePasswordAsync(Password, "new-password");
        Assert.True(changeResult.Success);
        await volume.DisposeAsync();

        var open = await FlashSkinkVolume.OpenAsync(_skinkRoot, "new-password", DefaultOptions);
        Assert.True(open.Success);
        await open.Value!.DisposeAsync();
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_ReturnsFailResult()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var result = await volume.ChangePasswordAsync("wrong-current", "new");
        Assert.False(result.Success);
    }

    // ── RestoreFromGracePeriodAsync ───────────────────────────────────────────

    [Fact]
    public async Task RestoreFromGracePeriod_ValidBlobId_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var payload = RandomNumberGenerator.GetBytes(64);
        var writeResult = await volume.WriteFileAsync(new MemoryStream(payload), "restore-me.bin");
        await volume.DeleteFileAsync("restore-me.bin");

        var restore = await volume.RestoreFromGracePeriodAsync(
            writeResult.Value!.BlobId, "restore-me.bin");
        Assert.True(restore.Success);

        var dest = new MemoryStream();
        var read = await volume.ReadFileAsync("restore-me.bin", dest);
        Assert.True(read.Success);
        Assert.Equal(payload, dest.ToArray());
    }

    // ── Compression branch round-trips ────────────────────────────────────────

    [Fact]
    public async Task WriteThenRead_HighlyCompressible100KB_UsesLz4Branch()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var payload = new byte[100 * 1024];
        Array.Fill(payload, (byte)'A');
        var receipt = (await volume.WriteFileAsync(new MemoryStream(payload), "lz4.bin")).Value!;

        var dest = new MemoryStream();
        var read = await volume.ReadFileAsync("lz4.bin", dest);
        Assert.True(read.Success);
        Assert.Equal(payload, dest.ToArray());

        await volume.DisposeAsync();
        var compression = await ReadBlobCompressionAsync(receipt.BlobId);
        Assert.Equal("LZ4", compression);
    }

    [Fact]
    public async Task WriteThenRead_HighlyCompressible1MB_UsesZstdBranch()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var payload = new byte[1024 * 1024];
        Array.Fill(payload, (byte)'B');
        var receipt = (await volume.WriteFileAsync(new MemoryStream(payload), "zstd.bin")).Value!;

        var dest = new MemoryStream();
        var read = await volume.ReadFileAsync("zstd.bin", dest);
        Assert.True(read.Success);
        Assert.Equal(payload, dest.ToArray());

        await volume.DisposeAsync();
        var compression = await ReadBlobCompressionAsync(receipt.BlobId);
        Assert.Equal("ZSTD", compression);
    }

    [Fact]
    public async Task WriteThenRead_RandomBytes1MB_UsesNoCompressionBranch()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
        var receipt = (await volume.WriteFileAsync(new MemoryStream(payload), "rand.bin")).Value!;

        var dest = new MemoryStream();
        var read = await volume.ReadFileAsync("rand.bin", dest);
        Assert.True(read.Success);
        Assert.Equal(payload, dest.ToArray());

        await volume.DisposeAsync();
        var compression = await ReadBlobCompressionAsync(receipt.BlobId);
        Assert.Null(compression);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteFileAsync_CancelledMidFlight_ReturnsCancelled()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var cts = new CancellationTokenSource();
        var data = new byte[16 * 1024 * 1024];
        Array.Fill(data, (byte)0xAB);

        var cancelStream = new CancelAfterNBytesStream(data, cancelAfterBytes: 64 * 1024);
        cancelStream.SetCts(cts);

        var result = await volume.WriteFileAsync(cancelStream, "cancel-write.bin", cts.Token);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task ReadFileAsync_CancelledMidFlight_ReturnsCancelled()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var payload = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        await volume.WriteFileAsync(new MemoryStream(payload), "cancel-read.bin");

        // ReadPipeline buffers the entire plaintext before writing to dest, so
        // cancelling at destination-write time is too late. Pre-cancel the token
        // so ThrowIfCancellationRequested fires at the first pipeline await site.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await volume.ReadFileAsync("cancel-read.bin", new MemoryStream(), cts.Token);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentReads_SerializeCorrectly()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var payload = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
        await volume.WriteFileAsync(new MemoryStream(payload), "shared.bin");

        var tasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            var dest = new MemoryStream();
            var result = await volume.ReadFileAsync("shared.bin", dest);
            Assert.True(result.Success);
            Assert.Equal(payload, dest.ToArray());
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ConcurrentWrites_SerializeCorrectly()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!.Volume;

        var tasks = Enumerable.Range(0, 5).Select(async i =>
        {
            var payload = RandomNumberGenerator.GetBytes(128);
            var result = await volume.WriteFileAsync(new MemoryStream(payload), $"concurrent-{i}.bin");
            Assert.True(result.Success);
        });

        await Task.WhenAll(tasks);
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_Idempotent_DoesNotThrow()
    {
        var volume = (await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions)).Value!.Volume;
        await volume.DisposeAsync();
        await volume.DisposeAsync();
    }

    [Fact]
    public async Task PublicMethod_AfterDispose_ThrowsObjectDisposedException()
    {
        var volume = (await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions)).Value!.Volume;
        await volume.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => volume.WriteFileAsync(new MemoryStream([1]), "after-dispose.bin"));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string?> ReadBrainSettingAsync(string key)
    {
        await using var connection = await OpenBrainConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT Value FROM Settings WHERE Key = @Key", new { Key = key });
    }

    private async Task<string?> ReadBlobCompressionAsync(string blobId)
    {
        await using var connection = await OpenBrainConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT Compression FROM Blobs WHERE BlobID = @BlobId", new { BlobId = blobId });
    }

    private async Task<SqliteConnection> OpenBrainConnectionAsync()
    {
        var vaultPath = Path.Combine(_skinkRoot, ".flashskink", "vault.bin");
        var brainPath = Path.Combine(_skinkRoot, ".flashskink", "brain.db");
        var kdf = new KeyDerivationService();
        var keyVault = new KeyVault(kdf, new MnemonicService());
        var brainFactory = new BrainConnectionFactory(kdf, NullLogger<BrainConnectionFactory>.Instance);

        var passwordBytes = Encoding.UTF8.GetBytes(Password);
        var unlockResult = await keyVault.UnlockAsync(
            vaultPath, new ReadOnlyMemory<byte>(passwordBytes), CancellationToken.None);
        CryptographicOperations.ZeroMemory(passwordBytes);

        if (!unlockResult.Success)
        {
            throw new InvalidOperationException($"Test brain unlock failed: {unlockResult.Error!.Message}");
        }

        var dek = unlockResult.Value!;
        var brainResult = await brainFactory.CreateAsync(brainPath, dek, CancellationToken.None);
        CryptographicOperations.ZeroMemory(dek);

        if (!brainResult.Success)
        {
            throw new InvalidOperationException($"Test brain open failed: {brainResult.Error!.Message}");
        }

        return brainResult.Value!;
    }
}
