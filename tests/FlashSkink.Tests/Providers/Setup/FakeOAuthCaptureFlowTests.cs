using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Tests._TestSupport;
using Xunit;

namespace FlashSkink.Tests.Providers.Setup;

/// <summary>Sanity tests for the <see cref="FakeOAuthCaptureFlow"/> test double itself.</summary>
public sealed class FakeOAuthCaptureFlowTests
{
    [Fact]
    public void Prepare_ReturnsDefaultContext_WhenNoOverride()
    {
        var fake = new FakeOAuthCaptureFlow();

        var result = fake.Prepare();

        Assert.True(result.Success);
        Assert.Equal("http://127.0.0.1:65000/oauth-callback/", result.Value!.RedirectUri);
        Assert.NotEmpty(result.Value.CodeChallenge);
        Assert.NotEmpty(result.Value.CodeVerifier);
    }

    [Fact]
    public void Prepare_HonoursOverride_WhenSet()
    {
        var fake = new FakeOAuthCaptureFlow
        {
            PrepareResult = Result<OAuthCaptureContext>.Fail(ErrorCode.Unknown, "boom"),
        };

        var result = fake.Prepare();

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Unknown, result.Error!.Code);
    }

    [Fact]
    public void Prepare_IncrementsCallCount()
    {
        var fake = new FakeOAuthCaptureFlow();

        fake.Prepare();
        fake.Prepare();
        fake.Prepare();

        Assert.Equal(3, fake.PrepareCallCount);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_ReturnsDefaultCode_WhenNoOverride()
    {
        var fake = new FakeOAuthCaptureFlow();
        var context = fake.Prepare().Value!;

        var result = await fake.AwaitAuthorizationCodeAsync(
            context, new Uri("https://example.com/auth"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("test-authorization-code", result.Value);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_HonoursOverride_WhenSet()
    {
        var fake = new FakeOAuthCaptureFlow
        {
            AwaitResult = Result<string>.Ok("custom-code-xyz"),
        };
        var context = fake.Prepare().Value!;

        var result = await fake.AwaitAuthorizationCodeAsync(
            context, new Uri("https://example.com/auth"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("custom-code-xyz", result.Value);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_CapturesAuthorizationUri()
    {
        var fake = new FakeOAuthCaptureFlow();
        var context = fake.Prepare().Value!;
        var uri = new Uri("https://accounts.google.com/o/oauth2/v2/auth?client_id=123");

        await fake.AwaitAuthorizationCodeAsync(context, uri, CancellationToken.None);

        Assert.Equal(uri, fake.CapturedAuthorizationUri);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_IncrementsCallCount()
    {
        var fake = new FakeOAuthCaptureFlow();
        var context = fake.Prepare().Value!;

        await fake.AwaitAuthorizationCodeAsync(context, new Uri("https://example.com/"), CancellationToken.None);
        await fake.AwaitAuthorizationCodeAsync(context, new Uri("https://example.com/"), CancellationToken.None);

        Assert.Equal(2, fake.AwaitCallCount);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_CancelledToken_ReturnsCancelled()
    {
        var fake = new FakeOAuthCaptureFlow();
        var context = fake.Prepare().Value!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await fake.AwaitAuthorizationCodeAsync(
            context, new Uri("https://example.com/auth"), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }
}
