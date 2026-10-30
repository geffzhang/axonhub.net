using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;

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
    private bool _originalModelCaptured;

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

    /// <summary>
    /// The model the client originally requested, captured once before the
    /// first attempt rewrites it (the reference's <c>state.OriginalModel</c>).
    /// Channel override templates expose it as <c>.RequestModel</c>.
    /// </summary>
    public string OriginalModel { get; private set; } = string.Empty;

    /// <summary>
    /// The unified request the current attempt is transforming (the reference's
    /// <c>state.LlmRequest</c>). Raw-request middlewares render override
    /// templates against it.
    /// </summary>
    public LlmRequest? CurrentLlmRequest { get; private set; }

    /// <summary>
    /// The candidate serving the current attempt, or <see langword="null"/> before
    /// the first transform. Raw-request middlewares read the channel-level
    /// override settings from here.
    /// </summary>
    public ChannelCandidate? CurrentCandidate =>
        _candidateIndex < _candidates.Length ? _candidates[_candidateIndex] : null;

    /// <summary>
    /// The channel key the current attempt sent upstream, when the provider
    /// exposes it (request_executions.channel_api_key_suffix).
    /// </summary>
    public string? CurrentApiKey => (_current as IApiKeyProvenance)?.SelectedApiKey;

    /// <summary>
    /// The 1-based position of <see cref="CurrentApiKey"/> over the channel's
    /// full key list, or null for single-key channels and before the first
    /// transform (request_executions.channel_api_key_index).
    /// </summary>
    public int? CurrentApiKeyIndex => (_current as IApiKeyProvenance)?.SelectedApiKeyIndex;

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

        // Reference model_mapper captures the client's model once per request,
        // before the outbound transformer rewrites it per candidate.
        if (!_originalModelCaptured)
        {
            OriginalModel = request.Model;
            _originalModelCaptured = true;
        }

        CurrentLlmRequest = request;

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

        // Local admission rejection: the same channel cannot make progress until
        // its local queue/RPM state changes, so bounce immediately to the next
        // candidate instead of burning a same-channel retry
        // (outbound.go: isChannelQueueError / isLocalRPMExhaustedError).
        if (GoErrors.As<ChannelQueueError>(error) is not null
            || GoErrors.As<LocalRpmExhaustedError>(error) is not null)
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
        if (ChannelRetryPredicate.ExtractStatusCode(error) == 429)
        {
            return false;
        }

        // The reference also advances to the next model inside the same channel
        // here (candidate.Models); the slice's candidates carry one model each.
        return ChannelRetryPredicate.IsRetryable(error, _candidates[_candidateIndex].RetrySettings);
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
}
