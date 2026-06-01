using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace FlashSkink.Core.Providers.OneDrive;

/// <summary>
/// Production <see cref="IStorageProvider"/> over Microsoft Graph's resumable upload-session
/// protocol for OneDrive. Drives <c>createUploadSession</c> → range <c>PUT</c>s → final-chunk
/// <c>DriveItem</c> through raw HTTP (no Graph SDK, no MSAL) so the provider carries no heavyweight
/// dependency and stays allocation-conscious on the upload hot path.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Session model.</strong> Graph returns a pre-signed <c>uploadUrl</c> that needs no
/// authorization header for the range PUTs. The URL plus the destination object name is encoded
/// into the single <see cref="UploadSession.SessionUri"/> field via <see cref="OneDriveSessionUri"/>.
/// Unlike a separate "finish" call, OneDrive returns the committed <c>DriveItem</c> on the final
/// range PUT; that id is cached and surfaced by <see cref="FinaliseUploadAsync"/>.
/// </para>
/// <para>
/// <strong>Verification.</strong> Does NOT implement a remote-hash-check capability. OneDrive's
/// QuickXorHash is implemented in <see cref="OneDriveQuickXorHash"/> for future use but is not wired
/// here; the AES-GCM authentication tag on the encrypted blob is the cryptographic authenticator.
/// </para>
/// </remarks>
internal sealed partial class OneDriveProvider : IStorageProvider, IAsyncDisposable
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";

    /// <summary>Graph upload sessions live ~7 days; mirrored into <see cref="UploadSession.ExpiresAt"/> as a fallback.</summary>
    private static readonly TimeSpan ResumableSessionTtl = TimeSpan.FromDays(7);

    private readonly OneDriveClientBundle _bundle;
    private readonly string _rootPath;
    private readonly ILogger<OneDriveProvider> _logger;
    private readonly ConcurrentDictionary<string, EarlyFinalisation> _earlyFinalised = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>Final DriveItem facts captured from the last range PUT (OneDrive commits there, not via a finish call).</summary>
    private readonly record struct EarlyFinalisation(string RemoteId, long Size, string? QuickXorHash);

    /// <inheritdoc/>
    public string ProviderID { get; }

    /// <inheritdoc/>
    public string ProviderType => "onedrive";

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <summary>Drive-relative root path resolved at setup time (e.g. <c>"/FlashSkink Backup"</c>).</summary>
    internal string RootPath => _rootPath;

    internal OneDriveProvider(
        string providerId,
        string displayName,
        OneDriveClientBundle bundle,
        string rootPath,
        ILogger<OneDriveProvider> logger)
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
            return Result<UploadSession>.Fail(ErrorCode.InvalidArgument, "remoteName must not be empty.");
        }
        if (totalBytes < 0)
        {
            return Result<UploadSession>.Fail(ErrorCode.InvalidArgument, "totalBytes must not be negative.");
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = BuildRelativePath(remoteName);
            var leafName = LeafName(relativePath);
            var url = $"{GraphRoot}/me/drive/root:/{EscapePath(relativePath)}:/createUploadSession";

            var requestBody = new CreateUploadSessionRequest
            {
                Item = new UploadSessionItem { Name = leafName },
            };
            using var content = new StringContent(
                JsonSerializer.Serialize(requestBody, OneDriveJsonContext.Default.CreateUploadSessionRequest),
                Encoding.UTF8,
                "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            using HttpResponseMessage response = await _bundle.AuthedClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await MapHttpFailureAsync<UploadSession>(response, "open the upload", ct).ConfigureAwait(false);
            }

            var payload = await response.Content
                .ReadFromJsonOrDefaultAsync(OneDriveJsonContext.Default.CreateUploadSessionResponse, ct)
                .ConfigureAwait(false);

            if (payload is null || string.IsNullOrEmpty(payload.UploadUrl))
            {
                _logger.LogError("OneDrive createUploadSession returned no upload URL for {Provider}.", ProviderID);
                return Result<UploadSession>.Fail(
                    ErrorCode.ProviderApiChanged, "The tail did not return an upload location.");
            }

            var expiresAt = payload.ExpirationDateTime ?? (DateTimeOffset.UtcNow + ResumableSessionTtl);
            var encoded = OneDriveSessionUri.Encode(payload.UploadUrl, remoteName);

            _logger.LogDebug(
                "Opened OneDrive upload session for {Remote} ({TotalBytes} bytes) for {Provider}.",
                relativePath, totalBytes, ProviderID);

            return Result<UploadSession>.Ok(new UploadSession
            {
                SessionUri = encoded,
                ExpiresAt = expiresAt,
                BytesUploaded = 0,
                TotalBytes = totalBytes,
            });
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<UploadSession>(ex, ct, "BeginUploadAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result<UploadSession>.Fail(
                ErrorCode.ProviderUnreachable, "Could not reach the tail to open the upload.", ex);
        }
        catch (IOException ex)
        {
            return Result<UploadSession>.Fail(
                ErrorCode.ProviderUnreachable, "Connection to the tail dropped opening the upload.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error opening OneDrive upload session.");
            return Result<UploadSession>.Fail(ErrorCode.Unknown, "Unexpected error opening the upload.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<long>> GetUploadedBytesAsync(UploadSession session, CancellationToken ct)
    {
        if (_earlyFinalised.ContainsKey(session.SessionUri))
        {
            // The final range already committed the item; everything has been received.
            return Result<long>.Ok(session.TotalBytes);
        }

        if (!OneDriveSessionUri.TryDecode(session.SessionUri, out var decoded))
        {
            return Result<long>.Fail(
                ErrorCode.UploadSessionExpired, "The stored upload state is unreadable; restarting.");
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, decoded.UploadUrl);
            using HttpResponseMessage response = await _bundle.PlainClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // Session staging gone → caller restarts from byte 0.
                return Result<long>.Ok(0);
            }
            if (!response.IsSuccessStatusCode)
            {
                return await MapHttpFailureAsync<long>(response, "check upload progress", ct).ConfigureAwait(false);
            }

            var status = await response.Content
                .ReadFromJsonOrDefaultAsync(OneDriveJsonContext.Default.UploadStatusResponse, ct)
                .ConfigureAwait(false);

            return Result<long>.Ok(NextOffsetFromRanges(status?.NextExpectedRanges, session.TotalBytes));
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<long>(ex, ct, "GetUploadedBytesAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result<long>.Fail(ErrorCode.ProviderUnreachable, "Could not reach the tail to check progress.", ex);
        }
        catch (IOException ex)
        {
            return Result<long>.Fail(ErrorCode.ProviderUnreachable, "Connection to the tail dropped checking progress.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error checking OneDrive upload progress.");
            return Result<long>.Fail(ErrorCode.Unknown, "Unexpected error checking upload progress.", ex);
        }
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

        if (!OneDriveSessionUri.TryDecode(session.SessionUri, out var decoded))
        {
            return Result.Fail(ErrorCode.UploadSessionExpired, "The stored upload state is unreadable; restarting.");
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            long lastByte = offset + data.Length - 1;
            using var content = new ReadOnlyMemoryContent(data);
            content.Headers.ContentLength = data.Length;
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentRange = new ContentRangeHeaderValue(offset, lastByte, session.TotalBytes);

            using var request = new HttpRequestMessage(HttpMethod.Put, decoded.UploadUrl) { Content = content };
            using HttpResponseMessage response = await _bundle.PlainClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                // 202 Accepted → more ranges expected.
                return Result.Ok();
            }
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                // Final chunk → the committed DriveItem is in the body. Cache its id for finalisation.
                var item = await response.Content
                    .ReadFromJsonOrDefaultAsync(OneDriveJsonContext.Default.DriveItemResponse, ct)
                    .ConfigureAwait(false);
                if (item is not null && !string.IsNullOrEmpty(item.Id))
                {
                    _earlyFinalised[session.SessionUri] = new EarlyFinalisation(item.Id, item.Size, item.File?.Hashes?.QuickXorHash);
                }
                return Result.Ok();
            }
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 416 → range already received; treat as success and let the caller advance.
                return Result.Ok();
            }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return Result.Fail(ErrorCode.UploadSessionExpired, "The upload session is no longer valid; restarting.");
            }

            return await MapHttpFailureAsync(response, "upload data", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation(ex, ct, "UploadRangeAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail(ErrorCode.ProviderUnreachable, "Could not reach the tail to upload data.", ex);
        }
        catch (IOException ex)
        {
            return Result.Fail(ErrorCode.ProviderUnreachable, "Connection to the tail dropped during upload.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during OneDrive range upload.");
            return Result.Fail(ErrorCode.Unknown, "Unexpected error uploading data.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<string>> FinaliseUploadAsync(UploadSession session, CancellationToken ct)
    {
        if (!OneDriveSessionUri.TryDecode(session.SessionUri, out var decoded))
        {
            return Result<string>.Fail(
                ErrorCode.UploadSessionExpired, "The stored upload state is unreadable; restarting.");
        }

        // OneDrive commits on the final range PUT; the item facts were cached there.
        if (_earlyFinalised.TryRemove(session.SessionUri, out var cached))
        {
            _logger.LogInformation(
                "OneDrive accepted file for {ProviderId}; id={FileId}, size={Size}, quickXorHash={Hash}.",
                ProviderID, cached.RemoteId, cached.Size, cached.QuickXorHash ?? "<absent>");
            return Result<string>.Ok(cached.RemoteId);
        }

        // Fallback: resolve the committed item by its path.
        try
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = BuildRelativePath(decoded.RemoteName);
            var url = $"{GraphRoot}/me/drive/root:/{EscapePath(relativePath)}?$select=id,size,file";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = await _bundle.AuthedClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return Result<string>.Fail(
                    ErrorCode.UploadSessionExpired, "The uploaded file could not be confirmed; restarting.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return await MapHttpFailureAsync<string>(response, "confirm the upload", ct).ConfigureAwait(false);
            }

            var item = await response.Content
                .ReadFromJsonOrDefaultAsync(OneDriveJsonContext.Default.DriveItemResponse, ct)
                .ConfigureAwait(false);
            if (item is null || string.IsNullOrEmpty(item.Id))
            {
                return Result<string>.Fail(ErrorCode.ProviderApiChanged, "The tail did not return a file identifier.");
            }

            _logger.LogInformation(
                "OneDrive accepted file for {ProviderId}; id={FileId}, size={Size}, quickXorHash={Hash}.",
                ProviderID, item.Id, item.Size, item.File?.Hashes?.QuickXorHash ?? "<absent>");
            return Result<string>.Ok(item.Id);
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<string>(ex, ct, "FinaliseUploadAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result<string>.Fail(ErrorCode.ProviderUnreachable, "Could not reach the tail to confirm the upload.", ex);
        }
        catch (IOException ex)
        {
            return Result<string>.Fail(ErrorCode.ProviderUnreachable, "Connection to the tail dropped confirming the upload.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error finalising OneDrive upload.");
            return Result<string>.Fail(ErrorCode.Unknown, "Unexpected error confirming the upload.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result> AbortUploadAsync(UploadSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _earlyFinalised.TryRemove(session.SessionUri, out _);

        if (!OneDriveSessionUri.TryDecode(session.SessionUri, out var decoded))
        {
            _logger.LogDebug("OneDrive AbortUpload requested for malformed session envelope; no-op.");
            return Result.Ok();
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, decoded.UploadUrl);

            // Compensation path: abort must not be cancelled mid-flight (principle 17).
            using HttpResponseMessage response = await _bundle.PlainClient
                .SendAsync(request, CancellationToken.None)
                .ConfigureAwait(false);

            if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.OK))
            {
                _logger.LogDebug(
                    "OneDrive AbortUpload returned {Status} for {Provider}; treating as best-effort success.",
                    (int)response.StatusCode, ProviderID);
            }

            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "AbortUploadAsync cancelled.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OneDrive AbortUpload best-effort failure for {Provider}; swallowed.", ProviderID);
            return Result.Ok();
        }
    }

    // ── Download / existence / delete ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<Stream>> DownloadAsync(string remoteId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(remoteId))
        {
            return Result<Stream>.Fail(ErrorCode.InvalidArgument, "remoteId must not be empty.");
        }

        HttpResponseMessage? response = null;
        try
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{GraphRoot}/me/drive/items/{Uri.EscapeDataString(remoteId)}/content";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            response = await _bundle.AuthedClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                response.Dispose();
                return Result<Stream>.Fail(ErrorCode.BlobNotFound, "The requested file is not on this tail.");
            }
            if (!response.IsSuccessStatusCode)
            {
                var mapped = await MapHttpFailureAsync<Stream>(response, "download the file", ct).ConfigureAwait(false);
                response.Dispose();
                return mapped;
            }

            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var owned = new ResponseOwningStream(stream, response);
            response = null; // ownership transferred to the wrapper
            return Result<Stream>.Ok(owned);
        }
        catch (OperationCanceledException ex)
        {
            response?.Dispose();
            return MapCancellation<Stream>(ex, ct, "DownloadAsync");
        }
        catch (HttpRequestException ex)
        {
            response?.Dispose();
            return Result<Stream>.Fail(ErrorCode.ProviderUnreachable, "Could not reach the tail to download the file.", ex);
        }
        catch (IOException ex)
        {
            response?.Dispose();
            return Result<Stream>.Fail(ErrorCode.ProviderUnreachable, "Connection to the tail dropped during download.", ex);
        }
        catch (Exception ex)
        {
            response?.Dispose();
            _logger.LogError(ex, "Unexpected error downloading {RemoteId} from OneDrive.", remoteId);
            return Result<Stream>.Fail(ErrorCode.Unknown, "Unexpected error downloading the file.", ex);
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

            var url = $"{GraphRoot}/me/drive/items/{Uri.EscapeDataString(remoteId)}";
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            using HttpResponseMessage response = await _bundle.AuthedClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // Idempotent: missing object → success.
                return Result.Ok();
            }

            return await MapHttpFailureAsync(response, "delete the file", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation(ex, ct, "DeleteAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail(ErrorCode.ProviderUnreachable, "Could not reach the tail to delete the file.", ex);
        }
        catch (IOException ex)
        {
            return Result.Fail(ErrorCode.ProviderUnreachable, "Connection to the tail dropped during delete.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting {RemoteId} from OneDrive.", remoteId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error deleting the file.", ex);
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

            var url = $"{GraphRoot}/me/drive/items/{Uri.EscapeDataString(remoteId)}?$select=id";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = await _bundle.AuthedClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return Result<bool>.Ok(false);
            }
            if (response.IsSuccessStatusCode)
            {
                return Result<bool>.Ok(true);
            }

            return await MapHttpFailureAsync<bool>(response, "check whether the file exists", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<bool>(ex, ct, "ExistsAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result<bool>.Fail(ErrorCode.ProviderUnreachable, "Could not reach the tail to check the file.", ex);
        }
        catch (IOException ex)
        {
            return Result<bool>.Fail(ErrorCode.ProviderUnreachable, "Connection to the tail dropped checking the file.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error checking existence of {RemoteId} on OneDrive.", remoteId);
            return Result<bool>.Fail(ErrorCode.Unknown, "Unexpected error checking the file.", ex);
        }
    }

    // ── Listing ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<IReadOnlyList<string>>> ListAsync(string prefix, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var filterPrefix = NormalisePrefix(prefix);
            var results = new List<string>();

            // Graph has no recursive list; descend folders breadth-first under the configured root and
            // post-filter on the requested relative prefix. Bounded by V1's expected object counts.
            var pending = new Queue<string>();
            pending.Enqueue(string.Empty);

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var folderRel = pending.Dequeue();
                var notFound = await ListFolderAsync(folderRel, filterPrefix, pending, results, ct).ConfigureAwait(false);
                if (notFound.Success && !notFound.Value && folderRel.Length == 0)
                {
                    // Root folder absent → nothing has been uploaded yet.
                    return Result<IReadOnlyList<string>>.Ok(Array.Empty<string>());
                }
                if (!notFound.Success)
                {
                    return Result<IReadOnlyList<string>>.Fail(notFound.Error);
                }
            }

            return Result<IReadOnlyList<string>>.Ok(results);
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<IReadOnlyList<string>>(ex, ct, "ListAsync");
        }
        catch (HttpRequestException ex)
        {
            return Result<IReadOnlyList<string>>.Fail(ErrorCode.ProviderUnreachable, "Could not reach the tail to list files.", ex);
        }
        catch (IOException ex)
        {
            return Result<IReadOnlyList<string>>.Fail(ErrorCode.ProviderUnreachable, "Connection to the tail dropped listing files.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing OneDrive prefix '{Prefix}'.", prefix);
            return Result<IReadOnlyList<string>>.Fail(ErrorCode.Unknown, "Unexpected error listing files.", ex);
        }
    }

    /// <summary>
    /// Lists one folder (paginating across <c>@odata.nextLink</c>), enqueues child folders, and
    /// collects file ids whose root-relative path matches <paramref name="filterPrefix"/>. The
    /// returned <see cref="bool"/> is <see langword="false"/> only when the folder itself is absent
    /// (404), letting the root case short-circuit to an empty listing.
    /// </summary>
    private async Task<Result<bool>> ListFolderAsync(
        string folderRel, string filterPrefix, Queue<string> pending, List<string> results, CancellationToken ct)
    {
        var combined = CombineRoot(folderRel);
        var url = $"{GraphRoot}/me/drive/root:/{EscapePath(combined)}:/children?$select=id,name,folder,file&$top=200";

        while (!string.IsNullOrEmpty(url))
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = await _bundle.AuthedClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return Result<bool>.Ok(false);
            }
            if (!response.IsSuccessStatusCode)
            {
                return await MapHttpFailureAsync<bool>(response, "list files", ct).ConfigureAwait(false);
            }

            var page = await response.Content
                .ReadFromJsonOrDefaultAsync(OneDriveJsonContext.Default.ChildrenResponse, ct)
                .ConfigureAwait(false);

            if (page?.Value is not null)
            {
                foreach (var child in page.Value)
                {
                    if (string.IsNullOrEmpty(child.Name))
                    {
                        continue;
                    }
                    var childRel = folderRel.Length == 0 ? "/" + child.Name : folderRel + "/" + child.Name;
                    if (child.Folder is not null)
                    {
                        pending.Enqueue(childRel);
                    }
                    else if (!string.IsNullOrEmpty(child.Id) &&
                             childRel.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(child.Id);
                    }
                }
            }

            url = page?.NextLink;
        }

        return Result<bool>.Ok(true);
    }

    // ── Health and capacity ───────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<ProviderHealth>> CheckHealthAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphRoot}/me/drive?$select=id");
            using HttpResponseMessage response = await _bundle.AuthedClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                return Result<ProviderHealth>.Ok(new ProviderHealth
                {
                    Status = ProviderHealthStatus.Healthy,
                    CheckedAt = DateTimeOffset.UtcNow,
                    RoundTripLatency = sw.Elapsed,
                });
            }

            var status = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderHealthStatus.AuthFailed,
                HttpStatusCode.TooManyRequests => ProviderHealthStatus.Degraded,
                _ => ProviderHealthStatus.Unreachable,
            };
            return Result<ProviderHealth>.Ok(new ProviderHealth
            {
                Status = status,
                CheckedAt = DateTimeOffset.UtcNow,
                RoundTripLatency = sw.Elapsed,
                Detail = $"The tail returned status {(int)response.StatusCode}.",
            });
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<ProviderHealth>(ex, ct, "CheckHealthAsync");
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
        catch (IOException ex)
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
            _logger.LogError(ex, "Unexpected error during OneDrive health probe.");
            return Result<ProviderHealth>.Fail(ErrorCode.Unknown, "Unexpected error during the tail health check.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<long>> GetUsedBytesAsync(CancellationToken ct)
    {
        var quota = await TryGetQuotaAsync(ct).ConfigureAwait(false);
        if (!quota.Success)
        {
            return Result<long>.Fail(quota.Error);
        }
        return Result<long>.Ok(quota.Value.Used ?? 0);
    }

    /// <inheritdoc/>
    public async Task<Result<long?>> GetQuotaBytesAsync(CancellationToken ct)
    {
        var quota = await TryGetQuotaAsync(ct).ConfigureAwait(false);
        if (!quota.Success)
        {
            return Result<long?>.Fail(quota.Error);
        }
        return Result<long?>.Ok(quota.Value.Total);
    }

    private async Task<Result<DriveQuota>> TryGetQuotaAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphRoot}/me/drive?$select=quota");
            using HttpResponseMessage response = await _bundle.AuthedClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await MapHttpFailureAsync<DriveQuota>(response, "read storage usage", ct).ConfigureAwait(false);
            }

            var drive = await response.Content
                .ReadFromJsonOrDefaultAsync(OneDriveJsonContext.Default.DriveResponse, ct)
                .ConfigureAwait(false);

            return Result<DriveQuota>.Ok(drive?.Quota ?? new DriveQuota());
        }
        catch (OperationCanceledException ex)
        {
            return MapCancellation<DriveQuota>(ex, ct, "GetQuota");
        }
        catch (HttpRequestException ex)
        {
            return Result<DriveQuota>.Fail(ErrorCode.ProviderUnreachable, "Could not reach the tail to read storage usage.", ex);
        }
        catch (IOException ex)
        {
            return Result<DriveQuota>.Fail(ErrorCode.ProviderUnreachable, "Connection to the tail dropped reading storage usage.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reading OneDrive storage usage.");
            return Result<DriveQuota>.Fail(ErrorCode.Unknown, "Unexpected error reading storage usage.", ex);
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

    /// <summary>Builds the root-relative object path (no leading slash) from <paramref name="remoteName"/>.</summary>
    private string BuildRelativePath(string remoteName)
    {
        var root = _rootPath.Trim('/');
        var leaf = remoteName.Trim('/');
        if (root.Length == 0)
        {
            return leaf;
        }
        return leaf.Length == 0 ? root : root + "/" + leaf;
    }

    /// <summary>Combines the configured root with a root-relative folder path (leading-slash form).</summary>
    private string CombineRoot(string folderRel)
    {
        var root = _rootPath.Trim('/');
        var sub = folderRel.Trim('/');
        if (sub.Length == 0)
        {
            return root;
        }
        return root.Length == 0 ? sub : root + "/" + sub;
    }

    /// <summary>URL-escapes each path segment individually, preserving the <c>/</c> separators Graph requires.</summary>
    private static string EscapePath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }
        return string.Join('/', segments);
    }

    private static string LeafName(string relativePath)
    {
        int idx = relativePath.LastIndexOf('/');
        return idx < 0 ? relativePath : relativePath[(idx + 1)..];
    }

    private static string NormalisePrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return string.Empty;
        }
        return prefix.StartsWith('/') ? prefix : "/" + prefix;
    }

    /// <summary>
    /// Reads the leading offset implied by Graph's <c>nextExpectedRanges</c> (e.g. <c>"5242880-"</c>).
    /// An empty list means the upload is complete, so the next offset is <paramref name="totalBytes"/>.
    /// </summary>
    private static long NextOffsetFromRanges(IReadOnlyList<string>? ranges, long totalBytes)
    {
        if (ranges is null || ranges.Count == 0)
        {
            return totalBytes;
        }
        var first = ranges[0];
        int dash = first.IndexOf('-');
        var startText = dash < 0 ? first : first[..dash];
        return long.TryParse(startText, out var start) ? start : 0;
    }

    private Result<T> MapCancellation<T>(OperationCanceledException ex, CancellationToken ct, string op)
    {
        if (ct.IsCancellationRequested)
        {
            return Result<T>.Fail(ErrorCode.Cancelled, $"{op} cancelled.", ex);
        }
        return Result<T>.Fail(ErrorCode.ProviderUnreachable, $"{op} timed out at the connection layer.", ex);
    }

    private Result MapCancellation(OperationCanceledException ex, CancellationToken ct, string op)
    {
        if (ct.IsCancellationRequested)
        {
            return Result.Fail(ErrorCode.Cancelled, $"{op} cancelled.", ex);
        }
        return Result.Fail(ErrorCode.ProviderUnreachable, $"{op} timed out at the connection layer.", ex);
    }

    /// <summary>
    /// Maps a non-success Graph HTTP response to a stable <see cref="ErrorCode"/> per the §4.5 plan's
    /// failure table. The (capped) response body is read once to disambiguate 403 sub-reasons; it is
    /// never logged (principle 26).
    /// </summary>
    private async Task<Result<T>> MapHttpFailureAsync<T>(HttpResponseMessage response, string action, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var body = await ReadBodyCappedAsync(response, ct).ConfigureAwait(false);

        if (status == 401)
        {
            return Result<T>.Fail(ErrorCode.TokenRefreshFailed, $"The tail rejected the saved sign-in trying to {action}.");
        }
        if (status == 507 || (status == 403 && body.Contains("quotaLimitReached", StringComparison.OrdinalIgnoreCase)))
        {
            return Result<T>.Fail(ErrorCode.ProviderQuotaExceeded, $"The tail is out of space trying to {action}.");
        }
        if (status == 429 || (status == 403 && body.Contains("activityLimitReached", StringComparison.OrdinalIgnoreCase)))
        {
            return Result<T>.Fail(ErrorCode.ProviderRateLimited, $"The tail is rate-limiting requests trying to {action}.");
        }
        if (status == 403)
        {
            return Result<T>.Fail(ErrorCode.ProviderAuthFailed, $"The tail denied permission trying to {action}.");
        }
        if (status >= 500)
        {
            _logger.LogWarning("OneDrive returned HTTP {Status} trying to {Action} for {Provider}.", status, action, ProviderID);
            return Result<T>.Fail(new ErrorContext
            {
                Code = ErrorCode.UploadFailed,
                Message = $"The tail reported a temporary failure trying to {action}.",
                Metadata = new Dictionary<string, string> { ["HttpStatus"] = status.ToString() },
            });
        }

        _logger.LogWarning("OneDrive returned HTTP {Status} trying to {Action} for {Provider}.", status, action, ProviderID);
        return Result<T>.Fail(ErrorCode.Unknown, $"The tail returned an unexpected status trying to {action}.");
    }

    private async Task<Result> MapHttpFailureAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        var typed = await MapHttpFailureAsync<object>(response, action, ct).ConfigureAwait(false);
        // Generic helper only ever produces failed Results; Error is non-null by construction (principle 37).
        return Result.Fail(typed.Error!);
    }

    private static async Task<string> ReadBodyCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return raw.Length <= 1024 ? raw : raw[..1024];
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// <see cref="Stream"/> wrapper that disposes the underlying <see cref="HttpResponseMessage"/>
    /// when the download stream is closed, so <see cref="DownloadAsync"/> can return a stream directly
    /// without leaking the response.
    /// </summary>
    private sealed class ResponseOwningStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        private int _disposed;

        public ResponseOwningStream(Stream inner, HttpResponseMessage response)
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
}

// ── Session URI envelope ────────────────────────────────────────────────────────────────────────

/// <summary>
/// Encodes <c>(uploadUrl, remoteName)</c> into the single <see cref="UploadSession.SessionUri"/>
/// field. Persistent envelope shape: <c>{"u":"...","n":"..."}</c>.
/// </summary>
internal static class OneDriveSessionUri
{
    public static string Encode(string uploadUrl, string remoteName)
    {
        ArgumentException.ThrowIfNullOrEmpty(uploadUrl);
        ArgumentException.ThrowIfNullOrEmpty(remoteName);
        var env = new OneDriveSessionEnvelope { UploadUrl = uploadUrl, RemoteName = remoteName };
        return JsonSerializer.Serialize(env, OneDriveSessionUriJsonContext.Default.OneDriveSessionEnvelope);
    }

    /// <summary>
    /// Robust decode. NEVER throws. Returns <see langword="false"/> for null/empty/whitespace,
    /// malformed JSON, missing fields, or any unexpected exception. A <see langword="false"/> outcome
    /// is mapped by callers to <see cref="ErrorCode.UploadSessionExpired"/> so the upload restarts.
    /// </summary>
    public static bool TryDecode(string? raw, out (string UploadUrl, string RemoteName) decoded)
    {
        decoded = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        try
        {
            var env = JsonSerializer.Deserialize(raw, OneDriveSessionUriJsonContext.Default.OneDriveSessionEnvelope);
            if (env is null ||
                string.IsNullOrWhiteSpace(env.UploadUrl) ||
                string.IsNullOrWhiteSpace(env.RemoteName))
            {
                return false;
            }
            decoded = (env.UploadUrl, env.RemoteName);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal sealed record OneDriveSessionEnvelope
    {
        [JsonPropertyName("u")] public string? UploadUrl { get; init; }
        [JsonPropertyName("n")] public string? RemoteName { get; init; }
    }
}

[JsonSerializable(typeof(OneDriveSessionUri.OneDriveSessionEnvelope))]
internal sealed partial class OneDriveSessionUriJsonContext : JsonSerializerContext;

// ── Graph wire DTOs ───────────────────────────────────────────────────────────────────────────

internal sealed record CreateUploadSessionRequest
{
    [JsonPropertyName("item")] public required UploadSessionItem Item { get; init; }
}

internal sealed record UploadSessionItem
{
    [JsonPropertyName("@microsoft.graph.conflictBehavior")] public string ConflictBehavior { get; init; } = "replace";
    [JsonPropertyName("name")] public required string Name { get; init; }
}

internal sealed record CreateUploadSessionResponse
{
    [JsonPropertyName("uploadUrl")] public string? UploadUrl { get; init; }
    [JsonPropertyName("expirationDateTime")] public DateTimeOffset? ExpirationDateTime { get; init; }
    [JsonPropertyName("nextExpectedRanges")] public IReadOnlyList<string>? NextExpectedRanges { get; init; }
}

internal sealed record UploadStatusResponse
{
    [JsonPropertyName("expirationDateTime")] public DateTimeOffset? ExpirationDateTime { get; init; }
    [JsonPropertyName("nextExpectedRanges")] public IReadOnlyList<string>? NextExpectedRanges { get; init; }
}

internal sealed record DriveItemResponse
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("file")] public FileFacet? File { get; init; }
}

internal sealed record FileFacet
{
    [JsonPropertyName("hashes")] public HashesFacet? Hashes { get; init; }
}

internal sealed record HashesFacet
{
    [JsonPropertyName("quickXorHash")] public string? QuickXorHash { get; init; }
    [JsonPropertyName("sha1Hash")] public string? Sha1Hash { get; init; }
    [JsonPropertyName("sha256Hash")] public string? Sha256Hash { get; init; }
}

internal sealed record ChildrenResponse
{
    [JsonPropertyName("value")] public IReadOnlyList<ChildItem>? Value { get; init; }
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; init; }
}

internal sealed record ChildItem
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("folder")] public JsonElement? Folder { get; init; }
    [JsonPropertyName("file")] public JsonElement? File { get; init; }
}

internal sealed record DriveResponse
{
    [JsonPropertyName("quota")] public DriveQuota? Quota { get; init; }
}

internal sealed record DriveQuota
{
    [JsonPropertyName("used")] public long? Used { get; init; }
    [JsonPropertyName("total")] public long? Total { get; init; }
    [JsonPropertyName("remaining")] public long? Remaining { get; init; }
}

[JsonSerializable(typeof(CreateUploadSessionRequest))]
[JsonSerializable(typeof(CreateUploadSessionResponse))]
[JsonSerializable(typeof(UploadStatusResponse))]
[JsonSerializable(typeof(DriveItemResponse))]
[JsonSerializable(typeof(ChildrenResponse))]
[JsonSerializable(typeof(DriveResponse))]
[JsonSerializable(typeof(DriveQuota))]
internal sealed partial class OneDriveJsonContext : JsonSerializerContext;

// ── Local helpers ───────────────────────────────────────────────────────────────────────────────

internal static class OneDriveHttpContentExtensions
{
    /// <summary>
    /// Deserialises the response content using the supplied source-generated type info, returning
    /// <see langword="null"/> on an empty body. Throws <see cref="JsonException"/> on malformed JSON,
    /// which the calling provider method maps to a <see cref="ErrorCode"/>.
    /// </summary>
    public static async Task<T?> ReadFromJsonOrDefaultAsync<T>(
        this HttpContent content, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (stream.CanSeek && stream.Length == 0)
        {
            return null;
        }
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, ct).ConfigureAwait(false);
    }
}
