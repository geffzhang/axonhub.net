using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.DeepSeek;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the deepseek branch of the channel transformer switch
/// (channel_llm.go:819-829): the reference installs the DeepSeek outbound
/// instead of falling through to a plain OpenAI channel, so the /beta
/// completion route and the thinking envelope survive channel wiring.
/// </summary>
public sealed class DeepSeekChannelWiringTests
{
    private static ChannelCandidate Candidate(string credentials) => new()
    {
        ChannelId = 81,
        ChannelName = "deepseek-wiring",
        ActualModel = "deepseek-chat",
        Type = ChannelOutboundFactory.DeepSeekChannelType,
        BaseUrl = "https://api.deepseek.com",
        Credentials = credentials,
    };

    private static LlmRequest Request()
        => new()
        {
            Model = "deepseek-chat",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static DeepSeekOutboundTransformer Create(string credentials)
        => Assert.IsType<DeepSeekOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(credentials),
                Request(),
                DateTimeOffset.UnixEpoch));

    [Fact]
    public void DeepSeekChannelBuildsTheDeepSeekTransformer()
    {
        var transformer = Create("""{"apiKey":"deepseek-key"}""");

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());

        // NormalizeBaseURL appends the family version before /chat/completions.
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("deepseek-key", httpRequest.Auth.ApiKey);
        Assert.Equal("deepseek-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void DeepSeekChannelKeepsTheBetaCompletionRoute()
    {
        // The branch must build the DeepSeek outbound, not a plain OpenAI one:
        // only the former rewrites legacy completions to /beta/completions.
        var transformer = Create("""{"apiKey":"deepseek-key"}""");

        var httpRequest = transformer.TransformRequest(new LlmRequest
        {
            Model = "deepseek-chat",
            RequestType = RequestTypes.Completion,
            Completion = new CompletionRequest { Prompt = "Hello" },
        });

        Assert.Equal("https://api.deepseek.com/beta/completions", httpRequest.Url);
        Assert.Equal(ApiFormats.OpenAICompletion, httpRequest.ApiFormat);
        Assert.Equal("deepseek-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void DeepSeekChannelWithoutKeysIsRejected()
    {
        // DeepSeek has no keyless deployment mode, unlike Ollama.
        var error = Assert.Throws<InvalidOperationException>(() => Create("{}"));

        Assert.Equal("missing api key for channel deepseek-wiring", error.Message);
    }

    [Fact]
    public void DeepSeekChannelSendsTheThinkingEnvelope()
    {
        var transformer = Create("""{"apiKey":"deepseek-key"}""");
        var request = Request();
        request.ReasoningEffort = "high";

        var httpRequest = transformer.TransformRequest(request);

        Assert.NotNull(httpRequest.Body);
        using var document = System.Text.Json.JsonDocument.Parse(httpRequest.Body!);
        Assert.Equal(
            "enabled",
            document.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }
}
