using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Metadata;

public class BackgroundFailureRepositoryTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly BackgroundFailureRepository _sut;

    public BackgroundFailureRepositoryTests()
    {
        _connection = BrainTestHelper.CreateInMemoryConnection();
        _sut = new BackgroundFailureRepository(_connection, NullLogger<BackgroundFailureRepository>.Instance);
    }

    public async Task InitializeAsync() => await BrainTestHelper.ApplySchemaAsync(_connection);

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private static BackgroundFailure MakeFailure() => new()
    {
        FailureId = Guid.NewGuid().ToString(),
        OccurredUtc = DateTime.UtcNow,
        Source = "TestService",
        ErrorCode = "Unknown",
        Message = "Test error message",
        Acknowledged = false,
    };

    // ── AppendAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AppendAsync_RoundTripsViaListUnacknowledged()
    {
        var failure = MakeFailure();

        await _sut.AppendAsync(failure, CancellationToken.None);
        var result = await _sut.ListUnacknowledgedAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Value!);
        Assert.Equal(failure.FailureId, result.Value![0].FailureId);
        Assert.Equal(failure.Source, result.Value[0].Source);
        Assert.Equal(failure.ErrorCode, result.Value[0].ErrorCode);
        Assert.False(result.Value[0].Acknowledged);
    }

    // ── AcknowledgeAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task AcknowledgeAsync_SetsAcknowledgedFlag()
    {
        var failure = MakeFailure();
        await _sut.AppendAsync(failure, CancellationToken.None);

        await _sut.AcknowledgeAsync(failure.FailureId, CancellationToken.None);
        var result = await _sut.ListUnacknowledgedAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Value!);
    }

    // ── AcknowledgeAllAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task AcknowledgeAllAsync_ClearsAllUnacknowledged()
    {
        for (var i = 0; i < 3; i++)
        {
            await _sut.AppendAsync(MakeFailure(), CancellationToken.None);
        }

        await _sut.AcknowledgeAllAsync(CancellationToken.None);
        var result = await _sut.ListUnacknowledgedAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Value!);
    }
}
