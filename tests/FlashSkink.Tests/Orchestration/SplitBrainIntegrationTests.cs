using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Identity;
using FlashSkink.Core.Orchestration;
using FlashSkink.Core.Providers;
using FlashSkink.Tests._TestSupport;
using FlashSkink.Tests.Engine;
using FlashSkink.Tests.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Orchestration;

/// <summary>
/// End-to-end tests for the §3.5.2 session-begin witness handshake and fenced state via
/// <see cref="FlashSkinkVolume"/>. Uses <see cref="FileSystemProvider"/> registered via
/// <c>RegisterTailAsync</c> as the tail. Manually seeds the tail's witness file before
/// reopening to simulate split-brain scenarios. Blueprint §19.6–19.7.
/// </summary>
public sealed class SplitBrainIntegrationTests : IAsyncLifetime
{
    private const string Password = "test-password-12345";
    private const string ProviderId = "tail-1";
    private const string ProviderType = "filesystem";
    private const string DisplayName = "Test Tail";

    private string _skinkRoot = string.Empty;
    private string _tailRoot = string.Empty;
    private InMemoryProviderRegistry _registry = null!;
    private RecordingNotificationBus _bus = null!;

    public Task InitializeAsync()
    {
        _skinkRoot = Path.Combine(
            Path.GetTempPath(), $"flashskink-splitbrain-skink-{Guid.NewGuid():N}");
        _tailRoot = Path.Combine(
            Path.GetTempPath(), $"flashskink-splitbrain-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_skinkRoot);
        Directory.CreateDirectory(_tailRoot);
        _registry = new InMemoryProviderRegistry(NullLogger<InMemoryProviderRegistry>.Instance);
        _bus = new RecordingNotificationBus();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_skinkRoot, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_tailRoot, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private VolumeCreationOptions DefaultOptions(
        INetworkAvailabilityMonitor? netMonitor = null,
        InMemoryProviderRegistry? registry = null) => new()
        {
            LoggerFactory = NullLoggerFactory.Instance,
            NotificationBus = _bus,
            ProviderRegistry = registry ?? _registry,
            NetworkMonitor = netMonitor,
        };

    private FileSystemProvider CreateFsProvider(string? providerId = null) =>
        new(providerId ?? ProviderId, DisplayName, _tailRoot,
            NullLogger<FileSystemProvider>.Instance);

    private async Task<FlashSkinkVolume> CreateAndRegisterAsync(
        VolumeCreationOptions? options = null,
        IStorageProvider? provider = null)
    {
        var result = await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, options ?? DefaultOptions());
        Assert.True(result.Success, result.Error?.Message);
        var volume = result.Value!.Volume;
        var reg = await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, provider ?? CreateFsProvider());
        Assert.True(reg.Success, reg.Error?.Message);
        return volume;
    }

    private async Task<FlashSkinkVolume> ReopenAsync(VolumeCreationOptions? options = null)
    {
        var result = await FlashSkinkVolume.OpenAsync(
            _skinkRoot, Password, options ?? DefaultOptions());
        Assert.True(result.Success, result.Error?.Message);
        return result.Value!;
    }

    /// <summary>
    /// Unlocks the vault and returns the DEK so tests can manually write witness files to
    /// the tail (simulating split-brain scenarios). Caller owns zeroing.
    /// </summary>
    private async Task<byte[]> ReadDekAsync()
    {
        var vaultPath = Path.Combine(_skinkRoot, ".flashskink", "vault.bin");
        var kdf = new KeyDerivationService();
        var keyVault = new KeyVault(kdf, new MnemonicService());
        var passwordBytes = Encoding.UTF8.GetBytes(Password);
        var unlock = await keyVault.UnlockAsync(
            vaultPath, new ReadOnlyMemory<byte>(passwordBytes), CancellationToken.None);
        CryptographicOperations.ZeroMemory(passwordBytes);
        Assert.True(unlock.Success, unlock.Error?.Message);
        return unlock.Value!;
    }

    /// <summary>
    /// Manually writes a witness payload to the tail (bypassing <c>FlashSkinkVolume</c>).
    /// Used to set up split-brain scenarios — the test acts as a "second skink" that has
    /// stamped a witness with a chosen epoch/marker.
    /// </summary>
    private async Task ManuallyWriteWitnessAsync(long epoch, bool conflictObserved)
    {
        var dek = await ReadDekAsync();
        try
        {
            var volumeId = await OrchestrationTestHelper.ReadSettingAsync(
                _skinkRoot, Password, "VolumeID");
            Assert.NotNull(volumeId);

            var provider = CreateFsProvider();
            var store = new WitnessStore(NullLogger<WitnessStore>.Instance);
            var payload = new WitnessPayload(
                VolumeId: volumeId!,
                Epoch: epoch,
                SessionId: Guid.NewGuid().ToString("D"),
                CommittedAtUtc: DateTime.UtcNow.ToString("O"),
                Host: "other-host",
                AppVersion: "other-version",
                ConflictObserved: conflictObserved);
            var result = await store.WriteAsync(
                provider, dek, payload, CancellationToken.None);
            Assert.True(result.Success, result.Error?.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private async Task<WitnessPayload?> ManuallyReadWitnessAsync()
    {
        var dek = await ReadDekAsync();
        try
        {
            var provider = CreateFsProvider();
            var store = new WitnessStore(NullLogger<WitnessStore>.Instance);
            var result = await store.TryReadAsync(provider, dek, CancellationToken.None);
            Assert.True(result.Success);
            return result.Value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private string TailBlobsDir() => Path.Combine(_tailRoot, "blobs");

    private static byte[] RandomBytes(int n) => RandomNumberGenerator.GetBytes(n);

    private int CountCriticalSplitBrainNotifications() =>
        _bus.Published.Count(n =>
            n.Severity == NotificationSeverity.Critical &&
            n.Error?.Code == ErrorCode.SplitBrainDetected);

    private int CountInfoFromVolume() =>
        _bus.Published.Count(n =>
            n.Severity == NotificationSeverity.Info &&
            n.Source == nameof(FlashSkinkVolume));

    // ── RegisterTailAsync writes the initial witness ─────────────────────────

    [Fact]
    public async Task RegisterTailAsync_WritesInitialWitness_OnRegistration()
    {
        await using var volume = await CreateAndRegisterAsync();

        var witness = await ManuallyReadWitnessAsync();
        Assert.NotNull(witness);
        // The seeded VolumeEpoch is 1 (see SeedInitialSettingsAsync). RegisterTailAsync runs
        // against an open volume — no OpenAsync has run since CreateAsync — so the current
        // epoch is still 1.
        Assert.Equal(1L, witness!.Value.Epoch);
        Assert.False(witness.Value.ConflictObserved);
    }

    // ── Reopen with no conflict ──────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_NoConflict_State_IsNormal()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();

        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Normal, v2.State);
    }

    // ── Trigger A: epoch comparison ──────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_DetectsEpochConflict_VolumeFenced()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();

        // Manually advance the tail's witness to epoch 99 — simulating another skink that
        // ran many sessions after the common ancestor.
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);
    }

    // ── Trigger B: conflict marker (the lagger detection — meta-plan item 12) ─

    [Fact]
    public async Task OpenAsync_DetectsConflictMarker_VolumeFenced()
    {
        // Initial create stamps epoch 1 via SeedInitialSettings.
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        // First reopen advances local epoch to 2.
        var v2 = await ReopenAsync();
        await v2.DisposeAsync();

        // Manually overwrite the tail's witness with a LOWER epoch but ConflictObserved=true.
        // On the next reopen, local newEpoch becomes 3 (> tail epoch 1) — Trigger A would NOT
        // fire. Trigger B (the marker) is what catches this.
        await ManuallyWriteWitnessAsync(epoch: 1L, conflictObserved: true);

        await using var v3 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v3.State);
    }

    // ── Notification + BackgroundFailures wiring ─────────────────────────────

    [Fact]
    public async Task OpenAsync_SplitBrain_PublishesCriticalNotification()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);

        var critical = _bus.Published.Where(n =>
            n.Severity == NotificationSeverity.Critical &&
            n.Error?.Code == ErrorCode.SplitBrainDetected).ToList();
        Assert.Single(critical);
        Assert.Equal(nameof(FlashSkinkVolume), critical[0].Source);
        Assert.Equal(ProviderId, critical[0].Error!.Metadata!["TailProviderID"]);
    }

    [Fact]
    public async Task OpenAsync_SplitBrain_WritesBackgroundFailureRow()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);
        await using (var v2 = await ReopenAsync())
        {
            Assert.Equal(VolumeState.Fenced, v2.State);
        }

        await using var brain = await OrchestrationTestHelper.OpenBrainConnectionAsync(
            _skinkRoot, Password);
        var rows = await brain.QueryAsync<(string Source, string ErrorCode, string Message)>(
            "SELECT Source, ErrorCode, Message FROM BackgroundFailures");
        var matching = rows.Where(r =>
            r.Source == nameof(FlashSkinkVolume) &&
            r.ErrorCode == nameof(ErrorCode.SplitBrainDetected)).ToList();
        Assert.Single(matching);
        Assert.Contains("conflicting copy", matching[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fenced state persists across reopen, single notification ─────────────

    [Fact]
    public async Task OpenAsync_FencedState_PersistedAcrossReopen()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);
        await v2.DisposeAsync();
        int afterFirstFence = CountCriticalSplitBrainNotifications();
        Assert.Equal(1, afterFirstFence);

        // Second reopen WITHOUT touching the tail — but the prior fenced-detection's write
        // phase stamped the tail with epoch 2 (the local newEpoch) and ConflictObserved=true.
        // Reopening advances local newEpoch to 3, so Trigger A no longer fires (tail epoch 2 < 3),
        // but Trigger B (ConflictObserved=true) DOES fire — and the volume is already Fenced
        // → the no-spam rule applies.
        await using var v3 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v3.State);
        Assert.Equal(afterFirstFence, CountCriticalSplitBrainNotifications());
    }

    [Fact]
    public async Task FreshConflictWhileAlreadyFenced_DoesNotRePublishNotification()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();

        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);
        await using (var v2 = await ReopenAsync())
        {
            Assert.Equal(VolumeState.Fenced, v2.State);
        }
        int afterFirst = CountCriticalSplitBrainNotifications();
        Assert.Equal(1, afterFirst);

        // Another "advancement" by the OTHER skink while we were closed.
        await ManuallyWriteWitnessAsync(epoch: 500L, conflictObserved: false);

        await using var v3 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v3.State);
        Assert.Equal(afterFirst, CountCriticalSplitBrainNotifications());

        // Also confirm BackgroundFailures has exactly one row for SplitBrainDetected.
        await using var brain = await OrchestrationTestHelper.OpenBrainConnectionAsync(
            _skinkRoot, Password);
        var count = await brain.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM BackgroundFailures WHERE ErrorCode = @Code",
            new { Code = nameof(ErrorCode.SplitBrainDetected) });
        Assert.Equal(1, count);
    }

    // ── Fenced state blocks Phase 2 uploads but allows Phase 1 writes ────────

    [Fact]
    public async Task OpenAsync_FencedState_UploadsAreBlocked()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);

        var w = await v2.WriteFileAsync(new MemoryStream(RandomBytes(8 * 1024)), "fenced.bin");
        Assert.True(w.Success, "Phase 1 writes must continue to work while fenced.");

        // Give the upload worker a generous window. While fenced, the worker idles — no
        // blobs/ directory should appear on the tail.
        await Task.Delay(TimeSpan.FromSeconds(1));
        if (Directory.Exists(TailBlobsDir()))
        {
            // The directory existing alone is permissible (FS provider quirk); assert no
            // blob files are present.
            var blobFiles = Directory.GetFiles(TailBlobsDir(), "*.bin", SearchOption.AllDirectories);
            Assert.Empty(blobFiles);
        }
    }

    [Fact]
    public async Task OpenAsync_FencedState_Phase1WritesSucceed()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);

        var payload = RandomBytes(4 * 1024);
        var write = await v2.WriteFileAsync(new MemoryStream(payload), "doc.bin");
        Assert.True(write.Success);

        var dest = new MemoryStream();
        var read = await v2.ReadFileAsync("doc.bin", dest);
        Assert.True(read.Success);
        Assert.Equal(payload, dest.ToArray());
    }

    // ── User-vocabulary discipline (Principle 25) ────────────────────────────

    [Fact]
    public async Task Notification_SplitBrainDetected_UsesUserVocabularyOnly()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);
        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);

        var critical = _bus.Published.Single(n =>
            n.Severity == NotificationSeverity.Critical &&
            n.Error?.Code == ErrorCode.SplitBrainDetected);

        string blob = (critical.Title + " " + critical.Message).ToLowerInvariant();
        // Affirmative — at least one user-vocabulary anchor appears.
        Assert.True(blob.Contains("skink") || blob.Contains("tail") || blob.Contains("copy"),
            $"User-vocabulary anchor missing from notification: {blob}");
        // Negative — no appliance vocabulary.
        foreach (var forbidden in new[] {
            "epoch", "witness", "split-brain", "split brain", "fence", "fenced",
            "wal", "stripe", "dek", "aad", "blob",
        })
        {
            Assert.DoesNotContain(forbidden, blob, StringComparison.OrdinalIgnoreCase);
        }

        // Same negative assertion on the BackgroundFailures row.
        await using var brain = await OrchestrationTestHelper.OpenBrainConnectionAsync(
            _skinkRoot, Password);
        var msg = await brain.QuerySingleAsync<string>(
            "SELECT Message FROM BackgroundFailures WHERE ErrorCode = @Code",
            new { Code = nameof(ErrorCode.SplitBrainDetected) });
        var lowered = msg.ToLowerInvariant();
        foreach (var forbidden in new[] {
            "epoch", "witness", "split-brain", "split brain", "fence", "fenced",
            "wal", "stripe", "dek", "aad", "blob",
        })
        {
            Assert.DoesNotContain(forbidden, lowered, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Two-sided fence: winner stamps ConflictObserved on the tail ──────────

    [Fact]
    public async Task TwoSidedFence_WinnerWritesConflictObservedMarker()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        await using (var v2 = await ReopenAsync())
        {
            Assert.Equal(VolumeState.Fenced, v2.State);
        }

        // After Fenced detection, the rewritten witness should have ConflictObserved=true so
        // that the OTHER skink (the lagger) detects it on its next handshake.
        var witness = await ManuallyReadWitnessAsync();
        Assert.NotNull(witness);
        Assert.True(witness!.Value.ConflictObserved);
    }

    // ── Auto-downgrade for stale Fenced rows (crash-recovery path) ───────────

    [Fact]
    public async Task AutoDowngrade_StaleFencedBrainRow_WitnessShowsNoConflict_DowngradesToNormal()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();

        // Simulate a PromoteAsync that wrote fresh witnesses to all tails but crashed before
        // persisting Settings["VolumeState"] = "Normal": brain says Fenced, witnesses are clean.
        await OrchestrationTestHelper.UpsertSettingAsync(
            _skinkRoot, Password, "VolumeState", "Fenced");
        // (RegisterTailAsync already wrote a clean witness with ConflictObserved=false. Leave
        // that as the "clean" witness state.)

        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Normal, v2.State);

        var persisted = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeState");
        Assert.Equal("Normal", persisted);

        // An Info-severity notification announces the resolution.
        Assert.Contains(_bus.Published, n =>
            n.Severity == NotificationSeverity.Info &&
            n.Source == nameof(FlashSkinkVolume));
    }

    [Fact]
    public async Task AutoDowngrade_NoTailsReachable_KeepsFencedState()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();

        await OrchestrationTestHelper.UpsertSettingAsync(
            _skinkRoot, Password, "VolumeState", "Fenced");

        // Build a registry with a faulty wrapper that fails every ListAsync — so the handshake
        // reads zero tails (TailsRead == 0) and the auto-downgrade gate is blocked.
        var unreachableRegistry = new InMemoryProviderRegistry(
            NullLogger<InMemoryProviderRegistry>.Instance);
        var inner = CreateFsProvider();
        var faulty = new FaultInjectingStorageProvider(inner);
        // The fault is single-shot per call, but the handshake makes exactly one ListAsync
        // call per tail in the read phase — so one knob is enough.
        faulty.FailNextListWith(ErrorCode.ProviderUnreachable);
        unreachableRegistry.Register(ProviderId, faulty);

        await using var v2 = await ReopenAsync(DefaultOptions(registry: unreachableRegistry));
        Assert.Equal(VolumeState.Fenced, v2.State);

        var persisted = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeState");
        Assert.Equal("Fenced", persisted);
    }

    // ── RegisterTailAsync write failure is non-fatal ─────────────────────────

    [Fact]
    public async Task WitnessWriteFailureOnRegistration_DoesNotFailRegisterTail()
    {
        var createResult = await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions());
        Assert.True(createResult.Success);
        await using var volume = createResult.Value!.Volume;

        var faulty = new FaultInjectingStorageProvider(CreateFsProvider());
        faulty.FailNextBeginWith(ErrorCode.ProviderUnreachable);

        var reg = await volume.RegisterTailAsync(
            ProviderId, ProviderType, DisplayName, null, faulty);
        Assert.True(reg.Success, reg.Error?.Message);

        // Brain row was committed.
        await using var brain = await OrchestrationTestHelper.OpenBrainConnectionAsync(
            _skinkRoot, Password);
        var count = await brain.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Providers WHERE ProviderID = @P",
            new { P = ProviderId });
        Assert.Equal(1, count);

        // In-memory registry has the adapter.
        var ids = await _registry.ListActiveProviderIdsAsync(CancellationToken.None);
        Assert.True(ids.Success);
        Assert.Contains(ProviderId, ids.Value!);
    }

    // ── PromoteAsync — §3.5.3 ────────────────────────────────────────────────

    [Fact]
    public async Task PromoteAsync_ClearsFencedState_ReturnsNormal()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);

        var promote = await v2.PromoteAsync();
        Assert.True(promote.Success, promote.Error?.Message);
        Assert.Equal(VolumeState.Normal, v2.State);

        var persisted = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeState");
        Assert.Equal("Normal", persisted);
    }

    [Fact]
    public async Task PromoteAsync_WritesFreshWitnessWithNoConflictMarker()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);

        var promote = await v2.PromoteAsync();
        Assert.True(promote.Success, promote.Error?.Message);

        var witness = await ManuallyReadWitnessAsync();
        Assert.NotNull(witness);
        Assert.False(witness!.Value.ConflictObserved);
    }

    [Fact]
    public async Task PromoteAsync_ResumesUploads()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);

        // Queue a write while fenced — uploads should NOT progress yet.
        var w = await v2.WriteFileAsync(new MemoryStream(RandomBytes(8 * 1024)), "resume.bin");
        Assert.True(w.Success, w.Error?.Message);

        var promote = await v2.PromoteAsync();
        Assert.True(promote.Success, promote.Error?.Message);

        // Poll for a blob file under TailBlobsDir() up to ~5 seconds.
        var sw = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(5);
        bool found = false;
        while (sw.Elapsed < deadline)
        {
            if (Directory.Exists(TailBlobsDir()))
            {
                var blobFiles = Directory.GetFiles(TailBlobsDir(), "*.bin", SearchOption.AllDirectories);
                if (blobFiles.Length > 0)
                {
                    found = true;
                    break;
                }
            }
            await Task.Delay(50);
        }
        Assert.True(found,
            "Expected at least one blob to land at the tail after PromoteAsync unfenced uploads.");
    }

    [Fact]
    public async Task PromoteAsync_OnUnfencedVolume_ReturnsOk()
    {
        await using var volume = await CreateAndRegisterAsync();
        Assert.Equal(VolumeState.Normal, volume.State);

        int beforeInfo = CountInfoFromVolume();
        var promote = await volume.PromoteAsync();
        Assert.True(promote.Success, promote.Error?.Message);
        Assert.Equal(VolumeState.Normal, volume.State);

        // Fast-path: no Info notification published.
        Assert.Equal(beforeInfo, CountInfoFromVolume());
    }

    [Fact]
    public async Task PromoteAsync_IsIdempotent()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);

        int beforeInfo = CountInfoFromVolume();

        var first = await v2.PromoteAsync();
        Assert.True(first.Success, first.Error?.Message);
        Assert.Equal(VolumeState.Normal, v2.State);

        var second = await v2.PromoteAsync();
        Assert.True(second.Success, second.Error?.Message);
        Assert.Equal(VolumeState.Normal, v2.State);

        // Exactly one Info notification across the two calls (second hit the fast-path).
        Assert.Equal(beforeInfo + 1, CountInfoFromVolume());
    }

    [Fact]
    public async Task PromoteAsync_OfflineTails_StillClearsLocalState()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);

        // Manually delete the witness file between reopen and PromoteAsync. The directory may
        // still exist; the file does not. PromoteAsync's WriteAsync to this tail will overwrite
        // (or write fresh); the local-state clear must succeed regardless.
        var witnessPath = Path.Combine(_tailRoot, "_witness", "current.enc");
        if (File.Exists(witnessPath))
        {
            File.Delete(witnessPath);
        }

        var promote = await v2.PromoteAsync();
        Assert.True(promote.Success, promote.Error?.Message);
        Assert.Equal(VolumeState.Normal, v2.State);

        var persisted = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeState");
        Assert.Equal("Normal", persisted);
    }

    [Fact]
    public async Task OpenAsync_AfterPromote_HandshakeKeepsVolumeNormal()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);
        var promote = await v2.PromoteAsync();
        Assert.True(promote.Success, promote.Error?.Message);
        await v2.DisposeAsync();

        // Reopen — the session-begin handshake should keep us Normal and write a clean witness.
        await using var v3 = await ReopenAsync();
        Assert.Equal(VolumeState.Normal, v3.State);

        var witness = await ManuallyReadWitnessAsync();
        Assert.NotNull(witness);
        Assert.False(witness!.Value.ConflictObserved);
    }

    [Fact]
    public async Task PromoteAsync_ThenSimulatedCrash_NextOpenAutoDowngrades()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);
        var promote = await v2.PromoteAsync();
        Assert.True(promote.Success, promote.Error?.Message);
        await v2.DisposeAsync();

        // Simulate a crash AFTER witnesses were written but BEFORE the brain row landed: re-mark
        // the brain as Fenced while leaving the now-clean witness in place.
        await OrchestrationTestHelper.UpsertSettingAsync(
            _skinkRoot, Password, "VolumeState", "Fenced");

        await using var v3 = await ReopenAsync();
        Assert.Equal(VolumeState.Normal, v3.State);

        var persisted = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeState");
        Assert.Equal("Normal", persisted);
    }

    [Fact]
    public async Task PromoteAsync_Notification_UsesUserVocabularyOnly()
    {
        var v1 = await CreateAndRegisterAsync();
        await v1.DisposeAsync();
        await ManuallyWriteWitnessAsync(epoch: 99L, conflictObserved: false);

        await using var v2 = await ReopenAsync();
        Assert.Equal(VolumeState.Fenced, v2.State);

        var promote = await v2.PromoteAsync();
        Assert.True(promote.Success, promote.Error?.Message);

        var info = _bus.Published.Single(n =>
            n.Severity == NotificationSeverity.Info &&
            n.Source == nameof(FlashSkinkVolume) &&
            string.Equals(n.Title, "Conflict resolved", StringComparison.Ordinal));

        string blob = (info.Title + " " + info.Message).ToLowerInvariant();
        Assert.True(
            blob.Contains("skink") || blob.Contains("tail") || blob.Contains("copy")
                || blob.Contains("flashskink"),
            $"User-vocabulary anchor missing from notification: {blob}");
        foreach (var forbidden in new[] {
            "epoch", "witness", "split-brain", "split brain", "fence", "fenced",
            "wal", "stripe", "dek", "aad", "blob",
        })
        {
            Assert.DoesNotContain(forbidden, blob, StringComparison.OrdinalIgnoreCase);
        }
    }
}
