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
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        Assert.True(File.Exists(Path.Combine(_skinkRoot, ".flashskink", "vault.bin")));
        Assert.True(File.Exists(Path.Combine(_skinkRoot, ".flashskink", "brain.db")));
        Assert.NotNull(volume);
    }

    [Fact]
    public async Task CreateAsync_NewSkinkRoot_GeneratesRecoveryPhrase()
    {
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue();
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
            _skinkRoot, Password, DefaultOptions)).AssertValue();
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
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;
        await volume.DisposeAsync();

        Assert.NotNull(await ReadBrainSettingAsync("GracePeriodDays"));
        Assert.NotNull(await ReadBrainSettingAsync("AuditIntervalHours"));
        Assert.NotNull(await ReadBrainSettingAsync("VolumeCreatedUtc"));
        Assert.NotNull(await ReadBrainSettingAsync("VolumeID"));
        Assert.NotNull(await ReadBrainSettingAsync("AppVersionCreatedWith"));
        Assert.NotNull(await ReadBrainSettingAsync("AppVersionLastOpened"));
        Assert.NotNull(await ReadBrainSettingAsync("AppVersionLastOpenedUtc"));
    }

    [Fact]
    public async Task OpenAsync_ExistingVolume_ReturnsOpenVolume()
    {
        var create = await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions);
        await create.AssertValue().Volume.DisposeAsync();

        var open = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(open.Success);
        await open.AssertValue().DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_WrongPassword_ReturnsFailResult()
    {
        var create = await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions);
        await create.AssertValue().Volume.DisposeAsync();

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
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

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
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

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
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var payload = Encoding.UTF8.GetBytes("same content");
        await volume.WriteFileAsync(new MemoryStream(payload), "f.txt");
        var second = await volume.WriteFileAsync(new MemoryStream(payload), "f.txt");

        Assert.True(second.Success);
        Assert.Equal(WriteStatus.Unchanged, second.AssertValue().Status);
    }

    [Fact]
    public async Task WriteFile_SamePath_DifferentContent_ReturnsPathConflict()
    {
        // WritePipeline uses INSERT (not UPSERT) in the brain commit — overwriting with
        // different content hits the UNIQUE index and returns PathConflict. Callers must
        // delete the existing file before writing new content at the same path.
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var first = Encoding.UTF8.GetBytes("first content");
        var second = Encoding.UTF8.GetBytes("different content");
        await volume.WriteFileAsync(new MemoryStream(first), "g.txt");
        var overwrite = await volume.WriteFileAsync(new MemoryStream(second), "g.txt");

        Assert.Equal(ErrorCode.PathConflict, overwrite.AssertError().Code);
    }

    [Fact]
    public async Task ReadFile_NonExistentPath_ReturnsFileNotFound()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var result = await volume.ReadFileAsync("no-such-file.txt", new MemoryStream());
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.FileNotFound, result.AssertError().Code);
    }

    // ── DeleteFileAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteFile_ExistingFile_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var payload = RandomNumberGenerator.GetBytes(64);
        await volume.WriteFileAsync(new MemoryStream(payload), "del.bin");

        var delResult = await volume.DeleteFileAsync("del.bin");
        Assert.True(delResult.Success);

        var readResult = await volume.ReadFileAsync("del.bin", new MemoryStream());
        Assert.Equal(ErrorCode.FileNotFound, readResult.AssertError().Code);
    }

    [Fact]
    public async Task DeleteFile_NonExistentPath_ReturnsFailResult()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var result = await volume.DeleteFileAsync("ghost.txt");
        Assert.False(result.Success);
    }

    // ── Folder operations ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateFolder_AtRoot_ReturnsId()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var result = await volume.CreateFolderAsync("docs", null);
        Assert.True(result.Success);
        Assert.NotEmpty(result.AssertValue());
    }

    [Fact]
    public async Task CreateFolder_UnderExistingParent_NestsCorrectly()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var docsResult = await volume.CreateFolderAsync("docs", null);
        var refResult = await volume.CreateFolderAsync("ref", docsResult.AssertValue());
        Assert.True(refResult.Success);

        var children = await volume.ListChildrenAsync(docsResult.AssertValue());
        Assert.True(children.Success);
        Assert.Single(children.AssertValue());
        Assert.Equal("docs/ref", children.AssertValue()[0].VirtualPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("sub/path")]
    public async Task CreateFolder_InvalidName_ReturnsInvalidArgument(string name)
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var result = await volume.CreateFolderAsync(name, null);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }

    [Fact]
    public async Task CreateFolder_DuplicateNameUnderSameParent_ReturnsPathConflict()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        await volume.CreateFolderAsync("dup", null);
        var second = await volume.CreateFolderAsync("dup", null);
        Assert.Equal(ErrorCode.PathConflict, second.AssertError().Code);
    }

    [Fact]
    public async Task DeleteFolder_Empty_WithoutConfirmation_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var folder = await volume.CreateFolderAsync("empty-folder", null);
        var result = await volume.DeleteFolderAsync(folder.AssertValue(), confirmed: false);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task DeleteFolder_NonEmpty_WithoutConfirmation_ReturnsConfirmationRequired()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var folder = await volume.CreateFolderAsync("nonempty", null);
        await volume.WriteFileAsync(new MemoryStream([1, 2, 3]), "nonempty/child.txt");

        var result = await volume.DeleteFolderAsync(folder.AssertValue(), confirmed: false);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ConfirmationRequired, result.Error.Code);
        Assert.True(result.Error.Metadata!.ContainsKey("ChildCount"));
        Assert.True(int.TryParse(result.Error.Metadata["ChildCount"], out var count) && count > 0);
    }

    [Fact]
    public async Task DeleteFolder_NonEmpty_WithConfirmation_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var folder = await volume.CreateFolderAsync("cascade", null);
        await volume.WriteFileAsync(new MemoryStream([1, 2, 3]), "cascade/child.txt");

        var result = await volume.DeleteFolderAsync(folder.AssertValue(), confirmed: true);
        Assert.True(result.Success);

        var read = await volume.ReadFileAsync("cascade/child.txt", new MemoryStream());
        Assert.Equal(ErrorCode.FileNotFound, read.AssertError().Code);
    }

    [Fact]
    public async Task RenameFolder_ExistingFolder_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var folder = await volume.CreateFolderAsync("old-name", null);
        var rename = await volume.RenameFolderAsync(folder.AssertValue(), "new-name");
        Assert.True(rename.Success);

        var children = await volume.ListChildrenAsync(null);
        Assert.Contains(children.AssertValue(), f => f.Name == "new-name");
        Assert.DoesNotContain(children.AssertValue(), f => f.Name == "old-name");
    }

    [Fact]
    public async Task MoveAsync_FileToFolder_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        await volume.WriteFileAsync(new MemoryStream([1, 2, 3]), "src.txt");
        var dest = await volume.CreateFolderAsync("dest-folder", null);

        var fileId = (await volume.ListChildrenAsync(null)).AssertValue()
            .First(f => !f.IsFolder).FileId;
        var result = await volume.MoveAsync(fileId, dest.AssertValue());
        Assert.True(result.Success);
    }

    [Fact]
    public async Task MoveAsync_FolderUnderItsOwnDescendant_ReturnsCyclicMoveDetected()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var a = await volume.CreateFolderAsync("A", null);
        var b = await volume.CreateFolderAsync("B", a.AssertValue());

        var result = await volume.MoveAsync(a.AssertValue(), b.AssertValue());
        Assert.Equal(ErrorCode.CyclicMoveDetected, result.AssertError().Code);
    }

    [Fact]
    public async Task MoveAsync_ToRoot_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var parent = await volume.CreateFolderAsync("parent", null);
        var child = await volume.CreateFolderAsync("child", parent.AssertValue());

        var result = await volume.MoveAsync(child.AssertValue(), newParentId: null);
        Assert.True(result.Success);

        var rootChildren = await volume.ListChildrenAsync(null);
        Assert.Contains(rootChildren.AssertValue(), f => f.FileId == child.Value);
    }

    // ── ListChildrenAsync / ListFilesAsync ────────────────────────────────────

    [Fact]
    public async Task ListChildren_PopulatedFolder_ReturnsExpectedItems()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        await volume.CreateFolderAsync("f1", null);
        await volume.CreateFolderAsync("f2", null);
        await volume.WriteFileAsync(new MemoryStream([1]), "a.txt");
        await volume.WriteFileAsync(new MemoryStream([2]), "b.txt");

        var result = await volume.ListChildrenAsync(null);
        Assert.True(result.Success);
        Assert.Equal(4, result.AssertValue().Count);
        Assert.True(result.Value[0].IsFolder);
        Assert.True(result.Value[1].IsFolder);
    }

    [Fact]
    public async Task ListFiles_PrefixMatch_IncludesAllNestedFiles()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        await volume.WriteFileAsync(new MemoryStream([1]), "a.txt");
        await volume.WriteFileAsync(new MemoryStream([2]), "docs/b.txt");
        await volume.WriteFileAsync(new MemoryStream([3]), "docs/ref/c.txt");

        var result = await volume.ListFilesAsync("docs");
        Assert.True(result.Success);
        Assert.Equal(2, result.AssertValue().Count(f => !f.IsFolder));
        Assert.DoesNotContain(result.AssertValue(), f => f.Name == "a.txt");
    }

    [Fact]
    public async Task ListFiles_EmptyPrefix_IncludesAll()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        await volume.WriteFileAsync(new MemoryStream([1]), "a.txt");
        await volume.WriteFileAsync(new MemoryStream([2]), "docs/b.txt");
        await volume.WriteFileAsync(new MemoryStream([3]), "docs/ref/c.txt");

        var result = await volume.ListFilesAsync(string.Empty);
        Assert.True(result.Success);
        Assert.Equal(3, result.AssertValue().Count(f => !f.IsFolder));
    }

    // ── ChangePasswordAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_ThenOpenWithNewPassword_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;
        var changeResult = await volume.ChangePasswordAsync(Password, "new-password");
        Assert.True(changeResult.Success);
        await volume.DisposeAsync();

        var open = await FlashSkinkVolume.OpenAsync(_skinkRoot, "new-password", DefaultOptions);
        Assert.True(open.Success);
        await open.AssertValue().DisposeAsync();
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_ReturnsFailResult()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var result = await volume.ChangePasswordAsync("wrong-current", "new");
        Assert.False(result.Success);
    }

    // ── RestoreFromGracePeriodAsync ───────────────────────────────────────────

    [Fact]
    public async Task RestoreFromGracePeriod_ValidBlobId_Succeeds()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var payload = RandomNumberGenerator.GetBytes(64);
        var writeResult = await volume.WriteFileAsync(new MemoryStream(payload), "restore-me.bin");
        await volume.DeleteFileAsync("restore-me.bin");

        var restore = await volume.RestoreFromGracePeriodAsync(
            writeResult.AssertValue().BlobId, "restore-me.bin");
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
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var payload = new byte[100 * 1024];
        Array.Fill(payload, (byte)'A');
        var receipt = (await volume.WriteFileAsync(new MemoryStream(payload), "lz4.bin")).AssertValue();

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
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var payload = new byte[1024 * 1024];
        Array.Fill(payload, (byte)'B');
        var receipt = (await volume.WriteFileAsync(new MemoryStream(payload), "zstd.bin")).AssertValue();

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
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
        var receipt = (await volume.WriteFileAsync(new MemoryStream(payload), "rand.bin")).AssertValue();

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
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var cts = new CancellationTokenSource();
        var data = new byte[16 * 1024 * 1024];
        Array.Fill(data, (byte)0xAB);

        var cancelStream = new CancelAfterNBytesStream(data, cancelAfterBytes: 64 * 1024);
        cancelStream.SetCts(cts);

        var result = await volume.WriteFileAsync(cancelStream, "cancel-write.bin", cts.Token);
        Assert.Equal(ErrorCode.Cancelled, result.AssertError().Code);
    }

    [Fact]
    public async Task ReadFileAsync_CancelledMidFlight_ReturnsCancelled()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

        var payload = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        await volume.WriteFileAsync(new MemoryStream(payload), "cancel-read.bin");

        // ReadPipeline buffers the entire plaintext before writing to dest, so
        // cancelling at destination-write time is too late. Pre-cancel the token
        // so ThrowIfCancellationRequested fires at the first pipeline await site.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await volume.ReadFileAsync("cancel-read.bin", new MemoryStream(), cts.Token);
        Assert.Equal(ErrorCode.Cancelled, result.AssertError().Code);
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentReads_SerializeCorrectly()
    {
        await using var volume = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

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
            _skinkRoot, Password, DefaultOptions)).AssertValue().Volume;

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
        var volume = (await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions)).AssertValue().Volume;
        await volume.DisposeAsync();
        await volume.DisposeAsync();
    }

    [Fact]
    public async Task PublicMethod_AfterDispose_ThrowsObjectDisposedException()
    {
        var volume = (await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions)).AssertValue().Volume;
        await volume.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => volume.WriteFileAsync(new MemoryStream([1]), "after-dispose.bin"));
    }

    [Fact]
    public async Task WriteFileAsync_QueuedBehindGate_RacingDispose_SurfacesObjectDisposed()
    {
        // Race orchestration for the post-acquire re-verify guard. Setup:
        //   - Holder: WriteFileAsync against a stream that blocks at the first ReadAsync,
        //             holding the volume's _gate inside WritePipeline.ExecuteAsync.
        //   - Waiter: a second WriteFileAsync that passes ThrowIfDisposed (so _disposed
        //             is still 0) and queues on _gate.WaitAsync behind the holder.
        //   - Disposer: DisposeAsync — sets _disposed = 1 then also queues on _gate.
        // When the holder releases, the gate has two waiters; SemaphoreSlim does not
        // guarantee FIFO wake order. With the re-verify guard, BOTH orderings produce
        // ObjectDisposedException at the waiter (either the guard trips on waiter-wakes-first,
        // or the disposer tears down _context and then the waiter wakes and the guard trips).
        // Without the guard, the disposer-wakes-first ordering would let the waiter touch
        // a destroyed _context and crash with NullReferenceException.
        var volume = (await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions)).AssertValue().Volume;
        bool ownsVolume = true;
        try
        {
            var firstReadReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holderRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holderStream = new BlockingFirstReadStream(firstReadReached, holderRelease.Task);

            var holderTask = volume.WriteFileAsync(holderStream, "holder.bin");
            await firstReadReached.Task; // ReadAsync entered — _gate held

            // Queue the waiter — it passes ThrowIfDisposed (still 0) and blocks on _gate.
            var waiterTask = volume.WriteFileAsync(new MemoryStream([1, 2, 3]), "waiter.bin");
            await Task.Delay(50);

            // Start dispose — flips _disposed = 1 and also blocks on _gate.
            var disposeTask = volume.DisposeAsync().AsTask();
            ownsVolume = false;
            await Task.Delay(50);

            // Release the holder. Gate is now contested between waiter and disposer.
            holderRelease.SetResult();

            // The waiter's contract is ObjectDisposedException, period. The fix ensures
            // this regardless of which thread SemaphoreSlim wakes first.
            var waiterException = await Record.ExceptionAsync(() => waiterTask);
            Assert.IsType<ObjectDisposedException>(waiterException);

            await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));
            // Holder outcome is not asserted — depending on the race it may have succeeded
            // (its ReadAsync returned EOF, pipeline finished) or failed (cancellation observed
            // mid-pipeline). Either is consistent with the contract.
            _ = await holderTask;
        }
        finally
        {
            if (ownsVolume)
            {
                await volume.DisposeAsync();
            }
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Test stream that blocks the first <see cref="ReadAsync(byte[], int, int, CancellationToken)"/>
    /// call on a signal, then returns EOF (0 bytes) on every subsequent read. Used to hold the
    /// volume's serialisation gate while a concurrent caller queues behind it.
    /// </summary>
    private sealed class BlockingFirstReadStream : Stream
    {
        private readonly TaskCompletionSource _firstReadReached;
        private readonly Task _release;
        private int _firstReadFired;

        internal BlockingFirstReadStream(TaskCompletionSource firstReadReached, Task release)
        {
            _firstReadReached = firstReadReached;
            _release = release;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _firstReadFired, 1) == 0)
            {
                _firstReadReached.TrySetResult();
                await _release.WaitAsync(ct).ConfigureAwait(false);
            }
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _firstReadFired, 1) == 0)
            {
                _firstReadReached.TrySetResult();
                await _release.WaitAsync(ct).ConfigureAwait(false);
            }
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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
            throw new InvalidOperationException($"Test brain unlock failed: {unlockResult.AssertError().Message}");
        }

        var dek = unlockResult.AssertValue();
        var brainResult = await brainFactory.CreateAsync(brainPath, dek, CancellationToken.None);
        CryptographicOperations.ZeroMemory(dek);

        if (!brainResult.Success)
        {
            throw new InvalidOperationException($"Test brain open failed: {brainResult.AssertError().Message}");
        }

        return brainResult.AssertValue();
    }
}
