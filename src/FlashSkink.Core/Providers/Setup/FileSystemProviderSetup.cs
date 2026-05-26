using System.Text.Json;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers.Setup;

/// <summary>
/// First <see cref="IProviderSetup"/> implementation — the no-OAuth <see cref="ProviderSetupKind.LocalPath"/>
/// setup. The template the cloud setups in §4.3–4.5 follow.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Validation rules</strong> (<see cref="ValidatePathAsync"/>):
/// <list type="number">
/// <item><description><paramref name="path"/> is non-empty and well-formed.</description></item>
/// <item><description>The path exists and is a directory.</description></item>
/// <item><description>The path is writable (probe-write under <c>{path}/_health/</c>).</description></item>
/// <item><description>The path is NOT the skink root or a subdirectory of it (Blueprint §24.3 —
/// would create a backup loop).</description></item>
/// </list>
/// Failures of (1)–(2) and (4) return <see cref="ValidationResult.Invalid(string)"/>. Failure of
/// (3) returns either <see cref="ValidationResult.Invalid"/> (the path is not writable from this
/// process) or <see cref="ErrorCode.StagingFailed"/> (real I/O failure during the probe).
/// </para>
/// <para>
/// <strong>OAuth methods</strong> return <see cref="ErrorCode.InvalidArgument"/> — FileSystem is
/// <see cref="ProviderSetupKind.LocalPath"/>.
/// </para>
/// <para>
/// <strong><see cref="CreateProviderAsync"/></strong> ignores <c>encryptedToken</c>,
/// <c>credentials</c>, and <c>dek</c> (LocalPath has none); the <c>rootPath</c> comes from
/// <c>providerConfigJson</c> (the existing <see cref="FileSystemProviderConfig"/> shape).
/// </para>
/// </remarks>
internal sealed class FileSystemProviderSetup : IProviderSetup
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FileSystemProviderSetup> _logger;

    /// <inheritdoc/>
    public string ProviderType => "filesystem";

    /// <inheritdoc/>
    public string DisplayName => "Local folder";

    /// <inheritdoc/>
    public ProviderSetupKind SetupKind => ProviderSetupKind.LocalPath;

    /// <summary>Creates a <see cref="FileSystemProviderSetup"/>.</summary>
    public FileSystemProviderSetup(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<FileSystemProviderSetup>();
    }

    /// <inheritdoc/>
    public Task<Result<Uri>> GetAuthorizationUriAsync(
        string redirectUri,
        string codeChallenge,
        ProviderCredentials credentials,
        CancellationToken ct)
    {
        return Task.FromResult(Result<Uri>.Fail(
            ErrorCode.InvalidArgument,
            "FileSystem provider does not use OAuth."));
    }

    /// <inheritdoc/>
    public Task<Result<byte[]>> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        ProviderCredentials credentials,
        ReadOnlyMemory<byte> dek,
        CancellationToken ct)
    {
        return Task.FromResult(Result<byte[]>.Fail(
            ErrorCode.InvalidArgument,
            "FileSystem provider does not use OAuth."));
    }

    /// <inheritdoc/>
    public Task<Result<ValidationResult>> ValidatePathAsync(
        string path,
        string skinkRoot,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(path))
            {
                return Task.FromResult(Result<ValidationResult>.Ok(
                    ValidationResult.Invalid("Path is empty.")));
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (ArgumentException)
            {
                return Task.FromResult(Result<ValidationResult>.Ok(
                    ValidationResult.Invalid($"Path '{path}' is not a valid filesystem path.")));
            }
            catch (PathTooLongException)
            {
                return Task.FromResult(Result<ValidationResult>.Ok(
                    ValidationResult.Invalid($"Path '{path}' is too long.")));
            }

            if (!Directory.Exists(fullPath))
            {
                return Task.FromResult(Result<ValidationResult>.Ok(
                    ValidationResult.Invalid($"Path '{path}' does not exist or is not a directory.")));
            }

            // Reject paths that are the skink root itself or any subdirectory of it — would
            // create a backup loop (Blueprint §24.3).
            var skinkRootFull = NormalizeWithTrailingSeparator(Path.GetFullPath(skinkRoot));
            var candidateFull = NormalizeWithTrailingSeparator(fullPath);

            if (string.Equals(candidateFull, skinkRootFull, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Result<ValidationResult>.Ok(
                    ValidationResult.Invalid(
                        "Path is the skink root. The skink cannot back up to itself.")));
            }

            if (candidateFull.StartsWith(skinkRootFull, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Result<ValidationResult>.Ok(
                    ValidationResult.Invalid(
                        "Path is inside the skink. Backing up to a folder inside the skink would create a loop.")));
            }

            // Probe-write to confirm the directory is writable from this process.
            var healthDir = Path.Combine(fullPath, "_health");
            string? probe = null;
            try
            {
                Directory.CreateDirectory(healthDir);
                probe = Path.Combine(healthDir, $"{Guid.NewGuid():N}.probe");
                File.WriteAllBytes(probe, [0x01]);
            }
            catch (UnauthorizedAccessException)
            {
                return Task.FromResult(Result<ValidationResult>.Ok(
                    ValidationResult.Invalid($"Path '{path}' is not writable by this process.")));
            }
            finally
            {
                if (probe is not null)
                {
                    try { File.Delete(probe); }
                    catch (IOException) { /* best-effort cleanup */ }
                    catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
                }
            }

            return Task.FromResult(Result<ValidationResult>.Ok(ValidationResult.Valid));
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(Result<ValidationResult>.Fail(
                ErrorCode.Cancelled, "Path validation was cancelled.", ex));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "I/O failure while validating path: {Path}", path);
            return Task.FromResult(Result<ValidationResult>.Fail(
                ErrorCode.StagingFailed, $"I/O failure while validating path '{path}'.", ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error validating path: {Path}", path);
            return Task.FromResult(Result<ValidationResult>.Fail(
                ErrorCode.Unknown, $"Unexpected error validating path '{path}'.", ex));
        }
    }

    /// <inheritdoc/>
    public Task<Result<IStorageProvider>> CreateProviderAsync(
        string providerId,
        string displayName,
        byte[] encryptedToken,
        ProviderCredentials credentials,
        string? providerConfigJson,
        ReadOnlyMemory<byte> dek,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(providerId))
            {
                return Task.FromResult(Result<IStorageProvider>.Fail(
                    ErrorCode.InvalidArgument, "providerId must be non-empty."));
            }
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return Task.FromResult(Result<IStorageProvider>.Fail(
                    ErrorCode.InvalidArgument, "displayName must be non-empty."));
            }
            if (string.IsNullOrWhiteSpace(providerConfigJson))
            {
                return Task.FromResult(Result<IStorageProvider>.Fail(
                    ErrorCode.InvalidArgument,
                    "FileSystem provider requires providerConfigJson with rootPath."));
            }

            FileSystemProviderConfig? config;
            try
            {
                config = JsonSerializer.Deserialize(
                    providerConfigJson,
                    FileSystemProviderConfigJsonContext.Default.FileSystemProviderConfig);
            }
            catch (JsonException ex)
            {
                return Task.FromResult(Result<IStorageProvider>.Fail(
                    ErrorCode.InvalidArgument,
                    "providerConfigJson is not valid FileSystem provider config.",
                    ex));
            }

            if (config is null || string.IsNullOrWhiteSpace(config.RootPath))
            {
                return Task.FromResult(Result<IStorageProvider>.Fail(
                    ErrorCode.InvalidArgument,
                    "providerConfigJson must contain a non-empty rootPath."));
            }

            var createResult = FileSystemProvider.Create(
                providerId,
                displayName,
                config.RootPath,
                _loggerFactory.CreateLogger<FileSystemProvider>());

            if (!createResult.Success)
            {
                return Task.FromResult(Result<IStorageProvider>.Fail(createResult.Error));
            }

            return Task.FromResult(Result<IStorageProvider>.Ok(createResult.Value));
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(Result<IStorageProvider>.Fail(
                ErrorCode.Cancelled, "Create FileSystem provider was cancelled.", ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating FileSystem provider: {ProviderId}", providerId);
            return Task.FromResult(Result<IStorageProvider>.Fail(
                ErrorCode.Unknown, "Unexpected error creating FileSystem provider.", ex));
        }
    }

    /// <summary>
    /// Returns <paramref name="fullPath"/> with a guaranteed trailing
    /// <see cref="Path.DirectorySeparatorChar"/> so prefix comparisons like
    /// <c>candidate.StartsWith(skinkRoot)</c> do not match unrelated siblings (e.g. matching
    /// <c>/foo</c> against <c>/foobar</c>).
    /// </summary>
    private static string NormalizeWithTrailingSeparator(string fullPath)
    {
        var sep = Path.DirectorySeparatorChar;
        return fullPath.EndsWith(sep) ? fullPath : fullPath + sep;
    }
}
