using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Cerebras;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenRouter;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Cerebras outbound (llm/transformer/cerebras): the
/// OpenRouter transformer with a default base URL and the <c>store</c> field
/// stripped from a shallow copy of the request.
/// </summary>
public sealed class CerebrasOutboundTransformerTests
{
    [Fact]
    public void ConstructorValidation()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => new CerebrasOutboundTransformer(null!));
        Assert.Equal(
            "invalid Cerebras transformer configuration: config is nil",
            nil.Message);

        var missingKey = Assert.Throws<InvalidOperationException>(
            () => new CerebrasOutboundTransformer(new CerebrasOutboundConfig
            {
                BaseUrl = "https://api.cerebras.ai/v1",
            }));
        Assert.Equal(
            "invalid Cerebras transformer configuration: API key provider is required",
            missingKey.Message);
    }

    [Fact]
    public void EmptyBaseUrlFallsBackToTheDefaultEndpoint()
    {
        var transformer = Transformer(string.Empty);

        Assert.Equal(
            "https://api.cerebras.ai/v1/chat/completions",
            transformer.TransformRequest(SimpleRequest()).Url);
    }

    [Fact]
    public void TransformRequest_StripsStoreFromAShallowCopy()
    {
        var request = SimpleRequest();
        request.Store = true;

        // The OpenRouter wire shape does carry store, so its absence below is
        // the Cerebras override rather than an unmapped field.
        var openRouterBody = Body(new OpenRouterOutboundTransformer(
            new OpenRouterOutboundConfig
            {
                BaseUrl = "https://api.cerebras.ai/v1",
                ApiKeyProvider = new StaticApiKeyProvider("cerebras-key"),
            }).TransformRequest(request));
        Assert.True(openRouterBody.GetProperty("store").GetBoolean());

        var httpRequest = Transformer().TransformRequest(request);

        var body = Body(httpRequest);
        Assert.False(body.TryGetProperty("store", out _));
        // The caller's request is not mutated (Go copies the struct value).
        Assert.True(request.Store);

        var stored = Transformer().TransformRequest(SimpleRequest());
        Assert.Equal("https://api.cerebras.ai/v1/chat/completions", stored.Url);
    }

    [Fact]
    public void TransformRequest_NilRequestErrors()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(null));
        Assert.Equal("chat completion request is nil", error.Message);
    }

    [Fact]
    public void TransformRequest_UsesOpenRouterChatSemantics()
    {
        var transformer = Transformer();
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

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("cerebras-key", httpRequest.Auth!.ApiKey);
        Assert.Equal("cerebras-key", transformer.SelectedApiKey);
        Assert.Equal("trace", Body(httpRequest).GetProperty("messages")[0]
            .GetProperty("reasoning").GetString());
    }

    [Fact]
    public void SelectedApiKeyIndexComesFromTheChannelConfig()
    {
        var transformer = new CerebrasOutboundTransformer(new CerebrasOutboundConfig
        {
            BaseUrl = "https://api.cerebras.ai/v1",
            ApiKeyProvider = new StaticApiKeyProvider("cerebras-key"),
            ChannelApiKeyIndex = 3,
        });

        Assert.Equal(3, transformer.SelectedApiKeyIndex);
        Assert.Null(Transformer().SelectedApiKeyIndex);
    }

    [Fact]
    public void TransformResponse_UsesTheOpenRouterConversion()
    {
        var response = Transformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = """{"id":"gen-1","object":"chat.completion","created":1,"model":"llama","choices":[{"index":0,"message":{"role":"assistant","content":"Hello","reasoning":"why"},"finish_reason":"stop"}]}"""u8.ToArray(),
        });

        var message = Assert.Single(response.Choices).Message!;
        Assert.Equal("Hello", message.Content!.Content);
        Assert.Equal("why", message.ReasoningContent);
    }

    [Fact]
    public void TransformError_UsesTheOpenRouterEnvelope()
    {
        var response = Transformer().TransformError(new HttpClientError(
            "POST",
            "https://api.cerebras.ai/v1/chat/completions",
            429,
            "429 Too Many Requests",
            Encoding.UTF8.GetBytes("""{"error":{"message":"slow down","code":429}}""")));

        Assert.Equal(429, response.StatusCode);
        Assert.Equal("slow down", response.Detail.Message);
        Assert.Equal("429", response.Detail.Code);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static CerebrasOutboundTransformer Transformer(
        string baseUrl = "https://api.cerebras.ai/v1")
        => new(new CerebrasOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = new StaticApiKeyProvider("cerebras-key"),
        });

    private static LlmRequest SimpleRequest()
        => new()
        {
            Model = "llama3.1-8b",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
