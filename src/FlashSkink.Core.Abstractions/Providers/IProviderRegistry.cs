using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Abstractions.Providers;

/// <summary>
/// Orchestrator-facing lookup for live <see cref="IStorageProvider"/> instances. Blueprint §3.1
/// cross-cutting decision 1.
/// </summary>
/// <remarks>
/// <para>
/// The upload orchestrator (<c>UploadQueueService</c>, §3.4) does not see a static provider list
/// at construction; it asks this registry on every scheduling tick and wakeup. Phase 3 ships
/// <c>InMemoryProviderRegistry</c>; Phase 4 ships <c>BrainBackedProviderRegistry</c> for cloud
/// providers that require decrypted OAuth refresh tokens.
/// </para>
/// </remarks>
public interface IProviderRegistry
{
    /// <summary>
    /// Returns the live <see cref="IStorageProvider"/> instance for the given
    /// <paramref name="providerId"/>, or <see cref="ErrorCode.ProviderUnreachable"/> when no
    /// provider with that id is currently registered.
    /// </summary>
    ValueTask<Result<IStorageProvider>> GetAsync(string providerId, CancellationToken ct);

    /// <summary>
    /// Returns the provider IDs of all currently registered (active) providers. The orchestrator
    /// calls this on every tick to reconcile the set of running worker tasks.
    /// </summary>
    ValueTask<Result<IReadOnlyList<string>>> ListActiveProviderIdsAsync(CancellationToken ct);
}
