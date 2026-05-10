using System.Text.Json.Serialization;

namespace FlashSkink.Core.Providers;

/// <summary>
/// Deserialisation target for the <c>Providers.ProviderConfig</c> JSON column for
/// <c>FileSystemProvider</c> rows. Blueprint §16.2 JSON shape: <c>{"rootPath":"..."}</c>.
/// </summary>
internal sealed record FileSystemProviderConfig(
    [property: JsonPropertyName("rootPath")] string RootPath);

[JsonSerializable(typeof(FileSystemProviderConfig))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class FileSystemProviderConfigJsonContext : JsonSerializerContext;
