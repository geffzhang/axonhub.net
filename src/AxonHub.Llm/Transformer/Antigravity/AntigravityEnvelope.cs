using System.Text.Json.Serialization;

namespace AxonHub.Llm.Transformer.Antigravity;

/// <summary>
/// Port of antigravity.AntigravityEnvelope (llm/transformer/antigravity/envelope.go):
/// a project-aware gateway wrapper. All standard LLM payloads must be wrapped
/// in this envelope.
/// </summary>
public sealed class AntigravityEnvelope
{
    /// <summary>The resolved Google Cloud Project ID.</summary>
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    /// <summary>
    /// The Antigravity model ID. The provider is inferred from this ID (e.g.
    /// "claude-..." implies Anthropic, "gemini-..." implies Google).
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>The provider-specific payload (e.g. Gemini Format for Gemini/Claude models via Antigravity).</summary>
    [JsonPropertyName("request")]
    public object? Request { get; set; }

    /// <summary>The type of request (e.g. "agent"). Go: omitempty.</summary>
    [JsonPropertyName("requestType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestType { get; set; }

    /// <summary>The client making the request. Go: omitempty.</summary>
    [JsonPropertyName("userAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserAgent { get; set; }

    /// <summary>A unique identifier for this request. Go: omitempty.</summary>
    [JsonPropertyName("requestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestID { get; set; }
}

/// <summary>Port of NewAntigravityEnvelope (envelope.go:29-38).</summary>
public static class AntigravityEnvelopeFactory
{
    /// <summary>Creates a new envelope with default values ("agent", "antigravity", "agent-&lt;uuid&gt;").</summary>
    public static AntigravityEnvelope NewAntigravityEnvelope(string project, string model, object? request)
        => new()
        {
            Project = project,
            Model = model,
            Request = request,
            RequestType = "agent",
            UserAgent = "antigravity",
            RequestID = "agent-" + Guid.NewGuid(),
        };
}
