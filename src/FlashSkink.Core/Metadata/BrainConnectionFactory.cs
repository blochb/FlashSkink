using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Crypto;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Metadata;

/// <summary>
/// Opens an encrypted SQLCipher brain connection from a DEK, applies all required
/// pragmas, validates integrity, and returns the ready-to-use connection.
/// The caller owns the returned connection and must dispose it.
/// </summary>
public sealed class BrainConnectionFactory
{
    private readonly KeyDerivationService _kdf;
    private readonly ILogger<BrainConnectionFactory> _logger;

    /// <summary>Creates a <see cref="BrainConnectionFactory"/> with the given KDF service and logger.</summary>
    public BrainConnectionFactory(KeyDerivationService kdf, ILogger<BrainConnectionFactory> logger)
    {
        _kdf = kdf;
        _logger = logger;
    }

    /// <summary>
    /// Derives the brain key from <paramref name="dek"/>, opens an encrypted SQLCipher
    /// connection at <paramref name="brainPath"/>, applies all required pragmas, and
    /// validates integrity. Returns the open connection on success; the caller owns it
    /// and must dispose it.
    /// </summary>
    public async Task<Result<SqliteConnection>> CreateAsync(
        string brainPath, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        // Derive brain key before any await — stackalloc cannot cross an await boundary
        // (Principle 20). The resulting string is ephemeral heap memory; see Drift Note 6
        // in pr-1.5.md for full rationale.
        Span<byte> brainKeySpan = stackalloc byte[32];
        var brainKeyResult = _kdf.DeriveBrainKey(dek.Span, brainKeySpan);
        if (!brainKeyResult.Success)
        {
            return Result<SqliteConnection>.Fail(brainKeyResult.Error!);
        }

        var pragmaKey = $"PRAGMA key = \"x'{Convert.ToHexString(brainKeySpan)}'\"";

        // Zero the source bytes before the first await (Principles 20 + 31).
        CryptographicOperations.ZeroMemory(brainKeySpan);

        SqliteConnection? connection = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            // Pooling=False prevents SQLite connection-pool WAL file handles from
            // persisting past Dispose() on Windows, which causes temp-dir deletion to fail.
            connection = new SqliteConnection($"Data Source={brainPath};Pooling=False");
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // PRAGMA key must not be cancelled mid-flight — activating encryption is
            // an uncancellable step (Principle 17).
            using (var keyCmd = connection.CreateCommand())
            {
                keyCmd.CommandText = pragmaKey;
                await keyCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await ApplyPragmasAsync(connection, ct).ConfigureAwait(false);

            // integrity_check: non-"ok" means corrupt or wrong key produced garbage data.
            using (var integrityCmd = connection.CreateCommand())
            {
                integrityCmd.CommandText = "PRAGMA integrity_check";
                var integrityResult = (string?)(await integrityCmd
                    .ExecuteScalarAsync(ct).ConfigureAwait(false));

                if (integrityResult != "ok")
                {
                    _logger.LogError(
                        "PRAGMA integrity_check returned {Result} for {BrainPath}",
                        integrityResult, brainPath);
                    connection.Dispose();
                    return Result<SqliteConnection>.Fail(ErrorCode.DatabaseCorrupt,
                        $"PRAGMA integrity_check returned '{integrityResult}'.");
                }
            }

            return Result<SqliteConnection>.Ok(connection);
        }
        catch (OperationCanceledException ex)
        {
            connection?.Dispose();
            return Result<SqliteConnection>.Fail(ErrorCode.Cancelled,
                "Brain connection open cancelled.", ex);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 11 /* SQLITE_CORRUPT */)
        {
            connection?.Dispose();
            _logger.LogError(ex,
                "Brain database at {BrainPath} is corrupt (SQLITE_CORRUPT)", brainPath);
            return Result<SqliteConnection>.Fail(ErrorCode.DatabaseCorrupt,
                "SQLite reports the brain database file is corrupt.", ex);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26 /* SQLITE_NOTADB */)
        {
            connection?.Dispose();
            _logger.LogError(ex,
                "Brain database at {BrainPath} cannot be read — wrong key or not a database (SQLITE_NOTADB)",
                brainPath);
            return Result<SqliteConnection>.Fail(ErrorCode.DatabaseCorrupt,
                "Brain database cannot be read — wrong key or corrupt file.", ex);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6)
        {
            connection?.Dispose();
            _logger.LogError(ex,
                "Brain database at {BrainPath} is busy or locked (SqliteErrorCode={Code})",
                brainPath, ex.SqliteErrorCode);
            return Result<SqliteConnection>.Fail(ErrorCode.DatabaseLocked,
                "SQLite reports the brain database is busy or locked.", ex);
        }
        catch (SqliteException ex)
        {
            connection?.Dispose();
            _logger.LogError(ex,
                "SQLite error opening brain at {BrainPath} (SqliteErrorCode={Code})",
                brainPath, ex.SqliteErrorCode);
            return Result<SqliteConnection>.Fail(ErrorCode.DatabaseWriteFailed,
                $"SQLite failed to open brain. SqliteErrorCode={ex.SqliteErrorCode}.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            connection?.Dispose();
            _logger.LogError(ex,
                "Access denied opening brain database at {BrainPath}", brainPath);
            return Result<SqliteConnection>.Fail(ErrorCode.DatabaseWriteFailed,
                "Access denied opening brain database file.", ex);
        }
        catch (IOException ex)
        {
            connection?.Dispose();
            _logger.LogError(ex,
                "I/O error opening brain database at {BrainPath}", brainPath);
            return Result<SqliteConnection>.Fail(ErrorCode.DatabaseWriteFailed,
                "I/O error opening brain database file.", ex);
        }
        catch (Exception ex)
        {
            connection?.Dispose();
            _logger.LogError(ex,
                "Unexpected error opening brain connection at {BrainPath}", brainPath);
            return Result<SqliteConnection>.Fail(ErrorCode.Unknown,
                "Unexpected error opening brain connection.", ex);
        }
    }

    private static async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        string[] pragmas =
        [
            "PRAGMA journal_mode = WAL",
            "PRAGMA synchronous = EXTRA",
            "PRAGMA foreign_keys = ON",
            "PRAGMA temp_store = MEMORY",
        ];

        foreach (var pragma in pragmas)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = pragma;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
