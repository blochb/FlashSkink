using System.Buffers.Binary;
using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Crypto;

/// <summary>
/// Serialises and parses the 20-byte versioned preamble that precedes every ciphertext blob
/// on disk. Centralises all format constants so no other type hard-codes sizes.
/// </summary>
/// <remarks>
/// On-disk layout (blueprint §13.6):
/// <code>
/// Offset  Size   Field
/// ------  ----   -----
/// 0       4      Magic ("FSBL")
/// 4       2      Version (uint16 = 1)
/// 6       2      Flags (uint16)
/// 8       12     Nonce
/// </code>
/// </remarks>
public static class BlobHeader
{
    /// <summary>Byte length of the magic marker <c>"FSBL"</c>.</summary>
    public const int MagicSize = 4;

    /// <summary>AES-GCM nonce length in bytes.</summary>
    public const int NonceSize = 12;

    /// <summary>Total on-disk header size: magic + version + flags + nonce.</summary>
    public const int HeaderSize = 20;

    /// <summary>AES-256-GCM authentication tag length in bytes.</summary>
    public const int TagSize = 16;

    private const ushort SupportedVersion = 1;
    private const ushort AllValidFlagsMask = (ushort)(BlobFlags.CompressedLz4 | BlobFlags.CompressedZstd);
    private static ReadOnlySpan<byte> Magic => "FSBL"u8;

    /// <summary>
    /// Writes exactly <see cref="HeaderSize"/> bytes to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">Target span; must be at least <see cref="HeaderSize"/> bytes.</param>
    /// <param name="flags">Compression flags to encode.</param>
    /// <param name="nonce">12-byte nonce; must be exactly <see cref="NonceSize"/> bytes.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when preconditions are violated — this method is internal so the throw
    /// does not cross a public API boundary (Principle 1).
    /// </exception>
    internal static void Write(Span<byte> destination, BlobFlags flags, ReadOnlySpan<byte> nonce)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException($"destination must be at least {HeaderSize} bytes.", nameof(destination));
        }

        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException($"nonce must be exactly {NonceSize} bytes.", nameof(nonce));
        }

        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], SupportedVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)flags);
        nonce.CopyTo(destination[8..]);
    }

    /// <summary>
    /// Parses the header from <paramref name="source"/>, validating magic and version.
    /// </summary>
    /// <param name="source">The full blob span; must be at least <see cref="HeaderSize"/> bytes.</param>
    /// <param name="flags">On success: the decoded compression flags. On failure: <see cref="BlobFlags.None"/>.</param>
    /// <param name="nonce">
    /// On success: a zero-copy slice of <paramref name="source"/> containing the 12-byte nonce.
    /// On failure: <see cref="ReadOnlySpan{T}.Empty"/>.
    /// <para>
    /// <b>Lifetime:</b> the returned nonce span is valid only as long as <paramref name="source"/> is in scope;
    /// callers must not store it beyond the lifetime of <paramref name="source"/>.
    /// </para>
    /// </param>
    /// <returns>
    /// <see cref="Result.Ok()"/> on success.
    /// <see cref="ErrorCode.VolumeCorrupt"/> on wrong magic or insufficient length.
    /// <see cref="ErrorCode.VolumeIncompatibleVersion"/> on unknown version.
    /// </returns>
    public static Result Parse(ReadOnlySpan<byte> source, out BlobFlags flags, out ReadOnlySpan<byte> nonce)
    {
        flags = BlobFlags.None;
        nonce = ReadOnlySpan<byte>.Empty;

        if (source.Length < HeaderSize)
        {
            return Result.Fail(ErrorCode.VolumeCorrupt, "Blob is too short to contain a valid header.");
        }

        if (!source[..MagicSize].SequenceEqual(Magic))
        {
            return Result.Fail(ErrorCode.VolumeCorrupt, "Blob header magic is invalid; the blob may be corrupt.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
        if (version != SupportedVersion)
        {
            return Result.Fail(ErrorCode.VolumeIncompatibleVersion,
                $"Blob header version {version} is not supported by this build (expected {SupportedVersion}).");
        }

        ushort rawFlags = BinaryPrimitives.ReadUInt16LittleEndian(source[6..]);
        if ((rawFlags & ~AllValidFlagsMask) != 0)
        {
            return Result.Fail(ErrorCode.VolumeCorrupt, $"Blob header contains unknown flag bits: 0x{rawFlags:X4}.");
        }

        flags = (BlobFlags)rawFlags;
        nonce = source.Slice(8, NonceSize);
        return Result.Ok();
    }
}
