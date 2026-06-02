using System.Text;
using FlashSkink.Core.Abstractions.Results;

namespace FlashSkink.CLI.Setup;

/// <summary>
/// Reads setup-guide text from embedded resources and substitutes the <c>{cli}</c> placeholder
/// with <see cref="CliMetadata.CommandName"/> so guide examples always reflect the binary name.
/// </summary>
internal static class SetupGuideResources
{
    /// <summary>
    /// Maps the provider name accepted by <c>--provider</c> to the embedded resource logical
    /// name set by <c>&lt;LogicalName&gt;</c> in the project file.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ResourceNames
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["google-drive"] = "guides.google-drive",
            ["dropbox"] = "guides.dropbox",
            ["onedrive"] = "guides.onedrive",
            ["filesystem"] = "guides.filesystem",
        };

    /// <summary>
    /// Returns the guide text for <paramref name="providerName"/> with <c>{cli}</c> replaced by
    /// <see cref="CliMetadata.CommandName"/>. Never throws.
    /// </summary>
    /// <returns>
    /// <see cref="ErrorCode.InvalidArgument"/> when <paramref name="providerName"/> is not a
    /// known provider. <see cref="ErrorCode.Unknown"/> when the embedded resource is missing from
    /// the assembly (should not occur in a correctly-built binary).
    /// </returns>
    public static Result<string> Read(string providerName)
    {
        if (!ResourceNames.TryGetValue(providerName, out var resourceName))
        {
            var known = string.Join(", ", ResourceNames.Keys);
            return Result<string>.Fail(
                ErrorCode.InvalidArgument,
                $"Unknown provider '{providerName}'. Supported providers: {known}.");
        }

        var assembly = typeof(SetupGuideResources).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return Result<string>.Fail(
                ErrorCode.Unknown,
                $"Embedded resource '{resourceName}' not found. This is a build defect.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        return Result<string>.Ok(
            text.Replace("{cli}", CliMetadata.CommandName, StringComparison.Ordinal));
    }
}
