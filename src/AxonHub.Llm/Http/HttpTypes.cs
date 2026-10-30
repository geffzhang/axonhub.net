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

    /// <summary>Observer invoked with the upstream response headers.</summary>
    [JsonIgnore]
    public Action<IReadOnlyDictionary<string, List<string>>>? OnResponseHeaders { get; set; }

    public void ObserveResponseHeaders(IReadOnlyDictionary<string, List<string>> headers)
    {
        OnResponseHeaders?.Invoke(headers);
    }
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
