using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Metadata;

public class MigrationRunnerTests
{
    private readonly MigrationRunner _sut =
        new(NullLogger<MigrationRunner>.Instance);

    private static SqliteConnection CreateInMemoryConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON";
        pragma.ExecuteNonQuery();
        return connection;
    }

    // ── Embedded resource ─────────────────────────────────────────────────────

    [Fact]
    public void EmbeddedResource_V001_IsFoundInAssemblyManifest()
    {
        var names = typeof(MigrationRunner).Assembly.GetManifestResourceNames();
        Assert.Contains(
            "FlashSkink.Core.Metadata.Migrations.V001_InitialSchema.sql", names);
    }

    // ── Fresh database ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_OnFreshDatabase_CreatesAllTables()
    {
        using var connection = CreateInMemoryConnection();

        var result = await _sut.RunAsync(connection, CancellationToken.None);

        Assert.True(result.Success);

        string[] expectedTables =
        [
            "SchemaVersions", "Files", "Blobs", "Providers",
            "TailUploads", "UploadSessions", "WAL",
            "BackgroundFailures", "ActivityLog", "DeleteLog", "Settings",
        ];

        foreach (var table in expectedTables)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
            cmd.Parameters.AddWithValue("@name", table);
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            Assert.True(count == 1, $"Expected table '{table}' to exist after migration.");
        }
    }

    [Fact]
    public async Task RunAsync_OnFreshDatabase_SetsSchemaVersionToOne()
    {
        using var connection = CreateInMemoryConnection();

        await _sut.RunAsync(connection, CancellationToken.None);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(Version) FROM SchemaVersions";
        var version = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, version);
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_IsIdempotent()
    {
        using var connection = CreateInMemoryConnection();

        var first = await _sut.RunAsync(connection, CancellationToken.None);
        var second = await _sut.RunAsync(connection, CancellationToken.None);

        Assert.True(first.Success);
        Assert.True(second.Success);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(Version) FROM SchemaVersions";
        var version = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, version);
    }

    // ── Version checks ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_OnSchemaNewerThanBuild_ReturnsVolumeIncompatibleVersion()
    {
        using var connection = CreateInMemoryConnection();

        // Manually create the SchemaVersions table and inject a future version.
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                "CREATE TABLE SchemaVersions " +
                "(Version INTEGER PRIMARY KEY, AppliedUtc TEXT NOT NULL, Description TEXT NOT NULL); " +
                "INSERT INTO SchemaVersions (Version, AppliedUtc, Description) " +
                "VALUES (999, '2099-01-01T00:00:00Z', 'Future version')";
            await setup.ExecuteNonQueryAsync();
        }

        var result = await _sut.RunAsync(connection, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.VolumeIncompatibleVersion, result.Error!.Code);
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Cancelled_ReturnsCancelled()
    {
        using var connection = CreateInMemoryConnection();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _sut.RunAsync(connection, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    // ── Constraint enforcement ────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CreatesForeignKeyConstraints()
    {
        using var connection = CreateInMemoryConnection();
        await _sut.RunAsync(connection, CancellationToken.None);

        // Insert a Files row referencing a non-existent ParentID — must fail.
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO Files " +
            "(FileID, ParentID, IsFolder, IsSymlink, Name, VirtualPath, " +
            " SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc) " +
            "VALUES ('f1', 'nonexistent', 0, 0, 'test.txt', '/test.txt', " +
            "        0, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z')";

        await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task RunAsync_UniqueIndexOnFilesParentName_IsEnforced()
    {
        using var connection = CreateInMemoryConnection();
        await _sut.RunAsync(connection, CancellationToken.None);

        const string insertSql =
            "INSERT INTO Files " +
            "(FileID, ParentID, IsFolder, IsSymlink, Name, VirtualPath, " +
            " SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc) " +
            "VALUES (@id, NULL, 1, 0, 'duplicate', '/duplicate', " +
            "        0, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z')";

        using var first = connection.CreateCommand();
        first.CommandText = insertSql;
        first.Parameters.AddWithValue("@id", "folder-1");
        await first.ExecuteNonQueryAsync();

        using var second = connection.CreateCommand();
        second.CommandText = insertSql;
        second.Parameters.AddWithValue("@id", "folder-2");

        await Assert.ThrowsAsync<SqliteException>(() => second.ExecuteNonQueryAsync());
    }
}
