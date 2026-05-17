using FlashSkink.Core.Orchestration;
using FlashSkink.Tests.Engine;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Orchestration;

/// <summary>
/// Asserts the volume-identity invariants introduced by Refactor PR A:
/// <c>VolumeID</c> is written at create time, persists across reopens, is unique
/// per volume, is backfilled on legacy brains that lack it, and is stable once
/// backfilled. (Blueprint §31.1, CLAUDE.md Principle 33.)
/// </summary>
public sealed class VolumeIdentityTests : IAsyncLifetime
{
    private string _skinkRoot = string.Empty;
    private const string Password = "test-password-volume-id";

    private static VolumeCreationOptions DefaultOptions => new()
    {
        LoggerFactory = NullLoggerFactory.Instance,
        NotificationBus = new RecordingNotificationBus(),
    };

    public Task InitializeAsync()
    {
        _skinkRoot = Path.Combine(Path.GetTempPath(), $"flashskink-id-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_skinkRoot);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_skinkRoot, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task VolumeID_IsWrittenOnCreate_AsValidGuid()
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

        var stored = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeID");

        Assert.NotNull(stored);
        Assert.True(Guid.TryParse(stored, out var parsed));
        Assert.NotEqual(Guid.Empty, parsed);
    }

    [Fact]
    public async Task VolumeID_IsStable_AcrossCloseAndReopen()
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

        var firstRead = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeID");
        SqliteConnection.ClearAllPools();

        // Reopen the volume; backfill helper should observe the existing VolumeID and not
        // overwrite it.
        var reopen = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(reopen.Success);
        await reopen.Value!.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var secondRead = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeID");

        Assert.Equal(firstRead, secondRead);
    }

    [Fact]
    public async Task VolumeID_IsUnique_AcrossSeparateVolumes()
    {
        var secondRoot = Path.Combine(
            Path.GetTempPath(), $"flashskink-id-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(secondRoot);
        try
        {
            var firstReceipt = (await FlashSkinkVolume.CreateAsync(
                _skinkRoot, Password, DefaultOptions)).Value!;
            try { await firstReceipt.Volume.DisposeAsync(); }
            finally { firstReceipt.RecoveryPhrase.Dispose(); }
            SqliteConnection.ClearAllPools();

            var secondReceipt = (await FlashSkinkVolume.CreateAsync(
                secondRoot, Password, DefaultOptions)).Value!;
            try { await secondReceipt.Volume.DisposeAsync(); }
            finally { secondReceipt.RecoveryPhrase.Dispose(); }
            SqliteConnection.ClearAllPools();

            var firstId = await OrchestrationTestHelper.ReadSettingAsync(
                _skinkRoot, Password, "VolumeID");
            SqliteConnection.ClearAllPools();
            var secondId = await OrchestrationTestHelper.ReadSettingAsync(
                secondRoot, Password, "VolumeID");

            Assert.NotNull(firstId);
            Assert.NotNull(secondId);
            Assert.NotEqual(firstId, secondId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(secondRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task VolumeID_IsBackfilledOnOpen_WhenMissingFromLegacyBrain()
    {
        // Simulate a brain created by a pre-Refactor-A build: it has no VolumeID row.
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try { await receipt.Volume.DisposeAsync(); }
        finally { receipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        await OrchestrationTestHelper.DeleteSettingAsync(_skinkRoot, Password, "VolumeID");
        SqliteConnection.ClearAllPools();

        Assert.Null(await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeID"));
        SqliteConnection.ClearAllPools();

        // Reopen — backfill should populate VolumeID.
        var reopen = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(reopen.Success);
        await reopen.Value!.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var backfilled = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeID");

        Assert.NotNull(backfilled);
        Assert.True(Guid.TryParse(backfilled, out var parsed));
        Assert.NotEqual(Guid.Empty, parsed);
    }

    [Fact]
    public async Task VolumeID_OnceBackfilled_DoesNotChange_OnSubsequentOpen()
    {
        // Create, dispose, delete VolumeID, reopen (backfill), record value, dispose,
        // reopen again — the second open must not regenerate the GUID.
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try { await receipt.Volume.DisposeAsync(); }
        finally { receipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        await OrchestrationTestHelper.DeleteSettingAsync(_skinkRoot, Password, "VolumeID");
        SqliteConnection.ClearAllPools();

        var firstOpen = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(firstOpen.Success);
        await firstOpen.Value!.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var backfilledValue = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeID");
        SqliteConnection.ClearAllPools();

        var secondOpen = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(secondOpen.Success);
        await secondOpen.Value!.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var stableValue = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "VolumeID");

        Assert.Equal(backfilledValue, stableValue);
    }
}
