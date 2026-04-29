using Dapper;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Metadata;

public class WalRepositoryTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly WalRepository _sut;

    public WalRepositoryTests()
    {
        _connection = BrainTestHelper.CreateInMemoryConnection();
        _sut = new WalRepository(_connection, NullLogger<WalRepository>.Instance);
    }

    public async Task InitializeAsync() => await BrainTestHelper.ApplySchemaAsync(_connection);

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private static WalRow MakeRow(string? id = null) => new(
        WalId: id ?? Guid.NewGuid().ToString(),
        Operation: "WRITE",
        Phase: "PREPARE",
        StartedUtc: DateTime.UtcNow,
        UpdatedUtc: DateTime.UtcNow,
        Payload: "{\"test\":true}");

    // ── InsertAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertAsync_InsertsRow_WithPreparePhase()
    {
        var row = MakeRow();

        var result = await _sut.InsertAsync(row, ct: CancellationToken.None);

        Assert.True(result.Success);
        var list = await _sut.ListIncompleteAsync(CancellationToken.None);
        Assert.True(list.Success);
        Assert.Single(list.Value!);
        Assert.Equal(row.WalId, list.Value![0].WalId);
        Assert.Equal("PREPARE", list.Value[0].Phase);
    }

    [Fact]
    public async Task InsertAsync_WithExternalTransaction_CommitsWithCaller()
    {
        var row = MakeRow();
        using var tx = _connection.BeginTransaction();

        var result = await _sut.InsertAsync(row, tx, CancellationToken.None);
        Assert.True(result.Success);
        tx.Commit();

        var list = await _sut.ListIncompleteAsync(CancellationToken.None);
        Assert.True(list.Success);
        Assert.Single(list.Value!);
    }

    // ── TransitionAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_UpdatesPhase()
    {
        var row = MakeRow();
        await _sut.InsertAsync(row, ct: CancellationToken.None);

        var result = await _sut.TransitionAsync(row.WalId, "COMMITTED", CancellationToken.None);

        Assert.True(result.Success);
        var phase = await _connection.QuerySingleAsync<string>(
            "SELECT Phase FROM WAL WHERE WALID = @Id", new { Id = row.WalId });
        Assert.Equal("COMMITTED", phase);
    }

    // ── ListIncompleteAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ListIncompleteAsync_ExcludesCommittedAndFailed()
    {
        var prepare = MakeRow();
        var committed = MakeRow();
        var failed = MakeRow();

        await _sut.InsertAsync(prepare, ct: CancellationToken.None);
        await _sut.InsertAsync(committed, ct: CancellationToken.None);
        await _sut.TransitionAsync(committed.WalId, "COMMITTED", CancellationToken.None);
        await _sut.InsertAsync(failed, ct: CancellationToken.None);
        await _sut.TransitionAsync(failed.WalId, "FAILED", CancellationToken.None);

        var result = await _sut.ListIncompleteAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Value!);
        Assert.Equal(prepare.WalId, result.Value![0].WalId);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesRow()
    {
        var row = MakeRow();
        await _sut.InsertAsync(row, ct: CancellationToken.None);

        var result = await _sut.DeleteAsync(row.WalId, CancellationToken.None);

        Assert.True(result.Success);
        var list = await _sut.ListIncompleteAsync(CancellationToken.None);
        Assert.True(list.Success);
        Assert.Empty(list.Value!);
    }
}
