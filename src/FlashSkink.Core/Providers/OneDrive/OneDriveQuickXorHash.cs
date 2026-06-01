using System.Buffers;

namespace FlashSkink.Core.Providers.OneDrive;

/// <summary>
/// Streaming implementation of Microsoft's QuickXorHash, the content hash OneDrive reports for
/// uploaded items. Implemented for future content-integrity verification but not yet wired into the
/// upload path. Output is the standard 20-byte digest, base64-encoded to match the Graph
/// <c>file.hashes.quickXorHash</c> field.
/// </summary>
internal static class OneDriveQuickXorHash
{
    private const int WidthInBits = 160;
    private const int Shift = 11;
    private const int BitsInLastCell = 32;
    private const int CellCount = 3;
    private const int OutputLength = 20;

    /// <summary>
    /// Computes the QuickXorHash of <paramref name="source"/> and returns it base64-encoded. Returns
    /// <see cref="string.Empty"/> if cancellation is observed. Never throws for cancellation.
    /// </summary>
    public static async Task<string> ComputeAsync(Stream source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (ct.IsCancellationRequested)
        {
            return string.Empty;
        }

        var data = new ulong[CellCount];
        long lengthSoFar = 0;
        int shiftSoFar = 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                HashCore(data, ref shiftSoFar, buffer.AsSpan(0, read));
                lengthSoFar += read;
            }
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }

        byte[] digest = HashFinal(data, lengthSoFar);
        return Convert.ToBase64String(digest);
    }

    private static void HashCore(ulong[] data, ref int shiftSoFar, ReadOnlySpan<byte> input)
    {
        for (int i = 0; i < input.Length; i++)
        {
            int vectorArrayIndex = shiftSoFar / 64;
            int vectorOffset = shiftSoFar % 64;

            // Distance from this byte's bit position to the next, used to detect cell wrap.
            bool isLastCell = vectorArrayIndex == CellCount - 1;
            int bitsInVectorCell = isLastCell ? BitsInLastCell : 64;

            if (vectorOffset <= bitsInVectorCell - 8)
            {
                data[vectorArrayIndex] ^= (ulong)input[i] << vectorOffset;
            }
            else
            {
                data[vectorArrayIndex] ^= (ulong)input[i] << vectorOffset;
                data[isLastCell ? 0 : vectorArrayIndex + 1] ^= (ulong)input[i] >> (bitsInVectorCell - vectorOffset);
            }

            shiftSoFar = (shiftSoFar + Shift) % WidthInBits;
        }
    }

    private static byte[] HashFinal(ulong[] data, long lengthSoFar)
    {
        var result = new byte[OutputLength];

        for (int i = 0; i < data.Length; i++)
        {
            int bytesToWrite = i == data.Length - 1 ? BitsInLastCell / 8 : 8;
            byte[] cellBytes = BitConverter.GetBytes(data[i]);
            Array.Copy(cellBytes, 0, result, i * 8, bytesToWrite);
        }

        // Mix the message length into the trailing bytes, matching the reference algorithm.
        byte[] lengthBytes = BitConverter.GetBytes(lengthSoFar);
        for (int i = 0; i < lengthBytes.Length; i++)
        {
            result[(WidthInBits / 8) - lengthBytes.Length + i] ^= lengthBytes[i];
        }

        return result;
    }
}
