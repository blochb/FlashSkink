using FlashSkink.Core.Abstractions.Providers;

namespace FlashSkink.Tests._TestSupport;

/// <summary>
/// Test double for <see cref="INetworkAvailabilityMonitor"/>. <see cref="SetAvailable"/> raises
/// <see cref="AvailabilityChanged"/> synchronously when the value changes; unchanged sets are
/// no-ops. Initial state is online (matches the §3.1 production
/// <c>AlwaysOnlineNetworkMonitor</c>).
/// </summary>
internal sealed class TestNetworkAvailabilityMonitor : INetworkAvailabilityMonitor
{
    private bool _isAvailable = true;

    public bool IsAvailable => _isAvailable;

    public event EventHandler<bool>? AvailabilityChanged;

    public void SetAvailable(bool value)
    {
        if (_isAvailable == value)
        {
            return;
        }

        _isAvailable = value;
        AvailabilityChanged?.Invoke(this, value);
    }
}
