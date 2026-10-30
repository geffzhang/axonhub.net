using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.PersistentOutboundTransformer
/// (internal/server/orchestrator/outbound.go:516-880) for the chat slice: it owns
/// the ordered candidate list, points every attempt at the current candidate and
/// implements the pipeline's retry seams.
/// </summary>
/// <remarks>
/// The retry loop itself lives in <see cref="Pipeline"/>, exactly as in the
/// reference: <c>CanRetry</c> decides same-channel retries, <c>HasMoreChannels</c>
/// and <c>NextChannel</c> drive channel switches, and the pipeline enforces the
/// two budgets and the inter-retry delay.
/// </remarks>
public sealed class PersistentOutboundTransformer : IOutboundTransformer, IRetryable, IChannelRetryable
{
    private readonly ChannelCandidate[] _candidates;
    private readonly TimeProvider _timeProvider;
    private readonly IChannelMetricsRecorder _metrics;
    private readonly Func<ChannelCandidate, DateTimeOffset, IOutboundTransformer> _factory;

    private int _candidateIndex;
    private IOutboundTransformer? _current;

    public PersistentOutboundTransformer(
        ChannelCandidate[] candidates,
        TimeProvider timeProvider,
        IChannelMetricsRecorder metrics,
        Func<ChannelCandidate, DateTimeOffset, IOutboundTransformer>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(metrics);

        _candidates = candidates;
        _timeProvider = timeProvider;
        _metrics = metrics;
        _factory = factory ?? ChannelOutboundFactory.Create;
    }

    /// <summary>The channel serving the current attempt, or 0 before the first transform.</summary>
    public long CurrentChannelId { get; private set; }

    /// <summary>
    /// The channel model used for routing and pricing (the reference's
    /// <c>GetCurrentModelID</c>); provider transforms may rewrite the model in
    /// the final HTTP request, so this is not read back from the body.
    /// </summary>
    public string CurrentModel { get; private set; } = string.Empty;

    /// <summary>The wrapped transformer for the current attempt.</summary>
    private IOutboundTransformer Current =>
        _current ?? throw new InvalidOperationException("no candidate selected yet");

    /// <summary>
    /// The provider transformer of the current attempt, for middleware that
    /// aggregates the provider's raw stream (request_execution persistence).
    /// </summary>
    internal IOutboundTransformer CurrentTransformer => Current;

    public string ApiFormat => Current.ApiFormat;

    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("chat completion request is nil");
        }

        if (_candidates.Length == 0)
        {
            throw new InvalidOperationException(
                "no candidates available: candidates should be selected by inbound transformer");
        }

        if (_candidateIndex >= _candidates.Length)
        {
            throw new InvalidOperationException(
                "server internal error, please try again later: all candidates exhausted");
        }

        var candidate = _candidates[_candidateIndex];
        CurrentChannelId = candidate.ChannelId;
        CurrentModel = candidate.ActualModel;
        _current = _factory(candidate, _timeProvider.GetUtcNow());

        // The upstream model is per-candidate: the client's requested model is
        // only used to select candidates.
        request.Model = candidate.ActualModel;

        return _current.TransformRequest(request);
    }

    public LlmResponse TransformResponse(HttpResponseData? response) => Current.TransformResponse(response);

    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => Current.TransformStream(request, stream);

    public ResponseError TransformError(HttpClientError? error) => Current.TransformError(error);

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => Current.AggregateStreamChunks(request, chunks, out meta);

    /// <summary>Port of HasMoreChannels: is there a candidate after the current one?</summary>
    public bool HasMoreChannels() => _candidateIndex + 1 < _candidates.Length;

    /// <summary>Port of NextChannel: advance the candidate and count the retry attempt.</summary>
    public Exception? TryNextChannel(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        _candidateIndex++;
        if (_candidateIndex >= _candidates.Length)
        {
            return new InvalidOperationException("no more candidates available for retry");
        }

        _current = null;
        RecordSelection(_candidates[_candidateIndex]);

        return null;
    }

    /// <summary>Port of CanRetry (outbound.go:766-818).</summary>
    public bool CanRetry(Exception error)
    {
        if (_candidateIndex >= _candidates.Length)
        {
            return false;
        }

        // Empty response detection: retry the same channel so the pipeline can
        // re-execute the request.
        if (GoErrors.IsAny(
            error,
            PipelineErrors.EmptyResponse,
            PipelineErrors.EmptyStreamChunks,
            PipelineErrors.EmptyAggregatedBody))
        {
            return true;
        }

        // 429: the upstream is rate-limiting this channel; a same-channel retry
        // would burn the budget without a chance of success.
        if (ExtractStatusCode(error) == 429)
        {
            return false;
        }

        // The reference also advances to the next model inside the same channel
        // here (candidate.Models); the slice's candidates carry one model each.
        return IsRetryableErrorForChannel(error);
    }

    /// <summary>Port of PrepareForRetry: count the retry attempt on the same channel.</summary>
    public Exception? TryPrepareForRetry(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        RecordSelection(_candidates[_candidateIndex]);
        return null;
    }

    private void RecordSelection(ChannelCandidate candidate)
        => _metrics.RecordSelection(candidate.ChannelId, _timeProvider.GetUtcNow());

    /// <summary>
    /// Port of isRetryableErrorForChannel minus the channel-level
    /// RetryableStatusCodes / RetryableErrorPatterns settings (C016).
    /// </summary>
    private static bool IsRetryableErrorForChannel(Exception error)
    {
        if (UpstreamTransportErrors.IsUpstreamTransportError(error))
        {
            return true;
        }

        var statusCode = ExtractStatusCode(error);

        // httpclient.IsHTTPStatusCodeRetryable: 429 and all 5xx.
        return statusCode == 429 || statusCode is >= 500 and <= 599;
    }

    /// <summary>Port of ExtractStatusCodeFromError.</summary>
    private static int ExtractStatusCode(Exception? error)
    {
        if (error is null)
        {
            return 0;
        }

        if (GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            return httpError.StatusCode;
        }

        return GoErrors.As<ResponseError>(error)?.StatusCode ?? 0;
    }
}
