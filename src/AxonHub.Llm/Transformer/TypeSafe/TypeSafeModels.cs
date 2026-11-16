using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.TypeSafe;

/// <summary>
/// Port of typesafe.systemOneWireRequest (llm/transformer/typesafe/model.go).
/// </summary>
public sealed class SystemOneWireRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Go <c>any</c>; written with Go's map ordering and number literals.</summary>
    [JsonPropertyName("state")]
    [JsonConverter(typeof(GoAnyJsonConverter))]
    public JsonElement State { get; set; }

    [JsonPropertyName("questions")]
    public Dictionary<string, SystemOneQuestion>? Questions { get; set; }

    /// <summary>
    /// <c>omitempty</c> in Go, but the outbound transformer never sets it — a
    /// SystemOne chat is always non-streaming.
    /// </summary>
    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }
}

/// <summary>Port of typesafe.systemOneWireUsage (llm/transformer/typesafe/model.go).</summary>
public sealed class SystemOneWireUsage
{
    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; set; }
}

/// <summary>
/// Port of typesafe.systemOneWireResponse (llm/transformer/typesafe/model.go).
/// </summary>
public sealed class SystemOneWireResponse
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Go map: nil marshals as <c>null</c>, an empty map as <c>{}</c>. Kept
    /// nullable so the inbound response can reproduce both.
    /// </summary>
    [JsonPropertyName("answers")]
    public Dictionary<string, SystemOneAnswer>? Answers { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SystemOneWireUsage? Usage { get; set; }
}
