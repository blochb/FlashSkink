namespace FlashSkink.Core.Engine;

/// <summary>
/// Detects file type from a filename and up to 16 header bytes.
/// Pure function over its inputs — never throws, never performs I/O,
/// allocates only the returned <see cref="FileTypeResult"/>.
/// Magic-byte MIME wins over extension MIME on conflict; the original extension
/// is always preserved as-given (lower-cased, dot-prefixed).
/// Qualifies for the pure-function carve-out under Principle 1 of CLAUDE.md.
/// </summary>
public sealed class FileTypeService
{
    /// <summary>
    /// Detects the file type. Never throws. Returns null fields when a signal
    /// is absent or unrecognised. A null <paramref name="fileName"/> is treated
    /// as "no extension". An empty <paramref name="header"/> skips magic-byte detection.
    /// </summary>
    public FileTypeResult Detect(string? fileName, ReadOnlySpan<byte> header)
    {
        string? extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (extension.Length == 0)
        {
            extension = null;
        }

        string? magicMime = TryMatchMagic(header);

        // ZIP disambiguation via KnownExtensions — single source of truth.
        // .docx/.xlsx/.pptx/.jar/.epub override the bare "application/zip" MIME.
        if (magicMime == "application/zip"
            && extension is not null
            && MagicBytes.KnownExtensions.TryGetValue(extension, out var extMime)
            && extMime != "application/zip")
        {
            magicMime = extMime;
        }

        // OLE/CFB disambiguation — same pattern as ZIP.
        // D0 CF 11 E0 is the compound-file container shared by .doc, .xls, .ppt, .msi, .msg, etc.
        // .xls/.ppt override the bare "application/msword" MIME when the extension maps differently.
        if (magicMime == "application/msword"
            && extension is not null
            && MagicBytes.KnownExtensions.TryGetValue(extension, out var oleExtMime)
            && oleExtMime != "application/msword")
        {
            magicMime = oleExtMime;
        }

        string? mimeType = magicMime
            ?? (extension is not null && MagicBytes.KnownExtensions.TryGetValue(extension, out var fromExt)
                ? fromExt
                : null);

        return new FileTypeResult(extension, mimeType);
    }

    private static string? TryMatchMagic(ReadOnlySpan<byte> header)
    {
        // Offset-zero signatures, ordered by specificity / commonness.
        if (header.StartsWith(MagicBytes.Jpeg))
        {
            return "image/jpeg";
        }
        if (header.StartsWith(MagicBytes.Png))
        {
            return "image/png";
        }
        if (header.StartsWith(MagicBytes.Gif))
        {
            return "image/gif";
        }
        if (header.StartsWith(MagicBytes.Pdf))
        {
            return "application/pdf";
        }
        if (header.StartsWith(MagicBytes.Zip))
        {
            return "application/zip";
        }
        if (header.StartsWith(MagicBytes.MsDoc))
        {
            return "application/msword";
        }
        if (header.StartsWith(MagicBytes.SevenZ))
        {
            return "application/x-7z-compressed";
        }
        if (header.StartsWith(MagicBytes.Rar))
        {
            return "application/x-rar-compressed";
        }
        if (header.StartsWith(MagicBytes.WebM))
        {
            return "video/webm";
        }
        if (header.StartsWith(MagicBytes.Mp3))
        {
            return "audio/mpeg";
        }
        if (header.StartsWith(MagicBytes.Ogg))
        {
            return "audio/ogg";
        }
        if (header.StartsWith(MagicBytes.Flac))
        {
            return "audio/flac";
        }
        if (header.StartsWith(MagicBytes.GZip))
        {
            return "application/gzip";
        }
        if (header.StartsWith(MagicBytes.BZip2))
        {
            return "application/x-bzip2";
        }

        // MP4 — "ftyp" at offset 4.
        if (header.Length >= 8 && header[4..].StartsWith(MagicBytes.Mp4))
        {
            return "video/mp4";
        }

        // WebP — RIFF at offset 0, "WEBP" at offset 8. Bare RIFF prefix is shared
        // with WAV ("WAVE") and AVI ("AVI ") and is NOT treated as WebP.
        if (header.Length >= 12
            && header.StartsWith(MagicBytes.Riff)
            && header[8..].StartsWith(MagicBytes.WebPMarker))
        {
            return "image/webp";
        }

        return null;
    }
}
