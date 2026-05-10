using Xunit;

namespace FlashSkink.Tests._TestSupport;

public sealed class FakeClockTests
{
    private static readonly DateTime StartTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── UtcNow ────────────────────────────────────────────────────────────────

    [Fact]
    public void UtcNow_Initially_ReturnsConstructorTime()
    {
        using var clock = new FakeClock(StartTime);

        Assert.Equal(StartTime, clock.UtcNow);
    }

    [Fact]
    public void Advance_AdvancesUtcNow()
    {
        using var clock = new FakeClock(StartTime);

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(StartTime + TimeSpan.FromMinutes(5), clock.UtcNow);
    }

    // ── Delay — immediate completion ──────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600000)] // -1 hour in ms
    public async Task Delay_ZeroOrNegative_CompletesImmediately(int milliseconds)
    {
        using var clock = new FakeClock(StartTime);

        await clock.Delay(TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None);
        // No exception = pass
    }

    [Fact]
    public async Task Delay_PreCancelledToken_ReturnsFaulted()
    {
        using var clock = new FakeClock(StartTime);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => clock.Delay(TimeSpan.FromSeconds(10), cts.Token).AsTask());
    }

    // ── Delay — pending without advance ──────────────────────────────────────

    [Fact]
    public void Delay_PositiveDelay_DoesNotCompleteWithoutAdvance()
    {
        using var clock = new FakeClock(StartTime);

        ValueTask delayTask = clock.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.False(delayTask.IsCompleted);
    }

    [Fact]
    public void Delay_AdvanceShortOfDeadline_DoesNotComplete()
    {
        using var clock = new FakeClock(StartTime);
        ValueTask delayTask = clock.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.False(delayTask.IsCompleted);
    }

    // ── Delay — completion via Advance ────────────────────────────────────────

    [Fact]
    public async Task Delay_AdvancePastDeadline_Completes()
    {
        using var clock = new FakeClock(StartTime);
        Task delayTask = clock.Delay(TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();

        clock.Advance(TimeSpan.FromSeconds(11));

        await delayTask;
    }

    [Fact]
    public async Task Delay_AdvanceExactlyToDeadline_Completes()
    {
        using var clock = new FakeClock(StartTime);
        Task delayTask = clock.Delay(TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();

        clock.Advance(TimeSpan.FromSeconds(10));

        await delayTask;
    }

    [Fact]
    public async Task Delay_MultiplePending_AllPastDeadlineComplete_OthersStayPending()
    {
        using var clock = new FakeClock(StartTime);
        Task short1 = clock.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask();
        Task short2 = clock.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask();
        Task longTask = clock.Delay(TimeSpan.FromSeconds(20), CancellationToken.None).AsTask();

        clock.Advance(TimeSpan.FromSeconds(10));

        await short1;
        await short2;
        Assert.False(longTask.IsCompleted);
    }

    // ── Delay — cancellation mid-wait ─────────────────────────────────────────

    [Fact]
    public async Task Delay_TokenCancelledMidWait_TaskFaultsWithOCE()
    {
        using var clock = new FakeClock(StartTime);
        using var cts = new CancellationTokenSource();
        Task delayTask = clock.Delay(TimeSpan.FromSeconds(10), cts.Token).AsTask();

        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => delayTask);
    }

    [Fact]
    public async Task Delay_CancelledTokenAfterAdvanceCompleted_NoEffect()
    {
        // Advance completes the delay first; subsequent cancel should not fault the task.
        using var clock = new FakeClock(StartTime);
        using var cts = new CancellationTokenSource();
        Task delayTask = clock.Delay(TimeSpan.FromSeconds(5), cts.Token).AsTask();

        clock.Advance(TimeSpan.FromSeconds(10));
        await delayTask; // must succeed

        cts.Cancel(); // should be a no-op now
        Assert.True(delayTask.IsCompletedSuccessfully);
    }

    // ── PendingDelayCount — cancellation ──────────────────────────────────────

    [Fact]
    public async Task PendingDelayCount_DecrementsAfterCancellation()
    {
        using var clock = new FakeClock(StartTime);
        using var cts = new CancellationTokenSource();

        Task t = clock.Delay(TimeSpan.FromSeconds(10), cts.Token).AsTask();
        Assert.Equal(1, clock.PendingDelayCount);

        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => t);

        Assert.Equal(0, clock.PendingDelayCount);
    }

    // ── PendingDelayCount — advance ───────────────────────────────────────────

    [Fact]
    public void PendingDelayCount_ReflectsActiveDelays()
    {
        using var clock = new FakeClock(StartTime);
        Assert.Equal(0, clock.PendingDelayCount);

        ValueTask t1 = clock.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(1, clock.PendingDelayCount);

        ValueTask t2 = clock.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(2, clock.PendingDelayCount);

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(1, clock.PendingDelayCount);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, clock.PendingDelayCount);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_CancelsAllPending()
    {
        var clock = new FakeClock(StartTime);
        Task t1 = clock.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).AsTask();
        Task t2 = clock.Delay(TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();

        clock.Dispose();

        await Assert.ThrowsAsync<TaskCanceledException>(() => t1);
        await Assert.ThrowsAsync<TaskCanceledException>(() => t2);
    }

    // ── Thread safety ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentAdvanceAndDelay_NoDeadlock()
    {
        using var clock = new FakeClock(StartTime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // wall-clock escape hatch

        var tasks = new List<Task>();

        // 50 tasks that schedule 1-second delays
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(() =>
                clock.Delay(TimeSpan.FromSeconds(1), cts.Token).AsTask(), cts.Token));
        }

        // One task that advances the clock repeatedly
        Task advancer = Task.Run(() =>
        {
            for (int i = 0; i < 10; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(200));
            }
        }, cts.Token);

        await advancer;
        // All delays should have been completed or cancelled — either is fine; we just need no deadlock.
        await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }, TaskContinuationOptions.None)));
    }
}
