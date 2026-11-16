using System.Text.Json.Serialization;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.ModerationCreateRequest (llm/transformer/openai/moderation.go:12-15):
/// both the inbound decode shape and the outbound request body.
/// </summary>
public sealed class OpenAiModerationCreateRequest
{
    [JsonPropertyName("input")]
    public ModerationInput Input { get; set; } = new();

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }
}

/// <summary>Port of openai.ModerationCreateResponse (llm/transformer/openai/moderation.go:18-22).</summary>
public sealed class OpenAiModerationCreateResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Go's nil slice marshals as <c>null</c>.</summary>
    [JsonPropertyName("results")]
    public List<ModerationClassification>? Results { get; set; }
}
