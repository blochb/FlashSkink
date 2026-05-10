namespace FlashSkink.Core.Upload;

/// <summary>
/// Pure encoding of blueprint §21.1's two retry ladders for provider upload failures.
/// Stateless — safe to share as a singleton via <see cref="Default"/>.
/// </summary>
/// <remarks>
/// This class encodes the per-range and per-cycle backoff numbers and returns a
/// <see cref="RetryDecision"/> for each attempt. It never waits, logs, or performs I/O —
/// the caller is responsible for awaiting <see cref="FlashSkink.Core.Abstractions.Time.IClock.Delay"/>
/// with the returned <see cref="RetryDecision.Delay"/>.
/// <para>
/// Health-status modulation (§22.1: longer backoff when <c>Degraded</c>) is a Phase 5 concern.
/// The orchestrator decides whether to enter the retry loop based on health; this class only
/// encodes the §21.1 progression once the loop is entered.
/// </para>
/// </remarks>
public sealed class RetryPolicy
{
    // §21.1 in-range ladder: 1 s / 4 s / 16 s — attempts 1 / 2 / 3; escalate on attempt 4+.
    private static readonly TimeSpan[] RangeDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(16),
    ];

    // §21.1 cycle ladder: 5 min / 30 min / 2 h / 12 h — cycles 1 / 2 / 3 / 4; mark failed on cycle 5+.
    private static readonly TimeSpan[] CycleDelays =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(12),
    ];

    /// <summary>Shared default singleton. The policy is stateless; a single instance is sufficient.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>Initialises a new <see cref="RetryPolicy"/>.</summary>
    public RetryPolicy() { }

    /// <summary>
    /// Returns the action to take after the <paramref name="rangeAttemptNumber"/>-th attempt at a
    /// single upload range failed.
    /// </summary>
    /// <param name="rangeAttemptNumber">
    /// 1-based attempt counter: 1 means this was the first attempt at the range.
    /// Values less than 1 are clamped to 1 (treated as the first attempt).
    /// </param>
    /// <returns>
    /// <list type="bullet">
    /// <item><description>Attempts 1/2/3 → <see cref="RetryDecision.Wait"/> with §21.1 delay (1 s / 4 s / 16 s).</description></item>
    /// <item><description>Attempt 4 and above → <see cref="RetryDecision.Escalate"/>.</description></item>
    /// </list>
    /// </returns>
    /// <remarks>Never throws (Principle 1 sanctioned pure-function exception: total function, no I/O, no allocations).</remarks>
    public RetryDecision NextRangeAttempt(int rangeAttemptNumber)
    {
        int idx = Math.Max(0, rangeAttemptNumber - 1);
        if (idx < RangeDelays.Length)
        {
            return RetryDecision.Wait(RangeDelays[idx]);
        }

        return RetryDecision.Escalate();
    }

    /// <summary>
    /// Returns the action to take after the <paramref name="cycleNumber"/>-th cycle for a
    /// <c>TailUploads</c> row failed (i.e. the in-range ladder fully escalated for the
    /// <paramref name="cycleNumber"/>-th time on this row).
    /// </summary>
    /// <param name="cycleNumber">
    /// 1-based cycle counter: 1 means the row's first cycle just failed.
    /// Values less than 1 are clamped to 1.
    /// </param>
    /// <returns>
    /// <list type="bullet">
    /// <item><description>Cycles 1/2/3/4 → <see cref="RetryDecision.Wait"/> with §21.1 delay (5 min / 30 min / 2 h / 12 h).</description></item>
    /// <item><description>Cycle 5 and above → <see cref="RetryDecision.Fail"/>.</description></item>
    /// </list>
    /// </returns>
    /// <remarks>Never throws (Principle 1 sanctioned pure-function exception: total function, no I/O, no allocations).</remarks>
    public RetryDecision NextCycleAttempt(int cycleNumber)
    {
        int idx = Math.Max(0, cycleNumber - 1);
        if (idx < CycleDelays.Length)
        {
            return RetryDecision.Wait(CycleDelays[idx]);
        }

        return RetryDecision.Fail();
    }
}
