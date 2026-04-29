namespace FlashSkink.Core.Abstractions.Models;

/// <summary>
/// One row from the <c>ActivityLog</c> brain table. Provides a user-facing audit trail of
/// file and tail operations. Append-only; rows are never updated after insertion.
/// </summary>
public sealed record ActivityLogEntry
{
    /// <summary>Primary key — a <see cref="Guid"/>-formatted string.</summary>
    public required string EntryId { get; init; }

    /// <summary>When the activity occurred (UTC).</summary>
    public required DateTime OccurredUtc { get; init; }

    /// <summary>
    /// Activity category. One of: <c>WRITE</c>, <c>DELETE</c>, <c>RESTORE</c>,
    /// <c>TAIL_ADDED</c>, <c>TAIL_REMOVED</c>, <c>RECOVERY</c>, <c>EXPORT</c>, <c>VERIFY</c>.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>Short human-readable description shown in the activity list.</summary>
    public required string Summary { get; init; }

    /// <summary>Optional JSON blob with structured detail; <see langword="null"/> when not applicable.</summary>
    public string? Detail { get; init; }
}
