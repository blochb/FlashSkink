namespace FlashSkink.Core.Abstractions.Models;

/// <summary>
/// EventArgs raised when a tail provider's health state changes; carries the provider ID
/// and new health snapshot.
/// </summary>
public sealed class TailStatusChangedEventArgs : EventArgs
{
    /// <summary>The provider ID whose health changed.</summary>
    public string ProviderId { get; }

    /// <summary>The new health snapshot for this provider.</summary>
    public ProviderHealth Health { get; }

    /// <summary>Initialises a new <see cref="TailStatusChangedEventArgs"/>.</summary>
    public TailStatusChangedEventArgs(string providerId, ProviderHealth health)
    {
        ProviderId = providerId;
        Health = health;
    }
}
