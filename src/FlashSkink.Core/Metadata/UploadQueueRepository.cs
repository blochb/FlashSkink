using System.Runtime.CompilerServices;
using Dapper;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Metadata;

/// <summary>
/// Manages the <c>TailUploads</c> (upload queue) and <c>UploadSessions</c> (resumable-upload
/// state) brain tables. <see cref="DequeueNextBatchAsync"/> uses a raw
/// <see cref="Microsoft.Data.Sqlite.SqliteDataReader"/> hot path (Principle 22); all other
/// reads use Dapper.
/// </summary>
public sealed class UploadQueueRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<UploadQueueRepository> _logger;

    /// <summary>Creates an <see cref="UploadQueueRepository"/> bound to the given open brain connection.</summary>
    public UploadQueueRepository(SqliteConnection connection, ILogger<UploadQueueRepository> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    // ── TailUploads ──────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a <c>TailUploads</c> row with <c>Status = PENDING</c>. Returns
    /// <see cref="ErrorCode.PathConflict"/> on a duplicate <c>(FileID, ProviderID)</c>.
    /// </summary>
    public async Task<Result> EnqueueAsync(string fileId, string providerId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow.ToString("O");
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO TailUploads (FileID, ProviderID, Status, QueuedUtc, AttemptCount)
                VALUES (@FileId, @ProviderId, 'PENDING', @QueuedUtc, 0)
                """,
                new { FileId = fileId, ProviderId = providerId, QueuedUtc = now },
                cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("EnqueueAsync cancelled for file {FileId}", fileId);
            return Result.Fail(ErrorCode.Cancelled, "Enqueue was cancelled.", ex);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteErrorCodes.UniqueConstraintFailed)
        {
            _logger.LogInformation("Duplicate enqueue for file {FileId} provider {ProviderId}", fileId, providerId);
            return Result.Fail(ErrorCode.PathConflict,
                $"File '{fileId}' is already enqueued for provider '{providerId}'.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error enqueueing file {FileId}", fileId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to enqueue upload.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error enqueueing file {FileId}", fileId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error enqueueing upload.", ex);
        }
    }

    /// <summary>
    /// Hot-path upload-queue scanner (Principle 22). Returns pending and failed rows for the
    /// given provider up to <paramref name="batchSize"/> items, ordered by <c>QueuedUtc ASC</c>.
    /// Uses a raw <see cref="Microsoft.Data.Sqlite.SqliteDataReader"/> to avoid Dapper allocation.
    /// <para>
    /// <b>Principle-1 note:</b> this is the one sanctioned deviation from "all public methods
    /// return <see cref="Result"/>". SQLite errors propagate as exceptions to the caller
    /// (Phase 3 <c>UploadQueueService</c>) per blueprint §9.7 and the CLAUDE.md Principle 1
    /// carve-out for <c>IAsyncEnumerable&lt;readonly record struct&gt;</c> hot-path readers.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<TailUploadRow> DequeueNextBatchAsync(
        string providerId,
        int batchSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT FileID, ProviderID, Status, RemoteId, QueuedUtc, AttemptCount
            FROM TailUploads
            WHERE ProviderID = @providerId AND Status IN ('PENDING', 'FAILED')
            ORDER BY QueuedUtc ASC
            LIMIT @batchSize
            """;
        cmd.Parameters.AddWithValue("@providerId", providerId);
        cmd.Parameters.AddWithValue("@batchSize", batchSize);

        ct.ThrowIfCancellationRequested();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new TailUploadRow(
                FileId: reader.GetString(0),
                ProviderId: reader.GetString(1),
                Status: reader.GetString(2),
                RemoteId: reader.IsDBNull(3) ? null : reader.GetString(3),
                QueuedUtc: DateTime.Parse(reader.GetString(4), null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                AttemptCount: reader.GetInt32(5));
        }
    }

    /// <summary>Sets a row to <c>UPLOADING</c> and increments <c>AttemptCount</c>.</summary>
    public async Task<Result> MarkUploadingAsync(string fileId, string providerId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE TailUploads
                SET Status = 'UPLOADING', LastAttemptUtc = @Now, AttemptCount = AttemptCount + 1
                WHERE FileID = @FileId AND ProviderID = @ProviderId
                """,
                new { Now = DateTime.UtcNow.ToString("O"), FileId = fileId, ProviderId = providerId },
                cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("MarkUploadingAsync cancelled for file {FileId}", fileId);
            return Result.Fail(ErrorCode.Cancelled, "Mark uploading was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error marking file {FileId} as uploading", fileId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to mark upload as in-progress.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error marking file {FileId} as uploading", fileId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error marking upload as in-progress.", ex);
        }
    }

    /// <summary>Sets a row to <c>UPLOADED</c> and records <c>RemoteId</c> and <c>UploadedUtc</c>.</summary>
    public async Task<Result> MarkUploadedAsync(
        string fileId, string providerId, string remoteId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE TailUploads
                SET Status = 'UPLOADED', RemoteId = @RemoteId, UploadedUtc = @Now
                WHERE FileID = @FileId AND ProviderID = @ProviderId
                """,
                new { RemoteId = remoteId, Now = DateTime.UtcNow.ToString("O"), FileId = fileId, ProviderId = providerId },
                cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("MarkUploadedAsync cancelled for file {FileId}", fileId);
            return Result.Fail(ErrorCode.Cancelled, "Mark uploaded was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error marking file {FileId} as uploaded", fileId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to mark upload as complete.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error marking file {FileId} as uploaded", fileId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error marking upload as complete.", ex);
        }
    }

    /// <summary>Sets a row to <c>FAILED</c> and records <c>LastError</c> and <c>LastAttemptUtc</c>.</summary>
    public async Task<Result> MarkFailedAsync(
        string fileId, string providerId, string lastError, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE TailUploads
                SET Status = 'FAILED', LastError = @LastError, LastAttemptUtc = @Now
                WHERE FileID = @FileId AND ProviderID = @ProviderId
                """,
                new { LastError = lastError, Now = DateTime.UtcNow.ToString("O"), FileId = fileId, ProviderId = providerId },
                cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("MarkFailedAsync cancelled for file {FileId}", fileId);
            return Result.Fail(ErrorCode.Cancelled, "Mark failed was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error marking file {FileId} as failed", fileId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to mark upload as failed.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error marking file {FileId} as failed", fileId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error marking upload as failed.", ex);
        }
    }

    // ── UploadSessions ───────────────────────────────────────────────────────

    /// <summary>
    /// Upserts an <c>UploadSessions</c> row (INSERT OR REPLACE), resetting
    /// <c>BytesUploaded = 0</c> and <c>LastActivityUtc = now</c>. Returns the stored row.
    /// </summary>
    public async Task<Result<UploadSessionRow?>> GetOrCreateSessionAsync(
        string fileId,
        string providerId,
        string sessionUri,
        DateTime sessionExpiresUtc,
        long totalBytes,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow.ToString("O");
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT OR REPLACE INTO UploadSessions
                    (FileID, ProviderID, SessionUri, SessionExpiresUtc, BytesUploaded, TotalBytes, LastActivityUtc)
                VALUES
                    (@FileId, @ProviderId, @SessionUri, @SessionExpiresUtc, 0, @TotalBytes, @Now)
                """,
                new
                {
                    FileId = fileId,
                    ProviderId = providerId,
                    SessionUri = sessionUri,
                    SessionExpiresUtc = sessionExpiresUtc.ToString("O"),
                    TotalBytes = totalBytes,
                    Now = now,
                }, cancellationToken: ct)).ConfigureAwait(false);

            var row = await _connection.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(
                """
                SELECT FileID, ProviderID, SessionUri, SessionExpiresUtc,
                       BytesUploaded, TotalBytes, LastActivityUtc
                FROM UploadSessions
                WHERE FileID = @FileId AND ProviderID = @ProviderId
                """,
                new { FileId = fileId, ProviderId = providerId },
                cancellationToken: ct)).ConfigureAwait(false);

            if (row is null)
            {
                return Result<UploadSessionRow?>.Ok(null);
            }

            var session = new UploadSessionRow(
                FileId: (string)row.FileID,
                ProviderId: (string)row.ProviderID,
                SessionUri: (string)row.SessionUri,
                SessionExpiresUtc: DateTime.Parse((string)row.SessionExpiresUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                BytesUploaded: (long)row.BytesUploaded,
                TotalBytes: (long)row.TotalBytes,
                LastActivityUtc: DateTime.Parse((string)row.LastActivityUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind));

            return Result<UploadSessionRow?>.Ok(session);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("GetOrCreateSessionAsync cancelled for file {FileId}", fileId);
            return Result<UploadSessionRow?>.Fail(ErrorCode.Cancelled, "Session upsert was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error upserting session for file {FileId}", fileId);
            return Result<UploadSessionRow?>.Fail(ErrorCode.DatabaseWriteFailed, "Failed to get or create upload session.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error upserting session for file {FileId}", fileId);
            return Result<UploadSessionRow?>.Fail(ErrorCode.Unknown, "Unexpected error getting or creating upload session.", ex);
        }
    }

    /// <summary>Updates <c>BytesUploaded</c> and <c>LastActivityUtc</c> for an active session.</summary>
    public async Task<Result> UpdateSessionProgressAsync(
        string fileId, string providerId, long bytesUploaded, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE UploadSessions
                SET BytesUploaded = @BytesUploaded, LastActivityUtc = @Now
                WHERE FileID = @FileId AND ProviderID = @ProviderId
                """,
                new { BytesUploaded = bytesUploaded, Now = DateTime.UtcNow.ToString("O"), FileId = fileId, ProviderId = providerId },
                cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("UpdateSessionProgressAsync cancelled for file {FileId}", fileId);
            return Result.Fail(ErrorCode.Cancelled, "Session progress update was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error updating session progress for file {FileId}", fileId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to update session progress.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating session progress for file {FileId}", fileId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error updating session progress.", ex);
        }
    }

    /// <summary>Deletes the <c>UploadSessions</c> row after finalisation or abort.</summary>
    public async Task<Result> DeleteSessionAsync(string fileId, string providerId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM UploadSessions WHERE FileID = @FileId AND ProviderID = @ProviderId",
                new { FileId = fileId, ProviderId = providerId },
                cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("DeleteSessionAsync cancelled for file {FileId}", fileId);
            return Result.Fail(ErrorCode.Cancelled, "Session delete was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error deleting session for file {FileId}", fileId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to delete upload session.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting session for file {FileId}", fileId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error deleting upload session.", ex);
        }
    }
}
