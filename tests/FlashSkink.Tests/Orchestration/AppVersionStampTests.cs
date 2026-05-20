using FlashSkink.Core.Orchestration;
using FlashSkink.Tests.Engine;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Orchestration;

/// <summary>
/// Asserts the brain's app-version stamping behaviour introduced by Refactor PR A:
/// <c>AppVersionCreatedWith</c> is written once at create (immutable across reopens);
/// <c>AppVersionLastOpened</c> / <c>AppVersionLastOpenedUtc</c> are overwritten on
/// every open; legacy <c>AppVersion</c> keys are migrated to
/// <c>AppVersionCreatedWith</c> on first open under the new code; the sentinel
/// <c>"unknown-legacy"</c> is used when both keys are missing.
/// (Blueprint §31.2, §31.3, CLAUDE.md Principle 34.)
/// </summary>
public sealed class AppVersionStampTests : IAsyncLifetime
{
    private string _skinkRoot = string.Empty;
    private const string Password = "test-password-app-version";

    private static VolumeCreationOptions DefaultOptions => new()
    {
        LoggerFactory = NullLoggerFactory.Instance,
        NotificationBus = new RecordingNotificationBus(),
    };

    public Task InitializeAsync()
    {
        _skinkRoot = Path.Combine(Path.GetTempPath(), $"flashskink-ver-test-{Guid.NewGuid():N}");
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
    public async Task AppVersionCreatedWith_IsWrittenOnCreate_FromInformationalVersion()
    {
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try { await receipt.Volume.DisposeAsync(); }
        finally { receipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        var stored = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "AppVersionCreatedWith");

        Assert.NotNull(stored);
        Assert.NotEmpty(stored);
        Assert.Equal(OrchestrationTestHelper.CurrentInformationalVersion, stored);
    }

    [Fact]
    public async Task AppVersionCreatedWith_IsStable_AcrossReopens()
    {
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try { await receipt.Volume.DisposeAsync(); }
        finally { receipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        var atCreate = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "AppVersionCreatedWith");
        SqliteConnection.ClearAllPools();

        var reopen = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(reopen.Success);
        await reopen.Value!.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var afterReopen = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "AppVersionCreatedWith");

        Assert.Equal(atCreate, afterReopen);
    }

    [Fact]
    public async Task AppVersionLastOpened_IsPresent_AndMatchesCurrentVersionOnOpen()
    {
        // In a single-build test run, the open-time value should always equal the
        // current process's informational version. (Cross-build divergence — version A
        // creates, version B opens — is verified by the legacy-migration test below.)
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try { await receipt.Volume.DisposeAsync(); }
        finally { receipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        var reopen = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(reopen.Success);
        await reopen.Value!.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var lastOpened = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "AppVersionLastOpened");

        Assert.NotNull(lastOpened);
        Assert.Equal(OrchestrationTestHelper.CurrentInformationalVersion, lastOpened);
    }

    [Fact]
    public async Task AppVersionLastOpenedUtc_AdvancesAcrossOpens()
    {
        // The seed and backfill helpers both call DateTime.UtcNow directly, so two
        // back-to-back opens with a small wall-clock delay must produce strictly-
        // increasing ISO-8601 timestamps. 50 ms is well above the sub-second
        // resolution of the "O" format string.
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try { await receipt.Volume.DisposeAsync(); }
        finally { receipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        var atCreate = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "AppVersionLastOpenedUtc");
        SqliteConnection.ClearAllPools();

        await Task.Delay(TimeSpan.FromMilliseconds(50));

        var reopen = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(reopen.Success);
        await reopen.Value!.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var afterReopen = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "AppVersionLastOpenedUtc");

        Assert.NotNull(atCreate);
        Assert.NotNull(afterReopen);
        var first = DateTime.Parse(atCreate, null,
            System.Globalization.DateTimeStyles.RoundtripKind);
        var second = DateTime.Parse(afterReopen, null,
            System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(second > first,
            $"Expected last-opened to advance, but {afterReopen} <= {atCreate}.");
    }

    [Fact]
    public async Task LegacyAppVersionKey_IsMigratedTo_AppVersionCreatedWith_OnOpen()
    {
        // Simulate a brain produced by the pre-Refactor-A build: it has the legacy
        // `AppVersion` key but not `AppVersionCreatedWith`. First open under the new
        // code should migrate the value and delete the legacy key.
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try { await receipt.Volume.DisposeAsync(); }
        finally { receipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        await OrchestrationTestHelper.DeleteSettingAsync(
            _skinkRoot, Password, "AppVersionCreatedWith");
        SqliteConnection.ClearAllPools();
        await OrchestrationTestHelper.UpsertSettingAsync(
            _skinkRoot, Password, "AppVersion", "0.0.7-legacy");
        SqliteConnection.ClearAllPools();

        var reopen = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(reopen.Success);
        await reopen.Value!.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var migrated = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "AppVersionCreatedWith");
        SqliteConnection.ClearAllPools();
        var legacyAfter = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "AppVersion");

        Assert.Equal("0.0.7-legacy", migrated);
        Assert.Null(legacyAfter);
    }

    [Fact]
    public async Task BothVersionKeysMissing_BackfillsCreatedWith_AsUnknownLegacySentinel()
    {
        // Corrupt or hand-edited brain: neither `AppVersionCreatedWith` nor the legacy
        // `AppVersion` key is present. Backfill seeds the distinguishable sentinel
        // `"unknown-legacy"` (not `"0.0.0-unknown"`, which is used when MinVer is unwired
        // at create time) so audit logs can tell the two cases apart.
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try { await receipt.Volume.DisposeAsync(); }
        finally { receipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        await OrchestrationTestHelper.DeleteSettingAsync(
            _skinkRoot, Password, "AppVersionCreatedWith");
        SqliteConnection.ClearAllPools();
        await OrchestrationTestHelper.DeleteSettingAsync(
            _skinkRoot, Password, "AppVersion");
        SqliteConnection.ClearAllPools();

        var reopen = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(reopen.Success);
        await reopen.Value!.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var stamped = await OrchestrationTestHelper.ReadSettingAsync(
            _skinkRoot, Password, "AppVersionCreatedWith");

        Assert.Equal("unknown-legacy", stamped);
    }
}
