using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of the response and stream half of
/// internal/server/orchestrator/pass_through_test.go and
/// pass_through_budget_test.go: raw provider responses and events are captured
/// before decoding and replayed to the client, with a bounded pre-attachment
/// backlog and cancellation-safe channel semantics.
/// </summary>
public sealed class PassThroughStreamTests
{
    private const string SettingsEnabled = "{\"passThroughBody\":true}";

    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(5);

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static LlmRequest Request(bool? stream, string apiFormat = ApiFormats.OpenAIChatCompletion)
        => new()
        {
            Model = "gpt-4o",
            ApiFormat = apiFormat,
            Stream = stream,
            RawRequest = new HttpRequestData
            {
                Method = "POST",
                Url = "http://gateway.test/v1",
                ApiFormat = apiFormat,
                Body = Bytes("{\"model\":\"gpt-4o\"}"),
            },
        };

    /// <summary>
    /// Builds the transformer after one outbound transform (so the original
    /// stream intent is captured) and stores the raw provider request the way
    /// applyPassThroughRequestBody does.
    /// </summary>
    private static PersistentOutboundTransformer Outbound(
        LlmRequest request,
        string? channelSettings = SettingsEnabled,
        string? rawProviderApiFormat = null,
        IOutboundTransformer? transformer = null)
    {
        var candidate = new ChannelCandidate
        {
            ChannelId = 1,
            ChannelName = "test-channel",
            ActualModel = request.Model,
            Type = "openai",
            BaseUrl = "http://upstream.test",
            Credentials = "{\"apiKey\":\"sk-test\"}",
            PassThroughSettings = ChannelPassThroughSettings.Parse(channelSettings),
        };

        var outbound = new PersistentOutboundTransformer(
            [candidate],
            TimeProvider.System,
            new RecordingMetricsRecorder(),
            (_, _, _) => transformer ?? new EchoOutboundTransformer());

        outbound.TransformRequest(request);
        outbound.RawProviderRequest = new HttpRequestData
        {
            Method = "POST",
            Url = "http://upstream.test/v1",
            ApiFormat = rawProviderApiFormat ?? request.ApiFormat ?? ApiFormats.OpenAIChatCompletion,
        };

        return outbound;
    }

    private static PassThroughStreamState State(
        BlockingCollection<StreamEvent> channel,
        RawStreamBacklog? backlog = null,
        PassThroughErrorRef? errorRef = null,
        Action? cancel = null)
        => new()
        {
            RawChannel = channel,
            Backlog = backlog ?? new RawStreamBacklog(),
            ErrorRef = errorRef ?? new PassThroughErrorRef(),
            Cancel = cancel ?? (() => { }),
        };

    private static BlockingCollection<StreamEvent> Channel(int capacity = 8)
        => new(new ConcurrentQueue<StreamEvent>(), capacity);

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    private static List<StreamEvent> Drain(IStream<StreamEvent> stream, int limit)
    {
        var events = new List<StreamEvent>();
        while (events.Count < limit && stream.Next())
        {
            events.Add(stream.Current!);
        }

        return events;
    }

    /// <summary>Yields a fixed event list, then reports a terminal error.</summary>
    private sealed class ErrorStream(Exception error) : IStream<StreamEvent>
    {
        public StreamEvent? Current => null;

        public Exception? Err => error;

        public bool Next() => false;

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// Parks the first read until the stream is disposed, like an upstream
    /// connection waiting on the client.
    /// </summary>
    private sealed class BlockingStream : IStream<StreamEvent>
    {
        private readonly ManualResetEventSlim _started = new(false);
        private readonly ManualResetEventSlim _closed = new(false);

        public StreamEvent? Current => null;

        public Exception? Err => null;

        public bool WaitStarted(TimeSpan timeout) => _started.Wait(timeout);

        public bool WaitClosed(TimeSpan timeout) => _closed.Wait(timeout);

        public bool Next()
        {
            _started.Set();
            _closed.Wait(SafetyTimeout);
            return false;
        }

        public void Dispose() => _closed.Set();
    }

    /// <summary>
    /// Yields events up to <paramref name="gateAt"/>, then waits for
    /// <see cref="Release"/>, so the test can attach the consumer in between.
    /// A gate that never opens is recorded as a timeout instead of hanging.
    /// </summary>
    private sealed class GatedStream(IReadOnlyList<StreamEvent> events, int gateAt) : IStream<StreamEvent>
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _next;

        public StreamEvent? Current { get; private set; }

        public Exception? Err => null;

        public bool TimedOut { get; private set; }

        public void Release() => _release.Set();

        public bool Next()
        {
            if (_next == gateAt && !_release.Wait(SafetyTimeout))
            {
                TimedOut = true;
            }

            if (_next >= events.Count)
            {
                return false;
            }

            Current = events[_next];
            _next++;
            return true;
        }

        public void Dispose() => _release.Set();
    }

    /// <summary>Signals when the inner stream has been read to its end.</summary>
    private sealed class DoneStream(IStream<StreamEvent> inner) : IStream<StreamEvent>
    {
        private readonly ManualResetEventSlim _done = new(false);

        public StreamEvent? Current => inner.Current;

        public Exception? Err => inner.Err;

        public bool WaitDone(TimeSpan timeout) => _done.Wait(timeout);

        public bool Next()
        {
            var next = inner.Next();
            if (!next)
            {
                _done.Set();
            }

            return next;
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// Minimal provider transformer: echoes raw event bytes through the unified
    /// model so the pipeline reads events without touching a provider schema.
    /// </summary>
    private sealed class EchoOutboundTransformer : IOutboundTransformer
    {
        public string ApiFormat => ApiFormats.OpenAIChatCompletion;

        public HttpRequestData TransformRequest(LlmRequest? request)
            => new()
            {
                Method = "POST",
                Url = "http://upstream.test/v1",
                ApiFormat = ApiFormat,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = ["application/json"],
                },
                Body = request?.RawRequest?.Body,
            };

        public LlmResponse TransformResponse(HttpResponseData? response)
            => new() { Model = Encoding.UTF8.GetString(response?.Body ?? []) };

        public IStream<LlmResponse> TransformStream(
            HttpRequestData? request,
            IStream<StreamEvent> stream)
            => new MapStream<StreamEvent, LlmResponse>(
                stream,
                @event => new LlmResponse { Model = Encoding.UTF8.GetString(@event.Data) });

        public ResponseError TransformError(HttpClientError? error)
            => throw new NotSupportedException();

        public byte[] AggregateStreamChunks(
            HttpRequestData? request,
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
        {
            meta = new ResponseMeta();
            return [];
        }
    }

    private sealed class StubInboundTransformer : IInboundTransformer
    {
        public LlmRequest TransformRequest(HttpRequestData? request)
        {
            using var document = JsonDocument.Parse(request?.Body ?? []);
            var root = document.RootElement;
            return new LlmRequest
            {
                Model = root.GetProperty("model").GetString() ?? string.Empty,
                ApiFormat = ApiFormats.OpenAIChatCompletion,
                Stream = root.TryGetProperty("stream", out var stream) && stream.GetBoolean(),
                RawRequest = request,
            };
        }

        public HttpResponseData TransformResponse(LlmResponse? response)
            => new() { StatusCode = 200, Body = Bytes("transformed") };

        public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
            => new MapStream<LlmResponse, StreamEvent>(stream, response => TestChannels.Data(response.Model));

        public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
        {
            _ = chunks;
            meta = new ResponseMeta();
            return [];
        }

        public HttpResponseData TransformError(Exception? error) => new() { StatusCode = 500 };
    }

    /// <summary>Counts the unified events the pipeline actually consumed.</summary>
    private sealed class TrackingLlmMiddleware : DummyMiddleware
    {
        private int _events;

        public TrackingLlmMiddleware()
            : base("tracking-llm")
        {
        }

        public bool Called { get; private set; }

        public int Events => Volatile.Read(ref _events);

        public override IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream)
        {
            Called = true;
            return new CountingStream(stream, this);
        }

        private sealed class CountingStream(IStream<LlmResponse> inner, TrackingLlmMiddleware owner)
            : IStream<LlmResponse>
        {
            public LlmResponse? Current => inner.Current;

            public Exception? Err => inner.Err;

            public bool Next()
            {
                if (!inner.Next())
                {
                    return false;
                }

                Interlocked.Increment(ref owner._events);
                return true;
            }

            public void Dispose() => inner.Dispose();
        }
    }

    private static HttpRequestData ClientRequest(bool stream)
        => new()
        {
            Method = "POST",
            Url = "http://gateway.test/v1/chat/completions",
            Path = "/v1/chat/completions",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Bytes(stream
                ? "{\"model\":\"gpt-4o\",\"stream\":true}"
                : "{\"model\":\"gpt-4o\"}"),
        };

    // ------------------------------------------------------------------
    // captureRawProviderResponse / applyPassThroughResponse
    // ------------------------------------------------------------------

    [Fact]
    public void CaptureResponseStoresItAndReturnsIt()
    {
        var outbound = Outbound(Request(stream: null));
        var raw = new HttpResponseData { StatusCode = 200, Body = Bytes("ok") };

        var result = new CaptureRawProviderResponseMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformOutboundRawResponse(raw);

        Assert.Same(raw, result);
        Assert.Same(raw, outbound.RawProviderResponse);
    }

    [Fact]
    public void CaptureResponseDisabledDoesNotStore()
    {
        var outbound = Outbound(Request(stream: null), channelSettings: null);
        var raw = new HttpResponseData { StatusCode = 200, Body = Bytes("ok") };

        var result = new CaptureRawProviderResponseMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformOutboundRawResponse(raw);

        Assert.Same(raw, result);
        Assert.Null(outbound.RawProviderResponse);
    }

    [Fact]
    public void ApplyResponseDisabledReturnsTransformed()
    {
        var outbound = Outbound(Request(stream: null), channelSettings: null);
        outbound.RawProviderResponse = new HttpResponseData { StatusCode = 200, Body = Bytes("raw") };
        var transformed = new HttpResponseData { StatusCode = 200, Body = Bytes("transformed") };

        var result = new PassThroughResponseMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformInboundRawResponse(transformed);

        Assert.Same(transformed, result);
    }

    [Fact]
    public void ApplyResponseEnabledReturnsACloneOfRaw()
    {
        // The reference returns &cloned (a struct copy), not the captured
        // pointer, so later mutations of the raw response never leak out.
        var outbound = Outbound(Request(stream: null));
        var raw = new HttpResponseData { StatusCode = 200, Body = Bytes("raw") };
        outbound.RawProviderResponse = raw;
        var transformed = new HttpResponseData { StatusCode = 200, Body = Bytes("transformed") };

        var result = new PassThroughResponseMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformInboundRawResponse(transformed);

        Assert.NotSame(raw, result);
        Assert.NotSame(transformed, result);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("raw", Encoding.UTF8.GetString(result.Body!));
        Assert.Same(raw.Headers, result.Headers);
        Assert.Same(raw.Body, result.Body);
    }

    [Fact]
    public void ApplyResponseMismatchedApiFormatReturnsTransformed()
    {
        var outbound = Outbound(
            Request(stream: null),
            rawProviderApiFormat: ApiFormats.AnthropicMessage);
        outbound.RawProviderResponse = new HttpResponseData { StatusCode = 200, Body = Bytes("raw") };
        var transformed = new HttpResponseData { StatusCode = 200, Body = Bytes("transformed") };

        var result = new PassThroughResponseMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformInboundRawResponse(transformed);

        Assert.Same(transformed, result);
    }

    [Fact]
    public void ApplyResponseWithoutAttemptReturnsTransformed()
    {
        // Reference TestApplyPassThroughResponse_NilLlmRequest: without a
        // unified request the pass-through check cannot run.
        var outbound = new PersistentOutboundTransformer(
            [],
            TimeProvider.System,
            new RecordingMetricsRecorder());
        outbound.RawProviderResponse = new HttpResponseData { StatusCode = 200, Body = Bytes("raw") };
        var transformed = new HttpResponseData { StatusCode = 200, Body = Bytes("transformed") };

        var result = new PassThroughResponseMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformInboundRawResponse(transformed);

        Assert.Same(transformed, result);
    }

    [Fact]
    public void ApplyResponseEnabledWithoutRawFallsBackToTransformed()
    {
        var outbound = Outbound(Request(stream: null));
        var transformed = new HttpResponseData { StatusCode = 200, Body = Bytes("transformed") };

        var result = new PassThroughResponseMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformInboundRawResponse(transformed);

        Assert.Same(transformed, result);
    }

    // ------------------------------------------------------------------
    // captureRawProviderStream
    // ------------------------------------------------------------------

    [Fact]
    public void CaptureStreamDisabledReturnsSameStream()
    {
        var outbound = Outbound(Request(stream: true), channelSettings: null);
        var original = new ListStream<StreamEvent>([TestChannels.Data("e1")]);

        var result = new CaptureRawProviderStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformOutboundRawStream(original);

        Assert.Same(original, result);
        Assert.Null(outbound.PassThroughStream);
    }

    [Fact]
    public void CaptureStreamWithoutAttemptReturnsSameStream()
    {
        // Reference TestCaptureRawProviderStream_NilLlmRequest.
        var outbound = new PersistentOutboundTransformer(
            [],
            TimeProvider.System,
            new RecordingMetricsRecorder());
        var original = new ListStream<StreamEvent>([TestChannels.Data("e1")]);

        var result = new CaptureRawProviderStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformOutboundRawStream(original);

        Assert.Same(original, result);
        Assert.Null(outbound.PassThroughStream);
    }

    [Fact]
    public void CaptureStreamMismatchedApiFormatReturnsSameStream()
    {
        var outbound = Outbound(
            Request(stream: true),
            rawProviderApiFormat: ApiFormats.AnthropicMessage);
        var original = new ListStream<StreamEvent>([TestChannels.Data("e1")]);

        var result = new CaptureRawProviderStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformOutboundRawStream(original);

        Assert.Same(original, result);
    }

    [Fact]
    public void CaptureStreamFansOutToPipelineAndPassThrough()
    {
        // Reference TestCaptureRawProviderStream_FansOut: the pipeline reads
        // more events than the raw channel buffers before the consumer
        // attaches; the backlog must hold them without stalling the fan-out.
        const int preReadCount = 100;
        var events = Enumerable.Range(1, preReadCount + 3)
            .Select(i => TestChannels.Data("{\"id\":\"evt" + i + "\"}"))
            .ToList();
        var source = new GatedStream(events, preReadCount);

        var outbound = Outbound(Request(stream: true));
        var result = new CaptureRawProviderStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformOutboundRawStream(source);

        Assert.NotNull(outbound.PassThroughStream);

        var pipelineEvents = new List<StreamEvent>();
        for (var i = 0; i < preReadCount; i++)
        {
            Assert.True(result.Next());
            pipelineEvents.Add(result.Current!);
        }

        var passThrough = new PassThroughStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformInboundRawStream(new ListStream<StreamEvent>([]));

        source.Release();

        pipelineEvents.AddRange(Drain(result, events.Count));
        var passThroughEvents = Drain(passThrough, events.Count);

        Assert.False(source.TimedOut);
        Assert.Equal<StreamEvent>(events, pipelineEvents);
        Assert.Equal<StreamEvent>(events, passThroughEvents);
    }

    [Fact]
    public void CaptureStreamPropagatesTheUpstreamError()
    {
        // Reference TestCaptureRawProviderStream_PropagatesError.
        var error = new IOException("stream error");
        var outbound = Outbound(Request(stream: true));
        var result = new CaptureRawProviderStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformOutboundRawStream(new ErrorStream(error));

        Assert.Empty(Drain(result, 10));

        Assert.Same(error, result.Err);
        Assert.Same(error, outbound.PassThroughStream!.ErrorRef.Error);
    }

    [Fact]
    public void CaptureStreamDisposeStopsABlockedUpstream()
    {
        // Reference TestCaptureRawProviderStream_CloseStopsBlockedUpstream.
        var source = new BlockingStream();
        var outbound = Outbound(Request(stream: true));
        var result = new CaptureRawProviderStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformOutboundRawStream(source);

        Assert.True(source.WaitStarted(SafetyTimeout));

        result.Dispose();

        Assert.True(source.WaitClosed(SafetyTimeout));
        Assert.True(WaitUntil(
            () => outbound.PassThroughStream!.RawChannel.IsCompleted,
            SafetyTimeout));
    }

    // ------------------------------------------------------------------
    // applyPassThroughStream
    // ------------------------------------------------------------------

    [Fact]
    public void ApplyStreamDisabledReturnsSameStream()
    {
        var outbound = Outbound(Request(stream: true), channelSettings: null);
        var transformed = new ListStream<StreamEvent>([TestChannels.Data("t1")]);

        var result = new PassThroughStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformInboundRawStream(transformed);

        Assert.Same(transformed, result);
    }

    [Fact]
    public void ApplyStreamWithoutRawChannelReturnsSameStream()
    {
        var outbound = Outbound(Request(stream: true));
        var transformed = new ListStream<StreamEvent>([TestChannels.Data("t1")]);

        var result = new PassThroughStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformInboundRawStream(transformed);

        Assert.Same(transformed, result);
    }

    [Fact]
    public void ApplyStreamReturnsRawEvents()
    {
        // Reference TestApplyPassThroughStream_ReturnsRawEvents.
        var outbound = Outbound(Request(stream: true));
        var channel = Channel();
        var rawEvents = new[]
        {
            TestChannels.Data("{\"id\":\"r1\"}"),
            TestChannels.Data("{\"id\":\"r2\"}"),
        };
        foreach (var @event in rawEvents)
        {
            channel.Add(@event);
        }

        channel.CompleteAdding();
        outbound.PassThroughStream = State(channel);

        var transformed = new ListStream<StreamEvent>(
        [
            TestChannels.Data("{\"id\":\"t1\"}"),
            TestChannels.Data("{\"id\":\"t2\"}"),
        ]);

        var result = new PassThroughStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformInboundRawStream(transformed);

        Assert.Equal<StreamEvent>(rawEvents, Drain(result, 10));
    }

    [Fact]
    public void ApplyStreamPrependsPreAttachmentEvents()
    {
        var outbound = Outbound(Request(stream: true));
        var backlog = new RawStreamBacklog();
        var held1 = TestChannels.Data("{\"id\":\"b1\"}");
        var held2 = TestChannels.Data("{\"id\":\"b2\"}");
        Assert.True(backlog.Hold(held1, out _));
        Assert.True(backlog.Hold(held2, out _));

        var channel = Channel();
        var raw = TestChannels.Data("{\"id\":\"r1\"}");
        channel.Add(raw);
        channel.CompleteAdding();
        outbound.PassThroughStream = State(channel, backlog);

        var result = new PassThroughStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformInboundRawStream(new ListStream<StreamEvent>([]));

        Assert.Equal<StreamEvent>([held1, held2, raw], Drain(result, 10));
    }

    [Fact]
    public void ApplyStreamReturnsTheRawError()
    {
        var outbound = Outbound(Request(stream: true));
        var errorRef = new PassThroughErrorRef();
        var error = new IOException("stream error");
        errorRef.SetIfNull(error);

        var channel = Channel();
        channel.CompleteAdding();
        outbound.PassThroughStream = State(channel, errorRef: errorRef);

        var result = new PassThroughStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformInboundRawStream(new ListStream<StreamEvent>([]));

        Assert.Empty(Drain(result, 10));
        Assert.Same(error, result.Err);
    }

    [Fact]
    public void ApplyStreamDrainsTheTransformedStream()
    {
        // Reference TestApplyPassThroughStream_DrainsInner: the discarded inner
        // stream still runs the LLM middleware chain to completion.
        var outbound = Outbound(Request(stream: true));
        var channel = Channel();
        channel.Add(TestChannels.Data("{\"id\":\"r1\"}"));
        channel.CompleteAdding();
        outbound.PassThroughStream = State(channel);

        var transformed = new DoneStream(new ListStream<StreamEvent>(
        [
            TestChannels.Data("{\"id\":\"t1\"}"),
        ]));

        var result = new PassThroughStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformInboundRawStream(transformed);

        Assert.Single(Drain(result, 10));
        Assert.True(transformed.WaitDone(SafetyTimeout));
    }

    // ------------------------------------------------------------------
    // passThroughChannelStream
    // ------------------------------------------------------------------

    [Fact]
    public void ChannelStreamDrainsBufferedEventsAfterCancel()
    {
        // Reference TestPassThroughChannelStream_DrainsBufferedEventsAfterCancel:
        // a client disconnect must not lose the terminal event already buffered.
        using var cts = new CancellationTokenSource();
        var channel = Channel(capacity: 2);
        var chunk = TestChannels.Data("{\"id\":\"chunk\"}");
        var done = TestChannels.Data("[DONE]");
        channel.Add(chunk);
        channel.Add(done);
        channel.CompleteAdding();
        cts.Cancel();

        var stream = new PassThroughChannelStream(cts.Token, channel, new PassThroughErrorRef(), null);
        var events = Drain(stream, 10);

        Assert.Equal<StreamEvent>([chunk, done], events);
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(events[1].Data));
    }

    [Fact]
    public void ChannelStreamStopsAtEmptyBufferAfterCancel()
    {
        // Reference TestPassThroughChannelStream_StopsAtEmptyBufferAfterCancel:
        // after cancellation Next() must never block on the producer.
        using var cts = new CancellationTokenSource();
        var channel = Channel(capacity: 2);
        var chunk = TestChannels.Data("{\"id\":\"chunk\"}");
        channel.Add(chunk);
        cts.Cancel();

        var upstreamCanceled = false;
        var stream = new PassThroughChannelStream(
            cts.Token,
            channel,
            new PassThroughErrorRef(),
            () => upstreamCanceled = true);

        Assert.True(stream.Next());
        Assert.Equal(chunk, stream.Current);
        Assert.False(stream.Next());
        Assert.True(upstreamCanceled);
    }

    // ------------------------------------------------------------------
    // rawStreamBacklog
    // ------------------------------------------------------------------

    [Fact]
    public void BacklogExactCountBudget()
    {
        // Reference TestRawStreamBacklog_ExactBudgets/count.
        var backlog = new RawStreamBacklog();
        for (var i = 0; i < RawStreamBacklog.MaxRawPreAttachEvents; i++)
        {
            Assert.True(backlog.Hold(TestChannels.Data("{}"), out var error));
            Assert.Null(error);
        }

        Assert.False(backlog.Hold(TestChannels.Data("{}"), out var overflow));
        Assert.NotNull(overflow);
        Assert.True(GoErrors.Is(overflow, PipelineErrors.PreCommitBufferExceeded));
        Assert.Equal(RawStreamBacklog.MaxRawPreAttachEvents, backlog.Attach().Length);

        // Once attached, events belong to the raw channel.
        Assert.False(backlog.Hold(TestChannels.Data("{}"), out var afterAttach));
        Assert.Null(afterAttach);
    }

    [Fact]
    public void BacklogByteBudgetIncludesTypeAndId()
    {
        // Reference TestRawStreamBacklog_ExactBudgets/bytes include type and ID.
        var backlog = new RawStreamBacklog();
        var fitting = new StreamEvent
        {
            Type = "ping",
            LastEventId = "1",
            Data = new byte[RawStreamBacklog.MaxRawPreAttachBytes - 5],
        };

        Assert.True(backlog.Hold(fitting, out var firstError));
        Assert.Null(firstError);

        Assert.False(backlog.Hold(TestChannels.Data("x"), out var overflow));
        Assert.NotNull(overflow);
        Assert.True(GoErrors.Is(overflow, PipelineErrors.PreCommitBufferExceeded));
        Assert.Single(backlog.Attach());
    }

    // ------------------------------------------------------------------
    // rawStreamQueue (live f272549f budgets)
    // ------------------------------------------------------------------

    [Fact]
    public void QueueRejectsASingleEventAboveTheLiveEventLimit()
    {
        // rawStreamBacklog.reserve: a single event above 32 MiB fails even
        // after attachment, with the pre-commit budget sentinel.
        var backlog = new RawStreamBacklog();
        _ = backlog.Attach();

        var error = Assert.ThrowsAny<Exception>(
            () => backlog.Reserve(
                CancellationToken.None,
                RawStreamBacklog.MaxRawEventBytes + 1));

        Assert.True(GoErrors.Is(error, PipelineErrors.PreCommitBufferExceeded));
    }

    [Fact]
    public void QueuePostAttachByteBudgetWaitsForTheConsumerToRelease()
    {
        // rawStreamBacklog.reserve after attachment: capacity is 128 MiB and
        // a full budget blocks instead of failing.
        var backlog = new RawStreamBacklog();
        _ = backlog.Attach();
        var chunk = RawStreamBacklog.MaxRawEventBytes;
        for (var i = 0; i < RawStreamBacklog.MaxRawQueueBytes / chunk; i++)
        {
            backlog.Reserve(CancellationToken.None, chunk);
        }

        using var completed = new ManualResetEventSlim(false);
        var blocked = new Thread(() =>
        {
            backlog.Reserve(CancellationToken.None, 1);
            completed.Set();
        })
        {
            IsBackground = true,
        };
        blocked.Start();

        Assert.False(completed.Wait(TimeSpan.FromMilliseconds(150)));

        backlog.Release(chunk);

        Assert.True(completed.Wait(SafetyTimeout));
    }

    [Fact]
    public void QueueReserveStopsWaitingWhenTheAttemptIsCanceled()
    {
        var backlog = new RawStreamBacklog();
        _ = backlog.Attach();
        var chunk = RawStreamBacklog.MaxRawEventBytes;
        for (var i = 0; i < RawStreamBacklog.MaxRawQueueBytes / chunk; i++)
        {
            backlog.Reserve(CancellationToken.None, chunk);
        }

        using var cts = new CancellationTokenSource();
        Exception? caught = null;
        using var completed = new ManualResetEventSlim(false);
        var blocked = new Thread(() =>
        {
            try
            {
                backlog.Reserve(cts.Token, 1);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
        };
        blocked.Start();

        cts.Cancel();

        Assert.True(completed.Wait(SafetyTimeout));
        Assert.IsAssignableFrom<OperationCanceledException>(caught);
    }

    [Fact]
    public void QueueAttachKeepsHeldEventsChargedUntilTheyAreRead()
    {
        // attach moves the held events without releasing their budget; each
        // read releases that event's bytes (live raw_stream_queue.go:25-100).
        var backlog = new RawStreamBacklog();
        var queue = new RawStreamQueue(backlog);
        var held = TestChannels.Data("{}");
        Assert.True(backlog.Hold(held, out _));
        var heldSize = RawStreamBacklog.EventSize(held);
        queue.Attach();

        var available = RawStreamBacklog.MaxRawQueueBytes - heldSize;
        while (available > 0)
        {
            var chunk = Math.Min(available, RawStreamBacklog.MaxRawEventBytes);
            backlog.Reserve(CancellationToken.None, chunk);
            available -= chunk;
        }

        using var completed = new ManualResetEventSlim(false);
        var blocked = new Thread(() =>
        {
            backlog.Reserve(CancellationToken.None, 1);
            completed.Set();
        })
        {
            IsBackground = true,
        };
        blocked.Start();

        Assert.False(completed.Wait(TimeSpan.FromMilliseconds(150)));

        Assert.Same(held, queue.Next(CancellationToken.None));
        Assert.True(completed.Wait(SafetyTimeout));
    }

    [Fact]
    public void QueueBlocksLiveSendsUntilTheBoundedChannelDrains()
    {
        // Reference TestRawStreamQueue_BlocksLargeLiveEventsUntilConsumerReads:
        // a full channel applies backpressure instead of dropping the event.
        var backlog = new RawStreamBacklog();
        _ = backlog.Attach();
        var queue = new RawStreamQueue(backlog);
        for (var i = 0; i < RawStreamQueue.ChannelCapacity; i++)
        {
            queue.Enqueue(CancellationToken.None, new StreamEvent());
        }

        var large = new StreamEvent { Data = new byte[16 * 1024 * 1024] };
        using var completed = new ManualResetEventSlim(false);
        var blocked = new Thread(() =>
        {
            queue.Enqueue(CancellationToken.None, large);
            completed.Set();
        })
        {
            IsBackground = true,
        };
        blocked.Start();

        Assert.False(completed.Wait(TimeSpan.FromMilliseconds(150)));
        Assert.True(queue.Channel.TryTake(out _));
        Assert.True(completed.Wait(SafetyTimeout));

        var drained = new List<StreamEvent>();
        while (queue.Channel.TryTake(out var sent))
        {
            drained.Add(sent);
        }

        Assert.Same(large, drained[^1]);
    }

    [Fact]
    public void QueueNextReturnsHeldEventsBeforeLiveEvents()
    {
        var backlog = new RawStreamBacklog();
        var queue = new RawStreamQueue(backlog);
        var held1 = TestChannels.Data("held-1");
        var held2 = TestChannels.Data("held-2");
        Assert.True(backlog.Hold(held1, out _));
        Assert.True(backlog.Hold(held2, out _));
        queue.Attach();
        var live = TestChannels.Data("live");
        queue.Enqueue(CancellationToken.None, live);
        queue.Channel.CompleteAdding();

        Assert.Same(held1, queue.Next(CancellationToken.None));
        Assert.Same(held2, queue.Next(CancellationToken.None));
        Assert.Same(live, queue.Next(CancellationToken.None));
        Assert.Null(queue.Next(CancellationToken.None));
    }

    [Fact]
    public void CaptureStreamReportsThePreAttachmentBudgetAsItsError()
    {
        // A pipeline that pre-reads filtered events beyond the backlog budget
        // must fail explicitly instead of stalling or attaching (live
        // TestPassThroughAnthropicStream_RawBudget).
        var events = Enumerable
            .Range(0, RawStreamBacklog.MaxRawPreAttachEvents + 1)
            .Select(i => new StreamEvent { Type = "ping", Data = Bytes("{}") })
            .ToList();

        var outbound = Outbound(Request(stream: true));
        var result = new CaptureRawProviderStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformOutboundRawStream(new ListStream<StreamEvent>(events));

        var received = Drain(result, RawStreamBacklog.MaxRawPreAttachEvents + 10);

        Assert.Equal(RawStreamBacklog.MaxRawPreAttachEvents, received.Count);
        Assert.NotNull(result.Err);
        Assert.True(GoErrors.Is(result.Err, PipelineErrors.PreCommitBufferExceeded));
        Assert.Contains(
            "pass-through pre-attachment raw event budget exceeded",
            result.Err!.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyStreamReadsHeldEventsFromTheQueue()
    {
        var outbound = Outbound(Request(stream: true));
        var backlog = new RawStreamBacklog();
        var queue = new RawStreamQueue(backlog);
        var held = TestChannels.Data("held");
        Assert.True(backlog.Hold(held, out _));
        var live = TestChannels.Data("live");
        queue.Enqueue(CancellationToken.None, live);
        queue.Channel.CompleteAdding();
        outbound.PassThroughStream = new PassThroughStreamState
        {
            RawChannel = queue.Channel,
            RawQueue = queue,
            Backlog = backlog,
            ErrorRef = new PassThroughErrorRef(),
            Cancel = () => { },
        };

        var result = new PassThroughStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformInboundRawStream(new ListStream<StreamEvent>([]));

        Assert.Equal<StreamEvent>([held, live], Drain(result, 10));
    }

    // ------------------------------------------------------------------
    // Pipeline integration
    // ------------------------------------------------------------------

    private static Pipeline NewPipeline(
        ScriptedExecutor executor,
        PersistentOutboundTransformer outbound,
        PassThroughSettings settings,
        IPipelineMiddleware? extra = null,
        bool streaming = false)
    {
        var options = new PipelineOptions();
        options.Middlewares.Add(new PassThroughRequestBodyMiddleware(outbound, settings));
        if (streaming)
        {
            options.Middlewares.Add(new PassThroughStreamMiddleware(
                outbound,
                settings,
                CancellationToken.None));
        }
        else
        {
            options.Middlewares.Add(new PassThroughResponseMiddleware(outbound, settings));
        }

        if (extra is not null)
        {
            options.Middlewares.Add(extra);
        }

        if (streaming)
        {
            options.Middlewares.Add(new CaptureRawProviderStreamMiddleware(
                outbound,
                settings,
                CancellationToken.None));
        }
        else
        {
            options.Middlewares.Add(new CaptureRawProviderResponseMiddleware(outbound, settings));
        }

        return new Pipeline(executor, new StubInboundTransformer(), outbound, options);
    }

    [Fact]
    public void PipelineReplacesNonStreamResponseWithTheRawProviderResponse()
    {
        var outbound = Outbound(Request(stream: false));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = 200,
                Body = Bytes("raw-provider-body"),
            },
        };

        var result = NewPipeline(executor, outbound, PassThroughSettings.Disabled)
            .Process(ClientRequest(stream: false));

        Assert.False(result.Stream);
        Assert.Equal(
            "raw-provider-body",
            Encoding.UTF8.GetString(result.Response!.Body!));
        Assert.NotNull(outbound.RawProviderResponse);
    }

    [Fact]
    public void PipelineKeepsTheTransformedResponseWhenDisabled()
    {
        var outbound = Outbound(Request(stream: false), channelSettings: null);
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = 200,
                Body = Bytes("raw-provider-body"),
            },
        };

        var result = NewPipeline(executor, outbound, PassThroughSettings.Disabled)
            .Process(ClientRequest(stream: false));

        Assert.False(result.Stream);
        Assert.Equal("transformed", Encoding.UTF8.GetString(result.Response!.Body!));
        Assert.Null(outbound.RawProviderResponse);
    }

    [Fact]
    public void PipelineStreamsRawEventsWhileLlmMiddlewareRuns()
    {
        // Reference TestPassThroughStream_LLMMiddlewareRuns: the client receives
        // the raw provider events while the discarded transformed stream has
        // already passed through the LLM middleware.
        const int eventCount = 80;
        var rawEvents = Enumerable.Range(1, eventCount)
            .Select(i => TestChannels.Data("evt" + i))
            .ToList();

        var outbound = Outbound(Request(stream: true));
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(rawEvents),
        };
        var tracking = new TrackingLlmMiddleware();

        var result = NewPipeline(
                executor,
                outbound,
                PassThroughSettings.Disabled,
                tracking,
                streaming: true)
            .Process(ClientRequest(stream: true));

        Assert.True(result.Stream);

        var clientEvents = Drain(result.EventStream!, eventCount + 4);

        Assert.Equal<StreamEvent>(rawEvents, clientEvents);
        Assert.True(WaitUntil(
            () => tracking.Called && tracking.Events == eventCount,
            SafetyTimeout));
    }
}
