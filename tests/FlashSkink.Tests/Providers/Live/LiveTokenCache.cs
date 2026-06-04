using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlashSkink.Tests.Providers.Live;

/// <summary>
/// Local, gitignored cache of the one-time OAuth result per provider, so the interactive browser
/// consent runs once and the upload/download/resume tests run headless. Stores the DEK-encrypted
/// refresh-token envelope alongside the throwaway test-only DEK that decrypts it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Sensitivity.</strong> Because the file holds the encrypted refresh token and the key that
/// decrypts it side by side, it is as sensitive as a plaintext refresh token. It lives under
/// <c>&lt;repoRoot&gt;/.livetest-cache/</c>, which is <c>.gitignore</c>d, and the developer treats it
/// with the same hygiene as <c>.env</c>. The DEK here has no relationship to any real volume's key
/// hierarchy; it exists only to round-trip the envelope within this cache.
/// </para>
/// </remarks>
internal static partial class LiveTokenCache
{
    private static string CacheDir() => Path.Combine(LiveProviderCredentials.RepoRoot(), ".livetest-cache");

    private static string CachePath(string providerKey)
        => Path.Combine(CacheDir(), $"{providerKey.ToLowerInvariant()}.json");

    /// <summary>Whether a cache file exists for <paramref name="providerKey"/> (checked at discovery time).</summary>
    public static bool Exists(string providerKey) => File.Exists(CachePath(providerKey));

    /// <summary>
    /// Loads the cached envelope + DEK for <paramref name="providerKey"/>. Returns
    /// <see langword="false"/> (with empty out-arrays) when the file is absent or unreadable.
    /// </summary>
    public static bool TryLoad(string providerKey, out byte[] encryptedToken, out byte[] dek)
    {
        encryptedToken = [];
        dek = [];

        var path = CachePath(providerKey);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            var entry = JsonSerializer.Deserialize(json, LiveTokenCacheJsonContext.Default.CacheEntry);
            if (entry is null ||
                string.IsNullOrEmpty(entry.EncryptedTokenBase64) ||
                string.IsNullOrEmpty(entry.DekBase64))
            {
                return false;
            }

            encryptedToken = Convert.FromBase64String(entry.EncryptedTokenBase64);
            dek = Convert.FromBase64String(entry.DekBase64);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Writes the envelope + DEK for <paramref name="providerKey"/>, creating the cache directory.</summary>
    public static void Save(string providerKey, byte[] encryptedToken, ReadOnlyMemory<byte> dek)
    {
        Directory.CreateDirectory(CacheDir());
        var entry = new CacheEntry
        {
            EncryptedTokenBase64 = Convert.ToBase64String(encryptedToken),
            DekBase64 = Convert.ToBase64String(dek.ToArray()),
        };
        var json = JsonSerializer.Serialize(entry, LiveTokenCacheJsonContext.Default.CacheEntry);
        File.WriteAllText(CachePath(providerKey), json);
    }

    /// <summary>On-disk shape of a cache entry.</summary>
    internal sealed record CacheEntry
    {
        [JsonPropertyName("encryptedToken")] public string? EncryptedTokenBase64 { get; init; }
        [JsonPropertyName("dek")] public string? DekBase64 { get; init; }
    }

    [JsonSerializable(typeof(CacheEntry))]
    internal sealed partial class LiveTokenCacheJsonContext : JsonSerializerContext;
}
