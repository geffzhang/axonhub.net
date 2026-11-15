using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the Responses API inbound transformer and its response
/// converter (llm/transformer/openai/responses/inbound.go, inbound_convert.go).
/// Expectations mirror inbound_test.go.
/// </summary>
public sealed class ResponsesInboundTransformerTests
{
    private static readonly ResponsesInboundTransformer Inbound = new();

    [Fact]
    public void RejectsNilRequest()
    {
        var ex = Assert.Throws<TransformerException>(() => Inbound.TransformRequest(null));
        Assert.Equal("invalid request: http request is nil", ex.Message);
    }

    [Fact]
    public void RejectsEmptyBody()
    {
        var ex = Assert.Throws<TransformerException>(
            () => Inbound.TransformRequest(new HttpRequestData { Body = [] }));
        Assert.Equal("invalid request: request body is empty", ex.Message);
    }

    [Fact]
    public void ReportsGoSyntaxErrorForMalformedBody()
    {
        var ex = Assert.Throws<TransformerException>(() => Inbound.TransformRequest(JsonRequest("{invalid json}")));
        Assert.StartsWith(
            "invalid request: failed to decode responses api request: invalid character",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsGoRootTypeErrorForNonObjectBody()
    {
        var ex = Assert.Throws<TransformerException>(() => Inbound.TransformRequest(JsonRequest("[]")));
        Assert.Equal(
            "invalid request: failed to decode responses api request: "
            + "json: cannot unmarshal array into Go value of type responses.Request",
            ex.Message);
    }

    [Fact]
    public void ReportsSystemTextJsonWordingForFieldTypeErrors()
    {
        // Syntax and top-level type errors use Go's wording, but field-level
        // type mismatches surface the decoder's own message (difference C064).
        var ex = Assert.Throws<TransformerException>(
            () => Inbound.TransformRequest(JsonRequest("{\"model\":123,\"input\":\"Hello\"}")));

        Assert.StartsWith(
            "invalid request: failed to decode responses api request: The JSON value could not be converted",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresModel()
    {
        var ex = Assert.Throws<TransformerException>(
            () => Inbound.TransformRequest(JsonRequest("{\"input\":\"Hello\"}")));
        Assert.Equal("invalid request: model is required", ex.Message);
    }

    [Fact]
    public void RejectsStreamIdWithParam()
    {
        var ex = Assert.Throws<ResponseError>(
            () => Inbound.TransformRequest(JsonRequest(
                "{\"model\":\"gpt-4o\",\"input\":\"Hello\",\"stream_id\":\"main\"}")));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Unsupported parameter: stream_id", ex.Detail.Message);
        Assert.Equal("invalid_request_error", ex.Detail.Type);
        Assert.Equal("stream_id", ex.Detail.Param);
    }

    [Fact]
    public void RejectsUnsupportedContentType()
    {
        var request = JsonRequest("{\"model\":\"gpt-4o\",\"input\":\"Hello\"}");
        request.Headers!["Content-Type"] = ["text/plain"];

        var ex = Assert.Throws<TransformerException>(() => Inbound.TransformRequest(request));
        Assert.Equal("invalid request: unsupported content type: text/plain", ex.Message);
    }

    [Fact]
    public void ConvertsSimpleTextInput()
    {
        var request = Transform("{\"model\":\"gpt-4o\",\"input\":\"Hello, world!\"}");

        Assert.Equal("gpt-4o", request.Model);
        Assert.Equal(RequestTypes.Chat, request.RequestType);
        Assert.Equal(ApiFormats.OpenAIResponse, request.ApiFormat);

        // Go's Request.Stream is *bool and the unified Stream is *bool too, so
        // an absent stream flag stays nil rather than becoming false.
        Assert.Null(request.Stream);
        var message = Assert.Single(request.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal("Hello, world!", message.Content!.Content);
    }

    [Fact]
    public void ConvertsInstructionsToSystemMessage()
    {
        var request = Transform(
            "{\"model\":\"gpt-4o\",\"instructions\":\"You are a helpful assistant.\",\"input\":\"Hello!\"}");

        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal("You are a helpful assistant.", request.Messages[0].Content!.Content);
        Assert.Equal("user", request.Messages[1].Role);
        Assert.Equal("Hello!", request.Messages[1].Content!.Content);
    }

    [Fact]
    public void ConvertsSamplingAndTokenLimits()
    {
        var request = Transform(
            "{\"model\":\"gpt-4o\",\"input\":\"Hello\","
            + "\"temperature\":0.7,\"top_p\":0.9,\"max_output_tokens\":1000,"
            + "\"top_logprobs\":5}");

        Assert.Equal(0.7, request.Temperature);
        Assert.Equal(0.9, request.TopP);
        Assert.Equal(1000, request.MaxCompletionTokens);
        Assert.Equal(5, request.TopLogprobs);
    }

    [Fact]
    public void ConvertsFunctionAndNamespaceTools()
    {
        var request = Transform(
            """
            {
              "model": "gpt-4o",
              "input": "List the projects.",
              "tools": [
                {
                  "type": "namespace",
                  "name": "mcp__codebase_memory_mcp",
                  "tools": [
                    {
                      "type": "function",
                      "name": "list_projects",
                      "description": "List stored projects",
                      "parameters": {"type": "object", "properties": {}},
                      "strict": true
                    },
                    {
                      "type": "function",
                      "name": "get_project",
                      "parameters": {"type": "object", "properties": {"id": {"type": "string"}}, "required": ["id"]}
                    },
                    {"type": "web_search", "name": "unsupported_nested_tool"}
                  ]
                },
                {"type": "function", "name": "get_weather", "parameters": {"type": "object", "properties": {}}}
              ]
            }
            """);

        Assert.NotNull(request.Tools);
        Assert.Equal(3, request.Tools!.Count);

        var listProjects = request.Tools[0];
        Assert.Equal("function", listProjects.Type);
        Assert.Equal("mcp__codebase_memory_mcp__list_projects", listProjects.Function.Name);
        Assert.Equal("mcp__codebase_memory_mcp", listProjects.Function.Namespace);
        Assert.Equal("List stored projects", listProjects.Function.Description);
        Assert.True(listProjects.Function.Strict);

        Assert.Equal("mcp__codebase_memory_mcp__get_project", request.Tools[1].Function.Name);
        Assert.Equal("get_weather", request.Tools[2].Function.Name);
    }

    [Fact]
    public void ConvertsImageGenerationTool()
    {
        var request = Transform(
            "{\"model\":\"gpt-4o\",\"input\":\"Generate an image of a cat\","
            + "\"tools\":[{\"type\":\"image_generation\",\"quality\":\"high\",\"size\":\"1024x1024\"}]}");

        var tool = Assert.Single(request.Tools!);
        Assert.Equal(ToolTypes.ImageGeneration, tool.Type);
        Assert.NotNull(tool.ImageGeneration);
        Assert.Equal("high", tool.ImageGeneration!.Quality);
        Assert.Equal("1024x1024", tool.ImageGeneration.Size);
    }

    [Theory]
    [InlineData("{\"effort\":\"high\",\"max_tokens\":5000}", "high", 5000L, null)]
    [InlineData("{\"effort\":\"medium\",\"summary\":\"detailed\"}", "medium", null, "detailed")]
    [InlineData("{\"effort\":\"low\",\"generate_summary\":\"concise\"}", "low", null, "concise")]
    [InlineData(
        "{\"effort\":\"high\",\"summary\":\"auto\",\"generate_summary\":\"detailed\"}",
        "high",
        null,
        "auto")]
    public void ConvertsReasoning(
        string reasoning,
        string expectedEffort,
        long? expectedBudget,
        string? expectedSummary)
    {
        var request = Transform(
            "{\"model\":\"o3\",\"input\":\"Solve this problem\",\"reasoning\":" + reasoning + "}");

        Assert.Equal(expectedEffort, request.ReasoningEffort);
        Assert.Equal(expectedBudget, request.ReasoningBudget);
        Assert.Equal(expectedSummary, request.ReasoningSummary);
    }

    [Theory]
    [InlineData("\"auto\"", "auto", null, null)]
    [InlineData("{\"mode\":\"auto\"}", "auto", null, null)]
    [InlineData("{\"type\":\"function\",\"name\":\"get_weather\"}", null, "function", "get_weather")]
    public void ConvertsToolChoice(
        string toolChoice,
        string? expectedMode,
        string? expectedType,
        string? expectedName)
    {
        var request = Transform(
            "{\"model\":\"gpt-4o\",\"input\":\"Hello\",\"tool_choice\":" + toolChoice + "}");

        Assert.NotNull(request.ToolChoice);
        Assert.Equal(expectedMode, request.ToolChoice!.ToolChoiceMode);
        if (expectedType is not null)
        {
            Assert.NotNull(request.ToolChoice.NamedToolChoice);
            Assert.Equal(expectedType, request.ToolChoice.NamedToolChoice!.Type);
            Assert.Equal(expectedName, request.ToolChoice.NamedToolChoice.Function.Name);
        }
    }

    [Fact]
    public void ConvertsMetadataStoreAndServiceTier()
    {
        var request = Transform(
            "{\"model\":\"gpt-4o\",\"input\":\"Hello\","
            + "\"metadata\":{\"user_id\":\"123\",\"session_id\":\"abc\"},"
            + "\"store\":true,\"service_tier\":\"default\"}");

        Assert.NotNull(request.Metadata);
        Assert.Equal("123", request.Metadata!["user_id"]);
        Assert.Equal("abc", request.Metadata["session_id"]);
        Assert.True(request.Store);
        Assert.Equal("default", request.ServiceTier);
    }

    [Fact]
    public void ConvertsTextFormat()
    {
        var request = Transform(
            "{\"model\":\"gpt-4o\",\"input\":\"Return JSON\","
            + "\"text\":{\"format\":{\"type\":\"json_object\"}}}");

        Assert.NotNull(request.ResponseFormat);
        Assert.Equal("json_object", request.ResponseFormat!.Type);
    }

    [Fact]
    public void ConvertsPreviousResponseId()
    {
        var request = Transform(
            "{\"model\":\"gpt-5.4\",\"previous_response_id\":\"resp_prev_123\",\"input\":\"Continue\"}");

        Assert.Equal("resp_prev_123", request.PreviousResponseId);
    }

    [Fact]
    public void KeepsAdditionalToolsInputItemAsRawFragment()
    {
        var request = Transform(
            """
            {
              "model": "gpt-4o",
              "input": [
                {"type": "additional_tools", "tools": [{"type": "function", "name": "shell"}]},
                {"type": "message", "role": "user", "content": "Hello"}
              ]
            }
            """);

        var message = Assert.Single(request.Messages);
        Assert.Equal("user", message.Role);

        var fragments = request.ProviderExtensions?.OpenAIResponses?.Request?.RawInputItems;
        Assert.NotNull(fragments);
        var fragment = Assert.Single(fragments!);
        Assert.Equal("additional_tools", fragment.Type);
        Assert.Equal(0, fragment.OriginalIndex);
        Assert.Contains("shell", fragment.Raw!.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TransformResponseRejectsNil()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Inbound.TransformResponse(null));
        Assert.Equal("chat completion response is nil", ex.Message);
    }

    [Fact]
    public void TransformsTextResponse()
    {
        var response = Inbound.TransformResponse(new LlmResponse
        {
            Id = "chatcmpl-123",
            Object = "chat.completion",
            Created = 1677652288,
            Model = "gpt-4o",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Message = new Message
                    {
                        Role = "assistant",
                        Content = MessageContent.FromText("Hello! How can I help you?"),
                    },
                    FinishReason = "stop",
                },
            ],
            Usage = new Usage
            {
                PromptTokens = 10,
                CompletionTokens = 20,
                TotalTokens = 30,
            },
        });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(response.Headers, "Content-Type"));
        Assert.Equal("no-cache", HttpClientUtils.HeaderGet(response.Headers, "Cache-Control"));

        var body = ParseResponse(response);
        Assert.Equal("response", body.Object);
        Assert.Equal("chatcmpl-123", body.Id);
        Assert.Equal("gpt-4o", body.Model);
        Assert.Equal("completed", body.Status);

        var output = Assert.Single(body.Output!);
        Assert.Equal("message", output.Type);
        Assert.Equal("assistant", output.Role);
        Assert.Equal("Hello! How can I help you?", output.Content!.Items![0].Text);
    }

    [Fact]
    public void TransformsToolCallResponseToCompleted()
    {
        var response = Inbound.TransformResponse(new LlmResponse
        {
            Id = "chatcmpl-456",
            Model = "gpt-4o",
            Choices =
            [
                new Choice
                {
                    Message = new Message
                    {
                        Role = "assistant",
                        ToolCalls =
                        [
                            new ToolCall
                            {
                                Id = "call_123",
                                Type = "function",
                                Function = new FunctionCall
                                {
                                    Name = "get_weather",
                                    Arguments = "{\"location\": \"San Francisco\"}",
                                },
                            },
                        ],
                    },
                    FinishReason = "tool_calls",
                },
            ],
        });

        var body = ParseResponse(response);
        Assert.Equal("completed", body.Status);
    }

    [Fact]
    public void TransformsUsageDetails()
    {
        var response = Inbound.TransformResponse(new LlmResponse
        {
            Id = "chatcmpl-789",
            Model = "gpt-4o",
            Choices =
            [
                new Choice
                {
                    Message = new Message { Role = "assistant", Content = MessageContent.FromText("ok") },
                    FinishReason = "stop",
                },
            ],
            Usage = new Usage
            {
                PromptTokens = 100,
                CompletionTokens = 50,
                TotalTokens = 150,
                PromptTokensDetails = new PromptTokensDetails { CachedTokens = 20 },
                CompletionTokensDetails = new CompletionTokensDetails { ReasoningTokens = 10 },
            },
        });

        var usage = ParseResponse(response).Usage;
        Assert.NotNull(usage);
        Assert.Equal(100, usage!.InputTokens);
        Assert.Equal(50, usage.OutputTokens);
        Assert.Equal(150, usage.TotalTokens);
        Assert.Equal(20, usage.InputTokensDetails.CachedTokens);
        Assert.Equal(10, usage.OutputTokensDetails.ReasoningTokens);
    }

    [Theory]
    [InlineData("stop", "completed")]
    [InlineData("tool_calls", "completed")]
    [InlineData("length", "incomplete")]
    [InlineData("error", "failed")]
    public void MapsFinishReasonToStatus(string finishReason, string expectedStatus)
    {
        var response = Inbound.TransformResponse(new LlmResponse
        {
            Id = "chatcmpl-status",
            Model = "gpt-4o",
            Choices =
            [
                new Choice
                {
                    Message = new Message { Role = "assistant", Content = MessageContent.FromText("text") },
                    FinishReason = finishReason,
                },
            ],
        });

        Assert.Equal(expectedStatus, ParseResponse(response).Status);
    }

    [Fact]
    public void TransformsPreviousResponseId()
    {
        var response = Inbound.TransformResponse(new LlmResponse
        {
            Id = "chatcmpl-prev",
            Model = "gpt-5.4",
            PreviousResponseId = "resp_prev_123",
            Choices =
            [
                new Choice
                {
                    Message = new Message { Role = "assistant", Content = MessageContent.FromText("continued") },
                    FinishReason = "stop",
                },
            ],
        });

        Assert.Equal("resp_prev_123", ParseResponse(response).PreviousResponseId);
    }

    [Fact]
    public void TransformErrorForNilErrorIsInternal()
    {
        var response = Inbound.TransformError(null);

        Assert.Equal(500, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body!);
        Assert.Equal("internal server error", body.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("internal_error", body.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public void TransformErrorMapsInvalidRequestSentinel()
    {
        var response = Inbound.TransformError(TransformerErrors.InvalidRequest("bad input"));

        Assert.Equal(400, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body!);
        Assert.Equal("invalid_request_error", body.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public void TransformErrorMapsInvalidModelSentinel()
    {
        var response = Inbound.TransformError(TransformerErrors.InvalidModel("unknown"));

        Assert.Equal(422, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body!);
        Assert.Equal("invalid_model_error", body.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public void TransformErrorMapsResponseErrorEnvelope()
    {
        var response = Inbound.TransformError(new ResponseError
        {
            StatusCode = 429,
            Detail = new ErrorDetail
            {
                Message = "Rate limit exceeded",
                Type = "rate_limit_error",
                Code = "rate_limit",
            },
        });

        Assert.Equal(429, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body!);
        var error = body.RootElement.GetProperty("error");
        Assert.Equal("Rate limit exceeded", error.GetProperty("message").GetString());
        Assert.Equal("rate_limit_error", error.GetProperty("type").GetString());
        Assert.Equal("rate_limit", error.GetProperty("code").GetString());
    }

    [Fact]
    public void TransformErrorPassesHttpClientErrorThrough()
    {
        byte[] payload = Encoding.UTF8.GetBytes("upstream said no");
        var response = Inbound.TransformError(
            new HttpClientError("POST", "https://upstream.test", 502, "502 Bad Gateway", payload));

        Assert.Equal(502, response.StatusCode);
        Assert.Equal(payload, response.Body);
    }

    // Port of inbound_test.go
    // TestConvertInputToMessages_KeepsToolOutputAdjacentAcrossAnInterveningMessage:
    // a Responses client may place a message item between a tool call and its
    // output, but Chat-compatible upstreams require the tool message next to the
    // assistant message carrying its tool_calls.
    [Fact]
    public void KeepsToolOutputAdjacentAcrossAnInterveningMessage()
    {
        static List<string> Roles(List<Message> messages) =>
            messages.Select(static message => message.Role ?? string.Empty).ToList();

        static List<Message> Convert(params ResponsesItem[] items) =>
            ResponsesInboundConvert.ConvertInputToMessages(
                new ResponsesInput { Items = [.. items] })!;

        var messages = Convert(
            new ResponsesItem
            {
                Type = "function_call",
                CallId = "call_a",
                Name = "run_command",
                Arguments = """{"cmd":"ls"}""",
            },
            new ResponsesItem
            {
                Role = "system",
                Content = new ResponsesInput { Text = "Approved command prefix saved" },
            },
            new ResponsesItem
            {
                Type = "function_call_output",
                CallId = "call_a",
                Output = new ResponsesInput { Text = "first result" },
            });

        Assert.Equal(["assistant", "tool", "system"], Roles(messages));
        Assert.Equal("call_a", messages[0].ToolCalls![0].Id);
        Assert.Equal("call_a", messages[1].ToolCallId);
        Assert.Equal("first result", messages[1].Content!.Content);
        Assert.Equal("Approved command prefix saved", messages[2].Content!.Content);

        messages = Convert(
            new ResponsesItem
            {
                Type = "function_call",
                CallId = "call_a",
                Name = "first_tool",
                Arguments = "{}",
            },
            new ResponsesItem
            {
                Role = "user",
                Content = new ResponsesInput { Text = "New turn." },
            },
            new ResponsesItem
            {
                Type = "function_call_output",
                CallId = "call_a",
                Output = new ResponsesInput { Text = "first result" },
            });

        Assert.Equal(["assistant", "tool", "user"], Roles(messages));
        Assert.Equal("call_a", messages[1].ToolCallId);
        Assert.Equal("New turn.", messages[2].Content!.Content);

        messages = Convert(
            new ResponsesItem
            {
                Type = "custom_tool_call",
                CallId = "call_custom",
                Name = "custom_tool",
                Input = "freeform",
            },
            new ResponsesItem
            {
                Role = "user",
                Content = new ResponsesInput { Text = "Interjected." },
            },
            new ResponsesItem
            {
                Type = "custom_tool_call_output",
                CallId = "call_custom",
                Output = new ResponsesInput { Text = "custom result" },
            });

        Assert.Equal(["assistant", "tool", "user"], Roles(messages));
        Assert.Equal("call_custom", messages[1].ToolCallId);
        Assert.Equal("custom result", messages[1].Content!.Content);

        messages = Convert(
            new ResponsesItem
            {
                Id = "reasoning_1",
                Type = "reasoning",
                Summary = [new ResponsesReasoningSummary { Text = "thinking" }],
            },
            new ResponsesItem
            {
                Type = "function_call",
                CallId = "call_a",
                Name = "first_tool",
                Arguments = "{}",
            },
            new ResponsesItem
            {
                Role = "system",
                Content = new ResponsesInput { Text = "Note." },
            },
            new ResponsesItem
            {
                Type = "function_call_output",
                CallId = "call_a",
                Output = new ResponsesInput { Text = "first result" },
            });

        Assert.Equal(["assistant", "tool", "system"], Roles(messages));
        Assert.Single(messages[0].ToolCalls!);
        Assert.Equal("call_a", messages[0].ToolCalls![0].Id);
        Assert.Equal("call_a", messages[1].ToolCallId);

        messages = Convert(
            new ResponsesItem
            {
                Type = "function_call",
                CallId = "call_a",
                Name = "first_tool",
                Arguments = "{}",
            },
            new ResponsesItem
            {
                Type = "function_call",
                CallId = "call_b",
                Name = "second_tool",
                Arguments = "{}",
            },
            new ResponsesItem
            {
                Role = "user",
                Content = new ResponsesInput { Text = "Interjected." },
            },
            new ResponsesItem
            {
                Type = "function_call_output",
                CallId = "call_b",
                Output = new ResponsesInput { Text = "second result" },
            },
            new ResponsesItem
            {
                Type = "function_call_output",
                CallId = "call_a",
                Output = new ResponsesInput { Text = "first result" },
            },
            new ResponsesItem
            {
                Role = "user",
                Content = new ResponsesInput { Text = "Continue." },
            });

        Assert.Equal(["assistant", "tool", "tool", "user", "user"], Roles(messages));
        Assert.Equal(2, messages[0].ToolCalls!.Count);
        Assert.Equal("call_b", messages[1].ToolCallId);
        Assert.Equal("second result", messages[1].Content!.Content);
        Assert.Equal("call_a", messages[2].ToolCallId);
        Assert.Equal("Interjected.", messages[3].Content!.Content);
        Assert.Equal("Continue.", messages[4].Content!.Content);

        messages = Convert(
            new ResponsesItem
            {
                Type = "function_call",
                CallId = "call_a",
                Name = "first_tool",
                Arguments = "{}",
            },
            new ResponsesItem
            {
                Role = "user",
                Content = new ResponsesInput { Text = "Interjected." },
            },
            new ResponsesItem
            {
                Type = "function_call",
                CallId = "call_b",
                Name = "second_tool",
                Arguments = "{}",
            },
            new ResponsesItem
            {
                Type = "function_call_output",
                CallId = "call_b",
                Output = new ResponsesInput { Text = "second result" },
            });

        Assert.Equal(["assistant", "user", "assistant", "tool"], Roles(messages));
        Assert.Equal("call_a", messages[0].ToolCalls![0].Id);
        Assert.Equal("call_b", messages[2].ToolCalls![0].Id);
        Assert.Equal("call_b", messages[3].ToolCallId);
    }

    private static HttpRequestData JsonRequest(string body) => new()
    {
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        },
        Body = Encoding.UTF8.GetBytes(body),
    };

    private static LlmRequest Transform(string body) => Inbound.TransformRequest(JsonRequest(body));

    private static ResponsesResponse ParseResponse(HttpResponseData response)
    {
        var parsed = LlmJson.Deserialize<ResponsesResponse>(response.Body!);
        return Assert.IsType<ResponsesResponse>(parsed);
    }
}
