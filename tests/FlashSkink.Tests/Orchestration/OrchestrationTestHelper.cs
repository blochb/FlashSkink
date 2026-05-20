using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using FlashSkink.Core.Crypto;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlashSkink.Tests.Orchestration;

/// <summary>
/// Shared utilities for tests that need to peek at or mutate the brain directly
/// (e.g. backfill-on-open verification, legacy-key migration scenarios).
/// Mirrors the helper pattern in <c>FlashSkinkVolumeTests</c> but exposed as a
/// static class so multiple orchestration test classes can share it without
/// duplication.
/// </summary>
internal static class OrchestrationTestHelper
{
    /// <summary>
    /// Opens a fresh <see cref="SqliteConnection"/> against the brain at
    /// <c>[skinkRoot]/.flashskink/brain.db</c>. The caller owns disposal.
    /// </summary>
    internal static async Task<SqliteConnection> OpenBrainConnectionAsync(
        string skinkRoot, string password)
    {
        var vaultPath = Path.Combine(skinkRoot, ".flashskink", "vault.bin");
        var brainPath = Path.Combine(skinkRoot, ".flashskink", "brain.db");
        var kdf = new KeyDerivationService();
        var keyVault = new KeyVault(kdf, new MnemonicService());
        var brainFactory = new BrainConnectionFactory(
            kdf, NullLogger<BrainConnectionFactory>.Instance);

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var unlockResult = await keyVault.UnlockAsync(
            vaultPath, new ReadOnlyMemory<byte>(passwordBytes), CancellationToken.None);
        CryptographicOperations.ZeroMemory(passwordBytes);

        if (!unlockResult.Success)
        {
            throw new InvalidOperationException(
                $"Test brain unlock failed: {unlockResult.Error!.Message}");
        }

        var dek = unlockResult.Value!;
        var brainResult = await brainFactory.CreateAsync(
            brainPath, dek, CancellationToken.None);
        CryptographicOperations.ZeroMemory(dek);

        if (!brainResult.Success)
        {
            throw new InvalidOperationException(
                $"Test brain open failed: {brainResult.Error!.Message}");
        }

        return brainResult.Value!;
    }

    /// <summary>Reads a single <c>Settings</c> row by key, or <see langword="null"/> if absent.</summary>
    internal static async Task<string?> ReadSettingAsync(
        string skinkRoot, string password, string key)
    {
        await using var connection = await OpenBrainConnectionAsync(skinkRoot, password);
        return await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT Value FROM Settings WHERE Key = @Key", new { Key = key });
    }

    /// <summary>
    /// Replaces (or inserts) a <c>Settings</c> row. Used by tests that simulate legacy or
    /// hand-edited brains.
    /// </summary>
    internal static async Task UpsertSettingAsync(
        string skinkRoot, string password, string key, string value)
    {
        await using var connection = await OpenBrainConnectionAsync(skinkRoot, password);
        await connection.ExecuteAsync(
            "INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@Key, @Value)",
            new { Key = key, Value = value });
    }

    /// <summary>Deletes a <c>Settings</c> row by key (no-op if absent).</summary>
    internal static async Task DeleteSettingAsync(
        string skinkRoot, string password, string key)
    {
        await using var connection = await OpenBrainConnectionAsync(skinkRoot, password);
        await connection.ExecuteAsync(
            "DELETE FROM Settings WHERE Key = @Key", new { Key = key });
    }

    /// <summary>
    /// Returns the current process's stamp value — the same
    /// <see cref="AssemblyInformationalVersionAttribute"/> that production code reads.
    /// Works against MinVer-stamped (CI) and unstamped (local dev) builds alike — the
    /// assertion target is "the stored value equals what the running assembly reports,"
    /// not a literal version string.
    /// </summary>
    internal static string CurrentInformationalVersion
        => typeof(FlashSkink.Core.Orchestration.FlashSkinkVolume).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
               .InformationalVersion
           ?? "0.0.0-unknown";
}
