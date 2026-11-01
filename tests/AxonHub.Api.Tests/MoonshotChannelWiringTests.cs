using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Moonshot;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the moonshot branch of the channel transformer switch
/// (channel_llm.go:831-841): the reference installs the Moonshot outbound,
/// whose request path is chat-only and downgrades json_schema to json_object.
/// </summary>
public sealed class MoonshotChannelWiringTests
{
    private static ChannelCandidate Candidate(string? baseUrl, string credentials) => new()
    {
        ChannelId = 83,
        ChannelName = "moonshot-wiring",
        ActualModel = "moonshot-v1-8k",
        Type = ChannelOutboundFactory.MoonshotChannelType,
        BaseUrl = baseUrl,
        Credentials = credentials,
    };

    private static LlmRequest Request()
        => new()
        {
            Model = "moonshot-v1-8k",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    [Fact]
    public void MoonshotChannelBuildsTheMoonshotTransformer()
    {
        var transformer = Assert.IsType<MoonshotOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate("https://api.moonshot.cn", """{"apiKey":"moonshot-key"}"""),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal("https://api.moonshot.cn/v1/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("moonshot-key", httpRequest.Auth.ApiKey);
        Assert.Equal("moonshot-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void MoonshotChannelDowngradesJsonSchema()
    {
        var transformer = Assert.IsType<MoonshotOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate("https://api.moonshot.cn/v1", """{"apiKey":"moonshot-key"}"""),
                Request(),
                DateTimeOffset.UnixEpoch));

        var request = Request();
        request.ResponseFormat = new ResponseFormat { Type = "json_schema" };

        var httpRequest = transformer.TransformRequest(request);
        Assert.NotNull(httpRequest.Body);

        using var document = JsonDocument.Parse(httpRequest.Body!);
        var responseFormat = document.RootElement.GetProperty("response_format");
        Assert.Equal("json_object", responseFormat.GetProperty("type").GetString());
        Assert.False(responseFormat.TryGetProperty("json_schema", out _));
    }

    [Fact]
    public void MoonshotChannelWithoutKeysIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(null, "{}"),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel moonshot-wiring", error.Message);
    }
}
