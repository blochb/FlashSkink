using System.Security.Cryptography;

namespace FlashSkink.Core.Identity;

/// <summary>
/// AES-256-GCM envelope crypto for the witness file (dev plan §3.5.1, Blueprint §19.6 —
/// added in §3.5.2). A standalone helper rather than reusing <c>CryptoPipeline</c>
/// because the witness is small infrastructure metadata (~200 bytes plaintext), not a
/// user blob: the full pipeline format (compression header, per-blob layout, incremental
/// hash) would be wasteful, and adding an <c>Identity → Crypto</c> reference would couple
/// two otherwise-independent areas.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Envelope format:</strong>
/// <code>
/// [1 byte : version (currently 0x01)]
/// [12 bytes: random nonce (RandomNumberGenerator.GetBytes)]
/// [N bytes : AES-256-GCM ciphertext of the UTF-8 plaintext]
/// [16 bytes: AES-256-GCM authentication tag]
/// </code>
/// The version byte reserves room for a future re-key or format change; current readers
/// reject anything other than <c>0x01</c>.
/// </para>
/// <para>
/// <strong>Associated data (AAD):</strong> empty. The <see cref="WitnessPayload.VolumeId"/>
/// binding lives inside the plaintext and is checked by the §3.5.2 handshake — a separate
/// AAD binding was considered but rejected as added complexity for no correctness gain.
/// The ciphertext authenticity already proves the DEK holder wrote this file.
/// </para>
/// <para>
/// <strong>Nonce randomness:</strong> a 12-byte random nonce gives birthday-bound
/// collision probability after ~2^48 encryptions per key. The witness is written at most
/// once per <c>OpenAsync</c> per tail; the bound is effectively infinite. (Principle 26
/// — no secrets in logs: this helper never logs the DEK, the nonce, the plaintext, or
/// the ciphertext.)
/// </para>
/// </remarks>
internal static class WitnessCrypto
{
    /// <summary>The envelope format version currently emitted by <see cref="Encrypt"/>.</summary>
    internal const byte CurrentVersion = 0x01;

    /// <summary>AES-GCM nonce size in bytes (matches <see cref="AesGcm.NonceByteSizes"/> default).</summary>
    internal const int NonceSize = 12;

    /// <summary>AES-GCM authentication tag size in bytes (matches <see cref="AesGcm.TagByteSizes"/> default).</summary>
    internal const int TagSize = 16;

    /// <summary>Total envelope-overhead bytes: 1 version + 12 nonce + 16 tag.</summary>
    private const int Overhead = 1 + NonceSize + TagSize;

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> into a fresh envelope using
    /// <paramref name="dek"/> as the AES-256-GCM key. The nonce is freshly random per call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Preconditions: <paramref name="dek"/> must be 32 bytes. The caller is internal and
    /// the contract is enforced by <see cref="AesGcm"/>'s constructor — passing a wrong-sized
    /// key throws <see cref="CryptographicException"/>, which is a programming error here
    /// (the DEK comes from the unlocked vault and is guaranteed 32 bytes).
    /// </para>
    /// <para>
    /// Allocates the envelope as a single <see cref="byte"/> array of length
    /// <c>1 + 12 + plaintext.Length + 16</c>. The version byte and nonce are written in
    /// place; <see cref="AesGcm.Encrypt(ReadOnlySpan{byte}, ReadOnlySpan{byte}, Span{byte}, Span{byte}, ReadOnlySpan{byte})"/>
    /// writes ciphertext and tag into their slices.
    /// </para>
    /// </remarks>
    internal static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> dek)
    {
        var envelope = new byte[Overhead + plaintext.Length];
        envelope[0] = CurrentVersion;

        // Slice the envelope into its regions. Spans are zero-cost views — no copy.
        var nonce = envelope.AsSpan(1, NonceSize);
        var ciphertext = envelope.AsSpan(1 + NonceSize, plaintext.Length);
        var tag = envelope.AsSpan(1 + NonceSize + plaintext.Length, TagSize);

        // RandomNumberGenerator.GetBytes(Span<byte>) is the .NET 6+ allocation-free overload.
        RandomNumberGenerator.Fill(nonce);

        // AesGcm holds the key in unmanaged memory; using ensures it's wiped promptly
        // (Principle 31 — keys zeroed on release).
        using var aes = new AesGcm(dek, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData: ReadOnlySpan<byte>.Empty);

        return envelope;
    }

    /// <summary>
    /// Attempts to decrypt <paramref name="envelope"/> using <paramref name="dek"/>.
    /// Returns <see langword="false"/> for any failure mode: truncated envelope, unknown
    /// version byte, wrong DEK, tampered ciphertext, or tampered tag. Never throws.
    /// </summary>
    /// <param name="envelope">The full envelope as produced by <see cref="Encrypt"/>.</param>
    /// <param name="dek">The 32-byte DEK to decrypt with.</param>
    /// <param name="plaintext">
    /// On success, the decrypted UTF-8 bytes. On failure, an empty array (the caller
    /// should not inspect it).
    /// </param>
    /// <remarks>
    /// Callers — <c>WitnessStore.TryReadAsync</c> — interpret a <see langword="false"/>
    /// return as "this tail has no usable witness," treating it identically to "witness
    /// file absent." The §3.5.2 handshake then proceeds as if no information were
    /// available from this tail.
    /// </remarks>
    internal static bool TryDecrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> dek, out byte[] plaintext)
    {
        plaintext = [];

        if (envelope.Length < Overhead)
        {
            return false;
        }
        if (envelope[0] != CurrentVersion)
        {
            return false;
        }

        var ciphertextLength = envelope.Length - Overhead;
        var nonce = envelope.Slice(1, NonceSize);
        var ciphertext = envelope.Slice(1 + NonceSize, ciphertextLength);
        var tag = envelope.Slice(1 + NonceSize + ciphertextLength, TagSize);

        var buffer = new byte[ciphertextLength];
        try
        {
            using var aes = new AesGcm(dek, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, buffer, associatedData: ReadOnlySpan<byte>.Empty);
        }
        catch (CryptographicException)
        {
            // Wrong key, tampered ciphertext, or tampered tag — same outcome: no usable witness.
            return false;
        }

        plaintext = buffer;
        return true;
    }
}
