using FlashSkink.Core.Abstractions.Notifications;
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
}
