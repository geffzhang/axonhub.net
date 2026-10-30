using System.Text;
using System.Text.Json;
using AxonHub.Api.Http;
using AxonHub.Api.Persistence;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
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
/// request hook, and finalizes both at the terminal event (or at the error that
/// ended the attempt). The recorder is synchronous by design; see C018.
/// </remarks>
public sealed class RequestChainPersistenceMiddleware : DummyMiddleware
{
    private const string StatusProcessing = "processing";
    private const string StatusCompleted = "completed";
    private const string StatusFailed = "failed";
    private const string StatusCanceled = "canceled";

    private readonly IRequestChainRecorder _recorder;
    private readonly PersistentOutboundTransformer _outbound;
    private readonly HttpRequestData _rawRequest;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _cancellationToken;
    private readonly StreamTerminalTracker _requestTerminalTracker;

    private long _requestId;
    private long _executionId;
    private long? _requestChannelId;
    private bool _stream;
    private string _format = ApiFormats.OpenAIChatCompletion;
    private DateTimeOffset _requestStartedAt;
    private DateTimeOffset _attemptStartedAt;
    private DateTimeOffset? _firstTokenAt;
    private HttpResponseData? _rawResponse;
    private LlmResponse? _llmResponse;
    private IReadOnlyDictionary<string, List<string>>? _rawResponseHeaders;
    private Usage? _streamUsage;
    private string _upstreamModelId = string.Empty;
    private bool _executionTerminal;
    private bool _executionCompleted;
    private bool _requestFinalized;
    private bool _usageLogged;

    public RequestChainPersistenceMiddleware(
        IRequestChainRecorder recorder,
        PersistentOutboundTransformer outbound,
        HttpRequestData rawRequest,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(rawRequest);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _recorder = recorder;
        _outbound = outbound;
        _rawRequest = rawRequest;
        _timeProvider = timeProvider;
        _cancellationToken = cancellationToken;
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
            ProjectId = 1,
        });

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

        var now = _timeProvider.GetUtcNow();
        _attemptStartedAt = now;
        _rawResponse = null;
        _rawResponseHeaders = null;
        _firstTokenAt = null;
        _streamUsage = null;
        _upstreamModelId = string.Empty;
        _executionTerminal = false;
        _executionCompleted = false;

        // Both writes return their error in the reference, so a failure here
        // fails the attempt instead of letting it run unrecorded.
        _executionId = _recorder.CreateExecution(new RequestExecutionRecord
        {
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
            ProjectId = 1,
            ModelId = _outbound.CurrentModel,
            Format = _outbound.ApiFormat,
            RequestBody = RequestChainPayloads.JsonBody(request.JsonBody ?? request.Body),
            RequestHeaders = RequestChainPayloads.MaskedHeadersJson(request.Headers),
            RequestUrl = request.Url,
            Status = StatusProcessing,
            Stream = _stream,
            RequestId = _requestId,
            ChannelId = _outbound.CurrentChannelId,
        });

        // The transport reports stream response headers before the first event;
        // the execution row can store them even though a stream never produces a
        // raw HttpResponseData.
        request.OnResponseHeaders = headers => _rawResponseHeaders = headers;

        if (_outbound.CurrentChannelId != 0 && _requestChannelId != _outbound.CurrentChannelId)
        {
            _recorder.UpdateRequestChannel(_requestId, _outbound.CurrentChannelId);
            _requestChannelId = _outbound.CurrentChannelId;
        }

        return request;
    }

    public override HttpResponseData TransformOutboundRawResponse(HttpResponseData response)
    {
        _rawResponse = response;
        return response;
    }

    public override LlmResponse TransformOutboundLlmResponse(LlmResponse response)
    {
        _llmResponse = response;
        FinalizeExecution(response);
        return response;
    }

    /// <summary>
    /// Port of persistRequestExecution.OnOutboundRawError. The reference always
    /// records the attempt as failed here; a client cancellation is turned into
    /// "canceled" by the orchestrator's final status update.
    /// </summary>
    public override void OnOutboundRawError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (_executionId == 0 || _executionCompleted)
        {
            return;
        }

        TryPersist(() =>
        {
            var classified = UpstreamTransportErrors.Classify(error);

            // The reference passes nil metrics and derives the upstream model
            // from the raw response, so this update only records the outcome.
            _recorder.UpdateExecutionStatus(
                _executionId,
                StatusFailed,
                RequestChainPayloads.ErrorStatusCode(classified),
                NullIfEmpty(RequestChainPayloads.ReportedModel(_rawResponse?.Body)),
                RequestChainPayloads.ErrorMessage(classified),
                null,
                null,
                _timeProvider.GetUtcNow().UtcDateTime);
        });
    }

    public override IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream)
        => new ExecutionObservationStream(stream, this);

    public override HttpResponseData TransformInboundRawResponse(HttpResponseData response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (_requestId == 0 || _requestFinalized)
        {
            return response;
        }

        TryPersist(() =>
        {
            var now = _timeProvider.GetUtcNow();

            // UpdateRequestFinalized writes status and external id, keeps latency
            // metrics, and stores the client-facing body. Response headers are
            // written by the downstream response-header middleware in the
            // reference; the port stores the client response headers here.
            _recorder.UpdateRequestResult(
                _requestId,
                StatusCompleted,
                _llmResponse?.Id,
                RequestChainPayloads.MaskedHeadersJson(response.Headers),
                RequestChainPayloads.JsonBody(response.Body),
                null,
                ElapsedMs(_requestStartedAt),
                null,
                now.UtcDateTime);

            _requestFinalized = true;
        });

        return response;
    }

    public override IStream<StreamEvent> TransformInboundRawStream(IStream<StreamEvent> stream)
        => new RequestObservationStream(stream, this);

    /// <summary>
    /// Port of the orchestrator's post-Process failure handling: the last
    /// execution and the request are marked canceled (client cancellation) or
    /// failed. Called once the pipeline throws.
    /// </summary>
    public void FinalizeFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        TryPersist(() =>
        {
            var now = _timeProvider.GetUtcNow();
            var canceled = RequestChainPayloads.IsCancellation(error, _cancellationToken);
            var status = canceled ? StatusCanceled : StatusFailed;
            var classified = UpstreamTransportErrors.Classify(error);

            if (_executionId != 0 && !_executionCompleted)
            {
                // persistRequestExecutionFailure is called with nil metrics and
                // an empty upstream model id, so only the outcome is rewritten.
                _recorder.UpdateExecutionStatus(
                    _executionId,
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

    /// <summary>Finalizes the request after the client stream ended.</summary>
    internal void FinalizeRequestStream(Exception? streamError)
    {
        if (_requestId == 0 || _requestFinalized)
        {
            return;
        }

        TryPersist(() =>
        {
            var now = _timeProvider.GetUtcNow();
            var completed = _requestTerminalTracker.Terminal;

            if (completed)
            {
                _recorder.UpdateRequestResult(
                    _requestId,
                    StatusCompleted,
                    null,
                    null,
                    null,
                    null,
                    ElapsedMs(_requestStartedAt),
                    FirstTokenLatencyMs(),
                    now.UtcDateTime);
            }
            else
            {
                _recorder.UpdateRequestStatus(
                    _requestId,
                    RequestChainPayloads.TerminalStatus(streamError, _cancellationToken),
                    now.UtcDateTime);
            }

            _requestFinalized = true;
        });
    }

    private void ObserveRequestEvent(StreamEvent? streamEvent)
        => _requestTerminalTracker.Observe(streamEvent);

    private void ObserveExecutionEvent(LlmResponse? response)
    {
        if (response is null)
        {
            return;
        }

        if (IsTerminal(response))
        {
            _executionTerminal = true;
        }

        if (response.Usage is { } usage)
        {
            _streamUsage = usage;
        }

        if (_upstreamModelId.Length == 0)
        {
            _upstreamModelId = RequestChainPayloads.ValidModel(response.Model);
        }

        if (_firstTokenAt is null && ResponseContent.HasResponseContent(response))
        {
            _firstTokenAt = _timeProvider.GetUtcNow();
        }
    }

    /// <summary>Finalizes the execution after the unified stream ended.</summary>
    internal void FinalizeExecutionStream(Exception? streamError)
    {
        if (_executionId == 0 || _executionCompleted)
        {
            return;
        }

        TryPersist(() =>
        {
            var now = _timeProvider.GetUtcNow();
            var completed = _executionTerminal;

            if (completed)
            {
                _recorder.UpdateExecutionResult(
                    _executionId,
                    StatusCompleted,
                    null,
                    NullIfEmpty(_upstreamModelId),
                    RequestChainPayloads.MaskedHeadersJson(_rawResponseHeaders),
                    null,
                    null,
                    null,
                    ElapsedMs(_attemptStartedAt),
                    FirstTokenLatencyMs(),
                    now.UtcDateTime);

                _executionCompleted = true;
                LogUsage(_streamUsage);
            }
            else
            {
                var classified = streamError is null ? null : UpstreamTransportErrors.Classify(streamError);

                _recorder.UpdateExecutionStatus(
                    _executionId,
                    RequestChainPayloads.TerminalStatus(streamError, _cancellationToken),
                    RequestChainPayloads.ErrorStatusCode(classified),
                    NullIfEmpty(_upstreamModelId),
                    classified is null
                        ? RequestChainPayloads.StreamEndedMessage(_cancellationToken)
                        : RequestChainPayloads.ErrorMessage(classified),
                    ElapsedMs(_attemptStartedAt),
                    FirstTokenLatencyMs(),
                    now.UtcDateTime);
            }
        });
    }

    private void FinalizeExecution(LlmResponse response)
    {
        if (_executionId == 0 || _executionCompleted)
        {
            return;
        }

        TryPersist(() =>
        {
            _recorder.UpdateExecutionResult(
                _executionId,
                StatusCompleted,
                response.Id,
                NullIfEmpty(RequestChainPayloads.ReportedModel(_rawResponse?.Body)),
                RequestChainPayloads.MaskedHeadersJson(_rawResponse?.Headers),
                RequestChainPayloads.JsonBody(_rawResponse?.Body),
                null,
                null,
                ElapsedMs(_attemptStartedAt),
                FirstTokenLatencyMs(),
                _timeProvider.GetUtcNow().UtcDateTime);

            _executionCompleted = true;
            LogUsage(response.Usage);
        });
    }

    private void LogUsage(Usage? usage)
    {
        if (usage is null || _usageLogged || _requestId == 0 || _executionId == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // CreateUsageLogFromRequest uses request.APIKeyID, which is Go's zero
        // value when authentication has not run (C014).
        _recorder.CreateUsageLog(new UsageLogRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            ApiKeyId = 0,
            ModelId = _outbound.CurrentModel,
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
            CostItems = "[]",
            ChannelId = _outbound.CurrentChannelId,
            ProjectId = 1,
            RequestId = _requestId,
        });

        _usageLogged = true;
    }

    private long? FirstTokenLatencyMs()
        => _firstTokenAt is { } firstToken ? ElapsedMs(_attemptStartedAt, firstToken) : null;

    private long ElapsedMs(DateTimeOffset since) => ElapsedMs(since, _timeProvider.GetUtcNow());

    private static long ElapsedMs(DateTimeOffset since, DateTimeOffset until)
        => Math.Max(0, (long)(until - since).TotalMilliseconds);

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static bool IsTerminal(LlmResponse response)
    {
        if (ReferenceEquals(response, LlmResponse.DoneResponse) || response.Object == "[DONE]")
        {
            return true;
        }

        foreach (var choice in response.Choices)
        {
            if (!string.IsNullOrEmpty(choice.FinishReason))
            {
                return true;
            }
        }

        return false;
    }

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

    private sealed class ExecutionObservationStream(IStream<LlmResponse> stream, RequestChainPersistenceMiddleware owner)
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

            owner.ObserveExecutionEvent(stream.Current);
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
                owner.FinalizeExecutionStream(stream.Err);
            }
            finally
            {
                stream.Dispose();
            }
        }
    }

    private sealed class RequestObservationStream(IStream<StreamEvent> stream, RequestChainPersistenceMiddleware owner)
        : IStream<StreamEvent>
    {
        private bool _disposed;

        public StreamEvent? Current => stream.Current;

        public Exception? Err => stream.Err;

        public bool Next()
        {
            if (!stream.Next())
            {
                return false;
            }

            owner.ObserveRequestEvent(stream.Current);
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
                owner.FinalizeRequestStream(stream.Err);
            }
            finally
            {
                stream.Dispose();
            }
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
    /// Port of modelname.FromResponse's OpenAI chat path: the first reported
    /// model in the provider response body (Cline's success/data envelope is
    /// unwrapped), validated like jsonModel.
    /// </summary>
    public static string ReportedModel(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
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

    public static string TerminalStatus(Exception? streamError, CancellationToken cancellationToken)
        => IsCancellation(streamError, cancellationToken) ? "canceled" : "failed";

    public static string StreamEndedMessage(CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
            ? "request canceled"
            : "stream ended without terminal event";

    public static bool IsCancellation(Exception? error, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return true;
        }

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
    /// Port of orchestrator.ExtractErrorMessage's gjson lookup. gjson reports a
    /// string field as found even when empty, so only the <c>errors[0].message</c>
    /// fallback requires a non-empty value.
    /// </summary>
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
