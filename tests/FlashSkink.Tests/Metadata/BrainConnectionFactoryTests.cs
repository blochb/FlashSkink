using System.Data;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Metadata;

public class BrainConnectionFactoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BrainConnectionFactory _sut;

    // 32-byte all-zeros DEK used throughout; different key for wrong-key tests.
    private static readonly byte[] TestDekA = new byte[32];
    private static readonly byte[] TestDekB = Enumerable.Repeat((byte)0xFF, 32).ToArray();

    public BrainConnectionFactoryTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(), $"BrainConnectionFactoryTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sut = new BrainConnectionFactory(
            new KeyDerivationService(),
            NullLogger<BrainConnectionFactory>.Instance);
    }

    public void Dispose()
    {
        // SQLite WAL mode holds file handles in the connection pool on Windows even after
        // SqliteConnection.Dispose(). ClearAllPools() forces immediate release so the temp
        // directory can be deleted.
        SqliteConnection.ClearAllPools();
        Directory.Delete(_tempDir, recursive: true);
    }

    private string BrainPath(string name = "brain.db") => Path.Combine(_tempDir, name);

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_WithValidDek_ReturnsOpenConnection()
    {
        var result = await _sut.CreateAsync(BrainPath(), TestDekA, CancellationToken.None);

        Assert.True(result.Success);
        await using var _ = result.Value!;
        Assert.Equal(ConnectionState.Open, result.Value!.State);
    }

    [Fact]
    public async Task CreateAsync_WithValidDek_IntegrityCheckPasses()
    {
        var result = await _sut.CreateAsync(BrainPath(), TestDekA, CancellationToken.None);
        Assert.True(result.Success);
        await using var connection = result.Value!;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        var integrity = (string?)(await cmd.ExecuteScalarAsync());

        Assert.Equal("ok", integrity);
    }

    [Fact]
    public async Task CreateAsync_ThenReopen_WithSameDek_Succeeds()
    {
        var path = BrainPath();
        var first = await _sut.CreateAsync(path, TestDekA, CancellationToken.None);
        Assert.True(first.Success);
        await using var c1 = first.Value!;
        c1.Close();

        var second = await _sut.CreateAsync(path, TestDekA, CancellationToken.None);

        Assert.True(second.Success);
        await using var c2 = second.Value!;
        Assert.Equal(ConnectionState.Open, c2.State);
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_Cancelled_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _sut.CreateAsync(BrainPath(), TestDekA, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    // ── Corruption and wrong key ──────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_WithCorruptFile_ReturnsDatabaseCorrupt()
    {
        var path = BrainPath();
        await File.WriteAllBytesAsync(path,
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(512));

        var result = await _sut.CreateAsync(path, TestDekA, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.DatabaseCorrupt, result.Error!.Code);
    }

    [Fact]
    public async Task CreateAsync_ThenReopen_WithDifferentDek_ReturnsDatabaseCorrupt()
    {
        var path = BrainPath();
        var first = await _sut.CreateAsync(path, TestDekA, CancellationToken.None);
        Assert.True(first.Success);

        // Populate the schema so the database has real page content — an empty
        // database may not fail integrity_check even with a wrong key since there
        // are no data pages to verify.
        var migrationRunner = new MigrationRunner(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MigrationRunner>.Instance);
        await migrationRunner.RunAsync(first.Value!, CancellationToken.None);
        first.Value!.Dispose();
        SqliteConnection.ClearAllPools();

        var second = await _sut.CreateAsync(path, TestDekB, CancellationToken.None);

        Assert.False(second.Success);
        Assert.Equal(ErrorCode.DatabaseCorrupt, second.Error!.Code);
    }

    // ── Integration: ciphertext on disk ───────────────────────────────────────

    [Trait("Category", "Integration")]
    [Fact]
    public async Task CreateAsync_EncryptionIsActive_FileBytesAreNotPlaintext()
    {
        var path = BrainPath();
        var result = await _sut.CreateAsync(path, TestDekA, CancellationToken.None);
        Assert.True(result.Success);

        // Write something to ensure the first page is flushed to disk.
        using var cmd = result.Value!.CreateCommand();
        cmd.CommandText = "CREATE TABLE _smoke (x INTEGER)";
        await cmd.ExecuteNonQueryAsync();

        result.Value!.Dispose();
        // Release pooled file handles before reading raw bytes (WAL mode on Windows).
        SqliteConnection.ClearAllPools();

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.Length >= 16, "Brain file should have at least 16 bytes.");

        // A plain SQLite file always starts with "SQLite format 3\0".
        // SQLCipher encrypts page 1, so this header must NOT appear.
        var sqliteMagic = "SQLite format 3\0"u8;
        Assert.False(bytes[..16].SequenceEqual(sqliteMagic.ToArray()),
            "Brain file appears to be unencrypted — SQLite magic header found.");
    }
}
