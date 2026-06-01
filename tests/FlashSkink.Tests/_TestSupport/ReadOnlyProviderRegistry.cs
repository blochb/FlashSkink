using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Tests._TestSupport;

/// <summary>
/// An <see cref="IProviderRegistry"/> that deliberately does <strong>not</strong> implement the
/// internal <c>IMutableProviderRegistry</c> seam, so <c>AddTailAsync</c>/<c>RemoveTailAsync</c>
/// reject it with <see cref="ErrorCode.InvalidArgument"/>. Behaves as an empty registry.
/// </summary>
internal sealed class ReadOnlyProviderRegistry : IProviderRegistry
{
    public ValueTask<Result<IStorageProvider>> GetAsync(string providerId, CancellationToken ct)
        => ValueTask.FromResult(Result<IStorageProvider>.Fail(
            ErrorCode.ProviderUnreachable, $"Provider '{providerId}' is not registered."));

    public ValueTask<Result<IReadOnlyList<string>>> ListActiveProviderIdsAsync(CancellationToken ct)
        => ValueTask.FromResult(Result<IReadOnlyList<string>>.Ok([]));
}
