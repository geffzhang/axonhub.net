using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the alias branches of the channel transformer switch
/// (channel_llm.go:855-863, 885-909, 990-1024, 1056-1064, 1277-1292;
/// channel_endpoint.go:189-197, 236-237, 251, 268, 277, 296, 303, 307,
/// 332-337): the OpenAI-compatible aliases share the plain OpenAI outbound,
/// the *_anthropic variants are single-endpoint channels serving
/// anthropic/messages through their platform (Direct for qiniu/aihubmix/
/// evolink, LongCat, DeepSeek, Moonshot, Bedrock for anthropic_aws), and
/// xai_responses serves the single responses endpoint without the transport
/// inference that openai_responses gets.
/// </summary>
public sealed class AliasChannelWiringTests
{
    private const string BaseUrl = "https://alias.example.com/v1";

    private static ChannelCandidate Candidate(string type, string? baseUrl = BaseUrl) => new()
    {
        ChannelId = 92,
        ChannelName = type + "-wiring",
        ActualModel = "alias-model",
        Type = type,
        BaseUrl = baseUrl,
        Credentials = """{"apiKey":"provider-key"}""",
    };

    private static LlmRequest ChatRequest() => new()
    {
        Model = "alias-model",
        ApiFormat = ApiFormats.OpenAIChatCompletion,
        Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
    };

    private static LlmRequest AnthropicRequest() => new()
    {
        Model = "alias-model",
        ApiFormat = ApiFormats.AnthropicMessage,
        MaxTokens = 1024,
        Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
    };

    private static LlmRequest ResponsesRequest() => new()
    {
        Model = "alias-model",
        ApiFormat = ApiFormats.OpenAIResponse,
        RequestType = RequestTypes.Chat,
        Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
    };

    private static IOutboundTransformer Create(
        string type,
        LlmRequest? request = null,
        string? baseUrl = null)
        => ChannelOutboundFactory.Create(
            Candidate(type, baseUrl ?? BaseUrl),
            request ?? ChatRequest(),
            DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(ChannelOutboundFactory.AtlascloudChannelType)]
    [InlineData(ChannelOutboundFactory.VercelChannelType)]
    [InlineData(ChannelOutboundFactory.DeepinfraChannelType)]
    [InlineData(ChannelOutboundFactory.QiniuChannelType)]
    [InlineData(ChannelOutboundFactory.PpioChannelType)]
    [InlineData(ChannelOutboundFactory.SiliconflowChannelType)]
    [InlineData(ChannelOutboundFactory.AihubmixChannelType)]
    [InlineData(ChannelOutboundFactory.BurncloudChannelType)]
    [InlineData(ChannelOutboundFactory.GithubChannelType)]
    [InlineData(ChannelOutboundFactory.EvolinkChannelType)]
    [InlineData(ChannelOutboundFactory.GroqChannelType)]
    public void OpenAICompatibleAliasesShareThePlainOpenAIOutbound(string type)
    {
        // channel_llm.go:1277-1292: the alias group installs the plain
        // OpenAI outbound (PlatformOpenAI) on the channel's base URL.
        var transformer = Assert.IsType<OpenAiOutboundTransformer>(Create(type));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(ChatRequest());

        Assert.Equal(BaseUrl + "/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.QiniuAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.AihubmixAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.EvolinkAnthropicChannelType)]
    public void AnthropicAliasVariantsServeMessagesThroughTheDirectPlatform(string type)
    {
        // channel_llm.go:898-909: qiniu/aihubmix/evolink anthropic variants
        // share the plain Direct platform — X-API-Key auth, not Bearer
        // (outbound.go:249-261 only switches LongCat, Ollama, Bedrock and
        // Command Code to Bearer).
        var request = AnthropicRequest();
        var transformer = Assert.IsType<AnthropicOutboundTransformer>(Create(type, request));

        Assert.Equal(ApiFormats.AnthropicMessage, transformer.ApiFormat);
        Assert.Equal(AnthropicPlatformType.Direct, transformer.Config.Type);

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(BaseUrl + "/messages", httpRequest.Url);
        Assert.NotNull(httpRequest.Auth);
        Assert.Equal(AuthTypes.ApiKey, httpRequest.Auth.Type);
        Assert.Equal("provider-key", httpRequest.Auth.ApiKey);
        Assert.Equal("X-API-Key", httpRequest.Auth.HeaderKey);
    }

    [Fact]
    public void LongcatAnthropicServesMessagesThroughTheLongCatPlatform()
    {
        // channel_llm.go:885-893: LongCat keeps Bearer auth.
        var request = AnthropicRequest();
        var transformer = Assert.IsType<AnthropicOutboundTransformer>(
            Create(ChannelOutboundFactory.LongcatAnthropicChannelType, request));

        Assert.Equal(AnthropicPlatformType.LongCat, transformer.Config.Type);

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(BaseUrl + "/messages", httpRequest.Url);
        Assert.NotNull(httpRequest.Auth);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth.Type);
        Assert.Equal("provider-key", httpRequest.Auth.ApiKey);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.DeepseekAnthropicChannelType, AnthropicPlatformType.DeepSeek)]
    [InlineData(ChannelOutboundFactory.MoonshotAnthropicChannelType, AnthropicPlatformType.Moonshot)]
    public void DeepseekAndMoonshotAnthropicServeMessagesThroughTheirPlatforms(
        string type,
        AnthropicPlatformType platform)
    {
        // channel_llm.go:990-998, 1016-1024: dedicated platforms, and both
        // stay on X-API-Key — the Go Bearer condition covers only LongCat,
        // Ollama, Bedrock and Command Code.
        var request = AnthropicRequest();
        var transformer = Assert.IsType<AnthropicOutboundTransformer>(Create(type, request));

        Assert.Equal(ApiFormats.AnthropicMessage, transformer.ApiFormat);
        Assert.Equal(platform, transformer.Config.Type);

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(BaseUrl + "/messages", httpRequest.Url);
        Assert.NotNull(httpRequest.Auth);
        Assert.Equal(AuthTypes.ApiKey, httpRequest.Auth.Type);
        Assert.Equal("provider-key", httpRequest.Auth.ApiKey);
        Assert.Equal("X-API-Key", httpRequest.Auth.HeaderKey);
    }

    [Fact]
    public void AnthropicAwsServesMessagesThroughTheBedrockPlatform()
    {
        // channel_llm.go:1056-1064: anthropic_aws speaks Anthropic Messages
        // through Bedrock — Bearer auth and the /model/{model}/invoke URL
        // envelope (buildFullRequestURL's Bedrock branch).
        var request = AnthropicRequest();
        var transformer = Assert.IsType<AnthropicOutboundTransformer>(
            Create(ChannelOutboundFactory.AnthropicAwsChannelType, request));

        Assert.Equal(AnthropicPlatformType.Bedrock, transformer.Config.Type);

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(BaseUrl + "/model/alias-model/invoke", httpRequest.Url);
        Assert.NotNull(httpRequest.Auth);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth.Type);
        Assert.Equal("provider-key", httpRequest.Auth.ApiKey);
        Assert.Equal(
            "bedrock-2023-05-31",
            HttpClientUtils.HeaderGet(httpRequest.Headers, "Anthropic-Version"));
    }

    [Fact]
    public void XaiResponsesServesTheSingleResponsesEndpoint()
    {
        // channel_llm.go:855-863, channel_endpoint.go:296: exactly one
        // endpoint, the responses API, served by the shared Responses
        // outbound without the transport inference openai_responses gets
        // (no primaryEndpointTransport step).
        var request = ResponsesRequest();
        var transformer = Assert.IsType<ResponsesOutboundTransformer>(
            Create(ChannelOutboundFactory.XaiResponsesChannelType, request));

        Assert.Equal(ApiFormats.OpenAIResponse, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(BaseUrl + "/responses", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Theory]
    [InlineData(BaseUrl)]
    [InlineData("wss://relay.example.com")]
    public void XaiResponsesKeepsTheHttpTransportWhateverTheScheme(string baseUrl)
    {
        // The Go config passes no Transport, so the HTTP transport and its
        // websocket-beta stripping apply even for a ws/wss base URL — unlike
        // openai_responses, where the scheme selects the transport.
        var transformer = Assert.IsType<ResponsesOutboundTransformer>(
            Create(ChannelOutboundFactory.XaiResponsesChannelType, baseUrl: baseUrl));

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "OpenAI-Beta", "responses_websockets=2026-02-06");
        var wireRequest = new HttpRequestData
        {
            Headers = headers,
            Body = Encoding.UTF8.GetBytes("""{"model":"alias-model"}"""),
        };

        Assert.NotSame(wireRequest, transformer.FinalizeTransportRequest(wireRequest));
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.AtlascloudChannelType)]
    [InlineData(ChannelOutboundFactory.VercelChannelType)]
    [InlineData(ChannelOutboundFactory.DeepinfraChannelType)]
    [InlineData(ChannelOutboundFactory.QiniuChannelType)]
    [InlineData(ChannelOutboundFactory.PpioChannelType)]
    [InlineData(ChannelOutboundFactory.SiliconflowChannelType)]
    [InlineData(ChannelOutboundFactory.AihubmixChannelType)]
    [InlineData(ChannelOutboundFactory.BurncloudChannelType)]
    [InlineData(ChannelOutboundFactory.GithubChannelType)]
    [InlineData(ChannelOutboundFactory.EvolinkChannelType)]
    [InlineData(ChannelOutboundFactory.GroqChannelType)]
    [InlineData(ChannelOutboundFactory.QiniuAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.AihubmixAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.EvolinkAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.LongcatAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.MoonshotAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.DeepseekAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.AnthropicAwsChannelType)]
    [InlineData(ChannelOutboundFactory.XaiResponsesChannelType)]
    public void AliasChannelsWithoutKeysAreRejected(string type)
    {
        var candidate = Candidate(type);
        candidate.Credentials = "{}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                candidate,
                ChatRequest(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel " + type + "-wiring", error.Message);
    }
}
