using System.Security.Cryptography;
using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Abstractions.Results;
using FlashSkink.Core.Providers.Setup;
using FlashSkink.Core.Upload;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlashSkink.Tests.Providers.Live;

/// <summary>
/// Provider-agnostic exercises for the live provider tests, written against the frozen
/// <see cref="IStorageProvider"/> / <see cref="IProviderSetup"/> contract only (phase-4.5 §4.5.1).
/// Each provider's test class (Google Drive here; Dropbox/OneDrive in §4.5.2/§4.5.3) reuses this
/// unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Cleanup (cross-cutting decision 5, as resolved in §4.5.1).</strong> Every object is
/// written under <c>_livetest/{runId}/…</c> and deleted in a <c>finally</c>. Deletion is
/// <em>self-scoped</em> — a test only removes IDs it created — so concurrent runners and shared
/// accounts can never have their in-flight data removed. There is no automatic cross-run reap
/// (<see cref="IStorageProvider.ListAsync"/> returns opaque IDs, not names); stale leftovers are
/// cleared by the opt-in <see cref="PurgeAllAsync"/> maintenance path.
/// </para>
/// <para>Principle 26: no secret (token, secret, code, verifier) appears in any assertion message.</para>
/// </remarks>
internal sealed class LiveProviderHarness
{
    /// <summary>Process-wide run identifier: <c>{unixSeconds}-{guid}</c>. Groups this run's objects.</summary>
    private static readonly string RunId =
        $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid():N}";

    /// <summary>1 MiB partial final range used by the (4,3) resume case.</summary>
    private const int PartialFinalRangeBytes = 1 * 1024 * 1024;

    private readonly IProviderSetup _setup;
    private readonly string _providerKey;
    private readonly string _displayName;
    private readonly ILoggerFactory _loggerFactory;

    public LiveProviderHarness(
        IProviderSetup setup, string providerKey, string displayName, ILoggerFactory loggerFactory)
    {
        _setup = setup;
        _providerKey = providerKey;
        _displayName = displayName;
        _loggerFactory = loggerFactory;
    }

    private string ProviderId => $"live-{_providerKey.ToLowerInvariant()}";

    /// <summary>A unique remote name for a test under this run's prefix.</summary>
    public string NewObjectName(string label) => $"_livetest/{RunId}/{label}-{Guid.NewGuid():N}.bin";

    // ── Setup / provider construction ───────────────────────────────────────────────────────────

    /// <summary>
    /// The setup-with-secrets building block. Runs the real browser consent once (caching the
    /// result), or reuses the cache, then builds a provider and asserts it is healthy.
    /// </summary>
    public async Task<IStorageProvider> RunSetupAsync(CancellationToken ct)
    {
        if (LiveTokenCache.Exists(_providerKey))
        {
            return await BuildProviderFromCacheAsync(ct).ConfigureAwait(false);
        }

        var (creds, present) = LiveProviderCredentials.Resolve(_providerKey);
        Assert.True(present, $"Credentials for {_providerKey} must be present to run setup.");

        var dek = RandomNumberGenerator.GetBytes(32);

        using var capture = new LoopbackOAuthCapture(_loggerFactory);
        var context = capture.Prepare().AssertValue();

        var authUri = (await _setup
            .GetAuthorizationUriAsync(context.RedirectUri, context.CodeChallenge, creds, ct)
            .ConfigureAwait(false)).AssertValue();

        var code = (await capture
            .AwaitAuthorizationCodeAsync(context, authUri, ct)
            .ConfigureAwait(false)).AssertValue();

        var envelope = (await _setup
            .ExchangeCodeAsync(code, context.CodeVerifier, context.RedirectUri, creds, dek, ct)
            .ConfigureAwait(false)).AssertValue();

        LiveTokenCache.Save(_providerKey, envelope, dek);

        var provider = (await _setup
            .CreateProviderAsync(ProviderId, _displayName, envelope, creds, providerConfigJson: null, dek, ct)
            .ConfigureAwait(false)).AssertValue();

        // Dispose the freshly-constructed provider if the health assertion fails — otherwise the
        // exception escapes before the caller can dispose it, leaking its HttpClient/auth state.
        try
        {
            var health = (await provider.CheckHealthAsync(ct).ConfigureAwait(false)).AssertValue();
            Assert.Equal(ProviderHealthStatus.Healthy, health.Status);
        }
        catch
        {
            await DisposeProviderAsync(provider).ConfigureAwait(false);
            throw;
        }

        return provider;
    }

    /// <summary>
    /// Builds a provider headlessly from the cached envelope. Cache presence is guaranteed at
    /// discovery by <c>[LiveProviderFact(requiresCache: true)]</c>; the assertion here is defensive
    /// against a concurrent cache deletion.
    /// </summary>
    public async Task<IStorageProvider> BuildProviderFromCacheAsync(CancellationToken ct)
    {
        var loaded = LiveTokenCache.TryLoad(_providerKey, out var encryptedToken, out var dek);
        Assert.True(loaded, $"Token cache for {_providerKey} not found — run the setup test first.");

        var (creds, present) = LiveProviderCredentials.Resolve(_providerKey);
        Assert.True(present, $"Credentials for {_providerKey} must be present.");

        return (await _setup
            .CreateProviderAsync(ProviderId, _displayName, encryptedToken, creds, providerConfigJson: null, dek, ct)
            .ConfigureAwait(false)).AssertValue();
    }

    // ── Building blocks ─────────────────────────────────────────────────────────────────────────

    /// <summary>Upload a multi-range blob, download it, assert byte-identical, then delete.</summary>
    public async Task UploadDownloadRoundTripAsync(IStorageProvider provider, CancellationToken ct)
    {
        var data = RandomNumberGenerator.GetBytes(3 * UploadConstants.RangeSize);
        var name = NewObjectName("roundtrip");
        string? remoteId = null;
        try
        {
            remoteId = await UploadAllAsync(provider, name, data, ct).ConfigureAwait(false);
            var downloaded = await DownloadAllAsync(provider, remoteId, ct).ConfigureAwait(false);
            Assert.Equal(data, downloaded);
        }
        finally
        {
            await BestEffortDeleteAsync(provider, remoteId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Upload <paramref name="resumeAfterRanges"/> ranges, discard the session, re-query the
    /// server-confirmed offset, resume the rest, finalise, and verify byte-identical.
    /// </summary>
    public async Task ResumeFromPartialAsync(
        IStorageProvider provider, int totalRanges, int resumeAfterRanges, CancellationToken ct)
    {
        var fullRanges = totalRanges - 1;
        var lastRangeLen = totalRanges == 4 ? PartialFinalRangeBytes : UploadConstants.RangeSize;
        var total = (long)fullRanges * UploadConstants.RangeSize + lastRangeLen;
        var data = RandomNumberGenerator.GetBytes((int)total);
        var name = NewObjectName($"resume-{totalRanges}-{resumeAfterRanges}");
        string? remoteId = null;
        try
        {
            var session = (await provider.BeginUploadAsync(name, total, ct).ConfigureAwait(false)).AssertValue();

            // Upload the first `resumeAfterRanges` (full) ranges.
            long offset = 0;
            for (var i = 0; i < resumeAfterRanges; i++)
            {
                var len = (int)Math.Min(UploadConstants.RangeSize, total - offset);
                AssertOk(await provider
                    .UploadRangeAsync(session, offset, new ReadOnlyMemory<byte>(data, (int)offset, len), ct)
                    .ConfigureAwait(false));
                offset += len;
            }

            // Discard the in-memory session; rebuild from the persisted SessionUri (cross-process resume).
            var resumed = new UploadSession
            {
                SessionUri = session.SessionUri,
                ExpiresAt = session.ExpiresAt,
                BytesUploaded = 0,
                TotalBytes = total,
            };

            var confirmed = (await provider.GetUploadedBytesAsync(resumed, ct).ConfigureAwait(false)).AssertValue();
            Assert.Equal(offset, confirmed);

            // Resume the remaining ranges from the server-confirmed offset.
            var resumeOffset = confirmed;
            while (resumeOffset < total)
            {
                var len = (int)Math.Min(UploadConstants.RangeSize, total - resumeOffset);
                AssertOk(await provider
                    .UploadRangeAsync(resumed, resumeOffset, new ReadOnlyMemory<byte>(data, (int)resumeOffset, len), ct)
                    .ConfigureAwait(false));
                resumeOffset += len;
            }

            remoteId = (await provider.FinaliseUploadAsync(resumed, ct).ConfigureAwait(false)).AssertValue();
            var downloaded = await DownloadAllAsync(provider, remoteId, ct).ConfigureAwait(false);
            Assert.Equal(data, downloaded);
        }
        finally
        {
            await BestEffortDeleteAsync(provider, remoteId).ConfigureAwait(false);
        }
    }

    /// <summary>Exercise existence, delete (incl. idempotent re-delete), listing, and capacity reads.</summary>
    public async Task SurfaceSweepAsync(IStorageProvider provider, CancellationToken ct)
    {
        var data = RandomNumberGenerator.GetBytes(64 * 1024);
        var name = NewObjectName("surface");
        var remoteId = await UploadAllAsync(provider, name, data, ct).ConfigureAwait(false);
        var deleted = false;
        try
        {
            Assert.True((await provider.ExistsAsync(remoteId, ct).ConfigureAwait(false)).AssertValue());

            var listed = (await provider.ListAsync("_livetest/", ct).ConfigureAwait(false)).AssertValue();
            Assert.Contains(remoteId, listed);

            AssertOk(await provider.DeleteAsync(remoteId, ct).ConfigureAwait(false));
            deleted = true;
            Assert.False((await provider.ExistsAsync(remoteId, ct).ConfigureAwait(false)).AssertValue());

            // Delete is idempotent — a second delete of a missing object still succeeds.
            AssertOk(await provider.DeleteAsync(remoteId, ct).ConfigureAwait(false));

            var used = (await provider.GetUsedBytesAsync(ct).ConfigureAwait(false)).AssertValue();
            Assert.True(used >= 0, "GetUsedBytes should be non-negative.");

            var quota = (await provider.GetQuotaBytesAsync(ct).ConfigureAwait(false)).AssertValue();
            Assert.True(quota is null || quota > 0, "Quota should be null (unknown/unlimited) or positive.");
        }
        finally
        {
            if (!deleted)
            {
                await BestEffortDeleteAsync(provider, remoteId).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Opt-in maintenance: delete every object under <c>_livetest/</c>. Destructive; run alone.</summary>
    public async Task PurgeAllAsync(IStorageProvider provider, CancellationToken ct)
    {
        var ids = (await provider.ListAsync("_livetest/", ct).ConfigureAwait(false)).AssertValue();
        foreach (var id in ids)
        {
            AssertOk(await provider.DeleteAsync(id, ct).ConfigureAwait(false));
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Disposes a provider via the strongest disposal interface it implements.</summary>
    public static async Task DisposeProviderAsync(IStorageProvider provider)
    {
        if (provider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (provider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static async Task<string> UploadAllAsync(
        IStorageProvider provider, string name, byte[] data, CancellationToken ct)
    {
        var session = (await provider.BeginUploadAsync(name, data.Length, ct).ConfigureAwait(false)).AssertValue();
        var offset = 0;
        while (offset < data.Length)
        {
            var len = Math.Min(UploadConstants.RangeSize, data.Length - offset);
            AssertOk(await provider
                .UploadRangeAsync(session, offset, new ReadOnlyMemory<byte>(data, offset, len), ct)
                .ConfigureAwait(false));
            offset += len;
        }
        return (await provider.FinaliseUploadAsync(session, ct).ConfigureAwait(false)).AssertValue();
    }

    private static async Task<byte[]> DownloadAllAsync(
        IStorageProvider provider, string remoteId, CancellationToken ct)
    {
        await using var stream = (await provider.DownloadAsync(remoteId, ct).ConfigureAwait(false)).AssertValue();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// Best-effort cleanup delete using <see cref="CancellationToken.None"/> so teardown is not
    /// cancelled. Swallows any failure so a cleanup error in a <c>finally</c> can never replace the
    /// original test assertion failure.
    /// </summary>
    private static async Task BestEffortDeleteAsync(IStorageProvider provider, string? remoteId)
    {
        if (remoteId is null)
        {
            return;
        }

        try
        {
            await provider.DeleteAsync(remoteId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: a teardown failure must not mask the real test outcome.
        }
    }

    private static void AssertOk(Result result)
        => Assert.True(result.Success, result.Error?.Message ?? "Expected a successful result.");
}
