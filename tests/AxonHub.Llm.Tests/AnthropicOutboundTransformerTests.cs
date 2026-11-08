using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Anthropic outbound transformer
/// (llm/transformer/anthropic/outbound.go, outbound_convert.go,
/// outbound_test.go, config_test.go).
/// </summary>
public sealed class AnthropicOutboundTransformerTests
{
    // ------------------------------------------------------------------
    // Request shaping
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequest_BuildsDirectAnthropicRequest()
    {
        var transformer = Direct();
        var result = transformer.TransformRequest(SimpleRequest());

        Assert.Equal("POST", result.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", result.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(result.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(result.Headers, "Accept"));
        Assert.Equal("2023-06-01", HttpClientUtils.HeaderGet(result.Headers, "Anthropic-Version"));
        Assert.Equal(ApiFormats.AnthropicMessage, result.ApiFormat);

        Assert.NotNull(result.Auth);
        Assert.Equal(AuthTypes.ApiKey, result.Auth!.Type);
        Assert.Equal("test-api-key", result.Auth.ApiKey);
        Assert.Equal("X-API-Key", result.Auth.HeaderKey);

        var body = Body(result);
        Assert.Equal("claude-3-sonnet-20240229", body.GetProperty("model").GetString());
        Assert.Equal(1024L, body.GetProperty("max_tokens").GetInt64());

        var message = body.GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("Hello, Claude!", message.GetProperty("content").GetString());

        // Go omitempty: absent stream/tools/system stay off the wire.
        Assert.False(body.TryGetProperty("stream", out _));
        Assert.False(body.TryGetProperty("tools", out _));
        Assert.False(body.TryGetProperty("system", out _));
    }

    [Fact]
    public void TransformRequest_AccountIdentityIsNotSentForDirect()
    {
        var result = Direct().TransformRequest(SimpleRequest());

        // The reference asserts hreq.Metadata stays nil; the unified request
        // metadata must not become transport metadata.
        Assert.Null(result.Metadata);
    }

    [Fact]
    public void TransformRequest_ConvertsSystemPromptsToTopLevelSystem()
    {
        var request = SimpleRequest("Hello!");
        request.Messages.Insert(0, new Message
        {
            Role = "system",
            Content = MessageContent.FromText("You are a helpful assistant."),
        });

        var body = Body(Direct().TransformRequest(request));

        Assert.Equal("You are a helpful assistant.", body.GetProperty("system").GetString());
        Assert.Equal(1, body.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public void TransformRequest_DefaultMaxTokens()
    {
        var cases = new (long? MaxTokens, long? MaxCompletionTokens, long? DefaultMaxTokens, long Expected)[]
        {
            (1024, null, null, 1024),
            (null, 2048, null, 2048),
            (512, 2048, null, 512),
            (null, null, 131072, 131072),
            (1024, null, 131072, 1024),
            (null, null, 0, 8192),
            (null, null, null, 8192),
        };

        foreach (var testCase in cases)
        {
            var request = new LlmRequest
            {
                Model = "claude-3-sonnet-20240229",
                MaxTokens = testCase.MaxTokens,
                MaxCompletionTokens = testCase.MaxCompletionTokens,
                TransformOptions = testCase.DefaultMaxTokens is null
                    ? null
                    : new TransformOptions { DefaultMaxTokens = testCase.DefaultMaxTokens },
            };

            Assert.Equal(testCase.Expected, AnthropicOutboundRequest.ResolveMaxTokens(request));
        }
    }

    [Fact]
    public void TransformRequest_UsesModelCardDefaultMaxTokens()
    {
        var request = new LlmRequest
        {
            Model = "glm-5.3",
            Messages = [UserMessage("Hello")],
            TransformOptions = new TransformOptions { DefaultMaxTokens = 131072 },
        };

        var body = Body(Direct().TransformRequest(request));

        Assert.Equal(131072L, body.GetProperty("max_tokens").GetInt64());
    }

    [Fact]
    public void TransformRequest_EndpointPathOverridesDefaultPath()
    {
        var transformer = new AnthropicOutboundTransformer(new AnthropicOutboundConfig
        {
            Type = AnthropicPlatformType.Direct,
            BaseUrl = "https://example.com/anthropic",
            EndpointPath = "/v1/messages",
        });

        Assert.Equal("https://example.com/anthropic/v1/messages", transformer.BuildFullRequestUrl(SimpleRequest()));
    }

    [Fact]
    public void TransformRequest_DeepSeekReasoningEffortUsesOutputConfig()
    {
        var transformer = new AnthropicOutboundTransformer(new AnthropicOutboundConfig
        {
            Type = AnthropicPlatformType.DeepSeek,
            BaseUrl = "https://api.deepseek.com",
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
        });

        var request = SimpleRequest("Hello");
        request.Model = "deepseek-chat";
        request.ReasoningEffort = "max";

        var body = Body(transformer.TransformRequest(request));

        Assert.Equal("max", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(body.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void TransformRequest_OllamaBearerAuth()
    {
        var transformer = new AnthropicOutboundTransformer(new AnthropicOutboundConfig
        {
            Type = AnthropicPlatformType.Ollama,
            BaseUrl = "http://localhost:11434",
            ApiKeyProvider = new StaticApiKeyProvider("ollama-secret-key"),
        });

        var result = transformer.TransformRequest(SimpleRequest("Hello"));

        Assert.Equal("http://localhost:11434/v1/messages", result.Url);
        Assert.Equal("2023-06-01", HttpClientUtils.HeaderGet(result.Headers, "Anthropic-Version"));
        Assert.NotNull(result.Auth);
        Assert.Equal(AuthTypes.Bearer, result.Auth!.Type);
        Assert.Equal("ollama-secret-key", result.Auth.ApiKey);
        Assert.Null(result.Auth.HeaderKey);
    }

    [Fact]
    public void TransformRequest_OllamaNoAuthWhenKeyAbsent()
    {
        var transformer = new AnthropicOutboundTransformer(new AnthropicOutboundConfig
        {
            Type = AnthropicPlatformType.Ollama,
            BaseUrl = "http://localhost:11434",
        });

        var result = transformer.TransformRequest(SimpleRequest("Hello"));

        Assert.Null(result.Auth);
    }

    [Fact]
    public void CreateWithConfig_returns_the_plain_outbound_for_direct()
    {
        var transformer = AnthropicOutboundTransformer.CreateWithConfig(new AnthropicOutboundConfig
        {
            Type = AnthropicPlatformType.Direct,
            BaseUrl = "https://api.anthropic.com",
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
        });

        var plain = Assert.IsType<AnthropicOutboundTransformer>(transformer);
        Assert.Equal("https://api.anthropic.com/v1", plain.Config.BaseUrl);
    }

    // ------------------------------------------------------------------
    // Validation (reference TestOutboundTransformer_ErrorHandling)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequest_ValidationErrors()
    {
        var transformer = Direct();

        var nil = Assert.Throws<InvalidOperationException>(() => transformer.TransformRequest(null));
        Assert.Equal("chat completion request is nil", nil.Message);

        var missingModel = SimpleRequest();
        missingModel.Model = string.Empty;
        var modelError = Assert.Throws<InvalidOperationException>(
            () => transformer.TransformRequest(missingModel));
        Assert.Equal("model is required", modelError.Message);

        var noMessages = SimpleRequest();
        noMessages.Messages.Clear();
        var messagesError = Assert.Throws<TransformerException>(() => transformer.TransformRequest(noMessages));
        Assert.Equal(TransformerErrorKind.InvalidRequest, messagesError.Kind);
        Assert.Equal("invalid request: messages are required", messagesError.Message);

        foreach (var maxTokens in new long?[] { 0, -1 })
        {
            var invalidMaxTokens = SimpleRequest();
            invalidMaxTokens.MaxTokens = maxTokens;
            var maxTokensError = Assert.Throws<TransformerException>(
                () => transformer.TransformRequest(invalidMaxTokens));
            Assert.Equal("invalid request: max_tokens must be positive", maxTokensError.Message);
        }

        var compact = SimpleRequest();
        compact.RequestType = RequestTypes.Compact;
        var compactError = Assert.Throws<TransformerException>(() => transformer.TransformRequest(compact));
        Assert.Equal(
            "invalid request: compact is only supported by OpenAI Responses API",
            compactError.Message);

        var unsupported = SimpleRequest();
        unsupported.RequestType = "image_generation";
        var unsupportedError = Assert.Throws<TransformerException>(
            () => transformer.TransformRequest(unsupported));
        Assert.Equal("invalid request: image_generation is not supported", unsupportedError.Message);

        var audio = SimpleRequest();
        audio.Messages =
        [
            new Message
            {
                Role = "user",
                Content = MessageContent.FromParts(
                [
                    new MessageContentPart { Type = "input_audio" },
                ]),
            },
        ];
        var audioError = Assert.Throws<TransformerException>(() => transformer.TransformRequest(audio));
        Assert.Equal(
            "invalid request: input_audio content parts are not supported by the Anthropic Messages API",
            audioError.Message);
    }

    [Fact]
    public void TransformRequest_RejectsSystemOnlyConversation()
    {
        var request = new LlmRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1024,
            Messages =
            [
                new Message
                {
                    Role = "system",
                    Content = MessageContent.FromText("You are a helpful assistant."),
                },
            ],
        };

        var exception = Assert.Throws<TransformerException>(() => Direct().TransformRequest(request));

        Assert.Equal(TransformerErrorKind.InvalidRequest, exception.Kind);
        Assert.Equal(
            "invalid request: messages must contain at least one user or assistant message; "
            + "system and developer messages are converted to the system prompt",
            exception.Message);
    }

    // ------------------------------------------------------------------
    // Tools (reference TestOutboundTransformer_ToolUse)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequest_ConvertsFunctionToolsAndFiltersOthers()
    {
        var request = SimpleRequest("Use any tool available");
        request.Tools =
        [
            new Tool
            {
                Type = ToolTypes.Function,
                Function = new Function
                {
                    Name = "valid_function",
                    Description = "A valid function",
                    Parameters = Json("""{"type": "object"}"""),
                },
            },
            new Tool
            {
                Type = "code_interpreter",
                Function = new Function
                {
                    Name = "invalid_tool",
                    Description = "This should not be included",
                    Parameters = Json("""{"type": "object"}"""),
                },
            },
        ];

        var body = Body(Direct().TransformRequest(request));

        var tools = body.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("valid_function", tools[0].GetProperty("name").GetString());
        Assert.Equal("A valid function", tools[0].GetProperty("description").GetString());
        Assert.Equal("object", tools[0].GetProperty("input_schema").GetProperty("type").GetString());
    }

    [Fact]
    public void TransformRequest_EmptyToolsArrayIsOmitted()
    {
        var request = SimpleRequest("Hello");
        request.Tools = [];

        var body = Body(Direct().TransformRequest(request));

        Assert.False(body.TryGetProperty("tools", out _));
    }

    [Fact]
    public void TransformRequest_NullToolParametersBecomeEmptyObjectSchema()
    {
        var request = SimpleRequest("Hello");
        request.Tools =
        [
            new Tool
            {
                Type = ToolTypes.Function,
                Function = new Function { Name = "lookup" },
            },
        ];

        var body = Body(Direct().TransformRequest(request));

        var schema = body.GetProperty("tools")[0].GetProperty("input_schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public void TransformRequest_FunctionToolPreservesStrict()
    {
        var request = SimpleRequest("Use the tool");
        request.Tools =
        [
            new Tool
            {
                Type = ToolTypes.Function,
                Function = new Function
                {
                    Name = "get_weather",
                    Description = "Get the weather",
                    Parameters = Json("""{"type":"object","properties":{}}"""),
                    Strict = true,
                },
            },
        ];

        var body = Body(Direct().TransformRequest(request));

        Assert.True(body.GetProperty("tools")[0].GetProperty("strict").GetBoolean());
    }

    [Fact]
    public void TransformRequest_WebSearchToolUsesNativeTypeAndBetaHeader()
    {
        var request = SimpleRequest("Search the web for latest AI news");
        request.Tools = [new Tool { Type = ToolTypes.WebSearch }];

        var result = Direct().TransformRequest(request);
        var body = Body(result);

        var tool = body.GetProperty("tools")[0];
        Assert.Equal("web_search", tool.GetProperty("name").GetString());
        Assert.Equal("web_search_20250305", tool.GetProperty("type").GetString());
        Assert.False(tool.TryGetProperty("description", out _));
        Assert.False(tool.TryGetProperty("input_schema", out _));

        Assert.Equal("web-search-2025-03-05", HttpClientUtils.HeaderGet(result.Headers, "Anthropic-Beta"));
    }

    [Fact]
    public void TransformRequest_FunctionToolDoesNotSetBetaHeader()
    {
        var request = SimpleRequest("Use the calculator");
        request.Tools =
        [
            new Tool
            {
                Type = ToolTypes.Function,
                Function = new Function { Name = "calculator", Parameters = Json("""{"type":"object"}""") },
            },
        ];

        var result = Direct().TransformRequest(request);

        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(result.Headers, "Anthropic-Beta"));
    }

    [Fact]
    public void TransformRequest_ConvertsToolChoice()
    {
        var required = SimpleRequest("Use the calculator");
        required.Tools = [CalculatorTool()];
        required.ToolChoice = new ToolChoice { ToolChoiceMode = "required" };

        var requiredBody = Body(Direct().TransformRequest(required));
        Assert.Equal("any", requiredBody.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.False(requiredBody.GetProperty("tool_choice").TryGetProperty("name", out _));

        var named = SimpleRequest("Use the calculator");
        named.Tools = [CalculatorTool()];
        named.ToolChoice = new ToolChoice
        {
            NamedToolChoice = new NamedToolChoice
            {
                Type = "function",
                Function = new ToolFunction { Name = "calculator" },
            },
        };

        var namedBody = Body(Direct().TransformRequest(named));
        Assert.Equal("tool", namedBody.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal("calculator", namedBody.GetProperty("tool_choice").GetProperty("name").GetString());
    }

    [Fact]
    public void TransformRequest_ParallelToolCalls()
    {
        var disabled = SimpleRequest("Use the calculator");
        disabled.Tools = [CalculatorTool()];
        disabled.ParallelToolCalls = false;

        var disabledBody = Body(Direct().TransformRequest(disabled));
        Assert.Equal("auto", disabledBody.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.True(disabledBody.GetProperty("tool_choice").GetProperty("disable_parallel_tool_use").GetBoolean());

        var enabled = SimpleRequest("Use the calculator");
        enabled.Tools = [CalculatorTool()];
        enabled.ParallelToolCalls = true;
        enabled.ToolChoice = new ToolChoice
        {
            NamedToolChoice = new NamedToolChoice
            {
                Type = "function",
                Function = new ToolFunction { Name = "calculator" },
            },
        };

        var enabledBody = Body(Direct().TransformRequest(enabled));
        Assert.False(enabledBody.GetProperty("tool_choice").GetProperty("disable_parallel_tool_use").GetBoolean());
    }

    [Fact]
    public void TransformRequest_RepairsToolArguments()
    {
        var reparable = new LlmRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1024,
            Messages =
            [
                new Message
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Id = "call_repair",
                            Type = "function",
                            Function = new FunctionCall
                            {
                                Name = "test_func",
                                Arguments = """{"invalid": json}""",
                            },
                        },
                    ],
                },
            ],
        };

        var repaired = ToolUseBlocks(Body(Direct().TransformRequest(reparable)));
        Assert.Single(repaired);
        Assert.Equal("call_repair", repaired[0].GetProperty("id").GetString());
        Assert.Equal("json", repaired[0].GetProperty("input").GetProperty("invalid").GetString());

        var empty = new LlmRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1024,
            Messages =
            [
                new Message
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Id = "call_empty",
                            Type = "function",
                            Function = new FunctionCall { Name = "test_func" },
                        },
                    ],
                },
            ],
        };

        var emptyBlocks = ToolUseBlocks(Body(Direct().TransformRequest(empty)));
        Assert.Single(emptyBlocks);
        Assert.Empty(emptyBlocks[0].GetProperty("input").EnumerateObject());
    }

    // ------------------------------------------------------------------
    // Responses (reference TestOutboundTransformer_TransformResponse)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformResponse_ConvertsMessageToChatCompletion()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """
                {
                    "id": "msg_123",
                    "type": "message",
                    "role": "assistant",
                    "content": [
                        {
                            "type": "text",
                            "text": "Hello! How can I help you?"
                        }
                    ],
                    "model": "claude-3-sonnet-20240229",
                    "stop_reason": "end_turn",
                    "usage": {
                        "input_tokens": 10,
                        "output_tokens": 20
                    }
                }
                """),
        };

        var result = Direct().TransformResponse(response);

        Assert.Equal("chat.completion", result.Object);
        Assert.Equal("msg_123", result.Id);
        Assert.Equal("claude-3-sonnet-20240229", result.Model);
        Assert.Single(result.Choices);

        var message = result.Choices[0].Message;
        Assert.NotNull(message);
        Assert.Equal("assistant", message!.Role);
        Assert.Equal("Hello! How can I help you?", message.Content?.Content);
        Assert.Equal("stop", result.Choices[0].FinishReason);

        Assert.NotNull(result.Usage);
        Assert.Equal(10, result.Usage!.PromptTokens);
        Assert.Equal(20, result.Usage.CompletionTokens);
        Assert.Equal(30, result.Usage.TotalTokens);
    }

    [Fact]
    public void TransformResponse_ConcatenatesMultipleTextBlocks()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """
                {
                    "id": "msg_456",
                    "type": "message",
                    "role": "assistant",
                    "content": [
                        {"type": "text", "text": "I can see"},
                        {"type": "text", "text": " an image."}
                    ],
                    "model": "claude-3-sonnet-20240229",
                    "stop_reason": "end_turn"
                }
                """),
        };

        var result = Direct().TransformResponse(response);

        Assert.Equal("chat.completion", result.Object);
        Assert.Equal("I can see an image.", result.Choices[0].Message?.Content?.Content);
    }

    [Fact]
    public void TransformResponse_NullCitationElementIsIgnored()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """
                {
                    "id": "msg_789",
                    "type": "message",
                    "role": "assistant",
                    "content": [
                        {
                            "type": "text",
                            "text": "cited",
                            "citations": [null, {"type": "char_location"}]
                        }
                    ],
                    "model": "claude-3-sonnet-20240229",
                    "stop_reason": "end_turn"
                }
                """),
        };

        var result = Direct().TransformResponse(response);

        var annotation = Assert.Single(result.Choices[0].Message!.Annotations!);
        Assert.Equal("char_location", annotation.Type);
        Assert.Null(annotation.UrlCitation);
    }

    [Fact]
    public void TransformResponse_Errors()
    {
        var transformer = Direct();

        var nil = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(null));
        Assert.Equal("http response is nil", nil.Message);

        var httpError = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            new HttpResponseData
            {
                StatusCode = 400,
                Body = Encoding.UTF8.GetBytes("""{"error": {"message": "Bad request"}}"""),
            }));
        Assert.Equal("HTTP error 400", httpError.Message);

        var empty = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            new HttpResponseData { StatusCode = 200, Body = [] }));
        Assert.Equal("response body is empty", empty.Message);

        var invalidJson = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes("invalid json"),
            }));
        Assert.Contains("failed to unmarshal anthropic response", invalidJson.Message, StringComparison.Ordinal);

        var malformed = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes("""{"id": 123, "type": "message"}"""),
            }));
        Assert.Contains("failed to unmarshal anthropic response", malformed.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Error envelope (reference TestOutboundTransformer_TransformError)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformError_NilErrorUsesInternalServerErrorShape()
    {
        var error = Direct().TransformError(null);

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("api_error", error.Detail.Type);
        Assert.Equal("Request failed.", error.Detail.Message);
        Assert.Null(error.Detail.Code);
    }

    [Fact]
    public void TransformError_ParsesNestedErrorEnvelope()
    {
        var error = Direct().TransformError(new HttpClientError(
            "POST",
            "https://api.anthropic.com/v1/messages",
            400,
            "400 Bad Request",
            Encoding.UTF8.GetBytes(
                """{"type":"error","error":{"type":"invalid_request_error","message":"bad"},"request_id":"req_1"}""")));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("api_error", error.Detail.Type);
        Assert.Equal("bad", error.Detail.Message);
        Assert.Equal("req_1", error.Detail.RequestId);
    }

    [Fact]
    public void TransformError_FallsBackToBodyTextWhenEnvelopeHasNoNestedMessage()
    {
        // The top-level "message" is not a field of the AnthropicError struct,
        // so the reference falls back to the whole body text.
        var bodyText = """{"type": "api_error", "message": "bad request", "request_id": "req_123"}""";

        var error = Direct().TransformError(new HttpClientError(
            "POST",
            "https://api.anthropic.com/v1/messages",
            400,
            "400 Bad Request",
            Encoding.UTF8.GetBytes(bodyText)));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("api_error", error.Detail.Type);
        Assert.Equal(bodyText, error.Detail.Message);

        var plain = Direct().TransformError(new HttpClientError(
            "POST",
            "https://api.anthropic.com/v1/messages",
            500,
            "500 Internal Server Error",
            Encoding.UTF8.GetBytes("internal server error")));

        Assert.Equal("internal server error", plain.Detail.Message);
        Assert.Equal("Internal Server Error", plain.Detail.Code);
    }

    [Fact]
    public void TransformError_EmptyBodyUsesStatusText()
    {
        var error = Direct().TransformError(new HttpClientError(
            "POST",
            "https://api.anthropic.com/v1/messages",
            502,
            "502 Bad Gateway"));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("Bad Gateway", error.Detail.Message);
        Assert.Equal("Bad Gateway", error.Detail.Code);
        Assert.Equal("api_error", error.Detail.Type);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AnthropicOutboundTransformer Direct()
        => AnthropicOutboundTransformer.Create("https://api.anthropic.com", "test-api-key");

    private static LlmRequest SimpleRequest(string text = "Hello, Claude!")
        => new()
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1024,
            Messages = [UserMessage(text)],
        };

    private static Message UserMessage(string text)
        => new() { Role = "user", Content = MessageContent.FromText(text) };

    private static Tool CalculatorTool()
        => new()
        {
            Type = ToolTypes.Function,
            Function = new Function
            {
                Name = "calculator",
                Description = "Perform calculations",
                Parameters = Json(
                    """{"type": "object", "properties": {"expression": {"type": "string"}}, "required": ["expression"]}"""),
            },
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

    private static List<JsonElement> ToolUseBlocks(JsonElement body)
    {
        var blocks = new List<JsonElement>();
        foreach (var message in body.GetProperty("messages").EnumerateArray())
        {
            if (!message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "tool_use")
                {
                    blocks.Add(block);
                }
            }
        }

        return blocks;
    }
}
