using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Identity;
using FlashSkink.Core.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Identity;

/// <summary>
/// Edge-case behaviour tests for the witness protocol — meta-plan items 13–17 (truncated
/// envelopes, manual deletion, same-phrase separate init, restore-from-image, clock skew).
/// These are "documentation-as-tests" pinning behaviour that emerges from
/// <see cref="WitnessStore"/> + <see cref="WitnessHandshake"/> without requiring any
/// additional product code. Blueprint §19.6, §19.8.
/// </summary>
public sealed class WitnessEdgeCaseTests : IDisposable
{
    private const string TestVolumeId = "test-volume-id";
    private const string TestAppVersion = "0.1.0+edge-case-test";

    private readonly string _rootBase;
    private readonly WitnessStore _store;
    private readonly WitnessHandshake _handshake;
    private readonly byte[] _dek;
    private readonly List<string> _tailRoots = new();

    public WitnessEdgeCaseTests()
    {
        _rootBase = Path.Combine(
            Path.GetTempPath(), "flashskink-edge-case-tests", Guid.NewGuid().ToString("N"));
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

    private static string WitnessPath(string tailRoot) =>
        Path.Combine(tailRoot, "_witness", "current.enc");

    // ── Meta-plan item 13 — truncated envelope ───────────────────────────────

    [Fact]
    public async Task PartialWitnessFile_TruncatedDuringWrite_TreatedAsAbsent()
    {
        var tail = NewTail("tail-1");
        var tailRoot = _tailRoots[^1];

        // Manually write a truncated envelope. Less than 1 (version) + 12 (nonce) + 16 (tag).
        Directory.CreateDirectory(Path.Combine(tailRoot, "_witness"));
        await File.WriteAllBytesAsync(WitnessPath(tailRoot), new byte[10]);

        var read = await _store.TryReadAsync(tail, _dek, CancellationToken.None);
        Assert.True(read.Success);
        Assert.Null(read.Value);

        // Now run the handshake — it should treat the truncated file as no-witness, not as a
        // conflict, and write a fresh witness with the new epoch.
        var tails = new[] { ("tail-1", (IStorageProvider)tail) };
        var run = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);
        Assert.True(run.Success);
        Assert.False(run.Value.ConflictDetected);

        var readBack = await _store.TryReadAsync(tail, _dek, CancellationToken.None);
        Assert.True(readBack.Success);
        Assert.NotNull(readBack.Value);
        Assert.Equal(5L, readBack.Value!.Value.Epoch);
        Assert.False(readBack.Value!.Value.ConflictObserved);
    }

    // ── Meta-plan item 14 — manual deletion ──────────────────────────────────

    [Fact]
    public async Task WitnessDeletion_ManualDelete_TreatedAsFirstUse()
    {
        var tail = NewTail("tail-1");
        var tailRoot = _tailRoots[^1];

        await SeedWitnessAsync(tail, epoch: 3L, conflictObserved: false);
        File.Delete(WitnessPath(tailRoot));

        var tails = new[] { ("tail-1", (IStorageProvider)tail) };
        var run = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);
        Assert.True(run.Success);
        Assert.False(run.Value.ConflictDetected);
        Assert.Equal(1, run.Value.TailsRead);
        Assert.Equal(1, run.Value.TailsWritten);

        var readBack = await _store.TryReadAsync(tail, _dek, CancellationToken.None);
        Assert.True(readBack.Success);
        Assert.NotNull(readBack.Value);
        Assert.Equal(5L, readBack.Value!.Value.Epoch);
        Assert.False(readBack.Value!.Value.ConflictObserved);
    }

    // ── Meta-plan item 15 — same recovery phrase, separate init ──────────────

    [Fact]
    public async Task SamePhraseSeperateInit_DifferentVolumeIds_NoWitnessInteraction()
    {
        var tail = NewTail("tail-1");
        // A "foreign" volume's witness — different VolumeId on the same physical tail (a shared
        // provider account the user reused for two separately-initialised skinks).
        await SeedWitnessAsync(
            tail, epoch: 99L, conflictObserved: true,
            volumeId: "different-volume-id-altogether");

        var tails = new[] { ("tail-1", (IStorageProvider)tail) };
        var run = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(run.Success);
        Assert.False(run.Value.ConflictDetected);
        // VolumeId mismatch → handshake does NOT count this tail as read evidence (so the
        // auto-downgrade gate cannot use it as confirmation). The write phase still runs.
        Assert.Equal(0, run.Value.TailsRead);
        Assert.Equal(1, run.Value.TailsWritten);

        // The witness on the shared tail now carries our VolumeId.
        var readBack = await _store.TryReadAsync(tail, _dek, CancellationToken.None);
        Assert.True(readBack.Success);
        Assert.Equal(TestVolumeId, readBack.Value!.Value.VolumeId);
    }

    // ── Meta-plan item 16 — restore from image ───────────────────────────────

    [Fact]
    public async Task RestoreFromImage_IdenticalVolumeIdAndEpoch_DetectedOnNextOpen()
    {
        var tail = NewTail("tail-1");
        // The restored skink and the live skink share VolumeId AND the seeded VolumeEpoch.
        await SeedWitnessAsync(tail, epoch: 5L, conflictObserved: false);

        var tails = new[] { ("tail-1", (IStorageProvider)tail) };
        var run = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 5L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(run.Success);
        Assert.True(run.Value.ConflictDetected);
        Assert.Equal(ConflictTrigger.EpochComparison, run.Value.Trigger);
        Assert.Equal("tail-1", run.Value.ConflictingProviderId);
    }

    // ── Meta-plan item 17 — clock skew ───────────────────────────────────────

    [Fact]
    public async Task ClockSkew_LargeTimestampDifference_DoesNotAffectConflictDetection()
    {
        // Symmetric check: a far-future timestamp AND a far-past timestamp both leave the
        // conflict-detection outcome determined by Epoch alone.
        await AssertNoConflictForTimestampAsync(DateTime.UtcNow.AddYears(100));
        await AssertNoConflictForTimestampAsync(DateTime.UtcNow.AddYears(-100));
    }

    private async Task AssertNoConflictForTimestampAsync(DateTime committedAtUtc)
    {
        var providerId = $"tail-{Guid.NewGuid():N}";
        var tail = NewTail(providerId);

        var payload = new WitnessPayload(
            VolumeId: TestVolumeId,
            Epoch: 1L,
            SessionId: Guid.NewGuid().ToString("D"),
            CommittedAtUtc: committedAtUtc.ToString("O"),
            Host: "seed-host",
            AppVersion: "seed-version",
            ConflictObserved: false);

        var write = await _store.WriteAsync(tail, _dek, payload, CancellationToken.None);
        Assert.True(write.Success, write.Error?.Code.ToString());

        var tails = new[] { (providerId, (IStorageProvider)tail) };
        var run = await _handshake.RunAsync(
            tails, TestVolumeId, newEpoch: 2L, TestAppVersion, _dek,
            alreadyFenced: false, CancellationToken.None);

        Assert.True(run.Success);
        Assert.False(run.Value.ConflictDetected);
        Assert.Equal(ConflictTrigger.None, run.Value.Trigger);
    }
}
