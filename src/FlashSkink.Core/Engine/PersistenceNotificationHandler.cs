using System.Text.Json;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Metadata;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Engine;

/// <summary>
/// Persists <see cref="NotificationSeverity.Error"/> and <see cref="NotificationSeverity.Critical"/>
/// notifications to <c>BackgroundFailures</c> so they survive process restart (Principle 24).
/// <see cref="NotificationSeverity.Info"/> and <see cref="NotificationSeverity.Warning"/> are not persisted.
/// </summary>
public sealed class PersistenceNotificationHandler : INotificationHandler
{
    private readonly BackgroundFailureRepository _repository;
    private readonly ILogger<PersistenceNotificationHandler> _logger;

    /// <param name="repository">The repository used to persist background failures.</param>
    /// <param name="logger">Logger for persistence faults and cancellation events.</param>
    public PersistenceNotificationHandler(
        BackgroundFailureRepository repository,
        ILogger<PersistenceNotificationHandler> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask HandleAsync(Notification notification, CancellationToken ct)
    {
        if (notification.Severity is NotificationSeverity.Info or NotificationSeverity.Warning)
        {
            return;
        }

        var failure = new BackgroundFailure
        {
            FailureId = Guid.NewGuid().ToString(),
            OccurredUtc = notification.OccurredUtc,
            Source = notification.Source,
            ErrorCode = notification.Error?.Code.ToString() ?? "Unknown",
            Message = notification.Message,
            Metadata = SerialiseMetadata(notification.Error?.Metadata),
            Acknowledged = false,
        };

        try
        {
            var result = await _repository.AppendAsync(failure, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogWarning(
                    "Failed to persist background failure {FailureId}: {Code} {Message}",
                    failure.FailureId,
                    result.Error?.Code,
                    result.Error?.Message);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Persist of background failure {FailureId} cancelled.", failure.FailureId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error persisting background failure {FailureId}.", failure.FailureId);
        }
    }

    private static string? SerialiseMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(metadata);
    }
}
