using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Billing;
using AxonHub.Api.Http;
using AxonHub.Api.Persistence;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the request-chain persistence middleware pair
/// (internal/server/orchestrator/request.go and request_execution.go) for the
/// chat slice.
/// </summary>
/// <remarks>
/// One middleware instance is created per client request. It creates the request
/// row on the inbound hook, one execution row per attempt on the outbound raw
/// request hook, and finalizes both when their streams end, exactly like
/// InboundPersistentStream.Close and OutboundPersistentStream.Close. The recorder
/// is synchronous by design; see C018.
/// </remarks>
public sealed class RequestChainPersistenceMiddleware : DummyMiddleware
{
    private const string StatusProcessing = "processing";
    private const string StatusCompleted = "completed";
    private const string StatusFailed = "failed";
    private const string StatusCanceled = "canceled";
    private const int MinLatencyMs = 10;

    private readonly IRequestChainRecorder _recorder;
    private readonly IUsageCostService? _usageCostService;
    private readonly PersistentOutboundTransformer _outbound;
    private readonly IInboundTransformer _inbound;
    private readonly HttpRequestData _rawRequest;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<long>? _onRequestCreated;
    private readonly ChannelPerformanceState? _performance;
    private readonly RequestIdentity _identity;

    /// <summary>Per-request stream state shared by the inbound and outbound streams.</summary>
    private readonly RequestStreamState _streamState = new();

    private readonly StreamTerminalTracker _requestTerminalTracker;
    private readonly StreamOutcome _requestOutcome = new();
    private readonly List<StreamEvent> _requestChunks = [];
    private StreamTerminalState _requestTerminalState = StreamTerminalState.None;

    private long _requestId;
    private long? _requestChannelId;
    private bool _stream;
    private string _format = ApiFormats.OpenAIChatCompletion;
    private DateTimeOffset _requestStartedAt;
    private bool _requestFinalized;
    private ExecutionAttempt? _attempt;

    /// <summary>
    /// The unified response stored by persistRequest.OnOutboundLlmResponse; the
    /// non-stream finalization hook refuses to persist without it.
    /// </summary>
    private LlmResponse? _llmResponse;

    public RequestChainPersistenceMiddleware(
        IRequestChainRecorder recorder,
        PersistentOutboundTransformer outbound,
        IInboundTransformer inbound,
        HttpRequestData rawRequest,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        Action<long>? onRequestCreated = null,
        ChannelPerformanceState? performance = null,
        RequestIdentity? identity = null,
        IUsageCostService? usageCostService = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(rawRequest);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _recorder = recorder;
        _usageCostService = usageCostService;
        _outbound = outbound;
        _inbound = inbound;
        _rawRequest = rawRequest;
        _timeProvider = timeProvider;
        _cancellationToken = cancellationToken;
        _onRequestCreated = onRequestCreated;
        _performance = performance;
        _identity = identity ?? RequestIdentity.Default;
        _requestTerminalTracker = new StreamTerminalTracker(
            ExpectedStreamChoices.FromRequestBody(rawRequest.Body));
    }

    /// <summary>Port of persistRequest.OnInboundLlmRequest: create the request row once.</summary>
    public override LlmRequest TransformInboundLlmRequest(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_requestId != 0)
        {
            return request;
        }

        var now = _timeProvider.GetUtcNow();
        var raw = request.RawRequest ?? _rawRequest;

        _format = request.ApiFormat ?? ApiFormats.OpenAIChatCompletion;
        _stream = request.Stream is true;
        _requestStartedAt = now;

        // The reference returns the create error, which fails the request: a
        // request that cannot be recorded must not reach an upstream.
        _requestId = _recorder.CreateRequest(new RequestRecord
        {
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
            Source = "api",
            ModelId = request.Model,
            ReasoningEffort = string.IsNullOrEmpty(request.ReasoningEffort)
                ? null
                : request.ReasoningEffort,
            Format = _format,
            RequestHeaders = RequestChainPayloads.MaskedHeadersJson(raw.Headers),
            RequestBody = RequestChainPayloads.JsonBody(raw.JsonBody ?? raw.Body),
            Status = StatusProcessing,
            Stream = _stream,
            ClientIp = raw.ClientIp,
            UserAgent = raw.UserAgent,
            ApiKeyId = _identity.ApiKeyId,
            ProjectId = _identity.ProjectId,
            // persistRequest calls SetTraceID with contexts.GetTrace(ctx).ID, so
            // the request joins the trace TraceMiddleware persisted.
            TraceId = _identity.TraceId,
        });

        // The HTTP boundary uses the id to write the downstream response
        // headers after the handler returns (middleware/response_headers.go).
        _onRequestCreated?.Invoke(_requestId);

        return request;
    }

    /// <summary>Port of persistRequestExecution.OnOutboundRawRequest: one execution per attempt.</summary>
    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_requestId == 0)
        {
            return request;
        }

        var transformer = _outbound.CurrentTransformer;
        var now = _timeProvider.GetUtcNow();
        var attempt = new ExecutionAttempt
        {
            StartedAt = now,
            Transformer = transformer,
            Request = request,
            ApiFormat = string.IsNullOrEmpty(request.ApiFormat) ? transformer.ApiFormat : request.ApiFormat,
            ModelId = _outbound.CurrentModel,
            ChannelId = _outbound.CurrentChannelId,
            Tracker = new StreamTerminalTracker(
                ExpectedStreamChoices.FromRequestBody(_rawRequest.Body)),
        };

        _attempt = attempt;

        // PrepareForRetry / NextChannel reset these before every new attempt
        // (outbound.go): a previous attempt's terminal event must not classify
        // this one's stream.
        _streamState.OutboundStreamTerminal = StreamTerminalState.None;
        _streamState.CleanEofCompletionEvidence = false;

        // Both writes return their error in the reference, so a failure here
        // fails the attempt instead of letting it run unrecorded.
        attempt.ExecutionId = _recorder.CreateExecution(new RequestExecutionRecord
        {
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
            ProjectId = _identity.ProjectId,
            ModelId = attempt.ModelId,
            Format = attempt.ApiFormat,
            RequestBody = RequestChainPayloads.JsonBody(request.JsonBody ?? request.Body),
            RequestHeaders = RequestChainPayloads.MaskedHeadersJson(request.Headers),
            RequestUrl = request.Url,
            Status = StatusProcessing,
            Stream = _stream,
            RequestId = _requestId,
            ChannelId = attempt.ChannelId,
            ChannelApiKeySuffix = ApiKeySuffix(_outbound.CurrentApiKey),
            ChannelApiKeyIndex = _outbound.CurrentApiKeyIndex,

            // persistRequestExecution stores the pass-through flag the
            // body middleware set for this attempt.
            PassThroughApplied = _outbound.PassThroughApplied,
        });

        // The transport reports response headers before the first event. The
        // reference persists them immediately (executionHeaderObserver).
        request.OnResponseHeaders = headers => ObserveExecutionHeaders(attempt, headers);

        if (attempt.ChannelId != 0 && _requestChannelId != attempt.ChannelId)
        {
            _recorder.UpdateRequestChannel(_requestId, attempt.ChannelId);
            _requestChannelId = attempt.ChannelId;
        }

        return request;
    }

    public override HttpResponseData TransformOutboundRawResponse(HttpResponseData response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (_attempt is { } attempt)
        {
            attempt.RawResponse = response;

            if (!attempt.HeadersObserved)
            {
                ObserveExecutionHeaders(attempt, response.Headers);
            }
        }

        return response;
    }

    /// <summary>
    /// Port of persistRequestExecution.OnOutboundLlmResponse: a non-stream
    /// attempt completed. Response headers were persisted when the transport
    /// reported them, never here.
    /// </summary>
    public override LlmResponse TransformOutboundLlmResponse(LlmResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        // persistRequest.OnOutboundLlmResponse injects the calculated cost into
        // the unified response before the usage log is written and before the
        // client-facing response is serialized.
        InjectUsageCost(response);

        // persistRequest.OnOutboundLlmResponse stores the unified response; the
        // non-stream finalization hook refuses to persist without it.
        if (_requestId != 0)
        {
            _llmResponse = response;
        }

        if (_attempt is not { } attempt || attempt.ExecutionId == 0 || attempt.NonStreamFinalized)
        {
            return response;
        }

        attempt.NonStreamFinalized = true;

        TryPersist(() =>
        {
            var now = _timeProvider.GetUtcNow();
            var rawResponse = attempt.RawResponse;
            var upstreamModel = RequestChainPayloads.ReportedModel(rawResponse?.Body);

            _recorder.UpdateExecutionResult(
                attempt.ExecutionId,
                StatusCompleted,
                response.Id,
                NullIfEmpty(upstreamModel),
                RequestChainPayloads.JsonBody(rawResponse?.Body),
                null,
                LatencyMs(attempt.StartedAt, attempt.EndedAt ?? now),
                FirstTokenLatencyMs(attempt),
                now.UtcDateTime);

            LogUsage(response.Usage, attempt);
        });

        return response;
    }

    /// <summary>
    /// Port of persistRequestExecution.OnOutboundRawError. The reference always
    /// records the attempt as failed here; a client cancellation is turned into
    /// "canceled" by the final status update.
    /// </summary>
    public override void OnOutboundRawError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (_attempt is not { } attempt || attempt.ExecutionId == 0)
        {
            return;
        }

        TryPersist(() =>
        {
            var classified = UpstreamTransportErrors.Classify(error);

            // The reference passes nil metrics and derives the upstream model
            // from the raw response, so this update only records the outcome.
            _recorder.UpdateExecutionStatus(
                attempt.ExecutionId,
                StatusFailed,
                RequestChainPayloads.ErrorStatusCode(classified),
                NullIfEmpty(RequestChainPayloads.ReportedModel(attempt.RawResponse?.Body)),
                RequestChainPayloads.ErrorMessage(classified),
                null,
                null,
                _timeProvider.GetUtcNow().UtcDateTime);
        });
    }

    /// <summary>
    /// Port of persistRequestExecution.OnOutboundRawStream: wraps the provider's
    /// raw stream so its chunks can be aggregated when it ends
    /// (OutboundPersistentStream).
    /// </summary>
    public override IStream<StreamEvent> TransformOutboundRawStream(IStream<StreamEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (_requestId == 0 || _attempt is not { ExecutionId: not 0 } attempt)
        {
            return stream;
        }

        return new ExecutionRawStream(stream, this, attempt);
    }

    /// <summary>First-token observation for streams (the reference's recordPerformanceStream).</summary>
    public override IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return _attempt is { } attempt ? new FirstTokenStream(stream, attempt, this) : stream;
    }

    /// <summary>
    /// Port of persistRequest.OnInboundRawResponse: a non-stream request
    /// completed. The downstream response headers are written separately at the
    /// HTTP boundary, exactly like the reference.
    /// </summary>
    public override HttpResponseData TransformInboundRawResponse(HttpResponseData response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (_requestId == 0 || _requestFinalized)
        {
            return response;
        }

        // The reference logs and returns without persisting when no unified
        // response was stored (the response hook cannot run before it).
        if (_llmResponse is null)
        {
            return response;
        }

        _requestFinalized = true;

        TryPersist(() =>
        {
            var now = _timeProvider.GetUtcNow();
            var startedAt = _attempt?.StartedAt ?? _requestStartedAt;

            // UpdateRequestFinalized writes status, external id and the
            // client-facing body; non-stream requests have no first-token
            // latency.
            _recorder.UpdateRequestResult(
                _requestId,
                StatusCompleted,
                _llmResponse.Id,
                RequestChainPayloads.JsonBody(response.Body),
                LatencyMs(startedAt, _attempt?.EndedAt ?? now),
                null,
                now.UtcDateTime);
        });

        return response;
    }

    /// <summary>
    /// Port of persistRequest.OnInboundRawStream: wraps the client-facing event
    /// stream whenever the client asked for a stream (InboundPersistentStream).
    /// </summary>
    public override IStream<StreamEvent> TransformInboundRawStream(IStream<StreamEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return _requestId == 0 ? stream : new RequestRawStream(stream, this);
    }

    /// <summary>
    /// Port of the orchestrator's post-Process failure handling: the last
    /// execution and the request are marked canceled (when the failure itself is
    /// a cancellation) or failed. Called once the pipeline throws.
    /// </summary>
    public void FinalizeFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        TryPersist(() =>
        {
            var now = _timeProvider.GetUtcNow();
            var status = StreamOutcome.IsContextCancellation(error) ? StatusCanceled : StatusFailed;
            var classified = UpstreamTransportErrors.Classify(error);

            if (_attempt is { ExecutionId: not 0 } attempt)
            {
                // persistRequestExecutionFailure is called with nil metrics and
                // an empty upstream model id, so only the outcome is rewritten.
                _recorder.UpdateExecutionStatus(
                    attempt.ExecutionId,
                    status,
                    RequestChainPayloads.ErrorStatusCode(classified),
                    null,
                    RequestChainPayloads.ErrorMessage(classified),
                    null,
                    null,
                    now.UtcDateTime);
            }

            if (_requestId != 0 && !_requestFinalized)
            {
                _recorder.UpdateRequestStatus(_requestId, status, now.UtcDateTime);
                _requestFinalized = true;
            }
        });
    }

    private void ObserveExecutionHeaders(
        ExecutionAttempt attempt,
        IReadOnlyDictionary<string, List<string>>? headers)
    {
        // The reference marks the header as observed before it tries to write,
        // so a nil/empty header map never results in a second write.
        attempt.HeadersObserved = true;

        if (attempt.ExecutionId == 0 || headers is null || headers.Count == 0)
        {
            return;
        }

        var json = RequestChainPayloads.MaskedHeadersJson(headers);

        TryPersist(() => _recorder.UpdateExecutionResponseHeaders(
            attempt.ExecutionId,
            json,
            _timeProvider.GetUtcNow().UtcDateTime));
    }

    private void ObserveExecutionRawEvent(ExecutionAttempt attempt, StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            return;
        }

        if (attempt.UpstreamModelId.Length == 0)
        {
            attempt.UpstreamModelId = RequestChainPayloads.EventModel(streamEvent, attempt.ApiFormat);
        }

        // Only a size summary is kept for binary audio payloads.
        attempt.Chunks.Add(StreamEvent.SummarizeBinaryChunk(streamEvent)!);

        if (attempt.TerminalState != StreamTerminalState.None)
        {
            return;
        }

        if (attempt.Tracker.Observe(streamEvent))
        {
            var state = StreamTerminalClassifier.ClassifyAcceptedTerminalEvent(streamEvent);
            attempt.TerminalState = state;
            _streamState.OutboundStreamTerminal = state;

            // markPerformanceTerminal stops the latency clock at the terminal
            // event, not at Close.
            var now = _timeProvider.GetUtcNow();
            attempt.EndedAt = now;

            if (state != StreamTerminalState.Completed)
            {
                attempt.TerminalError = StreamTerminalClassifier.TerminalErrorMessage(streamEvent, state);
            }

            // The channel outcome follows the same classification: completed and
            // incomplete count as success, canceled is not a channel fault.
            _performance?.Current?.MarkStreamTerminal(state, attempt.TerminalError, now);

            attempt.Outcome.ObserveTerminal(state);
        }
    }

    /// <summary>Port of OutboundPersistentStream.Close.</summary>
    private void FinalizeExecutionStream(ExecutionAttempt attempt, Exception? streamError)
    {
        if (attempt.Closed || attempt.ExecutionId == 0)
        {
            return;
        }

        attempt.Closed = true;

        var contextError = _cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(_cancellationToken)
            : null;
        var outcome = attempt.Outcome;

        outcome.ObserveTransportError(streamError);
        outcome.ObserveContextError(contextError);

        if (outcome.HasFinalEvidence)
        {
            attempt.TerminalState = outcome.FinalState();
        }

        TryPersist(() =>
        {
            var now = _timeProvider.GetUtcNow();

            // A terminal event carries the final outcome. Persist its structured
            // response even if a transport or context error arrives afterward.
            if (attempt.TerminalState != StreamTerminalState.None)
            {
                PersistExecutionResponseChunks(attempt, now);
                return;
            }

            byte[]? responseBody = null;
            var meta = new ResponseMeta();
            Exception? aggregateError = null;
            var aggregatedCompleted = false;

            // If we never received a terminal event, the buffered chunks may
            // still form a complete response (models that aggregate internally,
            // or a proxy that dropped after the full payload).
            if (attempt.Chunks.Count > 0)
            {
                try
                {
                    responseBody = attempt.Transformer.AggregateStreamChunks(
                        attempt.Request,
                        attempt.Chunks,
                        out meta);
                    aggregatedCompleted = responseBody.Length > 0
                        && meta.Completed
                        && !string.IsNullOrEmpty(meta.Id);
                }
                catch (Exception ex)
                {
                    aggregateError = ex;
                }

                if (attempt.ApiFormat == ApiFormats.OpenAIChatCompletion
                    && (streamError is not null || contextError is not null))
                {
                    // Aggregation alone cannot prove a multi-choice stream
                    // completed after the connection broke.
                    aggregatedCompleted = aggregatedCompleted && attempt.Tracker.AllChoicesFinished();
                }
            }

            if (!aggregatedCompleted
                && _streamState.CleanEofCompletionEvidence
                && streamError is null
                && aggregateError is null
                && responseBody is { Length: > 0 })
            {
                aggregatedCompleted = true;
                outcome.ObserveValidatedCompletion();
            }
            else if (aggregatedCompleted)
            {
                if (streamError is not null || contextError is not null)
                {
                    outcome.ObserveValidatedCompletion();
                }
                else
                {
                    outcome.ObserveAggregatedCompletion(true);
                }
            }

            if (aggregatedCompleted)
            {
                // markPerformanceTerminal in the validated/aggregated branch: a
                // complete aggregate without a terminal event counts as success.
                attempt.EndedAt ??= now;
                _performance?.Current?.MarkStreamTerminal(StreamTerminalState.Completed, string.Empty, now);
            }

            if (outcome.HasFinalEvidence)
            {
                attempt.TerminalState = outcome.FinalState();
            }

            var decision = outcome.FinalDecision();
            if (attempt.TerminalState == StreamTerminalState.None)
            {
                attempt.TerminalState = decision.State;
            }

            if (decision.State != StreamTerminalState.Completed)
            {
                // Both reference branches (transport/context error and clean EOF
                // without a terminal event) write the same outcome.
                PersistExecutionFailure(attempt, decision.Cause, now);
                return;
            }

            if (responseBody is { Length: > 0 })
            {
                PersistAggregatedExecution(attempt, responseBody, meta, now);
                return;
            }

            PersistExecutionResponseChunks(attempt, now);
        });
    }

    /// <summary>Port of OutboundPersistentStream.persistResponseChunks.</summary>
    private void PersistExecutionResponseChunks(ExecutionAttempt attempt, DateTimeOffset now)
    {
        byte[] responseBody;
        ResponseMeta meta;

        try
        {
            responseBody = attempt.Transformer.AggregateStreamChunks(
                attempt.Request,
                attempt.Chunks,
                out meta);
        }
        catch (Exception)
        {
            // Aggregation failure must not discard a known terminal outcome or
            // overwrite it with a local processing error. Persist the status
            // alone, without trusting any partial response or usage.
            _recorder.UpdateExecutionStatus(
                attempt.ExecutionId,
                attempt.TerminalState.ToExecutionStatus(),
                null,
                NullIfEmpty(attempt.UpstreamModelId),
                NullIfEmpty(attempt.TerminalError),
                LatencyMs(attempt.StartedAt, attempt.EndedAt ?? now),
                FirstTokenLatencyMs(attempt),
                now.UtcDateTime);
            return;
        }

        PersistAggregatedExecution(attempt, responseBody, meta, now);
    }

    /// <summary>Port of OutboundPersistentStream.persistAggregatedResponse.</summary>
    private void PersistAggregatedExecution(
        ExecutionAttempt attempt,
        byte[] responseBody,
        ResponseMeta meta,
        DateTimeOffset now)
    {
        if (meta.Usage is { } usage)
        {
            LogUsage(usage, attempt);
        }

        _recorder.UpdateExecutionResult(
            attempt.ExecutionId,
            attempt.TerminalState.ToExecutionStatus(),
            meta.Id,
            NullIfEmpty(attempt.UpstreamModelId),
            RequestChainPayloads.JsonBody(responseBody),
            NullIfEmpty(attempt.TerminalError),
            LatencyMs(attempt.StartedAt, attempt.EndedAt ?? now),
            FirstTokenLatencyMs(attempt),
            now.UtcDateTime);
    }

    /// <summary>Port of persistRequestExecutionFailure.</summary>
    private void PersistExecutionFailure(ExecutionAttempt attempt, Exception? cause, DateTimeOffset now)
    {
        var classified = cause is null ? null : UpstreamTransportErrors.Classify(cause);
        var status = cause is not null && StreamOutcome.IsContextCancellation(cause)
            ? StatusCanceled
            : StatusFailed;

        _recorder.UpdateExecutionStatus(
            attempt.ExecutionId,
            status,
            RequestChainPayloads.ErrorStatusCode(classified),
            NullIfEmpty(attempt.UpstreamModelId),
            RequestChainPayloads.ErrorMessage(classified),
            LatencyMs(attempt.StartedAt, attempt.EndedAt ?? now),
            FirstTokenLatencyMs(attempt),
            now.UtcDateTime);
    }

    private void ObserveRequestEvent(StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            return;
        }

        // Only a size summary is kept for binary audio payloads.
        _requestChunks.Add(StreamEvent.SummarizeBinaryChunk(streamEvent)!);

        // The reference stops observing once a terminal event was accepted.
        if (_requestTerminalState != StreamTerminalState.None)
        {
            return;
        }

        if (streamEvent.CleanEofCompletionEvidence)
        {
            _streamState.CleanEofCompletionEvidence = true;
        }

        if (_requestTerminalTracker.Observe(streamEvent))
        {
            _requestTerminalState = StreamTerminalClassifier.ClassifyAcceptedTerminalEvent(streamEvent);

            // The generic finish_reason may hide a provider failure the
            // outbound stream already saw.
            _requestTerminalState = FinalRequestTerminalState();
            _requestOutcome.ObserveTerminal(_requestTerminalState);
        }
    }

    /// <summary>
    /// Generic finish_reason/message_stop events can lose the provider's failure
    /// or cancellation; the outbound stream's non-successful outcome wins.
    /// </summary>
    private StreamTerminalState FinalRequestTerminalState()
        => _streamState.OutboundStreamTerminal is StreamTerminalState.Failed
            or StreamTerminalState.Incomplete
            or StreamTerminalState.Canceled
            ? _streamState.OutboundStreamTerminal
            : _requestTerminalState;

    /// <summary>Port of InboundPersistentStream.Close.</summary>
    private void FinalizeRequestStream(Exception? streamError)
    {
        if (_requestId == 0 || _requestFinalized)
        {
            return;
        }

        _requestFinalized = true;

        var contextError = _cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(_cancellationToken)
            : null;
        var outcome = _requestOutcome;

        outcome.ObserveTransportError(streamError);
        outcome.ObserveContextError(contextError);
        _requestTerminalState = FinalRequestTerminalState();

        if (outcome.HasFinalEvidence)
        {
            _requestTerminalState = outcome.FinalState();
        }

        TryPersist(() =>
        {
            var now = _timeProvider.GetUtcNow();

            if (_requestTerminalState != StreamTerminalState.None)
            {
                PersistRequestResponseChunks(now);
                return;
            }

            byte[]? responseBody = null;
            var meta = new ResponseMeta();
            Exception? aggregateError = null;

            if (_requestChunks.Count > 0 && !outcome.HasFinalEvidence)
            {
                try
                {
                    responseBody = _inbound.AggregateStreamChunks(_requestChunks, out meta);
                }
                catch (Exception ex)
                {
                    aggregateError = ex;
                }

                var aggregatedCompleted = meta.Completed;
                if (_format == ApiFormats.OpenAIChatCompletion
                    && (streamError is not null || contextError is not null))
                {
                    aggregatedCompleted = aggregatedCompleted && _requestTerminalTracker.AllChoicesFinished();
                }

                if (aggregateError is null
                    && !string.IsNullOrEmpty(meta.Id)
                    && responseBody is { Length: > 0 }
                    && aggregatedCompleted)
                {
                    if (streamError is not null || contextError is not null)
                    {
                        outcome.ObserveValidatedCompletion();
                    }
                    else
                    {
                        outcome.ObserveAggregatedCompletion(true);
                    }
                }
            }

            if (outcome.HasFinalEvidence)
            {
                _requestTerminalState = outcome.FinalState();
            }

            var decision = outcome.FinalDecision();
            if (_requestTerminalState == StreamTerminalState.None)
            {
                _requestTerminalState = decision.State;
            }

            if (decision.State != StreamTerminalState.Completed)
            {
                // UpdateRequestStatusFromError, called with a detached context,
                // only looks at the error itself.
                _recorder.UpdateRequestStatus(
                    _requestId,
                    RequestChainPayloads.StatusFromError(decision.Cause),
                    now.UtcDateTime);
                return;
            }

            if (responseBody is { Length: > 0 })
            {
                PersistRequestResponse(responseBody, meta, now);
                return;
            }

            PersistRequestResponseChunks(now);
        });
    }

    /// <summary>Port of InboundPersistentStream.persistResponseChunks.</summary>
    private void PersistRequestResponseChunks(DateTimeOffset now)
    {
        var responseBody = Array.Empty<byte>();
        var meta = new ResponseMeta();

        try
        {
            responseBody = _inbound.AggregateStreamChunks(_requestChunks, out meta);
        }
        catch (Exception)
        {
            // The reference logs the aggregation failure and still persists the
            // terminal status with the zero meta (empty external id).
        }

        PersistRequestResponse(responseBody, meta, now);
    }

    /// <summary>Port of InboundPersistentStream._persistResponse.</summary>
    private void PersistRequestResponse(byte[] responseBody, ResponseMeta meta, DateTimeOffset now)
    {
        var startedAt = _attempt?.StartedAt ?? _requestStartedAt;

        _recorder.UpdateRequestResult(
            _requestId,
            _requestTerminalState.ToRequestStatus(),
            meta.Id,
            RequestChainPayloads.JsonBody(responseBody),
            LatencyMs(startedAt, _attempt?.EndedAt ?? now),
            FirstTokenLatencyMs(_attempt),
            now.UtcDateTime);
    }

    /// <summary>Port of CreateUsageLogFromRequest for this slice.</summary>
    private void LogUsage(Usage? usage, ExecutionAttempt attempt)
    {
        if (usage is null || attempt.UsageLogged || _requestId == 0 || attempt.ExecutionId == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // CreateUsageLogFromRequest prices the usage independently of the
        // inject-usage-cost setting; injection only decides whether the cost is
        // exposed to the client.
        var cost = TryComputeCost(usage, attempt);

        // CreateUsageLogFromRequest uses request.APIKeyID, which is Go's zero
        // value when authentication has not run.
        _recorder.CreateUsageLog(new UsageLogRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            ApiKeyId = _identity.ApiKeyId ?? 0,
            ModelId = attempt.ModelId,
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens,
            TotalTokens = usage.TotalTokens,
            PromptAudioTokens = usage.PromptTokensDetails?.AudioTokens,
            PromptCachedTokens = usage.PromptTokensDetails?.CachedTokens,
            PromptWriteCachedTokens = usage.PromptTokensDetails?.WriteCachedTokens,
            PromptWriteCachedTokens5m = usage.PromptTokensDetails?.WriteCached5MinTokens,
            PromptWriteCachedTokens1h = usage.PromptTokensDetails?.WriteCached1HourTokens,
            CompletionAudioTokens = usage.CompletionTokensDetails?.AudioTokens,
            CompletionReasoningTokens = usage.CompletionTokensDetails?.ReasoningTokens,
            CompletionAcceptedPredictionTokens = usage.CompletionTokensDetails?.AcceptedPredictionTokens,
            CompletionRejectedPredictionTokens = usage.CompletionTokensDetails?.RejectedPredictionTokens,
            Source = "api",
            Format = _format,
            TotalCost = cost?.TotalCost,

            // SetCostItems is always called: without a price it receives nil,
            // which Ent marshals as JSON null (bypassing the "[]" column
            // default). When the cost seam is absent the previous port
            // behaviour is kept.
            CostItems = cost is not null
                ? BillingJson.SerializeCostItems(cost.Items)
                : _usageCostService is null ? "[]" : "null",
            CostPriceReferenceId = string.IsNullOrEmpty(cost?.ReferenceId) ? null : cost.ReferenceId,
            ChannelId = attempt.ChannelId,
            ProjectId = _identity.ProjectId,
            RequestId = _requestId,
        });

        attempt.UsageLogged = true;
    }

    /// <summary>
    /// Port of persistRequestMiddleware.injectUsageCost: writes the calculated
    /// cost onto the unified usage (null when no price matches) when
    /// system_inject_usage_cost is true.
    /// </summary>
    private void InjectUsageCost(LlmResponse? response)
    {
        if (_usageCostService is null
            || response?.Usage is not { } usage
            || _attempt is not { } attempt)
        {
            return;
        }

        try
        {
            if (!_usageCostService.InjectUsageCostEnabled())
            {
                return;
            }

            // A lookup miss clears an upstream-reported cost as well.
            usage.Cost = _usageCostService.Compute(attempt.ChannelId, attempt.ModelId, usage)?.TotalCost;
        }
        catch (Exception)
        {
            // The reference logs a warning and leaves usage untouched when the
            // setting cannot be read; cost lookup is never allowed to fail a
            // client call.
        }
    }

    /// <summary>The usage-log side of computeUsageCost, independent of the injection gate.</summary>
    private UsageCost? TryComputeCost(Usage usage, ExecutionAttempt attempt)
    {
        if (_usageCostService is null)
        {
            return null;
        }

        try
        {
            return _usageCostService.Compute(attempt.ChannelId, attempt.ModelId, usage);
        }
        catch (Exception)
        {
            // Cost is derived data: a lookup failure must not lose the token log.
            return null;
        }
    }

    /// <summary>
    /// First-token latency mirrors PerformanceRecord: only streams report it,
    /// and it is clamped like every other latency (biz.MinLatencyMs).
    /// </summary>
    private long? FirstTokenLatencyMs(ExecutionAttempt? attempt)
        => _stream && attempt?.FirstTokenAt is { } firstToken
            ? LatencyMs(attempt.StartedAt, firstToken)
            : null;

    /// <summary>
    /// Port of the channel_api_key_suffix rule (biz/request.go): the last four
    /// runes, omitted entirely when the key is not longer than four runes.
    /// </summary>
    private static string? ApiKeySuffix(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        var runes = apiKey.EnumerateRunes().ToArray();
        return runes.Length > 4
            ? string.Concat(runes[^4..].Select(static rune => rune.ToString()))
            : null;
    }

    /// <summary>
    /// Port of ClampLatency: the reference enforces a minimum of 10ms on every
    /// reported latency, never a maximum.
    /// </summary>
    private static long LatencyMs(DateTimeOffset start, DateTimeOffset end)
        => Math.Max(MinLatencyMs, (long)(end - start).TotalMilliseconds);

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static void TryPersist(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // The reference logs and continues when a persistence update fails;
            // the data path must not turn a logging problem into a client error.
        }
    }

    /// <summary>State one attempt owns (reset when the next attempt starts).</summary>
    private sealed class ExecutionAttempt
    {
        public long ExecutionId { get; set; }

        public DateTimeOffset StartedAt { get; init; }

        /// <summary>
        /// The performance record's EndTime: set when the terminal event (or the
        /// aggregated completion) is observed, so latency stops there.
        /// </summary>
        public DateTimeOffset? EndedAt { get; set; }

        public IOutboundTransformer Transformer { get; init; } = null!;

        public HttpRequestData? Request { get; init; }

        public string ApiFormat { get; init; } = ApiFormats.OpenAIChatCompletion;

        public string ModelId { get; init; } = string.Empty;

        public long ChannelId { get; init; }

        public string UpstreamModelId { get; set; } = string.Empty;

        public DateTimeOffset? FirstTokenAt { get; set; }

        public List<StreamEvent> Chunks { get; } = [];

        public StreamTerminalTracker Tracker { get; init; } = null!;

        public StreamOutcome Outcome { get; } = new();

        public StreamTerminalState TerminalState { get; set; } = StreamTerminalState.None;

        public string TerminalError { get; set; } = string.Empty;

        public HttpResponseData? RawResponse { get; set; }

        public bool HeadersObserved { get; set; }

        public bool NonStreamFinalized { get; set; }

        public bool Closed { get; set; }

        /// <summary>CreateUsageLogFromRequest runs once per successful attempt.</summary>
        public bool UsageLogged { get; set; }
    }

    /// <summary>PersistenceState subset shared by the inbound and outbound streams.</summary>
    private sealed class RequestStreamState
    {
        public StreamTerminalState OutboundStreamTerminal { get; set; } = StreamTerminalState.None;

        public bool CleanEofCompletionEvidence { get; set; }
    }

    /// <summary>Port of OutboundPersistentStream's stream surface.</summary>
    private sealed class ExecutionRawStream(
        IStream<StreamEvent> stream,
        RequestChainPersistenceMiddleware owner,
        ExecutionAttempt attempt) : IStream<StreamEvent>
    {
        private bool _disposed;

        public StreamEvent? Current
        {
            get
            {
                var streamEvent = stream.Current;
                owner.ObserveExecutionRawEvent(attempt, streamEvent);
                return streamEvent;
            }
        }

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
                owner.FinalizeExecutionStream(attempt, stream.Err);
            }
            finally
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>Port of InboundPersistentStream's stream surface.</summary>
    private sealed class RequestRawStream(IStream<StreamEvent> stream, RequestChainPersistenceMiddleware owner)
        : IStream<StreamEvent>
    {
        private bool _disposed;

        public StreamEvent? Current
        {
            get
            {
                var streamEvent = stream.Current;
                owner.ObserveRequestEvent(streamEvent);
                return streamEvent;
            }
        }

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
                owner.FinalizeRequestStream(stream.Err);
            }
            finally
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>Marks the first token on the first unified event with real content.</summary>
    private sealed class FirstTokenStream(
        IStream<LlmResponse> stream,
        ExecutionAttempt attempt,
        RequestChainPersistenceMiddleware owner) : IStream<LlmResponse>
    {
        private bool _disposed;

        /// <summary>
        /// Port of usageCostStream.Current: every response handed to the inbound
        /// side gets its usage cost injected, so streamed chunks carry
        /// usage.cost just like non-stream responses.
        /// </summary>
        public LlmResponse? Current
        {
            get
            {
                var response = stream.Current;
                owner.InjectUsageCost(response);
                return response;
            }
        }

        public Exception? Err => stream.Err;

        public bool Next()
        {
            if (!stream.Next())
            {
                return false;
            }

            if (attempt.FirstTokenAt is null
                && stream.Current is { } response
                && ResponseContent.HasMeaningfulStreamOutput(response))
            {
                attempt.FirstTokenAt = owner._timeProvider.GetUtcNow();
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
            stream.Dispose();
        }
    }
}

/// <summary>JSON payload conversion shared by the request-chain middleware.</summary>
internal static class RequestChainPayloads
{
    private const string MaskedValue = "******";
    private const int MaxModelBytes = 512;

    public static string MaskedHeadersJson(IReadOnlyDictionary<string, List<string>>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return "{}";
        }

        var masked = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, values) in headers)
        {
            masked[key] = HttpClientUtils.IsSensitiveHeader(key)
                ? [MaskedValue]
                : [.. values];
        }

        return JsonSerializer.Serialize(masked);
    }

    /// <summary>
    /// Stores a request/response body in a jsonb column. Valid JSON is kept
    /// verbatim; anything else is stored as a JSON string, mirroring Go's
    /// <c>xjson.Marshal</c> of a non-JSON body.
    /// </summary>
    public static string JsonBody(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return "{}";
        }

        try
        {
            using var _ = JsonDocument.Parse(body);
            return Encoding.UTF8.GetString(body);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(Encoding.UTF8.GetString(body));
        }
    }

    /// <summary>
    /// Port of modelname.FromEvent for the formats that report a top-level
    /// <c>model</c>: OpenAI chat, legacy completions and Ollama chat. The first
    /// event that carries a valid name wins. Binary audio chunks carry no
    /// metadata.
    /// </summary>
    public static string EventModel(StreamEvent streamEvent, string apiFormat)
    {
        if (apiFormat is not (ApiFormats.OpenAIChatCompletion
                or ApiFormats.OpenAICompletion
                or ApiFormats.OllamaChat)
            || streamEvent.IsBinaryAudioChunk())
        {
            return string.Empty;
        }

        return ReportedModel(streamEvent.Data);
    }

    /// <summary>
    /// Port of modelname's top-level <c>model</c> path, shared by the OpenAI
    /// chat / legacy completion and Ollama chat formats: the reported model in
    /// the provider body (Cline's success/data envelope is unwrapped),
    /// validated like jsonModel.
    /// </summary>
    public static string ReportedModel(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            // gjson ignores ill-formed UTF-8; json.Unmarshal repairs it.
            using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(body) ?? body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
            {
                return root.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("model", out var envelopeModel)
                    ? ValidModel(envelopeModel)
                    : string.Empty;
            }

            return root.TryGetProperty("model", out var model) ? ValidModel(model) : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Port of modelname.validModel.</summary>
    public static string ValidModel(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || Encoding.UTF8.GetByteCount(value) > MaxModelBytes
            || value.Trim().Length == 0)
        {
            return string.Empty;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return string.Empty;
            }
        }

        return value;
    }

    public static string? ErrorMessage(Exception? error)
    {
        if (error is null)
        {
            return null;
        }

        if (GoErrors.As<ResponseError>(error) is { } responseError
            && !string.IsNullOrEmpty(responseError.Detail.Message))
        {
            return responseError.Detail.Message;
        }

        if (GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            return HttpErrorMessage(httpError);
        }

        return error.Message;
    }

    public static long? ErrorStatusCode(Exception? error)
    {
        if (error is null)
        {
            return null;
        }

        if (GoErrors.As<HttpClientError>(error) is { } httpError && httpError.StatusCode != 0)
        {
            return httpError.StatusCode;
        }

        var responseError = GoErrors.As<ResponseError>(error);
        return responseError is { StatusCode: not 0 } ? responseError.StatusCode : null;
    }

    /// <summary>Port of UpdateRequestStatusFromError's status choice.</summary>
    public static string StatusFromError(Exception? cause)
        => cause is not null && StreamOutcome.IsContextCancellation(cause) ? "canceled" : "failed";

    private static string HttpErrorMessage(HttpClientError error)
    {
        if (error.Body.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(error.Body);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("error", out var errorElement)
                    && errorElement.ValueKind == JsonValueKind.Object
                    && errorElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? string.Empty;
                }

                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("errors", out var errors))
                {
                    if (errors.ValueKind == JsonValueKind.Array
                        && errors.GetArrayLength() > 0
                        && errors[0].ValueKind == JsonValueKind.Object
                        && errors[0].TryGetProperty("message", out var first)
                        && first.ValueKind == JsonValueKind.String
                        && first.GetString() is { Length: > 0 } firstMessage)
                    {
                        return firstMessage;
                    }

                    if (errors.ValueKind == JsonValueKind.Object
                        && errors.TryGetProperty("message", out var nested)
                        && nested.ValueKind == JsonValueKind.String)
                    {
                        return nested.GetString() ?? error.Message;
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through to the HTTP status message.
            }
        }

        return error.Message;
    }

    private static string ValidModel(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? ValidModel(value.GetString()) : string.Empty;
}
