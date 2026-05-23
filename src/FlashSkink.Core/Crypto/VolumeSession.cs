using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Crypto;

/// <summary>
/// Holds the live DEK and the brain <see cref="IBrainAccess"/> wrapper for the duration
/// of an unlocked volume. Zeroes the DEK and disposes the brain access (which closes the
/// underlying connection) on dispose.
/// </summary>
public sealed class VolumeSession : IAsyncDisposable
{
    private readonly byte[] _dek;
    private readonly BrainAccess? _brain;
    private int _disposed;

    /// <summary>
    /// The live 32-byte data-encryption key. Do not zero; <see cref="DisposeAsync"/> owns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Lifetime contract.</strong> Callers MUST hold a strong reference to the owning
    /// <see cref="VolumeSession"/> for the entire duration of any read of the returned array.
    /// <see cref="DisposeAsync"/> zeroes the backing byte[] in place — a reference grabbed
    /// before dispose still points to the same array after dispose, but its contents will have
    /// been overwritten with zeros. There is no defensive copy: the caller would have no way
    /// to zero the copy, and copying defeats the zero-on-dispose guarantee.
    /// </para>
    /// <para>
    /// <strong>Ordering responsibility belongs to the volume orchestrator.</strong>
    /// <c>FlashSkinkVolume.DisposeAsync</c> holds the volume-wide single-writer gate when it
    /// invokes <see cref="DisposeAsync"/> on the session, which serialises this method with
    /// every public crypto-using operation. Components that read <see cref="Dek"/> outside that
    /// gate (e.g. background services) are themselves drained before
    /// <see cref="VolumeSession.DisposeAsync"/> runs.
    /// </para>
    /// <para>
    /// The <see cref="ObjectDisposedException"/> below is a best-effort guard for the
    /// "dispose has already finished" case — it does <em>not</em> prevent the
    /// dispose-mid-read race the lifetime contract above is designed to exclude.
    /// </para>
    /// </remarks>
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
    /// The brain access wrapper. <see langword="null"/> only if the caller explicitly
    /// passes <see langword="null"/> (not expected in normal use). All brain SQL flows
    /// through this; raw <see cref="SqliteConnection"/> is never exposed (Principle 36).
    /// </summary>
    public IBrainAccess? Brain => _brain;

    internal VolumeSession(byte[] dek, BrainAccess? brain)
    {
        _dek = dek;
        _brain = brain;
    }

    /// <summary>
    /// Zeroes the DEK and disposes the brain access (which closes the underlying
    /// connection). Idempotent — safe to call multiple times.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_dek);
        if (_brain is not null)
        {
            await _brain.DisposeAsync().ConfigureAwait(false);
        }
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

        // Wrap the raw connection in BrainAccess immediately. From here on, no
        // raw SqliteConnection flows downstream (Principle 36 — except the
        // sanctioned migration/factory paths that ran above).
        var brain = new BrainAccess(connection);
        return Result<VolumeSession>.Ok(new VolumeSession(dek, brain));
    }
}
