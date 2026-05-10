namespace FlashSkink.Core.Abstractions.Providers;

/// <summary>
/// Passive, synchronous online/offline signal consulted by background loops before scheduling
/// upload or health-check work. Blueprint §22.2.
/// </summary>
/// <remarks>
/// <para>
/// The signal is OS-mediated and passive — no packets are sent (preserves the no-telemetry
/// discipline, Principle 32). Phase 3 ships <c>AlwaysOnlineNetworkMonitor</c> as the default
/// V1 stub. Phase 5 wires the real OS-mediated implementation (<c>NetworkAvailabilityMonitor</c>)
/// that reads <c>NetworkInterface.GetIsNetworkAvailable()</c> at startup and subscribes to
/// <c>NetworkChange.NetworkAvailabilityChanged</c> for transition-driven wakeups.
/// </para>
/// <para>
/// Callers snapshot <see cref="IsAvailable"/> synchronously; the <see cref="AvailabilityChanged"/>
/// event is for transition-driven wakeups where a change from offline to online should trigger
/// an immediate retry rather than waiting for the next idle-poll interval.
/// </para>
/// </remarks>
public interface INetworkAvailabilityMonitor
{
    /// <summary>
    /// <see langword="true"/> when the network is considered available; <see langword="false"/>
    /// when all interfaces are down (wi-fi off, ethernet unplugged, airplane mode, VPN dropped).
    /// Does <em>not</em> detect captive portals, ISP outages, or DNS failures.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Raised when <see cref="IsAvailable"/> transitions. The event argument is the new value.
    /// Never raised by <c>AlwaysOnlineNetworkMonitor</c>.
    /// </summary>
    event EventHandler<bool>? AvailabilityChanged;
}
