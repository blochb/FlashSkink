namespace FlashSkink.Core.Abstractions.Models;

/// <summary>
/// One item in a <c>WriteBulkAsync</c> request — a plaintext source stream and its target
/// virtual path. The optional <see cref="OwnedSource"/> lets callers hand ownership of a
/// disposable resource (e.g., a <see cref="System.IO.FileStream"/>) to the bulk method; the
/// volume disposes it after the per-item commit completes (regardless of success). When
/// <see langword="null"/>, the caller retains ownership of <see cref="Source"/>.
/// </summary>
public sealed record BulkWriteItem
{
    /// <summary>The plaintext byte source for this item.</summary>
    public required Stream Source { get; init; }

    /// <summary>The target virtual path for this item inside the volume.</summary>
    public required string VirtualPath { get; init; }

    /// <summary>
    /// Optional disposable whose lifetime is handed to the bulk method; disposed after the
    /// per-item commit completes (success or failure). <see langword="null"/> when the caller
    /// retains ownership.
    /// </summary>
    public IDisposable? OwnedSource { get; init; }
}
