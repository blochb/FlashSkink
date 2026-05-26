using System.Text.Json.Serialization;

namespace FlashSkink.Core.Providers.Dropbox;

/// <summary>
/// Deserialisation target for the <c>Providers.ProviderConfig</c> JSON column for
/// <c>dropbox</c> rows. Carries the root path under which all FlashSkink objects live
/// in the user's Dropbox account (e.g. <c>"/FlashSkink Backup"</c>).
/// </summary>
/// <remarks>
/// JSON shape: <c>{"rootPath":"/FlashSkink Backup"}</c>. The path is provider-relative — Dropbox
/// addresses files by path within the user's Dropbox root. Persisted once at setup time by §4.6's
/// <c>AddTailAsync</c>; reused on every volume open by
/// <c>BrainBackedProviderRegistry.TryBuildDropboxAdapterAsync</c>.
/// </remarks>
internal sealed record DropboxProviderConfig
{
    /// <summary>Root path under the user's Dropbox account (e.g. <c>"/FlashSkink Backup"</c>); never null.</summary>
    [JsonPropertyName("rootPath")]
    public required string RootPath { get; init; }
}

[JsonSerializable(typeof(DropboxProviderConfig))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class DropboxProviderConfigJsonContext : JsonSerializerContext;
