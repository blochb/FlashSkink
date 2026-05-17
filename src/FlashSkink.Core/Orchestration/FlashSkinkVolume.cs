using System.Security.Cryptography;
using System.Text;
using Dapper;
using FlashSkink.Core.Abstractions.Crypto;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Abstractions.Time;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Engine;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Providers;
using FlashSkink.Core.Storage;
using FlashSkink.Core.Upload;
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
    private readonly IProviderRegistry _providerRegistry;
    private readonly INetworkAvailabilityMonitor _networkMonitor;
    private readonly IClock _clock;
    private readonly UploadWakeupSignal _wakeupSignal;
    private readonly UploadQueueService _uploadQueueService;
    private readonly BrainMirrorService _brainMirrorService;
    private readonly CancellationTokenSource _volumeCts;
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
        string vaultPath,
        IProviderRegistry providerRegistry,
        INetworkAvailabilityMonitor networkMonitor,
        IClock clock,
        UploadWakeupSignal wakeupSignal,
        UploadQueueService uploadQueueService,
        BrainMirrorService brainMirrorService,
        CancellationTokenSource volumeCts)
    {
        _session = session;
        _context = context;
        _writePipeline = writePipeline;
        _readPipeline = readPipeline;
        _lifecycle = lifecycle;
        _keyVault = keyVault;
        _vaultPath = vaultPath;
        _providerRegistry = providerRegistry;
        _networkMonitor = networkMonitor;
        _clock = clock;
        _wakeupSignal = wakeupSignal;
        _uploadQueueService = uploadQueueService;
        _brainMirrorService = brainMirrorService;
        _volumeCts = volumeCts;
    }

    // ── Static factory methods ───────────────────────────────────────────────

    /// <summary>
    /// Creates a new volume at <paramref name="skinkRoot"/>: creates the directory skeleton,
    /// generates a recovery phrase, creates the vault, runs migrations, seeds initial
    /// Settings rows, and returns a <see cref="VolumeCreationReceipt"/> containing the open
    /// volume and the phrase.
    /// </summary>
    /// <remarks>
    /// <b>The recovery phrase is returned exactly once and is not persisted by FlashSkink
    /// anywhere</b> — neither on the skink nor on any tail (blueprint §18.8, §29 Decision
    /// A16). The caller is responsible for displaying the phrase to the user and disposing
    /// <see cref="VolumeCreationReceipt.RecoveryPhrase"/> when done; losing the receipt
    /// without recording the phrase forfeits the only out-of-band recovery path.
    /// </remarks>
    public static async Task<Result<VolumeCreationReceipt>> CreateAsync(
        string skinkRoot,
        string password,
        VolumeCreationOptions options,
        CancellationToken ct = default)
    {
        var vaultPath = Path.Combine(skinkRoot, ".flashskink", "vault.bin");
        var brainPath = Path.Combine(skinkRoot, ".flashskink", "brain.db");
        var stagingPath = Path.Combine(skinkRoot, ".flashskink", "staging");

        var services = BuildServices(options);
        var (_, streamManager, _, mnemonicService, keyVault, brainFactory, migrationRunner, lifecycle) = services;

        byte[]? passwordBytes = null;
        byte[]? dek = null;
        SqliteConnection? connection = null;
        bool vaultCreated = false;
        bool brainCreated = false;
        RecoveryPhrase? phrase = null;
        bool phraseOwned = false;

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
                return Result<VolumeCreationReceipt>.Fail(vaultResult.Error!);
            }

            vaultCreated = true;
            dek = vaultResult.Value!;

            var brainResult = await brainFactory.CreateAsync(brainPath, dek, ct).ConfigureAwait(false);
            if (!brainResult.Success)
            {
                return Result<VolumeCreationReceipt>.Fail(brainResult.Error!);
            }

            brainCreated = true;
            connection = brainResult.Value!;

            var migrationResult = await migrationRunner.RunAsync(connection, ct).ConfigureAwait(false);
            if (!migrationResult.Success)
            {
                return Result<VolumeCreationReceipt>.Fail(migrationResult.Error!);
            }

            var mnemonicResult = mnemonicService.Generate();
            if (!mnemonicResult.Success)
            {
                return Result<VolumeCreationReceipt>.Fail(mnemonicResult.Error!);
            }

            phrase = mnemonicResult.Value!;
            phraseOwned = true;

            var seedResult = await SeedInitialSettingsAsync(connection, ct).ConfigureAwait(false);
            if (!seedResult.Success)
            {
                return Result<VolumeCreationReceipt>.Fail(seedResult.Error!);
            }

            // Take ownership — clear locals so finally does not double-zero or delete files.
            var ownedDek = dek;
            var ownedConnection = connection;
            dek = null;
            connection = null;
            vaultCreated = false;
            brainCreated = false;

            var session = new VolumeSession(ownedDek, ownedConnection);
            var volume = await BuildVolumeFromSessionAsync(session, skinkRoot, options,
                streamManager, lifecycle, keyVault, vaultPath).ConfigureAwait(false);

            // Ownership of the phrase transfers to the receipt; the caller will dispose it.
            phraseOwned = false;
            return Result<VolumeCreationReceipt>.Ok(new VolumeCreationReceipt(volume, phrase));
        }
        catch (OperationCanceledException ex)
        {
            return Result<VolumeCreationReceipt>.Fail(ErrorCode.Cancelled, "Create volume was cancelled.", ex);
        }
        catch (Exception ex)
        {
            return Result<VolumeCreationReceipt>.Fail(ErrorCode.Unknown, "Unexpected error creating volume.", ex);
        }
        finally
        {
            if (phraseOwned && phrase is not null)
            {
                phrase.Dispose();
            }
            if (passwordBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
            if (dek is not null)
            {
                CryptographicOperations.ZeroMemory(dek);
            }
            connection?.Dispose();
            if (brainCreated && File.Exists(brainPath))
            {
                try { File.Delete(brainPath); } catch { /* best-effort brain cleanup on failure */ }
            }
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
        var (_, streamManager, _, _, keyVault, _, _, lifecycle) = services;

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

            var volume = await BuildVolumeFromSessionAsync(ownedSession, skinkRoot, options,
                streamManager, lifecycle, keyVault, vaultPath).ConfigureAwait(false);
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
            var result = await _writePipeline.ExecuteAsync(source, virtualPath, _context, ct).ConfigureAwait(false);
            if (result.Success)
            {
                _wakeupSignal.Pulse();
                _brainMirrorService.NotifyWriteCommitted();
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes a batch of items sequentially under the volume gate (cross-cutting decision 1 of
    /// Phase 2 — single-writer serialisation). Per §11.1, the bulk operation is not transactional
    /// across items; each item is an independent Phase-1 commit and per-item failures live inside
    /// the returned <see cref="BulkWriteReceipt"/>. The outer <see cref="Result{T}"/> only fails
    /// on pre-condition errors (null argument list, disposed volume, or cancellation observed
    /// while acquiring the gate).
    /// </summary>
    /// <remarks>
    /// When a <see cref="BulkWriteItem.OwnedSource"/> is provided, it is disposed in a
    /// <c>finally</c> block after each item's pipeline call — both on success and on failure.
    /// Cancellation observed mid-bulk records the in-flight item with
    /// <see cref="ErrorCode.Cancelled"/>, stops iterating, and returns the partial receipt;
    /// already-committed items remain committed. A single wakeup pulse is sent at the end of
    /// the bulk when at least one item succeeded — the upload-queue channel coalesces to
    /// capacity 1 and the brain-mirror debounce window absorbs multiple commits, so per-item
    /// pulses are not needed.
    /// </remarks>
    public async Task<Result<BulkWriteReceipt>> WriteBulkAsync(
        IReadOnlyList<BulkWriteItem> items,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (items is null)
        {
            return Result<BulkWriteReceipt>.Fail(
                ErrorCode.InvalidArgument, "Bulk write items list must not be null.");
        }

        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex)
        {
            return Result<BulkWriteReceipt>.Fail(ErrorCode.Cancelled, "Bulk write cancelled.", ex);
        }

        var results = new List<BulkItemResult>(items.Count);
        bool sawSuccess = false;
        try
        {
            foreach (var item in items)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var outcome = await _writePipeline.ExecuteAsync(
                        item.Source, item.VirtualPath, _context, ct).ConfigureAwait(false);
                    results.Add(new BulkItemResult
                    {
                        VirtualPath = item.VirtualPath,
                        Outcome = outcome,
                    });
                    if (outcome.Success)
                    {
                        sawSuccess = true;
                    }
                }
                catch (OperationCanceledException ocex)
                {
                    results.Add(new BulkItemResult
                    {
                        VirtualPath = item.VirtualPath,
                        Outcome = Result<WriteReceipt>.Fail(
                            ErrorCode.Cancelled, "Bulk write cancelled.", ocex),
                    });
                    break;
                }
                finally
                {
                    item.OwnedSource?.Dispose();
                }
            }

            if (sawSuccess)
            {
                _wakeupSignal.Pulse();
                _brainMirrorService.NotifyWriteCommitted();
            }

            return Result<BulkWriteReceipt>.Ok(new BulkWriteReceipt { Items = results });
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
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/'))
        {
            return Result<string>.Fail(ErrorCode.InvalidArgument,
                "Folder name must not be empty, whitespace, or contain '/'.");
        }

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

    // ── Tail registration (Phase 3 internal admin entry; Phase 4 replaces) ──

    /// <summary>
    /// Inserts a <c>Providers</c> row with no OAuth credentials (token / secret / client-id
    /// columns stay <see langword="null"/>; Phase 4 wires those in), registers
    /// <paramref name="provider"/> in the volume's <see cref="IProviderRegistry"/>, and pulses
    /// the upload wakeup signal so the orchestrator picks the new tail up on its next tick.
    /// </summary>
    /// <remarks>
    /// Idempotent on the brain row: when a <c>Providers</c> row with the same
    /// <paramref name="providerId"/> already exists, the insert is skipped and
    /// <see cref="Result.Ok"/> is returned. The <see cref="IStorageProvider"/> instance is
    /// registered in the in-memory registry regardless — the in-memory registry is rebuilt on
    /// every volume open. Pre-existing <c>TailUploads</c> rows are not mutated; Phase 4's
    /// public <c>AddTailAsync</c> will add the §11.1 "queue every existing file" backfill.
    /// </remarks>
    internal async Task<Result> RegisterTailAsync(
        string providerId,
        string providerType,
        string displayName,
        string? providerConfigJson,
        IStorageProvider provider,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(providerType)
            || string.IsNullOrWhiteSpace(displayName)
            || provider is null)
        {
            return Result.Fail(ErrorCode.InvalidArgument,
                "providerId, providerType, displayName, and provider must all be non-empty.");
        }

        if (_providerRegistry is not InMemoryProviderRegistry inMem)
        {
            return Result.Fail(ErrorCode.InvalidArgument,
                "RegisterTailAsync requires an InMemoryProviderRegistry-backed volume.");
        }

        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "Register tail cancelled.", ex);
        }

        try
        {
            var connection = _context.BrainConnection;
            var existing = await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(
                    "SELECT ProviderID FROM Providers WHERE ProviderID = @ProviderId",
                    new { ProviderId = providerId },
                    cancellationToken: ct))
                .ConfigureAwait(false);

            if (existing is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO Providers " +
                    "(ProviderID, ProviderType, DisplayName, ProviderConfig, HealthStatus, AddedUtc, IsActive) " +
                    "VALUES (@ProviderId, @ProviderType, @DisplayName, @ProviderConfig, 'Healthy', @AddedUtc, 1)",
                    new
                    {
                        ProviderId = providerId,
                        ProviderType = providerType,
                        DisplayName = displayName,
                        ProviderConfig = providerConfigJson,
                        AddedUtc = DateTime.UtcNow.ToString("O"),
                    },
                    cancellationToken: ct)).ConfigureAwait(false);
            }

            // Always register the in-process instance — the in-memory registry is rebuilt
            // every volume open, so even an idempotent re-registration must put the adapter back.
            inMem.Register(providerId, provider);
            _wakeupSignal.Pulse();
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "Register tail cancelled.", ex);
        }
        catch (SqliteException ex) when (ex.IsUniqueConstraintViolation())
        {
            return Result.Fail(ErrorCode.PathConflict,
                $"A provider with ID '{providerId}' already exists.", ex);
        }
        catch (SqliteException ex)
        {
            return Result.Fail(ErrorCode.DatabaseWriteFailed,
                "Failed to register tail.", ex);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCode.Unknown,
                "Unexpected error registering tail.", ex);
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
            // Step 1 — cancel the volume CTS. Workers and timers observe this at the next ct
            // check and exit their loops.
            _volumeCts.Cancel();

            // Step 2 — drain the upload queue. Workers may be mid-brain-transaction
            // (MarkUploading / MarkUploaded / DeleteSession) on the shared SqliteConnection;
            // wait for them to exit before any other code touches the connection. Microsoft's
            // SqliteConnection is not thread-safe, so this ordering is load-bearing.
            // Note: the dev plan §3.6 originally specified "mirror first, queue second" with
            // a rationale about per-provider HTTP contention (cloud-provider concern in
            // Phase 4). The shared brain connection is the practical race in Phase 3, so
            // queue-first is the correct ordering today. See pr-3.6.md drift note 5.
            await _uploadQueueService.DisposeAsync().ConfigureAwait(false);

            // Step 3 — brain mirror. With the queue drained, the brain connection has no
            // concurrent writers; the final-mirror call's BackupDatabase snapshot is safe.
            // DisposeAsync runs the final mirror under CancellationToken.None through every
            // active tail (Principle 17 — compensation must complete), then awaits the
            // timer and debounce tasks (5 s budget each).
            await _brainMirrorService.DisposeAsync().ConfigureAwait(false);

            // Step 4 — existing teardown: dispose VolumeContext (IncrementalHash,
            // CompressionService), then VolumeSession (zero DEK, dispose brain connection).
            _context.Dispose();
            await _session.DisposeAsync().ConfigureAwait(false);

            _volumeCts.Dispose();
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

    private static async Task<FlashSkinkVolume> BuildVolumeFromSessionAsync(
        VolumeSession session,
        string skinkRoot,
        VolumeCreationOptions options,
        RecyclableMemoryStreamManager streamManager,
        VolumeLifecycle lifecycle,
        KeyVault keyVault,
        string vaultPath)
    {
        var loggerFactory = options.LoggerFactory;
        var notificationBus = options.NotificationBus;
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

        // ── Phase 3 background services ─────────────────────────────────────
        var registry = options.ProviderRegistry
            ?? new InMemoryProviderRegistry(loggerFactory.CreateLogger<InMemoryProviderRegistry>());
        var netMonitor = options.NetworkMonitor ?? new AlwaysOnlineNetworkMonitor();
        var clock = options.Clock ?? SystemClock.Instance;

        var uploadQueueRepo = new UploadQueueRepository(
            connection, loggerFactory.CreateLogger<UploadQueueRepository>());
        var retryPolicy = new RetryPolicy();
        var rangeUploader = new RangeUploader(
            uploadQueueRepo, clock, retryPolicy,
            loggerFactory.CreateLogger<RangeUploader>());

        var wakeupSignal = new UploadWakeupSignal();

        var uploadQueueService = new UploadQueueService(
            uploadQueueRepo, blobs, files, activityLog,
            registry, netMonitor, notificationBus,
            rangeUploader, retryPolicy, clock, wakeupSignal,
            connection, skinkRoot,
            loggerFactory.CreateLogger<UploadQueueService>());

        var brainMirrorService = new BrainMirrorService(
            connection, dek, skinkRoot, registry,
            notificationBus, clock,
            loggerFactory.CreateLogger<BrainMirrorService>());

        var volumeCts = new CancellationTokenSource();

        var queueStartResult = uploadQueueService.Start(volumeCts.Token);
        if (!queueStartResult.Success)
        {
            await uploadQueueService.DisposeAsync().ConfigureAwait(false);
            await brainMirrorService.DisposeAsync().ConfigureAwait(false);
            volumeCts.Dispose();
            context.Dispose();
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Upload queue service failed to start: {queueStartResult.Error!.Code}");
        }

        var mirrorStartResult = brainMirrorService.Start(volumeCts.Token);
        if (!mirrorStartResult.Success)
        {
            await uploadQueueService.DisposeAsync().ConfigureAwait(false);
            await brainMirrorService.DisposeAsync().ConfigureAwait(false);
            volumeCts.Dispose();
            context.Dispose();
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Brain mirror service failed to start: {mirrorStartResult.Error!.Code}");
        }

        return new FlashSkinkVolume(
            session, context, writePipeline, readPipeline, lifecycle, keyVault, vaultPath,
            registry, netMonitor, clock, wakeupSignal, uploadQueueService, brainMirrorService,
            volumeCts);
    }

    private static async Task<Result> SeedInitialSettingsAsync(
        SqliteConnection connection,
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
            // Recovery phrase is intentionally NOT persisted here — it is returned to the
            // caller exactly once via VolumeCreationReceipt.RecoveryPhrase. See blueprint
            // §18.8 ("not persisted by FlashSkink") and §29 Decision A16.
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
