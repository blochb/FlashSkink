using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dropbox.Api;
using Dropbox.Api.Files;
using Dropbox.Api.Stone;
using Dropbox.Api.Users;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;
using DropboxMetadata = Dropbox.Api.Files.Metadata;

namespace FlashSkink.Core.Providers.Dropbox;

/// <summary>
/// Production <see cref="IStorageProvider"/> over Dropbox's upload-sessions protocol.
/// Drives <c>upload_session/start</c> → <c>upload_session/append_v2</c> →
/// <c>upload_session/finish</c> through the SDK's <see cref="DropboxClient.Files"/> routes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Session model.</strong> <see cref="BeginUploadAsync"/> opens an empty session and
/// returns the SDK's <c>SessionId</c> alongside the destination path encoded in the
/// <see cref="UploadSession.SessionUri"/> JSON envelope (per Discrepancy 7 of the plan — the
/// finalisation step requires the path, but the public <see cref="UploadSession"/> record has only
/// one opaque field for state). The 48 h session TTL (Blueprint §15.2) is reflected in
/// <see cref="UploadSession.ExpiresAt"/>.
/// </para>
/// <para>
/// <strong>Verification.</strong> Does NOT implement <c>ISupportsRemoteHashCheck</c>. Dropbox's
/// <c>ContentHash</c> is a SHA-256-based digest, structurally incompatible with the
/// <c>ulong</c> XXHash64 the capability interface returns. The AES-GCM authentication tag on the
/// encrypted blob (Principle 6) is the cryptographic authenticator. The Dropbox-side hash + size
/// are logged at <see cref="LogLevel.Information"/> on finalisation for observability. Resolved at
/// Gate 1 (Discrepancy 1).
/// </para>
/// <para>
/// <strong>Notification bus.</strong> The provider does not reference <c>INotificationBus</c>.
/// Per Principle 24 the publisher is <c>UploadQueueService</c>, which observes
/// <see cref="Result.Fail"/> outcomes and publishes accordingly.
/// </para>
/// <para>
/// <strong>Exception translation.</strong> Every public method follows the catch ordering
/// documented in <c>.claude/plans/pr-4.4.md</c> Method-body contracts → UploadRangeAsync table:
/// typed <see cref="ApiException{TError}"/> → <see cref="AuthException"/> →
/// <see cref="RateLimitException"/> → <see cref="RetryException"/> →
/// <see cref="BadInputException"/> → <see cref="HttpException"/> → BCL networking
/// (<see cref="HttpRequestException"/>, <see cref="TaskCanceledException"/> disambiguated from
/// user cancellation, <see cref="IOException"/>) → <see cref="Exception"/> fallback. Each maps to a
/// stable <see cref="ErrorCode"/> the <c>RangeUploader</c> retry classifier understands.
/// </para>
/// </remarks>
internal sealed partial class DropboxProvider : IStorageProvider, IAsyncDisposable
{
    /// <summary>48-hour session TTL per Blueprint §15.2.</summary>
    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(48);

    private readonly DropboxClientBundle _bundle;
    private readonly string _rootPath;
    private readonly ILogger<DropboxProvider> _logger;
    private int _disposed;

    /// <inheritdoc/>
    public string ProviderID { get; }

    /// <inheritdoc/>
    public string ProviderType => "dropbox";

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <summary>
    /// Root path resolved at setup time (e.g. <c>"/FlashSkink Backup"</c>). Surfaced so §4.6's
    /// <c>AddTailAsync</c> can persist it into <c>Providers.ProviderConfig</c>.
    /// </summary>
    internal string RootPath => _rootPath;

    internal DropboxProvider(
        string providerId,
        string displayName,
        DropboxClientBundle bundle,
        string rootPath,
        ILogger<DropboxProvider> logger)
    {
        ProviderID = providerId;
        DisplayName = displayName;
        _bundle = bundle;
        _rootPath = rootPath;
        _logger = logger;
    }

    // ── Upload session lifecycle ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<UploadSession>> BeginUploadAsync(
        string remoteName, long totalBytes, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(remoteName))
        {
            return Result<UploadSession>.Fail(
                ErrorCode.InvalidArgument, "remoteName must not be empty.");
        }
        if (totalBytes < 0)
        {
            return Result<UploadSession>.Fail(
                ErrorCode.InvalidArgument, "totalBytes must not be negative.");
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var destPath = BuildRemotePath(remoteName);

            // Open a session with an empty body; subsequent append_v2 calls supply the bytes.
            using var emptyBody = new MemoryStream(Array.Empty<byte>(), writable: false);
            var startResult = await _bundle.Client.Files
                .UploadSessionStartAsync(close: false, sessionType: null, contentHash: null, body: emptyBody)
                .ConfigureAwait(false);

            if (startResult is null || string.IsNullOrEmpty(startResult.SessionId))
            {
                _logger.LogError(
                    "Dropbox upload_session/start returned a null session id for {Provider}.", ProviderID);
                return Result<UploadSession>.Fail(
                    ErrorCode.ProviderApiChanged,
                    "Dropbox returned a null session id from upload_session/start.");
            }

            var encoded = DropboxSessionUri.Encode(startResult.SessionId, destPath);

            _logger.LogDebug(
                "Opened Dropbox upload session for {Remote} ({TotalBytes} bytes) for {Provider}.",
                destPath, totalBytes, ProviderID);

            return Result<UploadSession>.Ok(new UploadSession
            {
                SessionUri = encoded,
                ExpiresAt = DateTimeOffset.UtcNow + SessionTtl,
                BytesUploaded = 0,
                TotalBytes = totalBytes,
            });
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<UploadSession>(ex, ct, "BeginUploadAsync");
        }
        catch (ApiException<UploadSessionStartError> aex)
        {
            return MapUploadSessionStartError<UploadSession>(aex);
        }
        catch (AuthException aex)
        {
            _logger.LogError(aex, "Dropbox auth rejected during BeginUploadAsync.");
            return Result<UploadSession>.Fail(
                ErrorCode.TokenRefreshFailed, "Dropbox rejected the access token (refresh failed).", aex);
        }
        catch (RateLimitException rex)
        {
            return Result<UploadSession>.Fail(
                ErrorCode.ProviderRateLimited,
                $"Dropbox rate-limited BeginUploadAsync; retry after {rex.RetryAfter}s.", rex);
        }
        catch (RetryException rex)
        {
            // RateLimitException is caught earlier; any RetryException here is a transient
            // SDK-flagged failure (typically 5xx).
            return Result<UploadSession>.Fail(
                ErrorCode.ProviderUnreachable, "Dropbox transient failure (retry).", rex);
        }
        catch (BadInputException bex)
        {
            _logger.LogWarning(bex, "Dropbox rejected BeginUploadAsync request shape.");
            return Result<UploadSession>.Fail(
                ErrorCode.UploadFailed, "Dropbox rejected the BeginUploadAsync request as malformed.", bex);
        }
        catch (HttpException hex)
        {
            return MapHttpException<UploadSession>(hex, "BeginUploadAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result<UploadSession>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure initiating Dropbox upload.", ex);
        }
        catch (IOException ex)
        {
            return Result<UploadSession>.Fail(
                ErrorCode.ProviderUnreachable, "I/O failure initiating Dropbox upload.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error initiating Dropbox upload.");
            return Result<UploadSession>.Fail(
                ErrorCode.Unknown, "Unexpected error initiating Dropbox upload.", ex);
        }
    }

    /// <inheritdoc/>
    public Task<Result<long>> GetUploadedBytesAsync(UploadSession session, CancellationToken ct)
    {
        // Per Discrepancy 4: Dropbox exposes no "how much have you received?" endpoint. We trust
        // the locally-persisted offset. Drift surfaces on the next UploadRangeAsync via
        // IncorrectOffset → UploadSessionExpired → RangeUploader restarts from byte 0.
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(Result<long>.Fail(
                ErrorCode.Cancelled, "GetUploadedBytesAsync cancelled."));
        }
        return Task.FromResult(Result<long>.Ok(session.BytesUploaded));
    }

    /// <inheritdoc/>
    public async Task<Result> UploadRangeAsync(
        UploadSession session, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (offset < 0)
        {
            return Result.Fail(ErrorCode.InvalidArgument, "offset must not be negative.");
        }
        if (data.Length == 0)
        {
            return Result.Fail(ErrorCode.InvalidArgument, "data must not be empty.");
        }
        if (offset + data.Length > session.TotalBytes)
        {
            return Result.Fail(
                ErrorCode.InvalidArgument,
                $"Range [{offset}..{offset + data.Length - 1}] exceeds totalBytes={session.TotalBytes}.");
        }

        if (!DropboxSessionUri.TryDecode(session.SessionUri, out var decoded))
        {
            return Result.Fail(
                ErrorCode.UploadSessionExpired,
                "SessionUri envelope is malformed or unrecognised; restarting upload.");
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var cursor = new UploadSessionCursor(decoded.SessionId, (ulong)offset);

            // Dropbox.Api accepts Stream for the body. Wrap the ReadOnlyMemory in a non-resizable
            // MemoryStream over the underlying array (or a copy if the memory is not array-backed).
            using var body = ToReadOnlyStream(data);

            await _bundle.Client.Files
                .UploadSessionAppendV2Async(cursor, close: false, contentHash: null, body: body)
                .ConfigureAwait(false);

            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation(ex, ct, "UploadRangeAsync");
        }
        catch (ApiException<UploadSessionAppendError> aex)
        {
            return MapUploadSessionAppendError(aex);
        }
        catch (ApiException<UploadSessionLookupError> aex)
        {
            return MapUploadSessionLookupError(aex);
        }
        catch (AuthException aex)
        {
            _logger.LogError(aex, "Dropbox auth rejected during UploadRangeAsync.");
            return Result.Fail(
                ErrorCode.TokenRefreshFailed, "Dropbox rejected the access token (refresh failed).", aex);
        }
        catch (RateLimitException rex)
        {
            return Result.Fail(
                ErrorCode.ProviderRateLimited,
                $"Dropbox rate-limited UploadRangeAsync; retry after {rex.RetryAfter}s.", rex);
        }
        catch (RetryException rex)
        {
            return Result.Fail(
                ErrorCode.ProviderUnreachable, "Dropbox transient failure (retry).", rex);
        }
        catch (BadInputException bex)
        {
            _logger.LogWarning(bex, "Dropbox rejected UploadRangeAsync request shape.");
            return Result.Fail(
                ErrorCode.UploadFailed, "Dropbox rejected the UploadRangeAsync request as malformed.", bex);
        }
        catch (HttpException hex)
        {
            return MapHttpException(hex, "UploadRangeAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail(
                ErrorCode.ProviderUnreachable, "Network failure during Dropbox range upload.", ex);
        }
        catch (IOException ex)
        {
            return Result.Fail(
                ErrorCode.ProviderUnreachable, "I/O failure during Dropbox range upload.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Dropbox range upload.");
            return Result.Fail(
                ErrorCode.Unknown, "Unexpected error during Dropbox range upload.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<string>> FinaliseUploadAsync(UploadSession session, CancellationToken ct)
    {
        if (!DropboxSessionUri.TryDecode(session.SessionUri, out var decoded))
        {
            return Result<string>.Fail(
                ErrorCode.UploadSessionExpired,
                "SessionUri envelope is malformed or unrecognised; restarting upload.");
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var cursor = new UploadSessionCursor(decoded.SessionId, (ulong)session.TotalBytes);
            var commit = new CommitInfo(
                path: decoded.RemotePath,
                mode: WriteMode.Overwrite.Instance,
                autorename: false,
                clientModified: null,
                mute: true,
                propertyGroups: null,
                strictConflict: false);

            using var emptyBody = new MemoryStream(Array.Empty<byte>(), writable: false);
            var metadata = await _bundle.Client.Files
                .UploadSessionFinishAsync(cursor, commit, contentHash: null, body: emptyBody)
                .ConfigureAwait(false);

            if (metadata is null || string.IsNullOrEmpty(metadata.Id))
            {
                _logger.LogError(
                    "Dropbox upload_session/finish returned null metadata for {Provider}.", ProviderID);
                return Result<string>.Fail(
                    ErrorCode.ProviderApiChanged,
                    "Dropbox returned null metadata from upload_session/finish.");
            }

            _logger.LogInformation(
                "Dropbox accepted blob for {ProviderId}; id={FileId}, size={Size}, contentHash={ContentHash}.",
                ProviderID, metadata.Id, metadata.Size, metadata.ContentHash ?? "<absent>");

            return Result<string>.Ok(metadata.Id);
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<string>(ex, ct, "FinaliseUploadAsync");
        }
        catch (ApiException<UploadSessionFinishError> aex)
        {
            return MapUploadSessionFinishError<string>(aex);
        }
        catch (ApiException<UploadSessionLookupError> aex)
        {
            return MapUploadSessionLookupErrorTyped<string>(aex);
        }
        catch (AuthException aex)
        {
            _logger.LogError(aex, "Dropbox auth rejected during FinaliseUploadAsync.");
            return Result<string>.Fail(
                ErrorCode.TokenRefreshFailed, "Dropbox rejected the access token (refresh failed).", aex);
        }
        catch (RateLimitException rex)
        {
            return Result<string>.Fail(
                ErrorCode.ProviderRateLimited,
                $"Dropbox rate-limited FinaliseUploadAsync; retry after {rex.RetryAfter}s.", rex);
        }
        catch (RetryException rex)
        {
            return Result<string>.Fail(
                ErrorCode.ProviderUnreachable, "Dropbox transient failure (retry).", rex);
        }
        catch (BadInputException bex)
        {
            return Result<string>.Fail(
                ErrorCode.UploadFailed, "Dropbox rejected the FinaliseUploadAsync request as malformed.", bex);
        }
        catch (HttpException hex)
        {
            return MapHttpException<string>(hex, "FinaliseUploadAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result<string>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure finalising Dropbox upload.", ex);
        }
        catch (IOException ex)
        {
            return Result<string>.Fail(
                ErrorCode.ProviderUnreachable, "I/O failure finalising Dropbox upload.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error finalising Dropbox upload.");
            return Result<string>.Fail(
                ErrorCode.Unknown, "Unexpected error finalising Dropbox upload.", ex);
        }
    }

    /// <inheritdoc/>
    public Task<Result> AbortUploadAsync(UploadSession session, CancellationToken ct)
    {
        // Discrepancy 3: Dropbox has no abort endpoint. Sessions expire naturally after 48h.
        // Observe ct at entry per Principle 13, then no-op.
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(Result.Fail(ErrorCode.Cancelled, "AbortUploadAsync cancelled."));
        }

        if (DropboxSessionUri.TryDecode(session.SessionUri, out var decoded))
        {
            _logger.LogDebug(
                "Dropbox AbortUpload requested for session {SessionId} ({Path}); no-op (Dropbox has no abort endpoint).",
                decoded.SessionId, decoded.RemotePath);
        }
        else
        {
            _logger.LogDebug("Dropbox AbortUpload requested for malformed session envelope; no-op.");
        }

        return Task.FromResult(Result.Ok());
    }

    // ── Download / existence / delete ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<Stream>> DownloadAsync(string remoteId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(remoteId))
        {
            return Result<Stream>.Fail(ErrorCode.InvalidArgument, "remoteId must not be empty.");
        }

        IDownloadResponse<FileMetadata>? response = null;
        try
        {
            ct.ThrowIfCancellationRequested();

            // Dropbox's `path` parameter accepts both human paths and `id:abc` opaque IDs.
            response = await _bundle.Client.Files
                .DownloadAsync(path: remoteId, rev: null)
                .ConfigureAwait(false);

            var inner = await response.GetContentAsStreamAsync().ConfigureAwait(false);
            var wrapped = new DownloadStreamWrapper(inner, response);
            response = null; // ownership transferred to the wrapper
            return Result<Stream>.Ok(wrapped);
        }
        catch (OperationCanceledException ex)
        {
            response?.Dispose();
            return MapCancellation<Stream>(ex, ct, "DownloadAsync");
        }
        catch (ApiException<DownloadError> aex) when (aex.ErrorResponse?.IsPath == true)
        {
            response?.Dispose();
            return MapLookupErrorAsNotFound<Stream>(aex, aex.ErrorResponse!.AsPath!.Value, "download");
        }
        catch (ApiException<DownloadError> aex)
        {
            response?.Dispose();
            return Result<Stream>.Fail(
                ErrorCode.UploadFailed, $"Dropbox download failed: {aex.ErrorResponse}", aex);
        }
        catch (AuthException aex)
        {
            response?.Dispose();
            return Result<Stream>.Fail(
                ErrorCode.TokenRefreshFailed, "Dropbox rejected the access token (refresh failed).", aex);
        }
        catch (RateLimitException rex)
        {
            response?.Dispose();
            return Result<Stream>.Fail(
                ErrorCode.ProviderRateLimited,
                $"Dropbox rate-limited DownloadAsync; retry after {rex.RetryAfter}s.", rex);
        }
        catch (RetryException rex)
        {
            response?.Dispose();
            return Result<Stream>.Fail(
                ErrorCode.ProviderUnreachable, "Dropbox transient failure (retry).", rex);
        }
        catch (BadInputException bex)
        {
            response?.Dispose();
            return Result<Stream>.Fail(
                ErrorCode.UploadFailed, "Dropbox rejected the DownloadAsync request as malformed.", bex);
        }
        catch (HttpException hex)
        {
            response?.Dispose();
            return MapHttpException<Stream>(hex, "DownloadAsync");
        }
        catch (HttpRequestException ex)
        {
            response?.Dispose();
            return Result<Stream>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure downloading from Dropbox.", ex);
        }
        catch (IOException ex)
        {
            response?.Dispose();
            return Result<Stream>.Fail(
                ErrorCode.ProviderUnreachable, "I/O failure downloading from Dropbox.", ex);
        }
        catch (Exception ex)
        {
            response?.Dispose();
            _logger.LogError(ex, "Unexpected error downloading {RemoteId} from Dropbox.", remoteId);
            return Result<Stream>.Fail(
                ErrorCode.Unknown, "Unexpected error downloading from Dropbox.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result> DeleteAsync(string remoteId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(remoteId))
        {
            return Result.Fail(ErrorCode.InvalidArgument, "remoteId must not be empty.");
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            await _bundle.Client.Files.DeleteV2Async(path: remoteId, parentRev: null).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation(ex, ct, "DeleteAsync");
        }
        catch (ApiException<DeleteError> aex) when (aex.ErrorResponse?.IsPathLookup == true)
        {
            var lookup = aex.ErrorResponse!.AsPathLookup!.Value;
            if (lookup.IsNotFound)
            {
                // Idempotent: missing object → success.
                return Result.Ok();
            }
            return Result.Fail(
                ErrorCode.UploadFailed, $"Dropbox delete lookup failed: {lookup}", aex);
        }
        catch (ApiException<DeleteError> aex)
        {
            return Result.Fail(
                ErrorCode.UploadFailed, $"Dropbox delete failed: {aex.ErrorResponse}", aex);
        }
        catch (AuthException aex)
        {
            return Result.Fail(
                ErrorCode.TokenRefreshFailed, "Dropbox rejected the access token (refresh failed).", aex);
        }
        catch (RateLimitException rex)
        {
            return Result.Fail(
                ErrorCode.ProviderRateLimited,
                $"Dropbox rate-limited DeleteAsync; retry after {rex.RetryAfter}s.", rex);
        }
        catch (RetryException rex)
        {
            return Result.Fail(
                ErrorCode.ProviderUnreachable, "Dropbox transient failure (retry).", rex);
        }
        catch (BadInputException bex)
        {
            return Result.Fail(
                ErrorCode.UploadFailed, "Dropbox rejected the DeleteAsync request as malformed.", bex);
        }
        catch (HttpException hex)
        {
            return MapHttpException(hex, "DeleteAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail(
                ErrorCode.ProviderUnreachable, "Network failure deleting from Dropbox.", ex);
        }
        catch (IOException ex)
        {
            return Result.Fail(
                ErrorCode.ProviderUnreachable, "I/O failure deleting from Dropbox.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting {RemoteId} from Dropbox.", remoteId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error during Dropbox delete.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<bool>> ExistsAsync(string remoteId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(remoteId))
        {
            return Result<bool>.Fail(ErrorCode.InvalidArgument, "remoteId must not be empty.");
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var meta = await _bundle.Client.Files
                .GetMetadataAsync(
                    path: remoteId,
                    includeMediaInfo: false,
                    includeDeleted: false,
                    includeHasExplicitSharedMembers: false,
                    includePropertyGroups: null)
                .ConfigureAwait(false);

            // Deleted entries shouldn't surface with includeDeleted=false, but defend anyway.
            if (meta is null || meta.IsDeleted)
            {
                return Result<bool>.Ok(false);
            }
            return Result<bool>.Ok(true);
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<bool>(ex, ct, "ExistsAsync");
        }
        catch (ApiException<GetMetadataError> aex) when (aex.ErrorResponse?.IsPath == true)
        {
            var lookup = aex.ErrorResponse!.AsPath!.Value;
            if (lookup.IsNotFound)
            {
                return Result<bool>.Ok(false);
            }
            return Result<bool>.Fail(
                ErrorCode.UploadFailed, $"Dropbox metadata lookup failed: {lookup}", aex);
        }
        catch (AuthException aex)
        {
            return Result<bool>.Fail(
                ErrorCode.TokenRefreshFailed, "Dropbox rejected the access token (refresh failed).", aex);
        }
        catch (RateLimitException rex)
        {
            return Result<bool>.Fail(
                ErrorCode.ProviderRateLimited, "Dropbox rate-limited ExistsAsync.", rex);
        }
        catch (RetryException rex)
        {
            return Result<bool>.Fail(
                ErrorCode.ProviderUnreachable, "Dropbox transient failure (retry).", rex);
        }
        catch (BadInputException bex)
        {
            return Result<bool>.Fail(
                ErrorCode.UploadFailed, "Dropbox rejected the ExistsAsync request as malformed.", bex);
        }
        catch (HttpException hex)
        {
            return MapHttpException<bool>(hex, "ExistsAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result<bool>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure checking existence on Dropbox.", ex);
        }
        catch (IOException ex)
        {
            return Result<bool>.Fail(
                ErrorCode.ProviderUnreachable, "I/O failure checking existence on Dropbox.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error checking existence of {RemoteId} on Dropbox.", remoteId);
            return Result<bool>.Fail(
                ErrorCode.Unknown, "Unexpected error during Dropbox existence check.", ex);
        }
    }

    // ── Listing ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<IReadOnlyList<string>>> ListAsync(string prefix, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // We always list the root recursively and post-filter on the requested prefix. Dropbox's
            // ListFolder isn't prefix-scoped; recursive listing under the root is the simplest correct
            // approach for V1's expected blob counts.
            var listingPath = _rootPath.TrimEnd('/');
            var filterPrefix = NormalisePrefixForFilter(prefix);

            var results = new List<string>();
            var page = await _bundle.Client.Files
                .ListFolderAsync(
                    path: listingPath,
                    recursive: true,
                    includeMediaInfo: false,
                    includeDeleted: false,
                    includeHasExplicitSharedMembers: false,
                    includeMountedFolders: false,
                    limit: null,
                    sharedLink: null,
                    includePropertyGroups: null,
                    includeNonDownloadableFiles: false)
                .ConfigureAwait(false);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                CollectFiles(page.Entries, filterPrefix, _rootPath, results);
                if (!page.HasMore)
                {
                    break;
                }
                page = await _bundle.Client.Files
                    .ListFolderContinueAsync(page.Cursor)
                    .ConfigureAwait(false);
            }

            return Result<IReadOnlyList<string>>.Ok(results);
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<IReadOnlyList<string>>(ex, ct, "ListAsync");
        }
        catch (ApiException<ListFolderError> aex) when (aex.ErrorResponse?.IsPath == true)
        {
            var lookup = aex.ErrorResponse!.AsPath!.Value;
            if (lookup.IsNotFound)
            {
                // No root folder yet → no entries.
                return Result<IReadOnlyList<string>>.Ok(Array.Empty<string>());
            }
            return Result<IReadOnlyList<string>>.Fail(
                ErrorCode.UploadFailed, $"Dropbox list lookup failed: {lookup}", aex);
        }
        catch (ApiException<ListFolderContinueError> aex)
        {
            return Result<IReadOnlyList<string>>.Fail(
                ErrorCode.UploadFailed, $"Dropbox list_continue failed: {aex.ErrorResponse}", aex);
        }
        catch (AuthException aex)
        {
            return Result<IReadOnlyList<string>>.Fail(
                ErrorCode.TokenRefreshFailed, "Dropbox rejected the access token (refresh failed).", aex);
        }
        catch (RateLimitException rex)
        {
            return Result<IReadOnlyList<string>>.Fail(
                ErrorCode.ProviderRateLimited, "Dropbox rate-limited ListAsync.", rex);
        }
        catch (RetryException rex)
        {
            return Result<IReadOnlyList<string>>.Fail(
                ErrorCode.ProviderUnreachable, "Dropbox transient failure (retry).", rex);
        }
        catch (BadInputException bex)
        {
            return Result<IReadOnlyList<string>>.Fail(
                ErrorCode.UploadFailed, "Dropbox rejected the ListAsync request as malformed.", bex);
        }
        catch (HttpException hex)
        {
            return MapHttpException<IReadOnlyList<string>>(hex, "ListAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result<IReadOnlyList<string>>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure listing Dropbox.", ex);
        }
        catch (IOException ex)
        {
            return Result<IReadOnlyList<string>>.Fail(
                ErrorCode.ProviderUnreachable, "I/O failure listing Dropbox.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing Dropbox prefix '{Prefix}'.", prefix);
            return Result<IReadOnlyList<string>>.Fail(
                ErrorCode.Unknown, "Unexpected error listing Dropbox.", ex);
        }
    }

    // ── Health and capacity ───────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ProviderHealth>> CheckHealthAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            await _bundle.Client.Users.GetCurrentAccountAsync().ConfigureAwait(false);
            sw.Stop();
            return Result<ProviderHealth>.Ok(new ProviderHealth
            {
                Status = ProviderHealthStatus.Healthy,
                CheckedAt = DateTimeOffset.UtcNow,
                RoundTripLatency = sw.Elapsed,
            });
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<ProviderHealth>(ex, ct, "CheckHealthAsync");
        }
        catch (AuthException aex)
        {
            sw.Stop();
            return Result<ProviderHealth>.Ok(new ProviderHealth
            {
                Status = ProviderHealthStatus.AuthFailed,
                CheckedAt = DateTimeOffset.UtcNow,
                RoundTripLatency = sw.Elapsed,
                Detail = aex.Message,
            });
        }
        catch (RateLimitException rex)
        {
            sw.Stop();
            return Result<ProviderHealth>.Ok(new ProviderHealth
            {
                Status = ProviderHealthStatus.Degraded,
                CheckedAt = DateTimeOffset.UtcNow,
                RoundTripLatency = sw.Elapsed,
                Detail = $"Rate-limited; retry after {rex.RetryAfter}s.",
            });
        }
        catch (HttpException hex)
        {
            sw.Stop();
            return Result<ProviderHealth>.Ok(new ProviderHealth
            {
                Status = ProviderHealthStatus.Unreachable,
                CheckedAt = DateTimeOffset.UtcNow,
                RoundTripLatency = sw.Elapsed,
                Detail = $"Dropbox returned HTTP {hex.StatusCode}.",
            });
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return Result<ProviderHealth>.Ok(new ProviderHealth
            {
                Status = ProviderHealthStatus.Unreachable,
                CheckedAt = DateTimeOffset.UtcNow,
                RoundTripLatency = sw.Elapsed,
                Detail = ex.Message,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Dropbox health probe.");
            return Result<ProviderHealth>.Fail(
                ErrorCode.Unknown, "Unexpected error during Dropbox health probe.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<long>> GetUsedBytesAsync(CancellationToken ct)
    {
        var usage = await TryGetSpaceUsageAsync(ct).ConfigureAwait(false);
        if (!usage.Success)
        {
            return Result<long>.Fail(usage.Error);
        }
        return Result<long>.Ok(SaturateToLong(usage.Value.Used));
    }

    /// <inheritdoc/>
    public async Task<Result<long?>> GetQuotaBytesAsync(CancellationToken ct)
    {
        var usage = await TryGetSpaceUsageAsync(ct).ConfigureAwait(false);
        if (!usage.Success)
        {
            return Result<long?>.Fail(usage.Error);
        }

        var allocation = usage.Value.Allocation;
        if (allocation is null)
        {
            return Result<long?>.Ok(null);
        }
        if (allocation.IsIndividual)
        {
            return Result<long?>.Ok(SaturateToLong(allocation.AsIndividual.Value.Allocated));
        }
        if (allocation.IsTeam)
        {
            return Result<long?>.Ok(SaturateToLong(allocation.AsTeam.Value.Allocated));
        }
        // Unknown allocation variant → quota is "unknown / unlimited" per the contract.
        return Result<long?>.Ok(null);
    }

    /// <summary>
    /// Saturating <see cref="ulong"/> → <see cref="long"/> conversion. The Dropbox API exposes
    /// byte counts as <see cref="ulong"/> but our contract is <see cref="long"/>; for the
    /// astronomically-unlikely case where a value exceeds <see cref="long.MaxValue"/> (9.2 EB),
    /// saturate rather than throw <see cref="OverflowException"/> across the
    /// <see cref="IStorageProvider"/> public boundary (Principle 1).
    /// </summary>
    private static long SaturateToLong(ulong value)
        => value > long.MaxValue ? long.MaxValue : (long)value;

    private async Task<Result<SpaceUsage>> TryGetSpaceUsageAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var usage = await _bundle.Client.Users.GetSpaceUsageAsync().ConfigureAwait(false);
            return Result<SpaceUsage>.Ok(usage);
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<SpaceUsage>(ex, ct, "GetSpaceUsage");
        }
        catch (AuthException aex)
        {
            return Result<SpaceUsage>.Fail(
                ErrorCode.TokenRefreshFailed, "Dropbox rejected the access token (refresh failed).", aex);
        }
        catch (RateLimitException rex)
        {
            return Result<SpaceUsage>.Fail(
                ErrorCode.ProviderRateLimited, "Dropbox rate-limited GetSpaceUsage.", rex);
        }
        catch (RetryException rex)
        {
            return Result<SpaceUsage>.Fail(
                ErrorCode.ProviderUnreachable, "Dropbox transient failure (retry).", rex);
        }
        catch (HttpException hex)
        {
            return MapHttpException<SpaceUsage>(hex, "GetSpaceUsage");
        }
        catch (HttpRequestException ex)
        {
            return Result<SpaceUsage>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure querying Dropbox space usage.", ex);
        }
        catch (IOException ex)
        {
            return Result<SpaceUsage>.Fail(
                ErrorCode.ProviderUnreachable, "I/O failure querying Dropbox space usage.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error querying Dropbox space usage.");
            return Result<SpaceUsage>.Fail(
                ErrorCode.Unknown, "Unexpected error querying Dropbox space usage.", ex);
        }
    }

    // ── Disposal ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }
        try { _bundle.Dispose(); } catch { /* swallow */ }
        return ValueTask.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the absolute Dropbox path from the relative <paramref name="remoteName"/> under the
    /// configured root. Dropbox supports subfolders and creates intermediate path components
    /// implicitly on upload — no need to flatten path separators like the Drive provider does.
    /// </summary>
    private string BuildRemotePath(string remoteName)
    {
        var root = _rootPath.TrimEnd('/');
        var leaf = remoteName.StartsWith('/') ? remoteName : "/" + remoteName;
        return root + leaf;
    }

    /// <summary>Normalises the user-supplied list prefix to a leading-slash, root-relative form.</summary>
    private static string NormalisePrefixForFilter(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return string.Empty;
        }
        return prefix.StartsWith('/') ? prefix : "/" + prefix;
    }

    /// <summary>
    /// Append <c>FileMetadata.Id</c>s from <paramref name="entries"/> whose root-relative path
    /// starts with <paramref name="filterPrefix"/> into <paramref name="acc"/>. Folder and deleted
    /// entries are skipped.
    /// </summary>
    private static void CollectFiles(
        IList<DropboxMetadata> entries, string filterPrefix, string rootPath, List<string> acc)
    {
        var root = rootPath.TrimEnd('/');
        foreach (var entry in entries)
        {
            if (!entry.IsFile)
            {
                continue;
            }
            var file = entry.AsFile;
            if (string.IsNullOrEmpty(file.Id))
            {
                continue;
            }

            if (filterPrefix.Length > 0)
            {
                var pathDisplay = file.PathDisplay ?? file.PathLower ?? string.Empty;
                if (string.IsNullOrEmpty(pathDisplay))
                {
                    continue;
                }
                // pathDisplay starts with the absolute Dropbox path; trim root prefix to compare
                // against the requested relative prefix.
                if (!pathDisplay.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var rel = pathDisplay[root.Length..];
                if (!rel.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            acc.Add(file.Id);
        }
    }

    /// <summary>Wraps a <see cref="ReadOnlyMemory{T}"/> in a non-resizable read-only <see cref="Stream"/>.</summary>
    private static Stream ToReadOnlyStream(ReadOnlyMemory<byte> data)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out var seg) &&
            seg.Array is not null)
        {
            return new MemoryStream(seg.Array, seg.Offset, seg.Count, writable: false, publiclyVisible: false);
        }
        // Fallback: copy. Range uploads are <= 4 MiB so the copy cost is bounded.
        var copy = data.ToArray();
        return new MemoryStream(copy, writable: false);
    }

    private Result<T> MapCancellation<T>(OperationCanceledException ex, CancellationToken ct, string op)
    {
        if (ct.IsCancellationRequested)
        {
            return Result<T>.Fail(ErrorCode.Cancelled, $"{op} cancelled.", ex);
        }
        // OCE not from our token → likely HttpClient timeout.
        return Result<T>.Fail(
            ErrorCode.ProviderUnreachable, $"{op} timed out at the HTTP layer.", ex);
    }

    private Result MapCancellation(OperationCanceledException ex, CancellationToken ct, string op)
    {
        if (ct.IsCancellationRequested)
        {
            return Result.Fail(ErrorCode.Cancelled, $"{op} cancelled.", ex);
        }
        return Result.Fail(ErrorCode.ProviderUnreachable, $"{op} timed out at the HTTP layer.", ex);
    }

    private Result<T> MapHttpException<T>(HttpException hex, string op)
    {
        var status = hex.StatusCode;
        if (status >= 500 || status == 408)
        {
            _logger.LogWarning("Dropbox {Op} returned HTTP {Status}.", op, status);
            return Result<T>.Fail(
                ErrorCode.ProviderUnreachable, $"Dropbox returned HTTP {status} during {op}.", hex);
        }
        if (status == 401)
        {
            return Result<T>.Fail(
                ErrorCode.TokenRefreshFailed, $"Dropbox returned 401 during {op}.", hex);
        }
        if (status == 429)
        {
            return Result<T>.Fail(
                ErrorCode.ProviderRateLimited, $"Dropbox returned 429 during {op}.", hex);
        }
        _logger.LogWarning("Dropbox {Op} returned HTTP {Status}.", op, status);
        return Result<T>.Fail(
            ErrorCode.UploadFailed, $"Dropbox returned HTTP {status} during {op}.", hex);
    }

    private Result MapHttpException(HttpException hex, string op)
    {
        var typed = MapHttpException<object>(hex, op);
        // Generic helper only ever produces failed Results; Error is non-null by construction (principle 37).
        return Result.Fail(typed.Error!);
    }

    private Result<T> MapUploadSessionStartError<T>(ApiException<UploadSessionStartError> aex)
    {
        var err = aex.ErrorResponse;
        if (err is null)
        {
            return Result<T>.Fail(ErrorCode.UploadFailed, "Dropbox upload_session/start failed.", aex);
        }
        if (err.IsPayloadTooLarge)
        {
            return Result<T>.Fail(
                ErrorCode.UploadFailed,
                "Dropbox rejected upload_session/start: payload too large.", aex);
        }
        if (err.IsContentHashMismatch)
        {
            return Result<T>.Fail(
                ErrorCode.UploadFailed,
                "Dropbox rejected upload_session/start: content hash mismatch.", aex);
        }
        return Result<T>.Fail(
            ErrorCode.UploadFailed, $"Dropbox upload_session/start failed: {err}", aex);
    }

    private static Result MapUploadSessionAppendError(ApiException<UploadSessionAppendError> aex)
    {
        var err = aex.ErrorResponse;
        if (err is null)
        {
            return Result.Fail(ErrorCode.UploadFailed, "Dropbox upload_session/append_v2 failed.", aex);
        }
        if (err.IsIncorrectOffset ||
            err.IsNotFound ||
            err.IsClosed ||
            err.IsConcurrentSessionInvalidOffset ||
            err.IsConcurrentSessionInvalidDataSize)
        {
            return Result.Fail(
                ErrorCode.UploadSessionExpired,
                $"Dropbox session no longer usable for append: {err}; restart.",
                aex);
        }
        if (err.IsContentHashMismatch)
        {
            return Result.Fail(
                ErrorCode.UploadFailed,
                "Dropbox content hash mismatch on append (bytes corrupted in transit).", aex);
        }
        if (err.IsTooLarge || err.IsPayloadTooLarge)
        {
            return Result.Fail(
                ErrorCode.UploadFailed,
                "Dropbox rejected append: range exceeds provider limit.", aex);
        }
        // NotClosed / Other / unknown → fail.
        return Result.Fail(
            ErrorCode.UploadFailed, $"Dropbox upload_session/append_v2 failed: {err}", aex);
    }

    private static Result MapUploadSessionLookupError(ApiException<UploadSessionLookupError> aex)
    {
        var err = aex.ErrorResponse;
        if (err is null)
        {
            return Result.Fail(ErrorCode.UploadFailed, "Dropbox session lookup failed.", aex);
        }
        if (err.IsNotFound ||
            err.IsIncorrectOffset ||
            err.IsClosed ||
            err.IsConcurrentSessionInvalidOffset ||
            err.IsConcurrentSessionInvalidDataSize)
        {
            return Result.Fail(
                ErrorCode.UploadSessionExpired,
                $"Dropbox session no longer valid: {err}; restart.", aex);
        }
        return Result.Fail(
            ErrorCode.UploadFailed, $"Dropbox session lookup failed: {err}", aex);
    }

    private static Result<T> MapUploadSessionLookupErrorTyped<T>(ApiException<UploadSessionLookupError> aex)
    {
        var err = aex.ErrorResponse;
        if (err is null)
        {
            return Result<T>.Fail(ErrorCode.UploadFailed, "Dropbox session lookup failed.", aex);
        }
        // Keep this list in lock-step with MapUploadSessionLookupError (non-typed). PR review #1.
        if (err.IsNotFound ||
            err.IsIncorrectOffset ||
            err.IsClosed ||
            err.IsConcurrentSessionInvalidOffset ||
            err.IsConcurrentSessionInvalidDataSize)
        {
            return Result<T>.Fail(
                ErrorCode.UploadSessionExpired,
                $"Dropbox session no longer valid: {err}; restart.", aex);
        }
        return Result<T>.Fail(
            ErrorCode.UploadFailed, $"Dropbox session lookup failed: {err}", aex);
    }

    private Result<T> MapUploadSessionFinishError<T>(ApiException<UploadSessionFinishError> aex)
    {
        var err = aex.ErrorResponse;
        if (err is null)
        {
            return Result<T>.Fail(ErrorCode.UploadFailed, "Dropbox upload_session/finish failed.", aex);
        }
        if (err.IsLookupFailed)
        {
            var lookup = err.AsLookupFailed.Value;
            if (lookup.IsNotFound || lookup.IsClosed)
            {
                return Result<T>.Fail(
                    ErrorCode.UploadSessionExpired,
                    $"Dropbox session no longer valid on finish: {lookup}; restart.", aex);
            }
            return Result<T>.Fail(
                ErrorCode.UploadFailed,
                $"Dropbox upload_session/finish lookup failed: {lookup}", aex);
        }
        if (err.IsContentHashMismatch)
        {
            return Result<T>.Fail(
                ErrorCode.UploadFailed,
                "Dropbox content hash mismatch on finish (bytes corrupted in transit).", aex);
        }
        if (err.IsPayloadTooLarge)
        {
            return Result<T>.Fail(
                ErrorCode.UploadFailed, "Dropbox rejected finish: payload too large.", aex);
        }
        return Result<T>.Fail(
            ErrorCode.UploadFailed, $"Dropbox upload_session/finish failed: {err}", aex);
    }

    private static Result<T> MapLookupErrorAsNotFound<T>(Exception inner, LookupError lookup, string op)
    {
        if (lookup.IsNotFound)
        {
            return Result<T>.Fail(
                ErrorCode.BlobNotFound, $"Dropbox object not found during {op}.", inner);
        }
        return Result<T>.Fail(
            ErrorCode.UploadFailed, $"Dropbox {op} lookup failed: {lookup}", inner);
    }
}

/// <summary>
/// Encodes <c>(sessionId, remotePath)</c> into the single <see cref="UploadSession.SessionUri"/>
/// field (Discrepancy 7 of <c>.claude/plans/pr-4.4.md</c>). The persistent envelope is JSON of
/// shape <c>{"sid":"...","path":"..."}</c>; the encoded string is what <c>UploadSessions.SessionUri</c>
/// stores.
/// </summary>
internal static class DropboxSessionUri
{
    /// <summary>Encodes the pair. Both values must be non-null and non-empty.</summary>
    public static string Encode(string sessionId, string remotePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(remotePath);
        var env = new DropboxSessionEnvelope { Sid = sessionId, Path = remotePath };
        return JsonSerializer.Serialize(env, DropboxSessionUriJsonContext.Default.DropboxSessionEnvelope);
    }

    /// <summary>
    /// Robust decode. NEVER throws. Returns <see langword="false"/> for null/empty/whitespace,
    /// malformed JSON, missing-required-fields, empty-after-parse, or any unexpected exception
    /// during parse. The caller maps a <see langword="false"/> outcome to
    /// <see cref="ErrorCode.UploadSessionExpired"/> so the upload restarts cleanly.
    /// </summary>
    public static bool TryDecode(string? raw, out (string SessionId, string RemotePath) decoded)
    {
        decoded = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        try
        {
            var env = JsonSerializer.Deserialize(raw, DropboxSessionUriJsonContext.Default.DropboxSessionEnvelope);
            if (env is null ||
                string.IsNullOrWhiteSpace(env.Sid) ||
                string.IsNullOrWhiteSpace(env.Path))
            {
                return false;
            }
            decoded = (env.Sid, env.Path);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (Exception)
        {
            // Defensive: anything else (e.g. malformed UTF-8 bytes surfaced as some other exception)
            // is still a "restart the upload" outcome, not a thrown failure.
            return false;
        }
    }

    internal sealed record DropboxSessionEnvelope
    {
        [JsonPropertyName("sid")] public string? Sid { get; init; }
        [JsonPropertyName("path")] public string? Path { get; init; }
    }
}

[JsonSerializable(typeof(DropboxSessionUri.DropboxSessionEnvelope))]
internal sealed partial class DropboxSessionUriJsonContext : JsonSerializerContext;

/// <summary>
/// <see cref="Stream"/> wrapper that disposes the underlying SDK download response (and its
/// underlying HTTP response) when the stream is closed. Lets
/// <see cref="DropboxProvider.DownloadAsync"/> return a stream directly without leaking the
/// response.
/// </summary>
internal sealed class DownloadStreamWrapper : Stream
{
    private readonly Stream _inner;
    private readonly IDownloadResponse<FileMetadata> _response;
    private int _disposed;

    public DownloadStreamWrapper(Stream inner, IDownloadResponse<FileMetadata> response)
    {
        _inner = inner;
        _response = response;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _inner.Dispose(); } catch { /* swallow */ }
            try { _response.Dispose(); } catch { /* swallow */ }
        }
        base.Dispose(disposing);
    }
}
