namespace FlashSkink.Core.Engine;

/// <summary>
/// Indicates whether a <see cref="WritePipeline.ExecuteAsync"/> call wrote new content to the
/// skink or detected that the file was already present and unchanged (change-detection
/// short-circuit — §14.1 stage 2).
/// </summary>
public enum WriteStatus
{
    /// <summary>A new blob and file row were committed to the skink.</summary>
    Written = 0,

    /// <summary>
    /// The plaintext SHA-256 matched an existing active blob at the same virtual path; no new
    /// blob or file was written.
    /// </summary>
    Unchanged = 1,
}
