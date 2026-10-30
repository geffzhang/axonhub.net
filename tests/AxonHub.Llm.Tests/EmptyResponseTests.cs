using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

// See PipelineRetryTests: the namespace name shadows the Pipeline type.
using PipelineType = AxonHub.Llm.Pipeline.Pipeline;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/pipeline/empty_response_test.go, chat subset. The reference
/// cases for decisions / alpha-search / speech / transcription / embedding /
/// moderation responses are deferred until those request types exist in the
/// unified model; see porting/01-baseline.md.
/// </summary>
public sealed class EmptyResponseTests
{
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
}
