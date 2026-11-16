using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Zai;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Zai/Zhipu outbound (llm/transformer/zai and its
/// outbound_test.go / thinking_test.go): URL normalization, the GLM thinking
/// ladder, the 6-128 rune user_id rule and the metadata-to-field extraction.
/// </summary>
public sealed class ZaiOutboundTransformerTests
{
    public static TheoryData<string, string> UrlCases => new()
    {
        { "https://api.zai.com/v1", "https://api.zai.com/v1/v4/chat/completions" },
        { "https://api.zai.com/v4", "https://api.zai.com/v4/chat/completions" },
        { "https://api.zai.com", "https://api.zai.com/v4/chat/completions" },
        { "https://api.zai.com/", "https://api.zai.com/v4/chat/completions" },
        { "https://api.zai.com/v4/", "https://api.zai.com/v4/chat/completions" },
    };

    // Reference TestOutboundTransformer_TransformRequest_URL.
    [Theory]
    [MemberData(nameof(UrlCases))]
    public void TransformRequest_NormalizesTheVersionedUrl(string baseUrl, string expectedUrl)
    {
        var transformer = Transformer(baseUrl);
        var request = transformer.TransformRequest(SimpleRequest("gpt-4"));

        Assert.Equal("POST", request.Method);
        Assert.Equal(expectedUrl, request.Url);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, request.ApiFormat);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal("test-api-key", request.Auth.ApiKey);
        Assert.Equal("test-api-key", transformer.SelectedApiKey);
        Assert.Equal("application/json", request.Headers!["Content-Type"].Single());
        Assert.Equal("application/json", request.Headers["Accept"].Single());

        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("gpt-4", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("metadata", out _));
    }

    [Fact]
    public void TransformRequest_ValidatesTheRequest()
    {
        Assert.Equal(
            "chat completion request is nil",
            Assert.Throws<InvalidOperationException>(
                () => Transformer("https://api.zai.com/v4").TransformRequest(null)).Message);

        var missingModel = Assert.Throws<TransformerException>(
            () => Transformer("https://api.zai.com/v4").TransformRequest(new LlmRequest
            {
                Messages = [UserMessage()],
            }));
        Assert.Equal("invalid request: model is required", missingModel.Message);

        var missingMessages = Assert.Throws<TransformerException>(
            () => Transformer("https://api.zai.com/v4").TransformRequest(new LlmRequest
            {
                Model = "gpt-4",
                Messages = [],
            }));
        Assert.Equal("invalid request: messages are required", missingMessages.Message);

        var compact = Assert.Throws<TransformerException>(
            () => Transformer("https://api.zai.com/v4").TransformRequest(new LlmRequest
            {
                Model = "gpt-4",
                RequestType = RequestTypes.Compact,
                Messages = [UserMessage()],
            }));
        Assert.Equal(
            "invalid request: compact is only supported by OpenAI Responses API",
            compact.Message);

        var unsupported = Assert.Throws<TransformerException>(
            () => Transformer("https://api.zai.com/v4").TransformRequest(new LlmRequest
            {
                Model = "gpt-4",
                RequestType = RequestTypes.Embedding,
                Messages = [UserMessage()],
            }));
        Assert.Equal("invalid request: embedding is not supported", unsupported.Message);
    }

    // Reference TestOutboundTransformer_TransformRequest_WithMetadata.
    [Fact]
    public void TransformRequest_ExtractsUserIdAndRequestId()
    {
        var request = SimpleRequest("gpt-4");
        request.Metadata = new Dictionary<string, string>
        {
            ["user_id"] = "test-user-123",
            ["request_id"] = "test-request-456",
        };

        var httpRequest = Transformer("https://api.zai.com/v4").TransformRequest(request);
        var body = Parse(httpRequest);

        Assert.Equal("https://api.zai.com/v4/chat/completions", httpRequest.Url);
        Assert.Equal("test-user-123", body.GetProperty("user_id").GetString());
        Assert.Equal("test-request-456", body.GetProperty("request_id").GetString());
        Assert.False(body.TryGetProperty("metadata", out _));
    }

    // The request_id falls back to the shared session id (C062).
    [Fact]
    public void TransformRequest_FallsBackToTheSessionIdForRequestId()
    {
        var transformer = new ZaiOutboundTransformer(new ZaiOutboundConfig
        {
            BaseUrl = "https://api.zai.com",
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
            SessionIdProvider = () => "sess-123456",
        });

        var body = Parse(transformer.TransformRequest(SimpleRequest("gpt-4")));

        Assert.Equal("sess-123456", body.GetProperty("request_id").GetString());
    }

    // Reference TestOutboundTransformer_TransformRequest_WithThinking: a
    // non-GLM model only understands thinking on/off and leaks no effort.
    [Fact]
    public void TransformRequest_NonGlmModelSendsThinkingOnly()
    {
        var request = SimpleRequest("gpt-4");
        request.ReasoningEffort = "high";

        var body = Parse(Transformer("https://api.zai.com").TransformRequest(request));

        Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    // Reference TestOutboundTransformer_TransformRequest_GLM53NoneMapsToLow.
    [Fact]
    public void TransformRequest_Glm53NoneMapsToLow()
    {
        var request = SimpleRequest("glm-5.3-flash");
        request.ReasoningEffort = "none";

        var body = Parse(Transformer("https://api.zai.com").TransformRequest(request));

        Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("low", body.GetProperty("reasoning_effort").GetString());
    }

    // Reference TestOutboundTransformer_TransformRequest_NonGLMStripsReasoningEffort.
    [Fact]
    public void TransformRequest_NonGlmModelStripsReasoningEffort()
    {
        var request = SimpleRequest("gpt-4");
        request.ReasoningEffort = "high";

        var body = Parse(Transformer("https://api.zai.com").TransformRequest(request));

        Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    // Reference TestOutboundTransformer_TransformRequest_ResponseFormat.
    [Theory]
    [InlineData("json_schema", "json_object")]
    [InlineData("json_object", "json_object")]
    [InlineData("text", "text")]
    public void TransformRequest_DowngradesJsonSchema(string type, string expectedType)
    {
        var request = SimpleRequest("gpt-4");
        request.ResponseFormat = new ResponseFormat
        {
            Type = type,
            JsonSchema = type == "json_schema"
                ? JsonElement.Parse("""{"type":"object","properties":{"name":{"type":"string"}}}""")
                    .Clone()
                : null,
        };

        var body = Parse(Transformer("https://api.zai.com/v4").TransformRequest(request));

        var format = body.GetProperty("response_format");
        Assert.Equal(expectedType, format.GetProperty("type").GetString());
        Assert.False(format.TryGetProperty("json_schema", out _));
    }

    // Reference TestOutboundTransformer_TransformRequest_URLOverrides.
    [Theory]
    [InlineData("https://open.bigmodel.cn/api/coding/paas/v4", "", "https://open.bigmodel.cn/api/coding/paas/v4/chat/completions")]
    [InlineData("https://open.bigmodel.cn/api/coding/paas", "", "https://open.bigmodel.cn/api/coding/paas/v4/chat/completions")]
    [InlineData("https://open.bigmodel.cn/api/coding/paas/v4", "/chat/completions", "https://open.bigmodel.cn/api/coding/paas/v4/chat/completions")]
    [InlineData("https://open.bigmodel.cn/api/coding/paas/v4/chat/completions##", "", "https://open.bigmodel.cn/api/coding/paas/v4/chat/completions")]
    public void TransformRequest_UrlOverrides(string baseUrl, string endpointPath, string expectedUrl)
    {
        var transformer = new ZaiOutboundTransformer(new ZaiOutboundConfig
        {
            BaseUrl = baseUrl,
            EndpointPath = endpointPath,
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
        });

        var request = transformer.TransformRequest(SimpleRequest("glm-5.3-flash"));

        Assert.Equal(expectedUrl, request.Url);
    }

    // Reference TestOutboundTransformer_TransformRequest_UserIDLength.
    [Fact]
    public void TransformRequest_UserIdLengthRules()
    {
        const string longValue = "{\"device_id\":\"334251bf6b147d22d006e2e9cb990169a3ba5e77fecb52a03473e32eb1afe62e\",\"account_uuid\":\"\",\"session_id\":\"db0eef37-75a9-4288-8098-0557997cd879\"}";
        Assert.Equal(150, longValue.EnumerateRunes().Count());

        var truncated = BodyWithUserId(longValue).GetProperty("user_id").GetString();
        Assert.Equal(128, truncated!.EnumerateRunes().Count());

        Assert.False(BodyWithUserId("abc").TryGetProperty("user_id", out _));

        Assert.Equal(
            "user-123456",
            BodyWithUserId("user-123456").GetProperty("user_id").GetString());
    }

    // Go slices []rune, so astral characters count once, not as UTF-16 pairs.
    [Fact]
    public void TransformRequest_UserIdTruncationCountsRunes()
    {
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 130));

        var truncated = BodyWithUserId(emoji).GetProperty("user_id").GetString();

        Assert.Equal(128, truncated!.EnumerateRunes().Count());
        Assert.Equal(256, truncated.Length);
    }

    // Reference TestOutboundTransformer_TransformRequest_StripsMetadata.
    [Fact]
    public void TransformRequest_StripsMetadataButKeepsUserId()
    {
        var request = SimpleRequest("glm-5.3-flash");
        request.Metadata = new Dictionary<string, string> { ["user_id"] = "user-123" };

        var body = Parse(Transformer("https://open.bigmodel.cn/api/coding/paas/v4")
            .TransformRequest(request));

        Assert.False(body.TryGetProperty("metadata", out _));
        Assert.Equal("user-123", body.GetProperty("user_id").GetString());
    }

    // Reference TestSupportsNativeReasoningEffort.
    [Theory]
    [InlineData("glm-4.5", false)]
    [InlineData("glm-4.7", false)]
    [InlineData("glm-4.7-flash", false)]
    [InlineData("glm-5.0", false)]
    [InlineData("glm-5.1", false)]
    [InlineData("glm-5.2", true)]
    [InlineData("glm-5.3", true)]
    [InlineData("glm-5.3-flash", true)]
    [InlineData("zai-org/glm-5.3:thinking", true)]
    [InlineData("GLM-5.3-FLASH", true)]
    [InlineData("gpt-4", false)]
    public void SupportsNativeReasoningEffortTable(string model, bool expected)
        => Assert.Equal(expected, ZaiOutboundTransformer.SupportsNativeReasoningEffort(model));

    // Reference TestIsAlwaysThinkingModel.
    [Theory]
    [InlineData("glm-5.3", true)]
    [InlineData("glm-5.3-flash", true)]
    [InlineData("zai-org/glm-5.3:thinking", true)]
    [InlineData("GLM-5.3-FLASH", true)]
    [InlineData("glm-5.2", false)]
    [InlineData("glm-4.7", false)]
    [InlineData("gpt-4", false)]
    public void IsAlwaysThinkingModelTable(string model, bool expected)
        => Assert.Equal(expected, ZaiOutboundTransformer.IsAlwaysThinkingModel(model));

    // Reference TestNormalizeGLM53Effort.
    [Theory]
    [InlineData("none", "low")]
    [InlineData("minimal", "low")]
    [InlineData("low", "low")]
    [InlineData("medium", "high")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "max")]
    [InlineData("max", "max")]
    [InlineData("unknown", "low")]
    public void NormalizeGlm53EffortTable(string effort, string expected)
        => Assert.Equal(expected, ZaiOutboundTransformer.NormalizeGlm53Effort(effort));

    // Reference TestResolveZaiThinking.
    [Theory]
    [InlineData("glm-5.3-flash", "none", "enabled", "low")]
    [InlineData("glm-5.3", "high", "enabled", "high")]
    [InlineData("glm-5.3", "xhigh", "enabled", "max")]
    [InlineData("glm-5.2", "none", "disabled", null)]
    [InlineData("glm-5.2", "minimal", "enabled", "low")]
    [InlineData("glm-5.2", "low", "enabled", "low")]
    [InlineData("glm-4.7", "none", "disabled", null)]
    [InlineData("glm-4.7", "high", "enabled", null)]
    [InlineData("glm-5.3", "", null, null)]
    public void ResolveZaiThinkingTable(
        string model,
        string effort,
        string? expectedType,
        string? expectedEffort)
    {
        var (thinking, resolvedEffort) = ZaiOutboundTransformer.ResolveZaiThinking(model, effort);

        if (expectedType is null)
        {
            Assert.Null(thinking);
        }
        else
        {
            Assert.NotNull(thinking);
            Assert.Equal(expectedType, thinking!.Type);
        }

        Assert.Equal(expectedEffort, resolvedEffort);
    }

    [Fact]
    public void TransformResponse_DelegatesChatToTheOpenAiOutbound()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = """
                {"id":"test-123","object":"chat.completion","created":1234567890,"model":"glm-4.7",
                 "choices":[{"index":0,"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}]}
                """u8.ToArray(),
            Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIChatCompletion },
        };

        var result = Transformer("https://api.zai.com").TransformResponse(response);

        Assert.Equal("test-123", result.Id);
        Assert.Equal("Hello!", Assert.Single(result.Choices).Message!.Content!.Content);
    }

    [Fact]
    public void TransformResponse_Errors()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => Transformer("https://api.zai.com").TransformResponse(null));
        Assert.Equal("http response is nil", nil.Message);

        var status = Assert.Throws<InvalidOperationException>(
            () => Transformer("https://api.zai.com").TransformResponse(new HttpResponseData
            {
                StatusCode = 429,
                Body = """{"error":"slow down"}"""u8.ToArray(),
            }));
        Assert.Equal("HTTP error 429", status.Message);

        var empty = Assert.Throws<InvalidOperationException>(
            () => Transformer("https://api.zai.com").TransformResponse(new HttpResponseData
            {
                StatusCode = 200,
            }));
        Assert.Equal("response body is empty", empty.Message);
    }

    [Fact]
    public void ConstructorValidation()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => new ZaiOutboundTransformer(null));
        Assert.Equal(
            "invalid Zai transformer configuration: config cannot be nil",
            nil.Message);

        var missingKey = Assert.Throws<InvalidOperationException>(
            () => new ZaiOutboundTransformer(new ZaiOutboundConfig
            {
                BaseUrl = "https://api.zai.com",
            }));
        Assert.StartsWith(
            "invalid Zai transformer configuration: ",
            missingKey.Message,
            StringComparison.Ordinal);
        Assert.Contains("API key provider is required", missingKey.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ZaiOutboundTransformer Transformer(string baseUrl)
        => new(new ZaiOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
        });

    private static JsonElement BodyWithUserId(string userId)
    {
        var request = SimpleRequest("glm-5.3-flash");
        request.Metadata = new Dictionary<string, string> { ["user_id"] = userId };

        var httpRequest = Transformer("https://open.bigmodel.cn/api/coding/paas/v4")
            .TransformRequest(request);

        return Parse(httpRequest);
    }

    private static JsonElement Parse(HttpRequestData request)
    {
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }

    private static LlmRequest SimpleRequest(string model) => new()
    {
        Model = model,
        Messages = [UserMessage()],
    };

    private static Message UserMessage()
        => new() { Role = "user", Content = MessageContent.FromText("Hello, world!") };
}
