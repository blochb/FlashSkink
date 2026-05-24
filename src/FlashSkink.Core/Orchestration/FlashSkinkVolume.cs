using System.Globalization;
using System.Reflection;
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
using FlashSkink.Core.Identity;
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
    private readonly InstanceLock _instanceLock;
    private readonly WitnessStore _witnessStore;
    private readonly ILogger<FlashSkinkVolume> _logger;
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

    /// <summary>
    /// The current operational state of this volume. <see cref="VolumeState.Fenced"/> indicates
    /// that a split-brain conflict was detected during the session-begin handshake; Phase 2
    /// uploads are blocked until resolved by <c>PromoteAsync</c> (dev plan §3.5.3). Read-only
    /// to callers; transitions to <see cref="VolumeState.Fenced"/> happen inside
    /// <see cref="OpenAsync"/> when the handshake detects a fresh conflict, and the value
    /// persists across close/reopen via <c>Settings["VolumeState"]</c>. (Blueprint §19.7.)
    /// </summary>
    public VolumeState State { get; private set; }

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
        CancellationTokenSource volumeCts,
        InstanceLock instanceLock,
        WitnessStore witnessStore,
        VolumeState initialState,
        ILogger<FlashSkinkVolume> logger)
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
        _instanceLock = instanceLock;
        _witnessStore = witnessStore;
        State = initialState;
        _logger = logger;
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
        InstanceLock? instanceLock = null;
        bool lockHeld = false;

        try
        {
            ct.ThrowIfCancellationRequested();

            Directory.CreateDirectory(stagingPath);
            await FsyncDirectoryAsync(stagingPath).ConfigureAwait(false);

            // Acquire the single-instance lock before any vault/brain work so a concurrent
            // CreateAsync/OpenAsync on the same root fails fast with SingleInstanceLockHeld
            // instead of racing on vault.bin / brain.db (Blueprint §19.5, Principle 35).
            var lockResult = await InstanceLock.AcquireAsync(
                skinkRoot,
                GetAppInformationalVersion(),
                options.ForceUnlock,
                options.LoggerFactory.CreateLogger<InstanceLock>(),
                ct).ConfigureAwait(false);
            if (!lockResult.Success)
            {
                return Result<VolumeCreationReceipt>.Fail(lockResult.Error!);
            }
            instanceLock = lockResult.Value!;
            lockHeld = true;

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
            var ownedLock = instanceLock;
            dek = null;
            connection = null;
            vaultCreated = false;
            brainCreated = false;
            lockHeld = false;

            // Wrap the raw connection in BrainAccess at the ownership-transfer boundary
            // (Principle 36 — from here, no raw SqliteConnection flows downstream).
            var brainAccess = new BrainAccess(ownedConnection);
            var session = new VolumeSession(ownedDek, brainAccess);
            // A brand-new volume has no registered tails, so no witness handshake can fire
            // and the initial state is always Normal. RegisterTailAsync will write the first
            // witness to each tail as it is registered (dev plan §3.5.2 Change 3).
            var volume = await BuildVolumeFromSessionAsync(session, skinkRoot, options,
                streamManager, lifecycle, keyVault, vaultPath, ownedLock!,
                VolumeState.Normal).ConfigureAwait(false);

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
            // Release the single-instance lock last so any concurrent second-instance launch
            // sees SingleInstanceLockHeld for the entire window during which vault/brain
            // files might exist in a half-built state (Blueprint §19.5).
            if (lockHeld && instanceLock is not null)
            {
                await instanceLock.DisposeAsync().ConfigureAwait(false);
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
        InstanceLock? instanceLock = null;
        bool lockHeld = false;

        try
        {
            ct.ThrowIfCancellationRequested();

            Directory.CreateDirectory(stagingPath);

            // Acquire the single-instance lock before vault unlock so a concurrent open
            // fails fast with SingleInstanceLockHeld instead of racing to derive the same
            // KEK (Blueprint §19.5, Principle 35).
            var lockResult = await InstanceLock.AcquireAsync(
                skinkRoot,
                GetAppInformationalVersion(),
                options.ForceUnlock,
                options.LoggerFactory.CreateLogger<InstanceLock>(),
                ct).ConfigureAwait(false);
            if (!lockResult.Success)
            {
                return Result<FlashSkinkVolume>.Fail(lockResult.Error!);
            }
            instanceLock = lockResult.Value!;
            lockHeld = true;

            passwordBytes = Encoding.UTF8.GetBytes(password);
            var passwordMem = new ReadOnlyMemory<byte>(passwordBytes);

            var openResult = await lifecycle.OpenAsync(skinkRoot, passwordMem, ct).ConfigureAwait(false);
            if (!openResult.Success)
            {
                return Result<FlashSkinkVolume>.Fail(openResult.Error!);
            }

            session = openResult.Value!;

            // Backfill volume identity, stamp the open-time app-version (Refactor PR A),
            // and increment VolumeEpoch + read VolumeState (dev plan §3.5.1). Runs before
            // ownership transfer so a failure leaves `session` non-null and the existing
            // finally block disposes it cleanly. The returned (state, newEpoch) tuple is
            // captured but unused in §3.5.1 — it is consumed by the §3.5.2 witness
            // handshake call site added later in this folder.
            var backfillResult = await BackfillAndStampOnOpenAsync(
                session.Brain!, ct).ConfigureAwait(false);
            if (!backfillResult.Success)
            {
                return Result<FlashSkinkVolume>.Fail(backfillResult.Error!);
            }
            var (preHandshakeState, newEpoch) = backfillResult.Value;

            // Session-begin witness handshake (Blueprint §19.6, Principle 35). At this
            // point the single-instance lock is held, the brain is open and migrated, the
            // epoch has been incremented, and ownership has NOT yet transferred to
            // BuildVolumeFromSessionAsync. A handshake failure leaves `session` non-null and
            // the finally block disposes session and lock cleanly.
            var handshakeResult = await RunWitnessHandshakeAsync(
                session, options, newEpoch, preHandshakeState, ct).ConfigureAwait(false);
            if (!handshakeResult.Success)
            {
                return Result<FlashSkinkVolume>.Fail(handshakeResult.Error!);
            }
            var postHandshakeState = handshakeResult.Value;

            // Take ownership so the finally block does not dispose the session or lock.
            var ownedSession = session;
            var ownedLock = instanceLock;
            session = null;
            lockHeld = false;

            var volume = await BuildVolumeFromSessionAsync(ownedSession, skinkRoot, options,
                streamManager, lifecycle, keyVault, vaultPath, ownedLock!,
                postHandshakeState).ConfigureAwait(false);
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
            // Release the single-instance lock last so any concurrent second-instance
            // launch sees SingleInstanceLockHeld for the entire window during which the
            // session might exist in a half-built state (Blueprint §19.5).
            if (lockHeld && instanceLock is not null)
            {
                await instanceLock.DisposeAsync().ConfigureAwait(false);
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
        ThrowIfDisposedAndReleaseGate();
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
        ThrowIfDisposedAndReleaseGate();

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
        ThrowIfDisposedAndReleaseGate();
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
        ThrowIfDisposedAndReleaseGate();
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
        ThrowIfDisposedAndReleaseGate();
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
        ThrowIfDisposedAndReleaseGate();
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
        ThrowIfDisposedAndReleaseGate();
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
        ThrowIfDisposedAndReleaseGate();
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
        ThrowIfDisposedAndReleaseGate();
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
        ThrowIfDisposedAndReleaseGate();
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
        ThrowIfDisposedAndReleaseGate();
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
        ThrowIfDisposedAndReleaseGate();
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
        ThrowIfDisposedAndReleaseGate();

        try
        {
            // Hold the brain scope across the SELECT + INSERT so the select-then-insert is
            // atomic against any worker SQL on the shared connection (Principle 36).
            using (var brainScope = await _context.Brain.LockAsync(ct).ConfigureAwait(false))
            {
                var connection = brainScope.Connection;
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
            }

            // Always register the in-process instance — the in-memory registry is rebuilt
            // every volume open, so even an idempotent re-registration must put the adapter back.
            inMem.Register(providerId, provider);

            // Write an initial witness to the freshly-registered tail. Without this, a clone
            // of the USB opened between this RegisterTailAsync and the next OpenAsync would
            // see "no witness on this tail" and treat as first use — silently missing the
            // conflict. Writing here closes that window (dev plan §3.5.2 Change 3).
            //
            // Failure to write the witness is logged at Warning but does NOT fail the
            // RegisterTailAsync call: the brain row is committed, the registry has the
            // provider, and the next session-begin handshake will reseed the witness as part
            // of its normal read-then-write algorithm.
            string volumeId;
            long currentEpoch;
            VolumeState currentState;
            using (var brainScope = await _context.Brain.LockAsync(ct).ConfigureAwait(false))
            {
                volumeId = await brainScope.Connection.QuerySingleAsync<string>(
                    new CommandDefinition(
                        "SELECT Value FROM Settings WHERE Key = 'VolumeID'",
                        cancellationToken: ct)).ConfigureAwait(false);
                var epochRaw = await brainScope.Connection.QuerySingleAsync<string>(
                    new CommandDefinition(
                        "SELECT Value FROM Settings WHERE Key = 'VolumeEpoch'",
                        cancellationToken: ct)).ConfigureAwait(false);
                currentEpoch = long.Parse(epochRaw, CultureInfo.InvariantCulture);
                var stateRaw = await brainScope.Connection.QuerySingleOrDefaultAsync<string?>(
                    new CommandDefinition(
                        "SELECT Value FROM Settings WHERE Key = 'VolumeState'",
                        cancellationToken: ct)).ConfigureAwait(false);
                currentState = stateRaw is not null
                    && Enum.TryParse<VolumeState>(stateRaw, ignoreCase: false, out var parsed)
                        ? parsed
                        : VolumeState.Normal;
            }

            var initialWitness = WitnessPayload.ForNewSession(
                volumeId, currentEpoch, GetAppInformationalVersion())
                with
            { ConflictObserved = currentState == VolumeState.Fenced };
            var writeResult = await _witnessStore.WriteAsync(
                provider,
                _session.Dek,
                initialWitness,
                CancellationToken.None).ConfigureAwait(false);
            if (!writeResult.Success)
            {
                _logger.LogWarning(
                    "Initial witness write to newly-registered tail {ProviderId} failed ({Code}); the next session-begin handshake will retry.",
                    providerId, writeResult.Error!.Code);
            }

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

    // ── Conflict resolution ──────────────────────────────────────────────────

    /// <summary>
    /// Resolves a split-brain conflict by declaring this skink canonical. Clears the fenced
    /// state (<c>Settings["VolumeState"] = "Normal"</c>), writes a fresh witness with
    /// <c>ConflictObserved = false</c> to every accessible tail, unfences
    /// <see cref="UploadQueueService"/> so Phase 2 uploads resume, and pulses the wakeup
    /// signal so workers exit their fenced-idle wait promptly. Idempotent — safe to call on
    /// an unfenced volume (returns <see cref="Result.Ok"/> immediately).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The demoted skink (the one not promoted) retains its <c>Settings["VolumeState"] = "Fenced"</c>
    /// and will see <c>ConflictObserved = false</c> in the fresh witness on its next session
    /// open. A fenced volume that opens and reads <c>ConflictObserved = false</c> with an
    /// epoch it recognises as ahead of its own knows the winner has promoted: the demoted
    /// skink should display a user message recommending recovery via a tail rather than
    /// attempting to promote itself (this guidance is surfaced as a notification; the volume
    /// still works for Phase 1 and reads).
    /// </para>
    /// <para>
    /// <strong>Idempotency vs. stale-tail repair.</strong> An offline tail at the time of a
    /// prior promote retains its old <c>ConflictObserved = true</c> witness.
    /// <see cref="PromoteAsync"/> does NOT re-attempt the write — that repair is performed
    /// automatically by the next <see cref="OpenAsync"/>'s handshake: with
    /// <c>alreadyFenced=false</c> post-promote, the handshake writes a fresh
    /// <c>ConflictObserved = false</c> witness to every accessible tail (including the
    /// previously-offline one if it is now reachable). This keeps <see cref="PromoteAsync"/>
    /// cheap and lets the recurring session-open handshake act as the eventual-consistency
    /// repair loop.
    /// </para>
    /// <para>
    /// <strong>Crash-safety.</strong> The sequence is: write fresh witnesses to all accessible
    /// tails, then upsert <c>Settings["VolumeState"] = "Normal"</c>, then flip the in-memory
    /// flag, then notify. If the process crashes after the witness writes succeed but before
    /// the brain write, the next open observes brain-says-Fenced but witnesses-say-clean and
    /// auto-downgrades (dev plan §3.5.2's <c>RunWitnessHandshakeAsync</c> transition table;
    /// Blueprint §19.7). No brain-side journal is required. (Blueprint §19.8.)
    /// </para>
    /// </remarks>
    public async Task<Result> PromoteAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "Promote cancelled.", ex);
        }
        ThrowIfDisposedAndReleaseGate();

        try
        {
            // Idempotent fast-path. Already-Normal volumes return immediately — no log, no
            // notification, no tail writes, no brain write. The second invocation in a
            // PromoteAsync_IsIdempotent test hits this path.
            if (State == VolumeState.Normal)
            {
                return Result.Ok();
            }

            _logger.LogInformation(
                "User-initiated promote — clearing fenced state and writing fresh witnesses to all accessible tails.");

            // ── Read VolumeID, VolumeEpoch, and active providers from the brain ─
            string volumeId;
            long currentEpoch;
            IReadOnlyList<string> activeProviderIds;
            using (var scope = await _session.Brain!.LockAsync(ct).ConfigureAwait(false))
            {
                volumeId = await scope.Connection.QuerySingleAsync<string>(
                    new CommandDefinition(
                        "SELECT Value FROM Settings WHERE Key = 'VolumeID'",
                        cancellationToken: ct)).ConfigureAwait(false);
                var epochRaw = await scope.Connection.QuerySingleAsync<string>(
                    new CommandDefinition(
                        "SELECT Value FROM Settings WHERE Key = 'VolumeEpoch'",
                        cancellationToken: ct)).ConfigureAwait(false);
                currentEpoch = long.Parse(epochRaw, CultureInfo.InvariantCulture);
                var ids = await scope.Connection.QueryAsync<string>(
                    new CommandDefinition(
                        "SELECT ProviderID FROM Providers WHERE IsActive = 1",
                        cancellationToken: ct)).ConfigureAwait(false);
                activeProviderIds = ids.AsList();
            }

            // ── Build the fresh witness payload (ConflictObserved = false by default) ─
            var payload = WitnessPayload.ForNewSession(
                volumeId, currentEpoch, GetAppInformationalVersion());

            // ── Write the fresh witness to every accessible tail ────────────────
            // Principle 17: every write site below uses CancellationToken.None as a literal —
            // once we have decided to clear the fence, cancellation mid-promote-write would
            // leave half the tails carrying ConflictObserved=true and the other half
            // carrying ConflictObserved=false, defeating the resolution-on-next-handshake
            // guarantee for the demoted skink. A per-tail failure is logged at Warning and
            // does NOT fail the overall operation — the next session-begin handshake retries
            // as part of its normal read-then-write loop (eventual-consistency repair).
            foreach (var providerId in activeProviderIds)
            {
                var resolved = await _providerRegistry.GetAsync(providerId, ct).ConfigureAwait(false);
                if (!resolved.Success)
                {
                    _logger.LogDebug(
                        "Provider {ProviderId} not resolvable from registry ({Code}); skipping in promote.",
                        providerId, resolved.Error!.Code);
                    continue;
                }

                var writeResult = await _witnessStore.WriteAsync(
                    resolved.Value!, _session.Dek, payload, CancellationToken.None).ConfigureAwait(false);
                if (!writeResult.Success)
                {
                    _logger.LogWarning(
                        "Witness write to tail {ProviderId} during promote failed ({Code}); the next session-begin handshake will retry.",
                        providerId, writeResult.Error!.Code);
                }
            }

            // ── Persist Settings["VolumeState"] = "Normal" ──────────────────────
            // Reuses the §3.5.2 helper which already runs the upsert under
            // CancellationToken.None (Principle 17). Identical brain-write semantics to the
            // auto-downgrade path means the two terminal states are byte-equivalent.
            await PersistVolumeStateAsync(_session.Brain!, VolumeState.Normal).ConfigureAwait(false);

            // ── Unfence the upload queue + wake workers ─────────────────────────
            _uploadQueueService.SetFenced(false);
            State = VolumeState.Normal;
            _wakeupSignal.Pulse();

            // ── Publish the resolution notification (Principle 25 vocabulary) ───
            await _context.NotificationBus.PublishAsync(new Notification
            {
                Source = nameof(FlashSkinkVolume),
                Severity = NotificationSeverity.Info,
                Title = "Conflict resolved",
                Message = "FlashSkink will now resume uploading to your tails.",
                OccurredUtc = _clock.UtcNow,
                RequiresUserAction = false,
            }, CancellationToken.None).ConfigureAwait(false);

            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "Promote cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            return Result.Fail(ErrorCode.DatabaseWriteFailed,
                "Failed to persist volume state during promote.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error during promote.");
            return Result.Fail(ErrorCode.Unknown, "Unexpected error during promote.", ex);
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

            // Step 5 — release the single-instance lock LAST. Holding the lock past
            // session teardown means a concurrent second-instance launch sees
            // SingleInstanceLockHeld for the entire duration of our shutdown, not just
            // up to the point where the brain connection closes. This preserves the
            // "two processes never both think they own the volume" invariant across the
            // full dispose interval. (Blueprint §19.5, Principle 35.)
            await _instanceLock.DisposeAsync().ConfigureAwait(false);

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

    /// <summary>
    /// Re-verifies <see cref="_disposed"/> after the caller has acquired <see cref="_gate"/>.
    /// If a concurrent <see cref="DisposeAsync"/> ran completely between the public method's
    /// initial <see cref="ThrowIfDisposed"/> and its <c>await _gate.WaitAsync(ct)</c>, the
    /// caller would wake on a healthy gate (which is intentionally not disposed — see
    /// <see cref="DisposeAsync"/>) but touch <see cref="_context"/>, <see cref="_session"/>,
    /// <see cref="_writePipeline"/>, etc. — all of which DisposeAsync has just torn down.
    /// This helper releases the gate and throws so the work body never observes that state.
    /// Call from every gated public method, immediately after the <c>await _gate.WaitAsync</c>
    /// (or its <c>catch (OperationCanceledException)</c> branch returns) and before any field
    /// access.
    /// </summary>
    private void ThrowIfDisposedAndReleaseGate()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            _gate.Release();
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
        string vaultPath,
        InstanceLock instanceLock,
        VolumeState initialState)
    {
        var loggerFactory = options.LoggerFactory;
        var notificationBus = options.NotificationBus;
        var brain = session.Brain!;
        var dek = new ReadOnlyMemory<byte>(session.Dek);
        bool initiallyFenced = initialState == VolumeState.Fenced;

        var wal = new WalRepository(brain, loggerFactory.CreateLogger<WalRepository>());
        var blobs = new BlobRepository(brain, loggerFactory.CreateLogger<BlobRepository>());
        var files = new FileRepository(brain, wal, loggerFactory.CreateLogger<FileRepository>());
        var activityLog = new ActivityLogRepository(brain, loggerFactory.CreateLogger<ActivityLogRepository>());
        var blobWriter = new AtomicBlobWriter(loggerFactory.CreateLogger<AtomicBlobWriter>());
        var crypto = new CryptoPipeline();
        var compression = new CompressionService();
        var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var context = new VolumeContext(
            brain, dek, skinkRoot, sha256, crypto, compression,
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
            brain, loggerFactory.CreateLogger<UploadQueueRepository>());
        var retryPolicy = new RetryPolicy();
        var rangeUploader = new RangeUploader(
            uploadQueueRepo, clock, retryPolicy,
            loggerFactory.CreateLogger<RangeUploader>());

        var wakeupSignal = new UploadWakeupSignal();

        var uploadQueueService = new UploadQueueService(
            uploadQueueRepo, blobs, files, activityLog,
            registry, netMonitor, notificationBus,
            rangeUploader, retryPolicy, clock, wakeupSignal,
            brain, skinkRoot, initiallyFenced,
            loggerFactory.CreateLogger<UploadQueueService>());

        var brainMirrorService = new BrainMirrorService(
            brain, dek, skinkRoot, registry,
            notificationBus, clock,
            loggerFactory.CreateLogger<BrainMirrorService>());

        // Witness store + volume-level logger are kept as fields on FlashSkinkVolume so
        // PromoteAsync (§3.5.3) can drive fresh witness writes without rebuilding services,
        // and so RegisterTailAsync can write the initial witness on tail registration
        // (dev plan §3.5.2 Change 5).
        var witnessStore = new WitnessStore(loggerFactory.CreateLogger<WitnessStore>());
        var volumeLogger = loggerFactory.CreateLogger<FlashSkinkVolume>();

        var volumeCts = new CancellationTokenSource();

        var queueStartResult = uploadQueueService.Start(volumeCts.Token);
        if (!queueStartResult.Success)
        {
            await uploadQueueService.DisposeAsync().ConfigureAwait(false);
            await brainMirrorService.DisposeAsync().ConfigureAwait(false);
            volumeCts.Dispose();
            context.Dispose();
            await session.DisposeAsync().ConfigureAwait(false);
            await instanceLock.DisposeAsync().ConfigureAwait(false);
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
            await instanceLock.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Brain mirror service failed to start: {mirrorStartResult.Error!.Code}");
        }

        return new FlashSkinkVolume(
            session, context, writePipeline, readPipeline, lifecycle, keyVault, vaultPath,
            registry, netMonitor, clock, wakeupSignal, uploadQueueService, brainMirrorService,
            volumeCts, instanceLock, witnessStore, initialState, volumeLogger);
    }

    /// <summary>
    /// Writes the initial <c>Settings</c> rows at volume creation: grace period, audit
    /// interval, creation timestamp, the random <c>VolumeID</c> GUID, and three app-version
    /// rows (<c>AppVersionCreatedWith</c>, <c>AppVersionLastOpened</c>,
    /// <c>AppVersionLastOpenedUtc</c>). <c>VolumeID</c> is generated via
    /// <see cref="Guid.NewGuid"/> and is intentionally independent of any cryptographic key
    /// material — two skinks initialised from the same recovery phrase are distinct volumes
    /// (blueprint §31). App-version values come from
    /// <see cref="AssemblyInformationalVersionAttribute"/> (MinVer-stamped at build time).
    /// </summary>
    private static async Task<Result> SeedInitialSettingsAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            const string upsert = "INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@Key, @Value)";
            var appVersion = GetAppInformationalVersion();
            var nowUtc = DateTime.UtcNow.ToString("O");
            var volumeId = Guid.NewGuid().ToString("D");

            // Wrap all seven upserts in a single transaction so a SqliteException or
            // cancellation mid-sequence leaves the brain unchanged. CreateAsync's failure
            // path deletes the brain file anyway, but the transaction closes the narrow
            // partial-seed window and matches the pattern used in BackfillAndStampOnOpenAsync.
            using var tx = connection.BeginTransaction();

            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "GracePeriodDays", Value = "30" }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "AuditIntervalHours", Value = "168" }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "VolumeCreatedUtc", Value = nowUtc }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "VolumeID", Value = volumeId }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            // VolumeEpoch — monotonically-increasing per-session counter for the witness
            // protocol (dev plan §3.5.1, Blueprint §19.6). Seeded directly at "1" rather
            // than "0"-then-incremented because CreateAsync does not call
            // BackfillAndStampOnOpenAsync (which is what would do the increment). The
            // first session ever IS the create session, so it gets epoch 1.
            // VolumeState is intentionally NOT seeded — its absence is interpreted as
            // VolumeState.Normal by BackfillAndStampOnOpenAsync, keeping this seed step
            // minimal. §3.5.2's handshake owns writes to Settings["VolumeState"].
            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "VolumeEpoch", Value = "1" }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "AppVersionCreatedWith", Value = appVersion }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "AppVersionLastOpened", Value = appVersion }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(upsert, new { Key = "AppVersionLastOpenedUtc", Value = nowUtc }, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);

            // If Commit throws, SqliteTransaction.Dispose() rolls back the underlying
            // SQLite transaction during stack unwinding; the original exception is caught
            // below and returned as Result.Fail with the brain in its pre-transaction state.
            tx.Commit();
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

    /// <summary>
    /// Runs the session-begin witness handshake against every reachable tail and applies the
    /// fenced-state transition table. Called by <see cref="OpenAsync"/> after
    /// <see cref="BackfillAndStampOnOpenAsync"/> has incremented the epoch and before
    /// <see cref="BuildVolumeFromSessionAsync"/> constructs the background services. The
    /// returned <see cref="VolumeState"/> is the post-handshake state — possibly transitioned
    /// from the <paramref name="currentState"/> argument (Normal → Fenced on a fresh conflict,
    /// or Fenced → Normal on a successful auto-downgrade). (Blueprint §19.6–19.7.)
    /// </summary>
    private static async Task<Result<VolumeState>> RunWitnessHandshakeAsync(
        VolumeSession session,
        VolumeCreationOptions options,
        long newEpoch,
        VolumeState currentState,
        CancellationToken ct)
    {
        var logger = options.LoggerFactory.CreateLogger(typeof(FlashSkinkVolume));
        // Mirror BuildVolumeFromSessionAsync's IClock fallback so tests that pass a FakeClock
        // see deterministic timestamps on notifications and BackgroundFailures rows.
        var clock = options.Clock ?? SystemClock.Instance;
        try
        {
            ct.ThrowIfCancellationRequested();

            // Mirror the BuildVolumeFromSessionAsync registry fallback: a brand-new volume
            // with no registered tails opens against an empty registry, the handshake sees
            // no tails, and returns no-conflict. In production Phase 4 a BrainBackedProviderRegistry
            // populates from the brain; in Phase 3 tests an InMemoryProviderRegistry is passed
            // via options (cross-cutting decision 8).
            var registry = options.ProviderRegistry
                ?? new InMemoryProviderRegistry(
                    options.LoggerFactory.CreateLogger<InMemoryProviderRegistry>());

            // ── Read VolumeID and active providers from the brain ────────────
            string volumeId;
            IReadOnlyList<string> activeProviderIds;
            using (var scope = await session.Brain!.LockAsync(ct).ConfigureAwait(false))
            {
                volumeId = await scope.Connection.QuerySingleAsync<string>(
                    new CommandDefinition(
                        "SELECT Value FROM Settings WHERE Key = 'VolumeID'",
                        cancellationToken: ct)).ConfigureAwait(false);
                var ids = await scope.Connection.QueryAsync<string>(
                    new CommandDefinition(
                        "SELECT ProviderID FROM Providers WHERE IsActive = 1",
                        cancellationToken: ct)).ConfigureAwait(false);
                activeProviderIds = ids.AsList();
            }

            // ── Resolve providers from registry ──────────────────────────────
            var tails = new List<(string ProviderId, IStorageProvider Provider)>(activeProviderIds.Count);
            foreach (var providerId in activeProviderIds)
            {
                var providerResult = await registry.GetAsync(providerId, ct).ConfigureAwait(false);
                if (!providerResult.Success)
                {
                    // Normal in tests that haven't called RegisterTailAsync yet; normal in
                    // Phase 4 if a brain-backed registry fails to reconstruct a cloud adapter.
                    logger.LogDebug(
                        "Provider {ProviderId} present in brain but not resolvable from registry ({Code}); skipping in handshake.",
                        providerId, providerResult.Error!.Code);
                    continue;
                }
                tails.Add((providerId, providerResult.Value!));
            }

            // ── Run handshake ────────────────────────────────────────────────
            var store = new WitnessStore(options.LoggerFactory.CreateLogger<WitnessStore>());
            var handshake = new WitnessHandshake(
                store, options.LoggerFactory.CreateLogger<WitnessHandshake>());
            var dek = new ReadOnlyMemory<byte>(session.Dek);
            var outcomeResult = await handshake.RunAsync(
                tails, volumeId, newEpoch, GetAppInformationalVersion(), dek,
                alreadyFenced: currentState == VolumeState.Fenced, ct).ConfigureAwait(false);
            if (!outcomeResult.Success)
            {
                return Result<VolumeState>.Fail(outcomeResult.Error!);
            }
            var outcome = outcomeResult.Value;

            // ── Transition table (Blueprint §19.7) ───────────────────────────
            if (outcome.ConflictDetected && currentState == VolumeState.Normal)
            {
                // Fresh detection — enter Fenced. Persist, notify, write BackgroundFailures.
                await PersistVolumeStateAsync(session.Brain!, VolumeState.Fenced).ConfigureAwait(false);

                logger.LogError(
                    "Split-brain detected on tail {ProviderId} via {Trigger}; volume entering fenced state. WitnessEpoch={WitnessEpoch}, LocalEpoch={LocalEpoch}.",
                    outcome.ConflictingProviderId,
                    outcome.Trigger,
                    outcome.ConflictWitness?.Epoch,
                    newEpoch);

                var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["TailProviderID"] = outcome.ConflictingProviderId ?? string.Empty,
                    ["ConflictTrigger"] = outcome.Trigger.ToString(),
                    ["WitnessEpoch"] = outcome.ConflictWitness?.Epoch.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    ["LocalEpoch"] = newEpoch.ToString(CultureInfo.InvariantCulture),
                    ["WitnessHost"] = outcome.ConflictWitness?.Host ?? string.Empty,
                    ["WitnessSessionId"] = outcome.ConflictWitness?.SessionId ?? string.Empty,
                    ["ConflictObserved"] = outcome.ConflictWitness?.ConflictObserved.ToString() ?? string.Empty,
                };
                const string fencedUserMessage =
                    "FlashSkink found a conflicting copy of this skink on one of your tails. Uploads are paused until you resolve the conflict.";
                await options.NotificationBus.PublishAsync(new Notification
                {
                    Source = nameof(FlashSkinkVolume),
                    Severity = NotificationSeverity.Critical,
                    Title = "Conflicting copy detected",
                    Message = fencedUserMessage,
                    Error = new ErrorContext
                    {
                        Code = ErrorCode.SplitBrainDetected,
                        Message = outcome.ConflictWitness is { } cw
                            ? $"Conflict detected on tail '{outcome.ConflictingProviderId}'. {cw.ToDisplayString()}"
                            : $"Conflict detected on tail '{outcome.ConflictingProviderId}'.",
                        Metadata = metadata,
                    },
                    OccurredUtc = clock.UtcNow,
                    RequiresUserAction = true,
                }, CancellationToken.None).ConfigureAwait(false);

                var backgroundFailures = new BackgroundFailureRepository(
                    session.Brain!,
                    options.LoggerFactory.CreateLogger<BackgroundFailureRepository>());
                var bgResult = await backgroundFailures.AppendAsync(new BackgroundFailure
                {
                    FailureId = Guid.NewGuid().ToString(),
                    OccurredUtc = clock.UtcNow,
                    Source = nameof(FlashSkinkVolume),
                    ErrorCode = nameof(ErrorCode.SplitBrainDetected),
                    Message = fencedUserMessage,
                    Metadata = null,
                    Acknowledged = false,
                }, CancellationToken.None).ConfigureAwait(false);
                if (!bgResult.Success)
                {
                    // The notification was published; the persisted-failure write failing is
                    // unfortunate but not fatal — log and continue.
                    logger.LogWarning(
                        "Failed to persist BackgroundFailures row for split-brain detection: {Code}.",
                        bgResult.Error!.Code);
                }

                return Result<VolumeState>.Ok(VolumeState.Fenced);
            }

            if (outcome.ConflictDetected && currentState == VolumeState.Fenced)
            {
                // Already fenced; another fresh trigger observed. No state change, no spam
                // (Principle 24 — initial detection only). Diagnostic log.
                logger.LogWarning(
                    "Volume already fenced; handshake observed another fresh trigger on tail {ProviderId} via {Trigger}.",
                    outcome.ConflictingProviderId, outcome.Trigger);
                return Result<VolumeState>.Ok(VolumeState.Fenced);
            }

            if (!outcome.ConflictDetected && currentState == VolumeState.Fenced)
            {
                // Auto-downgrade check: positive evidence required (TailsRead > 0).
                if (outcome.TailsRead > 0)
                {
                    await PersistVolumeStateAsync(session.Brain!, VolumeState.Normal).ConfigureAwait(false);
                    logger.LogInformation(
                        "Auto-downgraded stale Fenced state — handshake observed {TailsRead} tail(s) with no conflict signal. A prior promote likely completed the tail writes before persisting the local state change.",
                        outcome.TailsRead);
                    await options.NotificationBus.PublishAsync(new Notification
                    {
                        Source = nameof(FlashSkinkVolume),
                        Severity = NotificationSeverity.Info,
                        Title = "Conflict resolved",
                        Message = "FlashSkink finished resolving a prior conflict; uploads are resuming.",
                        OccurredUtc = clock.UtcNow,
                        RequiresUserAction = false,
                    }, CancellationToken.None).ConfigureAwait(false);
                    return Result<VolumeState>.Ok(VolumeState.Normal);
                }

                // No reachable tails to confirm against — keep Fenced. Absence of evidence
                // is not evidence of absence.
                logger.LogDebug(
                    "Volume remains fenced; no reachable tails confirm the prior promote completed.");
                return Result<VolumeState>.Ok(VolumeState.Fenced);
            }

            // (No fresh conflict, currently Normal) — ordinary path, no change.
            return Result<VolumeState>.Ok(VolumeState.Normal);
        }
        catch (OperationCanceledException ex)
        {
            return Result<VolumeState>.Fail(
                ErrorCode.Cancelled, "Witness handshake was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            return Result<VolumeState>.Fail(
                ErrorCode.DatabaseWriteFailed,
                "Failed to persist volume state during witness handshake.", ex);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected error during witness handshake.");
            return Result<VolumeState>.Fail(
                ErrorCode.Unknown, "Unexpected error during witness handshake.", ex);
        }
    }

    /// <summary>
    /// Upserts <c>Settings["VolumeState"]</c> with the supplied state name. Uses
    /// <see cref="CancellationToken.None"/> per Principle 17 — once the handshake decides to
    /// transition, the persist must complete regardless of concurrent shutdown.
    /// </summary>
    private static async Task PersistVolumeStateAsync(IBrainAccess brain, VolumeState state)
    {
        using var scope = await brain.LockAsync(CancellationToken.None).ConfigureAwait(false);
        await scope.Connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO Settings (Key, Value) VALUES ('VolumeState', @Value)",
            new { Value = state.ToString() },
            cancellationToken: CancellationToken.None)).ConfigureAwait(false);
    }

    /// <summary>
    /// Idempotent post-open maintenance: writes the random <c>VolumeID</c> GUID if missing
    /// from a legacy brain, migrates the legacy <c>AppVersion</c> key to
    /// <c>AppVersionCreatedWith</c> if present, always overwrites <c>AppVersionLastOpened</c>
    /// and <c>AppVersionLastOpenedUtc</c> with the current app version and wall-clock
    /// time, reads <c>VolumeState</c> (defaulting to <see cref="VolumeState.Normal"/> for
    /// absent or unparseable rows), and increments <c>VolumeEpoch</c> by exactly one
    /// (treating an absent row as <c>0</c>, so the next-written value becomes <c>1</c>).
    /// All operations run in a single transaction so a failure mid-flight leaves the brain
    /// unchanged (blueprint §31, CLAUDE.md Principles 33 / 34; dev plan §3.5.1 adds the
    /// epoch increment and state read). Safe to call on a brand-new brain just seeded by
    /// <see cref="SeedInitialSettingsAsync"/>: the backfills are no-ops; only the
    /// last-opened stamps refresh (the seed and the open happen at the same moment, so
    /// the refresh writes the same values) and the epoch increments past the seed value.
    /// </summary>
    /// <returns>
    /// A tuple carrying the current <see cref="VolumeState"/> read from the brain and the
    /// new <c>VolumeEpoch</c> value just written. The <see cref="VolumeState"/> is the
    /// pre-handshake state; §3.5.2 may transition it to <see cref="VolumeState.Fenced"/>
    /// based on the handshake outcome and re-persist the row.
    /// </returns>
    private static async Task<Result<(VolumeState State, long NewEpoch)>> BackfillAndStampOnOpenAsync(
        IBrainAccess brain,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var nowUtc = DateTime.UtcNow.ToString("O");
            var appVersion = GetAppInformationalVersion();
            const string upsert = "INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@Key, @Value)";

            // Hold the scope across the full backfill transaction (Principle 36). Runs during
            // volume open, before any background service starts — no other thread can contend
            // for the gate, so this is effectively single-threaded as before.
            using var brainScope = await brain.LockAsync(ct).ConfigureAwait(false);
            var connection = brainScope.Connection;
            using var tx = connection.BeginTransaction();

            // 1. Backfill VolumeID if a legacy brain lacks it.
            var existingVolumeId = await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(
                    "SELECT Value FROM Settings WHERE Key = 'VolumeID'",
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            if (existingVolumeId is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO Settings (Key, Value) VALUES ('VolumeID', @Value)",
                    new { Value = Guid.NewGuid().ToString("D") },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            }

            // 2. Backfill AppVersionCreatedWith from the legacy AppVersion key if present.
            //    If both are missing (hand-edited or partially-corrupt brain), seed a
            //    distinguishable sentinel so audit logs can tell "we don't know" apart
            //    from "we don't know specifically because MinVer wasn't wired" ("0.0.0-unknown").
            var existingCreatedWith = await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(
                    "SELECT Value FROM Settings WHERE Key = 'AppVersionCreatedWith'",
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            if (existingCreatedWith is null)
            {
                var legacyAppVersion = await connection.QuerySingleOrDefaultAsync<string?>(
                    new CommandDefinition(
                        "SELECT Value FROM Settings WHERE Key = 'AppVersion'",
                        transaction: tx,
                        cancellationToken: ct)).ConfigureAwait(false);
                var seedValue = legacyAppVersion ?? "unknown-legacy";
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO Settings (Key, Value) VALUES ('AppVersionCreatedWith', @Value)",
                    new { Value = seedValue },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
                if (legacyAppVersion is not null)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM Settings WHERE Key = 'AppVersion'",
                        transaction: tx,
                        cancellationToken: ct)).ConfigureAwait(false);
                }
            }

            // 3. Always overwrite the open-time stamps so they reflect the current opener.
            await connection.ExecuteAsync(new CommandDefinition(upsert,
                new { Key = "AppVersionLastOpened", Value = appVersion },
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(upsert,
                new { Key = "AppVersionLastOpenedUtc", Value = nowUtc },
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false);

            // 4. Read VolumeState (dev plan §3.5.1; Blueprint §19.7 — added §3.5.2). An
            //    absent row is interpreted as Normal — the default. A row that cannot be
            //    parsed as a VolumeState (corruption, hand-edit, future enum value) is ALSO
            //    treated as Normal so a bad row never blocks open. The §3.5.2 handshake
            //    owns writes to this row; this step only reads.
            var volumeStateRaw = await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(
                    "SELECT Value FROM Settings WHERE Key = 'VolumeState'",
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            var volumeState = volumeStateRaw is not null
                && Enum.TryParse<VolumeState>(volumeStateRaw, ignoreCase: false, out var parsedState)
                    ? parsedState
                    : VolumeState.Normal;

            // 5. Increment VolumeEpoch (dev plan §3.5.1). Absent row → 0 → write 1. A
            //    non-parseable value is a HARD error here, not silently treated as 0:
            //    the epoch IS the conflict-detection signal, and zeroing a corrupted
            //    value would mask divergence. The strict parse is documented in dev
            //    plan §3.5.1 and tested by VolumeEpochTests.OpenAsync_GarbageEpochValue.
            var currentEpochRaw = await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(
                    "SELECT Value FROM Settings WHERE Key = 'VolumeEpoch'",
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            long currentEpoch;
            if (currentEpochRaw is null)
            {
                currentEpoch = 0L;
            }
            else if (!long.TryParse(
                currentEpochRaw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out currentEpoch))
            {
                return Result<(VolumeState, long)>.Fail(
                    ErrorCode.DatabaseReadFailed,
                    $"Settings['VolumeEpoch'] contains a non-numeric value: '{currentEpochRaw}'.");
            }
            var newEpoch = currentEpoch + 1L;
            await connection.ExecuteAsync(new CommandDefinition(upsert,
                new
                {
                    Key = "VolumeEpoch",
                    Value = newEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false);

            // If Commit throws, SqliteTransaction.Dispose() rolls back the underlying
            // SQLite transaction during stack unwinding; the original exception is caught
            // below and returned as Result.Fail with the brain in its pre-transaction state.
            tx.Commit();
            return Result<(VolumeState, long)>.Ok((volumeState, newEpoch));
        }
        catch (OperationCanceledException ex)
        {
            return Result<(VolumeState, long)>.Fail(
                ErrorCode.Cancelled, "Backfill-and-stamp on open was cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            return Result<(VolumeState, long)>.Fail(
                ErrorCode.DatabaseWriteFailed,
                $"Failed to backfill and stamp settings on open. SqliteErrorCode={ex.SqliteErrorCode}.", ex);
        }
        catch (Exception ex)
        {
            return Result<(VolumeState, long)>.Fail(
                ErrorCode.Unknown,
                "Unexpected error during backfill-and-stamp on open.", ex);
        }
    }

    /// <summary>
    /// Returns the MinVer-stamped informational version of the
    /// <c>FlashSkink.Core</c> assembly (a SemVer string with optional commit suffix),
    /// or the sentinel <c>"0.0.0-unknown"</c> when the attribute is missing. Never throws.
    /// </summary>
    private static string GetAppInformationalVersion()
        => typeof(FlashSkinkVolume).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
               .InformationalVersion
           ?? "0.0.0-unknown";

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
