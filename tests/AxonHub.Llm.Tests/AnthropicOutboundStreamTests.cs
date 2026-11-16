using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Anthropic outbound SSE transformer
/// (llm/transformer/anthropic/outbound_stream.go and its tests), including the
/// fixture-driven Anthropic→LLM chunk comparison.
/// </summary>
public sealed class AnthropicOutboundStreamTests
{
    private static readonly string TestDataRoot =
        Path.Combine(AppContext.BaseDirectory, "testdata", "anthropic");

    // ------------------------------------------------------------------
    // Fixture-driven transformation (reference
    // TestOutboundTransformer_StreamTransformation_WithTestData)
    // ------------------------------------------------------------------

    [Fact]
    public void StreamTransformation_MatchesExpectedLlmChunks()
    {
        var cases = new (string Name, string StreamFile, string ExpectedFile)[]
        {
            ("response with stop finish reason", "anthropic-stop.stream.jsonl", "llm-stop.stream.jsonl"),
            ("response with tool calls", "anthropic-tool.stream.jsonl", "llm-tool.stream.jsonl"),
            ("response with thinking", "anthropic-think.stream.jsonl", "llm-think.stream.jsonl"),
        };

        foreach (var testCase in cases)
        {
            var transformer = Direct();
            var source = new SliceStream<StreamEvent>(LoadStreamChunks(testCase.StreamFile));

            using var transformed = transformer.TransformStream(null, source);
            var actual = GoStreams.All(transformed, out var error);
            Assert.Null(error);

            var expected = LoadLlmResponses(testCase.ExpectedFile);
            Assert.Equal(expected.Count, actual.Count);

            for (var i = 0; i < expected.Count; i++)
            {
                AssertResponseMatches(expected[i], actual[i], testCase.Name, i);
            }
        }
    }

    // ------------------------------------------------------------------
    // Error events (reference TestOutboundTransformer_StreamTransformation_ErrorEvent)
    // ------------------------------------------------------------------

    [Fact]
    public void StreamTransformation_SurfacesErrorEvent()
    {
        var transformer = Direct();
        var source = new SliceStream<StreamEvent>(LoadStreamChunks("anthropic-error.stream.jsonl"));

        using var transformed = transformer.TransformStream(null, source);
        GoStreams.All(transformed, out var error);

        var responseError = Assert.IsAssignableFrom<ResponseError>(error);
        Assert.Contains("当前订阅套餐暂未开放GPT-6权限", responseError.Detail.Message, StringComparison.Ordinal);
        Assert.Equal("1311", responseError.Detail.Code);
    }

    [Fact]
    public void ParseAnthropicStreamErrorEvent_AnthropicEnvelope()
    {
        var error = AnthropicOutboundStream.ParseAnthropicStreamErrorEvent(Event(
            "error",
            """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"},"request_id":"req_9"}"""));

        Assert.NotNull(error);
        Assert.Equal("overloaded_error", error!.Detail.Type);
        Assert.Equal("Overloaded", error.Detail.Message);
        Assert.Equal("req_9", error.Detail.RequestId);
    }

    [Fact]
    public void ParseAnthropicStreamErrorEvent_FallsBackToErrorObjectText()
    {
        var error = AnthropicOutboundStream.ParseAnthropicStreamErrorEvent(Event(
            "error",
            """{"type":"error","error":{"type":"api_error"}}"""));

        Assert.NotNull(error);
        Assert.Equal("api_error", error!.Detail.Type);
        Assert.Equal("""{"type":"api_error"}""", error.Detail.Message);
    }

    [Fact]
    public void ParseAnthropicStreamErrorEvent_MarksBareErrorEnvelope()
    {
        var error = AnthropicOutboundStream.ParseAnthropicStreamErrorEvent(Event(
            "error",
            """{"type":"error","error":{"message":"boom"}}"""));

        Assert.NotNull(error);
        Assert.Equal("stream_error", error!.Detail.Type);
        Assert.Equal("boom", error.Detail.Message);
    }

    [Fact]
    public void ParseAnthropicStreamErrorEvent_EmptyData()
    {
        var error = AnthropicOutboundStream.ParseAnthropicStreamErrorEvent(new StreamEvent { Type = "error" });

        Assert.NotNull(error);
        Assert.Equal("stream_error", error!.Detail.Type);
        Assert.Equal("stream error", error.Detail.Message);

        Assert.Null(AnthropicOutboundStream.ParseAnthropicStreamErrorEvent(null));
    }

    // ------------------------------------------------------------------
    // Usage merging (reference TestOutboundTransformer_StreamTransformation_UsesFinalPromptTokensWhenPresent)
    // ------------------------------------------------------------------

    [Fact]
    public void StreamTransformation_UsesFinalPromptTokensWhenPresent()
    {
        var transformer = new AnthropicOutboundTransformer(new AnthropicOutboundConfig
        {
            Type = AnthropicPlatformType.Zhipu,
            BaseUrl = "https://example.com",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

        var events = new[]
        {
            Event(
                "message_start",
                """
                {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"glm-5.1","usage":{"input_tokens":0,"output_tokens":0}}}
                """),
            Event(
                "message_delta",
                """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":10,"output_tokens":3}}"""),
            Event("message_stop", """{"type":"message_stop"}"""),
        };

        using var transformed = transformer.TransformStream(null, new SliceStream<StreamEvent>(events));
        var responses = GoStreams.All(transformed, out var error);

        Assert.Null(error);
        Assert.Equal(4, responses.Count);
        Assert.NotNull(responses[2].Usage);
        var usage = responses[2].Usage!;
        Assert.Equal(10, usage.PromptTokens);
        Assert.Equal(3, usage.CompletionTokens);
        Assert.Equal(13, usage.TotalTokens);
    }

    // ------------------------------------------------------------------
    // Delta presence (reference outbound_stream_delta_test.go)
    // ------------------------------------------------------------------

    [Fact]
    public void FinishReason_AlwaysIncludesDelta()
    {
        var responses = RunFixture("anthropic-stop.stream.jsonl");

        var finishReasonResponse = responses.FirstOrDefault(
            static response => response.Choices.Count > 0 && response.Choices[0].FinishReason is not null);

        Assert.NotNull(finishReasonResponse);
        Assert.Single(finishReasonResponse!.Choices);

        var choice = finishReasonResponse.Choices[0];
        Assert.NotNull(choice.Delta);
        Assert.Equal("stop", choice.FinishReason);
    }

    [Fact]
    public void AllStreamingChunks_HaveDelta()
    {
        foreach (var file in new[]
                 {
                     "anthropic-stop.stream.jsonl",
                     "anthropic-tool.stream.jsonl",
                     "anthropic-think.stream.jsonl",
                 })
        {
            var responses = RunFixture(file);

            for (var i = 0; i < responses.Count; i++)
            {
                var response = responses[i];
                if (response.Object == "[DONE]" || response.Choices.Count == 0)
                {
                    continue;
                }

                Assert.True(
                    response.Choices[0].Delta is not null,
                    "Response " + i + " in " + file + ": choice must have Delta");
            }
        }
    }

    // ------------------------------------------------------------------
    // Server-side tool blocks (reference outbound_stream_server_tool_use_test.go)
    // ------------------------------------------------------------------

    [Fact]
    public void ServerToolUse_NoPanic()
    {
        var transformer = Direct();

        var events = new[]
        {
            Event(
                "message_start",
                """
                {"type":"message_start","message":{"id":"msg_01AEsGpin3gJumakZWMTyQp3","type":"message","role":"assistant","model":"claude-opus-4-7","content":[],"usage":{"input_tokens":6,"output_tokens":0}}}
                """),
            Event(
                "content_block_start",
                """
                {"type":"content_block_start","index":1,"content_block":{"type":"server_tool_use","id":"srvtoolu_01U9uNSdDhJHvxz2mBp8qtdv","name":"web_search","input":{}}}
                """),
            Event(
                "content_block_delta",
                """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":""}}"""),
            Event(
                "content_block_delta",
                """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"query\": \"K"}}"""),
            Event(
                "content_block_delta",
                """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"Win\"}"}}"""),
            Event(
                "content_block_stop",
                """{"type":"content_block_stop","index":1}"""),
            Event(
                "message_delta",
                """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":10}}"""),
            Event("message_stop", """{"type":"message_stop"}"""),
        };

        using var transformed = transformer.TransformStream(null, new SliceStream<StreamEvent>(events));
        var responses = GoStreams.All(transformed, out var error);

        Assert.Null(error);
        Assert.NotEmpty(responses);
        Assert.Contains(responses, static response => ReferenceEquals(response, LlmResponse.DoneResponse));

        var combinedArgs = new StringBuilder();
        string? toolCallId = null;
        string? toolCallName = null;
        string? anthropicType = null;

        foreach (var response in responses)
        {
            foreach (var choice in response.Choices)
            {
                foreach (var toolCall in choice.Delta?.ToolCalls ?? [])
                {
                    if (!string.IsNullOrEmpty(toolCall.Id))
                    {
                        toolCallId = toolCall.Id;
                    }

                    if (!string.IsNullOrEmpty(toolCall.Function.Name))
                    {
                        toolCallName = toolCall.Function.Name;
                    }

                    if (toolCall.Function.Arguments.Length > 0)
                    {
                        combinedArgs.Append(toolCall.Function.Arguments);
                    }

                    var type = AnthropicJson.GetAnthropicType(toolCall.TransformerMetadata);
                    if (type.Length > 0)
                    {
                        anthropicType = type;
                    }
                }
            }
        }

        Assert.Equal("srvtoolu_01U9uNSdDhJHvxz2mBp8qtdv", toolCallId);
        Assert.Equal("web_search", toolCallName);
        Assert.Equal("server_tool_use", anthropicType);
        Assert.Equal("""{"query": "KWin"}""", combinedArgs.ToString());
        Assert.True(IsValidJson(combinedArgs.ToString()));
    }

    [Fact]
    public void WebSearchToolResult_InlineResult()
    {
        var transformer = Direct();

        var events = new[]
        {
            Event(
                "message_start",
                """
                {"type":"message_start","message":{"id":"msg_abc","type":"message","role":"assistant","model":"claude-opus-4-7","content":[],"usage":{"input_tokens":1,"output_tokens":0}}}
                """),
            Event(
                "content_block_start",
                """
                {"type":"content_block_start","index":0,"content_block":{"type":"web_search_tool_result","tool_use_id":"srvtoolu_abc","content":[{"type":"web_search_result","url":"https://example.com","title":"Example","encrypted_content":"EqgfCio..."}]}}
                """),
            Event(
                "content_block_stop",
                """{"type":"content_block_stop","index":0}"""),
            Event(
                "message_delta",
                """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}"""),
            Event("message_stop", """{"type":"message_stop"}"""),
        };

        using var transformed = transformer.TransformStream(null, new SliceStream<StreamEvent>(events));
        var responses = GoStreams.All(transformed, out var error);

        Assert.Null(error);

        InlineToolResult? inline = null;
        foreach (var response in responses)
        {
            foreach (var choice in response.Choices)
            {
                if (choice.Delta?.InlineToolResults is { Count: > 0 } results)
                {
                    inline = results[0];
                }
            }
        }

        Assert.NotNull(inline);
        Assert.Equal("srvtoolu_abc", inline!.ToolCallId);
        Assert.Equal("web_search_tool_result", AnthropicJson.GetAnthropicType(inline.TransformerMetadata));
        Assert.False(inline.IsError);

        var raw = AnthropicJson.GetAnthropicToolResultContent(inline.TransformerMetadata);
        Assert.NotNull(raw);
        Assert.Equal(JsonValueKind.Array, raw.Value.ValueKind);
        Assert.Equal("web_search_result", raw.Value[0].GetProperty("type").GetString());
    }

    // ------------------------------------------------------------------
    // Event filtering and chunk decoding
    // ------------------------------------------------------------------

    [Fact]
    public void FilterStreamEvent_KeepsOnlyContributingEvents()
    {
        Assert.False(AnthropicOutboundStream.FilterStreamEvent(new StreamEvent { Type = "message_start" }));
        Assert.False(AnthropicOutboundStream.FilterStreamEvent(Event("ping", "{}")));
        Assert.False(AnthropicOutboundStream.FilterStreamEvent(Event("content_block_stop", "{}")));
        Assert.True(AnthropicOutboundStream.FilterStreamEvent(Event("message_start", "{}")));
        Assert.True(AnthropicOutboundStream.FilterStreamEvent(Event("message_stop", "{}")));
        Assert.True(AnthropicOutboundStream.FilterStreamEvent(Event("error", "{}")));
    }

    [Fact]
    public void TransformStreamChunk_DoneSentinel()
    {
        using var stream = new AnthropicOutboundStream(new SliceStream<StreamEvent>([]), AnthropicPlatformType.Direct);

        Assert.Same(LlmResponse.DoneResponse, stream.TransformStreamChunk(Event("message_stop", "[DONE]")));
    }

    [Fact]
    public void TransformStreamChunk_RejectsNilAndEmptyData()
    {
        using var stream = new AnthropicOutboundStream(new SliceStream<StreamEvent>([]), AnthropicPlatformType.Direct);

        var nil = Assert.Throws<InvalidOperationException>(() => stream.TransformStreamChunk(null));
        Assert.Equal("stream event is nil", nil.Message);

        var empty = Assert.Throws<InvalidOperationException>(
            () => stream.TransformStreamChunk(new StreamEvent { Type = "message_stop" }));
        Assert.Equal("event data is empty", empty.Message);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AnthropicOutboundTransformer Direct()
        => AnthropicOutboundTransformer.Create("https://example.com", "direct");

    private static List<LlmResponse> RunFixture(string fileName)
    {
        var source = new SliceStream<StreamEvent>(LoadStreamChunks(fileName));
        using var transformed = Direct().TransformStream(null, source);
        var responses = GoStreams.All(transformed, out var error);
        Assert.Null(error);
        return responses;
    }

    private static StreamEvent Event(string type, string data)
        => new() { Type = type, Data = Encoding.UTF8.GetBytes(data) };

    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AssertResponseMatches(LlmResponse expected, LlmResponse actual, string caseName, int index)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Object, actual.Object);
        Assert.Equal(expected.Model, actual.Model);
        Assert.Equal(expected.Created, actual.Created);
        Assert.Equal(expected.Choices.Count, actual.Choices.Count);

        if (expected.Choices.Count > 0 && actual.Choices.Count > 0)
        {
            var expectedChoice = expected.Choices[0];
            var actualChoice = actual.Choices[0];

            Assert.Equal(expectedChoice.Index, actualChoice.Index);
            Assert.Equal(expectedChoice.FinishReason, actualChoice.FinishReason);
            AssertDeltaMatches(expectedChoice.Delta, actualChoice.Delta, caseName, index);
        }

        if (expected.Usage is null)
        {
            Assert.Null(actual.Usage);
        }
        else
        {
            Assert.NotNull(actual.Usage);
            Assert.Equal(expected.Usage.PromptTokens, actual.Usage!.PromptTokens);
            Assert.Equal(expected.Usage.CompletionTokens, actual.Usage.CompletionTokens);
            Assert.Equal(expected.Usage.TotalTokens, actual.Usage.TotalTokens);
        }
    }

    private static void AssertDeltaMatches(Message? expected, Message? actual, string caseName, int index)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Role, actual!.Role);
        Assert.Equal(expected.Content?.Content, actual.Content?.Content);
        Assert.Equal(expected.ReasoningContent, actual.ReasoningContent);

        var expectedCalls = expected.ToolCalls ?? [];
        var actualCalls = actual.ToolCalls ?? [];
        Assert.Equal(expectedCalls.Count, actualCalls.Count);

        for (var i = 0; i < expectedCalls.Count; i++)
        {
            Assert.Equal(expectedCalls[i].Index, actualCalls[i].Index);
            Assert.Equal(expectedCalls[i].Id, actualCalls[i].Id);
            Assert.Equal(expectedCalls[i].Type, actualCalls[i].Type);
            Assert.Equal(expectedCalls[i].Function.Name, actualCalls[i].Function.Name);
            Assert.Equal(expectedCalls[i].Function.Arguments, actualCalls[i].Function.Arguments);
        }

        var expectedInline = expected.InlineToolResults ?? [];
        var actualInline = actual.InlineToolResults ?? [];
        Assert.Equal(expectedInline.Count, actualInline.Count);

        for (var i = 0; i < expectedInline.Count; i++)
        {
            Assert.Equal(expectedInline[i].ToolCallId, actualInline[i].ToolCallId);
            Assert.Equal(expectedInline[i].Output, actualInline[i].Output);
            Assert.Equal(expectedInline[i].IsError, actualInline[i].IsError);
        }
    }

    private static List<LlmResponse> LoadLlmResponses(string fileName)
    {
        var responses = new List<LlmResponse>();
        foreach (var chunk in LoadStreamChunks(fileName))
        {
            if (Encoding.UTF8.GetString(chunk.Data).Contains("[DONE]", StringComparison.Ordinal))
            {
                responses.Add(LlmResponse.Done());
                continue;
            }

            responses.Add(LlmJson.Deserialize<LlmResponse>(chunk.Data)!);
        }

        return responses;
    }

    private static List<StreamEvent> LoadStreamChunks(string fileName)
    {
        var events = new List<StreamEvent>();
        foreach (var line in File.ReadLines(Path.Combine(TestDataRoot, fileName)))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            events.Add(new StreamEvent
            {
                LastEventId = root.TryGetProperty("LastEventID", out var lastEventId)
                    ? lastEventId.GetString() ?? string.Empty
                    : string.Empty,
                Type = root.TryGetProperty("Type", out var type) ? type.GetString() ?? string.Empty : string.Empty,
                Data = Encoding.UTF8.GetBytes(
                    root.TryGetProperty("Data", out var data) ? data.GetString() ?? string.Empty : string.Empty),
            });
        }

        return events;
    }
}
