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

    public UsageCost? Compute(long channelId, string modelId, Usage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        // GetEnabledChannel(0) never matches: ids start at 1.
        if (channelId <= 0)
        {
            return null;
        }

        var rows = pricing.ListEnabledChannelPrices(channelId);
        if (rows.Count == 0)
        {
            return null;
        }

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

        if (!prices.TryGetValue(modelId, out var entry))
        {
            return null;
        }

        var result = UsageCostCalculator.Compute(usage, entry.Price, timeProvider.GetUtcNow());
        return new UsageCost(result.Items, result.Total, entry.ReferenceId);
    }

    public bool InjectUsageCostEnabled()
        => string.Equals(
            pricing.GetSystemValue(InjectUsageCostKey),
            "true",
            StringComparison.Ordinal);
}
