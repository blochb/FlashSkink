using System.Threading.Channels;
using FlashSkink.Core.Abstractions.Notifications;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Presentation.Notifications;

/// <summary>
/// In-process notification bus owning a bounded channel and a single dispatch loop.
/// The channel is configured with capacity 100, <c>DropOldest</c> overflow policy,
/// <c>SingleReader = true</c>, <c>SingleWriter = false</c>.
/// </summary>
public sealed class NotificationBus : INotificationBus, IAsyncDisposable
{
    private const int ChannelCapacity = 100;

    private readonly Channel<Notification> _channel;
    private readonly NotificationDispatcher _dispatcher;
    private readonly ILogger<NotificationBus> _logger;
    private readonly Task _dispatchLoop;

    private int _inFlightCount;
    private int _dropsSinceLastDrain;
    private int _disposed;

    /// <param name="dispatcher">The dispatcher that handles per-notification fan-out and dedup.</param>
    /// <param name="logger">Logger for channel-full warnings and loop-level faults.</param>
    public NotificationBus(NotificationDispatcher dispatcher, ILogger<NotificationBus> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _channel = Channel.CreateBounded<Notification>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _dispatchLoop = DispatchLoopAsync();
    }

    /// <inheritdoc/>
    public ValueTask PublishAsync(Notification notification, CancellationToken ct = default)
    {
        var count = Interlocked.Increment(ref _inFlightCount);
        if (count > ChannelCapacity)
        {
            var drops = Interlocked.Increment(ref _dropsSinceLastDrain);
            Interlocked.Decrement(ref _inFlightCount);
            _logger.LogWarning(
                "Notification channel at capacity; dropping oldest. Drops since last drain: {Drops}.",
                drops);
        }

        return _channel.Writer.WriteAsync(notification, ct);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _channel.Writer.Complete();
        await _dispatchLoop.ConfigureAwait(false);
        await _dispatcher.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DispatchLoopAsync()
    {
        await foreach (var notification in _channel.Reader.ReadAllAsync())
        {
            Interlocked.Decrement(ref _inFlightCount);
            Interlocked.Exchange(ref _dropsSinceLastDrain, 0);
            try
            {
                await _dispatcher.DispatchAsync(notification, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Defence in depth — dispatcher already swallows handler exceptions, but if
                // the dispatcher itself throws (DI/lifecycle bug), we log and keep the loop running.
                _logger.LogError(ex, "Dispatcher threw; loop continues.");
            }
        }
    }
}
