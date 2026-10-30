using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/connection_tracking_test.go: per-channel
/// concurrency admission through the middleware, release on every attempt exit
/// path (response, raw error, stream dispose) exactly once, and the ordering
/// guarantee that a queued waiter is only admitted after the previous attempt's
/// upstream stream has been torn down.
/// </summary>
public sealed class ChannelLimiterMiddlewareTests
{
    [Fact]
    public void AcquireAndReleaseOnResponse()
    {
        var settings = RateLimitTestData.Concurrency(2);
        var manager = new ChannelLimiterManager();
        var outbound = RateLimitTestData.Outbound(settings, 1, "k");
        var middleware = new ChannelLimiterMiddleware(outbound, manager, CancellationToken.None);
        var limiter = manager.GetOrCreate(1, "k", settings);
        Assert.NotNull(limiter);

        middleware.TransformOutboundRawRequest(new HttpRequestData());

        Assert.Equal(1, limiter.Stats().InFlight);

        middleware.TransformOutboundLlmResponse(new LlmResponse());

        Assert.Equal(0, limiter.Stats().InFlight);
    }

    [Fact]
    public void NoLimitChannelBypasses()
    {
        var manager = new ChannelLimiterManager();
        var outbound = RateLimitTestData.Outbound(settings: null, 9, "open");
        var middleware = new ChannelLimiterMiddleware(outbound, manager, CancellationToken.None);

        middleware.TransformOutboundRawRequest(new HttpRequestData());

        // Channels without a concurrency limit never engage the limiter: no
        // entry is installed, so the load balancer reads "unlimited".
        Assert.False(manager.TryGetStats(9, out _, out _));

        // A following response hook is a harmless no-op.
        middleware.TransformOutboundLlmResponse(new LlmResponse());
    }

    [Fact]
    public async Task QueueFullReturnsTypedError()
    {
        // Capacity 1 + bounded queue 1: once both are taken, the next acquire
        // must fail immediately with the typed queue-full sentinel.
        var settings = RateLimitTestData.Concurrency(1, queueSize: 1);
        var manager = new ChannelLimiterManager();
        var limiter = manager.GetOrCreate(2, "kimi", settings);
        Assert.NotNull(limiter);
        limiter.Acquire(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waiter = RunBlocking(() =>
        {
            try
            {
                limiter.Acquire(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // The queued waiter is only here to occupy the queue.
            }
        });

        try
        {
            await WaitUntilAsync(
                () => limiter.Stats().Waiting == 1,
                "the queued waiter must enqueue before the middleware call");

            var outbound = RateLimitTestData.Outbound(settings, 2, "kimi");
            var middleware = new ChannelLimiterMiddleware(outbound, manager, CancellationToken.None);

            var error = Assert.Throws<ChannelQueueError>(
                () => middleware.TransformOutboundRawRequest(new HttpRequestData()));

            Assert.Equal(ChannelQueueReason.QueueFull, error.Cause.Reason);
            Assert.Equal(2L, error.ChannelId);

            // No slot may be retained after a failed acquire.
            var (inFlight, waiting) = limiter.Stats();
            Assert.Equal(1, inFlight);
            Assert.Equal(1, waiting);
        }
        finally
        {
            cts.Cancel();
            await waiter;
            limiter.Release();
        }
    }

    [Fact]
    public void ReleaseRunsExactlyOnceAcrossConcurrentPaths()
    {
        var settings = RateLimitTestData.Concurrency(5);
        var manager = new ChannelLimiterManager();
        var outbound = RateLimitTestData.Outbound(settings, 3, "x");
        var middleware = new ChannelLimiterMiddleware(outbound, manager, CancellationToken.None);
        var limiter = manager.GetOrCreate(3, "x", settings);
        Assert.NotNull(limiter);

        middleware.TransformOutboundRawRequest(new HttpRequestData());
        Assert.Equal(1, limiter.Stats().InFlight);

        Parallel.For(0, 8, _ =>
        {
            middleware.TransformOutboundLlmResponse(new LlmResponse());
            middleware.OnOutboundRawError(new InvalidOperationException("boom"));

            using var stream = middleware.TransformOutboundLlmStream(new EmptyLlmStream());
        });

        Assert.Equal(0, limiter.Stats().InFlight);
    }

    [Fact]
    public void StreamDisposeReleases()
    {
        var settings = RateLimitTestData.Concurrency(3);
        var manager = new ChannelLimiterManager();
        var outbound = RateLimitTestData.Outbound(settings, 4, "y");
        var middleware = new ChannelLimiterMiddleware(outbound, manager, CancellationToken.None);
        var limiter = manager.GetOrCreate(4, "y", settings);
        Assert.NotNull(limiter);

        middleware.TransformOutboundRawRequest(new HttpRequestData());

        var wrapped = middleware.TransformOutboundLlmStream(new EmptyLlmStream());
        wrapped.Dispose();

        Assert.Equal(0, limiter.Stats().InFlight);
    }

    /// <summary>
    /// The pipeline re-enters the raw-request hook on same-channel retries and
    /// channel switches. A once-per-middleware guard would short-circuit every
    /// release after the first and leak the slot; per-attempt slots must not.
    /// </summary>
    [Fact]
    public void RetryReacquireDoesNotLeak()
    {
        var settings = RateLimitTestData.Concurrency(2);
        var manager = new ChannelLimiterManager();
        var outbound = RateLimitTestData.Outbound(settings, 7, "retry-ch");
        var middleware = new ChannelLimiterMiddleware(outbound, manager, CancellationToken.None);
        var limiter = manager.GetOrCreate(7, "retry-ch", settings);
        Assert.NotNull(limiter);

        middleware.TransformOutboundRawRequest(new HttpRequestData());
        middleware.OnOutboundRawError(new InvalidOperationException("upstream 429"));
        Assert.Equal(0, limiter.Stats().InFlight);

        middleware.TransformOutboundRawRequest(new HttpRequestData());
        Assert.Equal(1, limiter.Stats().InFlight);

        middleware.TransformOutboundLlmResponse(new LlmResponse());
        Assert.Equal(0, limiter.Stats().InFlight);

        middleware.TransformOutboundRawRequest(new HttpRequestData());
        middleware.TransformOutboundLlmStream(new EmptyLlmStream()).Dispose();
        Assert.Equal(0, limiter.Stats().InFlight);
    }

    /// <summary>
    /// Guards the concurrency cap at the physical upstream connection when a
    /// bounded queue is configured: closing A's wrapped stream must tear down
    /// A's upstream stream before handing A's slot to B. A release-before-close
    /// ordering would let B dial a new upstream connection while A's is still
    /// open, momentarily exceeding the cap. The recorder captures the limiter
    /// state at the instant the upstream stream is disposed, so waiting == 1 is
    /// the pass condition and waiting == 0 is the bug.
    /// </summary>
    [Fact]
    public async Task StreamDisposeDoesNotAdmitWaiterBeforeUpstreamClosed()
    {
        var settings = RateLimitTestData.Concurrency(1, queueSize: 1);
        var manager = new ChannelLimiterManager();
        var outbound = RateLimitTestData.Outbound(settings, 11, "stream-handoff");
        var middleware = new ChannelLimiterMiddleware(outbound, manager, CancellationToken.None);
        var limiter = manager.GetOrCreate(11, "stream-handoff", settings);
        Assert.NotNull(limiter);

        // Request A takes the only slot and wraps its stream through the real
        // middleware, so the stream under test is the production one.
        middleware.TransformOutboundRawRequest(new HttpRequestData());

        var recorder = new RecordingLlmStream(limiter);
        var wrapped = middleware.TransformOutboundLlmStream(recorder);

        Assert.Equal(1, limiter.Stats().InFlight);

        using var cts = new CancellationTokenSource();
        var acquired = false;
        var waiter = RunBlocking(() =>
        {
            try
            {
                limiter.Acquire(cts.Token);
                acquired = true;
            }
            catch (OperationCanceledException)
            {
                // Cleanup path when the assertion below fails.
            }
        });

        try
        {
            await WaitUntilAsync(
                () => limiter.Stats().Waiting == 1,
                "waiter B must enqueue before A's stream is closed");

            wrapped.Dispose();

            Assert.True(recorder.DisposeCalled, "the upstream stream must be disposed");
            Assert.Equal(
                1,
                recorder.WaitingAtDispose);

            // B is admitted only after A's slot is released.
            await waiter.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(acquired, "waiter B must be admitted after A's slot is released");
        }
        finally
        {
            if (acquired)
            {
                limiter.Release();
            }
            else
            {
                cts.Cancel();
                await waiter;
            }
        }

        var (inFlight, waiting) = limiter.Stats();
        Assert.Equal(0, inFlight);
        Assert.Equal(0, waiting);
    }

    /// <summary>
    /// Runs a blocking <see cref="ChannelLimiter.Acquire"/> on a dedicated
    /// thread: the port blocks the calling thread (difference C042), and a
    /// pool thread would starve the test's own continuations.
    /// </summary>
    private static Task RunBlocking(Action action)
        => Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(message);
            }

            await Task.Delay(5);
        }
    }

    /// <summary>A minimal empty <see cref="IStream{T}"/> (the reference's <c>emptyResponseStream</c>).</summary>
    private sealed class EmptyLlmStream : IStream<LlmResponse>
    {
        public LlmResponse? Current => null;

        public Exception? Err => null;

        public bool Next() => false;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Captures the limiter's live state at the exact moment this request's
    /// upstream stream is torn down (the reference's <c>hardModeStreamRecorder</c>).
    /// </summary>
    private sealed class RecordingLlmStream(ChannelLimiter limiter) : IStream<LlmResponse>
    {
        public LlmResponse? Current => null;

        public Exception? Err => null;

        public bool DisposeCalled { get; private set; }

        public int InFlightAtDispose { get; private set; }

        public int WaitingAtDispose { get; private set; }

        public bool Next() => false;

        public void Dispose()
        {
            DisposeCalled = true;
            (InFlightAtDispose, WaitingAtDispose) = limiter.Stats();
        }
    }
}
