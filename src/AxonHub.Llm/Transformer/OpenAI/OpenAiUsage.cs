using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.Usage.ToLLMUsage / UsageFromLLM
/// (llm/transformer/openai/usage.go:70-150).
/// </summary>
public static class OpenAiUsageConvert
{
    public static Usage? ToLlmUsage(this OpenAiUsage? u)
    {
        if (u is null)
        {
            return null;
        }

        var promptDetails = u.PromptTokensDetails;
        var completionDetails = u.CompletionTokensDetails;

        var usage = new Usage
        {
            PromptTokens = u.PromptTokens,
            CompletionTokens = u.CompletionTokens,
            TotalTokens = u.TotalTokens,
        };

        if (promptDetails is not null && !IsZero(promptDetails))
        {
            usage.PromptTokensDetails = new PromptTokensDetails
            {
                AudioTokens = promptDetails.AudioTokens,
                CachedTokens = promptDetails.CachedTokens,
                WriteCachedTokens = promptDetails.WriteCachedTokens,
            };
        }

        // Some OpenAI-compatible providers report reasoning tokens as a top-level
        // field instead of completion_tokens_details; the nested value wins.
        var reasoningTokens = completionDetails?.ReasoningTokens ?? 0;
        if (reasoningTokens == 0)
        {
            reasoningTokens = u.ReasoningTokens;
        }

        if ((completionDetails is not null && !IsZero(completionDetails)) || reasoningTokens != 0)
        {
            usage.CompletionTokensDetails = new CompletionTokensDetails
            {
                AudioTokens = completionDetails?.AudioTokens ?? 0,
                ReasoningTokens = reasoningTokens,
                AcceptedPredictionTokens = completionDetails?.AcceptedPredictionTokens ?? 0,
                RejectedPredictionTokens = completionDetails?.RejectedPredictionTokens ?? 0,
            };
        }

        if ((usage.PromptTokensDetails is null || usage.PromptTokensDetails.CachedTokens == 0) && u.CachedTokens > 0)
        {
            usage.PromptTokensDetails ??= new PromptTokensDetails();
            usage.PromptTokensDetails.CachedTokens = u.CachedTokens;
        }

        return usage;
    }

    public static OpenAiUsage? FromLlm(Usage? u)
    {
        if (u is null)
        {
            return null;
        }

        var usage = new OpenAiUsage
        {
            PromptTokens = u.PromptTokens,
            CompletionTokens = u.CompletionTokens,
            TotalTokens = u.TotalTokens,
            Cost = u.Cost,
            // The reference fields are value structs without omitempty: they are
            // always written, as zeros when absent.
            PromptTokensDetails = new OpenAiPromptTokensDetails(),
            CompletionTokensDetails = new OpenAiCompletionTokensDetails(),
        };

        if (u.PromptTokensDetails is not null)
        {
            usage.PromptTokensDetails = new OpenAiPromptTokensDetails
            {
                AudioTokens = u.PromptTokensDetails.AudioTokens,
                CachedTokens = u.PromptTokensDetails.CachedTokens,
                WriteCachedTokens = u.PromptTokensDetails.WriteCachedTokens,
            };
        }

        if (u.CompletionTokensDetails is not null)
        {
            usage.CompletionTokensDetails = new OpenAiCompletionTokensDetails
            {
                AudioTokens = u.CompletionTokensDetails.AudioTokens,
                ReasoningTokens = u.CompletionTokensDetails.ReasoningTokens,
                AcceptedPredictionTokens = u.CompletionTokensDetails.AcceptedPredictionTokens,
                RejectedPredictionTokens = u.CompletionTokensDetails.RejectedPredictionTokens,
            };
        }

        return usage;
    }

    private static bool IsZero(OpenAiPromptTokensDetails d)
        => d.AudioTokens == 0 && d.CachedTokens == 0 && d.WriteCachedTokens == 0;

    private static bool IsZero(OpenAiCompletionTokensDetails d)
        => d.AudioTokens == 0 && d.ReasoningTokens == 0
           && d.AcceptedPredictionTokens == 0 && d.RejectedPredictionTokens == 0;
}

/// <summary>
/// Port of xjson.ParseOptionalFloat64: accepts a JSON number or a numeric
/// string and yields null for anything else instead of failing the decode.
/// </summary>
public sealed class GoOptionalNumberConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.TryGetDouble(out var value) ? value : null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteNumberValue(value.Value);
    }
}
