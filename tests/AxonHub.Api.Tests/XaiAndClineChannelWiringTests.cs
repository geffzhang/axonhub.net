using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Cline;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.Xai;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the xAI and Cline branches of the channel transformer switch
/// (channel_llm.go:741-751 and 843-853): xAI is the OpenAI outbound with
/// reasoning_content and a peer responses endpoint, Cline the OpenAI outbound
/// with the {success,data,error,errors} envelope.
/// </summary>
public sealed class XaiAndClineChannelWiringTests
{
    private static ChannelCandidate Candidate(string type, string baseUrl = "https://api.x.ai/v1")
        => new()
        {
            ChannelId = 85,
            ChannelName = type + "-wiring",
            ActualModel = "grok-4",
            Type = type,
            BaseUrl = baseUrl,
            Credentials = """{"apiKey":"provider-key"}""",
        };

    private static LlmRequest Request(string? apiFormat = null, string model = "grok-4")
        => new()
        {
            Model = model,
            ApiFormat = apiFormat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    [Fact]
    public void XaiChannelBuildsTheXaiChatTransformer()
    {
        var transformer = Assert.IsType<XaiOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(ChannelOutboundFactory.XaiChannelType),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal("https://api.x.ai/v1/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void XaiChannelUsesTheDefaultBaseUrlWhenTheCandidateHasNone()
    {
        var candidate = Candidate(ChannelOutboundFactory.XaiChannelType);
        candidate.BaseUrl = null;

        var transformer = Assert.IsType<XaiOutboundTransformer>(
            ChannelOutboundFactory.Create(candidate, Request(), DateTimeOffset.UnixEpoch));

        Assert.Equal(
            "https://api.x.ai/v1/chat/completions",
            transformer.TransformRequest(Request()).Url);
    }

    [Theory]
    [InlineData(ApiFormats.OpenAIResponse)]
    [InlineData(ApiFormats.OpenAIResponseCompact)]
    public void XaiChannelServesTheResponsesEndpoint(string apiFormat)
    {
        // channel_llm.go:437-455: the xai channel's responses endpoint is served
        // by the Responses outbound, not the chat transformer.
        var transformer = Assert.IsType<ResponsesOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(ChannelOutboundFactory.XaiChannelType),
                Request(apiFormat),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(ApiFormats.OpenAIResponse, transformer.ApiFormat);
    }

    [Fact]
    public void ClineChannelBuildsTheClineTransformer()
    {
        var transformer = Assert.IsType<ClineOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(
                    ChannelOutboundFactory.ClineChannelType,
                    "https://api.cline.bot/api/v1"),
                Request(model: "cline-pass/deepseek-v4-flash"),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request(model: "cline-pass/deepseek-v4-flash"));

        Assert.Equal("https://api.cline.bot/api/v1/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("cline-cli", Assert.Single(httpRequest.Headers!["X-Client-Type"]));
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void ClineChannelUnwrapsTheResponseEnvelope()
    {
        var transformer = Assert.IsType<ClineOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(
                    ChannelOutboundFactory.ClineChannelType,
                    "https://api.cline.bot/api/v1"),
                Request(model: "cline-pass/deepseek-v4-flash"),
                DateTimeOffset.UnixEpoch));

        var response = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """{"success":true,"data":{"id":"chatcmpl-9","object":"chat.completion","created":1,"model":"cline-pass/deepseek-v4-flash","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}}"""),
        });

        Assert.Equal("chatcmpl-9", response.Id);
        Assert.Equal("ok", Assert.Single(response.Choices).Message!.Content!.Content);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.XaiChannelType)]
    [InlineData(ChannelOutboundFactory.ClineChannelType)]
    public void ChannelsWithoutKeysAreRejected(string type)
    {
        var candidate = Candidate(type);
        candidate.Credentials = "{}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(candidate, Request(), DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel " + type + "-wiring", error.Message);
    }
}
