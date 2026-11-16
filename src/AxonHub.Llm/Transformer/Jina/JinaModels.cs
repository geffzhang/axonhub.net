using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Jina;

/// <summary>Port of jina.RerankRequest (llm/transformer/jina/model.go).</summary>
public sealed class JinaRerankRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("documents")]
    public List<string> Documents { get; set; } = [];

    [JsonPropertyName("top_n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TopN { get; set; }

    [JsonPropertyName("return_documents")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReturnDocuments { get; set; }
}

/// <summary>Port of jina.RerankResponse (llm/transformer/jina/model.go).</summary>
public sealed class JinaRerankResponse
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("results")]
    public List<JinaRerankResult> Results { get; set; } = [];

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JinaRerankUsage? Usage { get; set; }
}

/// <summary>Port of jina.RerankResult.</summary>
public sealed class JinaRerankResult
{
    [JsonPropertyName("index")]
    public long Index { get; set; }

    [JsonPropertyName("relevance_score")]
    public double RelevanceScore { get; set; }

    [JsonPropertyName("document")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JinaRerankDocument? Document { get; set; }
}

/// <summary>Port of jina.RerankDocument.</summary>
public sealed class JinaRerankDocument
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Port of jina.RerankUsage + its UnmarshalJSON: cost is decoded with
/// xjson.ParseOptionalFloat64, so a numeric string is accepted and any other
/// shape leaves it null instead of failing the decode.
/// </summary>
public sealed class JinaRerankUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    [JsonPropertyName("cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(GoOptionalNumberConverter))]
    public double? Cost { get; set; }
}

/// <summary>Port of jina.EmbeddingRequest (llm/transformer/jina/model.go).</summary>
public sealed class JinaEmbeddingRequest
{
    [JsonPropertyName("input")]
    public EmbeddingInput Input { get; set; } = new();

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Task { get; set; }

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

/// <summary>Port of jina.EmbeddingResponse.</summary>
public sealed class JinaEmbeddingResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public List<JinaEmbeddingData> Data { get; set; } = [];

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("usage")]
    public JinaEmbeddingUsage Usage { get; set; } = new();
}

/// <summary>Port of jina.EmbeddingData.</summary>
public sealed class JinaEmbeddingData
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("embedding")]
    public Embedding Embedding { get; set; } = new();

    [JsonPropertyName("index")]
    public long Index { get; set; }
}

/// <summary>
/// Port of jina.EmbeddingUsage + its UnmarshalJSON: cost goes through
/// xjson.ParseOptionalFloat64 like the rerank usage.
/// </summary>
public sealed class JinaEmbeddingUsage
{
    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }

    [JsonPropertyName("cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(GoOptionalNumberConverter))]
    public double? Cost { get; set; }
}
