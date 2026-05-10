using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Storage;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers;

/// <summary>
/// Real production <see cref="IStorageProvider"/> whose "remote" is a configured local or NAS
/// filesystem path. The use case is a NAS mount, an external drive, or a local folder where the
/// user wants a redundant copy without a cloud account. Also serves as the deterministic,
/// network-free test double for cloud providers that arrive in Phase 4.
/// Blueprint §15.2 (FileSystem row), §15.7 (verification), §27.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Session model:</strong> No provider-side session protocol. The adapter implements one
/// over atomic file operations: a small JSON sidecar (<c>.flashskink-staging/{remote}.session</c>)
/// is written at session open; a partial file (<c>.flashskink-staging/{remote}.partial</c>)
/// accumulates range writes; finalisation atomically renames the partial file to its sharded
/// destination and deletes the sidecar. Sessions never expire — <see cref="UploadSession.ExpiresAt"/>
/// is always <see cref="DateTimeOffset.MaxValue"/>. If the sidecar is deleted by external action,
/// <see cref="GetUploadedBytesAsync"/> returns 0 and the upload restarts from byte 0 on the next
/// resume — identical behaviour to a cloud provider returning an expired session.
/// </para>
/// <para>
/// <strong>Destination layout:</strong>
/// <c>{rootPath}/blobs/{remoteName[0..2]}/{remoteName[2..4]}/{remoteName}</c> — mirrors the
/// skink's <c>.flashskink/blobs/{xx}/{yy}/</c> sharding for cognitive consistency.
/// <c>_brain/</c> and <c>_health/</c> subdirectories co-exist at the root level.
/// </para>
/// <para>
/// <strong>Hash-check capability (<c>ISupportsRemoteHashCheck</c>)</strong> is wired in §3.3
/// when that interface is introduced.
/// </para>
/// </remarks>
public sealed class FileSystemProvider : IStorageProvider
{
    private readonly string _rootPath;
    private readonly ILogger<FileSystemProvider> _logger;

    /// <inheritdoc/>
    public string ProviderID { get; }

    /// <inheritdoc/>
    public string ProviderType => "filesystem";

    /// <inheritdoc/>
    public string DisplayName { get; }

    // ── Construction ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="FileSystemProvider"/>. Does not validate <paramref name="rootPath"/>;
    /// prefer <see cref="Create"/> for validated construction.
    /// </summary>
    public FileSystemProvider(
        string providerId,
        string displayName,
        string rootPath,
        ILogger<FileSystemProvider> logger)
    {
        ProviderID = providerId;
        DisplayName = displayName;
        _rootPath = rootPath;
        _logger = logger;
    }

    /// <summary>
    /// Creates and validates a <see cref="FileSystemProvider"/>. Writes a 1-byte probe to
    /// <c>{rootPath}/_health/</c> to confirm the path exists and is writable.
    /// Returns <see cref="ErrorCode.ProviderUnreachable"/> when validation fails.
    /// </summary>
    public static Result<FileSystemProvider> Create(
        string providerId,
        string displayName,
        string rootPath,
        ILogger<FileSystemProvider> logger)
    {
        try
        {
            if (!Directory.Exists(rootPath))
            {
                return Result<FileSystemProvider>.Fail(
                    ErrorCode.ProviderUnreachable,
                    $"Configured root path '{rootPath}' does not exist.");
            }

            var healthDir = Path.Combine(rootPath, "_health");
            Directory.CreateDirectory(healthDir);
            var probe = Path.Combine(healthDir, $"{Guid.NewGuid():N}.probe");
            File.WriteAllBytes(probe, [0x01]);
            File.Delete(probe);
            return Result<FileSystemProvider>.Ok(
                new FileSystemProvider(providerId, displayName, rootPath, logger));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "FileSystemProvider root path validation failed: {RootPath}", rootPath);
            return Result<FileSystemProvider>.Fail(
                ErrorCode.ProviderUnreachable,
                $"Configured root path '{rootPath}' is not accessible.",
                ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error validating FileSystemProvider root path: {RootPath}", rootPath);
            return Result<FileSystemProvider>.Fail(
                ErrorCode.Unknown,
                $"Unexpected error validating root path '{rootPath}'.",
                ex);
        }
    }

    // ── Upload session lifecycle ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<UploadSession>> BeginUploadAsync(
        string remoteName, long totalBytes, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var safe = SanitiseRemoteName(remoteName);
            var stagingDir = StagingDir();
            Directory.CreateDirectory(stagingDir);
            AtomicWriteHelper.FsyncDirectory(Path.GetDirectoryName(stagingDir)!);

            var sidecarPath = SidecarPath(safe);
            var sidecarJson = JsonSerializer.SerializeToUtf8Bytes(
                new { totalBytes, createdUtc = DateTimeOffset.UtcNow },
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            await AtomicWriteHelper.WriteAndFsyncAsync(sidecarPath, sidecarJson, ct).ConfigureAwait(false);

            _logger.LogDebug("Opened upload session for {Remote} ({TotalBytes} bytes)", safe, totalBytes);

            return Result<UploadSession>.Ok(new UploadSession
            {
                SessionUri = safe,
                ExpiresAt = DateTimeOffset.MaxValue,
                BytesUploaded = 0,
                TotalBytes = totalBytes,
            });
        }
        catch (OperationCanceledException ex)
        {
            return Result<UploadSession>.Fail(ErrorCode.Cancelled, "BeginUploadAsync cancelled.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied opening upload session at {RootPath}", _rootPath);
            return Result<UploadSession>.Fail(ErrorCode.ProviderUnreachable, "Access denied on tail root path.", ex);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "I/O error opening upload session for {Remote}", remoteName);
            return Result<UploadSession>.Fail(ErrorCode.UploadFailed, "I/O error starting upload session.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error opening upload session for {Remote}", remoteName);
            return Result<UploadSession>.Fail(ErrorCode.Unknown, "Unexpected error starting upload session.", ex);
        }
    }

    /// <inheritdoc/>
    public Task<Result<long>> GetUploadedBytesAsync(UploadSession session, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var partialPath = PartialPath(session.SessionUri);
            var info = new FileInfo(partialPath);
            // Missing partial file is not an error — upload restarts from byte 0.
            var length = info.Exists ? info.Length : 0L;
            return Task.FromResult(Result<long>.Ok(length));
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(Result<long>.Fail(ErrorCode.Cancelled, "GetUploadedBytesAsync cancelled.", ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading partial-file length for {SessionUri}", session.SessionUri);
            return Task.FromResult(Result<long>.Fail(ErrorCode.Unknown, "Unexpected error reading uploaded byte count.", ex));
        }
    }

    /// <inheritdoc/>
    public async Task<Result> UploadRangeAsync(
        UploadSession session, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var partialPath = PartialPath(session.SessionUri);
            await using var fs = new FileStream(
                partialPath,
                new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    Options = FileOptions.None,
                    // PreallocationSize is only valid for FileMode.Create/CreateNew in .NET 10+;
                    // OpenOrCreate throws ArgumentException if PreallocationSize > 0.
                });
            fs.Seek(offset, SeekOrigin.Begin);
            await fs.WriteAsync(data, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
            RandomAccess.FlushToDisk(fs.SafeFileHandle);

            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "UploadRangeAsync cancelled.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied writing range at offset {Offset} for {SessionUri}", offset, session.SessionUri);
            return Result.Fail(ErrorCode.ProviderUnreachable, "Access denied writing upload range.", ex);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "I/O error writing range at offset {Offset} for {SessionUri}", offset, session.SessionUri);
            return Result.Fail(ErrorCode.UploadFailed, "I/O error writing upload range.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error writing range for {SessionUri}", session.SessionUri);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error writing upload range.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<string>> FinaliseUploadAsync(UploadSession session, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var partialPath = PartialPath(session.SessionUri);
            var info = new FileInfo(partialPath);

            if (!info.Exists || info.Length != session.TotalBytes)
            {
                _logger.LogError(
                    "Finalise size mismatch for {SessionUri}: expected {Expected}, got {Actual}",
                    session.SessionUri, session.TotalBytes, info.Exists ? info.Length : 0L);
                return Result<string>.Fail(
                    ErrorCode.UploadFailed,
                    $"Final size mismatch: expected {session.TotalBytes} bytes.");
            }

            var destPath = ComputeRemotePath(session.SessionUri);
            var destDir = Path.GetDirectoryName(destPath)!;

            var midDir = Path.GetDirectoryName(destDir)!;
            var midExisted = Directory.Exists(midDir);
            var leafExisted = Directory.Exists(destDir);
            Directory.CreateDirectory(destDir);
            if (!midExisted)
            {
                AtomicWriteHelper.FsyncDirectory(Path.GetDirectoryName(midDir)!);
            }
            if (!leafExisted)
            {
                AtomicWriteHelper.FsyncDirectory(midDir);
            }

            ct.ThrowIfCancellationRequested();

            if (File.Exists(destPath))
            {
                _logger.LogError("Destination already exists for {SessionUri}: {DestPath}", session.SessionUri, destPath);
                return Result<string>.Fail(ErrorCode.UploadFailed, "Destination file already exists on tail.");
            }

            File.Move(partialPath, destPath, overwrite: false);
            AtomicWriteHelper.FsyncDirectory(destDir);

            // Best-effort sidecar cleanup — Principle 17: use CancellationToken.None.
            TryDeleteFile(SidecarPath(session.SessionUri));

            var remoteId = ComputeRemoteId(session.SessionUri);
            _logger.LogInformation("Finalised upload for {SessionUri} → {RemoteId}", session.SessionUri, remoteId);
            return Result<string>.Ok(remoteId);
        }
        catch (OperationCanceledException ex)
        {
            return Result<string>.Fail(ErrorCode.Cancelled, "FinaliseUploadAsync cancelled.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied finalising upload for {SessionUri}", session.SessionUri);
            return Result<string>.Fail(ErrorCode.ProviderUnreachable, "Access denied finalising upload.", ex);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "I/O error finalising upload for {SessionUri}", session.SessionUri);
            return Result<string>.Fail(ErrorCode.UploadFailed, "I/O error finalising upload.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error finalising upload for {SessionUri}", session.SessionUri);
            return Result<string>.Fail(ErrorCode.Unknown, "Unexpected error finalising upload.", ex);
        }
    }

    /// <inheritdoc/>
    public Task<Result> AbortUploadAsync(UploadSession session, CancellationToken ct)
    {
        // Best-effort: swallow errors — the orchestrator has already moved on.
        TryDeleteFile(PartialPath(session.SessionUri));
        TryDeleteFile(SidecarPath(session.SessionUri));
        return Task.FromResult(Result.Ok());
    }

    // ── Download / existence / delete ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<Result<Stream>> DownloadAsync(string remoteId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = FullPath(remoteId);
            Stream stream = File.OpenRead(fullPath);
            return Task.FromResult(Result<Stream>.Ok(stream));
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(Result<Stream>.Fail(ErrorCode.Cancelled, "DownloadAsync cancelled.", ex));
        }
        catch (FileNotFoundException ex)
        {
            return Task.FromResult(Result<Stream>.Fail(ErrorCode.BlobNotFound, $"Remote object '{remoteId}' not found on tail.", ex));
        }
        catch (DirectoryNotFoundException ex)
        {
            return Task.FromResult(Result<Stream>.Fail(ErrorCode.BlobNotFound, $"Remote object '{remoteId}' not found on tail.", ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied downloading {RemoteId}", remoteId);
            return Task.FromResult(Result<Stream>.Fail(ErrorCode.ProviderUnreachable, "Access denied reading from tail.", ex));
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "I/O error downloading {RemoteId}", remoteId);
            return Task.FromResult(Result<Stream>.Fail(ErrorCode.ProviderUnreachable, "I/O error reading from tail.", ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error downloading {RemoteId}", remoteId);
            return Task.FromResult(Result<Stream>.Fail(ErrorCode.Unknown, "Unexpected error during download.", ex));
        }
    }

    /// <inheritdoc/>
    public Task<Result> DeleteAsync(string remoteId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = FullPath(remoteId);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            return Task.FromResult(Result.Ok());
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(Result.Fail(ErrorCode.Cancelled, "DeleteAsync cancelled.", ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied deleting {RemoteId}", remoteId);
            return Task.FromResult(Result.Fail(ErrorCode.ProviderUnreachable, "Access denied deleting from tail.", ex));
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "I/O error deleting {RemoteId}", remoteId);
            return Task.FromResult(Result.Fail(ErrorCode.ProviderUnreachable, "I/O error deleting from tail.", ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting {RemoteId}", remoteId);
            return Task.FromResult(Result.Fail(ErrorCode.Unknown, "Unexpected error during delete.", ex));
        }
    }

    /// <inheritdoc/>
    public Task<Result<bool>> ExistsAsync(string remoteId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var exists = File.Exists(FullPath(remoteId));
            return Task.FromResult(Result<bool>.Ok(exists));
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(Result<bool>.Fail(ErrorCode.Cancelled, "ExistsAsync cancelled.", ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error checking existence of {RemoteId}", remoteId);
            return Task.FromResult(Result<bool>.Fail(ErrorCode.Unknown, "Unexpected error checking existence.", ex));
        }
    }

    // ── Listing ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<Result<IReadOnlyList<string>>> ListAsync(string prefix, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var searchRoot = string.IsNullOrEmpty(prefix)
                ? _rootPath
                : Path.Combine(_rootPath, prefix);

            if (!Directory.Exists(searchRoot))
            {
                return Task.FromResult(Result<IReadOnlyList<string>>.Ok(
                    Array.Empty<string>() as IReadOnlyList<string>));
            }

            var results = Directory
                .EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_rootPath, f).Replace('\\', '/'))
                .ToArray() as IReadOnlyList<string>;

            return Task.FromResult(Result<IReadOnlyList<string>>.Ok(results));
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(Result<IReadOnlyList<string>>.Fail(ErrorCode.Cancelled, "ListAsync cancelled.", ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied listing prefix '{Prefix}' on tail", prefix);
            return Task.FromResult(Result<IReadOnlyList<string>>.Fail(ErrorCode.ProviderUnreachable, "Access denied listing tail.", ex));
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "I/O error listing prefix '{Prefix}' on tail", prefix);
            return Task.FromResult(Result<IReadOnlyList<string>>.Fail(ErrorCode.ProviderUnreachable, "I/O error listing tail.", ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing prefix '{Prefix}'", prefix);
            return Task.FromResult(Result<IReadOnlyList<string>>.Fail(ErrorCode.Unknown, "Unexpected error listing tail.", ex));
        }
    }

    // ── Health and capacity ───────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ProviderHealth>> CheckHealthAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();

            var healthDir = Path.Combine(_rootPath, "_health");
            Directory.CreateDirectory(healthDir);

            var probe = Path.Combine(healthDir, $"{Guid.NewGuid():N}.probe");
            await File.WriteAllBytesAsync(probe, [0x01], ct).ConfigureAwait(false);
            await File.ReadAllBytesAsync(probe, ct).ConfigureAwait(false);
            File.Delete(probe);

            sw.Stop();
            return Result<ProviderHealth>.Ok(new ProviderHealth
            {
                Status = ProviderHealthStatus.Healthy,
                CheckedAt = DateTimeOffset.UtcNow,
                RoundTripLatency = sw.Elapsed,
            });
        }
        catch (OperationCanceledException ex)
        {
            return Result<ProviderHealth>.Fail(ErrorCode.Cancelled, "CheckHealthAsync cancelled.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Health probe failed for tail root {RootPath}", _rootPath);
            return Result<ProviderHealth>.Ok(new ProviderHealth
            {
                Status = ProviderHealthStatus.Unreachable,
                CheckedAt = DateTimeOffset.UtcNow,
                RoundTripLatency = sw.Elapsed,
                Detail = ex.Message,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during health probe for {RootPath}", _rootPath);
            return Result<ProviderHealth>.Fail(ErrorCode.Unknown, "Unexpected error during health probe.", ex);
        }
    }

    /// <inheritdoc/>
    public Task<Result<long>> GetUsedBytesAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var drive = new DriveInfo(_rootPath);
            var used = drive.TotalSize - drive.AvailableFreeSpace;
            return Task.FromResult(Result<long>.Ok(used));
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(Result<long>.Fail(ErrorCode.Cancelled, "GetUsedBytesAsync cancelled.", ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading drive info for {RootPath}", _rootPath);
            return Task.FromResult(Result<long>.Fail(ErrorCode.ProviderUnreachable, "Could not read drive usage.", ex));
        }
    }

    /// <inheritdoc/>
    public Task<Result<long?>> GetQuotaBytesAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var drive = new DriveInfo(_rootPath);
            long? quota = drive.TotalSize;
            return Task.FromResult(Result<long?>.Ok(quota));
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(Result<long?>.Fail(ErrorCode.Cancelled, "GetQuotaBytesAsync cancelled.", ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading drive quota for {RootPath}", _rootPath);
            return Task.FromResult(Result<long?>.Fail(ErrorCode.ProviderUnreachable, "Could not read drive quota.", ex));
        }
    }

    // ── XXHash64 capability (wired in §3.3 when ISupportsRemoteHashCheck is introduced) ──────

    /// <summary>
    /// Re-reads the finalised object at <paramref name="remoteId"/> and computes its XXHash64.
    /// Blueprint §15.7 FileSystem row. Called by <c>RangeUploader</c> via <c>ISupportsRemoteHashCheck</c>
    /// once that interface is introduced in §3.3.
    /// </summary>
    internal async Task<Result<ulong>> GetRemoteXxHash64Async(string remoteId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = FullPath(remoteId);
            var bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            var hash = XxHash64.HashToUInt64(bytes);
            return Result<ulong>.Ok(hash);
        }
        catch (OperationCanceledException ex)
        {
            return Result<ulong>.Fail(ErrorCode.Cancelled, "GetRemoteXxHash64Async cancelled.", ex);
        }
        catch (FileNotFoundException ex)
        {
            return Result<ulong>.Fail(ErrorCode.BlobNotFound, $"Remote object '{remoteId}' not found.", ex);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "I/O error hashing {RemoteId}", remoteId);
            return Result<ulong>.Fail(ErrorCode.ProviderUnreachable, "I/O error reading remote object for hash.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error hashing {RemoteId}", remoteId);
            return Result<ulong>.Fail(ErrorCode.Unknown, "Unexpected error computing remote hash.", ex);
        }
    }

    // ── Path helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sanitises a remote name for use as a flat staging filename: replaces path separators with
    /// underscores. The original remote name is preserved in the final sharded destination path.
    /// </summary>
    private static string SanitiseRemoteName(string remoteName) =>
        remoteName.Replace('/', '_').Replace('\\', '_');

    private string StagingDir() =>
        Path.Combine(_rootPath, ".flashskink-staging");

    private string SidecarPath(string sanitisedRemote) =>
        Path.Combine(StagingDir(), sanitisedRemote + ".session");

    private string PartialPath(string sanitisedRemote) =>
        Path.Combine(StagingDir(), sanitisedRemote + ".partial");

    /// <summary>
    /// Returns the full absolute destination path for a finalised object.
    /// Layout: <c>{rootPath}/blobs/{remoteName[0..2]}/{remoteName[2..4]}/{remoteName}</c>.
    /// Mirrors the skink's <c>.flashskink/blobs/{xx}/{yy}/{blobId}.bin</c> sharding.
    /// </summary>
    private string ComputeRemotePath(string sanitisedRemote)
    {
        Debug.Assert(sanitisedRemote.Length >= 4, "remote name must be >= 4 chars to shard");
        return Path.Combine(_rootPath, "blobs", sanitisedRemote[..2], sanitisedRemote[2..4], sanitisedRemote);
    }

    /// <summary>
    /// Returns the relative remote id (relative from <c>_rootPath</c>, forward-slash separated)
    /// stored in <c>TailUploads.RemoteID</c> and used by <see cref="DownloadAsync"/> /
    /// <see cref="DeleteAsync"/> / <see cref="ExistsAsync"/>.
    /// </summary>
    private string ComputeRemoteId(string sanitisedRemote)
    {
        var full = ComputeRemotePath(sanitisedRemote);
        return Path.GetRelativePath(_rootPath, full).Replace('\\', '/');
    }

    private string FullPath(string remoteId) =>
        Path.Combine(_rootPath, remoteId.Replace('/', Path.DirectorySeparatorChar));

    private static void TryDeleteFile(string path)
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
