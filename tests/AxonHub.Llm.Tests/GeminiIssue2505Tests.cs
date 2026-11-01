using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the developer-message fix (issue #2505,
/// llm/transformer/gemini/outbound_issue2505_test.go): a developer-only
/// request is promoted into systemInstruction, leaving contents empty, and
/// must fail with the reference error.
/// </summary>
public sealed class GeminiIssue2505Tests
{
    private const string DeveloperOnlyError =
        "invalid request: contents must contain at least one user or model turn; " +
        "system and developer messages are converted to systemInstruction";

    private static GeminiOutboundTransformer NewTransformer()
        => GeminiOutboundTransformer.Create("https://generativelanguage.googleapis.com", "test-key");

    // A developer-only request is promoted into systemInstruction, leaving
    // contents empty. Gemini requires at least one content entry, so the
    // conversion must fail with a clear error.
    [Fact]
    public void DeveloperOnlyMessage()
    {
        var ex = Assert.Throws<TransformerException>(() => NewTransformer().TransformRequest(new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Messages =
            [
                new Message
                {
                    Role = "developer",
                    Content = new MessageContent { Content = "instruction text" },
                },
            ],
        }));

        Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
        Assert.Equal(DeveloperOnlyError, ex.Message);
    }

    // A developer message must reach Gemini as a systemInstruction, matching the
    // Anthropic path, while real turns stay in contents.
    [Fact]
    public void DeveloperMessageBecomesSystemInstruction()
    {
        var result = NewTransformer().TransformRequest(new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Messages =
            [
                new Message
                {
                    Role = "developer",
                    Content = new MessageContent { Content = "instruction text" },
                },
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello!" },
                },
            ],
        });

        var geminiReq = LlmJson.Deserialize<GenerateContentRequest>(result.Body!)!;
        Assert.NotNull(geminiReq.SystemInstruction);
        Assert.Single(geminiReq.SystemInstruction!.Parts!);
        Assert.Equal("instruction text", geminiReq.SystemInstruction.Parts![0].Text);
        Assert.Single(geminiReq.Contents!);
        Assert.Equal("user", geminiReq.Contents![0].Role);
    }

    // The conversion itself promotes a developer-only message into
    // systemInstruction; only the resulting empty contents is invalid, which
    // TransformRequest rejects above.
    [Fact]
    public void ConvertLlmToGeminiRequest_DeveloperOnlyMessageBecomesSystemInstruction()
    {
        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Messages =
            [
                new Message
                {
                    Role = "developer",
                    Content = new MessageContent { Content = "instruction text" },
                },
            ],
        });

        Assert.NotNull(result.SystemInstruction);
        Assert.Single(result.SystemInstruction!.Parts!);
        Assert.Equal("instruction text", result.SystemInstruction.Parts![0].Text);
        Assert.Empty(result.Contents ?? []);
    }
}
