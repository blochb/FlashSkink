namespace FlashSkink.Core.Upload;

/// <summary>
/// The action a caller should take after consulting <see cref="RetryPolicy"/>.
/// </summary>
public enum RetryOutcome
{
    /// <summary>Wait <see cref="RetryDecision.Delay"/> then retry the same operation from the same offset.</summary>
    Retry = 0,

    /// <summary>
    /// In-range retry budget exhausted. Record a row-level failure and consult
    /// <see cref="RetryPolicy.NextCycleAttempt"/> to enter the cycle-level backoff ladder.
    /// </summary>
    EscalateCycle = 1,

    /// <summary>Cycle retry budget exhausted. Mark the <c>TailUploads</c> row <c>FAILED</c> and stop retrying.</summary>
    MarkFailed = 2,
}
