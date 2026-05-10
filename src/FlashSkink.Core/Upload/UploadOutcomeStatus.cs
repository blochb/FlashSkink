namespace FlashSkink.Core.Upload;

/// <summary>
/// Discriminator on <see cref="UploadOutcome"/> describing the result of one
/// <c>RangeUploader.UploadAsync</c> call.
/// </summary>
public enum UploadOutcomeStatus
{
    /// <summary>The blob was uploaded, finalised, and (when the provider supports it) verified.</summary>
    Completed = 0,

    /// <summary>
    /// The in-range retry ladder (§21.1) escalated for at least one range. The caller should
    /// consult <see cref="RetryPolicy.NextCycleAttempt"/> and re-enter <c>UploadAsync</c> later.
    /// </summary>
    RetryableFailure = 1,

    /// <summary>
    /// An unrecoverable condition was encountered (auth failed, quota exceeded, hash mismatch,
    /// local blob corruption). The caller should mark the row <c>FAILED</c> immediately.
    /// </summary>
    PermanentFailure = 2,
}
