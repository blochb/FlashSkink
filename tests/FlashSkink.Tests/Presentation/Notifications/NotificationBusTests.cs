using System.Threading.Channels;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Presentation.Notifications;
using FlashSkink.Tests._TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlashSkink.Tests.Presentation.Notifications;

public sealed class NotificationBusTests : IAsyncLifetime
{
    private readonly RecordingLogger<NotificationBus> _busLogger = new();
    private readonly RecordingLogger<NotificationDispatcher> _dispatcherLogger = new();

    private NotificationDispatcher _dispatcher = null!;
    private NotificationBus _bus = null!;
    private bool _disposed;

    public Task InitializeAsync()
    {
        _dispatcher = new NotificationDispatcher(
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(5),
            _dispatcherLogger);
        _bus = new NotificationBus(_dispatcher, _busLogger);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (!_disposed)
        {
            await _bus.DisposeAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static Notification MakeNotification(string source = "TestSource") => new()
    {
        Source = source,
        Severity = NotificationSeverity.Info,
        Title = "Test notification",
        Message = "This is a test.",
    };

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_SingleNotification_ReachesDispatcher()
    {
        var received = new List<Notification>();
        _dispatcher.RegisterHandler(new RecordingHandler(received));

        await _bus.PublishAsync(MakeNotification());

        await WaitForAsync(() => received.Count >= 1, TimeSpan.FromSeconds(2));

        Assert.Single(received);
    }

    [Fact]
    public async Task PublishAsync_BurstAtCapacity_LogsDropWarning()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.RegisterHandler(new BlockingHandler(tcs.Task));

        // Publish 200 notifications — double the 100-capacity channel.
        // The dispatch loop reader blocks on the first notification, so the 101st+ overflow.
        for (var i = 0; i < 200; i++)
        {
            await _bus.PublishAsync(MakeNotification());
        }

        Assert.True(
            _busLogger.HasEntry(LogLevel.Warning, "channel at capacity"),
            "Expected at least one 'channel at capacity' warning log entry.");

        tcs.SetResult();
    }

    [Fact]
    public async Task PublishAsync_AfterDispose_Throws()
    {
        _disposed = true;
        await _bus.DisposeAsync();

        await Assert.ThrowsAsync<ChannelClosedException>(
            async () => await _bus.PublishAsync(MakeNotification()));
    }

    [Fact]
    public async Task DisposeAsync_DrainsPendingNotifications()
    {
        var received = new List<Notification>();
        _dispatcher.RegisterHandler(new RecordingHandler(received));

        for (var i = 0; i < 5; i++)
        {
            await _bus.PublishAsync(MakeNotification());
        }

        _disposed = true;
        await _bus.DisposeAsync();

        Assert.Equal(5, received.Count);
    }

    [Fact]
    public async Task Handler_ThrowingException_DoesNotKillDispatchLoop()
    {
        // A handler that throws on the 2nd call exercises the dispatcher's handler-isolation
        // catch block (§8.3). The bus loop continues dispatching the remaining notifications.
        var received = new List<Notification>();
        _dispatcher.RegisterHandler(new ThrowOnNthHandler(throwOnCall: 2, received));

        for (var i = 0; i < 5; i++)
        {
            await _bus.PublishAsync(MakeNotification());
        }

        _disposed = true;
        await _bus.DisposeAsync();

        // Call 2 throws before adding to received; all others succeed.
        Assert.Equal(4, received.Count);
        Assert.True(
            _dispatcherLogger.HasEntry(LogLevel.Warning, "failed"),
            "Expected dispatcher to log a warning for the throwing handler.");
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

    private sealed class BlockingHandler(Task releaseSignal) : INotificationHandler
    {
        public async ValueTask HandleAsync(Notification notification, CancellationToken ct)
        {
            await releaseSignal.ConfigureAwait(false);
        }
    }

    private sealed class ThrowOnNthHandler(int throwOnCall, List<Notification> received) : INotificationHandler
    {
        private int _callCount;

        public ValueTask HandleAsync(Notification notification, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _callCount);
            if (n == throwOnCall)
            {
                throw new InvalidOperationException("Simulated handler fault.");
            }

            received.Add(notification);
            return ValueTask.CompletedTask;
        }
    }
}
