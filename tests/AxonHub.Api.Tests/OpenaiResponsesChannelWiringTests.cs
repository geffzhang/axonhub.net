using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the openai_responses branch of the channel transformer switch
/// (channel_llm.go:1313-1322): exactly one default endpoint, the responses
/// API (channel_endpoint.go:233), served by the shared Responses outbound,
/// with the transport inferred from the base URL's ws/wss scheme.
/// </summary>
public sealed class OpenaiResponsesChannelWiringTests
{
    private const string BaseUrl = "https://api.openai.com/v1";

    private static ChannelCandidate Candidate(string? baseUrl = BaseUrl) => new()
    {
        ChannelId = 90,
        ChannelName = "openai_responses-wiring",
        ActualModel = "gpt-5",
        Type = ChannelOutboundFactory.OpenaiResponsesChannelType,
        BaseUrl = baseUrl,
        Credentials = """{"apiKey":"provider-key"}""",
    };

    private static LlmRequest Request(
        string? apiFormat = null,
        string model = "gpt-5")
        => new()
        {
            Model = model,
            ApiFormat = apiFormat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    [Fact]
    public void OpenaiResponsesChannelAlwaysUsesTheResponsesOutbound()
    {
        // channel_llm.go:1313-1322: the channel registers exactly one endpoint
        // (responses) and routes every request through the shared Responses
        // outbound, whatever the inbound format.
        var transformer = Assert.IsType<ResponsesOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(),
                Request(ApiFormats.OpenAIChatCompletion),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(ApiFormats.OpenAIResponse, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-5",
            ApiFormat = ApiFormats.OpenAIResponse,
            RequestType = RequestTypes.Chat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        });

        Assert.Equal(BaseUrl + "/responses", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void OpenaiResponsesChannelKeepsACustomBaseUrl()
    {
        var transformer = Assert.IsType<ResponsesOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate("https://responses-proxy.internal/v1"),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(
            "https://responses-proxy.internal/v1/responses",
            transformer.TransformRequest(Request(ApiFormats.OpenAIResponse)).Url);
    }

    [Fact]
    public void AChannelWithoutAnApiKeyIsRejected()
    {
        var candidate = Candidate();
        candidate.Credentials = "{}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(candidate, Request(), DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel openai_responses-wiring", error.Message);
    }

    [Fact]
    public void OpenaiResponsesChannelInfersTheTransportFromTheBaseUrlScheme()
    {
        var wsTransformer = Assert.IsType<ResponsesOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate("wss://relay.example.com"),
                Request(),
                DateTimeOffset.UnixEpoch));
        var httpTransformer = Assert.IsType<ResponsesOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(),
                Request(),
                DateTimeOffset.UnixEpoch));

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "OpenAI-Beta", "responses_websockets=2026-02-06");
        var request = new HttpRequestData
        {
            Headers = headers,
            Body = Encoding.UTF8.GetBytes("""{"model":"gpt-5"}"""),
        };

        // channel_llm.go:276-292 (primaryEndpointTransport → endpointTransport):
        // a ws/wss base URL selects the websocket transport, which leaves the
        // request untouched on finalize; anything else keeps the HTTP
        // transport, which strips the websocket beta header.
        Assert.Same(request, wsTransformer.FinalizeTransportRequest(request));
        Assert.NotSame(request, httpTransformer.FinalizeTransportRequest(request));
    }
}
