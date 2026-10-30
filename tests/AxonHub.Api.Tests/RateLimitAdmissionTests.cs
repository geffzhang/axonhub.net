using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/rate_limit_admission_test.go: strict
/// per-instance RPM admission, the no-limit bypass, and the rule that local
/// admission failures neither retry the same channel nor cool it down.
/// </summary>
public sealed class RateLimitAdmissionTests
{
    [Fact]
    public void AllowsOnlyConfiguredRpm()
    {
        var tracker = new ChannelRequestTracker(new TestTimeProvider(RateLimitTestData.Start));
        var outbound = RateLimitTestData.Outbound(RateLimitTestData.Rpm(2), 1, "strict-rpm");
        var middleware = new RateLimitAdmissionMiddleware(outbound, tracker);
        var request = new HttpRequestData();

        middleware.TransformOutboundRawRequest(request);
        middleware.TransformOutboundRawRequest(request);

        var error = Assert.Throws<LocalRpmExhaustedError>(
            () => middleware.TransformOutboundRawRequest(request));

        Assert.Equal(1, error.ChannelId);
        Assert.Equal(2, error.Limit);
        Assert.Equal(2, tracker.GetRequestCount(1));
    }

    [Fact]
    public void NoRpmBypasses()
    {
        var tracker = new ChannelRequestTracker(new TestTimeProvider(RateLimitTestData.Start));
        var outbound = RateLimitTestData.Outbound(RateLimitTestData.Rpm(0), 2, "no-rpm");
        var middleware = new RateLimitAdmissionMiddleware(outbound, tracker);
        var request = new HttpRequestData();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            middleware.TransformOutboundRawRequest(request);
        }

        Assert.Equal(0, tracker.GetRequestCount(2));
    }

    [Fact]
    public void SameChannelRetryCannotBypassRpm()
    {
        var tracker = new ChannelRequestTracker(new TestTimeProvider(RateLimitTestData.Start));
        var outbound = RateLimitTestData.Outbound(RateLimitTestData.Rpm(1), 5, "retry-rpm");
        var middleware = new RateLimitAdmissionMiddleware(outbound, tracker);
        var request = new HttpRequestData();

        middleware.TransformOutboundRawRequest(request);

        var error = Assert.Throws<LocalRpmExhaustedError>(
            () => middleware.TransformOutboundRawRequest(request));

        Assert.Equal(5, error.ChannelId);
        Assert.Equal(1, tracker.GetRequestCount(5));
    }

    [Fact]
    public void TrackingIgnoresLocalRpmExhausted()
    {
        var clock = new TestTimeProvider(RateLimitTestData.Start);
        var tracker = new ChannelRequestTracker(clock);
        var outbound = RateLimitTestData.Outbound(RateLimitTestData.Rpm(1), 3, "strict-rpm");
        var middleware = new RateLimitTrackingMiddleware(outbound, tracker, clock);

        middleware.OnOutboundRawError(LocalRpmExhaustedError.Create(3, "strict-rpm", 1));

        Assert.False(
            tracker.IsCoolingDown(3),
            "local RPM rejection must not trigger upstream-style cooldown");
    }

    [Fact]
    public void CanRetryIsFalseForLocalRpmExhausted()
    {
        var outbound = RateLimitTestData.Outbound(RateLimitTestData.Rpm(1), 4, "strict-rpm");

        Assert.False(outbound.CanRetry(LocalRpmExhaustedError.Create(4, "strict-rpm", 1)));
    }
}
