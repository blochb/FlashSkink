using System.Net;
using System.Text;
using System.Text.Json;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.GoogleDrive;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers.GoogleDrive;

/// <summary>
/// Unit tests for <see cref="GoogleDriveProvider"/>. PR §4.3.
/// All HTTP traffic is intercepted by <see cref="RecordingHttpMessageHandler"/>; no real Drive calls.
/// </summary>
public sealed class GoogleDriveProviderTests
{
    private const string FolderId = "folder-fake-id";
    private const string SessionUri =
        "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=abc";

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static (GoogleDriveProvider Provider, FakeGoogleDriveClientFactory Factory) Build()
    {
        var factory = new FakeGoogleDriveClientFactory();
        var bundleResult = factory.Create("cid", "csec", "rtok", NullLoggerFactory.Instance);
        Assert.True(bundleResult.Success);
        var provider = new GoogleDriveProvider(
            "drive-1", "Google Drive", bundleResult.Value!, FolderId,
            NullLoggerFactory.Instance.CreateLogger<GoogleDriveProvider>());
        return (provider, factory);
    }

    private static UploadSession SessionAt(long bytesUploaded, long totalBytes) => new()
    {
        SessionUri = SessionUri,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        BytesUploaded = bytesUploaded,
        TotalBytes = totalBytes,
    };

    // ── BeginUploadAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BeginUploadAsync_HappyPath_ReturnsSessionWithLocation()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files",
            _ => CannedResponses.ResumableInit(SessionUri));

        var result = await provider.BeginUploadAsync("blob.bin", 1024, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SessionUri, result.Value!.SessionUri);
        Assert.Equal(1024, result.Value!.TotalBytes);
        Assert.Equal(0, result.Value!.BytesUploaded);
        Assert.True(result.Value!.ExpiresAt > DateTimeOffset.UtcNow.AddDays(6));
    }

    [Fact]
    public async Task BeginUploadAsync_RequestIncludesFolderIdAsParent()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files",
            _ => CannedResponses.ResumableInit(SessionUri));

        var result = await provider.BeginUploadAsync("blob.bin", 1024, CancellationToken.None);
        Assert.True(result.Success);

        var req = factory.ResumableHandler.ReceivedRequests.Single();
        Assert.Contains($"\"parents\":[\"{FolderId}\"]", req.BodyString);
    }

    [Fact]
    public async Task BeginUploadAsync_RequestUrlAndHeadersAreCorrect()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files",
            _ => CannedResponses.ResumableInit(SessionUri));

        var result = await provider.BeginUploadAsync("blob.bin", 4096, CancellationToken.None);
        Assert.True(result.Success);

        var req = factory.ResumableHandler.ReceivedRequests.Single();
        Assert.Equal("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable",
            req.Url.AbsoluteUri);
        Assert.Equal("4096", req.GetHeader("X-Upload-Content-Length"));
        Assert.Equal("application/octet-stream", req.GetHeader("X-Upload-Content-Type"));
    }

    [Fact]
    public async Task BeginUploadAsync_FlattenedRemoteName_UsesUnderscoreForSlash()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files",
            _ => CannedResponses.ResumableInit(SessionUri));

        var result = await provider.BeginUploadAsync("_brain/ts.bin", 100, CancellationToken.None);
        Assert.True(result.Success);

        var body = factory.ResumableHandler.ReceivedRequests.Single().BodyString!;
        Assert.Contains("\"name\":\"_brain_ts.bin\"", body);
        Assert.DoesNotContain("/", body[(body.IndexOf("name", StringComparison.Ordinal))..(body.IndexOf("parents", StringComparison.Ordinal))]);
    }

    [Fact]
    public async Task BeginUploadAsync_NoLocationHeader_ReturnsProviderApiChanged()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files",
            _ => CannedResponses.Status(200));

        var result = await provider.BeginUploadAsync("blob.bin", 1024, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderApiChanged, result.Error!.Code);
    }

    [Fact]
    public async Task BeginUploadAsync_403StorageQuotaExceeded_ReturnsProviderQuotaExceeded()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files",
            _ => CannedResponses.WithJson(403, "{\"error\":{\"errors\":[{\"reason\":\"storageQuotaExceeded\"}]}}"));

        var result = await provider.BeginUploadAsync("blob.bin", 1024, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderQuotaExceeded, result.Error!.Code);
    }

    [Fact]
    public async Task BeginUploadAsync_500_ReturnsUploadFailed()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files",
            _ => CannedResponses.Status(500));

        var result = await provider.BeginUploadAsync("blob.bin", 1024, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadFailed, result.Error!.Code);
    }

    [Fact]
    public async Task BeginUploadAsync_Cancelled_ReturnsCancelled()
    {
        var (provider, _) = Build();
        await using var _p = provider;

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await provider.BeginUploadAsync("blob.bin", 1024, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task BeginUploadAsync_EmptyRemoteName_ReturnsInvalidArgument()
    {
        var (provider, _) = Build();
        await using var _p = provider;

        var result = await provider.BeginUploadAsync("", 1024, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task BeginUploadAsync_NegativeTotalBytes_ReturnsInvalidArgument()
    {
        var (provider, _) = Build();
        await using var _p = provider;

        var result = await provider.BeginUploadAsync("blob.bin", -1, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    // ── UploadRangeAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadRangeAsync_308_ReturnsOk()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.Range308(4095));

        var data = new byte[4096];
        var result = await provider.UploadRangeAsync(SessionAt(0, 8192), 0, data, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task UploadRangeAsync_200_CachesEarlyFinalisation()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.WithJson(200, "{\"id\":\"file-final-id\",\"md5Checksum\":\"abc123\"}"));

        var data = new byte[1024];
        var range = await provider.UploadRangeAsync(SessionAt(0, 1024), 0, data, CancellationToken.None);
        Assert.True(range.Success);

        // FinaliseUploadAsync should short-circuit using the cached id with no additional HTTP call.
        var beforeCount = factory.ResumableHandler.RequestCount;
        var fin = await provider.FinaliseUploadAsync(SessionAt(1024, 1024), CancellationToken.None);
        Assert.True(fin.Success);
        Assert.Equal("file-final-id", fin.Value);
        Assert.Equal(beforeCount, factory.ResumableHandler.RequestCount);
    }

    [Fact]
    public async Task UploadRangeAsync_404_ReturnsUploadSessionExpired()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.Status(404));

        var result = await provider.UploadRangeAsync(SessionAt(0, 1024), 0, new byte[100], CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadSessionExpired, result.Error!.Code);
    }

    [Fact]
    public async Task UploadRangeAsync_410_ReturnsUploadSessionExpired()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.Status(410));

        var result = await provider.UploadRangeAsync(SessionAt(0, 1024), 0, new byte[100], CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadSessionExpired, result.Error!.Code);
    }

    [Fact]
    public async Task UploadRangeAsync_400InvalidRange_ReturnsUploadSessionExpired()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.WithJson(400, "{\"error\":{\"message\":\"Invalid range\"}}"));

        var result = await provider.UploadRangeAsync(SessionAt(0, 1024), 0, new byte[100], CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadSessionExpired, result.Error!.Code);
    }

    [Fact]
    public async Task UploadRangeAsync_500_ReturnsUploadFailed_BodyCapturedInMetadata()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.WithJson(500, "{\"error\":\"internal\"}"));

        var result = await provider.UploadRangeAsync(SessionAt(0, 1024), 0, new byte[100], CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadFailed, result.Error!.Code);
        Assert.NotNull(result.Error.Metadata);
        Assert.Contains("responseBody", result.Error.Metadata!.Keys);
    }

    [Fact]
    public async Task UploadRangeAsync_RequestContainsContentRangeHeader()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.Range308(4095));

        var data = new byte[4096];
        await provider.UploadRangeAsync(SessionAt(0, 8192), 0, data, CancellationToken.None);

        var req = factory.ResumableHandler.ReceivedRequests.Single();
        Assert.Equal("bytes 0-4095/8192", req.GetHeader("Content-Range"));
    }

    [Fact]
    public async Task UploadRangeAsync_RequestBodyMatchesData()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.Range308(99));

        var data = new byte[100];
        for (var i = 0; i < 100; i++)
        {
            data[i] = (byte)i;
        }
        await provider.UploadRangeAsync(SessionAt(0, 200), 0, data, CancellationToken.None);

        var req = factory.ResumableHandler.ReceivedRequests.Single();
        Assert.Equal(data, req.BodyBytes);
    }

    [Fact]
    public async Task UploadRangeAsync_OutOfRange_ReturnsInvalidArgument()
    {
        var (provider, _) = Build();
        await using var _p = provider;

        var result = await provider.UploadRangeAsync(
            SessionAt(0, 1024), 1000, new byte[100], CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    // ── GetUploadedBytesAsync ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUploadedBytesAsync_308_ParsesRangeHeader()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.Range308(4095));

        var result = await provider.GetUploadedBytesAsync(SessionAt(0, 8192), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(4096, result.Value);
    }

    [Fact]
    public async Task GetUploadedBytesAsync_308NoRangeHeader_ReturnsZero()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.Empty308());

        var result = await provider.GetUploadedBytesAsync(SessionAt(0, 8192), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public async Task GetUploadedBytesAsync_200_ReturnsTotalBytes()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.Status(200));

        var result = await provider.GetUploadedBytesAsync(SessionAt(0, 8192), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(8192, result.Value);
    }

    [Fact]
    public async Task GetUploadedBytesAsync_404_ReturnsUploadSessionExpired()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.Status(404));

        var result = await provider.GetUploadedBytesAsync(SessionAt(0, 8192), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadSessionExpired, result.Error!.Code);
    }

    // ── FinaliseUploadAsync ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FinaliseUploadAsync_AfterAllRanges_QueriesAndReturnsId()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.WithJson(200, "{\"id\":\"final-id\",\"md5Checksum\":\"deadbeef\"}"));

        var result = await provider.FinaliseUploadAsync(SessionAt(1024, 1024), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("final-id", result.Value);
    }

    [Fact]
    public async Task FinaliseUploadAsync_StillIncomplete_ReturnsUploadFailed()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.Range308(511));

        var result = await provider.FinaliseUploadAsync(SessionAt(1024, 1024), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadFailed, result.Error!.Code);
    }

    [Fact]
    public async Task FinaliseUploadAsync_NoFileId_ReturnsProviderApiChanged()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Put, SessionUri,
            _ => CannedResponses.WithJson(200, "{\"md5Checksum\":\"deadbeef\"}"));

        var result = await provider.FinaliseUploadAsync(SessionAt(1024, 1024), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderApiChanged, result.Error!.Code);
    }

    // ── AbortUploadAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AbortUploadAsync_204_ReturnsOk()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Delete, SessionUri,
            _ => CannedResponses.Status(204));

        var result = await provider.AbortUploadAsync(SessionAt(0, 1024), CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task AbortUploadAsync_404_ReturnsOk()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Delete, SessionUri,
            _ => CannedResponses.Status(404));

        var result = await provider.AbortUploadAsync(SessionAt(0, 1024), CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task AbortUploadAsync_500_SwallowsAndReturnsOk()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Delete, SessionUri,
            _ => CannedResponses.Status(500));

        var result = await provider.AbortUploadAsync(SessionAt(0, 1024), CancellationToken.None);
        Assert.True(result.Success);
    }

    // ── DownloadAsync ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_HappyPath_ReturnsStream()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        var body = Encoding.UTF8.GetBytes("hello world");
        factory.ResumableHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/files/",
            _ => CannedResponses.WithBody(200, body));

        var result = await provider.DownloadAsync("rid-1", CancellationToken.None);

        Assert.True(result.Success);
        using var ms = new MemoryStream();
        await result.Value!.CopyToAsync(ms);
        Assert.Equal(body, ms.ToArray());
        result.Value.Dispose();
    }

    [Fact]
    public async Task DownloadAsync_404_ReturnsBlobNotFound()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.ResumableHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/files/",
            _ => CannedResponses.Status(404));

        var result = await provider.DownloadAsync("rid-1", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.BlobNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task DownloadAsync_EmptyRemoteId_ReturnsInvalidArgument()
    {
        var (provider, _) = Build();
        await using var _p = provider;

        var result = await provider.DownloadAsync("", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_404_TreatsAsSuccess()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.SdkHandler.Setup(HttpMethod.Delete,
            "https://www.googleapis.com/drive/v3/files/",
            _ => CannedResponses.Status(404));

        var result = await provider.DeleteAsync("rid-1", CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task DeleteAsync_HappyPath_ReturnsOk()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.SdkHandler.Setup(HttpMethod.Delete,
            "https://www.googleapis.com/drive/v3/files/",
            _ => CannedResponses.Status(204));

        var result = await provider.DeleteAsync("rid-1", CancellationToken.None);
        Assert.True(result.Success);
    }

    // ── ExistsAsync ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistsAsync_HappyPath_ReturnsTrue()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.SdkHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/files/",
            _ => CannedResponses.WithJson(200, "{\"id\":\"rid-1\"}"));

        var result = await provider.ExistsAsync("rid-1", CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task ExistsAsync_404_ReturnsFalse()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.SdkHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/files/",
            _ => CannedResponses.Status(404));

        var result = await provider.ExistsAsync("rid-1", CancellationToken.None);
        Assert.True(result.Success);
        Assert.False(result.Value);
    }

    // ── ListAsync ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_PostFiltersStartsWith_AfterSdkSubstringQuery()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        // SDK returns 3 results; only 2 start with "_brain_" — the third is a substring match elsewhere.
        var responseJson = """
            {
                "files": [
                    {"id":"id-a","name":"_brain_2026.bin"},
                    {"id":"id-b","name":"_brain_2025.bin"},
                    {"id":"id-c","name":"other_brain_x.bin"}
                ]
            }
            """;

        factory.SdkHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/files",
            _ => CannedResponses.WithJson(200, responseJson));

        var result = await provider.ListAsync("_brain/", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Value!.Count);
        Assert.Contains("id-a", result.Value);
        Assert.Contains("id-b", result.Value);
        Assert.DoesNotContain("id-c", result.Value);
    }

    [Fact]
    public async Task ListAsync_EmptyPrefix_ReturnsAll()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        var responseJson = """
            {
                "files": [
                    {"id":"id-a","name":"a.bin"},
                    {"id":"id-b","name":"b.bin"}
                ]
            }
            """;

        factory.SdkHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/files",
            _ => CannedResponses.WithJson(200, responseJson));

        var result = await provider.ListAsync("", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Value!.Count);
    }

    // ── CheckHealthAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckHealthAsync_HappyPath_ReturnsHealthy()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.SdkHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/about",
            _ => CannedResponses.WithJson(200, "{\"user\":{\"emailAddress\":\"t@example.com\"}}"));

        var result = await provider.CheckHealthAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(ProviderHealthStatus.Healthy, result.Value!.Status);
        Assert.NotNull(result.Value.RoundTripLatency);
    }

    [Fact]
    public async Task CheckHealthAsync_NetworkFailure_ReturnsUnreachableInsideOk()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.SdkHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/about",
            _ => throw new HttpRequestException("boom"));

        var result = await provider.CheckHealthAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(ProviderHealthStatus.Unreachable, result.Value!.Status);
    }

    // ── Used / Quota ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUsedBytesAsync_ParsesStorageQuotaUsage()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.SdkHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/about",
            _ => CannedResponses.WithJson(200, "{\"storageQuota\":{\"usage\":\"12345\",\"limit\":\"99999\"}}"));

        var result = await provider.GetUsedBytesAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(12345, result.Value);
    }

    [Fact]
    public async Task GetQuotaBytesAsync_ParsesStorageQuotaLimit()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.SdkHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/about",
            _ => CannedResponses.WithJson(200, "{\"storageQuota\":{\"usage\":\"1\",\"limit\":\"99999\"}}"));

        var result = await provider.GetQuotaBytesAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(99999L, result.Value);
    }

    [Fact]
    public async Task GetQuotaBytesAsync_NoLimit_ReturnsNullValue()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        factory.SdkHandler.Setup(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/about",
            _ => CannedResponses.WithJson(200, "{\"storageQuota\":{\"usage\":\"1\"}}"));

        var result = await provider.GetQuotaBytesAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Value);
    }

    // ── Capability + metadata ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GoogleDriveProvider_DoesNotImplementISupportsRemoteHashCheck()
    {
        var (provider, _) = Build();
        await using var _p = provider;

        // Use reflection rather than `is` — the compiler proves the static type doesn't
        // implement the interface (CS0184), but the test's purpose is to capture that fact
        // explicitly so a future revision that adds the capability triggers a deliberate
        // re-think (see Gate-1 resolution #1 in `.claude/plans/pr-4.3.md`).
        var implements = typeof(ISupportsRemoteHashCheck)
            .IsAssignableFrom(provider.GetType());
        Assert.False(implements);
    }

    [Fact]
    public async Task FolderId_IsExposedInternally_FromConstructor()
    {
        var (provider, _) = Build();
        await using var _p = provider;

        Assert.Equal(FolderId, provider.FolderId);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var (provider, _) = Build();
        await provider.DisposeAsync();
        await provider.DisposeAsync(); // must not throw
    }

    // ── Static helpers ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("blob.bin", "blob.bin")]
    [InlineData("_brain/ts.bin", "_brain_ts.bin")]
    [InlineData("blobs/ab/cd/x.bin", "blobs_ab_cd_x.bin")]
    [InlineData("_witness/current.enc", "_witness_current.enc")]
    public void FlattenRemoteName_ReplacesSlashesWithUnderscores(string input, string expected)
    {
        Assert.Equal(expected, GoogleDriveProvider.FlattenRemoteName(input));
    }
}
