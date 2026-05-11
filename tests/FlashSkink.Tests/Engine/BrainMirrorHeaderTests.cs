using FlashSkink.Core.Engine;
using Xunit;

namespace FlashSkink.Tests.Engine;

public sealed class BrainMirrorHeaderTests
{
    [Fact]
    public void Write_ProducesExpectedBytes()
    {
        var ts = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Span<byte> buf = stackalloc byte[BrainMirrorHeader.Size];
        BrainMirrorHeader.Write(buf, ts);

        // Magic "FSBM" little-endian
        Assert.Equal(0x46, buf[0]);
        Assert.Equal(0x53, buf[1]);
        Assert.Equal(0x42, buf[2]);
        Assert.Equal(0x4D, buf[3]);
        // Version = 1 (uint16 LE)
        Assert.Equal(0x01, buf[4]);
        Assert.Equal(0x00, buf[5]);
        // Reserved = 0
        Assert.Equal(0x00, buf[6]);
        Assert.Equal(0x00, buf[7]);
    }

    [Fact]
    public void TryParse_RoundTrip_ReturnsTrueAndValues()
    {
        var ts = new DateTime(2026, 5, 11, 14, 30, 45, DateTimeKind.Utc);
        Span<byte> buf = stackalloc byte[BrainMirrorHeader.Size];
        BrainMirrorHeader.Write(buf, ts);

        bool ok = BrainMirrorHeader.TryParse(buf, out ushort version, out DateTime parsed);

        Assert.True(ok);
        Assert.Equal(1, version);
        Assert.Equal(ts, parsed);
    }

    [Fact]
    public void TryParse_WrongMagic_ReturnsFalse()
    {
        var ts = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Span<byte> buf = stackalloc byte[BrainMirrorHeader.Size];
        BrainMirrorHeader.Write(buf, ts);
        buf[0] = 0x00; // corrupt magic

        bool ok = BrainMirrorHeader.TryParse(buf, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_UnknownVersion_ReturnsTrueWithParsedVersion()
    {
        var ts = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Span<byte> buf = stackalloc byte[BrainMirrorHeader.Size];
        BrainMirrorHeader.Write(buf, ts);
        buf[4] = 0x02; // bump version to 2

        bool ok = BrainMirrorHeader.TryParse(buf, out ushort version, out _);

        Assert.True(ok);
        Assert.Equal(2, version);
    }

    [Fact]
    public void TryParse_Truncated_ReturnsFalse()
    {
        Span<byte> tooShort = stackalloc byte[BrainMirrorHeader.Size - 1];
        bool ok = BrainMirrorHeader.TryParse(tooShort, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void Write_TooSmallBuffer_Throws()
    {
        byte[] tooSmall = new byte[BrainMirrorHeader.Size - 1];
        Assert.Throws<ArgumentException>(() =>
            BrainMirrorHeader.Write(tooSmall, DateTime.UtcNow));
    }
}
