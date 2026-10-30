using AxonHub.Api.Http;
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
/// The reference marks success when the unified response is produced and failure
/// in the raw-error hook; streams are marked when their terminal event is
/// observed. Client cancellation marks neither (a canceled attempt is not a
/// channel fault).
/// </remarks>
public sealed class ChannelMetricsMiddleware : DummyMiddleware
{
    private readonly PersistentOutboundTransformer _outbound;
    private readonly IChannelMetricsRecorder _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _cancellationToken;
    private readonly int _expectedChoices;

    public ChannelMetricsMiddleware(
        PersistentOutboundTransformer outbound,
        IChannelMetricsRecorder metrics,
        TimeProvider timeProvider,
        byte[]? requestBody,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _outbound = outbound;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _cancellationToken = cancellationToken;
        _expectedChoices = ExpectedStreamChoices.FromRequestBody(requestBody);
    }

    public override LlmResponse TransformOutboundLlmResponse(LlmResponse response)
    {
        _metrics.RecordSuccess(_outbound.CurrentChannelId, _timeProvider.GetUtcNow());
        return response;
    }

    public override void OnOutboundRawError(Exception error)
    {
        if (IsCancellation(error))
        {
            return;
        }

        _metrics.RecordFailure(_outbound.CurrentChannelId, _timeProvider.GetUtcNow());
    }

    public override IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream)
        => new MetricsRecordingStream(stream, this);

    private void ObserveStreamEnd(bool terminalSeen, Exception? streamError)
    {
        if (_cancellationToken.IsCancellationRequested || IsCancellation(streamError))
        {
            return;
        }

        if (streamError is not null)
        {
            _metrics.RecordFailure(_outbound.CurrentChannelId, _timeProvider.GetUtcNow());
            return;
        }

        // A stream that ends without a terminal event leaves the outcome to the
        // persistence layer in the reference; the port records only evidence it
        // actually has (C014).
        if (terminalSeen)
        {
            _metrics.RecordSuccess(_outbound.CurrentChannelId, _timeProvider.GetUtcNow());
        }
    }

    private static bool IsCancellation(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Port of recordPerformanceStream.Observe: remembers the [DONE] sentinel and
    /// per-choice finish reasons, and reports a success once every expected
    /// choice has finished.
    /// </summary>
    private sealed class MetricsRecordingStream : IStream<LlmResponse>
    {
        private readonly IStream<LlmResponse> _stream;
        private readonly ChannelMetricsMiddleware _owner;
        private readonly HashSet<int> _choices = [];
        private readonly HashSet<int> _finished = [];
        private bool _terminal;
        private bool _disposed;

        public MetricsRecordingStream(IStream<LlmResponse> stream, ChannelMetricsMiddleware owner)
        {
            _stream = stream;
            _owner = owner;
        }

        public LlmResponse? Current { get; private set; }

        public Exception? Err => _stream.Err;

        public bool Next()
        {
            if (!_stream.Next())
            {
                return false;
            }

            Current = _stream.Current;
            if (Current is not null)
            {
                Observe(Current);
            }

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
                _owner.ObserveStreamEnd(_terminal, _stream.Err);
            }
            finally
            {
                _stream.Dispose();
            }
        }

        private void Observe(LlmResponse response)
        {
            if (ReferenceEquals(response, LlmResponse.DoneResponse) || response.Object == "[DONE]")
            {
                _terminal = true;
                return;
            }

            foreach (var choice in response.Choices)
            {
                _choices.Add(choice.Index);
                if (!string.IsNullOrEmpty(choice.FinishReason))
                {
                    _finished.Add(choice.Index);
                }
            }

            if (_owner._expectedChoices > 0
                && _finished.Count >= _owner._expectedChoices
                && _choices.Count == _owner._expectedChoices)
            {
                _terminal = true;
            }
        }
    }
}
