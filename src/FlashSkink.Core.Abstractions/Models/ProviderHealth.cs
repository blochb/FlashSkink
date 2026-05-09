namespace FlashSkink.Core.Abstractions.Models;

/// <summary>
/// A snapshot of a tail provider's health state, returned by <c>IStorageProvider.CheckHealthAsync</c>
/// and carried in <see cref="TailStatusChangedEventArgs"/>. Blueprint §10.4.
/// </summary>
public sealed record ProviderHealth
{
    /// <summary>The health classification at the time of the check.</summary>
    public required ProviderHealthStatus Status { get; init; }

    /// <summary>When the check was performed (UTC).</summary>
    public required DateTimeOffset CheckedAt { get; init; }

    /// <summary>Round-trip latency of the last health probe; <see langword="null"/> when the probe failed before any response.</summary>
    public TimeSpan? RoundTripLatency { get; init; }

    /// <summary>Optional human-readable detail string surfaced from the provider error; <see langword="null"/> when healthy.</summary>
    public string? Detail { get; init; }
}

/// <summary>Classification values for <see cref="ProviderHealth.Status"/>.</summary>
public enum ProviderHealthStatus
{
    /// <summary>The last health check succeeded normally.</summary>
    Healthy,

    /// <summary>Recent failures are occurring; retries are in progress.</summary>
    Degraded,

    /// <summary>Sustained failure — the provider cannot be reached.</summary>
    Unreachable,

    /// <summary>The OAuth token has expired or been revoked.</summary>
    AuthFailed,

    /// <summary>The provider reports it is out of storage quota.</summary>
    QuotaExceeded,
}
