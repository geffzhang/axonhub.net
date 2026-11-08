using System.Text.Json.Serialization;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Nanogpt;

/// <summary>
/// Port of nanogpt.Response. The reference embeds openai.Response but shadows
/// its choices with the NanoGPT choice type; the port spells the fields out.
/// </summary>
public sealed class NanogptResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<NanogptChoice?>? Choices { get; set; }

    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("usage")]
    public OpenAiUsage? Usage { get; set; }

    [JsonPropertyName("system_fingerprint")]
    public string? SystemFingerprint { get; set; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("citations")]
    public List<string>? Citations { get; set; }

    [JsonPropertyName("error")]
    public OpenAiError? Error { get; set; }

    /// <summary>
    /// Port of Response.ToOpenAIResponse: the reference always installs a
    /// non-nil choice slice (make with len 0), unlike OpenRouter's append.
    /// </summary>
    public OpenAiResponse ToOpenAiResponse()
    {
        var choices = new List<OpenAiChoice>(Choices?.Count ?? 0);
        foreach (var choice in Choices ?? [])
        {
            choices.Add((choice ?? new NanogptChoice()).ToOpenAiChoice());
        }

        return new OpenAiResponse
        {
            Id = Id,
            Choices = choices,
            Object = Object,
            Created = Created,
            Model = Model,
            Usage = Usage,
            SystemFingerprint = SystemFingerprint,
            ServiceTier = ServiceTier,
            Citations = Citations,
            Error = Error,
        };
    }
}

/// <summary>Port of nanogpt.Choice (embedded openai.Choice plus Message/Delta).</summary>
public sealed class NanogptChoice
{
    [JsonPropertyName("index")]
    public long Index { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }

    [JsonPropertyName("logprobs")]
    public OpenAiLogprobs? Logprobs { get; set; }

    [JsonPropertyName("message")]
    public NanogptMessage? Message { get; set; }

    [JsonPropertyName("delta")]
    public NanogptMessage? Delta { get; set; }

    public OpenAiChoice ToOpenAiChoice() => new()
    {
        Index = Index,
        FinishReason = FinishReason,
        Logprobs = Logprobs,
        Message = Message?.ToOpenAiMessage(),
        Delta = Delta?.ToOpenAiMessage(),
    };
}

/// <summary>
/// Port of nanogpt.Message: openai.Message plus NanoGPT's top-level reasoning
/// field. The embedded openai.Message reasoning field is shadowed by the outer
/// one and therefore never carries a value.
/// </summary>
public sealed class NanogptMessage
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    [JsonConverter(typeof(OpenAiMessageContentJsonConverter))]
    public OpenAiMessageContent? Content { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("refusal")]
    public string? Refusal { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OpenAiToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }

    [JsonPropertyName("annotations")]
    public List<OpenAiAnnotation>? Annotations { get; set; }

    [JsonPropertyName("audio")]
    public OpenAiOutputAudio? Audio { get; set; }

    public OpenAiMessage ToOpenAiMessage()
    {
        // Map reasoning to reasoning_content when present.
        if (Reasoning is not null)
        {
            ReasoningContent = Reasoning;
        }

        // Parse XML tool calls from plain string content if present.
        if (Content?.Content is { Length: > 0 } content
            && NanogptXmlToolCalls.MaybeHasXmlToolCalls(content))
        {
            var tools = NanogptXmlToolCalls.ParseXmlToolCalls(content, out var remaining);
            if (tools is { Count: > 0 })
            {
                ToolCalls = NanogptXmlToolCalls.ToOpenAiToolCalls(tools);
                Content = remaining.Length > 0
                    ? NanogptXmlToolCalls.ToOpenAiMessageContent(remaining)
                    : null; // Go's zero MessageContent has no content field.
            }
        }

        return new OpenAiMessage
        {
            Role = Role,
            Content = Content,
            Name = Name,
            Refusal = Refusal,
            ToolCallId = ToolCallId,
            ToolCalls = ToolCalls,
            ReasoningContent = ReasoningContent,
            Annotations = Annotations,
            Audio = Audio,
        };
    }
}
