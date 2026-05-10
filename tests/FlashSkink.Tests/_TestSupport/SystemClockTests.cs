using FlashSkink.Core.Abstractions.Time;
using Xunit;

namespace FlashSkink.Tests._TestSupport;

public sealed class SystemClockTests
{
    // ── IClock contract: negative/zero delay completes immediately ────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600000)] // -1 hour in ms
    public async Task Delay_ZeroOrNegative_CompletesImmediately(int milliseconds)
    {
        var clock = SystemClock.Instance;

        await clock.Delay(TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None);
        // No exception = pass; Task.Delay would have thrown ArgumentOutOfRangeException without the guard.
    }

    [Fact]
    public void UtcNow_ReturnsUtcKind()
    {
        var clock = SystemClock.Instance;

        Assert.Equal(DateTimeKind.Utc, clock.UtcNow.Kind);
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        Assert.Same(SystemClock.Instance, SystemClock.Instance);
    }
}
