using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Orchestration;
using FlashSkink.Tests.Engine;
using FlashSkink.Tests.Orchestration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Identity;

/// <summary>
/// Integration tests for the <c>VolumeEpoch</c> seed + increment story and for
/// <see cref="FlashSkink.Core.Identity.VolumeState"/> read paths in
/// <c>BackfillAndStampOnOpenAsync</c>. Dev plan §3.5.1.
/// </summary>
public sealed class VolumeEpochTests : IAsyncLifetime
{
    private string _skinkRoot = string.Empty;
    private const string Password = "test-password-volume-epoch";

    private static VolumeCreationOptions DefaultOptions => new()
    {
        LoggerFactory = NullLoggerFactory.Instance,
        NotificationBus = new RecordingNotificationBus(),
    };

    public Task InitializeAsync()
    {
        _skinkRoot = Path.Combine(
            Path.GetTempPath(), $"flashskink-epoch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_skinkRoot);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_skinkRoot, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    private async Task CreateAndDisposeVolumeAsync()
    {
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try
        {
            await receipt.Volume.DisposeAsync();
        }
        finally
        {
            receipt.RecoveryPhrase.Dispose();
        }
        SqliteConnection.ClearAllPools();
    }

    private async Task OpenAndDisposeVolumeAsync()
    {
        var openResult = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(openResult.Success);
        await openResult.Value!.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    // ── Epoch seed + increment ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_SeedsEpochAtOne()
    {
        await CreateAndDisposeVolumeAsync();

        var epochRaw = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeEpoch");

        Assert.Equal("1", epochRaw);
    }

    [Fact]
    public async Task OpenAsync_IncrementsEpoch_FromOneToTwo()
    {
        await CreateAndDisposeVolumeAsync();
        await OpenAndDisposeVolumeAsync();

        var epochRaw = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeEpoch");

        Assert.Equal("2", epochRaw);
    }

    [Fact]
    public async Task OpenAsync_IncrementsAgainOnSecondReopen()
    {
        await CreateAndDisposeVolumeAsync();
        await OpenAndDisposeVolumeAsync();
        await OpenAndDisposeVolumeAsync();

        var epochRaw = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeEpoch");

        Assert.Equal("3", epochRaw);
    }

    [Fact]
    public async Task OpenAsync_LegacyBrainMissingEpochRow_BackfillsToOne()
    {
        await CreateAndDisposeVolumeAsync();

        // Simulate a legacy brain by deleting the seeded VolumeEpoch row before reopen.
        await OrchestrationTestHelper.DeleteSettingAsync(_skinkRoot, Password, "VolumeEpoch");
        SqliteConnection.ClearAllPools();

        await OpenAndDisposeVolumeAsync();

        var epochRaw = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeEpoch");

        // Missing → treated as 0 → newEpoch = 1 written.
        Assert.Equal("1", epochRaw);
    }

    [Fact]
    public async Task OpenAsync_GarbageEpochValue_FailsOpen()
    {
        await CreateAndDisposeVolumeAsync();
        await OrchestrationTestHelper.UpsertSettingAsync(
            _skinkRoot, Password, "VolumeEpoch", "not-a-number");
        SqliteConnection.ClearAllPools();

        var openResult = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);

        // Defensive parse is strict for VolumeEpoch — the epoch IS the conflict-detection
        // signal, and silently zeroing a corrupted value would mask divergence.
        Assert.False(openResult.Success);
        Assert.Equal(ErrorCode.DatabaseReadFailed, openResult.Error!.Code);
    }

    // ── VolumeState read paths (handshake enforcement comes in §3.5.2) ──────────────────────

    [Fact]
    public async Task OpenAsync_VolumeStateAbsentRow_OpenSucceeds()
    {
        // Brand-new volume has no VolumeState row (§3.5.1 does not seed it). Open should
        // succeed cleanly; the backfill helper interprets absence as Normal.
        await CreateAndDisposeVolumeAsync();

        var stateRaw = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeState");
        Assert.Null(stateRaw);

        SqliteConnection.ClearAllPools();
        var openResult = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(openResult.Success);
        await openResult.Value!.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_VolumeStateGarbageValue_OpenSucceeds()
    {
        await CreateAndDisposeVolumeAsync();
        await OrchestrationTestHelper.UpsertSettingAsync(
            _skinkRoot, Password, "VolumeState", "not-a-state");
        SqliteConnection.ClearAllPools();

        // Defensive parse: garbage → Normal → open proceeds without error.
        var openResult = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);

        Assert.True(openResult.Success);
        await openResult.Value!.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_VolumeStateFencedValue_OpenSucceeds()
    {
        await CreateAndDisposeVolumeAsync();
        await OrchestrationTestHelper.UpsertSettingAsync(
            _skinkRoot, Password, "VolumeState", "Fenced");
        SqliteConnection.ClearAllPools();

        // §3.5.1 only reads VolumeState — there is no enforcement yet. The open succeeds;
        // the §3.5.2 handshake will be what acts on the Fenced state.
        var openResult = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);

        Assert.True(openResult.Success);
        await openResult.Value!.DisposeAsync();
    }

    [Fact]
    public async Task BackfillAndStampOnOpenAsync_DoesNotMutateVolumeState()
    {
        // The handshake (§3.5.2) owns Settings["VolumeState"] writes. §3.5.1's backfill
        // only reads the row; running an open against a brain with VolumeState="Fenced"
        // must leave the row untouched.
        await CreateAndDisposeVolumeAsync();
        await OrchestrationTestHelper.UpsertSettingAsync(
            _skinkRoot, Password, "VolumeState", "Fenced");
        SqliteConnection.ClearAllPools();

        await OpenAndDisposeVolumeAsync();

        var stateRaw = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeState");
        Assert.Equal("Fenced", stateRaw);
    }
}
