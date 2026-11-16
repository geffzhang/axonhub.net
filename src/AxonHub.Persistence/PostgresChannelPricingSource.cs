namespace AxonHub.Persistence;

/// <summary>
/// One model-library lookup for the billing fallback. <see cref="Failed"/>
/// separates a transient/decoding failure from a genuine miss so the caller
/// does not memoize a failed attempt.
/// </summary>
public readonly record struct ModelLibraryModelCardLookup(
    string? ModelCardJson,
    bool Found,
    bool Failed)
{
    public static ModelLibraryModelCardLookup Hit(string modelCardJson)
        => new(modelCardJson, Found: true, Failed: false);

    public static ModelLibraryModelCardLookup Miss => new(null, Found: false, Failed: false);

    public static ModelLibraryModelCardLookup Failure => new(null, Found: false, Failed: true);
}

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

    /// <summary>
    /// True when the channel is live and enabled. The price list is empty both
    /// for a disabled channel and for an enabled channel without prices, and
    /// the model-library fallback only applies to the latter.
    /// </summary>
    bool IsEnabledChannel(long channelId);

    /// <summary>
    /// Model-library card for a model id, restricted to live enabled/disabled
    /// entries (the same eligibility the auto-pricing path uses).
    /// </summary>
    ModelLibraryModelCardLookup FindModelLibraryModelCard(string modelId);

    /// <summary>A live system setting value, or null when the key is missing.</summary>
    string? GetSystemValue(string key);
}

/// <summary>PostgreSQL implementation of <see cref="IChannelPricingSource"/>.</summary>
public sealed class PostgresChannelPricingSource(
    IAxonHubDbContextFactory dbFactory,
    SystemValueCache? systemValueCache = null) : IChannelPricingSource
{
    public IReadOnlyList<ChannelModelPriceRecord> ListEnabledChannelPrices(long channelId)
    {
        using var context = dbFactory.CreateDbContext();
        return new ChannelModelPriceStore(context)
            .ListEnabledChannelPricesAsync(channelId)
            .GetAwaiter()
            .GetResult();
    }

    public bool IsEnabledChannel(long channelId)
    {
        using var context = dbFactory.CreateDbContext();
        return context.Channels.Any(channel =>
            channel.Id == channelId
            && channel.DeletedAt == 0
            && channel.Status == "enabled");
    }

    public ModelLibraryModelCardLookup FindModelLibraryModelCard(string modelId)
    {
        try
        {
            using var context = dbFactory.CreateDbContext();
            var modelCard = context.Models
                .Where(model =>
                    model.ModelId == modelId
                    && (model.Status == "enabled" || model.Status == "disabled")
                    && model.DeletedAt == 0)
                .Select(model => model.ModelCard)
                .FirstOrDefault();

            return modelCard is null
                ? ModelLibraryModelCardLookup.Miss
                : ModelLibraryModelCardLookup.Hit(modelCard);
        }
        catch (Exception)
        {
            // The reference logs and returns a lookup error so the request memo
            // does not cache it; a later healthy lookup retries.
            return ModelLibraryModelCardLookup.Failure;
        }
    }

    public string? GetSystemValue(string key)
    {
        using var context = dbFactory.CreateDbContext();
        ISystemStore store = systemValueCache is null
            ? new SystemStore(context)
            : new CachingSystemStore(new SystemStore(context), systemValueCache);
        return store.GetByKeyAsync(key).GetAwaiter().GetResult()?.Value;
    }
}
