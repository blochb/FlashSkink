using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Identity;
using FlashSkink.Core.Providers;
using FlashSkink.Tests.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Identity;

/// <summary>
/// Unit tests for <see cref="WitnessHandshake.RunAsync"/>. Uses a real
/// <see cref="WitnessStore"/> backed by one or more <see cref="FileSystemProvider"/>
/// instances rooted at per-test temp dirs (plus <see cref="FaultInjectingStorageProvider"/>
/// wrappers for unreachable-tail and partial-failure scenarios). Dev plan §3.5.2.
/// </summary>
public sealed class WitnessHandshakeTests : IDisposable
{
    private const string TestVolumeId = "test-volume-id";
    private const string TestAppVersion = "0.1.0+handshake-test";

    private readonly string _rootBase;
    private readonly WitnessStore _store;
    private readonly WitnessHandshake _handshake;
    private readonly byte[] _dek;
    private readonly List<string> _tailRoots = new();

    public WitnessHandshakeTests()
    {
        _rootBase = Path.Combine(
            Path.GetTempPath(), "flashskink-handshake-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootBase);
        _store = new WitnessStore(NullLogger<WitnessStore>.Instance);
        _handshake = new WitnessHandshake(_store, NullLogger<WitnessHandshake>.Instance);
        _dek = RandomNumberGenerator.GetBytes(32);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootBase))
        {
            try { Directory.Delete(_rootBase, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private FileSystemProvider NewTail(string providerId)
    {
        var root = Path.Combine(_rootBase, providerId);
        Directory.CreateDirectory(root);
        _tailRoots.Add(root);
        return new FileSystemProvider(
            providerId, $"Display {providerId}", root,
            NullLogger<FileSystemProvider>.Instance);
    }

    private async Task SeedWitnessAsync(
        IStorageProvider provider, long epoch, bool conflictObserved, string? volumeId = null)
    {
        var payload = new WitnessPayload(
            VolumeId: volumeId ?? TestVolumeId,
            Epoch: epoch,
            SessionId: Guid.NewGuid().ToString("D"),
            CommittedAtUtc: DateTime.UtcNow.ToString("O"),
            Host: "seed-host",
            AppVersion: "seed-version",
            ConflictObserved: conflictObserved);

        var result = await _store.WriteAsync(provider, _dek, payload, CancellationToken.None);
        Assert.True(result.Success, $"Seed write failed: {result.Error?.Code}");
    }

    // ── No-conflict paths ────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoTails_ReturnsNoConflictAndNoWrites()
    {
        var result = await _handshake.RunAsync(
            tails: Array.Empty<(string, IStorageProvider)>(),
            volumeId: TestVolumeId,
            newEpoch: 5L,
            appVersion: TestAppVersion,
            dek: _dek,
            alreadyFenced: false,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value.ConflictDetected);
        Assert.Equal(ConflictTrigger.None, result.Value.Trigger);
        Assert.Null(result.Value.ConflictingProviderId);
        Assert.Null(result.Value.ConflictWitness);
        Assert.Equal(0, result.Value.TailsRead);
        Assert.Equal(0, result.Value.TailsWritten);
    }

    [Fact]
    public async Task RunAsync_NoPriorWitness_WritesWitness_ReturnsNoConflict()
    {
        var tail = NewTail("tail-1");
        var tails = new[] { ("tail-1", (IStorageProvider)tail) };

        var result = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value.ConflictDetected);
        Assert.Equal(1, result.Value.TailsRead);
        Assert.Equal(1, result.Value.TailsWritten);

        // Verify the written witness has the handshake's epoch and ConflictObserved=false.
        var readBack = await _store.TryReadAsync(tail, _dek, CancellationToken.None);
        Assert.True(readBack.Success);
        Assert.NotNull(readBack.Value);
        Assert.Equal(5L, readBack.Value!.Value.Epoch);
        Assert.False(readBack.Value!.Value.ConflictObserved);
        Assert.Equal(TestVolumeId, readBack.Value!.Value.VolumeId);
    }

    [Fact]
    public async Task RunAsync_WitnessEpochLower_ReturnsNoConflict_WritesNewEpoch()
    {
        var tail = NewTail("tail-1");
        await SeedWitnessAsync(tail, epoch: 3L, conflictObserved: false);
        var tails = new[] { ("tail-1", (IStorageProvider)tail) };

        var result = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value.ConflictDetected);
        Assert.Equal(ConflictTrigger.None, result.Value.Trigger);

        var readBack = await _store.TryReadAsync(tail, _dek, CancellationToken.None);
        Assert.True(readBack.Success);
        Assert.Equal(5L, readBack.Value!.Value.Epoch);
        Assert.False(readBack.Value!.Value.ConflictObserved);
    }

    // ── Trigger A (epoch comparison) ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WitnessEpochEqual_ReturnsConflict_EpochComparison()
    {
        var tail = NewTail("tail-1");
        await SeedWitnessAsync(tail, epoch: 5L, conflictObserved: false);
        var tails = new[] { ("tail-1", (IStorageProvider)tail) };

        var result = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Value.ConflictDetected);
        Assert.Equal(ConflictTrigger.EpochComparison, result.Value.Trigger);
        Assert.Equal("tail-1", result.Value.ConflictingProviderId);

        // After a conflict-detecting run, the rewritten witness has ConflictObserved=true.
        var readBack = await _store.TryReadAsync(tail, _dek, CancellationToken.None);
        Assert.True(readBack.Success);
        Assert.True(readBack.Value!.Value.ConflictObserved);
    }

    [Fact]
    public async Task RunAsync_WitnessEpochHigher_ReturnsConflict_EpochComparison()
    {
        var tail = NewTail("tail-1");
        await SeedWitnessAsync(tail, epoch: 7L, conflictObserved: false);
        var tails = new[] { ("tail-1", (IStorageProvider)tail) };

        var result = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Value.ConflictDetected);
        Assert.Equal(ConflictTrigger.EpochComparison, result.Value.Trigger);
        Assert.Equal(7L, result.Value.ConflictWitness!.Value.Epoch);
    }

    // ── Trigger B (conflict marker — meta-plan item 12) ──────────────────────

    [Fact]
    public async Task RunAsync_ConflictMarkerLowerEpoch_ReturnsConflict_ConflictMarker()
    {
        var tail = NewTail("tail-1");
        // Lagger scenario: tail's witness epoch is BELOW our newEpoch, but carries
        // ConflictObserved=true from a prior winner. Without Trigger B, the lagger would
        // never learn it was the lagger.
        await SeedWitnessAsync(tail, epoch: 2L, conflictObserved: true);
        var tails = new[] { ("tail-1", (IStorageProvider)tail) };

        var result = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Value.ConflictDetected);
        Assert.Equal(ConflictTrigger.ConflictMarker, result.Value.Trigger);
        Assert.Equal("tail-1", result.Value.ConflictingProviderId);
    }

    // ── Two-sided fence — write phase stamps ConflictObserved ────────────────

    [Fact]
    public async Task RunAsync_ConflictDetected_WritesWitnessWithConflictObservedTrue_OnAllTails()
    {
        var tail1 = NewTail("tail-1");
        var tail2 = NewTail("tail-2");
        await SeedWitnessAsync(tail1, epoch: 9L, conflictObserved: false);
        // tail-2 has no prior witness.
        var tails = new[]
        {
            ("tail-1", (IStorageProvider)tail1),
            ("tail-2", (IStorageProvider)tail2),
        };

        var result = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Value.ConflictDetected);
        Assert.Equal(2, result.Value.TailsWritten);

        foreach (var tail in new[] { tail1, tail2 })
        {
            var readBack = await _store.TryReadAsync(tail, _dek, CancellationToken.None);
            Assert.True(readBack.Value!.Value.ConflictObserved);
        }
    }

    [Fact]
    public async Task RunAsync_AlreadyFenced_NoConflictOnTail_WritesConflictObservedTrue()
    {
        var tail = NewTail("tail-1");
        await SeedWitnessAsync(tail, epoch: 1L, conflictObserved: false);
        var tails = new[] { ("tail-1", (IStorageProvider)tail) };

        var result = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value.ConflictDetected);
        Assert.Equal(ConflictTrigger.None, result.Value.Trigger);

        var readBack = await _store.TryReadAsync(tail, _dek, CancellationToken.None);
        Assert.True(readBack.Value!.Value.ConflictObserved);
        Assert.Equal(5L, readBack.Value!.Value.Epoch);
    }

    [Fact]
    public async Task RunAsync_AlreadyFenced_NoTails_WritesNothing()
    {
        var result = await _handshake.RunAsync(
            tails: Array.Empty<(string, IStorageProvider)>(),
            volumeId: TestVolumeId,
            newEpoch: 5L,
            appVersion: TestAppVersion,
            dek: _dek,
            alreadyFenced: true,
            ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value.ConflictDetected);
        Assert.Equal(0, result.Value.TailsWritten);
    }

    // ── Partial failure paths ────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_UnreachableTail_SkipsAndReturnsNoConflict()
    {
        var inner = NewTail("tail-1");
        var faulty = new FaultInjectingStorageProvider(inner);
        faulty.FailNextListWith(ErrorCode.ProviderUnreachable);
        var tails = new[] { ("tail-1", (IStorageProvider)faulty) };

        var result = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value.ConflictDetected);
        Assert.Equal(0, result.Value.TailsRead);
        // Write phase fires regardless — the inner provider accepts it.
        Assert.Equal(1, result.Value.TailsWritten);
    }

    [Fact]
    public async Task RunAsync_WriteFailureToOneTail_LoggedNotFatal()
    {
        var inner1 = NewTail("tail-1");
        var inner2 = NewTail("tail-2");
        var faulty1 = new FaultInjectingStorageProvider(inner1);
        faulty1.FailNextBeginWith(ErrorCode.ProviderUnreachable);
        var tails = new[]
        {
            ("tail-1", (IStorageProvider)faulty1),
            ("tail-2", (IStorageProvider)inner2),
        };

        var result = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value.ConflictDetected);
        Assert.Equal(2, result.Value.TailsRead);
        Assert.Equal(1, result.Value.TailsWritten);
    }

    [Fact]
    public async Task RunAsync_PartiallyUnreachable_ConflictsOnReachableTail_ReturnsConflict()
    {
        var inner1 = NewTail("tail-1");
        var inner2 = NewTail("tail-2");
        var faulty1 = new FaultInjectingStorageProvider(inner1);
        faulty1.FailNextListWith(ErrorCode.ProviderUnreachable);
        await SeedWitnessAsync(inner2, epoch: 99L, conflictObserved: false);
        var tails = new[]
        {
            ("tail-1", (IStorageProvider)faulty1),
            ("tail-2", (IStorageProvider)inner2),
        };

        var result = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Value.ConflictDetected);
        Assert.Equal(ConflictTrigger.EpochComparison, result.Value.Trigger);
        Assert.Equal("tail-2", result.Value.ConflictingProviderId);
    }

    [Fact]
    public async Task RunAsync_FirstTriggerWins_DeterministicConflictAttribution()
    {
        var tail1 = NewTail("tail-1");
        var tail2 = NewTail("tail-2");
        var tail3 = NewTail("tail-3");
        await SeedWitnessAsync(tail1, epoch: 99L, conflictObserved: false);
        await SeedWitnessAsync(tail2, epoch: 99L, conflictObserved: false);
        await SeedWitnessAsync(tail3, epoch: 99L, conflictObserved: false);

        var orderA = new[]
        {
            ("tail-1", (IStorageProvider)tail1),
            ("tail-2", (IStorageProvider)tail2),
            ("tail-3", (IStorageProvider)tail3),
        };
        var resultA = await _handshake.RunAsync(
            orderA, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);
        Assert.Equal("tail-1", resultA.Value.ConflictingProviderId);

        // The conflict-detecting run above stamps all tails with ConflictObserved=true and
        // epoch 5. Re-seed with the original epoch=99 setup before re-running with the
        // reversed order.
        await SeedWitnessAsync(tail1, epoch: 99L, conflictObserved: false);
        await SeedWitnessAsync(tail2, epoch: 99L, conflictObserved: false);
        await SeedWitnessAsync(tail3, epoch: 99L, conflictObserved: false);
        var orderB = new[]
        {
            ("tail-3", (IStorageProvider)tail3),
            ("tail-2", (IStorageProvider)tail2),
            ("tail-1", (IStorageProvider)tail1),
        };
        var resultB = await _handshake.RunAsync(
            orderB, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);
        Assert.Equal("tail-3", resultB.Value.ConflictingProviderId);
    }

    [Fact]
    public async Task RunAsync_VolumeIdMismatch_SkipsTail_NoConflict()
    {
        var tail = NewTail("tail-1");
        await SeedWitnessAsync(
            tail, epoch: 99L, conflictObserved: false, volumeId: "different-volume-id");
        var tails = new[] { ("tail-1", (IStorageProvider)tail) };

        var result = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value.ConflictDetected);
        Assert.Equal(1, result.Value.TailsRead);
        Assert.Equal(1, result.Value.TailsWritten);

        // The rewritten witness now carries the handshake's volumeId.
        var readBack = await _store.TryReadAsync(tail, _dek, CancellationToken.None);
        Assert.Equal(TestVolumeId, readBack.Value!.Value.VolumeId);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CancelledBeforeStart_ReturnsCancelled()
    {
        var tail = NewTail("tail-1");
        var tails = new[] { ("tail-1", (IStorageProvider)tail) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }
}
