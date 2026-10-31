using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

// See PipelineRetryTests: the namespace name shadows the Pipeline type.
using PipelineType = AxonHub.Llm.Pipeline.Pipeline;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/pipeline/empty_response_test.go, chat/embedding/moderation/
/// alpha-search/decisions subset. The reference cases for speech /
/// transcription / rerank / video responses are deferred until those request
/// types exist in the unified model; see porting/01-baseline.md.
/// </summary>
public sealed class EmptyResponseTests
{
    // Port of TestHasResponseContent/decisions answers.
    [Fact]
    public void HasResponseContent_DecisionsAnswers()
    {
        using var answer = JsonDocument.Parse("""{"type":"refusal"}""");

        Assert.True(ResponseContent.HasResponseContent(new LlmResponse
        {
            Decisions = new DecisionsResponse
            {
                Answers = [answer.RootElement.Clone()],
            },
        }));

        Assert.False(ResponseContent.HasResponseContent(new LlmResponse
        {
            Decisions = new DecisionsResponse { Answers = [] },
        }));
    }

    // Port of TestHasResponseContent/alpha search response.
    [Fact]
    public void HasResponseContent_AlphaSearchResponse()
    {
        Assert.True(ResponseContent.HasResponseContent(new LlmResponse
        {
            AlphaSearch = new AlphaSearchResponse { Body = Encoding.UTF8.GetBytes("""{"ok":true}""") },
        }));

        Assert.False(ResponseContent.HasResponseContent(new LlmResponse
        {
            AlphaSearch = new AlphaSearchResponse { Body = [] },
        }));
    }

    // Port of TestHasResponseContent_ReasoningSignature.
    [Fact]
    public void HasResponseContent_ReasoningSignature()
    {
        const string signature = "gAAAA_reasoning";

        Assert.True(ResponseContent.HasResponseContent(new LlmResponse
        {
            Object = "chat.completion.chunk",
            Choices = [new Choice { Delta = new Message { ReasoningSignature = signature } }],
        }));
    }

    // Port of TestHasResponseContent/empty response.
    [Fact]
    public void HasResponseContent_EmptyResponse()
    {
        Assert.False(ResponseContent.HasResponseContent(new LlmResponse()));
    }

    // Port of TestHasResponseContent/message text content.
    [Fact]
    public void HasResponseContent_MessageTextContent()
    {
        Assert.True(ResponseContent.HasResponseContent(new LlmResponse
        {
            Choices =
            [
                new Choice
                {
                    Message = new Message { Content = new MessageContent { Content = "hello" } },
                },
            ],
        }));
    }

    // Port of TestHasResponseContent/tool calls.
    [Fact]
    public void HasResponseContent_ToolCalls()
    {
        Assert.True(ResponseContent.HasResponseContent(new LlmResponse
        {
            Choices =
            [
                new Choice
                {
                    Message = new Message { ToolCalls = [new ToolCall { Id = "call_1" }] },
                },
            ],
        }));
    }

    // Port of TestHasResponseContent/embedding response data.
    [Fact]
    public void HasResponseContent_EmbeddingResponseData()
    {
        Assert.True(ResponseContent.HasResponseContent(new LlmResponse
        {
            Embedding = new EmbeddingResponse
            {
                Object = "list",
                Data =
                [
                    new EmbeddingData
                    {
                        Object = "embedding",
                        Embedding = new Embedding { Values = [0.1, 0.2, 0.3] },
                        Index = 0,
                    },
                ],
            },
        }));
    }

    // Port of TestHasResponseContent/moderation response with results.
    [Fact]
    public void HasResponseContent_ModerationResponseWithResults()
    {
        Assert.True(ResponseContent.HasResponseContent(new LlmResponse
        {
            Moderation = new ModerationResponse
            {
                Results =
                [
                    new ModerationClassification
                    {
                        Flagged = false,
                        Categories = new Dictionary<string, bool> { ["hate"] = false },
                        CategoryScores = new Dictionary<string, double> { ["hate"] = 0.01 },
                    },
                ],
            },
        }));
    }

    // Port of TestHasResponseContent/empty moderation response.
    [Fact]
    public void HasResponseContent_EmptyModerationResponse()
    {
        Assert.False(ResponseContent.HasResponseContent(new LlmResponse
        {
            Moderation = new ModerationResponse(),
        }));
    }

    // Port of TestHasResponseContent/reasoning content.
    [Fact]
    public void HasResponseContent_ReasoningContent()
    {
        Assert.True(ResponseContent.HasResponseContent(new LlmResponse
        {
            Choices =
            [
                new Choice { Message = new Message { ReasoningContent = "thinking" } },
            ],
        }));
    }

    // Port of TestPipeline_Process_StreamEmptyResponseDetection/retries on empty stream response.
    [Fact]
    public void StreamEmptyResponseDetection_RetriesOnEmptyStreamResponse()
    {
        var streamCalls = 0;
        var executor = new MockExecutor
        {
            DoStreamFn = (_, _) =>
            {
                streamCalls++;
                return new SliceStream<StreamEvent>(new List<StreamEvent> { new() });
            },
        };

        var prepareCalls = 0;
        var outbound = new MockOutbound
        {
            TransformStreamFn = (_, _) =>
            {
                if (streamCalls == 1)
                {
                    // First call: empty response (finish reason, no content).
                    return new SliceStream<LlmResponse>(new List<LlmResponse>
                    {
                        new()
                        {
                            Choices = [new Choice { FinishReason = "stop", Message = new Message() }],
                        },
                    });
                }

                // Second call: response with content.
                return new SliceStream<LlmResponse>(
                    new List<LlmResponse> { LlmTestData.ContentChunk("ok"), LlmResponse.DoneResponse });
            },
            CanRetryFn = err => GoErrors.Is(err, PipelineErrors.EmptyResponse),
            PrepareForRetryFn = _ =>
            {
                prepareCalls++;
                return null;
            },
        };

        var inbound = new MockInbound
        {
            TransformRequestFn = _ => new LlmRequest { Stream = true },
        };

        var options = new PipelineOptions
        {
            MaxSameChannelRetries = 1,
            EmptyResponseDetection = true,
        };
        var pipeline = new PipelineType(executor, inbound, outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.True(res.Stream);
        Assert.Equal(2, streamCalls);
        Assert.Equal(1, prepareCalls);
    }

    // Port of TestPipeline_Process_StreamEmptyResponseDetection/accepts stream response with content.
    [Fact]
    public void StreamEmptyResponseDetection_AcceptsStreamResponseWithContent()
    {
        var streamCalls = 0;
        var executor = new MockExecutor
        {
            DoStreamFn = (_, _) =>
            {
                streamCalls++;
                return new SliceStream<StreamEvent>(new List<StreamEvent> { new() });
            },
        };

        var outbound = new MockOutbound
        {
            TransformStreamFn = (_, _) => new SliceStream<LlmResponse>(
                new List<LlmResponse> { LlmTestData.ContentChunk("hello"), LlmResponse.DoneResponse }),
        };

        var inbound = new MockInbound
        {
            TransformRequestFn = _ => new LlmRequest { Stream = true },
        };

        var options = new PipelineOptions { EmptyResponseDetection = true };
        var pipeline = new PipelineType(executor, inbound, outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.True(res.Stream);
        Assert.Equal(1, streamCalls);
    }

    // Port of TestPipeline_Process_NonStreamEmptyResponseDetection/retries on empty non-stream response.
    [Fact]
    public void NonStreamEmptyResponseDetection_RetriesOnEmptyNonStreamResponse()
    {
        var execCalls = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                execCalls++;
                return new HttpResponseData();
            },
        };

        var prepareCalls = 0;
        var outbound = new MockOutbound
        {
            TransformResponseFn = _ =>
            {
                if (execCalls == 1)
                {
                    return new LlmResponse
                    {
                        Choices = [new Choice { Message = new Message() }],
                    };
                }

                return new LlmResponse
                {
                    Choices =
                    [
                        new Choice
                        {
                            Message = new Message { Content = new MessageContent { Content = "ok" } },
                        },
                    ],
                };
            },
            CanRetryFn = err => GoErrors.Is(err, PipelineErrors.EmptyResponse),
            PrepareForRetryFn = _ =>
            {
                prepareCalls++;
                return null;
            },
        };

        var options = new PipelineOptions
        {
            MaxSameChannelRetries = 1,
            EmptyResponseDetection = true,
        };
        var pipeline = new PipelineType(executor, new MockInbound(), outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(2, execCalls);
        Assert.Equal(1, prepareCalls);
    }

    // Port of TestPipeline_Process_NonStreamEmptyResponseDetection/accepts non-stream tool-call response.
    [Fact]
    public void NonStreamEmptyResponseDetection_AcceptsToolCallResponse()
    {
        var execCalls = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                execCalls++;
                return new HttpResponseData();
            },
        };

        var outbound = new MockOutbound
        {
            TransformResponseFn = _ => new LlmResponse
            {
                Choices =
                [
                    new Choice
                    {
                        Message = new Message { ToolCalls = [new ToolCall { Id = "call_1" }] },
                    },
                ],
            },
        };

        var options = new PipelineOptions { EmptyResponseDetection = true };
        var pipeline = new PipelineType(executor, new MockInbound(), outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(1, execCalls);
    }

    // Port of TestPipeline_Process_NonStreamEmptyResponseDetection/accepts non-stream embedding response.
    [Fact]
    public void NonStreamEmptyResponseDetection_AcceptsEmbeddingResponse()
    {
        var execCalls = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                execCalls++;
                return new HttpResponseData();
            },
        };

        var outbound = new MockOutbound
        {
            TransformResponseFn = _ => new LlmResponse
            {
                RequestType = RequestTypes.Embedding,
                Embedding = new EmbeddingResponse
                {
                    Object = "list",
                    Data =
                    [
                        new EmbeddingData
                        {
                            Object = "embedding",
                            Embedding = new Embedding { Values = [0.1, 0.2, 0.3] },
                            Index = 0,
                        },
                    ],
                },
            },
        };

        var options = new PipelineOptions { EmptyResponseDetection = true };
        var pipeline = new PipelineType(executor, new MockInbound(), outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(1, execCalls);
    }

    // Port of TestPipeline_Process_NonStreamEmptyResponseDetection/accepts non-stream moderation response.
    [Fact]
    public void NonStreamEmptyResponseDetection_AcceptsModerationResponse()
    {
        var execCalls = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                execCalls++;
                return new HttpResponseData();
            },
        };

        var outbound = new MockOutbound
        {
            TransformResponseFn = _ => new LlmResponse
            {
                RequestType = RequestTypes.Moderation,
                ApiFormat = ApiFormats.OpenAIModeration,
                Moderation = new ModerationResponse
                {
                    Results =
                    [
                        new ModerationClassification
                        {
                            Flagged = false,
                            Categories = new Dictionary<string, bool> { ["hate"] = false },
                            CategoryScores = new Dictionary<string, double> { ["hate"] = 0.01 },
                        },
                    ],
                },
            },
        };

        var options = new PipelineOptions { EmptyResponseDetection = true };
        var pipeline = new PipelineType(executor, new MockInbound(), outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(1, execCalls);
    }
}
