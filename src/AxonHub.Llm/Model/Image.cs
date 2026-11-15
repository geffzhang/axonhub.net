using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm;

/// <summary>
/// Port of llm.ImageRequest (llm/image.go:5-64). Common fields such as Model
/// live on the parent <see cref="LlmRequest"/>, not here. Go's plain
/// <c>string</c> + <c>omitempty</c> fields use <c>string?</c> with
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/> per difference C001.
/// </summary>
public sealed class ImageRequest
{
    /// <summary>Go: <c>ModelSpecified bool json:"-"</c>.</summary>
    [JsonIgnore]
    public bool ModelSpecified { get; set; }

    /// <summary>The text prompt for image generation.</summary>
    [JsonPropertyName("prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Prompt { get; set; }

    /// <summary>Raw image data for edit/variation requests; never serialized.</summary>
    [JsonIgnore]
    public List<byte[]>? Images { get; set; }

    /// <summary>Reference forms for edit images (data URL, HTTP URL or file ID).</summary>
    [JsonPropertyName("images")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ImageReference>? ImageReferences { get; set; }

    /// <summary>Optional mask image data for edit requests; never serialized.</summary>
    [JsonIgnore]
    public byte[]? Mask { get; set; }

    /// <summary>Reference form for the edit mask.</summary>
    [JsonPropertyName("mask")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageReference? MaskReference { get; set; }

    /// <summary>The number of images to generate.</summary>
    [JsonPropertyName("n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? N { get; set; }

    /// <summary>The size of the generated images.</summary>
    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; set; }

    /// <summary>The quality of the generated images.</summary>
    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quality { get; set; }

    /// <summary>The response format (url or b64_json).</summary>
    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseFormat { get; set; }

    /// <summary>An optional unique identifier for the end-user.</summary>
    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }

    /// <summary>The background type (opaque or transparent) for GPT Image models.</summary>
    [JsonPropertyName("background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Background { get; set; }

    /// <summary>The output image format (png, webp, jpeg) for GPT Image models.</summary>
    [JsonPropertyName("output_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputFormat { get; set; }

    /// <summary>The compression level (0-100) for GPT Image models.</summary>
    [JsonPropertyName("output_compression")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OutputCompression { get; set; }

    /// <summary>The input fidelity for edit requests.</summary>
    [JsonPropertyName("input_fidelity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputFidelity { get; set; }

    /// <summary>The moderation level (low, auto) for generation requests.</summary>
    [JsonPropertyName("moderation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Moderation { get; set; }

    /// <summary>The number of partial images for GPT Image models.</summary>
    [JsonPropertyName("partial_images")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PartialImages { get; set; }

    /// <summary>The style for DALL-E 3 (vivid or natural).</summary>
    [JsonPropertyName("style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Style { get; set; }

    /// <summary>Provider-specific reference image descriptors (Go: json.RawMessage).</summary>
    [JsonPropertyName("subject_reference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SubjectReference { get; set; }

    /// <summary>Provider-specific image dimensions.</summary>
    [JsonPropertyName("aspect_ratio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AspectRatio { get; set; }

    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Width { get; set; }

    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Height { get; set; }

    /// <summary>Enables provider prompt optimization when supported.</summary>
    [JsonPropertyName("prompt_optimizer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PromptOptimizer { get; set; }
}

/// <summary>Port of llm.ImageReference.</summary>
public sealed class ImageReference
{
    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("file_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileId { get; set; }
}

/// <summary>
/// Port of llm.ImageResponse (llm/image.go:66-75). Common fields such as Usage
/// live on the parent <see cref="LlmResponse"/>.
/// </summary>
public sealed class ImageResponse
{
    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("data")]
    public List<ImageData> Data { get; set; } = [];

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
}

/// <summary>Port of llm.ImageData (llm/image.go:77-82).</summary>
public sealed class ImageData
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
