using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Metadata;

public class ActivityLogRepositoryTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly ActivityLogRepository _sut;

    public ActivityLogRepositoryTests()
    {
        _connection = BrainTestHelper.CreateInMemoryConnection();
        _sut = new ActivityLogRepository(_connection, NullLogger<ActivityLogRepository>.Instance);
    }

    public async Task InitializeAsync() => await BrainTestHelper.ApplySchemaAsync(_connection);

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private static ActivityLogEntry MakeEntry(string category = "WRITE") => new()
    {
        EntryId = Guid.NewGuid().ToString(),
        OccurredUtc = DateTime.UtcNow,
        Category = category,
        Summary = $"Test event [{category}]",
    };

    // ── AppendAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AppendAsync_RoundTripsViaListRecent()
    {
        var entry = MakeEntry();

        await _sut.AppendAsync(entry, CancellationToken.None);
        var result = await _sut.ListRecentAsync(10, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Value!);
        Assert.Equal(entry.EntryId, result.Value![0].EntryId);
        Assert.Equal(entry.Category, result.Value[0].Category);
        Assert.Equal(entry.Summary, result.Value[0].Summary);
    }

    // ── ListRecentAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListRecentAsync_RespectsLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _sut.AppendAsync(MakeEntry(), CancellationToken.None);
        }

        var result = await _sut.ListRecentAsync(3, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.Value!.Count);
    }

    // ── ListByCategoryAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ListByCategoryAsync_FiltersCategory()
    {
        await _sut.AppendAsync(MakeEntry("WRITE"), CancellationToken.None);
        await _sut.AppendAsync(MakeEntry("WRITE"), CancellationToken.None);
        await _sut.AppendAsync(MakeEntry("DELETE"), CancellationToken.None);

        var result = await _sut.ListByCategoryAsync("WRITE", 10, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Value!.Count);
        Assert.All(result.Value, e => Assert.Equal("WRITE", e.Category));
    }
}
