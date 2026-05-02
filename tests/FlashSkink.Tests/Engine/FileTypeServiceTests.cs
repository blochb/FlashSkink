using FlashSkink.Core.Engine;
using Xunit;

namespace FlashSkink.Tests.Engine;

public class FileTypeServiceTests
{
    private readonly FileTypeService _sut = new();

    // Test data is authored inline rather than referenced from MagicBytes —
    // see CLAUDE.md §Testing: tests do not consume internals via [InternalsVisibleTo].

    [Fact]
    public void Detect_JpegHeader_ReturnsJpegExtensionAndMime()
    {
        ReadOnlySpan<byte> header = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

        FileTypeResult result = _sut.Detect("photo.jpg", header);

        Assert.Equal(".jpg", result.Extension);
        Assert.Equal("image/jpeg", result.MimeType);
    }

    [Fact]
    public void Detect_PngHeader_ReturnsPngMime()
    {
        ReadOnlySpan<byte> header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        FileTypeResult result = _sut.Detect("image.PNG", header);

        Assert.Equal(".png", result.Extension); // lower-cased
        Assert.Equal("image/png", result.MimeType);
    }

    [Fact]
    public void Detect_ZipHeaderWithDocxExtension_ReturnsOoxml()
    {
        ReadOnlySpan<byte> header = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00];

        FileTypeResult result = _sut.Detect("report.docx", header);

        Assert.Equal(".docx", result.Extension);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            result.MimeType);
    }

    [Fact]
    public void Detect_ZipHeaderWithNoKnownExtension_ReturnsApplicationZip()
    {
        ReadOnlySpan<byte> header = [0x50, 0x4B, 0x03, 0x04];

        FileTypeResult result = _sut.Detect("archive.zip", header);

        Assert.Equal(".zip", result.Extension);
        Assert.Equal("application/zip", result.MimeType);
    }

    [Fact]
    public void Detect_ZipHeaderWithUnrecognisedExtension_ReturnsApplicationZip()
    {
        // .apk is not in KnownExtensions — TryGetValue returns false and the
        // disambiguation block does not fire. Verifies the non-disambiguation path.
        ReadOnlySpan<byte> header = [0x50, 0x4B, 0x03, 0x04];

        FileTypeResult result = _sut.Detect("app.apk", header);

        Assert.Equal(".apk", result.Extension);
        Assert.Equal("application/zip", result.MimeType);
    }

    [Fact]
    public void Detect_ZipHeaderWithXlsxExtension_ReturnsSpreadsheetMime()
    {
        ReadOnlySpan<byte> header = [0x50, 0x4B, 0x03, 0x04];

        FileTypeResult result = _sut.Detect("data.xlsx", header);

        Assert.Equal(".xlsx", result.Extension);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            result.MimeType);
    }

    [Fact]
    public void Detect_ZipHeaderWithPptxExtension_ReturnsPresentationMime()
    {
        ReadOnlySpan<byte> header = [0x50, 0x4B, 0x03, 0x04];

        FileTypeResult result = _sut.Detect("slides.pptx", header);

        Assert.Equal(".pptx", result.Extension);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            result.MimeType);
    }

    [Fact]
    public void Detect_ZipHeaderWithJarExtension_ReturnsJavaArchiveMime()
    {
        ReadOnlySpan<byte> header = [0x50, 0x4B, 0x03, 0x04];

        FileTypeResult result = _sut.Detect("lib.jar", header);

        Assert.Equal(".jar", result.Extension);
        Assert.Equal("application/java-archive", result.MimeType);
    }

    [Fact]
    public void Detect_ZipHeaderWithEpubExtension_ReturnsEpubZipMime()
    {
        ReadOnlySpan<byte> header = [0x50, 0x4B, 0x03, 0x04];

        FileTypeResult result = _sut.Detect("book.epub", header);

        Assert.Equal(".epub", result.Extension);
        Assert.Equal("application/epub+zip", result.MimeType);
    }

    [Fact]
    public void Detect_Mp4Header_ReturnsMp4Mime()
    {
        // 4 bytes of size, then "ftyp" at offset 4.
        ReadOnlySpan<byte> header = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32];

        FileTypeResult result = _sut.Detect("video.mp4", header);

        Assert.Equal(".mp4", result.Extension);
        Assert.Equal("video/mp4", result.MimeType);
    }

    [Fact]
    public void Detect_Mp4ShortHeader_DoesNotMatch()
    {
        // Only 6 bytes — below the >= 8 guard. Reading offset-4 for 4 bytes would run past the end.
        ReadOnlySpan<byte> header = [0x00, 0x00, 0x66, 0x74, 0x79, 0x70];

        FileTypeResult result = _sut.Detect("ambiguous", header);

        Assert.NotEqual("video/mp4", result.MimeType);
    }

    [Fact]
    public void Detect_UnknownMagicKnownExtension_ReturnsMimeFromExtension()
    {
        ReadOnlySpan<byte> header = [0x01, 0x02, 0x03, 0x04];

        FileTypeResult result = _sut.Detect("doc.txt", header);

        Assert.Equal(".txt", result.Extension);
        Assert.Equal("text/plain", result.MimeType);
    }

    [Fact]
    public void Detect_UnknownMagicUnknownExtension_ReturnsBothNull()
    {
        ReadOnlySpan<byte> header = [0x01, 0x02, 0x03, 0x04];

        FileTypeResult result = _sut.Detect("noext", header);

        Assert.Null(result.Extension);
        Assert.Null(result.MimeType);
    }

    [Fact]
    public void Detect_NoExtensionKnownMagic_ReturnsNullExtensionWithMime()
    {
        ReadOnlySpan<byte> header = [0xFF, 0xD8, 0xFF, 0xE0];

        FileTypeResult result = _sut.Detect("photo", header);

        Assert.Null(result.Extension);
        Assert.Equal("image/jpeg", result.MimeType);
    }

    [Fact]
    public void Detect_EmptyHeader_DoesNotThrow()
    {
        FileTypeResult result = _sut.Detect("any.txt", ReadOnlySpan<byte>.Empty);

        Assert.Equal(".txt", result.Extension);
        Assert.Equal("text/plain", result.MimeType);
    }

    [Fact]
    public void Detect_GifHeader_ReturnsGifMime()
    {
        ReadOnlySpan<byte> header = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61];

        FileTypeResult result = _sut.Detect("anim.gif", header);

        Assert.Equal(".gif", result.Extension);
        Assert.Equal("image/gif", result.MimeType);
    }

    [Fact]
    public void Detect_MsDocHeader_ReturnsMsWordMime()
    {
        ReadOnlySpan<byte> header = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1];

        FileTypeResult result = _sut.Detect("legacy.doc", header);

        Assert.Equal(".doc", result.Extension);
        Assert.Equal("application/msword", result.MimeType);
    }

    [Fact]
    public void Detect_MsDocHeaderWithXlsExtension_ReturnsExcelMime()
    {
        // OLE/CFB magic (D0 CF 11 E0) is shared by .doc, .xls, .ppt, and others.
        // Extension wins when the magic maps to msword but the extension maps elsewhere.
        ReadOnlySpan<byte> header = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1];

        FileTypeResult result = _sut.Detect("budget.xls", header);

        Assert.Equal(".xls", result.Extension);
        Assert.Equal("application/vnd.ms-excel", result.MimeType);
    }

    [Fact]
    public void Detect_MsDocHeaderWithPptExtension_ReturnsPowerpointMime()
    {
        ReadOnlySpan<byte> header = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1];

        FileTypeResult result = _sut.Detect("slides.ppt", header);

        Assert.Equal(".ppt", result.Extension);
        Assert.Equal("application/vnd.ms-powerpoint", result.MimeType);
    }

    [Fact]
    public void Detect_ExtensionCaseNormalized_ReturnsLowercase()
    {
        ReadOnlySpan<byte> header = [0xFF, 0xD8, 0xFF];

        FileTypeResult result = _sut.Detect("IMAGE.JPG", header);

        Assert.Equal(".jpg", result.Extension);
    }

    [Fact]
    public void Detect_MagicOverridesExtension_OnConflict()
    {
        // PNG magic but .txt extension — magic-byte MIME wins, extension preserved.
        ReadOnlySpan<byte> header = [0x89, 0x50, 0x4E, 0x47];

        FileTypeResult result = _sut.Detect("misnamed.txt", header);

        Assert.Equal(".txt", result.Extension);
        Assert.Equal("image/png", result.MimeType);
    }

    [Fact]
    public void Detect_WebPHeader_ReturnsWebPMime()
    {
        // RIFF + 4 size bytes + "WEBP" at offset 8.
        ReadOnlySpan<byte> header =
        [
            0x52, 0x49, 0x46, 0x46,
            0x24, 0x00, 0x00, 0x00,
            0x57, 0x45, 0x42, 0x50,
        ];

        FileTypeResult result = _sut.Detect("image.webp", header);

        Assert.Equal(".webp", result.Extension);
        Assert.Equal("image/webp", result.MimeType);
    }

    [Fact]
    public void Detect_WavHeader_DoesNotReturnWebPMime()
    {
        // RIFF + 4 size bytes + "WAVE" at offset 8.
        ReadOnlySpan<byte> header =
        [
            0x52, 0x49, 0x46, 0x46,
            0x24, 0x00, 0x00, 0x00,
            0x57, 0x41, 0x56, 0x45,
        ];

        FileTypeResult result = _sut.Detect("clip.wav", header);

        Assert.NotEqual("image/webp", result.MimeType);
        Assert.Equal(".wav", result.Extension);
    }

    [Fact]
    public void Detect_AviHeader_DoesNotReturnWebPMime()
    {
        // RIFF + 4 size bytes + "AVI " at offset 8.
        ReadOnlySpan<byte> header =
        [
            0x52, 0x49, 0x46, 0x46,
            0x24, 0x00, 0x00, 0x00,
            0x41, 0x56, 0x49, 0x20,
        ];

        FileTypeResult result = _sut.Detect("clip.avi", header);

        Assert.NotEqual("image/webp", result.MimeType);
    }

    [Fact]
    public void Detect_RiffPrefixOnlyShortHeader_DoesNotReturnWebPMime()
    {
        // Only 4 bytes — below the >= 12 guard.
        ReadOnlySpan<byte> header = [0x52, 0x49, 0x46, 0x46];

        FileTypeResult result = _sut.Detect("unknown", header);

        Assert.NotEqual("image/webp", result.MimeType);
    }

    [Fact]
    public void Detect_NullFileName_DoesNotThrow()
    {
        ReadOnlySpan<byte> header = [0xFF, 0xD8, 0xFF];

        FileTypeResult result = _sut.Detect(null, header);

        Assert.Null(result.Extension);
        Assert.Equal("image/jpeg", result.MimeType);
    }
}
