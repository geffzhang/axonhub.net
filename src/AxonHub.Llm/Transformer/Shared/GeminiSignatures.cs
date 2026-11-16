namespace AxonHub.Llm.Transformer.Shared;

/// <summary>
/// Port of llm/transformer/shared/gemini.go: Gemini thought-signature
/// handling shared by the Gemini transformers.
/// </summary>
public static class GeminiSignatures
{
    /// <summary>Port of shared.TransformerMetadataKeyGoogleThoughtSignature.</summary>
    public const string TransformerMetadataKeyGoogleThoughtSignature = "google_thought_signature";

    /// <summary>
    /// Port of shared.EncodeGeminiThoughtSignature: Gemini thought signatures
    /// are already base64-encoded, so this is a passthrough.
    /// </summary>
    public static string? EncodeGeminiThoughtSignature(string? signature)
        => signature;

    /// <summary>
    /// Port of shared.DecodeGeminiThoughtSignature: returns the blob only when
    /// the heuristic recognizes Gemini, so signatures from other providers
    /// (OpenAI/Anthropic) or unknown formats are dropped.
    /// </summary>
    public static string? DecodeGeminiThoughtSignature(string? signature)
        => signature is not null
            && AnthropicSignatures.GuessSignatureProvider(signature).Provider == SignatureProvider.Gemini
            ? signature
            : null;
}
