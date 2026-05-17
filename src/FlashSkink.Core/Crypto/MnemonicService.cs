using System.Buffers;
using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FlashSkink.Core.Abstractions.Crypto;
using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Crypto;

/// <summary>
/// Owns the BIP-39 mnemonic lifecycle: generation, validation, and seed
/// derivation. Operates on the zeroizable <see cref="RecoveryPhrase"/>
/// type rather than <c>string[]</c> so that mnemonic words can be cleared
/// from memory after use (blueprint §18.6, §18.8; CLAUDE.md Principle 31).
/// </summary>
public sealed class MnemonicService
{
    // Loaded once from the embedded resource on first access.
    private static readonly string[] _wordlistArray = LoadWordlist();
    private static readonly FrozenDictionary<string, int> _wordIndex = BuildWordIndex(_wordlistArray);

    // Span-keyed alternate lookup over the same FrozenDictionary — lets us
    // look up a word in the wordlist using a ReadOnlySpan<char> from a
    // RecoveryPhrase without allocating a string per word (.NET 9+).
    private static readonly FrozenDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _wordIndexSpan
        = _wordIndex.GetAlternateLookup<ReadOnlySpan<char>>();

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

    private static FrozenDictionary<string, int> BuildWordIndex(string[] words)
    {
        var dict = new Dictionary<string, int>(words.Length, StringComparer.Ordinal);
        for (var i = 0; i < words.Length; i++)
        {
            dict[words[i]] = i;
        }

        return dict.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Produces a 24-word BIP-39 mnemonic from 32 bytes of cryptographic entropy,
    /// returned as a zeroizable <see cref="RecoveryPhrase"/>. The caller owns
    /// the returned phrase and must dispose it after display so the underlying
    /// <c>char[]</c> word buffers are cleared.
    /// </summary>
    /// <remarks>
    /// Intermediate buffers holding the raw entropy and the concatenated
    /// entropy+checksum bit stream are zeroed before this method returns
    /// (success or failure). Without that, a memory dump taken between
    /// generation and process exit would yield the full 256-bit key material
    /// — cryptographically equivalent to recovering the phrase — defeating
    /// the zeroization the receipt's <see cref="RecoveryPhrase"/> provides.
    /// </remarks>
    public Result<RecoveryPhrase> Generate()
    {
        byte[]? entropy = null;
        byte[]? hash = null;
        byte[]? bits = null;
        try
        {
            entropy = RandomNumberGenerator.GetBytes(32);
            hash = SHA256.HashData(entropy);
            var checksum = hash[0];

            // Concatenate 256 entropy bits + 8 checksum bits = 264 bits = 24 × 11-bit indices.
            bits = new byte[33];
            Buffer.BlockCopy(entropy, 0, bits, 0, 32);
            bits[32] = checksum;

            var words = new char[24][];
            for (var i = 0; i < 24; i++)
            {
                var index = Extract11Bits(bits, i * 11);
                // Allocate a fresh char[] per word so the RecoveryPhrase owns
                // its buffer and can zero it on Dispose. The wordlist string
                // itself is public BIP-39 dictionary data (not secret) and is
                // not zeroed.
                words[i] = _wordlistArray[index].ToCharArray();
            }

            return Result<RecoveryPhrase>.Ok(new RecoveryPhrase(words));
        }
        catch (Exception ex)
        {
            return Result<RecoveryPhrase>.Fail(ErrorCode.EncryptionFailed,
                "Failed to generate mnemonic due to a cryptographic error.", ex);
        }
        finally
        {
            // All three buffers are cryptographically equivalent to the phrase
            // (entropy is the seed of the words; bits is its concatenation with
            // the checksum; hash[0] is the checksum byte, and the rest of hash
            // is a SHA-256 of entropy that doesn't add disclosure but is cheap
            // to clear). Zero all three.
            if (entropy is not null) { CryptographicOperations.ZeroMemory(entropy); }
            if (hash is not null) { CryptographicOperations.ZeroMemory(hash); }
            if (bits is not null) { CryptographicOperations.ZeroMemory(bits); }
        }
    }

    /// <summary>
    /// Validates that all 24 words exist in the BIP-39 English wordlist and
    /// that the embedded checksum is correct. Returns
    /// <see cref="ErrorCode.InvalidMnemonic"/> on any validation failure.
    /// </summary>
    /// <remarks>
    /// Intermediate buffers reconstructed from <paramref name="phrase"/>
    /// (<c>indices</c>, <c>bits</c>, <c>entropy</c>, <c>hash</c>) are zeroed
    /// before return. The caller already holds the phrase, so the additional
    /// disclosure risk is small, but consistency with the zeroization stance
    /// elsewhere in this file (and a shared compaction window with any
    /// memory snapshot tool) makes this worth doing uniformly.
    /// </remarks>
    public Result Validate(RecoveryPhrase phrase)
    {
        if (phrase.Count != 24)
        {
            return Result.Fail(ErrorCode.InvalidMnemonic,
                $"Mnemonic must contain exactly 24 words; got {phrase.Count}.");
        }

        // Map each word to its 11-bit index; reject unknown words. The span
        // lookup avoids allocating a string per word.
        var indices = new int[24];
        byte[]? bits = null;
        byte[]? entropy = null;
        byte[]? hash = null;
        try
        {
            for (var i = 0; i < 24; i++)
            {
                if (!_wordIndexSpan.TryGetValue(phrase[i], out var idx))
                {
                    return Result.Fail(ErrorCode.InvalidMnemonic,
                        "Mnemonic contains an unrecognised word.");
                }

                indices[i] = idx;
            }

            // Reconstruct 264-bit string from 24 × 11-bit indices.
            bits = new byte[33];
            for (var i = 0; i < 24; i++)
            {
                Inject11Bits(bits, i * 11, indices[i]);
            }

            // Verify checksum: SHA-256 of the 32-byte entropy, first byte must match bits[32].
            entropy = bits[..32];
            hash = SHA256.HashData(entropy);
            var expectedChecksum = hash[0];
            if (bits[32] != expectedChecksum)
            {
                return Result.Fail(ErrorCode.InvalidMnemonic,
                    "Mnemonic checksum is invalid.");
            }

            return Result.Ok();
        }
        finally
        {
            Array.Clear(indices);
            if (bits is not null) { CryptographicOperations.ZeroMemory(bits); }
            if (entropy is not null) { CryptographicOperations.ZeroMemory(entropy); }
            if (hash is not null) { CryptographicOperations.ZeroMemory(hash); }
        }
    }

    /// <summary>
    /// Derives the 64-byte BIP-39 seed from a phrase using PBKDF2-HMAC-SHA512
    /// with an empty passphrase. The caller must zero the returned byte array
    /// after use. Returns <see cref="ErrorCode.InvalidMnemonic"/> if the words
    /// fail validation.
    /// </summary>
    /// <remarks>
    /// NFKD normalization (called for by the BIP-39 spec) is a bit-identity
    /// transform on the English wordlist, which is restricted to lowercase
    /// ASCII (a-z). We skip the explicit <c>string.Normalize</c> call — which
    /// would force a non-zeroizable string allocation — and rely on the
    /// canonical all-zeros test vector
    /// (<c>ToSeed_Bip39AllZerosVector_ProducesKnownSeedPrefix</c>) to catch
    /// any divergence. If a non-ASCII wordlist is added later this assumption
    /// must be revisited.
    /// </remarks>
    public Result<byte[]> ToSeed(RecoveryPhrase phrase)
    {
        var validation = Validate(phrase);
        if (!validation.Success)
        {
            return Result<byte[]>.Fail(validation.Error!);
        }

        // Total joined length = sum of word lengths + (count-1) separator spaces.
        var wordCount = phrase.Count;
        var totalChars = wordCount - 1;
        for (var i = 0; i < wordCount; i++)
        {
            totalChars += phrase[i].Length;
        }

        var charBuf = ArrayPool<char>.Shared.Rent(totalChars);
        // ASCII guarantees byte count == char count for our wordlist, but
        // ask the encoder so a future non-ASCII wordlist sizes correctly.
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(totalChars);
        var byteBuf = ArrayPool<byte>.Shared.Rent(maxByteCount);
        var byteCount = 0;

        try
        {
            var pos = 0;
            for (var i = 0; i < wordCount; i++)
            {
                if (i > 0)
                {
                    charBuf[pos++] = ' ';
                }

                var word = phrase[i];
                word.CopyTo(charBuf.AsSpan(pos, word.Length));
                pos += word.Length;
            }

            byteCount = Encoding.UTF8.GetBytes(charBuf.AsSpan(0, totalChars), byteBuf);

            var salt = "mnemonic"u8;
            var seed = Rfc2898DeriveBytes.Pbkdf2(
                byteBuf.AsSpan(0, byteCount), salt, 2048, HashAlgorithmName.SHA512, outputLength: 64);

            return Result<byte[]>.Ok(seed);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Fail(ErrorCode.EncryptionFailed,
                "Failed to derive seed from mnemonic due to a cryptographic error.", ex);
        }
        finally
        {
            // Clearing before return-to-pool is mandatory — pooled buffers
            // may be handed to another caller immediately.
            Array.Clear(charBuf, 0, totalChars);
            if (byteCount > 0)
            {
                Array.Clear(byteBuf, 0, byteCount);
            }

            ArrayPool<char>.Shared.Return(charBuf);
            ArrayPool<byte>.Shared.Return(byteBuf);
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
