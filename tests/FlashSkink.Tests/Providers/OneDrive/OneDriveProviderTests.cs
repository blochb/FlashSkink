using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.OneDrive;
using FlashSkink.Tests._TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers.OneDrive;

/// <summary>
/// Unit tests for <see cref="OneDriveProvider"/> driving the Microsoft Graph resumable-upload
/// protocol through a scripted <see cref="RecordingHttpMessageHandler"/>. No network.
/// </summary>
public sealed class OneDriveProviderTests
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";
    private const string CreatePrefix = GraphRoot + "/me/drive/root:";
    private const string ItemsPrefix = GraphRoot + "/me/drive/items/";
    private const string DrivePrefix = GraphRoot + "/me/drive?";
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string UploadUrl = "https://upload.example.test/session/abc";
    private const string RootPath = "/FlashSkink Backup";

    private static (OneDriveProvider Provider, RecordingHttpMessageHandler Handler, RecordingLogger<OneDriveProvider> Logger) Build()
    {
        var handler = new RecordingHttpMessageHandler();
        var bundle = new FakeOneDriveClientFactory(handler)
            .Create("client-id", "client-secret", "refresh-token", NullLoggerFactory.Instance)
            .AssertValue();
        var logger = new RecordingLogger<OneDriveProvider>();
        var provider = new OneDriveProvider("provider-1", "OneDrive", bundle, RootPath, logger);
        return (provider, handler, logger);
    }

    private static async Task<UploadSession> BeginAsync(OneDriveProvider provider, RecordingHttpMessageHandler handler, long totalBytes, string remoteName = "blob1")
    {
        handler.Setup(HttpMethod.Post, CreatePrefix, _ => OneDriveCannedResponses.CreateUploadSession(UploadUrl));
        var begin = await provider.BeginUploadAsync(remoteName, totalBytes, CancellationToken.None);
        return begin.AssertValue();
    }

    // ── BeginUpload ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BeginUploadAsync_Success_ReturnsSessionWithEncodedUri()
    {
        var (provider, handler, _) = Build();
        var session = await BeginAsync(provider, handler, totalBytes: 100);

        Assert.True(OneDriveSessionUri.TryDecode(session.SessionUri, out var decoded));
        Assert.Equal(UploadUrl, decoded.UploadUrl);
        Assert.Equal("blob1", decoded.RemoteName);
        Assert.Equal(100, session.TotalBytes);
        Assert.Equal(0, session.BytesUploaded);

        // Only the createUploadSession POST happened; no range PUT yet.
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpMethod.Post, handler.ReceivedRequests[0].Method);

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task BeginUploadAsync_PathAddressing_PostsToRootColonPath()
    {
        var (provider, handler, _) = Build();
        await BeginAsync(provider, handler, totalBytes: 10);

        var url = Uri.UnescapeDataString(handler.ReceivedRequests[0].Url.AbsoluteUri);
        Assert.Contains("root:/FlashSkink Backup/blob1:/createUploadSession", url);

        await provider.DisposeAsync();
    }

    // ── UploadRange ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadRangeAsync_202_ReturnsOkMoreExpected()
    {
        var (provider, handler, _) = Build();
        var session = await BeginAsync(provider, handler, totalBytes: 100);
        handler.Setup(HttpMethod.Put, UploadUrl, _ => OneDriveCannedResponses.Status(202));

        var result = await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);

        Assert.True(result.Success);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task UploadRangeAsync_OmitsAuthorizationHeaderOnPlainClient()
    {
        var (provider, handler, _) = Build();
        var session = await BeginAsync(provider, handler, totalBytes: 100);
        handler.Setup(HttpMethod.Put, UploadUrl, _ => OneDriveCannedResponses.Status(202));

        await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);

        var put = handler.ReceivedRequests.Single(r => r.Method == HttpMethod.Put);
        Assert.Null(put.GetHeader("Authorization"));
        Assert.NotNull(put.GetHeader("Content-Range"));

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task UploadRangeAsync_200_CachesEarlyFinalisationAndFinaliseReturnsId()
    {
        var (provider, handler, _) = Build();
        var session = await BeginAsync(provider, handler, totalBytes: 10);
        handler.Setup(HttpMethod.Put, UploadUrl, _ => OneDriveCannedResponses.DriveItem(201, "item-xyz", 10, "hash-abc"));

        var range = await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);
        Assert.True(range.Success);

        var finalise = await provider.FinaliseUploadAsync(session, CancellationToken.None);
        Assert.Equal("item-xyz", finalise.AssertValue());

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task UploadRangeAsync_416_TreatedAsAlreadyReceived_ReturnsOk()
    {
        var (provider, handler, _) = Build();
        var session = await BeginAsync(provider, handler, totalBytes: 100);
        handler.Setup(HttpMethod.Put, UploadUrl, _ => OneDriveCannedResponses.Status(416));

        var result = await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);

        Assert.True(result.Success);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task UploadRangeAsync_404_MapsUploadSessionExpired()
    {
        var (provider, handler, _) = Build();
        var session = await BeginAsync(provider, handler, totalBytes: 100);
        handler.Setup(HttpMethod.Put, UploadUrl, _ => OneDriveCannedResponses.Status(404));

        var result = await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);

        Assert.Equal(ErrorCode.UploadSessionExpired, result.AssertError().Code);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task UploadRangeAsync_MalformedSessionUri_MapsUploadSessionExpired()
    {
        var (provider, _, _) = Build();
        var session = new UploadSession
        {
            SessionUri = "not-valid-json",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            BytesUploaded = 0,
            TotalBytes = 100,
        };

        var result = await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);

        Assert.Equal(ErrorCode.UploadSessionExpired, result.AssertError().Code);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task GetUploadedBytesAsync_ParsesNextExpectedRanges()
    {
        var (provider, handler, _) = Build();
        var session = await BeginAsync(provider, handler, totalBytes: 10_485_760);
        handler.Setup(HttpMethod.Get, UploadUrl, _ => OneDriveCannedResponses.UploadStatus("5242880-10485759"));

        var result = await provider.GetUploadedBytesAsync(session, CancellationToken.None);

        Assert.Equal(5_242_880L, result.AssertValue());
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task FinaliseUploadAsync_LogsServerHashAndSize_NoLocalCompare()
    {
        var (provider, handler, logger) = Build();
        var session = await BeginAsync(provider, handler, totalBytes: 10);
        handler.Setup(HttpMethod.Put, UploadUrl, _ => OneDriveCannedResponses.DriveItem(201, "item-xyz", 10, "hash-abc"));

        await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);
        var finalise = await provider.FinaliseUploadAsync(session, CancellationToken.None);

        Assert.Equal("item-xyz", finalise.AssertValue());
        Assert.True(logger.HasEntry(LogLevel.Information, "item-xyz"));
        Assert.True(logger.HasEntry(LogLevel.Information, "hash-abc"));

        // No local-hash comparison: nothing was downloaded.
        Assert.DoesNotContain(handler.ReceivedRequests, r => r.Url.AbsoluteUri.Contains("/content"));

        await provider.DisposeAsync();
    }

    // ── Abort ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AbortUploadAsync_204_ReturnsOk()
    {
        var (provider, handler, _) = Build();
        var session = await BeginAsync(provider, handler, totalBytes: 100);
        handler.Setup(HttpMethod.Delete, UploadUrl, _ => OneDriveCannedResponses.Status(204));

        var result = await provider.AbortUploadAsync(session, CancellationToken.None);

        Assert.True(result.Success);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task AbortUploadAsync_404_ReturnsOk()
    {
        var (provider, handler, _) = Build();
        var session = await BeginAsync(provider, handler, totalBytes: 100);
        handler.Setup(HttpMethod.Delete, UploadUrl, _ => OneDriveCannedResponses.Status(404));

        var result = await provider.AbortUploadAsync(session, CancellationToken.None);

        Assert.True(result.Success);
        await provider.DisposeAsync();
    }

    // ── Download / Delete / Exists ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_200_StreamsBody_DisposesResponseOnClose()
    {
        var (provider, handler, _) = Build();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        handler.Setup(HttpMethod.Get, ItemsPrefix, _ => OneDriveCannedResponses.WithBody(200, payload));

        var result = await provider.DownloadAsync("item-1", CancellationToken.None);
        var stream = result.AssertValue();

        using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms);
            Assert.Equal(payload, ms.ToArray());
        }
        stream.Dispose(); // idempotent; disposes underlying response

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task DownloadAsync_404_MapsBlobNotFound()
    {
        var (provider, handler, _) = Build();
        handler.Setup(HttpMethod.Get, ItemsPrefix, _ => OneDriveCannedResponses.Status(404));

        var result = await provider.DownloadAsync("item-1", CancellationToken.None);

        Assert.Equal(ErrorCode.BlobNotFound, result.AssertError().Code);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task DeleteAsync_404_ReturnsOk()
    {
        var (provider, handler, _) = Build();
        handler.Setup(HttpMethod.Delete, ItemsPrefix, _ => OneDriveCannedResponses.Status(404));

        var result = await provider.DeleteAsync("item-1", CancellationToken.None);

        Assert.True(result.Success);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ExistsAsync_200_True()
    {
        var (provider, handler, _) = Build();
        handler.Setup(HttpMethod.Get, ItemsPrefix, _ => OneDriveCannedResponses.Status(200));

        var result = await provider.ExistsAsync("item-1", CancellationToken.None);

        Assert.True(result.AssertValue());
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ExistsAsync_404_False()
    {
        var (provider, handler, _) = Build();
        handler.Setup(HttpMethod.Get, ItemsPrefix, _ => OneDriveCannedResponses.Status(404));

        var result = await provider.ExistsAsync("item-1", CancellationToken.None);

        Assert.False(result.AssertValue());
        await provider.DisposeAsync();
    }

    // ── List ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_FollowsNextLink_AggregatesNames()
    {
        var (provider, handler, _) = Build();
        const string nextLink = GraphRoot + "/drivenext-page";
        handler.Setup(HttpMethod.Get, CreatePrefix, _ =>
            OneDriveCannedResponses.Children(nextLink, ("f1", "blobA", false)));
        handler.Setup(HttpMethod.Get, nextLink, _ =>
            OneDriveCannedResponses.Children(null, ("f2", "blobB", false)));

        var result = await provider.ListAsync(string.Empty, CancellationToken.None);
        var list = result.AssertValue();

        Assert.Equal(2, list.Count);
        Assert.Contains("f1", list);
        Assert.Contains("f2", list);
        await provider.DisposeAsync();
    }

    // ── Health / capacity ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckHealthAsync_Reachable_ReturnsHealthy()
    {
        var (provider, handler, _) = Build();
        handler.Setup(HttpMethod.Get, DrivePrefix, _ => OneDriveCannedResponses.Drive(used: 1, total: 100));

        var result = await provider.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(ProviderHealthStatus.Healthy, result.AssertValue().Status);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task CheckHealthAsync_401_ReturnsUnhealthyTokenRefreshFailed()
    {
        var (provider, handler, _) = Build();
        handler.Setup(HttpMethod.Post, TokenEndpoint, _ => OneDriveCannedResponses.Token("fresh-token"));
        handler.Setup(HttpMethod.Get, DrivePrefix, _ => OneDriveCannedResponses.Status(401));

        var result = await provider.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(ProviderHealthStatus.AuthFailed, result.AssertValue().Status);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task GetQuotaBytesAsync_TotalAbsent_ReturnsNull()
    {
        var (provider, handler, _) = Build();
        handler.Setup(HttpMethod.Get, DrivePrefix, _ => OneDriveCannedResponses.Drive(used: 100, total: null));

        var result = await provider.GetQuotaBytesAsync(CancellationToken.None);

        Assert.Null(result.AssertValue());
        await provider.DisposeAsync();
    }

    // ── Failure mapping ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(403, "{\"error\":{\"code\":\"quotaLimitReached\"}}", ErrorCode.ProviderQuotaExceeded)]
    [InlineData(403, "{\"error\":{\"code\":\"activityLimitReached\"}}", ErrorCode.ProviderRateLimited)]
    [InlineData(403, "{\"error\":{\"code\":\"accessDenied\"}}", ErrorCode.ProviderAuthFailed)]
    [InlineData(429, "", ErrorCode.ProviderRateLimited)]
    [InlineData(507, "", ErrorCode.ProviderQuotaExceeded)]
    [InlineData(500, "", ErrorCode.UploadFailed)]
    public async Task MapHttpFailure_MapsByStatusAndBody(int status, string body, ErrorCode expected)
    {
        var (provider, handler, _) = Build();
        handler.Setup(HttpMethod.Delete, ItemsPrefix, _ =>
            body.Length == 0 ? OneDriveCannedResponses.Status(status) : OneDriveCannedResponses.WithJson(status, body));

        var result = await provider.DeleteAsync("item-1", CancellationToken.None);

        Assert.Equal(expected, result.AssertError().Code);
        await provider.DisposeAsync();
    }

    // ── Auth handler ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Auth401ThenRefresh_RetriesOnceWithNewToken()
    {
        var (provider, handler, logger) = Build();
        handler.Setup(HttpMethod.Post, TokenEndpoint, _ => OneDriveCannedResponses.Token("fresh-token"));
        // Standing setup makes the per-prefix queue eligible; the queue supplies 401 then 200 in order.
        handler.Setup(HttpMethod.Get, ItemsPrefix, _ => OneDriveCannedResponses.Status(200));
        handler.Enqueue(HttpMethod.Get, ItemsPrefix, _ => OneDriveCannedResponses.Status(401));
        handler.Enqueue(HttpMethod.Get, ItemsPrefix, _ => OneDriveCannedResponses.Status(200));

        var result = await provider.ExistsAsync("item-1", CancellationToken.None);

        Assert.True(result.AssertValue());

        // Exactly one refresh round-trip to the token endpoint.
        Assert.Equal(1, handler.ReceivedRequests.Count(r =>
            r.Method == HttpMethod.Post && r.Url.AbsoluteUri.StartsWith(TokenEndpoint, StringComparison.Ordinal)));

        // Retry carried the new token.
        var retried = handler.ReceivedRequests.Last(r => r.Method == HttpMethod.Get);
        Assert.Equal("Bearer fresh-token", retried.GetHeader("Authorization"));

        // The token never appears in any log line (Principle 26).
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("fresh-token", StringComparison.Ordinal));

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task RefreshFailure_MapsTokenRefreshFailed()
    {
        var (provider, handler, _) = Build();
        handler.Setup(HttpMethod.Post, TokenEndpoint, _ => OneDriveCannedResponses.Status(400));
        handler.Setup(HttpMethod.Get, ItemsPrefix, _ => OneDriveCannedResponses.Status(401));

        var result = await provider.ExistsAsync("item-1", CancellationToken.None);

        Assert.Equal(ErrorCode.TokenRefreshFailed, result.AssertError().Code);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task FinaliseUploadAsync_CacheMiss_ResolvesItemByPath()
    {
        var (provider, handler, logger) = Build();
        var session = await BeginAsync(provider, handler, totalBytes: 10);
        // Final range returns 202 (not 200/201) → the early-finalisation cache stays empty.
        handler.Setup(HttpMethod.Put, UploadUrl, _ => OneDriveCannedResponses.Status(202));
        // Finalisation falls back to a metadata GET by path.
        handler.Setup(HttpMethod.Get, CreatePrefix, _ => OneDriveCannedResponses.DriveItem(200, "item-fallback", 10, "hash-fb"));

        await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);
        var finalise = await provider.FinaliseUploadAsync(session, CancellationToken.None);

        Assert.Equal("item-fallback", finalise.AssertValue());
        Assert.True(logger.HasEntry(LogLevel.Information, "item-fallback"));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task GetUploadedBytesAsync_SessionGone_ReturnsZero()
    {
        var (provider, handler, _) = Build();
        var session = await BeginAsync(provider, handler, totalBytes: 100);
        handler.Setup(HttpMethod.Get, UploadUrl, _ => OneDriveCannedResponses.Status(404));

        var result = await provider.GetUploadedBytesAsync(session, CancellationToken.None);

        Assert.Equal(0L, result.AssertValue());
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ListAsync_DescendsIntoSubfolders()
    {
        var (provider, handler, _) = Build();
        // Root has one child folder "sub"; "sub" has one file. The provider must enqueue the folder
        // and recurse, collecting only file ids.
        handler.Setup(HttpMethod.Get, CreatePrefix, req =>
            (req.RequestUri?.AbsoluteUri ?? string.Empty).Contains("/sub", StringComparison.Ordinal)
                ? OneDriveCannedResponses.Children(null, ("fileid", "blobX", false))
                : OneDriveCannedResponses.Children(null, ("subid", "sub", true)));

        var result = await provider.ListAsync(string.Empty, CancellationToken.None);
        var list = result.AssertValue();

        Assert.Equal(new[] { "fileid" }, list);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task GetUsedBytesAsync_ReturnsUsedBytes()
    {
        var (provider, handler, _) = Build();
        handler.Setup(HttpMethod.Get, DrivePrefix, _ => OneDriveCannedResponses.Drive(used: 12_345, total: 99_999));

        var result = await provider.GetUsedBytesAsync(CancellationToken.None);

        Assert.Equal(12_345L, result.AssertValue());
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task GetUsedBytesAsync_UsedAbsent_ReturnsZero()
    {
        var (provider, handler, _) = Build();
        handler.Setup(HttpMethod.Get, DrivePrefix, _ => OneDriveCannedResponses.Drive(used: null, total: 100));

        var result = await provider.GetUsedBytesAsync(CancellationToken.None);

        Assert.Equal(0L, result.AssertValue());
        await provider.DisposeAsync();
    }

    // ── Disposal ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_Idempotent_SafeToCallTwice()
    {
        var (provider, _, _) = Build();
        await provider.DisposeAsync();
        await provider.DisposeAsync();
    }
}
