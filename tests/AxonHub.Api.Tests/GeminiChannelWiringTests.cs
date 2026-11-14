using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Transformer.Gemini;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the native gemini branches of the channel transformer switch
/// (channel_llm.go:1327-1346): gemini and zenmux_gemini share the native
/// Gemini transformer (zenmux falls back to the ZenMux Vertex base URL when
/// the channel stores none), and gemini_vertex pins the Vertex platform.
/// </summary>
public sealed class GeminiChannelWiringTests
{
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";

    private static ChannelCandidate Candidate(string type, string? baseUrl, string credentials) => new()
    {
        ChannelId = 58,
        ChannelName = "gemini-wiring",
        ActualModel = "gemini-2.5-flash",
        Type = type,
        BaseUrl = baseUrl,
        Credentials = credentials,
    };

    private static LlmRequest Request()
        => new()
        {
            Model = "gemini-2.5-flash",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static GeminiOutboundTransformer Create(string type, string? baseUrl)
        => Assert.IsType<GeminiOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(type, baseUrl, """{"apiKey":"gemini-key"}"""),
                Request(),
                DateTimeOffset.UnixEpoch));

    [Fact]
    public void GeminiChannelUsesTheNativeTransformerWithKeyAndBaseUrl()
    {
        var transformer = Create(ChannelOutboundFactory.GeminiChannelType, DefaultBaseUrl);

        Assert.Equal(ApiFormats.GeminiContents, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());
        Assert.Equal(
            DefaultBaseUrl + "/v1beta/models/gemini-2.5-flash:generateContent",
            httpRequest.Url);
        Assert.Equal("gemini-key", httpRequest.Auth!.ApiKey);
        Assert.Equal("x-goog-api-key", httpRequest.Auth.HeaderKey);
    }

    [Fact]
    public void GeminiChannelKeepsACustomBaseUrl()
    {
        var transformer = Create(
            ChannelOutboundFactory.GeminiChannelType,
            "https://custom.gemini.example.com");

        var httpRequest = transformer.TransformRequest(Request());
        Assert.Equal(
            "https://custom.gemini.example.com/v1beta/models/gemini-2.5-flash:generateContent",
            httpRequest.Url);
    }

    [Fact]
    public void ZenmuxGeminiChannelDefaultsToTheZenmuxVertexBaseUrl()
    {
        var transformer = Create(ChannelOutboundFactory.ZenmuxGeminiChannelType, baseUrl: null);

        var httpRequest = transformer.TransformRequest(Request());
        Assert.Equal(
            "https://zenmux.ai/api/vertex-ai/v1beta/models/gemini-2.5-flash:generateContent",
            httpRequest.Url);
    }

    [Fact]
    public void ZenmuxGeminiChannelKeepsAStoredBaseUrl()
    {
        var transformer = Create(
            ChannelOutboundFactory.ZenmuxGeminiChannelType,
            "https://my-proxy.example.com");

        var httpRequest = transformer.TransformRequest(Request());
        Assert.Equal(
            "https://my-proxy.example.com/v1beta/models/gemini-2.5-flash:generateContent",
            httpRequest.Url);
    }

    [Fact]
    public void GeminiVertexChannelUsesTheVertexUrlShape()
    {
        var transformer = Create(
            ChannelOutboundFactory.GeminiVertexChannelType,
            "https://us-central1-aiplatform.googleapis.com");

        var httpRequest = transformer.TransformRequest(Request());
        Assert.Equal(
            "https://us-central1-aiplatform.googleapis.com/v1/publishers/google/models/gemini-2.5-flash:generateContent",
            httpRequest.Url);
    }

    [Fact]
    public void GeminiVertexUsesTheSelectedKeysProjectAndRegion()
    {
        var transformer = Assert.IsType<GeminiOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(
                    ChannelOutboundFactory.GeminiVertexChannelType,
                    "https://us-central1-aiplatform.googleapis.com",
                    """{"vertexApiKeys":[{"projectID":"proj-a","apiKey":"vk-a","region":"europe-west1"}]}"""),
                Request(),
                DateTimeOffset.UnixEpoch));

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/proj-a/locations/europe-west1"
            + "/publishers/google/models/gemini-2.5-flash:generateContent",
            httpRequest.Url);
    }

    [Fact]
    public void AChannelWithoutKeysIsRejectedBeforeTheTransformerIsBuilt()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(ChannelOutboundFactory.GeminiChannelType, DefaultBaseUrl, "{}"),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel gemini-wiring", error.Message);
    }
}
