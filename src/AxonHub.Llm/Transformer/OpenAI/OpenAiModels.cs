using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of llm/transformer/openai/model.go — the OpenAI Chat Completions wire
/// model. Field order follows the Go struct declaration order because Go
/// marshals in that order and the acceptance corpus compares raw bytes.
/// </summary>
public sealed class OpenAiRequest
{
    [JsonPropertyName("messages")]
    public List<OpenAiMessage>? Messages { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("frequency_penalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FrequencyPenalty { get; set; }

    [JsonPropertyName("logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Logprobs { get; set; }

    [JsonPropertyName("max_completion_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxCompletionTokens { get; set; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxTokens { get; set; }

    [JsonPropertyName("presence_penalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PresencePenalty { get; set; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Seed { get; set; }

    [JsonPropertyName("store")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Store { get; set; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TopLogprobs { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }

    [JsonPropertyName("prompt_cache_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromptCacheKey { get; set; }

    [JsonPropertyName("safety_identifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SafetyIdentifier { get; set; }

    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }

    [JsonPropertyName("logit_bias")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, long>? LogitBias { get; set; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Metadata { get; set; }

    [JsonPropertyName("modalities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Modalities { get; set; }

    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("reasoning_budget")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ReasoningBudget { get; set; }

    [JsonPropertyName("reasoning_summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningSummary { get; set; }

    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("stop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiStop? Stop { get; set; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }

    [JsonPropertyName("stream_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiStreamOptions? StreamOptions { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiTool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiToolChoice? ToolChoice { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiResponseFormat? ResponseFormat { get; set; }

    [JsonPropertyName("verbosity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verbosity { get; set; }

    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiThinking? Thinking { get; set; }
}

/// <summary>Port of openai.Thinking.</summary>
public sealed class OpenAiThinking
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

/// <summary>Port of openai.StreamOptions. Note the OpenAI variant omits false.</summary>
public sealed class OpenAiStreamOptions
{
    [JsonPropertyName("include_usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeUsage { get; set; }
}

/// <summary>
/// Port of openai.Stop: a string or an array of strings; a non-null instance
/// with neither representation set marshals as <c>[]</c>.
/// </summary>
[JsonConverter(typeof(OpenAiStopJsonConverter))]
public sealed class OpenAiStop
{
    public string? Single { get; set; }

    public List<string>? Multiple { get; set; }
}

public sealed class OpenAiStopJsonConverter : JsonConverter<OpenAiStop>
{
    public override OpenAiStop? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new OpenAiStop { Single = reader.GetString() };
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var values = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return new OpenAiStop { Multiple = values };
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException("invalid stop type");
                }

                values.Add(reader.GetString()!);
            }

            throw new JsonException("invalid stop type");
        }

        throw new JsonException("invalid stop type");
    }

    public override void Write(Utf8JsonWriter writer, OpenAiStop value, JsonSerializerOptions options)
    {
        if (value.Single is not null)
        {
            writer.WriteStringValue(value.Single);
            return;
        }

        if (value.Multiple is { Count: > 0 })
        {
            writer.WriteStartArray();
            foreach (var item in value.Multiple)
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
            return;
        }

        writer.WriteStartArray();
        writer.WriteEndArray();
    }
}

/// <summary>Port of openai.Message.</summary>
public sealed class OpenAiMessage
{
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Role { get; set; }

    /// <summary>Go tag is <c>content,omitzero</c>; null models the zero value.</summary>
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiMessageContent? Content { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("refusal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Refusal { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reasoning { get; set; }

    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiAnnotation>? Annotations { get; set; }

    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiOutputAudio? Audio { get; set; }
}

/// <summary>Port of openai.Annotation.</summary>
public sealed class OpenAiAnnotation
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Type { get; set; }

    [JsonPropertyName("start_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StartIndex { get; set; }

    [JsonPropertyName("end_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? EndIndex { get; set; }

    [JsonPropertyName("url_citation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiUrlCitation? UrlCitation { get; set; }
}

/// <summary>Port of openai.URLCitation.</summary>
public sealed class OpenAiUrlCitation
{
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Url { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Title { get; set; }
}

/// <summary>
/// Port of openai.MessageContent. Wire shape: bare string, array of parts, or
/// null. A single text part collapses to a bare string.
/// </summary>
[JsonConverter(typeof(OpenAiMessageContentJsonConverter))]
public sealed class OpenAiMessageContent
{
    public string? Content { get; set; }

    public List<OpenAiMessageContentPart>? MultipleContent { get; set; }

    /// <summary>Go: MessageContent.MarshalJSON.</summary>
    internal object? ToJsonValue()
    {
        if (MultipleContent is { Count: > 0 })
        {
            if (MultipleContent.Count == 1 && MultipleContent[0].Type == "text")
            {
                return MultipleContent[0].Text;
            }

            return MultipleContent;
        }

        return Content;
    }
}

public sealed class OpenAiMessageContentJsonConverter : JsonConverter<OpenAiMessageContent>
{
    public override OpenAiMessageContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Go's openai.MessageContent.UnmarshalJSON tolerates "null" explicitly.
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new OpenAiMessageContent { Content = reader.GetString()! };
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var parts = JsonSerializer.Deserialize<List<OpenAiMessageContentPart>>(ref reader, options);
            return new OpenAiMessageContent { MultipleContent = parts ?? [] };
        }

        throw new JsonException("invalid content type");
    }

    public override void Write(Utf8JsonWriter writer, OpenAiMessageContent value, JsonSerializerOptions options)
    {
        var json = value.ToJsonValue();
        if (json is null)
        {
            writer.WriteNullValue();
            return;
        }

        JsonSerializer.Serialize(writer, json, json.GetType(), options);
    }
}

/// <summary>Port of openai.MessageContentPart.</summary>
public sealed class OpenAiMessageContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiImageUrl? ImageUrl { get; set; }

    [JsonPropertyName("video_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiVideoUrl? VideoUrl { get; set; }

    [JsonPropertyName("input_audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiInputAudio? InputAudio { get; set; }

    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiFile? File { get; set; }
}

/// <summary>Port of openai.File.</summary>
public sealed class OpenAiFile
{
    [JsonPropertyName("file_data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? FileData { get; set; }

    [JsonPropertyName("file_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? FileId { get; set; }

    [JsonPropertyName("filename")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Filename { get; set; }
}

/// <summary>Port of openai.ImageURL.</summary>
public sealed class OpenAiImageUrl
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
}

/// <summary>Port of openai.VideoURL.</summary>
public sealed class OpenAiVideoUrl
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>Port of openai.InputAudio.</summary>
public sealed class OpenAiInputAudio
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;
}

/// <summary>Port of openai.OutputAudio.</summary>
public sealed class OpenAiOutputAudio
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }

    [JsonPropertyName("expires_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ExpiresAt { get; set; }

    [JsonPropertyName("transcript")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transcript { get; set; }
}

/// <summary>Port of openai.ResponseFormat.</summary>
public sealed class OpenAiResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("json_schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? JsonSchema { get; set; }
}

/// <summary>Port of openai.Response.</summary>
public sealed class OpenAiResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<OpenAiChoice>? Choices { get; set; }

    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("usage")]
    public OpenAiUsage? Usage { get; set; }

    [JsonPropertyName("system_fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? SystemFingerprint { get; set; }

    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("citations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Citations { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiError? Error { get; set; }
}

/// <summary>Port of openai.Choice: finish_reason and logprobs are always written.</summary>
public sealed class OpenAiChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiMessage? Message { get; set; }

    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiMessage? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }

    [JsonPropertyName("logprobs")]
    public OpenAiLogprobs? Logprobs { get; set; }
}

/// <summary>Port of openai.Logprobs.</summary>
public sealed class OpenAiLogprobs
{
    [JsonPropertyName("content")]
    public List<OpenAiTokenLogprob>? Content { get; set; }
}

/// <summary>Port of openai.TokenLogprob.</summary>
public sealed class OpenAiTokenLogprob
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("logprob")]
    public double Logprob { get; set; }

    [JsonPropertyName("bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Bytes { get; set; }

    [JsonPropertyName("top_logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiTopLogprob>? TopLogprobs { get; set; }
}

/// <summary>Port of openai.TopLogprob.</summary>
public sealed class OpenAiTopLogprob
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("logprob")]
    public double Logprob { get; set; }

    [JsonPropertyName("bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Bytes { get; set; }
}

/// <summary>Port of openai.OpenAIError: the detail is nested under "error".</summary>
public sealed class OpenAiError
{
    [JsonIgnore]
    public int StatusCode { get; set; }

    [JsonPropertyName("error")]
    public ErrorDetail Detail { get; set; } = new();
}

/// <summary>Port of openai.Tool.</summary>
public sealed class OpenAiTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("function")]
    public OpenAiFunction Function { get; set; } = new();
}

/// <summary>Port of openai.Function.</summary>
public sealed class OpenAiFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }

    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; set; }
}

/// <summary>Port of openai.FunctionCall.</summary>
public sealed class OpenAiFunctionCall
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}

/// <summary>Port of openai.ToolCallExtraContent.</summary>
public sealed class OpenAiToolCallExtraContent
{
    [JsonPropertyName("google")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiToolCallGoogleExtraContent? Google { get; set; }
}

/// <summary>Port of openai.ToolCallExtraFields.</summary>
public sealed class OpenAiToolCallExtraFields
{
    [JsonPropertyName("extra_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiToolCallExtraContent? ExtraContent { get; set; }
}

/// <summary>Port of openai.ToolCallGoogleExtraContent.</summary>
public sealed class OpenAiToolCallGoogleExtraContent
{
    [JsonPropertyName("thought_signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ThoughtSignature { get; set; }
}

/// <summary>Port of openai.ToolCall.</summary>
public sealed class OpenAiToolCall
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("function")]
    public OpenAiFunctionCall Function { get; set; } = new();

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("extra_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiToolCallExtraContent? ExtraContent { get; set; }

    [JsonPropertyName("extra_fields")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiToolCallExtraFields? ExtraFields { get; set; }
}

/// <summary>Port of openai.ToolFunction.</summary>
public sealed class OpenAiToolFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Port of openai.NamedToolChoice.</summary>
public sealed class OpenAiNamedToolChoice
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("function")]
    public OpenAiToolFunction Function { get; set; } = new();
}

/// <summary>Port of openai.AllowedTools.</summary>
public sealed class OpenAiAllowedTools
{
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiNamedToolChoice>? Tools { get; set; }
}

/// <summary>Chat Completions wire shape of an <c>allowed_tools</c> choice.</summary>
public sealed class OpenAiAllowedToolsChoice
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("allowed_tools")]
    public OpenAiAllowedTools Allowed { get; set; } = new();
}

/// <summary>Port of openai.ToolChoice (string | named | allowed_tools envelope).</summary>
[JsonConverter(typeof(OpenAiToolChoiceJsonConverter))]
public sealed class OpenAiToolChoice
{
    public string? ToolChoice { get; set; }

    public OpenAiNamedToolChoice? NamedToolChoice { get; set; }

    public OpenAiAllowedTools? AllowedTools { get; set; }
}

public sealed class OpenAiToolChoiceJsonConverter : JsonConverter<OpenAiToolChoice>
{
    public override OpenAiToolChoice? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new OpenAiToolChoice { ToolChoice = reader.GetString() };
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement.Clone();

        if (root.ValueKind == JsonValueKind.Object)
        {
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            if (type == "allowed_tools"
                && root.TryGetProperty("allowed_tools", out var allowedEl)
                && allowedEl.ValueKind == JsonValueKind.Object)
            {
                return new OpenAiToolChoice
                {
                    NamedToolChoice = new OpenAiNamedToolChoice { Type = "allowed_tools" },
                    AllowedTools = allowedEl.Deserialize<OpenAiAllowedTools>(options) ?? new OpenAiAllowedTools(),
                };
            }

            return new OpenAiToolChoice
            {
                NamedToolChoice = root.Deserialize<OpenAiNamedToolChoice>(options),
            };
        }

        throw new JsonException("invalid tool choice type");
    }

    public override void Write(Utf8JsonWriter writer, OpenAiToolChoice value, JsonSerializerOptions options)
    {
        if (value.NamedToolChoice is not null && value.NamedToolChoice.Type == "allowed_tools")
        {
            JsonSerializer.Serialize(writer, new OpenAiAllowedToolsChoice
            {
                Type = "allowed_tools",
                Allowed = value.AllowedTools ?? new OpenAiAllowedTools(),
            }, options);
            return;
        }

        if (value.ToolChoice is not null)
        {
            writer.WriteStringValue(value.ToolChoice);
            return;
        }

        JsonSerializer.Serialize(writer, value.NamedToolChoice, options);
    }
}

/// <summary>Port of openai.Usage.</summary>
public sealed class OpenAiUsage
{
    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }

    [JsonPropertyName("prompt_tokens_details")]
    public OpenAiPromptTokensDetails? PromptTokensDetails { get; set; }

    [JsonPropertyName("completion_tokens_details")]
    public OpenAiCompletionTokensDetails? CompletionTokensDetails { get; set; }

    [JsonPropertyName("reasoning_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ReasoningTokens { get; set; }

    [JsonPropertyName("cached_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long CachedTokens { get; set; }

    [JsonPropertyName("cost")]
    [JsonConverter(typeof(GoOptionalNumberConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Cost { get; set; }
}

/// <summary>Port of openai.PromptTokensDetails.</summary>
public sealed class OpenAiPromptTokensDetails
{
    [JsonPropertyName("audio_tokens")]
    public long AudioTokens { get; set; }

    [JsonPropertyName("cached_tokens")]
    public long CachedTokens { get; set; }

    [JsonPropertyName("write_cached_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long WriteCachedTokens { get; set; }
}

/// <summary>Port of openai.CompletionTokensDetails.</summary>
public sealed class OpenAiCompletionTokensDetails
{
    [JsonPropertyName("audio_tokens")]
    public long AudioTokens { get; set; }

    [JsonPropertyName("reasoning_tokens")]
    public long ReasoningTokens { get; set; }

    [JsonPropertyName("accepted_prediction_tokens")]
    public long AcceptedPredictionTokens { get; set; }

    [JsonPropertyName("rejected_prediction_tokens")]
    public long RejectedPredictionTokens { get; set; }
}
