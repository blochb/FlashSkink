using System.Buffers;
using System.Security.Cryptography;

namespace FlashSkink.Core.Buffers;

/// <summary>
/// Wraps a rented <see cref="IMemoryOwner{T}"/> and zeroes the full underlying span via
/// <see cref="CryptographicOperations.ZeroMemory"/> before returning the buffer to the pool.
/// The <see cref="Memory"/> property exposes a slice of exactly the requested length — the
/// over-allocation from the pool is hidden from callers (see blueprint §9.2).
/// </summary>
internal sealed class ClearOnDisposeOwner : IMemoryOwner<byte>
{
    private IMemoryOwner<byte>? _inner;
    private readonly int _length;

    /// <summary>
    /// Wraps <paramref name="inner"/>, exposing exactly <paramref name="length"/> bytes.
    /// The caller transfers ownership of <paramref name="inner"/> to this instance.
    /// </summary>
    internal ClearOnDisposeOwner(IMemoryOwner<byte> inner, int length)
    {
        _inner = inner;
        _length = length;
    }

    /// <inheritdoc/>
    public Memory<byte> Memory => _inner is null ? Memory<byte>.Empty : _inner.Memory[.._length];

    /// <summary>
    /// Zeroes the full rented span (including any over-allocation beyond <see cref="Memory"/>),
    /// then returns the buffer to the pool. Idempotent — safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        var inner = Interlocked.Exchange(ref _inner, null);
        if (inner is null) { return; }
        CryptographicOperations.ZeroMemory(inner.Memory.Span);
        inner.Dispose();
    }

    /// <summary>
    /// Rents a buffer of at least <paramref name="length"/> bytes from <paramref name="pool"/>
    /// (or <see cref="MemoryPool{T}.Shared"/> when <see langword="null"/>) and wraps it in a
    /// <see cref="ClearOnDisposeOwner"/> that will zero and return it on dispose.
    /// </summary>
    /// <param name="length">The exact number of bytes the caller needs.</param>
    /// <param name="pool">
    /// Pool to rent from. Supply a recording pool in tests to verify zero-on-dispose behaviour
    /// without relying on <see cref="System.Buffers.ArrayPool{T}"/> return ordering.
    /// </param>
    internal static ClearOnDisposeOwner Rent(int length, MemoryPool<byte>? pool = null)
    {
        var owner = (pool ?? MemoryPool<byte>.Shared).Rent(length);
        return new ClearOnDisposeOwner(owner, length);
    }
}
