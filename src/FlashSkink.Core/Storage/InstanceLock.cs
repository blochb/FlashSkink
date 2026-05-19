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
/// <para>
/// Two-file scheme: <c>instance.lock</c> is the exclusion file, opened with
/// <see cref="FileShare.None"/>. This is the one mode where .NET's cross-platform behaviour
/// is reliably exclusive — on Windows the kernel sharing-mode check rejects a second open;
/// on Linux/macOS .NET maps <see cref="FileShare.None"/> to an <c>flock(LOCK_EX)</c>-style
/// exclusive advisory lock. Other share modes (<see cref="FileShare.Read"/>, etc.) map to
/// <c>LOCK_SH</c> on Linux, which is compatible with itself and would let two writers both
/// succeed — so they cannot be used for exclusion.
/// </para>
/// <para>
/// Because <see cref="FileShare.None"/> excludes <i>all</i> concurrent access including
/// read, the holder identity (pid, host, started-at, app-version) is written to a
/// <i>separate</i> file, <c>instance.manifest</c>, that any process can read. A
/// second-instance launch fails the lock acquisition, then reads the sibling manifest file
/// to populate <see cref="ErrorContext.Metadata"/>.
/// </para>
/// </remarks>
internal sealed class InstanceLock : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly string _lockFilePath;
    private readonly string _manifestFilePath;
    private readonly ILogger? _logger;
    private int _disposed;

    /// <summary>The absolute path to the exclusion file this instance is holding.</summary>
    internal string LockFilePath => _lockFilePath;

    /// <summary>The absolute path to the sibling manifest file this instance wrote.</summary>
    internal string ManifestFilePath => _manifestFilePath;

    private InstanceLock(FileStream stream, string lockFilePath, string manifestFilePath, ILogger? logger)
    {
        _stream = stream;
        _lockFilePath = lockFilePath;
        _manifestFilePath = manifestFilePath;
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
        var flashskinkDir = Path.Combine(skinkRoot, ".flashskink");
        var lockFilePath = Path.Combine(flashskinkDir, "instance.lock");
        var manifestFilePath = Path.Combine(flashskinkDir, "instance.manifest");

        try
        {
            ct.ThrowIfCancellationRequested();

            if (force)
            {
                // Detect a live lock before touching the file. File.Delete (unlink) on
                // Linux/macOS succeeds on an flock-held file: it removes the directory
                // entry without affecting the holder's lock on the underlying inode. A
                // subsequent OpenOrCreate would then create a *different* inode at the
                // same path and acquire its *own* flock — two processes would both
                // believe they hold the exclusive lock, violating Principle 35.
                //
                // Probe-open with FileShare.None first. If the probe fails, the lock is
                // genuinely held and --force must refuse. If it succeeds, the lock is
                // stale — close the probe (so the delete below can remove the file on
                // Windows, where deleting an open file fails) and fall through to the
                // OpenOrCreate path. There is a small TOCTOU window between probe.Dispose
                // and the OpenOrCreate where another process could race to acquire; --force
                // is a human-initiated recovery action where that window is acceptable.
                if (File.Exists(lockFilePath))
                {
                    FileStream? probe = null;
                    try
                    {
                        probe = new FileStream(
                            lockFilePath,
                            FileMode.Open,
                            FileAccess.ReadWrite,
                            FileShare.None,
                            bufferSize: 0,
                            FileOptions.None);
                    }
                    catch (IOException ex)
                    {
                        var holder = TryReadHolderManifest(manifestFilePath);
                        logger?.LogWarning(
                            "Force-unlock blocked at {LockFilePath} — lock is live: {Holder}",
                            lockFilePath, holder.ToDisplayString());
                        return Task.FromResult(Result<InstanceLock>.Fail(
                            BuildHeldErrorContext(ex, holder, lockFilePath, manifestFilePath,
                                message:
                                    $"FlashSkink is already running on this volume from another process or host " +
                                    $"({holder.ToDisplayString()}); --force cannot clear a live lock.")));
                    }
                    // Probe succeeded → lock is stale. Release the probe so the delete
                    // below can remove the file on Windows (NTFS denies File.Delete while
                    // any handle is open; Linux/macOS would allow it but we always close
                    // for consistency).
                    probe.Dispose();
                }

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
                    // FileShare.None is the only mode that is reliably exclusive on both
                    // Windows (kernel sharing-mode check) and Linux/macOS (flock LOCK_EX).
                    FileShare.None,
                    bufferSize: 0,
                    FileOptions.WriteThrough);
            }
            catch (IOException ex)
            {
                // Another process holds the lock (or, less commonly, an unrelated I/O
                // error). Read the sibling manifest file to surface the holder identity.
                var holder = TryReadHolderManifest(manifestFilePath);
                logger?.LogWarning(
                    "Single-instance lock at {LockFilePath} is already held: {Holder}",
                    lockFilePath, holder.ToDisplayString());
                return Task.FromResult(Result<InstanceLock>.Fail(
                    BuildHeldErrorContext(ex, holder, lockFilePath, manifestFilePath,
                        message:
                            $"FlashSkink is already running on this volume from another process or host " +
                            $"({holder.ToDisplayString()}). Close the other instance and try again.")));
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

            // Lock acquired. Write the sibling manifest so a concurrent reader can identify
            // us. We're the sole writer of the manifest while we hold the lock; a peer
            // attempting to acquire will fail the lock-open and read the manifest with
            // shared-read access. If the manifest write throws, we must dispose the stream
            // to release the OS lock — otherwise the lock is held but no manifest exists,
            // and we cannot return ownership to the caller.
            try
            {
                var manifestBytes = InstanceLockManifest.Current(appVersion).SerializeUtf8();
                File.WriteAllBytes(manifestFilePath, manifestBytes);
            }
            catch (Exception ex)
            {
                stream.Dispose();
                logger?.LogWarning(
                    ex, "Failed to write single-instance lock manifest at {ManifestFilePath}.",
                    manifestFilePath);
                return Task.FromResult(Result<InstanceLock>.Fail(
                    ErrorCode.StagingFailed,
                    $"Failed to write single-instance lock manifest at '{manifestFilePath}'.",
                    ex));
            }

            return Task.FromResult(Result<InstanceLock>.Ok(
                new InstanceLock(stream, lockFilePath, manifestFilePath, logger)));
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
            // Live-lock detection is handled by the probe-open inside the `force` block;
            // reaching this catch means File.Delete failed for some other reason (disk
            // error, transient I/O fault). Map to StagingFailed rather than
            // SingleInstanceLockHeld — the lock state is genuinely unknown here.
            logger?.LogWarning(
                ex, "Force-clear of single-instance lock at {LockFilePath} failed.",
                lockFilePath);
            return Task.FromResult(Result<InstanceLock>.Fail(
                ErrorCode.StagingFailed,
                $"Failed to force-clear single-instance lock at '{lockFilePath}'.",
                ex));
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

    /// <summary>Builds the shared <see cref="ErrorContext"/> for a lock-held failure.</summary>
    private static ErrorContext BuildHeldErrorContext(
        Exception ex,
        InstanceLockManifest holder,
        string lockFilePath,
        string manifestFilePath,
        string message)
        => new()
        {
            Code = ErrorCode.SingleInstanceLockHeld,
            Message = message,
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
                ["ManifestFilePath"] = manifestFilePath,
            },
        };

    /// <summary>
    /// Reads the holder's manifest from the sibling file (independent of the locked exclusion
    /// file). Returns <see cref="InstanceLockManifest.Unknown"/> if the file does not exist
    /// or cannot be parsed — holder identity is best-effort diagnostic detail; the lock
    /// conflict itself is what the caller needs to know about.
    /// </summary>
    private static InstanceLockManifest TryReadHolderManifest(string manifestFilePath)
    {
        try
        {
            // The manifest is a plain file written by the holder — no lock contention,
            // any process can read with default shared semantics.
            if (!File.Exists(manifestFilePath))
            {
                return InstanceLockManifest.Unknown;
            }
            var bytes = File.ReadAllBytes(manifestFilePath);
            return InstanceLockManifest.TryParse(bytes, out var m)
                ? m
                : InstanceLockManifest.Unknown;
        }
        catch
        {
            // File deleted between Exists and ReadAllBytes, denied, etc. — fall back to placeholder.
            return InstanceLockManifest.Unknown;
        }
    }

    /// <summary>
    /// Releases the OS lock by closing the file handle, then best-effort deletes both the
    /// manifest file and the lock file. Idempotent — safe to call multiple times. Never
    /// throws (matches the <see cref="VolumeSession"/> / <see cref="FlashSkinkVolume"/>
    /// dispose contract).
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // Delete the manifest first — a peer reading it during the dispose window sees
        // either the holder's data (if still present) or `Unknown` (if already deleted),
        // both correct. After this the holder is no longer identifiable, but the lock is
        // still held, so the peer's acquire still fails with SingleInstanceLockHeld.
        try
        {
            File.Delete(_manifestFilePath);
        }
        catch
        {
            _logger?.LogDebug(
                "Best-effort delete of {ManifestFilePath} on dispose did not succeed.",
                _manifestFilePath);
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
