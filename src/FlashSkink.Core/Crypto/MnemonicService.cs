using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Crypto;

/// <summary>
/// Owns the BIP-39 mnemonic lifecycle: generation, validation, and seed derivation.
/// Never persists or logs mnemonic words.
/// </summary>
public sealed class MnemonicService
{
    // Loaded once from the embedded resource on first access.
    private static readonly string[] _wordlistArray = LoadWordlist();
    private static readonly FrozenSet<string> _wordlistSet = _wordlistArray.ToFrozenSet(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, int> _wordIndex = BuildWordIndex(_wordlistArray);

    private static string[] LoadWordlist()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "FlashSkink.Core.Resources.bip39-english.txt";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static Dictionary<string, int> BuildWordIndex(string[] words)
    {
        var index = new Dictionary<string, int>(words.Length, StringComparer.Ordinal);
        for (var i = 0; i < words.Length; i++)
        {
            index[words[i]] = i;
        }

        return index;
    }

    /// <summary>
    /// Produces a 24-word BIP-39 mnemonic from 32 bytes of cryptographic entropy.
    /// The caller is responsible for zeroing/clearing the returned array after display.
    /// </summary>
    public Result<string[]> Generate()
    {
        try
        {
            var entropy = RandomNumberGenerator.GetBytes(32);
            var hash = SHA256.HashData(entropy);
            var checksum = hash[0];

            // Concatenate 256 entropy bits + 8 checksum bits = 264 bits = 24 × 11-bit indices.
            var bits = new byte[33];
            Buffer.BlockCopy(entropy, 0, bits, 0, 32);
            bits[32] = checksum;

            var words = new string[24];
            for (var i = 0; i < 24; i++)
            {
                var index = Extract11Bits(bits, i * 11);
                words[i] = _wordlistArray[index];
            }

            return Result<string[]>.Ok(words);
        }
        catch (Exception ex)
        {
            return Result<string[]>.Fail(ErrorCode.EncryptionFailed,
                "Failed to generate mnemonic due to a cryptographic error.", ex);
        }
    }

    /// <summary>
    /// Validates that all 24 words exist in the BIP-39 English wordlist and that the
    /// embedded checksum is correct. Returns <see cref="ErrorCode.InvalidMnemonic"/> on
    /// any validation failure.
    /// </summary>
    public Result Validate(string[] words)
    {
        if (words.Length != 24)
        {
            return Result.Fail(ErrorCode.InvalidMnemonic,
                $"Mnemonic must contain exactly 24 words; got {words.Length}.");
        }

        // Map each word to its 11-bit index; reject unknown words.
        var indices = new int[24];
        for (var i = 0; i < 24; i++)
        {
            var word = words[i];
            if (word is null || !_wordIndex.TryGetValue(word, out var idx))
            {
                return Result.Fail(ErrorCode.InvalidMnemonic,
                    "Mnemonic contains an unrecognised word.");
            }

            indices[i] = idx;
        }

        // Reconstruct 264-bit string from 24 × 11-bit indices.
        var bits = new byte[33];
        for (var i = 0; i < 24; i++)
        {
            Inject11Bits(bits, i * 11, indices[i]);
        }

        // Verify checksum: SHA-256 of the 32-byte entropy, first byte must match bits[32].
        var entropy = bits[..32];
        var expectedChecksum = SHA256.HashData(entropy)[0];
        if (bits[32] != expectedChecksum)
        {
            return Result.Fail(ErrorCode.InvalidMnemonic,
                "Mnemonic checksum is invalid.");
        }

        return Result.Ok();
    }

    /// <summary>
    /// Derives the 64-byte BIP-39 seed from a mnemonic using PBKDF2-HMAC-SHA512 with an
    /// empty passphrase. The caller must zero the returned byte array after use.
    /// Returns <see cref="ErrorCode.InvalidMnemonic"/> if the words fail validation.
    /// </summary>
    public Result<byte[]> ToSeed(string[] words)
    {
        var validation = Validate(words);
        if (!validation.Success)
        {
            return Result<byte[]>.Fail(validation.Error!);
        }

        try
        {
            // BIP-39 seed derivation: PBKDF2-HMAC-SHA512, salt = "mnemonic" (empty passphrase).
            var mnemonic = string.Join(" ", words).Normalize(NormalizationForm.FormKD);
            var password = Encoding.UTF8.GetBytes(mnemonic);
            var salt = Encoding.UTF8.GetBytes("mnemonic");

            var seed = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, 2048, HashAlgorithmName.SHA512, outputLength: 64);

            return Result<byte[]>.Ok(seed);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Fail(ErrorCode.EncryptionFailed,
                "Failed to derive seed from mnemonic due to a cryptographic error.", ex);
        }
    }

    /// <summary>The ordered BIP-39 English wordlist (2048 entries). Read-only.</summary>
    public static IReadOnlyList<string> Wordlist => _wordlistArray;

    // Extracts an 11-bit value from a byte array starting at bit offset `bitOffset`.
    private static int Extract11Bits(byte[] bits, int bitOffset)
    {
        var result = 0;
        for (var i = 0; i < 11; i++)
        {
            var byteIdx = (bitOffset + i) / 8;
            var bitIdx = 7 - ((bitOffset + i) % 8);
            if ((bits[byteIdx] & (1 << bitIdx)) != 0)
            {
                result |= 1 << (10 - i);
            }
        }

        return result;
    }

    // Injects an 11-bit value into a byte array starting at bit offset `bitOffset`.
    private static void Inject11Bits(byte[] bits, int bitOffset, int value)
    {
        for (var i = 0; i < 11; i++)
        {
            var byteIdx = (bitOffset + i) / 8;
            var bitIdx = 7 - ((bitOffset + i) % 8);
            if ((value & (1 << (10 - i))) != 0)
            {
                bits[byteIdx] |= (byte)(1 << bitIdx);
            }
            else
            {
                bits[byteIdx] &= (byte)~(1 << bitIdx);
            }
        }
    }
}
