using System.Net;
using System.Text;

namespace FlashSkink.Tests.Providers.OneDrive;

/// <summary>
/// Helpers for constructing canned Microsoft Graph <see cref="HttpResponseMessage"/>s in the OneDrive
/// provider/setup tests. The shared <see cref="RecordingHttpMessageHandler"/> lives in the parent
/// <c>FlashSkink.Tests.Providers</c> namespace. JSON is hand-built with plain concatenation to keep
/// the literal brace runs unambiguous.
/// </summary>
internal static class OneDriveCannedResponses
{
    public static HttpResponseMessage Status(int statusCode) =>
        new((HttpStatusCode)statusCode) { Content = new ByteArrayContent(Array.Empty<byte>()) };

    public static HttpResponseMessage WithJson(int statusCode, string json)
    {
        return new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    public static HttpResponseMessage WithBody(int statusCode, byte[] body, string contentType = "application/octet-stream")
    {
        var resp = new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new ByteArrayContent(body),
        };
        resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return resp;
    }

    /// <summary>200 response to <c>createUploadSession</c>.</summary>
    public static HttpResponseMessage CreateUploadSession(string uploadUrl, DateTimeOffset? expiration = null)
    {
        var exp = (expiration ?? DateTimeOffset.UtcNow.AddDays(7)).ToString("o");
        var json = "{\"uploadUrl\":\"" + uploadUrl + "\",\"expirationDateTime\":\"" + exp + "\",\"nextExpectedRanges\":[\"0-\"]}";
        return WithJson(200, json);
    }

    /// <summary>200 status query for an upload session, reporting the next expected ranges.</summary>
    public static HttpResponseMessage UploadStatus(params string[] nextExpectedRanges)
    {
        var ranges = string.Join(",", nextExpectedRanges.Select(r => "\"" + r + "\""));
        var json = "{\"expirationDateTime\":\"" + DateTimeOffset.UtcNow.AddDays(7).ToString("o") +
            "\",\"nextExpectedRanges\":[" + ranges + "]}";
        return WithJson(200, json);
    }

    /// <summary>A committed DriveItem (returned on the final range PUT or a metadata GET).</summary>
    public static HttpResponseMessage DriveItem(int statusCode, string id, long size, string? quickXorHash = null)
    {
        var hashes = quickXorHash is null
            ? string.Empty
            : ",\"file\":{\"hashes\":{\"quickXorHash\":\"" + quickXorHash + "\"}}";
        var json = "{\"id\":\"" + id + "\",\"name\":\"blob\",\"size\":" + size + hashes + "}";
        return WithJson(statusCode, json);
    }

    /// <summary>A children listing page, optionally with an <c>@odata.nextLink</c>.</summary>
    public static HttpResponseMessage Children(string? nextLink, params (string Id, string Name, bool IsFolder)[] items)
    {
        var entries = items.Select(i => i.IsFolder
            ? "{\"id\":\"" + i.Id + "\",\"name\":\"" + i.Name + "\",\"folder\":{\"childCount\":0}}"
            : "{\"id\":\"" + i.Id + "\",\"name\":\"" + i.Name + "\",\"file\":{}}");
        var value = string.Join(",", entries);
        var link = nextLink is null ? string.Empty : ",\"@odata.nextLink\":\"" + nextLink + "\"";
        var json = "{\"value\":[" + value + "]" + link + "}";
        return WithJson(200, json);
    }

    /// <summary>A <c>/me/drive</c> response carrying a quota facet.</summary>
    public static HttpResponseMessage Drive(long? used, long? total)
    {
        var fields = new List<string>();
        if (used is not null)
        {
            fields.Add("\"used\":" + used.Value);
        }
        if (total is not null)
        {
            fields.Add("\"total\":" + total.Value);
        }
        var quota = "{" + string.Join(",", fields) + "}";
        var json = "{\"id\":\"drive-1\",\"quota\":" + quota + "}";
        return WithJson(200, json);
    }

    /// <summary>A successful token-endpoint response.</summary>
    public static HttpResponseMessage Token(string accessToken, string? refreshToken = null)
    {
        var rt = refreshToken is null ? string.Empty : ",\"refresh_token\":\"" + refreshToken + "\"";
        var json = "{\"access_token\":\"" + accessToken + "\",\"token_type\":\"Bearer\",\"expires_in\":3600" + rt + "}";
        return WithJson(200, json);
    }
}
