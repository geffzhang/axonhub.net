using System.Text.Json;
using AxonHub.Llm;
using AxonHub.Persistence;

namespace AxonHub.Api.Billing;

/// <summary>
/// A priced usage: the exact items, the exact decimal total, and the price
/// row's reference id (the reference's computeUsageCost result).
/// </summary>
public sealed record UsageCost(IReadOnlyList<CostItem> Items, decimal Total, string ReferenceId)
{
    /// <summary>decimal.InexactFloat64, used by both usage.cost and total_cost.</summary>
    public double TotalCost => (double)Total;
}

/// <summary>
/// Stored cost lookup seam (internal/server/biz/usage_log.go's
/// computeUsageCost / InjectUsageCost). The reference reads an in-memory price
/// cache; the port reads the same rows through a short-lived unit of work, so
/// the seam is synchronous like the request-chain recorder (C018).
/// </summary>
public interface IUsageCostService
{
    /// <summary>
    /// The priced usage for an enabled channel and model, or null when the
    /// channel is not enabled, has no live price for the model, or a price row
    /// cannot be decoded.
    /// </summary>
    UsageCost? Compute(long channelId, string modelId, Usage usage);

    /// <summary>system_inject_usage_cost; a missing row means false.</summary>
    bool InjectUsageCostEnabled();
}

/// <summary>
/// Port of UsageLogService.computeUsageCost and the inject-usage-cost setting
/// (internal/server/biz/system.go: InjectUsageCostEnabled).
/// </summary>
public sealed class UsageCostService(IChannelPricingSource pricing, TimeProvider timeProvider)
    : IUsageCostService
{
    /// <summary>SystemKeyInjectUsageCost.</summary>
    public const string InjectUsageCostKey = "system_inject_usage_cost";

    // One resolution per (request, model id), including misses. The reference
    // installs this memo on the request context; here the cost service is
    // scoped, so its instance is the request boundary.
    private readonly object _modelLibraryMemoGate = new();
    private Dictionary<string, ModelLibraryMemoEntry>? _modelLibraryMemo;

    public UsageCost? Compute(long channelId, string modelId, Usage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        // GetEnabledChannel(0) never matches: ids start at 1.
        if (channelId <= 0)
        {
            return null;
        }

        var rows = pricing.ListEnabledChannelPrices(channelId);

        // preloadModelPrices builds one map per channel. A row whose JSON fails
        // to decode makes the whole preload fail, so the channel ends up with no
        // cached prices at all rather than skipping just that row.
        var prices = new Dictionary<string, (ModelPrice Price, string ReferenceId)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ModelPrice? price;
            try
            {
                // JSON null is a valid price value in Go: unmarshalling null
                // into a struct leaves its zero value, i.e. no items.
                price = JsonSerializer.Deserialize<ModelPrice>(row.Price, BillingJson.Options)
                    ?? new ModelPrice();
            }
            catch (JsonException)
            {
                return null;
            }

            prices[row.ModelId] = (price, row.ReferenceId);
        }

        if (prices.TryGetValue(modelId, out var entry))
        {
            var result = UsageCostCalculator.Compute(usage, entry.Price, timeProvider.GetUtcNow());
            return new UsageCost(result.Items, result.Total, entry.ReferenceId);
        }

        // An empty price list also covers a disabled or deleted channel, which
        // the reference rejects before its model-library fallback. Only an
        // enabled channel without an explicit price may use the library price.
        if (rows.Count == 0 && !pricing.IsEnabledChannel(channelId))
        {
            return null;
        }

        // Fallback: the channel has no explicit price for this model, so bill
        // with the model library price configured under the Models menu. This
        // keeps cost working when auto-configuration could not snapshot the
        // price, for example when the price was added after channel creation.
        var libraryPrice = ResolveModelLibraryPrice(modelId);
        if (libraryPrice is null)
        {
            return null;
        }

        var libraryResult = UsageCostCalculator.Compute(
            usage,
            libraryPrice,
            timeProvider.GetUtcNow());
        return new UsageCost(
            libraryResult.Items,
            libraryResult.Total,
            ModelLibraryPrice.ReferenceId(modelId));
    }

    public bool InjectUsageCostEnabled()
        => string.Equals(
            pricing.GetSystemValue(InjectUsageCostKey),
            "true",
            StringComparison.Ordinal);

    /// <summary>
    /// Port of ChannelService.resolveModelLibraryPrice: exact model id first,
    /// then a known effort/tier suffix stripped. A lookup error is not cached,
    /// so the usage-log write (which runs after cost injection) can retry with
    /// a healthy context; a genuine miss is cached for the request.
    /// </summary>
    private ModelPrice? ResolveModelLibraryPrice(string modelId)
    {
        if (modelId.Length == 0)
        {
            return null;
        }

        lock (_modelLibraryMemoGate)
        {
            if (_modelLibraryMemo is not null
                && _modelLibraryMemo.TryGetValue(modelId, out var cached))
            {
                return cached.Price;
            }
        }

        var lookupFailed = false;
        foreach (var candidate in ModelLibraryPrice.Candidates(modelId))
        {
            var lookup = pricing.FindModelLibraryModelCard(candidate);
            if (lookup.Failed)
            {
                lookupFailed = true;
                continue;
            }

            if (!lookup.Found || lookup.ModelCardJson is null)
            {
                continue;
            }

            ModelPrice? price;
            try
            {
                price = ModelCardPricing.ToPriceFromJson(lookup.ModelCardJson);
            }
            catch (JsonException)
            {
                // ent decodes model_card while scanning the row, so a malformed
                // card is a failed lookup rather than a genuine miss.
                lookupFailed = true;
                continue;
            }

            if (price is null)
            {
                continue;
            }

            lock (_modelLibraryMemoGate)
            {
                _modelLibraryMemo ??= new Dictionary<string, ModelLibraryMemoEntry>(
                    StringComparer.Ordinal);
                _modelLibraryMemo[modelId] = new ModelLibraryMemoEntry(price);
            }

            return price;
        }

        if (!lookupFailed)
        {
            lock (_modelLibraryMemoGate)
            {
                _modelLibraryMemo ??= new Dictionary<string, ModelLibraryMemoEntry>(
                    StringComparer.Ordinal);
                _modelLibraryMemo[modelId] = new ModelLibraryMemoEntry(null);
            }
        }

        return null;
    }

    private readonly record struct ModelLibraryMemoEntry(ModelPrice? Price);
}
