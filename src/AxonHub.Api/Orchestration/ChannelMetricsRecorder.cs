using AxonHub.Grains.Abstractions;
using Orleans;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Seam over the per-channel performance counters. The reference records them
/// through <c>ChannelService.AsyncRecordPerformance</c>
/// (internal/server/biz/channel_metrics.go); the port routes each record to the
/// channel grain that owns the counters.
/// </summary>
public interface IChannelMetricsRecorder
{
    void RecordSelection(long channelId, DateTimeOffset selectedAt);

    void RecordSuccess(long channelId, DateTimeOffset completedAt);

    void RecordFailure(long channelId, DateTimeOffset completedAt);
}

/// <summary>
/// Grain-backed recorder. Pipeline execution is synchronous (port of the Go
/// pipeline), so each record waits for the grain call; there is no
/// synchronization context on the ASP.NET request thread or thread-pool.
/// </summary>
public sealed class GrainChannelMetricsRecorder(IGrainFactory grainFactory) : IChannelMetricsRecorder
{
    public void RecordSelection(long channelId, DateTimeOffset selectedAt)
        => Record(channelId, grain => grain.RecordSelectionAsync(selectedAt));

    public void RecordSuccess(long channelId, DateTimeOffset completedAt)
        => Record(channelId, grain => grain.RecordSuccessAsync(completedAt));

    public void RecordFailure(long channelId, DateTimeOffset completedAt)
        => Record(channelId, grain => grain.RecordFailureAsync(completedAt));

    private void Record(long channelId, Func<IChannelGrain, Task> record)
    {
        // IChannelGrain calls always complete from this thread's continuation
        // pool; GetResult only unwraps the already-completed Task.
        record(grainFactory.GetGrain<IChannelGrain>(channelId)).GetAwaiter().GetResult();
    }
}
