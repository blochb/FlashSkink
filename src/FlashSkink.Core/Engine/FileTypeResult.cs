namespace FlashSkink.Core.Engine;

/// <summary>
/// Holds the detected extension and MIME type for a file.
/// Either field may be null when the respective signal is absent or unrecognised.
/// </summary>
/// <param name="Extension">Lower-case, dot-prefixed extension (e.g. ".jpg") or null when the filename has no extension.</param>
/// <param name="MimeType">Canonical MIME type (e.g. "image/jpeg") or null when neither magic bytes nor extension yield a match.</param>
public sealed record FileTypeResult(
    string? Extension,
    string? MimeType);
