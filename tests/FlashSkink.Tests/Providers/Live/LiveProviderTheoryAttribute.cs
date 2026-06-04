using Xunit;

namespace FlashSkink.Tests.Providers.Live;

/// <summary>
/// A <see cref="TheoryAttribute"/> for parameterized live provider tests. Same discovery-time
/// self-skip logic as <see cref="LiveProviderFactAttribute"/>, via <see cref="LiveSkip.Reason"/>.
/// </summary>
/// <remarks>
/// xUnit enumerates <c>[InlineData]</c> rows at discovery even for skipped theories, so theory data
/// for live tests must be static and cheap — never credential- or network-dependent (phase-4.5
/// cross-cutting decision 1).
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LiveProviderTheoryAttribute : TheoryAttribute
{
    public LiveProviderTheoryAttribute(
        string providerKey, bool requiresCache = true, string? requiresEnvVar = null)
    {
        Skip = LiveSkip.Reason(providerKey, requiresCache, requiresEnvVar);
    }
}
