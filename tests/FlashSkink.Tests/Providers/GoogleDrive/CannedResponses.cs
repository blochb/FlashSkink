using System.Net;
using System.Text;

namespace FlashSkink.Tests.Providers.GoogleDrive;

/// <summary>
/// Helpers for constructing canned <see cref="HttpResponseMessage"/>s in the Google Drive provider
/// tests. The shared <see cref="RecordingHttpMessageHandler"/> lives in the parent
/// <c>FlashSkink.Tests.Providers</c> namespace; these Drive-specific helpers were split out of the
/// former per-folder handler copy when it was de-duplicated.
/// </summary>
internal static class CannedResponses
{
    public static HttpResponseMessage Status(int statusCode) =>
        new((HttpStatusCode)statusCode) { Content = new ByteArrayContent(Array.Empty<byte>()) };

    public static HttpResponseMessage WithJson(int statusCode, string json)
    {
        var resp = new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return resp;
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

    public static HttpResponseMessage ResumableInit(string sessionUri)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        resp.Headers.Location = new Uri(sessionUri);
        return resp;
    }

    public static HttpResponseMessage Range308(long lastByte)
    {
        var resp = new HttpResponseMessage((HttpStatusCode)308)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        resp.Headers.TryAddWithoutValidation("Range", $"bytes=0-{lastByte}");
        return resp;
    }

    public static HttpResponseMessage Empty308()
    {
        return new HttpResponseMessage((HttpStatusCode)308)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
    }
}
