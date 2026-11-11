using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of the <c>StreamEventType</c> constants (stream_event.go:10-69).
/// </summary>
internal static class ResponsesStreamEventTypes
{
    public const string Error = "error";

    // Response lifecycle events.
    public const string ResponseCreated = "response.created";
    public const string ResponseInProgress = "response.in_progress";
    public const string ResponseMetadata = "response.metadata";
    public const string ResponseCompleted = "response.completed";
    public const string ResponseQueued = "response.queued";
    public const string ResponseFailed = "response.failed";
    public const string ResponseCancelled = "response.cancelled";
    public const string ResponseIncomplete = "response.incomplete";

    // Output item events.
    public const string OutputItemAdded = "response.output_item.added";
    public const string OutputItemDone = "response.output_item.done";

    // Content part events.
    public const string ContentPartAdded = "response.content_part.added";
    public const string ContentPartDone = "response.content_part.done";

    // Output text events.
    public const string OutputTextDelta = "response.output_text.delta";
    public const string OutputTextDone = "response.output_text.done";

    // Function call events.
    public const string FunctionCallArgumentsDelta = "response.function_call_arguments.delta";
    public const string FunctionCallArgumentsDone = "response.function_call_arguments.done";

    // Custom tool call events.
    public const string CustomToolCallInputDelta = "response.custom_tool_call_input.delta";
    public const string CustomToolCallInputDone = "response.custom_tool_call_input.done";

    // Reasoning events.
    public const string ReasoningSummaryPartAdded = "response.reasoning_summary_part.added";
    public const string ReasoningSummaryPartDone = "response.reasoning_summary_part.done";
    public const string ReasoningSummaryTextDelta = "response.reasoning_summary_text.delta";
    public const string ReasoningSummaryTextDone = "response.reasoning_summary_text.done";
    public const string ReasoningTextDelta = "response.reasoning_text.delta";
    public const string ReasoningTextDone = "response.reasoning_text.done";

    // Image generation events.
    public const string ImageGenerationGenerating = "response.image_generation_call.generating";
    public const string ImageGenerationInProgress = "response.image_generation_call.in_progress";
    public const string ImageGenerationPartialImage = "response.image_generation_call.partial_image";
    public const string ImageGenerationCompleted = "response.image_generation_call.completed";
}

/// <summary>
/// Port of responses.StreamEvent (stream_event.go:73-146). Go emits empty
/// strings with <c>omitempty</c>; the optional strings here stay null when unset.
/// </summary>
public sealed class ResponsesStreamEvent
{
    // Common fields.
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(ResponsesStreamEventStatusConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Status { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesError? Error { get; set; }

    // For response.* events.
    [JsonPropertyName("response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesResponse? Response { get; set; }

    // For output_item.* events.
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; set; }

    [JsonPropertyName("item")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesItem? Item { get; set; }

    // For content_part.*, output_text.*, reasoning_summary_text.*,
    // reasoning_text.* and function_call_arguments.* events.
    [JsonPropertyName("item_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ItemId { get; set; }

    [JsonPropertyName("content_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContentIndex { get; set; }

    // For content_part.added/done events.
    [JsonPropertyName("part")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponsesStreamEventPart? Part { get; set; }

    // For output_text.delta, reasoning_summary_text.delta, reasoning_text.delta
    // and function_call_arguments.delta events.
    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Delta { get; set; }

    // For output_text.done, reasoning_summary_text.done and reasoning_text.done events.
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    // For function_call_arguments.done events.
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("namespace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Namespace { get; set; }

    [JsonPropertyName("call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallId { get; set; }

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arguments { get; set; }

    // For custom_tool_call_input.done events.
    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Input { get; set; }

    // For reasoning_summary_* events.
    [JsonPropertyName("summary_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SummaryIndex { get; set; }

    // For image_generation_call.partial_image events.
    [JsonPropertyName("partial_image_b64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartialImageB64 { get; set; }

    [JsonPropertyName("partial_image_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PartialImageIndex { get; set; }

    // For error events.
    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("param")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Param { get; set; }
}

/// <summary>
/// Port of responses.StreamEventContentPart (stream_event.go:149-160).
/// </summary>
public sealed class ResponsesStreamEventPart
{
    /// <summary>Any of "output_text", "reasoning", "refusal".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>The text of the part, for output_text.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>The annotations of the output text part (Go <c>omitzero</c>).</summary>
    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResponsesAnnotation>? Annotations { get; set; }

    /// <summary>The refusal reason, for refusal.</summary>
    [JsonPropertyName("refusal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Refusal { get; set; }
}

/// <summary>
/// Port of the custom status decoding in StreamEvent.UnmarshalJSON
/// (stream_event.go:120-140). A missing or null status stays 0, a JSON number
/// must decode with Go's int rules (no fraction or exponent, int64 range), and
/// any other string decodes to 0; other JSON kinds fail.
/// </summary>
public sealed class ResponsesStreamEventStatusConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return 0;

            case JsonTokenType.Number:
                {
                    var raw = reader.ValueSpan;
                    if (!long.TryParse(
                            System.Text.Encoding.UTF8.GetString(raw),
                            System.Globalization.NumberStyles.AllowLeadingSign,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var value))
                    {
                        throw new JsonException(
                            "json: cannot unmarshal number " + System.Text.Encoding.UTF8.GetString(raw) + " into Go value of type int");
                    }

                    return unchecked((int)value);
                }

            case JsonTokenType.String:
                // The reference decodes any string into a string and drops it.
                reader.GetString();
                return 0;

            default:
                throw new JsonException(
                    "json: cannot unmarshal " + reader.TokenType.ToString().ToLowerInvariant() + " into Go value of type string");
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>Port of <c>MarshalStreamEvent</c> (stream_event.go:162-164).</summary>
internal static class ResponsesStreamEventMarshal
{
    public static byte[] Marshal(ResponsesStreamEvent ev) => GoJson.Marshal(ev);
}
