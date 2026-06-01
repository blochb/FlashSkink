namespace FlashSkink.Core.Abstractions.Models;

/// <summary>
/// Snapshot of a configured tail. Returned by <c>FlashSkinkVolume.AddTailAsync</c> (for the
/// just-added tail) and <c>FlashSkinkVolume.ListTailsAsync</c> (one per active tail). Blueprint §11.
/// </summary>
public sealed record TailInfo
{
    /// <summary>Stable provider id (brain primary key).</summary>
    public required string ProviderId { get; init; }

    /// <summary>Provider type token (<c>"filesystem"</c>, <c>"google-drive"</c>, …).</summary>
    public required string ProviderType { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Last known health string from <c>Providers.HealthStatus</c> (e.g. <c>"Healthy"</c>).</summary>
    public required string Health { get; init; }

    /// <summary>When the tail was added.</summary>
    public required DateTimeOffset AddedUtc { get; init; }

    /// <summary>Timestamp of the most recent successful upload to this tail, or <see langword="null"/> if none yet.</summary>
    public DateTimeOffset? LastSuccessfulUploadUtc { get; init; }

    /// <summary>Count of files fully uploaded to this tail.</summary>
    public long UploadedFileCount { get; init; }

    /// <summary>Count of files still pending (or failed) upload to this tail.</summary>
    public long PendingFileCount { get; init; }
}
