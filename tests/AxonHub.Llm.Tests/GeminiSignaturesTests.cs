using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for shared Gemini thought-signature handling
/// (llm/transformer/shared/gemini_test.go): encoding is a passthrough, and
/// decoding only releases signatures the heuristic recognizes as Gemini.
/// </summary>
public sealed class GeminiSignaturesTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    // Protobuf-like base64: {0x0a, 0x04, 't', 'e', 's', 't'}.
    [InlineData("CgR0ZXN0", "CgR0ZXN0")]
    // OpenAI-like signature (gAAA prefix) - rejected.
    [InlineData("gAAAAABpg2hk4yLqQUPBKlNLPwYE5lSfBmhv0", null)]
    // Anthropic-like signature (Eq prefix) - rejected.
    [InlineData("EqQBCAEDEgQIAhAEGAAgAigBMOzOAg==", null)]
    // Unknown standard base64 with + char - rejected.
    [InlineData("+AA=", null)]
    public void DecodeGeminiThoughtSignature_MatchesReference(string? signature, string? expected)
    {
        Assert.Equal(expected, GeminiSignatures.DecodeGeminiThoughtSignature(signature));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("some-signature", "some-signature")]
    public void EncodeGeminiThoughtSignature_IsAPassthrough(string? signature, string? expected)
    {
        Assert.Equal(expected, GeminiSignatures.EncodeGeminiThoughtSignature(signature));
    }

    [Fact]
    public void EncodeDecodeRoundTrip_KeepsAGeminiSignature()
    {
        const string original = "CgR0ZXN0";

        var encoded = GeminiSignatures.EncodeGeminiThoughtSignature(original);
        Assert.Equal(original, encoded);

        var decoded = GeminiSignatures.DecodeGeminiThoughtSignature(encoded);
        Assert.Equal(original, decoded);
    }
}
