using FlashSkink.Core.Abstractions.Models;
using FlashSkink.Core.Abstractions.Providers;
using FlashSkink.Core.Providers.OneDrive;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSkink.Tests.Providers.Live;

/// <summary>
/// Live verification of the OneDrive provider against the real Microsoft Graph API (phase-4.5 §4.5.3).
/// Every test self-skips unless <c>FLASHSKINK_LIVE_ONEDRIVE_CLIENTID/_CLIENTSECRET</c> are set (and,
/// except for setup, unless the token cache exists) — so the class runs and reports <c>Skipped</c>
/// in CI without secrets and never gates a merge.
/// </summary>
/// <remarks>
/// Class-level <c>[Trait("Category","LiveProvider")]</c> selects the whole set with
/// <c>dotnet test --filter Category=LiveProvider</c>. No shared <c>[Collection]</c>, so the provider
/// classes run in parallel (correctness rests on per-test prefix quarantine, decision 10). The
/// shared <see cref="LiveProviderHarness"/> is reused unchanged; OneDrive exposes the same frozen
/// <see cref="IStorageProvider"/>/<see cref="IProviderSetup"/> surface as the other providers, needs
/// no fixed redirect port (Microsoft identity ignores the loopback port), and queries the server for
/// the resume offset via <c>nextExpectedRanges</c>.
/// </remarks>
[Trait("Category", "LiveProvider")]
public sealed class OneDriveLiveTests
{
    private const string Key = "ONEDRIVE";

    private static LiveProviderHarness CreateHarness()
        => new(
            new OneDriveSetup(NullLoggerFactory.Instance),
            Key,
            "OneDrive (live test)",
            NullLoggerFactory.Instance);

    [LiveProviderFact(Key, requiresCache: false)]
    public async Task OneDrive_Setup_RealConsent_ProducesUsableProvider()
    {
        var harness = CreateHarness();
        // RunSetupAsync asserts CheckHealthAsync == Healthy (and disposes on its own failure).
        var provider = await harness.RunSetupAsync(CancellationToken.None);
        try
        {
            // Setup is fully validated inside RunSetupAsync; nothing further to exercise here.
            // The try/finally matches every other test in the class and guards against a future edit.
        }
        finally
        {
            await LiveProviderHarness.DisposeProviderAsync(provider);
        }
    }

    [LiveProviderFact(Key)]
    public async Task OneDrive_UploadDownload_RoundTrips()
    {
        var harness = CreateHarness();
        var provider = await harness.BuildProviderFromCacheAsync(CancellationToken.None);
        try
        {
            await harness.UploadDownloadRoundTripAsync(provider, CancellationToken.None);
        }
        finally
        {
            await LiveProviderHarness.DisposeProviderAsync(provider);
        }
    }

    [LiveProviderTheory(Key)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    public async Task OneDrive_Resume_FromPartialUpload_Completes(int totalRanges, int resumeAfterRanges)
    {
        var harness = CreateHarness();
        var provider = await harness.BuildProviderFromCacheAsync(CancellationToken.None);
        try
        {
            await harness.ResumeFromPartialAsync(provider, totalRanges, resumeAfterRanges, CancellationToken.None);
        }
        finally
        {
            await LiveProviderHarness.DisposeProviderAsync(provider);
        }
    }

    [LiveProviderFact(Key)]
    public async Task OneDrive_RemoteHashCheck_NotSupported_ByDesign()
    {
        var harness = CreateHarness();
        var provider = await harness.BuildProviderFromCacheAsync(CancellationToken.None);
        try
        {
            // OneDrive deliberately does not implement ISupportsRemoteHashCheck: its QuickXorHash is
            // computed (OneDriveQuickXorHash) but not wired to the capability, so integrity rests on
            // the encrypted blob's GCM tag plus the byte-identical round-trip.
            Assert.False(
                provider is ISupportsRemoteHashCheck,
                "OneDrive must not implement ISupportsRemoteHashCheck (QuickXorHash is unwired).");
        }
        finally
        {
            await LiveProviderHarness.DisposeProviderAsync(provider);
        }
    }

    [LiveProviderFact(Key)]
    public async Task OneDrive_ExistsDeleteList_BehaveCorrectly()
    {
        var harness = CreateHarness();
        var provider = await harness.BuildProviderFromCacheAsync(CancellationToken.None);
        try
        {
            await harness.SurfaceSweepAsync(provider, CancellationToken.None);
        }
        finally
        {
            await LiveProviderHarness.DisposeProviderAsync(provider);
        }
    }

    [LiveProviderFact(Key)]
    public async Task OneDrive_HealthQuotaUsed_ReturnSaneValues()
    {
        var harness = CreateHarness();
        var provider = await harness.BuildProviderFromCacheAsync(CancellationToken.None);
        try
        {
            var health = (await provider.CheckHealthAsync(CancellationToken.None)).AssertValue();
            Assert.Equal(ProviderHealthStatus.Healthy, health.Status);

            var used = (await provider.GetUsedBytesAsync(CancellationToken.None)).AssertValue();
            Assert.True(used >= 0, "GetUsedBytes should be non-negative.");

            var quota = (await provider.GetQuotaBytesAsync(CancellationToken.None)).AssertValue();
            Assert.True(quota is null || quota > 0, "Quota should be null (unknown/unlimited) or positive.");
        }
        finally
        {
            await LiveProviderHarness.DisposeProviderAsync(provider);
        }
    }

    [LiveProviderFact(Key, requiresEnvVar: "FLASHSKINK_LIVE_PURGE")]
    public async Task OneDrive_PurgeAllLiveTestObjects()
    {
        var harness = CreateHarness();
        var provider = await harness.BuildProviderFromCacheAsync(CancellationToken.None);
        try
        {
            await harness.PurgeAllAsync(provider, CancellationToken.None);
        }
        finally
        {
            await LiveProviderHarness.DisposeProviderAsync(provider);
        }
    }
}
