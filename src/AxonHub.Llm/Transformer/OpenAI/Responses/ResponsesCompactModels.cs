using System.Text.Json.Serialization;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of responses.CompactAPIRequest (compact.go:9-16): the body for
/// POST /v1/responses/compact. <c>input</c> is <c>omitzero</c>, so the builder
/// keeps it null when Go's zero Input would be omitted.
/// </summary>
public sealed class CompactApiRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesInput? Input { get; set; }

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    [JsonPropertyName("prompt_cache_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromptCacheKey { get; set; }
}

/// <summary>
/// Port of responses.CompactAPIResponse (compact.go:19-27). <c>id</c>,
/// <c>created_at</c>, <c>object</c> and <c>output</c> are always emitted.
/// </summary>
public sealed class CompactApiResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    [JsonPropertyName("output")]
    public List<ResponsesItem>? Output { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesUsage? Usage { get; set; }
}
