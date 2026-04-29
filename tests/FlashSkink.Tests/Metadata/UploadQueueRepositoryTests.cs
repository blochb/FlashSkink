using Dapper;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Metadata;

public class UploadQueueRepositoryTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly UploadQueueRepository _sut;

    // Constant IDs used as FK targets in every test.
    private const string FileId = "file-001";
    private const string ProviderId = "provider-001";

    public UploadQueueRepositoryTests()
    {
        _connection = BrainTestHelper.CreateInMemoryConnection();
        _sut = new UploadQueueRepository(_connection, NullLogger<UploadQueueRepository>.Instance);
    }

    public async Task InitializeAsync()
    {
        await BrainTestHelper.ApplySchemaAsync(_connection);
        BrainTestHelper.InsertTestProvider(_connection, ProviderId);
        BrainTestHelper.InsertTestFile(_connection, FileId);
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    // ── EnqueueAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EnqueueAsync_InsertsRowInPendingState()
    {
        var result = await _sut.EnqueueAsync(FileId, ProviderId, CancellationToken.None);

        Assert.True(result.Success);
        var status = await _connection.QuerySingleAsync<string>(
            "SELECT Status FROM TailUploads WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId, ProviderId });
        Assert.Equal("PENDING", status);
    }

    [Fact]
    public async Task EnqueueAsync_DuplicateFileAndProvider_ReturnsPathConflict()
    {
        await _sut.EnqueueAsync(FileId, ProviderId, CancellationToken.None);

        var result = await _sut.EnqueueAsync(FileId, ProviderId, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.PathConflict, result.Error!.Code);
    }

    // ── DequeueNextBatchAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task DequeueNextBatchAsync_ReturnsPendingRows()
    {
        // Enqueue the base file plus 2 additional files so we have 3 total.
        // Pass a unique name each time to avoid the IX_Files_Parent_Name unique constraint.
        await _sut.EnqueueAsync(FileId, ProviderId, CancellationToken.None);
        for (var i = 2; i <= 3; i++)
        {
            var extraId = $"file-{i:D3}";
            BrainTestHelper.InsertTestFile(_connection, extraId, name: extraId);
            await _sut.EnqueueAsync(extraId, ProviderId, CancellationToken.None);
        }

        var rows = new List<TailUploadRow>();
        await foreach (var r in _sut.DequeueNextBatchAsync(ProviderId, 2, CancellationToken.None))
        {
            rows.Add(r);
        }

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("PENDING", r.Status));
    }

    [Fact]
    public async Task DequeueNextBatchAsync_SkipsUploadedRows()
    {
        await _sut.EnqueueAsync(FileId, ProviderId, CancellationToken.None);
        await _sut.MarkUploadedAsync(FileId, ProviderId, "remote-001", CancellationToken.None);

        var rows = new List<TailUploadRow>();
        await foreach (var r in _sut.DequeueNextBatchAsync(ProviderId, 10, CancellationToken.None))
        {
            rows.Add(r);
        }

        Assert.Empty(rows);
    }

    // ── MarkUploadingAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task MarkUploadingAsync_ChangesStatus()
    {
        await _sut.EnqueueAsync(FileId, ProviderId, CancellationToken.None);

        var result = await _sut.MarkUploadingAsync(FileId, ProviderId, CancellationToken.None);

        Assert.True(result.Success);
        var row = await _connection.QuerySingleAsync<(string Status, int AttemptCount)>(
            "SELECT Status, AttemptCount FROM TailUploads WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId, ProviderId });
        Assert.Equal("UPLOADING", row.Status);
        Assert.Equal(1, row.AttemptCount);
    }

    // ── MarkUploadedAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task MarkUploadedAsync_SetsRemoteId()
    {
        await _sut.EnqueueAsync(FileId, ProviderId, CancellationToken.None);

        var result = await _sut.MarkUploadedAsync(FileId, ProviderId, "remote-xyz", CancellationToken.None);

        Assert.True(result.Success);
        var row = await _connection.QuerySingleAsync<(string Status, string RemoteId)>(
            "SELECT Status, RemoteId FROM TailUploads WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId, ProviderId });
        Assert.Equal("UPLOADED", row.Status);
        Assert.Equal("remote-xyz", row.RemoteId);
    }

    // ── MarkFailedAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task MarkFailedAsync_SetsLastError()
    {
        await _sut.EnqueueAsync(FileId, ProviderId, CancellationToken.None);

        var result = await _sut.MarkFailedAsync(FileId, ProviderId, "network timeout", CancellationToken.None);

        Assert.True(result.Success);
        var row = await _connection.QuerySingleAsync<(string Status, string LastError)>(
            "SELECT Status, LastError FROM TailUploads WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId, ProviderId });
        Assert.Equal("FAILED", row.Status);
        Assert.Equal("network timeout", row.LastError);
    }

    // ── GetOrCreateSessionAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetOrCreateSessionAsync_InsertsOnFirstCall_Returns()
    {
        var expires = DateTime.UtcNow.AddHours(1);

        var result = await _sut.GetOrCreateSessionAsync(
            FileId, ProviderId, "https://session-uri/test", expires, 1024, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(0, result.Value!.BytesUploaded);
        Assert.Equal(1024, result.Value.TotalBytes);
    }

    [Fact]
    public async Task GetOrCreateSessionAsync_CalledTwice_ReplacesExisting()
    {
        var expires = DateTime.UtcNow.AddHours(1);
        await _sut.GetOrCreateSessionAsync(
            FileId, ProviderId, "https://old-uri", expires, 512, CancellationToken.None);

        // Call again with a different SessionUri — INSERT OR REPLACE should reset BytesUploaded.
        var result = await _sut.GetOrCreateSessionAsync(
            FileId, ProviderId, "https://new-uri", expires, 512, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://new-uri", result.Value!.SessionUri);
        Assert.Equal(0, result.Value.BytesUploaded);
    }

    // ── UpdateSessionProgressAsync ────────────────────────────────────────────

    [Fact]
    public async Task UpdateSessionProgressAsync_UpdatesBytesUploaded()
    {
        var expires = DateTime.UtcNow.AddHours(1);
        await _sut.GetOrCreateSessionAsync(
            FileId, ProviderId, "https://session-uri/test", expires, 1024, CancellationToken.None);

        var result = await _sut.UpdateSessionProgressAsync(FileId, ProviderId, 512, CancellationToken.None);

        Assert.True(result.Success);
        var bytes = await _connection.QuerySingleAsync<long>(
            "SELECT BytesUploaded FROM UploadSessions WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId, ProviderId });
        Assert.Equal(512, bytes);
    }

    // ── DeleteSessionAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSessionAsync_RemovesRow()
    {
        var expires = DateTime.UtcNow.AddHours(1);
        await _sut.GetOrCreateSessionAsync(
            FileId, ProviderId, "https://session-uri/test", expires, 1024, CancellationToken.None);

        var result = await _sut.DeleteSessionAsync(FileId, ProviderId, CancellationToken.None);

        Assert.True(result.Success);
        var count = await _connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM UploadSessions WHERE FileID = @FileId AND ProviderID = @ProviderId",
            new { FileId, ProviderId });
        Assert.Equal(0, count);
    }
}
