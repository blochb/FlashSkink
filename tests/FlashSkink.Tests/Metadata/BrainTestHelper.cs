using Dapper;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlashSkink.Tests.Metadata;

/// <summary>
/// Shared in-memory database helpers used by all repository test classes.
/// </summary>
internal static class BrainTestHelper
{
    /// <summary>
    /// Creates an open in-memory SQLite connection with foreign-key enforcement enabled.
    /// The connection string uses the "Data Source=:memory:" form (trailing colon required;
    /// omitting it creates a file literally named ":memory").
    /// </summary>
    internal static SqliteConnection CreateInMemoryConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Runs the V001 migration so all brain tables exist on the given connection.</summary>
    internal static async Task ApplySchemaAsync(SqliteConnection conn)
    {
        var runner = new MigrationRunner(NullLogger<MigrationRunner>.Instance);
        var result = await runner.RunAsync(conn, CancellationToken.None);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Schema migration failed in test setup: {result.Error!.Message}");
        }
    }

    /// <summary>Inserts a minimal <c>Providers</c> row to satisfy <c>TailUploads.ProviderID</c> FK.</summary>
    internal static void InsertTestProvider(SqliteConnection conn, string providerId)
    {
        conn.Execute(
            """
            INSERT INTO Providers (ProviderID, ProviderType, DisplayName, HealthStatus, AddedUtc)
            VALUES (@Id, 'FileSystem', 'Test Provider', 'OK', @Now)
            """,
            new { Id = providerId, Now = DateTime.UtcNow.ToString("O") });
    }

    /// <summary>
    /// Inserts a minimal <c>Files</c> row to satisfy <c>TailUploads.FileID</c> and
    /// <c>UploadSessions.FileID</c> FK constraints.
    /// </summary>
    internal static void InsertTestFile(
        SqliteConnection conn,
        string fileId,
        string name = "file",
        string? parentId = null,
        bool isFolder = false)
    {
        var now = DateTime.UtcNow.ToString("O");
        conn.Execute(
            """
            INSERT INTO Files
                (FileID, ParentID, IsFolder, IsSymlink, Name, VirtualPath,
                 SizeBytes, CreatedUtc, ModifiedUtc, AddedUtc)
            VALUES
                (@FileId, @ParentId, @IsFolder, 0, @Name, @VirtualPath, 0, @Now, @Now, @Now)
            """,
            new
            {
                FileId = fileId,
                ParentId = parentId,
                IsFolder = isFolder ? 1 : 0,
                Name = name,
                VirtualPath = parentId is null ? name : $"{parentId}/{name}",
                Now = now,
            });
    }

    /// <summary>Inserts a minimal <c>Blobs</c> row. Used in repository tests that need blob FK targets.</summary>
    internal static void InsertTestBlob(
        SqliteConnection conn,
        string blobId,
        string blobPath = "blobs/test.bin",
        string? sha256 = null)
    {
        conn.Execute(
            """
            INSERT INTO Blobs
                (BlobID, EncryptedSize, PlaintextSize, PlaintextSHA256,
                 EncryptedXXHash, BlobPath, CreatedUtc)
            VALUES
                (@BlobId, 1024, 1000, @Sha256, 'xxhash-test', @BlobPath, @Now)
            """,
            new
            {
                BlobId = blobId,
                Sha256 = sha256 ?? $"sha256-{blobId}",
                BlobPath = blobPath,
                Now = DateTime.UtcNow.ToString("O"),
            });
    }
}
