namespace AxonHub.Llm.Transformer.Shared;

/// <summary>
/// Port of llm/transformer/shared/openai.go: OpenAI encrypted_content is already
/// base64url-encoded, so encoding is a passthrough and decoding only accepts
/// blobs the signature heuristic recognizes as OpenAI.
/// </summary>
public static class OpenAiSignatures
{
    /// <summary>Port of shared.EncodeOpenAIEncryptedContent.</summary>
    public static string? EncodeOpenAIEncryptedContent(string? content) => content;

    /// <summary>
    /// Port of shared.DecodeOpenAIEncryptedContent: returns the blob only when
    /// it is recognized as OpenAI, so Anthropic/Gemini signatures never travel
    /// upstream as OpenAI encrypted content.
    /// </summary>
    public static string? DecodeOpenAIEncryptedContent(string? content)
        => content is not null && AnthropicSignatures.GuessSignatureProvider(content).Provider == SignatureProvider.OpenAI
            ? content
            : null;
}
