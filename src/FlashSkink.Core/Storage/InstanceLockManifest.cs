using System.Text;
using System.Text.Json;

namespace FlashSkink.Core.Storage;

/// <summary>
/// On-disk payload written into <c>[skinkRoot]/.flashskink/instance.manifest</c> by the
/// holder immediately after acquiring the sibling exclusion file
/// (<c>[skinkRoot]/.flashskink/instance.lock</c>). A second-instance launch fails to open
/// the exclusion file, then reads this manifest with default file sharing to surface the
/// holder's identity in the user-facing error message — "FlashSkink is already running on
/// this volume from another process or host". The two-file scheme is forced by the fact
/// that <see cref="System.IO.FileShare.None"/> — the only reliably-exclusive mode on
/// Linux/macOS — also blocks peer reads. (Blueprint §19.5.)
/// </summary>
/// <remarks>
/// None of the four fields are secrets per Principle 26 — they appear in logs, notifications,
/// and bug reports. The shape is intentionally tiny: four flat strings, no nesting, no
/// versioning. Future fields are additive; a missing field on read returns the default value
/// rather than failing the parse.
/// </remarks>
internal readonly record struct InstanceLockManifest(
    int Pid,
    string Host,
    string StartedAtUtc,
    string AppVersion)
{
    /// <summary>
    /// Captures the current process's identity into a fresh manifest. <paramref name="appVersion"/>
    /// is supplied by the caller (matches the value seeded into <c>Settings.AppVersionLastOpened</c>)
    /// so this type stays free of any dependency on <c>FlashSkinkVolume</c>'s private helpers.
    /// </summary>
    internal static InstanceLockManifest Current(string appVersion)
        => new(
            Pid: Environment.ProcessId,
            Host: Environment.MachineName,
            StartedAtUtc: DateTime.UtcNow.ToString("O"),
            AppVersion: appVersion);

    /// <summary>
    /// Serializes the manifest as compact UTF-8 JSON. Field order is fixed for cosmetic
    /// stability of the on-disk file; readers tolerate any order.
    /// </summary>
    internal byte[] SerializeUtf8()
    {
        // System.Text.Json is in the .NET 10 BCL — no package reference required.
        // Compact (no indentation) keeps the file small; the manifest is human-readable
        // enough at ~150 bytes.
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            pid = Pid,
            host = Host,
            startedAtUtc = StartedAtUtc,
            appVersion = AppVersion,
        });
    }

    /// <summary>
    /// Forgiving parser. Returns <see langword="false"/> for empty / non-JSON / mis-shaped
    /// input rather than throwing — the caller falls back to "unknown" placeholders in the
    /// error message when the holder's file is unreadable.
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> utf8, out InstanceLockManifest manifest)
    {
        manifest = default;
        if (utf8.IsEmpty)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(utf8);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var pid = root.TryGetProperty("pid", out var pidEl) && pidEl.ValueKind == JsonValueKind.Number
                ? pidEl.GetInt32() : 0;
            var host = root.TryGetProperty("host", out var hostEl) && hostEl.ValueKind == JsonValueKind.String
                ? hostEl.GetString() ?? string.Empty : string.Empty;
            var startedAt = root.TryGetProperty("startedAtUtc", out var stEl) && stEl.ValueKind == JsonValueKind.String
                ? stEl.GetString() ?? string.Empty : string.Empty;
            var version = root.TryGetProperty("appVersion", out var vEl) && vEl.ValueKind == JsonValueKind.String
                ? vEl.GetString() ?? string.Empty : string.Empty;

            manifest = new InstanceLockManifest(pid, host, startedAt, version);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns a manifest with placeholder "unknown" values, used when the holder's file is
    /// empty, missing, or unparseable. The presence of an InstanceLock conflict is what
    /// matters; the holder identity is best-effort diagnostic detail.
    /// </summary>
    internal static InstanceLockManifest Unknown { get; } = new(
        Pid: 0,
        Host: "unknown",
        StartedAtUtc: "unknown",
        AppVersion: "unknown");

    /// <summary>
    /// Renders a human-readable single-line summary of the holder for inclusion in an
    /// <c>ErrorContext.Message</c>. Format: <c>pid={Pid}, host={Host}, started={StartedAtUtc}, version={AppVersion}</c>.
    /// </summary>
    internal string ToDisplayString()
        => $"pid={Pid}, host={Host}, started={StartedAtUtc}, version={AppVersion}";

    /// <summary>
    /// Convenience overload reading from a heap-allocated byte array.
    /// </summary>
    internal static bool TryParse(byte[] utf8, out InstanceLockManifest manifest)
        => TryParse(new ReadOnlySpan<byte>(utf8), out manifest);

    /// <summary>
    /// Exposes the JSON as a UTF-8 string. Only used by tests; production code calls
    /// <see cref="SerializeUtf8"/> directly to avoid the extra string allocation.
    /// </summary>
    internal string SerializeAsString()
        => Encoding.UTF8.GetString(SerializeUtf8());
}
