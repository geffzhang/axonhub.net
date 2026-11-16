using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the *_fake branches of the channel transformer switch
/// (channel_llm.go:1089-1095): both channel types are keyless, return the fake
/// transformer, and replace the transport with the embedded fixtures.
/// </summary>
public sealed class FakeChannelWiringTests
{
    private const string Model = "claude-3-7-sonnet-20250219";

    [Fact]
    public void AnthropicFakeChannelBuildsTheFixtureTransformerWithoutCredentials()
    {
        var transformer = Create(ChannelOutboundFactory.AnthropicFakeChannelType);

        Assert.IsType<AnthropicFakeTransformer>(transformer);
        Assert.Equal(ApiFormats.AnthropicMessage, transformer.ApiFormat);

        var request = transformer.TransformRequest(Request());
        Assert.Equal("https://fake.anthropic.com/v1/messages", request.Url);

        var response = ((IChannelCustomizedExecutor)transformer)
            .CustomizeExecutor(new ThrowingExecutor())
            .Do(CancellationToken.None, request);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(
            "msg_bdrk_01Fbg5HKuVfmtT6mAMxQoCSn",
            Encoding.UTF8.GetString(response.Body!),
            StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAiFakeChannelBuildsTheFixtureTransformerWithoutCredentials()
    {
        var transformer = Create(ChannelOutboundFactory.OpenaiFakeChannelType);

        Assert.IsType<OpenAiFakeTransformer>(transformer);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var request = transformer.TransformRequest(Request());
        Assert.Equal("https://fake.openai.com/v1/chat/completions", request.Url);

        var response = ((IChannelCustomizedExecutor)transformer)
            .CustomizeExecutor(new ThrowingExecutor())
            .Do(CancellationToken.None, request);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(
            "gen-1754577344-bfGaoVZhBY3iT78Psu02",
            Encoding.UTF8.GetString(response.Body!),
            StringComparison.Ordinal);
    }

    private static IOutboundTransformer Create(string type)
    {
        var candidate = new ChannelCandidate
        {
            ChannelId = 71,
            ChannelName = "fake-channel-wiring",
            ActualModel = Model,
            Type = type,
            Credentials = "{}",
        };

        return ChannelOutboundFactory.Create(candidate, Request(), DateTimeOffset.UnixEpoch);
    }

    private static LlmRequest Request()
        => new()
        {
            Model = Model,
            MaxTokens = 1024,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    /// <summary>Both fake channels customize the executor, so the network transport never runs.</summary>
    private sealed class ThrowingExecutor : IExecutor
    {
        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
            => throw new InvalidOperationException("fake channels must not use the network executor");

        public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
            => throw new InvalidOperationException("fake channels must not use the network executor");
    }
}
