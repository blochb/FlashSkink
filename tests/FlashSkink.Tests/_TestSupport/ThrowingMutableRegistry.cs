using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers;

namespace FlashSkink.Tests._TestSupport;

/// <summary>
/// <see cref="IMutableProviderRegistry"/> whose <see cref="Register"/> throws. Used to
/// deterministically exercise <c>AddTailAsync</c>'s failure-disposal path: the adapter is
/// constructed, the brain row is persisted, then registration throws — so the constructed
/// adapter must be disposed before the call returns a failed result. The read-side methods
/// behave like an empty registry so the upload orchestrator idles harmlessly.
/// </summary>
internal sealed class ThrowingMutableRegistry : IProviderRegistry, IMutableProviderRegistry
{
    public void Register(string providerId, IStorageProvider provider)
        => throw new InvalidOperationException("ThrowingMutableRegistry.Register always throws.");

    public bool Remove(string providerId) => false;

    public ValueTask<Result<IStorageProvider>> GetAsync(string providerId, CancellationToken ct)
        => ValueTask.FromResult(Result<IStorageProvider>.Fail(
            ErrorCode.ProviderUnreachable, $"Provider '{providerId}' is not registered."));

    public ValueTask<Result<IReadOnlyList<string>>> ListActiveProviderIdsAsync(CancellationToken ct)
        => ValueTask.FromResult(Result<IReadOnlyList<string>>.Ok([]));
}
