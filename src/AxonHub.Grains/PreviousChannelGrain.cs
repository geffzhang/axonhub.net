using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains;

/// <summary>
/// The reference's previous-channel cache entry as an actor
/// (internal/server/biz/request.go:1733-1801): one string-keyed grain per trace
/// or thread scope, holding the most recently selected channel and the
/// thirty-minute expiry that trace sticky routing reads.
/// </summary>
/// <remarks>
/// <para>
/// The reference keeps the value in <c>xcache.Cache[int]</c>, one process-local
/// map by default and Redis when configured. One grain per key gives every silo
/// in the cluster the same entry, and Orleans serializes its turns, so reads
/// and writes have a single order per key (§147; C181).
/// </para>
/// <para>
/// The entry is dropped lazily on the first read after expiry, which matches
/// the in-process xcache backend. <see cref="Grain.DelayDeactivation"/> keeps
/// the activation alive until the TTL passes: the default collection age
/// (fifteen minutes) is shorter than the reference's thirty-minute TTL, and a
/// reactivated grain would otherwise forget a still-live entry.
/// </para>
/// </remarks>
public sealed class PreviousChannelGrain(PreviousChannelOptions options, TimeProvider timeProvider)
    : Grain, IPreviousChannelGrain
{
    /// <summary>
    /// Extra activation lifetime past the entry expiry so the runtime cannot
    /// collect the grain while a still-valid entry is readable.
    /// </summary>
    private static readonly TimeSpan DeactivationPadding = TimeSpan.FromMinutes(1);

    private long _channelId;
    private DateTimeOffset _expiresAt;

    public Task<long> GetAsync()
    {
        if (_expiresAt <= timeProvider.GetUtcNow())
        {
            // A miss, including the never-written zero timestamp.
            _channelId = 0;
            return Task.FromResult(0L);
        }

        return Task.FromResult(_channelId);
    }

    public Task RememberAsync(long channelId)
    {
        // setPreviousChannelCache overwrites per key and restarts the TTL.
        var now = timeProvider.GetUtcNow();
        _channelId = channelId;
        _expiresAt = now + options.Ttl;
        DelayDeactivation(options.Ttl + DeactivationPadding);
        return Task.CompletedTask;
    }
}
