using System.Diagnostics.CodeAnalysis;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Pipeline;

/// <summary>
/// Port of the streaming half of llm/pipeline: pipeline.stream, the first-event
/// timeout guard, and preReadLlmStream (llm/pipeline/stream.go).
/// </summary>
public sealed partial class Pipeline
{
    private const int MaxPreReadEvents = 3;

    // Visible to the test assembly so the buffer-limit tests can size their
    // inputs exactly like the reference tests do.
    internal const int MaxPreCommitBufferedEvents = 1024;
    internal const int MaxPreCommitBufferedBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Port of pipeline.stream: outbound transform -> HTTP stream -> outbound
    /// stream transform -> pre-read gate -> inbound stream transform.
    /// </summary>
    private IStream<StreamEvent> Stream(
        CancellationToken cancellationToken,
        IExecutor executor,
        HttpRequestData request,
        TimeSpan firstEventTimeout)
    {
        var (guard, streamToken) = FirstEventTimeoutGuard.Create(firstEventTimeout, cancellationToken);

        IStream<StreamEvent>? outboundStream;
        try
        {
            outboundStream = executor.DoStream(streamToken, request);

            // If the timeout already fired while the transport was connecting, the
            // stream we just got is unusable: fail the attempt so it can be retried.
            if (guard is { TimedOut: true })
            {
                outboundStream.Dispose();
                var timeoutErr = guard.FinishBeforeFirstEvent(PipelineErrors.StreamFirstEventTimeout);
                ApplyRawErrorMiddlewares(timeoutErr);
                throw timeoutErr;
            }
        }
        catch (Exception ex) when (ex != PipelineErrors.StreamFirstEventTimeout)
        {
            var err = guard?.FinishBeforeFirstEvent(ex) ?? ex;
            ApplyRawErrorMiddlewares(err);

            if (GoErrors.Is(err, PipelineErrors.StreamFirstEventTimeout))
            {
                throw err;
            }

            if (GoErrors.As<HttpClientError>(err) is { } httpErr)
            {
                throw UpstreamErrors.Wrap(_outbound.TransformError(httpErr))!;
            }

            throw UpstreamErrors.Wrap(err)!;
        }

        var responseHeaders = ResponseHeaders.MergeForwardResponseHeaders(
            null,
            ResponseHeaders.GetResponseHeaders(outboundStream));

        try
        {
            outboundStream = ApplyRawStreamMiddlewares(outboundStream);
        }
        catch (Exception ex)
        {
            outboundStream.Dispose();
            var err = guard?.FinishBeforeFirstEvent(ex) ?? ex;
            ApplyRawErrorMiddlewares(err);

            if (GoErrors.Is(err, PipelineErrors.StreamFirstEventTimeout))
            {
                throw err;
            }

            throw GoErrors.Wrap(err, "failed to apply raw stream middlewares: ");
        }

        IStream<LlmResponse> llmStream;
        try
        {
            llmStream = _outbound.TransformStream(request, outboundStream);
        }
        catch (Exception ex)
        {
            outboundStream.Dispose();
            var err = guard?.FinishBeforeFirstEvent(ex) ?? ex;
            ApplyRawErrorMiddlewares(err);

            if (GoErrors.Is(err, PipelineErrors.StreamFirstEventTimeout))
            {
                throw err;
            }

            throw UpstreamErrors.Wrap(err)!;
        }

        var rawLlmStream = llmStream;

        try
        {
            llmStream = ApplyLlmStreamMiddlewares(llmStream);
        }
        catch (Exception ex)
        {
            rawLlmStream.Dispose();
            var err = guard?.FinishBeforeFirstEvent(ex) ?? ex;
            ApplyRawErrorMiddlewares(err);

            if (GoErrors.Is(err, PipelineErrors.StreamFirstEventTimeout))
            {
                throw err;
            }

            throw GoErrors.Wrap(err, "failed to apply llm stream middlewares: ");
        }

        // Check stream start before the handler commits the response: this enforces
        // the first-event timeout, empty-response detection, and pre-content retry.
        if (_options.EmptyResponseDetection || firstEventTimeout > TimeSpan.Zero || HasStreamRetryBudget())
        {
            var preReadSource = llmStream;
            try
            {
                llmStream = PreReadLlmStream(llmStream, guard, cancellationToken);
            }
            catch (Exception ex)
            {
                preReadSource.Dispose();
                var err = guard?.FinishBeforeFirstEvent(ex) ?? ex;
                ApplyRawErrorMiddlewares(err);

                if (!ShouldWrapPreReadStreamError(err))
                {
                    throw err;
                }

                throw UpstreamErrors.Wrap(err)!;
            }
        }
        else
        {
            guard?.Stop();
        }

        var metadata = request.TransformerMetadata;
        llmStream = new MapStream<LlmResponse, LlmResponse>(
            llmStream,
            resp => WithTransformerMetadata(metadata, resp));

        IStream<StreamEvent> inboundStream;
        try
        {
            inboundStream = _inbound.TransformStream(llmStream);
        }
        catch (Exception ex)
        {
            llmStream.Dispose();
            guard?.CancelStream();
            ApplyRawErrorMiddlewares(ex);
            throw;
        }

        var rawInboundStream = inboundStream;

        try
        {
            inboundStream = ApplyInboundRawStreamMiddlewares(inboundStream);
        }
        catch (Exception ex)
        {
            rawInboundStream.Dispose();
            guard?.CancelStream();
            ApplyRawErrorMiddlewares(ex);
            throw GoErrors.Wrap(ex, "failed to apply inbound raw stream middlewares: ");
        }

        if (guard is not null)
        {
            inboundStream = new CancelOnCloseStream(inboundStream, guard);
        }

        return ResponseHeaders.WithResponseHeaders(inboundStream, responseHeaders)!;
    }

    private bool HasStreamRetryBudget()
        => MaxSameChannelRetriesFor() > 0 || _options.MaxChannelRetries > 0;

    private static bool ShouldWrapPreReadStreamError(Exception err)
        => !GoErrors.IsAny(
            err,
            PipelineErrors.StreamFirstEventTimeout,
            PipelineErrors.EmptyResponse)
            && !HasCancellationCause(err);

    /// <summary>
    /// Go: <c>errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded)</c>.
    /// Both surface as <see cref="OperationCanceledException"/> in .NET, and both
    /// can be wrapped, so the whole cause chain is checked.
    /// </summary>
    private static bool HasCancellationCause(Exception err)
    {
        for (Exception? current = err; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Port of preReadLlmStream: reads the initial events so the pipeline can
    /// enforce the first-event timeout, detect empty responses, and keep
    /// retryable stream failures inside the pipeline until the first meaningful
    /// content. Internal so the ported package-level tests can drive it
    /// directly, as the reference tests do.
    /// </summary>
    internal IStream<LlmResponse> PreReadLlmStream(
        IStream<LlmResponse> llmStream,
        FirstEventTimeoutGuard? firstEventGuard,
        CancellationToken cancellationToken)
    {
        var preReadUntilContent = HasStreamRetryBudget();
        const int probeLimit = MaxPreReadEvents;

        var buffered = new List<LlmResponse>();
        var bufferedBytes = 0;

        for (var i = 0; ; i++)
        {
            var (hasNext, err) = NextLlmStreamEvent(cancellationToken, llmStream, i == 0, firstEventGuard);
            if (err is not null)
            {
                llmStream.Dispose();
                throw err;
            }

            if (!hasNext)
            {
                break;
            }

            var @event = llmStream.Current!;
            if (ResponseContent.HasResponseContent(@event))
            {
                // Meaningful output commits the attempt immediately. The private
                // metadata budget must not apply to legitimate large media payloads.
                buffered.Add(@event);
                return new PrependStream<LlmResponse>(llmStream, [.. buffered]);
            }

            if (preReadUntilContent)
            {
                var eventBytes = GoJson.Marshal(@event).Length;
                if (eventBytes > MaxPreCommitBufferedBytes - bufferedBytes)
                {
                    llmStream.Dispose();
                    throw PipelineErrors.PreCommitBufferExceeded;
                }

                bufferedBytes += eventBytes;
            }

            buffered.Add(@event);

            if (preReadUntilContent && buffered.Count > MaxPreCommitBufferedEvents)
            {
                llmStream.Dispose();
                throw PipelineErrors.PreCommitBufferExceeded;
            }

            if (!preReadUntilContent && !_options.EmptyResponseDetection)
            {
                return new PrependStream<LlmResponse>(llmStream, [.. buffered]);
            }

            // Recognize both the shared sentinel and a freshly built "[DONE]":
            // outbound transformers may construct their own terminal event.
            if (IsTerminalLlmStreamEvent(@event))
            {
                if (!_options.EmptyResponseDetection)
                {
                    return new PrependStream<LlmResponse>(llmStream, [.. buffered]);
                }

                llmStream.Dispose();
                throw PipelineErrors.EmptyResponse;
            }

            // Once retry is enabled, metadata stays private to this attempt until
            // meaningful content commits it, a terminal event completes it, or the
            // stream fails. Flushing at the probe limit would leak a failed attempt.
            if (!preReadUntilContent && buffered.Count >= probeLimit)
            {
                break;
            }
        }

        if (llmStream.Err is { } streamErr)
        {
            llmStream.Dispose();
            throw streamErr;
        }

        if (preReadUntilContent)
        {
            llmStream.Dispose();
            throw LlmErrors.StreamIncomplete;
        }

        // Nothing in the bounded probe: hand the buffered events to the handler.
        if (buffered.Count > 0)
        {
            return new PrependStream<LlmResponse>(llmStream, [.. buffered]);
        }

        return llmStream;
    }

    /// <summary>Port of nextLlmStreamEvent.</summary>
    private static (bool HasNext, Exception? Error) NextLlmStreamEvent(
        CancellationToken cancellationToken,
        IStream<LlmResponse> llmStream,
        bool firstEvent,
        FirstEventTimeoutGuard? firstEventGuard)
    {
        if (!firstEvent || firstEventGuard is null)
        {
            return (llmStream.Next(), null);
        }

        var hasNext = llmStream.Next();
        if (hasNext)
        {
            if (!firstEventGuard.AcceptFirstEvent())
            {
                llmStream.Dispose();
                return (false, PipelineErrors.StreamFirstEventTimeout);
            }

            return (true, null);
        }

        firstEventGuard.CompleteFirstEventPhase();
        if (firstEventGuard.TimedOut)
        {
            llmStream.Dispose();
            return (false, PipelineErrors.StreamFirstEventTimeout);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            firstEventGuard.CancelStream();
            llmStream.Dispose();
            return (false, new OperationCanceledException(cancellationToken));
        }

        return (false, null);
    }

    /// <summary>Port of hasFinishReason.</summary>
    private static bool HasFinishReason(LlmResponse? resp)
    {
        if (resp is null)
        {
            return false;
        }

        foreach (var choice in resp.Choices)
        {
            if (choice.FinishReason is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of isTerminalLlmStreamEvent.</summary>
    private static bool IsTerminalLlmStreamEvent(LlmResponse? resp)
        => ReferenceEquals(resp, LlmResponse.DoneResponse)
            || (resp is not null && resp.Object == "[DONE]")
            || HasFinishReason(resp);

    /// <summary>Port of firstEventTimeoutGuard.</summary>
    [SuppressMessage(
        "Design",
        "CA1001:Types that own disposable fields should be disposable",
        Justification = "The timer is one-shot and is disposed by Stop() whenever a first-event transition completes; "
            + "if the stream is torn down earlier it fires once and becomes collectible, exactly like Go's "
            + "time.AfterFunc. The reference guard has no Close/Dispose, so adding IDisposable would invent "
            + "an ownership the port does not have.")]
    internal sealed class FirstEventTimeoutGuard
    {
        private const int Pending = 0;
        private const int Completed = 1;
        private const int TimedOutState = 2;

        private readonly CancellationTokenSource _cts;
        private readonly Timer _timer;
        private int _state = Pending;
        private bool _stopped;

        private FirstEventTimeoutGuard(CancellationTokenSource cts, TimeSpan timeout)
        {
            _cts = cts;
            _timer = new Timer(
                static state => ((FirstEventTimeoutGuard)state!).OnTimeout(),
                this,
                timeout,
                Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token => _cts.Token;

        public bool TimedOut => Volatile.Read(ref _state) == TimedOutState;

        /// <summary>Port of newFirstEventTimeoutGuard.</summary>
        public static (FirstEventTimeoutGuard? Guard, CancellationToken Token) Create(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
            {
                return (null, cancellationToken);
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var guard = new FirstEventTimeoutGuard(cts, timeout);
            return (guard, guard.Token);
        }

        public void Stop()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _timer.Dispose();
        }

        public void CancelStream() => _cts.Cancel();

        /// <summary>Port of completeFirstEventPhase.</summary>
        public void CompleteFirstEventPhase()
        {
            Interlocked.CompareExchange(ref _state, Completed, Pending);
            Stop();
        }

        /// <summary>Port of acceptFirstEvent.</summary>
        public bool AcceptFirstEvent()
        {
            // If the timeout already canceled the stream, keep that state so the
            // caller can retry instead of returning a partially usable stream.
            if (Interlocked.CompareExchange(ref _state, Completed, Pending) == Pending)
            {
                Stop();
                return true;
            }

            return Volatile.Read(ref _state) == Completed;
        }

        /// <summary>Port of finishBeforeFirstEvent.</summary>
        public Exception FinishBeforeFirstEvent(Exception err)
        {
            CompleteFirstEventPhase();
            CancelStream();

            return TimedOut ? PipelineErrors.StreamFirstEventTimeout : err;
        }

        private void OnTimeout()
        {
            if (Interlocked.CompareExchange(ref _state, TimedOutState, Pending) == Pending)
            {
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The stream was already released; nothing to cancel.
                }
            }
        }
    }

    /// <summary>
    /// Port of cancelOnCloseStream: releases the stream context with the stream
    /// and forwards acknowledgements, the delivery boundary and finalization.
    /// The reference deliberately does not expose ExpectedStreamChoices here.
    /// </summary>
    private sealed class CancelOnCloseStream(IStream<StreamEvent> stream, FirstEventTimeoutGuard guard)
        : IStream<StreamEvent>, IDeliveryAcknowledger, IDeliveryBoundary, IFinalizationWaiter
    {
        private bool _cancelled;

        public StreamEvent? Current => stream.Current;

        public Exception? Err => stream.Err;

        public bool DeliveryAcknowledged
            => stream is IDeliveryAcknowledger observer && observer.DeliveryAcknowledged;

        public bool Next() => stream.Next();

        public void AcknowledgeDelivery()
            => (stream as IDeliveryAcknowledger)?.AcknowledgeDelivery();

        public Task<Exception?> DeliverAsync(
            Func<Task<DeliveryWrite>> write,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(write);

            if (stream is IDeliveryBoundary boundary)
            {
                return boundary.DeliverAsync(write, cancellationToken);
            }

            return DeliveryCapabilities.DeliverWithoutBoundaryAsync(
                stream as IDeliveryAcknowledger,
                write);
        }

        public void WaitForFinalization()
            => (stream as IFinalizationWaiter)?.WaitForFinalization();

        public void Dispose()
        {
            stream.Dispose();
            if (!_cancelled)
            {
                _cancelled = true;
                guard.CancelStream();
            }
        }
    }
}
