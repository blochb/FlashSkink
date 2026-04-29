using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Metadata;

/// <summary>
/// Persisted queue for background-service failures that survive process restarts. Background
/// services append rows here; the UI surface reads and acknowledges them on startup (Principle 24).
/// </summary>
public sealed class BackgroundFailureRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<BackgroundFailureRepository> _logger;

    /// <summary>Creates a <see cref="BackgroundFailureRepository"/> bound to the given open brain connection.</summary>
    public BackgroundFailureRepository(SqliteConnection connection, ILogger<BackgroundFailureRepository> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    private static BackgroundFailure MapFailure(dynamic r) => new()
    {
        FailureId = (string)r.FailureID,
        OccurredUtc = DateTime.Parse((string)r.OccurredUtc, null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        Source = (string)r.Source,
        ErrorCode = (string)r.ErrorCode,
        Message = (string)r.Message,
        Metadata = r.Metadata is DBNull || r.Metadata is null ? null : (string)r.Metadata,
        Acknowledged = ((long)r.Acknowledged) != 0,
    };

    /// <summary>Appends a new background failure to the queue.</summary>
    public async Task<Result> AppendAsync(BackgroundFailure failure, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO BackgroundFailures
                    (FailureID, OccurredUtc, Source, ErrorCode, Message, Metadata, Acknowledged)
                VALUES
                    (@FailureId, @OccurredUtc, @Source, @ErrorCode, @Message, @Metadata, 0)
                """,
                new
                {
                    failure.FailureId,
                    OccurredUtc = failure.OccurredUtc.ToString("O"),
                    failure.Source,
                    failure.ErrorCode,
                    failure.Message,
                    failure.Metadata,
                }, cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("AppendAsync cancelled for failure {FailureId}", failure.FailureId);
            return Result.Fail(ErrorCode.Cancelled, "Background failure append was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error appending background failure {FailureId}", failure.FailureId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to append background failure.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error appending background failure {FailureId}", failure.FailureId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error appending background failure.", ex);
        }
    }

    /// <summary>
    /// Returns all unacknowledged failures, ordered newest first. Called at launch to surface
    /// missed background failures to the user.
    /// </summary>
    public async Task<Result<IReadOnlyList<BackgroundFailure>>> ListUnacknowledgedAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var rows = await _connection.QueryAsync<dynamic>(new CommandDefinition(
                """
                SELECT FailureID, OccurredUtc, Source, ErrorCode, Message, Metadata, Acknowledged
                FROM BackgroundFailures
                WHERE Acknowledged = 0
                ORDER BY OccurredUtc DESC
                """, cancellationToken: ct)).ConfigureAwait(false);
            return Result<IReadOnlyList<BackgroundFailure>>.Ok(rows.Select(MapFailure).ToList());
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("ListUnacknowledgedAsync cancelled");
            return Result<IReadOnlyList<BackgroundFailure>>.Fail(ErrorCode.Cancelled, "List unacknowledged was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error listing unacknowledged failures");
            return Result<IReadOnlyList<BackgroundFailure>>.Fail(ErrorCode.DatabaseWriteFailed, "Failed to list unacknowledged failures.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing unacknowledged failures");
            return Result<IReadOnlyList<BackgroundFailure>>.Fail(ErrorCode.Unknown, "Unexpected error listing unacknowledged failures.", ex);
        }
    }

    /// <summary>Marks a single failure as acknowledged.</summary>
    public async Task<Result> AcknowledgeAsync(string failureId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await _connection.ExecuteAsync(new CommandDefinition(
                "UPDATE BackgroundFailures SET Acknowledged = 1 WHERE FailureID = @FailureId",
                new { FailureId = failureId }, cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("AcknowledgeAsync cancelled for failure {FailureId}", failureId);
            return Result.Fail(ErrorCode.Cancelled, "Acknowledge was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error acknowledging failure {FailureId}", failureId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to acknowledge failure.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error acknowledging failure {FailureId}", failureId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error acknowledging failure.", ex);
        }
    }

    /// <summary>Acknowledges all currently unacknowledged failures in one UPDATE.</summary>
    public async Task<Result> AcknowledgeAllAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await _connection.ExecuteAsync(new CommandDefinition(
                "UPDATE BackgroundFailures SET Acknowledged = 1 WHERE Acknowledged = 0",
                cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("AcknowledgeAllAsync cancelled");
            return Result.Fail(ErrorCode.Cancelled, "Acknowledge all was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error acknowledging all failures");
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to acknowledge all failures.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error acknowledging all failures");
            return Result.Fail(ErrorCode.Unknown, "Unexpected error acknowledging all failures.", ex);
        }
    }
}
