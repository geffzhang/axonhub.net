using System.Text;
using AxonHub.Abstractions;
using AxonHub.Api.Http;
using AxonHub.Api.Management;
using AxonHub.Grains;
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
/// Reporting has two entry points, exactly like the reference: the stream's
/// on-close hook reports a record that was completed while streaming
/// (recordPerformanceStream.Close), and the aggregated-completion branch reports
/// the record it just marked through <see cref="ChannelPerformanceState.ReportCompleted"/>
/// (enqueueCompletedPerformance in outbound.go). The latter is needed because the
/// performance stream closes before finalization runs, so its on-close report
/// sees an incomplete record and stays silent.
/// </remarks>
public sealed class ChannelMetricsMiddleware : DummyMiddleware
{
    private readonly PersistentOutboundTransformer _outbound;
    private readonly IChannelMetricsRecorder _metrics;
    private readonly ChannelPerformanceState _state;
    private readonly TimeProvider _timeProvider;
    private readonly ChannelApiKeySelectionRegistry? _apiKeySelection;
    private readonly IChannelAutoDisableEvaluationQueue? _autoDisableQueue;
    private readonly ModelCircuitBreaker? _modelCircuitBreaker;
    private readonly bool _trackModelCircuitBreaker;

    private bool _stream;

    public ChannelMetricsMiddleware(
        PersistentOutboundTransformer outbound,
        IChannelMetricsRecorder metrics,
        ChannelPerformanceState state,
        TimeProvider timeProvider,
        ChannelApiKeySelectionRegistry? apiKeySelection = null,
        IChannelAutoDisableEvaluationQueue? autoDisableQueue = null,
        ModelCircuitBreaker? modelCircuitBreaker = null,
        bool trackModelCircuitBreaker = false)
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _outbound = outbound;
        _metrics = metrics;
        _state = state;
        _timeProvider = timeProvider;
        _apiKeySelection = apiKeySelection;
        _autoDisableQueue = autoDisableQueue;
        _modelCircuitBreaker = modelCircuitBreaker;
        _trackModelCircuitBreaker = trackModelCircuitBreaker;

        // The persistent-stream middleware reports an aggregated completion
        // through the same state; the sink is Record, so both paths share the
        // reference's single AsyncRecordPerformance entry point.
        state.OnCompleted = Record;
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

        // performance.go:79-83: the provider publishes the selected key through
        // the request context; OAuth channels never pass through a key provider,
        // so they carry the fixed OAuth credential ref instead.
        var apiKey = _outbound.CurrentApiKey ?? string.Empty;
        if (apiKey.Length == 0
            && _outbound.CurrentCandidate is { } candidate
            && ChannelCredentials.IsOAuth(candidate.Credentials))
        {
            apiKey = ChannelCredentials.OAuthCredentialRef;
        }

        var modelId = _outbound.OriginalModel.Length > 0
            ? _outbound.OriginalModel
            : _outbound.CurrentModel;
        var wasProbe = false;
        if (_trackModelCircuitBreaker
            && _modelCircuitBreaker is not null
            && modelId.Length > 0
            && _modelCircuitBreaker.GetStats(channelId, modelId).State
                == ModelCircuitBreakerState.Open)
        {
            if (!_modelCircuitBreaker.TryBeginProbe(
                    channelId,
                    modelId,
                    _timeProvider.GetUtcNow()))
            {
                throw new ModelCircuitBreakerSkipException(channelId, modelId);
            }

            wasProbe = true;
        }

        _state.Current = new ChannelPerformanceRecord
        {
            ChannelId = channelId,
            ModelId = modelId,
            WasProbe = wasProbe,
            StartTime = _timeProvider.GetUtcNow(),
            Stream = _stream,
            ApiKey = apiKey,
        };

        return request;
    }

    /// <summary>Port of performanceRecording.OnOutboundLlmResponse: a non-stream success.</summary>
    public override LlmResponse TransformOutboundLlmResponse(LlmResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (_state.Current is { } record)
        {
            if (response.Usage is { } usage)
            {
                record.ObserveCompletionTokens(usage.CompletionTokens);
            }

            record.MarkSuccess(_timeProvider.GetUtcNow());
            Record(record);
        }

        return response;
    }

    /// <summary>
    /// Captures the first meaningful streamed output and the provider's final
    /// usage for the latency EWMA (C034).
    /// </summary>
    private void ObserveStreamResponse(LlmResponse? response)
    {
        if (_state.Current is not { Stream: true } record)
        {
            return;
        }

        if (record.FirstTokenTime is null
            && ResponseContent.HasMeaningfulStreamOutput(response))
        {
            record.ObserveFirstToken(_timeProvider.GetUtcNow());
        }

        if (response?.Usage is { } usage)
        {
            record.ObserveCompletionTokens(usage.CompletionTokens);
        }
    }

    /// <summary>
    /// Port of performanceRecording.OnOutboundRawError: a client cancellation is
    /// canceled, not a channel fault; every other error marks the attempt failed.
    /// </summary>
    public override void OnOutboundRawError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        // A model circuit skip is a local bounce, not a channel fault
        // (model_circuit_breaker.go:103).
        if (error is ModelCircuitBreakerSkipException)
        {
            return;
        }

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
            var statusCode = ExtractErrorCode(error);

            // performance.go:268-281: the recorded message is the extracted
            // error message plus the first 8 KiB of the response body, so the
            // auto-disable keyword patterns can match provider text.
            record.MarkFailed(statusCode, ExtractErrorMessageForMatching(error), now);
        }

        Record(record);
    }

    /// <summary>
    /// Port of performanceRecording.OnOutboundLlmStream. recordPerformanceStream
    /// only reports the record on Close, and only when the outcome was already
    /// marked by the persistent stream (a terminal event seen while streaming).
    /// An aggregate completed during finalization is reported by the
    /// aggregated-completion branch itself (enqueueCompletedPerformance).
    /// </summary>
    public override IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream)
        => new PerformanceStream(stream, this);

    /// <summary>
    /// Port of ChannelService.RecordPerformance's counter side: canceled attempts
    /// (and incomplete records) change neither successes nor failures.
    /// </summary>
    private void Record(ChannelPerformanceRecord? record)
    {
        // Release a probe lease even when the attempt was canceled; the
        // reference releases before its context.Canceled check.
        if (record is { WasProbe: true } && _modelCircuitBreaker is not null)
        {
            _modelCircuitBreaker.EndProbe(record.ChannelId, record.ModelId);
        }

        if (record is not { IsValid: true } || record.Canceled)
        {
            return;
        }

        // channel_metrics.go:526-530: the auto-disable counters are updated
        // before the window metrics. The reference hands the record to a
        // background worker (perfCh); the port's queue is the same seam.
        _autoDisableQueue?.Enqueue(record);

        if (record.Success)
        {
            // channel_metrics.go:531: a success lets the round_robin_success
            // strategy count the key and move its cursor once the key has
            // served its share of successful requests.
            _apiKeySelection?.OnApiKeySuccess(record.ChannelId, record.ApiKey);
            _metrics.RecordSuccess(record.ChannelId, record.EndTime);
        }
        else
        {
            _metrics.RecordFailure(record.ChannelId, record.EndTime);
        }

        // model_circuit_breaker.go:71-113: the reference tracker records the
        // model outcome only under the circuit-breaker policy.
        if (_trackModelCircuitBreaker
            && _modelCircuitBreaker is not null
            && record.ModelId.Length > 0)
        {
            if (record.Success)
            {
                _modelCircuitBreaker.RecordSuccess(record.ChannelId, record.ModelId);
            }
            else
            {
                _modelCircuitBreaker.RecordError(
                    record.ChannelId,
                    record.ModelId,
                    record.WasProbe);
            }
        }

        if (record.Success && record.MeasureLatency() is { } latency)
        {
            _metrics.RecordLatency(latency);
        }
    }

    /// <summary>
    /// Port of performance.ExtractErrorCode (performance.go:255-266): only an
    /// HTTP error carries a provider status; anything else defaults to 500.
    /// </summary>
    internal static int ExtractErrorCode(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return GoErrors.As<HttpClientError>(error) is { } httpError
            ? httpError.StatusCode
            : 500;
    }

    /// <summary>
    /// Port of performance.extractErrorMessageForMatching (performance.go:268-281):
    /// the extracted message plus the first 8 KiB of the raw response body, so
    /// keyword patterns can also match provider prose outside the JSON envelope.
    /// </summary>
    internal static string ExtractErrorMessageForMatching(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var message = StreamErrorFormatter.ExtractErrorMessage(error);
        if (GoErrors.As<HttpClientError>(error) is not { } httpError || httpError.Body.Length == 0)
        {
            return message;
        }

        var body = httpError.Body;
        if (body.Length > ErrorMatchBodyLimit)
        {
            body = body[..ErrorMatchBodyLimit];
        }

        return message + "\n" + Encoding.UTF8.GetString(body);
    }

    private const int ErrorMatchBodyLimit = 8 * 1024;

    private sealed class PerformanceStream(IStream<LlmResponse> stream, ChannelMetricsMiddleware owner)
        : IStream<LlmResponse>
    {
        private bool _disposed;

        public LlmResponse? Current => stream.Current;

        public Exception? Err => stream.Err;

        public bool Next()
        {
            if (!stream.Next())
            {
                return false;
            }

            owner.ObserveStreamResponse(stream.Current);
            return true;
        }

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
