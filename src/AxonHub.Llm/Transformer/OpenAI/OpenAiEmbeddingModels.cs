using System.Text.Json.Serialization;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.EmbeddingRequest (llm/transformer/openai/embedding.go:14-20):
/// both the inbound decode shape and the outbound request body.
/// </summary>
public sealed class OpenAiEmbeddingRequest
{
    [JsonPropertyName("input")]
    public EmbeddingInput Input { get; set; } = new();

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("encoding_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncodingFormat { get; set; }

    [JsonPropertyName("dimensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Dimensions { get; set; }

    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }
}

/// <summary>Port of openai.EmbeddingResponse (llm/transformer/openai/embedding.go:22-27).</summary>
public sealed class OpenAiEmbeddingResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    /// <summary>Go's nil slice marshals as <c>null</c>.</summary>
    [JsonPropertyName("data")]
    public List<OpenAiEmbeddingData>? Data { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Go's struct is always emitted; null models a missing upstream field.</summary>
    [JsonPropertyName("usage")]
    public OpenAiEmbeddingUsage? Usage { get; set; }
}

/// <summary>Port of openai.EmbeddingData (llm/transformer/openai/embedding.go:29-33).</summary>
public sealed class OpenAiEmbeddingData
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("embedding")]
    public Embedding Embedding { get; set; } = new();

    [JsonPropertyName("index")]
    public long Index { get; set; }
}

/// <summary>Port of openai.EmbeddingUsage (llm/transformer/openai/embedding.go:35-39).</summary>
public sealed class OpenAiEmbeddingUsage
{
    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }

    [JsonPropertyName("cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Cost { get; set; }
}
