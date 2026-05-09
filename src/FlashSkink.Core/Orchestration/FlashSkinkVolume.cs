using System.Security.Cryptography;
using System.Text;
using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Engine;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.IO;

namespace FlashSkink.Core.Orchestration;

/// <summary>
/// Root public API for all file and folder operations on an open FlashSkink volume.
/// Single-writer serialised through an internal <see cref="SemaphoreSlim(1,1)"/> gate
/// (cross-cutting decision 1). Construct via <see cref="CreateAsync"/> or
/// <see cref="OpenAsync"/> only — no public constructor.
/// </summary>
public sealed class FlashSkinkVolume : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VolumeSession _session;
    private readonly VolumeContext _context;
    private readonly WritePipeline _writePipeline;
    private readonly ReadPipeline _readPipeline;
    private readonly VolumeLifecycle _lifecycle;
    private readonly KeyVault _keyVault;
    private readonly string _vaultPath;
    private int _disposed;

    // ── Events (declared; raisers arrive in later phases) ────────────────────
    // CS0067 suppressed: events are part of the §11 public contract declared here so
    // subscribers can bind from day one; raisers land when UsbMonitorService and
    // HealthMonitorService are wired in later phases.
#pragma warning disable CS0067

    /// <summary>
    /// Raised when the skink USB device is removed while the volume is open. Raisers
    /// arrive in Phase 6 when <c>UsbMonitorService</c> is wired.
    /// </summary>
    public event EventHandler<UsbRemovedEventArgs>? UsbRemoved;

    /// <summary>
    /// Raised when a previously-removed skink USB device is reinserted. Raisers
    /// arrive in Phase 6.
    /// </summary>
    public event EventHandler<UsbRemovedEventArgs>? UsbReinserted;

    /// <summary>
    /// Raised when a tail provider's health state changes. Raisers arrive in Phase 4
    /// when <c>HealthMonitorService</c> is wired.
    /// </summary>
    public event EventHandler<TailStatusChangedEventArgs>? TailStatusChanged;

#pragma warning restore CS0067

    // ── Private constructor ──────────────────────────────────────────────────

    private FlashSkinkVolume(
        VolumeSession session,
        VolumeContext context,
        WritePipeline writePipeline,
        ReadPipeline readPipeline,
        VolumeLifecycle lifecycle,
        KeyVault keyVault,
        string vaultPath)
    {
        _session = session;
        _context = context;
        _writePipeline = writePipeline;
        _readPipeline = readPipeline;
        _lifecycle = lifecycle;
        _keyVault = keyVault;
        _vaultPath = vaultPath;
    }

    // ── Static factory methods ───────────────────────────────────────────────

    /// <summary>
    /// Creates a new volume at <paramref name="skinkRoot"/>: creates the directory skeleton,
    /// generates a recovery phrase, creates the vault, runs migrations, seeds initial
    /// Settings rows, and returns an open <see cref="FlashSkinkVolume"/>.
    /// </summary>
    public static async Task<Result<FlashSkinkVolume>> CreateAsync(
        string skinkRoot,
        string password,
        VolumeCreationOptions options,
        CancellationToken ct = default)
    {
        var vaultPath = Path.Combine(skinkRoot, ".flashskink", "vault.bin");
        var brainPath = Path.Combine(skinkRoot, ".flashskink", "brain.db");
        var stagingPath = Path.Combine(skinkRoot, ".flashskink", "staging");

        var services = BuildServices(options);
        var (loggerFactory, streamManager, kdf, mnemonicService, keyVault, brainFactory, migrationRunner, lifecycle) = services;

        byte[]? passwordBytes = null;
        byte[]? dek = null;
        SqliteConnection? connection = null;
        bool vaultCreated = false;

        try
        {
            ct.ThrowIfCancellationRequested();

            Directory.CreateDirectory(stagingPath);
            await FsyncDirectoryAsync(stagingPath).ConfigureAwait(false);

            passwordBytes = Encoding.UTF8.GetBytes(password);
            var passwordMem = new ReadOnlyMemory<byte>(passwordBytes);

            var vaultResult = await keyVault.CreateAsync(vaultPath, passwordMem, ct).ConfigureAwait(false);
            if (!vaultResult.Success)
            {
                return Result<FlashSkinkVolume>.Fail(vaultResult.Error!);
            }

            vaultCreated = true;
            dek = vaultResult.Value!;

            var brainResult = await brainFactory.CreateAsync(brainPath, dek, ct).ConfigureAwait(false);
            if (!brainResult.Success)
            {
                return Result<FlashSkinkVolume>.Fail(brainResult.Error!);
            }

            connection = brainResult.Value!;

            var migrationResult = await migrationRunner.RunAsync(connection, ct).ConfigureAwait(false);
            if (!migrationResult.Success)
            {
                return Result<FlashSkinkVolume>.Fail(migrationResult.Error!);
            }

            var mnemonicResult = mnemonicService.Generate();
            if (!mnemonicResult.Success)
            {
                return Result<FlashSkinkVolume>.Fail(mnemonicResult.Error!);
            }

            var seedResult = await SeedInitialSettingsAsync(connection, mnemonicResult.Value!, ct).ConfigureAwait(false);
            if (!seedResult.Success)
            {
                return Result<FlashSkinkVolume>.Fail(seedResult.Error!);
            }

            // Take ownership — clear locals so finally does not double-zero or delete vault.
            var ownedDek = dek;
            var ownedConnection = connection;
            dek = null;
            connection = null;
            vaultCreated = false;

            var session = new VolumeSession(ownedDek, ownedConnection);
            var volume = BuildVolumeFromSession(session, skinkRoot, streamManager,
                options.NotificationBus, loggerFactory, lifecycle, keyVault, vaultPath);
            return Result<FlashSkinkVolume>.Ok(volume);
        }
        catch (OperationCanceledException ex)
        {
            return Result<FlashSkinkVolume>.Fail(ErrorCode.Cancelled, "Create volume was cancelled.", ex);
        }
        catch (Exception ex)
        {
            return Result<FlashSkinkVolume>.Fail(ErrorCode.Unknown, "Unexpected error creating volume.", ex);
        }
        finally
        {
            if (passwordBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
            if (dek is not null)
            {
                CryptographicOperations.ZeroMemory(dek);
            }
            connection?.Dispose();
            if (vaultCreated && File.Exists(vaultPath))
            {
                try { File.Delete(vaultPath); } catch { /* best-effort vault cleanup on failure */ }
            }
        }
    }

    /// <summary>
    /// Opens an existing volume at <paramref name="skinkRoot"/>: unlocks the vault, derives
    /// the DEK, opens and migrates the brain connection, and returns an open
    /// <see cref="FlashSkinkVolume"/>.
    /// </summary>
    public static async Task<Result<FlashSkinkVolume>> OpenAsync(
        string skinkRoot,
        string password,
        VolumeCreationOptions options,
        CancellationToken ct = default)
    {
        var vaultPath = Path.Combine(skinkRoot, ".flashskink", "vault.bin");
        var stagingPath = Path.Combine(skinkRoot, ".flashskink", "staging");

        var services = BuildServices(options);
        var (loggerFactory, streamManager, _, _, keyVault, _, _, lifecycle) = services;

        byte[]? passwordBytes = null;
        VolumeSession? session = null;

        try
        {
            ct.ThrowIfCancellationRequested();

            Directory.CreateDirectory(stagingPath);

            passwordBytes = Encoding.UTF8.GetBytes(password);
            var passwordMem = new ReadOnlyMemory<byte>(passwordBytes);

            var openResult = await lifecycle.OpenAsync(skinkRoot, passwordMem, ct).ConfigureAwait(false);
            if (!openResult.Success)
            {
                return Result<FlashSkinkVolume>.Fail(openResult.Error!);
            }

            // Take ownership so the finally block does not dispose the session.
            session = openResult.Value!;
            var ownedSession = session;
            session = null;

            var volume = BuildVolumeFromSession(ownedSession, skinkRoot, streamManager,
                options.NotificationBus, loggerFactory, lifecycle, keyVault, vaultPath);
            return Result<FlashSkinkVolume>.Ok(volume);
        }
        catch (OperationCanceledException ex)
        {
            return Result<FlashSkinkVolume>.Fail(ErrorCode.Cancelled, "Open volume was cancelled.", ex);
        }
        catch (Exception ex)
        {
            return Result<FlashSkinkVolume>.Fail(ErrorCode.Unknown, "Unexpected error opening volume.", ex);
        }
        finally
        {
            if (passwordBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // ── File operations ──────────────────────────────────────────────────────

    /// <summary>
    /// Encrypts and commits <paramref name="source"/> to the skink at
    /// <paramref name="virtualPath"/>. Delegates to <see cref="WritePipeline"/>.
    /// </summary>
    public async Task<Result<WriteReceipt>> WriteFileAsync(
        Stream source,
        string virtualPath,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { return Result<WriteReceipt>.Fail(ErrorCode.Cancelled, "Write cancelled.", ex); }
        try
        {
            return await _writePipeline.ExecuteAsync(source, virtualPath, _context, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Decrypts the file at <paramref name="virtualPath"/> and streams the verified
    /// plaintext into <paramref name="destination"/>. Delegates to <see cref="ReadPipeline"/>.
    /// </summary>
    public async Task<Result> ReadFileAsync(
        string virtualPath,
        Stream destination,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { return Result.Fail(ErrorCode.Cancelled, "Read cancelled.", ex); }
        try
        {
            return await _readPipeline.ExecuteAsync(virtualPath, destination, _context, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Soft-deletes the file at <paramref name="virtualPath"/>. Returns
    /// <see cref="ErrorCode.FileNotFound"/> when no matching row exists.
    /// </summary>
    public async Task<Result> DeleteFileAsync(
        string virtualPath,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { return Result.Fail(ErrorCode.Cancelled, "Delete cancelled.", ex); }
        try
        {
            var lookupResult = await _context.Files.GetByVirtualPathAsync(virtualPath, ct).ConfigureAwait(false);
            if (!lookupResult.Success)
            {
                return Result.Fail(lookupResult.Error!);
            }
            if (lookupResult.Value is null)
            {
                return Result.Fail(ErrorCode.FileNotFound, $"File not found at '{virtualPath}'.");
            }

            return await _context.Files.DeleteFileAsync(lookupResult.Value.FileId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Folder operations ────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new folder named <paramref name="name"/> under the parent identified by
    /// <paramref name="parentId"/> (or at root when <see langword="null"/>). Returns the
    /// new folder's <c>FileID</c>. Returns <see cref="ErrorCode.PathConflict"/> when the
    /// name already exists at the same location. (<c>CreateFolderAsync(name, parentId?)</c>
    /// is the path-vs-ID hybrid: the name is user-supplied; the parent is navigated.)
    /// </summary>
    public async Task<Result<string>> CreateFolderAsync(
        string name,
        string? parentId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { return Result<string>.Fail(ErrorCode.Cancelled, "Create folder cancelled.", ex); }
        try
        {
            string parentVirtualPath = string.Empty;
            if (parentId is not null)
            {
                var parentResult = await _context.Files.GetByIdAsync(parentId, ct).ConfigureAwait(false);
                if (!parentResult.Success)
                {
                    return Result<string>.Fail(parentResult.Error!);
                }
                if (parentResult.Value is null)
                {
                    return Result<string>.Fail(ErrorCode.FileNotFound, $"Parent folder '{parentId}' not found.");
                }
                parentVirtualPath = parentResult.Value.VirtualPath;
            }

            var virtualPath = parentVirtualPath.Length == 0
                ? name
                : parentVirtualPath + "/" + name;

            var now = DateTime.UtcNow;
            var folderId = Guid.NewGuid().ToString();
            var folder = new VolumeFile
            {
                FileId = folderId,
                ParentId = parentId,
                IsFolder = true,
                IsSymlink = false,
                Name = name,
                VirtualPath = virtualPath,
                SizeBytes = 0,
                CreatedUtc = now,
                ModifiedUtc = now,
                AddedUtc = now,
            };

            var insertResult = await _context.Files.InsertAsync(folder, ct).ConfigureAwait(false);
            if (!insertResult.Success)
            {
                return Result<string>.Fail(insertResult.Error!);
            }

            return Result<string>.Ok(folderId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes the folder identified by <paramref name="folderId"/>. When the folder is
    /// non-empty and <paramref name="confirmed"/> is <see langword="false"/>, returns
    /// <see cref="ErrorCode.ConfirmationRequired"/> with <c>Metadata["ChildCount"]</c>
    /// populated. Pass <see langword="true"/> to cascade-delete all descendants.
    /// </summary>
    public async Task<Result> DeleteFolderAsync(
        string folderId,
        bool confirmed,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { return Result.Fail(ErrorCode.Cancelled, "Delete folder cancelled.", ex); }
        try
        {
            return await _context.Files.DeleteFolderCascadeAsync(folderId, confirmed, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Renames the folder identified by <paramref name="folderId"/> to
    /// <paramref name="newName"/>, cascading <c>VirtualPath</c> updates to all descendants.
    /// </summary>
    public async Task<Result> RenameFolderAsync(
        string folderId,
        string newName,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { return Result.Fail(ErrorCode.Cancelled, "Rename folder cancelled.", ex); }
        try
        {
            return await _context.Files.RenameFolderAsync(folderId, newName, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Moves the file or folder identified by <paramref name="fileId"/> to a new parent
    /// (<paramref name="newParentId"/> = <see langword="null"/> means root). For folders,
    /// cascades <c>VirtualPath</c> updates to all descendants. Returns
    /// <see cref="ErrorCode.CyclicMoveDetected"/> when the target is a descendant of the
    /// item being moved.
    /// </summary>
    public async Task<Result> MoveAsync(
        string fileId,
        string? newParentId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { return Result.Fail(ErrorCode.Cancelled, "Move cancelled.", ex); }
        try
        {
            return await _context.Files.MoveAsync(fileId, newParentId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the immediate children of the folder identified by
    /// <paramref name="parentId"/> (or root-level items when <see langword="null"/>),
    /// ordered folders-first then alphabetically.
    /// </summary>
    public async Task<Result<IReadOnlyList<VolumeFile>>> ListChildrenAsync(
        string? parentId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { return Result<IReadOnlyList<VolumeFile>>.Fail(ErrorCode.Cancelled, "List children cancelled.", ex); }
        try
        {
            return await _context.Files.ListChildrenAsync(parentId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns all files whose <c>VirtualPath</c> begins with
    /// <paramref name="virtualPathPrefix"/>.
    /// </summary>
    public async Task<Result<IReadOnlyList<VolumeFile>>> ListFilesAsync(
        string virtualPathPrefix,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { return Result<IReadOnlyList<VolumeFile>>.Fail(ErrorCode.Cancelled, "List files cancelled.", ex); }
        try
        {
            return await _context.Files.ListFilesAsync(virtualPathPrefix, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Password and recovery ────────────────────────────────────────────────

    /// <summary>
    /// Re-wraps the DEK under a new password. The brain key is unaffected (HKDF from the
    /// same DEK). Returns <see cref="ErrorCode.InvalidPassword"/> when
    /// <paramref name="currentPassword"/> is wrong.
    /// </summary>
    public async Task<Result> ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { return Result.Fail(ErrorCode.Cancelled, "Change password cancelled.", ex); }
        byte[]? currentBytes = null;
        byte[]? newBytes = null;
        try
        {
            currentBytes = Encoding.UTF8.GetBytes(currentPassword);
            newBytes = Encoding.UTF8.GetBytes(newPassword);
            return await _keyVault.ChangePasswordAsync(
                _vaultPath,
                new ReadOnlyMemory<byte>(currentBytes),
                new ReadOnlyMemory<byte>(newBytes),
                ct).ConfigureAwait(false);
        }
        finally
        {
            if (currentBytes is not null) { CryptographicOperations.ZeroMemory(currentBytes); }
            if (newBytes is not null) { CryptographicOperations.ZeroMemory(newBytes); }
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-activates a soft-deleted blob identified by <paramref name="blobId"/> and
    /// re-inserts a <c>Files</c> row at <paramref name="virtualPath"/>. Returns
    /// <see cref="ErrorCode.BlobNotFound"/> when the blob's grace period has expired and
    /// it has already been hard-deleted.
    /// </summary>
    public async Task<Result> RestoreFromGracePeriodAsync(
        string blobId,
        string virtualPath,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { return Result.Fail(ErrorCode.Cancelled, "Restore cancelled.", ex); }
        try
        {
            return await _context.Files.RestoreFromGracePeriodAsync(blobId, virtualPath, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Zeroes all key material, disposes the brain connection, and releases the
    /// serialisation gate. Idempotent. Acquires the gate with
    /// <see cref="CancellationToken.None"/> as a literal before tearing down to avoid
    /// racing in-flight operations (Principle 17).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _context.Dispose();
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            // _gate is intentionally not disposed: SemaphoreSlim holds no unmanaged resources
            // unless AvailableWaitHandle is accessed (which this code never does), and disposing
            // it would race with concurrent callers that passed ThrowIfDisposed but haven't yet
            // called WaitAsync, causing an ObjectDisposedException to escape the public API.
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(FlashSkinkVolume));
        }
    }

    private static (ILoggerFactory, RecyclableMemoryStreamManager, KeyDerivationService,
        MnemonicService, KeyVault, BrainConnectionFactory, MigrationRunner, VolumeLifecycle)
        BuildServices(VolumeCreationOptions options)
    {
        var lf = options.LoggerFactory;
        var sm = options.StreamManager ?? new RecyclableMemoryStreamManager();
        var kdf = new KeyDerivationService();
        var mnemonic = new MnemonicService();
        var vault = new KeyVault(kdf, mnemonic);
        var brainFactory = new BrainConnectionFactory(kdf, lf.CreateLogger<BrainConnectionFactory>());
        var migrations = new MigrationRunner(lf.CreateLogger<MigrationRunner>());
        var lifecycle = new VolumeLifecycle(vault, brainFactory, migrations, lf.CreateLogger<VolumeLifecycle>());
        return (lf, sm, kdf, mnemonic, vault, brainFactory, migrations, lifecycle);
    }

    private static FlashSkinkVolume BuildVolumeFromSession(
        VolumeSession session,
        string skinkRoot,
        RecyclableMemoryStreamManager streamManager,
        INotificationBus notificationBus,
        ILoggerFactory loggerFactory,
        VolumeLifecycle lifecycle,
        KeyVault keyVault,
        string vaultPath)
    {
        var connection = session.BrainConnection!;
        var dek = new ReadOnlyMemory<byte>(session.Dek);

        var wal = new WalRepository(connection, loggerFactory.CreateLogger<WalRepository>());
        var blobs = new BlobRepository(connection, loggerFactory.CreateLogger<BlobRepository>());
        var files = new FileRepository(connection, wal, loggerFactory.CreateLogger<FileRepository>());
        var activityLog = new ActivityLogRepository(connection, loggerFactory.CreateLogger<ActivityLogRepository>());
        var blobWriter = new AtomicBlobWriter(loggerFactory.CreateLogger<AtomicBlobWriter>());
        var crypto = new CryptoPipeline();
        var compression = new CompressionService();
        var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var context = new VolumeContext(
            connection, dek, skinkRoot, sha256, crypto, compression,
            blobWriter, streamManager, notificationBus,
            blobs, files, wal, activityLog);

        var writePipeline = new WritePipeline(
            new FileTypeService(), new EntropyDetector(), loggerFactory);
        var readPipeline = new ReadPipeline(loggerFactory);

        return new FlashSkinkVolume(
            session, context, writePipeline, readPipeline, lifecycle, keyVault, vaultPath);
    }

    private static async Task<Result> SeedInitialSettingsAsync(
        SqliteConnection connection,
        string[] mnemonicWords,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string upsert = "INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@Key, @Value)";
            var appVersion = typeof(FlashSkinkVolume).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "GracePeriodDays", Value = "30" }, cancellationToken: ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "AuditIntervalHours", Value = "168" }, cancellationToken: ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "VolumeCreatedUtc", Value = DateTime.UtcNow.ToString("O") }, cancellationToken: ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "AppVersion", Value = appVersion }, cancellationToken: ct)).ConfigureAwait(false);
            // Stored in Settings (not logged, not surfaced through ErrorContext.Metadata) — Principle 26.
            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "RecoveryPhrase", Value = string.Join(" ", mnemonicWords) }, cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "Settings seed was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            return Result.Fail(ErrorCode.DatabaseWriteFailed, "Failed to seed initial settings.", ex);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCode.Unknown, "Unexpected error seeding settings.", ex);
        }
    }

    private static Task FsyncDirectoryAsync(string path)
    {
        // On Linux/macOS, fsync the staging directory so the mkdir is durable before
        // any rename that targets it (§13.4). On Windows, NTFS metadata journaling
        // provides equivalent durability without explicit FlushFileBuffers on the dir.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                RandomAccess.FlushToDisk(handle);
            }
            catch
            {
                // Best-effort — most modern filesystems do not require this for correctness.
            }
        }

        return Task.CompletedTask;
    }
}
