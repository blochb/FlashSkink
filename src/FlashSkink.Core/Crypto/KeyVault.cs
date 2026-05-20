using System.Buffers.Binary;
using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Crypto;
using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Crypto;

/// <summary>
/// Manages the on-disk DEK vault file. Creates, unlocks, and re-keys the 100-byte
/// vault stored at <c>[skink]/.flashskink/vault.bin</c>. Never holds key material as
/// fields — all key buffers are local and zeroed before return on every path.
/// </summary>
public sealed class KeyVault
{
    // Vault binary layout (100 bytes total):
    //   0–3  : Magic "FSVT" (4 bytes)
    //   4–5  : Version uint16 LE = 1 (2 bytes)
    //   6    : MemoryMib — Argon2id memory in MiB (1 byte)
    //   7    : (iterations << 4) | parallelism (1 byte)
    //   8–39 : Argon2id salt (32 bytes)
    //  40–51 : AES-GCM nonce (12 bytes)
    //  52–83 : Wrapped DEK ciphertext (32 bytes)
    //  84–99 : GCM authentication tag (16 bytes)
    private const int VaultSize = 100;
    private const ushort VaultVersion = 1;
    private static readonly byte[] Magic = "FSVT"u8.ToArray();
    private static ReadOnlySpan<byte> Aad => "DEK_VAULT"u8;

    private readonly KeyDerivationService _kdf;
    private readonly MnemonicService _mnemonic;

    /// <summary>Creates a <see cref="KeyVault"/> with the given KDF and mnemonic services.</summary>
    public KeyVault(KeyDerivationService kdf, MnemonicService mnemonic)
    {
        _kdf = kdf;
        _mnemonic = mnemonic;
    }

    /// <summary>
    /// Generates a random DEK, wraps it with a KEK derived from <paramref name="password"/>,
    /// and writes the vault atomically. Returns the unwrapped DEK — caller must zero it.
    /// </summary>
    /// <param name="password">Password bytes; caller zeroes after this call returns.</param>
    public async Task<Result<byte[]>> CreateAsync(
        string vaultPath, ReadOnlyMemory<byte> password, CancellationToken ct)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var kek = Array.Empty<byte>();

        try
        {
            ct.ThrowIfCancellationRequested();
            var kdfResult = _kdf.DeriveKekFromPassword(password.Span, salt, out kek);
            if (!kdfResult.Success)
            {
                return Result<byte[]>.Fail(kdfResult.Error!);
            }

            var ciphertext = new byte[32];
            var tag = new byte[16];
            using (var aesGcm = new AesGcm(kek, tag.Length))
            {
                aesGcm.Encrypt(nonce, dek, ciphertext, tag, Aad);
            }

            var vault = SerializeVault(salt, nonce, ciphertext, tag);
            var writeResult = await AtomicWriteAsync(vaultPath, vault, ct).ConfigureAwait(false);
            if (!writeResult.Success)
            {
                return Result<byte[]>.Fail(writeResult.Error!);
            }

            return Result<byte[]>.Ok(dek);
        }
        catch (OperationCanceledException ex)
        {
            return Result<byte[]>.Fail(ErrorCode.Cancelled, "Vault creation cancelled.", ex);
        }
        catch (CryptographicException ex)
        {
            return Result<byte[]>.Fail(ErrorCode.EncryptionFailed,
                "AES-GCM encryption failed during vault creation.", ex);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Fail(ErrorCode.Unknown, "Unexpected error during vault creation.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>
    /// Reads the vault, derives a KEK from <paramref name="password"/>, and unwraps the DEK.
    /// Returns <see cref="ErrorCode.InvalidPassword"/> when GCM authentication fails.
    /// Returns the DEK — caller must zero it.
    /// </summary>
    /// <param name="password">Password bytes; caller zeroes after this call returns.</param>
    public async Task<Result<byte[]>> UnlockAsync(
        string vaultPath, ReadOnlyMemory<byte> password, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var readResult = await ReadVaultAsync(vaultPath, ct).ConfigureAwait(false);
            if (!readResult.Success)
            {
                return Result<byte[]>.Fail(readResult.Error!);
            }

            return DecryptDek(password.Span, readResult.Value!);
        }
        catch (OperationCanceledException ex)
        {
            return Result<byte[]>.Fail(ErrorCode.Cancelled, "Vault unlock cancelled.", ex);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Fail(ErrorCode.Unknown, "Unexpected error during vault unlock.", ex);
        }
    }

    /// <summary>
    /// Recovery path: derives KEK from a BIP-39 mnemonic and unwraps the DEK.
    /// Returns <see cref="ErrorCode.InvalidMnemonic"/> for invalid words,
    /// <see cref="ErrorCode.InvalidPassword"/> when the mnemonic produces the wrong KEK.
    /// Returns the DEK — caller must zero it.
    /// </summary>
    /// <param name="phrase">
    /// The recovery phrase. This method does <b>not</b> dispose <paramref name="phrase"/>;
    /// the caller (typically a CLI handler that constructed it via
    /// <see cref="RecoveryPhrase.FromUserInput"/>) retains ownership and is responsible
    /// for disposal after this call returns.
    /// </param>
    public async Task<Result<byte[]>> UnlockFromMnemonicAsync(
        string vaultPath, RecoveryPhrase phrase, CancellationToken ct)
    {
        var seed = Array.Empty<byte>();
        var kek = Array.Empty<byte>();

        try
        {
            ct.ThrowIfCancellationRequested();
            var readResult = await ReadVaultAsync(vaultPath, ct).ConfigureAwait(false);
            if (!readResult.Success)
            {
                return Result<byte[]>.Fail(readResult.Error!);
            }

            var header = readResult.Value!;

            var seedResult = _mnemonic.ToSeed(phrase);
            if (!seedResult.Success)
            {
                return Result<byte[]>.Fail(seedResult.Error!);
            }

            seed = seedResult.Value!;

            var kdfResult = _kdf.DeriveKek(
                seed, header.Salt,
                header.MemoryMib * 1024, header.Iterations, header.Parallelism,
                out kek);
            if (!kdfResult.Success)
            {
                return Result<byte[]>.Fail(kdfResult.Error!);
            }

            return DecryptDekWithKek(kek, header);
        }
        catch (OperationCanceledException ex)
        {
            return Result<byte[]>.Fail(ErrorCode.Cancelled, "Vault mnemonic unlock cancelled.", ex);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Fail(ErrorCode.Unknown, "Unexpected error during mnemonic unlock.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>
    /// Unlocks the vault with <paramref name="currentPassword"/>, re-wraps the DEK under
    /// <paramref name="newPassword"/>, and atomically replaces the vault file.
    /// Propagates <see cref="ErrorCode.InvalidPassword"/>, <see cref="ErrorCode.VolumeNotFound"/>,
    /// <see cref="ErrorCode.VolumeCorrupt"/>, and <see cref="ErrorCode.StagingFailed"/>.
    /// </summary>
    /// <param name="currentPassword">Current password bytes; caller zeroes after this call returns.</param>
    /// <param name="newPassword">New password bytes; caller zeroes after this call returns.</param>
    public async Task<Result> ChangePasswordAsync(
        string vaultPath,
        ReadOnlyMemory<byte> currentPassword,
        ReadOnlyMemory<byte> newPassword,
        CancellationToken ct)
    {
        var dek = Array.Empty<byte>();
        var newKek = Array.Empty<byte>();

        try
        {
            ct.ThrowIfCancellationRequested();
            var unlockResult = await UnlockAsync(vaultPath, currentPassword, ct).ConfigureAwait(false);
            if (!unlockResult.Success)
            {
                return Result.Fail(unlockResult.Error!);
            }

            dek = unlockResult.Value!;

            var newSalt = RandomNumberGenerator.GetBytes(32);
            var newNonce = RandomNumberGenerator.GetBytes(12);

            var kdfResult = _kdf.DeriveKekFromPassword(newPassword.Span, newSalt, out newKek);
            if (!kdfResult.Success)
            {
                return Result.Fail(kdfResult.Error!);
            }

            var ciphertext = new byte[32];
            var tag = new byte[16];
            using (var aesGcm = new AesGcm(newKek, tag.Length))
            {
                aesGcm.Encrypt(newNonce, dek, ciphertext, tag, Aad);
            }

            var vault = SerializeVault(newSalt, newNonce, ciphertext, tag);
            return await AtomicWriteAsync(vaultPath, vault, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "Password change cancelled.", ex);
        }
        catch (CryptographicException ex)
        {
            return Result.Fail(ErrorCode.EncryptionFailed,
                "AES-GCM encryption failed during password change.", ex);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCode.Unknown, "Unexpected error during password change.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(newKek);
        }
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private Result<byte[]> DecryptDek(ReadOnlySpan<byte> password, VaultHeader header)
    {
        var kek = Array.Empty<byte>();
        try
        {
            var kdfResult = _kdf.DeriveKekFromPassword(
                password, header.Salt,
                header.MemoryMib * 1024, header.Iterations, header.Parallelism,
                out kek);
            if (!kdfResult.Success)
            {
                return Result<byte[]>.Fail(kdfResult.Error!);
            }

            return DecryptDekWithKek(kek, header);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static Result<byte[]> DecryptDekWithKek(byte[] kek, VaultHeader header)
    {
        try
        {
            var dek = new byte[32];
            using var aesGcm = new AesGcm(kek, header.Tag.Length);
            aesGcm.Decrypt(header.Nonce, header.Ciphertext, header.Tag, dek, Aad);
            return Result<byte[]>.Ok(dek);
        }
        catch (CryptographicException ex)
        {
            // AES-GCM raises CryptographicException (no sub-type) when the tag mismatches.
            return Result<byte[]>.Fail(ErrorCode.InvalidPassword,
                "Vault authentication tag mismatch — wrong password or key.", ex);
        }
    }

    private static async Task<Result<VaultHeader>> ReadVaultAsync(string vaultPath, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(vaultPath, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            return Result<VaultHeader>.Fail(ErrorCode.VolumeNotFound,
                $"Vault file not found at '{vaultPath}'.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Result<VaultHeader>.Fail(ErrorCode.VolumeNotFound,
                $"Vault directory not found at '{vaultPath}'.", ex);
        }
        catch (IOException ex)
        {
            return Result<VaultHeader>.Fail(ErrorCode.StagingFailed,
                $"I/O error reading vault at '{vaultPath}'.", ex);
        }

        return ParseVault(bytes);
    }

    private static Result<VaultHeader> ParseVault(byte[] bytes)
    {
        if (bytes.Length != VaultSize)
        {
            return Result<VaultHeader>.Fail(ErrorCode.VolumeCorrupt,
                $"Vault file is {bytes.Length} bytes; expected {VaultSize}.");
        }

        if (bytes[0] != Magic[0] || bytes[1] != Magic[1] ||
            bytes[2] != Magic[2] || bytes[3] != Magic[3])
        {
            return Result<VaultHeader>.Fail(ErrorCode.VolumeCorrupt,
                "Vault magic bytes are invalid.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        if (version != VaultVersion)
        {
            return Result<VaultHeader>.Fail(ErrorCode.VolumeCorrupt,
                $"Unsupported vault version {version}; expected {VaultVersion}.");
        }

        var memMib = bytes[6];
        var iterPar = bytes[7];
        var iterations = (byte)(iterPar >> 4);
        var parallelism = (byte)(iterPar & 0x0F);

        return Result<VaultHeader>.Ok(new VaultHeader(
            Version: version,
            MemoryMib: memMib,
            Iterations: iterations,
            Parallelism: parallelism,
            Salt: bytes[8..40],
            Nonce: bytes[40..52],
            Ciphertext: bytes[52..84],
            Tag: bytes[84..100]));
    }

    private static byte[] SerializeVault(byte[] salt, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        const int memMib = KeyDerivationService.Argon2MemoryKilobytes / 1024;
        const int iterations = KeyDerivationService.Argon2Iterations;
        const int parallelism = KeyDerivationService.Argon2Parallelism;

        var vault = new byte[VaultSize];
        Magic.CopyTo(vault, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(vault.AsSpan(4, 2), VaultVersion);
        vault[6] = (byte)memMib;
        vault[7] = (byte)((iterations << 4) | parallelism);
        salt.CopyTo(vault, 8);
        nonce.CopyTo(vault, 40);
        ciphertext.CopyTo(vault, 52);
        tag.CopyTo(vault, 84);
        return vault;
    }

    private static async Task<Result> AtomicWriteAsync(string vaultPath, byte[] data, CancellationToken ct)
    {
        var tmpPath = vaultPath + ".tmp";
        try
        {
            await using var fs = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true);
            await fs.WriteAsync(data, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }
        catch (OperationCanceledException ex)
        {
            TryDelete(tmpPath);
            return Result.Fail(ErrorCode.Cancelled, "Vault write cancelled.", ex);
        }
        catch (IOException ex)
        {
            TryDelete(tmpPath);
            return Result.Fail(ErrorCode.StagingFailed, $"I/O error writing vault to '{tmpPath}'.", ex);
        }

        try
        {
            File.Move(tmpPath, vaultPath, overwrite: true);
            return Result.Ok();
        }
        catch (IOException ex)
        {
            TryDelete(tmpPath);
            return Result.Fail(ErrorCode.StagingFailed,
                $"Failed to rename vault tmp file to '{vaultPath}'.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private readonly record struct VaultHeader(
        ushort Version,
        byte MemoryMib,
        byte Iterations,
        byte Parallelism,
        byte[] Salt,
        byte[] Nonce,
        byte[] Ciphertext,
        byte[] Tag);
}
