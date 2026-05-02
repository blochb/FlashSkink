namespace FlashSkink.Core.Engine;

/// <summary>
/// Decides whether a file is a candidate for compression based on its
/// known extension or magic-byte signature. Returns false for formats
/// that are already compressed; returns true for everything else,
/// including unknown content (the pipeline's no-gain rule catches actual
/// incompressibles without penalising plain-text files with no extension).
/// Pure function over its inputs — never throws, never performs I/O.
/// Qualifies for the pure-function carve-out under Principle 1 of CLAUDE.md.
/// </summary>
public sealed class EntropyDetector
{
    /// <summary>
    /// Returns false when extension or header indicates already-compressed content.
    /// Returns true otherwise, including when both signals are absent. Never throws.
    /// </summary>
    public bool IsCompressible(string? extension, ReadOnlySpan<byte> header)
    {
        if (extension is not null && MagicBytes.CompressedExtensions.Contains(extension))
        {
            return false;
        }

        if (HeaderIndicatesCompressed(header))
        {
            return false;
        }

        return true;
    }

    private static bool HeaderIndicatesCompressed(ReadOnlySpan<byte> header)
    {
        // Offset-zero signatures of already-compressed formats.
        if (header.StartsWith(MagicBytes.Jpeg))
        {
            return true;
        }
        if (header.StartsWith(MagicBytes.Png))
        {
            return true;
        }
        if (header.StartsWith(MagicBytes.Gif))
        {
            return true;
        }
        if (header.StartsWith(MagicBytes.Pdf))
        {
            return true;
        }
        if (header.StartsWith(MagicBytes.Zip))
        {
            return true;
        }
        if (header.StartsWith(MagicBytes.GZip))
        {
            return true;
        }
        if (header.StartsWith(MagicBytes.BZip2))
        {
            return true;
        }
        if (header.StartsWith(MagicBytes.SevenZ))
        {
            return true;
        }
        if (header.StartsWith(MagicBytes.Rar))
        {
            return true;
        }
        if (header.StartsWith(MagicBytes.WebM))
        {
            return true;
        }
        if (header.StartsWith(MagicBytes.Mp3))
        {
            return true;
        }
        if (header.StartsWith(MagicBytes.Ogg))
        {
            return true;
        }
        if (header.StartsWith(MagicBytes.Flac))
        {
            return true;
        }

        // MP4 — "ftyp" at offset 4.
        if (header.Length >= 8 && header[4..].StartsWith(MagicBytes.Mp4))
        {
            return true;
        }

        // WebP — RIFF at offset 0, "WEBP" at offset 8. Bare RIFF prefix
        // belongs to WAV / AVI which are NOT already-compressed.
        if (header.Length >= 12
            && header.StartsWith(MagicBytes.Riff)
            && header[8..].StartsWith(MagicBytes.WebPMarker))
        {
            return true;
        }

        return false;
    }
}
