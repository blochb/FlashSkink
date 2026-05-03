namespace FlashSkink.Core.Abstractions.Notifications;

/// <summary>
/// Publish surface for the in-process notification system.
/// This is the only entry point background services use to surface events to the UI and persistence layers.
/// </summary>
public interface INotificationBus
{
    /// <summary>
    /// Publishes <paramref name="notification"/> to the bus for asynchronous dispatch.
    /// If the internal channel is at capacity the oldest queued notification is evicted silently;
    /// a warning is logged but this method does not throw.
    /// </summary>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask PublishAsync(Notification notification, CancellationToken ct = default);
}
