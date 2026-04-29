using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Metadata;

/// <summary>
/// All file-tree and folder-tree operations against the <c>Files</c> and <c>DeleteLog</c>
/// brain tables. Uses recursive CTEs for tree traversal and WAL journalling for multi-step
/// mutations. Multi-step writes soft-delete <c>Blobs</c> rows directly (within the same
/// transaction) rather than delegating to <see cref="BlobRepository"/> — this is deliberate
/// to avoid cross-repository coordination and keeps the Blobs soft-delete atomically coupled
/// to the Files DML.
/// </summary>
public sealed class FileRepository
{
    private readonly SqliteConnection _connection;
    private readonly WalRepository _wal;
    private readonly ILogger<FileRepository> _logger;

    /// <summary>Creates a <see cref="FileRepository"/> bound to the given open brain connection.</summary>
    public FileRepository(SqliteConnection connection, WalRepository wal, ILogger<FileRepository> logger)
    {
        _connection = connection;
        _wal = wal;
        _logger = logger;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static VolumeFile MapFile(dynamic r) => new()
    {
        FileId = (string)r.FileID,
        ParentId = r.ParentID is DBNull || r.ParentID is null ? null : (string)r.ParentID,
        IsFolder = ((long)r.IsFolder) != 0,
        IsSymlink = ((long)r.IsSymlink) != 0,
        SymlinkTarget = r.SymlinkTarget is DBNull || r.SymlinkTarget is null ? null : (string)r.SymlinkTarget,
        Name = (string)r.Name,
        Extension = r.Extension is DBNull || r.Extension is null ? null : (string)r.Extension,
        MimeType = r.MimeType is DBNull || r.MimeType is null ? null : (string)r.MimeType,
        VirtualPath = (string)r.VirtualPath,
        SizeBytes = (long)r.SizeBytes,
        CreatedUtc = DateTime.Parse((string)r.CreatedUtc, null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        ModifiedUtc = DateTime.Parse((string)r.ModifiedUtc, null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        AddedUtc = DateTime.Parse((string)r.AddedUtc, null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        BlobId = r.BlobID is DBNull || r.BlobID is null ? null : (string)r.BlobID,
    };

    private async Task<int> ReadGracePeriodDaysAsync()
    {
        var raw = await _connection.QuerySingleOrDefaultAsync<string>(
            "SELECT Value FROM Settings WHERE Key = 'GracePeriodDays'").ConfigureAwait(false);
        return int.TryParse(raw, out var d) ? d : 30;
    }

    // ── Insert ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts one <c>Files</c> row using all fields from <paramref name="file"/> (including
    /// <see cref="VolumeFile.BlobId"/>). Returns <see cref="ErrorCode.PathConflict"/> on a
    /// unique-index violation (duplicate name under the same parent).
    /// </summary>
    public async Task<Result> InsertAsync(VolumeFile file, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string sql =
                """
                INSERT INTO Files
                    (FileID, ParentID, IsFolder, IsSymlink, SymlinkTarget, Name, Extension,
                     MimeType, VirtualPath, SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc, BlobID)
                VALUES
                    (@FileId, @ParentId, @IsFolder, @IsSymlink, @SymlinkTarget, @Name, @Extension,
                     @MimeType, @VirtualPath, @SizeBytes, @CreatedUtc, @ModifiedUtc, @AddedUtc, @BlobId)
                """;
            await _connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                file.FileId,
                file.ParentId,
                IsFolder = file.IsFolder ? 1 : 0,
                IsSymlink = file.IsSymlink ? 1 : 0,
                file.SymlinkTarget,
                file.Name,
                file.Extension,
                file.MimeType,
                file.VirtualPath,
                file.SizeBytes,
                CreatedUtc = file.CreatedUtc.ToString("O"),
                ModifiedUtc = file.ModifiedUtc.ToString("O"),
                AddedUtc = file.AddedUtc.ToString("O"),
                file.BlobId,
            }, cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("InsertAsync cancelled for file {FileId}", file.FileId);
            return Result.Fail(ErrorCode.Cancelled, "File insert was cancelled.", ex);
        }
        catch (SqliteException ex) when (ex.IsUniqueConstraintViolation())
        {
            _logger.LogInformation("Path conflict inserting file {Name} under parent {ParentId}",
                file.Name, file.ParentId);
            return Result.Fail(ErrorCode.PathConflict,
                $"A file or folder named '{file.Name}' already exists at this location.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error inserting file {FileId}", file.FileId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to insert file.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error inserting file {FileId}", file.FileId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error inserting file.", ex);
        }
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="VolumeFile"/> with the given ID, or a successful result with
    /// <see langword="null"/> value when no matching row exists.
    /// </summary>
    public async Task<Result<VolumeFile?>> GetByIdAsync(string fileId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string sql =
                """
                SELECT FileID, ParentID, IsFolder, IsSymlink, SymlinkTarget, Name, Extension,
                       MimeType, VirtualPath, SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc, BlobID
                FROM Files
                WHERE FileID = @FileId
                """;
            var rows = await _connection.QueryAsync<dynamic>(
                new CommandDefinition(sql, new { FileId = fileId }, cancellationToken: ct))
                .ConfigureAwait(false);
            var row = rows.FirstOrDefault();
            return Result<VolumeFile?>.Ok(row is null ? null : MapFile(row));
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("GetByIdAsync cancelled for {FileId}", fileId);
            return Result<VolumeFile?>.Fail(ErrorCode.Cancelled, "File fetch was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error fetching file {FileId}", fileId);
            return Result<VolumeFile?>.Fail(ErrorCode.DatabaseReadFailed, "Failed to fetch file.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching file {FileId}", fileId);
            return Result<VolumeFile?>.Fail(ErrorCode.Unknown, "Unexpected error fetching file.", ex);
        }
    }

    /// <summary>
    /// Returns the immediate children of <paramref name="parentId"/> (or root-level items when
    /// <see langword="null"/>), ordered folders-first then alphabetically within each group (§16.4).
    /// </summary>
    public async Task<Result<IReadOnlyList<VolumeFile>>> ListChildrenAsync(
        string? parentId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string sql =
                """
                SELECT FileID, ParentID, IsFolder, IsSymlink, SymlinkTarget, Name, Extension,
                       MimeType, VirtualPath, SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc, BlobID
                FROM Files
                WHERE (@ParentId IS NULL AND ParentID IS NULL)
                   OR (ParentID = @ParentId)
                ORDER BY IsFolder DESC, Name ASC
                """;
            var rows = await _connection.QueryAsync<dynamic>(
                new CommandDefinition(sql, new { ParentId = parentId }, cancellationToken: ct))
                .ConfigureAwait(false);
            return Result<IReadOnlyList<VolumeFile>>.Ok(rows.Select(MapFile).ToList());
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("ListChildrenAsync cancelled");
            return Result<IReadOnlyList<VolumeFile>>.Fail(ErrorCode.Cancelled, "List children was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error listing children of {ParentId}", parentId);
            return Result<IReadOnlyList<VolumeFile>>.Fail(ErrorCode.DatabaseReadFailed, "Failed to list children.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing children of {ParentId}", parentId);
            return Result<IReadOnlyList<VolumeFile>>.Fail(ErrorCode.Unknown, "Unexpected error listing children.", ex);
        }
    }

    /// <summary>
    /// Returns all files whose <c>VirtualPath</c> begins with <paramref name="virtualPathPrefix"/>.
    /// Uses a prefix LIKE search, not a recursive CTE.
    /// </summary>
    public async Task<Result<IReadOnlyList<VolumeFile>>> ListFilesAsync(
        string virtualPathPrefix, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            // Escape LIKE wildcards in the caller-supplied prefix so literal '%', '_',
            // and '\' in folder names do not expand into wildcard patterns (issue #4).
            var escapedPrefix = virtualPathPrefix
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_");
            const string sql =
                """
                SELECT FileID, ParentID, IsFolder, IsSymlink, SymlinkTarget, Name, Extension,
                       MimeType, VirtualPath, SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc, BlobID
                FROM Files
                WHERE VirtualPath LIKE @Prefix || '%' ESCAPE '\'
                ORDER BY VirtualPath ASC
                """;
            var rows = await _connection.QueryAsync<dynamic>(
                new CommandDefinition(sql, new { Prefix = escapedPrefix }, cancellationToken: ct))
                .ConfigureAwait(false);
            return Result<IReadOnlyList<VolumeFile>>.Ok(rows.Select(MapFile).ToList());
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("ListFilesAsync cancelled");
            return Result<IReadOnlyList<VolumeFile>>.Fail(ErrorCode.Cancelled, "List files was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error listing files under {Prefix}", virtualPathPrefix);
            return Result<IReadOnlyList<VolumeFile>>.Fail(ErrorCode.DatabaseReadFailed, "Failed to list files.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing files under {Prefix}", virtualPathPrefix);
            return Result<IReadOnlyList<VolumeFile>>.Fail(ErrorCode.Unknown, "Unexpected error listing files.", ex);
        }
    }

    /// <summary>
    /// Walks each segment of <paramref name="virtualPath"/>, creating any missing folder rows.
    /// Returns the <c>FileId</c> of the deepest (leaf) folder, or <see langword="null"/> for
    /// an empty/root path. Returns <see cref="ErrorCode.PathConflict"/> if any segment resolves
    /// to an existing file rather than a folder. Idempotent — calling twice with the same path
    /// returns the same leaf ID without creating duplicate rows.
    /// </summary>
    public async Task<Result<string?>> EnsureFolderPathAsync(string virtualPath, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var segments = virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return Result<string?>.Ok(null);
            }

            string? currentParentId = null;
            string currentPath = string.Empty;

            foreach (var segment in segments)
            {
                ct.ThrowIfCancellationRequested();
                currentPath = currentPath.Length == 0 ? segment : currentPath + "/" + segment;

                // Find existing row with this name under current parent.
                const string findSql =
                    """
                    SELECT FileID, IsFolder FROM Files
                    WHERE Name = @Name
                      AND ((@ParentId IS NULL AND ParentID IS NULL) OR ParentID = @ParentId)
                    """;
                var existing = await _connection.QuerySingleOrDefaultAsync<(string FileID, long IsFolder)>(
                    new CommandDefinition(findSql, new { Name = segment, ParentId = currentParentId },
                        cancellationToken: ct)).ConfigureAwait(false);

                if (existing.FileID is not null)
                {
                    if (existing.IsFolder == 0)
                    {
                        _logger.LogInformation(
                            "Path conflict: segment '{Segment}' exists as a file, not a folder", segment);
                        return Result<string?>.Fail(ErrorCode.PathConflict,
                            $"'{segment}' exists as a file at this location and cannot be used as a folder.");
                    }
                    currentParentId = existing.FileID;
                }
                else
                {
                    // Create the missing folder.
                    var now = DateTime.UtcNow;
                    var newId = Guid.NewGuid().ToString();
                    var folder = new VolumeFile
                    {
                        FileId = newId,
                        ParentId = currentParentId,
                        IsFolder = true,
                        IsSymlink = false,
                        Name = segment,
                        VirtualPath = currentPath,
                        SizeBytes = 0,
                        CreatedUtc = now,
                        ModifiedUtc = now,
                        AddedUtc = now,
                    };
                    var insertResult = await InsertAsync(folder, ct).ConfigureAwait(false);
                    if (!insertResult.Success)
                    {
                        return Result<string?>.Fail(insertResult.Error!);
                    }
                    currentParentId = newId;
                }
            }

            return Result<string?>.Ok(currentParentId);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("EnsureFolderPathAsync cancelled");
            return Result<string?>.Fail(ErrorCode.Cancelled, "EnsureFolderPath was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error ensuring folder path {Path}", virtualPath);
            return Result<string?>.Fail(ErrorCode.DatabaseWriteFailed, "Failed to ensure folder path.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error ensuring folder path {Path}", virtualPath);
            return Result<string?>.Fail(ErrorCode.Unknown, "Unexpected error ensuring folder path.", ex);
        }
    }

    /// <summary>Returns the number of immediate children of the given folder.</summary>
    public async Task<Result<int>> CountChildrenAsync(string folderId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var count = await _connection.QuerySingleAsync<int>(
                new CommandDefinition(
                    "SELECT COUNT(*) FROM Files WHERE ParentID = @FolderId",
                    new { FolderId = folderId }, cancellationToken: ct)).ConfigureAwait(false);
            return Result<int>.Ok(count);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("CountChildrenAsync cancelled for {FolderId}", folderId);
            return Result<int>.Fail(ErrorCode.Cancelled, "Count children was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error counting children of {FolderId}", folderId);
            return Result<int>.Fail(ErrorCode.DatabaseReadFailed, "Failed to count children.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error counting children of {FolderId}", folderId);
            return Result<int>.Fail(ErrorCode.Unknown, "Unexpected error counting children.", ex);
        }
    }

    /// <summary>Returns all descendants of <paramref name="folderId"/> via recursive CTE (§16.4).</summary>
    public async Task<Result<IReadOnlyList<VolumeFile>>> GetDescendantsAsync(
        string folderId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string sql =
                """
                WITH RECURSIVE descendants AS (
                    SELECT FileID, ParentID, IsFolder, IsSymlink, SymlinkTarget, Name, Extension,
                           MimeType, VirtualPath, SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc, BlobID
                    FROM Files WHERE FileID = @FolderId
                    UNION ALL
                    SELECT f.FileID, f.ParentID, f.IsFolder, f.IsSymlink, f.SymlinkTarget, f.Name,
                           f.Extension, f.MimeType, f.VirtualPath, f.SizeBytes, f.CreatedUtc,
                           f.ModifiedUtc, f.AddedUtc, f.BlobID
                    FROM Files f
                    INNER JOIN descendants d ON f.ParentID = d.FileID
                )
                SELECT * FROM descendants
                """;
            var rows = await _connection.QueryAsync<dynamic>(
                new CommandDefinition(sql, new { FolderId = folderId }, cancellationToken: ct))
                .ConfigureAwait(false);
            return Result<IReadOnlyList<VolumeFile>>.Ok(rows.Select(MapFile).ToList());
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("GetDescendantsAsync cancelled for {FolderId}", folderId);
            return Result<IReadOnlyList<VolumeFile>>.Fail(ErrorCode.Cancelled, "Get descendants was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Database error getting descendants of {FolderId}", folderId);
            return Result<IReadOnlyList<VolumeFile>>.Fail(ErrorCode.DatabaseReadFailed, "Failed to get descendants.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting descendants of {FolderId}", folderId);
            return Result<IReadOnlyList<VolumeFile>>.Fail(ErrorCode.Unknown, "Unexpected error getting descendants.", ex);
        }
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes the <c>Files</c> row, soft-deletes the referenced blob (if any), and writes a
    /// <c>DeleteLog</c> entry with <c>Trigger = USER_ACTION</c>, all in one transaction with a
    /// WAL row (operation <c>DELETE</c>). Compensation paths use <see cref="CancellationToken.None"/>
    /// literals (Principle 17).
    /// </summary>
    public async Task<Result> DeleteFileAsync(string fileId, CancellationToken ct)
    {
        VolumeFile? file = null;
        WalRow? walRow = null;
        SqliteTransaction? tx = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var fileResult = await GetByIdAsync(fileId, ct).ConfigureAwait(false);
            if (!fileResult.Success)
            {
                return Result.Fail(fileResult.Error!);
            }
            file = fileResult.Value;
            if (file is null)
            {
                return Result.Fail(ErrorCode.FileNotFound, $"File '{fileId}' not found.");
            }

            var graceDays = await ReadGracePeriodDaysAsync().ConfigureAwait(false);
            var now = DateTime.UtcNow;

            tx = _connection.BeginTransaction();

            walRow = new WalRow(
                WalId: Guid.NewGuid().ToString(),
                Operation: "DELETE",
                Phase: "PREPARE",
                StartedUtc: now,
                UpdatedUtc: now,
                Payload: $"{{\"fileId\":\"{fileId}\"}}");
            var walResult = await _wal.InsertAsync(walRow, tx, CancellationToken.None).ConfigureAwait(false);
            if (!walResult.Success)
            {
                tx.Rollback();
                return walResult;
            }

            // Soft-delete the blob if referenced.
            if (file.BlobId is not null)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE Blobs SET SoftDeletedUtc = @Now, PurgeAfterUtc = @PurgeAfter WHERE BlobID = @BlobId",
                    new { Now = now.ToString("O"), PurgeAfter = now.AddDays(graceDays).ToString("O"), BlobId = file.BlobId },
                    tx)).ConfigureAwait(false);
            }

            await _connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM Files WHERE FileID = @FileId",
                new { FileId = fileId }, tx)).ConfigureAwait(false);

            await _connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO DeleteLog (LogID, DeletedAt, FileID, Name, VirtualPath, IsFolder, Trigger)
                VALUES (@LogId, @DeletedAt, @FileId, @Name, @VirtualPath, @IsFolder, 'USER_ACTION')
                """,
                new
                {
                    LogId = Guid.NewGuid().ToString(),
                    DeletedAt = now.ToString("O"),
                    FileId = fileId,
                    file.Name,
                    file.VirtualPath,
                    IsFolder = file.IsFolder ? 1 : 0,
                }, tx)).ConfigureAwait(false);

            await _wal.TransitionAsync(walRow.WalId, "COMMITTED", CancellationToken.None).ConfigureAwait(false);
            tx.Commit();
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            tx?.Rollback();
            if (walRow is not null)
            {
                await _wal.TransitionAsync(walRow.WalId, "FAILED", CancellationToken.None).ConfigureAwait(false);
            }
            _logger.LogInformation("DeleteFileAsync cancelled for {FileId}", fileId);
            return Result.Fail(ErrorCode.Cancelled, "Delete file was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            tx?.Rollback();
            if (walRow is not null)
            {
                await _wal.TransitionAsync(walRow.WalId, "FAILED", CancellationToken.None).ConfigureAwait(false);
            }
            _logger.LogError(ex, "Database error deleting file {FileId}", fileId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to delete file.", ex);
        }
        catch (Exception ex)
        {
            tx?.Rollback();
            if (walRow is not null)
            {
                await _wal.TransitionAsync(walRow.WalId, "FAILED", CancellationToken.None).ConfigureAwait(false);
            }
            _logger.LogError(ex, "Unexpected error deleting file {FileId}", fileId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error deleting file.", ex);
        }
        finally
        {
            tx?.Dispose();
        }
    }

    /// <summary>
    /// Cascade-deletes a folder and all its descendants. Returns
    /// <see cref="ErrorCode.ConfirmationRequired"/> (with <c>Metadata["ChildCount"]</c>) when
    /// <paramref name="confirmed"/> is <see langword="false"/> and the folder is non-empty.
    /// All <c>DeleteLog</c> entries use <c>Trigger = CASCADE</c>. Compensation paths use
    /// <see cref="CancellationToken.None"/> literals (Principle 17).
    /// </summary>
    public async Task<Result> DeleteFolderCascadeAsync(
        string folderId, bool confirmed, CancellationToken ct)
    {
        WalRow? walRow = null;
        SqliteTransaction? tx = null;
        try
        {
            ct.ThrowIfCancellationRequested();

            if (!confirmed)
            {
                var countResult = await CountChildrenAsync(folderId, ct).ConfigureAwait(false);
                if (!countResult.Success)
                {
                    return Result.Fail(countResult.Error!);
                }
                if (countResult.Value > 0)
                {
                    return Result.Fail(new ErrorContext
                    {
                        Code = ErrorCode.ConfirmationRequired,
                        Message = $"Folder contains {countResult.Value} item(s). Pass confirmed=true to delete.",
                        Metadata = new Dictionary<string, string>
                        { ["ChildCount"] = countResult.Value.ToString() },
                    });
                }
            }

            var descendantsResult = await GetDescendantsAsync(folderId, ct).ConfigureAwait(false);
            if (!descendantsResult.Success)
            {
                return Result.Fail(descendantsResult.Error!);
            }
            var descendants = descendantsResult.Value!;

            var graceDays = await ReadGracePeriodDaysAsync().ConfigureAwait(false);
            var now = DateTime.UtcNow;

            tx = _connection.BeginTransaction();

            walRow = new WalRow(
                WalId: Guid.NewGuid().ToString(),
                Operation: "CASCADE_DELETE",
                Phase: "PREPARE",
                StartedUtc: now,
                UpdatedUtc: now,
                Payload: $"{{\"folderId\":\"{folderId}\"}}");
            var walResult = await _wal.InsertAsync(walRow, tx, CancellationToken.None).ConfigureAwait(false);
            if (!walResult.Success)
            {
                tx.Rollback();
                return walResult;
            }

            // Soft-delete all referenced blobs.
            foreach (var d in descendants.Where(d => d.BlobId is not null))
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE Blobs SET SoftDeletedUtc = @Now, PurgeAfterUtc = @PurgeAfter WHERE BlobID = @BlobId",
                    new { Now = now.ToString("O"), PurgeAfter = now.AddDays(graceDays).ToString("O"), BlobId = d.BlobId },
                    tx)).ConfigureAwait(false);
            }

            // Delete all Files rows in the subtree.
            if (descendants.Count > 0)
            {
                var idArray = descendants.Select(d => d.FileId).ToArray();
                await _connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM Files WHERE FileID IN @Ids",
                    new { Ids = idArray }, tx)).ConfigureAwait(false);

                // Write DeleteLog entries.
                foreach (var d in descendants)
                {
                    await _connection.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO DeleteLog (LogID, DeletedAt, FileID, Name, VirtualPath, IsFolder, Trigger)
                        VALUES (@LogId, @DeletedAt, @FileId, @Name, @VirtualPath, @IsFolder, 'CASCADE')
                        """,
                        new
                        {
                            LogId = Guid.NewGuid().ToString(),
                            DeletedAt = now.ToString("O"),
                            FileId = d.FileId,
                            d.Name,
                            d.VirtualPath,
                            IsFolder = d.IsFolder ? 1 : 0,
                        }, tx)).ConfigureAwait(false);
                }
            }

            await _wal.TransitionAsync(walRow.WalId, "COMMITTED", CancellationToken.None).ConfigureAwait(false);
            tx.Commit();
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            tx?.Rollback();
            if (walRow is not null)
            {
                await _wal.TransitionAsync(walRow.WalId, "FAILED", CancellationToken.None).ConfigureAwait(false);
            }
            _logger.LogInformation("DeleteFolderCascadeAsync cancelled for {FolderId}", folderId);
            return Result.Fail(ErrorCode.Cancelled, "Cascade delete was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            tx?.Rollback();
            if (walRow is not null)
            {
                await _wal.TransitionAsync(walRow.WalId, "FAILED", CancellationToken.None).ConfigureAwait(false);
            }
            _logger.LogError(ex, "Database error in cascade delete of {FolderId}", folderId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Cascade delete failed.", ex);
        }
        catch (Exception ex)
        {
            tx?.Rollback();
            if (walRow is not null)
            {
                await _wal.TransitionAsync(walRow.WalId, "FAILED", CancellationToken.None).ConfigureAwait(false);
            }
            _logger.LogError(ex, "Unexpected error in cascade delete of {FolderId}", folderId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error in cascade delete.", ex);
        }
        finally
        {
            tx?.Dispose();
        }
    }

    // ── Rename / Move ─────────────────────────────────────────────────────────

    /// <summary>
    /// Renames a folder in place, cascading <c>VirtualPath</c> updates to all descendants via
    /// recursive CTE (§16.4). Returns <see cref="ErrorCode.PathConflict"/> when the new name is
    /// already taken under the same parent.
    /// </summary>
    public async Task<Result> RenameFolderAsync(string folderId, string newName, CancellationToken ct)
    {
        SqliteTransaction? tx = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var folderResult = await GetByIdAsync(folderId, ct).ConfigureAwait(false);
            if (!folderResult.Success)
            {
                return Result.Fail(folderResult.Error!);
            }
            var folder = folderResult.Value;
            if (folder is null)
            {
                return Result.Fail(ErrorCode.FileNotFound, $"Folder '{folderId}' not found.");
            }

            var now = DateTime.UtcNow;
            var oldPrefix = folder.VirtualPath;
            var parentPrefix = folder.ParentId is null
                ? string.Empty
                : (folder.VirtualPath.LastIndexOf('/') is int sep and > 0
                    ? folder.VirtualPath[..sep]
                    : string.Empty);
            var newVirtualPath = parentPrefix.Length > 0 ? parentPrefix + "/" + newName : newName;

            tx = _connection.BeginTransaction();

            // Update the folder row itself.
            await _connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Files SET Name = @Name, VirtualPath = @VirtualPath, ModifiedUtc = @ModifiedUtc WHERE FileID = @FileId",
                new { Name = newName, VirtualPath = newVirtualPath, ModifiedUtc = now.ToString("O"), FileId = folderId },
                tx)).ConfigureAwait(false);

            // Cascade VirtualPath updates to all descendants (§16.4).
            await _connection.ExecuteAsync(new CommandDefinition(
                """
                WITH RECURSIVE descendants AS (
                    SELECT FileID FROM Files WHERE ParentID = @FolderId
                    UNION ALL
                    SELECT f.FileID FROM Files f
                    INNER JOIN descendants d ON f.ParentID = d.FileID
                )
                UPDATE Files SET
                    VirtualPath = @NewPrefix || SUBSTR(VirtualPath, @OldPrefixLength + 1),
                    ModifiedUtc = @ModifiedUtc
                WHERE FileID IN (SELECT FileID FROM descendants)
                """,
                new
                {
                    FolderId = folderId,
                    NewPrefix = newVirtualPath,
                    OldPrefixLength = oldPrefix.Length,
                    ModifiedUtc = now.ToString("O"),
                }, tx)).ConfigureAwait(false);

            tx.Commit();
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            tx?.Rollback();
            _logger.LogInformation("RenameFolderAsync cancelled for {FolderId}", folderId);
            return Result.Fail(ErrorCode.Cancelled, "Rename folder was cancelled.", ex);
        }
        catch (SqliteException ex) when (ex.IsUniqueConstraintViolation())
        {
            tx?.Rollback();
            _logger.LogInformation("Path conflict renaming folder {FolderId} to '{NewName}'", folderId, newName);
            return Result.Fail(ErrorCode.PathConflict,
                $"A folder named '{newName}' already exists at this location.", ex);
        }
        catch (SqliteException ex)
        {
            tx?.Rollback();
            _logger.LogError(ex, "Database error renaming folder {FolderId}", folderId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to rename folder.", ex);
        }
        catch (Exception ex)
        {
            tx?.Rollback();
            _logger.LogError(ex, "Unexpected error renaming folder {FolderId}", folderId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error renaming folder.", ex);
        }
        finally
        {
            tx?.Dispose();
        }
    }

    /// <summary>
    /// Moves a file or folder to a new parent (or root when <paramref name="newParentId"/> is
    /// <see langword="null"/>). For folders, cascades <c>VirtualPath</c> updates to descendants.
    /// Runs cycle-detection CTE before any mutation (§16.4). Does not rename the item; to
    /// move-and-rename, call <see cref="MoveAsync"/> then <see cref="RenameFolderAsync"/> as
    /// two separate transactions.
    /// </summary>
    public async Task<Result> MoveAsync(string fileId, string? newParentId, CancellationToken ct)
    {
        SqliteTransaction? tx = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var fileResult = await GetByIdAsync(fileId, ct).ConfigureAwait(false);
            if (!fileResult.Success)
            {
                return Result.Fail(fileResult.Error!);
            }
            var file = fileResult.Value;
            if (file is null)
            {
                return Result.Fail(ErrorCode.FileNotFound, $"Item '{fileId}' not found.");
            }

            // Cycle detection — only relevant when moving a folder.
            if (file.IsFolder && newParentId is not null)
            {
                const string cycleSql =
                    """
                    WITH RECURSIVE ancestors AS (
                        SELECT FileID, ParentID FROM Files WHERE FileID = @NewParentId
                        UNION ALL
                        SELECT f.FileID, f.ParentID FROM Files f
                        INNER JOIN ancestors a ON f.FileID = a.ParentID
                    )
                    SELECT FileID FROM ancestors WHERE FileID = @FileId
                    """;
                var cycleRow = await _connection.QuerySingleOrDefaultAsync<string>(
                    new CommandDefinition(cycleSql, new { NewParentId = newParentId, FileId = fileId },
                        cancellationToken: ct)).ConfigureAwait(false);
                if (cycleRow is not null)
                {
                    return Result.Fail(ErrorCode.CyclicMoveDetected,
                        "The move would create a cyclic parent–child relationship.");
                }
            }

            // Resolve new VirtualPath.
            string newVirtualPath;
            if (newParentId is null)
            {
                newVirtualPath = file.Name;
            }
            else
            {
                var parentResult = await GetByIdAsync(newParentId, ct).ConfigureAwait(false);
                if (!parentResult.Success)
                {
                    return Result.Fail(parentResult.Error!);
                }
                var parent = parentResult.Value;
                if (parent is null)
                {
                    return Result.Fail(ErrorCode.FileNotFound, $"Target parent '{newParentId}' not found.");
                }
                newVirtualPath = parent.VirtualPath + "/" + file.Name;
            }

            var oldPrefix = file.VirtualPath;
            var now = DateTime.UtcNow;

            tx = _connection.BeginTransaction();

            await _connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Files SET ParentID = @NewParentId, VirtualPath = @VirtualPath, ModifiedUtc = @Now WHERE FileID = @FileId",
                new { NewParentId = newParentId, VirtualPath = newVirtualPath, Now = now.ToString("O"), FileId = fileId },
                tx)).ConfigureAwait(false);

            // Cascade VirtualPath for folder descendants.
            if (file.IsFolder)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    WITH RECURSIVE descendants AS (
                        SELECT FileID FROM Files WHERE ParentID = @FolderId
                        UNION ALL
                        SELECT f.FileID FROM Files f
                        INNER JOIN descendants d ON f.ParentID = d.FileID
                    )
                    UPDATE Files SET
                        VirtualPath = @NewPrefix || SUBSTR(VirtualPath, @OldPrefixLength + 1),
                        ModifiedUtc = @Now
                    WHERE FileID IN (SELECT FileID FROM descendants)
                    """,
                    new
                    {
                        FolderId = fileId,
                        NewPrefix = newVirtualPath,
                        OldPrefixLength = oldPrefix.Length,
                        Now = now.ToString("O"),
                    }, tx)).ConfigureAwait(false);
            }

            tx.Commit();
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            tx?.Rollback();
            _logger.LogInformation("MoveAsync cancelled for {FileId}", fileId);
            return Result.Fail(ErrorCode.Cancelled, "Move was cancelled.", ex);
        }
        catch (SqliteException ex) when (ex.IsUniqueConstraintViolation())
        {
            tx?.Rollback();
            _logger.LogInformation("Path conflict moving {FileId} to parent {NewParentId}", fileId, newParentId);
            return Result.Fail(ErrorCode.PathConflict, "An item with the same name already exists at the target location.", ex);
        }
        catch (SqliteException ex)
        {
            tx?.Rollback();
            _logger.LogError(ex, "Database error moving {FileId}", fileId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to move item.", ex);
        }
        catch (Exception ex)
        {
            tx?.Rollback();
            _logger.LogError(ex, "Unexpected error moving {FileId}", fileId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error moving item.", ex);
        }
        finally
        {
            tx?.Dispose();
        }
    }

    // ── Restore ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-activates a soft-deleted blob and re-inserts a <c>Files</c> row pointing at it.
    /// Returns <see cref="ErrorCode.BlobNotFound"/> when the blob no longer exists (sweeper
    /// already hard-deleted it). Both operations run in one transaction.
    /// </summary>
    public async Task<Result> RestoreFromGracePeriodAsync(
        string blobId, string virtualPath, CancellationToken ct)
    {
        SqliteTransaction? tx = null;
        try
        {
            ct.ThrowIfCancellationRequested();

            // Verify the blob still exists.
            var blobRow = await _connection.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    "SELECT BlobID FROM Blobs WHERE BlobID = @BlobId",
                    new { BlobId = blobId }, cancellationToken: ct)).ConfigureAwait(false);
            if (blobRow is null)
            {
                return Result.Fail(ErrorCode.BlobNotFound,
                    $"Blob '{blobId}' no longer exists; it may have been permanently deleted.");
            }

            var now = DateTime.UtcNow;
            var name = virtualPath.Split('/').Last();

            tx = _connection.BeginTransaction();

            await _connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Blobs SET SoftDeletedUtc = NULL, PurgeAfterUtc = NULL WHERE BlobID = @BlobId",
                new { BlobId = blobId }, tx)).ConfigureAwait(false);

            var file = new VolumeFile
            {
                FileId = Guid.NewGuid().ToString(),
                ParentId = null,
                IsFolder = false,
                IsSymlink = false,
                Name = name,
                VirtualPath = virtualPath,
                SizeBytes = 0,
                CreatedUtc = now,
                ModifiedUtc = now,
                AddedUtc = now,
                BlobId = blobId,
            };

            await _connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO Files
                    (FileID, ParentID, IsFolder, IsSymlink, SymlinkTarget, Name, Extension,
                     MimeType, VirtualPath, SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc, BlobID)
                VALUES
                    (@FileId, @ParentId, @IsFolder, @IsSymlink, @SymlinkTarget, @Name, @Extension,
                     @MimeType, @VirtualPath, @SizeBytes, @CreatedUtc, @ModifiedUtc, @AddedUtc, @BlobId)
                """,
                new
                {
                    file.FileId,
                    file.ParentId,
                    IsFolder = 0,
                    IsSymlink = 0,
                    SymlinkTarget = (string?)null,
                    file.Name,
                    Extension = (string?)null,
                    MimeType = (string?)null,
                    file.VirtualPath,
                    file.SizeBytes,
                    CreatedUtc = file.CreatedUtc.ToString("O"),
                    ModifiedUtc = file.ModifiedUtc.ToString("O"),
                    AddedUtc = file.AddedUtc.ToString("O"),
                    file.BlobId,
                }, tx)).ConfigureAwait(false);

            tx.Commit();
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            tx?.Rollback();
            _logger.LogInformation("RestoreFromGracePeriodAsync cancelled for blob {BlobId}", blobId);
            return Result.Fail(ErrorCode.Cancelled, "Restore was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            tx?.Rollback();
            _logger.LogError(ex, "Database error restoring blob {BlobId}", blobId);
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to restore from grace period.", ex);
        }
        catch (Exception ex)
        {
            tx?.Rollback();
            _logger.LogError(ex, "Unexpected error restoring blob {BlobId}", blobId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error restoring from grace period.", ex);
        }
        finally
        {
            tx?.Dispose();
        }
    }
}

/// <summary>
/// SQLite error codes used in exception filters across repositories.
/// <para>
/// <c>SQLITE_CONSTRAINT</c> (primary code 19) fires for every constraint type — UNIQUE, FK,
/// CHECK, NOT NULL. To distinguish UNIQUE violations from the others without relying on
/// <c>SqliteException.SqliteExtendedErrorCode</c> (which is unavailable in the bundled
/// SQLitePCLRaw version), filters pair the primary code check with a
/// <c>Message.Contains("UNIQUE")</c> guard. SQLite's error message format has been stable
/// since v3.7 and always includes the word "UNIQUE" when an index uniqueness constraint fires.
/// </para>
/// </summary>
internal static class SqliteErrorCodes
{
    /// <summary>SQLITE_CONSTRAINT (primary code 19) — any constraint violation.</summary>
    internal const int ConstraintViolation = 19;

    /// <summary>SQLITE_CONSTRAINT_FOREIGNKEY (extended code 787) — a foreign-key constraint was violated.</summary>
    internal const int ForeignKeyViolation = 787;
}

/// <summary>Helper for narrowing SQLITE_CONSTRAINT exceptions to specific constraint types.</summary>
internal static class SqliteExceptionExtensions
{
    /// <summary>Returns <see langword="true"/> when the exception represents a UNIQUE index violation.</summary>
    internal static bool IsUniqueConstraintViolation(this SqliteException ex) =>
        ex.SqliteErrorCode == SqliteErrorCodes.ConstraintViolation
        && ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
}
