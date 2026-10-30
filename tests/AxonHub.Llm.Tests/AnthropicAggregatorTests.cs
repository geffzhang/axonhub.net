using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Anthropic;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Anthropic stream aggregator
/// (llm/transformer/anthropic/aggregator.go, aggregator_test.go) and the
/// usage conversion it exposes (usage.go, usage_test.go).
/// </summary>
public sealed class AnthropicAggregatorTests
{
    private static readonly string TestDataRoot =
        Path.Combine(AppContext.BaseDirectory, "testdata", "anthropic");

    // ------------------------------------------------------------------
    // Basic aggregation (TestAggregateStreamChunks)
    // ------------------------------------------------------------------

    [Fact]
    public void EmptyChunksAreRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => AnthropicAggregator.AggregateStreamChunks(
                Array.Empty<StreamEvent>(),
                AnthropicPlatformType.Direct,
                out _));

        Assert.Contains("empty stream chunks", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleChunkAggregatesText()
    {
        var message = Aggregate(
        [
            """
            {
                "type": "message_start",
                "message": {
                    "id": "msg_123",
                    "type": "message",
                    "role": "assistant",
                    "content": [],
                    "model": "claude-3-sonnet-20240229"
                }
            }
            """,
            """
            {
                "type": "content_block_delta",
                "index": 0,
                "delta": {
                    "type": "text_delta",
                    "text": "Hello!"
                }
            }
            """,
            """
            {
                "type": "message_delta",
                "delta": {
                    "stop_reason": "end_turn"
                },
                "usage": {
                    "input_tokens": 10,
                    "output_tokens": 5
                }
            }
            """,
        ]);

        Assert.NotNull(message.Content);
        Assert.NotEmpty(message.Content!);
        Assert.Equal("Hello!", message.Content![0].Text);
        Assert.Equal("assistant", message.Role);
    }

    [Fact]
    public void MultipleContentChunksAggregateText()
    {
        var message = Aggregate(
        [
            """
            {
                "type": "message_start",
                "message": {
                    "id": "msg_456",
                    "type": "message",
                    "role": "assistant",
                    "content": [],
                    "model": "claude-3-sonnet-20240229"
                }
            }
            """,
            """
            {
                "type": "content_block_delta",
                "index": 0,
                "delta": {
                    "type": "text_delta",
                    "text": "Hello"
                }
            }
            """,
            """
            {
                "type": "content_block_delta",
                "index": 0,
                "delta": {
                    "type": "text_delta",
                    "text": ", world!"
                }
            }
            """,
            """
            {
                "type": "message_delta",
                "delta": {
                    "stop_reason": "end_turn"
                }
            }
            """,
        ]);

        Assert.NotNull(message.Content);
        Assert.NotEmpty(message.Content!);
        Assert.Equal("Hello, world!", message.Content![0].Text);
        Assert.Equal("assistant", message.Role);
    }

    // ------------------------------------------------------------------
    // ResponseMeta (TestAggregateStreamChunks_SetsCompletedForStopReason)
    // ------------------------------------------------------------------

    [Fact]
    public void SetsCompletedForStopReason()
    {
        var chunks = new[]
        {
            Chunk("""{"type":"message_start","message":{"id":"msg_stop","type":"message","role":"assistant","content":[],"model":"claude-sonnet-4-6"}}"""),
            Chunk("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"done"}}"""),
            Chunk("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":10,"output_tokens":1}}"""),
        };

        _ = AnthropicAggregator.AggregateStreamChunks(chunks, AnthropicPlatformType.Direct, out var meta);

        Assert.Equal("msg_stop", meta.Id);
        Assert.True(meta.Completed);
    }

    [Fact]
    public void DoesNotCompleteWithoutMessageStart()
    {
        var chunks = new[]
        {
            Chunk("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"partial"}}"""),
            Chunk("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":10,"output_tokens":1}}"""),
        };

        _ = AnthropicAggregator.AggregateStreamChunks(chunks, AnthropicPlatformType.Direct, out var meta);

        Assert.Equal("msg_unknown", meta.Id);
        Assert.False(meta.Completed);
    }

    // ------------------------------------------------------------------
    // Edge cases (TestAggregateStreamChunks_EdgeCases)
    // ------------------------------------------------------------------

    [Fact]
    public void EdgeCases()
    {
        var cases = new (string Name, string[] Chunks, Action<AnthropicMessage> Validate)[]
        {
            (
                "chunks with invalid JSON",
                [
                    """
                    {
                        "type": "message_start",
                        "message": {
                            "id": "msg_123",
                            "type": "message",
                            "role": "assistant",
                            "content": [],
                            "model": "claude-3-sonnet-20240229"
                        }
                    }
                    """,
                    "{invalid json}",
                    """
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {
                            "type": "text_delta",
                            "text": "Hello"
                        }
                    }
                    """,
                    """
                    {
                        "type": "message_delta",
                        "delta": {
                            "stop_reason": "end_turn"
                        }
                    }
                    """,
                ],
                message =>
                {
                    Assert.NotEmpty(message.Content!);
                    Assert.Equal("Hello", message.Content![0].Text);
                }),
            (
                "chunks with unknown event types",
                [
                    """
                    {
                        "type": "message_start",
                        "message": {
                            "id": "msg_123",
                            "type": "message",
                            "role": "assistant",
                            "content": [],
                            "model": "claude-3-sonnet-20240229"
                        }
                    }
                    """,
                    """
                    {
                        "type": "unknown_event",
                        "some_field": "value"
                    }
                    """,
                    """
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {
                            "type": "text_delta",
                            "text": "Hello"
                        }
                    }
                    """,
                ],
                message =>
                {
                    Assert.NotEmpty(message.Content!);
                    Assert.Equal("Hello", message.Content![0].Text);
                }),
            (
                "chunks missing message_start",
                [
                    """
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {
                            "type": "text_delta",
                            "text": "Hello"
                        }
                    }
                    """,
                    """
                    {
                        "type": "message_delta",
                        "delta": {
                            "stop_reason": "end_turn"
                        }
                    }
                    """,
                ],
                message =>
                {
                    Assert.Equal("msg_unknown", message.Id);
                    Assert.Equal("message", message.Type);
                    Assert.Equal("assistant", message.Role);
                    Assert.Equal("claude-3-sonnet-20240229", message.Model);
                    Assert.Equal("end_turn", message.StopReason);
                }),
            (
                "chunks with all event types",
                [
                    """
                    {
                        "type": "message_start",
                        "message": {
                            "id": "msg_complete",
                            "type": "message",
                            "role": "assistant",
                            "content": [],
                            "model": "claude-3-sonnet-20240229",
                            "usage": {"input_tokens": 5, "output_tokens": 0}
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_start",
                        "index": 0,
                        "content_block": {
                            "type": "text",
                            "text": ""
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {
                            "type": "text_delta",
                            "text": "Complete"
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_stop",
                        "index": 0
                    }
                    """,
                    """
                    {
                        "type": "message_delta",
                        "delta": {
                            "stop_reason": "end_turn"
                        },
                        "usage": {"output_tokens": 8}
                    }
                    """,
                    """
                    {
                        "type": "message_stop"
                    }
                    """,
                ],
                message =>
                {
                    Assert.NotEmpty(message.Content!);
                    Assert.Equal("Complete", message.Content![0].Text);
                    Assert.Equal("msg_complete", message.Id);
                    Assert.Equal("end_turn", message.StopReason);
                    Assert.NotNull(message.Usage);
                    Assert.Equal(5, message.Usage!.InputTokens);
                    Assert.Equal(8, message.Usage.OutputTokens);
                }),
            (
                "chunks with detailed usage information",
                [
                    """{"type": "message_start", "message": {"id": "msg_detailed_usage", "type": "message", "role": "assistant", "content": [], "model": "claude-3-sonnet-20240229", "usage": {"input_tokens": 100, "output_tokens": 0, "cache_creation_input_tokens": 20, "cache_read_input_tokens": 50}}}""",
                    """{"type": "content_block_start", "index": 0, "content_block": {"type": "text", "text": ""}}""",
                    """{"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": "Response with detailed usage"}}""",
                    """{"type": "message_delta", "delta": {"stop_reason": "end_turn"}, "usage": {"output_tokens": 25, "cache_creation_input_tokens": 20, "cache_read_input_tokens": 50}}""",
                ],
                message =>
                {
                    Assert.NotEmpty(message.Content!);
                    Assert.Equal("Response with detailed usage", message.Content![0].Text);
                    Assert.Equal("msg_detailed_usage", message.Id);
                    Assert.Equal("end_turn", message.StopReason);
                    Assert.NotNull(message.Usage);
                    Assert.Equal(100, message.Usage!.InputTokens);
                    Assert.Equal(25, message.Usage.OutputTokens);
                    Assert.Equal(50, message.Usage.CacheReadInputTokens);
                    Assert.Equal(20, message.Usage.CacheCreationInputTokens);
                }),
            (
                "chunks with usage but no cache tokens",
                [
                    """{"type": "message_start", "message": {"id": "msg_no_cache_stream", "type": "message", "role": "assistant", "content": [], "model": "claude-3-sonnet-20240229", "usage": {"input_tokens": 80, "output_tokens": 1, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0}}}""",
                    """{"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": "No cache response"}}""",
                    """{"type": "message_delta", "delta": {"stop_reason": "end_turn"}, "usage": {"output_tokens": 40, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0}}""",
                ],
                message =>
                {
                    Assert.NotEmpty(message.Content!);
                    Assert.Equal("No cache response", message.Content![0].Text);
                    Assert.Equal("msg_no_cache_stream", message.Id);
                    Assert.Equal("end_turn", message.StopReason);
                    Assert.NotNull(message.Usage);
                    Assert.Equal(80, message.Usage!.InputTokens);
                    Assert.Equal(40, message.Usage.OutputTokens);
                    Assert.Equal(0, message.Usage.CacheCreationInputTokens);
                    Assert.Equal(0, message.Usage.CacheReadInputTokens);
                }),
            (
                "chunks with thinking blocks",
                [
                    """
                    {
                        "type": "message_start",
                        "message": {
                            "id": "msg_thinking",
                            "type": "message",
                            "role": "assistant",
                            "content": [],
                            "model": "claude-3-sonnet-20240229"
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_start",
                        "index": 0,
                        "content_block": {
                            "type": "thinking",
                            "thinking": "Let me think about this..."
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {
                            "type": "thinking_delta",
                            "thinking": " some more"
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_start",
                        "index": 1,
                        "content_block": {
                            "type": "text",
                            "text": ""
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_delta",
                        "index": 1,
                        "delta": {
                            "type": "text_delta",
                            "text": "Final answer"
                        }
                    }
                    """,
                    """
                    {
                        "type": "message_delta",
                        "delta": {
                            "stop_reason": "end_turn"
                        }
                    }
                    """,
                ],
                message =>
                {
                    Assert.NotNull(message.Content);
                    Assert.Equal(2, message.Content!.Count);
                    Assert.Equal("thinking", message.Content[0].Type);
                    Assert.Equal("Let me think about this... some more", message.Content[0].Thinking);
                    Assert.Equal("text", message.Content[1].Type);
                    Assert.Equal("Final answer", message.Content[1].Text);
                }),
            (
                "chunks with tool use",
                [
                    """
                    {
                        "type": "message_start",
                        "message": {
                            "id": "msg_tool",
                            "type": "message",
                            "role": "assistant",
                            "content": [],
                            "model": "claude-3-sonnet-20240229"
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_start",
                        "index": 0,
                        "content_block": {
                            "type": "text",
                            "text": ""
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {
                            "type": "text_delta",
                            "text": "I'll use a tool"
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_start",
                        "index": 1,
                        "content_block": {
                            "type": "tool_use",
                            "id": "tool_123",
                            "name": "calculator",
                            "input": "{\"expression\": \"2+2\"}"
                        }
                    }
                    """,
                    """
                    {
                        "type": "message_delta",
                        "delta": {
                            "stop_reason": "tool_use"
                        }
                    }
                    """,
                ],
                message =>
                {
                    Assert.NotEmpty(message.Content!);
                    Assert.Equal("tool_use", message.StopReason);
                    Assert.Equal(2, message.Content!.Count);
                    Assert.Equal("text", message.Content[0].Type);
                    Assert.Equal("I'll use a tool", message.Content[0].Text);
                    Assert.Equal("tool_use", message.Content[1].Type);
                    Assert.Equal("tool_123", message.Content[1].Id);
                    // content_block_start inputs are reset; the input is rebuilt
                    // only from input_json_delta events.
                    Assert.Null(message.Content[1].Input);
                }),
            (
                "chunks with partial JSON",
                [
                    """
                    {
                        "type": "message_start",
                        "message": {
                            "id": "msg_partial",
                            "type": "message",
                            "role": "assistant",
                            "content": [],
                            "model": "claude-3-sonnet-20240229"
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_start",
                        "index": 0,
                        "content_block": {
                            "type": "tool_use",
                            "id": "tool_456",
                            "name": "json_tool"
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {
                            "type": "input_json_delta",
                            "partial_json": "{\"key\":"
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {
                            "type": "input_json_delta",
                            "partial_json": "\"value\"}"
                        }
                    }
                    """,
                    """
                    {
                        "type": "message_delta",
                        "delta": {
                            "stop_reason": "tool_use"
                        }
                    }
                    """,
                ],
                message =>
                {
                    var content = message.Content!;
                    Assert.NotEmpty(content);
                    Assert.Equal("tool_use", message.StopReason);
                    Assert.Single(content);
                    Assert.Equal("tool_use", content[0].Type);
                    Assert.Equal("tool_456", content[0].Id);
                    Assert.NotNull(content[0].Input);
                    Assert.Equal(
                        "{\"key\":\"value\"}",
                        content[0].Input!.Value.GetRawText());
                }),
            (
                "chunks with ping events",
                [
                    """
                    {
                        "type": "message_start",
                        "message": {
                            "id": "msg_ping",
                            "type": "message",
                            "role": "assistant",
                            "content": [],
                            "model": "claude-3-sonnet-20240229"
                        }
                    }
                    """,
                    """
                    {
                        "type": "ping"
                    }
                    """,
                    """
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {
                            "type": "text_delta",
                            "text": "After ping"
                        }
                    }
                    """,
                    """
                    {
                        "type": "message_delta",
                        "delta": {
                            "stop_reason": "end_turn"
                        }
                    }
                    """,
                ],
                message =>
                {
                    Assert.NotEmpty(message.Content!);
                    Assert.Equal("After ping", message.Content![0].Text);
                }),
            (
                "chunks with signature delta",
                [
                    """
                    {
                        "type": "message_start",
                        "message": {
                            "id": "msg_sig",
                            "type": "message",
                            "role": "assistant",
                            "content": [],
                            "model": "claude-3-sonnet-20240229"
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_start",
                        "index": 0,
                        "content_block": {
                            "type": "thinking",
                            "thinking": "",
                            "signature": ""
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {
                            "type": "thinking_delta",
                            "thinking": "Thinking..."
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {
                            "type": "signature_delta",
                            "signature": "abc123"
                        }
                    }
                    """,
                    """
                    {
                        "type": "message_delta",
                        "delta": {
                            "stop_reason": "end_turn"
                        }
                    }
                    """,
                ],
                message =>
                {
                    var content = message.Content!;
                    Assert.NotEmpty(content);
                    Assert.Equal("thinking", content[0].Type);
                    Assert.Equal("Thinking...", content[0].Thinking);
                    Assert.Equal("abc123", content[0].Signature);
                }),
            (
                "chunks with multiple stop reasons",
                [
                    """
                    {
                        "type": "message_start",
                        "message": {
                            "id": "msg_multi_stop",
                            "type": "message",
                            "role": "assistant",
                            "content": [],
                            "model": "claude-3-sonnet-20240229"
                        }
                    }
                    """,
                    """
                    {
                        "type": "content_block_delta",
                        "index": 0,
                        "delta": {
                            "type": "text_delta",
                            "text": "Test"
                        }
                    }
                    """,
                    """
                    {
                        "type": "message_delta",
                        "delta": {
                            "stop_reason": "max_tokens"
                        }
                    }
                    """,
                ],
                message =>
                {
                    Assert.NotEmpty(message.Content!);
                    Assert.Equal("max_tokens", message.StopReason);
                }),
        };

        foreach (var testCase in cases)
        {
            var message = Aggregate(testCase.Chunks);
            testCase.Validate(message);
        }
    }

    // ------------------------------------------------------------------
    // Citations (TestAggregateStreamChunks_WithCitationsDelta)
    // ------------------------------------------------------------------

    [Fact]
    public void WithCitationsDelta()
    {
        var message = Aggregate(
        [
            """
            {
                "type": "message_start",
                "message": {
                    "id": "msg_citations",
                    "type": "message",
                    "role": "assistant",
                    "content": [],
                    "model": "claude-sonnet-4-6"
                }
            }
            """,
            """
            {
                "type": "content_block_start",
                "index": 0,
                "content_block": {
                    "type": "text",
                    "text": ""
                }
            }
            """,
            """
            {
                "type": "content_block_delta",
                "index": 0,
                "delta": {
                    "type": "text_delta",
                    "text": "Answer with source"
                }
            }
            """,
            """
            {
                "type": "content_block_delta",
                "index": 0,
                "delta": {
                    "type": "citations_delta",
                    "citation": {
                        "type": "url_citation",
                        "url": "https://example.com/source",
                        "title": "Example Source"
                    }
                }
            }
            """,
            """
            {
                "type": "content_block_stop",
                "index": 0
            }
            """,
            """
            {
                "type": "message_delta",
                "delta": {
                    "stop_reason": "end_turn"
                }
            }
            """,
        ]);

        Assert.NotNull(message.Content);
        Assert.Single(message.Content!);
        Assert.Equal("text", message.Content![0].Type);
        Assert.Equal("Answer with source", message.Content[0].Text);
        Assert.NotNull(message.Content[0].Citations);
        var citation = Assert.Single(message.Content[0].Citations!);
        Assert.Equal("url_citation", citation.Type);
        Assert.Equal("https://example.com/source", citation.Url);
        Assert.Equal("Example Source", citation.Title);
        Assert.Null(citation.EncryptedIndex);
    }

    // ------------------------------------------------------------------
    // Fixture aggregation (TestAggregateStreamChunks_WithTestData)
    // ------------------------------------------------------------------

    [Fact]
    public void WithTestData()
    {
        var cases = new (string StreamFile, string ResponseFile)[]
        {
            ("anthropic-stop.stream.jsonl", "anthropic-stop.response.json"),
            ("anthropic-tool.stream.jsonl", "anthropic-tool.response.json"),
            ("anthropic-think.stream.jsonl", "anthropic-think.response.json"),
        };

        foreach (var testCase in cases)
        {
            var chunks = LoadStreamChunks(testCase.StreamFile);
            Assert.NotEmpty(chunks);

            var expected = LlmJson.Deserialize<AnthropicMessage>(
                File.ReadAllBytes(Path.Combine(TestDataRoot, testCase.ResponseFile)));

            var data = AnthropicAggregator.AggregateStreamChunks(
                chunks,
                AnthropicPlatformType.Direct,
                out _);
            var actual = LlmJson.Deserialize<AnthropicMessage>(data);

            Assert.NotNull(expected);
            Assert.NotNull(actual);
            Assert.Equal(expected!.Id, actual!.Id);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Role, actual.Role);
            Assert.Equal(expected.Model, actual.Model);
            Assert.Equal(expected.StopReason, actual.StopReason);

            Assert.NotNull(expected.Content);
            Assert.NotNull(actual.Content);
            Assert.Equal(expected.Content!.Count, actual.Content!.Count);

            for (var i = 0; i < expected.Content.Count; i++)
            {
                var expectedContent = expected.Content[i];
                var actualContent = actual.Content[i];

                Assert.Equal(expectedContent.Type, actualContent.Type);

                if (expectedContent.Type == "text")
                {
                    Assert.Equal(expectedContent.Text, actualContent.Text);
                }

                if (expectedContent.Type == "thinking")
                {
                    Assert.Equal(expectedContent.Thinking, actualContent.Thinking);
                    // Signatures are generated or provider-supplied and are
                    // ignored by the reference fixture comparison as well.
                }

                if (expectedContent.Type == "tool_use")
                {
                    Assert.Equal(expectedContent.Id, actualContent.Id);
                    Assert.Equal(expectedContent.Name, actualContent.Name);

                    if (expectedContent.Input is { } expectedInput && actualContent.Input is { } actualInput)
                    {
                        Assert.Equal(CanonicalJson(expectedInput), CanonicalJson(actualInput));
                    }
                }
            }

            if (expected.Usage is not null)
            {
                Assert.NotNull(actual.Usage);
                Assert.Equal(expected.Usage!.InputTokens, actual.Usage!.InputTokens);
                Assert.Equal(expected.Usage.OutputTokens, actual.Usage.OutputTokens);
                Assert.Equal(expected.Usage.CacheCreationInputTokens, actual.Usage.CacheCreationInputTokens);
                Assert.Equal(expected.Usage.CacheReadInputTokens, actual.Usage.CacheReadInputTokens);
            }
        }
    }

    // ------------------------------------------------------------------
    // convertToLlmUsage (Test_convertUsage)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertToLlmUsageMatchesReference()
    {
        var cases = new (string Name, AnthropicUsage Usage, AnthropicPlatformType Platform, long Prompt, long Completion, long Cached, long WriteCached)[]
        {
            (
                "base case - Anthropic official",
                new AnthropicUsage
                {
                    InputTokens = 100,
                    OutputTokens = 50,
                    CacheCreationInputTokens = 20,
                    CacheReadInputTokens = 30,
                    ServiceTier = "standard",
                },
                AnthropicPlatformType.Direct,
                150, 50, 30, 20),
            (
                "cache read tokens greater than input tokens - Anthropic official",
                new AnthropicUsage
                {
                    InputTokens = 100,
                    OutputTokens = 50,
                    CacheCreationInputTokens = 20,
                    CacheReadInputTokens = 150,
                    ServiceTier = "standard",
                },
                AnthropicPlatformType.Direct,
                270, 50, 150, 20),
            (
                "zero values - Anthropic official",
                new AnthropicUsage { InputTokens = 0, OutputTokens = 0, ServiceTier = string.Empty },
                AnthropicPlatformType.Direct,
                0, 0, 0, 0),
            (
                "moonshot cached tokens conversion - Anthropic official",
                new AnthropicUsage
                {
                    InputTokens = 100,
                    OutputTokens = 50,
                    CachedTokens = 75,
                    ServiceTier = "standard",
                },
                AnthropicPlatformType.Direct,
                175, 50, 75, 0),
            (
                "only input and output tokens - Anthropic official",
                new AnthropicUsage { InputTokens = 100, OutputTokens = 50 },
                AnthropicPlatformType.Direct,
                100, 50, 0, 0),
            (
                "only cache creation tokens - Anthropic official",
                new AnthropicUsage { CacheCreationInputTokens = 100, OutputTokens = 50 },
                AnthropicPlatformType.Direct,
                100, 50, 0, 100),
            (
                "only cache read tokens - Anthropic official",
                new AnthropicUsage { CacheReadInputTokens = 100, OutputTokens = 50 },
                AnthropicPlatformType.Direct,
                100, 50, 100, 0),
            (
                "large numbers - Anthropic official",
                new AnthropicUsage
                {
                    InputTokens = 1000000,
                    OutputTokens = 500000,
                    CacheCreationInputTokens = 200000,
                    CacheReadInputTokens = 300000,
                    ServiceTier = "priority",
                },
                AnthropicPlatformType.Direct,
                1500000, 500000, 300000, 200000),
            (
                "empty usage struct - Anthropic official",
                new AnthropicUsage(),
                AnthropicPlatformType.Direct,
                0, 0, 0, 0),
            (
                "moonshot with cache creation tokens - cached tokens ignored",
                new AnthropicUsage
                {
                    InputTokens = 100,
                    OutputTokens = 50,
                    CachedTokens = 75,
                    ServiceTier = "standard",
                },
                AnthropicPlatformType.Moonshot,
                100, 50, 75, 0),
            (
                "only output tokens - Anthropic official",
                new AnthropicUsage { OutputTokens = 50 },
                AnthropicPlatformType.Direct,
                0, 50, 0, 0),
            (
                "Moonshot - cache read tokens included in input tokens",
                new AnthropicUsage
                {
                    InputTokens = 100,
                    OutputTokens = 50,
                    CacheCreationInputTokens = 20,
                    CacheReadInputTokens = 30,
                    ServiceTier = "standard",
                },
                AnthropicPlatformType.Moonshot,
                100, 50, 30, 20),
            (
                "cache creation with ttl - Anthropic official",
                new AnthropicUsage
                {
                    InputTokens = 100,
                    OutputTokens = 50,
                    CacheCreationInputTokens = 20,
                    CacheReadInputTokens = 30,
                    CacheCreation = new AnthropicCacheCreation
                    {
                        Ephemeral5mInputTokens = 10,
                        Ephemeral1hInputTokens = 10,
                    },
                    ServiceTier = "standard",
                },
                AnthropicPlatformType.Direct,
                150, 50, 30, 20),
            (
                "Moonshot - negative input tokens with cache discount",
                new AnthropicUsage
                {
                    InputTokens = -126648,
                    OutputTokens = 52,
                    CacheReadInputTokens = 126976,
                    ServiceTier = "standard",
                },
                AnthropicPlatformType.Moonshot,
                127304, 52, 126976, 0),
            (
                "Moonshot - future format where InputTokens doesn't include cached",
                new AnthropicUsage
                {
                    InputTokens = 100,
                    OutputTokens = 50,
                    CacheReadInputTokens = 150,
                    ServiceTier = "standard",
                },
                AnthropicPlatformType.Moonshot,
                250, 50, 150, 0),
        };

        foreach (var testCase in cases)
        {
            var actual = AnthropicAggregator.ConvertToLlmUsage(testCase.Usage, testCase.Platform);

            Assert.NotNull(actual);
            Assert.Equal(testCase.Prompt, actual!.PromptTokens);
            Assert.Equal(testCase.Completion, actual.CompletionTokens);
            Assert.Equal(testCase.Prompt + testCase.Completion, actual.TotalTokens);

            if (testCase.Cached > 0 || testCase.WriteCached > 0)
            {
                Assert.NotNull(actual.PromptTokensDetails);
                Assert.Equal(testCase.Cached, actual.PromptTokensDetails!.CachedTokens);
                Assert.Equal(testCase.WriteCached, actual.PromptTokensDetails.WriteCachedTokens);
            }
        }

        Assert.Null(AnthropicAggregator.ConvertToLlmUsage(null, AnthropicPlatformType.Direct));
    }

    [Fact]
    public void ConvertToLlmUsageKeepsCacheCreationTtl()
    {
        var usage = new AnthropicUsage
        {
            InputTokens = 100,
            OutputTokens = 50,
            CacheCreationInputTokens = 20,
            CacheReadInputTokens = 30,
            CacheCreation = new AnthropicCacheCreation
            {
                Ephemeral5mInputTokens = 10,
                Ephemeral1hInputTokens = 10,
            },
        };

        var actual = AnthropicAggregator.ConvertToLlmUsage(usage, AnthropicPlatformType.Direct);

        Assert.NotNull(actual);
        Assert.NotNull(actual!.PromptTokensDetails);
        Assert.Equal(10, actual.PromptTokensDetails!.WriteCached5MinTokens);
        Assert.Equal(10, actual.PromptTokensDetails.WriteCached1HourTokens);
    }

    // ------------------------------------------------------------------
    // Difference-ledger locks
    // ------------------------------------------------------------------

    [Fact]
    public void SkipsNegativeContentBlockIndex()
    {
        // Go evaluates contentBlocks[index] and panics on a negative index;
        // the port skips the delta instead (difference ledger C053).
        var message = Aggregate(
        [
            """
            {
                "type": "message_start",
                "message": {
                    "id": "msg_negative",
                    "type": "message",
                    "role": "assistant",
                    "content": [],
                    "model": "claude-3-sonnet-20240229"
                }
            }
            """,
            """
            {
                "type": "content_block_delta",
                "index": -1,
                "delta": {
                    "type": "text_delta",
                    "text": "ignored"
                }
            }
            """,
            """
            {
                "type": "message_delta",
                "delta": {
                    "stop_reason": "end_turn"
                }
            }
            """,
        ]);

        var content = message.Content!;
        Assert.Single(content);
        Assert.Equal("text", content[0].Type);
        Assert.Equal(string.Empty, content[0].Text);
        Assert.Equal("end_turn", message.StopReason);
    }

    [Fact]
    public void InvalidBufferedToolInputThrowsInsteadOfReturningAMessage()
    {
        // Go's content_block_stop jsonrepair writes to a copy of the block (a
        // no-op), so the invalid RawMessage survives until json.Marshal fails;
        // the port omits the dead repair and fails while materializing the
        // buffered input (difference ledger C054).
        var chunks = new List<StreamEvent>
        {
            Chunk(
                """
                {
                    "type": "message_start",
                    "message": {
                        "id": "msg_bad_input",
                        "type": "message",
                        "role": "assistant",
                        "content": [],
                        "model": "claude-3-sonnet-20240229"
                    }
                }
                """),
            Chunk(
                """
                {
                    "type": "content_block_start",
                    "index": 0,
                    "content_block": {
                        "type": "tool_use",
                        "id": "tool_bad",
                        "name": "write",
                        "input": {}
                    }
                }
                """),
            Chunk(
                """
                {
                    "type": "content_block_delta",
                    "index": 0,
                    "delta": {
                        "type": "input_json_delta",
                        "partial_json": "{\"content\":\"unfinished"
                    }
                }
                """),
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => AnthropicAggregator.AggregateStreamChunks(
                chunks,
                AnthropicPlatformType.Direct,
                out _));

        Assert.IsAssignableFrom<JsonException>(error.InnerException);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AnthropicMessage Aggregate(string[] chunksJson)
    {
        var chunks = chunksJson.Select(Chunk).ToList();
        var data = AnthropicAggregator.AggregateStreamChunks(
            chunks,
            AnthropicPlatformType.Direct,
            out _);
        var message = LlmJson.Deserialize<AnthropicMessage>(data);
        Assert.NotNull(message);
        return message!;
    }

    private static StreamEvent Chunk(string json) => new()
    {
        Data = Encoding.UTF8.GetBytes(json),
    };

    private static string CanonicalJson(JsonElement element)
        => GoJson.CompactElement(element).GetRawText();

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
