using FlashSkink.Core.Upload;
using Xunit;

namespace FlashSkink.Tests.Upload;

public sealed class UploadWakeupSignalTests
{
    [Fact]
    public async Task Pulse_BeforeWait_WaitCompletesImmediately()
    {
        var signal = new UploadWakeupSignal();
        signal.Pulse();

        var task = signal.WaitAsync(CancellationToken.None).AsTask();
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(task, completed);
        await task;
    }

    [Fact]
    public async Task Pulse_DuringWait_WaitCompletes()
    {
        var signal = new UploadWakeupSignal();
        var task = signal.WaitAsync(CancellationToken.None).AsTask();

        Assert.False(task.IsCompleted);
        signal.Pulse();

        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(task, completed);
        await task;
    }

    [Fact]
    public async Task PulseMultiple_OnlyOneWaitCompletes()
    {
        var signal = new UploadWakeupSignal();
        signal.Pulse();
        signal.Pulse();
        signal.Pulse();

        // First wait completes from the buffered token.
        await signal.WaitAsync(CancellationToken.None);

        // Second wait must block — the extra pulses were dropped (DropWrite).
        var second = signal.WaitAsync(CancellationToken.None).AsTask();
        var raceWinner = await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(second, raceWinner);

        // Releasing it: one more Pulse.
        signal.Pulse();
        await second;
    }

    [Fact]
    public async Task Wait_TokenPreCancelled_ThrowsOperationCanceled()
    {
        var signal = new UploadWakeupSignal();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await signal.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task Wait_TokenCancelledMidWait_ThrowsOperationCanceled()
    {
        var signal = new UploadWakeupSignal();
        using var cts = new CancellationTokenSource();
        var task = signal.WaitAsync(cts.Token).AsTask();

        Assert.False(task.IsCompleted);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task Complete_PendingWait_ReturnsWithoutThrow()
    {
        var signal = new UploadWakeupSignal();
        var task = signal.WaitAsync(CancellationToken.None).AsTask();

        Assert.False(task.IsCompleted);
        signal.Complete();

        // WaitAsync swallows ChannelClosedException and returns normally.
        await task;
    }

    [Fact]
    public void Pulse_AfterComplete_NoOp()
    {
        var signal = new UploadWakeupSignal();
        signal.Complete();

        // Should not throw.
        signal.Pulse();
        signal.Pulse();
    }

    [Fact]
    public async Task MultipleWaiters_OnePulse_OneCompletes()
    {
        var signal = new UploadWakeupSignal();
        var waiter1 = signal.WaitAsync(CancellationToken.None).AsTask();
        var waiter2 = signal.WaitAsync(CancellationToken.None).AsTask();

        signal.Pulse();

        // One waiter must complete within the budget; the other must remain pending.
        var winner = await Task.WhenAny(waiter1, waiter2);
        Assert.True(winner == waiter1 || winner == waiter2);

        var loser = winner == waiter1 ? waiter2 : waiter1;
        var race = await Task.WhenAny(loser, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(loser, race);

        // Release the second waiter.
        signal.Pulse();
        await loser;
    }
}
