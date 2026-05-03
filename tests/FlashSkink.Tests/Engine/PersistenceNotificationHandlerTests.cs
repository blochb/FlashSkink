using System.Text.Json;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Engine;
using FlashSkink.Core.Metadata;
using FlashSkink.Tests._TestSupport;
using FlashSkink.Tests.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Engine;

public sealed class PersistenceNotificationHandlerTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private BackgroundFailureRepository _repository = null!;
    private RecordingLogger<PersistenceNotificationHandler> _logger = null!;
    private PersistenceNotificationHandler _handler = null!;

    public async Task InitializeAsync()
    {
        _connection = BrainTestHelper.CreateInMemoryConnection();
        await BrainTestHelper.ApplySchemaAsync(_connection);
        _repository = new BackgroundFailureRepository(_connection, NullLogger<BackgroundFailureRepository>.Instance);
        _logger = new RecordingLogger<PersistenceNotificationHandler>();
        _handler = new PersistenceNotificationHandler(_repository, _logger);
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static Notification Make(
        NotificationSeverity severity,
        ErrorCode? code = ErrorCode.UploadFailed,
        string source = "TestService",
        string message = "Something failed.",
        IReadOnlyDictionary<string, string>? metadata = null) => new()
        {
            Source = source,
            Severity = severity,
            Title = "Background operation failed",
            Message = message,
            Error = code.HasValue
            ? new ErrorContext
            {
                Code = code.Value,
                Message = message,
                Metadata = metadata,
            }
            : null,
            OccurredUtc = DateTime.UtcNow,
        };

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_InfoNotification_DoesNotPersist()
    {
        await _handler.HandleAsync(Make(NotificationSeverity.Info), CancellationToken.None);

        var result = await _repository.ListUnacknowledgedAsync(CancellationToken.None);
        Assert.True(result.Success);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task HandleAsync_WarningNotification_DoesNotPersist()
    {
        await _handler.HandleAsync(Make(NotificationSeverity.Warning), CancellationToken.None);

        var result = await _repository.ListUnacknowledgedAsync(CancellationToken.None);
        Assert.True(result.Success);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task HandleAsync_ErrorNotification_PersistsRow()
    {
        var notification = Make(
            NotificationSeverity.Error,
            code: ErrorCode.DownloadFailed,
            source: "UploadService",
            message: "Download from tail failed.");

        await _handler.HandleAsync(notification, CancellationToken.None);

        var result = await _repository.ListUnacknowledgedAsync(CancellationToken.None);
        Assert.True(result.Success);
        var row = Assert.Single(result.Value!);
        Assert.Equal("UploadService", row.Source);
        Assert.Equal(ErrorCode.DownloadFailed.ToString(), row.ErrorCode);
        Assert.Equal("Download from tail failed.", row.Message);
        // Timestamps round-trip through SQLite ISO-8601 strings; allow 1-second tolerance.
        Assert.True(
            Math.Abs((row.OccurredUtc - notification.OccurredUtc).TotalSeconds) < 1,
            "OccurredUtc should round-trip within 1 second.");
        Assert.False(row.Acknowledged);
    }

    [Fact]
    public async Task HandleAsync_CriticalNotification_PersistsRow()
    {
        var notification = Make(
            NotificationSeverity.Critical,
            code: ErrorCode.VolumeCorrupt,
            source: "VolumeService",
            message: "Storage appears corrupted.");

        await _handler.HandleAsync(notification, CancellationToken.None);

        var result = await _repository.ListUnacknowledgedAsync(CancellationToken.None);
        Assert.True(result.Success);
        var row = Assert.Single(result.Value!);
        Assert.Equal(ErrorCode.VolumeCorrupt.ToString(), row.ErrorCode);
        Assert.False(row.Acknowledged);
    }

    [Fact]
    public async Task HandleAsync_NullError_PersistsErrorCodeUnknown()
    {
        var notification = new Notification
        {
            Source = "SomeService",
            Severity = NotificationSeverity.Error,
            Title = "Operation failed",
            Message = "An unexpected issue occurred.",
            Error = null,
        };

        await _handler.HandleAsync(notification, CancellationToken.None);

        var result = await _repository.ListUnacknowledgedAsync(CancellationToken.None);
        Assert.True(result.Success);
        var row = Assert.Single(result.Value!);
        Assert.Equal("Unknown", row.ErrorCode);
    }

    [Fact]
    public async Task HandleAsync_RepositoryFailure_LogsAndSwallows()
    {
        // Close the connection so AppendAsync returns a failed Result.
        _connection.Close();

        var notification = Make(NotificationSeverity.Error);

        // Must not throw.
        var exception = await Record.ExceptionAsync(
            () => _handler.HandleAsync(notification, CancellationToken.None).AsTask());

        Assert.Null(exception);
        Assert.True(
            _logger.HasEntry(LogLevel.Warning, string.Empty) ||
            _logger.HasEntry(LogLevel.Error, string.Empty),
            "Expected a log entry indicating the persistence failure.");
    }

    [Fact]
    public async Task HandleAsync_Cancellation_LogsAtInformation_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var exception = await Record.ExceptionAsync(
            () => _handler.HandleAsync(Make(NotificationSeverity.Error), cts.Token).AsTask());

        Assert.Null(exception);
        Assert.True(
            _logger.HasEntry(LogLevel.Information, "cancel"),
            "Expected an Information log for cancelled persist.");
    }

    [Fact]
    public async Task HandleAsync_MetadataRoundTrips()
    {
        var metadata = new Dictionary<string, string>
        {
            ["BlobId"] = "blob-abc",
            ["ProviderId"] = "FileSystem",
        };

        var notification = Make(
            NotificationSeverity.Error,
            code: ErrorCode.BlobCorrupt,
            metadata: metadata);

        await _handler.HandleAsync(notification, CancellationToken.None);

        var result = await _repository.ListUnacknowledgedAsync(CancellationToken.None);
        Assert.True(result.Success);
        var row = Assert.Single(result.Value!);
        Assert.NotNull(row.Metadata);

        var roundTripped = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Metadata!);
        Assert.NotNull(roundTripped);
        Assert.Equal(metadata["BlobId"], roundTripped!["BlobId"]);
        Assert.Equal(metadata["ProviderId"], roundTripped["ProviderId"]);
    }
}
