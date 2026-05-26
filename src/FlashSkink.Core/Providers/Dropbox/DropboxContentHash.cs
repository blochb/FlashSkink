using System.Buffers;
using System.Security.Cryptography;

namespace FlashSkink.Core.Providers.Dropbox;

/// <summary>
/// Implementation of Dropbox's content-hash algorithm: SHA-256 of each 4 MiB block,
/// concatenated digests SHA-256'd, hex-encoded lowercase.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Memory contract.</strong> The helper streams its input — never materialises the full
/// file in memory regardless of size. For a 10 GiB input the total resident footprint is one
/// pooled 4 MiB block buffer plus roughly <c>2560 × 32 B ≈ 80 KiB</c> of accumulated block
/// digests. The 32-byte digest buffer for each block is <c>stackalloc</c>'d; the final hash buffer
/// is also <c>stackalloc</c>'d. No <c>ReadAllBytesAsync</c>, no full-stream <see cref="MemoryStream"/>
/// of the input. Resolved at Gate 1 review point #2 of <c>.claude/plans/pr-4.4.md</c>.
/// </para>
/// <para>
/// <strong>Empty input.</strong> Returns the SHA-256 of the empty string
/// (<c>e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855</c>) — matches Dropbox's
/// documented behaviour for zero-byte uploads.
/// </para>
/// <para>
/// <strong>Use sites in §4.4.</strong> The provider does NOT consume this helper in V1's upload
/// path (the <see cref="DropboxProvider.FinaliseUploadAsync"/> log line records only the
/// Dropbox-side hash + size — see plan resolution under <c>FinaliseUploadAsync</c>). The helper
/// is shipped now so Phase 5 cross-tail verification has nothing to retrofit.
/// </para>
/// </remarks>
internal static class DropboxContentHash
{
    /// <summary>Block size: 4 MiB per Dropbox's documented algorithm.</summary>
    public const int BlockSize = 4 * 1024 * 1024;

    /// <summary>SHA-256 digest size.</summary>
    private const int DigestSize = 32;

    /// <summary>
    /// Computes Dropbox's content-hash over <paramref name="source"/>.
    /// </summary>
    /// <param name="source">Input stream. Read until EOF. The caller owns the stream's lifetime.</param>
    /// <param name="ct">Cancellation token. On cancellation returns <see cref="string.Empty"/>.</param>
    /// <returns>
    /// The hex-encoded lowercase content-hash. For empty input, the SHA-256 of the empty string.
    /// </returns>
    public static async Task<string> ComputeAsync(Stream source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (ct.IsCancellationRequested)
        {
            return string.Empty;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        // Per-block digest buffer reused across iterations. 32 B heap-allocated rather than
        // stackalloc — Principle 20 forbids stackalloc crossing await, and the buffer must survive
        // across the await on ReadAtLeastAsync. One allocation per ComputeAsync call is bounded.
        byte[] blockDigest = new byte[DigestSize];
        try
        {
            // Concatenated per-block digests grow by 32 B each. Initial capacity = a few blocks.
            using var concat = new MemoryStream(capacity: DigestSize * 8);
            bool anyBlock = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // ReadAtLeastAsync drains until BlockSize bytes are obtained or EOF — Dropbox's
                // algorithm requires fixed BlockSize blocks except for the (possibly shorter) last.
                int read = await source.ReadAtLeastAsync(
                    buffer.AsMemory(0, BlockSize),
                    minimumBytes: BlockSize,
                    throwOnEndOfStream: false,
                    cancellationToken: ct).ConfigureAwait(false);

                if (read == 0)
                {
                    // No more bytes. If we never read any, anyBlock stays false and we fall
                    // through to the empty-input branch below.
                    break;
                }

                SHA256.HashData(buffer.AsSpan(0, read), blockDigest);
                concat.Write(blockDigest);
                anyBlock = true;

                if (read < BlockSize)
                {
                    // Tail block — we're done.
                    break;
                }
            }

            // No more awaits — stackalloc is safe here for the final digest.
            Span<byte> finalDigest = stackalloc byte[DigestSize];
            if (anyBlock)
            {
                // Hash the concatenation of per-block digests.
                SHA256.HashData(concat.GetBuffer().AsSpan(0, (int)concat.Length), finalDigest);
            }
            else
            {
                // Empty input: SHA-256 of the empty string — matches Dropbox's empty-upload hash.
                SHA256.HashData(ReadOnlySpan<byte>.Empty, finalDigest);
            }

            return Convert.ToHexStringLower(finalDigest);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }
}
