using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of the mockInbound used by llm/pipeline/pipeline_retry_test.go and
/// llm/pipeline/empty_response_test.go. Hooks mirror the reference's optional
/// function fields; unset hooks reproduce the reference defaults.
/// </summary>
internal sealed class MockInbound : IInboundTransformer
{
    public Func<HttpRequestData?, LlmRequest>? TransformRequestFn { get; set; }

    public Func<IStream<LlmResponse>, IStream<StreamEvent>>? TransformStreamFn { get; set; }

    public Func<IReadOnlyList<StreamEvent>, byte[]>? AggregateStreamChunksFn { get; set; }

    public LlmRequest TransformRequest(HttpRequestData? request)
        => TransformRequestFn?.Invoke(request) ?? new LlmRequest();

    public HttpResponseData TransformResponse(LlmResponse? response) => new();

    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => TransformStreamFn?.Invoke(stream)
            ?? new MapStream<LlmResponse, StreamEvent>(stream, static _ => new StreamEvent());

    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        return AggregateStreamChunksFn?.Invoke(chunks) ?? Encoding.UTF8.GetBytes("{}");
    }

    public HttpResponseData TransformError(Exception? error)
    {
        _ = error;
        return new HttpResponseData { StatusCode = 500 };
    }
}

/// <summary>
/// Port of the mockOutbound. The reference mock embeds transformer.Outbound and
/// implements Retryable and ChannelRetryable unconditionally, so the pipeline's
/// type assertions always succeed; the C# double implements them all too.
/// </summary>
internal sealed class MockOutbound : IOutboundTransformer, IRetryable, IChannelRetryable,
    IResponseTimeoutProvider
{
    public string ApiFormat => string.Empty;

    public TimeSpan? StreamFirstEventTimeoutOverride { get; set; }

    public TimeSpan? NonStreamTimeoutOverride { get; set; }

    public (TimeSpan? StreamFirstEventTimeout, TimeSpan? NonStreamTimeout) ResponseTimeouts()
        => (StreamFirstEventTimeoutOverride, NonStreamTimeoutOverride);

    public Func<LlmRequest?, HttpRequestData>? TransformRequestFn { get; set; }

    public Func<HttpResponseData?, LlmResponse>? TransformResponseFn { get; set; }

    public Func<HttpRequestData?, IStream<StreamEvent>, IStream<LlmResponse>>? TransformStreamFn { get; set; }

    public Func<HttpClientError?, ResponseError>? TransformErrorFn { get; set; }

    public Func<Exception, bool>? CanRetryFn { get; set; }

    public Func<CancellationToken, Exception?>? PrepareForRetryFn { get; set; }

    public Func<bool>? HasMoreChannelsFn { get; set; }

    public Func<CancellationToken, Exception?>? NextChannelFn { get; set; }

    public HttpRequestData TransformRequest(LlmRequest? request)
        => TransformRequestFn?.Invoke(request) ?? new HttpRequestData();

    public LlmResponse TransformResponse(HttpResponseData? response)
        => TransformResponseFn?.Invoke(response) ?? new LlmResponse();

    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => TransformStreamFn is { } fn
            ? fn(request, stream)
            : throw new InvalidOperationException("mock outbound: TransformStream is not configured");

    public ResponseError TransformError(HttpClientError? error)
        => TransformErrorFn?.Invoke(error) ?? new ResponseError();

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        return Encoding.UTF8.GetBytes("{}");
    }

    public bool HasMoreChannels() => HasMoreChannelsFn?.Invoke() ?? false;

    public Exception? TryNextChannel(CancellationToken cancellationToken) => NextChannelFn?.Invoke(cancellationToken);

    public bool CanRetry(Exception error) => CanRetryFn?.Invoke(error) ?? false;

    public Exception? TryPrepareForRetry(CancellationToken cancellationToken)
        => PrepareForRetryFn?.Invoke(cancellationToken);
}

/// <summary>Port of the mockExecutor.</summary>
internal sealed class MockExecutor : IExecutor
{
    public Func<CancellationToken, HttpRequestData, HttpResponseData>? DoFn { get; set; }

    public Func<CancellationToken, HttpRequestData, IStream<StreamEvent>>? DoStreamFn { get; set; }

    public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
        => DoFn is { } fn
            ? fn(cancellationToken, request)
            : throw new InvalidOperationException("mock executor: Do is not configured");

    public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
        => DoStreamFn is { } fn
            ? fn(cancellationToken, request)
            : throw new InvalidOperationException("mock executor: DoStream is not configured");
}

/// <summary>
/// Port of llmErrorAfterStream: yields fixed items, then reports a terminal
/// error once every item has been consumed.
/// </summary>
internal sealed class LlmErrorAfterStream : IStream<LlmResponse>
{
    public List<LlmResponse> Items { get; init; } = [];

    public Exception? TerminalError { get; init; }

    public int Index { get; private set; }

    public bool Closed { get; private set; }

    public LlmResponse? Current { get; private set; }

    public Exception? Err => Index >= Items.Count ? TerminalError : null;

    public bool Next()
    {
        if (Index >= Items.Count)
        {
            return false;
        }

        Current = Items[Index];
        Index++;
        return true;
    }

    public void Dispose() => Closed = true;
}

/// <summary>Port of the mockMiddleware: counts raw-error notifications.</summary>
internal sealed class MockMiddleware : DummyMiddleware
{
    public int ErrorCalls { get; private set; }

    public override void OnOutboundRawError(Exception error) => ErrorCalls++;
}

/// <summary>
/// Wraps a stream and records disposal. The reference asserts that the pipeline
/// closed the underlying response body; in the port the decoder is closed
/// through the stream chain, so disposal of the raw stream is the observable.
/// </summary>
internal sealed class DisposeTrackingStream<T> : IStream<T>
    where T : class
{
    private readonly IStream<T> _inner;

    public DisposeTrackingStream(IStream<T> inner) => _inner = inner;

    public bool Disposed { get; private set; }

    public T? Current => _inner.Current;

    public Exception? Err => _inner.Err;

    public bool Next() => _inner.Next();

    public void Dispose()
    {
        Disposed = true;
        _inner.Dispose();
    }
}

internal static class LlmTestData
{
    /// <summary>Port of llmEmptyChunk: a chunk that carries no content.</summary>
    public static LlmResponse EmptyChunk() => new()
    {
        Object = "chat.completion.chunk",
        Choices = [new Choice { Delta = new Message() }],
    };

    /// <summary>Port of llmContentChunk.</summary>
    public static LlmResponse ContentChunk(string content) => new()
    {
        Object = "chat.completion.chunk",
        Choices =
        [
            new Choice { Delta = new Message { Content = new MessageContent { Content = content } } },
        ],
    };
}
