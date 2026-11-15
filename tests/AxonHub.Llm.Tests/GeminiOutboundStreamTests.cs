using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.OpenAI;
using GeminiFunctionCall = AxonHub.Llm.Transformer.Gemini.FunctionCall;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the Gemini streaming outbound
/// (llm/transformer/gemini/outbound_stream.go + aggregator.go and their
/// outbound_stream_test.go / aggregator_test.go). Every test mirrors a
/// reference behaviour one-for-one; the testdata cases replay the reference's
/// gemini-*.stream.jsonl fixtures through both TransformStream and
/// AggregateStreamChunks.
/// </summary>
public sealed class GeminiOutboundStreamTests
{
    private static GeminiOutboundTransformer NewTransformer()
        => GeminiOutboundTransformer.CreateWithConfig(new GeminiOutboundConfig
        {
            BaseUrl = "https://generativelanguage.googleapis.com",
            ApiVersion = "v1beta",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

    private static byte[] Marshal(object value) => GoJson.Marshal(value);

    private static StreamEvent Chunk(GenerateContentResponse resp) => new() { Data = Marshal(resp) };

    private static Dictionary<long, string> FinishReasons(params StreamEvent[] chunks)
    {
        var results = GoStreams.All(
                NewTransformer().TransformStream(null, new SliceStream<StreamEvent>(chunks)),
                out var err)
            .Where(static response => response.Object != "[DONE]")
            .ToList();
        Assert.Null(err);

        var reasons = new Dictionary<long, string>();
        foreach (var response in results)
        {
            foreach (var choice in response.Choices)
            {
                if (choice.FinishReason is not null)
                {
                    reasons[choice.Index] = choice.FinishReason;
                }
            }
        }

        return reasons;
    }

    private static Content FunctionCallContent(string name) => new()
    {
        Role = "model",
        Parts =
        [
            new Part
            {
                FunctionCall = new GeminiFunctionCall
                {
                    Id = "call-" + name,
                    Name = name,
                    Args = new Dictionary<string, JsonElement>(),
                },
            },
        ],
    };

    private static LlmResponse ConvertAggregated(byte[] data)
        => GeminiOutboundConvert.ConvertGeminiToLlmResponse(
            LlmJson.Deserialize<GenerateContentResponse>(data)!, false);

    // ------------------------------------------------------------ TransformStreamChunk

    [Fact]
    public void TransformStreamChunk_NilEventReturnsNull()
    {
        Assert.Null(NewTransformer().TransformStreamChunk(null));
    }

    [Fact]
    public void TransformStreamChunk_EmptyDataReturnsNull()
    {
        Assert.Null(NewTransformer().TransformStreamChunk(new StreamEvent { Data = [] }));
    }

    [Fact]
    public void TransformStreamChunk_DoneMarkerReturnsDoneResponse()
    {
        var resp = NewTransformer().TransformStreamChunk(new StreamEvent { Data = "[DONE]"u8.ToArray() });

        Assert.NotNull(resp);
        Assert.Equal("[DONE]", resp.Object);
    }

    [Fact]
    public void TransformStreamChunk_SimpleTextResponse()
    {
        var resp = NewTransformer().TransformStreamChunk(Chunk(new GenerateContentResponse
        {
            ResponseId = "resp-123",
            ModelVersion = "gemini-2.0-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Hello, world!" }],
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
        }));

        Assert.NotNull(resp);
        Assert.Equal("resp-123", resp.Id);
        Assert.Equal("gemini-2.0-flash", resp.Model);
        Assert.Equal("chat.completion.chunk", resp.Object);
        Assert.Single(resp.Choices);
        Assert.NotNull(resp.Choices[0].Delta);
        Assert.Equal("Hello, world!", resp.Choices[0].Delta!.Content!.Content);
        Assert.Equal("stop", resp.Choices[0].FinishReason);
        Assert.NotNull(resp.Usage);
        Assert.Equal(10, resp.Usage!.PromptTokens);
        Assert.Equal(5, resp.Usage.CompletionTokens);
    }

    [Fact]
    public void TransformStreamChunk_ThinkingContent()
    {
        var resp = NewTransformer().TransformStreamChunk(Chunk(new GenerateContentResponse
        {
            ResponseId = "resp-456",
            ModelVersion = "gemini-2.0-flash-thinking",
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
                            new Part { Text = "Let me think about this...", Thought = true },
                            new Part { Text = "The answer is 42." },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        }));

        Assert.NotNull(resp);
        Assert.Single(resp.Choices);
        Assert.Equal("Let me think about this...", resp.Choices[0].Delta!.ReasoningContent);
        Assert.Equal("The answer is 42.", resp.Choices[0].Delta!.Content!.Content);
    }

    [Fact]
    public void TransformStreamChunk_FunctionCall()
    {
        var resp = NewTransformer().TransformStreamChunk(Chunk(new GenerateContentResponse
        {
            ResponseId = "resp-789",
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
                                FunctionCall = new GeminiFunctionCall
                                {
                                    Id = "call-123",
                                    Name = "get_weather",
                                    Args = new Dictionary<string, JsonElement>
                                    {
                                        ["location"] = JsonElement.Parse("\"Tokyo\""),
                                    },
                                },
                            },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        }));

        Assert.NotNull(resp);
        Assert.Single(resp.Choices);
        Assert.Single(resp.Choices[0].Delta!.ToolCalls!);
        Assert.Equal("call-123", resp.Choices[0].Delta!.ToolCalls![0].Id);
        Assert.Equal("get_weather", resp.Choices[0].Delta!.ToolCalls![0].Function.Name);
        Assert.Contains("Tokyo", resp.Choices[0].Delta!.ToolCalls![0].Function.Arguments);
    }

    [Fact]
    public void TransformStreamChunk_InvalidJsonReturnsError()
    {
        // Go returns the raw json.Unmarshal error; the port raises the STJ
        // message in the Go style (no " Path: " suffix) — C113.
        var ex = Assert.Throws<InvalidOperationException>(
            () => NewTransformer().TransformStreamChunk(new StreamEvent { Data = "invalid json"u8.ToArray() }));

        Assert.Contains("invalid start of a value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ TransformStream

    [Fact]
    public void TransformStream_MapsEveryChunkAndAppendsDone()
    {
        var input = new SliceStream<StreamEvent>(
        [
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-stream-1",
                ModelVersion = "gemini-2.0-flash",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = new Content { Role = "model", Parts = [new Part { Text = "Hello" }] },
                    },
                ],
            }),
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-stream-1",
                ModelVersion = "gemini-2.0-flash",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = new Content { Role = "model", Parts = [new Part { Text = ", world!" }] },
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
        ]);

        var output = NewTransformer().TransformStream(null, input);
        var results = GoStreams.All(output, out var err);

        Assert.Null(err);
        Assert.True(results.Count >= 2);
        Assert.Equal("Hello", results[0].Choices[0].Delta!.Content!.Content);
        Assert.Equal(", world!", results[1].Choices[0].Delta!.Content!.Content);
        // The appended DONE sentinel closes the stream.
        Assert.Equal("[DONE]", results[^1].Object);
    }

    [Fact]
    public void TransformStream_AllowsMissingResponseId()
    {
        var input = new SliceStream<StreamEvent>(
        [
            Chunk(new GenerateContentResponse
            {
                ModelVersion = "gemini-2.5-flash",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = new Content { Role = "model", Parts = [new Part { Text = "第一段" }] },
                    },
                ],
            }),
            Chunk(new GenerateContentResponse
            {
                ModelVersion = "gemini-2.5-flash",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = new Content { Role = "model", Parts = [new Part { Text = "第二段" }] },
                        FinishReason = "STOP",
                    },
                ],
            }),
        ]);

        var output = NewTransformer().TransformStream(null, input);
        var results = GoStreams.All(output, out var err);

        Assert.Null(err);
        var responses = results.Where(r => r.Object != "[DONE]").ToList();
        Assert.Equal(2, responses.Count);
        Assert.NotEmpty(responses[0].Id);
        Assert.Equal(responses[0].Id, responses[1].Id);
    }

    [Fact]
    public void TransformStream_ToolCallIndexAccumulation()
    {
        var input = new SliceStream<StreamEvent>(
        [
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-tool-stream-1",
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
                                    FunctionCall = new GeminiFunctionCall
                                    {
                                        Id = "call-1",
                                        Name = "get_weather",
                                        Args = new Dictionary<string, JsonElement>
                                        {
                                            ["location"] = JsonElement.Parse("\"Tokyo\""),
                                        },
                                    },
                                },
                            ],
                        },
                    },
                ],
            }),
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-tool-stream-1",
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
                                    FunctionCall = new GeminiFunctionCall
                                    {
                                        Id = "call-2",
                                        Name = "get_time",
                                        Args = new Dictionary<string, JsonElement>
                                        {
                                            ["timezone"] = JsonElement.Parse("\"JST\""),
                                        },
                                    },
                                },
                            ],
                        },
                        FinishReason = "STOP",
                    },
                ],
            }),
        ]);

        var output = NewTransformer().TransformStream(null, input);
        var results = GoStreams.All(output, out var err).Where(r => r.Object != "[DONE]").ToList();

        Assert.Null(err);
        Assert.Equal(2, results.Count);

        Assert.Single(results[0].Choices[0].Delta!.ToolCalls!);
        Assert.Equal(0, results[0].Choices[0].Delta!.ToolCalls![0].Index);
        Assert.Equal("call-1", results[0].Choices[0].Delta!.ToolCalls![0].Id);
        Assert.Equal("get_weather", results[0].Choices[0].Delta!.ToolCalls![0].Function.Name);

        Assert.Single(results[1].Choices[0].Delta!.ToolCalls!);
        Assert.Equal(1, results[1].Choices[0].Delta!.ToolCalls![0].Index);
        Assert.Equal("call-2", results[1].Choices[0].Delta!.ToolCalls![0].Id);
        Assert.Equal("get_time", results[1].Choices[0].Delta!.ToolCalls![0].Function.Name);
    }

    [Fact]
    public void TransformStream_MultipleToolCallsInSingleEvent()
    {
        var input = new SliceStream<StreamEvent>(
        [
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-multi-tool-1",
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
                                    FunctionCall = new GeminiFunctionCall
                                    {
                                        Id = "call-a",
                                        Name = "func_a",
                                        Args = new Dictionary<string, JsonElement>
                                        {
                                            ["param"] = JsonElement.Parse("\"value_a\""),
                                        },
                                    },
                                },
                                new Part
                                {
                                    FunctionCall = new GeminiFunctionCall
                                    {
                                        Id = "call-b",
                                        Name = "func_b",
                                        Args = new Dictionary<string, JsonElement>
                                        {
                                            ["param"] = JsonElement.Parse("\"value_b\""),
                                        },
                                    },
                                },
                            ],
                        },
                        FinishReason = "STOP",
                    },
                ],
            }),
        ]);

        var output = NewTransformer().TransformStream(null, input);
        var results = GoStreams.All(output, out var err).Where(r => r.Object != "[DONE]").ToList();

        Assert.Null(err);
        Assert.Single(results);
        Assert.Equal(2, results[0].Choices[0].Delta!.ToolCalls!.Count);
        Assert.Equal(0, results[0].Choices[0].Delta!.ToolCalls![0].Index);
        Assert.Equal("call-a", results[0].Choices[0].Delta!.ToolCalls![0].Id);
        Assert.Equal(1, results[0].Choices[0].Delta!.ToolCalls![1].Index);
        Assert.Equal("call-b", results[0].Choices[0].Delta!.ToolCalls![1].Id);
    }

    [Fact]
    public void TransformStream_KeepsToolCallsFinishReasonWhenStopArrivesLater()
    {
        var reasons = FinishReasons(
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-split-finish",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = FunctionCallContent("get_weather"),
                    },
                ],
            }),
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-split-finish",
                Candidates = [new Candidate { Index = 0, FinishReason = "STOP" }],
            }));

        Assert.Equal("tool_calls", reasons[0]);
    }

    [Fact]
    public void TransformStream_TracksToolCallFinishReasonPerCandidate()
    {
        var reasons = FinishReasons(
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-split-finish",
                Candidates =
                [
                    new Candidate { Index = 0, Content = FunctionCallContent("get_weather") },
                    new Candidate
                    {
                        Index = 1,
                        Content = new Content { Role = "model", Parts = [new Part { Text = "Hello" }] },
                    },
                ],
            }),
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-split-finish",
                Candidates =
                [
                    new Candidate { Index = 0, FinishReason = "STOP" },
                    new Candidate { Index = 1, FinishReason = "STOP" },
                ],
            }));

        Assert.Equal("tool_calls", reasons[0]);
        Assert.Equal("stop", reasons[1]);
    }

    [Fact]
    public void TransformStream_MaxTokensAfterAToolCallStaysLength()
    {
        var reasons = FinishReasons(
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-split-finish",
                Candidates =
                [
                    new Candidate { Index = 0, Content = FunctionCallContent("get_weather") },
                ],
            }),
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-split-finish",
                Candidates = [new Candidate { Index = 0, FinishReason = "MAX_TOKENS" }],
            }));

        Assert.Equal("length", reasons[0]);
    }

    // ------------------------------------------------------------ AggregateStreamChunks (transformer method)

    [Fact]
    public void AggregateStreamChunks_EmptyChunksReturnsEmptyResponse()
    {
        var data = NewTransformer().AggregateStreamChunks(null, [], out var meta);

        Assert.NotEmpty(data);
        Assert.Equal(string.Empty, meta.Id);
        using var _ = JsonDocument.Parse(data); // must be valid JSON
    }

    [Fact]
    public void AggregateStreamChunks_SimpleText()
    {
        var data = NewTransformer().AggregateStreamChunks(null,
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
                        Content = new Content { Role = "model", Parts = [new Part { Text = "Hello" }] },
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
                        Content = new Content { Role = "model", Parts = [new Part { Text = ", world!" }] },
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
        ], out var meta);

        var llmResp = ConvertAggregated(data);
        Assert.Equal("resp-agg-1", llmResp.Id);
        Assert.Single(llmResp.Choices);
        Assert.Equal("Hello, world!", llmResp.Choices[0].Message!.Content!.Content);
        Assert.NotNull(llmResp.Usage);
        Assert.Equal(10, llmResp.Usage!.PromptTokens);
    }

    [Fact]
    public void AggregateStreamChunks_ThinkingContent()
    {
        var data = NewTransformer().AggregateStreamChunks(null,
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
                        Content = new Content { Role = "model", Parts = [new Part { Text = "The answer is 42." }] },
                        FinishReason = "STOP",
                    },
                ],
            }),
        ], out _);

        var llmResp = ConvertAggregated(data);
        Assert.Single(llmResp.Choices);
        Assert.Equal("Let me think...", llmResp.Choices[0].Message!.ReasoningContent);
        Assert.Equal("The answer is 42.", llmResp.Choices[0].Message!.Content!.Content);
    }

    [Fact]
    public void AggregateStreamChunks_ToolCalls()
    {
        var data = NewTransformer().AggregateStreamChunks(null,
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
                                    FunctionCall = new GeminiFunctionCall
                                    {
                                        Id = "call-1",
                                        Name = "get_weather",
                                        Args = new Dictionary<string, JsonElement>
                                        {
                                            ["location"] = JsonElement.Parse("\"Tokyo\""),
                                        },
                                    },
                                },
                            ],
                        },
                        FinishReason = "STOP",
                    },
                ],
            }),
        ], out _);

        var llmResp = ConvertAggregated(data);
        Assert.Single(llmResp.Choices);
        Assert.Single(llmResp.Choices[0].Message!.ToolCalls!);
        Assert.Equal("call-1", llmResp.Choices[0].Message!.ToolCalls![0].Id);
        Assert.Equal("get_weather", llmResp.Choices[0].Message!.ToolCalls![0].Function.Name);
    }

    [Fact]
    public void AggregateStreamChunks_SkipsDoneMarker()
    {
        var data = NewTransformer().AggregateStreamChunks(null,
        [
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-done-1",
                ModelVersion = "gemini-2.0-flash",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = new Content { Role = "model", Parts = [new Part { Text = "Hello" }] },
                        FinishReason = "STOP",
                    },
                ],
            }),
            new StreamEvent { Data = "[DONE]"u8.ToArray() },
        ], out _);

        var llmResp = ConvertAggregated(data);
        Assert.Single(llmResp.Choices);
        Assert.Equal("Hello", llmResp.Choices[0].Message!.Content!.Content);
    }

    [Fact]
    public void AggregateStreamChunks_SkipsInvalidJsonChunks()
    {
        var data = NewTransformer().AggregateStreamChunks(null,
        [
            new StreamEvent { Data = "invalid json"u8.ToArray() },
            Chunk(new GenerateContentResponse
            {
                ResponseId = "resp-skip-1",
                ModelVersion = "gemini-2.0-flash",
                Candidates =
                [
                    new Candidate
                    {
                        Index = 0,
                        Content = new Content { Role = "model", Parts = [new Part { Text = "Valid response" }] },
                        FinishReason = "STOP",
                    },
                ],
            }),
        ], out _);

        var llmResp = ConvertAggregated(data);
        Assert.Single(llmResp.Choices);
        Assert.Equal("Valid response", llmResp.Choices[0].Message!.Content!.Content);
    }

    [Theory]
    [InlineData("STOP", "stop")]
    [InlineData("MAX_TOKENS", "length")]
    [InlineData("SAFETY", "content_filter")]
    [InlineData("RECITATION", "content_filter")]
    [InlineData("UNKNOWN", "stop")]
    public void TransformStreamChunk_FinishReasons(string geminiFinishReason, string expected)
    {
        var resp = NewTransformer().TransformStreamChunk(Chunk(new GenerateContentResponse
        {
            ResponseId = "test-response-id",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content { Role = "model", Parts = [new Part { Text = "test" }] },
                    FinishReason = geminiFinishReason,
                },
            ],
        }));

        Assert.NotNull(resp);
        Assert.Equal(expected, resp.Choices[0].FinishReason);
    }

    // ------------------------------------------------------------ stream transformation with testdata

    [Theory]
    [InlineData("gemini-stop.stream.jsonl")]
    [InlineData("gemini-tool.stream.jsonl")]
    [InlineData("gemini-think.stream.jsonl")]
    [InlineData("gemini-parallel_tool.stream.jsonl")]
    public void StreamTransformation_WithTestData(string inputStreamFile)
    {
        var transformer = NewTransformer();
        var geminiEvents = LoadGeminiStreamChunks(inputStreamFile);

        var transformedStream = transformer.TransformStream(null, new SliceStream<StreamEvent>(geminiEvents));
        var actualResponses = GoStreams.All(transformedStream, out var err);
        Assert.Null(err);
        Assert.NotEmpty(actualResponses);

        var aggregatedBytes = transformer.AggregateStreamChunks(null, geminiEvents, out var meta);
        Assert.NotEmpty(meta.Id);

        var aggregatedResp = ConvertAggregated(aggregatedBytes);
        ValidateAggregated(inputStreamFile, aggregatedResp);
    }

    private static void ValidateAggregated(string fixture, LlmResponse result)
    {
        switch (fixture)
        {
            case "gemini-stop.stream.jsonl":
                Assert.Equal("resp-gemini-stop-1", result.Id);
                Assert.Equal("gemini-2.0-flash", result.Model);
                Assert.Equal("chat.completion", result.Object);
                Assert.Single(result.Choices);
                Assert.NotNull(result.Choices[0].Message);
                Assert.Equal("Hello, world!", result.Choices[0].Message!.Content!.Content);
                Assert.Equal("stop", result.Choices[0].FinishReason);
                Assert.NotNull(result.Usage);
                Assert.Equal(10, result.Usage!.PromptTokens);
                Assert.Equal(5, result.Usage.CompletionTokens);
                return;

            case "gemini-tool.stream.jsonl":
                Assert.Equal("resp-gemini-tool-1", result.Id);
                Assert.Single(result.Choices);
                Assert.Single(result.Choices[0].Message!.ToolCalls!);
                Assert.Equal("get_weather", result.Choices[0].Message!.ToolCalls![0].Function.Name);
                Assert.Contains("Tokyo", result.Choices[0].Message!.ToolCalls![0].Function.Arguments);
                Assert.Equal("tool_calls", result.Choices[0].FinishReason);
                return;

            case "gemini-think.stream.jsonl":
                Assert.Equal("resp-gemini-think-1", result.Id);
                Assert.Single(result.Choices);
                Assert.NotNull(result.Choices[0].Message!.ReasoningContent);
                Assert.Contains("think", result.Choices[0].Message!.ReasoningContent!);
                Assert.Equal("The answer is 42.", result.Choices[0].Message!.Content!.Content);
                Assert.Equal("stop", result.Choices[0].FinishReason);
                return;

            case "gemini-parallel_tool.stream.jsonl":
                Assert.Equal("resp-gemini-parallel-1", result.Id);
                Assert.Single(result.Choices);
                Assert.Equal(2, result.Choices[0].Message!.ToolCalls!.Count);
                Assert.Equal(0, result.Choices[0].Message!.ToolCalls![0].Index);
                Assert.Equal("get_weather", result.Choices[0].Message!.ToolCalls![0].Function.Name);
                Assert.Equal(1, result.Choices[0].Message!.ToolCalls![1].Index);
                Assert.Equal("get_time", result.Choices[0].Message!.ToolCalls![1].Function.Name);
                Assert.Equal("tool_calls", result.Choices[0].FinishReason);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(fixture), fixture, "unknown fixture");
        }
    }

    /// <summary>Port of loadGeminiStreamChunks (outbound_stream_test.go:988-1019).</summary>
    private static List<StreamEvent> LoadGeminiStreamChunks(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "gemini", filename);
        var chunks = new List<StreamEvent>();

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;

            chunks.Add(new StreamEvent
            {
                LastEventId = ReadString(root, "LastEventID"),
                Type = ReadString(root, "Type") ?? string.Empty,
                Data = Encoding.UTF8.GetBytes(ReadString(root, "Data") ?? string.Empty),
            });
        }

        return chunks;
    }

    private static string? ReadString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
