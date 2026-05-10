using System.Collections.Concurrent;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers;

/// <summary>
/// In-process <see cref="IProviderRegistry"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Populated by <c>FlashSkinkVolume.RegisterTailAsync</c> (§3.6) and by tests. Phase 4's
/// <c>BrainBackedProviderRegistry</c> is an alternative implementation for cloud providers
/// that require decrypted OAuth refresh tokens at construction. Blueprint §3.1 cross-cutting
/// decision 1.
/// </summary>
/// <remarks>
/// Holds <strong>strong</strong> references to <see cref="IStorageProvider"/> instances. Tests are
/// responsible for disposing or replacing providers. Phase 4's registry owns a tighter lifecycle
/// for cloud adapters that hold network connections.
/// </remarks>
public sealed class InMemoryProviderRegistry : IProviderRegistry
{
    private readonly ConcurrentDictionary<string, IStorageProvider> _providers = new();
    private readonly ILogger<InMemoryProviderRegistry> _logger;

    /// <summary>Creates an <see cref="InMemoryProviderRegistry"/>.</summary>
    public InMemoryProviderRegistry(ILogger<InMemoryProviderRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers <paramref name="provider"/> under <paramref name="providerId"/>, overwriting
    /// any previously registered provider with the same id.
    /// </summary>
    public void Register(string providerId, IStorageProvider provider)
    {
        _providers[providerId] = provider;
        _logger.LogInformation("Provider registered: {ProviderId} ({DisplayName})", providerId, provider.DisplayName);
    }

    /// <summary>
    /// Removes the provider registered under <paramref name="providerId"/>.
    /// Returns <see langword="true"/> when a provider was removed, <see langword="false"/> when
    /// no provider with that id was found.
    /// </summary>
    public bool Remove(string providerId)
    {
        var removed = _providers.TryRemove(providerId, out _);
        if (removed)
        {
            _logger.LogInformation("Provider removed: {ProviderId}", providerId);
        }

        return removed;
    }

    /// <inheritdoc/>
    public ValueTask<Result<IStorageProvider>> GetAsync(string providerId, CancellationToken ct)
    {
        if (_providers.TryGetValue(providerId, out var provider))
        {
            return ValueTask.FromResult(Result<IStorageProvider>.Ok(provider));
        }

        return ValueTask.FromResult(
            Result<IStorageProvider>.Fail(
                ErrorCode.ProviderUnreachable,
                $"Provider '{providerId}' is not registered."));
    }

    /// <inheritdoc/>
    public ValueTask<Result<IReadOnlyList<string>>> ListActiveProviderIdsAsync(CancellationToken ct)
    {
        IReadOnlyList<string> ids = _providers.Keys.ToArray();
        return ValueTask.FromResult(Result<IReadOnlyList<string>>.Ok(ids));
    }
}
