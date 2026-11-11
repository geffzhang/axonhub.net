using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.Shared;
using LlmFunctionCall = AxonHub.Llm.FunctionCall;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the native Gemini inbound transformer
/// (llm/transformer/gemini/inbound_stream.go and its inbound_stream_test.go).
/// Every test mirrors a reference behaviour one-for-one; the testdata cases
/// replay the reference's llm-*.stream.jsonl fixtures through TransformStream
/// and compare each emitted event against the gemini-*.stream.jsonl goldens.
/// </summary>
public sealed class GeminiInboundTransformerTests
{
    private static readonly string TestDataRoot = Path.Combine(AppContext.BaseDirectory, "testdata", "gemini");

    private static GeminiInboundTransformer NewTransformer() => new();

    private static StreamEvent Chunk(GenerateContentResponse resp) => new() { Data = GoJson.Marshal(resp) };

    private static Candidate SingleCandidate(GenerateContentResponse resp)
    {
        var candidates = resp.Candidates;
        Assert.NotNull(candidates);
        return Assert.Single(candidates);
    }

    private static List<Part> PartsOf(GenerateContentResponse resp)
    {
        var candidate = SingleCandidate(resp);
        Assert.NotNull(candidate.Content);
        // Absent on the wire deserializes to null; Go's require.Empty accepts
        // the nil slice, so treat null and empty alike.
        return candidate.Content!.Parts ?? [];
    }

    // ------------------------------------------------------------ TransformStreamChunk

    [Fact]
    public void TransformStreamChunk_NilResponseReturnsNull()
    {
        Assert.Null(NewTransformer().TransformStreamChunk(null));
    }

    [Fact]
    public void TransformStreamChunk_DoneMarkerReturnsNull()
    {
        Assert.Null(NewTransformer().TransformStreamChunk(new LlmResponse { Object = "[DONE]" }));
    }

    [Fact]
    public void TransformStreamChunk_SimpleTextResponse()
    {
        var evt = NewTransformer().TransformStreamChunk(new LlmResponse
        {
            Id = "chatcmpl-123",
            Model = "gemini-2.0-flash",
            Object = "chat.completion.chunk",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Delta = new Message
                    {
                        Role = "assistant",
                        Content = new MessageContent { Content = "Hello, world!" },
                    },
                    FinishReason = "stop",
                },
            ],
        });

        Assert.NotNull(evt);
        Assert.NotEmpty(evt.Data);

        var resp = LlmJson.Deserialize<GenerateContentResponse>(evt.Data)!;
        Assert.Equal("chatcmpl-123", resp.ResponseId);
        var parts = PartsOf(resp);
        Assert.Equal("Hello, world!", Assert.Single(parts).Text);
    }

    [Fact]
    public void TransformStreamChunk_ReasoningContent()
    {
        var evt = NewTransformer().TransformStreamChunk(new LlmResponse
        {
            Id = "chatcmpl-456",
            Model = "gemini-2.0-flash-thinking",
            Object = "chat.completion.chunk",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Delta = new Message
                    {
                        Role = "assistant",
                        ReasoningContent = "Let me think about this...",
                        Content = new MessageContent { Content = "The answer is 42." },
                    },
                    FinishReason = "stop",
                },
            ],
        });

        Assert.NotNull(evt);

        var resp = LlmJson.Deserialize<GenerateContentResponse>(evt.Data)!;

        // Should have thinking part and text part
        var parts = PartsOf(resp);
        Assert.NotEmpty(parts);

        var hasThinking = false;
        var hasText = false;

        foreach (var part in parts)
        {
            if (part.Thought && part.Text == "Let me think about this...")
            {
                hasThinking = true;
            }

            if (!part.Thought && part.Text == "The answer is 42.")
            {
                hasText = true;
            }
        }

        Assert.True(hasThinking, "should have thinking part");
        Assert.True(hasText, "should have text part");
    }

    [Fact]
    public void TransformStreamChunk_ToolCalls()
    {
        var evt = NewTransformer().TransformStreamChunk(new LlmResponse
        {
            Id = "chatcmpl-789",
            Model = "gemini-2.0-flash",
            Object = "chat.completion.chunk",
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
                                Id = "call-123",
                                Type = "function",
                                Function = new LlmFunctionCall
                                {
                                    Name = "get_weather",
                                    Arguments = """{"location":"Tokyo"}""",
                                },
                            },
                        ],
                    },
                    FinishReason = "tool_calls",
                },
            ],
        });

        Assert.NotNull(evt);

        var resp = LlmJson.Deserialize<GenerateContentResponse>(evt.Data)!;

        var hasFunctionCall = false;

        foreach (var part in PartsOf(resp))
        {
            if (part.FunctionCall is { } fc)
            {
                hasFunctionCall = true;
                Assert.Equal("call-123", fc.Id);
                Assert.Equal("get_weather", fc.Name);
                Assert.Equal("Tokyo", fc.Args!["location"].GetString());
            }
        }

        Assert.True(hasFunctionCall, "should have function call part");
    }

    [Fact]
    public void TransformStreamChunk_Usage()
    {
        var evt = NewTransformer().TransformStreamChunk(new LlmResponse
        {
            Id = "chatcmpl-usage",
            Model = "gemini-2.0-flash",
            Object = "chat.completion.chunk",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Delta = new Message
                    {
                        Role = "assistant",
                        Content = new MessageContent { Content = "Test" },
                    },
                    FinishReason = "stop",
                },
            ],
            Usage = new Usage
            {
                PromptTokens = 10,
                CompletionTokens = 5,
                TotalTokens = 15,
            },
        });

        Assert.NotNull(evt);

        var resp = LlmJson.Deserialize<GenerateContentResponse>(evt.Data)!;
        Assert.NotNull(resp.UsageMetadata);
        Assert.Equal(10, resp.UsageMetadata.PromptTokenCount);
        Assert.Equal(5, resp.UsageMetadata.CandidatesTokenCount);
        Assert.Equal(15, resp.UsageMetadata.TotalTokenCount);
    }

    // ------------------------------------------------------------ TransformStream

    [Fact]
    public void TransformStream_TwoChunks()
    {
        LlmResponse[] responses =
        [
            new LlmResponse
            {
                Id = "chatcmpl-stream-1",
                Model = "gemini-2.0-flash",
                Object = "chat.completion.chunk",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Hello" },
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "chatcmpl-stream-1",
                Model = "gemini-2.0-flash",
                Object = "chat.completion.chunk",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Content = new MessageContent { Content = ", world!" },
                        },
                        FinishReason = "stop",
                    },
                ],
            },
        ];

        var stream = NewTransformer().TransformStream(new SliceStream<LlmResponse>(responses));

        var results = new List<StreamEvent>();
        while (stream.Next())
        {
            results.Add(stream.Current!);
        }

        Assert.Null(stream.Err);
        Assert.Equal(2, results.Count);

        var resp1 = LlmJson.Deserialize<GenerateContentResponse>(results[0].Data)!;
        Assert.Single(resp1.Candidates!);

        var resp2 = LlmJson.Deserialize<GenerateContentResponse>(results[1].Data)!;
        Assert.Single(resp2.Candidates!);
    }

    [Fact]
    public void TransformStream_AggregatesToolCallArguments()
    {
        LlmResponse[] responses =
        [
            new LlmResponse
            {
                Id = "chatcmpl-tool-agg-1",
                Model = "gemini-2.0-flash",
                Object = "chat.completion.chunk",
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
                                    Id = "call_1",
                                    Type = "function",
                                    Function = new LlmFunctionCall
                                    {
                                        Name = "calculate",
                                        Arguments = """{"expression":"15 *""",
                                    },
                                },
                            ],
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "chatcmpl-tool-agg-1",
                Model = "gemini-2.0-flash",
                Object = "chat.completion.chunk",
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
                                    Function = new LlmFunctionCall { Arguments = """ 23"}""" },
                                },
                            ],
                        },
                        FinishReason = "tool_calls",
                    },
                ],
            },
        ];

        var stream = NewTransformer().TransformStream(new SliceStream<LlmResponse>(responses));

        GenerateContentResponse? last = null;
        while (stream.Next())
        {
            last = LlmJson.Deserialize<GenerateContentResponse>(stream.Current!.Data)!;
        }

        Assert.Null(stream.Err);
        Assert.NotNull(last);

        AxonHub.Llm.Transformer.Gemini.FunctionCall? fc = null;
        foreach (var part in PartsOf(last))
        {
            if (part.FunctionCall is not null)
            {
                fc = part.FunctionCall;
                break;
            }
        }

        Assert.NotNull(fc);
        Assert.Equal("call_1", fc.Id);
        Assert.Equal("calculate", fc.Name);
        Assert.Equal("15 * 23", fc.Args!["expression"].GetString());
    }

    [Fact]
    public void TransformStream_FinishOnlyChunkWithReasoningSignature()
    {
        LlmResponse[] responses =
        [
            new LlmResponse
            {
                Id = "chatcmpl-finish-only-1",
                Model = "gemini-2.0-flash",
                Object = "chat.completion.chunk",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "OK" },
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Id = "chatcmpl-finish-only-1",
                Model = "gemini-2.0-flash",
                Object = "chat.completion.chunk",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            // EncodeGeminiThoughtSignature is a passthrough in the port.
                            ReasoningSignature = GeminiSignatures.EncodeGeminiThoughtSignature("finish_only_signature"),
                        },
                        FinishReason = "stop",
                    },
                ],
            },
        ];

        var stream = NewTransformer().TransformStream(new SliceStream<LlmResponse>(responses));

        var results = new List<StreamEvent>();
        while (stream.Next())
        {
            results.Add(stream.Current!);
        }

        Assert.Null(stream.Err);
        Assert.Equal(2, results.Count);

        var firstResp = LlmJson.Deserialize<GenerateContentResponse>(results[0].Data)!;
        Assert.Equal("OK", Assert.Single(PartsOf(firstResp)).Text);

        var finishResp = LlmJson.Deserialize<GenerateContentResponse>(results[1].Data)!;
        Assert.Equal("STOP", SingleCandidate(finishResp).FinishReason);
        Assert.Empty(PartsOf(finishResp));
    }

    // ------------------------------------------------------------ AggregateStreamChunks

    [Fact]
    public void AggregateStreamChunks_EmptyChunks()
    {
        var data = NewTransformer().AggregateStreamChunks([], out _);

        var resp = LlmJson.Deserialize<GenerateContentResponse>(data)!;
        Assert.NotNull(resp);
    }

    [Fact]
    public void AggregateStreamChunks_SimpleTextChunks()
    {
        StreamEvent[] chunks =
        [
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-agg-1",
                ModelVersion = "gemini-2.0-flash",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = new Content
                        {
                            Role = "model",
                            Parts = [new Part { Text = "Hello" }],
                        },
                    },
                ],
            }),
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-agg-1",
                ModelVersion = "gemini-2.0-flash",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = new Content
                        {
                            Role = "model",
                            Parts = [new Part { Text = ", world!" }],
                        },
                        FinishReason = "STOP",
                    },
                ],
                UsageMetadata = new UsageMetadata
                {
                    PromptTokenCount = 10,
                    CandidatesTokenCount = 5,
                    TotalTokenCount = 15,
                },
            }),
        ];

        var data = NewTransformer().AggregateStreamChunks(chunks, out var meta);

        var resp = LlmJson.Deserialize<GenerateContentResponse>(data)!;
        Assert.Equal("resp-agg-1", resp.ResponseId);

        var fullText = new StringBuilder();
        foreach (var part in PartsOf(resp))
        {
            if (!part.Thought)
            {
                fullText.Append(part.Text);
            }
        }

        Assert.Equal("Hello, world!", fullText.ToString());

        Assert.NotNull(resp.UsageMetadata);
        Assert.Equal(10, resp.UsageMetadata.PromptTokenCount);

        Assert.Equal("resp-agg-1", meta.Id);
        Assert.NotNull(meta.Usage);
        Assert.Equal(10, meta.Usage.PromptTokens);
    }

    [Fact]
    public void AggregateStreamChunks_ThinkingContent()
    {
        StreamEvent[] chunks =
        [
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-think-1",
                ModelVersion = "gemini-2.0-flash-thinking",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = new Content
                        {
                            Role = "model",
                            Parts = [new Part { Text = "Let me think...", Thought = true }],
                        },
                    },
                ],
            }),
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-think-1",
                ModelVersion = "gemini-2.0-flash-thinking",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = new Content
                        {
                            Role = "model",
                            Parts = [new Part { Text = "The answer is 42." }],
                        },
                        FinishReason = "STOP",
                    },
                ],
            }),
        ];

        var data = NewTransformer().AggregateStreamChunks(chunks, out _);

        var resp = LlmJson.Deserialize<GenerateContentResponse>(data)!;

        var hasThinking = false;
        var hasText = false;

        foreach (var part in PartsOf(resp))
        {
            if (part.Thought && part.Text == "Let me think...")
            {
                hasThinking = true;
            }

            if (!part.Thought && part.Text == "The answer is 42.")
            {
                hasText = true;
            }
        }

        Assert.True(hasThinking, "should have thinking part");
        Assert.True(hasText, "should have text part");
    }

    [Fact]
    public void AggregateStreamChunks_ToolCalls()
    {
        StreamEvent[] chunks =
        [
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-tool-1",
                ModelVersion = "gemini-2.0-flash",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = new Content
                        {
                            Role = "model",
                            Parts =
                            [
                                new Part
                                {
                                    FunctionCall = new AxonHub.Llm.Transformer.Gemini.FunctionCall
                                    {
                                        Id = "call-1",
                                        Name = "get_weather",
                                        Args = new Dictionary<string, JsonElement>
                                        {
                                            ["location"] = JsonElement.Parse("\"Tokyo\"").Clone(),
                                        },
                                    },
                                },
                            ],
                        },
                        FinishReason = "STOP",
                    },
                ],
            }),
        ];

        var data = NewTransformer().AggregateStreamChunks(chunks, out _);

        var resp = LlmJson.Deserialize<GenerateContentResponse>(data)!;

        var hasFunctionCall = false;

        foreach (var part in PartsOf(resp))
        {
            if (part.FunctionCall is { } fc)
            {
                hasFunctionCall = true;
                Assert.Equal("call-1", fc.Id);
                Assert.Equal("get_weather", fc.Name);
            }
        }

        Assert.True(hasFunctionCall, "should have function call part");
    }

    [Fact]
    public void AggregateStreamChunks_SkipEmptyChunks()
    {
        StreamEvent[] chunks =
        [
            new StreamEvent { Data = [] },
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-skip-1",
                ModelVersion = "gemini-2.0-flash",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = new Content
                        {
                            Role = "model",
                            Parts = [new Part { Text = "Valid response" }],
                        },
                        FinishReason = "STOP",
                    },
                ],
            }),
        ];

        var data = NewTransformer().AggregateStreamChunks(chunks, out _);

        var resp = LlmJson.Deserialize<GenerateContentResponse>(data)!;

        var fullText = new StringBuilder();
        foreach (var part in PartsOf(resp))
        {
            if (!part.Thought)
            {
                fullText.Append(part.Text);
            }
        }

        Assert.Equal("Valid response", fullText.ToString());
    }

    // ------------------------------------------------------------ StreamTransformation_WithTestData

    [Theory]
    [InlineData("llm-stop.stream.jsonl", "gemini-stop.stream.jsonl")]
    [InlineData("llm-tool.stream.jsonl", "gemini-tool.stream.jsonl")]
    [InlineData("llm-tool2.stream.jsonl", "gemini-tool2.stream.jsonl")]
    [InlineData("llm-tool-parallel.stream.jsonl", "gemini-tool-parallel.stream.jsonl")]
    [InlineData("llm-think.stream.jsonl", "gemini-think.stream.jsonl")]
    [InlineData("llm-parallel_tool.stream.jsonl", "gemini-parallel_tool.stream.jsonl")]
    public void StreamTransformation_WithTestData(string inputStreamFile, string expectedStreamFile)
    {
        var llmResponses = LoadLlmResponses(inputStreamFile);
        var expectedEvents = LoadStreamChunks(expectedStreamFile);

        var transformedStream = NewTransformer().TransformStream(new SliceStream<LlmResponse>(llmResponses));

        var actualEvents = new List<StreamEvent>();
        while (transformedStream.Next())
        {
            var evt = transformedStream.Current;
            if (evt is not null && evt.Data.Length > 0)
            {
                actualEvents.Add(evt);
            }
        }

        Assert.Null(transformedStream.Err);

        // Compare transformed events against golden file (semantic JSON equality).
        Assert.Equal(expectedEvents.Count, actualEvents.Count);

        for (var i = 0; i < expectedEvents.Count; i++)
        {
            ResponsesStreamTestSupport.AssertJsonEquivalent(
                Encoding.UTF8.GetString(expectedEvents[i].Data),
                Encoding.UTF8.GetString(actualEvents[i].Data),
                $"event {i} mismatch");
        }

        // Test aggregation
        var aggregatedBytes = NewTransformer().AggregateStreamChunks(actualEvents, out var meta);
        Assert.False(string.IsNullOrEmpty(meta.Id));

        var aggregatedResp = LlmJson.Deserialize<GenerateContentResponse>(aggregatedBytes)!;
        AssertAggregated(inputStreamFile, aggregatedResp);
    }

    /// <summary>Port of each WithTestData case's expectedAggregated closure.</summary>
    private static void AssertAggregated(string inputStreamFile, GenerateContentResponse result)
    {
        switch (inputStreamFile)
        {
            case "llm-stop.stream.jsonl":
                {
                    Assert.Equal("resp-gemini-stop-1", result.ResponseId);
                    Assert.Equal("gemini-2.0-flash", result.ModelVersion);

                    var stopText = new StringBuilder();
                    foreach (var part in PartsOf(result))
                    {
                        if (!part.Thought)
                        {
                            stopText.Append(part.Text);
                        }
                    }

                    Assert.Equal("Hello, world!", stopText.ToString());
                    Assert.Equal("STOP", SingleCandidate(result).FinishReason);
                    break;
                }

            case "llm-tool.stream.jsonl":
                {
                    Assert.Equal("resp-gemini-tool-1", result.ResponseId);

                    var hasToolCall = false;
                    foreach (var part in PartsOf(result))
                    {
                        if (part.FunctionCall is { } fc)
                        {
                            hasToolCall = true;
                            Assert.Equal("get_weather", fc.Name);
                            Assert.Equal("Tokyo", fc.Args!["location"].GetString());
                        }
                    }

                    Assert.True(hasToolCall, "should have function call part");
                    break;
                }

            case "llm-tool2.stream.jsonl":
                {
                    Assert.Equal("chatcmpl-C2W6446YQfJg7YrSTSPbxFlnlM14H", result.ResponseId);
                    Assert.Equal("gpt-4o-2024-11-20", result.ModelVersion);

                    var tool2Text = new StringBuilder();
                    var hasTool2Call = false;
                    foreach (var part in PartsOf(result))
                    {
                        if (part.FunctionCall is { } fc)
                        {
                            hasTool2Call = true;
                            Assert.Equal("get_user_city", fc.Name);
                            Assert.Equal("123", fc.Args!["user_id"].GetString());
                            continue;
                        }

                        if (!part.Thought)
                        {
                            tool2Text.Append(part.Text);
                        }
                    }

                    Assert.Equal("Let me check that for you.", tool2Text.ToString());
                    Assert.True(hasTool2Call, "should have function call part");
                    break;
                }

            case "llm-tool-parallel.stream.jsonl":
                {
                    Assert.Equal("msg_bdrk_01UJYyE5HVJvdHL9cSvFeFg2", result.ResponseId);
                    Assert.Equal("claude-sonnet-4-20250514", result.ModelVersion);

                    var parallelText = new StringBuilder();
                    var parallelToolCalls = new List<AxonHub.Llm.Transformer.Gemini.FunctionCall>();
                    foreach (var part in PartsOf(result))
                    {
                        if (part.FunctionCall is { } fc)
                        {
                            parallelToolCalls.Add(fc);
                            continue;
                        }

                        if (!part.Thought)
                        {
                            parallelText.Append(part.Text);
                        }
                    }

                    Assert.Equal(
                        "I'll help you get the weather information for San Francisco, CA. Let me start by getting the coordinates for San Francisco and determining the appropriate temperature unit for the US.",
                        parallelText.ToString());
                    Assert.Equal("STOP", SingleCandidate(result).FinishReason);
                    Assert.Equal(2, parallelToolCalls.Count);
                    Assert.Equal("get_coordinates", parallelToolCalls[0].Name);
                    Assert.Equal("San Francisco, CA", parallelToolCalls[0].Args!["location"].GetString());
                    Assert.Equal("get_temperature_unit", parallelToolCalls[1].Name);
                    Assert.Equal("United States", parallelToolCalls[1].Args!["country"].GetString());
                    break;
                }

            case "llm-think.stream.jsonl":
                {
                    Assert.Equal("resp-gemini-think-1", result.ResponseId);

                    var hasThinking = false;
                    var hasText = false;
                    foreach (var part in PartsOf(result))
                    {
                        if (part.Thought)
                        {
                            hasThinking = true;
                            Assert.Contains("think", part.Text);
                        }

                        if (!part.Thought && part.Text != "")
                        {
                            hasText = true;
                            Assert.Equal("The answer is 42.", part.Text);
                        }
                    }

                    Assert.True(hasThinking, "should have thinking part");
                    Assert.True(hasText, "should have text part");
                    break;
                }

            case "llm-parallel_tool.stream.jsonl":
                {
                    Assert.Equal("resp-gemini-parallel-1", result.ResponseId);

                    var functionCallCount = 0;
                    foreach (var part in PartsOf(result))
                    {
                        if (part.FunctionCall is not null)
                        {
                            functionCallCount++;
                        }
                    }

                    Assert.Equal(2, functionCallCount);
                    break;
                }

            default:
                throw new InvalidOperationException($"no aggregated validation for {inputStreamFile}");
        }
    }

    // ------------------------------------------------------------ TransformStreamChunk_FinishReasons

    [Theory]
    [InlineData("stop", "STOP")]
    [InlineData("length", "MAX_TOKENS")]
    [InlineData("content_filter", "SAFETY")]
    [InlineData("tool_calls", "STOP")]
    [InlineData("unknown", "STOP")]
    public void TransformStreamChunk_FinishReasons(string llmFinishReason, string expectedGeminiReason)
    {
        var evt = NewTransformer().TransformStreamChunk(new LlmResponse
        {
            Id = "test-123",
            Model = "gemini-2.0-flash",
            Object = "chat.completion.chunk",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Delta = new Message
                    {
                        Role = "assistant",
                        Content = new MessageContent { Content = "test" },
                    },
                    FinishReason = llmFinishReason,
                },
            ],
        });

        Assert.NotNull(evt);

        var resp = LlmJson.Deserialize<GenerateContentResponse>(evt.Data)!;
        Assert.Equal(expectedGeminiReason, SingleCandidate(resp).FinishReason);
    }

    // ------------------------------------------------------------ testdata helpers

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
