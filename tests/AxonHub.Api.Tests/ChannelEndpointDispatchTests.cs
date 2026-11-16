using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.Doubao;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.Jina;
using AxonHub.Llm.Transformer.Minimax;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.OpenCode;
using AxonHub.Llm.Transformer.TypeSafe;
using AxonHub.Llm.Transformer.Zai;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the user-endpoint half of the outbound map
/// (buildChannelWithOutbounds / buildNonDefaultEndpointOutbound,
/// channel_llm.go:236-251, 431-616): a stored endpoint replaces the outbound
/// registered for its API format, is dispatched by format rather than channel
/// type, and supplies its own base URL, path and transport.
/// </summary>
public sealed class ChannelEndpointDispatchTests
{
    private const string OAuthCredentials =
        """{"apiKey":"legacy-key","oauth":{"access_token":"oauth-access","expires_at":"2099-01-01T00:00:00Z"}}""";

    private static ChannelCandidate Candidate(
        string type,
        string baseUrl,
        string? endpoints = null,
        string credentials = """{"apiKey":"provider-key"}""") => new()
        {
            ChannelId = 194,
            ChannelName = type + "-endpoint",
            ActualModel = "glm-5.3-flash",
            Type = type,
            BaseUrl = baseUrl,
            Endpoints = endpoints,
            Credentials = credentials,
        };

    private static LlmRequest Request(
        string? apiFormat = ApiFormats.OpenAIChatCompletion,
        string model = "glm-5.3-flash",
        string? requestType = null) => new()
        {
            Model = model,
            ApiFormat = apiFormat,
            RequestType = requestType,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static IOutboundTransformer Create(
        ChannelCandidate candidate,
        LlmRequest? request = null)
        => ChannelOutboundFactory.Create(
            candidate,
            request ?? Request(),
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void ChatEndpointWithAPathKeepsTheZaiFamilyTransformer()
    {
        // channel_llm.go:461-478: an explicit path is unambiguous and always
        // opts into the family transformer, even on a /v1 channel base URL.
        var transformer = Assert.IsType<ZaiOutboundTransformer>(
            Create(Candidate(
                ChannelOutboundFactory.ZaiChannelType,
                "https://open.bigmodel.cn/api/paas",
                """[{"api_format":"openai/chat_completions","path":"/custom/chat"}]""")));

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal("https://open.bigmodel.cn/api/paas/custom/chat", httpRequest.Url);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
    }

    [Fact]
    public void ChatEndpointWithoutAPathFallsBackToGenericOpenAiOnAV1Base()
    {
        // channel_llm.go:461-486: an empty path on a base URL without the
        // provider version historically used the generic OpenAI transformer
        // (the /v1 route); a user endpoint may still override the base URL.
        var transformer = Assert.IsType<OpenAiOutboundTransformer>(
            Create(Candidate(
                ChannelOutboundFactory.ZaiChannelType,
                "https://ignored.example",
                """[{"api_format":"openai/chat_completions","base_url":"https://relay.example/v1"}]""")));

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal("https://relay.example/v1/chat/completions", httpRequest.Url);
    }

    [Fact]
    public void ChatEndpointWithoutAPathKeepsTheFamilyOnAVersionedBase()
    {
        // channel_llm.go:610-628: providerChatEndpointUsesFamilyVersion opts a
        // zai endpoint back into the family transformer when the resolved base
        // URL carries v4.
        var transformer = Assert.IsType<ZaiOutboundTransformer>(
            Create(Candidate(
                ChannelOutboundFactory.ZaiChannelType,
                "https://open.bigmodel.cn/api/paas/v4",
                """[{"api_format":"openai/chat_completions"}]""")));

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal(
            "https://open.bigmodel.cn/api/paas/v4/chat/completions",
            httpRequest.Url);
    }

    [Fact]
    public void DoubaoChatEndpointPathDrivesTheDoubaoUrl()
    {
        var transformer = Assert.IsType<DoubaoOutboundTransformer>(
            Create(Candidate(
                ChannelOutboundFactory.DoubaoChannelType,
                "https://ark.cn-beijing.volces.com/api/v3",
                """[{"api_format":"openai/chat_completions","path":"/custom/chat"}]""")));

        var httpRequest = transformer.TransformRequest(Request(model: "ep-20250101"));

        Assert.Equal(
            "https://ark.cn-beijing.volces.com/api/v3/custom/chat",
            httpRequest.Url);
    }

    [Fact]
    public void MinimaxImageEndpointPathDrivesTheImageUrl()
    {
        // channel_llm.go:516-529: MiniMax image endpoints keep the MiniMax
        // outbound and now carry the stored path instead of /image_generation.
        var imageRequest = Request(
            apiFormat: ApiFormats.OpenAIImageGeneration,
            model: "image-01",
            requestType: RequestTypes.Image);
        imageRequest.Image = new ImageRequest { Prompt = "cat" };

        var transformer = Assert.IsType<MinimaxOutboundTransformer>(
            Create(
                Candidate(
                    ChannelOutboundFactory.MinimaxChannelType,
                    "https://api.minimax.io",
                    """[{"api_format":"openai/image_generation","path":"/custom/api/images"}]"""),
                imageRequest));

        var httpRequest = transformer.TransformRequest(imageRequest);

        Assert.Equal("https://api.minimax.io/v1/custom/api/images", httpRequest.Url);
    }

    [Fact]
    public void AnthropicEndpointOnAZaiAnthropicChannelUsesTheDirectTransformer()
    {
        // channel_llm.go:554-573: a user anthropic/messages endpoint is built
        // as the direct Anthropic platform with its own base URL and path; the
        // channel type's primary Zai platform only backs the default endpoint.
        var anthropicRequest = Request(apiFormat: ApiFormats.AnthropicMessage, model: "glm-5.3");

        var transformer = Assert.IsType<AnthropicOutboundTransformer>(
            Create(
                Candidate(
                    ChannelOutboundFactory.ZaiAnthropicChannelType,
                    "https://open.bigmodel.cn/api/anthropic",
                    """[{"api_format":"anthropic/messages","base_url":"https://proxy.example/anthropic","path":"/custom/messages"}]"""),
                anthropicRequest));

        var httpRequest = transformer.TransformRequest(anthropicRequest);

        Assert.Equal("https://proxy.example/anthropic/custom/messages", httpRequest.Url);
        Assert.Equal("X-API-Key", httpRequest.Auth?.HeaderKey);
    }

    [Fact]
    public void CommandcodeAnthropicEndpointKeepsBearerAuth()
    {
        // channel_llm.go:554-566: both Command Code channel types keep the
        // Command Code platform (Bearer auth) for an anthropic endpoint.
        var anthropicRequest = Request(apiFormat: ApiFormats.AnthropicMessage, model: "claude-sonnet-5");

        var transformer = Assert.IsType<AnthropicOutboundTransformer>(
            Create(
                Candidate(
                    ChannelOutboundFactory.CommandcodeChannelType,
                    "https://api.commandcode.ai",
                    """[{"api_format":"anthropic/messages","path":"/custom/messages"}]"""),
                anthropicRequest));

        var httpRequest = transformer.TransformRequest(anthropicRequest);

        Assert.Equal("https://api.commandcode.ai/custom/messages", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
    }

    [Fact]
    public void GeminiContentsEndpointPathDrivesTheGeminiUrl()
    {
        var request = Request(apiFormat: ApiFormats.GeminiContents, model: "gemini-3-pro");

        var transformer = Assert.IsType<GeminiOutboundTransformer>(
            Create(
                Candidate(
                    ChannelOutboundFactory.GeminiChannelType,
                    "https://generativelanguage.googleapis.com",
                    """[{"api_format":"gemini/contents","path":"/custom/contents"}]"""),
                request));

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(
            "https://generativelanguage.googleapis.com/custom/contents",
            httpRequest.Url);
    }

    [Fact]
    public void JinaEndpointPathDrivesTheRerankUrl()
    {
        var request = Request(apiFormat: ApiFormats.JinaRerank, model: "jina-reranker-v2");
        request.RequestType = RequestTypes.Rerank;
        request.Rerank = new RerankRequest { Query = "q", Documents = ["d"] };

        var transformer = Assert.IsType<JinaOutboundTransformer>(
            Create(
                Candidate(
                    ChannelOutboundFactory.JinaChannelType,
                    "https://api.jina.ai/v1",
                    """[{"api_format":"jina/rerank","path":"/custom/rerank"}]"""),
                request));

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal("https://api.jina.ai/v1/custom/rerank", httpRequest.Url);
    }

    [Fact]
    public void TypesafeEndpointPathDrivesTheSystemOneUrl()
    {
        var transformer = Assert.IsType<TypeSafeOutboundTransformer>(
            Create(
                Candidate(
                    ChannelOutboundFactory.TypeSafeChannelType,
                    "https://api.typesafe.ai/v1",
                    """[{"api_format":"typesafe/systemone","path":"/custom/systemone"}]"""),
                Request(apiFormat: ApiFormats.TypeSafeSystemOne)));

        Assert.Equal("https://api.typesafe.ai/v1/custom/systemone", transformer.BuildUrl());
    }

    [Fact]
    public void SubscriptionResponsesEndpointOverridesTheSubscriptionWrapper()
    {
        // channel_llm.go:236-251: a user endpoint for the primary format
        // replaces the channel outbound, so xai_subscription's responses
        // endpoint is the plain Responses transformer.
        var request = Request(apiFormat: ApiFormats.OpenAIResponse, model: "grok-4.5");
        request.RequestType = RequestTypes.Chat;

        var transformer = Assert.IsType<ResponsesOutboundTransformer>(
            Create(
                Candidate(
                    ChannelOutboundFactory.XaiSubscriptionChannelType,
                    "https://ignored.example/v1",
                    """[{"api_format":"openai/responses","base_url":"https://proxy.example/grok","path":"/custom/responses"}]""",
                    OAuthCredentials),
                request));

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal("https://proxy.example/grok/custom/responses", httpRequest.Url);
    }

    [Fact]
    public void OpencodeUserEndpointIsWrappedWithTheSessionHeader()
    {
        // channel_llm.go:247-249: opencode wraps every user endpoint with its
        // session header; the default chat endpoint keeps the native OpenCode
        // outbound, but the user endpoint is the generic OpenAI transformer.
        var request = Request();
        request.RawRequest = new HttpRequestData { SessionId = "session-abc" };

        var transformer = Assert.IsType<OpenCodeSessionHeaderOutbound>(
            Create(
                Candidate(
                    ChannelOutboundFactory.OpenCodeGoChannelType,
                    "https://opencode.example",
                    """[{"api_format":"openai/chat_completions","path":"/custom/chat"}]"""),
                request));

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal("https://opencode.example/custom/chat", httpRequest.Url);
        Assert.Equal(
            "session-abc",
            httpRequest.Headers![OpenCodeHeaders.SessionHeader].Single());
    }

    [Fact]
    public void WebSocketTransportOnAChatEndpointIsRejected()
    {
        // channel_llm.go:450-452: only the Responses family may use WebSocket.
        var error = Assert.Throws<InvalidOperationException>(
            () => Create(Candidate(
                ChannelOutboundFactory.OpenAiChannelType,
                "https://relay.example/v1",
                """[{"api_format":"openai/chat_completions","transport":"websocket"}]""")));

        Assert.Equal(
            "websocket transport only supports api_format \"openai/responses\"",
            error.Message);
    }
}
