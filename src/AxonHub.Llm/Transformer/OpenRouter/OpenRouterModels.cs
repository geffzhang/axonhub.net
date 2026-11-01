using System.Text;
using System.Text.Json.Serialization;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.OpenRouter;

/// <summary>
/// Port of llm/transformer/openrouter/model.go ImageGenerationRequest.
/// OpenRouter's image router does not accept OpenAI's moderation,
/// response_format or user fields, so those are intentionally dropped.
/// </summary>
public sealed class OpenRouterImageGenerationRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? N { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; set; }

    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quality { get; set; }

    [JsonPropertyName("background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Background { get; set; }

    [JsonPropertyName("output_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("output_compression")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OutputCompression { get; set; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Seed { get; set; }

    /// <summary>Go omitempty drops the empty slice; null models that.</summary>
    [JsonPropertyName("input_references")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiMessageContentPart>? InputReferences { get; set; }
}

/// <summary>Port of ImageGenerationResponse.</summary>
public sealed class OpenRouterImageGenerationResponse
{
    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("data")]
    public List<OpenRouterImageGenerationData>? Data { get; set; }

    [JsonPropertyName("usage")]
    public OpenRouterImageGenerationUsage? Usage { get; set; }
}

/// <summary>Port of ImageGenerationData.</summary>
public sealed class OpenRouterImageGenerationData
{
    [JsonPropertyName("b64_json")]
    public string B64JSON { get; set; } = string.Empty;

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;
}

/// <summary>Port of ImageGenerationUsage.</summary>
public sealed class OpenRouterImageGenerationUsage
{
    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }

    [JsonPropertyName("prompt_tokens_details")]
    public OpenRouterImagePromptTokensDetails? PromptTokensDetails { get; set; }

    [JsonPropertyName("completion_tokens_details")]
    public OpenRouterImageCompletionTokensDetails? CompletionTokensDetails { get; set; }

    public Usage ToLlmUsage()
    {
        var usage = new Usage
        {
            PromptTokens = PromptTokens,
            CompletionTokens = CompletionTokens,
            TotalTokens = TotalTokens,
        };

        if (PromptTokensDetails is not null)
        {
            usage.PromptTokensDetails = new PromptTokensDetails
            {
                ImageTokens = PromptTokensDetails.ImageTokens,
                CachedTokens = PromptTokensDetails.CachedTokens,
            };
        }

        if (CompletionTokensDetails is not null)
        {
            usage.CompletionTokensDetails = new CompletionTokensDetails
            {
                ReasoningTokens = CompletionTokensDetails.ReasoningTokens,
            };
        }

        return usage;
    }
}

/// <summary>Port of ImagePromptTokensDetails.</summary>
public sealed class OpenRouterImagePromptTokensDetails
{
    [JsonPropertyName("image_tokens")]
    public long ImageTokens { get; set; }

    [JsonPropertyName("cached_tokens")]
    public long CachedTokens { get; set; }
}

/// <summary>Port of ImageCompletionTokensDetails.</summary>
public sealed class OpenRouterImageCompletionTokensDetails
{
    [JsonPropertyName("reasoning_tokens")]
    public long ReasoningTokens { get; set; }
}

/// <summary>
/// Port of openrouter.Response. The reference embeds openai.Response but its
/// own <c>Choices []Choice</c> shadows the embedded slice at depth zero; the
/// port spells the fields out with the outer choice type.
/// </summary>
public sealed class OpenRouterResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<OpenRouterChoice?>? Choices { get; set; }

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
    /// Port of Response.ToOpenAIResponse: Go appends into the embedded nil
    /// slice, so a response without choices stays nil rather than becoming [].
    /// </summary>
    public OpenAiResponse ToOpenAiResponse()
    {
        var choices = new List<OpenAiChoice>();
        foreach (var choice in Choices ?? [])
        {
            choices.Add((choice ?? new OpenRouterChoice()).ToOpenAiChoice());
        }

        return new OpenAiResponse
        {
            Id = Id,
            Choices = choices.Count > 0 ? choices : null,
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

/// <summary>Port of openrouter.Choice (embedded openai.Choice plus Message/Delta).</summary>
public sealed class OpenRouterChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }

    [JsonPropertyName("logprobs")]
    public OpenAiLogprobs? Logprobs { get; set; }

    [JsonPropertyName("message")]
    public OpenRouterMessage? Message { get; set; }

    [JsonPropertyName("delta")]
    public OpenRouterMessage? Delta { get; set; }

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
/// Port of openrouter.Message: openai.Message plus OpenRouter's reasoning
/// details and generated images. The reference's embedded Reasoning field is
/// never populated (the outer field shadows it during JSON decoding), so the
/// conversion never carries <c>reasoning</c> through.
/// </summary>
public sealed class OpenRouterMessage
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

    [JsonPropertyName("reasoning_details")]
    public List<OpenRouterReasoningDetail?>? ReasoningDetails { get; set; }

    [JsonPropertyName("images")]
    public List<OpenAiMessageContentPart?>? Images { get; set; }

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
            Annotations = Annotations,
            Audio = Audio,
        };

        // Prefer reasoning_details if available, fall back to reasoning.
        if (ReasoningDetails is { Count: > 0 })
        {
            var reasoning = new StringBuilder();
            foreach (var detail in ReasoningDetails)
            {
                reasoning.Append(detail?.Text ?? string.Empty);
            }

            message.ReasoningContent = reasoning.ToString();
        }
        else if (Reasoning is not null)
        {
            message.ReasoningContent = Reasoning;
        }

        if (Images is { Count: > 0 })
        {
            List<OpenAiMessageContentPart> parts;
            if (Content?.Content is { Length: > 0 } content)
            {
                parts = [new OpenAiMessageContentPart { Type = "text", Text = content }];
            }
            else
            {
                parts = Content?.MultipleContent is { } multiple
                    ? [..multiple]
                    : [];
            }

            foreach (var image in Images)
            {
                parts.Add(image ?? new OpenAiMessageContentPart());
            }

            message.Content = new OpenAiMessageContent { MultipleContent = parts };
        }
        else
        {
            // Preserve nil for empty slices to match the reference's converted
            // message shape.
            if (message.Content?.MultipleContent is { Count: 0 })
            {
                message.Content.MultipleContent = null;
            }

            if (message.ToolCalls is { Count: 0 })
            {
                message.ToolCalls = null;
            }
        }

        return message;
    }
}

/// <summary>Port of openrouter.ReasoningDetail.</summary>
public sealed class OpenRouterReasoningDetail
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
