using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Anthropic;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Anthropic inbound stream transformer
/// (llm/transformer/anthropic/inbound_stream.go and inbound_stream_test.go),
/// including the fixture-driven OpenAI→Anthropic event comparison.
/// </summary>
public sealed class AnthropicInboundStreamTests
{
    private static readonly AnthropicInboundTransformer Transformer = new();

    private static readonly string TestDataRoot =
        Path.Combine(AppContext.BaseDirectory, "testdata", "anthropic");

    // ------------------------------------------------------------------
    // Clean exhaustion without a finish reason
    // ------------------------------------------------------------------

    [Fact]
    public void FinalizesOnCleanExhaustionWithoutFinishReason()
    {
        var cases = new (string Name, Message Delta, Usage? Usage, string StopReason, long Output)[]
        {
            (
                "tool call",
                new Message
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Index = 0,
                            Id = "call_123",
                            Type = "function",
                            Function = new FunctionCall { Name = "search", Arguments = "{\"query\":\"test\"}" },
                        },
                    ],
                },
                new Usage { CompletionTokens = 7 },
                "tool_use",
                7),
            (
                "tool call with empty arguments",
                new Message
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Index = 0,
                            Id = "call_empty",
                            Type = "function",
                            Function = new FunctionCall { Name = "list" },
                        },
                    ],
                },
                null,
                "tool_use",
                0),
            (
                "server tool call",
                new Message
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Index = 0,
                            Id = "server_tool_123",
                            Type = "function",
                            Function = new FunctionCall { Name = "web_search", Arguments = "{\"query\":\"test\"}" },
                            TransformerMetadata = Metadata(AnthropicJson.KeyAnthropicType, "server_tool_use"),
                        },
                    ],
                },
                null,
                "end_turn",
                0),
            (
                "mcp tool call",
                new Message
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Index = 0,
                            Id = "mcp_tool_123",
                            Type = "function",
                            Function = new FunctionCall { Name = "mcp_search", Arguments = "{\"query\":\"test\"}" },
                            TransformerMetadata = Metadata(AnthropicJson.KeyAnthropicType, "mcp_tool_use"),
                        },
                    ],
                },
                null,
                "end_turn",
                0),
            (
                "text",
                new Message { Role = "assistant", Content = MessageContent.FromText("Done") },
                null,
                "end_turn",
                0),
            (
                "thinking",
                new Message { Role = "assistant", ReasoningContent = "Thinking" },
                null,
                "end_turn",
                0),
        };

        foreach (var testCase in cases)
        {
            var input = new List<LlmResponse>
            {
                new()
                {
                    Id = "msg_missing_finish_reason",
                    Object = "chat.completion.chunk",
                    Model = "claude-sonnet-4-6",
                    Choices = [new Choice { Index = 0, Delta = testCase.Delta }],
                },
                new()
                {
                    Id = "msg_missing_finish_reason",
                    Object = "chat.completion.chunk",
                    Model = "claude-sonnet-4-6",
                    Choices = [new Choice { Index = 0 }],
                    Usage = testCase.Usage,
                },
            };

            var events = Collect(input);

            Assert.True(events.Count >= 3, testCase.Name);
            var terminal = events.Skip(events.Count - 3).ToList();
            Assert.Equal("content_block_stop", terminal[0].Type);
            Assert.Equal("message_delta", terminal[1].Type);
            Assert.NotNull(terminal[1].Delta);
            Assert.Equal(testCase.StopReason, terminal[1].Delta!.StopReason);
            Assert.NotNull(terminal[1].Usage);
            Assert.Equal(testCase.Output, terminal[1].Usage!.OutputTokens);
            Assert.Equal("message_stop", terminal[2].Type);
        }
    }

    [Fact]
    public void RejectsIncompleteToolArgumentsOnCleanExhaustion()
    {
        var argumentsCases = new[]
        {
            "{\"content\":\"unfinished",
            "null",
            "[]",
            "\"value\"",
            "1",
            "true",
            " ",
        };

        foreach (var arguments in argumentsCases)
        {
            var input = new List<LlmResponse>
            {
                new()
                {
                    Id = "msg_incomplete_tool",
                    Object = "chat.completion.chunk",
                    Model = "test-model",
                    Choices =
                    [
                        new Choice
                        {
                            Index = 0,
                            Delta = new Message
                            {
                                Role = "assistant",
                                ToolCalls =
                                [
                                    new ToolCall
                                    {
                                        Index = 0,
                                        Id = "call_incomplete",
                                        Type = "function",
                                        Function = new FunctionCall { Name = "write", Arguments = arguments },
                                    },
                                ],
                            },
                        },
                    ],
                },
            };

            var (events, err) = CollectWithError(input);

            Assert.NotNull(err);
            Assert.Contains("invalid tool call arguments", err!.Message, StringComparison.Ordinal);
            Assert.Equal(0, Count(events, "content_block_stop"));
            Assert.Equal(0, Count(events, "message_delta"));
            Assert.Equal(0, Count(events, "message_stop"));
        }
    }

    [Fact]
    public void PreservesExplicitFinishReasonWithMalformedToolArguments()
    {
        var input = new List<LlmResponse>
        {
            new()
            {
                Id = "msg_incomplete_tool",
                Object = "chat.completion.chunk",
                Model = "test-model",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            ToolCalls =
                            [
                                new ToolCall
                                {
                                    Index = 0,
                                    Id = "call_incomplete",
                                    Type = "function",
                                    Function = new FunctionCall
                                    {
                                        Name = "write",
                                        Arguments = "{\"content\":\"unfinished",
                                    },
                                },
                            ],
                        },
                    },
                ],
            },
            new()
            {
                Id = "msg_incomplete_tool",
                Object = "chat.completion.chunk",
                Model = "test-model",
                Choices = [new Choice { Index = 0, FinishReason = "tool_calls" }],
                Usage = new Usage { CompletionTokens = 10 },
            },
        };

        var events = Collect(input);

        Assert.Equal(1, Count(events, "content_block_stop"));
        Assert.Equal(1, Count(events, "message_delta"));
        Assert.Equal(1, Count(events, "message_stop"));

        var messageDelta = events.Single(e => e.Type == "message_delta");
        Assert.NotNull(messageDelta.Delta);
        Assert.Equal("tool_use", messageDelta.Delta!.StopReason);
        Assert.NotNull(messageDelta.Usage);
        Assert.Equal(10, messageDelta.Usage!.OutputTokens);
    }

    [Fact]
    public void RejectsInvalidEarlierReadArgumentsOnCleanExhaustion()
    {
        var input = new List<LlmResponse>
        {
            new()
            {
                Id = "msg_multiple_tools",
                Object = "chat.completion.chunk",
                Model = "test-model",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            ToolCalls =
                            [
                                new ToolCall
                                {
                                    Index = 0,
                                    Id = "call_invalid",
                                    Type = "function",
                                    Function = new FunctionCall
                                    {
                                        Name = "Read",
                                        Arguments = "{\"content\":\"unfinished",
                                    },
                                },
                                new ToolCall
                                {
                                    Index = 1,
                                    Id = "call_valid",
                                    Type = "function",
                                    Function = new FunctionCall
                                    {
                                        Name = "bash",
                                        Arguments = "{\"command\":\"true\"}",
                                    },
                                },
                            ],
                        },
                    },
                ],
            },
        };

        var (events, err) = CollectWithError(input);

        Assert.NotNull(err);
        Assert.Contains("invalid tool call arguments", err!.Message, StringComparison.Ordinal);
        Assert.Equal(0, Count(events, "message_delta"));
        Assert.Equal(0, Count(events, "message_stop"));
    }

    [Fact]
    public void ReportsTheLowestInvalidToolCallIndex()
    {
        // Go iterates the tool-call map, so with several invalid calls the
        // reported index is whatever the runtime visits first. The port sorts
        // the keys, making the error deterministic (difference ledger C052).
        var input = new List<LlmResponse>
        {
            new()
            {
                Id = "msg_two_invalid_tools",
                Object = "chat.completion.chunk",
                Model = "test-model",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            ToolCalls =
                            [
                                new ToolCall
                                {
                                    Index = 1,
                                    Id = "call_later",
                                    Type = "function",
                                    Function = new FunctionCall
                                    {
                                        Name = "write",
                                        Arguments = "{\"b\":\"unfinished",
                                    },
                                },
                                new ToolCall
                                {
                                    Index = 0,
                                    Id = "call_earlier",
                                    Type = "function",
                                    Function = new FunctionCall
                                    {
                                        Name = "write",
                                        Arguments = "{\"a\":\"unfinished",
                                    },
                                },
                            ],
                        },
                    },
                ],
            },
        };

        var (_, err) = CollectWithError(input);

        Assert.NotNull(err);
        Assert.Contains("tool call index 0", err!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesExplicitFinishReasonWithoutUsage()
    {
        var input = new List<LlmResponse>
        {
            new()
            {
                Id = "msg_incomplete_tool",
                Object = "chat.completion.chunk",
                Model = "test-model",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            ToolCalls =
                            [
                                new ToolCall
                                {
                                    Index = 0,
                                    Id = "call_incomplete",
                                    Type = "function",
                                    Function = new FunctionCall
                                    {
                                        Name = "write",
                                        Arguments = "{\"content\":\"unfinished",
                                    },
                                },
                            ],
                        },
                    },
                ],
            },
            new()
            {
                Id = "msg_incomplete_tool",
                Object = "chat.completion.chunk",
                Model = "test-model",
                Choices = [new Choice { Index = 0, FinishReason = "tool_calls" }],
            },
        };

        var events = Collect(input);

        Assert.Equal(1, Count(events, "message_delta"));
        Assert.Equal(1, Count(events, "message_stop"));

        var messageDelta = events.Single(e => e.Type == "message_delta");
        Assert.NotNull(messageDelta.Delta);
        Assert.Equal("tool_use", messageDelta.Delta!.StopReason);
        Assert.NotNull(messageDelta.Usage);
        Assert.Equal(0, messageDelta.Usage!.OutputTokens);
    }

    // ------------------------------------------------------------------
    // Usage collection
    // ------------------------------------------------------------------

    [Fact]
    public void UsesUsageFromFinalUsageOnlyChunk()
    {
        var events = Collect(
        [
            new LlmResponse
            {
                Id = "chatcmpl-modelscope",
                Object = "chat.completion.chunk",
                Model = "deepseek-v4.1-flash",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message { Role = "assistant", Content = MessageContent.FromText("Hi") },
                    },
                ],
                Usage = new Usage(),
            },
            new LlmResponse
            {
                Id = "chatcmpl-modelscope",
                Object = "chat.completion.chunk",
                Model = "deepseek-v4.1-flash",
                Choices = [new Choice { Index = 0, FinishReason = "stop" }],
                Usage = new Usage(),
            },
            new LlmResponse
            {
                Id = "chatcmpl-modelscope",
                Object = "chat.completion.chunk",
                Model = "deepseek-v4.1-flash",
                Choices = [],
                Usage = new Usage
                {
                    PromptTokens = 338678,
                    CompletionTokens = 5795,
                    TotalTokens = 344473,
                    PromptTokensDetails = new PromptTokensDetails { CachedTokens = 100 },
                },
            },
        ]);

        Assert.Equal(1, Count(events, "message_delta"));
        Assert.Equal(1, Count(events, "message_stop"));

        var deltaUsage = events.Single(e => e.Type == "message_delta").Usage;
        Assert.NotNull(deltaUsage);
        // Anthropic reports cached tokens separately and excludes them from input_tokens.
        Assert.Equal(338578, deltaUsage!.InputTokens);
        Assert.Equal(100, deltaUsage.CacheReadInputTokens);
        Assert.Equal(5795, deltaUsage.OutputTokens);
    }

    [Fact]
    public void UsesUsageFromFinishChunkWithoutUsageOnlyChunk()
    {
        var events = Collect(
        [
            new LlmResponse
            {
                Id = "chatcmpl-standard",
                Object = "chat.completion.chunk",
                Model = "gpt-4o",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message { Role = "assistant", Content = MessageContent.FromText("Hi") },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "chatcmpl-standard",
                Object = "chat.completion.chunk",
                Model = "gpt-4o",
                Choices = [new Choice { Index = 0, FinishReason = "stop" }],
                Usage = new Usage { PromptTokens = 120, CompletionTokens = 8, TotalTokens = 128 },
            },
        ]);

        Assert.Equal(1, Count(events, "message_delta"));
        Assert.Equal(1, Count(events, "message_stop"));

        var deltaUsage = events.Single(e => e.Type == "message_delta").Usage;
        Assert.NotNull(deltaUsage);
        Assert.Equal(120, deltaUsage!.InputTokens);
        Assert.Equal(8, deltaUsage.OutputTokens);
    }

    [Fact]
    public void DoesNotDuplicateTerminalEvents()
    {
        var events = Collect(
        [
            new LlmResponse
            {
                Id = "msg_with_finish_reason",
                Object = "chat.completion.chunk",
                Model = "claude-sonnet-4-6",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message { Role = "assistant", Content = MessageContent.FromText("Done") },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "msg_with_finish_reason",
                Object = "chat.completion.chunk",
                Model = "claude-sonnet-4-6",
                Choices = [new Choice { Index = 0, FinishReason = "stop" }],
                Usage = new Usage(),
            },
        ]);

        Assert.Equal(1, Count(events, "message_delta"));
        Assert.Equal(1, Count(events, "message_stop"));
    }

    [Fact]
    public void DoesNotFinalizeOnSourceError()
    {
        var sourceError = new InvalidOperationException("upstream stream failed");
        var source = new LlmErrorAfterStream
        {
            Items =
            [
                new LlmResponse
                {
                    Id = "msg_stream_error",
                    Object = "chat.completion.chunk",
                    Model = "claude-sonnet-4-6",
                    Choices =
                    [
                        new Choice
                        {
                            Index = 0,
                            Delta = new Message { Role = "assistant", Content = MessageContent.FromText("Partial") },
                        },
                    ],
                },
            ],
            TerminalError = sourceError,
        };

        var (events, err) = CollectWithError(source);

        Assert.Same(sourceError, err);
        Assert.Equal(0, Count(events, "message_delta"));
        Assert.Equal(0, Count(events, "message_stop"));
    }

    // ------------------------------------------------------------------
    // Citations and Read-tool arguments
    // ------------------------------------------------------------------

    [Fact]
    public void EmitsCitationsDeltaBeforeContentBlockStop()
    {
        var events = Collect(
        [
            new LlmResponse
            {
                Id = "msg_annotations_stream",
                Object = "chat.completion.chunk",
                Model = "claude-sonnet-4-6",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            Content = MessageContent.FromText("Annotated answer"),
                            Annotations =
                            [
                                UrlAnnotation("https://example.com/1", "Source One"),
                                UrlAnnotation("https://example.com/2", "Source Two"),
                            ],
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "msg_annotations_stream",
                Object = "chat.completion.chunk",
                Model = "claude-sonnet-4-6",
                Choices = [new Choice { Index = 0, FinishReason = "stop" }],
                Usage = new Usage(),
            },
        ]);

        AssertCitationsDeltaBeforeContentBlockStop(
            events,
            [
                new AnthropicTextCitation
                {
                    Type = "url_citation",
                    Url = "https://example.com/1",
                    Title = "Source One",
                },
                new AnthropicTextCitation
                {
                    Type = "url_citation",
                    Url = "https://example.com/2",
                    Title = "Source Two",
                },
            ]);
    }

    [Fact]
    public void EmitsCitationsDeltaWhenAnnotationsArriveBeforeText()
    {
        var events = Collect(
        [
            new LlmResponse
            {
                Id = "msg_annotations_before_text",
                Object = "chat.completion.chunk",
                Model = "claude-sonnet-4-6",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            Annotations = [UrlAnnotation("https://example.com/metadata-first", "Metadata First")],
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "msg_annotations_before_text",
                Object = "chat.completion.chunk",
                Model = "claude-sonnet-4-6",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            Content = MessageContent.FromText("Annotated after metadata"),
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "msg_annotations_before_text",
                Object = "chat.completion.chunk",
                Model = "claude-sonnet-4-6",
                Choices = [new Choice { Index = 0, FinishReason = "stop" }],
                Usage = new Usage(),
            },
        ]);

        AssertCitationsDeltaBeforeContentBlockStop(
            events,
            [
                new AnthropicTextCitation
                {
                    Type = "url_citation",
                    Url = "https://example.com/metadata-first",
                    Title = "Metadata First",
                },
            ]);
    }

    [Fact]
    public void EmitsCitationsDeltaFromChoiceMessageAnnotations()
    {
        var events = Collect(
        [
            new LlmResponse
            {
                Id = "msg_choice_message_annotations",
                Object = "chat.completion.chunk",
                Model = "claude-sonnet-4-6",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Message = new Message
                        {
                            Annotations =
                            [
                                UrlAnnotation("https://example.com/message-annotations", "Message Annotation"),
                            ],
                        },
                        Delta = new Message
                        {
                            Role = "assistant",
                            Content = MessageContent.FromText("Annotated via choice.message"),
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "msg_choice_message_annotations",
                Object = "chat.completion.chunk",
                Model = "claude-sonnet-4-6",
                Choices = [new Choice { Index = 0, FinishReason = "stop" }],
                Usage = new Usage(),
            },
        ]);

        AssertCitationsDeltaBeforeContentBlockStop(
            events,
            [
                new AnthropicTextCitation
                {
                    Type = "url_citation",
                    Url = "https://example.com/message-annotations",
                    Title = "Message Annotation",
                },
            ]);
    }

    [Fact]
    public void NormalizesOpenAIWebSearchCitationTypeFromChunkMetadata()
    {
        var events = Collect(
        [
            new LlmResponse
            {
                Id = "msg_annotations_stream_web_search",
                Object = "chat.completion.chunk",
                Model = "gpt-4o-search-preview",
                TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["openai_responses_web_search_calls"] = JsonSerializer.SerializeToElement(new object[]
                    {
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["id"] = "ws_123",
                            ["type"] = "web_search_call",
                            ["status"] = "completed",
                            ["action"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["type"] = "search",
                                ["query"] = "latest ai news",
                            },
                        },
                    }),
                },
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            Content = MessageContent.FromText("Annotated answer"),
                            Annotations = [UrlAnnotation("https://example.com/result", "Example Result")],
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "msg_annotations_stream_web_search",
                Object = "chat.completion.chunk",
                Model = "gpt-4o-search-preview",
                Choices = [new Choice { Index = 0, FinishReason = "stop" }],
                Usage = new Usage(),
            },
        ]);

        AssertCitationsDeltaBeforeContentBlockStop(
            events,
            [
                new AnthropicTextCitation
                {
                    Type = "web_search_result_location",
                    Url = "https://example.com/result",
                    Title = "Example Result",
                },
            ]);
    }

    [Fact]
    public void RemovesEmptyReadPages()
    {
        var input = new List<LlmResponse>
        {
            new()
            {
                Id = "msg_read_pages_stream",
                Object = "chat.completion.chunk",
                Model = "claude-sonnet-4-6",
                Choices = [new Choice { Index = 0, Delta = new Message { Role = "assistant" } }],
            },
            ReadToolChunk("call_read", "function", "Read", "{\"file_path\":"),
            // The continuation chunk carries only the index and the next
            // argument fragment, as providers stream tool calls.
            ReadToolChunk(null, null, null, "\"/tmp/a.go\",\"pages\":\"\"}"),
            new()
            {
                Id = "msg_read_pages_stream",
                Object = "chat.completion.chunk",
                Model = "claude-sonnet-4-6",
                Choices = [new Choice { Index = 0, FinishReason = "tool_calls" }],
                Usage = new Usage(),
            },
        };

        var events = Collect(input);

        var argumentDeltas = events
            .Where(e => e.Type == "content_block_delta"
                        && e.Delta is { Type: "input_json_delta", PartialJson: not null })
            .Select(e => e.Delta!.PartialJson!)
            .ToList();

        Assert.Single(argumentDeltas);
        Assert.Equal("{\"file_path\":\"/tmp/a.go\"}", argumentDeltas[0]);
    }

    // ------------------------------------------------------------------
    // Wire shape
    // ------------------------------------------------------------------

    [Fact]
    public void ThinkingDeltaCarriesOnlyTypeAndThinking()
    {
        using var stream = Transformer.TransformStream(
            new SliceStream<LlmResponse>(
            [
                new LlmResponse
                {
                    Id = "msg_wire",
                    Object = "chat.completion.chunk",
                    Model = "claude",
                    Choices =
                    [
                        new Choice
                        {
                            Index = 0,
                            Delta = new Message { Role = "assistant", ReasoningContent = "abc" },
                        },
                    ],
                },
            ]));

        var raw = new List<string>();
        while (stream.Next())
        {
            raw.Add(Encoding.UTF8.GetString(stream.Current!.Data));
        }

        Assert.Null(stream.Err);
        Assert.Contains(
            raw,
            item => item.Contains(
                "\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"abc\"}",
                StringComparison.Ordinal));
        // message_start is emitted before the thinking block.
        Assert.Equal(
            "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_wire\",\"type\":\"message\","
            + "\"role\":\"assistant\",\"content\":[],\"model\":\"claude\","
            + "\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"cache_creation_input_tokens\":0,"
            + "\"cache_read_input_tokens\":0,\"cache_creation\":{\"ephemeral_5m_input_tokens\":0,"
            + "\"ephemeral_1h_input_tokens\":0}}}}",
            raw[0]);
    }

    // ------------------------------------------------------------------
    // Fixture-driven transformation (Go: TestInboundTransformer_StreamTransformation_WithTestData)
    // ------------------------------------------------------------------

    [Fact]
    public void StreamTransformationWithTestData()
    {
        var cases = new (string Name, string InputFile, string ExpectedFile, long InputTokens, Action<AnthropicMessage> Validate)[]
        {
            (
                "stream transformation with stop finish reason",
                "llm-stop.stream.jsonl",
                "anthropic-stop.stream.jsonl",
                21,
                message =>
                {
                    Assert.Equal("msg_bdrk_01Fbg5HKuVfmtT6mAMxQoCSn", message.Id);
                    Assert.Equal("message", message.Type);
                    Assert.Equal("claude-3-7-sonnet-20250219", message.Model);
                    Assert.NotEmpty(message.Content!);
                    Assert.Equal("assistant", message.Role);
                    Assert.Equal(
                        "1 2 3 4 5\n6 7 8 9 10\n11 12 13 14 15\n16 17 18 19 20",
                        message.Content![0].Text);
                }),
            (
                "stream transformation with parallel multiple tool calls",
                "llm-parallel_multiple_tool.stream.jsonl",
                "anthropic-parallel_multiple_tool.stream.jsonl",
                104,
                message =>
                {
                    Assert.Equal("chatcmpl-C2WBYGbjjGZj4CJNJI1FSlzO8U4vj", message.Id);
                    Assert.Equal("message", message.Type);
                    Assert.Equal("gpt-4o-2024-11-20", message.Model);
                    Assert.NotEmpty(message.Content!);
                    Assert.Equal("assistant", message.Role);
                    Assert.Equal("tool_use", message.StopReason);
                    Assert.Equal(2, message.Content!.Count);
                    Assert.Equal("tool_use", message.Content![0].Type);
                    Assert.Equal("call_tooG2dAMZaICWBfsYU5LYyvs", message.Content[0].Id);
                    Assert.Equal("get_user_city", message.Content[0].Name);
                    Assert.Equal("123", InputString(message.Content[0], "user_id"));
                    Assert.Equal("tool_use", message.Content[1].Type);
                    Assert.Equal("call_Ul0yUvKCpLfl5c32FHPcASEB", message.Content[1].Id);
                    Assert.Equal("get_user_language", message.Content[1].Name);
                    Assert.Equal("123", InputString(message.Content[1], "user_id"));
                }),
            (
                "stream transformation with thinking content and parallel tool calls",
                "llm-think.stream.jsonl",
                "anthropic-think.stream.jsonl",
                587,
                message =>
                {
                    Assert.Equal("msg_bdrk_01DDaPSX8bJqM5dRkdv32TkC", message.Id);
                    Assert.Equal("message", message.Type);
                    Assert.Equal("claude-sonnet-4-20250514", message.Model);
                    Assert.NotEmpty(message.Content!);
                    Assert.Equal("assistant", message.Role);
                    Assert.Equal("tool_use", message.StopReason);
                    Assert.Equal(4, message.Content!.Count);
                    Assert.Equal("thinking", message.Content[0].Type);
                    Assert.StartsWith("The user is asking for the weather", message.Content[0].Thinking);
                    Assert.Equal("text", message.Content[1].Type);
                    Assert.Contains("I'll help you get the weather", message.Content[1].Text);
                    Assert.Equal("tool_use", message.Content[2].Type);
                    Assert.Equal("toolu_bdrk_01RjxXDSvxn69XRfWLjn6Sur", message.Content[2].Id);
                    Assert.Equal("get_coordinates", message.Content[2].Name);
                    Assert.Equal("San Francisco, CA", InputString(message.Content[2], "location"));
                    Assert.Equal("tool_use", message.Content[3].Type);
                    Assert.Equal("toolu_bdrk_01E6Gr52e4i9TLwsDn8Sgimg", message.Content[3].Id);
                    Assert.Equal("get_temperature_unit", message.Content[3].Name);
                    Assert.Equal("United States", InputString(message.Content[3], "country"));
                }),
            (
                "stream transformation with OpenRouter content and tool call",
                "or-tool.stream.jsonl",
                "anthropic-or-tool.stream.jsonl",
                18810,
                message =>
                {
                    Assert.Equal("gen-1761365834-YlzLUYrcuUQ4OsjtP1qS", message.Id);
                    Assert.Equal("message", message.Type);
                    Assert.Equal("minimax/minimax-m2:free", message.Model);
                    Assert.NotEmpty(message.Content!);
                    Assert.Equal("assistant", message.Role);
                    Assert.Equal("tool_use", message.StopReason);
                    Assert.Equal(2, message.Content!.Count);
                    Assert.Equal("text", message.Content[0].Type);
                    Assert.Contains("代码Review结果", message.Content[0].Text);
                    Assert.Equal("tool_use", message.Content[1].Type);
                    Assert.Equal("call_function_6091710012_1", message.Content[1].Id);
                    Assert.Equal("TodoWrite", message.Content[1].Name);
                    Assert.True(message.Content[1].Input!.Value.TryGetProperty("todos", out _));
                }),
        };

        foreach (var testCase in cases)
        {
            var responses = LoadLlmResponses(testCase.InputFile);
            var expectedEvents = LoadStreamChunks(testCase.ExpectedFile);

            var (rawEvents, parsedEvents) = CollectRaw(responses);

            Assert.Equal(expectedEvents.Count, rawEvents.Count);

            for (var i = 0; i < expectedEvents.Count; i++)
            {
                Assert.Equal(expectedEvents[i].Type, rawEvents[i].Type);

                var expectedEvent = LlmJson.Deserialize<AnthropicStreamEvent>(expectedEvents[i].Data)!;
                var actualEvent = parsedEvents[i];

                Assert.Equal(expectedEvent.Type, actualEvent.Type);

                switch (expectedEvent.Type)
                {
                    case "message_start":
                        Assert.NotNull(expectedEvent.Message);
                        Assert.NotNull(actualEvent.Message);
                        Assert.Equal(expectedEvent.Message!.Id, actualEvent.Message!.Id);
                        Assert.Equal(expectedEvent.Message.Model, actualEvent.Message.Model);
                        Assert.Equal(expectedEvent.Message.Role, actualEvent.Message.Role);
                        if (expectedEvent.Message.Usage is not null && actualEvent.Message.Usage is not null)
                        {
                            Assert.Equal(expectedEvent.Message.Usage.InputTokens, actualEvent.Message.Usage.InputTokens);
                            Assert.Equal(expectedEvent.Message.Usage.OutputTokens, actualEvent.Message.Usage.OutputTokens);
                        }

                        break;
                    case "content_block_start":
                        Assert.NotNull(expectedEvent.ContentBlock);
                        Assert.NotNull(actualEvent.ContentBlock);
                        Assert.Equal(expectedEvent.ContentBlock!.Type, actualEvent.ContentBlock!.Type);
                        if (expectedEvent.ContentBlock.Type == "tool_use")
                        {
                            Assert.Equal(expectedEvent.ContentBlock.Id, actualEvent.ContentBlock.Id);
                            if (expectedEvent.ContentBlock.Name is not null
                                && actualEvent.ContentBlock.Name is not null)
                            {
                                Assert.Equal(expectedEvent.ContentBlock.Name, actualEvent.ContentBlock.Name);
                            }
                        }

                        break;
                    case "content_block_delta":
                        Assert.NotNull(expectedEvent.Delta);
                        Assert.NotNull(actualEvent.Delta);
                        AssertSameDelta(expectedEvent.Delta!, actualEvent.Delta!);
                        break;
                    case "content_block_stop":
                        Assert.Equal(expectedEvent.Index, actualEvent.Index);
                        break;
                    case "message_delta":
                        Assert.NotNull(expectedEvent.Delta);
                        Assert.NotNull(actualEvent.Delta);
                        Assert.Equal(expectedEvent.Delta!.StopReason, actualEvent.Delta!.StopReason);
                        AssertSameDelta(expectedEvent.Delta, actualEvent.Delta);
                        if (expectedEvent.Usage is not null && actualEvent.Usage is not null)
                        {
                            Assert.Equal(testCase.InputTokens, actualEvent.Usage.InputTokens);
                            Assert.Equal(expectedEvent.Usage.OutputTokens, actualEvent.Usage.OutputTokens);
                        }

                        break;
                    case "message_stop":
                        break;
                }
            }

            var aggregated = Transformer.AggregateStreamChunks(rawEvents, out _);
            var aggregatedMessage = LlmJson.Deserialize<AnthropicMessage>(aggregated)!;

            testCase.Validate(aggregatedMessage);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Dictionary<string, JsonElement> Metadata(string key, string value)
        => new(StringComparer.Ordinal) { [key] = JsonSerializer.SerializeToElement(value) };

    private static Annotation UrlAnnotation(string url, string title)
        => new() { Type = "url_citation", UrlCitation = new UrlCitation { Url = url, Title = title } };

    private static LlmResponse ReadToolChunk(
        string? id,
        string? type,
        string? name,
        string arguments) => new()
        {
            Id = "msg_read_pages_stream",
            Object = "chat.completion.chunk",
            Model = "claude-sonnet-4-6",
            Choices =
        [
            new Choice
            {
                Index = 0,
                Delta = new Message
                {
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Index = 0,
                            Id = id,
                            Type = type,
                            Function = new FunctionCall { Name = name ?? string.Empty, Arguments = arguments },
                        },
                    ],
                },
            },
        ],
        };

    private static string InputString(AnthropicMessageContentBlock block, string property)
    {
        Assert.NotNull(block.Input);
        return block.Input!.Value.GetProperty(property).GetString()!;
    }

    private static List<AnthropicStreamEvent> Collect(IStream<LlmResponse> source)
    {
        var (_, parsed) = CollectRaw(source);
        return parsed;
    }

    private static List<AnthropicStreamEvent> Collect(IReadOnlyList<LlmResponse> source)
    {
        using var stream = new SliceStream<LlmResponse>(source);
        return Collect(stream);
    }

    private static (List<StreamEvent> Raw, List<AnthropicStreamEvent> Parsed) CollectRaw(IStream<LlmResponse> source)
    {
        var (raw, parsed, err) = CollectRawWithError(source);
        Assert.Null(err);
        return (raw, parsed);
    }

    private static (List<StreamEvent> Raw, List<AnthropicStreamEvent> Parsed) CollectRaw(
        IReadOnlyList<LlmResponse> source)
    {
        using var stream = new SliceStream<LlmResponse>(source);
        return CollectRaw(stream);
    }

    private static (List<AnthropicStreamEvent> Events, Exception? Err) CollectWithError(IStream<LlmResponse> source)
    {
        var (_, parsed, err) = CollectRawWithError(source);
        return (parsed, err);
    }

    private static (List<AnthropicStreamEvent> Events, Exception? Err) CollectWithError(
        IReadOnlyList<LlmResponse> source)
    {
        using var stream = new SliceStream<LlmResponse>(source);
        return CollectWithError(stream);
    }

    private static (List<StreamEvent> Raw, List<AnthropicStreamEvent> Parsed, Exception? Err) CollectRawWithError(
        IStream<LlmResponse> source)
    {
        using var stream = Transformer.TransformStream(source);
        var raw = new List<StreamEvent>();
        var parsed = new List<AnthropicStreamEvent>();
        while (stream.Next())
        {
            var current = stream.Current!;
            raw.Add(current);
            parsed.Add(LlmJson.Deserialize<AnthropicStreamEvent>(current.Data)!);
        }

        return (raw, parsed, stream.Err);
    }

    private static int Count(List<AnthropicStreamEvent> events, string type)
        => events.Count(e => e.Type == type);

    private static void AssertCitationsDeltaBeforeContentBlockStop(
        List<AnthropicStreamEvent> events,
        List<AnthropicTextCitation> expected)
    {
        var citationIndexes = new List<int>();
        var citations = new List<AnthropicTextCitation>();
        var stopIndex = -1;

        for (var i = 0; i < events.Count; i++)
        {
            var streamEvent = events[i];
            if (streamEvent.Type == "content_block_delta" && streamEvent.Delta is { Type: "citations_delta" })
            {
                citationIndexes.Add(i);
                Assert.NotNull(streamEvent.Delta.Citation);
                var citation = streamEvent.Delta.Citation!;
                citations.Add(citation);
                Assert.Null(citation.EncryptedIndex);
                Assert.Null(citation.CitedText);
            }

            if (streamEvent.Type == "content_block_stop"
                && streamEvent.Index == 0
                && stopIndex == -1)
            {
                stopIndex = i;
            }
        }

        Assert.Equal(expected.Count, citationIndexes.Count);
        Assert.NotEqual(-1, stopIndex);
        foreach (var index in citationIndexes)
        {
            Assert.True(index < stopIndex);
        }

        Assert.Equal(expected.Count, citations.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Type, citations[i].Type);
            Assert.Equal(expected[i].Url, citations[i].Url);
            Assert.Equal(expected[i].Title, citations[i].Title);
        }
    }

    private static void AssertSameDelta(AnthropicStreamDelta expected, AnthropicStreamDelta actual)
    {
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.PartialJson, actual.PartialJson);
        Assert.Equal(expected.Thinking, actual.Thinking);
        Assert.Equal(expected.StopReason, actual.StopReason);
        Assert.Equal(expected.StopSequence, actual.StopSequence);

        // Signatures are random or provider-generated; the reference ignores
        // them in the stream comparison as well.
        if (expected.Citation is null)
        {
            Assert.Null(actual.Citation);
            return;
        }

        Assert.NotNull(actual.Citation);
        Assert.Equal(expected.Citation!.Type, actual.Citation!.Type);
        Assert.Equal(expected.Citation.Url, actual.Citation.Url);
        Assert.Equal(expected.Citation.Title, actual.Citation.Title);
        Assert.Equal(expected.Citation.EncryptedIndex, actual.Citation.EncryptedIndex);
        Assert.Equal(expected.Citation.CitedText, actual.Citation.CitedText);
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
