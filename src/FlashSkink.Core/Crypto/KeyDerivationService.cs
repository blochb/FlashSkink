using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Results;
using Konscious.Security.Cryptography;

namespace FlashSkink.Core.Crypto;

/// <summary>
/// Owns the cryptographic KDF layer: Argon2id KEK derivation and HKDF brain-key derivation.
/// Caller controls all output buffer lifetimes and must zero key material after use.
/// </summary>
public sealed class KeyDerivationService
{
    // Argon2id parameters per blueprint §18.2 (OWASP 2024 baseline).
    private const int Argon2MemoryKilobytes = 19_456;
    private const int Argon2Iterations = 2;
    private const int Argon2Parallelism = 1;
    private const int KekBytes = 32;
    private const int BrainKeyBytes = 32;

    /// <summary>
    /// Derives a 256-bit KEK from a BIP-39 seed using Argon2id. The caller must zero
    /// <paramref name="seed"/> and the returned <paramref name="kek"/> after use.
    /// </summary>
    /// <param name="seed">The 64-byte BIP-39 seed from <see cref="MnemonicService.ToSeed"/>.</param>
    /// <param name="argon2Salt">32-byte random salt stored in the vault header.</param>
    /// <param name="kek">On success: a fresh 32-byte KEK. On failure: <see cref="Array.Empty{T}"/>.</param>
    public Result DeriveKek(byte[] seed, ReadOnlySpan<byte> argon2Salt, out byte[] kek)
    {
        try
        {
            kek = RunArgon2(seed, argon2Salt);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            kek = Array.Empty<byte>();
            return Result.Fail(ErrorCode.KeyDerivationFailed,
                "Argon2id KEK derivation from seed failed.", ex);
        }
    }

    /// <summary>
    /// Derives a 256-bit KEK from a raw password buffer using Argon2id. Accepts
    /// <see cref="ReadOnlySpan{T}"/> so the caller can use a stack-pinned or pooled buffer
    /// and zero it after this call without the service retaining a reference.
    /// The internal copy is zeroed in a <see langword="finally"/> block.
    /// The caller must zero the returned <paramref name="kek"/> after use.
    /// </summary>
    /// <param name="passwordBytes">The user password encoded as bytes (caller zeroes after call).</param>
    /// <param name="argon2Salt">32-byte random salt stored in the vault header.</param>
    /// <param name="kek">On success: a fresh 32-byte KEK. On failure: <see cref="Array.Empty{T}"/>.</param>
    public Result DeriveKekFromPassword(
        ReadOnlySpan<byte> passwordBytes,
        ReadOnlySpan<byte> argon2Salt,
        out byte[] kek)
    {
        // Copy span to array only for the Konscious API; zeroed immediately after.
        var passwordCopy = passwordBytes.ToArray();
        try
        {
            kek = RunArgon2(passwordCopy, argon2Salt);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            kek = Array.Empty<byte>();
            return Result.Fail(ErrorCode.KeyDerivationFailed,
                "Argon2id KEK derivation from password failed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordCopy);
        }
    }

    /// <summary>
    /// Derives the 32-byte brain key via HKDF-SHA256 from the DEK with info label
    /// <c>"brain"</c>. Writes the result into <paramref name="destination"/>, which must
    /// be exactly 32 bytes. The caller must zero <paramref name="destination"/> after use.
    /// </summary>
    /// <param name="dek">The 32-byte data-encryption key.</param>
    /// <param name="destination">Output span; must be exactly 32 bytes.</param>
    public Result DeriveBrainKey(ReadOnlySpan<byte> dek, Span<byte> destination)
    {
        if (destination.Length != BrainKeyBytes)
        {
            return Result.Fail(ErrorCode.KeyDerivationFailed,
                $"Brain key destination must be exactly {BrainKeyBytes} bytes; got {destination.Length}.");
        }

        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, dek, destination,
                salt: ReadOnlySpan<byte>.Empty, info: "brain"u8);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCode.KeyDerivationFailed,
                "HKDF brain-key derivation failed.", ex);
        }
    }

    private static byte[] RunArgon2(byte[] password, ReadOnlySpan<byte> salt)
    {
        using var argon2 = new Argon2id(password);
        argon2.Salt = salt.ToArray();
        argon2.MemorySize = Argon2MemoryKilobytes;
        argon2.Iterations = Argon2Iterations;
        argon2.DegreeOfParallelism = Argon2Parallelism;
        return argon2.GetBytes(KekBytes);
    }
}
