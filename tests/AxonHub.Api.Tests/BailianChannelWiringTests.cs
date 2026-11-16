using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.Bailian;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the Bailian branches of the channel transformer switch
/// (channel_llm.go:1133-1167, channel_endpoint.go:313-315): <c>bailian</c>
/// uses the Bailian outbound, <c>bailian_responses</c> the shared Responses
/// outbound, and <c>bailian_anthropic</c> / <c>moonshot_coding</c> the plain
/// Anthropic direct outbound.
/// </summary>
public sealed class BailianChannelWiringTests
{
    private static ChannelCandidate Candidate(string type, string baseUrl)
        => new()
        {
            ChannelId = 88,
            ChannelName = type + "-wiring",
            ActualModel = "qwen-max",
            Type = type,
            BaseUrl = baseUrl,
            Credentials = """{"apiKey":"provider-key"}""",
        };

    private static LlmRequest Request(
        string? apiFormat = null,
        string? reasoningEffort = null,
        string? requestType = null)
        => new()
        {
            Model = "qwen-max",
            ApiFormat = apiFormat,
            ReasoningEffort = reasoningEffort,
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

    [Fact]
    public void BailianChannelBuildsTheBailianTransformer()
    {
        var transformer = Assert.IsType<BailianOutboundTransformer>(
            Create(
                ChannelOutboundFactory.BailianChannelType,
                "https://dashscope.aliyuncs.com/compatible-mode/v1",
                Request()));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal(
            "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
            httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void BailianChannelDisablesThinkingForReasoningNone()
    {
        var transformer = Assert.IsType<BailianOutboundTransformer>(
            Create(
                ChannelOutboundFactory.BailianChannelType,
                "https://dashscope.aliyuncs.com/compatible-mode/v1",
                Request()));

        var httpRequest = transformer.TransformRequest(Request(reasoningEffort: "none"));

        using var body = System.Text.Json.JsonDocument.Parse(httpRequest.Body!);
        Assert.False(body.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void BailianResponsesChannelBuildsTheResponsesTransformer()
    {
        var request = Request(apiFormat: ApiFormats.OpenAIResponse);

        var transformer = Assert.IsType<ResponsesOutboundTransformer>(
            Create(
                ChannelOutboundFactory.BailianResponsesChannelType,
                "https://dashscope.aliyuncs.com/compatible-mode/v1",
                request));

        Assert.Equal(ApiFormats.OpenAIResponse, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(request);

        Assert.EndsWith("/responses", httpRequest.Url, StringComparison.Ordinal);
        Assert.Equal(ApiFormats.OpenAIResponse, httpRequest.ApiFormat);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.BailianAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.MoonshotCodingChannelType)]
    public void AnthropicVariantsServeMessagesThroughTheAnthropicOutbound(string type)
    {
        var request = Request(apiFormat: ApiFormats.AnthropicMessage);

        var transformer = Assert.IsType<AnthropicOutboundTransformer>(
            Create(type, "https://dashscope.aliyuncs.com/apps/anthropic", request));

        Assert.Equal(ApiFormats.AnthropicMessage, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(request);

        Assert.EndsWith("/messages", httpRequest.Url, StringComparison.Ordinal);
        Assert.Equal(ApiFormats.AnthropicMessage, httpRequest.ApiFormat);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.BailianChannelType)]
    [InlineData(ChannelOutboundFactory.BailianResponsesChannelType)]
    [InlineData(ChannelOutboundFactory.BailianAnthropicChannelType)]
    [InlineData(ChannelOutboundFactory.MoonshotCodingChannelType)]
    public void ChannelsWithoutKeysAreRejected(string type)
    {
        var candidate = Candidate(type, "https://api.example.com");
        candidate.Credentials = "{}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(candidate, Request(), DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel " + type + "-wiring", error.Message);
    }
}
