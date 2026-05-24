using System.Security.Cryptography;
using System.Text;

namespace FlashSkink.Core.Providers.Setup;

/// <summary>
/// AES-256-GCM envelope crypto for DEK-encrypting OAuth refresh tokens and OAuth client secrets
/// before persisting them to the <c>Providers</c> brain row. Cross-cutting decision 2 of
/// phase-4-providers: reuses the same binary envelope shape as
/// <see cref="Identity.WitnessCrypto"/>, implemented independently to keep the two helpers
/// decoupled.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Envelope format</strong> — binary-compatible with <see cref="Identity.WitnessCrypto"/>:
/// <code>
/// [1 byte : version (currently 0x01)]
/// [12 bytes: random nonce (RandomNumberGenerator.Fill)]
/// [N bytes : AES-256-GCM ciphertext of the UTF-8 plaintext]
/// [16 bytes: AES-256-GCM authentication tag]
/// </code>
/// The version byte reserves room for a future re-key or format change; current readers reject
/// anything other than <c>0x01</c>.
/// </para>
/// <para>
/// <strong>Associated data (AAD):</strong> empty. The binding to the volume is implicit in the
/// brain row's relationship to the DEK; an external attacker without the DEK cannot construct a
/// valid envelope regardless of AAD.
/// </para>
/// <para>
/// <strong>Persistence.</strong> The full envelope is written into the BLOB column
/// (<c>Providers.EncryptedToken</c> or <c>Providers.EncryptedClientSecret</c>). The schema's
/// <c>TokenNonce</c> / <c>ClientSecretNonce</c> TEXT columns are vestigial under this format and
/// are left NULL — the nonce lives inside the envelope.
/// </para>
/// <para>
/// Principle 26: this helper never logs the DEK, the nonce, the plaintext, or the ciphertext.
/// </para>
/// </remarks>
internal static class ProviderTokenCrypto
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
    /// <param name="plaintext">UTF-8 string to encrypt (e.g. an OAuth refresh token).
    /// May be empty.</param>
    /// <param name="dek">32-byte DEK. <see cref="AesGcm"/>'s constructor enforces the length;
    /// passing a wrong-sized key throws <see cref="CryptographicException"/>, which is a
    /// programming error here (the DEK comes from the unlocked vault and is guaranteed 32 bytes).</param>
    /// <returns>Heap-allocated envelope ready to persist to a SQLite BLOB column.</returns>
    /// <remarks>
    /// Never throws under correct usage. Allocates the envelope as a single
    /// <see cref="byte"/> array of length <c>1 + 12 + utf8Length + 16</c>.
    /// </remarks>
    internal static byte[] Encrypt(string plaintext, ReadOnlyMemory<byte> dek)
    {
        // UTF-8 encode in one allocation; the byte length determines the final envelope size.
        var utf8Length = Encoding.UTF8.GetByteCount(plaintext);
        var envelope = new byte[Overhead + utf8Length];
        envelope[0] = CurrentVersion;

        var nonce = envelope.AsSpan(1, NonceSize);
        var ciphertext = envelope.AsSpan(1 + NonceSize, utf8Length);
        var tag = envelope.AsSpan(1 + NonceSize + utf8Length, TagSize);

        // Encode the plaintext directly into the ciphertext slot, then encrypt in place.
        // GetBytes(string, Span<byte>) is the .NET 6+ allocation-free overload.
        Encoding.UTF8.GetBytes(plaintext, ciphertext);
        RandomNumberGenerator.Fill(nonce);

        // AesGcm holds the key in unmanaged memory; using ensures it's wiped promptly
        // (Principle 31 — keys zeroed on release).
        using var aes = new AesGcm(dek.Span, TagSize);
        // Encrypt in place: input span = output span (allowed by AesGcm.Encrypt).
        aes.Encrypt(nonce, ciphertext, ciphertext, tag, associatedData: ReadOnlySpan<byte>.Empty);

        return envelope;
    }

    /// <summary>
    /// Attempts to decrypt <paramref name="envelope"/> using <paramref name="dek"/>.
    /// Returns <see langword="false"/> for any failure mode: truncated envelope, unknown version
    /// byte, wrong DEK, tampered ciphertext, tampered tag, or malformed UTF-8 in the plaintext.
    /// Never throws.
    /// </summary>
    /// <param name="envelope">Envelope as produced by <see cref="Encrypt"/>.</param>
    /// <param name="dek">32-byte DEK to decrypt with.</param>
    /// <param name="plaintext">On success, the decoded UTF-8 string. On failure,
    /// <see cref="string.Empty"/>.</param>
    internal static bool TryDecrypt(ReadOnlySpan<byte> envelope, ReadOnlyMemory<byte> dek, out string plaintext)
    {
        plaintext = string.Empty;

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
            using var aes = new AesGcm(dek.Span, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, buffer, associatedData: ReadOnlySpan<byte>.Empty);
        }
        catch (CryptographicException)
        {
            // Wrong key, tampered ciphertext, or tampered tag — same outcome: no usable token.
            return false;
        }

        try
        {
            plaintext = Encoding.UTF8.GetString(buffer);
        }
        catch (DecoderFallbackException)
        {
            // Defensive: an attacker producing a valid GCM tag with malformed UTF-8 is
            // cryptographically infeasible, but we still observe the contract.
            plaintext = string.Empty;
            return false;
        }

        return true;
    }
}
