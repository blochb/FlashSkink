using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Metadata;

public class FileRepositoryTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly WalRepository _wal;
    private readonly FileRepository _sut;

    public FileRepositoryTests()
    {
        _connection = BrainTestHelper.CreateInMemoryConnection();
        _wal = new WalRepository(_connection, NullLogger<WalRepository>.Instance);
        _sut = new FileRepository(_connection, _wal, NullLogger<FileRepository>.Instance);
    }

    public async Task InitializeAsync() => await BrainTestHelper.ApplySchemaAsync(_connection);

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static VolumeFile MakeFile(
        string fileId,
        string name,
        string? parentId = null,
        string? virtualPath = null,
        string? blobId = null) => new()
        {
            FileId = fileId,
            ParentId = parentId,
            IsFolder = false,
            IsSymlink = false,
            Name = name,
            VirtualPath = virtualPath ?? name,
            SizeBytes = 100,
            CreatedUtc = DateTime.UtcNow,
            ModifiedUtc = DateTime.UtcNow,
            AddedUtc = DateTime.UtcNow,
            BlobId = blobId,
        };

    private static VolumeFile MakeFolder(
        string fileId,
        string name,
        string? parentId = null,
        string? virtualPath = null) => new()
        {
            FileId = fileId,
            ParentId = parentId,
            IsFolder = true,
            IsSymlink = false,
            Name = name,
            VirtualPath = virtualPath ?? name,
            SizeBytes = 0,
            CreatedUtc = DateTime.UtcNow,
            ModifiedUtc = DateTime.UtcNow,
            AddedUtc = DateTime.UtcNow,
        };

    // ── InsertAsync / GetByIdAsync ────────────────────────────────────────────

    [Fact]
    public async Task InsertAsync_File_RoundTripsViaGetById()
    {
        var file = MakeFile("f1", "document.pdf");

        await _sut.InsertAsync(file, CancellationToken.None);
        var result = await _sut.GetByIdAsync("f1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal("f1", result.Value!.FileId);
        Assert.Equal("document.pdf", result.Value.Name);
        Assert.False(result.Value.IsFolder);
        Assert.False(result.Value.IsSymlink);
        Assert.Null(result.Value.ParentId);
    }

    [Fact]
    public async Task InsertAsync_Folder_IsFound_AsFolder()
    {
        var folder = MakeFolder("fd1", "photos");

        await _sut.InsertAsync(folder, CancellationToken.None);
        var result = await _sut.GetByIdAsync("fd1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Value!.IsFolder);
    }

    [Fact]
    public async Task InsertAsync_DuplicateName_SameParent_ReturnsPathConflict()
    {
        await _sut.InsertAsync(MakeFile("f1", "dup.txt"), CancellationToken.None);

        var result = await _sut.InsertAsync(MakeFile("f2", "dup.txt"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.PathConflict, result.Error!.Code);
    }

    [Fact]
    public async Task InsertAsync_DuplicateName_DifferentParent_Succeeds()
    {
        await _sut.InsertAsync(MakeFolder("parent1", "p1"), CancellationToken.None);
        await _sut.InsertAsync(MakeFolder("parent2", "p2"), CancellationToken.None);

        var r1 = await _sut.InsertAsync(
            MakeFile("f1", "readme.md", "parent1", "p1/readme.md"), CancellationToken.None);
        var r2 = await _sut.InsertAsync(
            MakeFile("f2", "readme.md", "parent2", "p2/readme.md"), CancellationToken.None);

        Assert.True(r1.Success);
        Assert.True(r2.Success);
    }

    // ── ListChildrenAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ListChildrenAsync_RootWithTwoItems_ReturnsBothFolderFirst()
    {
        await _sut.InsertAsync(MakeFile("f1", "a-file.txt"), CancellationToken.None);
        await _sut.InsertAsync(MakeFolder("fd1", "b-folder"), CancellationToken.None);

        var result = await _sut.ListChildrenAsync(null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Value!.Count);
        Assert.True(result.Value[0].IsFolder);   // folder sorts first
        Assert.False(result.Value[1].IsFolder);
    }

    [Fact]
    public async Task ListChildrenAsync_NullParent_ReturnsOnlyRootItems()
    {
        await _sut.InsertAsync(MakeFolder("fd1", "topfolder"), CancellationToken.None);
        await _sut.InsertAsync(
            MakeFile("nested", "nested.txt", "fd1", "topfolder/nested.txt"), CancellationToken.None);

        var result = await _sut.ListChildrenAsync(null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Value!);
        Assert.Equal("fd1", result.Value![0].FileId);
    }

    // ── EnsureFolderPathAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task EnsureFolderPathAsync_NewPath_CreatesAllSegments()
    {
        var result = await _sut.EnsureFolderPathAsync("docs/reports", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);

        // "docs" should exist at root.
        var rootChildren = await _sut.ListChildrenAsync(null, CancellationToken.None);
        var docs = Assert.Single(rootChildren.Value!);
        Assert.Equal("docs", docs.Name);

        // "reports" should be under "docs", and its FileId must equal the return value.
        var docsChildren = await _sut.ListChildrenAsync(docs.FileId, CancellationToken.None);
        var reports = Assert.Single(docsChildren.Value!);
        Assert.Equal("reports", reports.Name);
        Assert.Equal(result.Value, reports.FileId);
    }

    [Fact]
    public async Task EnsureFolderPathAsync_ExistingPath_IsIdempotent()
    {
        var first = await _sut.EnsureFolderPathAsync("docs", CancellationToken.None);
        var second = await _sut.EnsureFolderPathAsync("docs", CancellationToken.None);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.Value, second.Value); // same leaf FileId returned

        var children = await _sut.ListChildrenAsync(null, CancellationToken.None);
        Assert.Single(children.Value!); // exactly one row created, not two
    }

    [Fact]
    public async Task EnsureFolderPathAsync_FileAtSegment_ReturnsPathConflict()
    {
        // "docs" is a file, not a folder.
        await _sut.InsertAsync(MakeFile("docs-file", "docs"), CancellationToken.None);

        var result = await _sut.EnsureFolderPathAsync("docs/reports", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.PathConflict, result.Error!.Code);
    }

    // ── CountChildrenAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task CountChildrenAsync_EmptyFolder_ReturnsZero()
    {
        await _sut.InsertAsync(MakeFolder("fd1", "emptyfolder"), CancellationToken.None);

        var result = await _sut.CountChildrenAsync("fd1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public async Task CountChildrenAsync_TwoChildren_ReturnsTwo()
    {
        await _sut.InsertAsync(MakeFolder("fd1", "parent"), CancellationToken.None);
        await _sut.InsertAsync(MakeFile("f1", "child1.txt", "fd1", "parent/child1.txt"), CancellationToken.None);
        await _sut.InsertAsync(MakeFile("f2", "child2.txt", "fd1", "parent/child2.txt"), CancellationToken.None);

        var result = await _sut.CountChildrenAsync("fd1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Value);
    }

    // ── GetDescendantsAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetDescendantsAsync_ThreeLevels_ReturnsAllDescendants()
    {
        // L1 (root folder) → L2 (child folder) → L3 (leaf file)
        await _sut.InsertAsync(MakeFolder("fd1", "L1"), CancellationToken.None);
        await _sut.InsertAsync(MakeFolder("fd2", "L2", "fd1", "L1/L2"), CancellationToken.None);
        await _sut.InsertAsync(MakeFile("f3", "leaf.txt", "fd2", "L1/L2/leaf.txt"), CancellationToken.None);

        var result = await _sut.GetDescendantsAsync("fd1", CancellationToken.None);

        Assert.True(result.Success);
        // CTE includes fd1 itself + fd2 + f3 = 3 rows.
        Assert.Equal(3, result.Value!.Count);
    }

    // ── DeleteFileAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteFileAsync_RemovesFilesRow_SoftDeletesBlob()
    {
        var blobId = Guid.NewGuid().ToString();
        BrainTestHelper.InsertTestBlob(_connection, blobId);
        await _sut.InsertAsync(MakeFile("f1", "report.pdf", blobId: blobId), CancellationToken.None);

        await _sut.DeleteFileAsync("f1", CancellationToken.None);

        var fileRow = await _connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT FileID FROM Files WHERE FileID = 'f1'");
        Assert.Null(fileRow); // Files row is gone

        var softDeleted = await _connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT SoftDeletedUtc FROM Blobs WHERE BlobID = @BlobId", new { BlobId = blobId });
        Assert.NotNull(softDeleted); // blob is soft-deleted
    }

    // ── DeleteFolderCascadeAsync ──────────────────────────────────────────────

    [Fact]
    public async Task DeleteFolderCascadeAsync_WithChildren_RequiresConfirmation()
    {
        await _sut.InsertAsync(MakeFolder("fd1", "folder1"), CancellationToken.None);
        await _sut.InsertAsync(MakeFile("f1", "child.txt", "fd1", "folder1/child.txt"), CancellationToken.None);

        var result = await _sut.DeleteFolderCascadeAsync("fd1", confirmed: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ConfirmationRequired, result.Error!.Code);
        Assert.NotNull(result.Error.Metadata);
        Assert.True(result.Error.Metadata!.ContainsKey("ChildCount"));
        Assert.Equal("1", result.Error.Metadata["ChildCount"]);
    }

    [Fact]
    public async Task DeleteFolderCascadeAsync_Confirmed_DeletesAll()
    {
        await _sut.InsertAsync(MakeFolder("fd1", "folder1"), CancellationToken.None);
        await _sut.InsertAsync(MakeFile("f1", "child.txt", "fd1", "folder1/child.txt"), CancellationToken.None);

        var result = await _sut.DeleteFolderCascadeAsync("fd1", confirmed: true, CancellationToken.None);

        Assert.True(result.Success);
        var fileCount = await _connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM Files");
        Assert.Equal(0, fileCount);                                // both rows deleted
        var logCount = await _connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM DeleteLog");
        Assert.Equal(2, logCount);                                 // one entry per deleted row
    }

    // ── RenameFolderAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task RenameFolderAsync_UpdatesVirtualPathForDescendants()
    {
        await _sut.InsertAsync(MakeFolder("fd1", "old"), CancellationToken.None);
        await _sut.InsertAsync(MakeFile("f1", "child.txt", "fd1", "old/child.txt"), CancellationToken.None);

        var result = await _sut.RenameFolderAsync("fd1", "new", CancellationToken.None);

        Assert.True(result.Success);
        var child = await _sut.GetByIdAsync("f1", CancellationToken.None);
        Assert.Equal("new/child.txt", child.Value!.VirtualPath);
    }

    [Fact]
    public async Task RenameFolderAsync_NameConflict_ReturnsPathConflict()
    {
        await _sut.InsertAsync(MakeFolder("fd1", "folderA"), CancellationToken.None);
        await _sut.InsertAsync(MakeFolder("fd2", "folderB"), CancellationToken.None);

        // Rename "folderA" to "folderB" — conflicts with the existing sibling.
        var result = await _sut.RenameFolderAsync("fd1", "folderB", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.PathConflict, result.Error!.Code);
    }

    // ── MoveAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveAsync_FileToNewParent_UpdatesParentAndPath()
    {
        await _sut.InsertAsync(MakeFolder("fd1", "target"), CancellationToken.None);
        await _sut.InsertAsync(MakeFile("f1", "file.txt"), CancellationToken.None);

        var result = await _sut.MoveAsync("f1", "fd1", CancellationToken.None);

        Assert.True(result.Success);
        var moved = await _sut.GetByIdAsync("f1", CancellationToken.None);
        Assert.Equal("fd1", moved.Value!.ParentId);
        Assert.Equal("target/file.txt", moved.Value.VirtualPath);
    }

    [Fact]
    public async Task MoveAsync_FolderToDescendant_ReturnsCyclicMoveDetected()
    {
        await _sut.InsertAsync(MakeFolder("fd1", "parent"), CancellationToken.None);
        await _sut.InsertAsync(MakeFolder("fd2", "child", "fd1", "parent/child"), CancellationToken.None);

        // Trying to move "parent" into one of its own descendants must be detected.
        var result = await _sut.MoveAsync("fd1", "fd2", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.CyclicMoveDetected, result.Error!.Code);
    }

    // ── RestoreFromGracePeriodAsync ───────────────────────────────────────────

    [Fact]
    public async Task RestoreFromGracePeriodAsync_ClearsSoftDeleteFields()
    {
        var blobId = Guid.NewGuid().ToString();
        BrainTestHelper.InsertTestBlob(_connection, blobId);

        // Soft-delete the blob (simulates grace-period state).
        await _connection.ExecuteAsync(
            """
            UPDATE Blobs
            SET SoftDeletedUtc = @Now, PurgeAfterUtc = @Now
            WHERE BlobID = @BlobId
            """,
            new { Now = DateTime.UtcNow.AddDays(-1).ToString("O"), BlobId = blobId });

        var result = await _sut.RestoreFromGracePeriodAsync(blobId, "restored_file.txt", CancellationToken.None);

        Assert.True(result.Success);
        var softDeleted = await _connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT SoftDeletedUtc FROM Blobs WHERE BlobID = @BlobId", new { BlobId = blobId });
        Assert.Null(softDeleted);
        var purgeAfter = await _connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT PurgeAfterUtc FROM Blobs WHERE BlobID = @BlobId", new { BlobId = blobId });
        Assert.Null(purgeAfter);
    }
}
