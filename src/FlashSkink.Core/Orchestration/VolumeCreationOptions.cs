using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IO;

namespace FlashSkink.Core.Orchestration;

/// <summary>
/// Optional services injected into <see cref="FlashSkinkVolume"/> factory methods. Carries
/// <see cref="ILoggerFactory"/> and <see cref="INotificationBus"/> required for production use;
/// in tests these default to <see cref="NullLoggerFactory.Instance"/> and a no-op bus.
/// </summary>
public sealed class VolumeCreationOptions
{
    /// <summary>
    /// Logger factory used to create typed loggers for all volume components.
    /// Defaults to <see cref="NullLoggerFactory.Instance"/>.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;

    /// <summary>The notification bus that pipelines publish failure events to.</summary>
    public required INotificationBus NotificationBus { get; init; }

    /// <summary>
    /// Shared <see cref="RecyclableMemoryStreamManager"/>; when <see langword="null"/> the
    /// factory method creates one internally.
    /// </summary>
    public RecyclableMemoryStreamManager? StreamManager { get; init; }

    /// <summary>
    /// Provider registry shared between the upload queue, brain mirror, and
    /// <c>RegisterTailAsync</c>. When <see langword="null"/>, the factory creates a fresh
    /// in-memory registry. Tests typically pass an explicit registry so they can also call
    /// <c>Register</c> directly.
    /// </summary>
    public IProviderRegistry? ProviderRegistry { get; init; }

    /// <summary>
    /// Network-availability signal consumed by <c>UploadQueueService</c>. When
    /// <see langword="null"/>, the factory creates an always-online monitor (Phase 5 wires
    /// the real OS monitor).
    /// </summary>
    public INetworkAvailabilityMonitor? NetworkMonitor { get; init; }

    /// <summary>
    /// Clock for retry-backoff scheduling, orchestrator idle waits, and the brain-mirror
    /// debounce and 15-minute timer. When <see langword="null"/>, <see cref="SystemClock.Instance"/>
    /// is used.
    /// </summary>
    public IClock? Clock { get; init; }

    /// <summary>
    /// Opt-in stale-lock recovery. When <see langword="true"/>, <see cref="FlashSkinkVolume.CreateAsync"/>
    /// and <see cref="FlashSkinkVolume.OpenAsync"/> delete any pre-existing
    /// <c>[skinkRoot]/.flashskink/instance.lock</c> file before attempting to acquire the
    /// single-instance lock. Intended for the Phase-4 CLI <c>--force</c> flag exposed to
    /// users recovering from a previous-process crash that left a stale file behind; the OS
    /// already releases the underlying file lock on process exit, so this flag exists only
    /// to clear the cosmetic file artifact. Setting this to <see langword="true"/> does
    /// <b>not</b> bypass the lock — it merely tolerates a stale file. Production callers
    /// should leave this <see langword="false"/> (the default). (Blueprint §19.5.)
    /// </summary>
    public bool ForceUnlock { get; init; }
}
