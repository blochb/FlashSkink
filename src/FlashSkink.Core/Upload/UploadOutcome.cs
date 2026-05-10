using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Core.Upload;

/// <summary>
/// Result of one <c>RangeUploader.UploadAsync</c> call. Carries enough information for the
/// caller (<c>UploadQueueService</c>, §3.4) to write the appropriate brain transaction —
/// <c>MarkUploadedAsync</c> on <see cref="UploadOutcomeStatus.Completed"/>,
/// <c>MarkFailedAsync</c> on <see cref="UploadOutcomeStatus.PermanentFailure"/>, or a cycle-level
/// backoff schedule on <see cref="UploadOutcomeStatus.RetryableFailure"/>.
/// </summary>
/// <remarks>
/// One allocation per blob upload — not per range — so a class record is appropriate. The factory
/// methods are the only sanctioned construction paths; direct <c>new</c> with mismatched fields
/// (e.g. <see cref="RemoteId"/> set on a failure) is malformed and unsupported.
/// </remarks>
public sealed record UploadOutcome
{
    /// <summary>The category of the outcome.</summary>
    public required UploadOutcomeStatus Status { get; init; }

    /// <summary>
    /// Provider-side identifier returned by <see cref="Abstractions.Providers.IStorageProvider.FinaliseUploadAsync"/>.
    /// Non-<see langword="null"/> only when <see cref="Status"/> is
    /// <see cref="UploadOutcomeStatus.Completed"/>.
    /// </summary>
    public string? RemoteId { get; init; }

    /// <summary>Total encrypted bytes uploaded. Equals the blob's <c>EncryptedSize</c> on completion; <c>0</c> on failure.</summary>
    public long BytesUploaded { get; init; }

    /// <summary>Failure code on retryable or permanent failure; <see langword="null"/> on completion.</summary>
    public ErrorCode? FailureCode { get; init; }

    /// <summary>Human-readable failure message; <see langword="null"/> on completion.</summary>
    public string? FailureMessage { get; init; }

    /// <summary>Creates a <see cref="UploadOutcomeStatus.Completed"/> outcome.</summary>
    public static UploadOutcome Completed(string remoteId, long bytesUploaded) => new()
    {
        Status = UploadOutcomeStatus.Completed,
        RemoteId = remoteId,
        BytesUploaded = bytesUploaded,
    };

    /// <summary>Creates a <see cref="UploadOutcomeStatus.RetryableFailure"/> outcome carrying the underlying error.</summary>
    public static UploadOutcome Retryable(ErrorCode code, string message) => new()
    {
        Status = UploadOutcomeStatus.RetryableFailure,
        FailureCode = code,
        FailureMessage = message,
    };

    /// <summary>Creates a <see cref="UploadOutcomeStatus.PermanentFailure"/> outcome carrying the underlying error.</summary>
    public static UploadOutcome Permanent(ErrorCode code, string message) => new()
    {
        Status = UploadOutcomeStatus.PermanentFailure,
        FailureCode = code,
        FailureMessage = message,
    };
}
