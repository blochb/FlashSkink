namespace FlashSkink.Core.Abstractions.Models;

/// <summary>
/// One row from the <c>BackgroundFailures</c> brain table. Background services persist
/// failures here so they survive process restart and surface to the user on next launch
/// (Principle 24).
/// </summary>
public sealed record BackgroundFailure
{
    /// <summary>Primary key — a <see cref="Guid"/>-formatted string.</summary>
    public required string FailureId { get; init; }

    /// <summary>When the failure occurred (UTC).</summary>
    public required DateTime OccurredUtc { get; init; }

    /// <summary>Name of the background service that recorded this failure.</summary>
    public required string Source { get; init; }

    /// <summary>String representation of the <c>ErrorCode</c> enum value.</summary>
    public required string ErrorCode { get; init; }

    /// <summary>Human-readable failure description.</summary>
    public required string Message { get; init; }

    /// <summary>Optional JSON blob with additional structured context; <see langword="null"/> when not applicable.</summary>
    public string? Metadata { get; init; }

    /// <summary><see langword="true"/> when the user has dismissed this failure from the UI.</summary>
    public required bool Acknowledged { get; init; }
}
