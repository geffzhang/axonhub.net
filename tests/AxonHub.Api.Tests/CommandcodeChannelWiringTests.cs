using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the Command Code branches of the channel transformer switch
/// (channel_llm.go:640-647, 683-688, 705-711, 911-918, 1195-1204): both
/// channel types require an HTTPS base URL and a Bearer key; commandcode
/// serves chat through the plain OpenAI outbound and commandcode_anthropic
/// speaks Anthropic Messages through the Command Code platform (Bearer,
/// not X-API-Key).
/// </summary>
public sealed class CommandcodeChannelWiringTests
{
    private const string BaseUrl = "https://commandcode.example.com/v1";

    private static ChannelCandidate Candidate(
        string type,
        string? baseUrl = BaseUrl)
        => new()
        {
            ChannelId = 91,
            ChannelName = type + "-wiring",
            ActualModel = "claude-sonnet-4-5",
            Type = type,
            BaseUrl = baseUrl,
            Credentials = """{"apiKey":"provider-key"}""",
        };

    private static LlmRequest Request(
        string? apiFormat = null,
        string model = "claude-sonnet-4-5")
        => new()
        {
            Model = model,
            ApiFormat = apiFormat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static IOutboundTransformer Create(
        string type,
        string? baseUrl = BaseUrl,
        string? apiFormat = null)
        => ChannelOutboundFactory.Create(
            Candidate(type, baseUrl),
            Request(apiFormat),
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void CommandcodeChannelBuildsTheOpenAIChatOutbound()
    {
        var transformer = Assert.IsType<OpenAiOutboundTransformer>(
            Create(ChannelOutboundFactory.CommandcodeChannelType));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal(BaseUrl + "/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void CommandcodeAnthropicChannelBuildsTheCommandCodeAnthropicOutbound()
    {
        // channel_llm.go:911-918: the Anthropic Messages wire format with the
        // Command Code platform — Authorization: Bearer instead of X-API-Key
        // (outbound.go:245-260).
        var request = Request(apiFormat: ApiFormats.AnthropicMessage);
        var transformer = Assert.IsType<AnthropicOutboundTransformer>(
            Create(ChannelOutboundFactory.CommandcodeAnthropicChannelType, apiFormat: ApiFormats.AnthropicMessage));

        Assert.Equal(ApiFormats.AnthropicMessage, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(BaseUrl + "/messages", httpRequest.Url);
        Assert.NotNull(httpRequest.Auth);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth.Type);
        Assert.Equal("provider-key", httpRequest.Auth.ApiKey);
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(httpRequest.Headers, "X-API-Key"));
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.CommandcodeChannelType)]
    [InlineData(ChannelOutboundFactory.CommandcodeAnthropicChannelType)]
    public void ChannelsWithoutKeysAreRejected(string type)
    {
        var candidate = Candidate(type);
        candidate.Credentials = "{}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(candidate, Request(), DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel " + type + "-wiring", error.Message);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.CommandcodeChannelType)]
    [InlineData(ChannelOutboundFactory.CommandcodeAnthropicChannelType)]
    public void TheKeyCheckRunsBeforeTheBaseUrlCheck(string type)
    {
        // channel_llm.go:683-688 validates credentials before the HTTPS base
        // URL check at :705-711, so the missing-key message wins.
        var candidate = Candidate(type, "http://insecure.example.com");
        candidate.Credentials = "{}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(candidate, Request(), DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel " + type + "-wiring", error.Message);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.CommandcodeChannelType)]
    [InlineData(ChannelOutboundFactory.CommandcodeAnthropicChannelType)]
    public void ABaseUrlThatIsNotHttpsIsRejected(string type)
    {
        // channel_llm.go:640-647 (validateCommandCodeBaseURL): scheme https
        // and a non-empty host, or the same message for both channel types.
        var error = Assert.Throws<InvalidOperationException>(
            () => Create(type, "http://commandcode.example.com/v1"));

        Assert.Equal("Command Code base URL must use HTTPS", error.Message);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.CommandcodeChannelType)]
    [InlineData(ChannelOutboundFactory.CommandcodeAnthropicChannelType)]
    public void AGarbageBaseUrlIsRejected(string type)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Create(type, "not a url"));

        Assert.Equal("Command Code base URL must use HTTPS", error.Message);
    }

    [Theory]
    [InlineData(ChannelOutboundFactory.CommandcodeChannelType)]
    [InlineData(ChannelOutboundFactory.CommandcodeAnthropicChannelType)]
    public void AValidHttpsBaseUrlBuilds(string type)
    {
        // url.Parse lowercases the scheme before the https comparison; the
        // port's Uri.TryCreate normalizes it too, so an uppercase scheme and
        // a trailing slash both pass.
        var transformer = Create(type, "HTTPS://commandcode.example.com/v1/");

        Assert.Equal(
            type == ChannelOutboundFactory.CommandcodeChannelType
                ? ApiFormats.OpenAIChatCompletion
                : ApiFormats.AnthropicMessage,
            transformer.ApiFormat);
    }
}
