using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Pipeline;

/// <summary>
/// Port of llm/pipeline/pipeline.go: inbound transform, then attempts with
/// same-channel retry and channel switching, then the inbound transform back to
/// the client protocol.
/// </summary>
/// <remarks>
/// Two loops live here and they are not the same loop:
/// <list type="bullet">
/// <item><see cref="Process"/> owns the <b>request</b>: one client call, many
/// attempts. It reads the retry policy once and stops on context cancellation,
/// resource-limit failures, and exhausted budgets.</item>
/// <item><see cref="ProcessRequest"/> owns the <b>attempt</b>: one outbound
/// transform, one execution, one response or stream. Retry state must not leak
/// between attempts, which is why the original stream intent is restored at the
/// top of every iteration.</item>
/// </list>
/// </remarks>
public sealed partial class Pipeline
{
    private readonly IExecutor _executor;
    private readonly IInboundTransformer _inbound;
    private readonly IOutboundTransformer _outbound;
    private readonly PipelineOptions _options;

    public Pipeline(
        IExecutor executor,
        IInboundTransformer inbound,
        IOutboundTransformer outbound,
        PipelineOptions? options = null)
    {
        _executor = executor;
        _inbound = inbound;
        _outbound = outbound;
        _options = options ?? new PipelineOptions();
    }

    /// <summary>Port of pipeline.Process.</summary>
    public PipelineResult Process(HttpRequestData? request, CancellationToken cancellationToken = default)
    {
        var llmRequest = _inbound.TransformRequest(request!);

        // Make the original raw request available before request middlewares run.
        llmRequest.RawRequest = request;

        llmRequest = ApplyBeforeRequestMiddlewares(llmRequest);

        var originalStream = llmRequest.Stream;

        Exception? lastErr = null;
        var channelSwitches = 0;
        var sameChannelRetries = 0;

        while (true)
        {
            llmRequest.Stream = originalStream;

            try
            {
                return ProcessRequest(llmRequest, cancellationToken);
            }
            catch (Exception ex)
            {
                lastErr = ex;
            }

            // A missing Responses continuation context cannot be repaired by
            // replaying the same previous_response_id (pipeline.go:322-325).
            if (ResponsesRecovery.IsRecoveryError(lastErr))
            {
                throw ResponsesRecovery.Error();
            }

            // A transport pairing the transformers cannot produce is not
            // repairable by another attempt (pipeline.go:326-328).
            if (TransformerErrors.IsUnsupportedConversion(lastErr))
            {
                throw lastErr;
            }

            // Resource limits are local facts about this attempt's payload; retrying
            // would hit the same limit, so they bypass the whole retry policy.
            if (GoErrors.IsAny(lastErr, PipelineErrors.PreCommitBufferExceeded, StreamLimits.EventTooLarge))
            {
                throw lastErr;
            }

            // Stop retrying if the caller went away.
            if (cancellationToken.IsCancellationRequested)
            {
                throw lastErr;
            }

            var canRetry = false;
            var timeoutRetry = IsResponseTimeoutError(lastErr);

            // 1. Try same-channel retry first if supported. Response timeouts skip
            //    it: the channel just failed to answer in time, so retrying it again
            //    would spend the budget without new information.
            if (!timeoutRetry && _outbound is IChannelRetryable channelRetryable)
            {
                if (sameChannelRetries < _options.MaxSameChannelRetries && channelRetryable.CanRetry(lastErr))
                {
                    var prepareErr = channelRetryable.TryPrepareForRetry(cancellationToken);
                    if (prepareErr is null)
                    {
                        sameChannelRetries++;
                        canRetry = true;
                    }
                }
            }

            // 2. If same-channel retry is impossible or exhausted, try channel switching.
            if (!canRetry && _outbound is IRetryable retryable)
            {
                if (channelSwitches < _options.MaxChannelRetries && retryable.HasMoreChannels())
                {
                    var switchErr = retryable.TryNextChannel(cancellationToken);
                    if (switchErr is null)
                    {
                        channelSwitches++;
                        sameChannelRetries = 0; // Fresh budget for the new channel.
                        canRetry = true;
                    }
                }
            }

            if (!canRetry)
            {
                break;
            }

            if (_options.RetryDelay > TimeSpan.Zero)
            {
                Thread.Sleep(_options.RetryDelay);
            }
        }

        throw lastErr!;
    }

    /// <summary>Port of pipeline.processRequest.</summary>
    private PipelineResult ProcessRequest(LlmRequest request, CancellationToken cancellationToken)
    {
        var originalWantStream = request.Stream is true;
        var metadata = CloneMetadata(request.TransformerMetadata);

        // Go passes ctx to TransformRequest (pipeline.go); the port's transformer
        // interface is synchronous, so the token rides on the unified request and
        // OAuth token providers can cancel a refresh when the client goes away
        // (C094 (6)).
        request.RequestCancellationToken = cancellationToken;

        HttpRequestData httpReq;
        try
        {
            httpReq = _outbound.TransformRequest(request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "failed to transform request: ");
        }

        httpReq.TransformerMetadata = MergeTransformerMetadata(metadata, httpReq.TransformerMetadata);
        httpReq = HttpClientUtils.MergeInboundRequest(httpReq, request.RawRequest);

        // The reference carries the Responses lane and attempt in the request
        // context; the port threads them through the wire request (C289).
        httpReq.ResponsesLane = request.RawRequest?.ResponsesLane;
        httpReq.ResponsesAttempt = request.RawRequest?.ResponsesAttempt;
        httpReq.ResponsesWebSocket = request.RawRequest?.ResponsesWebSocket ?? false;

        try
        {
            httpReq = HttpClientUtils.FinalizeAuthHeaders(httpReq);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "invalid authentication config: ");
        }

        try
        {
            httpReq = ApplyRawRequestMiddlewares(httpReq);
        }
        catch (Exception ex)
        {
            ApplyRawErrorMiddlewares(ex);
            throw GoErrors.Wrap(ex, "failed to apply raw request middlewares: ");
        }

        var executor = _executor;
        if (_outbound is IChannelCustomizedExecutor custom)
        {
            executor = custom.CustomizeExecutor(executor);
        }

        // Resolve after TransformRequest so the outbound transformer has selected
        // the channel for this attempt and can apply per-channel overrides
        // (pipeline.go:438-446).
        var (streamFirstEventTimeout, nonStreamTimeout) = EffectiveResponseTimeouts();
        var effectiveWantStream = request.Stream is true;

        if (originalWantStream)
        {
            try
            {
                var stream = Stream(cancellationToken, executor, httpReq, streamFirstEventTimeout);
                return new PipelineResult { Stream = true, EventStream = stream };
            }
            catch (Exception ex)
            {
                throw GoErrors.Wrap(ex, "failed to stream request: ");
            }
        }

        if (effectiveWantStream)
        {
            using var timeout = NonStreamTimeoutScope.Create(nonStreamTimeout, cancellationToken);
            try
            {
                return new PipelineResult
                {
                    Stream = false,
                    Response = AutoAggregateStream(timeout.Token, executor, httpReq),
                };
            }
            catch (Exception ex)
            {
                if (timeout.TimedOut)
                {
                    throw PipelineErrors.NonStreamResponseTimeout;
                }

                throw GoErrors.Wrap(ex, "failed to auto-aggregate streaming response: ");
            }
        }

        using (var timeout = NonStreamTimeoutScope.Create(nonStreamTimeout, cancellationToken))
        {
            try
            {
                return new PipelineResult
                {
                    Stream = false,
                    Response = NotStream(timeout.Token, executor, httpReq),
                };
            }
            catch (Exception)
            {
                if (timeout.TimedOut)
                {
                    throw PipelineErrors.NonStreamResponseTimeout;
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Port of pipeline.effectiveResponseTimeouts (pipeline.go:497-520): let
    /// the outbound transformer override the attempt's defaults.
    /// </summary>
    internal (TimeSpan StreamFirstEventTimeout, TimeSpan NonStreamTimeout) EffectiveResponseTimeouts()
    {
        var streamFirstEventTimeout = _options.StreamFirstEventTimeout;
        var nonStreamTimeout = _options.NonStreamTimeout;

        if (_outbound is not IResponseTimeoutProvider provider)
        {
            return (streamFirstEventTimeout, nonStreamTimeout);
        }

        var (streamOverride, nonStreamOverride) = provider.ResponseTimeouts();
        if (streamOverride is { } stream)
        {
            streamFirstEventTimeout = stream;
        }

        if (nonStreamOverride is { } nonStream)
        {
            nonStreamTimeout = nonStream;
        }

        return (streamFirstEventTimeout, nonStreamTimeout);
    }

    /// <summary>Port of pipeline.notStream.</summary>
    private HttpResponseData NotStream(CancellationToken cancellationToken, IExecutor executor, HttpRequestData request)
    {
        // TransformResponse has no context parameter in the port; async task
        // transformers read the caller's token off the wire request (C104).
        request.RequestCancellationToken = cancellationToken;

        HttpResponseData httpResp;
        try
        {
            httpResp = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            ApplyRawErrorMiddlewares(ex);

            if (GoErrors.As<HttpClientError>(ex) is { } httpErr)
            {
                throw UpstreamErrors.Wrap(_outbound.TransformError(httpErr))!;
            }

            throw UpstreamErrors.Wrap(GoErrors.Wrap(ex, "failed to do request: "))!;
        }

        try
        {
            httpResp = ApplyRawResponseMiddlewares(httpResp);
        }
        catch (Exception ex)
        {
            ApplyRawErrorMiddlewares(ex);
            throw GoErrors.Wrap(ex, "failed to apply raw response middlewares: ");
        }

        var responseHeaders = ResponseHeaders.MergeForwardResponseHeaders(null, httpResp.Headers);

        LlmResponse llmResp;
        try
        {
            llmResp = _outbound.TransformResponse(httpResp);
        }
        catch (Exception ex)
        {
            ApplyRawErrorMiddlewares(ex);
            throw UpstreamErrors.Wrap(GoErrors.Wrap(ex, "failed to transform response: "))!;
        }

        try
        {
            llmResp = ApplyLlmResponseMiddlewares(llmResp);
        }
        catch (Exception ex)
        {
            ApplyRawErrorMiddlewares(ex);
            throw GoErrors.Wrap(ex, "failed to apply llm response middlewares: ");
        }

        if (_options.EmptyResponseDetection && !ResponseContent.HasResponseContent(llmResp))
        {
            ApplyRawErrorMiddlewares(PipelineErrors.EmptyResponse);
            throw PipelineErrors.EmptyResponse;
        }

        llmResp = WithTransformerMetadata(request.TransformerMetadata, llmResp);

        HttpResponseData finalResp;
        try
        {
            finalResp = _inbound.TransformResponse(llmResp);
        }
        catch (Exception ex)
        {
            ApplyRawErrorMiddlewares(ex);
            throw GoErrors.Wrap(ex, "failed to transform final response: ");
        }

        PreserveCodexTurnStateHeader(finalResp, httpResp);

        try
        {
            finalResp = ApplyInboundRawResponseMiddlewares(finalResp);
        }
        catch (Exception ex)
        {
            ApplyRawErrorMiddlewares(ex);
            throw GoErrors.Wrap(ex, "failed to apply inbound raw response middlewares: ");
        }

        finalResp.Headers = ResponseHeaders.MergeForwardResponseHeaders(finalResp.Headers, responseHeaders)!;

        return finalResp;
    }

    /// <summary>Port of pipeline.autoAggregateStream.</summary>
    private HttpResponseData AutoAggregateStream(
        CancellationToken cancellationToken,
        IExecutor executor,
        HttpRequestData request)
    {
        // The auto-aggregate path never enforces the first-event timeout: the
        // non-stream timeout covers the whole exchange instead.
        using var inboundStream = Stream(cancellationToken, executor, request, TimeSpan.Zero);

        var upstreamHeaders = ResponseHeaders.GetResponseHeaders(inboundStream);

        var chunks = new List<StreamEvent>(8);
        while (inboundStream.Next())
        {
            if (inboundStream.Current is { } ev)
            {
                chunks.Add(ev);
            }
        }

        if (inboundStream.Err is { } err)
        {
            ApplyRawErrorMiddlewares(err);
            throw err;
        }

        if (chunks.Count == 0)
        {
            ApplyRawErrorMiddlewares(PipelineErrors.EmptyStreamChunks);
            throw PipelineErrors.EmptyStreamChunks;
        }

        byte[] body;
        try
        {
            body = _inbound.AggregateStreamChunks(chunks, out _);
        }
        catch (Exception ex)
        {
            ApplyRawErrorMiddlewares(ex);
            throw;
        }

        if (body.Length == 0)
        {
            ApplyRawErrorMiddlewares(PipelineErrors.EmptyAggregatedBody);
            throw PipelineErrors.EmptyAggregatedBody;
        }

        var responseHeaders = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
            ["Cache-Control"] = ["no-cache"],
        };
        responseHeaders = ResponseHeaders.MergeForwardResponseHeaders(responseHeaders, upstreamHeaders)!;

        foreach (var chunk in chunks)
        {
            var value = HttpClientUtils.HeaderGet(chunk.Headers, "X-Codex-Turn-State");
            if (value.Length > 0)
            {
                responseHeaders["X-Codex-Turn-State"] = [value];
                break;
            }
        }

        var resp = new HttpResponseData
        {
            StatusCode = 200,
            Headers = responseHeaders,
            Body = body,
        };

        try
        {
            resp = ApplyInboundRawResponseMiddlewares(resp);
        }
        catch (Exception ex)
        {
            ApplyRawErrorMiddlewares(ex);
            throw GoErrors.Wrap(ex, "failed to apply inbound raw response middlewares: ");
        }

        resp.Headers = ResponseHeaders.MergeForwardResponseHeaders(resp.Headers, responseHeaders)!;

        return resp;
    }

    private static void PreserveCodexTurnStateHeader(HttpResponseData dst, HttpResponseData src)
    {
        var value = HttpClientUtils.HeaderGet(src.Headers, "X-Codex-Turn-State");
        if (value.Length > 0)
        {
            dst.Headers ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
            dst.Headers["X-Codex-Turn-State"] = [value];
        }
    }

    private static bool IsResponseTimeoutError(Exception err)
        => GoErrors.IsAny(err, PipelineErrors.StreamFirstEventTimeout, PipelineErrors.NonStreamResponseTimeout);

    internal static Dictionary<string, JsonElement>? CloneMetadata(Dictionary<string, JsonElement>? source)
        => source is null ? null : new Dictionary<string, JsonElement>(source, StringComparer.Ordinal);

    /// <summary>
    /// Port of mergeTransformerMetadata: fills missing destination keys from the
    /// source. Values are opaque to the pipeline; existing destination values
    /// take precedence. The result is a new map so one attempt's writes never
    /// reach another.
    /// </summary>
    internal static Dictionary<string, JsonElement>? MergeTransformerMetadata(
        Dictionary<string, JsonElement>? source,
        Dictionary<string, JsonElement>? destination)
    {
        if (source is null || source.Count == 0)
        {
            return CloneMetadata(destination);
        }

        var result = new Dictionary<string, JsonElement>(source, StringComparer.Ordinal);
        if (destination is not null)
        {
            foreach (var (key, value) in destination)
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Port of withTransformerMetadata: preserves decoder-owned responses and the
    /// shared [DONE] sentinel, otherwise returns a copy carrying the attempt's
    /// metadata.
    /// </summary>
    internal static LlmResponse WithTransformerMetadata(
        Dictionary<string, JsonElement>? metadata,
        LlmResponse response)
    {
        if (metadata is null || metadata.Count == 0 || ReferenceEquals(response, LlmResponse.DoneResponse))
        {
            return response;
        }

        var result = response.ShallowClone();
        result.TransformerMetadata = MergeTransformerMetadata(metadata, response.TransformerMetadata);
        return result;
    }

    private LlmRequest ApplyBeforeRequestMiddlewares(LlmRequest request)
    {
        var current = request;
        foreach (var middleware in _options.Middlewares)
        {
            current = middleware.TransformInboundLlmRequest(current);
        }

        return current;
    }

    private HttpResponseData ApplyInboundRawResponseMiddlewares(HttpResponseData response)
    {
        var current = response;
        foreach (var middleware in _options.Middlewares)
        {
            current = middleware.TransformInboundRawResponse(current);
        }

        return current;
    }

    private IStream<StreamEvent> ApplyInboundRawStreamMiddlewares(IStream<StreamEvent> stream)
    {
        var current = stream;
        foreach (var middleware in _options.Middlewares)
        {
            current = middleware.TransformInboundRawStream(current);
        }

        return current;
    }

    private HttpRequestData ApplyRawRequestMiddlewares(HttpRequestData request)
    {
        var current = request;
        foreach (var middleware in _options.Middlewares)
        {
            current = middleware.TransformOutboundRawRequest(current);
        }

        return current;
    }

    private HttpResponseData ApplyRawResponseMiddlewares(HttpResponseData response)
    {
        var current = response;
        for (var i = _options.Middlewares.Count - 1; i >= 0; i--)
        {
            current = _options.Middlewares[i].TransformOutboundRawResponse(current);
        }

        return current;
    }

    private IStream<StreamEvent> ApplyRawStreamMiddlewares(IStream<StreamEvent> stream)
    {
        var current = stream;
        for (var i = _options.Middlewares.Count - 1; i >= 0; i--)
        {
            current = _options.Middlewares[i].TransformOutboundRawStream(current);
        }

        return current;
    }

    private void ApplyRawErrorMiddlewares(Exception error)
    {
        for (var i = _options.Middlewares.Count - 1; i >= 0; i--)
        {
            _options.Middlewares[i].OnOutboundRawError(error);
        }
    }

    private LlmResponse ApplyLlmResponseMiddlewares(LlmResponse response)
    {
        var current = response;
        for (var i = _options.Middlewares.Count - 1; i >= 0; i--)
        {
            current = _options.Middlewares[i].TransformOutboundLlmResponse(current);
        }

        return current;
    }

    private IStream<LlmResponse> ApplyLlmStreamMiddlewares(IStream<LlmResponse> stream)
    {
        var current = stream;
        for (var i = _options.Middlewares.Count - 1; i >= 0; i--)
        {
            current = _options.Middlewares[i].TransformOutboundLlmStream(current);
        }

        return current;
    }
}
