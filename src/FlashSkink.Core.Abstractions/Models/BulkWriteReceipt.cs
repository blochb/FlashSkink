namespace FlashSkink.Core.Abstractions.Models;

/// <summary>
/// Aggregate result of <c>FlashSkinkVolume.WriteBulkAsync</c> — one <see cref="BulkItemResult"/>
/// per submitted item, in submission order. Per §11.1, bulk operations are partial-failure-aware:
/// individual item failures live inside the receipt rather than aborting the operation.
/// </summary>
public sealed record BulkWriteReceipt
{
    /// <summary>Per-item outcomes, in submission order.</summary>
    public required IReadOnlyList<BulkItemResult> Items { get; init; }
}
