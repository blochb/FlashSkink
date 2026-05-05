namespace FlashSkink.Core.Engine;

/// <summary>
/// The result returned by a successful <see cref="WritePipeline.ExecuteAsync"/> call.
/// Carries the identifiers and size metrics for the committed file and blob, plus the detected
/// MIME type and extension. On the <see cref="WriteStatus.Unchanged"/> path, <see cref="BlobId"/>
/// identifies the pre-existing blob and <see cref="EncryptedSize"/> is the pre-existing blob's
/// encrypted size.
/// </summary>
public sealed record WriteReceipt
{
    /// <summary>The <c>Files.FileID</c> of the committed or pre-existing file row.</summary>
    public required string FileId { get; init; }

    /// <summary>The <c>Blobs.BlobID</c> of the committed or pre-existing blob row.</summary>
    public required string BlobId { get; init; }

    /// <summary>
    /// Whether new content was written (<see cref="WriteStatus.Written"/>) or the file was
    /// already present and unchanged (<see cref="WriteStatus.Unchanged"/>).
    /// </summary>
    public required WriteStatus Status { get; init; }

    /// <summary>Number of plaintext bytes, as measured from the source stream by stage 1.</summary>
    public required long PlaintextSize { get; init; }

    /// <summary>Number of bytes in the on-disk encrypted blob (header + ciphertext + tag).</summary>
    public required long EncryptedSize { get; init; }

    /// <summary>MIME type detected by <see cref="FileTypeService"/>; <see langword="null"/> when unrecognised.</summary>
    public string? MimeType { get; init; }

    /// <summary>
    /// Lower-case file extension (dot-prefixed, e.g. <c>".txt"</c>) from the source path;
    /// <see langword="null"/> when the path has no extension.
    /// </summary>
    public string? Extension { get; init; }
}
