using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

// The Pipeline type lives in the AxonHub.Llm.Pipeline namespace, whose simple
// name shadows it from inside AxonHub.Llm.Tests. The alias keeps the reference
// type name readable in the ported tests.
using PipelineType = AxonHub.Llm.Pipeline.Pipeline;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/pipeline/pipeline_retry_test.go. Each test keeps the reference
/// test's name so the baseline ledger can be compared test by test.
/// </summary>
public sealed class PipelineRetryTests
{
    // Port of TestPipeline_Process_RetryLogic/SameChannelRetrySuccess.
    [Fact]
    public void SameChannelRetrySuccess()
    {
        var execCalls = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                execCalls++;
                if (execCalls == 1)
                {
                    throw new InvalidOperationException("temporary error");
                }

                return new HttpResponseData();
            },
        };

        var prepareCalls = 0;
        var outbound = new MockOutbound
        {
            CanRetryFn = _ => true,
            PrepareForRetryFn = _ =>
            {
                prepareCalls++;
                return null;
            },
        };

        var middleware = new MockMiddleware();
        var options = new PipelineOptions { MaxSameChannelRetries = 2 };
        options.Middlewares.Add(middleware);

        var pipeline = new PipelineType(executor, new MockInbound(), outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(2, execCalls);
        Assert.Equal(1, prepareCalls);
        Assert.Equal(1, middleware.ErrorCalls);
    }

    // Port of TestPipeline_Process_RetryLogic/CrossChannelRetrySuccess.
    [Fact]
    public void CrossChannelRetrySuccess()
    {
        var execCalls = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                execCalls++;
                if (execCalls == 1)
                {
                    throw new InvalidOperationException("channel error");
                }

                return new HttpResponseData();
            },
        };

        var switchCalls = 0;
        var outbound = new MockOutbound
        {
            CanRetryFn = _ => false, // No same-channel retry.
            HasMoreChannelsFn = () => true,
            NextChannelFn = _ =>
            {
                switchCalls++;
                return null;
            },
        };

        var options = new PipelineOptions { MaxChannelRetries = 1 };
        var pipeline = new PipelineType(executor, new MockInbound(), outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(2, execCalls);
        Assert.Equal(1, switchCalls);
    }

    [Fact]
    public void ChannelSwitchPolicyCanStopRetryingTerminalFailure()
    {
        var failure = new InvalidOperationException("terminal provider failure");
        var execCalls = 0;
        var switchCalls = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                execCalls++;
                throw failure;
            },
        };
        var outbound = new MockOutbound
        {
            CanRetryFn = _ => false,
            CanSwitchChannelFn = _ => false,
            HasMoreChannelsFn = () => true,
            NextChannelFn = _ =>
            {
                switchCalls++;
                return null;
            },
        };
        var pipeline = new PipelineType(
            executor,
            new MockInbound(),
            outbound,
            new PipelineOptions { MaxChannelRetries = 1 });

        var thrown = Assert.ThrowsAny<Exception>(
            () => pipeline.Process(new HttpRequestData(), CancellationToken.None));

        Assert.True(GoErrors.Is(thrown, failure));
        Assert.Equal(1, execCalls);
        Assert.Equal(0, switchCalls);
    }

    // Port of TestPipeline_Process_RetryLogic/MixedRetrySuccess.
    [Fact]
    public void MixedRetrySuccess()
    {
        var execCalls = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                execCalls++;
                if (execCalls < 4)
                {
                    throw new InvalidOperationException("fail");
                }

                return new HttpResponseData();
            },
        };

        var prepareCalls = 0;
        var switchCalls = 0;
        var outbound = new MockOutbound
        {
            CanRetryFn = _ => true,
            PrepareForRetryFn = _ =>
            {
                prepareCalls++;
                return null;
            },
            HasMoreChannelsFn = () => true,
            NextChannelFn = _ =>
            {
                switchCalls++;
                return null;
            },
        };

        var options = new PipelineOptions
        {
            MaxSameChannelRetries = 2,
            MaxChannelRetries = 1,
        };
        var pipeline = new PipelineType(executor, new MockInbound(), outbound, options);

        // Sequence:
        // 1. exec 1 -> fail
        // 2. same-channel retry 1 (prepare 1) -> exec 2 -> fail
        // 3. same-channel retry 2 (prepare 2) -> exec 3 -> fail
        // 4. same-channel exhausted -> switch 1 -> same-channel reset -> exec 4 -> success
        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(4, execCalls);
        Assert.Equal(2, prepareCalls);
        Assert.Equal(1, switchCalls);
    }

    // Port of TestPipeline_Process_RetryLogic/AllExhausted.
    [Fact]
    public void AllExhausted()
    {
        var execCalls = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                execCalls++;
                throw new InvalidOperationException("permanent fail");
            },
        };

        var outbound = new MockOutbound
        {
            CanRetryFn = _ => true,
            HasMoreChannelsFn = () => true,
        };

        var options = new PipelineOptions
        {
            MaxSameChannelRetries = 1,
            MaxChannelRetries = 1,
        };
        var pipeline = new PipelineType(executor, new MockInbound(), outbound, options);

        // Sequence:
        // 1. exec 1 -> fail
        // 2. same-channel retry 1 -> exec 2 -> fail
        // 3. same-channel exhausted -> switch 1 -> same-channel reset
        // 4. exec 3 -> fail
        // 5. same-channel retry 1 -> exec 4 -> fail
        // 6. same-channel exhausted -> switch 2 (but maxChannelRetries=1) -> stop
        Assert.ThrowsAny<Exception>(() =>
        {
            pipeline.Process(new HttpRequestData(), CancellationToken.None);
        });

        Assert.Equal(4, execCalls);
    }

    // Port of TestPipeline_Process_RetryPreservesOriginalStreamIntent.
    [Fact]
    public void RetryPreservesOriginalStreamIntent()
    {
        var inbound = new MockInbound
        {
            AggregateStreamChunksFn = _ => Encoding.UTF8.GetBytes("""{"ok":true}"""),
        };

        var attempts = 0;
        var executor = new MockExecutor
        {
            DoStreamFn = (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("temporary stream failure");
                }

                return new SliceStream<StreamEvent>(
                    new List<StreamEvent> { new() { Data = Encoding.UTF8.GetBytes("chunk") } });
            },
        };

        var transformAttempts = 0;
        var outbound = new MockOutbound
        {
            CanRetryFn = _ => true,
            PrepareForRetryFn = _ => null,
            TransformRequestFn = request =>
            {
                transformAttempts++;
                Assert.False(request?.Stream is true);
                request!.Stream = true;

                return new HttpRequestData();
            },
            TransformStreamFn = (_, _) => new SliceStream<LlmResponse>(
                new List<LlmResponse> { LlmTestData.ContentChunk("ok"), LlmResponse.DoneResponse }),
        };

        var options = new PipelineOptions { MaxSameChannelRetries = 1 };
        var pipeline = new PipelineType(executor, inbound, outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.False(res.Stream);
        Assert.NotNull(res.Response);

        var body = res.Response.Body;
        Assert.NotNull(body);
        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(body));
        Assert.Equal(2, attempts);
        Assert.Equal(2, transformAttempts);
    }

    // Port of TestPipeline_Process_StreamRetriesMetadataOnlyCleanEOFWithoutLeak.
    [Fact]
    public void StreamRetriesMetadataOnlyCleanEofWithoutLeak()
    {
        var inbound = new MockInbound
        {
            TransformRequestFn = _ => new LlmRequest { Stream = true },
            TransformStreamFn = TransformLlmContentToEvents,
        };

        var attempts = 0;
        var executor = new MockExecutor
        {
            DoStreamFn = (_, _) =>
            {
                attempts++;

                return new SliceStream<StreamEvent>(
                    new List<StreamEvent> { new() { Data = Encoding.UTF8.GetBytes("raw") } });
            },
        };

        var outbound = new MockOutbound
        {
            CanRetryFn = err => GoErrors.Is(err, LlmErrors.StreamIncomplete),
            PrepareForRetryFn = _ => null,
            TransformStreamFn = (_, _) =>
            {
                if (attempts == 1)
                {
                    return new SliceStream<LlmResponse>(new List<LlmResponse>
                    {
                        new()
                        {
                            Id = "first-attempt-metadata",
                            Object = "chat.completion.chunk",
                            Choices = [new Choice { Delta = new Message() }],
                        },
                    });
                }

                return new SliceStream<LlmResponse>(
                    new List<LlmResponse> { LlmTestData.ContentChunk("ok"), LlmResponse.DoneResponse });
            },
        };

        // Go: WithRetry(maxChannelRetries: 0, maxSameChannelRetries: 1, 0).
        var options = new PipelineOptions { MaxChannelRetries = 0, MaxSameChannelRetries = 1 };
        var pipeline = new PipelineType(executor, inbound, outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.Equal(2, attempts);
        Assert.True(res.Stream);
        Assert.NotNull(res.EventStream);

        var events = GoStreams.All(res.EventStream, out var streamErr);
        Assert.Null(streamErr);

        var payloads = events.Select(e => Encoding.UTF8.GetString(e.Data)).ToList();
        Assert.DoesNotContain("metadata", payloads);
        Assert.Equal(new List<string> { "ok", "[DONE]" }, payloads);
    }

    // Port of TestPipeline_Process_StreamRetriesPreCommitError.
    [Fact]
    public void StreamRetriesPreCommitError()
    {
        var upstreamError = new ResponseError
        {
            StatusCode = 500,
            Detail = new ErrorDetail
            {
                Message = "upstream stream failed before content",
                Type = "server_error",
            },
        };

        var inbound = new MockInbound
        {
            TransformRequestFn = _ => new LlmRequest { Stream = true },
            TransformStreamFn = TransformLlmContentToEvents,
        };

        var attempts = 0;
        var executor = new MockExecutor
        {
            DoStreamFn = (_, _) =>
            {
                attempts++;
                var reasoningIncluded = attempts == 1 ? "true" : "false";

                return ResponseHeaders.WithResponseHeaders(
                    new SliceStream<StreamEvent>(
                        new List<StreamEvent> { new() { Data = Encoding.UTF8.GetBytes("raw") } }),
                    new Dictionary<string, List<string>>(StringComparer.Ordinal)
                    {
                        [ResponseHeaders.ReasoningIncludedHeader] = new List<string> { reasoningIncluded },
                    });
            },
        };

        var prepareCalls = 0;
        var outbound = new MockOutbound
        {
            CanRetryFn = err => GoErrors.Is(err, upstreamError),
            PrepareForRetryFn = _ =>
            {
                prepareCalls++;
                return null;
            },
            TransformStreamFn = (_, _) =>
            {
                if (attempts == 1)
                {
                    return new LlmErrorAfterStream
                    {
                        Items = new List<LlmResponse>
                        {
                            new()
                            {
                                Object = "chat.completion.chunk",
                                Choices = [new Choice { Delta = new Message() }],
                            },
                        },
                        TerminalError = upstreamError,
                    };
                }

                return new SliceStream<LlmResponse>(
                    new List<LlmResponse> { LlmTestData.ContentChunk("ok"), LlmResponse.DoneResponse });
            },
        };

        // Go: WithRetry(maxChannelRetries: 0, maxSameChannelRetries: 1, 0).
        var options = new PipelineOptions { MaxChannelRetries = 0, MaxSameChannelRetries = 1 };
        var pipeline = new PipelineType(executor, inbound, outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.True(res.Stream);
        Assert.Equal(2, attempts);
        Assert.Equal(1, prepareCalls);
        Assert.Empty(HttpClientUtils.HeaderGet(
            ResponseHeaders.GetResponseHeaders(res.EventStream),
            ResponseHeaders.ReasoningIncludedHeader));

        var events = GoStreams.All(res.EventStream!, out var streamErr);
        Assert.Null(streamErr);
        Assert.Equal(2, events.Count);
        Assert.Equal("ok", Encoding.UTF8.GetString(events[0].Data));
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(events[1].Data));
    }

    // Port of TestPipeline_Process_StreamDoesNotRetryAfterContent.
    [Fact]
    public void StreamDoesNotRetryAfterContent()
    {
        var upstreamError = new ResponseError
        {
            StatusCode = 500,
            Detail = new ErrorDetail
            {
                Message = "upstream stream failed after content",
                Type = "server_error",
            },
        };

        var inbound = new MockInbound
        {
            TransformRequestFn = _ => new LlmRequest { Stream = true },
            TransformStreamFn = TransformLlmContentToEvents,
        };

        var attempts = 0;
        var executor = new MockExecutor
        {
            DoStreamFn = (_, _) =>
            {
                attempts++;

                return new SliceStream<StreamEvent>(
                    new List<StreamEvent> { new() { Data = Encoding.UTF8.GetBytes("raw") } });
            },
        };

        var prepareCalls = 0;
        var outbound = new MockOutbound
        {
            CanRetryFn = _ => true,
            PrepareForRetryFn = _ =>
            {
                prepareCalls++;
                return null;
            },
            TransformStreamFn = (_, _) => new LlmErrorAfterStream
            {
                Items = new List<LlmResponse> { LlmTestData.ContentChunk("partial") },
                TerminalError = upstreamError,
            },
        };

        // Go: WithRetry(maxChannelRetries: 0, maxSameChannelRetries: 1, 0).
        var options = new PipelineOptions { MaxChannelRetries = 0, MaxSameChannelRetries = 1 };
        var pipeline = new PipelineType(executor, inbound, outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.True(res.Stream);
        Assert.Equal(1, attempts);
        Assert.Equal(0, prepareCalls);

        var stream = res.EventStream!;
        Assert.True(stream.Next());
        Assert.Equal("partial", Encoding.UTF8.GetString(stream.Current!.Data));
        Assert.False(stream.Next());
        Assert.True(GoErrors.Is(stream.Err, upstreamError));
    }

    // Port of TestPipeline_Process_StreamKeepsMetadataPrivateUntilContent.
    [Fact]
    public void StreamKeepsMetadataPrivateUntilContent()
    {
        const int nonContentEventsBeforeContent = 64;

        var inbound = new MockInbound
        {
            TransformRequestFn = _ => new LlmRequest { Stream = true },
            TransformStreamFn = TransformLlmContentToEvents,
        };

        var attempts = 0;
        var executor = new MockExecutor
        {
            DoStreamFn = (_, _) =>
            {
                attempts++;

                return new SliceStream<StreamEvent>(
                    new List<StreamEvent> { new() { Data = Encoding.UTF8.GetBytes("raw") } });
            },
        };

        LlmErrorAfterStream? sourceStream = null;
        var outbound = new MockOutbound
        {
            CanRetryFn = _ => true,
            TransformStreamFn = (_, _) =>
            {
                var items = new List<LlmResponse>(nonContentEventsBeforeContent + 2);
                for (var i = 0; i < nonContentEventsBeforeContent; i++)
                {
                    items.Add(LlmTestData.EmptyChunk());
                }

                items.Add(LlmTestData.ContentChunk("ok"));
                items.Add(LlmResponse.DoneResponse);

                sourceStream = new LlmErrorAfterStream { Items = items };

                return sourceStream;
            },
        };

        // Go: WithRetry(maxChannelRetries: 0, maxSameChannelRetries: 1, 0).
        var options = new PipelineOptions { MaxChannelRetries = 0, MaxSameChannelRetries = 1 };
        var pipeline = new PipelineType(executor, inbound, outbound, options);

        var res = pipeline.Process(new HttpRequestData(), CancellationToken.None);

        Assert.NotNull(res);
        Assert.True(res.Stream);
        Assert.Equal(1, attempts);
        Assert.NotNull(sourceStream);
        Assert.Equal(nonContentEventsBeforeContent + 1, sourceStream.Index);

        var events = GoStreams.All(res.EventStream!, out var streamErr);
        Assert.Null(streamErr);
        Assert.Equal(nonContentEventsBeforeContent + 2, events.Count);
        Assert.Equal("metadata", Encoding.UTF8.GetString(events[0].Data));
        Assert.Equal("ok", Encoding.UTF8.GetString(events[nonContentEventsBeforeContent].Data));
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(events[nonContentEventsBeforeContent + 1].Data));
    }

    // Port of TestPipeline_Process_StreamFailsClosedAtPreCommitBufferLimit.
    [Fact]
    public void StreamFailsClosedAtPreCommitBufferLimit()
    {
        var inbound = new MockInbound
        {
            TransformRequestFn = _ => new LlmRequest { Stream = true },
        };

        var attempts = 0;
        var prepareForRetryCalls = 0;
        var nextChannelCalls = 0;
        var executor = new MockExecutor
        {
            DoStreamFn = (_, _) =>
            {
                attempts++;

                return new SliceStream<StreamEvent>(
                    new List<StreamEvent> { new() { Data = Encoding.UTF8.GetBytes("raw") } });
            },
        };

        var sourceEvents = new List<LlmResponse>(PipelineType.MaxPreCommitBufferedEvents + 1);
        for (var i = 0; i <= PipelineType.MaxPreCommitBufferedEvents; i++)
        {
            sourceEvents.Add(LlmTestData.EmptyChunk());
        }

        var sourceStream = new LlmErrorAfterStream { Items = sourceEvents };
        var outbound = new MockOutbound
        {
            HasMoreChannelsFn = () => true,
            NextChannelFn = _ =>
            {
                nextChannelCalls++;
                return null;
            },
            TransformStreamFn = (_, _) => sourceStream,
            CanRetryFn = _ => true,
            PrepareForRetryFn = _ =>
            {
                prepareForRetryCalls++;
                return null;
            },
        };

        // Go: WithRetry(maxChannelRetries: 1, maxSameChannelRetries: 1, 0).
        var options = new PipelineOptions { MaxChannelRetries = 1, MaxSameChannelRetries = 1 };
        var pipeline = new PipelineType(executor, inbound, outbound, options);

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            pipeline.Process(new HttpRequestData(), CancellationToken.None);
        });

        Assert.True(GoErrors.Is(ex, PipelineErrors.PreCommitBufferExceeded));
        Assert.Equal(1, attempts);
        Assert.Equal(0, prepareForRetryCalls);
        Assert.Equal(0, nextChannelCalls);
        Assert.True(sourceStream.Closed);
    }

    // Port of TestPipeline_PreReadLlmStreamReturnsIncompleteForMetadataOnlyEOFWithRetry.
    [Fact]
    public void PreReadLlmStreamReturnsIncompleteForMetadataOnlyEofWithRetry()
    {
        var sourceStream = new LlmErrorAfterStream
        {
            Items = new List<LlmResponse> { LlmTestData.EmptyChunk() },
        };
        var pipeline = new PipelineType(null!, null!, null!, new PipelineOptions { MaxSameChannelRetries = 1 });

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            pipeline.PreReadLlmStream(sourceStream, null, CancellationToken.None);
        });

        Assert.True(GoErrors.Is(ex, LlmErrors.StreamIncomplete));
        Assert.True(sourceStream.Closed);
    }

    // Port of TestPipeline_PreReadLlmStreamReturnsIncompleteForEmptyEOFWithRetry.
    [Fact]
    public void PreReadLlmStreamReturnsIncompleteForEmptyEofWithRetry()
    {
        var sourceStream = new LlmErrorAfterStream();
        var pipeline = new PipelineType(null!, null!, null!, new PipelineOptions { MaxSameChannelRetries = 1 });

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            pipeline.PreReadLlmStream(sourceStream, null, CancellationToken.None);
        });

        Assert.True(GoErrors.Is(ex, LlmErrors.StreamIncomplete));
        Assert.True(sourceStream.Closed);
    }

    // Port of TestPipeline_PreReadLlmStreamFailsClosedAtPreCommitByteLimit.
    [Fact]
    public void PreReadLlmStreamFailsClosedAtPreCommitByteLimit()
    {
        const int metadataEvents = 9;
        const int metadataBytesPerEvent = 1024 * 1024;

        var sourceEvents = new List<LlmResponse>(metadataEvents);
        for (var i = 0; i < metadataEvents; i++)
        {
            sourceEvents.Add(new LlmResponse
            {
                Id = new string('m', metadataBytesPerEvent),
                Object = "chat.completion.chunk",
                Choices = [new Choice { Delta = new Message() }],
            });
        }

        var sourceStream = new LlmErrorAfterStream { Items = sourceEvents };
        var pipeline = new PipelineType(null!, null!, null!, new PipelineOptions { MaxSameChannelRetries = 1 });

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            pipeline.PreReadLlmStream(sourceStream, null, CancellationToken.None);
        });

        Assert.True(GoErrors.Is(ex, PipelineErrors.PreCommitBufferExceeded));
        Assert.True(sourceStream.Closed);
    }

    // Port of TestPipeline_PreReadLlmStreamAllowsLargeFirstMeaningfulEvent.
    [Fact]
    public void PreReadLlmStreamAllowsLargeFirstMeaningfulEvent()
    {
        var largeContent = new string('x', PipelineType.MaxPreCommitBufferedBytes + 1);
        var sourceStream = new LlmErrorAfterStream
        {
            Items = new List<LlmResponse>
            {
                LlmTestData.ContentChunk(largeContent),
                LlmResponse.DoneResponse,
            },
        };
        var pipeline = new PipelineType(null!, null!, null!, new PipelineOptions { MaxSameChannelRetries = 1 });

        var stream = pipeline.PreReadLlmStream(sourceStream, null, CancellationToken.None);

        Assert.False(sourceStream.Closed);
        Assert.True(stream.Next());
        Assert.Equal(largeContent, stream.Current!.Choices[0].Delta!.Content!.Content);
        stream.Dispose();
    }

    // Port of TestPipeline_Process_StreamEventTooLargeBypassesAllRetries.
    [Fact]
    public void StreamEventTooLargeBypassesAllRetries()
    {
        var inbound = new MockInbound
        {
            TransformRequestFn = _ => new LlmRequest { Stream = true },
        };

        const int limit = 1024;
        var raw = "data: " + new string('x', limit + 1) + "\n\n";
        var body = new DisposeTrackingStream<StreamEvent>(new SseEventStream(raw, limit));

        var attempts = 0;
        var prepareForRetryCalls = 0;
        var nextChannelCalls = 0;
        var executor = new MockExecutor
        {
            DoStreamFn = (_, _) =>
            {
                attempts++;
                return body;
            },
        };
        var outbound = new MockOutbound
        {
            HasMoreChannelsFn = () => true,
            NextChannelFn = _ =>
            {
                nextChannelCalls++;
                return null;
            },
            TransformStreamFn = (_, rawStream) =>
                new MapStream<StreamEvent, LlmResponse>(rawStream, static _ => LlmTestData.EmptyChunk()),
            CanRetryFn = _ => true,
            PrepareForRetryFn = _ =>
            {
                prepareForRetryCalls++;
                return null;
            },
        };

        // Go: WithRetry(maxChannelRetries: 1, maxSameChannelRetries: 1, 0).
        var options = new PipelineOptions { MaxChannelRetries = 1, MaxSameChannelRetries = 1 };
        var pipeline = new PipelineType(executor, inbound, outbound, options);

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            pipeline.Process(new HttpRequestData(), CancellationToken.None);
        });

        Assert.True(GoErrors.Is(ex, StreamLimits.EventTooLarge));
        Assert.Equal(1, attempts);
        Assert.Equal(0, prepareForRetryCalls);
        Assert.Equal(0, nextChannelCalls);
        Assert.True(body.Disposed);
    }

    /// <summary>Port of transformLlmContentToEvents.</summary>
    private static MapStream<LlmResponse, StreamEvent> TransformLlmContentToEvents(IStream<LlmResponse> stream)
        => new(stream, static resp =>
        {
            if (ReferenceEquals(resp, LlmResponse.DoneResponse) || resp.Object == "[DONE]")
            {
                return new StreamEvent { Data = Encoding.UTF8.GetBytes("[DONE]") };
            }

            foreach (var choice in resp.Choices)
            {
                if (choice.Delta?.Content?.Content is { } content)
                {
                    return new StreamEvent { Data = Encoding.UTF8.GetBytes(content) };
                }
            }

            return new StreamEvent { Data = Encoding.UTF8.GetBytes("metadata") };
        });
}
