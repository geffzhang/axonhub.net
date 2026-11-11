using System.Text;
using AxonHub.Abstractions;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the live endpoint-routing contract: stored Responses endpoints supply
/// the outbound base URL, path and transport, and the model-protocol /
/// channel-test helpers normalize legacy HTTP intent (C144/C148 follow the live
/// tree, see the drift record in porting/00-reference.md).
/// </summary>
public sealed class ResponsesEndpointDispatchTests
{
    private static ChannelCandidate Candidate(
        string type,
        string baseUrl = "https://relay.example",
        string? endpoints = null) => new()
        {
            ChannelId = 77,
            ChannelName = "endpoint-dispatch",
            ActualModel = "gpt-5",
            Type = type,
            BaseUrl = baseUrl,
            Endpoints = endpoints,
            Credentials = """{"apiKey":"provider-key"}""",
        };

    private static LlmRequest ChatRequest(string? apiFormat = ApiFormats.OpenAIResponse)
        => new()
        {
            Model = "gpt-5",
            ApiFormat = apiFormat,
            RequestType = RequestTypes.Chat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    [Fact]
    public void LegacyWebSocketEndpointSuppliesTheCodexBaseUrlPathAndTransport()
    {
        // channel_llm.go:496-503 / channel_endpoint_responses.go:17-49: a stored
        // HTTP Responses endpoint with the legacy websocket transport normalizes
        // to openai/responses-ws and keeps its path; the endpoint-level base URL
        // wins over the channel base URL.
        var transformer = Assert.IsType<CodexOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(
                    ChannelOutboundFactory.CodexChannelType,
                    endpoints:
                        """[{"api_format":"openai/responses","transport":"websocket","path":"/v1/custom/responses","base_url":"https://endpoint.example"}]"""),
                ChatRequest(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(ApiFormats.OpenAIResponseWebSocket, transformer.ApiFormat);

        var request = transformer.TransformRequest(ChatRequest());
        Assert.Equal("https://endpoint.example/v1/custom/responses", request.Url);
    }

    [Fact]
    public void AlphaSearchEndpointPathDrivesTheCodexAlphaUrl()
    {
        var transformer = Assert.IsType<CodexOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(
                    ChannelOutboundFactory.CodexChannelType,
                    endpoints: """[{"api_format":"openai/alpha_search","path":"/v1/custom/alpha"}]"""),
                new LlmRequest
                {
                    Model = "gpt-5",
                    ApiFormat = ApiFormats.OpenAIAlphaSearch,
                    RequestType = RequestTypes.AlphaSearch,
                },
                DateTimeOffset.UnixEpoch));

        var request = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-5",
            ApiFormat = ApiFormats.OpenAIAlphaSearch,
            RequestType = RequestTypes.AlphaSearch,
            AlphaSearch = new AlphaSearchRequest
            {
                Body = Encoding.UTF8.GetBytes("""{"id":"search","model":"client-model"}"""),
            },
        });

        // channel_llm.go:511-514: the alpha endpoint's path is the alpha-search
        // path, not the Responses endpoint path.
        Assert.Equal("https://relay.example/v1/custom/alpha", request.Url);
    }

    [Fact]
    public void StoredWebSocketBaseUrlSelectsTheResponsesWebSocketTransport()
    {
        var transformer = Assert.IsType<ResponsesOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(
                    ChannelOutboundFactory.OpenaiResponsesChannelType,
                    endpoints: """[{"api_format":"openai/responses","base_url":"wss://relay.example"}]"""),
                ChatRequest(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(ApiFormats.OpenAIResponseWebSocket, transformer.ApiFormat);
        Assert.Equal("wss://relay.example/v1/responses", transformer.BuildFullRequestUrl());
    }

    [Fact]
    public void OpenAiChatEndpointSuppliesTheBaseUrlAndPath()
    {
        var transformer = Assert.IsType<AxonHub.Llm.Transformer.OpenAI.OpenAiOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(
                    ChannelOutboundFactory.OpenAiChannelType,
                    endpoints:
                        """[{"api_format":"openai/chat_completions","base_url":"https://endpoint.example/api","path":"/custom/chat"}]"""),
                ChatRequest(ApiFormats.OpenAIChatCompletion),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(
            "https://endpoint.example/api/custom/chat",
            transformer.TransformRequest(ChatRequest(ApiFormats.OpenAIChatCompletion)).Url);
    }

    [Fact]
    public void ForcedProtocolsRewriteLegacyHttpResponsesIntentToWebSocket()
    {
        // channel_endpoint_responses.go:95-110: normalizeModelProtocols rewrites
        // a pinned HTTP Responses format when the stored intent is WebSocket.
        var channel = new ChannelDescriptor
        {
            Id = 1,
            Name = "codex",
            OrderingWeight = 0,
            Type = ChannelOutboundFactory.CodexChannelType,
            BaseUrl = "https://example.test",
            Endpoints = """[{"api_format":"openai/responses","transport":"websocket","path":"/custom"}]""",
            Settings = """{"modelProtocols":[{"model":"test-model","apiFormats":["openai/responses"]}]}""",
        };

        var forced = ChannelEndpointSelection.ForcedApiFormats(
            channel,
            [new ChannelModelEntry("test-model", "test-model", "direct")],
            "test-model");

        Assert.Equal(new[] { ApiFormats.OpenAIResponseWebSocket }, forced);

        // The stored settings document is never mutated by the normalization.
        Assert.Equal(
            """{"modelProtocols":[{"model":"test-model","apiFormats":["openai/responses"]}]}""",
            channel.Settings);
    }

    [Fact]
    public void ForcedHttpProtocolStaysHttpWhenTheChannelHasNoWebSocketIntent()
    {
        var channel = new ChannelDescriptor
        {
            Id = 1,
            Name = "codex",
            OrderingWeight = 0,
            Type = ChannelOutboundFactory.CodexChannelType,
            BaseUrl = "https://example.test",
            Endpoints = """[{"api_format":"openai/responses"},{"api_format":"openai/responses-ws"}]""",
            Settings = """{"modelProtocols":[{"model":"test-model","apiFormats":["openai/responses"]}]}""",
        };

        var forced = ChannelEndpointSelection.ForcedApiFormats(
            channel,
            [new ChannelModelEntry("test-model", "test-model", "direct")],
            "test-model");

        Assert.Equal(new[] { ApiFormats.OpenAIResponse }, forced);
    }

    [Fact]
    public void ChannelTestPrefersChatOverSystemOneOnMixedSurfaces()
    {
        // tester.go:130-168 (TestChannelTestAPIFormatMixedEndpointsFallback):
        // System One only wins when the channel serves nothing else.
        var channel = new ChannelDescriptor
        {
            Id = 1,
            Name = "mixed",
            OrderingWeight = 0,
            Type = ChannelOutboundFactory.OpenAiChannelType,
            BaseUrl = "https://api.example.com/v1",
            SupportedModels = """["test-model"]""",
            Endpoints = """[{"api_format":"typesafe/systemone","path":"/systemone"}]""",
            DefaultTestModel = "test-model",
        };

        Assert.Equal(
            ApiFormats.OpenAIChatCompletion,
            ChannelTestService.ChannelTestApiFormat(channel, "test-model"));
    }

    [Fact]
    public void UsesResponsesWebSocketReadsTheNormalizedChannelEndpoints()
    {
        var legacyWebSocket = new ChannelDescriptor
        {
            Id = 1,
            Name = "codex",
            OrderingWeight = 0,
            Type = ChannelOutboundFactory.CodexChannelType,
            BaseUrl = "https://api.example.com/v1",
            Endpoints = """[{"api_format":"openai/responses","transport":"websocket"}]""",
        };
        var explicitHttpOnWss = new ChannelDescriptor
        {
            Id = 2,
            Name = "openai_responses",
            OrderingWeight = 0,
            Type = ChannelOutboundFactory.OpenaiResponsesChannelType,
            BaseUrl = "wss://api.example.com/v1",
            Endpoints = """[{"api_format":"openai/responses","transport":"http"}]""",
        };

        Assert.True(ChannelTestService.UsesResponsesWebSocket(legacyWebSocket));
        Assert.False(ChannelTestService.UsesResponsesWebSocket(explicitHttpOnWss));
    }

    [Fact]
    public void DefaultEndpointsResolverFollowsTheStoredCodexIntent()
    {
        // axonhub.resolvers.go:34-41 runs (&biz.Channel{Channel: obj}).DefaultEndpoints();
        // a stored WebSocket intent removes the HTTP Responses default while the
        // alpha-search and image defaults survive (channel_endpoint_responses.go:66-78).
        var channel = ChannelMapping.ToGraphQL(new ChannelRecord
        {
            Id = 9,
            Type = ChannelOutboundFactory.CodexChannelType,
            Name = "codex",
            BaseUrl = "https://legacy.example",
            Endpoints = """[{"api_format":"openai/responses","transport":"websocket","path":"/custom"}]""",
        });

        Assert.Equal(
            new[]
            {
                ApiFormats.OpenAIResponseWebSocket,
                ApiFormats.OpenAIAlphaSearch,
                ApiFormats.OpenAIImageGeneration,
                ApiFormats.OpenAIImageEdit,
            },
            channel.GetDefaultEndpoints().Select(static endpoint => endpoint.ApiFormat));

        // The type default keeps a canonical empty base URL; the stored intent
        // only filters formats and never rewrites the default table.
        Assert.All(channel.GetDefaultEndpoints(), static endpoint => Assert.Equal(string.Empty, endpoint.BaseUrl));
    }
}
