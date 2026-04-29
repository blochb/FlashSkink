namespace FlashSkink.Core.Metadata;

/// <summary>
/// In-memory representation of one row in the <c>UploadSessions</c> brain table. Holds the
/// resumable-upload state for one in-flight tail upload. Persisted on the skink so uploads
/// survive disconnect and host change (Principle 5).
/// </summary>
public sealed record UploadSessionRow(
    string FileId,
    string ProviderId,
    string SessionUri,
    DateTime SessionExpiresUtc,
    long BytesUploaded,
    long TotalBytes,
    DateTime LastActivityUtc);
