using System.Buffers;
using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Crypto;

/// <summary>
/// Stateless AES-256-GCM encrypt/decrypt over caller-supplied memory. Generates a fresh
/// random nonce per encryption. The caller owns all buffer lifetimes; this type never
/// allocates output buffers.
/// </summary>
public sealed class CryptoPipeline
{
    private const int DekBytes = 32;

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> with AES-256-GCM and writes the result into
    /// <paramref name="outputOwner"/>.
    /// </summary>
    /// <remarks>
    /// Output layout: <c>[Header (20B)] || [Ciphertext (plaintext.Length B)] || [Tag (16B)]</c>.
    /// The caller must size <paramref name="outputOwner"/> to at least
    /// <c>BlobHeader.HeaderSize + plaintext.Length + BlobHeader.TagSize</c> bytes.
    /// </remarks>
    /// <param name="plaintext">The bytes to encrypt. When compression was applied this is the
    /// compressed payload; <paramref name="flags"/> records which algorithm was used.</param>
    /// <param name="dek">The 256-bit (32-byte) Data Encryption Key.</param>
    /// <param name="aad">Additional authenticated data — 48 bytes: 16-byte raw BlobID GUID
    /// followed by 32-byte raw plaintext SHA-256 digest (cross-cutting decision 3).</param>
    /// <param name="flags">Compression flags to encode in the blob header.
    /// Pass <see cref="BlobFlags.None"/> when the payload is not compressed.</param>
    /// <param name="outputOwner">Caller-supplied output buffer; must be large enough.</param>
    /// <param name="bytesWritten">On success: total bytes written. On failure: 0.</param>
    /// <returns>
    /// <see cref="Result.Ok()"/> on success.
    /// <see cref="ErrorCode.EncryptionFailed"/> on <see cref="CryptographicException"/>.
    /// <see cref="ErrorCode.Unknown"/> on precondition violation or unexpected exception.
    /// </returns>
    public Result Encrypt(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> dek,
        ReadOnlySpan<byte> aad,
        BlobFlags flags,
        IMemoryOwner<byte> outputOwner,
        out int bytesWritten)
    {
        bytesWritten = 0;
        int requiredLength = BlobHeader.HeaderSize + plaintext.Length + BlobHeader.TagSize;

        if (dek.Length != DekBytes)
        {
            return Result.Fail(ErrorCode.Unknown,
                $"DEK must be exactly {DekBytes} bytes; got {dek.Length}.");
        }

        if (outputOwner.Memory.Length < requiredLength)
        {
            return Result.Fail(ErrorCode.Unknown,
                $"Output buffer is too small; need {requiredLength} bytes, got {outputOwner.Memory.Length}.");
        }

        if (aad.Length > 512)
        {
            return Result.Fail(ErrorCode.Unknown, "AAD is too long.");
        }

        Span<byte> output = outputOwner.Memory.Span;

        try
        {
            Span<byte> nonce = stackalloc byte[BlobHeader.NonceSize];
            RandomNumberGenerator.Fill(nonce);

            BlobHeader.Write(output[..BlobHeader.HeaderSize], flags, nonce);

            Span<byte> fullAad = stackalloc byte[BlobHeader.HeaderSize + aad.Length];
            output[..BlobHeader.HeaderSize].CopyTo(fullAad);
            aad.CopyTo(fullAad[BlobHeader.HeaderSize..]);

            Span<byte> ciphertext = output[BlobHeader.HeaderSize..(BlobHeader.HeaderSize + plaintext.Length)];
            Span<byte> tag = output[(BlobHeader.HeaderSize + plaintext.Length)..requiredLength];

            using var aes = new AesGcm(dek, BlobHeader.TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, fullAad);

            bytesWritten = requiredLength;
            return Result.Ok();
        }
        catch (CryptographicException ex)
        {
            return Result.Fail(ErrorCode.EncryptionFailed, "AES-GCM encryption failed.", ex);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCode.Unknown, "Unexpected error during encryption.", ex);
        }
    }

    /// <summary>
    /// Decrypts an AES-256-GCM blob and writes the plaintext into <paramref name="outputOwner"/>.
    /// </summary>
    /// <remarks>
    /// The blob must begin with the 20-byte header written by <see cref="Encrypt"/> and end
    /// with the 16-byte GCM tag. The caller must size <paramref name="outputOwner"/> to at
    /// least <c>blob.Length - BlobHeader.HeaderSize - BlobHeader.TagSize</c> bytes.
    /// </remarks>
    /// <param name="blob">The full on-disk blob: header + ciphertext + tag.</param>
    /// <param name="dek">The 256-bit (32-byte) Data Encryption Key.</param>
    /// <param name="aad">Additional authenticated data; must match what was passed to <see cref="Encrypt"/>.</param>
    /// <param name="outputOwner">Caller-supplied output buffer; must be large enough.</param>
    /// <param name="flags">On success: the compression flags from the blob header. The caller uses this to determine whether decompression is needed.</param>
    /// <param name="bytesWritten">On success: plaintext byte count. On failure: 0.</param>
    /// <returns>
    /// <see cref="Result.Ok()"/> on success.
    /// <see cref="ErrorCode.VolumeCorrupt"/> on a structurally invalid blob (too short or wrong magic).
    /// <see cref="ErrorCode.VolumeIncompatibleVersion"/> on an unsupported blob version.
    /// <see cref="ErrorCode.DecryptionFailed"/> when the GCM authentication tag does not match.
    /// <see cref="ErrorCode.Unknown"/> on precondition violation or unexpected exception.
    /// </returns>
    public Result Decrypt(
        ReadOnlySpan<byte> blob,
        ReadOnlySpan<byte> dek,
        ReadOnlySpan<byte> aad,
        IMemoryOwner<byte> outputOwner,
        out BlobFlags flags,
        out int bytesWritten)
    {
        flags = BlobFlags.None;
        bytesWritten = 0;
        int minBlobLength = BlobHeader.HeaderSize + BlobHeader.TagSize;

        if (dek.Length != DekBytes)
        {
            return Result.Fail(ErrorCode.Unknown,
                $"DEK must be exactly {DekBytes} bytes; got {dek.Length}.");
        }

        if (aad.Length > 512)
        {
            return Result.Fail(ErrorCode.Unknown, "AAD is too long.");
        }

        if (blob.Length < minBlobLength)
        {
            return Result.Fail(ErrorCode.VolumeCorrupt,
                $"Blob is too short; minimum is {minBlobLength} bytes, got {blob.Length}.");
        }

        Result parseResult = BlobHeader.Parse(blob, out flags, out ReadOnlySpan<byte> nonce);
        if (!parseResult.Success)
        {
            return parseResult;
        }

        int ciphertextLength = blob.Length - BlobHeader.HeaderSize - BlobHeader.TagSize;

        if (outputOwner.Memory.Length < ciphertextLength)
        {
            return Result.Fail(ErrorCode.Unknown,
                $"Output buffer is too small; need {ciphertextLength} bytes, got {outputOwner.Memory.Length}.");
        }

        ReadOnlySpan<byte> ciphertext = blob[BlobHeader.HeaderSize..(BlobHeader.HeaderSize + ciphertextLength)];
        ReadOnlySpan<byte> tag = blob[(BlobHeader.HeaderSize + ciphertextLength)..];
        Span<byte> plaintext = outputOwner.Memory.Span[..ciphertextLength];

        try
        {
            Span<byte> fullAad = stackalloc byte[BlobHeader.HeaderSize + aad.Length];
            blob[..BlobHeader.HeaderSize].CopyTo(fullAad);
            aad.CopyTo(fullAad[BlobHeader.HeaderSize..]);

            using var aes = new AesGcm(dek, BlobHeader.TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, fullAad);

            bytesWritten = ciphertextLength;
            return Result.Ok();
        }
        catch (CryptographicException ex)
        {
            return Result.Fail(ErrorCode.DecryptionFailed,
                "AES-GCM authentication failed — blob may be tampered.", ex);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCode.Unknown, "Unexpected error during decryption.", ex);
        }
    }
}
