using FlashSkink.Core.Abstractions.Providers;

namespace FlashSkink.Core.Engine;

/// <summary>
/// V1 default implementation of <see cref="INetworkAvailabilityMonitor"/>. Reports the network
/// as always available and never raises <see cref="AvailabilityChanged"/>. Blueprint §22.2.
/// Phase 5 replaces this with the real OS-mediated <c>NetworkAvailabilityMonitor</c> that reads
/// <c>NetworkInterface.GetIsNetworkAvailable()</c> and subscribes to
/// <c>NetworkChange.NetworkAvailabilityChanged</c>.
/// </summary>
public sealed class AlwaysOnlineNetworkMonitor : INetworkAvailabilityMonitor
{
    /// <inheritdoc/>
    /// <remarks>Always returns <see langword="true"/>. Phase 5 replaces with a real OS signal.</remarks>
    public bool IsAvailable => true;

    /// <inheritdoc/>
    /// <remarks>Never raised by this implementation; add/remove are no-ops.</remarks>
    public event EventHandler<bool>? AvailabilityChanged
    {
        add { }
        remove { }
    }
}
