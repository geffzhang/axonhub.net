using System.Diagnostics.Metrics;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/channel_limiter_metrics_test.go plus
/// the middleware wiring guards (difference ledger C044): the five instruments
/// keep the reference names, units, descriptions and <c>channel_id</c> /
/// <c>channel_name</c> tags on System.Diagnostics.Metrics.
/// </summary>
public sealed class ChannelLimiterMetricsTests
{
    /// <summary>The reference tolerates a nil meter and turns every call into a no-op.</summary>
    [Fact]
    public void NilMeterIsNoop()
    {
        var manager = new ChannelLimiterManager();
        var metrics = new ChannelLimiterMetrics(null, manager);

        metrics.IncQueueFull(1, "x");
        metrics.IncQueueTimeout(1, "x");
        metrics.ObserveQueueWait(1, "x", TimeSpan.Zero);
    }

    [Fact]
    public void GaugeCallbackEmitsLimiterStats()
    {
        using var meter = new Meter("axonhub-test");
        using var capture = new MetricsCapture(meter);
        var manager = new ChannelLimiterManager();
        _ = new ChannelLimiterMetrics(meter, manager);

        var settings = RateLimitTestData.Concurrency(5, queueSize: 3);
        var limiter = manager.GetOrCreate(42, "kimi", settings);
        Assert.NotNull(limiter);
        limiter.Acquire(CancellationToken.None);
        limiter.Acquire(CancellationToken.None);

        capture.RecordObservable();

        var inFlight = Assert.Single(capture.Gauges["axonhub_channel_inflight"]);
        Assert.Equal(2d, inFlight.Value);
        Assert.Equal(42L, (long)inFlight.Tags["channel_id"]!);
        Assert.Equal("kimi", (string)inFlight.Tags["channel_name"]!);

        var waiting = Assert.Single(capture.Gauges["axonhub_channel_queue_waiting"]);
        Assert.Equal(0d, waiting.Value);
        Assert.Equal(42L, (long)waiting.Tags["channel_id"]!);

        limiter.Release();
        limiter.Release();
    }

    [Fact]
    public void CountersAndHistogram()
    {
        using var meter = new Meter("axonhub-test");
        using var capture = new MetricsCapture(meter);
        var metrics = new ChannelLimiterMetrics(meter, manager: null);

        metrics.IncQueueFull(7, "openai");
        metrics.IncQueueFull(7, "openai");
        metrics.IncQueueTimeout(7, "openai");
        metrics.ObserveQueueWait(7, "openai", TimeSpan.Zero);

        var full = Assert.Single(capture.Counters["axonhub_channel_queue_full_total"]);
        Assert.Equal(2d, full.Value);
        Assert.Equal(7L, (long)full.Tags["channel_id"]!);
        Assert.Equal("openai", (string)full.Tags["channel_name"]!);

        var timeout = Assert.Single(capture.Counters["axonhub_channel_queue_timeout_total"]);
        Assert.Equal(1d, timeout.Value);
        Assert.Equal("openai", (string)timeout.Tags["channel_name"]!);

        var wait = Assert.Single(capture.Histograms["axonhub_channel_queue_wait_seconds"]);
        Assert.Equal(0d, wait.Value);
        Assert.Equal(7L, (long)wait.Tags["channel_id"]!);
    }

    /// <summary>
    /// The middleware increments the queue-full counter with the candidate's
    /// channel tags when admission is rejected (connection_tracking.go).
    /// </summary>
    [Fact]
    public async Task QueueFullRejectionIncrementsTheCounter()
    {
        var settings = RateLimitTestData.Concurrency(1, queueSize: 1);
        var manager = new ChannelLimiterManager();
        using var meter = new Meter("axonhub-test");
        using var capture = new MetricsCapture(meter);
        var metrics = new ChannelLimiterMetrics(meter, manager);

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
                // The queued waiter only occupies the FIFO slot.
            }
        });

        try
        {
            await WaitUntilAsync(
                () => limiter.Stats().Waiting == 1,
                "the queued waiter must enqueue before the middleware call");

            var outbound = RateLimitTestData.Outbound(settings, 2, "kimi");
            var middleware = new ChannelLimiterMiddleware(
                outbound,
                manager,
                CancellationToken.None,
                metrics);

            Assert.Throws<ChannelQueueError>(
                () => middleware.TransformOutboundRawRequest(new HttpRequestData()));

            var full = Assert.Single(capture.Counters["axonhub_channel_queue_full_total"]);
            Assert.Equal(1d, full.Value);
            Assert.Equal(2L, (long)full.Tags["channel_id"]!);
            Assert.Equal("kimi", (string)full.Tags["channel_name"]!);
        }
        finally
        {
            cts.Cancel();
            await waiter;
            limiter.Release();
        }
    }

    /// <summary>
    /// A successful acquire observes the elapsed wait with the candidate's
    /// tags; the fake clock pins the measurement to the middleware's own
    /// GetTimestamp/GetElapsedTime pair.
    /// </summary>
    [Fact]
    public void SuccessfulAcquireRecordsTheWait()
    {
        var settings = RateLimitTestData.Concurrency(2);
        var manager = new ChannelLimiterManager();
        using var meter = new Meter("axonhub-test");
        using var capture = new MetricsCapture(meter);
        var metrics = new ChannelLimiterMetrics(meter, manager);
        var clock = new FixedElapsedClock(TimeSpan.FromMilliseconds(7));

        var outbound = RateLimitTestData.Outbound(settings, 3, "x");
        var middleware = new ChannelLimiterMiddleware(
            outbound,
            manager,
            CancellationToken.None,
            metrics,
            clock);

        middleware.TransformOutboundRawRequest(new HttpRequestData());

        var wait = Assert.Single(capture.Histograms["axonhub_channel_queue_wait_seconds"]);
        Assert.Equal(0.007, wait.Value, 9);
        Assert.Equal(3L, (long)wait.Tags["channel_id"]!);
        Assert.Equal("x", (string)wait.Tags["channel_name"]!);

        middleware.TransformOutboundLlmResponse(new LlmResponse());
    }

    /// <summary>Runs <see cref="ChannelLimiter.Acquire"/> on a dedicated thread (difference C042).</summary>
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

    /// <summary>Returns a fixed elapsed time for every acquisition window.</summary>
    private sealed class FixedElapsedClock(TimeSpan elapsed) : TimeProvider
    {
        public override long GetTimestamp() => 0;

        public override TimeSpan GetElapsedTime(long startingTimestamp) => elapsed;
    }

    /// <summary>
    /// Collects every measurement from one <see cref="Meter"/> through a
    /// <see cref="MeterListener"/>, which is the .NET equivalent of the
    /// reference's SDK <c>ManualReader</c>.
    /// </summary>
    private sealed class MetricsCapture : IDisposable
    {
        private readonly MeterListener _listener = new();

        public MetricsCapture(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(OnLong);
            _listener.SetMeasurementEventCallback<double>(OnDouble);
            _listener.Start();
        }

        public Dictionary<string, List<Point>> Gauges { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<Point>> Counters { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<Point>> Histograms { get; } = new(StringComparer.Ordinal);

        public void RecordObservable() => _listener.RecordObservableInstruments();

        public void Dispose() => _listener.Dispose();

        private void OnLong(
            Instrument instrument,
            long value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
            => Record(instrument, value, tags);

        private void OnDouble(
            Instrument instrument,
            double value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
            => Record(instrument, value, tags);

        private void Record(
            Instrument instrument,
            double value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var bucket = instrument switch
            {
                ObservableGauge<long> => Gauges,
                ObservableGauge<double> => Gauges,
                Histogram<double> => Histograms,
                _ => Counters,
            };

            var tagMap = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                tagMap[tag.Key] = tag.Value;
            }

            if (!bucket.TryGetValue(instrument.Name, out var points))
            {
                bucket[instrument.Name] = points = [];
            }

            points.Add(new Point(value, tagMap));
        }
    }

    private sealed record Point(double Value, IReadOnlyDictionary<string, object?> Tags);
}
