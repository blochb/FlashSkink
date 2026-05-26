using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers.Setup;

/// <summary>
/// Unit tests for <see cref="FileSystemProviderSetup"/>. PR §4.1.
/// </summary>
public sealed class FileSystemProviderSetupTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _skinkRoot;
    private readonly FileSystemProviderSetup _sut;

    public FileSystemProviderSetupTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "flashskink-fs-setup-tests", Guid.NewGuid().ToString("N"));
        _skinkRoot = Path.Combine(_tempRoot, "skink");
        Directory.CreateDirectory(_skinkRoot);
        _sut = new FileSystemProviderSetup(NullLoggerFactory.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private string MakeTailDir(string suffix)
    {
        var p = Path.Combine(_tempRoot, suffix);
        Directory.CreateDirectory(p);
        return p;
    }

    // ── Static metadata ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Metadata_HasExpectedValues()
    {
        Assert.Equal("filesystem", _sut.ProviderType);
        Assert.Equal("Local folder", _sut.DisplayName);
        Assert.Equal(ProviderSetupKind.LocalPath, _sut.SetupKind);
    }

    // ── ValidatePathAsync ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidatePathAsync_ValidWritableDirectory_ReturnsValid()
    {
        var tail = MakeTailDir("tail-1");

        var result = await _sut.ValidatePathAsync(tail, _skinkRoot, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.AssertValue().IsValid);
        Assert.Null(result.AssertValue().Reason);
    }

    [Fact]
    public async Task ValidatePathAsync_NonExistentPath_ReturnsInvalid()
    {
        var missing = Path.Combine(_tempRoot, "no-such-dir");

        var result = await _sut.ValidatePathAsync(missing, _skinkRoot, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.AssertValue().IsValid);
        Assert.Contains("does not exist", result.AssertValue().Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidatePathAsync_EmptyPath_ReturnsInvalid()
    {
        var result = await _sut.ValidatePathAsync(string.Empty, _skinkRoot, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.AssertValue().IsValid);
        Assert.Contains("empty", result.AssertValue().Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidatePathAsync_PathIsSkinkRoot_ReturnsInvalid()
    {
        var result = await _sut.ValidatePathAsync(_skinkRoot, _skinkRoot, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.AssertValue().IsValid);
        Assert.Contains("skink", result.AssertValue().Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidatePathAsync_PathIsSubdirectoryOfSkink_ReturnsInvalid()
    {
        var inside = Path.Combine(_skinkRoot, "inside");
        Directory.CreateDirectory(inside);

        var result = await _sut.ValidatePathAsync(inside, _skinkRoot, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.AssertValue().IsValid);
        Assert.Contains("inside the skink", result.AssertValue().Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidatePathAsync_PathParallelToSkink_ReturnsValid()
    {
        var tail = MakeTailDir("parallel-tail");

        var result = await _sut.ValidatePathAsync(tail, _skinkRoot, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.AssertValue().IsValid);
    }

    [Fact]
    public async Task ValidatePathAsync_PrefixMatchButNotSubdirectory_ReturnsValid()
    {
        // skinkRoot is `[temp]/skink`. Create a sibling named `skink-backup` whose path
        // starts with the same first 5 chars but is not a subdirectory.
        var sibling = Path.Combine(_tempRoot, "skink-backup");
        Directory.CreateDirectory(sibling);

        var result = await _sut.ValidatePathAsync(sibling, _skinkRoot, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.AssertValue().IsValid);
    }

    [Fact]
    public async Task ValidatePathAsync_CancellationRequested_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _sut.ValidatePathAsync(_skinkRoot, _skinkRoot, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.AssertError().Code);
    }

    // ── OAuth methods ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAuthorizationUriAsync_ReturnsInvalidArgument()
    {
        var result = await _sut.GetAuthorizationUriAsync(
            "http://127.0.0.1:1234/oauth-callback",
            "challenge",
            new ProviderCredentials { ClientId = "id", ClientSecret = "secret" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }

    [Fact]
    public async Task ExchangeCodeAsync_ReturnsInvalidArgument()
    {
        var result = await _sut.ExchangeCodeAsync(
            "code",
            "verifier",
            "http://127.0.0.1:1234/oauth-callback",
            new ProviderCredentials { ClientId = "id", ClientSecret = "secret" },
            new ReadOnlyMemory<byte>(new byte[32]),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }

    // ── CreateProviderAsync ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProviderAsync_NullProviderConfig_ReturnsInvalidArgument()
    {
        var result = await _sut.CreateProviderAsync(
            "p1", "Test", Array.Empty<byte>(),
            new ProviderCredentials(), providerConfigJson: null,
            new ReadOnlyMemory<byte>(new byte[32]), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }

    [Fact]
    public async Task CreateProviderAsync_MalformedJson_ReturnsInvalidArgument()
    {
        var result = await _sut.CreateProviderAsync(
            "p1", "Test", Array.Empty<byte>(),
            new ProviderCredentials(), providerConfigJson: "{not valid json",
            new ReadOnlyMemory<byte>(new byte[32]), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }

    [Fact]
    public async Task CreateProviderAsync_EmptyRootPath_ReturnsInvalidArgument()
    {
        var result = await _sut.CreateProviderAsync(
            "p1", "Test", Array.Empty<byte>(),
            new ProviderCredentials(), providerConfigJson: """{"rootPath":""}""",
            new ReadOnlyMemory<byte>(new byte[32]), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }

    [Fact]
    public async Task CreateProviderAsync_ValidConfig_ReturnsFileSystemProvider()
    {
        var tail = MakeTailDir("create-target");
        var configJson = $$"""{"rootPath":"{{tail.Replace("\\", "\\\\")}}"}""";

        var result = await _sut.CreateProviderAsync(
            "p-created", "Created Tail", Array.Empty<byte>(),
            new ProviderCredentials(), providerConfigJson: configJson,
            new ReadOnlyMemory<byte>(new byte[32]), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal("p-created", result.AssertValue().ProviderID);
        Assert.Equal("Created Tail", result.AssertValue().DisplayName);
        Assert.Equal("filesystem", result.AssertValue().ProviderType);
    }

    [Fact]
    public async Task CreateProviderAsync_EmptyProviderId_ReturnsInvalidArgument()
    {
        var tail = MakeTailDir("empty-id");
        var configJson = $$"""{"rootPath":"{{tail.Replace("\\", "\\\\")}}"}""";

        var result = await _sut.CreateProviderAsync(
            string.Empty, "Test", Array.Empty<byte>(),
            new ProviderCredentials(), providerConfigJson: configJson,
            new ReadOnlyMemory<byte>(new byte[32]), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.AssertError().Code);
    }
}
