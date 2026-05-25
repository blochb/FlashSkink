using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace FlashSkink.Tests.Providers.GoogleDrive;

/// <summary>
/// Test-support <see cref="HttpMessageHandler"/> that matches outgoing requests by (method,
/// URL-prefix) and returns scripted <see cref="HttpResponseMessage"/>s. Records every received
/// request so tests can assert headers, body, and call ordering.
/// </summary>
/// <remarks>
/// <para>
/// Setup model: <c>Setup(HttpMethod.Put, "https://www.googleapis.com/upload/", req => …)</c>
/// registers a responder for any request whose method matches and whose URL starts with the prefix.
/// First matching responder wins. Setups are LIFO so a more-specific override can be stacked over
/// a default.
/// </para>
/// <para>
/// Per-prefix queue mode: tests that need different responses on successive calls to the same
/// prefix push a sequence via <see cref="Enqueue"/>. The handler consumes one response per call;
/// when the queue empties it falls back to the standing <see cref="Setup"/> responder if any.
/// </para>
/// </remarks>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly List<MatcherEntry> _setups = [];
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>>> _queues = new(StringComparer.Ordinal);
    private readonly List<RecordedRequest> _received = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<RecordedRequest> ReceivedRequests
    {
        get { lock (_lock) { return _received.ToArray(); } }
    }

    public int RequestCount
    {
        get { lock (_lock) { return _received.Count; } }
    }

    /// <summary>Registers a responder for any request matching (method, URL-prefix).</summary>
    public void Setup(HttpMethod method, string urlPrefix, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        lock (_lock)
        {
            _setups.Add(new MatcherEntry(method, urlPrefix, responder));
        }
    }

    /// <summary>Enqueues a one-shot responder for the next request matching (method, URL-prefix).</summary>
    public void Enqueue(HttpMethod method, string urlPrefix, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var key = MakeKey(method, urlPrefix);
        var queue = _queues.GetOrAdd(key, _ => new ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>>());
        queue.Enqueue(responder);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Read body eagerly so the recording captures what the caller actually sent.
        byte[]? bodyBytes = null;
        string? bodyString = null;
        if (request.Content is not null)
        {
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                bodyString = Encoding.UTF8.GetString(bodyBytes);
            }
            catch
            {
                bodyString = null;
            }
        }

        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri ?? new Uri("about:blank"),
            request.Headers.SelectMany(h => h.Value.Select(v => (h.Key, v))).ToList(),
            request.Content?.Headers.SelectMany(h => h.Value.Select(v => (h.Key, v))).ToList() ?? [],
            bodyBytes,
            bodyString);

        lock (_lock)
        {
            _received.Add(recorded);
        }

        // 1. Check per-prefix queues first.
        foreach (var entry in SnapshotSetups())
        {
            if (entry.Method != request.Method)
            {
                continue;
            }
            if (request.RequestUri is null || !request.RequestUri.AbsoluteUri.StartsWith(entry.UrlPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var key = MakeKey(entry.Method, entry.UrlPrefix);
            if (_queues.TryGetValue(key, out var queue) && queue.TryDequeue(out var queued))
            {
                return queued(request);
            }
        }

        // 2. Fall through to standing setups (LIFO).
        foreach (var entry in SnapshotSetups())
        {
            if (entry.Method != request.Method)
            {
                continue;
            }
            if (request.RequestUri is null || !request.RequestUri.AbsoluteUri.StartsWith(entry.UrlPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            return entry.Responder(request);
        }

        throw new InvalidOperationException(
            $"RecordingHttpMessageHandler received an unmatched request: {request.Method} {request.RequestUri?.AbsoluteUri}");
    }

    private IReadOnlyList<MatcherEntry> SnapshotSetups()
    {
        lock (_lock)
        {
            // LIFO so later Setup() calls override earlier ones.
            var copy = new MatcherEntry[_setups.Count];
            for (var i = 0; i < _setups.Count; i++)
            {
                copy[i] = _setups[_setups.Count - 1 - i];
            }
            return copy;
        }
    }

    private static string MakeKey(HttpMethod method, string urlPrefix) =>
        $"{method.Method}|{urlPrefix}";

    private sealed record MatcherEntry(
        HttpMethod Method,
        string UrlPrefix,
        Func<HttpRequestMessage, HttpResponseMessage> Responder);
}

/// <summary>Captured shape of one request observed by <see cref="RecordingHttpMessageHandler"/>.</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Url,
    IReadOnlyList<(string Name, string Value)> RequestHeaders,
    IReadOnlyList<(string Name, string Value)> ContentHeaders,
    byte[]? BodyBytes,
    string? BodyString)
{
    public string? GetHeader(string name) =>
        RequestHeaders.Concat(ContentHeaders)
            .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
            .Value;
}

/// <summary>Helpers for constructing canned <see cref="HttpResponseMessage"/>s in tests.</summary>
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
