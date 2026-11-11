namespace AxonHub.Api.Management;

/// <summary>
/// Port of ChannelProbeService's <c>lastExecutionTime</c> guard
/// (channel_probe.go:62-66, 252-267): the aligned timestamp of the last probe
/// pass, used to skip a rerun inside the same interval. The reference keeps it
/// on the process-wide singleton service; the port's service is scoped, so the
/// guard lives in this singleton.
/// </summary>
public sealed class ChannelProbeRunState
{
    private readonly object _gate = new();

    /// <summary>Go's zero <c>time.Time</c> means "never executed"; null here.</summary>
    private long? _lastExecutionUnix;

    /// <summary>
    /// Returns <see langword="false"/> when the current interval already ran;
    /// otherwise records <paramref name="alignedUnix"/> as the last execution
    /// and returns <see langword="true"/>. The reference stores the timestamp
    /// before the database work, so a failing pass still blocks a rerun in the
    /// same interval.
    /// </summary>
    public bool TryBeginRun(long alignedUnix)
    {
        lock (_gate)
        {
            if (_lastExecutionUnix == alignedUnix)
            {
                return false;
            }

            _lastExecutionUnix = alignedUnix;
            return true;
        }
    }
}
