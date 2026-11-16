using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.Ollama;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the ollama and ollama_anthropic halves of the channel wiring against
/// the reference: the type switch in <c>buildChannelWithTransformer</c>
/// (channel_llm.go:1392-1428) and the optional-key guard
/// (channel_llm.go:677-680). A local Ollama instance usually has no key, so the
/// factory must build a transformer instead of throwing, and must not send a
/// stored-but-disabled key.
/// </summary>
public sealed class OllamaChannelWiringTests
{
    private static ChannelCandidate Candidate(
        string type,
        string credentials,
        string? disabledApiKeys = null) => new()
        {
            ChannelId = 77,
            ChannelName = "ollama-wiring",
            ActualModel = "qwen3",
            Type = type,
            BaseUrl = "http://localhost:11434",
            Credentials = credentials,
            DisabledApiKeys = disabledApiKeys,
        };

    private static LlmRequest Request()
        => new()
        {
            Model = "qwen3",
            MaxTokens = 256,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static IOutboundTransformer Create(string type, string credentials, string? disabled = null)
        => ChannelOutboundFactory.Create(
            Candidate(type, credentials, disabled),
            Request(),
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void OllamaChannelWithoutKeysBuildsTheNativeTransformer()
    {
        // channel_llm.go:1392-1405: no stored key is a supported configuration;
        // the outbound is built without an API key provider.
        var transformer = Assert.IsType<OllamaOutboundTransformer>(
            Create(ChannelOutboundFactory.OllamaChannelType, "{}"));

        Assert.Equal(ApiFormats.OllamaChat, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal("http://localhost:11434/api/chat", httpRequest.Url);
        Assert.Null(httpRequest.Auth);
        Assert.False(Body(httpRequest).TryGetProperty("tools", out _));
    }

    [Fact]
    public void OllamaChannelWithAKeySendsABearerToken()
    {
        var transformer = Create(
            ChannelOutboundFactory.OllamaChannelType,
            """{"apiKey":"ollama-key"}""");

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("ollama-key", httpRequest.Auth.ApiKey);
    }

    [Fact]
    public void OllamaChannelWithOnlyDisabledKeysSendsNoAuth()
    {
        // The reference installs the provider only when there is at least one
        // enabled key; a fully disabled key set means an unauthenticated request
        // rather than reusing the disabled key.
        var transformer = Create(
            ChannelOutboundFactory.OllamaChannelType,
            """{"apiKey":"ollama-key"}""",
            """[{"key":"ollama-key"}]""");

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Null(httpRequest.Auth);
    }

    [Fact]
    public void OllamaChannelSendsEveryRequestBodyFieldUpstream()
    {
        var transformer = Create(
            ChannelOutboundFactory.OllamaChannelType,
            """{"apiKey":"ollama-key"}""");

        var body = Body(transformer.TransformRequest(Request()));

        Assert.Equal("qwen3", body.GetProperty("model").GetString());
        Assert.False(body.GetProperty("stream").GetBoolean());
        var options = body.GetProperty("options");
        Assert.Equal(256, options.GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public void OllamaAnthropicChannelBuildsTheAnthropicTransformer()
    {
        // channel_llm.go:1406-1428: the anthropic outbound with PlatformOllama,
        // which also gives the /v1/messages URL normalization.
        var transformer = Assert.IsType<AnthropicOutboundTransformer>(
            Create(ChannelOutboundFactory.OllamaAnthropicChannelType, """{"apiKey":"ollama-key"}"""));

        Assert.Equal(AnthropicPlatformType.Ollama, transformer.Config.Type);
        Assert.Equal(ApiFormats.AnthropicMessage, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal("http://localhost:11434/v1/messages", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("ollama-key", httpRequest.Auth.ApiKey);
    }

    [Fact]
    public void OllamaAnthropicChannelWithoutKeysBuildsWithoutAuth()
    {
        // The reference test guards exactly this: building without keys must not
        // panic, and the request carries no Authorization header.
        var transformer = Assert.IsType<AnthropicOutboundTransformer>(
            Create(ChannelOutboundFactory.OllamaAnthropicChannelType, "{}"));

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal("http://localhost:11434/v1/messages", httpRequest.Url);
        Assert.Null(httpRequest.Auth);
    }

    [Fact]
    public void OllamaChannelIsNotTheAnthropicChannel()
    {
        // The two types must not fall into each other's branch.
        Assert.IsType<OllamaOutboundTransformer>(
            Create(ChannelOutboundFactory.OllamaChannelType, "{}"));
        Assert.IsType<AnthropicOutboundTransformer>(
            Create(ChannelOutboundFactory.OllamaAnthropicChannelType, "{}"));
    }

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
