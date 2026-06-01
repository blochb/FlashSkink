using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlashSkink.Core.Providers.OneDrive;

/// <summary>Persisted per-provider configuration for a OneDrive tail.</summary>
internal sealed record OneDriveProviderConfig
{
    /// <summary>Drive-relative root path under which this tail's blobs are stored.</summary>
    [JsonPropertyName("rootPath")]
    public required string RootPath { get; init; }
}

[JsonSerializable(typeof(OneDriveProviderConfig))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class OneDriveProviderConfigJsonContext : JsonSerializerContext;
