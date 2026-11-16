using System.Text.Json.Serialization;

namespace AxonHub.Llm.Http;

/// <summary>
/// Port of httpclient.Request (llm/httpclient/model.go:15-61).
/// Transport-neutral request used by transformers and middleware.
/// </summary>
public sealed class HttpRequestData
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("query")]
    public Dictionary<string, List<string>>? Query { get; set; }

    /// <summary>
    /// Port-only: ASP.NET route values of the inbound request. The reference
    /// reads Go 1.22+ route values off the raw request
    /// (<c>RawRequest.RawRequest.PathValue</c>); the port's
    /// <see cref="HttpRequestMessage"/> has no route-value carrier, so
    /// HttpRequestReader threads them here (difference C112).
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, string?>? RouteValues { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, List<string>>? Headers { get; set; }

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Body { get; set; }

    [JsonPropertyName("json_body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? JsonBody { get; set; }

    [JsonPropertyName("auth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AuthConfig? Auth { get; set; }

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("client_ip")]
    public string ClientIp { get; set; } = string.Empty;

    [JsonPropertyName("user_agent")]
    public string UserAgent { get; set; } = string.Empty;

    [JsonPropertyName("request_type")]
    public string RequestType { get; set; } = string.Empty;

    [JsonPropertyName("api_format")]
    public string ApiFormat { get; set; } = string.Empty;

    /// <summary>Raw transport request; never serialized.</summary>
    [JsonIgnore]
    public HttpRequestMessage? RawRequest { get; set; }

    [JsonIgnore]
    public Dictionary<string, string>? Metadata { get; set; }

    [JsonIgnore]
    public Dictionary<string, System.Text.Json.JsonElement>? TransformerMetadata { get; set; }

    /// <summary>Prevents inbound query parameters from being merged in.</summary>
    [JsonIgnore]
    public bool SkipInboundQueryMerge { get; set; }

    /// <summary>
    /// Port-only: the value <c>shared.GetSessionID(ctx)</c> returned for this
    /// request, captured by the transformer that built it. The reference reads
    /// the request context inside the executor; the port has no trace/context
    /// subsystem (C013) and threads the value through the wire request instead
    /// (difference C062).
    /// </summary>
    [JsonIgnore]
    public string? SessionId { get; set; }

    /// <summary>
    /// Port-only: the value <c>shared.GetSessionScope(ctx)</c> returned for this
    /// request. The reference requires the scope for WebSocket connection
    /// pooling; the port captures it on the wire request because it has no
    /// request context seam (difference C062).
    /// </summary>
    [JsonIgnore]
    public string? SessionScope { get; set; }

    /// <summary>
    /// Port-only: the cancellation token of the caller exchange that produced
    /// this request. The reference passes <c>ctx</c> into
    /// <c>TransformResponse</c>; the port's context-free transformer interface
    /// carries the token on the wire request instead, so asynchronous task
    /// polling and downloads stop when the caller gives up (difference C104).
    /// </summary>
    [JsonIgnore]
    public CancellationToken RequestCancellationToken { get; set; }

    /// <summary>
    /// Port-only: the value <c>shared.GetResponsesLane(ctx)</c> resolved for
    /// this request. The reference reads the lane off the request context; the
    /// port threads it through the wire request (difference C289).
    /// </summary>
    [JsonIgnore]
    public AxonHub.Llm.Transformer.Shared.ResponsesLane? ResponsesLane { get; set; }

    /// <summary>
    /// Port-only: the value <c>shared.GetResponsesAttempt(ctx)</c> resolved for
    /// this request, carrying the origin of the attempt on the wire
    /// (difference C289).
    /// </summary>
    [JsonIgnore]
    public AxonHub.Llm.Transformer.Shared.ResponsesAttempt? ResponsesAttempt { get; set; }

    /// <summary>
    /// Port-only: the value <c>shared.IsResponsesWebSocket(ctx)</c> for this
    /// request (difference C289).
    /// </summary>
    [JsonIgnore]
    public bool ResponsesWebSocket { get; set; }

    /// <summary>Observer invoked with the upstream response headers.</summary>
    [JsonIgnore]
    public Action<IReadOnlyDictionary<string, List<string>>>? OnResponseHeaders { get; set; }

    public void ObserveResponseHeaders(IReadOnlyDictionary<string, List<string>> headers)
    {
        OnResponseHeaders?.Invoke(headers);
    }

    /// <summary>
    /// Field-wise copy without deep-cloning the mutable members (Go's
    /// <c>cloned := *request</c> in PrepareHTTPTransportRequest).
    /// </summary>
    internal HttpRequestData ShallowClone() => (HttpRequestData)MemberwiseClone();
}

/// <summary>Port of httpclient.AuthConfig.</summary>
public sealed class AuthConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("api_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; set; }

    [JsonPropertyName("header_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HeaderKey { get; set; }
}

/// <summary>Port of httpclient auth type constants.</summary>
public static class AuthTypes
{
    public const string Bearer = "bearer";
    public const string ApiKey = "api_key";

    /// <summary>Marks EOF for non-SSE streaming responses.</summary>
    public const string BinaryStreamDoneEventType = "binary.done";
}

/// <summary>Port of httpclient.Response.</summary>
public sealed class HttpResponseData
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, List<string>> Headers { get; set; } = [];

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Body { get; set; }

    [JsonIgnore]
    public Stream? Stream { get; set; }

    [JsonIgnore]
    public HttpRequestData? Request { get; set; }

    [JsonIgnore]
    public HttpResponseMessage? RawResponse { get; set; }

    [JsonIgnore]
    public HttpRequestMessage? RawRequest { get; set; }

    /// <summary>
    /// Go's struct copy (<c>cloned := *response</c>): every field carries over
    /// and the headers dictionary, body array and request pointers stay shared
    /// with the source, exactly like copying the reference's struct value.
    /// </summary>
    public HttpResponseData ShallowClone() => new()
    {
        StatusCode = StatusCode,
        Headers = Headers,
        Body = Body,
        Stream = Stream,
        Request = Request,
        RawResponse = RawResponse,
        RawRequest = RawRequest,
    };
}

/// <summary>Port of httpclient.StreamEvent.</summary>
public sealed class StreamEvent
{
    [JsonPropertyName("last_event_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastEventId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public byte[] Data { get; set; } = [];

    [JsonIgnore]
    public bool CleanEofCompletionEvidence { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Size { get; set; }

    [JsonIgnore]
    public Dictionary<string, List<string>>? Headers { get; set; }

    public bool IsBinaryAudioChunk()
    {
        if (string.IsNullOrEmpty(Type) || Type == AuthTypes.BinaryStreamDoneEventType)
        {
            return false;
        }

        var t = Type.Trim().ToLowerInvariant();
        return t.StartsWith("audio/", StringComparison.Ordinal) || t == "application/octet-stream";
    }

    /// <summary>Drops binary audio bytes, keeping the byte count (port of SummarizeBinaryChunk).</summary>
    public static StreamEvent? SummarizeBinaryChunk(StreamEvent? e)
    {
        if (e is null || !e.IsBinaryAudioChunk())
        {
            return e;
        }

        return new StreamEvent
        {
            LastEventId = e.LastEventId,
            Type = e.Type,
            Size = e.Data.Length,
        };
    }
}
