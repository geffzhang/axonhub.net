using AxonHub.Abstractions;

namespace AxonHub.Persistence;

/// <summary>
/// One stored-channel read for the same-channel retry guard. <see cref="Failed"/>
/// marks a transient lookup failure, which leaves the pinned candidate untouched
/// instead of ending its retries (retry_channel_refresh.go).
/// </summary>
public readonly record struct EnabledChannelRefreshLookup(
    ChannelDescriptor? Channel,
    bool Failed)
{
    public static EnabledChannelRefreshLookup Found(ChannelDescriptor channel)
        => new(channel, Failed: false);

    /// <summary>A confirmed miss: deleted, disabled or unknown.</summary>
    public static EnabledChannelRefreshLookup Unavailable => new(null, Failed: false);

    public static EnabledChannelRefreshLookup Failure => new(null, Failed: true);
}

/// <summary>
/// Synchronous stored-channel read for the request retry path. The reference's
/// <c>PrepareForRetry</c> re-reads the channel by id before a same-channel
/// retry; the port's retry seams are synchronous, so this source opens a
/// short-lived unit of work like the pricing seam.
/// </summary>
public interface IChannelRefreshSource
{
    /// <summary>
    /// The live enabled channel, a confirmed miss, or a transient failure.
    /// Disabled and soft-deleted rows are confirmed misses because both end the
    /// retry exactly like the reference's status check.
    /// </summary>
    EnabledChannelRefreshLookup GetEnabledChannel(long channelId);
}

/// <summary>PostgreSQL implementation of <see cref="IChannelRefreshSource"/>.</summary>
public sealed class PostgresChannelRefreshSource(IAxonHubDbContextFactory dbFactory)
    : IChannelRefreshSource
{
    public EnabledChannelRefreshLookup GetEnabledChannel(long channelId)
    {
        try
        {
            using var context = dbFactory.CreateDbContext();
            var channel = context.Channels.FirstOrDefault(row =>
                row.Id == channelId
                && row.DeletedAt == 0
                && row.Status == "enabled");

            return channel is null
                ? EnabledChannelRefreshLookup.Unavailable
                : EnabledChannelRefreshLookup.Found(
                    PostgresEnabledChannelSource.ToDescriptor(channel));
        }
        catch (Exception)
        {
            // A degraded read must never cancel an otherwise legitimate retry.
            return EnabledChannelRefreshLookup.Failure;
        }
    }
}
