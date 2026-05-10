namespace FlashSkink.Core.Upload;

/// <summary>
/// Shared constants for the upload pipeline. Blueprint §15.5 (range size), §15.6 (concurrency),
/// §15.8 (poll intervals), §16.7 (brain mirror). All cross-cutting decisions from Phase 3 §3.1
/// are encoded here so the orchestrator, the range uploader, and the brain-mirror service all
/// reference one source of truth.
/// </summary>
public static class UploadConstants
{
    /// <summary>
    /// Maximum bytes per upload range: 4 MiB. Blueprint §15.5 Decision B3-b. Adaptive sizing
    /// is post-V1; this constant is the single V1 source of truth for the range loop.
    /// </summary>
    public const int RangeSize = 4 * 1024 * 1024;

    /// <summary>
    /// Maximum concurrent ranges in flight per tail worker: 1 (sequential ranges). Blueprint §15.6
    /// cross-cutting decision 4. A single sequential range loop fully pipelines upload bandwidth on
    /// consumer connections; per-range concurrency is a profiling-driven V2+ optimisation.
    /// </summary>
    public const int MaxRangesInFlightPerTail = 1;

    /// <summary>Seconds a per-tail worker sleeps between dequeue iterations when the queue is empty. Blueprint §15.8.</summary>
    public const int WorkerIdlePollSeconds = 60;

    /// <summary>Seconds the orchestrator sleeps between registry polls when no wakeup signal is received. Blueprint §15.8.</summary>
    public const int OrchestratorIdlePollSeconds = 30;

    /// <summary>Minutes between scheduled brain-mirror uploads on a running volume. Blueprint §16.7.</summary>
    public const int BrainMirrorIntervalMinutes = 15;

    /// <summary>Number of rolling brain mirrors retained per tail. Older mirrors are pruned after each new upload. Blueprint §16.7.</summary>
    public const int BrainMirrorRollingCount = 3;
}
