using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Cerebras;
using AxonHub.Llm.Transformer.Nanogpt;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.OpenRouter;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the OpenRouter, Cerebras and nanoGPT branches of the channel
/// transformer switch (channel_llm.go:753-799): OpenRouter is the OpenAI
/// outbound with top-level reasoning and its own image route, Cerebras the
/// OpenRouter outbound with the default Cerebras URL and <c>store</c> stripped,
/// nanoGPT the OpenAI outbound plus XML tool extraction, and the
/// nanoGPT responses channel always the shared Responses outbound.
/// </summary>
public sealed class OpenRouterCerebrasNanogptChannelWiringTests
{
    private static ChannelCandidate Candidate(
        string type,
        string? baseUrl = "https://openrouter.ai/api/v1")
        => new()
        {
            ChannelId = 86,
            ChannelName = type + "-wiring",
            ActualModel = "z-ai/glm-4.5-air:free",
            Type = type,
            BaseUrl = baseUrl,
            Credentials = """{"apiKey":"provider-key"}""",
        };

    private static LlmRequest Request(
        string? apiFormat = null,
        string model = "z-ai/glm-4.5-air:free")
        => new()
        {
            Model = model,
            ApiFormat = apiFormat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    [Fact]
    public void OpenRouterChannelBuildsTheOpenRouterTransformer()
    {
        var transformer = Assert.IsType<OpenRouterOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(ChannelOutboundFactory.OpenRouterChannelType),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void OpenRouterChannelServesSpeechRequests()
    {
        // channel_endpoint.go:284-294: OpenRouter's default endpoint set is chat
        // plus speech/transcription/translation, all built by the same
        // transformer.
        var transformer = Assert.IsType<OpenRouterOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(ChannelOutboundFactory.OpenRouterChannelType),
                new LlmRequest
                {
                    Model = "tts-1",
                    ApiFormat = ApiFormats.OpenAISpeech,
                    RequestType = RequestTypes.Speech,
                    Speech = new SpeechRequest { Input = "Hello", Voice = "alloy" },
                },
                DateTimeOffset.UnixEpoch));

        var httpRequest = transformer.TransformRequest(new LlmRequest
        {
            Model = "tts-1",
            ApiFormat = ApiFormats.OpenAISpeech,
            RequestType = RequestTypes.Speech,
            Speech = new SpeechRequest { Input = "Hello", Voice = "alloy" },
        });

        Assert.Equal("https://openrouter.ai/api/v1/audio/speech", httpRequest.Url);
        Assert.Equal(ApiFormats.OpenAISpeech, httpRequest.ApiFormat);
    }

    [Fact]
    public void CerebrasChannelBuildsTheCerebrasTransformerWithTheDefaultBaseUrl()
    {
        // channel_llm.go:764-774 + cerebras.DefaultBaseURL: an empty channel
        // base URL falls back to https://api.cerebras.ai/v1.
        var transformer = Assert.IsType<CerebrasOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(ChannelOutboundFactory.CerebrasChannelType, baseUrl: null),
                Request(model: "llama3.1-8b"),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var request = Request(model: "llama3.1-8b");
        request.Store = true;
        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal("https://api.cerebras.ai/v1/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);

        using var body = System.Text.Json.JsonDocument.Parse(httpRequest.Body!);
        Assert.False(body.RootElement.TryGetProperty("store", out _));
    }

    [Fact]
    public void CerebrasChannelKeepsTheConfiguredBaseUrl()
    {
        var transformer = Assert.IsType<CerebrasOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(
                    ChannelOutboundFactory.CerebrasChannelType,
                    "https://cerebras-proxy.internal/v1"),
                Request(model: "llama3.1-8b"),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(
            "https://cerebras-proxy.internal/v1/chat/completions",
            transformer.TransformRequest(Request(model: "llama3.1-8b")).Url);
    }

    [Fact]
    public void NanogptChannelBuildsTheNanogptTransformer()
    {
        var transformer = Assert.IsType<NanogptOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(
                    ChannelOutboundFactory.NanogptChannelType,
                    "https://nano-gpt.com/api/v1"),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal("https://nano-gpt.com/api/v1/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void NanogptResponsesChannelAlwaysUsesTheResponsesTransformer()
    {
        // channel_llm.go:789-799: the nanoGPT responses channel registers
        // exactly one endpoint (responses) and routes every request through the
        // shared Responses outbound, whatever the inbound format.
        var transformer = Assert.IsType<ResponsesOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(
                    ChannelOutboundFactory.NanogptResponsesChannelType,
                    "https://nano-gpt.com/api/v1"),
                Request(ApiFormats.OpenAIChatCompletion),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(ApiFormats.OpenAIResponse, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(new LlmRequest
        {
            Model = "zai-org/glm-4.7:thinking",
            ApiFormat = ApiFormats.OpenAIResponse,
            RequestType = RequestTypes.Chat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        });

        Assert.Equal("https://nano-gpt.com/api/v1/responses", httpRequest.Url);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.OpenRouterChannelType)]
    [InlineData(ChannelOutboundFactory.CerebrasChannelType)]
    [InlineData(ChannelOutboundFactory.NanogptChannelType)]
    [InlineData(ChannelOutboundFactory.NanogptResponsesChannelType)]
    public void ChannelsWithoutKeysAreRejected(string type)
    {
        var candidate = Candidate(type);
        candidate.Credentials = "{}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(candidate, Request(), DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel " + type + "-wiring", error.Message);
    }
}
