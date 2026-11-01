using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.OpenCode;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the OpenCode Go outbound transformer
/// (llm/transformer/opencode/outbound.go and outbound_test.go).
/// </summary>
public sealed class OpenCodeOutboundTransformerTests
{
    // ------------------------------------------------------------------
    // Model routing (reference TestRouteForModel)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("deepseek-v4-pro", OpenCodeRoute.Deepseek)]
    [InlineData("deepseek-v4-flash", OpenCodeRoute.Deepseek)]
    [InlineData("grok-4.5", OpenCodeRoute.Responses)]
    [InlineData("gpt-5.6-luna", OpenCodeRoute.Responses)]
    [InlineData("minimax-m3", OpenCodeRoute.Anthropic)]
    [InlineData("minimax-m2.7", OpenCodeRoute.Anthropic)]
    [InlineData("qwen3.8-max", OpenCodeRoute.Anthropic)]
    [InlineData("qwen3.7-plus", OpenCodeRoute.Anthropic)]
    [InlineData("glm-5.2", OpenCodeRoute.Chat)]
    [InlineData("kimi-k3", OpenCodeRoute.Chat)]
    [InlineData("mimo-v2.5", OpenCodeRoute.Chat)]
    [InlineData("hy3", OpenCodeRoute.Chat)]
    [InlineData("unknown-model", OpenCodeRoute.Chat)]
    public void TestRouteForModel(string model, OpenCodeRoute expected)
        => Assert.Equal(expected, OpenCodeOutboundTransformer.RouteForModel(model));

    [Theory]
    [InlineData("deepseek-v4-flash", "https://opencode.ai/zen/go/v1/chat/completions", "deepseek")]
    [InlineData("grok-4.5", "https://opencode.ai/zen/go/v1/responses", "responses")]
    [InlineData("gpt-5.6-luna", "https://opencode.ai/zen/go/v1/responses", "responses")]
    [InlineData("minimax-m3", "https://opencode.ai/zen/go/v1/messages", "anthropic")]
    [InlineData("qwen3.8-max", "https://opencode.ai/zen/go/v1/messages", "anthropic")]
    [InlineData("glm-5.2", "https://opencode.ai/zen/go/v1/chat/completions", "chat")]
    public void TransformRequest_RoutesByModel(
        string model,
        string expectedUrl,
        string expectedRoute)
    {
        var request = Request(model);
        request.RawRequest = new HttpRequestData { SessionId = "session-123" };

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal(expectedUrl, httpRequest.Url);
        Assert.Equal("POST", httpRequest.Method);
        Assert.Equal(
            "session-123",
            HttpClientUtils.HeaderGet(httpRequest.Headers, OpenCodeHeaders.SessionHeader));

        // The route must be recorded for response routing.
        Assert.NotNull(httpRequest.TransformerMetadata);
        Assert.Equal(
            expectedRoute,
            httpRequest.TransformerMetadata![OpenCodeOutboundTransformer.RouteMetadataKey]
                .GetString());
    }

    // ------------------------------------------------------------------
    // Session affinity (reference TestOutboundTransformer_TransformRequest_SessionHeaderPrecedence)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequest_SessionHeaderPrecedence()
    {
        // resolveSessionID probes the headers in order: the current session
        // header, then X-Session-Id, then the legacy affinity header, then the
        // shared context session.
        var request = Request("glm-5.2");
        request.RawRequest = new HttpRequestData
        {
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                [OpenCodeHeaders.SessionAffinityHeader] = ["affinity-session"],
                [OpenCodeHeaders.SessionIdHeader] = ["session-id"],
                [OpenCodeHeaders.SessionHeader] = ["explicit-session"],
            },
            SessionId = "context-session",
        };

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal(
            "explicit-session",
            HttpClientUtils.HeaderGet(httpRequest.Headers, OpenCodeHeaders.SessionHeader));
    }

    [Theory]
    [InlineData(OpenCodeHeaders.SessionIdHeader, "session-id")]
    [InlineData(OpenCodeHeaders.SessionAffinityHeader, "affinity-session")]
    public void TransformRequest_LegacySessionHeadersOverrideTheSharedSession(
        string header,
        string expected)
    {
        var request = Request("glm-5.2");
        request.RawRequest = new HttpRequestData
        {
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                [header] = [expected],
            },
            SessionId = "context-session",
        };

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal(
            expected,
            HttpClientUtils.HeaderGet(httpRequest.Headers, OpenCodeHeaders.SessionHeader));
    }

    [Fact]
    public void TransformRequest_SessionHeaderValueIsTrimmed()
    {
        var request = Request("glm-5.2");
        request.RawRequest = new HttpRequestData
        {
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                [OpenCodeHeaders.SessionHeader] = ["  ses_trimmed  "],
            },
        };

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal(
            "ses_trimmed",
            HttpClientUtils.HeaderGet(httpRequest.Headers, OpenCodeHeaders.SessionHeader));
    }

    [Fact]
    public void TransformRequest_UsesTheSharedSessionAsFallback()
    {
        var request = Request("glm-5.2");

        // Port of shared.WithSessionID(ctx, ...): the trace session is carried
        // on the wire request (difference C062).
        request.RawRequest = new HttpRequestData { SessionId = " context-session " };

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal(
            "context-session",
            HttpClientUtils.HeaderGet(httpRequest.Headers, OpenCodeHeaders.SessionHeader));
    }

    [Fact]
    public void TransformRequest_GeneratesSessionHeader()
    {
        var httpRequest = Transformer().TransformRequest(Request("glm-5.2"));

        var sessionId = HttpClientUtils.HeaderGet(
            httpRequest.Headers,
            OpenCodeHeaders.SessionHeader);
        Assert.False(string.IsNullOrEmpty(sessionId));
        Assert.True(Guid.TryParse(sessionId, out _));
    }

    // ------------------------------------------------------------------
    // WithSessionHeader (reference TestWithSessionHeader_*)
    // ------------------------------------------------------------------

    [Fact]
    public void WithSessionHeader_PreservesCustomizedOutboundCapabilities()
    {
        var baseOutbound = ResponsesOutboundTransformer.Create(
            "https://opencode.ai/zen/go",
            "test-api-key");

        var wrapped = OpenCodeSessionHeader.WithSessionHeader(baseOutbound);

        Assert.NotNull(wrapped);
        Assert.IsType<OpenCodeSessionHeaderCustomizedOutbound>(wrapped);
        Assert.IsAssignableFrom<IChannelCustomizedExecutor>(wrapped);
        Assert.IsAssignableFrom<ITransportRequestFinalizer>(wrapped);
    }

    [Fact]
    public void WithSessionHeader_NilOutbound()
        => Assert.Null(OpenCodeSessionHeader.WithSessionHeader(null));

    [Fact]
    public void WithSessionHeader_AnthropicOutbound()
    {
        var baseOutbound = AnthropicOutboundTransformer.Create(
            "https://opencode.ai/zen/go",
            "test-api-key");

        var wrapped = OpenCodeSessionHeader.WithSessionHeader(baseOutbound);
        Assert.IsType<OpenCodeSessionHeaderOutbound>(wrapped);

        var request = Request("minimax-m3");
        request.RawRequest = new HttpRequestData { SessionId = "legacy-anthropic-session" };

        var httpRequest = wrapped!.TransformRequest(request);

        Assert.Equal("https://opencode.ai/zen/go/v1/messages", httpRequest.Url);
        Assert.Equal(
            "legacy-anthropic-session",
            HttpClientUtils.HeaderGet(httpRequest.Headers, OpenCodeHeaders.SessionHeader));
    }

    // ------------------------------------------------------------------
    // Sub-transformer behaviour
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequest_DeepSeekRouteEmitsThinking()
    {
        var httpRequest = Transformer().TransformRequest(Request("deepseek-v4-flash"));

        Assert.Equal(
            "enabled",
            Body(httpRequest).GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public void TransformRequest_ExposesTheSelectedApiKey()
    {
        var transformer = Transformer();
        Assert.Null(transformer.SelectedApiKey);

        transformer.TransformRequest(Request("glm-5.2"));

        Assert.Equal("test-api-key", transformer.SelectedApiKey);
    }

    // ------------------------------------------------------------------
    // Response / stream dispatch (reference TestOutboundTransformer_TransformResponse_*)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(
        "chat",
        """{"id":"chatcmpl-1","object":"chat.completion","model":"glm-5.2","choices":[{"index":0,"message":{"role":"assistant","content":"Hi"},"finish_reason":"stop"}]}""")]
    [InlineData(
        "anthropic",
        """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"Hi"}],"model":"minimax-m3","stop_reason":"end_turn"}""")]
    [InlineData(
        "responses",
        """{"id":"resp_1","object":"response","created_at":1750000000,"status":"completed","model":"grok-4.5","output":[{"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"Hi"}]}]}""")]
    public void TransformResponse_RoutesByMetadata(string route, string body)
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(body),
            Request = new HttpRequestData
            {
                TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [OpenCodeOutboundTransformer.RouteMetadataKey] =
                        JsonSerializer.SerializeToElement(route),
                },
            },
        };

        var result = Transformer().TransformResponse(response);

        Assert.Single(result.Choices);
        Assert.False(string.IsNullOrEmpty(result.Choices[0].Message!.Content!.Content));
    }

    [Fact]
    public void TransformResponse_FallsBackToChatWithoutMetadata()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """{"id":"chatcmpl-1","object":"chat.completion","model":"glm-5.2","choices":[{"index":0,"message":{"role":"assistant","content":"Hi"},"finish_reason":"stop"}]}"""),
        };

        var result = Transformer().TransformResponse(response);

        Assert.Single(result.Choices);
        Assert.Equal("Hi", result.Choices[0].Message!.Content!.Content);
    }

    [Fact]
    public void TransformStream_RoutesByMetadata()
    {
        var request = new HttpRequestData
        {
            TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [OpenCodeOutboundTransformer.RouteMetadataKey] =
                    JsonSerializer.SerializeToElement("deepseek"),
            },
        };

        var chunks = new SliceStream<StreamEvent>(
        [
            new StreamEvent
            {
                Type = "message",
                Data = Encoding.UTF8.GetBytes(
                    """{"id":"chatcmpl-1","choices":[{"index":0,"delta":{"content":"Hi"},"finish_reason":null}]}"""),
            },
            new StreamEvent { Type = "message", Data = Encoding.UTF8.GetBytes("[DONE]") },
        ]);

        using var stream = Transformer().TransformStream(request, chunks);
        var responses = GoStreams.All(stream, out var error);

        Assert.Null(error);
        Assert.Equal(2, responses.Count);
        Assert.Equal("Hi", responses[0].Choices[0].Delta!.Content!.Content);
    }

    // ------------------------------------------------------------------
    // Errors and construction
    // ------------------------------------------------------------------

    [Fact]
    public void TransformError_DelegatesToDefaultChatHandling()
    {
        var error = Transformer().TransformError(new HttpClientError(
            "POST",
            "https://opencode.ai/zen/go/v1/chat/completions",
            400,
            "400 Bad Request",
            Encoding.UTF8.GetBytes(
                """{"error":{"message":"bad request","type":"invalid_request_error"}}""")));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("bad request", error.Detail.Message);
    }

    [Fact]
    public void TransformRequest_NilRequest()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(null));

        Assert.Equal("request is nil", error.Message);
    }

    [Fact]
    public void ConstructorValidation()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => new OpenCodeOutboundTransformer(null!));
        Assert.Equal("config is nil", nil.Message);

        var missingKey = Assert.Throws<InvalidOperationException>(
            () => new OpenCodeOutboundTransformer(new OpenCodeOutboundConfig
            {
                BaseUrl = "https://opencode.ai/zen/go",
            }));
        Assert.Equal("API key provider is required", missingKey.Message);

        var missingUrl = Assert.Throws<InvalidOperationException>(
            () => new OpenCodeOutboundTransformer(new OpenCodeOutboundConfig
            {
                ApiKeyProvider = new StaticApiKeyProvider("k"),
            }));
        Assert.Equal("base URL is required", missingUrl.Message);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static OpenCodeOutboundTransformer Transformer()
        => new(new OpenCodeOutboundConfig
        {
            BaseUrl = "https://opencode.ai/zen/go",
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
        });

    private static LlmRequest Request(string model)
        => new()
        {
            Model = model,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
