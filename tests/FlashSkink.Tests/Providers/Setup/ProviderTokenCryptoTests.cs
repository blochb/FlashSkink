using System.Security.Cryptography;
using System.Text;
using FlashSkink.Core.Providers.Setup;
using Xunit;

namespace FlashSkink.Tests.Providers.Setup;

/// <summary>
/// Unit tests for <see cref="ProviderTokenCrypto"/>'s AES-256-GCM envelope. PR §4.1.
/// </summary>
public sealed class ProviderTokenCryptoTests
{
    private static byte[] RandomDek() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Encrypt_ThenTryDecrypt_RoundTripsPlaintext()
    {
        var dek = RandomDek();
        const string plaintext = "ya29.refresh-token-blob-xxx-1234567890";

        var envelope = ProviderTokenCrypto.Encrypt(plaintext, dek);
        Assert.True(ProviderTokenCrypto.TryDecrypt(envelope, dek, out var recovered));

        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void Encrypt_EmptyPlaintext_RoundTrips()
    {
        var dek = RandomDek();

        var envelope = ProviderTokenCrypto.Encrypt(string.Empty, dek);
        // Envelope is still 1 (version) + 12 (nonce) + 0 (ciphertext) + 16 (tag) = 29 bytes.
        Assert.Equal(29, envelope.Length);

        Assert.True(ProviderTokenCrypto.TryDecrypt(envelope, dek, out var recovered));
        Assert.Equal(string.Empty, recovered);
    }

    [Fact]
    public void TryDecrypt_WrongDek_ReturnsFalse()
    {
        var dekA = RandomDek();
        var dekB = RandomDek();
        var envelope = ProviderTokenCrypto.Encrypt("hello", dekA);

        Assert.False(ProviderTokenCrypto.TryDecrypt(envelope, dekB, out var recovered));
        Assert.Equal(string.Empty, recovered);
    }

    [Fact]
    public void TryDecrypt_TamperedCiphertext_ReturnsFalse()
    {
        var dek = RandomDek();
        var envelope = ProviderTokenCrypto.Encrypt("plaintext-of-some-length", dek);

        // Flip a byte inside the ciphertext region: index 13 (just past version + nonce).
        envelope[13] ^= 0xFF;

        Assert.False(ProviderTokenCrypto.TryDecrypt(envelope, dek, out var recovered));
        Assert.Equal(string.Empty, recovered);
    }

    [Fact]
    public void TryDecrypt_TamperedTag_ReturnsFalse()
    {
        var dek = RandomDek();
        var envelope = ProviderTokenCrypto.Encrypt("plaintext", dek);

        // Flip the last byte (inside the 16-byte tag region).
        envelope[^1] ^= 0xFF;

        Assert.False(ProviderTokenCrypto.TryDecrypt(envelope, dek, out var recovered));
        Assert.Equal(string.Empty, recovered);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(28)]   // one byte short of the 29-byte minimum
    public void TryDecrypt_TruncatedEnvelope_ReturnsFalse(int length)
    {
        var dek = RandomDek();
        var truncated = new byte[length];

        Assert.False(ProviderTokenCrypto.TryDecrypt(truncated, dek, out var recovered));
        Assert.Equal(string.Empty, recovered);
    }

    [Fact]
    public void TryDecrypt_WrongVersionByte_ReturnsFalse()
    {
        var dek = RandomDek();
        var envelope = ProviderTokenCrypto.Encrypt("plaintext", dek);

        envelope[0] = 0x02;

        Assert.False(ProviderTokenCrypto.TryDecrypt(envelope, dek, out var recovered));
        Assert.Equal(string.Empty, recovered);
    }

    [Fact]
    public void Encrypt_TwoCallsSamePlaintext_ProduceDifferentCiphertexts()
    {
        var dek = RandomDek();
        const string plaintext = "same plaintext";

        var envelope1 = ProviderTokenCrypto.Encrypt(plaintext, dek);
        var envelope2 = ProviderTokenCrypto.Encrypt(plaintext, dek);

        // Same version byte, but the nonce + ciphertext + tag regions must all differ thanks
        // to the random nonce.
        Assert.Equal(envelope1.Length, envelope2.Length);
        Assert.NotEqual(envelope1, envelope2);

        // Both still decrypt to the same plaintext.
        Assert.True(ProviderTokenCrypto.TryDecrypt(envelope1, dek, out var r1));
        Assert.True(ProviderTokenCrypto.TryDecrypt(envelope2, dek, out var r2));
        Assert.Equal(plaintext, r1);
        Assert.Equal(plaintext, r2);
    }

    [Fact]
    public void Encrypt_Output_HasExpectedLength()
    {
        var dek = RandomDek();
        const string plaintext = "12345";  // 5 bytes in UTF-8
        var utf8Length = Encoding.UTF8.GetByteCount(plaintext);

        var envelope = ProviderTokenCrypto.Encrypt(plaintext, dek);

        // 1 byte version + 12 byte nonce + utf8Length byte ciphertext + 16 byte tag.
        Assert.Equal(1 + 12 + utf8Length + 16, envelope.Length);
        Assert.Equal(ProviderTokenCrypto.CurrentVersion, envelope[0]);
    }

    [Fact]
    public void Encrypt_NonAsciiPlaintext_RoundTrips()
    {
        var dek = RandomDek();
        // Refresh tokens are ASCII in practice, but the envelope must handle multi-byte UTF-8
        // for the rare case where a provider returns a token with non-ASCII characters.
        const string plaintext = "ya29.refresh-токен-with-русский-and-日本語";

        var envelope = ProviderTokenCrypto.Encrypt(plaintext, dek);
        Assert.True(ProviderTokenCrypto.TryDecrypt(envelope, dek, out var recovered));
        Assert.Equal(plaintext, recovered);
    }
}
