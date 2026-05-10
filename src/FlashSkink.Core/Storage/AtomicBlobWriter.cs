using System.Diagnostics;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Storage;

/// <summary>
/// Implements the blueprint §13.4 file-level atomic write protocol for the skink: stage to
/// <c>.flashskink/staging/{blobId}.tmp</c>, fsync the file, atomically rename to the sharded
/// destination path, fsync the destination directory. Compensation entry-points
/// (<see cref="DeleteStagingAsync"/> and <see cref="DeleteDestinationAsync"/>) are used by
/// <see cref="WriteWalScope"/> on rollback. NOTE: FAT32 volumes do not support atomic rename;
/// this implementation assumes NTFS/ext4/APFS; FAT32 detection is Phase 6 setup work.
/// </summary>
public sealed class AtomicBlobWriter
{
    private readonly ILogger<AtomicBlobWriter> _logger;

    /// <summary>Creates an <see cref="AtomicBlobWriter"/>.</summary>
    public AtomicBlobWriter(ILogger<AtomicBlobWriter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Executes the §13.4 atomic write sequence:
    /// <list type="number">
    ///   <item>Create the two-level shard directory (idempotent).</item>
    ///   <item>Write <paramref name="blobBytes"/> to the staging file and fsync it.</item>
    ///   <item>Atomically rename the staging file to the sharded destination.</item>
    ///   <item>Fsync the destination directory (cross-platform — no-op on Windows).</item>
    /// </list>
    /// Returns <see cref="ErrorCode.UsbFull"/> when the disk is full,
    /// <see cref="ErrorCode.PathConflict"/> when a destination file already exists
    /// (BlobID collision or Phase-5 race), <see cref="ErrorCode.StagingFailed"/> for other
    /// I/O errors, and <see cref="ErrorCode.Cancelled"/> on cancellation.
    /// </summary>
    public async Task<Result<string>> WriteAsync(
        string skinkRoot,
        string blobId,
        ReadOnlyMemory<byte> blobBytes,
        CancellationToken ct)
    {
        var dest = ComputeDestinationPath(skinkRoot, blobId);
        var staging = ComputeStagingPath(skinkRoot, blobId);
        var leafDir = Path.GetDirectoryName(dest)!;   // ! safe: dest is always a file path with a parent dir
        var midDir = Path.GetDirectoryName(leafDir)!; // ! safe: leafDir is always a non-root directory

        try
        {
            // Step 1a — ensure the staging directory exists (idempotent). The staging directory is
            // created by volume setup, but we ensure it here for robustness and test isolation.
            var stagingDir = Path.GetDirectoryName(staging)!; // ! safe: staging is always a file path with a parent dir
            Directory.CreateDirectory(stagingDir);

            // Step 1b — create shard directory tree; fsync parent directories on first creation.
            // When midDir is new, fsync its parent (.flashskink/blobs/) to make the midDir
            // directory-entry durable. When leafDir is new, fsync midDir to make leafDir durable.
            var midExisted = Directory.Exists(midDir);
            var leafExisted = Directory.Exists(leafDir);
            Directory.CreateDirectory(leafDir);
            if (!midExisted)
            {
                FsyncDirectory(Path.GetDirectoryName(midDir)!); // ! safe: midDir is always a non-root directory
            }
            if (!leafExisted)
            {
                FsyncDirectory(midDir);
            }

            ct.ThrowIfCancellationRequested();

            // Step 2 — write staging file and fsync.
            await using (var fs = new FileStream(
                staging,
                new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.None,
                    PreallocationSize = blobBytes.Length,
                }))
            {
                await fs.WriteAsync(blobBytes, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                RandomAccess.FlushToDisk(fs.SafeFileHandle);
            }

            ct.ThrowIfCancellationRequested();

            // Step 3 — atomic rename.
            File.Move(staging, dest, overwrite: false);

            // Step 4 — fsync destination directory (no-op on Windows per §13.4 V1 assumption).
            FsyncDirectory(leafDir);

            // Step 5 — success.
            return Result<string>.Ok(dest);
        }
        catch (OperationCanceledException ex)
        {
            TryDeleteStaging(staging);
            return Result<string>.Fail(ErrorCode.Cancelled, "Atomic blob write cancelled.", ex);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            TryDeleteStaging(staging);
            _logger.LogError(ex, "Skink full while writing blob {BlobId}", blobId);
            return Result<string>.Fail(ErrorCode.UsbFull, "The skink is full; cannot write the file.", ex);
        }
        catch (IOException ex) when (IsFileExists(ex))
        {
            TryDeleteStaging(staging);
            _logger.LogError(ex, "Destination file already exists for blob {BlobId}", blobId);
            return Result<string>.Fail(ErrorCode.PathConflict, "A file already exists at the target path.", ex);
        }
        catch (IOException ex)
        {
            TryDeleteStaging(staging);
            _logger.LogError(ex, "I/O error writing blob {BlobId} at {Dest}", blobId, dest);
            return Result<string>.Fail(ErrorCode.StagingFailed, "I/O error during atomic blob write.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDeleteStaging(staging);
            _logger.LogError(ex, "Access denied writing blob {BlobId} at {Dest}", blobId, dest);
            return Result<string>.Fail(ErrorCode.StagingFailed, "Access denied during atomic blob write.", ex);
        }
        catch (Exception ex)
        {
            TryDeleteStaging(staging);
            _logger.LogError(ex, "Unexpected error writing blob {BlobId}", blobId);
            return Result<string>.Fail(ErrorCode.Unknown, "Unexpected error during atomic blob write.", ex);
        }
    }

    /// <summary>
    /// Best-effort deletes the staging file for <paramref name="blobId"/>. Missing file is
    /// success (idempotent). Called by <see cref="WriteWalScope.DisposeAsync"/> on rollback.
    /// </summary>
    public Task<Result> DeleteStagingAsync(string skinkRoot, string blobId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var path = ComputeStagingPath(skinkRoot, blobId);
            // File.Exists returns false for both "file absent" and "directory absent" cases.
            // Using it avoids a DirectoryNotFoundException from File.Delete when the staging
            // directory was never created (crash step 1 — no writes attempted at all).
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return Task.FromResult(Result.Ok());
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(Result.Fail(ErrorCode.Cancelled, "Staging delete cancelled.", ex));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete staging file for blob {BlobId}", blobId);
            return Task.FromResult(Result.Fail(ErrorCode.StagingFailed, "Could not delete staging file.", ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied deleting staging file for blob {BlobId}", blobId);
            return Task.FromResult(Result.Fail(ErrorCode.StagingFailed, "Access denied deleting staging file.", ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting staging file for blob {BlobId}", blobId);
            return Task.FromResult(Result.Fail(ErrorCode.Unknown, "Unexpected error deleting staging file.", ex));
        }
    }

    /// <summary>
    /// Best-effort deletes the sharded destination file for <paramref name="blobId"/>. Missing
    /// file is success (idempotent). Called by <see cref="WriteWalScope.DisposeAsync"/> when
    /// <see cref="WriteWalScope.MarkRenamed"/> was called but the brain transaction failed.
    /// </summary>
    public Task<Result> DeleteDestinationAsync(string skinkRoot, string blobId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var path = ComputeDestinationPath(skinkRoot, blobId);
            // File.Exists returns false for both "file absent" and "directory absent" cases.
            // Using it avoids a DirectoryNotFoundException from File.Delete when the shard
            // directory was never created (the blob write never completed step 1b).
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return Task.FromResult(Result.Ok());
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(Result.Fail(ErrorCode.Cancelled, "Destination delete cancelled.", ex));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete destination file for blob {BlobId}", blobId);
            return Task.FromResult(Result.Fail(ErrorCode.StagingFailed, "Could not delete destination file.", ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied deleting destination file for blob {BlobId}", blobId);
            return Task.FromResult(Result.Fail(ErrorCode.StagingFailed, "Access denied deleting destination file.", ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting destination file for blob {BlobId}", blobId);
            return Task.FromResult(Result.Fail(ErrorCode.Unknown, "Unexpected error deleting destination file.", ex));
        }
    }

    /// <summary>
    /// Returns the two-level sharded blob path:
    /// <c>[skinkRoot]/.flashskink/blobs/{blobId[0..2]}/{blobId[2..4]}/{blobId}.bin</c>.
    /// Pure function; never throws.
    /// </summary>
    public static string ComputeDestinationPath(string skinkRoot, string blobId)
    {
        Debug.Assert(blobId.Length >= 4, "blobId must be at least 4 characters (generated as Guid.NewGuid().ToString(\"N\") = 32 chars).");
        return Path.Combine(skinkRoot, ".flashskink", "blobs", blobId[..2], blobId[2..4], blobId + ".bin");
    }

    /// <summary>
    /// Returns the staging path:
    /// <c>[skinkRoot]/.flashskink/staging/{blobId}.tmp</c>.
    /// Pure function; never throws.
    /// </summary>
    public static string ComputeStagingPath(string skinkRoot, string blobId)
    {
        Debug.Assert(blobId.Length >= 4, "blobId must be at least 4 characters (generated as Guid.NewGuid().ToString(\"N\") = 32 chars).");
        return Path.Combine(skinkRoot, ".flashskink", "staging", blobId + ".tmp");
    }

    /// <summary>
    /// Delegates to <see cref="AtomicWriteHelper.FsyncDirectory"/> — shared with
    /// <see cref="FlashSkink.Core.Providers.FileSystemProvider"/> (Principle 29).
    /// </summary>
    private static void FsyncDirectory(string directoryPath) =>
        AtomicWriteHelper.FsyncDirectory(directoryPath);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="ex"/> represents a disk-full condition.
    /// Checks <c>ERROR_DISK_FULL</c> (<c>0x80070070</c>), which the .NET runtime uses on all
    /// platforms — ENOSPC is normalised to this HRESULT on Linux/macOS, so the Windows check
    /// covers both. The raw-errno branch (<c>HResult &amp; 0xFFFF == 28</c>) is a defensive
    /// fallback for non-standard <see cref="IOException"/> sources (e.g. native interop or custom
    /// streams) that set a raw Unix errno rather than a normalised HRESULT.
    /// </summary>
    private static bool IsDiskFull(IOException ex)
    {
        // 0x80070070 = ERROR_DISK_FULL. .NET normalises ENOSPC to this on Linux/macOS, so this
        // single check covers all platforms for standard file I/O.
        if ((uint)ex.HResult == 0x80070070)
        {
            return true;
        }
        // Defensive fallback: raw ENOSPC errno (28) in the low 16 bits, for non-standard sources.
        if ((ex.HResult & 0xFFFF) == 28)
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="ex"/> represents a file-already-exists
    /// condition. Checks <c>ERROR_FILE_EXISTS</c> (<c>0x80070050</c>) and
    /// <c>ERROR_ALREADY_EXISTS</c> (<c>0x800700B7</c>), which the .NET runtime uses on all
    /// platforms — EEXIST is normalised to <c>ERROR_FILE_EXISTS</c> on Linux/macOS. The
    /// raw-errno branch (<c>HResult &amp; 0xFFFF == 17</c>) is a defensive fallback for
    /// non-standard <see cref="IOException"/> sources that set a raw Unix errno.
    /// </summary>
    private static bool IsFileExists(IOException ex)
    {
        // 0x80070050 = ERROR_FILE_EXISTS, 0x800700B7 = ERROR_ALREADY_EXISTS. .NET normalises
        // EEXIST to ERROR_FILE_EXISTS on Linux/macOS, so these checks cover all platforms.
        var hr = (uint)ex.HResult;
        if (hr == 0x80070050 || hr == 0x800700B7)
        {
            return true;
        }
        // Defensive fallback: raw EEXIST errno (17) in the low 16 bits, for non-standard sources.
        if ((ex.HResult & 0xFFFF) == 17)
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Best-effort removes a staging file without throwing. Used on failure paths where no
    /// <see cref="WriteWalScope"/> exists yet to own the cleanup.
    /// </summary>
    private static void TryDeleteStaging(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
