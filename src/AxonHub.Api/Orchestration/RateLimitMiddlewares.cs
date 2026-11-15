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
    private readonly ChannelLimiterMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IChannelConcurrencyAdmission? _admission;
    private LimiterSlot? _current;

    public ChannelLimiterMiddleware(
        PersistentOutboundTransformer outbound,
        ChannelLimiterManager manager,
        CancellationToken cancellationToken,
        ChannelLimiterMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IChannelConcurrencyAdmission? admission = null)
        : base("channel-limiter")
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(manager);

        _outbound = outbound;
        _manager = manager;
        _cancellationToken = cancellationToken;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _admission = admission;
    }

    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidate = _outbound.CurrentCandidate;
        if (candidate is null)
        {
            return request;
        }

        var limiter = _admission is null ? _manager.GetOrCreate(
            candidate.ChannelId,
            candidate.ChannelName,
            candidate.RateLimitSettings) : null;
        if (_admission is null && limiter is null)
        {
            return request;
        }

        // The reference starts the wait clock at Acquire entry and observes it
        // only on success (connection_tracking.go: withChannelLimiter).
        var acquireStart = _timeProvider.GetTimestamp();

        try
        {
            if (_admission is not null)
            {
                var permit = _admission.Acquire(
                    candidate.ChannelId, candidate.RateLimitSettings, _cancellationToken);
                _current = permit is null ? null : new LimiterSlot(permit.Dispose, retainOnDisposeFailure: true);
            }
            else
            {
                limiter!.Acquire(_cancellationToken);
                _current = new LimiterSlot(limiter.Release);
            }
        }
        catch (ChannelQueueException ex)
        {
            switch (ex.Reason)
            {
                case ChannelQueueReason.QueueFull:
                    _metrics?.IncQueueFull(candidate.ChannelId, candidate.ChannelName);
                    break;
                case ChannelQueueReason.QueueTimeout:
                    _metrics?.IncQueueTimeout(candidate.ChannelId, candidate.ChannelName);
                    break;
            }

            throw ChannelQueueError.From(candidate.ChannelId, candidate.ChannelName, ex)!;
        }

        _metrics?.ObserveQueueWait(
            candidate.ChannelId,
            candidate.ChannelName,
            _timeProvider.GetElapsedTime(acquireStart));

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
    private sealed class LimiterSlot(Action release, bool retainOnDisposeFailure = false)
    {
        private int _released;

        public bool RetainOnDisposeFailure => retainOnDisposeFailure;

        public void Retain() => Interlocked.Exchange(ref _released, 1);

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                release();
            }
        }
    }

    /// <summary>
    /// Routes stream disposal to the slot release, after the upstream stream is
    /// torn down (the reference's <c>defer s.release(); return s.Stream.Close()</c>).
    /// </summary>
    private sealed class ChannelLimiterStream(IStream<LlmResponse> inner, LimiterSlot slot) : IStream<LlmResponse>
    {
        private int _disposed;

        public LlmResponse? Current => inner.Current;

        public Exception? Err => inner.Err;

        /// <summary>
        /// Port of <c>channelLimiterStream.Next</c> (connection_tracking.go:182-191):
        /// releases upstream admission when iteration ends, before downstream
        /// delivery finishes, so a follow-up reviewer can use the same channel
        /// slot. This is what lets the terminal-tail drain hand the slot to the
        /// classifier call (C243).
        /// </summary>
        public bool Next()
        {
            if (inner.Next())
            {
                return true;
            }

            Dispose();
            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                inner.Dispose();
            }
            catch
            {
                if (slot.RetainOnDisposeFailure)
                {
                    slot.Retain();
                }

                throw;
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
internal sealed class RateLimitAdmissionMiddleware : DummyMiddleware
{
    private readonly PersistentOutboundTransformer _outbound;
    private readonly IChannelRateLimitRecorder _recorder;
    private readonly TimeProvider _timeProvider;

    /// <summary>Direct-construction overload: the tracker's own clock governs.</summary>
    public RateLimitAdmissionMiddleware(PersistentOutboundTransformer outbound, ChannelRequestTracker tracker)
        : this(outbound, new InMemoryChannelRateLimitRecorder(tracker), TimeProvider.System)
    {
    }

    public RateLimitAdmissionMiddleware(
        PersistentOutboundTransformer outbound,
        IChannelRateLimitRecorder recorder,
        TimeProvider? timeProvider = null)
        : base("rate-limit-admission")
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(recorder);

        _outbound = outbound;
        _recorder = recorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidate = _outbound.CurrentCandidate;
        if (candidate?.RateLimitSettings?.Rpm is not { } rpm || rpm <= 0)
        {
            return request;
        }

        if (!_recorder.TryAcquireRequest(candidate.ChannelId, rpm, _timeProvider.GetUtcNow()))
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
internal sealed class RateLimitTrackingMiddleware : DummyMiddleware
{
    private readonly PersistentOutboundTransformer _outbound;
    private readonly IChannelRateLimitRecorder _recorder;
    private readonly TimeProvider _timeProvider;

    /// <summary>Direct-construction overload: the tracker's own clock governs.</summary>
    public RateLimitTrackingMiddleware(
        PersistentOutboundTransformer outbound,
        ChannelRequestTracker tracker,
        TimeProvider timeProvider)
        : this(outbound, new InMemoryChannelRateLimitRecorder(tracker), timeProvider)
    {
    }

    public RateLimitTrackingMiddleware(
        PersistentOutboundTransformer outbound,
        IChannelRateLimitRecorder recorder,
        TimeProvider timeProvider)
        : base("track-rate-limit")
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _outbound = outbound;
        _recorder = recorder;
        _timeProvider = timeProvider;
    }

    public override LlmResponse TransformOutboundLlmResponse(LlmResponse response)
    {
        // The reference tolerates a nil response (it returns it unchanged); the
        // port keeps the same guard instead of throwing on a nil the typed
        // pipeline would never produce.
        var candidate = _outbound.CurrentCandidate;
        if (response is null || candidate is null || response.Usage is not { } usage || usage.TotalTokens <= 0)
        {
            // Null in, null out: the annotation is non-nullable because the
            // pipeline never produces one, but the reference test does.
            return response!;
        }

        _recorder.AddTokens(candidate.ChannelId, usage.TotalTokens, _timeProvider.GetUtcNow());

        return response;
    }

    public override IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return new RateLimitTrackingStream(
            stream,
            _recorder,
            _timeProvider,
            () => _outbound.CurrentCandidate);
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

        var candidate = _outbound.CurrentCandidate;
        if (candidate is null || !HttpClientErrors.HasRetryAfterHeader(error))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (!HttpClientErrors.TryParseRetryAfter(error, now, out var cooldown))
        {
            return;
        }

        _recorder.SetCooldown(candidate.ChannelId, now + cooldown);
    }

    /// <summary>Counts tokens carried by streamed chunks, typically the final chunk.</summary>
    private sealed class RateLimitTrackingStream(
        IStream<LlmResponse> inner,
        IChannelRateLimitRecorder recorder,
        TimeProvider timeProvider,
        Func<ChannelCandidate?> currentCandidate) : IStream<LlmResponse>
    {
        public LlmResponse? Current
        {
            get
            {
                var current = inner.Current;
                if (current?.Usage is { TotalTokens: > 0 } usage && currentCandidate() is { } candidate)
                {
                    recorder.AddTokens(candidate.ChannelId, usage.TotalTokens, timeProvider.GetUtcNow());
                }

                return current;
            }
        }

        public Exception? Err => inner.Err;

        public bool Next() => inner.Next();

        public void Dispose() => inner.Dispose();
    }
}
