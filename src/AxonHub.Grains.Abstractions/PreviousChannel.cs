using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Settings for <see cref="IPreviousChannelGrain"/>. The only knob is the cache
/// TTL: the reference writes both scopes with
/// <c>WithExpiration(30*time.Minute)</c> (request.go:1783-1789). Production
/// keeps the default; the grain tests shorten it so expiry is observable
/// without waiting half an hour.
/// </summary>
public sealed record PreviousChannelOptions
{
    /// <summary>Reference <c>setPreviousChannelCache</c> expiry.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    /// <summary>Lifetime of one cached entry, restarted on every write.</summary>
    public TimeSpan Ttl { get; init; } = DefaultTtl;
}

/// <summary>
/// The reference's <c>previousChannelCache</c> entry as an actor: one grain per
/// cache key (<c>axonhub:routing:previous-channel:v1:trace|thread:&lt;id&gt;</c>,
/// request.go:1795-1801), holding the most recently selected channel id with
/// the thirty-minute expiry.
/// </summary>
/// <remarks>
/// The reference resolves the entry through
/// <c>RequestService.GetPreviousChannelID</c> /
/// <c>GetPreviousChannelIDByThread</c>, which read the cache only and never
/// query historical requests. A missing or expired key answers zero.
/// </remarks>
public interface IPreviousChannelGrain : IGrainWithStringKey
{
    /// <summary>
    /// Returns the cached channel id, or 0 when the key is absent or its TTL
    /// has passed (xcache reports a miss after expiry).
    /// </summary>
    Task<long> GetAsync();

    /// <summary>
    /// Port of <c>cachePreviousChannelForRequest</c>'s write: stores the channel
    /// and restarts the thirty-minute TTL.
    /// </summary>
    Task RememberAsync(long channelId);
}
