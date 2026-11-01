using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.Minimax;
using AxonHub.Llm.Transformer.Zai;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the Zai/Zhipu/Xiaomi/MiniMax branches of the channel transformer
/// switch (channel_llm.go:582-595, 898, 1029-1052, 1303-1315): the chat
/// families use the Zai outbound with the v4/v1 route, the *Anthropic channel
/// types serve anthropic/messages through their anthropic platform, and
/// MiniMax owns chat plus the image route.
/// </summary>
public sealed class ZaiMinimaxChannelWiringTests
{
    private static ChannelCandidate Candidate(string type, string baseUrl)
        => new()
        {
            ChannelId = 87,
            ChannelName = type + "-wiring",
            ActualModel = "glm-5.3-flash",
            Type = type,
            BaseUrl = baseUrl,
            Credentials = """{"apiKey":"provider-key"}""",
        };

    private static LlmRequest Request(
        string? apiFormat = null,
        string model = "glm-5.3-flash",
        string? requestType = null)
        => new()
        {
            Model = model,
            ApiFormat = apiFormat,
            RequestType = requestType,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static IOutboundTransformer Create(
        string type,
        string baseUrl,
        LlmRequest request)
        => ChannelOutboundFactory.Create(
            Candidate(type, baseUrl),
            request,
            DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(ChannelOutboundFactory.ZaiChannelType)]
    [InlineData(ChannelOutboundFactory.ZhipuChannelType)]
    [InlineData(ChannelOutboundFactory.ZaiAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.ZhipuAnthropicChannelType)]
    public void ZaiFamilyChatUsesTheVersionedZaiOutbound(string type)
    {
        var transformer = Assert.IsType<ZaiOutboundTransformer>(
            Create(type, "https://open.bigmodel.cn/api/paas", Request()));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal(
            "https://open.bigmodel.cn/api/paas/v4/chat/completions",
            httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.XiaomiChannelType)]
    [InlineData(ChannelOutboundFactory.XiaomiAnthropicChannelType)]
    public void XiaomiChatUsesTheV1ZaiOutbound(string type)
    {
        var transformer = Assert.IsType<ZaiOutboundTransformer>(
            Create(type, "https://api.xiaomi.com", Request(model: "mimo-v2-flash")));

        var httpRequest = transformer.TransformRequest(Request(model: "mimo-v2-flash"));

        Assert.Equal("https://api.xiaomi.com/v1/chat/completions", httpRequest.Url);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.ZaiAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.ZhipuAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.XiaomiAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.MinimaxAnthropicChannelType)]
    public void AnthropicVariantsServeMessagesThroughTheAnthropicOutbound(string type)
    {
        var anthropicRequest = Request(apiFormat: ApiFormats.AnthropicMessage, model: "glm-5.3");

        var transformer = Assert.IsType<AnthropicOutboundTransformer>(
            Create(type, "https://open.bigmodel.cn/api/anthropic", anthropicRequest));

        Assert.Equal(ApiFormats.AnthropicMessage, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(anthropicRequest);

        Assert.EndsWith("/messages", httpRequest.Url, StringComparison.Ordinal);
        Assert.Equal(ApiFormats.AnthropicMessage, httpRequest.ApiFormat);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
    }

    [Fact]
    public void ZaiChatFallsBackToTheSessionIdForRequestId()
    {
        var request = Request();
        // The endpoint threads the shared session id onto the wire request
        // (C062); the Zai transformer reads it for request_id.
        request.RawRequest = new HttpRequestData { SessionId = "trace-123456" };

        var transformer = Assert.IsType<ZaiOutboundTransformer>(
            Create(ChannelOutboundFactory.ZaiChannelType, "https://api.z.ai/api/paas/v4", request));

        var httpRequest = transformer.TransformRequest(request);

        using var body = System.Text.Json.JsonDocument.Parse(httpRequest.Body!);
        Assert.Equal(
            "trace-123456",
            body.RootElement.GetProperty("request_id").GetString());
    }

    [Fact]
    public void MinimaxChannelBuildsTheMinimaxTransformer()
    {
        var transformer = Assert.IsType<MinimaxOutboundTransformer>(
            Create(ChannelOutboundFactory.MinimaxChannelType, "https://api.minimax.io", Request()));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var chat = transformer.TransformRequest(Request(model: "abab6.5"));
        Assert.Equal("https://api.minimax.io/v1/chat/completions", chat.Url);
        Assert.Equal("provider-key", chat.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);

        var imageRequest = Request(
            apiFormat: ApiFormats.OpenAIImageGeneration,
            model: "image-01",
            requestType: RequestTypes.Image);
        imageRequest.Image = new ImageRequest { Prompt = "cat" };

        var image = transformer.TransformRequest(imageRequest);
        Assert.Equal("https://api.minimax.io/v1/image_generation", image.Url);
        Assert.Equal(ApiFormats.OpenAIImageGeneration, image.ApiFormat);
    }

    [Fact]
    public void MinimaxAnthropicChatUsesTheMinimaxTransformer()
    {
        // The anthropic channel type only changes the anthropic/messages route;
        // a chat-format endpoint still uses the provider family outbound.
        var transformer = Assert.IsType<MinimaxOutboundTransformer>(
            Create(
                ChannelOutboundFactory.MinimaxAnthropicChannelType,
                "https://api.minimax.io",
                Request()));

        var httpRequest = transformer.TransformRequest(Request(model: "abab6.5"));

        Assert.Equal("https://api.minimax.io/v1/chat/completions", httpRequest.Url);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.ZaiChannelType)]
    [InlineData(ChannelOutboundFactory.ZhipuChannelType)]
    [InlineData(ChannelOutboundFactory.ZaiAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.ZhipuAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.XiaomiChannelType)]
    [InlineData(ChannelOutboundFactory.XiaomiAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.MinimaxChannelType)]
    [InlineData(ChannelOutboundFactory.MinimaxAnthropicChannelType)]
    public void ChannelsWithoutKeysAreRejected(string type)
    {
        var candidate = Candidate(type, "https://api.example.com");
        candidate.Credentials = "{}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(candidate, Request(), DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel " + type + "-wiring", error.Message);
    }
}
