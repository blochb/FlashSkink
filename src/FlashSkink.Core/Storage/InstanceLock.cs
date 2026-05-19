using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Storage;

/// <summary>
/// Holds an OS-level exclusive file lock on <c>[skinkRoot]/.flashskink/instance.lock</c> for
/// the lifetime of an open <c>FlashSkinkVolume</c>. A second concurrent
/// <c>CreateAsync</c>/<c>OpenAsync</c> on the same skink root — same host, or a different host
/// against the same USB volume mounted via a network share — fails the
/// <see cref="AcquireAsync"/> call with <see cref="ErrorCode.SingleInstanceLockHeld"/> and
/// surfaces the holder's identity in <see cref="ErrorContext.Metadata"/>. The OS releases
/// the file lock on process exit (clean or otherwise); a stale file remaining after a crash
/// is recoverable via the <c>force</c> parameter, intended for the Phase-4 CLI
/// <c>--force</c> flag. (Blueprint §19.5; CLAUDE.md Principle 35.)
/// </summary>
/// <remarks>
/// The lock primitive is a <see cref="FileStream"/> opened with
/// <see cref="FileAccess.ReadWrite"/> and <see cref="FileShare.Read"/>. The holder excludes
/// any second writer (because the second writer's <see cref="FileAccess.ReadWrite"/>
/// request conflicts with the holder's <see cref="FileShare.Read"/>) while still allowing
/// a concurrent reader to peek at the manifest payload — the second-instance launch reads
/// the manifest with <see cref="FileAccess.Read"/> to identify the holder. On Windows this
/// maps to the kernel's sharing-mode check; on Linux/macOS .NET maps it to an advisory
/// <c>flock</c>-equivalent (POSIX OFD or BSD <c>flock</c> depending on runtime). Either
/// way, two processes cannot simultaneously hold the file open with write access.
/// </remarks>
internal sealed class InstanceLock : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly string _lockFilePath;
    private readonly ILogger? _logger;
    private int _disposed;

    /// <summary>The absolute path to the lock file this instance is holding.</summary>
    internal string LockFilePath => _lockFilePath;

    private InstanceLock(FileStream stream, string lockFilePath, ILogger? logger)
    {
        _stream = stream;
        _lockFilePath = lockFilePath;
        _logger = logger;
    }

    /// <summary>
    /// Acquires the exclusive lock for the volume rooted at <paramref name="skinkRoot"/>.
    /// </summary>
    /// <param name="skinkRoot">The volume root; <c>.flashskink/</c> beneath it must already
    /// exist (the caller — <c>FlashSkinkVolume.CreateAsync</c> / <c>OpenAsync</c> —
    /// creates it before calling).</param>
    /// <param name="appVersion">Informational app version, written into the lock manifest
    /// alongside pid/host/started-at. Caller passes the same value used for
    /// <c>Settings.AppVersionLastOpened</c>.</param>
    /// <param name="force">When <see langword="true"/>, attempts to delete any pre-existing
    /// lock file before acquiring. Intended for the Phase-4 CLI <c>--force</c> stale-lock
    /// recovery path; production callers should pass <see langword="false"/>.</param>
    /// <param name="logger">Optional logger; failures are logged at <c>Warning</c> level
    /// when present (Principle 27 — Core logs at the construction site of the
    /// <see cref="Result.Fail"/>).</param>
    /// <param name="ct">Cancellation token observed before the file-open attempt.</param>
    /// <returns>
    /// <see cref="Result{InstanceLock}.Ok(InstanceLock)"/> on success.
    /// On contention: <see cref="Result{T}.Fail(ErrorCode, string)"/> with
    /// <see cref="ErrorCode.SingleInstanceLockHeld"/> and metadata identifying the holder.
    /// On config error (read-only path, etc.): <see cref="ErrorCode.StagingFailed"/>.
    /// </returns>
    internal static Task<Result<InstanceLock>> AcquireAsync(
        string skinkRoot,
        string appVersion,
        bool force,
        ILogger? logger,
        CancellationToken ct)
    {
        var lockFilePath = Path.Combine(skinkRoot, ".flashskink", "instance.lock");

        try
        {
            ct.ThrowIfCancellationRequested();

            if (force)
            {
                try
                {
                    File.Delete(lockFilePath);
                }
                catch (FileNotFoundException)
                {
                    // No stale file to remove — that's fine.
                }
                catch (DirectoryNotFoundException)
                {
                    // Parent missing — the OpenOrCreate below will fail with a clearer error.
                }
                // UnauthorizedAccessException / IOException from File.Delete propagate up so
                // the caller sees that the lock cannot be forcibly cleared — the opposite
                // of the intent. Caught by the outer catch and mapped to StagingFailed
                // (UnauthorizedAccessException) or Unknown (other IOException).
            }

            FileStream stream;
            try
            {
                stream = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    // FileShare.Read excludes a second writer (its ReadWrite request
                    // conflicts with this Read-only share permission) while letting a
                    // peer process read the manifest payload to identify us.
                    FileShare.Read,
                    bufferSize: 0,
                    FileOptions.WriteThrough);
            }
            catch (IOException ex)
            {
                // Another process holds the lock (or, less commonly, an unrelated I/O
                // error). Read the manifest with shared access to surface the holder.
                var holder = TryReadHolderManifest(lockFilePath);
                logger?.LogWarning(
                    "Single-instance lock at {LockFilePath} is already held: {Holder}",
                    lockFilePath, holder.ToDisplayString());
                var ctx = new ErrorContext
                {
                    Code = ErrorCode.SingleInstanceLockHeld,
                    Message =
                        $"FlashSkink is already running on this volume from another process or host " +
                        $"({holder.ToDisplayString()}). Close the other instance and try again.",
                    ExceptionType = ex.GetType().FullName,
                    ExceptionMessage = ex.Message,
                    StackTrace = ex.StackTrace,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Pid"] = holder.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["Host"] = holder.Host,
                        ["StartedAtUtc"] = holder.StartedAtUtc,
                        ["AppVersion"] = holder.AppVersion,
                        ["LockFilePath"] = lockFilePath,
                    },
                };
                return Task.FromResult(Result<InstanceLock>.Fail(ctx));
            }
            catch (UnauthorizedAccessException ex)
            {
                logger?.LogWarning(
                    ex, "Cannot create single-instance lock at {LockFilePath} — access denied.",
                    lockFilePath);
                return Task.FromResult(Result<InstanceLock>.Fail(
                    ErrorCode.StagingFailed,
                    $"Cannot create single-instance lock at '{lockFilePath}': access denied.",
                    ex));
            }

            // Lock acquired. Write the manifest so a concurrent reader can identify us.
            // If this throws, we must dispose the stream to release the OS lock — otherwise
            // the lock is held but no manifest exists, and we have no way to return the
            // FileStream ownership to the caller.
            try
            {
                var manifest = InstanceLockManifest.Current(appVersion).SerializeUtf8();
                stream.SetLength(0);
                stream.Write(manifest, 0, manifest.Length);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception ex)
            {
                stream.Dispose();
                logger?.LogWarning(
                    ex, "Failed to write single-instance lock manifest at {LockFilePath}.",
                    lockFilePath);
                return Task.FromResult(Result<InstanceLock>.Fail(
                    ErrorCode.StagingFailed,
                    $"Failed to write single-instance lock manifest at '{lockFilePath}'.",
                    ex));
            }

            return Task.FromResult(Result<InstanceLock>.Ok(
                new InstanceLock(stream, lockFilePath, logger)));
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(Result<InstanceLock>.Fail(
                ErrorCode.Cancelled, "Single-instance lock acquisition was cancelled.", ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            // Thrown by the `force`-path File.Delete when permissions deny removal.
            logger?.LogWarning(
                ex, "Cannot force-clear single-instance lock at {LockFilePath} — access denied.",
                lockFilePath);
            return Task.FromResult(Result<InstanceLock>.Fail(
                ErrorCode.StagingFailed,
                $"Cannot force-clear single-instance lock at '{lockFilePath}': access denied.",
                ex));
        }
        catch (IOException ex)
        {
            // Thrown by the `force`-path File.Delete when the file is held by another process
            // (i.e., user passed --force but the lock is genuinely live, not stale).
            logger?.LogWarning(
                ex, "Cannot force-clear single-instance lock at {LockFilePath} — file in use.",
                lockFilePath);
            var holder = TryReadHolderManifest(lockFilePath);
            var ctx = new ErrorContext
            {
                Code = ErrorCode.SingleInstanceLockHeld,
                Message =
                    $"FlashSkink is already running on this volume from another process or host " +
                    $"({holder.ToDisplayString()}); --force cannot clear a live lock.",
                ExceptionType = ex.GetType().FullName,
                ExceptionMessage = ex.Message,
                StackTrace = ex.StackTrace,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Pid"] = holder.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Host"] = holder.Host,
                    ["StartedAtUtc"] = holder.StartedAtUtc,
                    ["AppVersion"] = holder.AppVersion,
                    ["LockFilePath"] = lockFilePath,
                },
            };
            return Task.FromResult(Result<InstanceLock>.Fail(ctx));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "Unexpected error acquiring single-instance lock at {LockFilePath}.",
                lockFilePath);
            return Task.FromResult(Result<InstanceLock>.Fail(
                ErrorCode.Unknown,
                $"Unexpected error acquiring single-instance lock at '{lockFilePath}'.",
                ex));
        }
    }

    /// <summary>
    /// Reads the holder's manifest with shared-read access. Returns
    /// <see cref="InstanceLockManifest.Unknown"/> if the file cannot be read or parsed —
    /// holder identity is best-effort diagnostic detail; the lock conflict itself is what
    /// the caller needs to know about.
    /// </summary>
    private static InstanceLockManifest TryReadHolderManifest(string lockFilePath)
    {
        try
        {
            using var read = new FileStream(
                lockFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 0,
                FileOptions.None);
            Span<byte> buffer = stackalloc byte[512];
            var total = 0;
            while (total < buffer.Length)
            {
                var read1 = read.Read(buffer[total..]);
                if (read1 <= 0) { break; }
                total += read1;
            }
            return InstanceLockManifest.TryParse(buffer[..total], out var m)
                ? m
                : InstanceLockManifest.Unknown;
        }
        catch
        {
            // File deleted between failure and read, denied, etc. — fall back to placeholder.
            return InstanceLockManifest.Unknown;
        }
    }

    /// <summary>
    /// Releases the OS lock by closing the file handle, then best-effort deletes the lock
    /// file. Idempotent — safe to call multiple times. Never throws (matches the
    /// <see cref="VolumeSession"/> / <see cref="FlashSkinkVolume"/> dispose contract).
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Closing a FileStream should not throw, but if it does we cannot do anything
            // useful; the OS will release the lock when the process exits.
        }

        try
        {
            File.Delete(_lockFilePath);
        }
        catch
        {
            // A stale lock file on disk is harmless — the OS lock is released, and the next
            // acquire either reuses the file (FileMode.OpenOrCreate) or recovers via --force.
            // Never escalate a delete failure into a dispose throw.
            _logger?.LogDebug(
                "Best-effort delete of {LockFilePath} on dispose did not succeed.",
                _lockFilePath);
        }

        return ValueTask.CompletedTask;
    }
}
