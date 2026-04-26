using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using Xunit;

namespace FlashSkink.Tests.Crypto;

public class MnemonicServiceTests
{
    private readonly MnemonicService _sut = new();

    // Standard BIP-39 all-zeros test vector (256-bit entropy = 32 zero bytes).
    // Mnemonic: 23× "abandon" + "art"
    // Seed (empty passphrase), first 8 bytes: 5e b0 0b bd dc f0 69 08
    private static readonly string[] AllZerosMnemonic =
    [
        "abandon", "abandon", "abandon", "abandon", "abandon", "abandon",
        "abandon", "abandon", "abandon", "abandon", "abandon", "abandon",
        "abandon", "abandon", "abandon", "abandon", "abandon", "abandon",
        "abandon", "abandon", "abandon", "abandon", "abandon", "art",
    ];

    [Fact]
    public void Generate_Succeeds()
    {
        var result = _sut.Generate();

        Assert.True(result.Success);
    }

    [Fact]
    public void Generate_Returns24Words()
    {
        var result = _sut.Generate();

        Assert.Equal(24, result.Value!.Length);
    }

    [Fact]
    public void Generate_AllWordsAreInBip39Wordlist()
    {
        var wordlist = MnemonicService.Wordlist;
        var wordSet = wordlist.ToHashSet(StringComparer.Ordinal);

        var result = _sut.Generate();

        Assert.All(result.Value!, w => Assert.Contains(w, wordSet));
    }

    [Fact]
    public void Generate_TwoCallsProduceDifferentMnemonics()
    {
        var first = _sut.Generate().Value!;
        var second = _sut.Generate().Value!;

        Assert.False(first.SequenceEqual(second),
            "Two Generate() calls produced identical mnemonics — RNG may be broken.");
    }

    [Fact]
    public void Validate_WithGeneratedMnemonic_Succeeds()
    {
        var words = _sut.Generate().Value!;

        var result = _sut.Validate(words);

        Assert.True(result.Success);
    }

    [Fact]
    public void Validate_WrongWordCount_ReturnsInvalidMnemonic()
    {
        var words = new string[23];
        Array.Fill(words, "abandon");

        var result = _sut.Validate(words);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidMnemonic, result.Error!.Code);
    }

    [Fact]
    public void Validate_UnknownWord_ReturnsInvalidMnemonic()
    {
        var words = (string[])AllZerosMnemonic.Clone();
        words[0] = "xyzzy";

        var result = _sut.Validate(words);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidMnemonic, result.Error!.Code);
    }

    [Fact]
    public void Validate_NullWord_ReturnsInvalidMnemonic()
    {
        var words = (string[])AllZerosMnemonic.Clone();
        words[0] = null!;

        var result = _sut.Validate(words);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidMnemonic, result.Error!.Code);
    }

    [Fact]
    public void Validate_BadChecksum_ReturnsInvalidMnemonic()
    {
        // Replacing "art" (last word of all-zeros vector) with "able" corrupts the checksum.
        var words = (string[])AllZerosMnemonic.Clone();
        words[23] = "able";

        var result = _sut.Validate(words);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidMnemonic, result.Error!.Code);
    }

    [Fact]
    public void Validate_KnownVector_Succeeds()
    {
        var result = _sut.Validate(AllZerosMnemonic);

        Assert.True(result.Success);
    }

    [Fact]
    public void ToSeed_Returns64Bytes()
    {
        var result = _sut.ToSeed(AllZerosMnemonic);

        Assert.True(result.Success);
        Assert.Equal(64, result.Value!.Length);
    }

    [Fact]
    public void ToSeed_IsDeterministic()
    {
        var first = _sut.ToSeed(AllZerosMnemonic).Value!;
        var second = _sut.ToSeed(AllZerosMnemonic).Value!;

        Assert.True(first.SequenceEqual(second));
    }

    [Fact]
    public void ToSeed_DifferentMnemonics_ProduceDifferentSeeds()
    {
        var words1 = _sut.Generate().Value!;
        var words2 = _sut.Generate().Value!;

        var seed1 = _sut.ToSeed(words1).Value!;
        var seed2 = _sut.ToSeed(words2).Value!;

        Assert.False(seed1.SequenceEqual(seed2));
    }

    [Fact]
    public void ToSeed_Bip39AllZerosVector_ProducesKnownSeedPrefix()
    {
        // 24-word all-zeros entropy (abandon×23 + art), empty passphrase (salt="mnemonic").
        // 408b285c... is the correct seed prefix for this specific vector.
        // Note: the well-cited 5eb00bbd... prefix is for the 12-word all-zeros vector with
        // "TREZOR" passphrase — a different combination entirely.
        var expectedPrefix = new byte[] { 0x40, 0x8b, 0x28, 0x5c, 0x12, 0x38, 0x36, 0x00 };

        var seed = _sut.ToSeed(AllZerosMnemonic).Value!;

        Assert.True(seed[..8].SequenceEqual(expectedPrefix),
            $"Expected seed prefix 408b285c12383600 but got {Convert.ToHexString(seed[..8]).ToLowerInvariant()}");
    }

    [Fact]
    public void ToSeed_InvalidWords_ReturnsInvalidMnemonic()
    {
        var words = new string[24];
        Array.Fill(words, "xyzzy");

        var result = _sut.ToSeed(words);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidMnemonic, result.Error!.Code);
    }
}
