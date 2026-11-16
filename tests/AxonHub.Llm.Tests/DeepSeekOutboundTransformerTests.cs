using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.DeepSeek;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the DeepSeek outbound transformer
/// (llm/transformer/deepseek/outbound.go and outbound_test.go).
/// </summary>
public sealed class DeepSeekOutboundTransformerTests
{
    // ------------------------------------------------------------------
    // Response format (reference TestOutboundTransformer_TransformRequest_ResponseFormat)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("json_schema", """{"type":"object","properties":{"name":{"type":"string"}}}""", "json_object")]
    [InlineData("json_object", null, "json_object")]
    [InlineData("text", null, "text")]
    public void TransformRequest_ResponseFormat(string type, string? jsonSchema, string expectedType)
    {
        var request = SimpleRequest();
        request.ResponseFormat = new ResponseFormat
        {
            Type = type,
            JsonSchema = jsonSchema is null ? null : Json(jsonSchema),
        };

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal("POST", httpRequest.Method);

        var body = Body(httpRequest);
        var responseFormat = body.GetProperty("response_format");
        Assert.Equal(expectedType, responseFormat.GetProperty("type").GetString());

        // DeepSeek only understands JSON mode, never a schema.
        Assert.False(responseFormat.TryGetProperty("json_schema", out _));
    }

    // ------------------------------------------------------------------
    // Thinking (reference TestOutboundTransformer_TransformRequest_Thinking)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("high", "enabled")]
    [InlineData("medium", "enabled")]
    [InlineData("none", "disabled")]
    [InlineData("", "enabled")]
    public void TransformRequest_Thinking(string reasoningEffort, string expectedThinking)
    {
        var request = SimpleRequest("deepseek-reasoner");
        request.ReasoningEffort = reasoningEffort;

        var body = Body(Transformer().TransformRequest(request));

        Assert.Equal(
            expectedThinking,
            body.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public void TransformRequest_ThinkingDisabledClearsReasoningEffort()
    {
        // DeepSeek only accepts high/low/medium/max/xhigh; "none" would be
        // rejected, so the reference drops the field and disables thinking.
        var request = SimpleRequest("deepseek-reasoner");
        request.ReasoningEffort = "none";

        var body = Body(Transformer().TransformRequest(request));

        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    // ------------------------------------------------------------------
    // URL (reference TestOutboundTransformer_TransformRequest_URL)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/v1/chat/completions")]
    public void TransformRequest_Url(string baseUrl, string expectedUrl)
    {
        var httpRequest = Transformer(baseUrl).TransformRequest(SimpleRequest());

        Assert.Equal(expectedUrl, httpRequest.Url);
    }

    // ------------------------------------------------------------------
    // reasoning_content (reference TestOutboundTransformer_TransformRequest_ReasoningContentFill)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequest_ReasoningContentFill()
    {
        var cases = new (string Name, string Effort, Message[] Messages, string?[] Expected)[]
        {
            (
                "thinking enabled fills empty reasoning_content",
                "high",
                [User("Hello"), Assistant("Hi", null)],
                [string.Empty]),
            (
                "thinking preserves existing reasoning_content",
                "high",
                [User("Hello"), Assistant("Hi", "Let me think...")],
                ["Let me think..."]),
            (
                "default thinking fills reasoning_content when effort is empty",
                "",
                [User("Hello"), Assistant("Hi", null)],
                [string.Empty]),
            (
                "thinking disabled does not fill reasoning_content",
                "none",
                [User("Hello"), Assistant("Hi", null)],
                [null]),
            (
                "multiple assistant messages all get filled",
                "medium",
                [
                    User("Hello"),
                    Assistant("Hi", null),
                    User("How are you?"),
                    Assistant("I'm fine", "thinking"),
                    User("Great"),
                    Assistant("Thanks", null),
                ],
                [string.Empty, "thinking", string.Empty]),
            (
                "non-assistant messages are not affected",
                "high",
                [
                    new Message { Role = "system", Content = MessageContent.FromText("You are helpful") },
                    User("Hello"),
                    Assistant("Hi", null),
                ],
                [string.Empty]),
        };

        foreach (var testCase in cases)
        {
            var request = new LlmRequest
            {
                Model = "deepseek-reasoner",
                ReasoningEffort = testCase.Effort,
                Messages = [.. testCase.Messages],
            };

            var body = Body(Transformer().TransformRequest(request));

            var assistantMessages = body.GetProperty("messages")
                .EnumerateArray()
                .Where(static message => message.GetProperty("role").GetString() == "assistant")
                .ToList();

            Assert.Equal(testCase.Expected.Length, assistantMessages.Count);

            for (var i = 0; i < testCase.Expected.Length; i++)
            {
                var message = assistantMessages[i];
                var hasReasoning = message.TryGetProperty("reasoning_content", out var reasoning);
                var actual = hasReasoning ? reasoning.GetString() : null;
                var detail = testCase.Name + ": assistant message " + i
                    + ", expected " + (testCase.Expected[i] ?? "<absent>")
                    + ", actual " + (hasReasoning ? actual ?? "<null>" : "<absent>");

                if (testCase.Expected[i] is null)
                {
                    Assert.False(hasReasoning, detail);
                }
                else
                {
                    Assert.True(actual == testCase.Expected[i], detail);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Completion dispatch (deepseek outbound.go:92-102)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequest_CompletionUsesTheBetaEndpoint()
    {
        var request = new LlmRequest
        {
            Model = "deepseek-chat",
            RequestType = RequestTypes.Completion,
            Completion = new CompletionRequest { Prompt = "Hello" },
        };

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal("https://api.deepseek.com/beta/completions", httpRequest.Url);
        Assert.Equal(RequestTypes.Completion, httpRequest.RequestType);
        Assert.Equal(ApiFormats.OpenAICompletion, httpRequest.ApiFormat);
        Assert.Equal("Hello", Body(httpRequest).GetProperty("prompt").GetString());
    }

    [Fact]
    public void TransformRequest_CompactIsRejected()
    {
        var request = SimpleRequest();
        request.RequestType = RequestTypes.Compact;

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
        Assert.Equal(
            "invalid request: compact is only supported by OpenAI Responses API",
            error.Message);
    }

    [Fact]
    public void TransformRequest_UnsupportedTypeIsRejected()
    {
        var request = SimpleRequest();
        request.RequestType = RequestTypes.Embedding;

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal(
            "invalid request: embedding is not supported",
            error.Message);
    }

    [Fact]
    public void TransformRequest_RequiresMessages()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(new LlmRequest { Model = "deepseek-chat" }));

        Assert.Equal("invalid request: messages are required", error.Message);
    }

    [Fact]
    public void TransformRequest_NilRequest()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(null));

        Assert.Equal("chat completion request is nil", error.Message);
    }

    // ------------------------------------------------------------------
    // Response dispatch (deepseek outbound.go:170-180)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformResponse_ChatRouteParsesChatCompletion()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"deepseek-chat","choices":[{"index":0,"message":{"role":"assistant","content":"Hi"},"finish_reason":"stop"}]}"""),
        };

        var result = Transformer().TransformResponse(response);

        Assert.Equal("chat.completion", result.Object);
        Assert.Single(result.Choices);
        Assert.Equal("Hi", result.Choices[0].Message!.Content!.Content);
    }

    [Fact]
    public void TransformResponse_CompletionRouteParsesCompletionResponse()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """{"id":"cmpl-1","object":"text_completion","created":1,"model":"deepseek-chat","choices":[{"index":0,"text":"Hi","finish_reason":"stop"}]}"""),
            Request = new HttpRequestData { RequestType = RequestTypes.Completion },
        };

        var result = Transformer().TransformResponse(response);

        Assert.Equal(RequestTypes.Completion, result.RequestType);
        Assert.NotNull(result.Completion);
        Assert.Single(result.Completion!.Choices);
        Assert.Equal("Hi", result.Completion.Choices[0].Text);
    }

    // ------------------------------------------------------------------
    // Credentials and construction
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequest_ExposesTheSelectedApiKey()
    {
        var transformer = Transformer();
        Assert.Null(transformer.SelectedApiKey);

        transformer.TransformRequest(SimpleRequest());

        Assert.Equal("test-api-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void ConstructorValidation()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => new DeepSeekOutboundTransformer(null!));
        Assert.Equal(
            "invalid DeepSeek transformer configuration: config cannot be nil",
            nil.Message);

        var missingKey = Assert.Throws<InvalidOperationException>(
            () => new DeepSeekOutboundTransformer(new DeepSeekOutboundConfig
            {
                BaseUrl = "https://api.deepseek.com",
            }));
        Assert.Contains(
            "API key provider is required",
            missingKey.Message,
            StringComparison.Ordinal);

        var missingUrl = Assert.Throws<InvalidOperationException>(
            () => new DeepSeekOutboundTransformer(new DeepSeekOutboundConfig
            {
                ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
            }));
        Assert.Contains("base URL is required", missingUrl.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static DeepSeekOutboundTransformer Transformer(
        string baseUrl = "https://api.deepseek.com/v1")
        => new(new DeepSeekOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
        });

    private static LlmRequest SimpleRequest(string model = "deepseek-chat")
        => new()
        {
            Model = model,
            Messages = [User("Hello")],
        };

    private static Message User(string text)
        => new() { Role = "user", Content = MessageContent.FromText(text) };

    private static Message Assistant(string text, string? reasoningContent)
        => new()
        {
            Role = "assistant",
            Content = MessageContent.FromText(text),
            ReasoningContent = reasoningContent,
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
