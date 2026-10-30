using System.Text.Json.Serialization;

namespace AxonHub.Llm;

/// <summary>Port of llm.CompactRequest (llm/compact.go).</summary>
public sealed class CompactRequest
{
    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Message>? Input { get; set; }

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    [JsonPropertyName("prompt_cache_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromptCacheKey { get; set; }
}

/// <summary>Port of llm.CompactResponse (llm/compact.go).</summary>
public sealed class CompactResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    [JsonPropertyName("output")]
    public List<Message> Output { get; set; } = [];
}
