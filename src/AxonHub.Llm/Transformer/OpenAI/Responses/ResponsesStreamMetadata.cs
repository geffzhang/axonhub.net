using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of the cross-transformer metadata carriers in inbound_stream.go
/// (raw response metadata events, transport headers, reasoning item metadata).
/// </summary>
/// <remarks>
/// Go stores in-process values in <c>map[string]any</c> (an
/// <c>http.Header</c>, a <c>json.RawMessage</c>, or a typed struct). The port's
/// <see cref="LlmResponse.TransformerMetadata"/> can only hold
/// <see cref="JsonElement"/> snapshots, so each carrier has an explicit JSON
/// encoding (difference C068):
/// <list type="bullet">
/// <item>raw events travel as a JSON string holding the exact UTF-8 text;</item>
/// <item>headers travel as a JSON object of name to string array;</item>
/// <item>reasoning item metadata travels as a JSON object.</item>
/// </list>
/// </remarks>
internal static class ResponsesStreamMetadata
{
    /// <summary>Go: <c>responseTransportHeaders</c>.</summary>
    public static bool TryGetTransportHeaders(
        Dictionary<string, JsonElement>? metadata,
        out Dictionary<string, List<string>> headers)
    {
        headers = [];
        if (metadata is not { Count: > 0 }
            || !metadata.TryGetValue(ResponsesKeys.ResponseHeadersTransformerMetadataKey, out var raw)
            || raw.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        Dictionary<string, List<string>>? decoded;
        try
        {
            decoded = raw.Deserialize<Dictionary<string, List<string>>>(LlmJson.Options);
        }
        catch (JsonException)
        {
            return false;
        }

        if (decoded is not { Count: > 0 })
        {
            return false;
        }

        headers = decoded;
        return true;
    }

    /// <summary>
    /// Go: <c>rawResponseMetadataEvent</c>. Accepted values are the JSON string
    /// form written by <see cref="EncodeRawEvent"/>.
    /// </summary>
    public static bool TryGetRawEvent(
        Dictionary<string, JsonElement>? metadata,
        out byte[] data)
    {
        data = [];
        if (metadata is not { Count: > 0 }
            || !metadata.TryGetValue(ResponsesKeys.ResponseMetadataTransformerMetadataKey, out var raw)
            || raw.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = raw.GetString() ?? string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        data = Encoding.UTF8.GetBytes(text);
        return true;
    }

    /// <summary>Stores raw event bytes as a JSON string element (Go: json.RawMessage).</summary>
    public static JsonElement EncodeRawEvent(byte[] data)
        => JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(data), LlmJson.Options);

    /// <summary>Stores transport headers as a JSON object (Go: http.Header).</summary>
    public static JsonElement EncodeTransportHeaders(Dictionary<string, List<string>> headers)
        => JsonSerializer.SerializeToElement(headers, LlmJson.Options);

    /// <summary>Go: <c>getResponsesReasoningItemMetadata</c>.</summary>
    public static bool TryGetReasoningItemMetadata(
        Dictionary<string, JsonElement>? metadata,
        out ResponsesReasoningItemMetadata item)
    {
        item = new ResponsesReasoningItemMetadata();
        if (metadata is not { Count: > 0 }
            || !metadata.TryGetValue(ResponsesKeys.ReasoningItemTransformerMetadataKey, out var raw)
            || raw.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        ResponsesReasoningItemMetadata? decoded;
        try
        {
            decoded = raw.Deserialize<ResponsesReasoningItemMetadata>(LlmJson.Options);
        }
        catch (JsonException)
        {
            return false;
        }

        if (decoded is null)
        {
            return false;
        }

        item = decoded;
        return item.Id is { Length: > 0 };
    }

    /// <summary>Stores reasoning item metadata as a JSON object (Go: the typed struct).</summary>
    public static JsonElement EncodeReasoningItemMetadata(string? id, bool done = false)
        => JsonSerializer.SerializeToElement(
            new ResponsesReasoningItemMetadata { Id = id, Done = done },
            LlmJson.Options);

    /// <summary>Go: a deep copy of <c>http.Header.Clone</c>.</summary>
    public static Dictionary<string, List<string>> CloneHeaders(Dictionary<string, List<string>> headers)
    {
        var clone = new Dictionary<string, List<string>>(headers.Count, StringComparer.Ordinal);
        foreach (var (name, values) in headers)
        {
            clone[name] = [.. values];
        }

        return clone;
    }
}
