using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm;

/// <summary>
/// Port of llm.DecisionsRequest (llm/decisions.go): preserves the original
/// decisions request body while exposing the fields routing and validation need.
/// </summary>
public sealed class DecisionsRequest
{
    /// <summary>Go: <c>Body []byte `json:"-"`</c> — carried but never serialized.</summary>
    [JsonIgnore]
    public byte[] Body { get; set; } = [];

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Go: <c>Input json.RawMessage `json:"input"`</c>.</summary>
    [JsonPropertyName("input")]
    public JsonElement? Input { get; set; }

    /// <summary>Go: <c>Questions []json.RawMessage `json:"questions"`</c>.</summary>
    [JsonPropertyName("questions")]
    public List<JsonElement>? Questions { get; set; }
}

/// <summary>Port of llm.DecisionsUsage: the only documented usage value mapped.</summary>
public sealed class DecisionsUsage
{
    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; set; }
}

/// <summary>
/// Port of llm.DecisionsResponse: preserves the original decisions response body
/// while exposing answers and the documented input-token usage value.
/// </summary>
public sealed class DecisionsResponse
{
    /// <summary>Go: <c>Body []byte `json:"-"`</c> — carried but never serialized.</summary>
    [JsonIgnore]
    public byte[] Body { get; set; } = [];

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Go: <c>Answers []json.RawMessage `json:"answers"`</c>.</summary>
    [JsonPropertyName("answers")]
    public List<JsonElement>? Answers { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DecisionsUsage? Usage { get; set; }
}
