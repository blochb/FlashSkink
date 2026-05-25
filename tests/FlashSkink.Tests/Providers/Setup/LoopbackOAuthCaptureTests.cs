using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers.Setup;

/// <summary>
/// Integration tests for <see cref="LoopbackOAuthCapture"/>. Each happy-path test simulates the
/// browser by issuing an HttpClient GET to the listener's redirect URI, replacing the role a
/// real browser would play after the user consents on the provider's authorisation page.
/// </summary>
public sealed class LoopbackOAuthCaptureTests
{
    private const int VerifierExpectedLength = 43;   // 32-byte verifier → 43-char base64url

    [Fact]
    public void Prepare_ReturnsContext_WithLoopbackRedirectAndPkce()
    {
        var launcher = new RecordingBrowserLauncher();
        using var capture = new LoopbackOAuthCapture(
            launcher, NullLoggerFactory.Instance, TimeProvider.System, TimeSpan.FromMinutes(5));

        var result = capture.Prepare();

        Assert.True(result.Success);
        var context = result.Value!;
        Assert.StartsWith("http://127.0.0.1:", context.RedirectUri);
        Assert.EndsWith("/oauth-callback/", context.RedirectUri);
        Assert.Equal(VerifierExpectedLength, context.CodeVerifier.Length);
        Assert.Equal(VerifierExpectedLength, context.CodeChallenge.Length);
    }

    [Fact]
    public void Prepare_TwoConsecutiveCalls_AllocateDistinctPorts()
    {
        using var capture = NewCapture();

        var a = capture.Prepare().Value!;
        var b = capture.Prepare().Value!;

        Assert.NotEqual(a.RedirectUri, b.RedirectUri);
    }

    [Fact]
    public void Prepare_PkceChallenge_IsBase64UrlOfSha256OfVerifier()
    {
        using var capture = NewCapture();

        var context = capture.Prepare().Value!;

        var verifierBytes = Encoding.ASCII.GetBytes(context.CodeVerifier);
        var hash = SHA256.HashData(verifierBytes);
        var expectedChallenge = System.Buffers.Text.Base64Url.EncodeToString(hash);
        Assert.Equal(expectedChallenge, context.CodeChallenge);
    }

    [Fact]
    public void Prepare_VerifierDiffers_AcrossCalls()
    {
        using var capture = NewCapture();

        var a = capture.Prepare().Value!;
        var b = capture.Prepare().Value!;

        Assert.NotEqual(a.CodeVerifier, b.CodeVerifier);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_HappyPath_ReturnsCode()
    {
        var launcher = new RecordingBrowserLauncher();
        using var capture = new LoopbackOAuthCapture(
            launcher, NullLoggerFactory.Instance, TimeProvider.System, TimeSpan.FromSeconds(30));
        var context = capture.Prepare().Value!;

        var awaitTask = capture.AwaitAuthorizationCodeAsync(
            context, new Uri("https://example.com/auth"), CancellationToken.None);

        using var http = new HttpClient();
        var responseTask = http.GetAsync($"{context.RedirectUri}?code=auth-code-xyz", CancellationToken.None);

        var result = await awaitTask;
        var response = await responseTask;

        Assert.True(result.Success);
        Assert.Equal("auth-code-xyz", result.Value);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("sign-in complete", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_LaunchesBrowser_WithSuppliedAuthorizationUri()
    {
        var launcher = new RecordingBrowserLauncher();
        using var capture = new LoopbackOAuthCapture(
            launcher, NullLoggerFactory.Instance, TimeProvider.System, TimeSpan.FromSeconds(30));
        var context = capture.Prepare().Value!;
        var authUri = new Uri("https://accounts.google.com/o/oauth2/v2/auth?client_id=test");

        var awaitTask = capture.AwaitAuthorizationCodeAsync(context, authUri, CancellationToken.None);

        using var http = new HttpClient();
        await http.GetAsync($"{context.RedirectUri}?code=irrelevant", CancellationToken.None);

        var result = await awaitTask;
        Assert.True(result.Success);
        Assert.Equal(authUri, launcher.LaunchedUri);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_QueryHasError_ReturnsProviderAuthFailed()
    {
        var launcher = new RecordingBrowserLauncher();
        using var capture = new LoopbackOAuthCapture(
            launcher, NullLoggerFactory.Instance, TimeProvider.System, TimeSpan.FromSeconds(30));
        var context = capture.Prepare().Value!;

        var awaitTask = capture.AwaitAuthorizationCodeAsync(
            context, new Uri("https://example.com/auth"), CancellationToken.None);

        using var http = new HttpClient();
        var responseTask = http.GetAsync($"{context.RedirectUri}?error=access_denied", CancellationToken.None);

        var result = await awaitTask;
        var response = await responseTask;

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderAuthFailed, result.Error!.Code);
        Assert.Contains("access_denied", result.Error.Message, StringComparison.Ordinal);

        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("did not complete", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_QueryMissingCode_ReturnsProviderAuthFailed()
    {
        var launcher = new RecordingBrowserLauncher();
        using var capture = new LoopbackOAuthCapture(
            launcher, NullLoggerFactory.Instance, TimeProvider.System, TimeSpan.FromSeconds(30));
        var context = capture.Prepare().Value!;

        var awaitTask = capture.AwaitAuthorizationCodeAsync(
            context, new Uri("https://example.com/auth"), CancellationToken.None);

        using var http = new HttpClient();
        await http.GetAsync($"{context.RedirectUri}?something_else=1", CancellationToken.None);

        var result = await awaitTask;
        Assert.False(result.Success);
        Assert.Equal(ErrorCode.ProviderAuthFailed, result.Error!.Code);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_AlreadyCancelledToken_ReturnsCancelled()
    {
        // Hits the synchronous ct.ThrowIfCancellationRequested() at method entry — a distinct
        // path from the callback-driven listener.Stop() route exercised by the test below.
        using var capture = NewCapture();
        var context = capture.Prepare().Value!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await capture.AwaitAuthorizationCodeAsync(
            context, new Uri("https://example.com/auth"), cts.Token);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_CancelledBeforeRedirect_ReturnsCancelled()
    {
        var launcher = new RecordingBrowserLauncher();
        using var capture = new LoopbackOAuthCapture(
            launcher, NullLoggerFactory.Instance, TimeProvider.System, TimeSpan.FromMinutes(5));
        var context = capture.Prepare().Value!;

        using var cts = new CancellationTokenSource();
        var awaitTask = capture.AwaitAuthorizationCodeAsync(
            context, new Uri("https://example.com/auth"), cts.Token);

        // Give the listener a moment to actually start awaiting, then cancel.
        await Task.Delay(50, CancellationToken.None);
        cts.Cancel();

        var result = await awaitTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_TimeoutFires_ReturnsTimeout()
    {
        var launcher = new RecordingBrowserLauncher();
        using var capture = new LoopbackOAuthCapture(
            launcher, NullLoggerFactory.Instance, TimeProvider.System, TimeSpan.FromMilliseconds(200));
        var context = capture.Prepare().Value!;

        var awaitTask = capture.AwaitAuthorizationCodeAsync(
            context, new Uri("https://example.com/auth"), CancellationToken.None);

        var result = await awaitTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Timeout, result.Error!.Code);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_ContextFromAnotherInstance_ReturnsInvalidArgument()
    {
        using var instanceA = NewCapture();
        using var instanceB = NewCapture();

        var contextFromA = instanceA.Prepare().Value!;

        var result = await instanceB.AwaitAuthorizationCodeAsync(
            contextFromA, new Uri("https://example.com/auth"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.InvalidArgument, result.Error!.Code);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_ContextConsumedTwice_SecondCallReturnsInvalidArgument()
    {
        var launcher = new RecordingBrowserLauncher();
        using var capture = new LoopbackOAuthCapture(
            launcher, NullLoggerFactory.Instance, TimeProvider.System, TimeSpan.FromSeconds(30));
        var context = capture.Prepare().Value!;

        var firstAwait = capture.AwaitAuthorizationCodeAsync(
            context, new Uri("https://example.com/auth"), CancellationToken.None);

        using var http = new HttpClient();
        await http.GetAsync($"{context.RedirectUri}?code=first", CancellationToken.None);
        var firstResult = await firstAwait;
        Assert.True(firstResult.Success);

        var secondResult = await capture.AwaitAuthorizationCodeAsync(
            context, new Uri("https://example.com/auth"), CancellationToken.None);

        Assert.False(secondResult.Success);
        Assert.Equal(ErrorCode.InvalidArgument, secondResult.Error!.Code);
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_BrowserLauncherThrows_ReturnsUnknown()
    {
        var launcher = new RecordingBrowserLauncher
        {
            ThrowOnLaunch = new PlatformNotSupportedException("no browser here"),
        };
        using var capture = new LoopbackOAuthCapture(
            launcher, NullLoggerFactory.Instance, TimeProvider.System, TimeSpan.FromSeconds(30));
        var context = capture.Prepare().Value!;

        var result = await capture.AwaitAuthorizationCodeAsync(
            context, new Uri("https://example.com/auth"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCode.Unknown, result.Error!.Code);
        Assert.Equal(typeof(PlatformNotSupportedException).FullName, result.Error.ExceptionType);
    }

    [Fact]
    public async Task Dispose_StopsActiveListeners()
    {
        var launcher = new RecordingBrowserLauncher();
        var capture = new LoopbackOAuthCapture(
            launcher, NullLoggerFactory.Instance, TimeProvider.System, TimeSpan.FromSeconds(30));
        var context = capture.Prepare().Value!;

        capture.Dispose();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        // After dispose the listener is torn down — the HTTP request should either fail
        // outright (HttpRequestException for connection refused) or be cancelled by the client
        // timeout (TaskCanceledException) because http.sys has no listener to respond.
        var ex = await Record.ExceptionAsync(async () =>
            await http.GetAsync($"{context.RedirectUri}?code=irrelevant", CancellationToken.None));
        Assert.NotNull(ex);
        Assert.True(
            ex is HttpRequestException or TaskCanceledException,
            $"Unexpected exception type after Dispose: {ex.GetType().FullName}");
    }

    [Fact]
    public async Task AwaitAuthorizationCodeAsync_AfterDispose_Throws()
    {
        var launcher = new RecordingBrowserLauncher();
        var capture = new LoopbackOAuthCapture(
            launcher, NullLoggerFactory.Instance, TimeProvider.System, TimeSpan.FromSeconds(30));
        var context = new OAuthCaptureContext("http://127.0.0.1:1/oauth-callback/", "x", "y");
        capture.Dispose();

        // An async method that throws before its first await still wraps the exception into
        // the returned Task — ThrowsAsync is the correct API.
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            capture.AwaitAuthorizationCodeAsync(context, new Uri("https://example.com/"), CancellationToken.None));
    }

    [Fact]
    public void Prepare_AfterDispose_Throws()
    {
        var launcher = new RecordingBrowserLauncher();
        var capture = new LoopbackOAuthCapture(
            launcher, NullLoggerFactory.Instance, TimeProvider.System, TimeSpan.FromSeconds(30));
        capture.Dispose();

        Assert.Throws<ObjectDisposedException>(() => capture.Prepare());
    }

    private static LoopbackOAuthCapture NewCapture() =>
        new(new RecordingBrowserLauncher(),
            NullLoggerFactory.Instance,
            TimeProvider.System,
            TimeSpan.FromMinutes(5));

    private sealed class RecordingBrowserLauncher : IBrowserLauncher
    {
        public Uri? LaunchedUri { get; private set; }
        public Exception? ThrowOnLaunch { get; set; }

        public void Launch(Uri url)
        {
            LaunchedUri = url;
            if (ThrowOnLaunch is not null)
            {
                throw ThrowOnLaunch;
            }
        }
    }
}
