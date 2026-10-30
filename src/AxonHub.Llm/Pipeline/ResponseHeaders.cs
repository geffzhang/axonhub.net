using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Pipeline;

/// <summary>Port of httpclient/response_headers.go.</summary>
public static class ResponseHeaders
{
    /// <summary>
    /// Tells Codex that upstream usage already includes previously generated
    /// reasoning tokens.
    /// </summary>
    public const string ReasoningIncludedHeader = "X-Reasoning-Included";

    /// <summary>
    /// Port of <c>WithResponseHeaders</c>: attaches response metadata to a
    /// stream without widening the generic stream interface.
    /// </summary>
    public static IStream<StreamEvent> WithResponseHeaders(
        IStream<StreamEvent>? stream,
        Dictionary<string, List<string>>? headers)
    {
        if (stream is null || headers is null || headers.Count == 0)
        {
            return stream!;
        }

        return new ResponseHeadersStream(stream, Clone(headers));
    }

    /// <summary>Port of <c>GetResponseHeaders</c>: a copy, or null when unattached.</summary>
    public static Dictionary<string, List<string>>? GetResponseHeaders(IStream<StreamEvent>? stream)
        => stream is IResponseHeadersProvider provider ? Clone(provider.ResponseHeaders) : null;

    /// <summary>
    /// Port of <c>MergeForwardResponseHeaders</c>: copies the small, explicit
    /// set of upstream headers that are safe at the client boundary.
    /// </summary>
    public static Dictionary<string, List<string>>? MergeForwardResponseHeaders(
        Dictionary<string, List<string>>? dst,
        Dictionary<string, List<string>>? src)
    {
        var forward = HasOnlyTrueHeaderValues(src, ReasoningIncludedHeader);

        if (dst is not null)
        {
            foreach (var key in dst.Keys.ToList())
            {
                if (string.Equals(key, ReasoningIncludedHeader, StringComparison.OrdinalIgnoreCase))
                {
                    dst.Remove(key);
                }
            }
        }

        if (!forward)
        {
            return dst;
        }

        dst ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        dst[ReasoningIncludedHeader] = ["true"];

        return dst;
    }

    private static bool HasOnlyTrueHeaderValues(Dictionary<string, List<string>>? headers, string name)
    {
        var found = false;
        if (headers is null)
        {
            return false;
        }

        foreach (var (key, values) in headers)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in values)
            {
                found = true;
                if (!string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return found;
    }

    private static Dictionary<string, List<string>> Clone(Dictionary<string, List<string>> headers)
    {
        var copy = new Dictionary<string, List<string>>(headers.Count, StringComparer.Ordinal);
        foreach (var (key, values) in headers)
        {
            copy[key] = [.. values];
        }

        return copy;
    }
}

/// <summary>Go: the unexported <c>responseHeadersProvider</c> interface.</summary>
public interface IResponseHeadersProvider
{
    Dictionary<string, List<string>> ResponseHeaders { get; }
}

/// <summary>Port of <c>responseHeadersStream</c>.</summary>
public sealed class ResponseHeadersStream : IStream<StreamEvent>, IResponseHeadersProvider
{
    private readonly IStream<StreamEvent> _stream;

    public ResponseHeadersStream(IStream<StreamEvent> stream, Dictionary<string, List<string>> headers)
    {
        _stream = stream;
        ResponseHeaders = headers;
    }

    public Dictionary<string, List<string>> ResponseHeaders { get; }

    public StreamEvent? Current => _stream.Current;

    public Exception? Err => _stream.Err;

    public bool Next() => _stream.Next();

    public void Dispose() => _stream.Dispose();
}
