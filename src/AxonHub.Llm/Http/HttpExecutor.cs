using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Http;

/// <summary>
/// Port of httpclient.HttpClient's transport methods (llm/httpclient/client.go):
/// executes the built request over <see cref="HttpClient"/> and reports a non-2xx
/// upstream response as <see cref="HttpClientError"/>, exactly where the Go
/// client does.
/// </summary>
/// <remarks>
/// The reference interface is synchronous (Go goroutines), so the async .NET
/// transport is bridged with <c>GetAwaiter().GetResult()</c>. The pipeline is
/// ported synchronously; there is no synchronization context under ASP.NET Core.
/// </remarks>
public sealed class HttpExecutor : IExecutor
{
    /// <summary>Go: <c>httpclient.MaxErrorBodySize</c> (1 MB).</summary>
    public const int MaxErrorBodySize = 1 << 20;

    private readonly HttpClient _client;

    public HttpExecutor(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var built = HttpClientUtils.BuildHttpRequest(request);

        // Only set the default Accept when the transformer did not specify one
        // (e.g. TTS sets Accept: */* to receive binary audio).
        if (string.IsNullOrEmpty(HttpClientUtils.HeaderGet(built.Headers, "Accept")))
        {
            HttpClientUtils.HeaderSet(built.Headers, "Accept", "application/json");
        }

        var raw = Send(built, cancellationToken);

        Dictionary<string, List<string>> headers;
        try
        {
            headers = ReadHeaders(raw);
        }
        catch
        {
            raw.Dispose();
            throw;
        }

        request.ObserveResponseHeaders(headers);

        if ((int)raw.StatusCode >= 400)
        {
            var statusCode = (int)raw.StatusCode;
            var statusLine = StatusLine(raw);
            byte[] errorBody;
            try
            {
                errorBody = ReadCapped(raw.Content.ReadAsStream(cancellationToken));
            }
            finally
            {
                raw.Dispose();
            }

            throw new HttpClientError(
                built.Method,
                built.Url,
                statusCode,
                statusLine,
                errorBody)
            {
                Headers = headers,
            };
        }

        try
        {
            var body = ReadAll(raw.Content.ReadAsStream(cancellationToken));
            return new HttpResponseData
            {
                StatusCode = (int)raw.StatusCode,
                Headers = headers,
                Body = body,
                Request = request,
                RawRequest = raw.RequestMessage,
                // The body is fully consumed above, so the response stays usable
                // as metadata (Go keeps the closed-body *http.Response too).
                RawResponse = raw,
            };
        }
        catch
        {
            raw.Dispose();
            throw;
        }
    }

    public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var built = HttpClientUtils.BuildHttpRequest(request);

        // Force SSE Accept unless the outbound transformer explicitly opted into
        // a non-JSON Accept (e.g. "*/*" for binary TTS chunks).
        var accept = HttpClientUtils.HeaderGet(built.Headers, "Accept");
        if (string.IsNullOrEmpty(accept) || string.Equals(accept, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            HttpClientUtils.HeaderSet(built.Headers, "Accept", "text/event-stream");
        }

        HttpClientUtils.HeaderSet(built.Headers, "Cache-Control", "no-cache");
        HttpClientUtils.HeaderSet(built.Headers, "Connection", "keep-alive");

        var raw = Send(built, cancellationToken);

        var headers = ReadHeaders(raw);
        request.ObserveResponseHeaders(headers);

        if ((int)raw.StatusCode >= 400)
        {
            var statusCode = (int)raw.StatusCode;
            var statusLine = StatusLine(raw);
            var errorBody = ReadCapped(raw.Content.ReadAsStream(cancellationToken));
            raw.Dispose();

            throw new HttpClientError(
                built.Method,
                built.Url,
                statusCode,
                statusLine,
                errorBody)
            {
                Headers = headers,
            };
        }

        var stream = new HttpSseEventStream(
            raw.Content.ReadAsStream(cancellationToken),
            raw);

        // Codex turn-state propagation: only the first event carries it.
        var firstEventHeaders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (TryGetHeader(headers, "X-Codex-Turn-State", out var turnState))
        {
            firstEventHeaders["X-Codex-Turn-State"] = turnState;
        }

        IStream<StreamEvent> events = new FirstEventHeadersStream(stream, firstEventHeaders);

        return ResponseHeaders.WithResponseHeaders(
            events,
            ResponseHeaders.MergeForwardResponseHeaders(null, headers));
    }

    private HttpResponseMessage Send(BuiltHttpRequest built, CancellationToken cancellationToken)
    {
        var raw = new HttpRequestMessage(new HttpMethod(built.Method), built.Url);

        if (built.Body.Length > 0)
        {
            raw.Content = new ByteArrayContent(built.Body);
        }

        foreach (var (key, values) in built.Headers)
        {
            // Content headers (Content-Type, Content-Length, ...) are rejected by
            // HttpRequestMessage.Headers; they belong to the content.
            if (!raw.Headers.TryAddWithoutValidation(key, values) && raw.Content is not null)
            {
                raw.Content.Headers.TryAddWithoutValidation(key, values);
            }
        }

        try
        {
            return _client
                .SendAsync(raw, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            raw.Dispose();
            throw;
        }
    }

    private static Dictionary<string, List<string>> ReadHeaders(HttpResponseMessage response)
    {
        // Go's net/http stores response headers under textproto canonical keys;
        // .NET uses its own casing for known descriptors (X-Request-ID), so
        // re-canonicalize to keep HeaderGet's ordinal lookup equivalent.
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (key, values) in response.Headers)
        {
            headers[HttpClientUtils.CanonicalHeaderKey(key)] = [.. values];
        }

        foreach (var (key, values) in response.Content.Headers)
        {
            var canonical = HttpClientUtils.CanonicalHeaderKey(key);
            if (headers.TryGetValue(canonical, out var existing))
            {
                existing.AddRange(values);
            }
            else
            {
                headers[canonical] = [.. values];
            }
        }

        return headers;
    }

    private static bool TryGetHeader(
        Dictionary<string, List<string>> headers,
        string name,
        out List<string> values)
    {
        foreach (var (key, headerValues) in headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                values = headerValues;
                return true;
            }
        }

        values = [];
        return false;
    }

    private static string StatusLine(HttpResponseMessage response)
    {
        var reason = response.ReasonPhrase ?? string.Empty;
        return ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (reason.Length > 0 ? " " + reason : string.Empty);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    private static byte[] ReadCapped(Stream stream)
    {
        using (stream)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (buffer.Length < MaxErrorBodySize)
            {
                var toRead = (int)Math.Min(chunk.Length, MaxErrorBodySize - buffer.Length);
                var read = stream.Read(chunk, 0, toRead);
                if (read <= 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    /// <summary>Attaches headers only to the first event, like the reference map.</summary>
    private sealed class FirstEventHeadersStream(
        IStream<StreamEvent> stream,
        Dictionary<string, List<string>> firstEventHeaders) : IStream<StreamEvent>
    {
        private bool _first = true;

        public StreamEvent? Current => stream.Current;

        public Exception? Err => stream.Err;

        public bool Next()
        {
            if (!stream.Next())
            {
                return false;
            }

            if (_first && stream.Current is { } current)
            {
                if (firstEventHeaders.Count > 0)
                {
                    current.Headers = firstEventHeaders;
                }

                _first = false;
            }

            return true;
        }

        public void Dispose() => stream.Dispose();
    }
}
