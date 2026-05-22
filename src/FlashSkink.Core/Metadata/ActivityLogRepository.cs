using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Metadata;

/// <summary>
/// Append-only audit trail for user-visible file and tail operations. All writes append new
/// rows; existing rows are never updated. Via Dapper.
/// </summary>
public sealed class ActivityLogRepository
{
    private readonly IBrainAccess _brain;
    private readonly ILogger<ActivityLogRepository> _logger;

    /// <summary>Creates an <see cref="ActivityLogRepository"/> bound to the given brain access wrapper.</summary>
    public ActivityLogRepository(IBrainAccess brain, ILogger<ActivityLogRepository> logger)
    {
        _brain = brain;
        _logger = logger;
    }

    private static ActivityLogEntry MapEntry(dynamic r) => new()
    {
        EntryId = (string)r.EntryID,
        OccurredUtc = DateTime.Parse((string)r.OccurredUtc, null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        Category = (string)r.Category,
        Summary = (string)r.Summary,
        Detail = r.Detail is DBNull || r.Detail is null ? null : (string)r.Detail,
    };

    /// <summary>Appends one activity log entry.</summary>
    public async Task<Result> AppendAsync(ActivityLogEntry entry, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var scope = await _brain.LockAsync(ct).ConfigureAwait(false);
            await scope.Connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ActivityLog (EntryID, OccurredUtc, Category, Summary, Detail)
                VALUES (@EntryId, @OccurredUtc, @Category, @Summary, @Detail)
                """,
                new
                {
                    entry.EntryId,
                    OccurredUtc = entry.OccurredUtc.ToString("O"),
                    entry.Category,
                    entry.Summary,
                    entry.Detail,
                }, cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("ActivityLog append cancelled");
            return Result.Fail(ErrorCode.Cancelled, "Activity log append was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error appending activity log entry");
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to append activity log entry.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error appending activity log entry");
            return Result.Fail(ErrorCode.Unknown, "Unexpected error appending activity log entry.", ex);
        }
    }

    /// <summary>Returns the <paramref name="limit"/> most recent log entries, newest first.</summary>
    public async Task<Result<IReadOnlyList<ActivityLogEntry>>> ListRecentAsync(
        int limit, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var scope = await _brain.LockAsync(ct).ConfigureAwait(false);
            var rows = await scope.Connection.QueryAsync<dynamic>(new CommandDefinition(
                """
                SELECT EntryID, OccurredUtc, Category, Summary, Detail
                FROM ActivityLog
                ORDER BY OccurredUtc DESC
                LIMIT @Limit
                """,
                new { Limit = limit }, cancellationToken: ct)).ConfigureAwait(false);
            return Result<IReadOnlyList<ActivityLogEntry>>.Ok(rows.Select(MapEntry).ToList());
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("ListRecentAsync cancelled");
            return Result<IReadOnlyList<ActivityLogEntry>>.Fail(ErrorCode.Cancelled, "List recent was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error listing recent activity log entries");
            return Result<IReadOnlyList<ActivityLogEntry>>.Fail(ErrorCode.DatabaseWriteFailed, "Failed to list activity log.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing recent activity log entries");
            return Result<IReadOnlyList<ActivityLogEntry>>.Fail(ErrorCode.Unknown, "Unexpected error listing activity log.", ex);
        }
    }

    /// <summary>
    /// Returns the <paramref name="limit"/> most recent entries with the given
    /// <paramref name="category"/>, newest first.
    /// </summary>
    public async Task<Result<IReadOnlyList<ActivityLogEntry>>> ListByCategoryAsync(
        string category, int limit, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var scope = await _brain.LockAsync(ct).ConfigureAwait(false);
            var rows = await scope.Connection.QueryAsync<dynamic>(new CommandDefinition(
                """
                SELECT EntryID, OccurredUtc, Category, Summary, Detail
                FROM ActivityLog
                WHERE Category = @Category
                ORDER BY OccurredUtc DESC
                LIMIT @Limit
                """,
                new { Category = category, Limit = limit }, cancellationToken: ct)).ConfigureAwait(false);
            return Result<IReadOnlyList<ActivityLogEntry>>.Ok(rows.Select(MapEntry).ToList());
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("ListByCategoryAsync cancelled for category {Category}", category);
            return Result<IReadOnlyList<ActivityLogEntry>>.Fail(ErrorCode.Cancelled, "List by category was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error listing activity log for category {Category}", category);
            return Result<IReadOnlyList<ActivityLogEntry>>.Fail(ErrorCode.DatabaseWriteFailed, "Failed to list activity log by category.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing activity log for category {Category}", category);
            return Result<IReadOnlyList<ActivityLogEntry>>.Fail(ErrorCode.Unknown, "Unexpected error listing activity log by category.", ex);
        }
    }
}
