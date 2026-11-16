using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Cloudflare;

/// <summary>Port of cloudflare.systemOneWireRequest (model.go:9-13).</summary>
internal sealed class CloudflareWireRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    [JsonConverter(typeof(GoAnyJsonConverter))]
    public JsonElement State { get; set; }

    [JsonPropertyName("questions")]
    public Dictionary<string, SystemOneQuestion>? Questions { get; set; }
}

/// <summary>Port of cloudflare.systemOneWireUsage (model.go:15-18).</summary>
internal sealed class CloudflareWireUsage
{
    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; set; }
}

/// <summary>Port of cloudflare.systemOneWireResponse (model.go:20-24).</summary>
internal sealed class CloudflareWireResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("answers")]
    public Dictionary<string, SystemOneAnswer>? Answers { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CloudflareWireUsage? Usage { get; set; }
}

/// <summary>Port of cloudflare.envelopeError (model.go:34-37).</summary>
internal sealed class CloudflareEnvelopeError
{
    /// <summary>Go json.RawMessage: any JSON token, kept as raw text.</summary>
    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
