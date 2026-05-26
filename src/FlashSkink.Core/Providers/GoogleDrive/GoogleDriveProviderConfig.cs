using System.Text.Json.Serialization;

namespace FlashSkink.Core.Providers.GoogleDrive;

/// <summary>
/// Deserialisation target for the <c>Providers.ProviderConfig</c> JSON column for
/// <c>google-drive</c> rows. Carries the stable Drive folder ID for the
/// <c>"FlashSkink Backup"</c> root folder, established once at setup time and reused on every
/// volume open to avoid a per-open lookup round-trip.
/// </summary>
/// <remarks>
/// JSON shape: <c>{"folderId":"…", "folderName":"FlashSkink Backup"}</c>. <c>folderName</c> is
/// stored for diagnostics only; <c>folderId</c> is the load-bearing field.
/// </remarks>
internal sealed record GoogleDriveProviderConfig
{
    /// <summary>Drive file ID of the root <c>"FlashSkink Backup"</c> folder (stable; never null).</summary>
    [JsonPropertyName("folderId")]
    public required string FolderId { get; init; }

    /// <summary>The literal folder name used at setup; defaults to <see cref="ProviderConstants.CloudRootFolderName"/>.</summary>
    [JsonPropertyName("folderName")]
    public string FolderName { get; init; } = ProviderConstants.CloudRootFolderName;
}

[JsonSerializable(typeof(GoogleDriveProviderConfig))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class GoogleDriveProviderConfigJsonContext : JsonSerializerContext;
