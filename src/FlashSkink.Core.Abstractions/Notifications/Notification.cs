using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Abstractions.Notifications;

/// <summary>
/// One published notification — the unit of work flowing through the bus and dispatcher.
/// All user-visible strings (<see cref="Title"/>, <see cref="Message"/>) must obey Principle 25:
/// no appliance vocabulary (blob, WAL, DEK, stripe, etc.).
/// </summary>
public sealed class Notification
{
    /// <summary>Logical origin of the notification, e.g. the service class name.</summary>
    public required string Source { get; init; }

    /// <summary>Severity that controls UI prominence and persistence policy.</summary>
    public required NotificationSeverity Severity { get; init; }

    /// <summary>Short user-facing label for the notification.</summary>
    public required string Title { get; init; }

    /// <summary>Full user-facing description of the notification.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// Optional error context. When non-null, <see cref="Results.ErrorCode"/> is used as the
    /// deduplication key together with <see cref="Source"/>.
    /// </summary>
    public ErrorContext? Error { get; init; }

    /// <summary>UTC timestamp recorded at publication; 1:1 with <c>BackgroundFailures.OccurredUtc</c>.</summary>
    public DateTime OccurredUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// <see langword="true"/> when the user must take explicit action to resolve the underlying problem.
    /// Consumed by Phase 6 UI handlers; not persisted to <c>BackgroundFailures</c>.
    /// </summary>
    public bool RequiresUserAction { get; init; }
}
