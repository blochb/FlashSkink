using System.Text.Json;

namespace FlashSkink.Core.Identity;

/// <summary>
/// On-disk payload written by <c>WitnessStore.WriteAsync</c> into
/// <c>_witness/current.enc</c> on each tail. AES-256-GCM encrypted with the DEK using
/// <see cref="WitnessCrypto.Encrypt"/>; the cleartext is the compact JSON produced by
/// <see cref="SerializeUtf8"/>. (Blueprint §19.6 — added in dev plan §3.5.2; the type and
/// serialization land here in §3.5.1 so that §3.5.2 has both ends of the read/write seam
/// available.)
/// </summary>
/// <remarks>
/// <para>
/// Conflict signals are carried by two fields: <see cref="Epoch"/> (a monotonically
/// increasing counter, one increment per <c>OpenAsync</c>) and <see cref="ConflictObserved"/>
/// (the two-sided fence marker — set to <see langword="true"/> by the higher-epoch holder
/// so the lagger detects the conflict on its next open even if its own epoch is now
/// ahead). The other four fields — <see cref="VolumeId"/>, <see cref="SessionId"/>,
/// <see cref="CommittedAtUtc"/>, <see cref="Host"/>, <see cref="AppVersion"/> — are
/// diagnostic context for error messages; none are used in conflict detection logic.
/// </para>
/// <para>
/// None of the fields are secrets (Principle 26): the witness file itself is encrypted by
/// the DEK, but the cleartext is intentionally free of sensitive material and the
/// <see cref="ToDisplayString"/> rendering appears verbatim in user-visible error
/// messages.
/// </para>
/// </remarks>
internal readonly record struct WitnessPayload(
    string VolumeId,
    long Epoch,
    string SessionId,
    string CommittedAtUtc,
    string Host,
    string AppVersion,
    bool ConflictObserved)
{
    /// <summary>
    /// Captures the current process's identity into a fresh witness payload.
    /// <paramref name="appVersion"/> is supplied by the caller (matches the value the
    /// brain writes into <c>Settings["AppVersionLastOpened"]</c>) so this type stays free
    /// of any dependency on <c>FlashSkinkVolume</c>'s private helpers. The
    /// <c>ConflictObserved</c> field is <see langword="false"/>; the §3.5.2 handshake sets
    /// it to <see langword="true"/> via <c>with</c> expression when stamping the
    /// two-sided fence marker.
    /// </summary>
    internal static WitnessPayload ForNewSession(string volumeId, long epoch, string appVersion)
        => new(
            VolumeId: volumeId,
            Epoch: epoch,
            SessionId: Guid.NewGuid().ToString("D"),
            CommittedAtUtc: DateTime.UtcNow.ToString("O"),
            Host: Environment.MachineName,
            AppVersion: appVersion,
            ConflictObserved: false);

    /// <summary>
    /// Serializes the payload as compact UTF-8 JSON. Field order is fixed for cosmetic
    /// stability of the on-disk plaintext (after decryption); readers tolerate any order.
    /// </summary>
    internal byte[] SerializeUtf8()
    {
        // System.Text.Json is in the .NET 10 BCL — no package reference required.
        // Compact (no indentation) keeps the cleartext small; the payload is ~200 bytes
        // and the ciphertext envelope is ~250 bytes including version byte, nonce, and tag.
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            volumeId = VolumeId,
            epoch = Epoch,
            sessionId = SessionId,
            committedAtUtc = CommittedAtUtc,
            host = Host,
            appVersion = AppVersion,
            conflictObserved = ConflictObserved,
        });
    }

    /// <summary>
    /// Forgiving parser. Returns <see langword="false"/> for empty input, non-JSON,
    /// or a JSON root that is not an object. Unknown fields are silently ignored;
    /// missing fields default (empty string, <c>0</c>, or <see langword="false"/>). Never
    /// throws: the caller — <c>WitnessStore.TryReadAsync</c> — falls back to "no witness"
    /// when the parse fails, treating the tail as having no usable conflict information.
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> utf8, out WitnessPayload payload)
    {
        payload = default;
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

            var volumeId = root.TryGetProperty("volumeId", out var volEl) && volEl.ValueKind == JsonValueKind.String
                ? volEl.GetString() ?? string.Empty : string.Empty;
            var epoch = root.TryGetProperty("epoch", out var epEl) && epEl.ValueKind == JsonValueKind.Number
                ? epEl.GetInt64() : 0L;
            var sessionId = root.TryGetProperty("sessionId", out var sidEl) && sidEl.ValueKind == JsonValueKind.String
                ? sidEl.GetString() ?? string.Empty : string.Empty;
            var committedAtUtc = root.TryGetProperty("committedAtUtc", out var cEl) && cEl.ValueKind == JsonValueKind.String
                ? cEl.GetString() ?? string.Empty : string.Empty;
            var host = root.TryGetProperty("host", out var hEl) && hEl.ValueKind == JsonValueKind.String
                ? hEl.GetString() ?? string.Empty : string.Empty;
            var appVersion = root.TryGetProperty("appVersion", out var avEl) && avEl.ValueKind == JsonValueKind.String
                ? avEl.GetString() ?? string.Empty : string.Empty;
            // Missing ConflictObserved defaults to false — additive field, backward-compatible.
            var conflictObserved = root.TryGetProperty("conflictObserved", out var coEl)
                && coEl.ValueKind == JsonValueKind.True;

            payload = new WitnessPayload(
                volumeId, epoch, sessionId, committedAtUtc, host, appVersion, conflictObserved);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Array overload of <see cref="TryParse(ReadOnlySpan{byte}, out WitnessPayload)"/>
    /// for callers that already hold a <see cref="byte"/> array (e.g. from
    /// <see cref="JsonSerializer.SerializeToUtf8Bytes(object?, System.Text.Json.JsonSerializerOptions?)"/>).
    /// </summary>
    internal static bool TryParse(byte[] utf8, out WitnessPayload payload)
        => TryParse(new ReadOnlySpan<byte>(utf8), out payload);

    /// <summary>
    /// Renders a single-line human-readable summary for inclusion in
    /// <see cref="FlashSkink.Core.Abstractions.Results.ErrorContext.Message"/>. Format:
    /// <c>epoch={Epoch}, host={Host}, session={SessionId}, appVersion={AppVersion}</c>.
    /// The <see cref="VolumeId"/>, <see cref="CommittedAtUtc"/>, and
    /// <see cref="ConflictObserved"/> fields are intentionally omitted from this short
    /// rendering — they live in the error <c>Metadata</c> dictionary for callers that
    /// need them programmatically.
    /// </summary>
    internal string ToDisplayString()
        => $"epoch={Epoch}, host={Host}, session={SessionId}, appVersion={AppVersion}";
}
