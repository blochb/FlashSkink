using System.Buffers;
using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using Xunit;

namespace FlashSkink.Tests.Crypto;

public class BlobHeaderTests
{
    [Fact]
    public void Write_ThenParse_RoundTrips_FlagsAndNonce()
    {
        Span<byte> buf = stackalloc byte[BlobHeader.HeaderSize];
        Span<byte> nonce = stackalloc byte[BlobHeader.NonceSize];
        RandomNumberGenerator.Fill(nonce);

        BlobHeader.Write(buf, BlobFlags.CompressedLz4, nonce);
        Result result = BlobHeader.Parse(buf, out BlobFlags flags, out ReadOnlySpan<byte> parsedNonce);

        Assert.True(result.Success);
        Assert.Equal(BlobFlags.CompressedLz4, flags);
        Assert.True(parsedNonce.SequenceEqual(nonce));
    }

    [Fact]
    public void Parse_WithBadMagic_ReturnsVolumeCorrupt()
    {
        Span<byte> buf = stackalloc byte[BlobHeader.HeaderSize];
        buf[0] = (byte)'X'; buf[1] = (byte)'X'; buf[2] = (byte)'X'; buf[3] = (byte)'X';

        Result result = BlobHeader.Parse(buf, out _, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeCorrupt, result.Error!.Code);
    }

    [Fact]
    public void Parse_WithTooShortSpan_ReturnsVolumeCorrupt()
    {
        ReadOnlySpan<byte> buf = stackalloc byte[10];

        Result result = BlobHeader.Parse(buf, out _, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeCorrupt, result.Error!.Code);
    }

    [Fact]
    public void Parse_WithUnknownVersion_ReturnsVolumeIncompatibleVersion()
    {
        Span<byte> buf = stackalloc byte[BlobHeader.HeaderSize];
        "FSBL"u8.CopyTo(buf);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buf[4..], 2);

        Result result = BlobHeader.Parse(buf, out _, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeIncompatibleVersion, result.Error!.Code);
    }

    [Fact]
    public void Parse_WithVersion1AndNoneFlags_Succeeds()
    {
        Span<byte> buf = stackalloc byte[BlobHeader.HeaderSize];
        Span<byte> nonce = stackalloc byte[BlobHeader.NonceSize];
        BlobHeader.Write(buf, BlobFlags.None, nonce);

        Result result = BlobHeader.Parse(buf, out BlobFlags flags, out _);

        Assert.True(result.Success);
        Assert.Equal(BlobFlags.None, flags);
    }

    [Fact]
    public void Write_SetsCorrectMagicBytes()
    {
        Span<byte> buf = stackalloc byte[BlobHeader.HeaderSize];
        Span<byte> nonce = stackalloc byte[BlobHeader.NonceSize];
        BlobHeader.Write(buf, BlobFlags.None, nonce);

        Assert.Equal((byte)'F', buf[0]);
        Assert.Equal((byte)'S', buf[1]);
        Assert.Equal((byte)'B', buf[2]);
        Assert.Equal((byte)'L', buf[3]);
    }

    [Fact]
    public void Write_SetsVersionOne()
    {
        Span<byte> buf = stackalloc byte[BlobHeader.HeaderSize];
        Span<byte> nonce = stackalloc byte[BlobHeader.NonceSize];
        BlobHeader.Write(buf, BlobFlags.None, nonce);

        ushort version = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf[4..]);
        Assert.Equal(1, version);
    }

    // ── Header auth hardening ─────────────────────────────────────────────────

    [Fact]
    public void Parse_UnknownFlagBits_ReturnsVolumeCorrupt()
    {
        Span<byte> buf = stackalloc byte[BlobHeader.HeaderSize];
        Span<byte> nonce = stackalloc byte[BlobHeader.NonceSize];
        BlobHeader.Write(buf, BlobFlags.None, nonce);
        // overwrite flags with a value that has unknown bits (bits 2–7 set)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buf[6..], 0x00FF);

        Result result = BlobHeader.Parse(buf, out _, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeCorrupt, result.Error!.Code);
    }

    [Fact]
    public void Parse_AllKnownFlagBitsSet_Succeeds()
    {
        Span<byte> buf = stackalloc byte[BlobHeader.HeaderSize];
        Span<byte> nonce = stackalloc byte[BlobHeader.NonceSize];
        BlobHeader.Write(buf, BlobFlags.CompressedLz4 | BlobFlags.CompressedZstd, nonce);

        Result result = BlobHeader.Parse(buf, out BlobFlags flags, out _);

        Assert.True(result.Success);
        Assert.Equal(BlobFlags.CompressedLz4 | BlobFlags.CompressedZstd, flags);
    }
}

/// <summary>IMemoryOwner that wraps an exact-length byte array — no over-allocation.</summary>
file sealed class ExactMemoryOwner(int length) : IMemoryOwner<byte>
{
    private readonly byte[] _buf = new byte[length];
    public Memory<byte> Memory => _buf.AsMemory(0, length);
    public void Dispose() { }
}

public class CryptoPipelineTests
{
    private static readonly byte[] Dek = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Aad = "test-aad"u8.ToArray();
    private readonly CryptoPipeline _pipeline = new();

    private static IMemoryOwner<byte> RentForEncrypt(int plaintextLength) =>
        MemoryPool<byte>.Shared.Rent(BlobHeader.HeaderSize + plaintextLength + BlobHeader.TagSize);

    private static IMemoryOwner<byte> RentForDecrypt(int plaintextLength) =>
        MemoryPool<byte>.Shared.Rent(Math.Max(1, plaintextLength));

    private static IMemoryOwner<byte> ExactBuffer(int length) => new ExactMemoryOwner(length);

    [Fact]
    public void Encrypt_ThenDecrypt_ProducesOriginalPlaintext()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(256);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);
        using IMemoryOwner<byte> decBuf = RentForDecrypt(plaintext.Length);

        Result encResult = _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, encBuf, out int encBytes);
        Assert.True(encResult.Success);

        ReadOnlySpan<byte> blob = encBuf.Memory.Span[..encBytes];
        Assert.False(blob[BlobHeader.HeaderSize..].SequenceEqual(plaintext));

        Result decResult = _pipeline.Decrypt(blob, Dek, Aad, decBuf, out _, out int decBytes);
        Assert.True(decResult.Success);
        Assert.Equal(plaintext.Length, decBytes);
        Assert.True(decBuf.Memory.Span[..decBytes].SequenceEqual(plaintext));
    }

    [Fact]
    public void Encrypt_ThenDecrypt_EmptyPlaintext_Succeeds()
    {
        using IMemoryOwner<byte> encBuf = RentForEncrypt(0);
        using IMemoryOwner<byte> decBuf = MemoryPool<byte>.Shared.Rent(1);

        Result encResult = _pipeline.Encrypt([], Dek, Aad, BlobFlags.None, encBuf, out int encBytes);
        Assert.True(encResult.Success);
        Assert.Equal(BlobHeader.HeaderSize + BlobHeader.TagSize, encBytes);

        Result decResult = _pipeline.Decrypt(encBuf.Memory.Span[..encBytes], Dek, Aad, decBuf, out _, out int decBytes);
        Assert.True(decResult.Success);
        Assert.Equal(0, decBytes);
    }

    [Fact]
    public void Encrypt_OutputLength_IsHeaderPlusCiphertextPlusTag()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(100);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);

        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, encBuf, out int bytesWritten);

        Assert.Equal(BlobHeader.HeaderSize + plaintext.Length + BlobHeader.TagSize, bytesWritten);
    }

    [Fact]
    public void Encrypt_GeneratesFreshNonceEachCall()
    {
        byte[] plaintext = new byte[32];
        using IMemoryOwner<byte> buf1 = RentForEncrypt(plaintext.Length);
        using IMemoryOwner<byte> buf2 = RentForEncrypt(plaintext.Length);

        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, buf1, out int len1);
        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, buf2, out int len2);

        ReadOnlySpan<byte> nonce1 = buf1.Memory.Span.Slice(8, BlobHeader.NonceSize);
        ReadOnlySpan<byte> nonce2 = buf2.Memory.Span.Slice(8, BlobHeader.NonceSize);
        Assert.False(nonce1.SequenceEqual(nonce2));
    }

    [Fact]
    public void Decrypt_WithTamperedCiphertext_ReturnsDecryptionFailed()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(64);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);
        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, encBuf, out int encBytes);

        byte[] blob = encBuf.Memory.Span[..encBytes].ToArray();
        blob[BlobHeader.HeaderSize]++;

        using IMemoryOwner<byte> decBuf = RentForDecrypt(plaintext.Length);
        Result result = _pipeline.Decrypt(blob, Dek, Aad, decBuf, out _, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.DecryptionFailed, result.Error!.Code);
    }

    [Fact]
    public void Decrypt_WithWrongKey_ReturnsDecryptionFailed()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(64);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);
        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, encBuf, out int encBytes);

        byte[] wrongDek = RandomNumberGenerator.GetBytes(32);
        using IMemoryOwner<byte> decBuf = RentForDecrypt(plaintext.Length);
        Result result = _pipeline.Decrypt(encBuf.Memory.Span[..encBytes], wrongDek, Aad, decBuf, out _, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.DecryptionFailed, result.Error!.Code);
    }

    [Fact]
    public void Decrypt_WithTamperedTag_ReturnsDecryptionFailed()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(64);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);
        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, encBuf, out int encBytes);

        byte[] blob = encBuf.Memory.Span[..encBytes].ToArray();
        blob[^1]++;

        using IMemoryOwner<byte> decBuf = RentForDecrypt(plaintext.Length);
        Result result = _pipeline.Decrypt(blob, Dek, Aad, decBuf, out _, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.DecryptionFailed, result.Error!.Code);
    }

    [Fact]
    public void Decrypt_WithTamperedHeader_ReturnsVolumeCorrupt()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(64);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);
        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, encBuf, out int encBytes);

        byte[] blob = encBuf.Memory.Span[..encBytes].ToArray();
        blob[0] = (byte)'X';

        using IMemoryOwner<byte> decBuf = RentForDecrypt(plaintext.Length);
        Result result = _pipeline.Decrypt(blob, Dek, Aad, decBuf, out _, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeCorrupt, result.Error!.Code);
    }

    [Fact]
    public void Decrypt_WithWrongAad_ReturnsDecryptionFailed()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(64);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);
        _pipeline.Encrypt(plaintext, Dek, "aad-A"u8.ToArray(), BlobFlags.None, encBuf, out int encBytes);

        using IMemoryOwner<byte> decBuf = RentForDecrypt(plaintext.Length);
        Result result = _pipeline.Decrypt(encBuf.Memory.Span[..encBytes], Dek, "aad-B"u8.ToArray(), decBuf, out _, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.DecryptionFailed, result.Error!.Code);
    }

    [Fact]
    public void Encrypt_WithWrongDekLength_ReturnsUnknown()
    {
        byte[] shortDek = new byte[16];
        using IMemoryOwner<byte> buf = RentForEncrypt(32);

        Result result = _pipeline.Encrypt(new byte[32], shortDek, Aad, BlobFlags.None, buf, out int bytesWritten);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Unknown, result.Error!.Code);
        Assert.Equal(0, bytesWritten);
    }

    [Fact]
    public void Encrypt_WithOutputBufferTooSmall_ReturnsUnknown()
    {
        byte[] plaintext = new byte[64];
        int needed = BlobHeader.HeaderSize + plaintext.Length + BlobHeader.TagSize;
        using IMemoryOwner<byte> buf = ExactBuffer(needed - 1);

        Result result = _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, buf, out int bytesWritten);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Unknown, result.Error!.Code);
        Assert.Equal(0, bytesWritten);
    }

    [Fact]
    public void Decrypt_WithBlobTooShort_ReturnsVolumeCorrupt()
    {
        byte[] tooShort = new byte[BlobHeader.HeaderSize + BlobHeader.TagSize - 1];
        using IMemoryOwner<byte> decBuf = MemoryPool<byte>.Shared.Rent(1);

        Result result = _pipeline.Decrypt(tooShort, Dek, Aad, decBuf, out _, out int bytesWritten);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeCorrupt, result.Error!.Code);
        Assert.Equal(0, bytesWritten);
    }

    [Fact]
    public void Decrypt_WithOutputBufferTooSmall_ReturnsUnknown()
    {
        byte[] plaintext = new byte[64];
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);
        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, encBuf, out int encBytes);

        using IMemoryOwner<byte> tinyBuf = ExactBuffer(plaintext.Length - 1);
        Result result = _pipeline.Decrypt(encBuf.Memory.Span[..encBytes], Dek, Aad, tinyBuf, out _, out int bytesWritten);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Unknown, result.Error!.Code);
        Assert.Equal(0, bytesWritten);
    }

    [Fact]
    public void Decrypt_WithWrongDekLength_ReturnsUnknown()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(64);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);
        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, encBuf, out int encBytes);

        byte[] shortDek = new byte[16];
        using IMemoryOwner<byte> decBuf = RentForDecrypt(plaintext.Length);
        Result result = _pipeline.Decrypt(encBuf.Memory.Span[..encBytes], shortDek, Aad, decBuf, out _, out int bytesWritten);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Unknown, result.Error!.Code);
        Assert.Equal(0, bytesWritten);
    }

    // ── New tests for §2.5 — BlobFlags round-trip via Encrypt ────────────────

    [Fact]
    public void Encrypt_WithCompressedLz4Flag_HeaderEncodesFlag()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(64);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);

        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.CompressedLz4, encBuf, out int encBytes);

        Result parseResult = BlobHeader.Parse(encBuf.Memory.Span[..encBytes], out BlobFlags flags, out _);
        Assert.True(parseResult.Success);
        Assert.Equal(BlobFlags.CompressedLz4, flags);
    }

    [Fact]
    public void Encrypt_WithCompressedZstdFlag_HeaderEncodesFlag()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(64);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);

        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.CompressedZstd, encBuf, out int encBytes);

        Result parseResult = BlobHeader.Parse(encBuf.Memory.Span[..encBytes], out BlobFlags flags, out _);
        Assert.True(parseResult.Success);
        Assert.Equal(BlobFlags.CompressedZstd, flags);
    }

    [Fact]
    public void EncryptThenDecrypt_FlagsRoundTrip()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(64);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);
        using IMemoryOwner<byte> decBuf = RentForDecrypt(plaintext.Length);

        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.CompressedLz4, encBuf, out int encBytes);

        Result decResult = _pipeline.Decrypt(
            encBuf.Memory.Span[..encBytes], Dek, Aad, decBuf, out BlobFlags outFlags, out int decBytes);
        Assert.True(decResult.Success);
        Assert.Equal(BlobFlags.CompressedLz4, outFlags);
        Assert.Equal(plaintext.Length, decBytes);
        Assert.True(decBuf.Memory.Span[..decBytes].SequenceEqual(plaintext));
    }

    // ── Header auth hardening ─────────────────────────────────────────────────

    [Fact]
    public void Decrypt_TamperedFlagByte_ReturnsDecryptionFailed()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(64);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);
        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, encBuf, out int encBytes);

        byte[] blob = encBuf.Memory.Span[..encBytes].ToArray();
        blob[6] ^= 0x01; // flip CompressedLz4 bit in flags low byte

        using IMemoryOwner<byte> decBuf = RentForDecrypt(plaintext.Length);
        Result result = _pipeline.Decrypt(blob, Dek, Aad, decBuf, out _, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.DecryptionFailed, result.Error!.Code);
    }

    [Fact]
    public void Decrypt_TamperedVersionByte_ReturnsVolumeIncompatibleVersion()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(64);
        using IMemoryOwner<byte> encBuf = RentForEncrypt(plaintext.Length);
        _pipeline.Encrypt(plaintext, Dek, Aad, BlobFlags.None, encBuf, out int encBytes);

        byte[] blob = encBuf.Memory.Span[..encBytes].ToArray();
        blob[4] = 0x02; // set version to 2

        using IMemoryOwner<byte> decBuf = RentForDecrypt(plaintext.Length);
        Result result = _pipeline.Decrypt(blob, Dek, Aad, decBuf, out _, out _);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeIncompatibleVersion, result.Error!.Code);
    }
}
