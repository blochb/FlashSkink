namespace FlashSkink.Core.Abstractions.Time;

/// <summary>
/// Synchronous time source plus a cancellable delay primitive. Consumers inject this instead of
/// <see cref="DateTime.UtcNow"/> and <see cref="System.Threading.Tasks.Task.Delay(TimeSpan, System.Threading.CancellationToken)"/>
/// so that tests can advance time deterministically via <c>FakeClock</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UtcNow"/> is a sanctioned non-<c>Result</c> property under Principle 1 (pure-function
/// carve-out): it reads a clock register and cannot fail in any meaningful sense.
/// </para>
/// <para>
/// <see cref="Delay"/> returns <see cref="ValueTask"/> rather than a <c>Result</c>-wrapped task.
/// Cancellation is the only failure mode, and <see cref="System.OperationCanceledException"/> is the
/// universal in-band carrier for it across <c>await</c> boundaries (identical to the BCL's own
/// <see cref="System.Threading.Tasks.Task.Delay(TimeSpan, System.Threading.CancellationToken)"/>).
/// Callers map the exception to <c>Result.Fail(Cancelled)</c> at their own public boundary,
/// exactly as Principle 14 prescribes for any cancellable awaitable.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>Current UTC time.</summary>
    DateTime UtcNow { get; }

    /// <summary>
    /// Returns a task that completes after <paramref name="delay"/> elapses or
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="delay">How long to wait. Zero or negative values complete immediately.</param>
    /// <param name="ct">Cancellation token. A cancelled token causes the returned
    /// <see cref="ValueTask"/> to complete with <see cref="System.OperationCanceledException"/>.</param>
    ValueTask Delay(TimeSpan delay, CancellationToken ct);
}
