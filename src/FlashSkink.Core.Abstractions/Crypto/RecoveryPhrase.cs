using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Abstractions.Crypto;

/// <summary>
/// Zeroizable owner of a BIP-39 recovery phrase. Words are held in owned
/// <c>char[]</c> buffers that are cleared on <see cref="Dispose"/>.
/// Construct via <see cref="FromUserInput"/> for user-typed input, or via
/// the mnemonic-generation path which uses the internal constructor.
/// </summary>
/// <remarks>
/// <para>
/// Post-dispose access to <see cref="Count"/> or the indexer throws
/// <see cref="ObjectDisposedException"/>. This is a deliberate deviation from
/// CLAUDE.md Principle 1 ("Core never throws across its public API boundary"):
/// <c>RecoveryPhrase</c> is a value-container with <see cref="IDisposable"/>
/// semantics, analogous to <c>Stream</c>, <c>SqliteConnection</c>, and
/// <c>SemaphoreSlim</c> — all of which throw <see cref="ObjectDisposedException"/>
/// after disposal. Use-after-dispose is a programming-error class, not a
/// runtime failure mode the caller should handle via <see cref="Result{T}"/>.
/// Factory methods (<see cref="FromUserInput"/> and the generation path) do
/// follow Principle 1 and return <see cref="Result{T}"/>.
/// </para>
/// <para>
/// Limitation of <see cref="FromUserInput"/>: the source <see cref="string"/>
/// values remain on the managed heap until garbage-collected and cannot be
/// zeroed by this factory. Only the destination <c>char[]</c> buffers are
/// zeroed on disposal. See blueprint §18.6 / §18.8.
/// </para>
/// </remarks>
public sealed class RecoveryPhrase : IDisposable
{
    private char[][]? _words;

    /// <summary>
    /// Wraps already-owned, freshly-allocated word buffers. Called by
    /// <c>MnemonicService.Generate</c> in <c>FlashSkink.Core</c> via
    /// <c>InternalsVisibleTo</c>; not part of the public API.
    /// </summary>
    internal RecoveryPhrase(char[][] words)
    {
        _words = words;
    }

    /// <summary>The number of words in the phrase.</summary>
    /// <exception cref="ObjectDisposedException">Thrown if accessed after <see cref="Dispose"/>.</exception>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            return _words!.Length;
        }
    }

    /// <summary>The word at <paramref name="index"/> as a read-only span over the owned buffer.</summary>
    /// <exception cref="ObjectDisposedException">Thrown if accessed after <see cref="Dispose"/>.</exception>
    /// <exception cref="IndexOutOfRangeException">Thrown if <paramref name="index"/> is out of range.</exception>
    public ReadOnlySpan<char> this[int index]
    {
        get
        {
            ThrowIfDisposed();
            return _words![index];
        }
    }

    /// <summary>
    /// Copies user-typed words into newly-allocated, zeroizable <c>char[]</c>
    /// buffers. Used for the recovery / password-reset path where the source
    /// is <see cref="System.Console.ReadLine"/> output.
    /// </summary>
    /// <param name="words">
    /// User-typed words. Empty list is allowed at this layer; word-count
    /// validation happens in <c>MnemonicService.Validate</c>.
    /// </param>
    /// <returns>
    /// <see cref="ErrorCode.InvalidMnemonic"/> if <paramref name="words"/> is
    /// null or contains a null entry. Otherwise an owned <see cref="RecoveryPhrase"/>.
    /// </returns>
    public static Result<RecoveryPhrase> FromUserInput(IReadOnlyList<string> words)
    {
        if (words is null)
        {
            return Result<RecoveryPhrase>.Fail(ErrorCode.InvalidMnemonic,
                "Recovery phrase word list cannot be null.");
        }

        var owned = new char[words.Count][];
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (word is null)
            {
                // Zero any already-allocated destination buffers before failing.
                for (var j = 0; j < i; j++)
                {
                    Array.Clear(owned[j]);
                }

                return Result<RecoveryPhrase>.Fail(ErrorCode.InvalidMnemonic,
                    "Recovery phrase contains a null word.");
            }

            owned[i] = word.ToCharArray();
        }

        return Result<RecoveryPhrase>.Ok(new RecoveryPhrase(owned));
    }

    /// <summary>
    /// Zeros every word's underlying <c>char[]</c> buffer and releases the
    /// reference. Idempotent — double-dispose is a no-op. Never throws.
    /// </summary>
    public void Dispose()
    {
        if (_words is null)
        {
            return;
        }

        foreach (var w in _words)
        {
            if (w is not null)
            {
                Array.Clear(w);
            }
        }

        _words = null;
    }

    private void ThrowIfDisposed()
    {
        if (_words is null)
        {
            throw new ObjectDisposedException(nameof(RecoveryPhrase));
        }
    }
}
