using FlashSkink.Core.Engine;
using Xunit;

namespace FlashSkink.Tests.Providers;

public sealed class AlwaysOnlineNetworkMonitorTests
{
    private readonly AlwaysOnlineNetworkMonitor _sut = new();

    [Fact]
    public void IsAvailable_AlwaysReturnsTrue()
    {
        Assert.True(_sut.IsAvailable);
        Assert.True(_sut.IsAvailable); // idempotent
    }

    [Fact]
    public void Subscribing_NeverInvokesHandler()
    {
        var invoked = false;
        _sut.AvailabilityChanged += (_, _) => invoked = true;

        // No way to trigger the event from outside; assert it was never raised.
        Assert.False(invoked);
    }
}
