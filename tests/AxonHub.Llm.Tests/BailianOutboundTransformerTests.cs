using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Bailian;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Bailian outbound (llm/transformer/bailian and its
/// outbound_test.go): the reasoning_effort to enable_thinking mapping, the
/// consecutive assistant tool-call merge, and the embedded OpenAI outbound.
/// </summary>
public sealed class BailianOutboundTransformerTests
{
    private static readonly string[] HelloAfter = ["hello", "after"];

    // Reference TestBailianTransformRequest_ReasoningEffort.
    [Theory]
    [InlineData("none", null, null)]
    [InlineData(null, null, null)]
    [InlineData("minimal", null, null)]
    [InlineData("low", null, null)]
    [InlineData("medium", null, null)]
    [InlineData("high", null, null)]
    [InlineData("xhigh", null, null)]
    [InlineData("provider-specific", null, null)]
    [InlineData("none", ApiFormats.OpenAIChatCompletion, """{"enable_thinking":true}""")]
    [InlineData("high", ApiFormats.OpenAIChatCompletion, """{"enable_thinking":false}""")]
    [InlineData(null, ApiFormats.OpenAIChatCompletion, """{"enable_thinking":false}""")]
    [InlineData("none", ApiFormats.AnthropicMessage, null)]
    public void TransformRequest_MapsReasoningEffortToEnableThinking(
        string? effort,
        string? apiFormat,
        string? rawBody)
    {
        var request = new LlmRequest
        {
            Model = "qwen-max",
            ReasoningEffort = effort,
            ApiFormat = apiFormat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
            RawRequest = rawBody is null
                ? null
                : new HttpRequestData { Body = Encoding.UTF8.GetBytes(rawBody) },
        };
        var original = JsonSerializer.Serialize(request, LlmJson.Options);

        var body = Body(Transformer().TransformRequest(request));

        if (effort == "none")
        {
            Assert.False(body.GetProperty("enable_thinking").GetBoolean());
            Assert.False(body.TryGetProperty("reasoning_effort", out _));
        }
        else
        {
            Assert.False(body.TryGetProperty("enable_thinking", out _));

            if (effort is null)
            {
                Assert.False(body.TryGetProperty("reasoning_effort", out _));
            }
            else
            {
                Assert.Equal(effort, body.GetProperty("reasoning_effort").GetString());
            }
        }

        // The caller's request is never mutated (Go copies before adjusting).
        Assert.Equal(original, JsonSerializer.Serialize(request, LlmJson.Options));
    }

    // Reference TestBailianTransformRequest_OpenAIInboundReasoningNone.
    [Fact]
    public void TransformRequest_DisablesThinkingForAnInboundReasoningNone()
    {
        var inbound = new HttpRequestData
        {
            Method = "POST",
            Url = "https://example.com/v1/chat/completions",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(
                """{"model":"qwen-max","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"none","enable_thinking":true}"""),
        };
        var request = new OpenAiInboundTransformer().TransformRequest(inbound);
        Assert.Equal("none", request.ReasoningEffort);

        var body = Body(Transformer().TransformRequest(request));

        Assert.False(body.GetProperty("enable_thinking").GetBoolean());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
        Assert.Equal("none", request.ReasoningEffort);
    }

    // Reference TestBailianTransformRequest_MergeConsecutiveToolCalls.
    [Fact]
    public void TransformRequest_MergesConsecutiveAssistantToolCalls()
    {
        var request = TwoConsecutiveToolCallMessages();

        var httpsRequest = Transformer().TransformRequest(request);
        var messages = Body(httpsRequest).GetProperty("messages");

        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());

        var toolCalls = messages[1].GetProperty("tool_calls");
        Assert.Equal(2, toolCalls.GetArrayLength());
        Assert.Equal("call_1", toolCalls[0].GetProperty("id").GetString());
        Assert.Equal("call_2", toolCalls[1].GetProperty("id").GetString());

        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call_2", messages[3].GetProperty("tool_call_id").GetString());

        // Go copies the pending message; the caller's request keeps both
        // original assistant messages.
        Assert.Equal(5, request.Messages.Count);
        Assert.Single(request.Messages[1].ToolCalls!);
        Assert.Single(request.Messages[2].ToolCalls!);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("no-tool-calls")]
    [InlineData("tool-call-id")]
    [InlineData("name")]
    [InlineData("refusal")]
    [InlineData("message-index")]
    [InlineData("tool-call-name")]
    [InlineData("tool-call-is-error")]
    [InlineData("reasoning-content")]
    [InlineData("reasoning-signature")]
    [InlineData("redacted-reasoning-content")]
    [InlineData("cache-control")]
    [InlineData("text-content")]
    [InlineData("multiple-content")]
    public void MergeConsecutiveToolCallMessages_KeepsUnmergeableMessages(string blocker)
    {
        var request = TwoConsecutiveToolCallMessages();
        BlockMerge(request.Messages[2], blocker);

        var merged = BailianOutboundTransformer.MergeConsecutiveToolCallMessages(request);

        Assert.Same(request, merged);
        Assert.Equal(5, merged!.Messages.Count);
    }

    [Fact]
    public void MergeConsecutiveToolCallMessages_MergesEachRunSeparately()
    {
        var request = TwoConsecutiveToolCallMessages();
        request.Messages.Add(new Message { Role = "user", Content = MessageContent.FromText("again") });
        request.Messages.Add(ToolCallMessage("call_3"));
        request.Messages.Add(ToolCallMessage("call_4"));

        var merged = BailianOutboundTransformer.MergeConsecutiveToolCallMessages(request);

        Assert.NotNull(merged);
        Assert.Equal(6, merged!.Messages.Count);
        Assert.Equal(2, merged.Messages[1].ToolCalls!.Count);
        Assert.Equal(2, merged.Messages[5].ToolCalls!.Count);
    }

    [Fact]
    public void TransformRequest_BasicWireShape()
    {
        var transformer = Transformer("https://dashscope.aliyuncs.com/compatible-mode/v1");

        var httpRequest = transformer.TransformRequest(new LlmRequest
        {
            Model = "qwen-max",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
        });

        Assert.Equal("POST", httpRequest.Method);
        Assert.Equal(
            "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
            httpRequest.Url);
        Assert.Equal("application/json", httpRequest.Headers!["Content-Type"][0]);
        Assert.Equal("application/json", httpRequest.Headers!["Accept"][0]);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("test-key", httpRequest.Auth.ApiKey);
        Assert.Equal("test-key", transformer.SelectedApiKey);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, httpRequest.ApiFormat);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);
    }

    [Fact]
    public void TransformResponse_DelegatesToTheOpenAiOutbound()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """{"id":"chat-1","object":"chat.completion","created":1,"model":"qwen-max","choices":[{"index":0,"message":{"role":"assistant","content":"Hi"},"finish_reason":"stop"}]}"""),
        };

        var result = Transformer().TransformResponse(response);

        Assert.Equal("chat-1", result.Id);
        Assert.Equal("Hi", Assert.Single(result.Choices).Message!.Content!.Content);
    }

    [Fact]
    public void TransformError_DelegatesToTheOpenAiOutbound()
    {
        var response = Transformer().TransformError(
            new HttpClientError(
                "POST",
                "https://example.com/v1/chat/completions",
                400,
                "400 Bad Request",
                Encoding.UTF8.GetBytes(
                    """{"error":{"message":"bad request","type":"invalid_request_error"}}""")));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("bad request", response.Detail.Message);
        Assert.Equal("invalid_request_error", response.Detail.Type);
    }

    [Fact]
    public void TransformStream_AppliesTheBailianFilter()
    {
        var transformer = Transformer();
        var events = new SliceStream<StreamEvent>(
        [
            Sse("""{"id":"resp_1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":"hello"}}]}"""),
            Sse("""{"id":"resp_1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"type":"function","function":{"name":"list_dir","arguments":"{\"path\":\"/\"}"}}]}}]}"""),
            Sse("""{"id":"resp_1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"after"}}]}"""),
            Sse("""{"id":"resp_1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""),
            Sse("[DONE]"),
        ]);

        using var stream = transformer.TransformStream(null, events);
        var output = GoStreams.All(stream, out var error);

        Assert.Null(error);
        Assert.Same(LlmResponse.DoneResponse, output[^1]);

        var texts = new List<string>();
        foreach (var response in output)
        {
            foreach (var choice in response.Choices)
            {
                if (choice.Delta?.Content?.Content is { Length: > 0 } text)
                {
                    texts.Add(text);
                }
            }
        }

        Assert.Equal(HelloAfter, texts);
    }

    [Fact]
    public void ConstructorValidation()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => new BailianOutboundTransformer(null));
        Assert.Equal(
            "invalid Bailian transformer configuration: config is nil",
            nil.Message);

        var missingKey = Assert.Throws<InvalidOperationException>(
            () => new BailianOutboundTransformer(new BailianOutboundConfig
            {
                BaseUrl = "https://example.com",
            }));
        // The inner OpenAI validator throws ArgumentException; the port keeps
        // its parameter suffix, so only the Go-compatible prefix is pinned (C103).
        Assert.StartsWith(
            "invalid Bailian transformer configuration: invalid OpenAI transformer configuration: ",
            missingKey.Message,
            StringComparison.Ordinal);
        Assert.Contains("API key provider is required", missingKey.Message, StringComparison.Ordinal);

        var missingUrl = Assert.Throws<InvalidOperationException>(
            () => new BailianOutboundTransformer(new BailianOutboundConfig
            {
                ApiKeyProvider = new StaticApiKeyProvider("test-key"),
            }));
        Assert.StartsWith(
            "invalid Bailian transformer configuration: invalid OpenAI transformer configuration: ",
            missingUrl.Message,
            StringComparison.Ordinal);
        Assert.Contains("base URL is required", missingUrl.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static BailianOutboundTransformer Transformer(string baseUrl = "https://example.com")
        => new(new BailianOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

    private static LlmRequest TwoConsecutiveToolCallMessages()
        => new()
        {
            Model = "qwen-max",
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("hi") },
                ToolCallMessage("call_1"),
                ToolCallMessage("call_2"),
                new Message
                {
                    Role = "tool",
                    ToolCallId = "call_1",
                    Content = MessageContent.FromText("out1"),
                },
                new Message
                {
                    Role = "tool",
                    ToolCallId = "call_2",
                    Content = MessageContent.FromText("out2"),
                },
            ],
        };

    private static Message ToolCallMessage(string id)
        => new()
        {
            Role = "assistant",
            ToolCalls =
            [
                new ToolCall
                {
                    Id = id,
                    Type = "function",
                    Function = new FunctionCall { Name = "tool_" + id, Arguments = "{}" },
                },
            ],
        };

    private static void BlockMerge(Message message, string blocker)
    {
        switch (blocker)
        {
            case "user":
                message.Role = "user";
                break;
            case "no-tool-calls":
                message.ToolCalls = null;
                break;
            case "tool-call-id":
                message.ToolCallId = "call_x";
                break;
            case "name":
                message.Name = "assistant";
                break;
            case "refusal":
                message.Refusal = "no";
                break;
            case "message-index":
                message.MessageIndex = 1;
                break;
            case "tool-call-name":
                message.ToolCallName = "tool_x";
                break;
            case "tool-call-is-error":
                message.ToolCallIsError = true;
                break;
            case "reasoning-content":
                message.ReasoningContent = "thinking";
                break;
            case "reasoning-signature":
                message.ReasoningSignature = "sig";
                break;
            case "redacted-reasoning-content":
                message.RedactedReasoningContent = "redacted";
                break;
            case "cache-control":
                message.CacheControl = new CacheControl { Type = "ephemeral" };
                break;
            case "text-content":
                message.Content = MessageContent.FromText("not empty");
                break;
            case "multiple-content":
                message.Content = MessageContent.FromParts(
                    [new MessageContentPart { Type = "image_url", ImageUrl = new ImageUrl { Url = "https://img" } }]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(blocker));
        }
    }

    private static StreamEvent Sse(string data)
        => new() { Data = Encoding.UTF8.GetBytes(data) };

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
