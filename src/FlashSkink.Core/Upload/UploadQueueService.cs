using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Abstractions.Time;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Upload;

/// <summary>
/// Per-volume background service that runs one upload worker per registered tail. Wakes on
/// <see cref="UploadWakeupSignal"/> pulses (post-commit hook from <c>WritePipeline</c> in §3.6),
/// on the §15.8 30-second orchestrator poll, and on the 60-second per-worker poll. Each worker
/// dequeues <c>TailUploads</c> rows for its provider, delegates the per-blob state machine to
/// <see cref="RangeUploader"/>, and applies the §21.1 cycle-level retry ladder + the §15.3 step 7c
/// brain transitions + the §8 notification surface.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Per-tail isolation (Principle 2).</strong> A failure on one tail never blocks
/// another. Each worker has its own <see cref="CancellationTokenSource"/> linked to the service
/// CTS; exceptions inside the worker loop are caught and logged, never propagated.
/// </para>
/// <para>
/// <strong>Network gating (§22.4).</strong> When
/// <see cref="INetworkAvailabilityMonitor.IsAvailable"/> is <see langword="false"/>, both the
/// orchestrator and every worker skip the active body of their tick and idle. A transition
/// back to <see langword="true"/> on <see cref="INetworkAvailabilityMonitor.AvailabilityChanged"/>
/// pulses the wakeup signal so they resume within one tick.
/// </para>
/// <para>
/// <strong>Shutdown (Principle 24).</strong> <see cref="DisposeAsync"/> cancels the service CTS,
/// awaits the orchestrator (10 s budget), then awaits every worker in parallel (10 s budget
/// each). In-flight <c>UploadSessions</c> rows are preserved so the next session resumes via
/// <see cref="UploadQueueRepository.LookupSessionAsync"/>.
/// </para>
/// </remarks>
public sealed class UploadQueueService : IAsyncDisposable
{
    private const string SourceTag = "UploadQueueService";

    private readonly UploadQueueRepository _uploadQueueRepository;
    private readonly BlobRepository _blobRepository;
    private readonly FileRepository _fileRepository;
    private readonly ActivityLogRepository _activityLogRepository;
    private readonly IProviderRegistry _providerRegistry;
    private readonly INetworkAvailabilityMonitor _networkMonitor;
    private readonly INotificationBus _notificationBus;
    private readonly RangeUploader _rangeUploader;
    private readonly RetryPolicy _retryPolicy;
    private readonly IClock _clock;
    private readonly UploadWakeupSignal _wakeupSignal;
    private readonly SqliteConnection _connection;
    private readonly string _skinkRoot;
    private readonly ILogger<UploadQueueService> _logger;

    private readonly Dictionary<string, (Task Task, CancellationTokenSource Cts)> _workers = [];
    private readonly SemaphoreSlim _workersLock = new(1, 1);
    private readonly EventHandler<bool> _availabilityHandler;

    private int _started;
    private int _disposed;
    private CancellationTokenSource? _serviceCts;
    private Task? _orchestratorTask;

    private static readonly TimeSpan OrchestratorIdle =
        TimeSpan.FromSeconds(UploadConstants.OrchestratorIdlePollSeconds);
    private static readonly TimeSpan WorkerIdle =
        TimeSpan.FromSeconds(UploadConstants.WorkerIdlePollSeconds);
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Creates an <see cref="UploadQueueService"/>. Does not start the orchestrator; call
    /// <see cref="Start"/> once the volume is ready.
    /// </summary>
    /// <param name="uploadQueueRepository">Queue and session repository.</param>
    /// <param name="blobRepository">Blob row repository.</param>
    /// <param name="fileRepository">File row repository (used to resolve <c>VirtualPath</c> for user-vocabulary notifications).</param>
    /// <param name="activityLogRepository">Audit-log repository.</param>
    /// <param name="providerRegistry">Live-provider lookup.</param>
    /// <param name="networkMonitor">Local-network availability gating per §22.4.</param>
    /// <param name="notificationBus">Notification publish path per §8.</param>
    /// <param name="rangeUploader">Per-blob state machine (§3.3).</param>
    /// <param name="retryPolicy">§21.1 retry ladders (§3.2).</param>
    /// <param name="clock">Time source (§3.2).</param>
    /// <param name="wakeupSignal">Shared wakeup signal — pulsed by <c>WritePipeline</c> post-commit (§3.6).</param>
    /// <param name="connection">Brain connection — used to open the §15.3 step 7c transactions.</param>
    /// <param name="skinkRoot">Absolute path to the skink root; combined with <c>Blobs.BlobPath</c> to locate the local blob.</param>
    /// <param name="logger">Service logger.</param>
    public UploadQueueService(
        UploadQueueRepository uploadQueueRepository,
        BlobRepository blobRepository,
        FileRepository fileRepository,
        ActivityLogRepository activityLogRepository,
        IProviderRegistry providerRegistry,
        INetworkAvailabilityMonitor networkMonitor,
        INotificationBus notificationBus,
        RangeUploader rangeUploader,
        RetryPolicy retryPolicy,
        IClock clock,
        UploadWakeupSignal wakeupSignal,
        SqliteConnection connection,
        string skinkRoot,
        ILogger<UploadQueueService> logger)
    {
        _uploadQueueRepository = uploadQueueRepository;
        _blobRepository = blobRepository;
        _fileRepository = fileRepository;
        _activityLogRepository = activityLogRepository;
        _providerRegistry = providerRegistry;
        _networkMonitor = networkMonitor;
        _notificationBus = notificationBus;
        _rangeUploader = rangeUploader;
        _retryPolicy = retryPolicy;
        _clock = clock;
        _wakeupSignal = wakeupSignal;
        _connection = connection;
        _skinkRoot = skinkRoot;
        _logger = logger;
        _availabilityHandler = OnAvailabilityChanged;
    }

    /// <summary>
    /// Starts the orchestrator background task linked to <paramref name="volumeToken"/>. Idempotent:
    /// a second call returns <see cref="Result.Ok"/> without effect. Returns
    /// <see cref="ErrorCode.ObjectDisposed"/> if the service has been disposed.
    /// </summary>
    public Result Start(CancellationToken volumeToken)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _logger.LogWarning("Start called on a disposed UploadQueueService.");
                return Result.Fail(ErrorCode.ObjectDisposed,
                    "Upload queue service has been disposed.");
            }

            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                return Result.Ok();
            }

            _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(volumeToken);
            _networkMonitor.AvailabilityChanged += _availabilityHandler;

            CancellationToken token = _serviceCts.Token;
            _orchestratorTask = Task.Factory.StartNew(
                () => OrchestratorAsync(token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();

            _logger.LogInformation("UploadQueueService orchestrator started.");
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error starting UploadQueueService.");
            return Result.Fail(ErrorCode.Unknown, "Unexpected error starting upload queue service.", ex);
        }
    }

    /// <summary>
    /// Stops the orchestrator and every worker, awaiting each within
    /// <see cref="ShutdownBudget"/>. In-flight <c>UploadSessions</c> rows are preserved.
    /// Idempotent.
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
            // Already disposed in a prior path — proceed.
        }

        // Unsubscribe before waiting so a late event cannot re-pulse a shutting-down loop.
        _networkMonitor.AvailabilityChanged -= _availabilityHandler;

        // Complete the wakeup signal so any straggler WaitAsync returns. Pulse() calls after
        // Complete are silent no-ops.
        _wakeupSignal.Complete();

        if (_orchestratorTask is not null)
        {
            try
            {
                await _orchestratorTask.WaitAsync(ShutdownBudget, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Orchestrator did not stop within {Budget}.", ShutdownBudget);
            }
            catch (OperationCanceledException)
            {
                // Expected — the orchestrator observed the service CTS and exited.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error awaiting orchestrator shutdown.");
            }
        }

        await _workersLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var workerTasks = new List<Task>(_workers.Count);
            foreach (var (providerId, entry) in _workers)
            {
                try
                {
                    entry.Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already cancelled; proceed.
                }

                workerTasks.Add(WaitWithBudgetAsync(entry.Task, ShutdownBudget, providerId));
            }

            await Task.WhenAll(workerTasks).ConfigureAwait(false);

            foreach (var entry in _workers.Values)
            {
                entry.Cts.Dispose();
            }

            _workers.Clear();
        }
        finally
        {
            _workersLock.Release();
        }

        try
        {
            _serviceCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Ignore — already disposed.
        }

        _workersLock.Dispose();

        _logger.LogInformation("UploadQueueService disposed.");
    }

    // ── Orchestrator loop ────────────────────────────────────────────────────────────────────

    private async Task OrchestratorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_networkMonitor.IsAvailable)
                {
                    await IdleAsync(OrchestratorIdle, ct).ConfigureAwait(false);
                    continue;
                }

                var activeListResult = await _providerRegistry
                    .ListActiveProviderIdsAsync(ct).ConfigureAwait(false);
                if (!activeListResult.Success)
                {
                    _logger.LogWarning(
                        "Could not list active providers: {Code}",
                        activeListResult.Error!.Code);
                    await _clock.Delay(OrchestratorIdle, ct).ConfigureAwait(false);
                    continue;
                }

                var active = new HashSet<string>(activeListResult.Value!, StringComparer.Ordinal);
                await EnsureAllRunningAndPruneDepartedAsync(active, ct).ConfigureAwait(false);

                await IdleAsync(OrchestratorIdle, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Orchestrator loop iteration faulted; continuing.");
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

        _logger.LogInformation("Orchestrator loop exited.");
    }

    private async Task EnsureAllRunningAndPruneDepartedAsync(
        HashSet<string> active, CancellationToken ct)
    {
        await _workersLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var id in active)
            {
                if (!_workers.ContainsKey(id))
                {
                    var workerCts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCts!.Token);
                    CancellationToken workerToken = workerCts.Token;
                    var workerTask = Task.Factory.StartNew(
                        () => WorkerAsync(id, workerToken),
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default).Unwrap();
                    _workers[id] = (workerTask, workerCts);
                    _logger.LogInformation("Worker started for provider {ProviderId}", id);
                }
            }

            var departed = new List<string>();
            foreach (var id in _workers.Keys)
            {
                if (!active.Contains(id))
                {
                    departed.Add(id);
                }
            }

            foreach (var id in departed)
            {
                await StopWorkerLockedAsync(id).ConfigureAwait(false);
            }
        }
        finally
        {
            _workersLock.Release();
        }
    }

    private async Task StopWorkerLockedAsync(string providerId)
    {
        if (!_workers.TryGetValue(providerId, out var entry))
        {
            return;
        }

        try
        {
            entry.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already cancelled.
        }

        try
        {
            await entry.Task.WaitAsync(ShutdownBudget, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Worker for {ProviderId} did not stop within {Budget}; abandoning.",
                providerId, ShutdownBudget);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error stopping worker for {ProviderId}.", providerId);
        }

        entry.Cts.Dispose();
        _workers.Remove(providerId);
        _logger.LogInformation("Worker stopped for provider {ProviderId}", providerId);
    }

    // ── Worker loop ──────────────────────────────────────────────────────────────────────────

    private async Task WorkerAsync(string providerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_networkMonitor.IsAvailable)
                {
                    await IdleAsync(WorkerIdle, ct).ConfigureAwait(false);
                    continue;
                }

                var providerResult = await _providerRegistry.GetAsync(providerId, ct)
                    .ConfigureAwait(false);
                if (!providerResult.Success)
                {
                    // The orchestrator will prune us on its next tick.
                    await _clock.Delay(WorkerIdle, ct).ConfigureAwait(false);
                    continue;
                }
                var provider = providerResult.Value!;

                bool processedAny = false;
                await foreach (var row in _uploadQueueRepository
                    .DequeueNextBatchAsync(providerId, batchSize: 1, ct)
                    .ConfigureAwait(false))
                {
                    processedAny = true;
                    var processResult = await ProcessOneAsync(row, provider, ct).ConfigureAwait(false);
                    if (!processResult.Success)
                    {
                        if (processResult.Error!.Code == ErrorCode.Cancelled)
                        {
                            // Shutdown — preserve UploadSessions row; exit loop.
                            return;
                        }

                        _logger.LogError(
                            "Brain bookkeeping faulted for file {FileId} on {ProviderId}: {Code}",
                            row.FileId, providerId, processResult.Error.Code);
                        break;
                    }
                }

                if (!processedAny)
                {
                    await IdleAsync(WorkerIdle, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SqliteException ex)
            {
                // DequeueNextBatchAsync is the §9.7 sanctioned raw-reader path; SqliteException
                // can propagate out of the IAsyncEnumerable. Absorb here so the worker doesn't die.
                _logger.LogError(ex,
                    "SQLite error in worker for {ProviderId}; idling and continuing.",
                    providerId);
                try
                {
                    await _clock.Delay(WorkerIdle, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Worker loop iteration faulted for {ProviderId}; idling and continuing.",
                    providerId);
                try
                {
                    await _clock.Delay(WorkerIdle, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Worker loop exited for provider {ProviderId}", providerId);
    }

    // ── Per-row processing ───────────────────────────────────────────────────────────────────

    private async Task<Result> ProcessOneAsync(
        TailUploadRow row, IStorageProvider provider, CancellationToken ct)
    {
        try
        {
            // 1. Look up file row (for VirtualPath in user-facing strings + BlobID).
            var fileResult = await _fileRepository.GetByIdAsync(row.FileId, ct).ConfigureAwait(false);
            if (!fileResult.Success)
            {
                return Result.Fail(fileResult.Error!);
            }
            if (fileResult.Value is null || fileResult.Value.BlobId is null)
            {
                _logger.LogError(
                    "File {FileId} missing or has no associated blob; marking failed.",
                    row.FileId);
                var markMissing = await _uploadQueueRepository
                    .MarkFailedAsync(row.FileId, row.ProviderId,
                        "File row missing or has no associated blob.", ct)
                    .ConfigureAwait(false);
                return markMissing.Success ? Result.Ok() : Result.Fail(markMissing.Error!);
            }
            var file = fileResult.Value;

            // 2. Look up blob row.
            var blobResult = await _blobRepository.GetByIdAsync(file.BlobId!, ct)
                .ConfigureAwait(false);
            if (!blobResult.Success)
            {
                return Result.Fail(blobResult.Error!);
            }
            if (blobResult.Value is null)
            {
                _logger.LogError(
                    "Blob {BlobId} missing for file {FileId}; marking failed.",
                    file.BlobId, row.FileId);
                var markMissing = await _uploadQueueRepository
                    .MarkFailedAsync(row.FileId, row.ProviderId,
                        "Blob row missing for file.", ct)
                    .ConfigureAwait(false);
                return markMissing.Success ? Result.Ok() : Result.Fail(markMissing.Error!);
            }
            var blob = blobResult.Value;

            // 3. Flip to UPLOADING (increments AttemptCount — the cycle counter).
            var markUploading = await _uploadQueueRepository
                .MarkUploadingAsync(row.FileId, row.ProviderId, ct)
                .ConfigureAwait(false);
            if (!markUploading.Success)
            {
                return Result.Fail(markUploading.Error!);
            }

            // 4. Look up resumable session (read-only).
            var sessionResult = await _uploadQueueRepository
                .LookupSessionAsync(row.FileId, row.ProviderId, ct)
                .ConfigureAwait(false);
            if (!sessionResult.Success)
            {
                return Result.Fail(sessionResult.Error!);
            }
            UploadSessionRow? existingSession = sessionResult.Value;

            // 5. Resolve absolute blob path.
            string blobAbsolutePath = Path.Combine(_skinkRoot, blob.BlobPath);

            // 6. Delegate to RangeUploader.
            var uploadResult = await _rangeUploader.UploadAsync(
                row.FileId, row.ProviderId, provider, blob, blobAbsolutePath, existingSession, ct)
                .ConfigureAwait(false);

            if (!uploadResult.Success)
            {
                if (uploadResult.Error!.Code == ErrorCode.Cancelled)
                {
                    // Preserve UploadSessions row; propagate.
                    return Result.Fail(uploadResult.Error);
                }

                _logger.LogError(
                    "RangeUploader returned Result.Fail for {FileId} on {ProviderId}: {Code} {Message}",
                    row.FileId, row.ProviderId,
                    uploadResult.Error.Code, uploadResult.Error.Message);
                return Result.Fail(uploadResult.Error);
            }

            // 7. Apply the per-blob outcome — brain transaction lives inside.
            return await ApplyOutcomeAsync(row, file, provider, uploadResult.Value!, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled,
                "Per-row processing was cancelled.", ex);
        }
    }

    private Task<Result> ApplyOutcomeAsync(
        TailUploadRow row, VolumeFile file, IStorageProvider provider,
        UploadOutcome outcome, CancellationToken ct) => outcome.Status switch
        {
            UploadOutcomeStatus.Completed =>
                ApplyCompletedAsync(row, file, provider, outcome, ct),
            UploadOutcomeStatus.RetryableFailure =>
                ApplyRetryableAsync(row, file, provider, outcome, ct),
            UploadOutcomeStatus.PermanentFailure =>
                ApplyPermanentAsync(row, file, provider, outcome, ct),
            _ => Task.FromResult(Result.Ok()),
        };

    private async Task<Result> ApplyCompletedAsync(
        TailUploadRow row, VolumeFile file, IStorageProvider provider,
        UploadOutcome outcome, CancellationToken ct)
    {
        await using (var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct)
            .ConfigureAwait(false))
        {
            var markUploaded = await _uploadQueueRepository
                .MarkUploadedAsync(row.FileId, row.ProviderId, outcome.RemoteId!, tx, ct)
                .ConfigureAwait(false);
            if (!markUploaded.Success)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await OnTerminalBrainFailureAsync(
                    row, file, provider, markUploaded.Error!,
                    "mark uploaded").ConfigureAwait(false);
            }

            var deleteSession = await _uploadQueueRepository
                .DeleteSessionAsync(row.FileId, row.ProviderId, tx, ct)
                .ConfigureAwait(false);
            if (!deleteSession.Success)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await OnTerminalBrainFailureAsync(
                    row, file, provider, deleteSession.Error!,
                    "delete session").ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        // Activity log append uses CancellationToken.None — post-commit bookkeeping must not
        // be cancellable (Principle 17). User vocabulary per Principle 25.
        var activity = await _activityLogRepository.AppendAsync(new ActivityLogEntry
        {
            EntryId = Guid.NewGuid().ToString(),
            OccurredUtc = _clock.UtcNow,
            Category = "UPLOADED",
            Summary = $"Uploaded '{file.VirtualPath}' to '{provider.DisplayName}'.",
        }, CancellationToken.None).ConfigureAwait(false);
        if (!activity.Success)
        {
            _logger.LogWarning(
                "Failed to append UPLOADED activity-log entry for {FileId} on {ProviderId}: {Code}",
                row.FileId, row.ProviderId, activity.Error!.Code);
        }

        _logger.LogInformation(
            "Upload completed for {FileId} on {ProviderId} ({BytesUploaded} bytes).",
            row.FileId, row.ProviderId, outcome.BytesUploaded);
        return Result.Ok();
    }

    private async Task<Result> ApplyRetryableAsync(
        TailUploadRow row, VolumeFile file, IStorageProvider provider,
        UploadOutcome outcome, CancellationToken ct)
    {
        string failureCodeText = outcome.FailureCode?.ToString() ?? "Unknown";
        string failureMessage = outcome.FailureMessage ?? "Upload failed.";
        string lastError = $"{failureCodeText}: {failureMessage}";

        // Flip to FAILED and consult the cycle ladder. row.AttemptCount was the AttemptCount
        // value DequeueNextBatchAsync read; MarkUploadingAsync incremented it to
        // row.AttemptCount + 1 — that's the cycle that just escalated.
        var markFailed = await _uploadQueueRepository
            .MarkFailedAsync(row.FileId, row.ProviderId, lastError, ct)
            .ConfigureAwait(false);
        if (!markFailed.Success)
        {
            return await OnTerminalBrainFailureAsync(
                row, file, provider, markFailed.Error!,
                "mark failed (retryable)").ConfigureAwait(false);
        }

        int cycleNumber = row.AttemptCount + 1;
        RetryDecision decision = _retryPolicy.NextCycleAttempt(cycleNumber);

        if (decision.Outcome == RetryOutcome.MarkFailed)
        {
            ErrorCode code = outcome.FailureCode ?? ErrorCode.UploadFailed;
            return await PromoteToPermanentAsync(row, file, provider, code, failureMessage)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Cycle {Cycle} failed for {FileId} on {ProviderId} with {Code}; next attempt after {Delay}.",
            cycleNumber, row.FileId, row.ProviderId, failureCodeText, decision.Delay);

        try
        {
            await _clock.Delay(decision.Delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown during cycle wait — preserve state; the row stays FAILED and will be
            // re-dequeued by the worker filter when the service resumes.
        }

        return Result.Ok();
    }

    private async Task<Result> ApplyPermanentAsync(
        TailUploadRow row, VolumeFile file, IStorageProvider provider,
        UploadOutcome outcome, CancellationToken ct)
    {
        ErrorCode code = outcome.FailureCode ?? ErrorCode.UploadFailed;
        string failureMessage = outcome.FailureMessage ?? "Upload failed.";
        string lastError = $"{code}: {failureMessage}";

        await using (var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct)
            .ConfigureAwait(false))
        {
            // Use the terminal-failure variant which bumps AttemptCount to the §21.1 cycle cap
            // so the DequeueNextBatchAsync filter excludes this row from future cycles.
            var markFailed = await _uploadQueueRepository
                .MarkTerminallyFailedAsync(row.FileId, row.ProviderId, lastError, tx, ct)
                .ConfigureAwait(false);
            if (!markFailed.Success)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await OnTerminalBrainFailureAsync(
                    row, file, provider, markFailed.Error!,
                    "mark terminally failed").ConfigureAwait(false);
            }

            var deleteSession = await _uploadQueueRepository
                .DeleteSessionAsync(row.FileId, row.ProviderId, tx, ct)
                .ConfigureAwait(false);
            if (!deleteSession.Success)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await OnTerminalBrainFailureAsync(
                    row, file, provider, deleteSession.Error!,
                    "delete session (permanent)").ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        await PublishFailureAsync(file, provider, code, failureMessage,
            NotificationSeverity.Error).ConfigureAwait(false);

        var activity = await _activityLogRepository.AppendAsync(new ActivityLogEntry
        {
            EntryId = Guid.NewGuid().ToString(),
            OccurredUtc = _clock.UtcNow,
            Category = "UPLOAD_FAILED",
            Summary = $"Could not upload '{file.VirtualPath}' to '{provider.DisplayName}'.",
        }, CancellationToken.None).ConfigureAwait(false);
        if (!activity.Success)
        {
            _logger.LogWarning(
                "Failed to append UPLOAD_FAILED activity-log entry for {FileId} on {ProviderId}: {Code}",
                row.FileId, row.ProviderId, activity.Error!.Code);
        }

        _logger.LogError(
            "Permanent upload failure for {FileId} on {ProviderId}: {Code} {Message}",
            row.FileId, row.ProviderId, code, failureMessage);
        return Result.Ok();
    }

    private async Task<Result> PromoteToPermanentAsync(
        TailUploadRow row, VolumeFile file, IStorageProvider provider,
        ErrorCode code, string failureMessage)
    {
        // Defensively clamp AttemptCount to the §21.1 cycle cap so the DequeueNextBatchAsync
        // filter excludes this row. Today MarkUploadingAsync has already bumped AttemptCount to
        // at least 5 on the 5th cycle, but doing it explicitly here makes the method
        // self-contained — robust against any future change to where MarkFailed is called from
        // (e.g., if RetryPolicy.NextCycleAttempt is ever extended to return MarkFailed at lower
        // cycles for specific failure classes).
        string lastError = $"{code}: {failureMessage}";
        var markTerminal = await _uploadQueueRepository
            .MarkTerminallyFailedAsync(row.FileId, row.ProviderId, lastError,
                transaction: null, CancellationToken.None)
            .ConfigureAwait(false);
        if (!markTerminal.Success)
        {
            _logger.LogWarning(
                "Failed to clamp AttemptCount to terminal cap for {FileId} on {ProviderId}: {Code}",
                row.FileId, row.ProviderId, markTerminal.Error!.Code);
        }

        // Best-effort session delete (Principle 17 — bookkeeping not cancellable).
        var deleteSession = await _uploadQueueRepository
            .DeleteSessionAsync(row.FileId, row.ProviderId, CancellationToken.None)
            .ConfigureAwait(false);
        if (!deleteSession.Success)
        {
            _logger.LogWarning(
                "Failed to delete session row for {FileId} on {ProviderId} after cycle exhaustion: {Code}",
                row.FileId, row.ProviderId, deleteSession.Error!.Code);
        }

        await PublishFailureAsync(file, provider, code, failureMessage,
            NotificationSeverity.Error).ConfigureAwait(false);

        var activity = await _activityLogRepository.AppendAsync(new ActivityLogEntry
        {
            EntryId = Guid.NewGuid().ToString(),
            OccurredUtc = _clock.UtcNow,
            Category = "UPLOAD_FAILED",
            Summary = $"Stopped retrying upload of '{file.VirtualPath}' to '{provider.DisplayName}'.",
        }, CancellationToken.None).ConfigureAwait(false);
        if (!activity.Success)
        {
            _logger.LogWarning(
                "Failed to append UPLOAD_FAILED activity-log entry for {FileId} on {ProviderId}: {Code}",
                row.FileId, row.ProviderId, activity.Error!.Code);
        }

        _logger.LogError(
            "Cycle ladder exhausted for {FileId} on {ProviderId}: {Code} {Message}",
            row.FileId, row.ProviderId, code, failureMessage);

        return Result.Ok();
    }

    /// <summary>
    /// Common terminal-brain-write-failure path. The row's <c>TailUploads.Status</c> is stuck at
    /// <c>UPLOADING</c> (set by <see cref="UploadQueueRepository.MarkUploadingAsync"/> earlier in
    /// the flow); since <see cref="UploadQueueRepository.DequeueNextBatchAsync"/> excludes
    /// <c>UPLOADING</c>, the row will not be picked up again until Phase 5's WAL recovery sweep
    /// resets orphaned <c>UPLOADING</c> rows on next volume open. To make the stuck state
    /// observable to the user in the interim, publish a <see cref="NotificationSeverity.Critical"/>
    /// notification (which the existing <c>PersistenceNotificationHandler</c> records to
    /// <c>BackgroundFailures</c>) so the next launch surfaces it.
    /// </summary>
    private async Task<Result> OnTerminalBrainFailureAsync(
        TailUploadRow row, VolumeFile file, IStorageProvider provider,
        ErrorContext error, string operationDescription)
    {
        _logger.LogError(
            "Brain write failed for {FileId} on {ProviderId} during {Operation}: {Code} {Message}",
            row.FileId, row.ProviderId, operationDescription, error.Code, error.Message);

        await PublishFailureAsync(file, provider,
            error.Code,
            $"Could not record the outcome of an upload for '{file.VirtualPath}'. Reopen the volume to retry.",
            NotificationSeverity.Critical).ConfigureAwait(false);

        return Result.Fail(error);
    }

    private async Task PublishFailureAsync(
        VolumeFile file, IStorageProvider provider,
        ErrorCode code, string errorMessage,
        NotificationSeverity severity)
    {
        // Principle 25 — user vocabulary in user-facing strings. No "tail", "blob", "session",
        // "range", "WAL", "OAuth", "DEK", "KEK", or "AAD".
        var notification = new Notification
        {
            Source = SourceTag,
            Severity = severity,
            Title = "Could not back up file",
            Message = $"Could not upload '{file.VirtualPath}' to '{provider.DisplayName}'.",
            Error = new ErrorContext
            {
                Code = code,
                Message = errorMessage,
            },
            OccurredUtc = _clock.UtcNow,
            RequiresUserAction = false,
        };

        try
        {
            // Principle 17 — bookkeeping must not be cancellable mid-flight.
            await _notificationBus.PublishAsync(notification, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Defence in depth: a misbehaving bus must not prevent the row's terminal state from
            // being recorded. Logged once at Warning so we don't double-log the underlying issue.
            _logger.LogWarning(ex,
                "Notification publish failed for {VirtualPath} on {ProviderId}.",
                file.VirtualPath, provider.DisplayName);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private async Task IdleAsync(TimeSpan pollInterval, CancellationToken ct)
    {
        // Wake on Pulse OR on the poll-interval cap. The losing Task.WhenAny branch must be
        // cancelled so we don't leak a pending channel reader continuation on every idle round
        // (capacity-1 channels still accumulate registered readers when no token is buffered).
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task wakeup = _wakeupSignal.WaitAsync(idleCts.Token).AsTask();
        Task poll = _clock.Delay(pollInterval, idleCts.Token).AsTask();
        try
        {
            await Task.WhenAny(wakeup, poll).ConfigureAwait(false);
        }
        finally
        {
            // Cancel the losing branch (and the winning branch's no-op completion path). Both
            // observe idleCts; this releases the channel-reader registration created by
            // _wakeupSignal.WaitAsync if poll won, and cancels the timer if wakeup won.
            idleCts.Cancel();
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task WaitWithBudgetAsync(Task task, TimeSpan budget, string providerId)
    {
        try
        {
            await task.WaitAsync(budget, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Worker for {ProviderId} did not stop within {Budget}.",
                providerId, budget);
        }
        catch (OperationCanceledException)
        {
            // Expected on graceful exit.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unexpected error awaiting worker shutdown for {ProviderId}.",
                providerId);
        }
    }

    private void OnAvailabilityChanged(object? sender, bool isAvailable)
    {
        if (isAvailable)
        {
            _wakeupSignal.Pulse();
        }
        // Flipping to offline is observed at the next tick; workers fall through to IdleAsync.
    }
}
