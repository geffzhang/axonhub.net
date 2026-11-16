using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Longcat;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the longcat branch of the channel transformer switch
/// (channel_llm.go:1121-1131): the reference installs the Longcat outbound,
/// which forces every message content into the array form.
/// </summary>
public sealed class LongcatChannelWiringTests
{
    private static ChannelCandidate Candidate(string credentials) => new()
    {
        ChannelId = 84,
        ChannelName = "longcat-wiring",
        ActualModel = "LongCat-Flash-Omni-2603",
        Type = ChannelOutboundFactory.LongcatChannelType,
        BaseUrl = "https://api.longcat.chat",
        Credentials = credentials,
    };

    private static LlmRequest Request()
        => new()
        {
            Model = "LongCat-Flash-Omni-2603",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    [Fact]
    public void LongcatChannelBuildsTheLongcatTransformer()
    {
        var transformer = Assert.IsType<LongcatOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate("""{"apiKey":"longcat-key"}"""),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal("https://api.longcat.chat/v1/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("longcat-key", httpRequest.Auth.ApiKey);
        Assert.Equal("longcat-key", transformer.SelectedApiKey);

        // The branch must not fall through to a plain OpenAI outbound: only the
        // Longcat wrapper rewrites the string content into a text-part array.
        Assert.NotNull(httpRequest.Body);
        using var document = JsonDocument.Parse(httpRequest.Body!);
        var content = document.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("Hello", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public void LongcatChannelWithoutKeysIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate("{}"),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel longcat-wiring", error.Message);
    }
}
