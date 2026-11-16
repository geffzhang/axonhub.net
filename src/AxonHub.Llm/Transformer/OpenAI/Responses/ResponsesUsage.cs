using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of responses.Usage (usage.go): input/output token counts with nested
/// detail objects that are always emitted, plus an optional cost. The Go
/// custom unmarshaler only exists so that a non-numeric <c>cost</c> never
/// fails the decode; <see cref="GoOptionalNumberConverter"/> reproduces that
/// per-property.
/// </summary>
public sealed class ResponsesUsage
{
    private ResponsesInputTokensDetails _inputTokensDetails = new();

    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; set; }

    /// <summary>
    /// Go decodes a null detail object as a no-op into the zero struct, so the
    /// setter ignores null instead of clearing the always-emitted default.
    /// </summary>
    [JsonPropertyName("input_tokens_details")]
    public ResponsesInputTokensDetails InputTokensDetails
    {
        get => _inputTokensDetails;
        set => _inputTokensDetails = value ?? _inputTokensDetails;
    }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; set; }

    /// <summary>See <see cref="InputTokensDetails"/> for the null handling.</summary>
    [JsonPropertyName("output_tokens_details")]
    public ResponsesOutputTokensDetails OutputTokensDetails { get; set; } = new();

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }

    [JsonPropertyName("cost")]
    [JsonConverter(typeof(GoOptionalNumberConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Cost { get; set; }

    /// <summary>Port of responses.Usage.ToUsage. Deliberately drops Cost, as in the reference.</summary>
    public Usage ToUsage() => new()
    {
        PromptTokens = InputTokens,
        CompletionTokens = OutputTokens,
        TotalTokens = TotalTokens,
        PromptTokensDetails = new PromptTokensDetails
        {
            CachedTokens = InputTokensDetails.CachedTokens,
            WriteCachedTokens = InputTokensDetails.CacheWriteTokens,
        },
        CompletionTokensDetails = new CompletionTokensDetails
        {
            ReasoningTokens = OutputTokensDetails.ReasoningTokens,
        },
    };

    /// <summary>Port of responses.ConvertLLMUsageToResponsesUsage. Carries Cost across.</summary>
    public static ResponsesUsage? ConvertLlmUsageToResponsesUsage(Usage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var result = new ResponsesUsage
        {
            InputTokens = usage.PromptTokens,
            OutputTokens = usage.CompletionTokens,
            TotalTokens = usage.TotalTokens,
            Cost = usage.Cost,
        };

        if (usage.PromptTokensDetails is { } promptDetails)
        {
            result.InputTokensDetails.CachedTokens = promptDetails.CachedTokens;
            result.InputTokensDetails.CacheWriteTokens = promptDetails.WriteCachedTokens;
        }

        if (usage.CompletionTokensDetails is { } completionDetails)
        {
            result.OutputTokensDetails.ReasoningTokens = completionDetails.ReasoningTokens;
        }

        return result;
    }
}

/// <summary>Port of the anonymous input_tokens_details struct (usage.go:9-14). Field order as in Go.</summary>
public sealed class ResponsesInputTokensDetails
{
    /// <summary>Number of input tokens written to the prompt cache.</summary>
    [JsonPropertyName("cache_write_tokens")]
    public long CacheWriteTokens { get; set; }

    /// <summary>Number of input tokens retrieved from the prompt cache.</summary>
    [JsonPropertyName("cached_tokens")]
    public long CachedTokens { get; set; }
}

/// <summary>Port of the anonymous output_tokens_details struct (usage.go:16-18).</summary>
public sealed class ResponsesOutputTokensDetails
{
    [JsonPropertyName("reasoning_tokens")]
    public long ReasoningTokens { get; set; }
}
