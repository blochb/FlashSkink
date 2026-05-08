using FlashSkink.Core.Abstractions.Notifications;

namespace FlashSkink.Tests.Engine;

/// <summary>
/// Captures all published notifications for assertion in tests. Set
/// <see cref="ThrowOnPublish"/> to verify that a failing bus does not mask the original error.
/// </summary>
internal sealed class RecordingNotificationBus : INotificationBus
{
    private readonly List<Notification> _notifications = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<Notification> Published
    {
        get
        {
            lock (_lock)
            {
                return _notifications.ToArray();
            }
        }
    }

    public bool ThrowOnPublish { get; set; }

    public ValueTask PublishAsync(Notification notification, CancellationToken ct = default)
    {
        if (ThrowOnPublish)
        {
            throw new InvalidOperationException("Test bus configured to throw.");
        }

        lock (_lock)
        {
            _notifications.Add(notification);
        }

        return ValueTask.CompletedTask;
    }
}
