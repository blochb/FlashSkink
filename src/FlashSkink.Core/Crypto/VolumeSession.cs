using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Data.Sqlite;

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

    /// <summary>The live 32-byte data-encryption key. Do not zero; <see cref="DisposeAsync"/> owns it.</summary>
    public byte[] Dek => _dek;

    /// <summary>
    /// The open encrypted brain connection, or <see langword="null"/> when the connection
    /// has not yet been wired (Phase 1 stub — wired in §1.5).
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
        _brainConnection?.Close();
        _brainConnection?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Thin orchestrator for volume open/close. Phase 1 skeleton — wired to
/// <c>BrainConnectionFactory</c> and <c>MigrationRunner</c> in §1.5.
/// </summary>
public sealed class VolumeLifecycle
{
    private readonly KeyVault _vault;
    private readonly KeyDerivationService _kdf;

    /// <summary>Creates a <see cref="VolumeLifecycle"/> with the given vault and KDF service.</summary>
    public VolumeLifecycle(KeyVault vault, KeyDerivationService kdf)
    {
        _vault = vault;
        _kdf = kdf;
    }

    /// <summary>
    /// Unlocks the vault at <c>[skinkRoot]/.flashskink/vault.bin</c>, derives the brain key,
    /// and returns an open <see cref="VolumeSession"/>. The brain connection is <see langword="null"/>
    /// in this Phase 1 stub — wired in §1.5 once <c>BrainConnectionFactory</c> exists.
    /// The caller owns the returned session and must dispose it.
    /// </summary>
    public async Task<Result<VolumeSession>> OpenAsync(
        string skinkRoot, ReadOnlyMemory<byte> password, CancellationToken ct)
    {
        var vaultPath = Path.Combine(skinkRoot, ".flashskink", "vault.bin");

        var unlockResult = await _vault.UnlockAsync(vaultPath, password, ct).ConfigureAwait(false);
        if (!unlockResult.Success)
        {
            return Result<VolumeSession>.Fail(unlockResult.Error!);
        }

        var dek = unlockResult.Value!;

        Span<byte> brainKey = stackalloc byte[32];
        var brainKeyResult = _kdf.DeriveBrainKey(dek, brainKey);
        CryptographicOperations.ZeroMemory(brainKey);

        if (!brainKeyResult.Success)
        {
            CryptographicOperations.ZeroMemory(dek);
            return Result<VolumeSession>.Fail(brainKeyResult.Error!);
        }

        // BrainConnectionFactory wired in §1.5 — brainConnection is null until then.
        return Result<VolumeSession>.Ok(new VolumeSession(dek, brainConnection: null));
    }
}
