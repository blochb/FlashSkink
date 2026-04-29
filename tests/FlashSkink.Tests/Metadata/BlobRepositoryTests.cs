using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Metadata;

public class BlobRepositoryTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly BlobRepository _sut;

    public BlobRepositoryTests()
    {
        _connection = BrainTestHelper.CreateInMemoryConnection();
        _sut = new BlobRepository(_connection, NullLogger<BlobRepository>.Instance);
    }

    public async Task InitializeAsync() => await BrainTestHelper.ApplySchemaAsync(_connection);

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private static BlobRecord MakeBlob(string? id = null, string? sha256 = null) => new()
    {
        BlobId = id ?? Guid.NewGuid().ToString(),
        EncryptedSize = 2048,
        PlaintextSize = 1024,
        PlaintextSha256 = sha256 ?? $"sha256-{Guid.NewGuid():N}",
        EncryptedXxHash = "xxhash-test-value",
        BlobPath = "blobs/ab/cd/ef.bin",
        CreatedUtc = DateTime.UtcNow,
    };

    // ── InsertAsync / GetByIdAsync ────────────────────────────────────────────

    [Fact]
    public async Task InsertAsync_RoundTripsViaGetById()
    {
        var blob = MakeBlob();

        await _sut.InsertAsync(blob, CancellationToken.None);
        var result = await _sut.GetByIdAsync(blob.BlobId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(blob.BlobId, result.Value!.BlobId);
        Assert.Equal(blob.EncryptedSize, result.Value.EncryptedSize);
        Assert.Equal(blob.PlaintextSize, result.Value.PlaintextSize);
        Assert.Equal(blob.PlaintextSha256, result.Value.PlaintextSha256);
        Assert.Equal(blob.EncryptedXxHash, result.Value.EncryptedXxHash);
        Assert.Equal(blob.BlobPath, result.Value.BlobPath);
        Assert.Null(result.Value.SoftDeletedUtc);
        Assert.Null(result.Value.PurgeAfterUtc);
    }

    // ── GetByPlaintextHashAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetByPlaintextHashAsync_ExistingHash_ReturnsBlobRecord()
    {
        var blob = MakeBlob(sha256: "known-sha256-hash");
        await _sut.InsertAsync(blob, CancellationToken.None);

        var result = await _sut.GetByPlaintextHashAsync("known-sha256-hash", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(blob.BlobId, result.Value!.BlobId);
    }

    [Fact]
    public async Task GetByPlaintextHashAsync_SoftDeletedBlob_ReturnsNull()
    {
        var blob = MakeBlob(sha256: "deleted-hash");
        await _sut.InsertAsync(blob, CancellationToken.None);
        await _sut.SoftDeleteAsync(blob.BlobId, DateTime.UtcNow.AddDays(30), CancellationToken.None);

        var result = await _sut.GetByPlaintextHashAsync("deleted-hash", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Value); // active-blobs-only filter excludes soft-deleted rows
    }

    [Fact]
    public async Task GetByPlaintextHashAsync_UnknownHash_ReturnsNull()
    {
        var result = await _sut.GetByPlaintextHashAsync("no-such-hash", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Value);
    }

    // ── SoftDeleteAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task SoftDeleteAsync_SetsTimestamps()
    {
        var blob = MakeBlob();
        await _sut.InsertAsync(blob, CancellationToken.None);

        await _sut.SoftDeleteAsync(blob.BlobId, DateTime.UtcNow.AddDays(30), CancellationToken.None);
        var result = await _sut.GetByIdAsync(blob.BlobId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value!.SoftDeletedUtc);
        Assert.NotNull(result.Value.PurgeAfterUtc);
    }

    // ── MarkCorruptAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task MarkCorruptAsync_SetsPurgeToNow()
    {
        var blob = MakeBlob();
        var before = DateTime.UtcNow;
        await _sut.InsertAsync(blob, CancellationToken.None);

        await _sut.MarkCorruptAsync(blob.BlobId, CancellationToken.None);
        var result = await _sut.GetByIdAsync(blob.BlobId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value!.PurgeAfterUtc);
        // PurgeAfterUtc should be at-or-after the timestamp taken before MarkCorruptAsync ran.
        Assert.True(result.Value.PurgeAfterUtc!.Value >= before.AddSeconds(-1));
    }

    // ── ListPendingPurgeAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ListPendingPurgeAsync_ReturnsOnlyEligible()
    {
        // One blob whose grace period has already expired.
        var past = MakeBlob();
        await _sut.InsertAsync(past, CancellationToken.None);
        await _sut.SoftDeleteAsync(past.BlobId, DateTime.UtcNow.AddSeconds(-1), CancellationToken.None);

        // One blob whose grace period is still in the future.
        var future = MakeBlob();
        await _sut.InsertAsync(future, CancellationToken.None);
        await _sut.SoftDeleteAsync(future.BlobId, DateTime.UtcNow.AddDays(30), CancellationToken.None);

        var result = await _sut.ListPendingPurgeAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Value!);
        Assert.Equal(past.BlobId, result.Value![0].BlobId);
    }

    // ── HardDeleteAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task HardDeleteAsync_RemovesRow()
    {
        var blob = MakeBlob();
        await _sut.InsertAsync(blob, CancellationToken.None);

        await _sut.HardDeleteAsync(blob.BlobId, CancellationToken.None);
        var result = await _sut.GetByIdAsync(blob.BlobId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Value);
    }
}
