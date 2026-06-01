using FlashSkink.Core.Abstractions.Providers;

namespace FlashSkink.Core.Providers;

/// <summary>
/// Write-side seam over a provider registry, used by <c>FlashSkinkVolume.AddTailAsync</c> /
/// <c>RemoveTailAsync</c> to mutate the live adapter cache in lock-step with the brain
/// <c>Providers</c> rows. Kept separate from <see cref="IProviderRegistry"/> — which is the
/// read-only contract consumed by the upload orchestrator and brain-mirror (Principle 23: the
/// frozen registry seam stays read-only). Implemented by both
/// <see cref="InMemoryProviderRegistry"/> (tests) and <see cref="BrainBackedProviderRegistry"/>
/// (production).
/// </summary>
internal interface IMutableProviderRegistry
{
    /// <summary>
    /// Registers <paramref name="provider"/> under <paramref name="providerId"/>, replacing any
    /// existing entry with the same id. The registry takes ownership of the adapter's lifetime.
    /// </summary>
    void Register(string providerId, IStorageProvider provider);

    /// <summary>
    /// Removes (and disposes) the adapter registered under <paramref name="providerId"/>. Returns
    /// <see langword="true"/> when an adapter was removed, <see langword="false"/> otherwise.
    /// </summary>
    bool Remove(string providerId);
}
