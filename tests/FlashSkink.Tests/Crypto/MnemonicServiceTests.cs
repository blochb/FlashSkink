using FlashSkink.Core.Abstractions.Crypto;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using Xunit;

namespace FlashSkink.Tests.Crypto;

public class MnemonicServiceTests
{
    private readonly MnemonicService _sut = new();

    // Standard BIP-39 all-zeros test vector (256-bit entropy = 32 zero bytes).
    // Mnemonic: 23× "abandon" + "art"
    private static readonly string[] AllZerosMnemonicWords =
    [
        "abandon", "abandon", "abandon", "abandon", "abandon", "abandon",
        "abandon", "abandon", "abandon", "abandon", "abandon", "abandon",
        "abandon", "abandon", "abandon", "abandon", "abandon", "abandon",
        "abandon", "abandon", "abandon", "abandon", "abandon", "art",
    ];

    private static RecoveryPhrase PhraseFrom(string[] words)
        => RecoveryPhrase.FromUserInput(words).Value!;

    private static RecoveryPhrase AllZerosPhrase()
        => PhraseFrom(AllZerosMnemonicWords);

    [Fact]
    public void Generate_Succeeds()
    {
        using var phrase = _sut.Generate().Value!;
        Assert.NotNull(phrase);
    }

    [Fact]
    public void Generate_Returns24Words()
    {
        using var phrase = _sut.Generate().Value!;
        Assert.Equal(24, phrase.Count);
    }

    [Fact]
    public void Generate_AllWordsAreInBip39Wordlist()
    {
        var wordSet = MnemonicService.Wordlist.ToHashSet(StringComparer.Ordinal);

        using var phrase = _sut.Generate().Value!;

        for (var i = 0; i < phrase.Count; i++)
        {
            Assert.Contains(phrase[i].ToString(), wordSet);
        }
    }

    [Fact]
    public void Generate_TwoCallsProduceDifferentMnemonics()
    {
        using var first = _sut.Generate().Value!;
        using var second = _sut.Generate().Value!;

        var anyDifferent = false;
        for (var i = 0; i < first.Count; i++)
        {
            if (!first[i].SequenceEqual(second[i]))
            {
                anyDifferent = true;
                break;
            }
        }

        Assert.True(anyDifferent,
            "Two Generate() calls produced identical mnemonics — RNG may be broken.");
    }

    [Fact]
    public void Validate_WithGeneratedMnemonic_Succeeds()
    {
        using var phrase = _sut.Generate().Value!;

        var result = _sut.Validate(phrase);

        Assert.True(result.Success);
    }

    [Fact]
    public void Validate_WrongWordCount_ReturnsInvalidMnemonic()
    {
        var words = new string[23];
        Array.Fill(words, "abandon");
        using var phrase = PhraseFrom(words);

        var result = _sut.Validate(phrase);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidMnemonic, result.Error!.Code);
    }

    [Fact]
    public void Validate_UnknownWord_ReturnsInvalidMnemonic()
    {
        var words = (string[])AllZerosMnemonicWords.Clone();
        words[0] = "xyzzy";
        using var phrase = PhraseFrom(words);

        var result = _sut.Validate(phrase);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidMnemonic, result.Error!.Code);
    }

    [Fact]
    public void Validate_BadChecksum_ReturnsInvalidMnemonic()
    {
        // Replacing "art" (last word of all-zeros vector) with "able" corrupts the checksum.
        var words = (string[])AllZerosMnemonicWords.Clone();
        words[23] = "able";
        using var phrase = PhraseFrom(words);

        var result = _sut.Validate(phrase);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidMnemonic, result.Error!.Code);
    }

    [Fact]
    public void Validate_KnownVector_Succeeds()
    {
        using var phrase = AllZerosPhrase();

        var result = _sut.Validate(phrase);

        Assert.True(result.Success);
    }

    [Fact]
    public void ToSeed_Returns64Bytes()
    {
        using var phrase = AllZerosPhrase();

        var result = _sut.ToSeed(phrase);

        Assert.True(result.Success);
        Assert.Equal(64, result.Value!.Length);
    }

    [Fact]
    public void ToSeed_IsDeterministic()
    {
        using var phrase1 = AllZerosPhrase();
        using var phrase2 = AllZerosPhrase();

        var first = _sut.ToSeed(phrase1).Value!;
        var second = _sut.ToSeed(phrase2).Value!;

        Assert.True(first.SequenceEqual(second));
    }

    [Fact]
    public void ToSeed_DifferentMnemonics_ProduceDifferentSeeds()
    {
        using var phrase1 = _sut.Generate().Value!;
        using var phrase2 = _sut.Generate().Value!;

        var seed1 = _sut.ToSeed(phrase1).Value!;
        var seed2 = _sut.ToSeed(phrase2).Value!;

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

        using var phrase = AllZerosPhrase();
        var seed = _sut.ToSeed(phrase).Value!;

        Assert.True(seed[..8].SequenceEqual(expectedPrefix),
            $"Expected seed prefix 408b285c12383600 but got {Convert.ToHexString(seed[..8]).ToLowerInvariant()}");
    }

    [Fact]
    public void ToSeed_InvalidWords_ReturnsInvalidMnemonic()
    {
        var words = new string[24];
        Array.Fill(words, "xyzzy");
        using var phrase = PhraseFrom(words);

        var result = _sut.ToSeed(phrase);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidMnemonic, result.Error!.Code);
    }
}
