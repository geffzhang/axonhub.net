using System.Diagnostics.Metrics;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Port of <c>orchestrator.ChannelLimiterMetrics</c>
/// (internal/server/orchestrator/channel_limiter_metrics.go): the per-channel
/// observability surface for the admission-control system.
/// </summary>
/// <remarks>
/// <para>Five instruments, same names / units / descriptions / tags as the
/// reference:</para>
/// <list type="bullet">
/// <item><c>axonhub_channel_inflight</c> — observable gauge, current in-flight count</item>
/// <item><c>axonhub_channel_queue_waiting</c> — observable gauge, current queue depth</item>
/// <item><c>axonhub_channel_queue_full_total</c> — counter, cumulative queue-full rejections</item>
/// <item><c>axonhub_channel_queue_timeout_total</c> — counter, cumulative wait-timeout exits</item>
/// <item><c>axonhub_channel_queue_wait_seconds</c> — histogram, wait time on successful acquires</item>
/// </list>
/// <para>
/// The reference registers on the process-wide OpenTelemetry meter named
/// <c>axonhub</c>. The port uses <see cref="Meter"/> from
/// System.Diagnostics.Metrics with the same name — the surface OpenTelemetry
/// .NET collects (difference ledger C044). The gauges read live state from
/// <see cref="ChannelLimiterManager.Snapshot"/> at scrape time.
/// </para>
/// <para>A null meter yields an instance whose emission methods are no-ops,
/// mirroring the reference's nil-meter path.</para>
/// </remarks>
public sealed class ChannelLimiterMetrics
{
    /// <summary>The reference's meter scope name (<c>provider.Meter("axonhub")</c>).</summary>
    public const string MeterName = "axonhub";

    /// <summary>
    /// The process-wide meter, matching the reference's global <c>metrics.Meter</c>.
    /// It lives for the process lifetime and needs no disposal.
    /// </summary>
    private static readonly Meter DefaultMeter = new(MeterName);

    private readonly Counter<long>? _queueFull;
    private readonly Counter<long>? _queueTimeout;
    private readonly Histogram<double>? _queueWait;

    /// <summary>
    /// Registers the instruments on <paramref name="meter"/> and wires the gauge
    /// callbacks against <paramref name="manager"/>.
    /// </summary>
    public ChannelLimiterMetrics(Meter? meter, ChannelLimiterManager? manager)
    {
        if (meter is null)
        {
            return;
        }

        _queueFull = meter.CreateCounter<long>(
            "axonhub_channel_queue_full_total",
            unit: "requests",
            description: "Total requests rejected because the channel queue was full");
        _queueTimeout = meter.CreateCounter<long>(
            "axonhub_channel_queue_timeout_total",
            unit: "requests",
            description: "Total requests that exited the channel queue via per-channel wait timeout");
        _queueWait = meter.CreateHistogram<double>(
            "axonhub_channel_queue_wait_seconds",
            unit: "s",
            description: "Wait time from channel-limiter Acquire entry to slot grant, on successful acquisitions");

        meter.CreateObservableGauge(
            "axonhub_channel_inflight",
            () => Observe(manager, static snapshot => snapshot.InFlight),
            unit: "requests",
            description: "In-flight requests currently holding a channel-limiter slot");
        meter.CreateObservableGauge(
            "axonhub_channel_queue_waiting",
            () => Observe(manager, static snapshot => snapshot.Waiting),
            unit: "requests",
            description: "Requests currently waiting in the channel-limiter FIFO queue");
    }

    /// <summary>Registers the instruments on the process-wide meter named <see cref="MeterName"/>.</summary>
    public static ChannelLimiterMetrics CreateDefault(ChannelLimiterManager? manager)
        => new(DefaultMeter, manager);

    /// <summary>Increments the queue-full rejection counter.</summary>
    public void IncQueueFull(long channelId, string channelName)
        => _queueFull?.Add(1, Tags(channelId, channelName));

    /// <summary>Increments the queue-timeout exit counter.</summary>
    public void IncQueueTimeout(long channelId, string channelName)
        => _queueTimeout?.Add(1, Tags(channelId, channelName));

    /// <summary>Records the wait duration for a successful acquire.</summary>
    public void ObserveQueueWait(long channelId, string channelName, TimeSpan duration)
        => _queueWait?.Record(duration.TotalSeconds, Tags(channelId, channelName));

    private static Measurement<long>[] Observe(
        ChannelLimiterManager? manager,
        Func<ChannelLimiterSnapshot, int> selector)
    {
        if (manager is null)
        {
            return [];
        }

        var snapshots = manager.Snapshot();
        var measurements = new Measurement<long>[snapshots.Count];
        for (var i = 0; i < snapshots.Count; i++)
        {
            var snapshot = snapshots[i];
            measurements[i] = new Measurement<long>(
                selector(snapshot),
                Tags(snapshot.ChannelId, snapshot.ChannelName));
        }

        return measurements;
    }

    private static KeyValuePair<string, object?>[] Tags(long channelId, string channelName)
        => [new("channel_id", channelId), new("channel_name", channelName)];
}
