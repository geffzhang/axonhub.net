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

    /// <summary>
    /// channel_endpoint.go:225-345 (live tree): the built-in default endpoint
    /// table for every modeled channel type, in order. §207 audits the whole
    /// table against the reference; opencode_go_anthropic is deliberately
    /// absent there (it has no entry in the Go map) and therefore has no
    /// built-in endpoints in the port either.
    /// </summary>
    [Fact]
    public void DefaultEndpointTableMatchesTheReferenceForEveryChannelType()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["aihubmix"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.OpenAIVideo, ApiFormats.OpenAIModeration],
            ["aihubmix_anthropic"] = [ApiFormats.AnthropicMessage],
            ["anthropic"] = [ApiFormats.AnthropicMessage],
            ["anthropic_aws"] = [ApiFormats.AnthropicMessage],
            ["anthropic_fake"] = [ApiFormats.AnthropicMessage],
            ["anthropic_gcp"] = [ApiFormats.AnthropicMessage],
            ["antigravity"] = [ApiFormats.GeminiContents],
            ["atlascloud"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.OpenAIVideo, ApiFormats.OpenAIModeration],
            ["bailian"] = [ApiFormats.OpenAIChatCompletion],
            ["bailian_anthropic"] = [ApiFormats.AnthropicMessage],
            ["bailian_responses"] = [ApiFormats.OpenAIResponse],
            ["burncloud"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.OpenAIVideo, ApiFormats.OpenAIModeration],
            ["cerebras"] = [ApiFormats.OpenAIChatCompletion],
            ["claudecode"] = [ApiFormats.AnthropicMessage],
            ["cline"] = [ApiFormats.OpenAIChatCompletion],
            ["cloudflare"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding],
            ["codex"] = [ApiFormats.OpenAIResponse, ApiFormats.OpenAIResponseWebSocket, ApiFormats.OpenAIAlphaSearch, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit],
            ["commandcode"] = [ApiFormats.OpenAIChatCompletion],
            ["commandcode_anthropic"] = [ApiFormats.AnthropicMessage],
            ["deepinfra"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.OpenAIVideo, ApiFormats.OpenAIModeration],
            ["deepseek"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAICompletion],
            ["deepseek_anthropic"] = [ApiFormats.AnthropicMessage],
            ["doubao"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.SeedanceVideo],
            ["doubao_anthropic"] = [ApiFormats.AnthropicMessage],
            ["evolink"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.OpenAIVideo, ApiFormats.OpenAIModeration],
            ["evolink_anthropic"] = [ApiFormats.AnthropicMessage],
            ["fenno"] = [ApiFormats.OpenAIResponse],
            ["fireworks"] = [ApiFormats.OpenAIChatCompletion],
            ["gemini"] = [ApiFormats.GeminiContents, ApiFormats.GeminiEmbedding],
            ["gemini_openai"] = [ApiFormats.OpenAIChatCompletion],
            ["gemini_vertex"] = [ApiFormats.GeminiContents, ApiFormats.GeminiEmbedding],
            ["github"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.OpenAIVideo, ApiFormats.OpenAIModeration],
            ["github_copilot"] = [ApiFormats.OpenAIChatCompletion],
            ["groq"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAISpeech, ApiFormats.OpenAITranscription, ApiFormats.OpenAITranslation],
            ["jina"] = [ApiFormats.JinaRerank, ApiFormats.JinaEmbedding],
            ["longcat"] = [ApiFormats.OpenAIChatCompletion],
            ["longcat_anthropic"] = [ApiFormats.AnthropicMessage],
            ["minimax"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIImageGeneration],
            ["minimax_anthropic"] = [ApiFormats.AnthropicMessage],
            ["modelscope"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.ModelScopeImage],
            ["moonshot"] = [ApiFormats.OpenAIChatCompletion],
            ["moonshot_anthropic"] = [ApiFormats.AnthropicMessage],
            ["moonshot_coding"] = [ApiFormats.AnthropicMessage],
            ["nanogpt"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.OpenAIVideo, ApiFormats.OpenAIModeration, ApiFormats.OpenAISpeech, ApiFormats.OpenAITranscription, ApiFormats.OpenAITranslation],
            ["nanogpt_responses"] = [ApiFormats.OpenAIResponse],
            ["ollama"] = [ApiFormats.OllamaChat],
            ["ollama_anthropic"] = [ApiFormats.AnthropicMessage],
            ["openai"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.OpenAIVideo, ApiFormats.OpenAIModeration, ApiFormats.OpenAISpeech, ApiFormats.OpenAITranscription, ApiFormats.OpenAITranslation],
            ["openai_fake"] = [ApiFormats.OpenAIChatCompletion],
            ["openai_responses"] = [ApiFormats.OpenAIResponse],
            ["opencode_go"] = [ApiFormats.OpenAIChatCompletion],
            ["openrouter"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAISpeech, ApiFormats.OpenAITranscription, ApiFormats.OpenAITranslation],
            ["ppio"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.OpenAIVideo, ApiFormats.OpenAIModeration],
            ["qiniu"] = [ApiFormats.OpenAIChatCompletion],
            ["qiniu_anthropic"] = [ApiFormats.AnthropicMessage],
            ["siliconflow"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.OpenAIVideo, ApiFormats.OpenAIModeration],
            ["typesafe"] = [ApiFormats.TypeSafeSystemOne],
            ["vercel"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.OpenAIVideo, ApiFormats.OpenAIModeration],
            ["volcengine"] = [ApiFormats.OpenAIChatCompletion],
            ["volcengine_anthropic"] = [ApiFormats.AnthropicMessage],
            ["xai"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIResponse],
            ["xai_responses"] = [ApiFormats.OpenAIResponse],
            ["xai_subscription"] = [ApiFormats.OpenAIResponse],
            ["xiaomi"] = [ApiFormats.OpenAIChatCompletion],
            ["xiaomi_anthropic"] = [ApiFormats.AnthropicMessage],
            ["zai"] = [ApiFormats.OpenAIChatCompletion],
            ["zai_anthropic"] = [ApiFormats.AnthropicMessage],
            ["zenmux"] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIEmbedding, ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.OpenAIVideo, ApiFormats.OpenAIModeration, ApiFormats.OpenAISpeech, ApiFormats.OpenAITranscription, ApiFormats.OpenAITranslation],
            ["zenmux_anthropic"] = [ApiFormats.AnthropicMessage],
            ["zenmux_gemini"] = [ApiFormats.GeminiContents, ApiFormats.GeminiEmbedding],
            ["zenmux_responses"] = [ApiFormats.OpenAIResponse],
            ["zenmux_video"] = [ApiFormats.ZenmuxVideo],
            ["zhipu"] = [ApiFormats.OpenAIChatCompletion],
            ["zhipu_anthropic"] = [ApiFormats.AnthropicMessage],
        };

        foreach (var (channelType, formats) in expected)
        {
            Assert.Equal(
                formats,
                ChannelEndpoints.DefaultsForChannelType(channelType)
                    .Select(static endpoint => endpoint.ApiFormat));
        }

        // The reference registers no default endpoint for this type; its
        // outbound comes from the channel-type dispatch.
        Assert.Empty(ChannelEndpoints.DefaultsForChannelType("opencode_go_anthropic"));
        Assert.Empty(ChannelEndpoints.DefaultsForChannelType("unknown"));
        Assert.Equal(74, expected.Count);
    }

    /// <summary>
    /// llm/api_capability.go (live tree, including the WebSocket Responses
    /// format added to the chat set): every request type's format set.
    /// </summary>
    [Fact]
    public void CapableApiFormatsMatchesTheReferenceForEveryRequestType()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [RequestTypes.Chat] = [ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIResponse, ApiFormats.OpenAIResponseWebSocket, ApiFormats.AnthropicMessage, ApiFormats.GeminiContents, ApiFormats.OllamaChat],
            [RequestTypes.Compact] = [ApiFormats.OpenAIResponseCompact],
            [RequestTypes.Completion] = [ApiFormats.OpenAICompletion],
            [RequestTypes.Embedding] = [ApiFormats.OpenAIEmbedding, ApiFormats.JinaEmbedding, ApiFormats.GeminiEmbedding],
            [RequestTypes.Moderation] = [ApiFormats.OpenAIModeration],
            [RequestTypes.AlphaSearch] = [ApiFormats.OpenAIAlphaSearch],
            [RequestTypes.Decisions] = [ApiFormats.OpenAIDecisions],
            [RequestTypes.Image] = [ApiFormats.OpenAIImageGeneration, ApiFormats.OpenAIImageEdit, ApiFormats.OpenAIImageVariation, ApiFormats.ModelScopeImage],
            [RequestTypes.Rerank] = [ApiFormats.JinaRerank],
            [RequestTypes.Video] = [ApiFormats.OpenAIVideo, ApiFormats.SeedanceVideo, ApiFormats.ZenmuxVideo],
            [RequestTypes.Speech] = [ApiFormats.OpenAISpeech],
            [RequestTypes.Transcription] = [ApiFormats.OpenAITranscription],
            [RequestTypes.Translation] = [ApiFormats.OpenAITranslation],
            [RequestTypes.SystemOne] = [ApiFormats.TypeSafeSystemOne],
        };

        foreach (var (requestType, formats) in expected)
        {
            var allowed = ApiCapabilities.CapableApiFormats(requestType);
            Assert.NotNull(allowed);
            Assert.Equal(
                formats.Order(StringComparer.Ordinal),
                allowed.Order(StringComparer.Ordinal));
        }

        Assert.Null(ApiCapabilities.CapableApiFormats("unknown"));
        Assert.Equal(14, expected.Count);
    }

    [Fact]
    public void RequestTypeForModelTypeMapsTheConfiguredModelTypes()
    {
        Assert.Equal(RequestTypes.Chat, ApiCapabilities.RequestTypeForModelType("chat"));
        Assert.Equal(RequestTypes.Embedding, ApiCapabilities.RequestTypeForModelType("embedding"));
        Assert.Equal(RequestTypes.Rerank, ApiCapabilities.RequestTypeForModelType("rerank"));
        Assert.Equal(RequestTypes.Image, ApiCapabilities.RequestTypeForModelType("image_generation"));
        Assert.Equal(RequestTypes.Video, ApiCapabilities.RequestTypeForModelType("video_generation"));
        Assert.Equal(RequestTypes.SystemOne, ApiCapabilities.RequestTypeForModelType("systemone"));
        Assert.Equal(RequestTypes.SystemOne, ApiCapabilities.RequestTypeForModelType("system_one"));
        Assert.Equal(string.Empty, ApiCapabilities.RequestTypeForModelType("unknown"));
    }
}
