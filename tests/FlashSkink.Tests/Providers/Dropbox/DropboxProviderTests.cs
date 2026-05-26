using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.Dropbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers.Dropbox;

/// <summary>
/// Unit tests for <see cref="DropboxProvider"/>. PR §4.4. No real Dropbox traffic — all SDK calls
/// are intercepted by <see cref="RecordingHttpMessageHandler"/>.
/// </summary>
public sealed class DropboxProviderTests
{
    private const string RootPath = "/FlashSkink Backup";
    private const string SessionId = "sid-abc-123";
    private const string FileId = "id:abc123def";

    // SDK URL prefixes — the SDK splits API vs content endpoints.
    private const string ContentUploadStart = "https://content.dropboxapi.com/2/files/upload_session/start";
    private const string ContentUploadAppend = "https://content.dropboxapi.com/2/files/upload_session/append_v2";
    private const string ContentUploadFinish = "https://content.dropboxapi.com/2/files/upload_session/finish";
    private const string ContentDownload = "https://content.dropboxapi.com/2/files/download";
    private const string ApiGetMetadata = "https://api.dropboxapi.com/2/files/get_metadata";
    private const string ApiDeleteV2 = "https://api.dropboxapi.com/2/files/delete_v2";
    private const string ApiListFolder = "https://api.dropboxapi.com/2/files/list_folder";
    private const string ApiListFolderContinue = "https://api.dropboxapi.com/2/files/list_folder/continue";
    private const string ApiGetCurrentAccount = "https://api.dropboxapi.com/2/users/get_current_account";
    private const string ApiGetSpaceUsage = "https://api.dropboxapi.com/2/users/get_space_usage";

    private static (DropboxProvider Provider, FakeDropboxClientFactory Factory) Build(string rootPath = RootPath)
    {
        var factory = new FakeDropboxClientFactory();
        var bundleResult = factory.Create("app-key", "app-secret", "rt-xyz", NullLoggerFactory.Instance);
        Assert.True(bundleResult.Success);
        var provider = new DropboxProvider(
            "dbx-1", "Dropbox", bundleResult.Value!, rootPath,
            NullLoggerFactory.Instance.CreateLogger<DropboxProvider>());
        return (provider, factory);
    }

    private static UploadSession SessionFor(string sessionId, string remotePath, long bytesUploaded, long totalBytes)
        => new()
        {
            SessionUri = DropboxSessionUri.Encode(sessionId, remotePath),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(47),
            BytesUploaded = bytesUploaded,
            TotalBytes = totalBytes,
        };

    // ── Metadata and SessionUri envelope ──────────────────────────────────────────────────────

    [Fact]
    public void Metadata_HasExpectedValues()
    {
        var (provider, _) = Build();
        Assert.Equal("dbx-1", provider.ProviderID);
        Assert.Equal("dropbox", provider.ProviderType);
        Assert.Equal("Dropbox", provider.DisplayName);
        Assert.Equal(RootPath, provider.RootPath);
    }

    [Fact]
    public void DropboxSessionUri_RoundTrip()
    {
        var encoded = DropboxSessionUri.Encode("sid-1", "/FlashSkink Backup/blob.bin");
        Assert.True(DropboxSessionUri.TryDecode(encoded, out var decoded));
        Assert.Equal("sid-1", decoded.SessionId);
        Assert.Equal("/FlashSkink Backup/blob.bin", decoded.RemotePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("{\"sid\":\"\"}")]
    [InlineData("{\"sid\":\"a\"}")]
    [InlineData("{\"path\":\"/p\"}")]
    [InlineData("{\"sid\":\"a\",\"path\":\"\"}")]
    [InlineData("{\"sid\":\"a\",\"path\":\"   \"}")]
    [InlineData("[]")]
    public void DropboxSessionUri_TryDecode_Robustness_Theory(string? input)
    {
        Assert.False(DropboxSessionUri.TryDecode(input, out _));
    }

    [Fact]
    public void DropboxSessionUri_TryDecode_ExtraFields_AreIgnored_ForwardCompat()
    {
        var json = "{\"sid\":\"a\",\"path\":\"/p\",\"extra\":\"x\",\"v\":2}";
        Assert.True(DropboxSessionUri.TryDecode(json, out var decoded));
        Assert.Equal("a", decoded.SessionId);
        Assert.Equal("/p", decoded.RemotePath);
    }

    // ── BeginUploadAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BeginUploadAsync_HappyPath_ReturnsEncodedSessionWithExpiry()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadStart,
            _ => DropboxCannedResponses.UploadSessionStartOk(SessionId));

        var result = await provider.BeginUploadAsync("blob.bin", 1024, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(DropboxSessionUri.TryDecode(result.Value!.SessionUri, out var decoded));
        Assert.Equal(SessionId, decoded.SessionId);
        Assert.Equal($"{RootPath}/blob.bin", decoded.RemotePath);
        Assert.Equal(1024, result.Value!.TotalBytes);
        Assert.Equal(0, result.Value!.BytesUploaded);
        Assert.True(result.Value!.ExpiresAt > DateTimeOffset.UtcNow.AddHours(47));
    }

    [Fact]
    public async Task BeginUploadAsync_RemoteNameWithLeadingSlash_NotDoubledUp()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadStart,
            _ => DropboxCannedResponses.UploadSessionStartOk(SessionId));

        var result = await provider.BeginUploadAsync("/blob.bin", 100, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(DropboxSessionUri.TryDecode(result.Value!.SessionUri, out var decoded));
        Assert.Equal($"{RootPath}/blob.bin", decoded.RemotePath);
    }

    [Fact]
    public async Task BeginUploadAsync_SubPathRemoteName_BuildsCorrectDestination()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadStart,
            _ => DropboxCannedResponses.UploadSessionStartOk(SessionId));

        var result = await provider.BeginUploadAsync("_brain/2026-05.bin", 0, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(DropboxSessionUri.TryDecode(result.Value!.SessionUri, out var decoded));
        Assert.Equal($"{RootPath}/_brain/2026-05.bin", decoded.RemotePath);
    }

    [Fact]
    public async Task BeginUploadAsync_AuthFailure_ReturnsTokenRefreshFailed()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadStart,
            _ => DropboxCannedResponses.AuthExpired());

        var result = await provider.BeginUploadAsync("blob.bin", 100, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.TokenRefreshFailed, result.Error!.Code);
    }

    [Fact]
    public async Task BeginUploadAsync_5xx_ReturnsProviderUnreachable()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadStart,
            _ => DropboxCannedResponses.Status(503));

        var result = await provider.BeginUploadAsync("blob.bin", 100, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderUnreachable, result.Error!.Code);
    }

    [Fact]
    public async Task BeginUploadAsync_NetworkFailure_ReturnsProviderUnreachable()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadStart,
            _ => throw new HttpRequestException("network down"));

        var result = await provider.BeginUploadAsync("blob.bin", 100, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderUnreachable, result.Error!.Code);
    }

    [Fact]
    public async Task BeginUploadAsync_Cancelled_ReturnsCancelled()
    {
        var (provider, _) = Build();
        await using var _disp = provider;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await provider.BeginUploadAsync("blob.bin", 100, cts.Token);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Theory]
    [InlineData("", 100)]
    [InlineData("name", -1)]
    public async Task BeginUploadAsync_InvalidArgument(string remoteName, long totalBytes)
    {
        var (provider, _) = Build();
        await using var _disp = provider;
        var result = await provider.BeginUploadAsync(remoteName, totalBytes, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    // ── GetUploadedBytesAsync ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUploadedBytesAsync_ReturnsLocalState_NoNetwork()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 4096, 8192);

        var result = await provider.GetUploadedBytesAsync(session, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(4096, result.Value);
        Assert.Equal(0, factory.Handler.RequestCount);
    }

    [Fact]
    public async Task GetUploadedBytesAsync_Cancelled_ReturnsCancelled()
    {
        var (provider, _) = Build();
        await using var _disp = provider;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var session = SessionFor(SessionId, "/p/x", 0, 1);
        var result = await provider.GetUploadedBytesAsync(session, cts.Token);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    // ── UploadRangeAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadRangeAsync_HappyPath_ReturnsOk()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadAppend,
            _ => DropboxCannedResponses.UploadSessionAppendOk());

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 0, 1024);
        var data = new byte[256];
        var result = await provider.UploadRangeAsync(session, 0, data, CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task UploadRangeAsync_IncorrectOffset_ReturnsUploadSessionExpired()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadAppend,
            _ => DropboxCannedResponses.UploadSessionAppendIncorrectOffset(0));

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 100, 1024);
        var result = await provider.UploadRangeAsync(session, 100, new byte[256], CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadSessionExpired, result.Error!.Code);
    }

    [Fact]
    public async Task UploadRangeAsync_SessionNotFound_ReturnsUploadSessionExpired()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadAppend,
            _ => DropboxCannedResponses.UploadSessionAppendNotFound());

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 0, 1024);
        var result = await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadSessionExpired, result.Error!.Code);
    }

    [Fact]
    public async Task UploadRangeAsync_SessionClosed_ReturnsUploadSessionExpired()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadAppend,
            _ => DropboxCannedResponses.UploadSessionAppendClosed());

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 0, 1024);
        var result = await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadSessionExpired, result.Error!.Code);
    }

    [Fact]
    public async Task UploadRangeAsync_AuthFailure_ReturnsTokenRefreshFailed()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadAppend,
            _ => DropboxCannedResponses.AuthExpired());

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 0, 1024);
        var result = await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.TokenRefreshFailed, result.Error!.Code);
    }

    [Fact]
    public async Task UploadRangeAsync_5xx_ReturnsProviderUnreachable()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadAppend,
            _ => DropboxCannedResponses.Status(503));

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 0, 1024);
        var result = await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderUnreachable, result.Error!.Code);
    }

    [Fact]
    public async Task UploadRangeAsync_NetworkAbort_ReturnsProviderUnreachable()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadAppend,
            _ => throw new HttpRequestException("aborted"));

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 0, 1024);
        var result = await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderUnreachable, result.Error!.Code);
    }

    [Fact]
    public async Task UploadRangeAsync_Cancelled_ReturnsCancelled()
    {
        var (provider, _) = Build();
        await using var _disp = provider;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 0, 1024);
        var result = await provider.UploadRangeAsync(session, 0, new byte[10], cts.Token);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task UploadRangeAsync_MalformedSessionUri_ReturnsUploadSessionExpired()
    {
        var (provider, _) = Build();
        await using var _disp = provider;
        var session = new UploadSession
        {
            SessionUri = "not-an-envelope",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            BytesUploaded = 0,
            TotalBytes = 1024,
        };
        var result = await provider.UploadRangeAsync(session, 0, new byte[10], CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadSessionExpired, result.Error!.Code);
    }

    [Theory]
    [InlineData(-1, 1, false)]
    [InlineData(0, 0, true)]
    [InlineData(0, 2000, false)]   // exceeds totalBytes=1024
    public async Task UploadRangeAsync_InvalidArgument(long offset, int dataLen, bool dataIsEmpty)
    {
        var (provider, _) = Build();
        await using var _disp = provider;
        var data = dataIsEmpty ? Array.Empty<byte>() : new byte[dataLen];
        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 0, 1024);
        var result = await provider.UploadRangeAsync(session, offset, data, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    // ── FinaliseUploadAsync ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FinaliseUploadAsync_HappyPath_ReturnsFileId()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadFinish,
            _ => DropboxCannedResponses.UploadSessionFinishOk(
                FileId, size: 1024, contentHash: "abc", pathLower: $"{RootPath.ToLowerInvariant()}/blob.bin"));

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 1024, 1024);
        var result = await provider.FinaliseUploadAsync(session, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(FileId, result.Value);
    }

    [Fact]
    public async Task FinaliseUploadAsync_LookupFailedNotFound_ReturnsUploadSessionExpired()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadFinish,
            _ => DropboxCannedResponses.UploadSessionFinishLookupNotFound());

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 1024, 1024);
        var result = await provider.FinaliseUploadAsync(session, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadSessionExpired, result.Error!.Code);
    }

    [Fact]
    public async Task FinaliseUploadAsync_AuthFailure_ReturnsTokenRefreshFailed()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentUploadFinish,
            _ => DropboxCannedResponses.AuthExpired());

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 1024, 1024);
        var result = await provider.FinaliseUploadAsync(session, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.TokenRefreshFailed, result.Error!.Code);
    }

    [Fact]
    public async Task FinaliseUploadAsync_MalformedSessionUri_ReturnsUploadSessionExpired()
    {
        var (provider, _) = Build();
        await using var _disp = provider;
        var session = new UploadSession
        {
            SessionUri = "{not json",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            BytesUploaded = 1,
            TotalBytes = 1,
        };
        var result = await provider.FinaliseUploadAsync(session, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.UploadSessionExpired, result.Error!.Code);
    }

    [Fact]
    public async Task FinaliseUploadAsync_Cancelled_ReturnsCancelled()
    {
        var (provider, _) = Build();
        await using var _disp = provider;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 1, 1);
        var result = await provider.FinaliseUploadAsync(session, cts.Token);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    // ── AbortUploadAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AbortUploadAsync_NoOp_ReturnsOk_NoNetwork()
    {
        var (provider, factory) = Build();
        await using var _ = provider;

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 0, 1024);
        var result = await provider.AbortUploadAsync(session, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(0, factory.Handler.RequestCount);
    }

    [Fact]
    public async Task AbortUploadAsync_Cancelled_ReturnsCancelled()
    {
        var (provider, _) = Build();
        await using var _disp = provider;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var session = SessionFor(SessionId, $"{RootPath}/blob.bin", 0, 1024);
        var result = await provider.AbortUploadAsync(session, cts.Token);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    // ── DownloadAsync ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_HappyPath_ReturnsStreamWithBytes()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        var body = new byte[] { 1, 2, 3, 4, 5 };
        factory.Handler.Setup(HttpMethod.Post, ContentDownload,
            _ => DropboxCannedResponses.DownloadOk(FileId, $"{RootPath.ToLowerInvariant()}/blob.bin", body));

        var result = await provider.DownloadAsync(FileId, CancellationToken.None);
        Assert.True(result.Success);
        using var ms = new MemoryStream();
        await result.Value!.CopyToAsync(ms);
        Assert.Equal(body, ms.ToArray());
        result.Value!.Dispose();
    }

    [Fact]
    public async Task DownloadAsync_NotFound_ReturnsBlobNotFound()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentDownload,
            _ => DropboxCannedResponses.DownloadNotFound());

        var result = await provider.DownloadAsync(FileId, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.BlobNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task DownloadAsync_AuthFailure_ReturnsTokenRefreshFailed()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ContentDownload,
            _ => DropboxCannedResponses.AuthExpired());

        var result = await provider.DownloadAsync(FileId, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.TokenRefreshFailed, result.Error!.Code);
    }

    [Fact]
    public async Task DownloadAsync_EmptyRemoteId_ReturnsInvalidArgument()
    {
        var (provider, _) = Build();
        await using var _disp = provider;
        var result = await provider.DownloadAsync("", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_HappyPath_ReturnsOk()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiDeleteV2,
            _ => DropboxCannedResponses.DeleteV2Ok(FileId, $"{RootPath.ToLowerInvariant()}/blob.bin"));

        var result = await provider.DeleteAsync(FileId, CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task DeleteAsync_NotFound_Idempotent_ReturnsOk()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiDeleteV2,
            _ => DropboxCannedResponses.DeleteV2NotFound());

        var result = await provider.DeleteAsync(FileId, CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task DeleteAsync_AuthFailure_ReturnsTokenRefreshFailed()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiDeleteV2,
            _ => DropboxCannedResponses.AuthExpired());

        var result = await provider.DeleteAsync(FileId, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.TokenRefreshFailed, result.Error!.Code);
    }

    // ── ExistsAsync ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistsAsync_Exists_ReturnsTrue()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiGetMetadata,
            _ => DropboxCannedResponses.GetMetadataFileOk(FileId, $"{RootPath.ToLowerInvariant()}/blob.bin", 100));

        var result = await provider.ExistsAsync(FileId, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task ExistsAsync_NotFound_ReturnsFalse()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiGetMetadata,
            _ => DropboxCannedResponses.GetMetadataNotFound());

        var result = await provider.ExistsAsync(FileId, CancellationToken.None);
        Assert.True(result.Success);
        Assert.False(result.Value);
    }

    // ── ListAsync ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_EmptyPrefix_ReturnsAllFiles()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiListFolder,
            _ => DropboxCannedResponses.ListFolderOk(
                new[]
                {
                    ("id:a1", $"{RootPath}/blob1.bin"),
                    ("id:a2", $"{RootPath}/_brain/2026-05.bin"),
                }));

        var result = await provider.ListAsync(string.Empty, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(2, result.Value!.Count);
        Assert.Contains("id:a1", result.Value);
        Assert.Contains("id:a2", result.Value);
    }

    [Fact]
    public async Task ListAsync_PrefixMatchesSubfolder_FiltersResults()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiListFolder,
            _ => DropboxCannedResponses.ListFolderOk(
                new[]
                {
                    ("id:a1", $"{RootPath}/blob1.bin"),
                    ("id:a2", $"{RootPath}/_brain/2026-05.bin"),
                }));

        var result = await provider.ListAsync("/_brain", CancellationToken.None);
        Assert.True(result.Success);
        Assert.Single(result.Value!);
        Assert.Equal("id:a2", result.Value![0]);
    }

    [Fact]
    public async Task ListAsync_RootNotFound_ReturnsEmpty()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiListFolder,
            _ => DropboxCannedResponses.ListFolderNotFound());

        var result = await provider.ListAsync(string.Empty, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task ListAsync_Pagination_FollowsCursor()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiListFolder,
            _ => DropboxCannedResponses.ListFolderOk(
                new[] { ("id:p1", $"{RootPath}/a.bin") },
                hasMore: true, cursor: "cur-1"));
        factory.Handler.Setup(HttpMethod.Post, ApiListFolderContinue,
            _ => DropboxCannedResponses.ListFolderOk(
                new[] { ("id:p2", $"{RootPath}/b.bin") }));

        var result = await provider.ListAsync(string.Empty, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(2, result.Value!.Count);
        Assert.Contains("id:p1", result.Value);
        Assert.Contains("id:p2", result.Value);
    }

    // ── CheckHealthAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckHealthAsync_HappyPath_ReturnsHealthy()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiGetCurrentAccount,
            _ => DropboxCannedResponses.GetCurrentAccountOk());

        var result = await provider.CheckHealthAsync(CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(ProviderHealthStatus.Healthy, result.Value!.Status);
        Assert.NotNull(result.Value!.RoundTripLatency);
    }

    [Fact]
    public async Task CheckHealthAsync_AuthFailure_ReturnsAuthFailed()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiGetCurrentAccount,
            _ => DropboxCannedResponses.AuthExpired());

        var result = await provider.CheckHealthAsync(CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(ProviderHealthStatus.AuthFailed, result.Value!.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_Network_ReturnsUnreachable()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiGetCurrentAccount,
            _ => throw new HttpRequestException("down"));

        var result = await provider.CheckHealthAsync(CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(ProviderHealthStatus.Unreachable, result.Value!.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_5xx_ReturnsUnreachable()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiGetCurrentAccount,
            _ => DropboxCannedResponses.Status(503));

        var result = await provider.CheckHealthAsync(CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(ProviderHealthStatus.Unreachable, result.Value!.Status);
    }

    // ── GetUsedBytesAsync / GetQuotaBytesAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetUsedBytesAsync_ReadsSpaceUsageUsed()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiGetSpaceUsage,
            _ => DropboxCannedResponses.GetSpaceUsageIndividualOk(used: 12345, allocated: 99999));

        var result = await provider.GetUsedBytesAsync(CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(12345L, result.Value);
    }

    [Fact]
    public async Task GetQuotaBytesAsync_IndividualAllocation_ReturnsAllocated()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiGetSpaceUsage,
            _ => DropboxCannedResponses.GetSpaceUsageIndividualOk(used: 1, allocated: 88888));

        var result = await provider.GetQuotaBytesAsync(CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(88888L, result.Value);
    }

    [Fact]
    public async Task GetQuotaBytesAsync_AuthFailure_ReturnsTokenRefreshFailed()
    {
        var (provider, factory) = Build();
        await using var _ = provider;
        factory.Handler.Setup(HttpMethod.Post, ApiGetSpaceUsage,
            _ => DropboxCannedResponses.AuthExpired());

        var result = await provider.GetQuotaBytesAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.TokenRefreshFailed, result.Error!.Code);
    }

    // ── DisposeAsync ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var (provider, _) = Build();
        await provider.DisposeAsync();
        await provider.DisposeAsync(); // must not throw
    }

    // ── Capability ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Provider_DoesNotImplement_ISupportsRemoteHashCheck()
    {
        // Discrepancy 1 — Dropbox's content-hash is SHA-256-based, not XXHash64 compatible.
        // Phase 5 cross-tail verification will design the capability across all providers.
        // Cast through object so the compiler doesn't fold this to a const false (the assertion
        // is intentional — we want it to fail loudly if a future PR adds the capability without
        // updating the plan).
        var (provider, _) = Build();
        Assert.False((object)provider is ISupportsRemoteHashCheck);
    }
}
