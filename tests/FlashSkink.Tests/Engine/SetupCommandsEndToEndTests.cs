using System.Diagnostics;
using System.Security.Cryptography;
using Dapper;
using FlashSkink.CLI.Setup;
using FlashSkink.Core.Abstractions.Notifications;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Orchestration;
using FlashSkink.Tests._TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Engine;

/// <summary>
/// End-to-end tests for the <c>setup</c> CLI command subtree. Commands are constructed via
/// <see cref="CliSetupFactory.CreateForTest"/> with injected fakes, then invoked via
/// <c>root.Parse(args).InvokeAsync()</c>, capturing stdout/stderr to <see cref="StringWriter"/>s.
/// Tests exercise the full path from argument parsing to volume mutation and back.
/// </summary>
public sealed class SetupCommandsEndToEndTests : IAsyncLifetime
{
    private string _skinkRoot = string.Empty;
    private string _tailRoot = string.Empty;

    private const string Password = "test-password-e2e";
    private const string CloudType = "fake-cloud";
    private const string ClientId = "test-client-id-xyz";
    private const string ClientSecret = "super-secret-value-never-echo";
    private const string FakeProviderId = "fake-cloud-p1";
    private const string FakeDisplayName = "Fake Cloud";

    private RecordingNotificationBus _bus = null!;
    private StringWriter _output = null!;
    private StringWriter _error = null!;

    public Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        _skinkRoot = Path.Combine(Path.GetTempPath(), $"fs-setup-e2e-skink-{id}");
        _tailRoot = Path.Combine(Path.GetTempPath(), $"fs-setup-e2e-tail-{id}");
        Directory.CreateDirectory(_skinkRoot);
        Directory.CreateDirectory(_tailRoot);
        _bus = new RecordingNotificationBus();
        _output = new StringWriter();
        _error = new StringWriter();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        _output.Dispose();
        _error.Dispose();
        try { Directory.Delete(_skinkRoot, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_tailRoot, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private VolumeCreationOptions DefaultOptions() => new()
    {
        LoggerFactory = NullLoggerFactory.Instance,
        NotificationBus = _bus,
    };

    /// <summary>
    /// Creates a volume at <c>_skinkRoot</c> with the test password, disposes it immediately so
    /// the single-instance lock is released and CLI commands can open it.
    /// </summary>
    private async Task CreateVolumeAndDisposeAsync()
    {
        var result = await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions());
        Assert.True(result.Success, result.Error?.Message);
        var receipt = result.AssertValue();
        receipt.RecoveryPhrase.Dispose();
        await receipt.Volume.DisposeAsync();
    }

    /// <summary>
    /// Builds a <see cref="CliSetupFactory"/> wired to the test output writers. All parameters
    /// default to fakes; pass explicit values to inject specific test doubles.
    /// </summary>
    private CliSetupFactory BuildFactory(
        IReadOnlyList<IProviderSetup>? cloudSetups = null,
        IOAuthCaptureFlow? oauthCapture = null,
        IPasswordReader? passwordReader = null,
        Func<string, bool>? confirmPrompt = null)
        => CliSetupFactory.CreateForTest(
            NullLoggerFactory.Instance,
            cloudSetups ?? [],
            oauthCapture ?? new FakeOAuthCaptureFlow(),
            passwordReader ?? new FakePasswordReader(Password),
            _bus,
            _output,
            _error,
            confirmPrompt);

    /// <summary>String args for <c>setup add --provider filesystem</c> on the test paths.</summary>
    private string[] FsAddArgs() =>
        ["setup", "add", "--provider", "filesystem", "--root", _tailRoot,
         "--skink", _skinkRoot, "--password", Password];

    /// <summary>
    /// Polls for the sharded blob at the tail root. Returns true once the file appears within
    /// <paramref name="budget"/>.
    /// </summary>
    private static async Task<bool> WaitForBlobAtTailAsync(
        string tailRoot, string blobId, TimeSpan? budget = null)
    {
        var path = Path.Combine(tailRoot, "blobs", blobId[..2], blobId[2..4], blobId + ".bin");
        var sw = Stopwatch.StartNew();
        var deadline = budget ?? TimeSpan.FromSeconds(15);
        while (sw.Elapsed < deadline)
        {
            if (File.Exists(path))
            {
                return true;
            }
            await Task.Delay(40);
        }
        return File.Exists(path);
    }

    // ── Guide tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Guide_KnownProvider_PrintsEmbeddedGuide_WithSkinkName()
    {
        await using var factory = BuildFactory();
        var exitCode = await factory.RootCommand
            .Parse(["setup", "guide", "--provider", "google-drive"])
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        var output = _output.ToString();
        // {cli} placeholder was substituted; the binary name appears in the guide.
        Assert.Contains("skink setup add", output);
        Assert.DoesNotContain("{cli}", output);
        Assert.Contains("Google Drive", output);
    }

    [Fact]
    public async Task Guide_UnknownProvider_PrintsErrorAndExits1()
    {
        await using var factory = BuildFactory();
        var exitCode = await factory.RootCommand
            .Parse(["setup", "guide", "--provider", "totally-unknown-provider"])
            .InvokeAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown provider", _error.ToString());
    }

    // ── Add tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_FileSystem_ConfiguresTail_AndPrintsSuccess()
    {
        await CreateVolumeAndDisposeAsync();

        await using var factory = BuildFactory();
        var exitCode = await factory.RootCommand.Parse(FsAddArgs()).InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Contains("tail configured", _output.ToString());
    }

    /// <summary>
    /// §4.6 e2e acceptance test: <c>setup add --provider filesystem</c> → write a file
    /// → file lands as an encrypted blob in the tail directory.
    /// </summary>
    [Fact]
    public async Task Add_FileSystem_ThenWrite_FileLandsOnTail()
    {
        // Step 1 — create a volume and write a file before the tail is configured.
        var result = await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, DefaultOptions());
        Assert.True(result.Success, result.Error?.Message);
        var receipt = result.AssertValue();
        string blobId;
        using (receipt.RecoveryPhrase)
        {
            await using var volume = receipt.Volume;
            var data = RandomNumberGenerator.GetBytes(512);
            var writeResult = await volume.WriteFileAsync(
                new MemoryStream(data), "docs/test-file.bin");
            Assert.True(writeResult.Success, writeResult.Error?.Message);
            blobId = writeResult.AssertValue().BlobId;
        }
        // Volume disposed; single-instance lock released.

        // Step 2 — run `setup add filesystem` to register the tail and backfill TailUploads.
        await using (var factory = BuildFactory())
        {
            var addExit = await factory.RootCommand.Parse(FsAddArgs()).InvokeAsync();
            Assert.Equal(0, addExit);
        }
        // CLI volume closed.

        // Step 3 — open the volume to start the upload queue service; wait for upload.
        var openResult = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions());
        Assert.True(openResult.Success, openResult.Error?.Message);
        await using var vol2 = openResult.Value!;

        var landed = await WaitForBlobAtTailAsync(_tailRoot, blobId);
        Assert.True(landed, $"Expected blob {blobId} to appear at the tail within 15 s.");
    }

    [Fact]
    public async Task Add_Cloud_WithFakeOAuthAndFakeSetup_ConfiguresTail()
    {
        await CreateVolumeAndDisposeAsync();

        var fakeAdapter = new DisposalTrackingStorageProvider(CloudType + "-id", CloudType, FakeDisplayName);
        var fakeSetup = new FakeProviderSetup
        {
            ProviderType = CloudType,
            DisplayName = FakeDisplayName,
            ProviderToReturn = fakeAdapter,
        };
        var fakeOAuth = new FakeOAuthCaptureFlow();

        await using var factory = BuildFactory(
            cloudSetups: [fakeSetup],
            oauthCapture: fakeOAuth);

        var exitCode = await factory.RootCommand.Parse(
            ["setup", "add",
             "--provider",      CloudType,
             "--client-id",     ClientId,
             "--client-secret", ClientSecret,
             "--skink",         _skinkRoot,
             "--password",      Password])
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(1, fakeOAuth.PrepareCallCount);
        Assert.Equal(1, fakeOAuth.AwaitCallCount);
        Assert.Contains("tail configured", _output.ToString());
    }

    [Fact]
    public async Task Add_Cloud_SecretNotEchoed()
    {
        await CreateVolumeAndDisposeAsync();

        var fakeAdapter = new DisposalTrackingStorageProvider(CloudType + "-id2", CloudType, FakeDisplayName);
        var fakeSetup = new FakeProviderSetup
        {
            ProviderType = CloudType,
            DisplayName = FakeDisplayName,
            ProviderToReturn = fakeAdapter,
        };

        await using var factory = BuildFactory(cloudSetups: [fakeSetup]);

        await factory.RootCommand.Parse(
            ["setup", "add",
             "--provider",      CloudType,
             "--client-id",     ClientId,
             "--client-secret", ClientSecret,
             "--skink",         _skinkRoot,
             "--password",      Password])
            .InvokeAsync();

        // Principle 26: secret must NOT appear in any captured output.
        Assert.DoesNotContain(ClientSecret, _output.ToString());
        Assert.DoesNotContain(ClientSecret, _error.ToString());
    }

    [Fact]
    public async Task Add_FileSystem_PasswordViaFakeReader_Works()
    {
        // Tests the redirected-stdin / fake-reader path: no --password arg; password resolved
        // via IPasswordReader injection (mimics piped stdin in CI).
        await CreateVolumeAndDisposeAsync();

        await using var factory = BuildFactory(
            passwordReader: new FakePasswordReader(Password));

        // Intentionally omit --password; the reader must supply it.
        var exitCode = await factory.RootCommand.Parse(
            ["setup", "add", "--provider", "filesystem", "--root", _tailRoot, "--skink", _skinkRoot])
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Contains("tail configured", _output.ToString());
    }

    [Fact]
    public async Task Add_FileSystem_PathInsideSkink_ReturnsError()
    {
        await CreateVolumeAndDisposeAsync();

        var insidePath = Path.Combine(_skinkRoot, "subtail");
        Directory.CreateDirectory(insidePath);

        await using var factory = BuildFactory();
        var exitCode = await factory.RootCommand.Parse(
            ["setup", "add", "--provider", "filesystem",
             "--root", insidePath, "--skink", _skinkRoot, "--password", Password])
            .InvokeAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains("Error", _error.ToString());
    }

    [Fact]
    public async Task Add_Cloud_UnknownProvider_ReturnsError()
    {
        await CreateVolumeAndDisposeAsync();

        // Empty cloud setups list — "not-a-real-provider" will not be found.
        await using var factory = BuildFactory(cloudSetups: []);

        var exitCode = await factory.RootCommand.Parse(
            ["setup", "add",
             "--provider", "not-a-real-provider",
             "--skink",    _skinkRoot,
             "--password", Password])
            .InvokeAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown provider", _error.ToString());
    }

    // ── List tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_AfterAdd_ShowsTailRow()
    {
        await CreateVolumeAndDisposeAsync();

        // Add the tail first.
        await using (var factory = BuildFactory())
        {
            await factory.RootCommand.Parse(FsAddArgs()).InvokeAsync();
        }

        // Reset output for the list command assertion.
        _output = new StringWriter();
        _error = new StringWriter();

        await using var factory2 = BuildFactory();
        var exitCode = await factory2.RootCommand.Parse(
            ["setup", "list", "--skink", _skinkRoot, "--password", Password])
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        // FileSystemProviderSetup.DisplayName == "Local folder".
        Assert.Contains("Local folder", _output.ToString());
    }

    [Fact]
    public async Task List_NoTails_PrintsEmptyMessage()
    {
        await CreateVolumeAndDisposeAsync();

        await using var factory = BuildFactory();
        var exitCode = await factory.RootCommand.Parse(
            ["setup", "list", "--skink", _skinkRoot, "--password", Password])
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.Contains("No tails configured", _output.ToString());
    }

    // ── Remove tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_AfterAdd_RemovesRowAndPrintsReminder()
    {
        await CreateVolumeAndDisposeAsync();

        // Add a tail first so there is something to remove.
        await using (var factory = BuildFactory())
        {
            await factory.RootCommand.Parse(FsAddArgs()).InvokeAsync();
        }

        // Provider ID defaults to the ProviderType when TailConfiguration.ProviderId is blank.
        // FlashSkinkVolume.AddTailAsync: var providerId = string.IsNullOrWhiteSpace(config.ProviderId)
        //   ? config.ProviderType : config.ProviderId;
        const string providerId = "filesystem";

        _output = new StringWriter();
        _error = new StringWriter();

        // Inject "yes" so removal proceeds without TTY interaction.
        await using var factory2 = BuildFactory(confirmPrompt: static _ => true);
        var exitCode = await factory2.RootCommand.Parse(
            ["setup", "remove",
             "--provider",  providerId,
             "--skink",     _skinkRoot,
             "--password",  Password,
             "--yes"])
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        var output = _output.ToString();
        Assert.Contains("Tail removed", output);
        Assert.Contains("not deleted", output);
    }

    [Fact]
    public async Task Remove_WithoutYes_PromptsAndAbortsOnNo()
    {
        await CreateVolumeAndDisposeAsync();

        // Add a tail.
        await using (var factory = BuildFactory())
        {
            await factory.RootCommand.Parse(FsAddArgs()).InvokeAsync();
        }

        // Provider ID is the ProviderType when TailConfiguration.ProviderId is not supplied.
        const string providerId = "filesystem";

        _output = new StringWriter();
        _error = new StringWriter();

        // Inject "no" via the confirm-prompt seam.
        await using var factory2 = BuildFactory(confirmPrompt: static _ => false);
        var exitCode = await factory2.RootCommand.Parse(
            ["setup", "remove",
             "--provider", providerId,
             "--skink",    _skinkRoot,
             "--password", Password])
            .InvokeAsync();

        // Exits 0 — cancellation is not an error.
        Assert.Equal(0, exitCode);
        Assert.Contains("Cancelled", _output.ToString());

        // Volume still has the tail: open it and call ListTailsAsync to verify.
        var openResult = await FlashSkinkVolume.OpenAsync(_skinkRoot, Password, DefaultOptions());
        Assert.True(openResult.Success, openResult.Error?.Message);
        await using var volume = openResult.Value!;
        var listResult = await volume.ListTailsAsync();
        Assert.True(listResult.Success);
        Assert.Contains(listResult.Value!, t => t.ProviderId == providerId);
    }

}
