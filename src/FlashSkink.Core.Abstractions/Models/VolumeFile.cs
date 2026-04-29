namespace FlashSkink.Core.Abstractions.Models;

/// <summary>
/// Domain model for one row in the <c>Files</c> brain table. Represents either a file or a
/// folder node in the user's virtual tree. Both files and folders share this type; callers
/// inspect <see cref="IsFolder"/> to distinguish them.
/// </summary>
public sealed record VolumeFile
{
    /// <summary>Primary key — a <see cref="Guid"/>-formatted string.</summary>
    public required string FileId { get; init; }

    /// <summary>Parent folder ID, or <see langword="null"/> for root-level items.</summary>
    public string? ParentId { get; init; }

    /// <summary><see langword="true"/> when this row represents a folder, not a file.</summary>
    public required bool IsFolder { get; init; }

    /// <summary><see langword="true"/> when this row represents a symbolic link.</summary>
    public required bool IsSymlink { get; init; }

    /// <summary>The link target path; non-<see langword="null"/> only when <see cref="IsSymlink"/> is <see langword="true"/>.</summary>
    public string? SymlinkTarget { get; init; }

    /// <summary>UTF-8 NFC-normalised item name without path separators.</summary>
    public required string Name { get; init; }

    /// <summary>Lowercase dot-prefixed extension (e.g. <c>".jpg"</c>); <see langword="null"/> for folders and symlinks.</summary>
    public string? Extension { get; init; }

    /// <summary>MIME type string (e.g. <c>"image/jpeg"</c>); <see langword="null"/> if undetectable.</summary>
    public string? MimeType { get; init; }

    /// <summary>Denormalised full virtual path; always consistent with <see cref="ParentId"/>.</summary>
    public required string VirtualPath { get; init; }

    /// <summary>Plaintext size in bytes; 0 for folders and symlinks.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Creation timestamp preserved from the source filesystem (UTC).</summary>
    public required DateTime CreatedUtc { get; init; }

    /// <summary>Last-modified timestamp preserved from the source filesystem (UTC).</summary>
    public required DateTime ModifiedUtc { get; init; }

    /// <summary>Timestamp when this item was added to FlashSkink (UTC).</summary>
    public required DateTime AddedUtc { get; init; }

    /// <summary>
    /// Foreign key into <c>Blobs</c>; <see langword="null"/> for folders and symlinks.
    /// When set, the referenced blob holds the encrypted payload for this file.
    /// </summary>
    public string? BlobId { get; init; }
}
