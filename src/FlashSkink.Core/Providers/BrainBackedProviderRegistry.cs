using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers;

/// <summary>
/// Production <see cref="IProviderRegistry"/> that reads the <c>Providers</c> brain table once at
/// volume open and caches the constructed <see cref="IStorageProvider"/> adapters for the volume's
/// lifetime. Cross-cutting decision 4 of phase-4-providers: clients are cached per-volume, not
/// per-call.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Per-type dispatch.</strong> <see cref="CreateAsync"/> reads each active
/// <c>Providers</c> row and constructs an adapter based on the row's <c>ProviderType</c>:
/// <list type="bullet">
/// <item><description><c>"filesystem"</c> — parses <c>ProviderConfig</c> JSON to obtain
/// <c>rootPath</c> and calls <c>FileSystemProvider.Create</c>.</description></item>
/// <item><description><c>"google-drive"</c> / <c>"dropbox"</c> / <c>"onedrive"</c> — logs at
/// <see cref="LogLevel.Warning"/> and skips. §4.3–4.5 will fill in these arms.</description></item>
/// <item><description>Any other value — logs at <see cref="LogLevel.Warning"/> and skips.</description></item>
/// </list>
/// A construction failure for any single row is a per-provider warning, not a fatal error on
/// <see cref="CreateAsync"/> — the registry returns with whichever adapters successfully built.
/// The upload orchestrator already handles "provider not registered" via
/// <see cref="ErrorCode.ProviderUnreachable"/> when it ticks.
/// </para>
/// <para>
/// <strong>DEK lifecycle.</strong> The DEK passed to <see cref="CreateAsync"/> is used during the
/// initial construction pass and then dropped — the registry instance never retains a reference
/// (Principle 31). FileSystem rows do not need the DEK; cloud sections (§4.3–4.5) will decrypt
/// the refresh token at construction and hand the cleartext to the SDK client, which then becomes
/// the (unavoidable) holder of the cleartext token until disposal.
/// </para>
/// <para>
/// <strong>Threading.</strong> The cache is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> because <see cref="GetAsync"/> runs from the
/// upload orchestrator's background loop. Mutation through <c>AddTailAsync</c> /
/// <c>RemoveTailAsync</c> arrives in §4.6.
/// </para>
/// </remarks>
public sealed class BrainBackedProviderRegistry : IProviderRegistry, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IStorageProvider> _adapters = new();
    private readonly ILogger<BrainBackedProviderRegistry> _logger;
    private int _disposed;

    /// <summary>Internal constructor — production callers use <see cref="CreateAsync"/>.</summary>
    internal BrainBackedProviderRegistry(ILogger<BrainBackedProviderRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Reads the active <c>Providers</c> rows from <paramref name="brain"/>, constructs the
    /// corresponding <see cref="IStorageProvider"/> adapters, and returns the populated registry.
    /// </summary>
    /// <param name="brain">Brain access (Principle 36).</param>
    /// <param name="dek">32-byte DEK. Used for cloud-row refresh-token decryption (§4.3–4.5);
    /// in §4.1 only FileSystem rows are handled and the DEK is unused.</param>
    /// <param name="loggerFactory">Logger factory for adapter-specific loggers.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<Result<BrainBackedProviderRegistry>> CreateAsync(
        IBrainAccess brain,
        ReadOnlyMemory<byte> dek,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger<BrainBackedProviderRegistry>();
        var registry = new BrainBackedProviderRegistry(logger);

        try
        {
            ct.ThrowIfCancellationRequested();

            List<ProviderRow> rows;
            using (var scope = await brain.LockAsync(ct).ConfigureAwait(false))
            {
                var enumerable = await scope.Connection.QueryAsync<ProviderRow>(
                    new CommandDefinition(
                        "SELECT ProviderID, ProviderType, DisplayName, ProviderConfig " +
                        "FROM Providers " +
                        "WHERE IsActive = 1 " +
                        "ORDER BY AddedUtc",
                        cancellationToken: ct)).ConfigureAwait(false);
                rows = enumerable.ToList();
            }

            // Construction runs OUTSIDE the brain scope — adapter construction can be slow for
            // cloud providers in §4.3-4.5, and we don't want the brain gate held during HTTP.
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var adapter = TryBuildAdapter(row, dek, loggerFactory, logger);
                if (adapter is not null)
                {
                    registry._adapters[row.ProviderID] = adapter;
                    logger.LogInformation(
                        "Provider loaded from brain: {ProviderId} ({ProviderType}, {DisplayName})",
                        row.ProviderID, row.ProviderType, row.DisplayName);
                }
            }

            return Result<BrainBackedProviderRegistry>.Ok(registry);
        }
        catch (OperationCanceledException ex)
        {
            return Result<BrainBackedProviderRegistry>.Fail(
                ErrorCode.Cancelled, "Provider registry initialisation was cancelled.", ex);
        }
        catch (SqliteException ex) when (
            ex.SqliteErrorCode == 11 /* SQLITE_CORRUPT */ ||
            ex.SqliteErrorCode == 26 /* SQLITE_NOTADB */)
        {
            logger.LogError(ex, "Brain SELECT for Providers failed: corrupt brain.");
            return Result<BrainBackedProviderRegistry>.Fail(
                ErrorCode.VolumeCorrupt, "Brain is corrupt; cannot read Providers.", ex);
        }
        catch (SqliteException ex)
        {
            logger.LogError(ex, "Brain SELECT for Providers failed with SQLite error code {Code}.",
                ex.SqliteErrorCode);
            return Result<BrainBackedProviderRegistry>.Fail(
                ErrorCode.Unknown, "Brain SELECT for Providers failed.", ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error initialising provider registry.");
            return Result<BrainBackedProviderRegistry>.Fail(
                ErrorCode.Unknown, "Unexpected error initialising provider registry.", ex);
        }
    }

    /// <inheritdoc/>
    public ValueTask<Result<IStorageProvider>> GetAsync(string providerId, CancellationToken ct)
    {
        if (_adapters.TryGetValue(providerId, out var provider))
        {
            return ValueTask.FromResult(Result<IStorageProvider>.Ok(provider));
        }
        return ValueTask.FromResult(
            Result<IStorageProvider>.Fail(
                ErrorCode.ProviderUnreachable,
                $"Provider '{providerId}' is not registered."));
    }

    /// <inheritdoc/>
    public ValueTask<Result<IReadOnlyList<string>>> ListActiveProviderIdsAsync(CancellationToken ct)
    {
        IReadOnlyList<string> ids = _adapters.Keys.ToArray();
        return ValueTask.FromResult(Result<IReadOnlyList<string>>.Ok(ids));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var kvp in _adapters)
        {
            try
            {
                switch (kvp.Value)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to dispose provider adapter: {ProviderId}", kvp.Key);
            }
        }

        _adapters.Clear();
    }

    /// <summary>
    /// Per-row dispatch. Returns <see langword="null"/> when no adapter can be constructed; the
    /// caller logs and skips. Failures are warnings, not registry-fatal errors.
    /// </summary>
    private static IStorageProvider? TryBuildAdapter(
        ProviderRow row,
        ReadOnlyMemory<byte> dek,
        ILoggerFactory loggerFactory,
        ILogger<BrainBackedProviderRegistry> logger)
    {
        // The DEK is reserved for §4.3-4.5 cloud-provider construction; it is unused for
        // FileSystem rows. Reference it once so it is not flagged as unused (the parameter
        // remains in the signature for the future cloud arms).
        _ = dek;

        switch (row.ProviderType)
        {
            case "filesystem":
                return TryBuildFileSystemAdapter(row, loggerFactory, logger);

            case "google-drive":
            case "dropbox":
            case "onedrive":
                logger.LogWarning(
                    "Provider type '{ProviderType}' is not yet supported in this build; " +
                    "row '{ProviderId}' will be unavailable until a later release. (§4.3-4.5)",
                    row.ProviderType, row.ProviderID);
                return null;

            default:
                logger.LogWarning(
                    "Unknown provider type '{ProviderType}'; row '{ProviderId}' will be ignored.",
                    row.ProviderType, row.ProviderID);
                return null;
        }
    }

    private static IStorageProvider? TryBuildFileSystemAdapter(
        ProviderRow row,
        ILoggerFactory loggerFactory,
        ILogger<BrainBackedProviderRegistry> logger)
    {
        if (string.IsNullOrWhiteSpace(row.ProviderConfig))
        {
            logger.LogWarning(
                "FileSystem provider row '{ProviderId}' has no ProviderConfig; skipping.",
                row.ProviderID);
            return null;
        }

        FileSystemProviderConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(
                row.ProviderConfig,
                FileSystemProviderConfigJsonContext.Default.FileSystemProviderConfig);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "FileSystem provider row '{ProviderId}' has malformed ProviderConfig; skipping.",
                row.ProviderID);
            return null;
        }

        if (config is null || string.IsNullOrWhiteSpace(config.RootPath))
        {
            logger.LogWarning(
                "FileSystem provider row '{ProviderId}' has empty rootPath; skipping.",
                row.ProviderID);
            return null;
        }

        var createResult = FileSystemProvider.Create(
            row.ProviderID,
            row.DisplayName,
            config.RootPath,
            loggerFactory.CreateLogger<FileSystemProvider>());

        if (!createResult.Success)
        {
            logger.LogWarning(
                "FileSystem provider row '{ProviderId}' failed to construct: {Code} ({Message}); skipping.",
                row.ProviderID,
                createResult.Error!.Code,
                createResult.Error!.Message);
            return null;
        }

        return createResult.Value!;
    }

    /// <summary>
    /// Row shape for the brain SELECT. Property names match the SQL column aliases.
    /// </summary>
    private sealed record ProviderRow(
        string ProviderID,
        string ProviderType,
        string DisplayName,
        string? ProviderConfig);
}
