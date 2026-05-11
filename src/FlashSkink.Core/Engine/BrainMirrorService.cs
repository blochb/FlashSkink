using System.Globalization;
using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Abstractions.Time;
using FlashSkink.Core.Storage;
using FlashSkink.Core.Upload;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Engine;

/// <summary>
/// Per-volume background service that mirrors the encrypted brain database to every active tail
/// on commit (debounced 10 s), every <see cref="UploadConstants.BrainMirrorIntervalMinutes"/>
/// minutes (timer), and on clean shutdown. Snapshot is produced via SQLite's online backup API,
/// encrypted with AES-256-GCM using the volume's DEK + a 16-byte authenticated header
/// (<see cref="BrainMirrorHeader"/>), and uploaded as
/// <c>_brain/{timestampUtc:yyyyMMddTHHmmssZ}.bin</c>. Each tail retains the 3 most recent mirrors;
/// older entries are pruned best-effort after each upload. Blueprint §16.7.
/// </summary>
/// <remarks>
/// <para>
/// Ownership: <c>brainConnection</c> and <c>dek</c> are <em>borrowed</em> from the volume —
/// not disposed here. The service owns its own <see cref="CancellationTokenSource"/>,
/// <see cref="SemaphoreSlim"/>, and <see cref="UploadWakeupSignal"/>.
/// </para>
/// <para>
/// Cross-tail isolation: a failure on tail A never prevents the mirror landing on tail B —
/// each provider is processed inside an isolating try/catch in <see cref="RunOneCycleAsync"/>.
/// </para>
/// </remarks>
public sealed class BrainMirrorService : IAsyncDisposable
{
    private const int DebounceWindowSeconds = 10;
    private const long DefaultMaxInMemoryMirrorBytes = 1L * 1024 * 1024 * 1024; // 1 GiB
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly SqliteConnection _brain;
    private readonly ReadOnlyMemory<byte> _dek;
    private readonly string _skinkRoot;
    private readonly IProviderRegistry _registry;
    private readonly INotificationBus _bus;
    private readonly IClock _clock;
    private readonly ILogger<BrainMirrorService> _logger;
    private readonly long _maxInMemoryMirrorBytes;

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly UploadWakeupSignal _debouncePulse = new();
    private long _lastCommitTicks;

    private CancellationTokenSource? _serviceCts;
    private Task? _timerTask;
    private Task? _debounceTask;
    private int _started;
    private int _disposed;

    /// <summary>
    /// Constructs a <see cref="BrainMirrorService"/>. The caller retains ownership of every
    /// reference parameter — none are disposed here.
    /// </summary>
    public BrainMirrorService(
        SqliteConnection brainConnection,
        ReadOnlyMemory<byte> dek,
        string skinkRoot,
        IProviderRegistry providerRegistry,
        INotificationBus notificationBus,
        IClock clock,
        ILogger<BrainMirrorService> logger)
        : this(brainConnection, dek, skinkRoot, providerRegistry, notificationBus, clock, logger,
               DefaultMaxInMemoryMirrorBytes)
    {
    }

    /// <summary>
    /// Test-only constructor exposing the in-memory mirror size cap. Production code uses the
    /// public constructor which defaults the cap to 1 GiB.
    /// </summary>
    internal BrainMirrorService(
        SqliteConnection brainConnection,
        ReadOnlyMemory<byte> dek,
        string skinkRoot,
        IProviderRegistry providerRegistry,
        INotificationBus notificationBus,
        IClock clock,
        ILogger<BrainMirrorService> logger,
        long maxInMemoryMirrorBytes)
    {
        _brain = brainConnection;
        _dek = dek;
        _skinkRoot = skinkRoot;
        _registry = providerRegistry;
        _bus = notificationBus;
        _clock = clock;
        _logger = logger;
        _maxInMemoryMirrorBytes = maxInMemoryMirrorBytes;
    }

    /// <summary>
    /// Enrols the 15-minute timer and the debounce loop linked to <paramref name="volumeToken"/>.
    /// Idempotent: a second call returns <see cref="Result.Ok"/> without effect.
    /// Returns <see cref="ErrorCode.ObjectDisposed"/> when called after <see cref="DisposeAsync"/>.
    /// </summary>
    public Result Start(CancellationToken volumeToken)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _logger.LogWarning("Start called on a disposed BrainMirrorService.");
                return Result.Fail(ErrorCode.ObjectDisposed,
                    "Brain mirror service has been disposed.");
            }

            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                return Result.Ok();
            }

            _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(volumeToken);
            var ct = _serviceCts.Token;

            _timerTask = Task.Factory.StartNew(
                () => TimerLoopAsync(ct),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();

            _debounceTask = Task.Factory.StartNew(
                () => DebounceLoopAsync(ct),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();

            _logger.LogInformation("BrainMirrorService started.");
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BrainMirrorService.Start failed.");
            return Result.Fail(ErrorCode.Unknown, "Could not start brain mirror service.", ex);
        }
    }

    /// <summary>
    /// Notifies the service that a <c>WritePipeline</c> commit just landed. Resets the
    /// 10-second debounce window; when the window elapses without further commits, one
    /// mirror cycle runs. Safe to call from any thread; never blocks; never throws.
    /// No-op when not started or already disposed.
    /// </summary>
    public void NotifyWriteCommitted()
    {
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastCommitTicks, _clock.UtcNow.Ticks);
        _debouncePulse.Pulse();
    }

    /// <summary>
    /// Runs one mirror cycle synchronously and waits for completion. Serializes with any other
    /// in-flight cycle on the internal run lock. Used by <see cref="DisposeAsync"/> for the
    /// final shutdown mirror; also exposed for tests.
    /// </summary>
    public async ValueTask<Result> TriggerMirrorAsync(CancellationToken ct)
    {
        try
        {
            await _runLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "Brain mirror trigger cancelled.", ex);
        }
        catch (ObjectDisposedException ex)
        {
            return Result.Fail(ErrorCode.ObjectDisposed,
                "Brain mirror service has been disposed.", ex);
        }

        try
        {
            return await RunOneCycleAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _runLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposal already released; ignore.
            }
        }
    }

    /// <summary>
    /// Cancels background tasks, runs one final mirror cycle with <see cref="CancellationToken.None"/>
    /// (Principle 17 — compensation must not be cancellable), awaits the background tasks
    /// (5 s budget each), and disposes internal state. Idempotent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _serviceCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed; ignore.
        }

        // Final mirror — Principle 17, run under CancellationToken.None.
        if (Volatile.Read(ref _started) != 0)
        {
            try
            {
                var final = await TriggerMirrorAsync(CancellationToken.None).ConfigureAwait(false);
                if (!final.Success && final.Error!.Code != ErrorCode.Cancelled)
                {
                    _logger.LogWarning(
                        "Final brain mirror on shutdown failed: {Code} {Message}",
                        final.Error.Code, final.Error.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Final brain mirror on shutdown faulted.");
            }
        }

        _debouncePulse.Complete();

        await AwaitWithBudgetAsync(_timerTask, "Timer loop").ConfigureAwait(false);
        await AwaitWithBudgetAsync(_debounceTask, "Debounce loop").ConfigureAwait(false);

        _serviceCts?.Dispose();
        _runLock.Dispose();
    }

    private async Task AwaitWithBudgetAsync(Task? task, string name)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("{Task} did not stop within 5 s.", name);
        }
        catch (OperationCanceledException)
        {
            // Expected on graceful exit.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Task} faulted during shutdown.", name);
        }
    }

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(UploadConstants.BrainMirrorIntervalMinutes);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _clock.Delay(interval, ct).ConfigureAwait(false);
                var result = await TriggerMirrorAsync(ct).ConfigureAwait(false);
                if (!result.Success && result.Error!.Code != ErrorCode.Cancelled)
                {
                    _logger.LogWarning(
                        "Timer-driven brain mirror cycle failed: {Code} {Message}",
                        result.Error.Code, result.Error.Message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Brain mirror timer loop faulted; continuing.");
                try
                {
                    await _clock.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task DebounceLoopAsync(CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(DebounceWindowSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _debouncePulse.WaitAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // Sliding window: keep waiting until the window is fully quiet.
                while (true)
                {
                    long startTicks = Interlocked.Read(ref _lastCommitTicks);
                    await _clock.Delay(window, ct).ConfigureAwait(false);
                    long endTicks = Interlocked.Read(ref _lastCommitTicks);
                    if (endTicks == startTicks)
                    {
                        break;
                    }
                }

                Interlocked.Exchange(ref _lastCommitTicks, 0L);

                var result = await TriggerMirrorAsync(ct).ConfigureAwait(false);
                if (!result.Success && result.Error!.Code != ErrorCode.Cancelled)
                {
                    _logger.LogWarning(
                        "Commit-driven brain mirror cycle failed: {Code} {Message}",
                        result.Error.Code, result.Error.Message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Brain mirror debounce loop faulted; continuing.");
                try
                {
                    await _clock.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<Result> RunOneCycleAsync(CancellationToken ct)
    {
        DateTime nowUtc = _clock.UtcNow;
        string? stagingPath = null;
        byte[]? payload = null;

        try
        {
            ct.ThrowIfCancellationRequested();

            // 1. Snapshot.
            var snapResult = await SnapshotAsync(nowUtc, ct).ConfigureAwait(false);
            if (!snapResult.Success)
            {
                return Result.Fail(snapResult.Error!);
            }
            stagingPath = snapResult.Value!;

            // 2. Size cap check.
            long snapSize = new FileInfo(stagingPath).Length;
            if (snapSize > _maxInMemoryMirrorBytes)
            {
                _logger.LogError(
                    "Brain snapshot size {Size} exceeds cap {Cap}; aborting cycle.",
                    snapSize, _maxInMemoryMirrorBytes);
                await PublishMirrorFailureAsync(
                    "(all tails)",
                    ErrorCode.FileTooLong,
                    $"Brain catalogue copy is too large for this version ({snapSize} bytes).")
                    .ConfigureAwait(false);

                var meta = new Dictionary<string, string>
                {
                    ["SnapshotSizeBytes"] = snapSize.ToString(CultureInfo.InvariantCulture),
                    ["CapBytes"] = _maxInMemoryMirrorBytes.ToString(CultureInfo.InvariantCulture),
                };
                return Result.Fail(new ErrorContext
                {
                    Code = ErrorCode.FileTooLong,
                    Message = "Brain mirror snapshot exceeds the supported in-memory size.",
                    Metadata = meta,
                });
            }

            // 3. Encrypt.
            var encResult = await ReadAndEncryptAsync(stagingPath, nowUtc, ct).ConfigureAwait(false);
            if (!encResult.Success)
            {
                return Result.Fail(encResult.Error!);
            }
            payload = encResult.Value!;

            // 4. List active providers.
            var listResult = await _registry.ListActiveProviderIdsAsync(ct).ConfigureAwait(false);
            if (!listResult.Success)
            {
                _logger.LogWarning(
                    "Cannot list providers for brain mirror: {Code}", listResult.Error!.Code);
                return Result.Fail(listResult.Error!);
            }

            // 5. Per-tail upload + prune, isolated.
            foreach (var providerId in listResult.Value!)
            {
                ct.ThrowIfCancellationRequested();

                var providerResult = await _registry.GetAsync(providerId, ct).ConfigureAwait(false);
                if (!providerResult.Success)
                {
                    _logger.LogWarning(
                        "Provider {Id} not available for brain mirror: {Code}",
                        providerId, providerResult.Error!.Code);
                    continue;
                }
                var provider = providerResult.Value!;

                try
                {
                    var upResult = await UploadToOneTailAsync(provider, payload, nowUtc, ct)
                        .ConfigureAwait(false);
                    if (!upResult.Success)
                    {
                        if (upResult.Error!.Code != ErrorCode.Cancelled)
                        {
                            await PublishMirrorFailureAsync(
                                provider.DisplayName,
                                upResult.Error.Code,
                                upResult.Error.Message ?? "Upload failed.")
                                .ConfigureAwait(false);
                        }
                        continue;
                    }

                    var pruneResult = await PruneOneTailAsync(provider, ct).ConfigureAwait(false);
                    if (!pruneResult.Success && pruneResult.Error!.Code != ErrorCode.Cancelled)
                    {
                        _logger.LogWarning(
                            "Brain mirror retention prune failed for {Tail}: {Code}",
                            provider.DisplayName, pruneResult.Error.Code);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Brain mirror to {Tail} faulted; continuing with remaining tails.",
                        provider.DisplayName);
                }
            }

            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("Brain mirror cycle cancelled.");
            return Result.Fail(ErrorCode.Cancelled, "Brain mirror cycle cancelled.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain mirror cycle faulted unexpectedly.");
            return Result.Fail(ErrorCode.Unknown, "Brain mirror cycle faulted.", ex);
        }
        finally
        {
            // Compensation — Principle 17: cleanup must complete regardless of ct.
            if (stagingPath is not null)
            {
                try
                {
                    File.Delete(stagingPath);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex,
                        "Could not delete staging snapshot {Path}", stagingPath);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex,
                        "Access denied deleting staging snapshot {Path}", stagingPath);
                }
            }

            // Ciphertext payload is not secret; null out to help GC reclaim the buffer.
            payload = null;
        }
    }

    private async Task<Result<string>> SnapshotAsync(DateTime nowUtc, CancellationToken ct)
    {
        string stagingDir = Path.Combine(_skinkRoot, ".flashskink", "staging");
        string stagingPath = Path.Combine(
            stagingDir,
            $"brain-mirror-{nowUtc.Ticks.ToString(CultureInfo.InvariantCulture)}.db");

        try
        {
            ct.ThrowIfCancellationRequested();

            Directory.CreateDirectory(stagingDir);
            AtomicWriteHelper.FsyncDirectory(stagingDir);

            // SqliteConnection.BackupDatabase is synchronous; offload so we don't block the
            // calling sync-context.
            await Task.Run(() =>
            {
                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = stagingPath,
                    Pooling = false,
                };
                using var dest = new SqliteConnection(csb.ConnectionString);
                dest.Open();
                _brain.BackupDatabase(dest);
                dest.Close();
            }, ct).ConfigureAwait(false);

            // fsync the snapshot file and the directory.
            using (var fs = new FileStream(
                stagingPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                RandomAccess.FlushToDisk(fs.SafeFileHandle);
            }
            AtomicWriteHelper.FsyncDirectory(stagingDir);

            return Result<string>.Ok(stagingPath);
        }
        catch (OperationCanceledException ex)
        {
            TryDelete(stagingPath);
            return Result<string>.Fail(ErrorCode.Cancelled, "Snapshot cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            TryDelete(stagingPath);
            return Result<string>.Fail(
                ErrorCode.DatabaseReadFailed, "Brain snapshot failed.", ex);
        }
        catch (IOException ex)
        {
            TryDelete(stagingPath);
            return Result<string>.Fail(
                ErrorCode.Unknown, "I/O failure during brain snapshot.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(stagingPath);
            return Result<string>.Fail(
                ErrorCode.Unknown, "Access denied during brain snapshot.", ex);
        }
        catch (Exception ex)
        {
            TryDelete(stagingPath);
            return Result<string>.Fail(
                ErrorCode.Unknown, "Unexpected error during brain snapshot.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of a partial snapshot; original failure already captured.
        }
    }

    private async Task<Result<byte[]>> ReadAndEncryptAsync(
        string stagingPath, DateTime headerTimestamp, CancellationToken ct)
    {
        try
        {
            long size = new FileInfo(stagingPath).Length;
            if (size > _maxInMemoryMirrorBytes)
            {
                // Defence in depth — RunOneCycleAsync already checked, but the file could
                // (theoretically) have grown between the two FileInfo reads.
                return Result<byte[]>.Fail(
                    ErrorCode.FileTooLong, "Brain snapshot exceeds in-memory cap.");
            }

            int plaintextLen = (int)size;
            byte[] plaintext = new byte[plaintextLen];

            await using (var fs = new FileStream(
                stagingPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 65536,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                }))
            {
                int read = 0;
                while (read < plaintextLen)
                {
                    int n = await fs.ReadAsync(
                        plaintext.AsMemory(read, plaintextLen - read), ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                        return Result<byte[]>.Fail(
                            ErrorCode.Unknown,
                            "Unexpected EOF while reading brain snapshot.");
                    }
                    read += n;
                }
            }

            // Payload layout: header (16) || nonce (12) || ciphertext (N) || tag (16).
            byte[] payload = new byte[BrainMirrorHeader.Size + NonceSize + plaintextLen + TagSize];

            // Header — also the AAD.
            BrainMirrorHeader.Write(payload.AsSpan(0, BrainMirrorHeader.Size), headerTimestamp);

            // Nonce — fresh random per mirror.
            Span<byte> nonce = stackalloc byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);
            nonce.CopyTo(payload.AsSpan(BrainMirrorHeader.Size, NonceSize));

            Span<byte> ciphertext = payload.AsSpan(
                BrainMirrorHeader.Size + NonceSize, plaintextLen);
            Span<byte> tag = payload.AsSpan(
                BrainMirrorHeader.Size + NonceSize + plaintextLen, TagSize);

            try
            {
                using var aes = new AesGcm(_dek.Span, TagSize);
                aes.Encrypt(
                    nonce,
                    plaintext,
                    ciphertext,
                    tag,
                    associatedData: payload.AsSpan(0, BrainMirrorHeader.Size));
            }
            catch (CryptographicException ex)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                return Result<byte[]>.Fail(
                    ErrorCode.EncryptionFailed, "Brain mirror AES-GCM failed.", ex);
            }

            // Wipe plaintext copy of the brain — defence in depth (Principle 31 strict scope
            // is keys, but the snapshot includes encrypted-OAuth-tokens and metadata).
            CryptographicOperations.ZeroMemory(plaintext);

            return Result<byte[]>.Ok(payload);
        }
        catch (OperationCanceledException ex)
        {
            return Result<byte[]>.Fail(
                ErrorCode.Cancelled, "Brain mirror encryption cancelled.", ex);
        }
        catch (IOException ex)
        {
            return Result<byte[]>.Fail(
                ErrorCode.Unknown, "I/O failure during brain mirror encryption.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during brain mirror encryption.");
            return Result<byte[]>.Fail(
                ErrorCode.Unknown, "Unexpected error during brain mirror encryption.", ex);
        }
    }

    private async Task<Result<string>> UploadToOneTailAsync(
        IStorageProvider provider, byte[] payload, DateTime headerTimestamp, CancellationToken ct)
    {
        string remoteName = string.Format(
            CultureInfo.InvariantCulture,
            "_brain/{0:yyyyMMddTHHmmssZ}.bin",
            headerTimestamp);

        var sessionResult = await provider
            .BeginUploadAsync(remoteName, payload.LongLength, ct).ConfigureAwait(false);
        if (!sessionResult.Success)
        {
            return Result<string>.Fail(sessionResult.Error!);
        }
        var session = sessionResult.Value!;

        int offset = 0;
        while (offset < payload.Length)
        {
            ct.ThrowIfCancellationRequested();
            int chunkLen = Math.Min(UploadConstants.RangeSize, payload.Length - offset);
            var rangeMem = new ReadOnlyMemory<byte>(payload, offset, chunkLen);

            var rangeResult = await provider
                .UploadRangeAsync(session, offset, rangeMem, ct).ConfigureAwait(false);
            if (!rangeResult.Success)
            {
                await provider
                    .AbortUploadAsync(session, CancellationToken.None).ConfigureAwait(false);
                return Result<string>.Fail(rangeResult.Error!);
            }
            offset += chunkLen;
        }

        var finResult = await provider.FinaliseUploadAsync(session, ct).ConfigureAwait(false);
        if (!finResult.Success)
        {
            await provider.AbortUploadAsync(session, CancellationToken.None).ConfigureAwait(false);
            return Result<string>.Fail(finResult.Error!);
        }

        return Result<string>.Ok(finResult.Value!);
    }

    private async Task<Result> PruneOneTailAsync(IStorageProvider provider, CancellationToken ct)
    {
        var listResult = await provider.ListAsync("_brain", ct).ConfigureAwait(false);
        if (!listResult.Success)
        {
            return Result.Fail(listResult.Error!);
        }

        // ISO-8601 yyyyMMddTHHmmssZ in lex order == chronological order.
        var entries = new List<string>();
        foreach (var id in listResult.Value!)
        {
            if (id.StartsWith("_brain/", StringComparison.Ordinal)
                && id.EndsWith(".bin", StringComparison.Ordinal))
            {
                entries.Add(id);
            }
        }
        entries.Sort(StringComparer.Ordinal);
        entries.Reverse(); // descending — most recent first

        if (entries.Count <= UploadConstants.BrainMirrorRollingCount)
        {
            return Result.Ok();
        }

        for (int i = UploadConstants.BrainMirrorRollingCount; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var stale = entries[i];
            var delResult = await provider.DeleteAsync(stale, ct).ConfigureAwait(false);
            if (!delResult.Success && delResult.Error!.Code != ErrorCode.Cancelled)
            {
                _logger.LogWarning(
                    "Could not prune stale mirror {RemoteId} from {Tail}: {Code}",
                    stale, provider.DisplayName, delResult.Error.Code);
            }
        }

        return Result.Ok();
    }

    private async Task PublishMirrorFailureAsync(
        string tailDisplayName, ErrorCode code, string message)
    {
        var notification = new Notification
        {
            Source = "BrainMirrorService",
            Severity = NotificationSeverity.Error,
            Title = "Could not save the catalogue copy",
            Message = $"Could not save the catalogue copy to '{tailDisplayName}'.",
            Error = new ErrorContext { Code = code, Message = message },
            OccurredUtc = _clock.UtcNow,
            RequiresUserAction = false,
        };

        try
        {
            // Principle 17 — notification publish is bookkeeping; CancellationToken.None.
            await _bus.PublishAsync(notification, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Swallow — failing to publish must not break the cycle. Log only.
            _logger.LogWarning(ex,
                "Could not publish brain-mirror failure notification for {Tail}.",
                tailDisplayName);
        }
    }
}
