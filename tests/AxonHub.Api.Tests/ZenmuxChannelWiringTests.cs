using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.Zenmux;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the ZenMux branches of the channel transformer switch
/// (channel_llm.go:695-705, 1269-1279, 1293-1302; channel_endpoint.go:279-284):
/// the default base URLs per type, the plain-<c>zenmux</c> OpenAI primary with
/// its shared <c>openai/video</c> endpoint, the native <c>zenmux/video</c>
/// outbound on every ZenMux type, and <c>zenmux_video</c>'s dedicated primary.
/// </summary>
public sealed class ZenmuxChannelWiringTests
{
    private static ChannelCandidate Candidate(string type, string baseUrl)
        => new()
        {
            ChannelId = 99,
            ChannelName = type + "-wiring",
            ActualModel = "provider/chat",
            Type = type,
            BaseUrl = baseUrl,
            Credentials = """{"apiKey":"provider-key"}""",
        };

    private static LlmRequest ChatRequest()
        => new()
        {
            Model = "provider/chat",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static LlmRequest VideoRequest(string apiFormat)
        => new()
        {
            Model = "provider/video",
            RequestType = RequestTypes.Video,
            ApiFormat = apiFormat,
            Video = new VideoRequest
            {
                Content = [new VideoContent { Type = "text", Text = "a cat reading" }],
            },
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
    [InlineData(ChannelOutboundFactory.ZenmuxChannelType)]
    [InlineData(ChannelOutboundFactory.ZenmuxResponsesChannelType)]
    public void OpenAiCompatibleTypesApplyTheZenMuxDefaultBaseUrl(string type)
    {
        var transformer = Create(type, string.Empty, ChatRequest());

        var httpRequest = transformer.TransformRequest(ChatRequest());

        // channel_llm_zenmux_test.go:35-36 asserts the prefix: the plain type
        // reaches /chat/completions, the responses type /responses.
        Assert.StartsWith(
            "https://zenmux.ai/api/v1/",
            httpRequest.Url,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlainZenmuxBuildsTheOpenAiTransformer()
    {
        var transformer = Assert.IsType<OpenAiOutboundTransformer>(
            Create(ChannelOutboundFactory.ZenmuxChannelType, string.Empty, ChatRequest()));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);
        Assert.Equal("https://zenmux.ai/api/v1", transformer.BaseUrl);

        var httpRequest = transformer.TransformRequest(ChatRequest());
        Assert.Equal("https://zenmux.ai/api/v1/chat/completions", httpRequest.Url);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
    }

    [Fact]
    public void PlainZenmuxServesOpenAiVideoThroughTheSharedOpenAiOutbound()
    {
        // channel_llm_zenmux_test.go:70-84: the primary OpenAI outbound is also
        // registered for openai/video; the native zenmux/video endpoint is not
        // configured by default.
        var request = VideoRequest(ApiFormats.OpenAIVideo);

        var transformer = Assert.IsType<OpenAiOutboundTransformer>(
            Create(ChannelOutboundFactory.ZenmuxChannelType, string.Empty, request));

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal("https://zenmux.ai/api/v1/videos", httpRequest.Url);
        Assert.Equal(ApiFormats.OpenAIVideo, httpRequest.ApiFormat);
    }

    [Fact]
    public void PlainZenmuxServesResponsesThroughTheResponsesOutbound()
    {
        // channel_llm.go:1269-1279: an openai/responses request on a plain
        // ZenMux channel is served by the Responses outbound, like the openai
        // channel type.
        var request = ChatRequest();
        request.ApiFormat = ApiFormats.OpenAIResponse;

        var transformer = Assert.IsType<ResponsesOutboundTransformer>(
            Create(ChannelOutboundFactory.ZenmuxChannelType, string.Empty, request));

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal("https://zenmux.ai/api/v1/responses", httpRequest.Url);
        Assert.Equal(ApiFormats.OpenAIResponse, httpRequest.ApiFormat);
    }

    [Fact]
    public void ZenmuxResponsesBuildsTheResponsesTransformer()
    {
        var transformer = Assert.IsType<ResponsesOutboundTransformer>(
            Create(ChannelOutboundFactory.ZenmuxResponsesChannelType, string.Empty, ChatRequest()));

        Assert.Equal(ApiFormats.OpenAIResponse, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(ChatRequest());
        Assert.Equal("https://zenmux.ai/api/v1/responses", httpRequest.Url);
    }

    [Fact]
    public void ZenmuxAnthropicBuildsTheAnthropicDirectTransformer()
    {
        var request = ChatRequest();
        request.ApiFormat = ApiFormats.AnthropicMessage;

        var transformer = Assert.IsType<AnthropicOutboundTransformer>(
            Create(ChannelOutboundFactory.ZenmuxAnthropicChannelType, string.Empty, request));

        Assert.Equal(ApiFormats.AnthropicMessage, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(request);
        Assert.Equal("https://zenmux.ai/api/anthropic/v1/messages", httpRequest.Url);
        Assert.Equal(ApiFormats.AnthropicMessage, httpRequest.ApiFormat);
    }

    [Fact]
    public void ZenmuxVideoBuildsTheNativeTransformer()
    {
        var transformer = Assert.IsType<ZenmuxOutboundTransformer>(
            Create(ChannelOutboundFactory.ZenmuxVideoChannelType, string.Empty, ChatRequest()));

        Assert.Equal(ApiFormats.ZenmuxVideo, transformer.ApiFormat);

        var createRequest = transformer.TransformRequest(VideoRequest(ApiFormats.ZenmuxVideo));
        Assert.Equal("https://zenmux.ai/api/v1/videos", createRequest.Url);

        var pollRequest = transformer.BuildGetVideoTaskRequest("task-1");
        Assert.Equal("https://zenmux.ai/api/v1/videos/task-1", pollRequest.Url);

        // The native transformer also serves chat through its embedded OpenAI
        // outbound (outbound.go:85-91).
        var chatRequest = transformer.TransformRequest(ChatRequest());
        Assert.Equal("https://zenmux.ai/api/v1/chat/completions", chatRequest.Url);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, chatRequest.ApiFormat);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.ZenmuxChannelType)]
    [InlineData(ChannelOutboundFactory.ZenmuxResponsesChannelType)]
    [InlineData(ChannelOutboundFactory.ZenmuxAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.ZenmuxVideoChannelType)]
    public void EveryZenmuxTypeCanServeTheNativeVideoEndpoint(string type)
    {
        // channel_llm_zenmux_test.go:86-106: a channel that configured the
        // zenmux/video endpoint gets the native transformer. The slice has no
        // endpoint records, so the persisted-format lookup reaches it directly
        // (C082).
        var transformer = Assert.IsType<ZenmuxOutboundTransformer>(
            Create(type, string.Empty, VideoRequest(ApiFormats.ZenmuxVideo)));

        var createRequest = transformer.TransformRequest(VideoRequest(ApiFormats.ZenmuxVideo));

        Assert.EndsWith("/videos", createRequest.Url, StringComparison.Ordinal);
        Assert.Equal(ApiFormats.ZenmuxVideo, createRequest.ApiFormat);
    }

    [Fact]
    public void ZenmuxAnthropicDefaultsItsNativeVideoEndpointToTheAnthropicBaseUrl()
    {
        var transformer = Assert.IsType<ZenmuxOutboundTransformer>(
            Create(
                ChannelOutboundFactory.ZenmuxAnthropicChannelType,
                string.Empty,
                VideoRequest(ApiFormats.ZenmuxVideo)));

        var createRequest = transformer.TransformRequest(VideoRequest(ApiFormats.ZenmuxVideo));

        Assert.Equal("https://zenmux.ai/api/anthropic/v1/videos", createRequest.Url);
    }

    [Fact]
    public void CustomBaseUrlsArePreserved()
    {
        var chatTransformer = Create(
            ChannelOutboundFactory.ZenmuxChannelType,
            "https://custom.test/api",
            ChatRequest());

        var chatRequest = chatTransformer.TransformRequest(ChatRequest());
        Assert.Equal("https://custom.test/api/v1/chat/completions", chatRequest.Url);

        var videoTransformer = Assert.IsType<ZenmuxOutboundTransformer>(
            Create(
                ChannelOutboundFactory.ZenmuxVideoChannelType,
                "https://custom.test/api",
                VideoRequest(ApiFormats.ZenmuxVideo)));

        var createRequest = videoTransformer.TransformRequest(VideoRequest(ApiFormats.ZenmuxVideo));
        Assert.Equal("https://custom.test/api/v1/videos", createRequest.Url);

        var pollRequest = videoTransformer.BuildGetVideoTaskRequest("task-1");
        Assert.Equal("https://custom.test/api/v1/videos/task-1", pollRequest.Url);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.ZenmuxChannelType)]
    [InlineData(ChannelOutboundFactory.ZenmuxResponsesChannelType)]
    [InlineData(ChannelOutboundFactory.ZenmuxAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.ZenmuxVideoChannelType)]
    public void ChannelsWithoutKeysAreRejected(string type)
    {
        var candidate = Candidate(type, "https://zenmux.ai/api/v1");
        candidate.Credentials = "{}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                candidate,
                ChatRequest(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel " + type + "-wiring", error.Message);
    }
}
