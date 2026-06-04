using Xunit;

namespace FlashSkink.Tests.Providers.Live;

/// <summary>
/// Centralises the discovery-time skip decision shared by <see cref="LiveProviderFactAttribute"/>
/// and <see cref="LiveProviderTheoryAttribute"/>. Returns the skip reason, or <see langword="null"/>
/// when the test may run. A non-null reason produces a true xUnit <c>Skipped</c> status — never a
/// false-positive pass (phase-4.5 §4.5.1 Gate-1 resolution).
/// </summary>
internal static class LiveSkip
{
    /// <summary>
    /// Evaluates the three skip checks in order: (1) BYOC credentials present in the environment;
    /// (2) when <paramref name="requiresCache"/>, the provider's token cache exists; (3) when
    /// <paramref name="requiresEnvVar"/> is set, that variable is non-empty. Loads <c>.env</c> first.
    /// </summary>
    public static string? Reason(string providerKey, bool requiresCache, string? requiresEnvVar)
    {
        LiveProviderCredentials.EnsureEnvLoaded();

        var key = providerKey.ToUpperInvariant();
        var clientId = Environment.GetEnvironmentVariable($"FLASHSKINK_LIVE_{key}_CLIENTID");
        var clientSecret = Environment.GetEnvironmentVariable($"FLASHSKINK_LIVE_{key}_CLIENTSECRET");
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            return $"Set FLASHSKINK_LIVE_{key}_CLIENTID and FLASHSKINK_LIVE_{key}_CLIENTSECRET to run live {key} tests.";
        }

        if (requiresCache && !LiveTokenCache.Exists(providerKey))
        {
            return $"Run the {key} setup test first to populate the token cache.";
        }

        if (!string.IsNullOrEmpty(requiresEnvVar) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(requiresEnvVar)))
        {
            return $"Set {requiresEnvVar}=1 to run this opt-in maintenance test.";
        }

        return null;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for live provider tests that self-skips at discovery time when the
/// provider's BYOC credentials (and, by default, its cached token) are not available — so the suite
/// runs and reports <c>Skipped</c> in CI without secrets and never gates a merge.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="requiresCache"/> defaults to <see langword="true"/>; set it to
/// <see langword="false"/> on the setup test (which creates the cache). <paramref name="requiresEnvVar"/>
/// gates opt-in maintenance tests (e.g. a destructive purge) behind an extra flag.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LiveProviderFactAttribute : FactAttribute
{
    public LiveProviderFactAttribute(
        string providerKey, bool requiresCache = true, string? requiresEnvVar = null)
    {
        Skip = LiveSkip.Reason(providerKey, requiresCache, requiresEnvVar);
    }
}
