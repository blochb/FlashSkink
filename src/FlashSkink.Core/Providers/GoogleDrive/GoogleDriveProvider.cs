using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using Google;
using Google.Apis.Drive.v3;
using Microsoft.Extensions.Logging;
using GoogleDriveData = Google.Apis.Drive.v3.Data;

namespace FlashSkink.Core.Providers.GoogleDrive;

/// <summary>
/// Production <see cref="IStorageProvider"/> over the Google Drive resumable-upload protocol.
/// Drives Drive's <c>uploadType=resumable</c> session lifecycle directly (raw HTTP) rather than
/// through the SDK's <c>MediaUpload</c> helper, because the helper takes ownership of the upload
/// loop and does not surface per-range boundaries — <c>RangeUploader</c> requires per-range commit
/// points for cross-host resumability (Blueprint §15.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Session model.</strong> <see cref="BeginUploadAsync"/> POSTs to
/// <c>upload/drive/v3/files?uploadType=resumable</c>; the returned <c>Location</c> header is the
/// session URI we PUT ranges to. <see cref="UploadRangeAsync"/> PUTs each range with a
/// <c>Content-Range</c> header. Drive's protocol auto-finalises when the last range is uploaded —
/// the server returns 200/201 with the <c>File</c> metadata instead of 308. We cache the resulting
/// file id keyed by session URI so <see cref="FinaliseUploadAsync"/> can return it without a
/// follow-up call.
/// </para>
/// <para>
/// <strong>Remote-name flattening.</strong> Drive's namespace under a single folder is flat — there
/// is no path separator inside a file name. FlashSkink's remote names contain <c>/</c>
/// (<c>blobs/ab/cd/{uuid}.bin</c>, <c>_brain/{ts}.bin</c>, <c>_witness/current.enc</c>). Drive sees
/// these flattened: <c>/</c> → <c>_</c>. Materialising the shard prefix as nested folders would
/// require ≥ 3 extra round-trips per upload to find/create folders; this is the V1 trade-off in
/// favour of upload latency. Resolved at Gate 1 (decision #3 in <c>.claude/plans/pr-4.3.md</c>).
/// </para>
/// <para>
/// <strong>Verification.</strong> <see cref="GoogleDriveProvider"/> does NOT implement
/// <c>ISupportsRemoteHashCheck</c>. Drive's metadata field is <c>md5Checksum</c>, which is
/// structurally distinct from the XXHash64 the capability interface returns. The encrypted blob's
/// AES-GCM authentication tag (Principle 6) is the cryptographic authenticator; tampering surfaces
/// at download-time decryption. Drive's MD5 is logged at <see cref="LogLevel.Information"/> on
/// finalisation for observability. Resolved at Gate 1 (decision #1 in
/// <c>.claude/plans/pr-4.3.md</c>).
/// </para>
/// <para>
/// <strong>Notification bus.</strong> The provider does not reference
/// <c>INotificationBus</c>. Per Principle 24 the publisher is <c>UploadQueueService</c>, which
/// observes <see cref="Result.Fail"/> outcomes and publishes accordingly. Resolved at Gate 1
/// (decision #7 in <c>.claude/plans/pr-4.3.md</c>).
/// </para>
/// </remarks>
internal sealed partial class GoogleDriveProvider : IStorageProvider, IAsyncDisposable
{
    // Drive resumable sessions are valid for 1 week per Blueprint §15.2.
    private static readonly TimeSpan ResumableSessionTtl = TimeSpan.FromDays(7);

    private const string ResumableInitEndpoint =
        "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable";

    private const string DriveDownloadEndpointTemplate =
        "https://www.googleapis.com/drive/v3/files/{0}?alt=media";

    private readonly GoogleDriveClientBundle _bundle;
    private readonly string _folderId;
    private readonly ILogger<GoogleDriveProvider> _logger;
    private int _disposed;

    /// <summary>
    /// Cache of remote IDs for sessions whose upload finalised early (i.e. the last
    /// <see cref="UploadRangeAsync"/> returned 200/201 instead of 308). Populated by
    /// <see cref="UploadRangeAsync"/>; consumed and cleared by <see cref="FinaliseUploadAsync"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, EarlyFinalisation> _earlyFinalised =
        new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public string ProviderID { get; }

    /// <inheritdoc/>
    public string ProviderType => "google-drive";

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <summary>
    /// The Drive folder ID resolved at setup time (the <c>"FlashSkink Backup"</c> folder).
    /// Surfaced to allow §4.6's <c>AddTailAsync</c> to persist the value into
    /// <c>Providers.ProviderConfig</c> after the first <c>CreateProviderAsync</c> call.
    /// Resolved at Gate 1 (decision #2 in <c>.claude/plans/pr-4.3.md</c>).
    /// </summary>
    internal string FolderId => _folderId;

    internal GoogleDriveProvider(
        string providerId,
        string displayName,
        GoogleDriveClientBundle bundle,
        string folderId,
        ILogger<GoogleDriveProvider> logger)
    {
        ProviderID = providerId;
        DisplayName = displayName;
        _bundle = bundle;
        _folderId = folderId;
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

            var flatName = FlattenRemoteName(remoteName);

            // Build the initiate-resumable request: JSON body with name + parent, plus
            // X-Upload-Content-Length / X-Upload-Content-Type headers. Drive returns 200 OK with
            // the session URI in the Location header.
            var metadata = JsonSerializer.Serialize(
                new DriveFileMetadata(flatName, [_folderId]),
                DriveJsonContext.Default.DriveFileMetadata);

            using var request = new HttpRequestMessage(HttpMethod.Post, ResumableInitEndpoint)
            {
                Content = new StringContent(metadata, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", totalBytes.ToString());
            request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");

            using var response = await _bundle.ResumableUploadClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await MapHttpFailureAsync<UploadSession>(
                    response, "initiate resumable upload", ct).ConfigureAwait(false);
            }

            if (response.Headers.Location is null)
            {
                _logger.LogError(
                    "Drive returned {Status} for resumable init but no Location header.",
                    (int)response.StatusCode);
                return Result<UploadSession>.Fail(
                    ErrorCode.ProviderApiChanged,
                    "Drive did not return a session URI for the resumable upload.");
            }

            var sessionUri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location.AbsoluteUri
                : response.Headers.Location.OriginalString;

            _logger.LogDebug(
                "Opened Drive resumable session for {Remote} ({TotalBytes} bytes).",
                flatName, totalBytes);

            return Result<UploadSession>.Ok(new UploadSession
            {
                SessionUri = sessionUri,
                ExpiresAt = DateTimeOffset.UtcNow + ResumableSessionTtl,
                BytesUploaded = 0,
                TotalBytes = totalBytes,
            });
        }
        catch (OperationCanceledException ex)
        {
            return Result<UploadSession>.Fail(ErrorCode.Cancelled, "BeginUploadAsync cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Drive resumable init network failure.");
            return Result<UploadSession>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure initiating Drive upload.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error initiating Drive upload.");
            return Result<UploadSession>.Fail(
                ErrorCode.Unknown, "Unexpected error initiating Drive upload.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<long>> GetUploadedBytesAsync(UploadSession session, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Put, session.SessionUri)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            };
            request.Content.Headers.ContentLength = 0;
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(session.TotalBytes);

            using var response = await _bundle.ResumableUploadClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // 308 Resume Incomplete: parse Range header (bytes=0-{last}).
            if ((int)response.StatusCode == 308)
            {
                if (response.Headers.TryGetValues("Range", out var rangeValues))
                {
                    var rangeHeader = rangeValues.FirstOrDefault();
                    var uploaded = ParseUploadedBytesFromRangeHeader(rangeHeader);
                    return Result<long>.Ok(uploaded);
                }
                // No Range header → server has 0 bytes.
                return Result<long>.Ok(0);
            }

            // 200/201: upload already complete on Drive's side.
            if (response.IsSuccessStatusCode)
            {
                return Result<long>.Ok(session.TotalBytes);
            }

            // 404/410: session is gone.
            if (response.StatusCode == HttpStatusCode.NotFound ||
                response.StatusCode == HttpStatusCode.Gone)
            {
                return Result<long>.Fail(
                    ErrorCode.UploadSessionExpired,
                    "Drive resumable session is no longer valid.");
            }

            return await MapHttpFailureAsync<long>(
                response, "query uploaded byte count", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return Result<long>.Fail(ErrorCode.Cancelled, "GetUploadedBytesAsync cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            return Result<long>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure querying Drive upload state.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error querying Drive upload state.");
            return Result<long>.Fail(
                ErrorCode.Unknown, "Unexpected error querying Drive upload state.", ex);
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

        try
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Put, session.SessionUri)
            {
                Content = new ReadOnlyMemoryContent(data),
            };
            request.Content.Headers.ContentLength = data.Length;
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                from: offset,
                to: offset + data.Length - 1,
                length: session.TotalBytes);

            using var response = await _bundle.ResumableUploadClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // 308 Resume Incomplete: normal — more ranges to come.
            if ((int)response.StatusCode == 308)
            {
                return Result.Ok();
            }

            // 200/201: Drive auto-finalised — cache the file id for the upcoming FinaliseUploadAsync.
            if (response.IsSuccessStatusCode)
            {
                var bodyJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var file = DeserialiseFile(bodyJson);
                if (file is not null && !string.IsNullOrEmpty(file.Id))
                {
                    _earlyFinalised[session.SessionUri] = new EarlyFinalisation(file.Id, file.Md5Checksum);
                    _logger.LogDebug(
                        "Drive auto-finalised upload via final range; id={FileId}.", file.Id);
                }
                else
                {
                    _logger.LogWarning(
                        "Drive returned {Status} on range PUT but body did not include a file id.",
                        (int)response.StatusCode);
                }
                return Result.Ok();
            }

            // 404/410/400-with-Invalid-Range: session expired.
            if (response.StatusCode == HttpStatusCode.NotFound ||
                response.StatusCode == HttpStatusCode.Gone)
            {
                return Result.Fail(
                    ErrorCode.UploadSessionExpired,
                    "Drive resumable session is no longer valid (404/410).");
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var body = await ReadResponseBodyCappedAsync(response, ct).ConfigureAwait(false);
                if (body.Contains("Invalid range", StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Fail(
                        ErrorCode.UploadSessionExpired,
                        "Drive rejected the range as invalid; treating as expired session.");
                }
                return Result.Fail(new ErrorContext
                {
                    Code = ErrorCode.UploadFailed,
                    Message = "Drive rejected the upload range.",
                    Metadata = new Dictionary<string, string> { ["responseBody"] = body },
                });
            }

            return await MapHttpFailureAsync(
                response, "upload range", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "UploadRangeAsync cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail(
                ErrorCode.ProviderUnreachable, "Network failure during Drive range upload.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Drive range upload.");
            return Result.Fail(
                ErrorCode.Unknown, "Unexpected error during Drive range upload.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<string>> FinaliseUploadAsync(UploadSession session, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // Fast path: previous UploadRangeAsync already received Drive's 200/201 with the file id.
            if (_earlyFinalised.TryRemove(session.SessionUri, out var early))
            {
                _logger.LogInformation(
                    "Drive accepted blob (early-finalised) for {ProviderId}; id={FileId}, md5Checksum={Md5}.",
                    ProviderID, early.RemoteId, early.Md5Checksum ?? "<absent>");
                return Result<string>.Ok(early.RemoteId);
            }

            // Otherwise issue a final "query state" PUT with empty body and Content-Range: */{total}.
            using var request = new HttpRequestMessage(HttpMethod.Put, session.SessionUri)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            };
            request.Content.Headers.ContentLength = 0;
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(session.TotalBytes);

            using var response = await _bundle.ResumableUploadClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var bodyJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var file = DeserialiseFile(bodyJson);
                if (file is null || string.IsNullOrEmpty(file.Id))
                {
                    return Result<string>.Fail(
                        ErrorCode.ProviderApiChanged,
                        "Drive finalisation response did not include a file id.");
                }
                _logger.LogInformation(
                    "Drive accepted blob for {ProviderId}; id={FileId}, md5Checksum={Md5}.",
                    ProviderID, file.Id, file.Md5Checksum ?? "<absent>");
                return Result<string>.Ok(file.Id);
            }

            if ((int)response.StatusCode == 308)
            {
                return Result<string>.Fail(
                    ErrorCode.UploadFailed,
                    "Drive reports the upload is not yet complete (received 308 on finalisation probe).");
            }

            return await MapHttpFailureAsync<string>(
                response, "finalise upload", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return Result<string>.Fail(ErrorCode.Cancelled, "FinaliseUploadAsync cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            return Result<string>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure finalising Drive upload.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error finalising Drive upload.");
            return Result<string>.Fail(
                ErrorCode.Unknown, "Unexpected error finalising Drive upload.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result> AbortUploadAsync(UploadSession session, CancellationToken ct)
    {
        // Best-effort cleanup. ct is observed at entry; subsequent network errors are swallowed.
        try
        {
            ct.ThrowIfCancellationRequested();

            // Clear any cached early-finalisation entry — the caller is abandoning the session.
            _earlyFinalised.TryRemove(session.SessionUri, out _);

            using var request = new HttpRequestMessage(HttpMethod.Delete, session.SessionUri);
            using var response = await _bundle.ResumableUploadClient
                .SendAsync(request, CancellationToken.None)
                .ConfigureAwait(false);

            // 204 No Content = success; 404/410 = already gone. Either way: ok.
            if (!response.IsSuccessStatusCode &&
                response.StatusCode != HttpStatusCode.NotFound &&
                response.StatusCode != HttpStatusCode.Gone)
            {
                _logger.LogDebug(
                    "Drive AbortUpload returned non-success {Status} for {SessionUri}; swallowed.",
                    (int)response.StatusCode, session.SessionUri);
            }
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "AbortUploadAsync cancelled.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Drive AbortUpload failed; swallowed (best-effort cleanup).");
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

        // Null-out ownership transfer (Principle 16): `response` is heap-allocated; on the success
        // path we hand it to ResponseOwningStream and null the local so the catch blocks don't
        // double-dispose. On every other path (including a throw between SendAsync and the
        // ownership transfer — e.g. cancellation during ReadAsStreamAsync) the catch blocks
        // dispose it.
        HttpResponseMessage? response = null;
        try
        {
            ct.ThrowIfCancellationRequested();

            var url = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                DriveDownloadEndpointTemplate, Uri.EscapeDataString(remoteId));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            response = await _bundle.ResumableUploadClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                response = null;
                return Result<Stream>.Fail(
                    ErrorCode.BlobNotFound, $"Drive object '{remoteId}' not found.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var failure = await MapHttpFailureAsync<Stream>(
                    response, "download", ct).ConfigureAwait(false);
                response.Dispose();
                response = null;
                return failure;
            }

            // Wrap the body in a stream that disposes the response on close.
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var owned = new ResponseOwningStream(stream, response);
            response = null; // ownership transferred to ResponseOwningStream
            return Result<Stream>.Ok(owned);
        }
        catch (OperationCanceledException ex)
        {
            response?.Dispose();
            return Result<Stream>.Fail(ErrorCode.Cancelled, "DownloadAsync cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            response?.Dispose();
            return Result<Stream>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure downloading from Drive.", ex);
        }
        catch (Exception ex)
        {
            response?.Dispose();
            _logger.LogError(ex, "Unexpected error downloading {RemoteId} from Drive.", remoteId);
            return Result<Stream>.Fail(ErrorCode.Unknown, "Unexpected error during Drive download.", ex);
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
            await _bundle.DriveService.Files.Delete(remoteId).ExecuteAsync(ct).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException ex)
        {
            return Result.Fail(ErrorCode.Cancelled, "DeleteAsync cancelled.", ex);
        }
        catch (GoogleApiException gex) when ((int)gex.HttpStatusCode == 404)
        {
            // Idempotent: missing object treated as success.
            return Result.Ok();
        }
        catch (GoogleApiException gex)
        {
            return MapGoogleApiException(gex, "delete");
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail(
                ErrorCode.ProviderUnreachable, "Network failure deleting from Drive.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting {RemoteId} from Drive.", remoteId);
            return Result.Fail(ErrorCode.Unknown, "Unexpected error during Drive delete.", ex);
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
            var request = _bundle.DriveService.Files.Get(remoteId);
            request.Fields = "id";
            await request.ExecuteAsync(ct).ConfigureAwait(false);
            return Result<bool>.Ok(true);
        }
        catch (OperationCanceledException ex)
        {
            return Result<bool>.Fail(ErrorCode.Cancelled, "ExistsAsync cancelled.", ex);
        }
        catch (GoogleApiException gex) when ((int)gex.HttpStatusCode == 404)
        {
            return Result<bool>.Ok(false);
        }
        catch (GoogleApiException gex)
        {
            return MapGoogleApiException<bool>(gex, "existence check");
        }
        catch (HttpRequestException ex)
        {
            return Result<bool>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure checking existence on Drive.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error checking existence of {RemoteId} on Drive.", remoteId);
            return Result<bool>.Fail(ErrorCode.Unknown, "Unexpected error during Drive existence check.", ex);
        }
    }

    // ── Listing ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<IReadOnlyList<string>>> ListAsync(string prefix, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var flatPrefix = string.IsNullOrEmpty(prefix) ? "" : FlattenRemoteName(prefix);

            // Drive's `name contains` is a substring match. We post-filter for true prefix.
            var nameClause = string.IsNullOrEmpty(flatPrefix)
                ? string.Empty
                : $" and name contains '{EscapeDriveQuery(flatPrefix)}'";

            var query = $"'{_folderId}' in parents and trashed = false{nameClause}";

            var results = new List<string>();
            string? pageToken = null;

            do
            {
                ct.ThrowIfCancellationRequested();

                var listRequest = _bundle.DriveService.Files.List();
                listRequest.Q = query;
                listRequest.Fields = "nextPageToken, files(id, name)";
                listRequest.PageSize = 1000;
                listRequest.PageToken = pageToken;

                var page = await listRequest.ExecuteAsync(ct).ConfigureAwait(false);
                foreach (var f in page.Files ?? Enumerable.Empty<GoogleDriveData.File>())
                {
                    if (string.IsNullOrEmpty(flatPrefix) ||
                        (f.Name?.StartsWith(flatPrefix, StringComparison.Ordinal) ?? false))
                    {
                        if (!string.IsNullOrEmpty(f.Id))
                        {
                            results.Add(f.Id);
                        }
                    }
                }
                pageToken = page.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            return Result<IReadOnlyList<string>>.Ok(results);
        }
        catch (OperationCanceledException ex)
        {
            return Result<IReadOnlyList<string>>.Fail(ErrorCode.Cancelled, "ListAsync cancelled.", ex);
        }
        catch (GoogleApiException gex)
        {
            return MapGoogleApiException<IReadOnlyList<string>>(gex, "list");
        }
        catch (HttpRequestException ex)
        {
            return Result<IReadOnlyList<string>>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure listing Drive.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing Drive prefix '{Prefix}'.", prefix);
            return Result<IReadOnlyList<string>>.Fail(
                ErrorCode.Unknown, "Unexpected error listing Drive.", ex);
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

            // Single low-cost call: About.Get with a minimal fields mask.
            var about = _bundle.DriveService.About.Get();
            about.Fields = "user(emailAddress)";
            await about.ExecuteAsync(ct).ConfigureAwait(false);

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
            return Result<ProviderHealth>.Fail(ErrorCode.Cancelled, "CheckHealthAsync cancelled.", ex);
        }
        catch (GoogleApiException gex)
        {
            sw.Stop();
            var status = (int)gex.HttpStatusCode switch
            {
                401 => ProviderHealthStatus.AuthFailed,
                403 when IsQuotaError(gex) => ProviderHealthStatus.QuotaExceeded,
                _ => ProviderHealthStatus.Unreachable,
            };
            _logger.LogWarning(gex, "Drive health probe returned {Status}.", (int)gex.HttpStatusCode);
            return Result<ProviderHealth>.Ok(new ProviderHealth
            {
                Status = status,
                CheckedAt = DateTimeOffset.UtcNow,
                RoundTripLatency = sw.Elapsed,
                Detail = gex.Message,
            });
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Drive health probe network failure.");
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
            _logger.LogError(ex, "Unexpected error during Drive health probe.");
            return Result<ProviderHealth>.Fail(
                ErrorCode.Unknown, "Unexpected error during Drive health probe.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Result<long>> GetUsedBytesAsync(CancellationToken ct)
    {
        var about = await TryGetAboutAsync(ct).ConfigureAwait(false);
        if (!about.Success)
        {
            return Result<long>.Fail(about.Error!);
        }

        var usage = about.Value!.StorageQuota?.UsageAsLong();
        if (usage is null)
        {
            return Result<long>.Fail(
                ErrorCode.ProviderApiChanged, "Drive About did not return a storageQuota.usage value.");
        }
        return Result<long>.Ok(usage.Value);
    }

    /// <inheritdoc/>
    public async Task<Result<long?>> GetQuotaBytesAsync(CancellationToken ct)
    {
        var about = await TryGetAboutAsync(ct).ConfigureAwait(false);
        if (!about.Success)
        {
            return Result<long?>.Fail(about.Error!);
        }

        // Limit may be null for unlimited-plan users (e.g. legacy Workspace pooled storage).
        var limit = about.Value!.StorageQuota?.LimitAsLong();
        return Result<long?>.Ok(limit);
    }

    private async Task<Result<GoogleDriveData.About>> TryGetAboutAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var about = _bundle.DriveService.About.Get();
            about.Fields = "storageQuota";
            var result = await about.ExecuteAsync(ct).ConfigureAwait(false);
            return Result<GoogleDriveData.About>.Ok(result);
        }
        catch (OperationCanceledException ex)
        {
            return Result<GoogleDriveData.About>.Fail(ErrorCode.Cancelled, "About.Get cancelled.", ex);
        }
        catch (GoogleApiException gex)
        {
            return MapGoogleApiException<GoogleDriveData.About>(gex, "About.Get");
        }
        catch (HttpRequestException ex)
        {
            return Result<GoogleDriveData.About>.Fail(
                ErrorCode.ProviderUnreachable, "Network failure querying Drive About.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Drive About.Get.");
            return Result<GoogleDriveData.About>.Fail(
                ErrorCode.Unknown, "Unexpected error during Drive About.Get.", ex);
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

    /// <summary>Flattens path-style remote names into a flat Drive name (Resolution 3 in plan).</summary>
    internal static string FlattenRemoteName(string remoteName) =>
        remoteName.Replace('/', '_').Replace('\\', '_');

    /// <summary>Drive's Q-language requires single-quote escaping inside string literals.</summary>
    private static string EscapeDriveQuery(string s) =>
        s.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>Parses the <c>last</c> from a Drive <c>Range: bytes=0-{last}</c> header.</summary>
    private static long ParseUploadedBytesFromRangeHeader(string? rangeHeader)
    {
        if (string.IsNullOrEmpty(rangeHeader))
        {
            return 0;
        }
        // Format: "bytes=0-4194303"
        var eq = rangeHeader.IndexOf('=');
        var dash = rangeHeader.IndexOf('-', eq + 1);
        if (eq < 0 || dash < 0)
        {
            return 0;
        }
        var lastStr = rangeHeader[(dash + 1)..].Trim();
        return long.TryParse(lastStr, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var last)
            ? last + 1
            : 0;
    }

    private static GoogleDriveData.File? DeserialiseFile(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            // The Drive SDK's File model is annotated with Newtonsoft.Json, which is unfortunate.
            // We deserialise into our own minimal DriveFileResponse and project.
            var minimal = JsonSerializer.Deserialize(json, DriveJsonContext.Default.DriveFileResponse);
            if (minimal is null)
            {
                return null;
            }
            return new GoogleDriveData.File
            {
                Id = minimal.Id,
                Md5Checksum = minimal.Md5Checksum,
                Name = minimal.Name,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadResponseBodyCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length > 1024 ? body[..1024] : body;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<Result<T>> MapHttpFailureAsync<T>(
        HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var body = await ReadResponseBodyCappedAsync(response, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (status == 401)
        {
            _logger.LogError(
                "Drive {Operation} returned 401 Unauthorized; access-token refresh failed.", operation);
            return Result<T>.Fail(
                ErrorCode.TokenRefreshFailed,
                "Drive rejected the access token; refresh failed.");
        }
        if (status == 403)
        {
            if (body.Contains("storageQuotaExceeded", StringComparison.Ordinal))
            {
                return Result<T>.Fail(
                    ErrorCode.ProviderQuotaExceeded, "Drive storage quota exceeded.");
            }
            if (body.Contains("userRateLimitExceeded", StringComparison.Ordinal) ||
                body.Contains("rateLimitExceeded", StringComparison.Ordinal))
            {
                return Result<T>.Fail(
                    ErrorCode.ProviderRateLimited, "Drive rate limit exceeded.");
            }
            _logger.LogWarning("Drive {Operation} returned 403; body={Body}.", operation, body);
            return Result<T>.Fail(
                ErrorCode.ProviderAuthFailed, $"Drive rejected the {operation} request (403).");
        }
        if (status >= 500)
        {
            _logger.LogWarning(
                "Drive {Operation} returned 5xx ({Status}); body={Body}.", operation, status, body);
            return Result<T>.Fail(new ErrorContext
            {
                Code = ErrorCode.UploadFailed,
                Message = $"Drive returned {status} during {operation}.",
                Metadata = new Dictionary<string, string> { ["responseBody"] = body },
            });
        }

        _logger.LogWarning(
            "Drive {Operation} returned {Status}; body={Body}.", operation, status, body);
        return Result<T>.Fail(new ErrorContext
        {
            Code = ErrorCode.UploadFailed,
            Message = $"Drive returned {status} during {operation}.",
            Metadata = new Dictionary<string, string> { ["responseBody"] = body },
        });
    }

    private async Task<Result> MapHttpFailureAsync(
        HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var typed = await MapHttpFailureAsync<object>(response, operation, ct).ConfigureAwait(false);
        return Result.Fail(typed.Error!);
    }

    private Result MapGoogleApiException(GoogleApiException gex, string operation)
    {
        var typed = MapGoogleApiException<object>(gex, operation);
        return Result.Fail(typed.Error!);
    }

    private Result<T> MapGoogleApiException<T>(GoogleApiException gex, string operation)
    {
        var status = (int)gex.HttpStatusCode;
        if (status == 401)
        {
            _logger.LogError(gex, "Drive {Operation} returned 401.", operation);
            return Result<T>.Fail(
                ErrorCode.TokenRefreshFailed, "Drive rejected the access token; refresh failed.", gex);
        }
        if (status == 403)
        {
            if (IsQuotaError(gex))
            {
                return Result<T>.Fail(ErrorCode.ProviderQuotaExceeded, "Drive storage quota exceeded.", gex);
            }
            return Result<T>.Fail(
                ErrorCode.ProviderAuthFailed, $"Drive rejected the {operation} request (403).", gex);
        }
        if (status >= 500)
        {
            return Result<T>.Fail(
                ErrorCode.ProviderUnreachable, $"Drive returned {status} during {operation}.", gex);
        }
        return Result<T>.Fail(
            ErrorCode.UploadFailed, $"Drive returned {status} during {operation}.", gex);
    }

    private static bool IsQuotaError(GoogleApiException gex)
    {
        var msg = gex.Message ?? string.Empty;
        return msg.Contains("storageQuotaExceeded", StringComparison.Ordinal) ||
               msg.Contains("Quota exceeded", StringComparison.Ordinal);
    }

    /// <summary>Cache value for an upload that auto-finalised on its last range PUT.</summary>
    private readonly record struct EarlyFinalisation(string RemoteId, string? Md5Checksum);

    /// <summary>JSON shape posted to Drive's resumable-init endpoint.</summary>
    internal sealed record DriveFileMetadata(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("parents")] IReadOnlyList<string> Parents);

    /// <summary>Minimal projection of Drive's File resource used to read finalisation responses.</summary>
    internal sealed record DriveFileResponse
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("md5Checksum")] public string? Md5Checksum { get; init; }
    }

    [JsonSerializable(typeof(DriveFileMetadata))]
    [JsonSerializable(typeof(DriveFileResponse))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    internal sealed partial class DriveJsonContext : JsonSerializerContext;

    /// <summary>
    /// Stream wrapper that disposes the owning <see cref="HttpResponseMessage"/> when the stream is
    /// closed. Lets <see cref="DownloadAsync"/> return a <see cref="Stream"/> directly to the
    /// caller without leaking the response.
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

/// <summary>
/// Extension helpers for <see cref="GoogleDriveData.About.StorageQuotaData"/> long parsing.
/// Drive's SDK exposes <c>Usage</c> and <c>Limit</c> as <c>long?</c> already, but the names differ
/// across SDK versions — these helpers centralise the access for forward-compatibility.
/// </summary>
internal static class GoogleDriveAboutExtensions
{
    public static long? UsageAsLong(this GoogleDriveData.About.StorageQuotaData q) => q.Usage;
    public static long? LimitAsLong(this GoogleDriveData.About.StorageQuotaData q) => q.Limit;
}
