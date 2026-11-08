using System.Text.Json;
using AxonHub.Llm;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Ports the live-tree endpoint tests
/// (internal/server/biz/channel_endpoint_responses_test.go and
/// channel_endpoint_mapping_test.go): the Responses-family normalization, the
/// codex default-endpoint table and the primary transport inference.
/// </summary>
public sealed class ChannelEndpointsTests
{
    private const string Http = ApiFormats.OpenAIResponse;
    private const string WebSocket = ApiFormats.OpenAIResponseWebSocket;

    private static string Json(params ChannelEndpoint[] endpoints)
        => JsonSerializer.Serialize(endpoints);

    [Theory]
    // unconfigured HTTPS / HTTP: both Responses defaults survive.
    [InlineData("https://chatgpt.com/backend-api/codex#", null, new[] { Http, WebSocket })]
    [InlineData("http://example.test:8080/base#", null, new[] { Http, WebSocket })]
    // legacy ws/wss channel base URLs keep only the WebSocket default.
    [InlineData("wss://example.test/base#", null, new[] { WebSocket })]
    [InlineData("ws://example.test/base#", null, new[] { WebSocket })]
    // explicit H / explicit legacy HTTP on a WSS base.
    [InlineData("https://example.test/base#", """[{"api_format":"openai/responses"}]""", new[] { Http })]
    [InlineData("wss://example.test/base#", """[{"api_format":"openai/responses","transport":"http"}]""", new[] { Http })]
    // legacy websocket intent and scheme-inferred override.
    [InlineData("https://example.test/base#", """[{"api_format":"openai/responses","transport":"websocket","path":"/custom"}]""", new[] { WebSocket })]
    [InlineData("https://example.test/base#", """[{"api_format":"openai/responses","base_url":"wss://other.test/custom#"}]""", new[] { WebSocket })]
    // an inherited WSS channel base URL turns an unqualified explicit H into W.
    [InlineData("wss://example.test/base#", """[{"api_format":"openai/responses"}]""", new[] { WebSocket })]
    [InlineData("https://example.test/base#", """[{"api_format":"openai/responses-ws"}]""", new[] { WebSocket })]
    [InlineData("https://example.test/base#", """[{"api_format":"openai/responses"},{"api_format":"openai/responses-ws"}]""", new[] { Http, WebSocket })]
    [InlineData("https://example.test/base#", """[{"api_format":"openai/responses","transport":"http"},{"api_format":"openai/responses","transport":"websocket"}]""", new[] { Http, WebSocket })]
    // unrelated overrides never affect the Responses family.
    [InlineData("https://example.test/base#", """[{"api_format":"openai/image_edit","path":"/edit"}]""", new[] { Http, WebSocket })]
    public void CodexResponsesDefaultEndpointsFollowTheStoredIntent(
        string baseUrl,
        string? endpointsJson,
        string[] expectedFormats)
    {
        var resolved = ChannelEndpoints.Resolve(ChannelEndpoints.CodexChannelType, endpointsJson, baseUrl);

        var formats = new List<string>();
        foreach (var endpoint in resolved)
        {
            if (endpoint.ApiFormat is Http or WebSocket)
            {
                formats.Add(endpoint.ApiFormat);
                // Normalization always clears the legacy transport field.
                Assert.Equal(string.Empty, endpoint.Transport);
            }
        }

        Assert.Equal(expectedFormats, formats);
        // Alpha search and the two image defaults always survive.
        Assert.Equal(expectedFormats.Length + 3, resolved.Count);

        // Reads never mutate the stored document.
        if (endpointsJson is not null)
        {
            var userEndpoints = ChannelEndpoints.ParseUser(endpointsJson);
            var reparsed = ChannelEndpoints.ParseUser(endpointsJson);
            Assert.Equal(userEndpoints.Count, reparsed.Count);
        }
    }

    [Fact]
    public void ExplicitHttpOnAWssBaseBecomesCanonicalHttpsAndIsIdempotent()
    {
        var endpoints = new[]
        {
            new ChannelEndpoint
            {
                ApiFormat = Http,
                Transport = ChannelEndpointTransports.Http,
                Path = "/custom",
            },
        };

        var canonical = ChannelEndpoints.NormalizeEndpoints(endpoints, "wss://example.test:8080/base#");

        var endpoint = Assert.Single(canonical);
        Assert.Equal(Http, endpoint.ApiFormat);
        Assert.Equal("https://example.test:8080/base#", endpoint.BaseUrl);
        Assert.Equal("/custom", endpoint.Path);
        Assert.Equal(string.Empty, endpoint.Transport);

        Assert.Equal(
            canonical,
            ChannelEndpoints.NormalizeEndpoints(canonical, "wss://example.test:8080/base#"));
    }

    [Fact]
    public void WebSocketFormatOwnsTheTransportEvenWithALegacyHttpField()
    {
        var endpoint = new ChannelEndpoint
        {
            ApiFormat = WebSocket,
            Transport = ChannelEndpointTransports.Http,
            BaseUrl = "https://example.test#",
        };

        Assert.Equal(
            ChannelEndpointTransports.WebSocket,
            ChannelEndpoints.EndpointTransport(endpoint));
    }

    [Fact]
    public void PrimaryTransportUsesTheFirstMatchingRawEndpointThenTheChannelScheme()
    {
        Assert.Equal(
            ChannelEndpointTransports.WebSocket,
            ChannelEndpoints.PrimaryEndpointTransport(null, "wss://api.openai.com/v1#", Http));

        Assert.Equal(
            ChannelEndpointTransports.WebSocket,
            ChannelEndpoints.PrimaryEndpointTransport(
                """[{"api_format":"openai/responses","transport":"websocket"}]""",
                "https://api.openai.com/v1",
                Http));

        // A stored WebSocket Responses endpoint makes an HTTP primary infer
        // WebSocket as well.
        Assert.Equal(
            ChannelEndpointTransports.WebSocket,
            ChannelEndpoints.PrimaryEndpointTransport(
                """[{"api_format":"openai/responses-ws"}]""",
                "https://example.test#",
                Http));

        Assert.Equal(
            string.Empty,
            ChannelEndpoints.PrimaryEndpointTransport(null, "https://api.openai.com/v1", Http));
    }

    [Fact]
    public void ResolveMergesDefaultsAndUserOverridesInBuiltInPositions()
    {
        var resolved = ChannelEndpoints.Resolve(
            "openai",
            """
            [
              {"api_format":"openai/chat_completions","path":"/v1/custom/chat"},
              {"api_format":"gemini/contents","path":"/v1/gemini"}
            ]
            """,
            "https://api.example.com");

        Assert.Equal(
            new[]
            {
                ApiFormats.OpenAIChatCompletion,
                ApiFormats.OpenAIEmbedding,
                ApiFormats.OpenAIImageGeneration,
                ApiFormats.OpenAIImageEdit,
                ApiFormats.OpenAIImageVariation,
                ApiFormats.OpenAIVideo,
                ApiFormats.OpenAIModeration,
                ApiFormats.OpenAISpeech,
                ApiFormats.OpenAITranscription,
                ApiFormats.OpenAITranslation,
                ApiFormats.GeminiContents,
            },
            resolved.Select(static endpoint => endpoint.ApiFormat));
        Assert.Equal("/v1/custom/chat", resolved[0].Path);
        Assert.Equal("/v1/gemini", resolved[^1].Path);
    }

    [Fact]
    public void ChatCapabilityIncludesTheWebSocketResponsesFormat()
    {
        var allowed = ApiCapabilities.CapableApiFormats(RequestTypes.Chat);

        Assert.NotNull(allowed);
        Assert.Contains(WebSocket, allowed);
        Assert.Contains(Http, allowed);
    }

    [Fact]
    public void FindUserEndpointReturnsTheLastNormalizedMatchOnly()
    {
        const string endpoints =
            """[{"api_format":"openai/responses","path":"/one"},{"api_format":"openai/responses","base_url":"wss://relay.test/base#","path":"/two"}]""";

        var endpoint = ChannelEndpoints.FindUserEndpoint(endpoints, "https://api.test", Http);
        // The second raw entry normalizes to WebSocket, so each format keeps
        // its own last match.
        Assert.NotNull(endpoint);
        Assert.Equal("/one", endpoint.Path);

        var webSocket = ChannelEndpoints.FindUserEndpoint(endpoints, "https://api.test", WebSocket);
        Assert.NotNull(webSocket);
        Assert.Equal("/two", webSocket.Path);
    }
}
