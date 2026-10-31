using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;

namespace AxonHub.Llm;

/// <summary>
/// Port of llm.Request (llm/model.go:45-308) for the chat subset.
/// Type-specific sub-requests (embedding, rerank, image, video, ...) land with
/// their respective slices; see porting/01-baseline.md for the scope cut.
/// </summary>
public sealed class LlmRequest
{
    [JsonPropertyName("messages")]
    public List<Message> Messages { get; set; } = [];

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

    [JsonPropertyName("previous_response_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousResponseId { get; set; }

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
    public Stop? Stop { get; set; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }

    [JsonPropertyName("stream_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StreamOptions? StreamOptions { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Tool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolChoice? ToolChoice { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseFormat? ResponseFormat { get; set; }

    [JsonPropertyName("verbosity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verbosity { get; set; }

    /// <summary>Provider extension payload; never sent to OpenAI-compatible upstreams.</summary>
    [JsonPropertyName("extra_body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ExtraBody { get; set; }

    /// <summary>Embedding sub-request; set when the request is an embedding request (llm/model.go:240).</summary>
    [JsonPropertyName("embedding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmbeddingRequest? Embedding { get; set; }

    /// <summary>Raw inbound request; persisted alongside the unified request.</summary>
    [JsonPropertyName("raw_request")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HttpRequestData? RawRequest { get; set; }

    [JsonPropertyName("request_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestType { get; set; }

    [JsonPropertyName("api_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiFormat { get; set; }

    [JsonPropertyName("transform_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TransformOptions? TransformOptions { get; set; }

    [JsonPropertyName("transformer_metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? TransformerMetadata { get; set; }

    /// <summary>Compact sub-request; set when the request is a compact request (llm/model.go:255).</summary>
    [JsonPropertyName("compact")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompactRequest? Compact { get; set; }

    /// <summary>Image sub-request; set when the request is an image request (llm/model.go:249).</summary>
    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageRequest? Image { get; set; }

    /// <summary>Moderation sub-request; set when the request is a moderation request (llm/model.go:270).</summary>
    [JsonPropertyName("moderation_request")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModerationRequest? Moderation { get; set; }

    /// <summary>Provider/API-format private sidecar data; never serialized (llm/model.go:307).</summary>
    [JsonIgnore]
    public ProviderExtensions? ProviderExtensions { get; set; }

    /// <summary>Go: <c>req := *src</c> — a shallow copy that shares nested objects.</summary>
    public LlmRequest ShallowClone() => (LlmRequest)MemberwiseClone();
}

/// <summary>Port of llm.StreamOptions.</summary>
public sealed class StreamOptions
{
    [JsonPropertyName("include_usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeUsage { get; set; }
}

/// <summary>
/// Port of llm.Stop: a string or an array of strings. A non-null instance with
/// neither representation set marshals as <c>[]</c> (llm/model.go:325-335).
/// </summary>
[JsonConverter(typeof(StopJsonConverter))]
public sealed class Stop
{
    public string? Single { get; set; }

    public List<string>? Multiple { get; set; }
}

public sealed class StopJsonConverter : JsonConverter<Stop>
{
    public override Stop? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new Stop { Single = reader.GetString() };
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var values = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return new Stop { Multiple = values };
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

    public override void Write(Utf8JsonWriter writer, Stop value, JsonSerializerOptions options)
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

/// <summary>Port of llm.TransformOptions.</summary>
public sealed class TransformOptions
{
    [JsonPropertyName("array_instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ArrayInstructions { get; set; }

    [JsonPropertyName("array_inputs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ArrayInputs { get; set; }

    [JsonPropertyName("default_max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DefaultMaxTokens { get; set; }
}
