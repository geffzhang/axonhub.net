using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Cline;

/// <summary>
/// Port of cline.Response (llm/transformer/cline/model.go:9-30): an OpenAI
/// response whose <c>choices</c> carry Cline's extended message shape. Go's
/// embedded struct shadowing makes the outer <c>choices</c> field receive the
/// payload; the port mirrors that by declaring the OpenAI fields directly and
/// reusing <see cref="OpenAiUsage"/> / <see cref="OpenAiError"/> for the shared
/// sub-objects.
/// </summary>
public sealed class ClineResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<ClineChoice>? Choices { get; set; }

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

    /// <summary>Port of Response.ToOpenAIResponse.</summary>
    public OpenAiResponse ToOpenAiResponse()
    {
        var response = new OpenAiResponse
        {
            Id = Id,
            // Go: make([]openai.Choice, 0, len(r.Choices)) — always non-nil, so
            // the re-marshaled body carries "choices":[] rather than null.
            Choices = new List<OpenAiChoice>(Choices?.Count ?? 0),
            Object = Object,
            Created = Created,
            Model = Model,
            Usage = Usage,
            SystemFingerprint = SystemFingerprint,
            ServiceTier = ServiceTier,
            Citations = Citations,
            Error = Error,
        };

        foreach (var choice in Choices ?? [])
        {
            response.Choices.Add(choice.ToOpenAiChoice());
        }

        return response;
    }
}

/// <summary>Port of cline.Choice (model.go:32-55).</summary>
public sealed class ClineChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClineMessage? Message { get; set; }

    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClineMessage? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }

    [JsonPropertyName("logprobs")]
    public OpenAiLogprobs? Logprobs { get; set; }

    /// <summary>Port of Choice.ToOpenAIChoice.</summary>
    public OpenAiChoice ToOpenAiChoice() => new()
    {
        Index = Index,
        Message = Message?.ToOpenAiMessage(),
        Delta = Delta?.ToOpenAiMessage(),
        FinishReason = FinishReason,
        Logprobs = Logprobs,
    };
}

/// <summary>Port of cline.Message (model.go:57-96).</summary>
public sealed class ClineMessage
{
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Role { get; set; }

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

    [JsonPropertyName("reasoning_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ClineReasoningDetail>? ReasoningDetails { get; set; }

    [JsonPropertyName("provider_metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    /// <summary>
    /// Port of Message.ToOpenAIMessage (model.go:98-131): concatenated
    /// reasoning_details win over the plain reasoning field, and empty
    /// collections are normalized to nil so they disappear from the body.
    /// </summary>
    public OpenAiMessage ToOpenAiMessage()
    {
        var message = new OpenAiMessage
        {
            Role = Role,
            Content = Content,
            Name = Name,
            Refusal = Refusal,
            ToolCallId = ToolCallId,
            ToolCalls = ToolCalls,
            ReasoningContent = ReasoningContent,
            Reasoning = Reasoning,
            Annotations = Annotations,
            Audio = Audio,
        };

        if (ReasoningDetails is { Count: > 0 } details)
        {
            var reasoningText = new StringBuilder();
            foreach (var detail in details)
            {
                reasoningText.Append(detail.Text);
            }

            message.ReasoningContent = reasoningText.ToString();
        }
        else if (Reasoning is not null)
        {
            message.ReasoningContent = Reasoning;
        }

        if (message.ToolCalls is { Count: 0 })
        {
            message.ToolCalls = null;
        }

        if (message.Annotations is { Count: 0 })
        {
            message.Annotations = null;
        }

        if (message.Content is not null && message.Content.MultipleContent is { Count: 0 })
        {
            message.Content.MultipleContent = null;
        }

        return message;
    }
}

/// <summary>Port of cline.ReasoningDetail (model.go:88-93).</summary>
public sealed class ClineReasoningDetail
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; set; }
}
