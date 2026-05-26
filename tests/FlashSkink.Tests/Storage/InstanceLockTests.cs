using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Storage;
using Xunit;

namespace FlashSkink.Tests.Storage;

/// <summary>
/// Unit tests for the <see cref="InstanceLock"/> primitive itself — exercises the
/// <c>FileShare.None</c>-based exclusion, the manifest write/read, the force-unlock
/// path, and cancellation. Integration through <see cref="FlashSkink.Core.Orchestration.FlashSkinkVolume"/>
/// lives in <c>tests/FlashSkink.Tests/Orchestration/SingleInstanceTests.cs</c>.
/// (Blueprint §19.5, CLAUDE.md Principle 35; Refactor PR B.)
/// </summary>
public sealed class InstanceLockTests : IDisposable
{
    private readonly string _skinkRoot;
    private readonly string _flashskinkDir;
    private readonly string _lockFilePath;
    private readonly string _manifestFilePath;
    private const string TestAppVersion = "0.1.0+test";

    public InstanceLockTests()
    {
        _skinkRoot = Path.Combine(
            Path.GetTempPath(), $"flashskink-lock-test-{Guid.NewGuid():N}");
        _flashskinkDir = Path.Combine(_skinkRoot, ".flashskink");
        _lockFilePath = Path.Combine(_flashskinkDir, "instance.lock");
        _manifestFilePath = Path.Combine(_flashskinkDir, "instance.manifest");
        Directory.CreateDirectory(_flashskinkDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_skinkRoot))
        {
            try { Directory.Delete(_skinkRoot, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task AcquireAsync_FirstCall_ReturnsOk_AndCreatesLockFile()
    {
        var result = await InstanceLock.AcquireAsync(
            _skinkRoot, TestAppVersion, force: false, logger: null, ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.True(File.Exists(_lockFilePath));
        Assert.True(File.Exists(_manifestFilePath));
        Assert.Equal(_lockFilePath, result.AssertValue().LockFilePath);
        Assert.Equal(_manifestFilePath, result.Value.ManifestFilePath);

        await result.Value.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_SecondCall_WhileFirstHeld_ReturnsSingleInstanceLockHeld()
    {
        var first = await InstanceLock.AcquireAsync(
            _skinkRoot, TestAppVersion, force: false, logger: null, ct: CancellationToken.None);
        Assert.True(first.Success);

        try
        {
            var second = await InstanceLock.AcquireAsync(
                _skinkRoot, TestAppVersion, force: false, logger: null, ct: CancellationToken.None);

            Assert.False(second.Success);
            Assert.NotNull(second.Error);
            Assert.Equal(ErrorCode.SingleInstanceLockHeld, second.AssertError().Code);
            Assert.NotNull(second.Error.Metadata);
            // First holder is this process — pid in metadata should match Environment.ProcessId.
            Assert.Equal(
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                second.Error.Metadata!["Pid"]);
            Assert.Equal(Environment.MachineName, second.Error.Metadata["Host"]);
            Assert.Equal(_lockFilePath, second.Error.Metadata["LockFilePath"]);
        }
        finally
        {
            await first.AssertValue().DisposeAsync();
        }
    }

    [Fact]
    public async Task AcquireAsync_AfterFirstDispose_SecondCallSucceeds()
    {
        var first = await InstanceLock.AcquireAsync(
            _skinkRoot, TestAppVersion, force: false, logger: null, ct: CancellationToken.None);
        Assert.True(first.Success);
        await first.AssertValue().DisposeAsync();

        var second = await InstanceLock.AcquireAsync(
            _skinkRoot, TestAppVersion, force: false, logger: null, ct: CancellationToken.None);

        Assert.True(second.Success);
        await second.AssertValue().DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_WithForce_DeletesStaleLockFile_AndSucceeds()
    {
        // Simulate a stale file left by a crashed prior process (no live handle on it).
        File.WriteAllText(_lockFilePath, "{\"pid\":999999,\"host\":\"stale\",\"startedAtUtc\":\"2020-01-01T00:00:00Z\",\"appVersion\":\"old\"}");
        Assert.True(File.Exists(_lockFilePath));

        var result = await InstanceLock.AcquireAsync(
            _skinkRoot, TestAppVersion, force: true, logger: null, ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(_lockFilePath));
        await result.AssertValue().DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_WithForce_WhileLockIsLive_ReturnsSingleInstanceLockHeld()
    {
        // The first holder owns a live lock (FileShare.None handle still open). A second
        // acquire with force=true must NOT bypass it: on Linux/macOS, File.Delete would
        // unlink the directory entry while the holder's flock remained on the original
        // inode, letting a fresh OpenOrCreate create a separate inode and acquire its own
        // flock — two processes both believing they hold the exclusive lock. The
        // probe-open inside the force path detects the live state first and refuses to
        // delete. (Principle 35; PR-72 review finding #1.)
        var first = await InstanceLock.AcquireAsync(
            _skinkRoot, TestAppVersion, force: false, logger: null, ct: CancellationToken.None);
        Assert.True(first.Success);

        try
        {
            var second = await InstanceLock.AcquireAsync(
                _skinkRoot, TestAppVersion, force: true, logger: null, ct: CancellationToken.None);

            Assert.False(second.Success);
            Assert.NotNull(second.Error);
            Assert.Equal(ErrorCode.SingleInstanceLockHeld, second.AssertError().Code);
            Assert.Contains("--force cannot clear a live lock", second.Error.Message);
            // The first holder is this process — pid metadata should match.
            Assert.Equal(
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                second.Error.Metadata!["Pid"]);
        }
        finally
        {
            await first.AssertValue().DisposeAsync();
        }
    }

    [Fact]
    public async Task AcquireAsync_WithoutForce_StaleLockFileOnDisk_StillSucceeds()
    {
        // Important behaviour pin: a stale *file* on disk (no live FileShare.None handle on
        // it) is not a held lock. FileMode.OpenOrCreate + FileShare.None succeeds because
        // no other process is holding it. The --force flag is only needed when an unrelated
        // application is holding the file open for reading while we try to overwrite — a
        // rare case, and not what "stale" usually means.
        File.WriteAllText(_lockFilePath, "{\"pid\":999999,\"host\":\"stale\",\"startedAtUtc\":\"2020-01-01T00:00:00Z\",\"appVersion\":\"old\"}");

        var result = await InstanceLock.AcquireAsync(
            _skinkRoot, TestAppVersion, force: false, logger: null, ct: CancellationToken.None);

        Assert.True(result.Success);
        await result.AssertValue().DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_LockFileContents_IncludePidHostStartedAtVersion()
    {
        var before = DateTime.UtcNow;
        var result = await InstanceLock.AcquireAsync(
            _skinkRoot, TestAppVersion, force: false, logger: null, ct: CancellationToken.None);
        Assert.True(result.Success);

        try
        {
            // Holder identity is written to the sibling manifest file (instance.manifest),
            // not to the exclusion lock file. Any process can read it with default sharing.
            var bytes = await File.ReadAllBytesAsync(_manifestFilePath);

            var parsed = InstanceLockManifest.TryParse(bytes, out var manifest);
            Assert.True(parsed);
            Assert.Equal(Environment.ProcessId, manifest.Pid);
            Assert.Equal(Environment.MachineName, manifest.Host);
            Assert.Equal(TestAppVersion, manifest.AppVersion);

            // StartedAtUtc round-trips as an ISO 8601 timestamp within the last few seconds.
            var startedAt = DateTime.Parse(manifest.StartedAtUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.True(startedAt >= before.AddSeconds(-1));
            Assert.True(startedAt <= DateTime.UtcNow.AddSeconds(1));
        }
        finally
        {
            await result.AssertValue().DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var result = await InstanceLock.AcquireAsync(
            _skinkRoot, TestAppVersion, force: false, logger: null, ct: CancellationToken.None);
        Assert.True(result.Success);

        await result.AssertValue().DisposeAsync();
        // Second dispose is a no-op and must not throw.
        await result.Value.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_DeletesLockAndManifestFiles()
    {
        var result = await InstanceLock.AcquireAsync(
            _skinkRoot, TestAppVersion, force: false, logger: null, ct: CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(File.Exists(_lockFilePath));
        Assert.True(File.Exists(_manifestFilePath));

        await result.AssertValue().DisposeAsync();

        Assert.False(File.Exists(_lockFilePath));
        Assert.False(File.Exists(_manifestFilePath));
    }

    [Fact]
    public async Task AcquireAsync_WithCancelledToken_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await InstanceLock.AcquireAsync(
            _skinkRoot, TestAppVersion, force: false, logger: null, ct: cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.AssertError().Code);
        // A cancelled call must not leave either file behind.
        Assert.False(File.Exists(_lockFilePath));
        Assert.False(File.Exists(_manifestFilePath));
    }

    [Fact]
    public void Manifest_SerializeRoundTrip_PreservesAllFields()
    {
        var original = new InstanceLockManifest(
            Pid: 12345,
            Host: "test-host",
            StartedAtUtc: "2025-05-19T14:32:01.1234567Z",
            AppVersion: "0.1.0+abc1234");

        var bytes = original.SerializeUtf8();
        Assert.True(InstanceLockManifest.TryParse(bytes, out var roundTripped));

        Assert.Equal(original.Pid, roundTripped.Pid);
        Assert.Equal(original.Host, roundTripped.Host);
        Assert.Equal(original.StartedAtUtc, roundTripped.StartedAtUtc);
        Assert.Equal(original.AppVersion, roundTripped.AppVersion);
    }

    [Fact]
    public void Manifest_TryParse_Empty_ReturnsFalse()
    {
        Assert.False(InstanceLockManifest.TryParse(ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void Manifest_TryParse_NonJson_ReturnsFalse()
    {
        var garbage = System.Text.Encoding.UTF8.GetBytes("this is not json at all");
        Assert.False(InstanceLockManifest.TryParse(garbage, out _));
    }
}
