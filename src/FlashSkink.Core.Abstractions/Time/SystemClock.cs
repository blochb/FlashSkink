namespace FlashSkink.Core.Abstractions.Time;

/// <summary>
/// Production <see cref="IClock"/> backed by the OS clock and <see cref="Task.Delay"/>.
/// Stateless — safe to use as a singleton.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <summary>Shared singleton. Prefer injecting <see cref="IClock"/> via constructor; use this as a default.</summary>
    public static SystemClock Instance { get; } = new();

    /// <summary>Initialises a new <see cref="SystemClock"/>.</summary>
    public SystemClock() { }

    /// <inheritdoc/>
    public DateTime UtcNow => DateTime.UtcNow;

    /// <inheritdoc/>
    public ValueTask Delay(TimeSpan delay, CancellationToken ct) =>
        new(Task.Delay(delay, ct));
}
