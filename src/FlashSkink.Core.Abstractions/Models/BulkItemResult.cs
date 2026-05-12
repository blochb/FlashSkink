using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Abstractions.Models;

/// <summary>
/// Per-item outcome inside a <see cref="BulkWriteReceipt"/>. Pairs the requested virtual path
/// with the <see cref="Result{T}"/> returned by the underlying single-item write — successful
/// items carry a <c>WriteReceipt</c>; failed items carry an <c>ErrorContext</c>.
/// </summary>
public sealed record BulkItemResult
{
    /// <summary>The virtual path that was submitted in the original <see cref="BulkWriteItem"/>.</summary>
    public required string VirtualPath { get; init; }

    /// <summary>The per-item write outcome.</summary>
    public required Result<WriteReceipt> Outcome { get; init; }
}
