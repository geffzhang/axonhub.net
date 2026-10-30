using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of anthropic.MessageRequest (llm/transformer/anthropic/model.go:11-103).
/// Only the fields the non-streaming inbound slice decodes are declared; every
/// field carries the reference's JSON name so unknown members keep being
/// ignored exactly like encoding/json does.
/// </summary>
public sealed class AnthropicMessageRequest
{
    // Go's int64: a JSON null is a no-op and leaves 0, which validation then
    // rejects. The nullable CLR type keeps that path away from STJ's error.
    [JsonPropertyName("max_tokens")]
    public long? MaxTokens { get; set; }

    [JsonPropertyName("messages")]
    public List<AnthropicMessageParam>? Messages { get; set; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("anthropic_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnthropicVersion { get; set; }

    [JsonPropertyName("anthropic_beta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AnthropicBeta { get; set; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_k")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TopK { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicMetadata? Metadata { get; set; }

    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("stop_sequences")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? StopSequences { get; set; }

    [JsonPropertyName("system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicSystemPrompt? System { get; set; }

    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicThinking? Thinking { get; set; }

    [JsonPropertyName("output_config")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicOutputConfig? OutputConfig { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnthropicTool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicToolChoice? ToolChoice { get; set; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }

    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicCacheControl? CacheControl { get; set; }
}

/// <summary>Port of anthropic.AnthropicMetadata.</summary>
public sealed class AnthropicMetadata
{
    [JsonPropertyName("user_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; set; }
}

/// <summary>
/// Port of anthropic.SystemPrompt: a string or an array of text parts.
/// A JSON <c>null</c> decodes to the empty string, matching Go's
/// <c>json.Unmarshal(null, &amp;str)</c> no-op inside UnmarshalJSON.
/// </summary>
[JsonConverter(typeof(AnthropicSystemPromptJsonConverter))]
public sealed class AnthropicSystemPrompt
{
    public string? Prompt { get; set; }

    public List<AnthropicSystemPromptPart>? MultiplePrompts { get; set; }

    public static AnthropicSystemPrompt FromText(string prompt) => new() { Prompt = prompt };
}

public sealed class AnthropicSystemPromptJsonConverter : JsonConverter<AnthropicSystemPrompt>
{
    public override AnthropicSystemPrompt? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            // Go's first attempt (`json.Unmarshal(data, &str)`) succeeds for
            // null and leaves the empty string, so Prompt points at "".
            return AnthropicSystemPrompt.FromText(string.Empty);
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return AnthropicSystemPrompt.FromText(reader.GetString()!);
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            List<AnthropicSystemPromptPart>? parts;
            try
            {
                parts = JsonSerializer.Deserialize<List<AnthropicSystemPromptPart>>(ref reader, options);
            }
            catch (JsonException ex)
            {
                throw new JsonException("invalid system prompt format", ex);
            }

            return new AnthropicSystemPrompt { MultiplePrompts = parts ?? [] };
        }

        throw new JsonException("invalid system prompt format");
    }

    public override void Write(Utf8JsonWriter writer, AnthropicSystemPrompt value, JsonSerializerOptions options)
    {
        if (value.Prompt is not null)
        {
            writer.WriteStringValue(value.Prompt);
            return;
        }

        if (value.MultiplePrompts is { Count: > 0 })
        {
            JsonSerializer.Serialize(writer, value.MultiplePrompts, options);
            return;
        }

        writer.WriteNullValue();
    }
}

/// <summary>Port of anthropic.SystemPromptPart.</summary>
public sealed class AnthropicSystemPromptPart
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicCacheControl? CacheControl { get; set; }
}

/// <summary>Port of anthropic.Thinking.</summary>
public sealed class AnthropicThinking
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Go int64: JSON null leaves 0, so the CLR type stays nullable.</summary>
    [JsonPropertyName("budget_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BudgetTokens { get; set; }

    [JsonPropertyName("display")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Display { get; set; }
}

/// <summary>Port of anthropic.OutputConfig.</summary>
public sealed class AnthropicOutputConfig
{
    [JsonPropertyName("effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; set; }
}

/// <summary>Port of anthropic.ToolChoice.</summary>
public sealed class AnthropicToolChoice
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("disable_parallel_tool_use")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DisableParallelToolUse { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

/// <summary>Port of anthropic.Tool.</summary>
public sealed class AnthropicTool
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicCacheControl? CacheControl { get; set; }

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
    public AnthropicWebSearchToolUserLocation? UserLocation { get; set; }
}

/// <summary>Port of anthropic.WebSearchToolUserLocation.</summary>
public sealed class AnthropicWebSearchToolUserLocation
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
    public string? Type { get; set; }
}

/// <summary>Port of anthropic.CacheControl.</summary>
public sealed class AnthropicCacheControl
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ttl { get; set; }
}

/// <summary>Port of anthropic.MessageParam.</summary>
public sealed class AnthropicMessageParam
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public AnthropicMessageContent? Content { get; set; }
}

/// <summary>
/// Port of anthropic.MessageContent: a string, an array of content blocks, or
/// <c>Raw</c> bytes re-emitted verbatim. JSON <c>null</c> is rejected with the
/// reference's sentinel message.
/// </summary>
[JsonConverter(typeof(AnthropicMessageContentJsonConverter))]
public sealed class AnthropicMessageContent
{
    public string? Content { get; set; }

    public List<AnthropicMessageContentBlock>? MultipleContent { get; set; }

    /// <summary>Raw JSON bytes emitted verbatim by the converter when set.</summary>
    public byte[]? Raw { get; set; }

    public void SetRaw(byte[] raw) => Raw = raw;
}

public sealed class AnthropicMessageContentJsonConverter : JsonConverter<AnthropicMessageContent>
{
    /// <summary>
    /// Go calls MessageContent.UnmarshalJSON for JSON null too (it is a value
    /// field on MessageParam), so the converter must see null and reject it.
    /// The one pointer field that uses this type, MessageContentBlock.Content,
    /// guards against null before deserializing.
    /// </summary>
    public override bool HandleNull => true;

    public override AnthropicMessageContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            throw new JsonException("content cannot be null");
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new AnthropicMessageContent { Content = reader.GetString()! };
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            List<AnthropicMessageContentBlock>? blocks;
            try
            {
                blocks = doc.RootElement.Deserialize<List<AnthropicMessageContentBlock>>(options);
            }
            catch (JsonException ex)
            {
                // Go tries the array decode first; any failure is folded into
                // the same sentinel once the string attempt also fails.
                throw new JsonException("invalid content type", ex);
            }

            return new AnthropicMessageContent
            {
                MultipleContent = blocks ?? [],
                Raw = Encoding.UTF8.GetBytes(doc.RootElement.GetRawText()),
            };
        }

        throw new JsonException("invalid content type");
    }

    public override void Write(Utf8JsonWriter writer, AnthropicMessageContent value, JsonSerializerOptions options)
    {
        if (value.Raw is { Length: > 0 })
        {
            writer.WriteRawValue(Encoding.UTF8.GetString(GoJson.Compact(value.Raw)), skipInputValidation: true);
            return;
        }

        if (value.Content is not null)
        {
            writer.WriteStringValue(value.Content);
            return;
        }

        if (value.MultipleContent is not null)
        {
            JsonSerializer.Serialize(writer, value.MultipleContent, options);
            return;
        }

        writer.WriteNullValue();
    }
}

/// <summary>
/// Port of anthropic.MessageContentBlock (model.go:351-397) including the
/// custom thinking-block marshal (thinking/signature are always emitted, last).
/// </summary>
[JsonConverter(typeof(AnthropicContentBlockJsonConverter))]
public sealed class AnthropicMessageContentBlock
{
    public string? Type { get; set; }

    public string? Text { get; set; }

    public List<AnthropicTextCitation>? Citations { get; set; }

    public string? Thinking { get; set; }

    public string? Signature { get; set; }

    public string? Data { get; set; }

    public AnthropicImageSource? Source { get; set; }

    public string? Id { get; set; }

    public string? Name { get; set; }

    public JsonElement? Input { get; set; }

    public AnthropicCacheControl? CacheControl { get; set; }

    public string? ToolUseId { get; set; }

    public AnthropicMessageContent? Content { get; set; }

    public bool? IsError { get; set; }

    public string? Url { get; set; }

    public string? Title { get; set; }

    public string? EncryptedContent { get; set; }

    public string? PageAge { get; set; }

    public JsonElement? Caller { get; set; }
}

/// <summary>Port of anthropic.ImageSource.</summary>
public sealed class AnthropicImageSource
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>Port of anthropic.TextCitation.</summary>
[JsonConverter(typeof(AnthropicTextCitationJsonConverter))]
public sealed class AnthropicTextCitation
{
    public string? Type { get; set; }

    public string? Url { get; set; }

    public string? Title { get; set; }

    public string? EncryptedIndex { get; set; }

    public string? CitedText { get; set; }
}

/// <summary>Port of anthropic.Message (the non-streaming response shape).</summary>
public sealed class AnthropicMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public List<AnthropicMessageContentBlock>? Content { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("stop_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopSequence { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicUsage? Usage { get; set; }
}

/// <summary>Port of anthropic.Usage.</summary>
public sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public long CacheCreationInputTokens { get; set; }

    [JsonPropertyName("cache_read_input_tokens")]
    public long CacheReadInputTokens { get; set; }

    [JsonPropertyName("cache_creation")]
    public AnthropicCacheCreation CacheCreation { get; set; } = new();

    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("cached_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long CachedTokens { get; set; }

    /// <summary>
    /// Port of Usage.UnmarshalJSON (usage_json.go): cost is parsed by
    /// xjson.ParseOptionalFloat64, so a numeric string is accepted and any
    /// other shape is ignored rather than failing the whole decode.
    /// </summary>
    [JsonPropertyName("cost")]
    [JsonConverter(typeof(GoOptionalNumberConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Cost { get; set; }
}

/// <summary>Port of anthropic.CacheCreation.</summary>
public sealed class AnthropicCacheCreation
{
    [JsonPropertyName("ephemeral_5m_input_tokens")]
    public long Ephemeral5mInputTokens { get; set; }

    [JsonPropertyName("ephemeral_1h_input_tokens")]
    public long Ephemeral1hInputTokens { get; set; }
}

/// <summary>Port of anthropic.ErrorDetail.</summary>
public sealed class AnthropicErrorDetail
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>Port of anthropic.AnthropicError.</summary>
public sealed class AnthropicError
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonIgnore]
    public int StatusCode { get; set; }

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public AnthropicErrorDetail Error { get; set; } = new();
}
