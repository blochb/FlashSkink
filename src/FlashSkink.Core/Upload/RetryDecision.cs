namespace FlashSkink.Core.Upload;

/// <summary>
/// The outcome of a <see cref="RetryPolicy"/> consultation: what action to take and, when
/// <see cref="Outcome"/> is <see cref="RetryOutcome.Retry"/>, how long to wait first.
/// Value type — allocation-free on the hot retry path.
/// </summary>
public readonly record struct RetryDecision(RetryOutcome Outcome, TimeSpan Delay)
{
    /// <summary>Creates a <see cref="RetryOutcome.Retry"/> decision with the given wait duration.</summary>
    public static RetryDecision Wait(TimeSpan delay) => new(RetryOutcome.Retry, delay);

    /// <summary>
    /// Creates an <see cref="RetryOutcome.EscalateCycle"/> decision.
    /// <see cref="Delay"/> is <see cref="TimeSpan.Zero"/>; the cycle-level delay is determined by
    /// <see cref="RetryPolicy.NextCycleAttempt"/>.
    /// </summary>
    public static RetryDecision Escalate() => new(RetryOutcome.EscalateCycle, TimeSpan.Zero);

    /// <summary>
    /// Creates a <see cref="RetryOutcome.MarkFailed"/> decision.
    /// <see cref="Delay"/> is <see cref="TimeSpan.Zero"/>; no further retries will occur.
    /// </summary>
    public static RetryDecision Fail() => new(RetryOutcome.MarkFailed, TimeSpan.Zero);
}
