using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/rate_limit_tracking_test.go: TPM
/// accounting on responses and stream chunks, and the Retry-After cooldown that
/// only an upstream 429 can set. RPM accounting belongs to the admission
/// middleware, so this middleware never increments the request counter.
/// </summary>
/// <remarks>
/// Two reference shapes have no port analogue and are covered elsewhere or
/// dropped: <c>noopRateLimitTracking</c> (the port always builds the middleware
/// with a tracker; there is no nil-tracker wiring) and the two distinct
/// "no current channel" states (both are an outbound transformer with an empty
/// candidate list).
/// </remarks>
public sealed class RateLimitTrackingTests
{
    [Theory]
    [InlineData(0, 150L, 150L)] // tracks tokens from the response
    [InlineData(1, 0L, 0L)] // handles nil usage
    [InlineData(2, 0L, 0L)] // handles zero tokens
    [InlineData(3, 0L, 0L)] // handles a nil response
    public void TracksTokensFromResponse(int mode, long totalTokens, long expectedTokens)
    {
        var clock = new TestTimeProvider(RateLimitTestData.Start);
        var tracker = new ChannelRequestTracker(clock);
        var outbound = RateLimitTestData.Outbound(settings: null);
        var middleware = new RateLimitTrackingMiddleware(outbound, tracker, clock);

        LlmResponse? response = mode switch
        {
            0 => new LlmResponse { Usage = new Usage { TotalTokens = totalTokens } },
            1 => new LlmResponse { Usage = null },
            2 => new LlmResponse { Usage = new Usage { TotalTokens = 0 } },
            _ => null,
        };

        var result = middleware.TransformOutboundLlmResponse(response!);

        Assert.Same(response, result);
        Assert.Equal(expectedTokens, tracker.GetTokenCount(1));
    }

    [Fact]
    public void TokensAreTrackedPerChannel()
    {
        var clock = new TestTimeProvider(RateLimitTestData.Start);
        var tracker = new ChannelRequestTracker(clock);

        var first = new RateLimitTrackingMiddleware(
            RateLimitTestData.Outbound(settings: null, 1, "channel-1"),
            tracker,
            clock);
        var second = new RateLimitTrackingMiddleware(
            RateLimitTestData.Outbound(settings: null, 2, "channel-2"),
            tracker,
            clock);

        first.TransformOutboundLlmResponse(new LlmResponse { Usage = new Usage { TotalTokens = 100 } });
        second.TransformOutboundLlmResponse(new LlmResponse { Usage = new Usage { TotalTokens = 200 } });

        Assert.Equal(100, tracker.GetTokenCount(1));
        Assert.Equal(200, tracker.GetTokenCount(2));
    }

    [Fact]
    public void StreamChunksWithUsageAddTokens()
    {
        var clock = new TestTimeProvider(RateLimitTestData.Start);
        var tracker = new ChannelRequestTracker(clock);
        var outbound = RateLimitTestData.Outbound(settings: null);
        var middleware = new RateLimitTrackingMiddleware(outbound, tracker, clock);

        var responses = new[]
        {
            new LlmResponse { Id = "1", Usage = null },
            new LlmResponse { Id = "2", Usage = null },
            new LlmResponse { Id = "3", Usage = new Usage { TotalTokens = 250 } },
        };

        var stream = new SliceStream<LlmResponse>(responses);
        using var wrapped = middleware.TransformOutboundLlmStream(stream);

        var tokenCount = 0L;
        while (wrapped.Next())
        {
            var current = wrapped.Current;
            if (current?.Usage is { } usage)
            {
                tokenCount = usage.TotalTokens;
            }
        }

        Assert.Null(wrapped.Err);
        Assert.Equal(250, tokenCount);
        Assert.Equal(250, tracker.GetTokenCount(1));
    }

    [Fact]
    public void RawRequestDoesNotIncrementRequests()
    {
        var clock = new TestTimeProvider(RateLimitTestData.Start);
        var tracker = new ChannelRequestTracker(clock);
        var outbound = RateLimitTestData.Outbound(settings: null);
        var middleware = new RateLimitTrackingMiddleware(outbound, tracker, clock);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            middleware.TransformOutboundRawRequest(new HttpRequestData());
        }

        Assert.Equal(0, tracker.GetRequestCount(1));
    }

    [Fact]
    public void CombinedFlowTracksTokensOnly()
    {
        var clock = new TestTimeProvider(RateLimitTestData.Start);
        var tracker = new ChannelRequestTracker(clock);
        var outbound = RateLimitTestData.Outbound(settings: null);
        var middleware = new RateLimitTrackingMiddleware(outbound, tracker, clock);

        // RPM admission is handled before this middleware; tracking only records
        // TPM and upstream cooldown signals.
        middleware.TransformOutboundRawRequest(new HttpRequestData());
        middleware.TransformOutboundRawRequest(new HttpRequestData());

        middleware.TransformOutboundLlmResponse(new LlmResponse { Usage = new Usage { TotalTokens = 100 } });

        middleware.TransformOutboundRawRequest(new HttpRequestData());

        middleware.TransformOutboundLlmResponse(new LlmResponse { Usage = new Usage { TotalTokens = 50 } });

        Assert.Equal(0, tracker.GetRequestCount(1));
        Assert.Equal(150, tracker.GetTokenCount(1));
    }

    [Fact]
    public void Upstream429WithRetryAfterSetsCooldown()
    {
        var clock = new TestTimeProvider(RateLimitTestData.Start);
        var tracker = new ChannelRequestTracker(clock);
        var outbound = RateLimitTestData.Outbound(settings: null);
        var middleware = new RateLimitTrackingMiddleware(outbound, tracker, clock);

        middleware.OnOutboundRawError(RateLimitTestData.HttpError(429, "30"));

        Assert.True(tracker.IsCoolingDown(1));
        Assert.True(tracker.TryGetCooldownUntil(1, out var until));
        Assert.Equal(RateLimitTestData.Start.AddSeconds(30), until);
    }

    [Fact]
    public void QueueErrorIsIgnored()
    {
        var clock = new TestTimeProvider(RateLimitTestData.Start);
        var tracker = new ChannelRequestTracker(clock);
        var outbound = RateLimitTestData.Outbound(settings: null, 7, "kimi");
        var middleware = new RateLimitTrackingMiddleware(outbound, tracker, clock);

        var queueError = ChannelQueueError.From(
            7,
            "kimi",
            new ChannelQueueException(ChannelQueueReason.QueueFull));
        Assert.NotNull(queueError);

        middleware.OnOutboundRawError(queueError);

        Assert.False(
            tracker.IsCoolingDown(7),
            "local queue rejection must not trigger upstream-style cooldown");
    }

    [Fact]
    public void Upstream429WithoutRetryAfterIsIgnored()
    {
        var clock = new TestTimeProvider(RateLimitTestData.Start);
        var tracker = new ChannelRequestTracker(clock);
        var outbound = RateLimitTestData.Outbound(settings: null);
        var middleware = new RateLimitTrackingMiddleware(outbound, tracker, clock);

        middleware.OnOutboundRawError(RateLimitTestData.HttpError(429, retryAfter: null));

        Assert.False(tracker.IsCoolingDown(1));
    }

    [Fact]
    public void Non429WithRetryAfterIsIgnored()
    {
        var clock = new TestTimeProvider(RateLimitTestData.Start);
        var tracker = new ChannelRequestTracker(clock);
        var outbound = RateLimitTestData.Outbound(settings: null);
        var middleware = new RateLimitTrackingMiddleware(outbound, tracker, clock);

        middleware.OnOutboundRawError(RateLimitTestData.HttpError(500, "30"));

        Assert.False(tracker.IsCoolingDown(1));
    }

    [Fact]
    public void RawErrorWithoutCandidateDoesNotThrow()
    {
        var clock = new TestTimeProvider(RateLimitTestData.Start);
        var tracker = new ChannelRequestTracker(clock);
        var middleware = new RateLimitTrackingMiddleware(
            RateLimitTestData.EmptyOutbound(),
            tracker,
            clock);

        middleware.OnOutboundRawError(RateLimitTestData.HttpError(429, "30"));

        Assert.False(tracker.IsCoolingDown(1));
    }
}
