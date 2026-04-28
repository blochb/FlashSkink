using FlashSkink.Core.Abstractions.Results;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Metadata;

/// <summary>
/// Applies versioned embedded SQL migration scripts to bring the brain schema to the
/// current expected version. Each migration runs in its own transaction.
/// </summary>
public sealed class MigrationRunner
{
    /// <summary>The highest schema version this build understands.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly ILogger<MigrationRunner> _logger;

    private readonly record struct MigrationEntry(
        int Version,
        string ResourceName,
        string Description);

    private static readonly MigrationEntry[] Migrations =
    [
        new(1, "FlashSkink.Core.Metadata.Migrations.V001_InitialSchema.sql", "Initial schema"),
    ];

    // Validates that Migrations is correctly ordered and all embedded resources exist.
    // Throws InvalidOperationException at type-init time so misconfiguration fails fast.
    static MigrationRunner()
    {
        var assembly = typeof(MigrationRunner).Assembly;

        for (var i = 0; i < Migrations.Length; i++)
        {
            if (i > 0 && Migrations[i].Version <= Migrations[i - 1].Version)
            {
                throw new InvalidOperationException(
                    $"Migrations array must be ordered by ascending Version. " +
                    $"Found Version {Migrations[i].Version} after {Migrations[i - 1].Version}.");
            }

            using var stream = assembly.GetManifestResourceStream(Migrations[i].ResourceName);
            if (stream is null)
            {
                throw new InvalidOperationException(
                    $"Embedded migration resource not found: {Migrations[i].ResourceName}. " +
                    $"Verify the EmbeddedResource is configured in FlashSkink.Core.csproj.");
            }
        }
    }

    /// <summary>Creates a <see cref="MigrationRunner"/> with the given logger.</summary>
    public MigrationRunner(ILogger<MigrationRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Applies any missing migration scripts to <paramref name="connection"/> in version
    /// order. Each script runs inside its own transaction with a <c>SchemaVersions</c>
    /// insert. Idempotent — calling on an already-current schema returns
    /// <see cref="Result.Ok()"/> without running any scripts.
    /// </summary>
    public async Task<Result> RunAsync(SqliteConnection connection, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var currentVersion = await ReadCurrentVersionAsync(connection, ct).ConfigureAwait(false);

            if (currentVersion > CurrentSchemaVersion)
            {
                return Result.Fail(ErrorCode.VolumeIncompatibleVersion,
                    $"Brain schema v{currentVersion} is newer than this build " +
                    $"(v{CurrentSchemaVersion}). Update FlashSkink to open this volume.");
            }

            foreach (var migration in Migrations)
            {
                if (migration.Version <= currentVersion)
                {
                    continue;
                }

                ct.ThrowIfCancellationRequested();

                var applyResult = await ApplyMigrationAsync(connection, migration, ct)
                    .ConfigureAwait(false);

                if (!applyResult.Success)
                {
                    return applyResult;
                }
            }

            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "Brain migration cancelled.", ex);
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex,
                "SQLite error during brain migration (SqliteErrorCode={Code})",
                ex.SqliteErrorCode);
            return Result.Fail(ErrorCode.DatabaseMigrationFailed,
                $"SQLite error during brain migration. SqliteErrorCode={ex.SqliteErrorCode}.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during brain migration");
            return Result.Fail(ErrorCode.Unknown, "Unexpected error during brain migration.", ex);
        }
    }

    private static async Task<int> ReadCurrentVersionAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        // Check sqlite_master (always present) rather than querying SchemaVersions directly.
        // This lets genuine SqliteExceptions (corruption, I/O) propagate to the caller
        // instead of being swallowed by a broad catch that masks DatabaseCorrupt scenarios.
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SchemaVersions'";
        var tableExists =
            (long)(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))! > 0;

        if (!tableExists) { return 0; }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersions";
        // COALESCE(MAX(Version), 0) is always non-null — the ! suppression is safe.
        return (int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private async Task<Result> ApplyMigrationAsync(
        SqliteConnection connection,
        MigrationEntry migration,
        CancellationToken ct)
    {
        var sql = await LoadSqlAsync(migration.ResourceName, ct).ConfigureAwait(false);

        using var tx = connection.BeginTransaction();
        try
        {
            using (var scriptCmd = connection.CreateCommand())
            {
                scriptCmd.Transaction = tx;
                scriptCmd.CommandText = sql;
                await scriptCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using (var insertCmd = connection.CreateCommand())
            {
                insertCmd.Transaction = tx;
                insertCmd.CommandText =
                    "INSERT INTO SchemaVersions (Version, AppliedUtc, Description) " +
                    "VALUES (@v, @ts, @desc)";
                insertCmd.Parameters.AddWithValue("@v", migration.Version);
                insertCmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
                insertCmd.Parameters.AddWithValue("@desc", migration.Description);
                await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();

            _logger.LogInformation(
                "Applied brain migration v{Version} ({Description})",
                migration.Version, migration.Description);

            return Result.Ok();
        }
        catch (OperationCanceledException)
        {
            // SqliteTransaction.Dispose() rolls back if not committed.
            throw;
        }
        catch (Exception ex)
        {
            // SqliteTransaction.Dispose() will roll back; explicit early log here.
            _logger.LogError(ex,
                "Brain migration v{Version} ({Description}) failed",
                migration.Version, migration.Description);
            return Result.Fail(ErrorCode.DatabaseMigrationFailed,
                $"Brain migration v{migration.Version} ({migration.Description}) failed.", ex);
        }
    }

    private static async Task<string> LoadSqlAsync(string resourceName, CancellationToken ct)
    {
        var assembly = typeof(MigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded migration resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}
