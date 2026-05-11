using FlashSkink.Core.Abstractions.Notifications;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Notifications;

/// <summary>
/// Per-notification fan-out with <c>(Source, ErrorCode)</c> deduplication within a configurable window.
/// Handlers are invoked in registration order; a throwing handler is logged at Warning and does not
/// interrupt fan-out to subsequent handlers (§8.3).
/// </summary>
public sealed class NotificationDispatcher : IAsyncDisposable
{
    private readonly TimeSpan _dedupWindow;
    private readonly TimeSpan _flushInterval;
    private readonly ILogger<NotificationDispatcher> _logger;

    private readonly List<INotificationHandler> _handlers = [];
    private readonly Dictionary<(string Source, string ErrorCode), DedupEntry> _dedup = [];
    private readonly object _dedupLock = new();

    private readonly CancellationTokenSource _flushCts = new();
    private readonly Task _flushTask;
    private int _disposed;

    /// <param name="logger">Logger for handler faults and flush events.</param>
    public NotificationDispatcher(ILogger<NotificationDispatcher> logger)
        : this(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5), logger) { }

    /// <param name="dedupWindow">Window during which repeated <c>(Source, ErrorCode)</c> pairs are suppressed.</param>
    /// <param name="flushInterval">How often the periodic flush timer runs to emit pending summary notifications.</param>
    /// <param name="logger">Logger for handler faults and flush events.</param>
    public NotificationDispatcher(TimeSpan dedupWindow, TimeSpan flushInterval, ILogger<NotificationDispatcher> logger)
    {
        _dedupWindow = dedupWindow;
        _flushInterval = flushInterval;
        _logger = logger;
        _flushTask = RunPeriodicFlushAsync(_flushCts.Token);
    }

    /// <summary>
    /// Registers a handler. Not thread-safe with concurrent <see cref="DispatchAsync"/> calls;
    /// all handlers must be registered before the bus begins dispatching (i.e. at startup).
    /// </summary>
    public void RegisterHandler(INotificationHandler handler)
    {
        _handlers.Add(handler);
    }

    /// <summary>
    /// Applies dedup logic then invokes all registered handlers in order.
    /// Called by <see cref="NotificationBus"/>'s dispatch loop.
    /// </summary>
    /// <param name="notification">The notification to dispatch.</param>
    /// <param name="ct">Cancellation token forwarded from the dispatch loop.</param>
    public async ValueTask DispatchAsync(Notification notification, CancellationToken ct)
    {
        if (notification.Error is not null)
        {
            var key = (notification.Source, notification.Error.Code.ToString());
            var now = DateTime.UtcNow;
            bool suppress;
            DedupEntry? staleEntry = null;

            lock (_dedupLock)
            {
                if (_dedup.TryGetValue(key, out var existing))
                {
                    var elapsed = now - existing.FirstOccurredUtc;
                    if (elapsed < _dedupWindow)
                    {
                        _dedup[key] = existing with
                        {
                            SuppressedCount = existing.SuppressedCount + 1,
                            LastOccurredUtc = now,
                        };
                        suppress = true;
                    }
                    else
                    {
                        if (existing.SuppressedCount > 0)
                        {
                            staleEntry = existing;
                        }
                        _dedup[key] = new DedupEntry
                        {
                            FirstOccurredUtc = now,
                            LastOccurredUtc = now,
                            SuppressedCount = 0,
                        };
                        suppress = false;
                    }
                }
                else
                {
                    _dedup[key] = new DedupEntry
                    {
                        FirstOccurredUtc = now,
                        LastOccurredUtc = now,
                        SuppressedCount = 0,
                    };
                    suppress = false;
                }
            }

            if (staleEntry.HasValue)
            {
                await EmitSummaryAsync(notification.Source, key.Item2, staleEntry.Value, ct).ConfigureAwait(false);
            }

            if (suppress)
            {
                return;
            }
        }

        await InvokeHandlersAsync(notification, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _flushCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _flushTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }

        // Flush any pending dedup summaries before disposing.
        List<(string Source, string ErrorCode, DedupEntry Entry)> pending;
        lock (_dedupLock)
        {
            pending = _dedup
                .Where(kv => kv.Value.SuppressedCount > 0)
                .Select(kv => (kv.Key.Source, kv.Key.ErrorCode, kv.Value))
                .ToList();
            _dedup.Clear();
        }

        foreach (var (source, errorCode, entry) in pending)
        {
            await EmitSummaryAsync(source, errorCode, entry, CancellationToken.None).ConfigureAwait(false);
        }

        _flushCts.Dispose();
    }

    private async Task RunPeriodicFlushAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_flushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await FlushStaleEntriesAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task FlushStaleEntriesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        List<(string Source, string ErrorCode, DedupEntry Entry)> toFlush;

        lock (_dedupLock)
        {
            toFlush = _dedup
                .Where(kv => now - kv.Value.FirstOccurredUtc >= _dedupWindow && kv.Value.SuppressedCount > 0)
                .Select(kv => (kv.Key.Source, kv.Key.ErrorCode, kv.Value))
                .ToList();

            foreach (var (source, errorCode, _) in toFlush)
            {
                _dedup.Remove((source, errorCode));
            }
        }

        foreach (var (source, errorCode, entry) in toFlush)
        {
            await EmitSummaryAsync(source, errorCode, entry, ct).ConfigureAwait(false);
        }
    }

    private async Task EmitSummaryAsync(string source, string errorCode, DedupEntry entry, CancellationToken ct)
    {
        var summary = new Notification
        {
            Source = source,
            Severity = NotificationSeverity.Warning,
            Title = "Repeated background failure",
            Message = $"{errorCode}: {entry.SuppressedCount} additional occurrence(s) within {_dedupWindow}.",
            Error = null,
            OccurredUtc = entry.LastOccurredUtc,
        };

        await InvokeHandlersAsync(summary, ct).ConfigureAwait(false);
    }

    private async Task InvokeHandlersAsync(Notification notification, CancellationToken ct)
    {
        foreach (var handler in _handlers)
        {
            try
            {
                await handler.HandleAsync(notification, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Handler {HandlerType} failed; continuing fan-out.", handler.GetType().Name);
            }
        }
    }

    private struct DedupEntry
    {
        public DateTime FirstOccurredUtc;
        public DateTime LastOccurredUtc;
        public int SuppressedCount;
    }
}
