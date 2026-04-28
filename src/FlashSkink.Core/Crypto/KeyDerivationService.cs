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
    // internal so KeyVault can write them into the vault header without duplicating the values.
    internal const int Argon2MemoryKilobytes = 19_456;
    internal const int Argon2Iterations = 2;
    internal const int Argon2Parallelism = 1;
    private const int KekBytes = 32;
    private const int BrainKeyBytes = 32;

    /// <summary>
    /// Derives a 256-bit KEK from a BIP-39 seed using Argon2id with the current baseline
    /// parameters. The caller must zero <paramref name="seed"/> and <paramref name="kek"/> after use.
    /// </summary>
    /// <param name="seed">The 64-byte BIP-39 seed from <see cref="MnemonicService.ToSeed"/>.</param>
    /// <param name="argon2Salt">32-byte random salt stored in the vault header.</param>
    /// <param name="kek">On success: a fresh 32-byte KEK. On failure: <see cref="Array.Empty{T}"/>.</param>
    public Result DeriveKek(byte[] seed, ReadOnlySpan<byte> argon2Salt, out byte[] kek)
        => DeriveKek(seed, argon2Salt, Argon2MemoryKilobytes, Argon2Iterations, Argon2Parallelism, out kek);

    /// <summary>
    /// Derives a 256-bit KEK from a BIP-39 seed using Argon2id with explicit parameters read
    /// from a vault header, enabling decryption of vaults created under any past baseline.
    /// The caller must zero <paramref name="seed"/> and <paramref name="kek"/> after use.
    /// </summary>
    /// <param name="seed">The 64-byte BIP-39 seed.</param>
    /// <param name="argon2Salt">32-byte random salt stored in the vault header.</param>
    /// <param name="memoryKilobytes">Argon2id memory in KiB, read from vault header.</param>
    /// <param name="iterations">Argon2id iteration count, read from vault header.</param>
    /// <param name="parallelism">Argon2id parallelism, read from vault header.</param>
    /// <param name="kek">On success: a fresh 32-byte KEK. On failure: <see cref="Array.Empty{T}"/>.</param>
    public Result DeriveKek(
        byte[] seed, ReadOnlySpan<byte> argon2Salt,
        int memoryKilobytes, int iterations, int parallelism,
        out byte[] kek)
    {
        try
        {
            kek = RunArgon2(seed, argon2Salt, memoryKilobytes, iterations, parallelism);
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
    /// Derives a 256-bit KEK from a raw password buffer using Argon2id with the current
    /// baseline parameters. The internal copy is zeroed in a <see langword="finally"/> block.
    /// The caller must zero the returned <paramref name="kek"/> after use.
    /// </summary>
    /// <param name="passwordBytes">The user password encoded as bytes (caller zeroes after call).</param>
    /// <param name="argon2Salt">32-byte random salt stored in the vault header.</param>
    /// <param name="kek">On success: a fresh 32-byte KEK. On failure: <see cref="Array.Empty{T}"/>.</param>
    /// <remarks>
    /// Argon2id is fully synchronous. A <c>CancellationToken</c> checked by the caller before
    /// this call has no effect once derivation starts — the ~500 ms–2 s CPU work runs to
    /// completion regardless. Callers that need mid-derivation responsiveness should wrap this
    /// call in <see cref="Task.Run(System.Action)"/> and observe the token on either side.
    /// </remarks>
    public Result DeriveKekFromPassword(
        ReadOnlySpan<byte> passwordBytes,
        ReadOnlySpan<byte> argon2Salt,
        out byte[] kek)
        => DeriveKekFromPassword(passwordBytes, argon2Salt,
            Argon2MemoryKilobytes, Argon2Iterations, Argon2Parallelism, out kek);

    /// <summary>
    /// Derives a 256-bit KEK from a raw password buffer using Argon2id with explicit parameters
    /// read from a vault header, enabling decryption of vaults created under any past baseline.
    /// The internal copy is zeroed in a <see langword="finally"/> block.
    /// The caller must zero the returned <paramref name="kek"/> after use.
    /// </summary>
    /// <param name="passwordBytes">The user password encoded as bytes (caller zeroes after call).</param>
    /// <param name="argon2Salt">32-byte random salt stored in the vault header.</param>
    /// <param name="memoryKilobytes">Argon2id memory in KiB, read from vault header.</param>
    /// <param name="iterations">Argon2id iteration count, read from vault header.</param>
    /// <param name="parallelism">Argon2id parallelism, read from vault header.</param>
    /// <param name="kek">On success: a fresh 32-byte KEK. On failure: <see cref="Array.Empty{T}"/>.</param>
    public Result DeriveKekFromPassword(
        ReadOnlySpan<byte> passwordBytes,
        ReadOnlySpan<byte> argon2Salt,
        int memoryKilobytes, int iterations, int parallelism,
        out byte[] kek)
    {
        // Copy span to array only for the Konscious API; zeroed immediately after.
        var passwordCopy = passwordBytes.ToArray();
        try
        {
            kek = RunArgon2(passwordCopy, argon2Salt, memoryKilobytes, iterations, parallelism);
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

    private static byte[] RunArgon2(
        byte[] password, ReadOnlySpan<byte> salt,
        int memoryKilobytes, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(password);
        argon2.Salt = salt.ToArray();
        argon2.MemorySize = memoryKilobytes;
        argon2.Iterations = iterations;
        argon2.DegreeOfParallelism = parallelism;
        return argon2.GetBytes(KekBytes);
    }
}
