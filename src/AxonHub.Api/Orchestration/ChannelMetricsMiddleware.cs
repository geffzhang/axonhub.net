using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.withPerformanceRecording (internal/server/orchestrator/
/// performance.go) reduced to the counters the channel grain owns: each attempt
/// records a success or a failure against the channel that served it.
/// </summary>
/// <remarks>
/// The record is created per outbound attempt (OnOutboundRawRequest) and shared
/// with the request-chain middleware through <see cref="ChannelPerformanceState"/>,
/// exactly like the reference keeps one PerformanceRecord on PersistenceState.
/// Non-stream success and raw errors are marked here; stream terminal states and
/// aggregated completion are marked by the persistent-stream counterpart, so a
/// canceled stream is never counted as a channel fault and a stream that ends
/// without a terminal event (and without a complete aggregate) records nothing.
/// The on-close hook reports the record after the inner stream has finalized,
/// mirroring recordPerformanceStream.Close + enqueueCompletedPerformance.
/// </remarks>
public sealed class ChannelMetricsMiddleware : DummyMiddleware
{
    private readonly PersistentOutboundTransformer _outbound;
    private readonly IChannelMetricsRecorder _metrics;
    private readonly ChannelPerformanceState _state;
    private readonly TimeProvider _timeProvider;

    private bool _stream;

    public ChannelMetricsMiddleware(
        PersistentOutboundTransformer outbound,
        IChannelMetricsRecorder metrics,
        ChannelPerformanceState state,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _outbound = outbound;
        _metrics = metrics;
        _state = state;
        _timeProvider = timeProvider;
    }

    /// <summary>Port of performanceRecording.OnInboundLlmRequest: the client's stream intent.</summary>
    public override LlmRequest TransformInboundLlmRequest(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _stream = request.Stream is true;
        return request;
    }

    /// <summary>
    /// Port of performanceRecording.OnOutboundRawRequest: a fresh record per
    /// attempt; the channel is the one about to serve it.
    /// </summary>
    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channelId = _outbound.CurrentChannelId;
        if (channelId <= 0)
        {
            return request;
        }

        _state.Current = new ChannelPerformanceRecord
        {
            ChannelId = channelId,
            StartTime = _timeProvider.GetUtcNow(),
            Stream = _stream,
        };

        return request;
    }

    /// <summary>Port of performanceRecording.OnOutboundLlmResponse: a non-stream success.</summary>
    public override LlmResponse TransformOutboundLlmResponse(LlmResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (_state.Current is { } record)
        {
            record.MarkSuccess(_timeProvider.GetUtcNow());
            Record(record);
        }

        return response;
    }

    /// <summary>
    /// Port of performanceRecording.OnOutboundRawError: a client cancellation is
    /// canceled, not a channel fault; every other error marks the attempt failed.
    /// </summary>
    public override void OnOutboundRawError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (_state.Current is not { } record)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (StreamOutcome.IsContextCancellation(error))
        {
            // MarkCanceled still completes the record; RecordPerformance records
            // nothing for a canceled attempt (channel_metrics.go).
            record.MarkCanceled(now);
        }
        else
        {
            // Port of performance.ExtractErrorCode: only an HTTP error carries a
            // provider status; anything else defaults to 500.
            var statusCode = GoErrors.As<HttpClientError>(error) is { } httpError
                ? httpError.StatusCode
                : 500;
            record.MarkFailed(statusCode, error.Message, now);
        }

        Record(record);
    }

    /// <summary>
    /// Port of performanceRecording.OnOutboundLlmStream. recordPerformanceStream
    /// only reports the record on Close; the outcome itself was marked by the
    /// persistent stream (terminal event or aggregation). Disposing the inner
    /// stream first lets the aggregated-completion mark happen before the report,
    /// matching OutboundPersistentStream.Close and enqueueCompletedPerformance.
    /// </summary>
    public override IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream)
        => new PerformanceStream(stream, this);

    /// <summary>
    /// Port of ChannelService.RecordPerformance's counter side: canceled attempts
    /// (and incomplete records) change neither successes nor failures.
    /// </summary>
    private void Record(ChannelPerformanceRecord? record)
    {
        if (record is not { IsValid: true } || record.Canceled)
        {
            return;
        }

        if (record.Success)
        {
            _metrics.RecordSuccess(record.ChannelId, record.EndTime);
        }
        else
        {
            _metrics.RecordFailure(record.ChannelId, record.EndTime);
        }
    }

    private sealed class PerformanceStream(IStream<LlmResponse> stream, ChannelMetricsMiddleware owner)
        : IStream<LlmResponse>
    {
        private bool _disposed;

        public LlmResponse? Current => stream.Current;

        public Exception? Err => stream.Err;

        public bool Next() => stream.Next();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                stream.Dispose();
            }
            finally
            {
                owner.Record(owner._state.Current);
            }
        }
    }
}
