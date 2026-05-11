using System.Buffers.Binary;

namespace FlashSkink.Core.Engine;

/// <summary>
/// 16-byte authenticated header prepended to every encrypted brain mirror payload.
/// Used both as the on-disk prefix and as AES-GCM additional authenticated data, so a tampered
/// header (rollback attempt with a swapped timestamp) fails decryption. Blueprint §16.7 / §3.5.
/// </summary>
/// <remarks>
/// Layout (little-endian throughout):
/// <list type="table">
///   <item><term>0..3</term><description>Magic <c>"FSBM"</c> = <c>0x46 0x53 0x42 0x4D</c>.</description></item>
///   <item><term>4..5</term><description>Version (uint16). Currently <c>1</c>.</description></item>
///   <item><term>6..7</term><description>Reserved (uint16). Always zero in V1.</description></item>
///   <item><term>8..15</term><description><see cref="DateTime.ToBinary"/> of the snapshot UTC timestamp.</description></item>
/// </list>
/// Phase 5 recovery reuses <see cref="TryParse"/> to validate a downloaded mirror before
/// AES-GCM decryption; the same 16-byte slice serves as the AAD passed to <c>AesGcm.Decrypt</c>.
/// </remarks>
internal static class BrainMirrorHeader
{
    /// <summary>Total header size in bytes.</summary>
    public const int Size = 16;

    /// <summary>Current header version.</summary>
    public const ushort Version = 1;

    /// <summary>ASCII <c>"FSBM"</c> encoded little-endian as a 32-bit constant.</summary>
    public const uint Magic = 0x4D425346;

    /// <summary>
    /// Writes the V1 header layout into <paramref name="dest"/>. <paramref name="dest"/> must be
    /// exactly <see cref="Size"/> bytes; passing a shorter span is an internal precondition
    /// violation and throws <see cref="ArgumentException"/> (sanctioned per Principle 1 for
    /// private helpers).
    /// </summary>
    public static void Write(Span<byte> dest, DateTime utcTimestamp)
    {
        if (dest.Length != Size)
        {
            throw new ArgumentException(
                $"Brain mirror header requires exactly {Size} bytes; got {dest.Length}.",
                nameof(dest));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(dest[..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(4, 2), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(6, 2), 0);
        BinaryPrimitives.WriteInt64LittleEndian(dest.Slice(8, 8), utcTimestamp.ToBinary());
    }

    /// <summary>
    /// Attempts to parse a brain-mirror header. Returns <see langword="false"/> when the input
    /// is shorter than <see cref="Size"/> or when the magic does not match. A mismatching
    /// version is <em>not</em> a parse failure — <paramref name="version"/> is populated and
    /// the caller decides whether it can handle the value (Phase 5 surfaces an "upgrade
    /// required" error for unknown versions).
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<byte> src,
        out ushort version,
        out DateTime utcTimestamp)
    {
        version = 0;
        utcTimestamp = default;

        if (src.Length < Size)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(src[..4]);
        if (magic != Magic)
        {
            return false;
        }

        version = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(4, 2));
        long binary = BinaryPrimitives.ReadInt64LittleEndian(src.Slice(8, 8));
        try
        {
            utcTimestamp = DateTime.FromBinary(binary);
        }
        catch (ArgumentException)
        {
            // FromBinary throws ArgumentException for ticks values outside DateTime's range;
            // treat as malformed header.
            utcTimestamp = default;
            return false;
        }

        return true;
    }
}
