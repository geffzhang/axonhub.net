namespace AxonHub.Persistence;

/// <summary>
/// Synchronous pricing/settings reads for the request-chain middleware. The
/// reference reads a per-process channel cache; the port reads the same rows
/// through a short-lived unit of work per call, mirroring the recorder seam
/// (C018): callers are synchronous pipeline hooks.
/// </summary>
public interface IChannelPricingSource
{
    /// <summary>Live prices for a live, enabled channel; empty otherwise.</summary>
    IReadOnlyList<ChannelModelPriceRecord> ListEnabledChannelPrices(long channelId);

    /// <summary>A live system setting value, or null when the key is missing.</summary>
    string? GetSystemValue(string key);
}

/// <summary>PostgreSQL implementation of <see cref="IChannelPricingSource"/>.</summary>
public sealed class PostgresChannelPricingSource(IAxonHubDbContextFactory dbFactory) : IChannelPricingSource
{
    public IReadOnlyList<ChannelModelPriceRecord> ListEnabledChannelPrices(long channelId)
    {
        using var context = dbFactory.CreateDbContext();
        return new ChannelModelPriceStore(context)
            .ListEnabledChannelPricesAsync(channelId)
            .GetAwaiter()
            .GetResult();
    }

    public string? GetSystemValue(string key)
    {
        using var context = dbFactory.CreateDbContext();
        return new SystemStore(context).GetByKeyAsync(key).GetAwaiter().GetResult()?.Value;
    }
}
