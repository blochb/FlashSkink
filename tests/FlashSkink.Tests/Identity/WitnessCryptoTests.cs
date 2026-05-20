using System.Security.Cryptography;
using FlashSkink.Core.Identity;
using Xunit;

namespace FlashSkink.Tests.Identity;

/// <summary>
/// Unit tests for <see cref="WitnessCrypto"/>'s AES-256-GCM envelope. Dev plan §3.5.1.
/// </summary>
public sealed class WitnessCryptoTests
{
    private static byte[] RandomDek() => RandomNumberGenerator.GetBytes(32);

    private static byte[] RandomPlaintext(int length)
    {
        var buf = new byte[length];
        RandomNumberGenerator.Fill(buf);
        return buf;
    }

    [Fact]
    public void Encrypt_ThenTryDecrypt_ReturnsOriginalPlaintext()
    {
        var dek = RandomDek();
        var plaintext = RandomPlaintext(200);

        var envelope = WitnessCrypto.Encrypt(plaintext, dek);
        Assert.True(WitnessCrypto.TryDecrypt(envelope, dek, out var recovered));

        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void Encrypt_EmptyPlaintext_RoundTrips()
    {
        var dek = RandomDek();
        var plaintext = Array.Empty<byte>();

        var envelope = WitnessCrypto.Encrypt(plaintext, dek);
        // Envelope is still 1 (version) + 12 (nonce) + 0 (ciphertext) + 16 (tag) = 29 bytes.
        Assert.Equal(29, envelope.Length);

        Assert.True(WitnessCrypto.TryDecrypt(envelope, dek, out var recovered));
        Assert.Empty(recovered);
    }

    [Fact]
    public void TryDecrypt_WrongDek_ReturnsFalse()
    {
        var dekA = RandomDek();
        var dekB = RandomDek();
        var plaintext = RandomPlaintext(50);

        var envelope = WitnessCrypto.Encrypt(plaintext, dekA);
        Assert.False(WitnessCrypto.TryDecrypt(envelope, dekB, out var recovered));
        Assert.Empty(recovered);
    }

    [Fact]
    public void TryDecrypt_TamperedCiphertext_ReturnsFalse()
    {
        var dek = RandomDek();
        var plaintext = RandomPlaintext(100);
        var envelope = WitnessCrypto.Encrypt(plaintext, dek);

        // Flip a bit in the ciphertext region (after the 1-byte version + 12-byte nonce).
        envelope[1 + 12 + 5] ^= 0x01;

        Assert.False(WitnessCrypto.TryDecrypt(envelope, dek, out _));
    }

    [Fact]
    public void TryDecrypt_TamperedTag_ReturnsFalse()
    {
        var dek = RandomDek();
        var plaintext = RandomPlaintext(100);
        var envelope = WitnessCrypto.Encrypt(plaintext, dek);

        // Flip a bit in the trailing 16-byte tag region.
        envelope[envelope.Length - 1] ^= 0x01;

        Assert.False(WitnessCrypto.TryDecrypt(envelope, dek, out _));
    }

    [Fact]
    public void TryDecrypt_TamperedNonce_ReturnsFalse()
    {
        var dek = RandomDek();
        var plaintext = RandomPlaintext(100);
        var envelope = WitnessCrypto.Encrypt(plaintext, dek);

        // Flip a bit in the nonce region (offset 1).
        envelope[5] ^= 0x01;

        Assert.False(WitnessCrypto.TryDecrypt(envelope, dek, out _));
    }

    [Fact]
    public void TryDecrypt_TruncatedEnvelope_ReturnsFalse()
    {
        var dek = RandomDek();

        // Envelope shorter than the minimum overhead (1 + 12 + 16 = 29 bytes).
        var truncated = new byte[5];
        Assert.False(WitnessCrypto.TryDecrypt(truncated, dek, out _));
    }

    [Fact]
    public void TryDecrypt_WrongVersionByte_ReturnsFalse()
    {
        var dek = RandomDek();
        var plaintext = RandomPlaintext(50);
        var envelope = WitnessCrypto.Encrypt(plaintext, dek);

        envelope[0] = 0x02; // unknown version

        Assert.False(WitnessCrypto.TryDecrypt(envelope, dek, out _));
    }

    [Fact]
    public void TryDecrypt_EmptyInput_ReturnsFalse()
    {
        var dek = RandomDek();
        Assert.False(WitnessCrypto.TryDecrypt(ReadOnlySpan<byte>.Empty, dek, out _));
    }

    [Fact]
    public void Encrypt_TwoCallsSamePlaintextSameDek_ProduceDifferentEnvelopes()
    {
        // Nonce is random per call — the envelope as a whole must differ even when
        // the plaintext and key are byte-identical.
        var dek = RandomDek();
        var plaintext = RandomPlaintext(100);

        var first = WitnessCrypto.Encrypt(plaintext, dek);
        var second = WitnessCrypto.Encrypt(plaintext, dek);

        Assert.Equal(first.Length, second.Length);
        Assert.NotEqual(first, second);

        // Both still decrypt to the same plaintext.
        Assert.True(WitnessCrypto.TryDecrypt(first, dek, out var firstPt));
        Assert.True(WitnessCrypto.TryDecrypt(second, dek, out var secondPt));
        Assert.Equal(plaintext, firstPt);
        Assert.Equal(plaintext, secondPt);
    }

    [Fact]
    public void EnvelopeLayout_VersionByteThenNonceThenCiphertextThenTag()
    {
        var dek = RandomDek();
        var plaintext = RandomPlaintext(40);

        var envelope = WitnessCrypto.Encrypt(plaintext, dek);

        Assert.Equal(WitnessCrypto.CurrentVersion, envelope[0]);
        Assert.Equal(1 + WitnessCrypto.NonceSize + plaintext.Length + WitnessCrypto.TagSize, envelope.Length);
    }
}
