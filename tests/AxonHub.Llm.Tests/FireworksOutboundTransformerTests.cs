using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Fireworks;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Fireworks outbound (llm/transformer/fireworks):
/// a plain OpenAI transformer with a default base URL, one trailing slash
/// trimmed and the reasoning field pinned to <c>reasoning_content</c>.
/// </summary>
public sealed class FireworksOutboundTransformerTests
{
    [Fact]
    public void EmptyBaseUrlFallsBackToTheDefaultInferenceEndpoint()
    {
        var transformer = FireworksOutboundTransformer.Create(new FireworksOutboundConfig
        {
            ApiKeyProvider = new StaticApiKeyProvider("fw-key"),
        });

        Assert.Equal(FireworksOutboundTransformer.DefaultBaseUrl, transformer.BaseUrl);
        Assert.Equal(
            "https://api.fireworks.ai/inference/v1/chat/completions",
            transformer.TransformRequest(SimpleRequest()).Url);
    }

    [Theory]
    [InlineData("https://api.fireworks.ai/inference/v1/", "https://api.fireworks.ai/inference/v1/chat/completions")]
    [InlineData("https://api.fireworks.ai/inference/v1", "https://api.fireworks.ai/inference/v1/chat/completions")]
    [InlineData("https://gateway.example.com/fireworks", "https://gateway.example.com/fireworks/v1/chat/completions")]
    public void BaseUrlTrailingSlashIsTrimmedBeforeVersionNormalization(
        string baseUrl,
        string expectedUrl)
    {
        var transformer = Create(baseUrl);

        Assert.Equal(expectedUrl, transformer.TransformRequest(SimpleRequest()).Url);
    }

    [Fact]
    public void ReasoningIsEchoedThroughTheContentField()
    {
        // fireworks.Config pins ReasoningFieldContent: the incoming reasoning is
        // sent as reasoning_content even when it only arrived in reasoning.
        var request = SimpleRequest();
        request.Messages =
        [
            new Message
            {
                Role = "assistant",
                Content = MessageContent.FromText("Hi"),
                Reasoning = "trace",
            },
        ];

        var body = Body(Create("https://api.fireworks.ai/inference/v1").TransformRequest(request));
        var assistant = body.GetProperty("messages")[0];

        Assert.Equal("trace", assistant.GetProperty("reasoning_content").GetString());
        Assert.False(assistant.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public void TransformRequestSendsBearerAuthAndOpenAiFormat()
    {
        var httpRequest = Create("https://api.fireworks.ai/inference/v1")
            .TransformRequest(SimpleRequest());

        Assert.Equal("POST", httpRequest.Method);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, httpRequest.ApiFormat);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("fw-key", httpRequest.Auth!.ApiKey);
        Assert.Equal("application/json", httpRequest.Headers!["Content-Type"][0]);
    }

    [Fact]
    public void ConstructorValidation()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => FireworksOutboundTransformer.Create(null!));
        Assert.Equal(
            "invalid Fireworks transformer configuration: config is nil",
            nil.Message);

        var missingKey = Assert.Throws<InvalidOperationException>(
            () => FireworksOutboundTransformer.Create(new FireworksOutboundConfig
            {
                BaseUrl = "https://api.fireworks.ai/inference/v1",
            }));
        Assert.Equal(
            "invalid Fireworks transformer configuration: API key provider is required",
            missingKey.Message);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static OpenAiOutboundTransformer Create(string baseUrl)
        => FireworksOutboundTransformer.Create(new FireworksOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = new StaticApiKeyProvider("fw-key"),
        });

    private static LlmRequest SimpleRequest(string model = "accounts/fireworks/models/llama-v3p1-8b-instruct")
        => new()
        {
            Model = model,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
