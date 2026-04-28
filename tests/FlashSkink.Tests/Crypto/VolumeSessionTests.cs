using System.Data;
using System.Text;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Crypto;

public class VolumeSessionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly KeyVault _vault;
    private readonly ReadOnlyMemory<byte> _password;
    private readonly BrainConnectionFactory _brainFactory;
    private readonly MigrationRunner _migrationRunner;

    public VolumeSessionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"VolumeSessionTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _vault = new KeyVault(new KeyDerivationService(), new MnemonicService());
        _password = Encoding.UTF8.GetBytes("test-password");
        _brainFactory = new BrainConnectionFactory(
            new KeyDerivationService(),
            NullLogger<BrainConnectionFactory>.Instance);
        _migrationRunner = new MigrationRunner(NullLogger<MigrationRunner>.Instance);
    }

    public void Dispose()
    {
        // SQLite WAL mode holds file handles in the connection pool on Windows even after
        // SqliteConnection.Dispose(). ClearAllPools() forces immediate release.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_tempDir, recursive: true);
    }

    private string SkinkRoot() => _tempDir;

    private VolumeLifecycle CreateLifecycle() =>
        new(_vault, _brainFactory, _migrationRunner,
            NullLogger<VolumeLifecycle>.Instance);

    private async Task SeedVaultAsync()
    {
        var flashskinkDir = Path.Combine(_tempDir, ".flashskink");
        Directory.CreateDirectory(flashskinkDir);
        var vaultPath = Path.Combine(flashskinkDir, "vault.bin");
        await _vault.CreateAsync(vaultPath, _password, CancellationToken.None);
    }

    // ── VolumeSession disposal ────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_ZeroesDek()
    {
        await SeedVaultAsync();
        var result = await CreateLifecycle().OpenAsync(SkinkRoot(), _password, CancellationToken.None);
        Assert.True(result.Success);
        var session = result.Value!;
        var dek = session.Dek; // holds reference to the underlying array

        await session.DisposeAsync();

        Assert.True(dek.All(b => b == 0));
    }

    [Fact]
    public async Task Dek_AfterDispose_ThrowsObjectDisposedException()
    {
        await SeedVaultAsync();
        var result = await CreateLifecycle().OpenAsync(SkinkRoot(), _password, CancellationToken.None);
        Assert.True(result.Success);
        var session = result.Value!;
        await session.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => _ = session.Dek);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_NoThrow()
    {
        await SeedVaultAsync();
        var result = await CreateLifecycle().OpenAsync(SkinkRoot(), _password, CancellationToken.None);
        Assert.True(result.Success);
        var session = result.Value!;

        await session.DisposeAsync();
        var ex = await Record.ExceptionAsync(() => session.DisposeAsync().AsTask());

        Assert.Null(ex);
    }

    // ── VolumeLifecycle.OpenAsync ─────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_WithCorrectPassword_ReturnsSessionWithDek()
    {
        await SeedVaultAsync();

        var result = await CreateLifecycle().OpenAsync(SkinkRoot(), _password, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(32, result.Value!.Dek.Length);
        await result.Value.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_WithCorrectPassword_BrainConnectionIsOpen()
    {
        await SeedVaultAsync();

        var result = await CreateLifecycle().OpenAsync(SkinkRoot(), _password, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value!.BrainConnection);
        Assert.Equal(ConnectionState.Open, result.Value!.BrainConnection!.State);
        await result.Value.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_WithWrongPassword_ReturnsInvalidPassword()
    {
        await SeedVaultAsync();
        ReadOnlyMemory<byte> wrong = Encoding.UTF8.GetBytes("wrong");

        var result = await CreateLifecycle().OpenAsync(SkinkRoot(), wrong, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidPassword, result.Error!.Code);
    }

    [Fact]
    public async Task OpenAsync_VaultNotFound_ReturnsVolumeNotFound()
    {
        var result = await CreateLifecycle().OpenAsync(SkinkRoot(), _password, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeNotFound, result.Error!.Code);
    }
}
