using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.Tests._TestSupport;

/// <summary>
/// Test double for <see cref="IOAuthCaptureFlow"/>. Reused by §4.3–§4.6 setup tests so they can
/// exercise OAuth provider setup logic without touching the real network or launching a browser.
/// </summary>
internal sealed class FakeOAuthCaptureFlow : IOAuthCaptureFlow
{
    private readonly object _lock = new();

    /// <summary>The last context the fake handed out from <see cref="Prepare"/>. Null until Prepare succeeds.</summary>
    public OAuthCaptureContext? LastPreparedContext { get; private set; }

    /// <summary>The authorisation URI captured by the last <see cref="AwaitAuthorizationCodeAsync"/> call.</summary>
    public Uri? CapturedAuthorizationUri { get; private set; }

    /// <summary>Number of times <see cref="Prepare"/> has been invoked.</summary>
    public int PrepareCallCount { get; private set; }

    /// <summary>Number of times <see cref="AwaitAuthorizationCodeAsync"/> has been invoked.</summary>
    public int AwaitCallCount { get; private set; }

    /// <summary>
    /// Result returned by <see cref="Prepare"/>. Defaults to a deterministic Ok context with
    /// the canonical loopback redirect URI.
    /// </summary>
    public Result<OAuthCaptureContext> PrepareResult { get; set; }

    /// <summary>
    /// Result returned by <see cref="AwaitAuthorizationCodeAsync"/>. Defaults to
    /// <c>Ok("test-authorization-code")</c>.
    /// </summary>
    public Result<string> AwaitResult { get; set; } = Result<string>.Ok("test-authorization-code");

    public FakeOAuthCaptureFlow()
    {
        PrepareResult = Result<OAuthCaptureContext>.Ok(new OAuthCaptureContext(
            RedirectUri: "http://127.0.0.1:65000/oauth-callback/",
            CodeChallenge: "test-code-challenge",
            CodeVerifier: "test-code-verifier"));
    }

    /// <inheritdoc/>
    public Result<OAuthCaptureContext> Prepare()
    {
        lock (_lock)
        {
            PrepareCallCount++;
            if (PrepareResult.Success)
            {
                LastPreparedContext = PrepareResult.Value;
            }
            return PrepareResult;
        }
    }

    /// <inheritdoc/>
    public Task<Result<string>> AwaitAuthorizationCodeAsync(
        OAuthCaptureContext context,
        Uri authorizationUri,
        CancellationToken ct)
    {
        lock (_lock)
        {
            AwaitCallCount++;
            CapturedAuthorizationUri = authorizationUri;
        }

        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(Result<string>.Fail(
                ErrorCode.Cancelled, "OAuth capture was cancelled."));
        }

        return Task.FromResult(AwaitResult);
    }
}
