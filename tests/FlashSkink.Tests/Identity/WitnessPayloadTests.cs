using System.Text;
using FlashSkink.Core.Identity;
using Xunit;

namespace FlashSkink.Tests.Identity;

/// <summary>
/// Unit tests for the <see cref="WitnessPayload"/> value type — the JSON serialized form
/// (decrypted) of the witness file on a tail. Dev plan §3.5.1.
/// </summary>
public sealed class WitnessPayloadTests
{
    [Fact]
    public void ForNewSession_SetsAllFields()
    {
        var before = DateTime.UtcNow;
        var payload = WitnessPayload.ForNewSession(
            volumeId: "vol-id-123",
            epoch: 42L,
            appVersion: "1.2.3+test");

        Assert.Equal("vol-id-123", payload.VolumeId);
        Assert.Equal(42L, payload.Epoch);
        Assert.True(Guid.TryParse(payload.SessionId, out var sessionGuid));
        Assert.NotEqual(Guid.Empty, sessionGuid);
        Assert.Equal(Environment.MachineName, payload.Host);
        Assert.Equal("1.2.3+test", payload.AppVersion);
        Assert.False(payload.ConflictObserved);

        var committedAt = DateTime.Parse(
            payload.CommittedAtUtc,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(committedAt >= before.AddSeconds(-1));
        Assert.True(committedAt <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void SerializeUtf8_ThenTryParse_RoundTripsAllFields()
    {
        var original = new WitnessPayload(
            VolumeId: "vol-id-xyz",
            Epoch: 99L,
            SessionId: "11111111-2222-3333-4444-555555555555",
            CommittedAtUtc: "2025-05-19T14:32:01.1234567Z",
            Host: "test-host",
            AppVersion: "0.1.0+abc1234",
            ConflictObserved: true);

        var bytes = original.SerializeUtf8();
        Assert.True(WitnessPayload.TryParse(bytes, out var roundTripped));

        Assert.Equal(original.VolumeId, roundTripped.VolumeId);
        Assert.Equal(original.Epoch, roundTripped.Epoch);
        Assert.Equal(original.SessionId, roundTripped.SessionId);
        Assert.Equal(original.CommittedAtUtc, roundTripped.CommittedAtUtc);
        Assert.Equal(original.Host, roundTripped.Host);
        Assert.Equal(original.AppVersion, roundTripped.AppVersion);
        Assert.Equal(original.ConflictObserved, roundTripped.ConflictObserved);
    }

    [Fact]
    public void TryParse_Empty_ReturnsFalse()
    {
        Assert.False(WitnessPayload.TryParse(ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void TryParse_NonJson_ReturnsFalse()
    {
        var garbage = Encoding.UTF8.GetBytes("this is not json at all");
        Assert.False(WitnessPayload.TryParse(garbage, out _));
    }

    [Fact]
    public void TryParse_JsonArrayRoot_ReturnsFalse()
    {
        var arrayJson = Encoding.UTF8.GetBytes("[]");
        Assert.False(WitnessPayload.TryParse(arrayJson, out _));
    }

    [Fact]
    public void TryParse_MissingConflictObserved_DefaultsFalse()
    {
        // JSON object that omits conflictObserved entirely — backward-compat path.
        var json = Encoding.UTF8.GetBytes(
            "{\"volumeId\":\"v\",\"epoch\":5,\"sessionId\":\"s\",\"committedAtUtc\":\"ts\",\"host\":\"h\",\"appVersion\":\"a\"}");

        Assert.True(WitnessPayload.TryParse(json, out var payload));
        Assert.False(payload.ConflictObserved);
        Assert.Equal(5L, payload.Epoch);
    }

    [Fact]
    public void TryParse_WithConflictObservedTrue_ParsesCorrectly()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"volumeId\":\"v\",\"epoch\":7,\"sessionId\":\"s\",\"committedAtUtc\":\"ts\",\"host\":\"h\",\"appVersion\":\"a\",\"conflictObserved\":true}");

        Assert.True(WitnessPayload.TryParse(json, out var payload));
        Assert.True(payload.ConflictObserved);
    }

    [Fact]
    public void TryParse_UnknownFields_AreIgnored()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"volumeId\":\"v\",\"epoch\":3,\"sessionId\":\"s\",\"committedAtUtc\":\"ts\",\"host\":\"h\",\"appVersion\":\"a\",\"conflictObserved\":false,\"extraField\":\"ignored\",\"another\":42}");

        Assert.True(WitnessPayload.TryParse(json, out var payload));
        Assert.Equal("v", payload.VolumeId);
        Assert.Equal(3L, payload.Epoch);
    }

    [Fact]
    public void TryParse_AbsentEpoch_DefaultsToZero()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"volumeId\":\"v\",\"sessionId\":\"s\",\"committedAtUtc\":\"ts\",\"host\":\"h\",\"appVersion\":\"a\"}");

        Assert.True(WitnessPayload.TryParse(json, out var payload));
        Assert.Equal(0L, payload.Epoch);
    }

    [Fact]
    public void ToDisplayString_ContainsEpochHostSessionAppVersion()
    {
        var payload = new WitnessPayload(
            VolumeId: "vol-id",
            Epoch: 13L,
            SessionId: "session-abc",
            CommittedAtUtc: "2025-05-19T14:32:01Z",
            Host: "alice-laptop",
            AppVersion: "0.1.0+sha",
            ConflictObserved: false);

        var rendered = payload.ToDisplayString();

        Assert.Contains("epoch=13", rendered);
        Assert.Contains("host=alice-laptop", rendered);
        Assert.Contains("session=session-abc", rendered);
        Assert.Contains("appVersion=0.1.0+sha", rendered);
    }

    [Fact]
    public void TryParse_ByteArrayOverload_Equivalent()
    {
        var payload = WitnessPayload.ForNewSession("v", 1L, "a");
        byte[] bytes = payload.SerializeUtf8();

        Assert.True(WitnessPayload.TryParse(bytes, out var fromArray));
        Assert.True(WitnessPayload.TryParse(new ReadOnlySpan<byte>(bytes), out var fromSpan));

        Assert.Equal(fromArray, fromSpan);
    }
}
