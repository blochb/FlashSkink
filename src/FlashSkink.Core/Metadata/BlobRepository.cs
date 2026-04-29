using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Metadata;

/// <summary>
/// CRUD operations on the <c>Blobs</c> brain table. Does not involve WAL — blob mutations
/// that must be atomic with Files DML are handled directly by <see cref="FileRepository"/>.
/// </summary>
public sealed class BlobRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<BlobRepository> _logger;

    /// <summary>Creates a <see cref="BlobRepository"/> bound to the given open brain connection.</summary>
    public BlobRepository(SqliteConnection connection, ILogger<BlobRepository> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    private static BlobRecord MapBlob(dynamic r) => new()
    {
        BlobId = (string)r.BlobID,
        EncryptedSize = (long)r.EncryptedSize,
        PlaintextSize = (long)r.PlaintextSize,
        PlaintextSha256 = (string)r.PlaintextSHA256,
        EncryptedXxHash = (string)r.EncryptedXXHash,
        Compression = r.Compression is DBNull || r.Compression is null ? null : (string)r.Compression,
        BlobPath = (string)r.BlobPath,
        CreatedUtc = DateTime.Parse((string)r.CreatedUtc, null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        SoftDeletedUtc = r.SoftDeletedUtc is DBNull || r.SoftDeletedUtc is null
            ? null
            : DateTime.Parse((string)r.SoftDeletedUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind),
        PurgeAfterUtc = r.PurgeAfterUtc is DBNull || r.PurgeAfterUtc is null
            ? null
            : DateTime.Parse((string)r.PurgeAfterUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind),
    };

    /// <summary>Inserts a new blob row.</summary>
    public async Task<Result> InsertAsync(BlobRecord blob, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string sql =
                """
                INSERT INTO Blobs
                    (BlobID, EncryptedSize, PlaintextSize, PlaintextSHA256, EncryptedXXHash,
                     Compression, BlobPath, CreatedUtc, SoftDeletedUtc, PurgeAfterUtc)
                VALUES
                    (@BlobId, @EncryptedSize, @PlaintextSize, @PlaintextSha256, @EncryptedXxHash,
                     @Compression, @BlobPath, @CreatedUtc, @SoftDeletedUtc, @PurgeAfterUtc)
                """;
            await _connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                blob.BlobId,
                blob.EncryptedSize,
                blob.PlaintextSize,
                blob.PlaintextSha256,
                blob.EncryptedXxHash,
                blob.Compression,
                blob.BlobPath,
                CreatedUtc = blob.CreatedUtc.ToString("O"),
                SoftDeletedUtc = blob.SoftDeletedUtc?.ToString("O"),
                PurgeAfterUtc = blob.PurgeAfterUtc?.ToString("O"),
            }, cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("Blob insert cancelled for {BlobId}", blob.BlobId);
            return Result.Fail(ErrorCode.Cancelled, "Blob insert was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error inserting blob {BlobId}", blob.BlobId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to insert blob.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error inserting blob {BlobId}", blob.BlobId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error inserting blob.", ex);
        }
    }

    /// <summary>
    /// Returns the blob with the given ID, or a successful result with <see langword="null"/> value
    /// when no matching row exists.
    /// </summary>
    public async Task<Result<BlobRecord?>> GetByIdAsync(string blobId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string sql =
                """
                SELECT BlobID, EncryptedSize, PlaintextSize, PlaintextSHA256, EncryptedXXHash,
                       Compression, BlobPath, CreatedUtc, SoftDeletedUtc, PurgeAfterUtc
                FROM Blobs
                WHERE BlobID = @BlobId
                """;
            var rows = await _connection.QueryAsync<dynamic>(
                new CommandDefinition(sql, new { BlobId = blobId }, cancellationToken: ct))
                .ConfigureAwait(false);
            var row = rows.FirstOrDefault();
            return Result<BlobRecord?>.Ok(row is null ? null : MapBlob(row));
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("GetByIdAsync cancelled for blob {BlobId}", blobId);
            return Result<BlobRecord?>.Fail(ErrorCode.Cancelled, "Blob fetch was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error fetching blob {BlobId}", blobId);
            return Result<BlobRecord?>.Fail(ErrorCode.DatabaseWriteFailed, "Failed to fetch blob.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching blob {BlobId}", blobId);
            return Result<BlobRecord?>.Fail(ErrorCode.Unknown, "Unexpected error fetching blob.", ex);
        }
    }

    /// <summary>
    /// Returns the active (non-soft-deleted) blob matching the given plaintext SHA-256 hash, or
    /// <see langword="null"/> when none exists. Used for change-detection short-circuit in Phase 2.
    /// </summary>
    public async Task<Result<BlobRecord?>> GetByPlaintextHashAsync(
        string plaintextSha256, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string sql =
                """
                SELECT BlobID, EncryptedSize, PlaintextSize, PlaintextSHA256, EncryptedXXHash,
                       Compression, BlobPath, CreatedUtc, SoftDeletedUtc, PurgeAfterUtc
                FROM Blobs
                WHERE PlaintextSHA256 = @Hash
                  AND SoftDeletedUtc IS NULL
                LIMIT 1
                """;
            var rows = await _connection.QueryAsync<dynamic>(
                new CommandDefinition(sql, new { Hash = plaintextSha256 }, cancellationToken: ct))
                .ConfigureAwait(false);
            var row = rows.FirstOrDefault();
            return Result<BlobRecord?>.Ok(row is null ? null : MapBlob(row));
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("GetByPlaintextHashAsync cancelled");
            return Result<BlobRecord?>.Fail(ErrorCode.Cancelled, "Blob hash lookup was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error looking up blob by hash");
            return Result<BlobRecord?>.Fail(ErrorCode.DatabaseWriteFailed, "Failed to look up blob by hash.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error looking up blob by hash");
            return Result<BlobRecord?>.Fail(ErrorCode.Unknown, "Unexpected error looking up blob by hash.", ex);
        }
    }

    /// <summary>Soft-deletes a blob, setting <c>SoftDeletedUtc</c> and <c>PurgeAfterUtc</c>.</summary>
    public async Task<Result> SoftDeleteAsync(string blobId, DateTime purgeAfterUtc, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow;
            await _connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Blobs SET SoftDeletedUtc = @Now, PurgeAfterUtc = @PurgeAfter WHERE BlobID = @BlobId",
                new { Now = now.ToString("O"), PurgeAfter = purgeAfterUtc.ToString("O"), BlobId = blobId },
                cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("SoftDeleteAsync cancelled for blob {BlobId}", blobId);
            return Result.Fail(ErrorCode.Cancelled, "Blob soft-delete was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error soft-deleting blob {BlobId}", blobId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to soft-delete blob.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error soft-deleting blob {BlobId}", blobId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error soft-deleting blob.", ex);
        }
    }

    /// <summary>
    /// Marks a blob as immediately purge-eligible by setting both <c>SoftDeletedUtc</c> and
    /// <c>PurgeAfterUtc</c> to now. The Phase 5 healing service will re-download from a tail
    /// and re-insert a fresh blob row.
    /// </summary>
    public async Task<Result> MarkCorruptAsync(string blobId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow.ToString("O");
            await _connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Blobs SET SoftDeletedUtc = @Now, PurgeAfterUtc = @Now WHERE BlobID = @BlobId",
                new { Now = now, BlobId = blobId },
                cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("MarkCorruptAsync cancelled for blob {BlobId}", blobId);
            return Result.Fail(ErrorCode.Cancelled, "Mark corrupt was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error marking blob {BlobId} as corrupt", blobId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to mark blob as corrupt.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error marking blob {BlobId} as corrupt", blobId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error marking blob as corrupt.", ex);
        }
    }

    /// <summary>
    /// Returns all blobs where <c>PurgeAfterUtc &lt;= now</c>. Called by the Phase 5 sweeper.
    /// </summary>
    public async Task<Result<IReadOnlyList<BlobRecord>>> ListPendingPurgeAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string sql =
                """
                SELECT BlobID, EncryptedSize, PlaintextSize, PlaintextSHA256, EncryptedXXHash,
                       Compression, BlobPath, CreatedUtc, SoftDeletedUtc, PurgeAfterUtc
                FROM Blobs
                WHERE PurgeAfterUtc <= @Now
                ORDER BY PurgeAfterUtc ASC
                """;
            var rows = await _connection.QueryAsync<dynamic>(
                new CommandDefinition(sql, new { Now = DateTime.UtcNow.ToString("O") }, cancellationToken: ct))
                .ConfigureAwait(false);
            return Result<IReadOnlyList<BlobRecord>>.Ok(rows.Select(MapBlob).ToList());
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("ListPendingPurgeAsync cancelled");
            return Result<IReadOnlyList<BlobRecord>>.Fail(ErrorCode.Cancelled, "List pending purge was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error listing pending-purge blobs");
            return Result<IReadOnlyList<BlobRecord>>.Fail(ErrorCode.DatabaseWriteFailed, "Failed to list pending-purge blobs.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing pending-purge blobs");
            return Result<IReadOnlyList<BlobRecord>>.Fail(ErrorCode.Unknown, "Unexpected error listing pending-purge blobs.", ex);
        }
    }

    /// <summary>
    /// Hard-deletes the blob row. Called by the Phase 5 sweeper after verifying the on-disk blob
    /// file has been removed.
    /// </summary>
    public async Task<Result> HardDeleteAsync(string blobId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM Blobs WHERE BlobID = @BlobId",
                new { BlobId = blobId }, cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("HardDeleteAsync cancelled for blob {BlobId}", blobId);
            return Result.Fail(ErrorCode.Cancelled, "Blob hard-delete was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error hard-deleting blob {BlobId}", blobId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to hard-delete blob.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error hard-deleting blob {BlobId}", blobId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error hard-deleting blob.", ex);
        }
    }
}
