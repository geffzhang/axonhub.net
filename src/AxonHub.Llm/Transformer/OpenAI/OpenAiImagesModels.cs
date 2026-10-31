using System.Text.Json.Serialization;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.ImagesResponse (llm/transformer/openai/image_outbound.go:638-647),
/// used for both decoding the upstream images response and serializing the
/// client-facing one. Go emits the fields in struct order and omits empty
/// <c>omitempty</c> strings, so the empty string is normalized to null (C001).
/// </summary>
public sealed class ImagesResponse
{
    [JsonPropertyName("created")]
    public long Created { get; set; }

    /// <summary>Go: <c>[]ImageData</c>; nil decodes to null but still ranges as empty.</summary>
    [JsonPropertyName("data")]
    public List<ImagesResponseData>? Data { get; set; }

    [JsonPropertyName("background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Background { get; set; }

    [JsonPropertyName("output_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quality { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImagesResponseUsage? Usage { get; set; }
}

/// <summary>Port of openai.ImageData (image_outbound.go:650-654).</summary>
public sealed class ImagesResponseData
{
    [JsonPropertyName("b64_json")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? B64JSON { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? URL { get; set; }

    [JsonPropertyName("revised_prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RevisedPrompt { get; set; }
}

/// <summary>Port of openai.ImagesResponseUsage (image_outbound.go:657-664).</summary>
public sealed class ImagesResponseUsage
{
    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }

    [JsonPropertyName("input_tokens_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImagesResponseUsageInputTokensDetails? InputTokensDetails { get; set; }

    [JsonPropertyName("output_tokens_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImagesResponseUsageOutputTokensDetails? OutputTokensDetails { get; set; }

    [JsonPropertyName("cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Cost { get; set; }
}

/// <summary>Port of openai.ImagesResponseUsageInputTokensDetails (image_outbound.go:667-671).</summary>
public sealed class ImagesResponseUsageInputTokensDetails
{
    [JsonPropertyName("image_tokens")]
    public long ImageTokens { get; set; }

    [JsonPropertyName("text_tokens")]
    public long TextTokens { get; set; }

    [JsonPropertyName("cached_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long CachedTokens { get; set; }
}

/// <summary>Port of openai.ImagesResponseUsageOutputTokensDetails (image_outbound.go:674-676).</summary>
public sealed class ImagesResponseUsageOutputTokensDetails
{
    [JsonPropertyName("reasoning_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ReasoningTokens { get; set; }
}

/// <summary>
/// Port of openai.ImageGenerationRequest (image_inbound.go:56-79): the JSON
/// body of <c>/v1/images/generations</c>. Raw fields are <see cref="System.Text.Json.JsonElement"/>
/// (Go: <c>json.RawMessage</c>); a missing field keeps the default
/// <see cref="System.Text.Json.JsonValueKind.Undefined"/>.
/// </summary>
public sealed class ImageGenerationRequest
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("n")]
    public long? N { get; set; }

    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("size")]
    public string? Size { get; set; }

    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("output_compression")]
    public long? OutputCompression { get; set; }

    [JsonPropertyName("moderation")]
    public string? Moderation { get; set; }

    [JsonPropertyName("partial_images")]
    public long? PartialImages { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("image")]
    public System.Text.Json.JsonElement Image { get; set; }

    /// <summary>Provider-specific image generation controls accepted by MiniMax.</summary>
    [JsonPropertyName("subject_reference")]
    public System.Text.Json.JsonElement SubjectReference { get; set; }

    [JsonPropertyName("aspect_ratio")]
    public string? AspectRatio { get; set; }

    [JsonPropertyName("width")]
    public long? Width { get; set; }

    [JsonPropertyName("height")]
    public long? Height { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    [JsonPropertyName("prompt_optimizer")]
    public bool? PromptOptimizer { get; set; }
}

/// <summary>
/// Port of openai.ImageEditJSONRequest (image_inbound.go:361-378): the
/// <c>application/json</c> variant of <c>/v1/images/edits</c>.
/// </summary>
public sealed class ImageEditJsonRequest
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("image")]
    public System.Text.Json.JsonElement Image { get; set; }

    [JsonPropertyName("images")]
    public System.Text.Json.JsonElement Images { get; set; }

    [JsonPropertyName("mask")]
    public string? Mask { get; set; }

    [JsonPropertyName("n")]
    public long? N { get; set; }

    [JsonPropertyName("size")]
    public string? Size { get; set; }

    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("output_compression")]
    public long? OutputCompression { get; set; }

    [JsonPropertyName("input_fidelity")]
    public string? InputFidelity { get; set; }

    [JsonPropertyName("partial_images")]
    public long? PartialImages { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }
}
