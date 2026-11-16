namespace AxonHub.Llm.Http;

/// <summary>
/// Port of the Go <c>*url.Error</c> raised when a redirect policy refuses to
/// follow a hop (llm/httpclient/client.go:112-144 and net/http/client.go:719-727).
/// </summary>
/// <remarks>
/// The reference returns this error wrapped by <c>HTTP request failed: %w</c>
/// (client.go:291-293) or <c>HTTP stream request failed: %w</c>
/// (client.go:385-387), so <see cref="Exception.Message"/> keeps the same
/// <c>&lt;prefix&gt;Get "&lt;url&gt;": &lt;reason&gt;</c> shape.
/// </remarks>
public sealed class HttpRedirectException : Exception
{
    public HttpRedirectException(string message)
        : base(message)
    {
    }
}
