using System.Security.Cryptography;
using System.Text;
using Dapper;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Identity;
using FlashSkink.Core.Metadata;
using FlashSkink.Core.Orchestration;
using FlashSkink.Core.Providers;
using FlashSkink.Core.Providers.Setup;
using FlashSkink.Tests._TestSupport;
using FlashSkink.Tests.Engine;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Orchestration;

/// <summary>
/// Unit + integration tests for the public §11 tail-management API (<c>AddTailAsync</c>,
/// <c>RemoveTailAsync</c>, <c>ListTailsAsync</c>) introduced in PR 4.6a, replacing the internal
/// <c>RegisterTailAsync</c> seam.
/// </summary>
public sealed class AddTailAsyncTests : IAsyncLifetime
{
    private string _skinkRoot = string.Empty;
    private string _tailRoot = string.Empty;
    private const string Password = "test-password-123";
    private const string ProviderId = "tail-1";
    private const string DisplayName = "Test Tail";

    private InMemoryProviderRegistry _registry = null!;
    private RecordingNotificationBus _bus = null!;

    public Task InitializeAsync()
    {
        _skinkRoot = Path.Combine(Path.GetTempPath(), $"flashskink-addtail-{Guid.NewGuid():N}");
        _tailRoot = Path.Combine(Path.GetTempPath(), $"flashskink-addtail-tail-{Guid.NewGuid():N}");
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

    private VolumeCreationOptions Options(
        IProviderRegistry? registry = null,
        IReadOnlyList<IProviderSetup>? setups = null) => new()
        {
            LoggerFactory = NullLoggerFactory.Instance,
            NotificationBus = _bus,
            ProviderRegistry = registry ?? _registry,
            ProviderSetups = setups,
        };

    private async Task<FlashSkinkVolume> CreateVolumeAsync(VolumeCreationOptions? options = null)
    {
        var result = await FlashSkinkVolume.CreateAsync(_skinkRoot, Password, options ?? Options());
        Assert.True(result.Success, result.Error?.Message);
        return result.AssertValue().Volume;
    }

    private static TailConfiguration FsConfig(string root) => new()
    {
        ProviderType = "filesystem",
        ProviderId = ProviderId,
        DisplayName = DisplayName,
        LocalPath = root,
    };

    private static TailConfiguration OauthConfig(string providerType) => new()
    {
        ProviderType = providerType,
        ProviderId = ProviderId,
        DisplayName = DisplayName,
        ClientId = "client-id-123",
        ClientSecret = "client-secret-xyz",
        AuthorizationCode = "auth-code-abc",
        CodeVerifier = "verifier-123",
        RedirectUri = "http://127.0.0.1:5000/oauth-callback/",
    };

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
        return unlock.AssertValue();
    }

    private async Task<SqliteConnection> OpenRawBrainAsync()
    {
        var brainPath = Path.Combine(_skinkRoot, ".flashskink", "brain.db");
        var kdf = new KeyDerivationService();
        var brainFactory = new BrainConnectionFactory(kdf, NullLogger<BrainConnectionFactory>.Instance);
        var dek = await ReadDekAsync();
        var brainResult = await brainFactory.CreateAsync(brainPath, dek, CancellationToken.None);
        CryptographicOperations.ZeroMemory(dek);
        Assert.True(brainResult.Success, brainResult.Error?.Message);
        return brainResult.AssertValue();
    }

    // ── AddTailAsync — FileSystem ─────────────────────────────────────────────

    [Fact]
    public async Task AddTail_FileSystem_ValidPath_InsertsRow_RegistersAdapter_ReturnsTailInfo()
    {
        await using var volume = await CreateVolumeAsync();

        var result = await volume.AddTailAsync(FsConfig(_tailRoot));

        Assert.True(result.Success, result.Error?.Message);
        var info = result.AssertValue();
        Assert.Equal(ProviderId, info.ProviderId);
        Assert.Equal("filesystem", info.ProviderType);
        Assert.Equal(DisplayName, info.DisplayName);
        Assert.Equal("Healthy", info.Health);

        var ids = await _registry.ListActiveProviderIdsAsync(CancellationToken.None);
        Assert.Contains(ProviderId, ids.AssertValue());

        await volume.DisposeAsync();
        await using var brain = await OpenRawBrainAsync();
        var row = await brain.QuerySingleAsync<(string ProviderID, string ProviderType, string DisplayName, string? ProviderConfig, string HealthStatus, long IsActive)>(
            "SELECT ProviderID, ProviderType, DisplayName, ProviderConfig, HealthStatus, IsActive FROM Providers WHERE ProviderID = @Id",
            new { Id = ProviderId });
        Assert.Equal("filesystem", row.ProviderType);
        Assert.Equal("Healthy", row.HealthStatus);
        Assert.Equal(1, row.IsActive);
        Assert.Contains("rootPath", row.ProviderConfig);
    }

    [Fact]
    public async Task AddTail_FileSystem_PathInsideSkink_ReturnsInvalidArgument()
    {
        await using var volume = await CreateVolumeAsync();
        var insidePath = Path.Combine(_skinkRoot, "inner-tail");
        Directory.CreateDirectory(insidePath);

        var result = await volume.AddTailAsync(FsConfig(insidePath));

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }

    // ── AddTailAsync — OAuth ──────────────────────────────────────────────────

    [Fact]
    public async Task AddTail_Oauth_RunsExchange_EncryptsSecret_PersistsClientIdAndToken()
    {
        var tokenBytes = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        var fakeSetup = new FakeProviderSetup
        {
            ProviderType = "fake-cloud",
            DisplayName = "Fake Cloud",
            SetupKind = ProviderSetupKind.OAuth,
            ProviderToReturn = new DisposalTrackingStorageProvider(ProviderId, "fake-cloud", "Fake Cloud"),
            ExchangeTokenBytes = tokenBytes,
        };
        await using var volume = await CreateVolumeAsync(Options(setups: [fakeSetup]));

        var result = await volume.AddTailAsync(OauthConfig("fake-cloud"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("auth-code-abc", fakeSetup.LastExchangeCode);

        await volume.DisposeAsync();
        await using var brain = await OpenRawBrainAsync();
        var row = await brain.QuerySingleAsync<(byte[]? EncryptedToken, byte[]? EncryptedClientSecret, string? ClientId)>(
            "SELECT EncryptedToken, EncryptedClientSecret, ClientId FROM Providers WHERE ProviderID = @Id",
            new { Id = ProviderId });
        Assert.NotNull(row.EncryptedToken);
        Assert.Equal(tokenBytes, row.EncryptedToken);
        Assert.Equal("client-id-123", row.ClientId);
        Assert.NotNull(row.EncryptedClientSecret);

        var dek = await ReadDekAsync();
        try
        {
            Assert.True(ProviderTokenCrypto.TryDecrypt(row.EncryptedClientSecret, dek, out var secret));
            Assert.Equal("client-secret-xyz", secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    [Fact]
    public async Task AddTail_Oauth_MissingCredentials_ReturnsInvalidArgument()
    {
        var fakeSetup = new FakeProviderSetup { ProviderType = "fake-cloud", SetupKind = ProviderSetupKind.OAuth };
        await using var volume = await CreateVolumeAsync(Options(setups: [fakeSetup]));

        var result = await volume.AddTailAsync(new TailConfiguration { ProviderType = "fake-cloud", ProviderId = ProviderId });

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }

    // ── Backfill, duplicates, unknown type ────────────────────────────────────

    [Fact]
    public async Task AddTail_BackfillsTailUploadsForExistingFiles()
    {
        await using var volume = await CreateVolumeAsync();
        for (var i = 0; i < 3; i++)
        {
            var write = await volume.WriteFileAsync(
                new MemoryStream(RandomNumberGenerator.GetBytes(2048)), $"doc{i}.bin");
            Assert.True(write.Success, write.Error?.Message);
        }

        var result = await volume.AddTailAsync(FsConfig(_tailRoot));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.AssertValue().PendingFileCount);

        await volume.DisposeAsync();
        await using var brain = await OpenRawBrainAsync();
        var pending = await brain.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM TailUploads WHERE ProviderID = @Id AND Status = 'PENDING'",
            new { Id = ProviderId });
        Assert.Equal(3, pending);
    }

    [Fact]
    public async Task AddTail_DuplicateProviderId_ReturnsPathConflict()
    {
        await using var volume = await CreateVolumeAsync();
        Assert.True((await volume.AddTailAsync(FsConfig(_tailRoot))).Success);

        var second = await volume.AddTailAsync(FsConfig(_tailRoot));

        Assert.False(second.Success);
        Assert.Equal(ErrorCode.PathConflict, second.AssertError().Code);
    }

    [Fact]
    public async Task AddTail_UnknownProviderType_ReturnsInvalidArgument()
    {
        await using var volume = await CreateVolumeAsync();

        var result = await volume.AddTailAsync(new TailConfiguration { ProviderType = "no-such-provider" });

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }

    [Fact]
    public async Task AddTail_WritesInitialWitnessToTail()
    {
        await using var volume = await CreateVolumeAsync();
        Assert.True((await volume.AddTailAsync(FsConfig(_tailRoot))).Success);

        var dek = await ReadDekAsync();
        try
        {
            var provider = new FileSystemProvider(ProviderId, DisplayName, _tailRoot, NullLogger<FileSystemProvider>.Instance);
            var store = new WitnessStore(NullLogger<WitnessStore>.Instance);
            var witness = await store.TryReadAsync(provider, dek, CancellationToken.None);
            Assert.True(witness.Success);
            Assert.NotNull(witness.Value);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    // ── Registry guard + failure-disposal ─────────────────────────────────────

    [Fact]
    public async Task AddTail_NonMutableRegistry_ReturnsInvalidArgument()
    {
        await using var volume = await CreateVolumeAsync(Options(registry: new ReadOnlyProviderRegistry()));

        var result = await volume.AddTailAsync(FsConfig(_tailRoot));

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }

    [Fact]
    public async Task AddTail_RegistrationFails_DisposesConstructedAdapter()
    {
        var tracking = new DisposalTrackingStorageProvider(ProviderId, "fake-cloud", "Fake Cloud");
        var fakeSetup = new FakeProviderSetup
        {
            ProviderType = "fake-cloud",
            SetupKind = ProviderSetupKind.OAuth,
            ProviderToReturn = tracking,
        };
        await using var volume = await CreateVolumeAsync(
            Options(registry: new ThrowingMutableRegistry(), setups: [fakeSetup]));

        var result = await volume.AddTailAsync(OauthConfig("fake-cloud"));

        Assert.False(result.Success);
        Assert.True(tracking.Disposed, "The constructed adapter should be disposed when registration fails.");
    }

    [Fact]
    public async Task AddTail_Success_DoesNotDisposeAdapter()
    {
        var tracking = new DisposalTrackingStorageProvider(ProviderId, "fake-cloud", "Fake Cloud");
        var fakeSetup = new FakeProviderSetup
        {
            ProviderType = "fake-cloud",
            SetupKind = ProviderSetupKind.OAuth,
            ProviderToReturn = tracking,
        };
        await using var volume = await CreateVolumeAsync(Options(setups: [fakeSetup]));

        var result = await volume.AddTailAsync(OauthConfig("fake-cloud"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.False(tracking.Disposed, "A successfully-registered adapter must not be disposed (the registry owns it).");
    }

    [Fact]
    public async Task AddTail_AfterDispose_ThrowsObjectDisposed()
    {
        var volume = await CreateVolumeAsync();
        await volume.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await volume.AddTailAsync(FsConfig(_tailRoot)));
    }

    // ── RemoveTailAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveTail_DeletesRows_AndEvictsAdapter()
    {
        await using var volume = await CreateVolumeAsync();
        Assert.True((await volume.AddTailAsync(FsConfig(_tailRoot))).Success);

        var remove = await volume.RemoveTailAsync(ProviderId);

        Assert.True(remove.Success, remove.Error?.Message);
        var ids = await _registry.ListActiveProviderIdsAsync(CancellationToken.None);
        Assert.DoesNotContain(ProviderId, ids.AssertValue());

        await volume.DisposeAsync();
        await using var brain = await OpenRawBrainAsync();
        var count = await brain.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Providers WHERE ProviderID = @Id", new { Id = ProviderId });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task RemoveTail_UnknownId_ReturnsInvalidArgument()
    {
        await using var volume = await CreateVolumeAsync();

        var result = await volume.RemoveTailAsync("no-such-tail");

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }

    [Fact]
    public async Task RemoveTail_DoesNotDeleteRemoteData()
    {
        await using var volume = await CreateVolumeAsync();
        Assert.True((await volume.AddTailAsync(FsConfig(_tailRoot))).Success);
        // The initial witness write places a file under the tail root; it must survive removal.
        Assert.True(Directory.EnumerateFileSystemEntries(_tailRoot).Any());

        Assert.True((await volume.RemoveTailAsync(ProviderId)).Success);

        Assert.True(Directory.Exists(_tailRoot));
        Assert.True(Directory.EnumerateFileSystemEntries(_tailRoot).Any());
    }

    // ── ListTailsAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTails_ReturnsRowPerActiveProvider_WithCounts()
    {
        await using var volume = await CreateVolumeAsync();
        var write = await volume.WriteFileAsync(new MemoryStream(RandomNumberGenerator.GetBytes(1024)), "doc.bin");
        Assert.True(write.Success);
        Assert.True((await volume.AddTailAsync(FsConfig(_tailRoot))).Success);

        var list = await volume.ListTailsAsync();

        Assert.True(list.Success, list.Error?.Message);
        var tails = list.AssertValue();
        var tail = Assert.Single(tails);
        Assert.Equal(ProviderId, tail.ProviderId);
        Assert.Equal(DisplayName, tail.DisplayName);
        // The single pre-existing file was backfilled as PENDING for the new tail.
        Assert.True(tail.PendingFileCount >= 0);
        Assert.Equal(1, tail.PendingFileCount + tail.UploadedFileCount);
    }

    [Fact]
    public async Task ListTails_EmptyWhenNoTails()
    {
        await using var volume = await CreateVolumeAsync();

        var list = await volume.ListTailsAsync();

        Assert.True(list.Success, list.Error?.Message);
        Assert.Empty(list.AssertValue());
    }
}
