using System.Collections.Concurrent;
using System.Text;

namespace FlashSkink.Tests.Providers.Dropbox;

/// <summary>
/// Test-support <see cref="HttpMessageHandler"/> that matches outgoing requests by (method,
/// URL-prefix) and returns scripted <see cref="HttpResponseMessage"/>s. Records every received
/// request so tests can assert headers, body, and call ordering.
/// </summary>
/// <remarks>
/// Duplicated from <c>tests/FlashSkink.Tests/Providers/GoogleDrive/RecordingHttpMessageHandler.cs</c>
/// per Discrepancy 2 of <c>.claude/plans/pr-4.4.md</c>. The Drive-specific
/// <c>CannedResponses.ResumableInit</c> / <c>Range308</c> / <c>Empty308</c> helpers are not
/// carried over — Dropbox-specific helpers live in <see cref="DropboxCannedResponses"/>.
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

    /// <summary>Registers a responder for any request matching (method, URL-prefix). LIFO override.</summary>
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

        foreach (var entry in SnapshotSetups())
        {
            if (entry.Method != request.Method) { continue; }
            if (request.RequestUri is null ||
                !request.RequestUri.AbsoluteUri.StartsWith(entry.UrlPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var key = MakeKey(entry.Method, entry.UrlPrefix);
            if (_queues.TryGetValue(key, out var queue) && queue.TryDequeue(out var queued))
            {
                return queued(request);
            }
        }

        foreach (var entry in SnapshotSetups())
        {
            if (entry.Method != request.Method) { continue; }
            if (request.RequestUri is null ||
                !request.RequestUri.AbsoluteUri.StartsWith(entry.UrlPrefix, StringComparison.Ordinal))
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
