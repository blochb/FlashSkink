using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Orchestration;
using FlashSkink.Tests.Engine;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Orchestration;

/// <summary>
/// Integration tests that exercise the single-instance lock through
/// <see cref="FlashSkinkVolume.CreateAsync"/>, <see cref="FlashSkinkVolume.OpenAsync"/>,
/// and <see cref="FlashSkinkVolume.DisposeAsync"/>. The primitive itself is unit-tested
/// in <c>tests/FlashSkink.Tests/Storage/InstanceLockTests.cs</c>; this suite asserts the
/// wiring across the orchestrator: lock acquired on open, released on dispose, blocks a
/// second concurrent open with <see cref="ErrorCode.SingleInstanceLockHeld"/>, and a
/// failed open does not leak the lock. (Blueprint §19.5, CLAUDE.md Principle 35;
/// Refactor PR B.)
/// </summary>
public sealed class SingleInstanceTests : IAsyncLifetime
{
    private string _skinkRoot = string.Empty;
    private const string Password = "test-password-single-instance";

    private static VolumeCreationOptions DefaultOptions => new()
    {
        LoggerFactory = NullLoggerFactory.Instance,
        NotificationBus = new RecordingNotificationBus(),
    };

    private string LockFilePath => Path.Combine(_skinkRoot, ".flashskink", "instance.lock");

    public Task InitializeAsync()
    {
        _skinkRoot = Path.Combine(
            Path.GetTempPath(), $"flashskink-singleinst-test-{Guid.NewGuid():N}");
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
    public async Task CreateAsync_AcquiresLock_LockFileExistsOnDisk()
    {
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;

        try
        {
            Assert.True(File.Exists(LockFilePath),
                $"Expected lock file at {LockFilePath} while volume is open.");
        }
        finally
        {
            try { await receipt.Volume.DisposeAsync(); }
            finally { receipt.RecoveryPhrase.Dispose(); }
        }
    }

    [Fact]
    public async Task OpenAsync_WhileVolumeOpen_SecondOpen_ReturnsSingleInstanceLockHeld()
    {
        // First, create + dispose so vault.bin and brain.db exist.
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try { await receipt.Volume.DisposeAsync(); }
        finally { receipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        // Open the volume — first holder.
        var firstOpen = await FlashSkinkVolume.OpenAsync(
            _skinkRoot, Password, DefaultOptions);
        Assert.True(firstOpen.Success);

        try
        {
            // Second OpenAsync on the same root must fail with SingleInstanceLockHeld.
            var secondOpen = await FlashSkinkVolume.OpenAsync(
                _skinkRoot, Password, DefaultOptions);

            Assert.False(secondOpen.Success);
            Assert.NotNull(secondOpen.Error);
            Assert.Equal(ErrorCode.SingleInstanceLockHeld, secondOpen.Error!.Code);
            Assert.NotNull(secondOpen.Error.Metadata);
            Assert.Equal(
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                secondOpen.Error.Metadata!["Pid"]);
            Assert.Equal(Environment.MachineName, secondOpen.Error.Metadata["Host"]);
        }
        finally
        {
            await firstOpen.Value!.DisposeAsync();
        }
    }

    [Fact]
    public async Task OpenAsync_AfterFirstDispose_SucceedsOnSecondCall()
    {
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try { await receipt.Volume.DisposeAsync(); }
        finally { receipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        var firstOpen = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(firstOpen.Success);
        await firstOpen.Value!.DisposeAsync();
        SqliteConnection.ClearAllPools();

        var secondOpen = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(secondOpen.Success);
        await secondOpen.Value!.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_WithForceUnlock_RecoversFromStaleLockFile()
    {
        // Create + dispose so vault.bin / brain.db exist.
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try { await receipt.Volume.DisposeAsync(); }
        finally { receipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        // Simulate a stale lock file (no live handle) left by a crashed prior process.
        File.WriteAllText(LockFilePath,
            "{\"pid\":999999,\"host\":\"stale-host\",\"startedAtUtc\":\"2020-01-01T00:00:00Z\",\"appVersion\":\"old\"}");

        // VolumeCreationOptions is a sealed class with init-only properties, not a record,
        // so a `with` expression isn't available — construct a fresh instance with
        // ForceUnlock = true.
        var forceOptions = new VolumeCreationOptions
        {
            LoggerFactory = NullLoggerFactory.Instance,
            NotificationBus = new RecordingNotificationBus(),
            ForceUnlock = true,
        };

        var result = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, forceOptions);
        Assert.True(result.Success);
        await result.Value!.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_ReleasesLock_LockFileIsDeleted()
    {
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try
        {
            Assert.True(File.Exists(LockFilePath));
            await receipt.Volume.DisposeAsync();
        }
        finally
        {
            receipt.RecoveryPhrase.Dispose();
        }
        SqliteConnection.ClearAllPools();

        // After clean dispose, the lock file is deleted (best-effort cleanup in
        // InstanceLock.DisposeAsync). A stale file remaining would not cause a
        // correctness problem — but the contract is "clean shutdown deletes it".
        Assert.False(File.Exists(LockFilePath),
            $"Expected lock file at {LockFilePath} to be deleted after DisposeAsync.");
    }

    [Fact]
    public async Task Failed_OpenAsync_ReleasesLock_OnFailurePath()
    {
        // Create with one password, dispose, then attempt to open with a wrong password.
        // OpenAsync acquires the lock first, then tries vault unlock — the unlock fails,
        // and the finally block must release the lock so a retry can succeed.
        var receipt = (await FlashSkinkVolume.CreateAsync(
            _skinkRoot, Password, DefaultOptions)).Value!;
        try { await receipt.Volume.DisposeAsync(); }
        finally { receipt.RecoveryPhrase.Dispose(); }
        SqliteConnection.ClearAllPools();

        var badOpen = await FlashSkinkVolume.OpenAsync(
            _skinkRoot, "WRONG-PASSWORD", DefaultOptions);
        Assert.False(badOpen.Success);
        Assert.Equal(ErrorCode.InvalidPassword, badOpen.Error!.Code);
        SqliteConnection.ClearAllPools();

        // The failure-path finally block must have released the lock — a retry with the
        // correct password should now succeed.
        Assert.False(File.Exists(LockFilePath),
            "Failed OpenAsync should clean up the lock file via its finally block.");
        var retry = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions);
        Assert.True(retry.Success);
        await retry.Value!.DisposeAsync();
    }
}
