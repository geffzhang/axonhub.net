using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>Port of the Responses wire constants (llm/transformer/openai/responses/model.go).</summary>
internal static class ResponsesKeys
{
    /// <summary>Go: <c>responsesWebSearchCallsTransformerMetadataKey</c>.</summary>
    public const string WebSearchCallsTransformerMetadataKey = "openai_responses_web_search_calls";

    /// <summary>Go: <c>responsesReasoningItemTransformerMetadataKey</c>.</summary>
    public const string ReasoningItemTransformerMetadataKey = "openai_responses_reasoning_item";

    /// <summary>Go: <c>responsesTerminalDetailsTransformerMetadataKey</c>.</summary>
    public const string TerminalDetailsTransformerMetadataKey = "openai_responses_terminal_details";

    /// <summary>Header that forces Responses Lite behaviour on the inbound path (inbound.go).</summary>
    public const string ResponsesLiteHeader = "X-Openai-Internal-Codex-Responses-Lite";
}

/// <summary>Port of responses.Tool (model.go:28-71). Strings that Go emits with omitempty are nullable here; builders map "" to null.</summary>
public sealed class ResponsesTool
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Parameters { get; set; }

    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResponsesTool>? Tools { get; set; }

    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesCustomToolFormat? Format { get; set; }

    [JsonPropertyName("filters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesWebSearchFilters? Filters { get; set; }

    [JsonPropertyName("user_location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesWebSearchUserLocation? UserLocation { get; set; }

    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Action { get; set; }

    [JsonPropertyName("background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Background { get; set; }

    [JsonPropertyName("input_fidelity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputFidelity { get; set; }

    [JsonPropertyName("input_image_mask")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? InputImageMask { get; set; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

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

    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quality { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; set; }
}

/// <summary>Port of responses.WebSearchFilters.</summary>
public sealed class ResponsesWebSearchFilters
{
    [JsonPropertyName("allowed_domains")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedDomains { get; set; }
}

/// <summary>Port of responses.WebSearchUserLocation.</summary>
public sealed class ResponsesWebSearchUserLocation
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

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
}

/// <summary>Port of responses.CustomToolFormat (model.go:86-93).</summary>
public sealed class ResponsesCustomToolFormat
{
    /// <summary>The format type, e.g. "grammar". Always emitted, as in the reference.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("syntax")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Syntax { get; set; }

    [JsonPropertyName("definition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Definition { get; set; }
}

/// <summary>
/// Port of responses.Request (model.go:97-162). Property order matches the Go
/// struct so attribute-based serialization emits keys in reference order.
/// <c>input</c>/<c>tools</c> carry <c>omitzero</c> in Go: builders keep them
/// null unless they hold content, so null and Go's omitted zero are equivalent.
/// </summary>
public sealed class ResponsesRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("instructions")]
    public string Instructions { get; set; } = string.Empty;

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesInput? Input { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResponsesTool>? Tools { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Background { get; set; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }

    [JsonPropertyName("store")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Store { get; set; }

    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("safety_identifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SafetyIdentifier { get; set; }

    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Metadata { get; set; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxOutputTokens { get; set; }

    [JsonPropertyName("max_tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxToolCalls { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesTextOptions? Text { get; set; }

    [JsonPropertyName("include")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Include { get; set; }

    [JsonPropertyName("previous_response_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousResponseId { get; set; }

    [JsonPropertyName("prompt_cache_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromptCacheKey { get; set; }

    [JsonPropertyName("prompt_cache_retention")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromptCacheRetention { get; set; }

    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesReasoning? Reasoning { get; set; }

    [JsonPropertyName("stream_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesStreamOptions? StreamOptions { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesToolChoice? ToolChoice { get; set; }

    [JsonPropertyName("truncation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Truncation { get; set; }

    [JsonPropertyName("top_logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TopLogprobs { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }
}

/// <summary>Port of responses.Prompt (model.go:165-169).</summary>
public sealed class ResponsesPrompt
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("variables")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Variables { get; set; }
}

/// <summary>Port of responses.Reasoning (request side, model.go:172-184).</summary>
public sealed class ResponsesReasoning
{
    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; set; }

    [JsonPropertyName("effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; set; }

    [JsonPropertyName("generate_summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerateSummary { get; set; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxTokens { get; set; }
}

/// <summary>Port of responses.StreamOptions (model.go:187-190).</summary>
public sealed class ResponsesStreamOptions
{
    [JsonPropertyName("include_obfuscation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IncludeObfuscation { get; set; }
}

/// <summary>
/// Port of responses.ToolChoice (model.go:193-234): a bare string when only
/// <see cref="Mode"/> is set, an object otherwise.
/// </summary>
[JsonConverter(typeof(ResponsesToolChoiceJsonConverter))]
public sealed class ResponsesToolChoice
{
    public string? Mode { get; set; }

    public string? Type { get; set; }

    public string? Name { get; set; }

    public List<ResponsesToolOption>? Tools { get; set; }
}

public sealed class ResponsesToolChoiceJsonConverter : JsonConverter<ResponsesToolChoice>
{
    private sealed class Alias
    {
        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("tools")]
        public List<ResponsesToolOption>? Tools { get; set; }
    }

    public override ResponsesToolChoice? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Go: xjson.To[string] succeeds for null as well (no-op decode), so
        // null decodes as Mode="".
        if (reader.TokenType is JsonTokenType.Null or JsonTokenType.String)
        {
            return new ResponsesToolChoice { Mode = reader.TokenType == JsonTokenType.String ? reader.GetString()! : string.Empty };
        }

        try
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var alias = doc.RootElement.Deserialize<Alias>(options)!;
            return new ResponsesToolChoice
            {
                Mode = alias.Mode,
                Type = alias.Type,
                Name = alias.Name,
                Tools = alias.Tools,
            };
        }
        catch (JsonException)
        {
            throw new JsonException("invalid tool choice type");
        }
    }

    public override void Write(Utf8JsonWriter writer, ResponsesToolChoice value, JsonSerializerOptions options)
    {
        if (value.Mode is not null && value.Type is null && value.Name is null && value.Tools is not { Count: > 0 })
        {
            writer.WriteStringValue(value.Mode);
            return;
        }

        writer.WriteStartObject();
        if (value.Mode is not null)
        {
            writer.WritePropertyName("mode");
            writer.WriteStringValue(value.Mode);
        }

        if (value.Type is not null)
        {
            writer.WritePropertyName("type");
            writer.WriteStringValue(value.Type);
        }

        if (value.Name is not null)
        {
            writer.WritePropertyName("name");
            writer.WriteStringValue(value.Name);
        }

        if (value.Tools is { Count: > 0 })
        {
            writer.WritePropertyName("tools");
            JsonSerializer.Serialize(writer, value.Tools, options);
        }

        writer.WriteEndObject();
    }
}

/// <summary>Port of responses.ToolOption (model.go:205-208).</summary>
public sealed class ResponsesToolOption
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Port of responses.ResponseToolChoice (model.go:237-278): a string or an
/// object on the wire. StringValue wins when both representations are set.
/// </summary>
[JsonConverter(typeof(ResponsesResponseToolChoiceJsonConverter))]
public sealed class ResponsesResponseToolChoice
{
    public string StringValue { get; set; } = string.Empty;

    public ResponsesToolChoice? ObjectValue { get; set; }
}

public sealed class ResponsesResponseToolChoiceJsonConverter : JsonConverter<ResponsesResponseToolChoice>
{
    public override ResponsesResponseToolChoice? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Go: null unmarshals into the string form as "" (no-op decode).
        if (reader.TokenType is JsonTokenType.Null or JsonTokenType.String)
        {
            return new ResponsesResponseToolChoice
            {
                StringValue = reader.TokenType == JsonTokenType.String ? reader.GetString()! : string.Empty,
            };
        }

        try
        {
            // Go decodes the object form through ToolChoice's own custom
            // unmarshaler; on failure the final error is the same either way.
            var obj = JsonSerializer.Deserialize<ResponsesToolChoice>(ref reader, options)!;
            return new ResponsesResponseToolChoice { ObjectValue = obj };
        }
        catch (JsonException)
        {
            throw new JsonException("tool_choice must be a string or object");
        }
    }

    public override void Write(Utf8JsonWriter writer, ResponsesResponseToolChoice value, JsonSerializerOptions options)
    {
        if (value.StringValue.Length > 0)
        {
            writer.WriteStringValue(value.StringValue);
            return;
        }

        if (value.ObjectValue is not null)
        {
            JsonSerializer.Serialize(writer, value.ObjectValue, options);
            return;
        }

        writer.WriteNullValue();
    }
}

/// <summary>Port of responses.Conversation (request side, model.go:281-283).</summary>
public sealed class ResponsesConversation
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }
}

/// <summary>Port of responses.ResponseConversation (model.go:287-290).</summary>
public sealed class ResponsesResponseConversation
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

/// <summary>Port of responses.ResponseIncompleteDetails (model.go:293-297).</summary>
public sealed class ResponsesResponseIncompleteDetails
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Port of responses.ResponseReasoning (model.go:300-309).</summary>
public sealed class ResponsesResponseReasoning
{
    [JsonPropertyName("effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; set; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    [JsonPropertyName("generate_summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerateSummary { get; set; }
}

/// <summary>Port of responses.TextOptions (model.go:311-321).</summary>
public sealed class ResponsesTextOptions
{
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesTextFormat? Format { get; set; }

    [JsonPropertyName("verbosity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verbosity { get; set; }
}

/// <summary>Port of responses.TextFormat (model.go:324-335).</summary>
public sealed class ResponsesTextFormat
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>Go: <c>json.RawMessage</c>. Callers re-compact with <see cref="GoJson.CompactElement"/> the way Go does on marshal.</summary>
    [JsonPropertyName("schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Schema { get; set; }

    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; set; }
}

/// <summary>
/// Port of responses.Input (model.go:337-370): a string or an array of items.
/// Text wins when both are set; a JSON null decodes as Text="" and re-marshals
/// as the empty string.
/// </summary>
[JsonConverter(typeof(ResponsesInputJsonConverter))]
public sealed class ResponsesInput
{
    public string? Text { get; set; }

    public List<ResponsesItem>? Items { get; set; }
}

public sealed class ResponsesInputJsonConverter : JsonConverter<ResponsesInput>
{
    public override ResponsesInput? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            // Go: json.Unmarshal("null", &string) is a no-op success, so Text
            // becomes a pointer to "" and the value marshals back as "".
            return new ResponsesInput { Text = string.Empty };
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new ResponsesInput { Text = reader.GetString()! };
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<ResponsesItem>>(ref reader, options)!;
                return new ResponsesInput { Items = items };
            }
            catch (JsonException)
            {
                throw new JsonException("invalid input: invalid request");
            }
        }

        throw new JsonException("invalid input: invalid request");
    }

    public override void Write(Utf8JsonWriter writer, ResponsesInput value, JsonSerializerOptions options)
    {
        if (value.Text is not null)
        {
            writer.WriteStringValue(value.Text);
            return;
        }

        JsonSerializer.Serialize(writer, value.Items, options);
    }
}

/// <summary>
/// Port of responses.Annotation (model.go:372-409): legacy <c>url</c>/<c>title</c>
/// fields merge into <c>url_citation</c> on decode and never re-marshal.
/// </summary>
[JsonConverter(typeof(ResponsesAnnotationJsonConverter))]
public sealed class ResponsesAnnotation
{
    public string? Type { get; set; }

    public long? StartIndex { get; set; }

    public long? EndIndex { get; set; }

    public ResponsesUrlCitation? UrlCitation { get; set; }
}

public sealed class ResponsesAnnotationJsonConverter : JsonConverter<ResponsesAnnotation>
{
    private sealed class Alias
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("start_index")]
        public long? StartIndex { get; set; }

        [JsonPropertyName("end_index")]
        public long? EndIndex { get; set; }

        [JsonPropertyName("url_citation")]
        public ResponsesUrlCitation? UrlCitation { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }

    public override ResponsesAnnotation? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Go: null into a struct is a no-op success, producing a zero value.
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new ResponsesAnnotation();
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var alias = doc.RootElement.Deserialize<Alias>(options)!;

        var annotation = new ResponsesAnnotation
        {
            Type = alias.Type,
            StartIndex = alias.StartIndex,
            EndIndex = alias.EndIndex,
            UrlCitation = alias.UrlCitation,
        };

        if (annotation.UrlCitation is null && (alias.Url is not null || alias.Title is not null))
        {
            annotation.UrlCitation = new ResponsesUrlCitation
            {
                Url = alias.Url ?? string.Empty,
                Title = alias.Title ?? string.Empty,
            };
        }

        return annotation;
    }

    public override void Write(Utf8JsonWriter writer, ResponsesAnnotation value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (!string.IsNullOrEmpty(value.Type))
        {
            writer.WritePropertyName("type");
            writer.WriteStringValue(value.Type);
        }

        if (value.StartIndex is { } startIndex)
        {
            writer.WritePropertyName("start_index");
            writer.WriteNumberValue(startIndex);
        }

        if (value.EndIndex is { } endIndex)
        {
            writer.WritePropertyName("end_index");
            writer.WriteNumberValue(endIndex);
        }

        if (value.UrlCitation is { } urlCitation)
        {
            writer.WritePropertyName("url_citation");
            JsonSerializer.Serialize(writer, urlCitation, options);
        }

        writer.WriteEndObject();
    }
}

/// <summary>Port of responses.URLCitation (model.go:412-417).</summary>
public sealed class ResponsesUrlCitation
{
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }
}

/// <summary>Port of responses.WebSearchSource (model.go:434-439).</summary>
public sealed class ResponsesWebSearchSource
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }
}

/// <summary>Port of responses.WebSearchAction (model.go:440-445).</summary>
public sealed class ResponsesWebSearchAction
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("query")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Query { get; set; }

    [JsonPropertyName("queries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Queries { get; set; }

    [JsonPropertyName("sources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResponsesWebSearchSource>? Sources { get; set; }
}

/// <summary>
/// Port of responses.ItemAction (model.go:450-510): a bare string for
/// image_generation_call actions or a structured web_search action.
/// </summary>
[JsonConverter(typeof(ResponsesItemActionJsonConverter))]
public sealed class ResponsesItemAction
{
    public string ImageGenerationAction { get; set; } = string.Empty;

    public ResponsesWebSearchAction? WebSearch { get; set; }

    /// <summary>Go: <c>NewImageGenerationAction</c>.</summary>
    public static ResponsesItemAction NewImageGenerationAction(string action) => new() { ImageGenerationAction = action };

    /// <summary>Go: <c>NewWebSearchAction</c>.</summary>
    public static ResponsesItemAction NewWebSearchAction(ResponsesWebSearchAction? action) => new() { WebSearch = action };

    /// <summary>Go: <c>(a *ItemAction) IsImageGeneration</c>.</summary>
    public bool IsImageGeneration() => ImageGenerationAction.Length > 0;

    /// <summary>Go: <c>(a *ItemAction) IsWebSearch</c>.</summary>
    public bool IsWebSearch() => WebSearch is not null;
}

public sealed class ResponsesItemActionJsonConverter : JsonConverter<ResponsesItemAction>
{
    public override ResponsesItemAction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Go: null unmarshals into the string form as "" (no-op decode).
        if (reader.TokenType is JsonTokenType.Null or JsonTokenType.String)
        {
            return new ResponsesItemAction
            {
                ImageGenerationAction = reader.TokenType == JsonTokenType.String ? reader.GetString()! : string.Empty,
            };
        }

        try
        {
            var action = JsonSerializer.Deserialize<ResponsesWebSearchAction>(ref reader, options)!;
            return new ResponsesItemAction { WebSearch = action };
        }
        catch (JsonException)
        {
            throw new JsonException("action must be a string or object");
        }
    }

    public override void Write(Utf8JsonWriter writer, ResponsesItemAction value, JsonSerializerOptions options)
    {
        if (value.ImageGenerationAction.Length > 0)
        {
            writer.WriteStringValue(value.ImageGenerationAction);
            return;
        }

        if (value.WebSearch is not null)
        {
            JsonSerializer.Serialize(writer, value.WebSearch, options);
            return;
        }

        writer.WriteNullValue();
    }
}

/// <summary>
/// Port of responses.Item (model.go:515-701). The custom converter reproduces
/// the per-type marshal rules: <c>function_call</c> forces <c>arguments</c>,
/// <c>custom_tool_call</c> forces <c>input</c>, <c>compaction</c> forces
/// <c>encrypted_content</c> after the embedded alias fields (duplicate keys
/// included), and <c>reasoning</c> forces a <c>summary</c> array while
/// non-reasoning items drop it. <c>arguments</c> decodes as a string or a
/// Go-compacted raw JSON fragment.
/// </summary>
[JsonConverter(typeof(ResponsesItemJsonConverter))]
public sealed class ResponsesItem
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public List<ResponsesAnnotation>? Annotations { get; set; }

    public string Role { get; set; } = string.Empty;

    public ResponsesInput? Content { get; set; }

    public string? Status { get; set; }

    public string? ImageUrl { get; set; }

    public string? Detail { get; set; }

    public string? FileData { get; set; }

    public string? FileId { get; set; }

    public string? FileUrl { get; set; }

    public string? Filename { get; set; }

    public string? Text { get; set; }

    public string? Background { get; set; }

    public string? OutputFormat { get; set; }

    public string? Quality { get; set; }

    public string? Size { get; set; }

    public string? Result { get; set; }

    public string CallId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string? Input { get; set; }

    public ResponsesInput? Output { get; set; }

    public List<ResponsesReasoningSummary>? Summary { get; set; }

    public ResponsesPolymorphicReasoningContent? ReasoningContent { get; set; }

    public string? EncryptedContent { get; set; }

    public ResponsesItemAction? Action { get; set; }

    public string? CreatedBy { get; set; }

    /// <summary>Go: <c>(item Item) isOutputMessageContent</c>.</summary>
    public bool IsOutputMessageContent()
    {
        if (Content is not { Items.Count: > 0 })
        {
            return false;
        }

        foreach (var ci in Content.Items)
        {
            if (ci.Type == "output_text")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Go: <c>(item Item) GetContentItems</c>.</summary>
    public List<ResponsesContentItem>? GetContentItems()
    {
        if (Content is not { Items.Count: > 0 })
        {
            return null;
        }

        var result = new List<ResponsesContentItem>(Content.Items.Count);
        foreach (var ci in Content.Items)
        {
            result.Add(new ResponsesContentItem
            {
                Type = ci.Type,
                Text = ci.Text ?? string.Empty,
                // Go: append([]Annotation(nil), empty...) yields nil, not [].
                Annotations = ci.Annotations is { Count: > 0 } ? [.. ci.Annotations] : null,
            });
        }

        return result;
    }

    /// <summary>Go: <c>(item *Item) SetContentItems</c>. Empty input is a no-op.</summary>
    public void SetContentItems(List<ResponsesContentItem>? items)
    {
        if (items is not { Count: > 0 })
        {
            return;
        }

        var contentItems = new List<ResponsesItem>(items.Count);
        foreach (var ci in items)
        {
            contentItems.Add(new ResponsesItem
            {
                Type = ci.Type,
                Text = ci.Text,
                Annotations = ci.Annotations is { Count: > 0 } ? [.. ci.Annotations] : null,
            });
        }

        Content = new ResponsesInput { Items = contentItems };
    }
}

public sealed class ResponsesItemJsonConverter : JsonConverter<ResponsesItem>
{
    private sealed class Alias
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("annotations")]
        public List<ResponsesAnnotation>? Annotations { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("content")]
        public ResponsesInput? Content { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("image_url")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("file_data")]
        public string? FileData { get; set; }

        [JsonPropertyName("file_id")]
        public string? FileId { get; set; }

        [JsonPropertyName("file_url")]
        public string? FileUrl { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("background")]
        public string? Background { get; set; }

        [JsonPropertyName("output_format")]
        public string? OutputFormat { get; set; }

        [JsonPropertyName("quality")]
        public string? Quality { get; set; }

        [JsonPropertyName("size")]
        public string? Size { get; set; }

        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("call_id")]
        public string? CallId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("namespace")]
        public string? Namespace { get; set; }

        [JsonPropertyName("arguments")]
        public JsonElement? ArgumentsElement { get; set; }

        [JsonPropertyName("input")]
        public string? Input { get; set; }

        [JsonPropertyName("output")]
        public ResponsesInput? Output { get; set; }

        [JsonPropertyName("summary")]
        public List<ResponsesReasoningSummary>? Summary { get; set; }

        [JsonPropertyName("reasoning_content")]
        public ResponsesPolymorphicReasoningContent? ReasoningContent { get; set; }

        [JsonPropertyName("encrypted_content")]
        public string? EncryptedContent { get; set; }

        [JsonPropertyName("action")]
        public ResponsesItemAction? Action { get; set; }

        [JsonPropertyName("created_by")]
        public string? CreatedBy { get; set; }
    }

    public override ResponsesItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Go: null into the raw struct is a no-op success and arguments is
        // empty, so a null element decodes as a zero item.
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new ResponsesItem();
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var alias = doc.RootElement.Deserialize<Alias>(options)!;

        var item = new ResponsesItem
        {
            Id = alias.Id ?? string.Empty,
            Type = alias.Type ?? string.Empty,
            Annotations = alias.Annotations,
            Role = alias.Role ?? string.Empty,
            Content = alias.Content,
            Status = alias.Status,
            ImageUrl = alias.ImageUrl,
            Detail = alias.Detail,
            FileData = alias.FileData,
            FileId = alias.FileId,
            FileUrl = alias.FileUrl,
            Filename = alias.Filename,
            Text = alias.Text,
            Background = alias.Background,
            OutputFormat = alias.OutputFormat,
            Quality = alias.Quality,
            Size = alias.Size,
            Result = alias.Result,
            CallId = alias.CallId ?? string.Empty,
            Name = alias.Name ?? string.Empty,
            Namespace = alias.Namespace ?? string.Empty,
            Input = alias.Input,
            Output = alias.Output,
            Summary = alias.Summary,
            ReasoningContent = alias.ReasoningContent,
            EncryptedContent = alias.EncryptedContent,
            Action = alias.Action,
            CreatedBy = alias.CreatedBy,
        };

        // Go: len(raw.Arguments)==0 or "null" leaves Arguments empty; a string
        // is taken as-is; anything else is stored json.Compact-compacted.
        if (alias.ArgumentsElement is { } arguments && arguments.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            item.Arguments = arguments.ValueKind == JsonValueKind.String
                ? arguments.GetString() ?? string.Empty
                : Encoding.UTF8.GetString(GoJson.Compact(Encoding.UTF8.GetBytes(arguments.GetRawText())));
        }

        return item;
    }

    public override void Write(Utf8JsonWriter writer, ResponsesItem value, JsonSerializerOptions options)
    {
        var type = value.Type;

        writer.WriteStartObject();
        if (value.Id.Length > 0)
        {
            writer.WritePropertyName("id");
            writer.WriteStringValue(value.Id);
        }

        if (type.Length > 0)
        {
            writer.WritePropertyName("type");
            writer.WriteStringValue(type);
        }

        // Go tag is `json:"annotations,omitzero"`: nil is omitted, a non-nil
        // empty slice is emitted as [].
        if (value.Annotations is not null)
        {
            writer.WritePropertyName("annotations");
            JsonSerializer.Serialize(writer, value.Annotations, options);
        }

        if (value.Role.Length > 0)
        {
            writer.WritePropertyName("role");
            writer.WriteStringValue(value.Role);
        }

        if (value.Content is { } content)
        {
            writer.WritePropertyName("content");
            JsonSerializer.Serialize(writer, content, options);
        }

        if (value.Status is { } status)
        {
            writer.WritePropertyName("status");
            writer.WriteStringValue(status);
        }

        if (value.ImageUrl is { } imageUrl)
        {
            writer.WritePropertyName("image_url");
            writer.WriteStringValue(imageUrl);
        }

        if (value.Detail is { } detail)
        {
            writer.WritePropertyName("detail");
            writer.WriteStringValue(detail);
        }

        if (value.FileData is { } fileData)
        {
            writer.WritePropertyName("file_data");
            writer.WriteStringValue(fileData);
        }

        if (value.FileId is { } fileId)
        {
            writer.WritePropertyName("file_id");
            writer.WriteStringValue(fileId);
        }

        if (value.FileUrl is { } fileUrl)
        {
            writer.WritePropertyName("file_url");
            writer.WriteStringValue(fileUrl);
        }

        if (value.Filename is { } filename)
        {
            writer.WritePropertyName("filename");
            writer.WriteStringValue(filename);
        }

        if (value.Text is { } text)
        {
            writer.WritePropertyName("text");
            writer.WriteStringValue(text);
        }

        if (value.Background is { } background)
        {
            writer.WritePropertyName("background");
            writer.WriteStringValue(background);
        }

        if (value.OutputFormat is { } outputFormat)
        {
            writer.WritePropertyName("output_format");
            writer.WriteStringValue(outputFormat);
        }

        if (value.Quality is { } quality)
        {
            writer.WritePropertyName("quality");
            writer.WriteStringValue(quality);
        }

        if (value.Size is { } size)
        {
            writer.WritePropertyName("size");
            writer.WriteStringValue(size);
        }

        if (value.Result is { } result)
        {
            writer.WritePropertyName("result");
            writer.WriteStringValue(result);
        }

        if (value.CallId.Length > 0)
        {
            writer.WritePropertyName("call_id");
            writer.WriteStringValue(value.CallId);
        }

        if (value.Name.Length > 0)
        {
            writer.WritePropertyName("name");
            writer.WriteStringValue(value.Name);
        }

        if (value.Namespace.Length > 0)
        {
            writer.WritePropertyName("namespace");
            writer.WriteStringValue(value.Namespace);
        }

        if (value.Arguments.Length > 0)
        {
            writer.WritePropertyName("arguments");
            writer.WriteStringValue(value.Arguments);
        }

        if (value.Input is { } input)
        {
            writer.WritePropertyName("input");
            writer.WriteStringValue(input);
        }

        if (value.Output is { } output)
        {
            writer.WritePropertyName("output");
            JsonSerializer.Serialize(writer, output, options);
        }

        // Go marshal rules: summary survives on function_call/custom_tool_call/
        // compaction/reasoning items (omitempty), is nil'd for everything else,
        // and reasoning forces a trailing summary array.
        if (type is "function_call" or "custom_tool_call" or "compaction" or "reasoning")
        {
            if (value.Summary is { Count: > 0 })
            {
                writer.WritePropertyName("summary");
                JsonSerializer.Serialize(writer, value.Summary, options);
            }
        }

        if (value.ReasoningContent is { } reasoningContent)
        {
            writer.WritePropertyName("reasoning_content");
            JsonSerializer.Serialize(writer, reasoningContent, options);
        }

        if (value.EncryptedContent is { } encryptedContent)
        {
            writer.WritePropertyName("encrypted_content");
            writer.WriteStringValue(encryptedContent);
        }

        if (value.Action is { } action)
        {
            writer.WritePropertyName("action");
            JsonSerializer.Serialize(writer, action, options);
        }

        if (value.CreatedBy is { } createdBy)
        {
            writer.WritePropertyName("created_by");
            writer.WriteStringValue(createdBy);
        }

        switch (type)
        {
            case "function_call":
                writer.WritePropertyName("arguments");
                writer.WriteStringValue(value.Arguments);
                break;
            case "custom_tool_call":
                writer.WritePropertyName("input");
                writer.WriteStringValue(value.Input ?? string.Empty);
                break;
            case "compaction":
                writer.WritePropertyName("encrypted_content");
                writer.WriteStringValue(value.EncryptedContent ?? string.Empty);
                break;
            case "reasoning":
                writer.WritePropertyName("summary");
                JsonSerializer.Serialize(writer, value.Summary ?? [], options);
                break;
        }

        writer.WriteEndObject();
    }
}

/// <summary>Port of responses.ReasoningSummary (model.go:762-767).</summary>
public sealed class ResponsesReasoningSummary
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// Port of responses.PolymorphicReasoningContent (model.go:773-801): a string
/// or an array of reasoning text items. Null decodes as Text="".
/// </summary>
[JsonConverter(typeof(ResponsesPolymorphicReasoningContentJsonConverter))]
public sealed class ResponsesPolymorphicReasoningContent
{
    public string? Text { get; set; }

    public List<ResponsesReasoningContent>? Items { get; set; }
}

public sealed class ResponsesPolymorphicReasoningContentJsonConverter : JsonConverter<ResponsesPolymorphicReasoningContent>
{
    public override ResponsesPolymorphicReasoningContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new ResponsesPolymorphicReasoningContent { Text = string.Empty };
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new ResponsesPolymorphicReasoningContent { Text = reader.GetString()! };
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<ResponsesReasoningContent>>(ref reader, options)!;
                return new ResponsesPolymorphicReasoningContent { Items = items };
            }
            catch (JsonException)
            {
                throw new JsonException("invalid reasoning_content: invalid request");
            }
        }

        throw new JsonException("invalid reasoning_content: invalid request");
    }

    public override void Write(Utf8JsonWriter writer, ResponsesPolymorphicReasoningContent value, JsonSerializerOptions options)
    {
        if (value.Text is not null)
        {
            writer.WriteStringValue(value.Text);
            return;
        }

        JsonSerializer.Serialize(writer, value.Items, options);
    }
}

/// <summary>Port of responses.ReasoningContent (model.go:804-809).</summary>
public sealed class ResponsesReasoningContent
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// Port of responses.Response (model.go:811-938). The custom converter owns
/// <c>created_at</c>: it must be a JSON number, accepts float-encoded integral
/// timestamps (1786360449.0, 1.7e9), and rejects strings, non-integral values,
/// and int64 overflow with the reference's exact error text. Every other field
/// uses default decoding, so a created_at type error never masks one.
/// </summary>
[JsonConverter(typeof(ResponsesResponseJsonConverter))]
public sealed class ResponsesResponse
{
    public string Object { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;

    public ResponsesError? Error { get; set; }

    public long CreatedAt { get; set; }

    public string Model { get; set; } = string.Empty;

    public List<ResponsesItem>? Output { get; set; }

    public string? Status { get; set; }

    public ResponsesResponseIncompleteDetails? IncompleteDetails { get; set; }

    public string? Instructions { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }

    public bool? ParallelToolCalls { get; set; }

    public double? Temperature { get; set; }

    public ResponsesResponseToolChoice? ToolChoice { get; set; }

    public List<ResponsesTool>? Tools { get; set; }

    public double? TopP { get; set; }

    public bool? Background { get; set; }

    public ResponsesResponseConversation? Conversation { get; set; }

    public long? MaxOutputTokens { get; set; }

    public long? MaxToolCalls { get; set; }

    public string? PreviousResponseId { get; set; }

    public ResponsesPrompt? Prompt { get; set; }

    public string? PromptCacheKey { get; set; }

    public string? PromptCacheRetention { get; set; }

    public ResponsesResponseReasoning? Reasoning { get; set; }

    public string? SafetyIdentifier { get; set; }

    public string? ServiceTier { get; set; }

    public ResponsesTextOptions? Text { get; set; }

    public long? TopLogprobs { get; set; }

    public string? Truncation { get; set; }

    public ResponsesUsage? Usage { get; set; }

    public string? User { get; set; }
}

public sealed class ResponsesResponseJsonConverter : JsonConverter<ResponsesResponse>
{
    private sealed class Alias
    {
        [JsonPropertyName("object")]
        public string? Object { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResponsesError? Error { get; set; }

        [JsonPropertyName("created_at")]
        public JsonElement? CreatedAt { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("output")]
        public List<ResponsesItem>? Output { get; set; }

        [JsonPropertyName("status")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Status { get; set; }

        [JsonPropertyName("incomplete_details")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResponsesResponseIncompleteDetails? IncompleteDetails { get; set; }

        [JsonPropertyName("instructions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Instructions { get; set; }

        [JsonPropertyName("metadata")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Metadata { get; set; }

        [JsonPropertyName("parallel_tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ParallelToolCalls { get; set; }

        [JsonPropertyName("temperature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Temperature { get; set; }

        [JsonPropertyName("tool_choice")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResponsesResponseToolChoice? ToolChoice { get; set; }

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ResponsesTool>? Tools { get; set; }

        [JsonPropertyName("top_p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? TopP { get; set; }

        [JsonPropertyName("background")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Background { get; set; }

        [JsonPropertyName("conversation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResponsesResponseConversation? Conversation { get; set; }

        [JsonPropertyName("max_output_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? MaxOutputTokens { get; set; }

        [JsonPropertyName("max_tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? MaxToolCalls { get; set; }

        [JsonPropertyName("previous_response_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PreviousResponseId { get; set; }

        [JsonPropertyName("prompt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResponsesPrompt? Prompt { get; set; }

        [JsonPropertyName("prompt_cache_key")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PromptCacheKey { get; set; }

        [JsonPropertyName("prompt_cache_retention")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PromptCacheRetention { get; set; }

        [JsonPropertyName("reasoning")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResponsesResponseReasoning? Reasoning { get; set; }

        [JsonPropertyName("safety_identifier")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SafetyIdentifier { get; set; }

        [JsonPropertyName("service_tier")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ServiceTier { get; set; }

        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResponsesTextOptions? Text { get; set; }

        [JsonPropertyName("top_logprobs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? TopLogprobs { get; set; }

        [JsonPropertyName("truncation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Truncation { get; set; }

        [JsonPropertyName("usage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResponsesUsage? Usage { get; set; }

        [JsonPropertyName("user")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? User { get; set; }
    }

    public override ResponsesResponse? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Go: null into the raw struct is a no-op success with no created_at,
        // producing a zero response.
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new ResponsesResponse();
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var alias = doc.RootElement.Deserialize<Alias>(options)!;

        var response = new ResponsesResponse
        {
            Object = alias.Object ?? string.Empty,
            Id = alias.Id ?? string.Empty,
            Error = alias.Error,
            Model = alias.Model ?? string.Empty,
            Output = alias.Output,
            Status = alias.Status,
            IncompleteDetails = alias.IncompleteDetails,
            Instructions = alias.Instructions,
            Metadata = alias.Metadata,
            ParallelToolCalls = alias.ParallelToolCalls,
            Temperature = alias.Temperature,
            ToolChoice = alias.ToolChoice,
            Tools = alias.Tools,
            TopP = alias.TopP,
            Background = alias.Background,
            Conversation = alias.Conversation,
            MaxOutputTokens = alias.MaxOutputTokens,
            MaxToolCalls = alias.MaxToolCalls,
            PreviousResponseId = alias.PreviousResponseId,
            Prompt = alias.Prompt,
            PromptCacheKey = alias.PromptCacheKey,
            PromptCacheRetention = alias.PromptCacheRetention,
            Reasoning = alias.Reasoning,
            SafetyIdentifier = alias.SafetyIdentifier,
            ServiceTier = alias.ServiceTier,
            Text = alias.Text,
            TopLogprobs = alias.TopLogprobs,
            Truncation = alias.Truncation,
            Usage = alias.Usage,
            User = alias.User,
        };

        if (alias.CreatedAt is not { } createdAt || createdAt.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return response;
        }

        var raw = createdAt.GetRawText();

        // created_at must be a JSON number; reject string forms such as
        // "1786360449" so the previous int64 field behaviour is preserved.
        if (raw[0] != '-' && (raw[0] < '0' || raw[0] > '9'))
        {
            throw new JsonException(
                "invalid responses api created_at: must be a JSON number, got " + ResponsesCreatedAt.GoQuote(raw));
        }

        var err = ResponsesCreatedAt.ParseCreatedAtSeconds(raw, out var value);
        if (err is not null)
        {
            throw new JsonException("invalid responses api created_at: " + err);
        }

        response.CreatedAt = value;
        return response;
    }

    public override void Write(Utf8JsonWriter writer, ResponsesResponse value, JsonSerializerOptions options)
    {
        using var createdDoc = JsonDocument.Parse(value.CreatedAt.ToString(CultureInfo.InvariantCulture));

        var alias = new Alias
        {
            Object = value.Object,
            Id = value.Id,
            Error = value.Error,
            CreatedAt = createdDoc.RootElement.Clone(),
            Model = value.Model,
            Output = value.Output,
            Status = value.Status,
            IncompleteDetails = value.IncompleteDetails,
            Instructions = value.Instructions,
            Metadata = value.Metadata,
            ParallelToolCalls = value.ParallelToolCalls,
            Temperature = value.Temperature,
            ToolChoice = value.ToolChoice,
            Tools = value.Tools,
            TopP = value.TopP,
            Background = value.Background,
            Conversation = value.Conversation,
            MaxOutputTokens = value.MaxOutputTokens,
            MaxToolCalls = value.MaxToolCalls,
            PreviousResponseId = value.PreviousResponseId,
            Prompt = value.Prompt,
            PromptCacheKey = value.PromptCacheKey,
            PromptCacheRetention = value.PromptCacheRetention,
            Reasoning = value.Reasoning,
            SafetyIdentifier = value.SafetyIdentifier,
            ServiceTier = value.ServiceTier,
            Text = value.Text,
            TopLogprobs = value.TopLogprobs,
            Truncation = value.Truncation,
            Usage = value.Usage,
            User = value.User,
        };

        JsonSerializer.Serialize(writer, alias, options);
    }
}

/// <summary>Port of responses.ContentItem (model.go:1051-1055).</summary>
public sealed class ResponsesContentItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResponsesAnnotation>? Annotations { get; set; }
}

/// <summary>Port of responses.Error (model.go:1057-1062).</summary>
public sealed class ResponsesError
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("param")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Param { get; set; }
}

/// <summary>Port of the private rawJSONSchema (model.go:1064-1069).</summary>
internal sealed class ResponsesRawJsonSchema
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Schema { get; set; }

    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; set; }
}

/// <summary>Port of the private responsesTerminalDetails (model.go:424-427).</summary>
internal sealed class ResponsesTerminalDetails
{
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesError? Error { get; set; }

    [JsonPropertyName("incomplete_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesResponseIncompleteDetails? IncompleteDetails { get; set; }
}

/// <summary>Port of the private responsesReasoningItemMetadata (model.go:429-432).</summary>
internal sealed class ResponsesReasoningItemMetadata
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("done")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Done { get; set; }
}

/// <summary>
/// Port of the created_at lexeme arithmetic from model.go:947-1039. Returns a
/// Go-formatted error message (without the "invalid responses api created_at:"
/// prefix) or null on success. Callers guarantee <paramref name="raw"/> is a
/// JSON number lexeme.
/// </summary>
internal static class ResponsesCreatedAt
{
    public static string? ParseCreatedAtSeconds(string raw, out long value)
    {
        // Integer form: Go strconv.ParseInt handles int64 range checks directly.
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return null;
        }

        // Float form: split into sign / integer / fraction / exponent.
        var sign = "";
        var body = raw;
        if (body.Length > 0 && (body[0] == '-' || body[0] == '+'))
        {
            sign = body[0].ToString();
            body = body[1..];
        }

        // Go strconv.Atoi is int64-sized on the reference platform; use long
        // so exponents like 1e-9223372036854775808 overflow identically.
        long exp = 0;
        var idx = body.IndexOfAny(['e', 'E']);
        if (idx >= 0)
        {
            if (!long.TryParse(body[(idx + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out exp))
            {
                value = 0;
                return "invalid created_at value " + GoQuote(raw);
            }

            body = body[..idx];
        }

        var frac = "";
        idx = body.IndexOf('.');
        if (idx >= 0)
        {
            (body, frac) = (body[..idx], body[(idx + 1)..]);
        }

        if (body.Length == 0 || !IsDecimalDigits(body) || !IsDecimalDigits(frac))
        {
            value = 0;
            return "invalid created_at value " + GoQuote(raw);
        }

        // digits is the sign-free digit sequence; the value equals
        // digits * 10^(exp - len(frac)).
        var digits = body + frac;

        // Leading zeros do not affect the value; all-zero input is exactly zero.
        var sig = digits.TrimStart('0');
        if (sig.Length == 0)
        {
            value = 0;
            return null;
        }

        // trailingZeros bounds how many zeros a negative exponent can cancel
        // before the value stops being an integer (e.g. 1786360449.5).
        var trailingZeros = 0;
        for (var i = digits.Length - 1; i >= 0 && digits[i] == '0'; i--)
        {
            trailingZeros++;
        }

        // Bound the exponent against the mantissa before computing the scale,
        // so machine integer arithmetic cannot overflow on values such as
        // 1e-9223372036854775808.
        if (exp > frac.Length + 19)
        {
            value = 0;
            return "created_at " + GoQuote(raw) + " is out of int64 range";
        }

        if (exp < -trailingZeros)
        {
            value = 0;
            return "created_at must be an integer number of seconds, got " + GoQuote(raw);
        }

        // scale = len(frac) - exp is now bounded; Go keeps it in int, and the
        // bounds above guarantee the same range here.
        var scale = frac.Length - (int)exp;
        switch (scale)
        {
            case < 0:
                // Pad trailing zeros: a value larger than int64 can be rejected
                // by digit count alone, before building the padded string.
                if (sig.Length + (-scale) > 19)
                {
                    value = 0;
                    return "created_at " + GoQuote(raw) + " is out of int64 range";
                }

                sig += new string('0', -scale);
                break;
            case > 0:
                // Strip trailing zeros: an insufficient zero tail means the
                // value is not an integer (e.g. 1786360449.5).
                if (sig.Length <= scale)
                {
                    value = 0;
                    return "created_at must be an integer number of seconds, got " + GoQuote(raw);
                }

                foreach (var c in sig[^scale..])
                {
                    if (c != '0')
                    {
                        value = 0;
                        return "created_at must be an integer number of seconds, got " + GoQuote(raw);
                    }
                }

                sig = sig[..^scale];
                break;
        }

        if (!long.TryParse(sign + sig, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            value = 0;
            return "created_at " + GoQuote(raw) + " is out of int64 range";
        }

        return null;
    }

    /// <summary>Go <c>strconv.Quote</c> for the %q error text (ASCII JSON lexemes).</summary>
    public static string GoQuote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\v': sb.Append("\\v"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                case '\u007f': sb.Append("\\u007f"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Go <c>isDecimalDigits</c>: true when empty or all ASCII digits.</summary>
    private static bool IsDecimalDigits(string s)
    {
        foreach (var c in s)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Ports of the xmap helpers (llm/internal/pkg/xmap/misc.go) used by the
/// Responses transformers, operating on the port's
/// <see cref="Dictionary{TKey,TValue}"/> transformer metadata. Values are
/// <see cref="JsonElement"/> snapshots, equivalent to Go's values after a
/// JSON round trip.
/// </summary>
internal static class ResponsesMetadata
{
    /// <summary>Go: <c>xmap.GetStringPtr</c>.</summary>
    public static string? GetStringPtr(Dictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var element))
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    /// <summary>Go: <c>xmap.GetInt64Ptr</c>.</summary>
    public static long? GetInt64Ptr(Dictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var element))
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var v) ? v : null;
    }

    /// <summary>Go: <c>xmap.GetBoolPtr</c>.</summary>
    public static bool? GetBoolPtr(Dictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var element))
        {
            return null;
        }

        return element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean() : null;
    }

    /// <summary>Go: <c>xmap.GetStringSlice</c>.</summary>
    public static List<string>? GetStringSlice(Dictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            result.Add(item.GetString()!);
        }

        return result;
    }
}
