using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using Xunit;

namespace FlashSkink.Tests.Crypto;

public class KeyDerivationServiceTests
{
    private readonly KeyDerivationService _sut = new();

    private static byte[] Filled(int length, byte value)
    {
        var b = new byte[length];
        Array.Fill(b, value);
        return b;
    }

    private static readonly byte[] FixedSeed = Filled(64, 0x42);
    private static readonly byte[] FixedSalt = Filled(32, 0x07);
    private static readonly byte[] AltSeed = Filled(64, 0x99);
    private static readonly byte[] AltSalt = Filled(32, 0xAB);
    private static readonly byte[] FixedDek = Filled(32, 0x55);
    private static readonly byte[] AltDek = Filled(32, 0xAA);

    [Fact]
    public void DeriveKek_ProducesKekOf32Bytes()
    {
        var result = _sut.DeriveKek(FixedSeed, FixedSalt, out var kek);

        Assert.True(result.Success);
        Assert.Equal(32, kek.Length);
    }

    [Fact]
    public void DeriveKek_IsDeterministic()
    {
        _sut.DeriveKek(FixedSeed, FixedSalt, out var kek1);
        _sut.DeriveKek(FixedSeed, FixedSalt, out var kek2);

        Assert.True(kek1.SequenceEqual(kek2));
    }

    [Fact]
    public void DeriveKek_DifferentSalt_ProducesDifferentKek()
    {
        _sut.DeriveKek(FixedSeed, FixedSalt, out var kek1);
        _sut.DeriveKek(FixedSeed, AltSalt, out var kek2);

        Assert.False(kek1.SequenceEqual(kek2));
    }

    [Fact]
    public void DeriveKek_DifferentSeed_ProducesDifferentKek()
    {
        _sut.DeriveKek(FixedSeed, FixedSalt, out var kek1);
        _sut.DeriveKek(AltSeed, FixedSalt, out var kek2);

        Assert.False(kek1.SequenceEqual(kek2));
    }

    [Fact]
    public void DeriveKekFromPassword_ProducesKekOf32Bytes()
    {
        var result = _sut.DeriveKekFromPassword(FixedSeed, FixedSalt, out var kek);

        Assert.True(result.Success);
        Assert.Equal(32, kek.Length);
    }

    [Fact]
    public void DeriveKekFromPassword_IsDeterministic()
    {
        _sut.DeriveKekFromPassword(FixedSeed, FixedSalt, out var kek1);
        _sut.DeriveKekFromPassword(FixedSeed, FixedSalt, out var kek2);

        Assert.True(kek1.SequenceEqual(kek2));
    }

    [Fact]
    public void DeriveKekFromPassword_DifferentPassword_ProducesDifferentKek()
    {
        _sut.DeriveKekFromPassword(FixedSeed, FixedSalt, out var kek1);
        _sut.DeriveKekFromPassword(AltSeed, FixedSalt, out var kek2);

        Assert.False(kek1.SequenceEqual(kek2));
    }

    [Fact]
    public void DeriveKekFromPassword_AndDeriveKek_WithSameBytesAndSalt_ProduceSameKek()
    {
        // Both overloads must produce the same output given identical input bytes.
        _sut.DeriveKek(FixedSeed, FixedSalt, out var kekFromSeed);
        _sut.DeriveKekFromPassword(FixedSeed, FixedSalt, out var kekFromPassword);

        Assert.True(kekFromSeed.SequenceEqual(kekFromPassword));
    }

    [Fact]
    public void DeriveBrainKey_ProducesExactly32Bytes()
    {
        var destination = new byte[32];

        var result = _sut.DeriveBrainKey(FixedDek, destination);

        Assert.True(result.Success);
        Assert.False(destination.All(b => b == 0), "Brain key output should be non-zero.");
    }

    [Fact]
    public void DeriveBrainKey_IsDeterministic()
    {
        var dest1 = new byte[32];
        var dest2 = new byte[32];

        _sut.DeriveBrainKey(FixedDek, dest1);
        _sut.DeriveBrainKey(FixedDek, dest2);

        Assert.True(dest1.SequenceEqual(dest2));
    }

    [Fact]
    public void DeriveBrainKey_DifferentDek_ProducesDifferentKey()
    {
        var dest1 = new byte[32];
        var dest2 = new byte[32];

        _sut.DeriveBrainKey(FixedDek, dest1);
        _sut.DeriveBrainKey(AltDek, dest2);

        Assert.False(dest1.SequenceEqual(dest2));
    }

    [Fact]
    public void DeriveBrainKey_WrongDestinationLength_ReturnsKeyDerivationFailed()
    {
        var destination = new byte[16];

        var result = _sut.DeriveBrainKey(FixedDek, destination);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.KeyDerivationFailed, result.Error!.Code);
    }
}

