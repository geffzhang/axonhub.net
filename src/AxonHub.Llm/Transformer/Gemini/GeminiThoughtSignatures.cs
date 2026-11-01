using System.Text;
using System.Text.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Gemini;

/// <summary>
/// Port of the tool-call thought-signature helpers
/// (llm/transformer/gemini/thinking_signature.go).
/// </summary>
public static class GeminiThoughtSignatures
{
    /// <summary>Port of ContextEngineeringThoughtSignature.</summary>
    public static readonly string ContextEngineeringThoughtSignature =
        Convert.ToBase64String(Encoding.UTF8.GetBytes("context_engineering_is_the_way_to_go"));

    private const string TransformerMetadataKeyGoogleThoughtSignature =
        GeminiSignatures.TransformerMetadataKeyGoogleThoughtSignature;

    /// <summary>Port of setInboundToolCallThoughtSignature.</summary>
    public static void SetInboundToolCallThoughtSignature(ToolCall? toolCall, string? signature)
    {
        if (toolCall is null || signature is null or "")
        {
            return;
        }

        toolCall.TransformerMetadata ??= new Dictionary<string, JsonElement>();
        toolCall.TransformerMetadata[TransformerMetadataKeyGoogleThoughtSignature] =
            JsonSerializer.SerializeToElement(signature);
    }

    /// <summary>Port of getInboundGeminiToolCallThoughtSignature.</summary>
    public static string? GetInboundGeminiToolCallThoughtSignature(ToolCall toolCall)
    {
        if (toolCall.TransformerMetadata is not { } metadata)
        {
            return null;
        }

        if (!metadata.TryGetValue(TransformerMetadataKeyGoogleThoughtSignature, out var raw)
            || raw.ValueKind != JsonValueKind.String
            || raw.GetString() is not { Length: > 0 } signature)
        {
            return null;
        }

        return signature;
    }

    /// <summary>Port of setOutboundToolCallThoughtSignature.</summary>
    public static void SetOutboundToolCallThoughtSignature(ToolCall? toolCall, string? signature)
    {
        if (toolCall is null || signature is null or "")
        {
            return;
        }

        var encoded = GeminiSignatures.EncodeGeminiThoughtSignature(signature);
        if (encoded is null)
        {
            return;
        }

        toolCall.TransformerMetadata ??= new Dictionary<string, JsonElement>();
        toolCall.TransformerMetadata[TransformerMetadataKeyGoogleThoughtSignature] =
            JsonSerializer.SerializeToElement(encoded);
    }

    /// <summary>Port of getOutbountGeminiToolCallThoughtSignature (sic).</summary>
    public static string? GetOutboundGeminiToolCallThoughtSignature(ToolCall toolCall)
    {
        if (toolCall.TransformerMetadata is not { } metadata)
        {
            return null;
        }

        if (!metadata.TryGetValue(TransformerMetadataKeyGoogleThoughtSignature, out var raw)
            || raw.ValueKind != JsonValueKind.String
            || raw.GetString() is not { Length: > 0 } signature)
        {
            return null;
        }

        return GeminiSignatures.DecodeGeminiThoughtSignature(signature);
    }
}
