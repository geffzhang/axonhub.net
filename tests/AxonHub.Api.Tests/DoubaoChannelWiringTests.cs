using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.Doubao;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the Doubao/Volcengine branches of the channel transformer switch
/// (channel_llm.go:598-604, 720-728, 1003-1015; channel_endpoint.go:271-275,
/// 300-301): doubao and volcengine install the Doubao outbound for chat plus
/// the Seedance video endpoints, while the *_anthropic variants serve
/// anthropic/messages through their Anthropic platform and fall back to the
/// Doubao family outbound for everything else.
/// </summary>
public sealed class DoubaoChannelWiringTests
{
    private const string BaseUrl = "https://ark.cn-beijing.volces.com/api";

    private static ChannelCandidate Candidate(string type, string baseUrl = BaseUrl)
        => new()
        {
            ChannelId = 90,
            ChannelName = type + "-wiring",
            ActualModel = "doubao-seed-2-1-pro",
            Type = type,
            BaseUrl = baseUrl,
            Credentials = """{"apiKey":"provider-key"}""",
        };

    private static LlmRequest ChatRequest()
        => new()
        {
            Model = "doubao-seed-2-1-pro",
            ApiFormat = ApiFormats.OpenAIChatCompletion,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static LlmRequest VideoRequest()
        => new()
        {
            Model = "doubao-seedance-1-0-pro",
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.SeedanceVideo,
            Video = new VideoRequest
            {
                Model = "doubao-seedance-1-0-pro",
                Content = [new VideoContent { Type = "text", Text = "a cat" }],
            },
        };

    private static LlmRequest AnthropicRequest()
        => new()
        {
            Model = "doubao-pro-4k",
            ApiFormat = ApiFormats.AnthropicMessage,
            MaxTokens = 1024,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static IOutboundTransformer Create(string type, LlmRequest request, string? baseUrl = null)
        => ChannelOutboundFactory.Create(
            Candidate(type, baseUrl ?? BaseUrl),
            request,
            DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(ChannelOutboundFactory.DoubaoChannelType)]
    [InlineData(ChannelOutboundFactory.VolcengineChannelType)]
    public void DoubaoFamilyChatUsesTheV3DoubaoOutbound(string type)
    {
        var transformer = Assert.IsType<DoubaoOutboundTransformer>(Create(type, ChatRequest()));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(ChatRequest());

        Assert.Equal(BaseUrl + "/v3/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.DoubaoChannelType)]
    [InlineData(ChannelOutboundFactory.VolcengineChannelType)]
    public void DoubaoFamilySharesTheOutboundWithTheSeedanceTaskApi(string type)
    {
        var transformer = Assert.IsType<DoubaoOutboundTransformer>(Create(type, VideoRequest()));

        var httpRequest = transformer.TransformRequest(VideoRequest());

        Assert.Equal(BaseUrl + "/v3/contents/generations/tasks", httpRequest.Url);
        Assert.Equal(ApiFormats.SeedanceVideo, httpRequest.ApiFormat);
        Assert.Equal(RequestTypes.Video, httpRequest.RequestType);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void DoubaoAnthropicServesMessagesThroughTheDoubaoPlatform()
    {
        var request = AnthropicRequest();
        var transformer = Assert.IsType<AnthropicOutboundTransformer>(
            Create(ChannelOutboundFactory.DoubaoAnthropicChannelType, request));

        Assert.Equal(ApiFormats.AnthropicMessage, transformer.ApiFormat);
        Assert.Equal(AnthropicPlatformType.Doubao, transformer.Config.Type);

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(BaseUrl + "/v1/messages", httpRequest.Url);
        Assert.Equal(ApiFormats.AnthropicMessage, httpRequest.ApiFormat);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void VolcengineAnthropicServesMessagesThroughTheDirectPlatform()
    {
        var request = AnthropicRequest();
        var transformer = Assert.IsType<AnthropicOutboundTransformer>(
            Create(ChannelOutboundFactory.VolcengineAnthropicChannelType, request));

        Assert.Equal(ApiFormats.AnthropicMessage, transformer.ApiFormat);
        Assert.Equal(AnthropicPlatformType.Direct, transformer.Config.Type);

        Assert.Equal(
            BaseUrl + "/v1/messages",
            transformer.TransformRequest(request).Url);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.DoubaoAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.VolcengineAnthropicChannelType)]
    public void AnthropicVariantsKeepTheDoubaoFamilyOutboundForChat(string type)
    {
        var transformer = Assert.IsType<DoubaoOutboundTransformer>(Create(type, ChatRequest()));

        Assert.Equal(
            BaseUrl + "/v3/chat/completions",
            transformer.TransformRequest(ChatRequest()).Url);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.DoubaoAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.VolcengineAnthropicChannelType)]
    public void AnthropicVariantsKeepTheDoubaoFamilyOutboundForSeedance(string type)
    {
        var transformer = Assert.IsType<DoubaoOutboundTransformer>(Create(type, VideoRequest()));

        Assert.Equal(
            BaseUrl + "/v3/contents/generations/tasks",
            transformer.TransformRequest(VideoRequest()).Url);
    }

    [Fact]
    public void DoubaoChannelWithoutKeysIsRejected()
    {
        var candidate = Candidate(ChannelOutboundFactory.DoubaoChannelType);
        candidate.Credentials = "{}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                candidate,
                ChatRequest(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel doubao-wiring", error.Message);
    }
}
