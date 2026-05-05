using System.Text.Json;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Storage;

/// <summary>
/// Wraps one user-level write attempt in a WAL row whose phase transitions:
/// <c>PREPARE → COMMITTED</c> on success (via <see cref="CompleteAsync"/>), or
/// <c>PREPARE → FAILED</c> on dispose-without-complete. Centralises the §21.3
/// invariant-restoring rollback logic — §2.5's <c>WritePipeline</c> constructs one of these
/// per write call. Construction is via <see cref="OpenAsync"/> only.
/// </summary>
public sealed class WriteWalScope : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = null, // preserve PascalCase on disk for manual recovery diagnostics
    };

    private readonly WalRepository _wal;
    private readonly AtomicBlobWriter _blobWriter;
    private readonly string _skinkRoot;
    private readonly string _fileId;
    private readonly string _blobId;
    private readonly string _virtualPath;
    private readonly string _walId;
    private readonly ILogger<WriteWalScope> _logger;

    private bool _renameCompleted;
    private bool _completed;
    private bool _disposed;

    private WriteWalScope(
        WalRepository wal,
        AtomicBlobWriter blobWriter,
        string skinkRoot,
        string fileId,
        string blobId,
        string virtualPath,
        string walId,
        ILogger<WriteWalScope> logger)
    {
        _wal = wal;
        _blobWriter = blobWriter;
        _skinkRoot = skinkRoot;
        _fileId = fileId;
        _blobId = blobId;
        _virtualPath = virtualPath;
        _walId = walId;
        _logger = logger;
    }

    /// <summary>
    /// Opens a new WAL scope by inserting a <c>PREPARE</c> row into the WAL table. Returns a
    /// failed result (no scope to dispose) if the WAL insert fails — there is no on-disk staging
    /// file yet, so no cleanup is required by the caller.
    /// </summary>
    public static async Task<Result<WriteWalScope>> OpenAsync(
        WalRepository wal,
        AtomicBlobWriter blobWriter,
        string skinkRoot,
        string fileId,
        string blobId,
        string virtualPath,
        ILogger<WriteWalScope> logger,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new WriteWalPayload(fileId, blobId, virtualPath, skinkRoot),
                s_jsonOptions);

            var walRow = new WalRow(
                WalId: Guid.NewGuid().ToString("N"),
                Operation: "WRITE",
                Phase: "PREPARE",
                StartedUtc: DateTime.UtcNow,
                UpdatedUtc: DateTime.UtcNow,
                Payload: payload);

            var insert = await wal.InsertAsync(walRow, transaction: null, ct).ConfigureAwait(false);
            if (!insert.Success)
            {
                return Result<WriteWalScope>.Fail(insert.Error!);
            }

            var scope = new WriteWalScope(
                wal, blobWriter, skinkRoot, fileId, blobId, virtualPath, walRow.WalId, logger);
            return Result<WriteWalScope>.Ok(scope);
        }
        catch (OperationCanceledException ex)
        {
            return Result<WriteWalScope>.Fail(ErrorCode.Cancelled, "WAL scope open cancelled.", ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error opening WAL scope for blob {BlobId}", blobId);
            return Result<WriteWalScope>.Fail(ErrorCode.Unknown, "Unexpected error opening WAL scope.", ex);
        }
    }

    /// <summary>
    /// Records that the atomic rename completed. Must be called by §2.5 after
    /// <see cref="AtomicBlobWriter.WriteAsync"/> returns success and before the brain transaction
    /// begins, so that <see cref="DisposeAsync"/> knows to also delete the destination file on
    /// rollback. Synchronous — no I/O, no allocations.
    /// </summary>
    public void MarkRenamed()
    {
        _renameCompleted = true;
    }

    /// <summary>
    /// Transitions the WAL row <c>PREPARE → COMMITTED</c>. Idempotent — calling twice returns
    /// <see cref="Result.Ok()"/> without issuing a second transition.
    /// </summary>
    /// <remarks>
    /// When <paramref name="transaction"/> is non-null, the WAL UPDATE rides inside the caller's
    /// transaction. In this case <see cref="ConfirmCommitted"/> must be called after the
    /// transaction commits successfully — only then does <see cref="DisposeAsync"/> treat the
    /// scope as done and skip rollback cleanup. If the transaction is rolled back (e.g.
    /// <c>SQLITE_IOERR</c> on <c>tx.Commit()</c>), the WAL UPDATE is undone by SQLite and
    /// <see cref="DisposeAsync"/> runs cleanup as normal because <c>_completed</c> was never set.
    /// When <paramref name="transaction"/> is null the WAL UPDATE auto-commits and
    /// <c>_completed</c> is set immediately.
    /// The <paramref name="ct"/> parameter is accepted for API symmetry (Principle 13) but is
    /// <em>not</em> forwarded to the WAL transition (Principle 17).
    /// </remarks>
    public async Task<Result> CompleteAsync(
        SqliteTransaction? transaction = null,
        CancellationToken ct = default)
    {
        if (_completed)
        {
            return Result.Ok();
        }

        var transition = await _wal.TransitionAsync(
                _walId, "COMMITTED", CancellationToken.None, transaction)
            .ConfigureAwait(false);
        if (!transition.Success)
        {
            _logger.LogError(
                "Failed to transition WAL row {WalId} to COMMITTED: {Code} {Message}. " +
                "Phase 5 recovery will reconcile.",
                _walId, transition.Error?.Code, transition.Error?.Message);
            return transition;
        }

        // When no transaction is provided the WAL UPDATE committed immediately — mark done now.
        // When a transaction is provided the caller must invoke ConfirmCommitted() after
        // tx.Commit() succeeds; until then _completed stays false so DisposeAsync can clean up
        // if the commit fails (§21.3).
        if (transaction is null)
        {
            _completed = true;
        }

        return Result.Ok();
    }

    /// <summary>
    /// Seals the scope after the caller's brain transaction has successfully committed.
    /// Must be called immediately after <c>tx.Commit()</c> returns without throwing, when
    /// <see cref="CompleteAsync"/> was invoked with a non-null transaction. After this call
    /// <see cref="DisposeAsync"/> is a no-op. Synchronous — no I/O, no allocations.
    /// </summary>
    public void ConfirmCommitted()
    {
        _completed = true;
    }

    /// <summary>
    /// Idempotent rollback. If <see cref="CompleteAsync"/> was never called, transitions the WAL
    /// row to <c>FAILED</c> and best-effort deletes staging and (if <see cref="MarkRenamed"/> was
    /// called) destination files. All internal <see langword="await"/> sites use
    /// <see cref="CancellationToken.None"/> as a literal (Principle 17). Never throws.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_completed)
        {
            return;
        }

        try
        {
            var deleteStaging = await _blobWriter
                .DeleteStagingAsync(_skinkRoot, _blobId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!deleteStaging.Success)
            {
                _logger.LogError(
                    "Failed to delete staging file during WAL rollback for blob {BlobId}: {Code} {Message}",
                    _blobId, deleteStaging.Error?.Code, deleteStaging.Error?.Message);
            }

            if (_renameCompleted)
            {
                var deleteDest = await _blobWriter
                    .DeleteDestinationAsync(_skinkRoot, _blobId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!deleteDest.Success)
                {
                    _logger.LogError(
                        "Failed to delete destination file during WAL rollback for blob {BlobId}: {Code} {Message}",
                        _blobId, deleteDest.Error?.Code, deleteDest.Error?.Message);
                }
            }

            var transition = await _wal
                .TransitionAsync(_walId, "FAILED", CancellationToken.None)
                .ConfigureAwait(false);
            if (!transition.Success)
            {
                _logger.LogError(
                    "Failed to transition WAL row {WalId} to FAILED: {Code} {Message}. " +
                    "Phase 5 recovery will reconcile.",
                    _walId, transition.Error?.Code, transition.Error?.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error during WriteWalScope rollback for WAL {WalId}, blob {BlobId}",
                _walId, _blobId);
        }
    }
}

/// <summary>
/// JSON payload persisted in <c>WAL.Payload</c> for <c>Operation = "WRITE"</c> rows.
/// PascalCase property names are preserved on disk for manual recovery diagnostics.
/// </summary>
internal sealed record WriteWalPayload(
    string FileID,
    string BlobID,
    string VirtualPath,
    string SkinkRoot);
