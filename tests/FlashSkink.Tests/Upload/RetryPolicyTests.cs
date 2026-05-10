using FlashSkink.Core.Upload;
using Xunit;

namespace FlashSkink.Tests.Upload;

public sealed class RetryPolicyTests
{
    private readonly RetryPolicy _policy = new();

    // ── NextRangeAttempt ──────────────────────────────────────────────────────

    [Fact]
    public void NextRangeAttempt_FirstAttempt_Returns1SecondWait()
    {
        RetryDecision decision = _policy.NextRangeAttempt(1);

        Assert.Equal(RetryOutcome.Retry, decision.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(1), decision.Delay);
    }

    [Fact]
    public void NextRangeAttempt_SecondAttempt_Returns4SecondWait()
    {
        RetryDecision decision = _policy.NextRangeAttempt(2);

        Assert.Equal(RetryOutcome.Retry, decision.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(4), decision.Delay);
    }

    [Fact]
    public void NextRangeAttempt_ThirdAttempt_Returns16SecondWait()
    {
        RetryDecision decision = _policy.NextRangeAttempt(3);

        Assert.Equal(RetryOutcome.Retry, decision.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(16), decision.Delay);
    }

    [Fact]
    public void NextRangeAttempt_FourthAttempt_ReturnsEscalate()
    {
        RetryDecision decision = _policy.NextRangeAttempt(4);

        Assert.Equal(RetryOutcome.EscalateCycle, decision.Outcome);
    }

    [Fact]
    public void NextRangeAttempt_HighAttemptNumber_ReturnsEscalate()
    {
        RetryDecision decision = _policy.NextRangeAttempt(100);

        Assert.Equal(RetryOutcome.EscalateCycle, decision.Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void NextRangeAttempt_ZeroOrNegative_TreatedAsFirstAttempt(int attemptNumber)
    {
        RetryDecision decision = _policy.NextRangeAttempt(attemptNumber);

        Assert.Equal(RetryOutcome.Retry, decision.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(1), decision.Delay);
    }

    // ── NextCycleAttempt ──────────────────────────────────────────────────────

    [Fact]
    public void NextCycleAttempt_FirstCycle_Returns5MinuteWait()
    {
        RetryDecision decision = _policy.NextCycleAttempt(1);

        Assert.Equal(RetryOutcome.Retry, decision.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(5), decision.Delay);
    }

    [Fact]
    public void NextCycleAttempt_SecondCycle_Returns30MinuteWait()
    {
        RetryDecision decision = _policy.NextCycleAttempt(2);

        Assert.Equal(RetryOutcome.Retry, decision.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(30), decision.Delay);
    }

    [Fact]
    public void NextCycleAttempt_ThirdCycle_Returns2HourWait()
    {
        RetryDecision decision = _policy.NextCycleAttempt(3);

        Assert.Equal(RetryOutcome.Retry, decision.Outcome);
        Assert.Equal(TimeSpan.FromHours(2), decision.Delay);
    }

    [Fact]
    public void NextCycleAttempt_FourthCycle_Returns12HourWait()
    {
        RetryDecision decision = _policy.NextCycleAttempt(4);

        Assert.Equal(RetryOutcome.Retry, decision.Outcome);
        Assert.Equal(TimeSpan.FromHours(12), decision.Delay);
    }

    [Fact]
    public void NextCycleAttempt_FifthCycle_ReturnsFail()
    {
        RetryDecision decision = _policy.NextCycleAttempt(5);

        Assert.Equal(RetryOutcome.MarkFailed, decision.Outcome);
    }

    [Fact]
    public void NextCycleAttempt_HighCycleNumber_ReturnsFail()
    {
        RetryDecision decision = _policy.NextCycleAttempt(50);

        Assert.Equal(RetryOutcome.MarkFailed, decision.Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void NextCycleAttempt_ZeroOrNegative_TreatedAsFirstCycle(int cycleNumber)
    {
        RetryDecision decision = _policy.NextCycleAttempt(cycleNumber);

        Assert.Equal(RetryOutcome.Retry, decision.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(5), decision.Delay);
    }

    // ── RetryPolicy.Default singleton ─────────────────────────────────────────

    [Fact]
    public void Default_ReturnsSingletonInstance()
    {
        Assert.Same(RetryPolicy.Default, RetryPolicy.Default);
    }

    // ── RetryDecision value-type equality ─────────────────────────────────────

    [Fact]
    public void Decisions_AreValueTypes_Equality()
    {
        RetryDecision a = RetryDecision.Wait(TimeSpan.FromSeconds(1));
        RetryDecision b = RetryDecision.Wait(TimeSpan.FromSeconds(1));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Decisions_DifferentDelays_NotEqual()
    {
        RetryDecision a = RetryDecision.Wait(TimeSpan.FromSeconds(1));
        RetryDecision b = RetryDecision.Wait(TimeSpan.FromSeconds(4));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void EscalateDecision_HasZeroDelay()
    {
        RetryDecision d = RetryDecision.Escalate();

        Assert.Equal(RetryOutcome.EscalateCycle, d.Outcome);
        Assert.Equal(TimeSpan.Zero, d.Delay);
    }

    [Fact]
    public void FailDecision_HasZeroDelay()
    {
        RetryDecision d = RetryDecision.Fail();

        Assert.Equal(RetryOutcome.MarkFailed, d.Outcome);
        Assert.Equal(TimeSpan.Zero, d.Delay);
    }
}
