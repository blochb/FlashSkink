using FlashSkink.Core.Engine;
using Xunit;

namespace FlashSkink.Tests.Engine;

public class EntropyDetectorTests
{
    private readonly EntropyDetector _sut = new();

    // Test data is authored inline rather than referenced from MagicBytes —
    // see CLAUDE.md §Testing: tests do not consume internals via [InternalsVisibleTo].

    [Fact]
    public void IsCompressible_JpegExtension_ReturnsFalse()
    {
        bool result = _sut.IsCompressible(".jpg", ReadOnlySpan<byte>.Empty);

        Assert.False(result);
    }

    [Fact]
    public void IsCompressible_PngMagic_ReturnsFalse()
    {
        ReadOnlySpan<byte> header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        bool result = _sut.IsCompressible(extension: null, header);

        Assert.False(result);
    }

    [Fact]
    public void IsCompressible_Mp4Magic_ReturnsFalse()
    {
        ReadOnlySpan<byte> header = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32];

        bool result = _sut.IsCompressible(extension: null, header);

        Assert.False(result);
    }

    [Fact]
    public void IsCompressible_ZipMagic_ReturnsFalse()
    {
        ReadOnlySpan<byte> header = [0x50, 0x4B, 0x03, 0x04];

        bool result = _sut.IsCompressible(extension: null, header);

        Assert.False(result);
    }

    [Fact]
    public void IsCompressible_TextExtension_ReturnsTrue()
    {
        bool result = _sut.IsCompressible(".txt", ReadOnlySpan<byte>.Empty);

        Assert.True(result);
    }

    [Fact]
    public void IsCompressible_NullExtensionUnknownMagic_ReturnsTrue()
    {
        ReadOnlySpan<byte> header = [0x01, 0x02, 0x03, 0x04];

        bool result = _sut.IsCompressible(extension: null, header);

        Assert.True(result);
    }

    [Fact]
    public void IsCompressible_GzipMagic_ReturnsFalse()
    {
        ReadOnlySpan<byte> header = [0x1F, 0x8B, 0x08, 0x00];

        bool result = _sut.IsCompressible(extension: null, header);

        Assert.False(result);
    }

    [Fact]
    public void IsCompressible_FlacMagic_ReturnsFalse()
    {
        ReadOnlySpan<byte> header = [0x66, 0x4C, 0x61, 0x43];

        bool result = _sut.IsCompressible(extension: null, header);

        Assert.False(result);
    }

    [Fact]
    public void IsCompressible_EmptyHeaderAndNullExtension_ReturnsTrue()
    {
        bool result = _sut.IsCompressible(extension: null, ReadOnlySpan<byte>.Empty);

        Assert.True(result);
    }

    [Fact]
    public void IsCompressible_PdfExtension_ReturnsFalse()
    {
        bool result = _sut.IsCompressible(".pdf", ReadOnlySpan<byte>.Empty);

        Assert.False(result);
    }

    [Fact]
    public void IsCompressible_WebPMagic_ReturnsFalse()
    {
        ReadOnlySpan<byte> header =
        [
            0x52, 0x49, 0x46, 0x46,
            0x24, 0x00, 0x00, 0x00,
            0x57, 0x45, 0x42, 0x50,
        ];

        bool result = _sut.IsCompressible(extension: null, header);

        Assert.False(result);
    }

    [Fact]
    public void IsCompressible_WavHeader_ReturnsTrue()
    {
        // RIFF + WAVE — uncompressed PCM is compressible. Bare RIFF prefix
        // must NOT flag this as already-compressed.
        ReadOnlySpan<byte> header =
        [
            0x52, 0x49, 0x46, 0x46,
            0x24, 0x00, 0x00, 0x00,
            0x57, 0x41, 0x56, 0x45,
        ];

        bool result = _sut.IsCompressible(extension: null, header);

        Assert.True(result);
    }

    [Fact]
    public void IsCompressible_DocxExtension_ReturnsFalse()
    {
        bool result = _sut.IsCompressible(".docx", ReadOnlySpan<byte>.Empty);

        Assert.False(result);
    }

    [Fact]
    public void IsCompressible_XlsxExtension_ReturnsFalse()
    {
        bool result = _sut.IsCompressible(".xlsx", ReadOnlySpan<byte>.Empty);

        Assert.False(result);
    }

    [Fact]
    public void IsCompressible_PptxExtension_ReturnsFalse()
    {
        bool result = _sut.IsCompressible(".pptx", ReadOnlySpan<byte>.Empty);

        Assert.False(result);
    }

    [Fact]
    public void IsCompressible_JarExtension_ReturnsFalse()
    {
        bool result = _sut.IsCompressible(".jar", ReadOnlySpan<byte>.Empty);

        Assert.False(result);
    }

    [Fact]
    public void IsCompressible_EpubExtension_ReturnsFalse()
    {
        bool result = _sut.IsCompressible(".epub", ReadOnlySpan<byte>.Empty);

        Assert.False(result);
    }
}
