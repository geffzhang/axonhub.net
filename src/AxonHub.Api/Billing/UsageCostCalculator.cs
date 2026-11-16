using System.Globalization;
using AxonHub.Llm;

namespace AxonHub.Api.Billing;

/// <summary>The price items resolved for one call and their exact decimal total.</summary>
public sealed record UsageCostResult(IReadOnlyList<CostItem> Items, decimal Total);

/// <summary>
/// Port of internal/server/biz/cost_calc.go. The calculator is pure: it takes
/// the unified usage, a decoded model price and the instant used for schedule
/// matching. Amounts stay decimal end-to-end; only the persisted
/// <c>usage_logs.total_cost</c> and <c>usage.cost</c> convert to double
/// (decimal.InexactFloat64).
/// </summary>
public static class UsageCostCalculator
{
    /// <summary>Port of ComputeUsageCost.</summary>
    public static UsageCostResult Compute(Usage usage, ModelPrice price, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(price);

        var effectiveItems = price.Items;
        if (price.Schedule is { } schedule)
        {
            var matched = FindMatchingOverride(now, schedule);
            if (matched is not null)
            {
                effectiveItems = matched.Items;
            }
        }

        return ComputeWithItems(usage, effectiveItems);
    }

    /// <summary>Port of computeUsageCostWithItems.</summary>
    private static UsageCostResult ComputeWithItems(Usage usage, List<ModelPriceItem>? priceItems)
    {
        var items = new List<CostItem>();
        var total = 0m;

        foreach (var priceItem in priceItems ?? [])
        {
            long quantity = 0;

            switch (priceItem.ItemCode)
            {
                case PriceItemCodes.PromptTokens:
                    // PromptTokens includes cached reads and cached writes; both
                    // are billed separately at their own rates.
                    quantity = usage.PromptTokens;
                    if (usage.PromptTokensDetails is { } promptDetails)
                    {
                        quantity -= promptDetails.CachedTokens;
                        quantity -= promptDetails.WriteCachedTokens;
                    }

                    if (quantity < 0)
                    {
                        quantity = 0;
                    }

                    break;
                case PriceItemCodes.CompletionTokens:
                    quantity = usage.CompletionTokens;
                    break;
                case PriceItemCodes.PromptCachedTokens:
                    if (usage.PromptTokensDetails is { } cachedDetails)
                    {
                        quantity = cachedDetails.CachedTokens;
                    }

                    break;
                case PriceItemCodes.PromptWriteCachedTokens:
                    if (usage.PromptTokensDetails is { } writeDetails)
                    {
                        if (writeDetails.WriteCached5MinTokens > 0 || writeDetails.WriteCached1HourTokens > 0)
                        {
                            if (writeDetails.WriteCached5MinTokens > 0)
                            {
                                var pricing = priceItem.FindPromptWriteCacheVariantPricing(
                                    PromptWriteCacheVariantCodes.FiveMin);
                                var (item, subtotal) = ComputeItemSubtotal(
                                    writeDetails.WriteCached5MinTokens,
                                    pricing);
                                item.ItemCode = priceItem.ItemCode;
                                item.PromptWriteCacheVariantCode = PromptWriteCacheVariantCodes.FiveMin;
                                items.Add(item);
                                total += subtotal;
                            }

                            if (writeDetails.WriteCached1HourTokens > 0)
                            {
                                var pricing = priceItem.FindPromptWriteCacheVariantPricing(
                                    PromptWriteCacheVariantCodes.OneHour);
                                var (item, subtotal) = ComputeItemSubtotal(
                                    writeDetails.WriteCached1HourTokens,
                                    pricing);
                                item.ItemCode = priceItem.ItemCode;
                                item.PromptWriteCacheVariantCode = PromptWriteCacheVariantCodes.OneHour;
                                items.Add(item);
                                total += subtotal;
                            }

                            // The variants replace the shared write-cache item.
                            continue;
                        }

                        quantity = writeDetails.WriteCachedTokens;
                    }

                    break;
                default:
                    quantity = 0;
                    break;
            }

            var (costItem, itemSubtotal) = ComputeItemSubtotal(quantity, priceItem.Pricing ?? new Pricing());
            costItem.ItemCode = priceItem.ItemCode;
            items.Add(costItem);
            total += itemSubtotal;
        }

        return new UsageCostResult(items, total);
    }

    /// <summary>Port of computeItemSubtotal.</summary>
    private static (CostItem Item, decimal Subtotal) ComputeItemSubtotal(long quantity, Pricing pricing)
    {
        var item = new CostItem { Quantity = quantity };

        switch (pricing.Mode)
        {
            case PricingModes.FlatFee:
                if (pricing.FlatFee is { } flatFee)
                {
                    item.Subtotal = flatFee;
                    return (item, flatFee);
                }

                return (item, 0m);
            case PricingModes.UsagePerUnit:
                if (pricing.UsagePerUnit is { } perUnit)
                {
                    var subtotal = perUnit * UnitsInMillionTokens(quantity);
                    item.Subtotal = subtotal;
                    return (item, subtotal);
                }

                return (item, 0m);
            case PricingModes.Tiered:
                if (pricing.UsageTiered is { } tiered)
                {
                    var total = 0m;
                    var previousUpTo = 0L;
                    List<TierCost>? breakdown = null;

                    foreach (var tier in tiered.Tiers ?? [])
                    {
                        long tierUnits;
                        if (tier.UpTo is { } upTo)
                        {
                            tierUnits = quantity <= upTo
                                ? Math.Max(quantity - previousUpTo, 0)
                                : Math.Max(upTo - previousUpTo, 0);
                        }
                        else
                        {
                            tierUnits = Math.Max(quantity - previousUpTo, 0);
                        }

                        if (tierUnits <= 0)
                        {
                            if (tier.UpTo is { } bound && quantity <= bound)
                            {
                                break;
                            }

                            previousUpTo = GetUpToOrZero(tier.UpTo);
                            continue;
                        }

                        var subtotal = tier.PricePerUnit * UnitsInMillionTokens(tierUnits);
                        total += subtotal;
                        (breakdown ??= []).Add(new TierCost
                        {
                            UpTo = tier.UpTo,
                            Units = tierUnits,
                            Subtotal = subtotal,
                        });
                        previousUpTo = GetUpToOrZero(tier.UpTo);

                        if (tier.UpTo is { } upper && quantity <= upper)
                        {
                            break;
                        }
                    }

                    item.Subtotal = total;
                    item.TierBreakdown = breakdown;
                    return (item, total);
                }

                return (item, 0m);
            case PricingModes.Volume:
                if (pricing.UsageTiered is { } volume)
                {
                    var tiers = volume.Tiers ?? [];
                    var matchedIndex = -1;

                    for (var index = 0; index < tiers.Count; index++)
                    {
                        var tier = tiers[index];
                        if (tier.UpTo is { } upTo && quantity <= upTo)
                        {
                            matchedIndex = index;
                            break;
                        }

                        if (tier.UpTo is null)
                        {
                            matchedIndex = index;
                            break;
                        }
                    }

                    if (matchedIndex >= 0)
                    {
                        var matchedTier = tiers[matchedIndex];
                        var subtotal = matchedTier.PricePerUnit * UnitsInMillionTokens(quantity);
                        item.Subtotal = subtotal;
                        item.TierBreakdown =
                        [
                            new TierCost
                            {
                                UpTo = matchedTier.UpTo,
                                Units = quantity,
                                Subtotal = subtotal,
                            },
                        ];

                        return (item, subtotal);
                    }

                    return (item, 0m);
                }

                return (item, 0m);
            default:
                return (item, 0m);
        }
    }

    private static long GetUpToOrZero(long? upTo) => upTo ?? 0;

    /// <summary>Port of unitsInMillionTokens: quantity / 1,000,000, clamped at zero.</summary>
    private static decimal UnitsInMillionTokens(long units)
        => units <= 0 ? 0m : units / 1_000_000m;

    /// <summary>Port of findMatchingOverride: the lowest priority value wins.</summary>
    private static PriceOverride? FindMatchingOverride(DateTimeOffset now, PriceSchedule schedule)
    {
        if (schedule.Overrides is not { Count: > 0 } overrides)
        {
            return null;
        }

        var localNow = TimeZoneInfo.ConvertTime(now, ResolveTimeZone(schedule.Timezone));

        PriceOverride? matched = null;
        foreach (var candidate in overrides)
        {
            // A missing "when" decodes to Go's zero OverrideWhen, which matches
            // every time.
            if (!MatchesOverride(localNow, candidate.When ?? new OverrideWhen()))
            {
                continue;
            }

            if (matched is null || candidate.Priority < matched.Priority)
            {
                matched = candidate;
            }
        }

        return matched;
    }

    /// <summary>Port of matchesOverride.</summary>
    private static bool MatchesOverride(DateTimeOffset localNow, OverrideWhen when)
    {
        if (when.DailyTime is { } dailyTime && !MatchesDailyTime(localNow, dailyTime))
        {
            return false;
        }

        if (when.Weekdays is { Count: > 0 } weekdays)
        {
            var weekday = (int)localNow.DayOfWeek;
            if (weekday == 0)
            {
                weekday = 7; // Sunday = 7
            }

            if (!weekdays.Contains(weekday))
            {
                return false;
            }
        }

        if (when.DateRange is { } dateRange)
        {
            // Both sides are zero-padded YYYY-MM-DD, so ordinal comparison is
            // the reference's lexicographic string comparison.
            var today = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.CompareOrdinal(today, dateRange.Start) < 0
                || string.CompareOrdinal(today, dateRange.End) > 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Port of matchesDailyTime, including cross-midnight ranges.</summary>
    private static bool MatchesDailyTime(DateTimeOffset localNow, DailyTimeRange range)
    {
        var current = (localNow.Hour * 60) + localNow.Minute;
        var start = ParseHhMm(range.Start);
        var end = ParseHhMm(range.End);

        if (start > end)
        {
            return current >= start || current < end;
        }

        return current >= start && current < end;
    }

    /// <summary>Port of parseHHMM; malformed values collapse to midnight.</summary>
    private static int ParseHhMm(string value)
    {
        if (value.Length != 5 || value[2] != ':')
        {
            return 0;
        }

        var hour = ((value[0] - '0') * 10) + (value[1] - '0');
        var minute = ((value[3] - '0') * 10) + (value[4] - '0');
        return (hour * 60) + minute;
    }

    /// <summary>
    /// time.LoadLocation: "" and "UTC" are UTC, "Local" is the host zone, and an
    /// unknown or unloadable name falls back to UTC.
    /// </summary>
    private static TimeZoneInfo ResolveTimeZone(string name)
    {
        if (name.Length == 0)
        {
            return TimeZoneInfo.Utc;
        }

        if (string.Equals(name, "Local", StringComparison.Ordinal))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
