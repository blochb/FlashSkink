using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Crypto;

/// <summary>
/// Holds the live DEK and open brain <see cref="SqliteConnection"/> for the duration of
/// an unlocked volume. Zeroes the DEK and closes the connection on dispose.
/// </summary>
public sealed class VolumeSession : IAsyncDisposable
{
    private readonly byte[] _dek;
    private readonly SqliteConnection? _brainConnection;
    private int _disposed;

    /// <summary>
    /// The live 32-byte data-encryption key. Do not zero; <see cref="DisposeAsync"/> owns it.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the session has been disposed.</exception>
    public byte[] Dek
    {
        get
        {
            if (_disposed != 0) { throw new ObjectDisposedException(nameof(VolumeSession)); }
            return _dek;
        }
    }

    /// <summary>
    /// The open encrypted brain connection. <see langword="null"/> only if the caller
    /// explicitly passes <see langword="null"/> (not expected in normal use).
    /// </summary>
    public SqliteConnection? BrainConnection => _brainConnection;

    internal VolumeSession(byte[] dek, SqliteConnection? brainConnection)
    {
        _dek = dek;
        _brainConnection = brainConnection;
    }

    /// <summary>
    /// Zeroes the DEK and closes the brain connection. Idempotent — safe to call multiple times.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        CryptographicOperations.ZeroMemory(_dek);
        _brainConnection?.Dispose(); // Dispose closes the connection implicitly.
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Thin orchestrator for volume open/close. Opens the vault, opens the encrypted brain
/// connection, runs schema migrations, and returns a live <see cref="VolumeSession"/>.
/// </summary>
public sealed class VolumeLifecycle
{
    private readonly KeyVault _vault;
    private readonly BrainConnectionFactory _brainFactory;
    private readonly MigrationRunner _migrationRunner;
    private readonly ILogger<VolumeLifecycle> _logger;

    /// <summary>Creates a <see cref="VolumeLifecycle"/> with the required collaborators.</summary>
    public VolumeLifecycle(
        KeyVault vault,
        BrainConnectionFactory brainFactory,
        MigrationRunner migrationRunner,
        ILogger<VolumeLifecycle> logger)
    {
        _vault = vault;
        _brainFactory = brainFactory;
        _migrationRunner = migrationRunner;
        _logger = logger;
    }

    /// <summary>
    /// Unlocks the vault, opens the encrypted brain connection, runs any pending schema
    /// migrations, and returns a live <see cref="VolumeSession"/>. The caller owns the
    /// returned session and must dispose it.
    /// </summary>
    public async Task<Result<VolumeSession>> OpenAsync(
        string skinkRoot, ReadOnlyMemory<byte> password, CancellationToken ct)
    {
        var vaultPath = Path.Combine(skinkRoot, ".flashskink", "vault.bin");
        var brainPath = Path.Combine(skinkRoot, ".flashskink", "brain.db");

        var unlockResult = await _vault.UnlockAsync(vaultPath, password, ct).ConfigureAwait(false);
        if (!unlockResult.Success)
        {
            _logger.LogError(
                "Vault unlock failed for {SkinkRoot}: {Code} — {Message}",
                skinkRoot, unlockResult.Error!.Code, unlockResult.Error.Message);
            return Result<VolumeSession>.Fail(unlockResult.Error!);
        }

        var dek = unlockResult.Value!;

        var brainResult = await _brainFactory
            .CreateAsync(brainPath, dek, ct).ConfigureAwait(false);
        if (!brainResult.Success)
        {
            CryptographicOperations.ZeroMemory(dek);
            _logger.LogError(
                "Brain connection failed for {BrainPath}: {Code} — {Message}",
                brainPath, brainResult.Error!.Code, brainResult.Error.Message);
            return Result<VolumeSession>.Fail(brainResult.Error!);
        }

        var connection = brainResult.Value!;

        var migrationResult = await _migrationRunner
            .RunAsync(connection, ct).ConfigureAwait(false);
        if (!migrationResult.Success)
        {
            connection.Dispose();
            CryptographicOperations.ZeroMemory(dek);
            _logger.LogError(
                "Brain migration failed for {BrainPath}: {Code} — {Message}",
                brainPath, migrationResult.Error!.Code, migrationResult.Error.Message);
            return Result<VolumeSession>.Fail(migrationResult.Error!);
        }

        return Result<VolumeSession>.Ok(new VolumeSession(dek, connection));
    }
}
