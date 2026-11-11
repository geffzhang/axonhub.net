using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Transformer.Gemini.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the gemini_openai branch of the channel transformer switch
/// (channel_llm.go:1109-1118): the reference builds the Gemini OpenAI outbound,
/// normalizes the channel base URL to /v1beta/openai, and wraps constructor
/// errors with "failed to create outbound transformer".
/// </summary>
public sealed class GeminiOpenAiChannelWiringTests
{
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";

    private static ChannelCandidate Candidate(string? baseUrl, string credentials) => new()
    {
        ChannelId = 58,
        ChannelName = "gemini-openai-wiring",
        ActualModel = "gemini-2.5-flash",
        Type = ChannelOutboundFactory.GeminiOpenAiChannelType,
        BaseUrl = baseUrl,
        Credentials = credentials,
    };

    private static LlmRequest Request()
        => new()
        {
            Model = "gemini-2.5-flash",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    [Fact]
    public void GeminiOpenAiChannelNormalizesTheBaseUrlAndUsesTheKey()
    {
        var transformer = Create(DefaultBaseUrl);

        Assert.Equal(DefaultBaseUrl + "/v1beta/openai", transformer.BaseUrl);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());
        Assert.Equal(DefaultBaseUrl + "/v1beta/openai/chat/completions", httpRequest.Url);
        Assert.Equal("gemini-key", httpRequest.Auth!.ApiKey);

        // The audit seam sees the key the attempt used.
        Assert.Equal("gemini-key", transformer.SelectedApiKey);
        Assert.Null(transformer.SelectedApiKeyIndex);
    }

    [Theory]
    [InlineData(DefaultBaseUrl + "/v1beta/openai")]
    [InlineData(DefaultBaseUrl + "/v1beta/openai/")]
    public void AnAlreadyNormalizedBaseUrlIsKept(string baseUrl)
    {
        var transformer = Create(baseUrl);

        Assert.Equal(DefaultBaseUrl + "/v1beta/openai", transformer.BaseUrl);
    }

    [Fact]
    public void AnEmptyBaseUrlIsWrappedAsAConstructionFailure()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Create(baseUrl: null));

        Assert.Equal(
            "failed to create outbound transformer: base URL is required for Gemini OpenAI transformer",
            error.Message);
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public void AChannelWithoutKeysIsRejectedBeforeTheTransformerIsBuilt()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(DefaultBaseUrl, "{}"),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel gemini-openai-wiring", error.Message);
    }

    [Fact]
    public void MetadataIsStrippedAndExtraBodyFlowsThroughTheFactory()
    {
        var transformer = Create(DefaultBaseUrl);

        var request = Request();
        request.Metadata = new Dictionary<string, string> { ["account"] = "identity" };
        request.ReasoningEffort = "high";
        request.ExtraBody = JsonElement.Parse("""
            {"google":{"thinking_config":{"include_thoughts":true}}}
            """).Clone();

        var httpRequest = transformer.TransformRequest(request);

        Assert.Null(httpRequest.Metadata);

        using var document = JsonDocument.Parse(httpRequest.Body!);
        var body = document.RootElement;
        Assert.False(body.TryGetProperty("metadata", out _));
        Assert.False(body.TryGetProperty("reasoning_effort", out _));

        var thinking = body.GetProperty("extra_body").GetProperty("google").GetProperty("thinking_config");
        Assert.Equal(24576L, thinking.GetProperty("thinking_budget").GetInt64());
        Assert.True(thinking.GetProperty("include_thoughts").GetBoolean());
    }

    private static GeminiOpenAiOutboundTransformer Create(string? baseUrl)
        => Assert.IsType<GeminiOpenAiOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(baseUrl, """{"apiKey":"gemini-key"}"""),
                Request(),
                DateTimeOffset.UnixEpoch));
}
