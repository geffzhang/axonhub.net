using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Moonshot;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Moonshot outbound (llm/transformer/moonshot and its
/// outbound_test.go): OpenAI chat wire format with a chat-only request path and
/// the json_schema-to-json_object downgrade.
/// </summary>
public sealed class MoonshotOutboundTransformerTests
{
    // Reference TestOutboundTransformer_TransformRequest_ResponseFormat.
    [Theory]
    [InlineData("json_schema", """{"type":"object","properties":{"name":{"type":"string"}}}""", "json_object")]
    [InlineData("json_object", null, "json_object")]
    [InlineData("text", null, "text")]
    public void TransformRequest_DowngradesJsonSchemaToJsonObject(
        string type,
        string? jsonSchema,
        string expectedType)
    {
        var request = SimpleRequest();
        request.ResponseFormat = new ResponseFormat
        {
            Type = type,
            JsonSchema = jsonSchema is null ? null : Json(jsonSchema),
        };

        var body = Body(Transformer().TransformRequest(request));
        var responseFormat = body.GetProperty("response_format");

        Assert.Equal(expectedType, responseFormat.GetProperty("type").GetString());
        Assert.False(responseFormat.TryGetProperty("json_schema", out _));
    }

    // Reference TestOutboundTransformer_TransformRequest_URL.
    [Theory]
    [InlineData("https://api.moonshot.cn/v1", "https://api.moonshot.cn/v1/chat/completions")]
    [InlineData("https://api.moonshot.cn", "https://api.moonshot.cn/v1/chat/completions")]
    [InlineData("https://api.moonshot.cn/v1/", "https://api.moonshot.cn/v1/chat/completions")]
    public void TransformRequest_NormalizesTheBaseUrl(string baseUrl, string expectedUrl)
    {
        Assert.Equal(expectedUrl, Transformer(baseUrl).TransformRequest(SimpleRequest()).Url);
    }

    // Reference TestOutboundTransformer_TransformRequest_Basic.
    [Fact]
    public void TransformRequest_BasicShape()
    {
        var transformer = Transformer();
        var httpRequest = transformer.TransformRequest(SimpleRequest());

        Assert.Equal("POST", httpRequest.Method);
        Assert.Equal("https://api.moonshot.cn/v1/chat/completions", httpRequest.Url);
        Assert.Equal("application/json", httpRequest.Headers!["Content-Type"][0]);
        Assert.Equal("application/json", httpRequest.Headers!["Accept"][0]);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("test-api-key", httpRequest.Auth!.ApiKey);
        Assert.Equal("test-api-key", transformer.SelectedApiKey);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, httpRequest.ApiFormat);

        var body = Body(httpRequest);
        Assert.Equal("moonshot-v1-8k", body.GetProperty("model").GetString());
        Assert.Single(body.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public void TransformRequest_SendsReasoningThroughTheContentField()
    {
        var request = SimpleRequest();
        request.Messages =
        [
            new Message
            {
                Role = "assistant",
                Content = MessageContent.FromText("Hi"),
                Reasoning = "trace",
            },
        ];

        var assistant = Body(Transformer().TransformRequest(request)).GetProperty("messages")[0];

        Assert.Equal("trace", assistant.GetProperty("reasoning_content").GetString());
        Assert.False(assistant.TryGetProperty("reasoning", out _));
    }

    // Reference TestOutboundTransformer_TransformRequest_Errors.
    [Fact]
    public void TransformRequest_RequiresMessages()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(new LlmRequest { Model = "moonshot-v1-8k" }));

        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
        Assert.Equal("invalid request: messages are required", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsUnsupportedTypes()
    {
        var embedding = SimpleRequest();
        embedding.RequestType = RequestTypes.Embedding;

        var unsupported = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(embedding));
        Assert.Equal(
            "invalid request: embedding is not supported",
            unsupported.Message);

        var compact = SimpleRequest();
        compact.RequestType = RequestTypes.Compact;

        var compactError = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(compact));
        Assert.Equal(
            "invalid request: compact is only supported by OpenAI Responses API",
            compactError.Message);
    }

    [Fact]
    public void TransformError_DelegatesToTheOpenAiOutbound()
    {
        // The reference embeds the OpenAI outbound, so the provider error
        // envelope is the OpenAI one.
        var response = Transformer().TransformError(
            new HttpClientError(
                "POST",
                "https://api.moonshot.cn/v1/chat/completions",
                400,
                "400 Bad Request",
                Encoding.UTF8.GetBytes(
                    """{"error":{"message":"bad request","type":"invalid_request_error"}}""")));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("bad request", response.Detail.Message);
        Assert.Equal("invalid_request_error", response.Detail.Type);
    }

    [Fact]
    public void ConstructorValidation()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => new MoonshotOutboundTransformer(null!));
        Assert.Equal(
            "invalid Moonshot transformer configuration: config cannot be nil",
            nil.Message);

        var missingKey = Assert.Throws<InvalidOperationException>(
            () => new MoonshotOutboundTransformer(new MoonshotOutboundConfig
            {
                BaseUrl = "https://api.moonshot.cn/v1",
            }));
        // The inner OpenAI validator throws ArgumentException; the port keeps
        // its parameter suffix, so only the Go-compatible prefix is pinned (C098).
        Assert.StartsWith(
            "invalid Moonshot transformer configuration: "
            + "invalid OpenAI transformer configuration: ",
            missingKey.Message,
            StringComparison.Ordinal);
        Assert.Contains("API key provider is required", missingKey.Message, StringComparison.Ordinal);

        var missingUrl = Assert.Throws<InvalidOperationException>(
            () => new MoonshotOutboundTransformer(new MoonshotOutboundConfig
            {
                ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
            }));
        Assert.StartsWith(
            "invalid Moonshot transformer configuration: "
            + "invalid OpenAI transformer configuration: ",
            missingUrl.Message,
            StringComparison.Ordinal);
        Assert.Contains("base URL is required", missingUrl.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static MoonshotOutboundTransformer Transformer(
        string baseUrl = "https://api.moonshot.cn/v1")
        => new(new MoonshotOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
        });

    private static LlmRequest SimpleRequest(string model = "moonshot-v1-8k")
        => new()
        {
            Model = model,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
