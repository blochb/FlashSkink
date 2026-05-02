using System.Collections.Frozen;

namespace FlashSkink.Core.Engine;

/// <summary>
/// Static byte-signature constants and extension lookup tables shared by
/// <see cref="FileTypeService"/> and <see cref="EntropyDetector"/>. Single source
/// of truth for magic bytes, MIME mappings, and the set of already-compressed
/// extensions. All tables are initialised once at type load (see blueprint §17.3).
/// </summary>
internal static class MagicBytes
{
    // Offset-zero signatures.
    internal static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    internal static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];
    internal static readonly byte[] Gif = [0x47, 0x49, 0x46, 0x38];
    internal static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46];
    internal static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    internal static readonly byte[] MsDoc = [0xD0, 0xCF, 0x11, 0xE0];
    internal static readonly byte[] GZip = [0x1F, 0x8B];
    internal static readonly byte[] BZip2 = [0x42, 0x5A, 0x68];
    internal static readonly byte[] SevenZ = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    internal static readonly byte[] Rar = [0x52, 0x61, 0x72, 0x21];
    internal static readonly byte[] WebM = [0x1A, 0x45, 0xDF, 0xA3];
    internal static readonly byte[] Mp3 = [0x49, 0x44, 0x33];
    internal static readonly byte[] Ogg = [0x4F, 0x67, 0x67, 0x53];
    internal static readonly byte[] Flac = [0x66, 0x4C, 0x61, 0x43];

    // Offset-non-zero signatures.
    internal static readonly byte[] Mp4 = [0x66, 0x74, 0x79, 0x70]; // at offset 4
    internal static readonly byte[] Riff = [0x52, 0x49, 0x46, 0x46]; // RIFF container — shared by WebP/WAV/AVI
    internal static readonly byte[] WebPMarker = [0x57, 0x45, 0x42, 0x50]; // "WEBP" at offset 8 within RIFF

    /// <summary>
    /// Extension → MIME type. Used when magic bytes are absent or unrecognised.
    /// Also serves as the ZIP disambiguation table — a ZIP magic match consults
    /// this dictionary by extension to surface OOXML/JAR/EPUB MIMEs.
    /// </summary>
    internal static readonly FrozenDictionary<string, string> KnownExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".pdf"] = "application/pdf",
            [".zip"] = "application/zip",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".ppt"] = "application/vnd.ms-powerpoint",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".jar"] = "application/java-archive",
            [".epub"] = "application/epub+zip",
            [".gz"] = "application/gzip",
            [".bz2"] = "application/x-bzip2",
            [".7z"] = "application/x-7z-compressed",
            [".rar"] = "application/x-rar-compressed",
            [".mp4"] = "video/mp4",
            [".webm"] = "video/webm",
            [".mp3"] = "audio/mpeg",
            [".ogg"] = "audio/ogg",
            [".flac"] = "audio/flac",
            [".txt"] = "text/plain",
            [".html"] = "text/html",
            [".htm"] = "text/html",
            [".css"] = "text/css",
            [".js"] = "text/javascript",
            [".json"] = "application/json",
            [".xml"] = "application/xml",
            [".svg"] = "image/svg+xml",
            [".csv"] = "text/csv",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extensions whose content is already compressed. <see cref="EntropyDetector"/>
    /// uses this to short-circuit the compression attempt for known formats.
    /// </summary>
    internal static readonly FrozenSet<string> CompressedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf",
            ".zip", ".docx", ".xlsx", ".pptx", ".jar", ".epub",
            ".gz", ".bz2", ".7z", ".rar",
            ".mp4", ".webm", ".mp3", ".ogg", ".flac",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
