namespace FlashSkink.Core.Abstractions.Notifications;

/// <summary>
/// Sink for dispatched notifications. Implementations must never throw:
/// exceptions propagate through the dispatcher's fan-out and would silently drop
/// all subsequent handlers in the same dispatch call.
/// </summary>
public interface INotificationHandler
{
    /// <summary>
    /// Handles one dispatched notification. Must not throw; all exceptions must be caught
    /// and swallowed internally (logging is encouraged).
    /// </summary>
    /// <param name="notification">The notification being dispatched.</param>
    /// <param name="ct">Cancellation token forwarded from the dispatcher.</param>
    ValueTask HandleAsync(Notification notification, CancellationToken ct);
}
