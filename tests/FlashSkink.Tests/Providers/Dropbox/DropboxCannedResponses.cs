using System.Net;
using System.Text;

namespace FlashSkink.Tests.Providers.Dropbox;

/// <summary>
/// Helpers for constructing canned <see cref="HttpResponseMessage"/>s in Dropbox tests.
/// </summary>
internal static class DropboxCannedResponses
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

    /// <summary>Successful upload_session/start: 200 with {"session_id": "..."}.</summary>
    public static HttpResponseMessage UploadSessionStartOk(string sessionId) =>
        WithJson(200, $"{{\"session_id\":\"{sessionId}\"}}");

    /// <summary>Successful upload_session/append_v2: 200 empty body.</summary>
    public static HttpResponseMessage UploadSessionAppendOk() =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };

    /// <summary>Successful upload_session/finish: 200 with FileMetadata JSON.</summary>
    public static HttpResponseMessage UploadSessionFinishOk(
        string id, ulong size, string contentHash, string pathLower)
    {
        var json = $$"""
        {
          ".tag": "file",
          "name": "{{System.IO.Path.GetFileName(pathLower)}}",
          "id": "{{id}}",
          "client_modified": "2026-05-26T12:00:00Z",
          "server_modified": "2026-05-26T12:00:01Z",
          "rev": "0123456789abcdef",
          "size": {{size}},
          "path_lower": "{{pathLower}}",
          "path_display": "{{pathLower}}",
          "is_downloadable": true,
          "content_hash": "{{contentHash}}"
        }
        """;
        return WithJson(200, json);
    }

    /// <summary>
    /// Dropbox-shaped 409 error for upload_session/append_v2. The SDK parses these and surfaces
    /// them as ApiException&lt;UploadSessionAppendError&gt;. Body shape per Dropbox's API.
    /// </summary>
    public static HttpResponseMessage UploadSessionAppendIncorrectOffset(ulong correctOffset)
    {
        var json = $$"""
        {
          "error_summary": "incorrect_offset/...",
          "error": {
            ".tag": "incorrect_offset",
            "correct_offset": {{correctOffset}}
          }
        }
        """;
        return WithJson(409, json);
    }

    /// <summary>Append against a session id that no longer exists (TTL expired or never started).</summary>
    public static HttpResponseMessage UploadSessionAppendNotFound()
    {
        var json = """
        {
          "error_summary": "not_found/...",
          "error": { ".tag": "not_found" }
        }
        """;
        return WithJson(409, json);
    }

    /// <summary>Append against a session closed by a prior close=true call.</summary>
    public static HttpResponseMessage UploadSessionAppendClosed()
    {
        var json = """
        {
          "error_summary": "closed/...",
          "error": { ".tag": "closed" }
        }
        """;
        return WithJson(409, json);
    }

    /// <summary>upload_session/finish: lookup_failed → not_found (session gone).</summary>
    public static HttpResponseMessage UploadSessionFinishLookupNotFound()
    {
        var json = """
        {
          "error_summary": "lookup_failed/not_found/...",
          "error": {
            ".tag": "lookup_failed",
            "lookup_failed": { ".tag": "not_found" }
          }
        }
        """;
        return WithJson(409, json);
    }

    /// <summary>401 with an AuthError body.</summary>
    public static HttpResponseMessage AuthExpired()
    {
        var json = """
        {
          "error_summary": "expired_access_token/...",
          "error": { ".tag": "expired_access_token" }
        }
        """;
        return WithJson(401, json);
    }

    /// <summary>429 rate-limit with a Retry-After hint.</summary>
    public static HttpResponseMessage RateLimited(int retryAfterSeconds = 1)
    {
        var json = $$"""
        {
          "error_summary": "too_many_requests/...",
          "error": {
            ".tag": "too_many_requests",
            "retry_after": {{retryAfterSeconds}}
          }
        }
        """;
        var resp = WithJson(429, json);
        resp.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds.ToString());
        return resp;
    }

    /// <summary>users/get_current_account: 200 with FullAccount JSON (minimal shape).</summary>
    public static HttpResponseMessage GetCurrentAccountOk()
    {
        var json = """
        {
          "account_id": "dbid:AAH4f99T0taONIb-OurWxbNQ6ywGRopQngc",
          "name": {
            "given_name": "Test", "surname": "User",
            "familiar_name": "Test", "display_name": "Test User", "abbreviated_name": "TU"
          },
          "email": "test@example.com",
          "email_verified": true,
          "disabled": false,
          "locale": "en",
          "referral_link": "https://db.tt/ZITNuhtI",
          "is_paired": false,
          "account_type": { ".tag": "basic" },
          "root_info": {
            ".tag": "user",
            "root_namespace_id": "1",
            "home_namespace_id": "1"
          }
        }
        """;
        return WithJson(200, json);
    }

    /// <summary>users/get_space_usage: 200 with SpaceUsage JSON (individual allocation).</summary>
    public static HttpResponseMessage GetSpaceUsageIndividualOk(ulong used, ulong allocated)
    {
        var json = $$"""
        {
          "used": {{used}},
          "allocation": {
            ".tag": "individual",
            "allocated": {{allocated}}
          }
        }
        """;
        return WithJson(200, json);
    }

    /// <summary>files/get_metadata: 200 with FileMetadata.</summary>
    public static HttpResponseMessage GetMetadataFileOk(string id, string pathLower, ulong size)
    {
        var json = $$"""
        {
          ".tag": "file",
          "name": "{{System.IO.Path.GetFileName(pathLower)}}",
          "id": "{{id}}",
          "client_modified": "2026-05-26T12:00:00Z",
          "server_modified": "2026-05-26T12:00:01Z",
          "rev": "abc",
          "size": {{size}},
          "path_lower": "{{pathLower}}",
          "path_display": "{{pathLower}}",
          "is_downloadable": true,
          "content_hash": "0000000000000000000000000000000000000000000000000000000000000000"
        }
        """;
        return WithJson(200, json);
    }

    /// <summary>files/get_metadata: 409 path/not_found.</summary>
    public static HttpResponseMessage GetMetadataNotFound()
    {
        var json = """
        {
          "error_summary": "path/not_found/...",
          "error": {
            ".tag": "path",
            "path": { ".tag": "not_found" }
          }
        }
        """;
        return WithJson(409, json);
    }

    /// <summary>files/delete_v2: 200 with DeleteResult.</summary>
    public static HttpResponseMessage DeleteV2Ok(string id, string pathLower)
    {
        var json = $$"""
        {
          "metadata": {
            ".tag": "file",
            "name": "{{System.IO.Path.GetFileName(pathLower)}}",
            "id": "{{id}}",
            "client_modified": "2026-05-26T12:00:00Z",
            "server_modified": "2026-05-26T12:00:01Z",
            "rev": "abc",
            "size": 0,
            "path_lower": "{{pathLower}}",
            "path_display": "{{pathLower}}",
            "is_downloadable": true,
            "content_hash": "0000000000000000000000000000000000000000000000000000000000000000"
          }
        }
        """;
        return WithJson(200, json);
    }

    /// <summary>files/delete_v2: 409 path_lookup/not_found.</summary>
    public static HttpResponseMessage DeleteV2NotFound()
    {
        var json = """
        {
          "error_summary": "path_lookup/not_found/...",
          "error": {
            ".tag": "path_lookup",
            "path_lookup": { ".tag": "not_found" }
          }
        }
        """;
        return WithJson(409, json);
    }

    /// <summary>files/list_folder: 200 with a single page of entries.</summary>
    public static HttpResponseMessage ListFolderOk(
        IEnumerable<(string Id, string PathDisplay)> files, bool hasMore = false, string cursor = "next-cursor")
    {
        var entries = string.Join(",\n", files.Select(f => $$"""
            {
              ".tag": "file",
              "name": "{{System.IO.Path.GetFileName(f.PathDisplay)}}",
              "id": "{{f.Id}}",
              "client_modified": "2026-05-26T12:00:00Z",
              "server_modified": "2026-05-26T12:00:01Z",
              "rev": "abc",
              "size": 0,
              "path_lower": "{{f.PathDisplay.ToLowerInvariant()}}",
              "path_display": "{{f.PathDisplay}}",
              "is_downloadable": true,
              "content_hash": "0000000000000000000000000000000000000000000000000000000000000000"
            }
            """));
        var json = $$"""
        {
          "entries": [{{entries}}],
          "cursor": "{{cursor}}",
          "has_more": {{(hasMore ? "true" : "false")}}
        }
        """;
        return WithJson(200, json);
    }

    /// <summary>files/list_folder: 409 path/not_found.</summary>
    public static HttpResponseMessage ListFolderNotFound()
    {
        var json = """
        {
          "error_summary": "path/not_found/...",
          "error": {
            ".tag": "path",
            "path": { ".tag": "not_found" }
          }
        }
        """;
        return WithJson(409, json);
    }

    /// <summary>files/download: 200 with body bytes + Dropbox-API-Result header carrying metadata JSON.</summary>
    public static HttpResponseMessage DownloadOk(string id, string pathLower, byte[] body)
    {
        var resp = WithBody(200, body);
        var metadata = $$"""{".tag":"file","name":"{{System.IO.Path.GetFileName(pathLower)}}","id":"{{id}}","client_modified":"2026-05-26T12:00:00Z","server_modified":"2026-05-26T12:00:01Z","rev":"abc","size":{{body.Length}},"path_lower":"{{pathLower}}","path_display":"{{pathLower}}","is_downloadable":true,"content_hash":"0000000000000000000000000000000000000000000000000000000000000000"}""";
        resp.Headers.TryAddWithoutValidation("Dropbox-API-Result", metadata);
        return resp;
    }

    /// <summary>files/download: 409 path/not_found.</summary>
    public static HttpResponseMessage DownloadNotFound()
    {
        var json = """
        {
          "error_summary": "path/not_found/...",
          "error": {
            ".tag": "path",
            "path": { ".tag": "not_found" }
          }
        }
        """;
        return WithJson(409, json);
    }
}
