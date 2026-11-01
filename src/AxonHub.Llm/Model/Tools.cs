using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm;

/// <summary>Port of llm.Tool (llm/tools.go:11-36).</summary>
public sealed class Tool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("function")]
    public Function Function { get; set; } = new();

    [JsonPropertyName("image_generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageGeneration? ImageGeneration { get; set; }

    [JsonPropertyName("web_search")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebSearch? WebSearch { get; set; }

    [JsonPropertyName("google")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoogleTools? Google { get; set; }

    [JsonPropertyName("response_custom_tool")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseCustomTool? ResponseCustomTool { get; set; }

    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CacheControl? CacheControl { get; set; }

    /// <summary>Go struct copy semantics: <c>req.Tools[i]</c> after <c>slices.Clone</c>.</summary>
    public Tool ShallowClone() => (Tool)MemberwiseClone();
}

/// <summary>
/// Port of the Google-native tool helpers (llm/tools.go:241-268). Google
/// native tools follow the <c>google_*</c> naming convention and are only
/// supported by the native Gemini API formats, not by the OpenAI-compatible
/// <c>gemini_openai</c> endpoint.
/// </summary>
public static class GoogleToolFilter
{
    /// <summary>Port of <c>llm.ContainsGoogleNativeTools</c>.</summary>
    public static bool ContainsGoogleNativeTools(IEnumerable<Tool>? tools)
    {
        if (tools is null)
        {
            return false;
        }

        foreach (var tool in tools)
        {
            if (IsGoogleNativeTool(tool))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of <c>llm.IsGoogleNativeTool</c>: the <c>google_</c> prefix.</summary>
    public static bool IsGoogleNativeTool(Tool tool)
        => tool.Type.StartsWith("google_", StringComparison.Ordinal);

    /// <summary>
    /// Port of <c>llm.FilterGoogleNativeTools</c>. The input list is returned
    /// unchanged when it is null or empty; otherwise a freshly built list
    /// without the Google-native tools is returned (which may be empty).
    /// </summary>
    public static List<Tool>? FilterGoogleNativeTools(List<Tool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return tools;
        }

        var filtered = new List<Tool>(tools.Count);
        foreach (var tool in tools)
        {
            if (!IsGoogleNativeTool(tool))
            {
                filtered.Add(tool);
            }
        }

        return filtered;
    }
}

/// <summary>Port of llm.Function.</summary>
public sealed class Function
{
    [JsonPropertyName("namespace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Namespace { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Parameters { get; set; }

    [JsonPropertyName("parametersJsonSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ParametersJsonSchema { get; set; }

    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; set; }

    /// <summary>Go struct copy semantics for the nested Function value.</summary>
    public Function ShallowClone() => (Function)MemberwiseClone();
}

/// <summary>Port of llm.FunctionCall.</summary>
public sealed class FunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("namespace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Namespace { get; set; }

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Go struct copy semantics for the nested FunctionCall value.</summary>
    public FunctionCall ShallowClone() => (FunctionCall)MemberwiseClone();
}

/// <summary>Port of llm.ToolCall.</summary>
public sealed class ToolCall
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("function")]
    public FunctionCall Function { get; set; } = new();

    [JsonPropertyName("response_custom_tool_call")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseCustomToolCall? ResponseCustomToolCall { get; set; }

    /// <summary>Go: no omitempty — index 0 must survive.</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CacheControl? CacheControl { get; set; }

    [JsonPropertyName("transformer_metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? TransformerMetadata { get; set; }

    /// <summary>Go struct copy semantics: <c>slices.Clone(src.ToolCalls)</c>.</summary>
    public ToolCall ShallowClone() => (ToolCall)MemberwiseClone();
}

/// <summary>Port of llm.ToolFunction.</summary>
public sealed class ToolFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Port of llm.ToolOption.</summary>
public sealed class ToolOption
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Port of llm.NamedToolChoice.</summary>
public sealed class NamedToolChoice
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("function")]
    public ToolFunction Function { get; set; } = new();
}

/// <summary>
/// Port of llm.ToolChoice (llm/tools.go:111-181).
/// Wire shape: bare string, named-tool object, tool list, or the
/// allowed_tools envelope.
/// </summary>
[JsonConverter(typeof(ToolChoiceJsonConverter))]
public sealed class ToolChoice
{
    public string? ToolChoiceMode { get; set; }

    public NamedToolChoice? NamedToolChoice { get; set; }

    public List<ToolOption>? Tools { get; set; }
}

public sealed class ToolChoiceJsonConverter : JsonConverter<ToolChoice>
{
    private sealed class AllowedToolsChoice
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("mode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Mode { get; set; }

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ToolOption>? Tools { get; set; }
    }

    public override ToolChoice? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement.Clone();

        if (root.ValueKind == JsonValueKind.String)
        {
            return new ToolChoice { ToolChoiceMode = root.GetString() };
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            return new ToolChoice { Tools = root.Deserialize<List<ToolOption>>(options) };
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            var named = root.Deserialize<NamedToolChoice>(options) ?? throw new JsonException("invalid tool choice type");
            if (named.Type == "allowed_tools")
            {
                var allowed = root.Deserialize<AllowedToolsChoice>(options);
                return new ToolChoice
                {
                    ToolChoiceMode = allowed?.Mode,
                    NamedToolChoice = named,
                    Tools = allowed?.Tools,
                };
            }

            return new ToolChoice { NamedToolChoice = named };
        }

        throw new JsonException("invalid tool choice type");
    }

    public override void Write(Utf8JsonWriter writer, ToolChoice value, JsonSerializerOptions options)
    {
        if (value.NamedToolChoice is not null && value.NamedToolChoice.Type == "allowed_tools")
        {
            JsonSerializer.Serialize(writer, new AllowedToolsChoice
            {
                Type = value.NamedToolChoice.Type,
                Mode = value.ToolChoiceMode,
                Tools = value.Tools,
            }, options);
            return;
        }

        if (value.ToolChoiceMode is not null)
        {
            JsonSerializer.Serialize(writer, value.ToolChoiceMode, options);
            return;
        }

        if (value.Tools is { Count: > 0 })
        {
            JsonSerializer.Serialize(writer, value.Tools, options);
            return;
        }

        JsonSerializer.Serialize(writer, value.NamedToolChoice, options);
    }
}

/// <summary>Port of llm.ResponseFormat.</summary>
public sealed class ResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("json_schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? JsonSchema { get; set; }
}

/// <summary>Port of llm.ImageGeneration.</summary>
public sealed class ImageGeneration
{
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Background { get; set; }

    [JsonPropertyName("input_fidelity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputFidelity { get; set; }

    [JsonPropertyName("input_image_mask")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? InputImageMask { get; set; }

    [JsonPropertyName("moderation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Moderation { get; set; }

    [JsonPropertyName("output_compression")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OutputCompression { get; set; }

    [JsonPropertyName("output_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("partial_images")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PartialImages { get; set; }

    [JsonPropertyName("n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? N { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quality { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; set; }

    [JsonPropertyName("style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Style { get; set; }

    [JsonPropertyName("watermark")]
    public bool Watermark { get; set; }
}

/// <summary>Port of llm.GoogleTools.</summary>
public sealed class GoogleTools
{
    [JsonPropertyName("search")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoogleSearch? Search { get; set; }

    [JsonPropertyName("code_execution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoogleCodeExecution? CodeExecution { get; set; }

    [JsonPropertyName("url_context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoogleUrlContext? UrlContext { get; set; }
}

public sealed class GoogleSearch;

public sealed class GoogleCodeExecution;

public sealed class GoogleUrlContext;

/// <summary>Port of llm.WebSearch.</summary>
public sealed class WebSearch
{
    [JsonPropertyName("max_uses")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxUses { get; set; }

    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; set; }

    [JsonPropertyName("allowed_domains")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedDomains { get; set; }

    [JsonPropertyName("blocked_domains")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? BlockedDomains { get; set; }

    [JsonPropertyName("user_location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebSearchToolUserLocation? UserLocation { get; set; }
}

/// <summary>Port of llm.WebSearchToolUserLocation.</summary>
public sealed class WebSearchToolUserLocation
{
    [JsonPropertyName("city")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? City { get; set; }

    [JsonPropertyName("country")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Country { get; set; }

    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; set; }

    [JsonPropertyName("timezone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Timezone { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

/// <summary>Port of llm.ResponseCustomTool.</summary>
public sealed class ResponseCustomTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseCustomToolFormat? Format { get; set; }
}

/// <summary>Port of llm.ResponseCustomToolFormat.</summary>
public sealed class ResponseCustomToolFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("syntax")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Syntax { get; set; }

    [JsonPropertyName("definition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Definition { get; set; }
}

/// <summary>Port of llm.ResponseCustomToolCall.</summary>
public sealed class ResponseCustomToolCall
{
    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("input")]
    public string Input { get; set; } = string.Empty;
}
