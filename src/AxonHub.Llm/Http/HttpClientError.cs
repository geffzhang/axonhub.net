namespace AxonHub.Llm.Http;

/// <summary>
/// Port of httpclient.Error (llm/httpclient/errors.go:79-90): the transport
/// layer reports a non-2xx upstream response through this error, keeping the
/// status line and raw body so the outbound transformer can map it.
/// </summary>
public sealed class HttpClientError : Exception
{
    public HttpClientError(string method, string url, int statusCode, string status, byte[]? body = null)
        : base(FormatMessage(method, url, status))
    {
        Method = method;
        Url = url;
        StatusCode = statusCode;
        Status = status;
        Body = body ?? [];
    }

    public string Method { get; }

    public string Url { get; }

    public int StatusCode { get; }

    /// <summary>Go's <c>Status</c>: the literal status line text, e.g. <c>"500 Internal Server Error"</c>.</summary>
    public string Status { get; }

    public byte[] Body { get; }

    /// <summary>Response headers; never serialized.</summary>
    public Dictionary<string, List<string>>? Headers { get; set; }

    /// <summary>Go: <c>fmt.Sprintf("%s - %s with status %s", e.Method, e.URL, e.Status)</c>.</summary>
    private static string FormatMessage(string method, string url, string status)
        => method + " - " + url + " with status " + status;
}
