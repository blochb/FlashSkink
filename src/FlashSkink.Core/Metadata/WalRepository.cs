using Dapper;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Metadata;

/// <summary>
/// Manages the <c>WAL</c> crash-recovery state machine. Callers embed WAL inserts in their
/// own transactions by passing the transaction to <see cref="InsertAsync"/>. Compensation
/// paths call <see cref="TransitionAsync"/> with <see cref="CancellationToken.None"/> (Principle 17).
/// </summary>
/// <remarks>
/// All SQL flows through <see cref="IBrainAccess"/> per Principle 36. Overloads that accept a
/// <see cref="SqliteTransaction"/> assume the caller already holds a <see cref="BrainScope"/>
/// for the transaction's lifetime and therefore do not re-acquire the brain gate; the SQL
/// runs against <see cref="SqliteTransaction.Connection"/>.
/// </remarks>
public sealed class WalRepository
{
    private readonly IBrainAccess _brain;
    private readonly ILogger<WalRepository> _logger;

    /// <summary>Creates a <see cref="WalRepository"/> bound to the given brain access wrapper.</summary>
    public WalRepository(IBrainAccess brain, ILogger<WalRepository> logger)
    {
        _brain = brain;
        _logger = logger;
    }

    /// <summary>
    /// Inserts a WAL row, optionally participating in an existing transaction. When
    /// <paramref name="transaction"/> is <see langword="null"/> this method acquires the brain
    /// scope itself and the INSERT auto-commits; when non-null the caller must already hold
    /// the brain scope for the transaction's lifetime.
    /// </summary>
    public async Task<Result> InsertAsync(
        WalRow row,
        SqliteTransaction? transaction = null,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string sql =
                """
                INSERT INTO WAL (WALID, Operation, Phase, StartedUtc, UpdatedUtc, Payload)
                VALUES (@WalId, @Operation, @Phase, @StartedUtc, @UpdatedUtc, @Payload)
                """;
            var param = new
            {
                row.WalId,
                row.Operation,
                row.Phase,
                StartedUtc = row.StartedUtc.ToString("O"),
                UpdatedUtc = row.UpdatedUtc.ToString("O"),
                row.Payload,
            };
            if (transaction is not null)
            {
                await transaction.Connection!.ExecuteAsync(
                    new CommandDefinition(sql, param, transaction, cancellationToken: ct))
                    .ConfigureAwait(false);
            }
            else
            {
                using var scope = await _brain.LockAsync(ct).ConfigureAwait(false);
                await scope.Connection.ExecuteAsync(
                    new CommandDefinition(sql, param, cancellationToken: ct))
                    .ConfigureAwait(false);
            }
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("WAL insert cancelled for {WalId}", row.WalId);
            return Result.Fail(ErrorCode.Cancelled, "WAL insert was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "WAL insert failed for {WalId}", row.WalId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to insert WAL row.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error inserting WAL row {WalId}", row.WalId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error inserting WAL row.", ex);
        }
    }

    /// <summary>
    /// Transitions a WAL row to a new phase. On compensation paths callers must pass
    /// <see cref="CancellationToken.None"/> as a literal (Principle 17). When
    /// <paramref name="transaction"/> is non-null the UPDATE participates in that transaction
    /// (caller holds the brain scope); when null this method acquires the brain scope and the
    /// UPDATE auto-commits.
    /// </summary>
    /// <remarks>
    /// The <paramref name="transaction"/> parameter is placed after <paramref name="ct"/> (rather
    /// than before) to preserve existing call-site signatures — see Drift Note 1 in
    /// <c>.claude/plans/pr-2.5.md</c>. This matches the deliberate ordering asymmetry of
    /// <see cref="InsertAsync"/> where <c>transaction</c> precedes <c>ct</c>.
    /// </remarks>
    public async Task<Result> TransitionAsync(
        string walId,
        string newPhase,
        CancellationToken ct,
        SqliteTransaction? transaction = null)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string sql =
                "UPDATE WAL SET Phase = @Phase, UpdatedUtc = @UpdatedUtc WHERE WALID = @WalId";
            var param = new
            {
                Phase = newPhase,
                UpdatedUtc = DateTime.UtcNow.ToString("O"),
                WalId = walId,
            };
            int rows;
            if (transaction is not null)
            {
                rows = await transaction.Connection!.ExecuteAsync(
                    new CommandDefinition(sql, param, transaction, cancellationToken: ct))
                    .ConfigureAwait(false);
            }
            else
            {
                using var scope = await _brain.LockAsync(ct).ConfigureAwait(false);
                rows = await scope.Connection.ExecuteAsync(
                    new CommandDefinition(sql, param, cancellationToken: ct))
                    .ConfigureAwait(false);
            }
            if (rows == 0)
            {
                _logger.LogWarning("WAL transition found no row for {WalId}", walId);
            }
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("WAL transition cancelled for {WalId}", walId);
            return Result.Fail(ErrorCode.Cancelled, "WAL transition was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "WAL transition failed for {WalId} → {Phase}", walId, newPhase);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to transition WAL row.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error transitioning WAL row {WalId}", walId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error transitioning WAL row.", ex);
        }
    }

    /// <summary>
    /// Returns all WAL rows whose phase is not <c>COMMITTED</c> or <c>FAILED</c>. Called at
    /// startup by the Phase 5 WAL recovery sweep.
    /// </summary>
    public async Task<Result<IReadOnlyList<WalRow>>> ListIncompleteAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string sql =
                """
                SELECT WALID, Operation, Phase, StartedUtc, UpdatedUtc, Payload
                FROM WAL
                WHERE Phase NOT IN ('COMMITTED', 'FAILED')
                ORDER BY StartedUtc ASC
                """;
            using var scope = await _brain.LockAsync(ct).ConfigureAwait(false);
            var rows = await scope.Connection.QueryAsync<dynamic>(
                new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
            var result = rows
                .Select(r => new WalRow(
                    WalId: (string)r.WALID,
                    Operation: (string)r.Operation,
                    Phase: (string)r.Phase,
                    StartedUtc: DateTime.Parse((string)r.StartedUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                    UpdatedUtc: DateTime.Parse((string)r.UpdatedUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                    Payload: (string)r.Payload))
                .ToList();
            return Result<IReadOnlyList<WalRow>>.Ok(result);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("WAL list cancelled");
            return Result<IReadOnlyList<WalRow>>.Fail(ErrorCode.Cancelled, "WAL list was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Failed to list incomplete WAL rows");
            return Result<IReadOnlyList<WalRow>>.Fail(ErrorCode.DatabaseReadFailed, "Failed to list WAL rows.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing WAL rows");
            return Result<IReadOnlyList<WalRow>>.Fail(ErrorCode.Unknown, "Unexpected error listing WAL rows.", ex);
        }
    }

    /// <summary>Hard-deletes a WAL row after successful recovery.</summary>
    public async Task<Result> DeleteAsync(string walId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var scope = await _brain.LockAsync(ct).ConfigureAwait(false);
            await scope.Connection.ExecuteAsync(
                new CommandDefinition("DELETE FROM WAL WHERE WALID = @WalId",
                    new { WalId = walId }, cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("WAL delete cancelled for {WalId}", walId);
            return Result.Fail(ErrorCode.Cancelled, "WAL delete was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "WAL delete failed for {WalId}", walId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to delete WAL row.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting WAL row {WalId}", walId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error deleting WAL row.", ex);
        }
    }
}
