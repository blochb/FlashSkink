namespace FlashSkink.Core.Abstractions.Notifications;

/// <summary>
/// Severity level for one notification; controls UI prominence and persistence policy.
/// <see cref="Error"/> and <see cref="Critical"/> are persisted by the persistence handler;
/// <see cref="Info"/> and <see cref="Warning"/> are not.
/// </summary>
public enum NotificationSeverity
{
    /// <summary>Informational; no action required.</summary>
    Info = 0,

    /// <summary>Degraded state; worth surfacing but not blocking.</summary>
    Warning = 1,

    /// <summary>A background service failed; persisted so the user sees it on next launch.</summary>
    Error = 2,

    /// <summary>A critical failure requiring immediate attention; persisted.</summary>
    Critical = 3,
}
