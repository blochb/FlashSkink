namespace FlashSkink.Core.Abstractions.Providers;

/// <summary>
/// In-memory representation of an open resumable upload session. Returned by
/// <see cref="IStorageProvider.BeginUploadAsync"/> and passed to every subsequent
/// <see cref="IStorageProvider.UploadRangeAsync"/> and <see cref="IStorageProvider.FinaliseUploadAsync"/>
/// call. Blueprint §10.2.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FileID"/> and <see cref="ProviderID"/> default to <see cref="string.Empty"/>
/// because <see cref="IStorageProvider.BeginUploadAsync"/> takes only <c>remoteName</c> and
/// <c>totalBytes</c> — the provider has no knowledge of the skink's internal identifiers.
/// The caller (<c>RangeUploader</c>, §3.3) stamps both fields with a <c>with</c>-expression
/// before persisting via <c>UploadQueueRepository.GetOrCreateSessionAsync</c>.
/// </para>
/// </remarks>
public sealed record UploadSession
{
    /// <summary>Skink-side file identifier. Stamped by the caller before persistence; defaults to <see cref="string.Empty"/>.</summary>
    public string FileID { get; init; } = string.Empty;

    /// <summary>Provider identifier. Stamped by the caller before persistence; defaults to <see cref="string.Empty"/>.</summary>
    public string ProviderID { get; init; } = string.Empty;

    /// <summary>Provider-returned session URI or opaque session key. For <c>FileSystemProvider</c> this is the sanitised remote name used for staging paths.</summary>
    public required string SessionUri { get; init; }

    /// <summary>UTC deadline after which the provider considers this session expired. <see cref="DateTimeOffset.MaxValue"/> for sessions that never expire (e.g. <c>FileSystemProvider</c>).</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Number of bytes the provider has confirmed receiving so far. The range loop starts from this offset.</summary>
    public required long BytesUploaded { get; init; }

    /// <summary>Total encrypted byte count the provider expects for this object.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>UTC timestamp of the last successful range upload or session creation. Updated by <c>UpdateSessionProgressAsync</c>.</summary>
    public DateTimeOffset LastActivityUtc { get; init; } = DateTimeOffset.UtcNow;
}
