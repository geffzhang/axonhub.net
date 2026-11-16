using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Fireworks;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the fireworks branch of the channel transformer switch
/// (channel_llm.go:729-739): the reference installs the Fireworks factory,
/// which pins reasoning_content and supplies the default inference base URL
/// when the channel stores none.
/// </summary>
public sealed class FireworksChannelWiringTests
{
    private static ChannelCandidate Candidate(string? baseUrl, string credentials) => new()
    {
        ChannelId = 82,
        ChannelName = "fireworks-wiring",
        ActualModel = "accounts/fireworks/models/llama-v3p1-8b-instruct",
        Type = ChannelOutboundFactory.FireworksChannelType,
        BaseUrl = baseUrl,
        Credentials = credentials,
    };

    private static LlmRequest Request()
        => new()
        {
            Model = "accounts/fireworks/models/llama-v3p1-8b-instruct",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    [Fact]
    public void FireworksChannelUsesTheDefaultInferenceEndpoint()
    {
        var transformer = Assert.IsType<OpenAiOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(null, """{"apiKey":"fw-key"}"""),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(FireworksOutboundTransformer.DefaultBaseUrl, transformer.BaseUrl);
        Assert.Equal(
            "https://api.fireworks.ai/inference/v1/chat/completions",
            transformer.TransformRequest(Request()).Url);
        Assert.Equal("fw-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void FireworksChannelKeepsTheReasoningContentField()
    {
        // The factory must build the Fireworks variant, not a plain OpenAI one:
        // only the former pins ReasoningField.Content for the channel.
        var transformer = Assert.IsType<OpenAiOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate("https://api.fireworks.ai/inference/v1", """{"apiKey":"fw-key"}"""),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(
            ReasoningField.Content,
            transformer.Config.ReasoningField);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);
    }

    [Fact]
    public void FireworksChannelWithoutKeysIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(null, "{}"),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel fireworks-wiring", error.Message);
    }
}
