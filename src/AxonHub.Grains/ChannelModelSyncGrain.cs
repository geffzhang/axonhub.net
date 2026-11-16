using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains;

/// <summary>
/// The reference's model-sync execution guard as a singleton actor
/// (internal/server/biz/channel_internal.go:33-46): the mutex-protected
/// <c>lastModelSyncExecutionTime</c> that decides whether a scheduled tick may
/// start a sync pass.
/// </summary>
/// <remarks>
/// The alignment arithmetic is the port's existing
/// <c>ChannelModelSyncService.ShouldRunModelSync</c>: Go's <c>Time.Truncate</c>
/// truncates the absolute UTC instant, and
/// <see cref="DateTimeOffset.UtcTicks"/> is the same epoch in .NET.
/// <see cref="Grain.DelayDeactivation"/> keeps the activation alive between the
/// reference's hourly cron ticks; without it the default collection age
/// (fifteen minutes) would collect the grain and lose the current window, so a
/// six-hour or daily schedule would run on the next hourly tick instead of
/// waiting for the next aligned window.
/// </remarks>
public sealed class ChannelModelSyncGrain : Grain, IChannelModelSyncGrain
{
    /// <summary>
    /// The reference's scheduler ticks every hour at minute 11; two hours of
    /// keep-alive bridges a delayed tick while still letting an abandoned
    /// activation expire.
    /// </summary>
    private static readonly TimeSpan DeactivationKeepAlive = TimeSpan.FromHours(2);

    private DateTimeOffset? _lastModelSyncExecutionTime;

    public Task<bool> TryBeginRunAsync(DateTimeOffset now, int intervalMinutes)
    {
        var interval = TimeSpan.FromMinutes(intervalMinutes);
        var utcTicks = now.UtcTicks;
        var aligned = new DateTimeOffset(utcTicks - (utcTicks % interval.Ticks), TimeSpan.Zero);

        DelayDeactivation(DeactivationKeepAlive);

        if (_lastModelSyncExecutionTime is { } last && last == aligned)
        {
            return Task.FromResult(false);
        }

        _lastModelSyncExecutionTime = aligned;
        return Task.FromResult(true);
    }
}
