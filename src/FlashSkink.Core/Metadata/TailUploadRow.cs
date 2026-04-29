namespace FlashSkink.Core.Metadata;

/// <summary>
/// Hot-path DTO yielded by <see cref="UploadQueueRepository.DequeueNextBatchAsync"/>. One
/// pending upload task per row. Kept as a <see langword="readonly record struct"/> to avoid
/// heap allocation on the upload-queue scan hot path (Principle 22).
/// </summary>
public readonly record struct TailUploadRow(
    string FileId,
    string ProviderId,
    string Status,
    string? RemoteId,
    DateTime QueuedUtc,
    int AttemptCount);
