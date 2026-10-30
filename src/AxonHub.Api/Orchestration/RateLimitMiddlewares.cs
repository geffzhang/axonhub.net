using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of <c>withChannelLimiter</c> (internal/server/orchestrator/connection_tracking.go):
/// acquires a per-channel concurrency slot before an attempt reaches the
/// provider and releases it once the attempt's response is fully drained — on
/// the LLM response, on a raw error, or when the returned stream is disposed.
/// </summary>
/// <remarks>
/// One middleware instance per client request, but the raw-request hook runs
/// once per attempt (same-channel retries and channel switches). Each acquire
/// mints its own slot so the release-once guard cannot bleed across attempts.
/// Dispose order matters: the wrapped stream tears down the upstream stream
/// before releasing the slot, so a queued waiter cannot be admitted (and dial a
/// new connection) while this attempt's connection is still open.
/// </remarks>
internal sealed class ChannelLimiterMiddleware : DummyMiddleware
{
    private readonly PersistentOutboundTransformer _outbound;
    private readonly ChannelLimiterManager _manager;
    private readonly CancellationToken _cancellationToken;
    private LimiterSlot? _current;

    public ChannelLimiterMiddleware(
        PersistentOutboundTransformer outbound,
        ChannelLimiterManager manager,
        CancellationToken cancellationToken)
        : base("channel-limiter")
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(manager);

        _outbound = outbound;
        _manager = manager;
        _cancellationToken = cancellationToken;
    }

    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidate = _outbound.CurrentCandidate;
        if (candidate is null)
        {
            return request;
        }

        var limiter = _manager.GetOrCreate(
            candidate.ChannelId,
            candidate.ChannelName,
            candidate.RateLimitSettings);
        if (limiter is null)
        {
            return request;
        }

        try
        {
            limiter.Acquire(_cancellationToken);
        }
        catch (ChannelQueueException ex)
        {
            throw ChannelQueueError.From(candidate.ChannelId, candidate.ChannelName, ex)!;
        }

        _current = new LimiterSlot(limiter);

        return request;
    }

    public override LlmResponse TransformOutboundLlmResponse(LlmResponse response)
    {
        // Release regardless of the response value: the reference releases on
        // every successful attempt, nil response included.
        ReleaseCurrent();

        return response;
    }

    public override IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var slot = _current;

        return slot is null ? stream : new ChannelLimiterStream(stream, slot);
    }

    public override void OnOutboundRawError(Exception error) => ReleaseCurrent();

    private void ReleaseCurrent() => _current?.Release();

    /// <summary>One acquire/release pair, guarded so every release path is idempotent.</summary>
    private sealed class LimiterSlot(ChannelLimiter limiter)
    {
        private int _released;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                limiter.Release();
            }
        }
    }

    /// <summary>
    /// Routes stream disposal to the slot release, after the upstream stream is
    /// torn down (the reference's <c>defer s.release(); return s.Stream.Close()</c>).
    /// </summary>
    private sealed class ChannelLimiterStream(IStream<LlmResponse> inner, LimiterSlot slot) : IStream<LlmResponse>
    {
        public LlmResponse? Current => inner.Current;

        public Exception? Err => inner.Err;

        public bool Next() => inner.Next();

        public void Dispose()
        {
            try
            {
                inner.Dispose();
            }
            finally
            {
                slot.Release();
            }
        }
    }
}

/// <summary>
/// Port of <c>withRateLimitAdmission</c> (internal/server/orchestrator/rate_limit_admission.go):
/// strict per-instance RPM admission before an attempt is sent upstream. It runs
/// after channel concurrency admission so queue rejections never consume RPM.
/// </summary>
internal sealed class RateLimitAdmissionMiddleware(PersistentOutboundTransformer outbound, ChannelRequestTracker tracker)
    : DummyMiddleware("rate-limit-admission")
{
    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidate = outbound.CurrentCandidate;
        if (candidate?.RateLimitSettings?.Rpm is not { } rpm || rpm <= 0)
        {
            return request;
        }

        if (!tracker.TryAcquireRequest(candidate.ChannelId, rpm))
        {
            throw LocalRpmExhaustedError.Create(candidate.ChannelId, candidate.ChannelName, rpm);
        }

        return request;
    }
}

/// <summary>
/// Port of <c>withRateLimitTracking</c> (internal/server/orchestrator/rate_limit_tracking.go):
/// records TPM usage and provider Retry-After cooldowns. RPM accounting belongs
/// to <see cref="RateLimitAdmissionMiddleware"/>, so request slots are consumed
/// atomically before an attempt reaches upstream.
/// </summary>
internal sealed class RateLimitTrackingMiddleware(
    PersistentOutboundTransformer outbound,
    ChannelRequestTracker tracker,
    TimeProvider timeProvider) : DummyMiddleware("track-rate-limit")
{
    public override LlmResponse TransformOutboundLlmResponse(LlmResponse response)
    {
        // The reference tolerates a nil response (it returns it unchanged); the
        // port keeps the same guard instead of throwing on a nil the typed
        // pipeline would never produce.
        var candidate = outbound.CurrentCandidate;
        if (response is null || candidate is null || response.Usage is not { } usage || usage.TotalTokens <= 0)
        {
            // Null in, null out: the annotation is non-nullable because the
            // pipeline never produces one, but the reference test does.
            return response!;
        }

        tracker.AddTokens(candidate.ChannelId, usage.TotalTokens);

        return response;
    }

    public override IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return new RateLimitTrackingStream(stream, tracker, () => outbound.CurrentCandidate);
    }

    /// <summary>
    /// A 429 with Retry-After cools the channel down. Local admission
    /// rejections never reached upstream and must not trigger a cooldown.
    /// </summary>
    public override void OnOutboundRawError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (GoErrors.As<ChannelQueueError>(error) is not null
            || GoErrors.As<LocalRpmExhaustedError>(error) is not null)
        {
            return;
        }

        var candidate = outbound.CurrentCandidate;
        if (candidate is null || !HttpClientErrors.HasRetryAfterHeader(error))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (!HttpClientErrors.TryParseRetryAfter(error, now, out var cooldown))
        {
            return;
        }

        tracker.SetCooldown(candidate.ChannelId, now + cooldown);
    }

    /// <summary>Counts tokens carried by streamed chunks, typically the final chunk.</summary>
    private sealed class RateLimitTrackingStream(
        IStream<LlmResponse> inner,
        ChannelRequestTracker tracker,
        Func<ChannelCandidate?> currentCandidate) : IStream<LlmResponse>
    {
        public LlmResponse? Current
        {
            get
            {
                var current = inner.Current;
                if (current?.Usage is { TotalTokens: > 0 } usage && currentCandidate() is { } candidate)
                {
                    tracker.AddTokens(candidate.ChannelId, usage.TotalTokens);
                }

                return current;
            }
        }

        public Exception? Err => inner.Err;

        public bool Next() => inner.Next();

        public void Dispose() => inner.Dispose();
    }
}
