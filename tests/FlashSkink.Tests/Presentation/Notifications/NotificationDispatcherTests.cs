using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Presentation.Notifications;
using FlashSkink.Tests._TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlashSkink.Tests.Presentation.Notifications;

public sealed class NotificationDispatcherTests : IAsyncLifetime
{
    private readonly RecordingLogger<NotificationDispatcher> _logger = new();
    private NotificationDispatcher _dispatcher = null!;

    public Task InitializeAsync()
    {
        // Short window and flush interval so timing-sensitive tests don't need long waits.
        _dispatcher = new NotificationDispatcher(
            dedupWindow: TimeSpan.FromSeconds(1),
            flushInterval: TimeSpan.FromMilliseconds(50),
            _logger);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _dispatcher.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static Notification MakeNotification(
        string source = "Svc",
        ErrorCode? code = null,
        string title = "Background operation failed",
        string message = "An operation failed.") => new()
        {
            Source = source,
            Severity = NotificationSeverity.Error,
            Title = title,
            Message = message,
            Error = code.HasValue
            ? new ErrorContext { Code = code.Value, Message = message }
            : null,
        };

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_NoError_BypassesDedup()
    {
        var received = new List<Notification>();
        _dispatcher.RegisterHandler(new RecordingHandler(received));

        var n1 = MakeNotification(source: "Svc", code: null);
        var n2 = MakeNotification(source: "Svc", code: null);

        await _dispatcher.DispatchAsync(n1, CancellationToken.None);
        await _dispatcher.DispatchAsync(n2, CancellationToken.None);

        Assert.Equal(2, received.Count);
    }

    [Fact]
    public async Task DispatchAsync_FirstWithError_Dispatches()
    {
        var received = new List<Notification>();
        _dispatcher.RegisterHandler(new RecordingHandler(received));

        await _dispatcher.DispatchAsync(MakeNotification(code: ErrorCode.UploadFailed), CancellationToken.None);

        Assert.Single(received);
    }

    [Fact]
    public async Task DispatchAsync_DuplicateInWindow_Suppresses()
    {
        var received = new List<Notification>();
        _dispatcher.RegisterHandler(new RecordingHandler(received));

        var n1 = MakeNotification(source: "Svc", code: ErrorCode.UploadFailed);
        var n2 = MakeNotification(source: "Svc", code: ErrorCode.UploadFailed);

        await _dispatcher.DispatchAsync(n1, CancellationToken.None);
        await _dispatcher.DispatchAsync(n2, CancellationToken.None); // same key — suppressed

        Assert.Single(received);
    }

    [Fact]
    public async Task DispatchAsync_DuplicateAcrossWindow_DispatchesBoth()
    {
        var received = new List<Notification>();
        _dispatcher.RegisterHandler(new RecordingHandler(received));

        await _dispatcher.DispatchAsync(MakeNotification(source: "Svc", code: ErrorCode.DownloadFailed), CancellationToken.None);

        // Wait for the dedup window to lapse.
        await Task.Delay(TimeSpan.FromSeconds(1.1));

        await _dispatcher.DispatchAsync(MakeNotification(source: "Svc", code: ErrorCode.DownloadFailed), CancellationToken.None);

        Assert.Equal(2, received.Count);
    }

    [Fact]
    public async Task DispatchAsync_DuplicateAcrossWindow_EmitsSummaryWhenSuppressedCountAboveOne()
    {
        var received = new List<Notification>();
        _dispatcher.RegisterHandler(new RecordingHandler(received));

        // First dispatch — window starts.
        await _dispatcher.DispatchAsync(MakeNotification(source: "Svc", code: ErrorCode.UploadFailed), CancellationToken.None);

        // Three more in-window — all suppressed (SuppressedCount becomes 3).
        for (var i = 0; i < 3; i++)
        {
            await _dispatcher.DispatchAsync(MakeNotification(source: "Svc", code: ErrorCode.UploadFailed), CancellationToken.None);
        }

        // Wait for window to lapse, then dispatch again to trigger lazy flush of stale entry.
        await Task.Delay(TimeSpan.FromSeconds(1.1));

        await _dispatcher.DispatchAsync(MakeNotification(source: "Svc", code: ErrorCode.UploadFailed), CancellationToken.None);

        // Expected: first notification, summary ("3 additional"), new notification.
        Assert.Equal(3, received.Count);
        Assert.Contains(received, n => n.Title == "Repeated background failure" && n.Message.Contains("3"));
    }

    [Fact]
    public async Task PeriodicFlush_EmitsSummaryAfterWindow()
    {
        // Use a short dedup window to keep total test time low.
        await _dispatcher.DisposeAsync();
        _dispatcher = new NotificationDispatcher(
            dedupWindow: TimeSpan.FromMilliseconds(300),
            flushInterval: TimeSpan.FromMilliseconds(50),
            _logger);

        var received = new List<Notification>();
        _dispatcher.RegisterHandler(new RecordingHandler(received));

        // Fire all three dispatches in rapid succession — no inter-dispatch delays.
        // Task.Delay(ms) on Windows CI can overshoot by hundreds of milliseconds, so
        // any delay between dispatches risks pushing a dispatch past the dedup window,
        // making it look like a fresh event rather than a suppressed duplicate.
        await _dispatcher.DispatchAsync(MakeNotification(source: "Svc", code: ErrorCode.BlobCorrupt), CancellationToken.None);
        await _dispatcher.DispatchAsync(MakeNotification(source: "Svc", code: ErrorCode.BlobCorrupt), CancellationToken.None);
        await _dispatcher.DispatchAsync(MakeNotification(source: "Svc", code: ErrorCode.BlobCorrupt), CancellationToken.None);

        // Wait for: dedup window (300 ms) to expire + periodic flush (≤50 ms) to fire.
        // Use 800 ms total to provide headroom for CI timer jitter.
        await Task.Delay(800);

        Assert.True(received.Count >= 2, $"Expected first notification + summary; got {received.Count}");
        Assert.Contains(received, n => n.Title == "Repeated background failure");
    }

    [Fact]
    public async Task RegisterHandler_TwoHandlers_BothReceive()
    {
        var received1 = new List<Notification>();
        var received2 = new List<Notification>();
        _dispatcher.RegisterHandler(new RecordingHandler(received1));
        _dispatcher.RegisterHandler(new RecordingHandler(received2));

        await _dispatcher.DispatchAsync(MakeNotification(), CancellationToken.None);

        Assert.Single(received1);
        Assert.Single(received2);
    }

    [Fact]
    public async Task Handler_ThrowingException_OtherHandlersStillReceive()
    {
        var received = new List<Notification>();
        _dispatcher.RegisterHandler(new ThrowingHandler());
        _dispatcher.RegisterHandler(new RecordingHandler(received));

        await _dispatcher.DispatchAsync(MakeNotification(), CancellationToken.None);

        Assert.Single(received);
        Assert.True(_logger.HasEntry(LogLevel.Warning, "failed"), "Expected warning about throwing handler.");
    }

    [Fact]
    public async Task DisposeAsync_FlushesPendingSummaries()
    {
        var received = new List<Notification>();
        _dispatcher.RegisterHandler(new RecordingHandler(received));

        // First, then 2 suppressed.
        await _dispatcher.DispatchAsync(MakeNotification(source: "Svc", code: ErrorCode.ProviderUnreachable), CancellationToken.None);
        await _dispatcher.DispatchAsync(MakeNotification(source: "Svc", code: ErrorCode.ProviderUnreachable), CancellationToken.None);
        await _dispatcher.DispatchAsync(MakeNotification(source: "Svc", code: ErrorCode.ProviderUnreachable), CancellationToken.None);

        // Dispose should flush the 2 suppressed as a summary notification.
        await _dispatcher.DisposeAsync();

        Assert.Equal(2, received.Count); // first + summary
        Assert.Contains(received, n => n.Title == "Repeated background failure" && n.Message.Contains("2"));
    }

    [Fact]
    public async Task DispatchAsync_NullError_DifferentSources_BothDispatch()
    {
        var received = new List<Notification>();
        _dispatcher.RegisterHandler(new RecordingHandler(received));

        await _dispatcher.DispatchAsync(MakeNotification(source: "SvcA", code: null), CancellationToken.None);
        await _dispatcher.DispatchAsync(MakeNotification(source: "SvcB", code: null), CancellationToken.None);

        Assert.Equal(2, received.Count);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────────

    private sealed class RecordingHandler(List<Notification> received) : INotificationHandler
    {
        public ValueTask HandleAsync(Notification notification, CancellationToken ct)
        {
            received.Add(notification);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : INotificationHandler
    {
        public ValueTask HandleAsync(Notification notification, CancellationToken ct)
            => throw new InvalidOperationException("Simulated handler fault.");
    }
}
