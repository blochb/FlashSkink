namespace FlashSkink.Core.Abstractions.Models;

/// <summary>
/// Domain model for one row in the <c>Blobs</c> brain table. Each blob is the encrypted,
/// optionally-compressed payload stored on the skink for one file. V1 enforces a 1:1
/// relationship between files and blobs; the schema is separated to support dedup in V2.
/// </summary>
public sealed record BlobRecord
{
    /// <summary>Primary key — a <see cref="Guid"/>-formatted string.</summary>
    public required string BlobId { get; init; }

    /// <summary>Size of the encrypted blob file on disk, in bytes.</summary>
    public required long EncryptedSize { get; init; }

    /// <summary>Size of the plaintext content before compression and encryption, in bytes.</summary>
    public required long PlaintextSize { get; init; }

    /// <summary>SHA-256 hex digest of the plaintext content. Used for change-detection short-circuit.</summary>
    public required string PlaintextSha256 { get; init; }

    /// <summary>XXHash digest of the encrypted blob on disk. Used for bit-rot detection.</summary>
    public required string EncryptedXxHash { get; init; }

    /// <summary>Compression algorithm applied before encryption; <see langword="null"/> when uncompressed, otherwise <c>"LZ4"</c> or <c>"ZSTD"</c>.</summary>
    public string? Compression { get; init; }

    /// <summary>Relative path of the blob file on the skink USB drive.</summary>
    public required string BlobPath { get; init; }

    /// <summary>Timestamp when this blob was written (UTC).</summary>
    public required DateTime CreatedUtc { get; init; }

    /// <summary>Timestamp when this blob was soft-deleted; <see langword="null"/> while active.</summary>
    public DateTime? SoftDeletedUtc { get; init; }

    /// <summary>Timestamp after which the sweeper may hard-delete this blob; <see langword="null"/> while active.</summary>
    public DateTime? PurgeAfterUtc { get; init; }
}
