using FlashSkink.Core.Identity;
using Xunit;

namespace FlashSkink.Tests.Identity;

/// <summary>
/// Sanity tests for the <see cref="VolumeState"/> enum. The enum is defined in dev plan
/// §3.5.1 to support the <c>BackfillAndStampOnOpenAsync</c> return-type widening; its
/// semantic enforcement lands in §3.5.2. These tests pin the contract that
/// <c>BackfillAndStampOnOpenAsync</c> relies on: <c>Enum.TryParse</c> succeeds for both
/// declared names and fails for garbage — the production code uses <c>TryParse</c> (not
/// <c>Parse</c>) so a corrupted <c>Settings["VolumeState"]</c> row defaults to Normal
/// rather than throwing.
/// </summary>
public sealed class VolumeStateRoundTripTests
{
    [Fact]
    public void Normal_IsZero()
    {
        Assert.Equal(0, (int)VolumeState.Normal);
    }

    [Fact]
    public void Fenced_IsOne()
    {
        Assert.Equal(1, (int)VolumeState.Fenced);
    }

    [Fact]
    public void ToString_Normal_ReturnsNormal()
    {
        Assert.Equal("Normal", VolumeState.Normal.ToString());
    }

    [Fact]
    public void ToString_Fenced_ReturnsFenced()
    {
        Assert.Equal("Fenced", VolumeState.Fenced.ToString());
    }

    [Fact]
    public void TryParse_NormalString_ReturnsNormal()
    {
        Assert.True(Enum.TryParse<VolumeState>("Normal", ignoreCase: false, out var result));
        Assert.Equal(VolumeState.Normal, result);
    }

    [Fact]
    public void TryParse_FencedString_ReturnsFenced()
    {
        Assert.True(Enum.TryParse<VolumeState>("Fenced", ignoreCase: false, out var result));
        Assert.Equal(VolumeState.Fenced, result);
    }

    [Fact]
    public void TryParse_Garbage_ReturnsFalse()
    {
        // Production code in BackfillAndStampOnOpenAsync uses TryParse and defaults to
        // Normal on false — never throws on a corrupted Settings row.
        Assert.False(Enum.TryParse<VolumeState>("garbage", ignoreCase: false, out _));
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsFalse()
    {
        Assert.False(Enum.TryParse<VolumeState>(string.Empty, ignoreCase: false, out _));
    }
}
