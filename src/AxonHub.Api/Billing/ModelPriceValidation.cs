using System.Globalization;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Billing;

/// <summary>
/// Port of objects.ModelPrice.Validate and the Equals family
/// (internal/objects/price.go:48-664). The reference exposes validation as
/// errors wrapped with fmt.Errorf("%w"); the port throws the same concatenated
/// messages as <see cref="InvalidOperationException"/>.
/// </summary>
public static class ModelPriceValidation
{
    public static void ValidateModelPrice(ModelPrice? price)
    {
        if (price is null)
        {
            throw new InvalidOperationException("modelPrice is nil");
        }

        for (var index = 0; index < (price.Items?.Count ?? 0); index++)
        {
            try
            {
                ValidateItem(price.Items![index]);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"items[{index}]: {ex.Message}", ex);
            }
        }

        if (price.Schedule is not null)
        {
            try
            {
                ValidateSchedule(price.Schedule);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("schedule: " + ex.Message, ex);
            }
        }
    }

    private static void ValidateItem(ModelPriceItem? item)
    {
        if (item is null)
        {
            throw new InvalidOperationException("modelPriceItem is nil");
        }

        try
        {
            ValidatePricing(item.Pricing);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("pricing: " + ex.Message, ex);
        }

        for (var index = 0; index < (item.PromptWriteCacheVariants?.Count ?? 0); index++)
        {
            var variant = item.PromptWriteCacheVariants![index];
            if (variant is null)
            {
                throw new InvalidOperationException(
                    $"promptWriteCacheVariants[{index}]: promptWriteCacheVariant is nil");
            }

            try
            {
                ValidatePricing(variant.Pricing);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"promptWriteCacheVariants[{index}]: pricing: {ex.Message}",
                    ex);
            }
        }
    }

    private static void ValidatePricing(Pricing? pricing)
    {
        if (pricing is null)
        {
            throw new InvalidOperationException("pricing is nil");
        }

        switch (pricing.Mode)
        {
            case PricingModes.FlatFee:
                if (pricing.FlatFee is null)
                {
                    throw new InvalidOperationException("flatFee is required");
                }

                break;
            case PricingModes.UsagePerUnit:
                if (pricing.UsagePerUnit is null)
                {
                    throw new InvalidOperationException("usagePerUnit is required");
                }

                break;
            case PricingModes.Tiered:
            case PricingModes.Volume:
                if (pricing.UsageTiered is null)
                {
                    throw new InvalidOperationException("usageTiered is required");
                }

                ValidateTiered(pricing.UsageTiered);
                break;
            default:
                throw new InvalidOperationException(
                    "unknown pricing mode: " + pricing.Mode);
        }
    }

    private static void ValidateTiered(TieredPricing? tiered)
    {
        if (tiered is null)
        {
            throw new InvalidOperationException("usageTiered is nil");
        }

        var tiers = tiered.Tiers ?? [];
        if (tiers.Count == 0)
        {
            throw new InvalidOperationException("tiers is required");
        }

        var lastIndex = tiers.Count - 1;
        for (var index = 0; index < tiers.Count; index++)
        {
            if (index == lastIndex)
            {
                if (tiers[index]?.UpTo is not null)
                {
                    throw new InvalidOperationException(
                        $"tiers[{index}].upTo must be null");
                }

                continue;
            }

            if (tiers[index]?.UpTo is null)
            {
                throw new InvalidOperationException(
                    $"tiers[{index}].upTo is required");
            }
        }
    }

    private static void ValidateSchedule(PriceSchedule? schedule)
    {
        if (schedule is null)
        {
            return;
        }

        if (schedule.Timezone.Length == 0)
        {
            throw new InvalidOperationException("schedule timezone is required");
        }

        if (!IsValidTimeZone(schedule.Timezone))
        {
            // time.LoadLocation's error text is discarded by the reference.
            throw new InvalidOperationException(
                "invalid timezone: " + schedule.Timezone);
        }

        var overrides = schedule.Overrides ?? [];
        if (overrides.Count == 0)
        {
            throw new InvalidOperationException("schedule overrides is required");
        }

        for (var index = 0; index < overrides.Count; index++)
        {
            try
            {
                ValidateOverride(overrides[index]);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"overrides[{index}]: {ex.Message}", ex);
            }
        }
    }

    private static void ValidateOverride(PriceOverride? priceOverride)
    {
        if (priceOverride is null)
        {
            return;
        }

        try
        {
            ValidateWhen(priceOverride.When);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("when: " + ex.Message, ex);
        }

        var items = priceOverride.Items ?? [];
        if (items.Count == 0)
        {
            throw new InvalidOperationException("items is required");
        }

        for (var index = 0; index < items.Count; index++)
        {
            try
            {
                ValidateItem(items[index]);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"items[{index}]: {ex.Message}", ex);
            }
        }
    }

    private static void ValidateWhen(OverrideWhen? when)
    {
        if (when is null)
        {
            throw new InvalidOperationException("when is required");
        }

        if (when.DailyTime is null
            && (when.Weekdays?.Count ?? 0) == 0
            && when.DateRange is null)
        {
            throw new InvalidOperationException(
                "at least one condition (dailyTime, weekdays, or dateRange) is required");
        }

        if (when.DailyTime is not null)
        {
            try
            {
                ValidateDailyTime(when.DailyTime.Start, "start");
                ValidateDailyTime(when.DailyTime.End, "end");
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("dailyTime: " + ex.Message, ex);
            }
        }

        for (var index = 0; index < (when.Weekdays?.Count ?? 0); index++)
        {
            var day = when.Weekdays![index];
            if (day is < 1 or > 7)
            {
                throw new InvalidOperationException(
                    $"weekdays[{index}] must be between 1 and 7, got {day.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (when.DateRange is not null)
        {
            try
            {
                ValidateDateRange(when.DateRange);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("dateRange: " + ex.Message, ex);
            }
        }
    }

    /// <summary>
    /// Port of validateDailyTime: exactly five bytes with a colon in the middle.
    /// The reference derives hour/minute with byte arithmetic and never checks
    /// that the other positions are digits, so this keeps the same acceptance
    /// set (and error order).
    /// </summary>
    private static void ValidateDailyTime(string? text, string field)
    {
        var value = text ?? string.Empty;
        if (value.Length != 5 || value[2] != ':')
        {
            throw new InvalidOperationException(
                field + ": invalid time format " + GoCast.GoQuote(value)
                + ", expected \"HH:mm\"");
        }

        var hour = ((value[0] - '0') * 10) + (value[1] - '0');
        var minute = ((value[3] - '0') * 10) + (value[4] - '0');

        if (hour is < 0 or > 23)
        {
            throw new InvalidOperationException(
                field + ": hour must be between 0 and 23, got " + hour.ToString(CultureInfo.InvariantCulture));
        }

        if (minute is < 0 or > 59)
        {
            throw new InvalidOperationException(
                field + ": minute must be between 0 and 59, got " + minute.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void ValidateDateRange(DateRange range)
    {
        if (range.Start.Length == 0)
        {
            throw new InvalidOperationException("start is required");
        }

        if (range.End.Length == 0)
        {
            throw new InvalidOperationException("end is required");
        }

        // time.Parse("2006-01-02", ...): zero-padded month/day, valid calendar.
        if (!DateOnly.TryParseExact(
                range.Start,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var start))
        {
            throw new InvalidOperationException(
                "invalid start date format: " + range.Start);
        }

        if (!DateOnly.TryParseExact(
                range.End,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var end))
        {
            throw new InvalidOperationException(
                "invalid end date format: " + range.End);
        }

        if (end < start)
        {
            throw new InvalidOperationException(
                "end date must be after or equal to start date");
        }
    }

    private static bool IsValidTimeZone(string timezone)
    {
        // Go's LoadLocation resolves "Local" to the process zone; .NET has no
        // such id. Every IANA id shares the same lookup result in both.
        if (string.Equals(timezone, "Local", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // objects.ModelPrice.Equals family (price.go:48-664). Null receivers fall
    // back to reference identity, exactly like the Go nil checks.
    // ---------------------------------------------------------------------

    public static bool ModelPriceEquals(ModelPrice? left, ModelPrice? right)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        var leftItems = left.Items ?? [];
        var rightItems = right.Items ?? [];
        if (leftItems.Count != rightItems.Count)
        {
            return false;
        }

        for (var index = 0; index < leftItems.Count; index++)
        {
            if (!ItemEquals(leftItems[index], rightItems[index]))
            {
                return false;
            }
        }

        if ((left.Schedule is null) != (right.Schedule is null))
        {
            return false;
        }

        return left.Schedule is null || ScheduleEquals(left.Schedule, right.Schedule);
    }

    public static bool ItemEquals(ModelPriceItem? left, ModelPriceItem? right)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        if (!string.Equals(left.ItemCode, right.ItemCode, StringComparison.Ordinal))
        {
            return false;
        }

        if (!PricingEquals(left.Pricing, right.Pricing))
        {
            return false;
        }

        var leftVariants = left.PromptWriteCacheVariants ?? [];
        var rightVariants = right.PromptWriteCacheVariants ?? [];
        if (leftVariants.Count != rightVariants.Count)
        {
            return false;
        }

        for (var index = 0; index < leftVariants.Count; index++)
        {
            if (!VariantEquals(leftVariants[index], rightVariants[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool VariantEquals(
        PromptWriteCacheVariant? left,
        PromptWriteCacheVariant? right)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        return string.Equals(left.VariantCode, right.VariantCode, StringComparison.Ordinal)
            && PricingEquals(left.Pricing, right.Pricing);
    }

    private static bool PricingEquals(Pricing? left, Pricing? right)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        if (!string.Equals(left.Mode, right.Mode, StringComparison.Ordinal))
        {
            return false;
        }

        switch (left.Mode)
        {
            case PricingModes.FlatFee:
                if ((left.FlatFee is null) != (right.FlatFee is null))
                {
                    return false;
                }

                return left.FlatFee is null || left.FlatFee == right.FlatFee;
            case PricingModes.UsagePerUnit:
                if ((left.UsagePerUnit is null) != (right.UsagePerUnit is null))
                {
                    return false;
                }

                return left.UsagePerUnit is null || left.UsagePerUnit == right.UsagePerUnit;
            case PricingModes.Tiered:
            case PricingModes.Volume:
                return TieredEquals(left.UsageTiered, right.UsageTiered);
            default:
                return true;
        }
    }

    private static bool TieredEquals(TieredPricing? left, TieredPricing? right)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        var leftTiers = left.Tiers ?? [];
        var rightTiers = right.Tiers ?? [];
        if (leftTiers.Count != rightTiers.Count)
        {
            return false;
        }

        for (var index = 0; index < leftTiers.Count; index++)
        {
            var leftTier = leftTiers[index];
            var rightTier = rightTiers[index];
            if ((leftTier is null) != (rightTier is null))
            {
                return false;
            }

            if (leftTier is null)
            {
                continue;
            }

            if (leftTier.UpTo != rightTier!.UpTo)
            {
                return false;
            }

            if (leftTier.PricePerUnit != rightTier.PricePerUnit)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ScheduleEquals(PriceSchedule? left, PriceSchedule? right)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        if (!string.Equals(left.Timezone, right.Timezone, StringComparison.Ordinal))
        {
            return false;
        }

        var leftOverrides = left.Overrides ?? [];
        var rightOverrides = right.Overrides ?? [];
        if (leftOverrides.Count != rightOverrides.Count)
        {
            return false;
        }

        for (var index = 0; index < leftOverrides.Count; index++)
        {
            if (!OverrideEquals(leftOverrides[index], rightOverrides[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool OverrideEquals(PriceOverride? left, PriceOverride? right)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            || left.Priority != right.Priority)
        {
            return false;
        }

        if (!WhenEquals(left.When, right.When))
        {
            return false;
        }

        var leftItems = left.Items ?? [];
        var rightItems = right.Items ?? [];
        if (leftItems.Count != rightItems.Count)
        {
            return false;
        }

        for (var index = 0; index < leftItems.Count; index++)
        {
            if (!ItemEquals(leftItems[index], rightItems[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool WhenEquals(OverrideWhen? left, OverrideWhen? right)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        if ((left.DailyTime is null) != (right.DailyTime is null))
        {
            return false;
        }

        if (left.DailyTime is not null
            && (!string.Equals(left.DailyTime.Start, right.DailyTime!.Start, StringComparison.Ordinal)
                || !string.Equals(left.DailyTime.End, right.DailyTime!.End, StringComparison.Ordinal)))
        {
            return false;
        }

        var leftWeekdays = left.Weekdays ?? [];
        var rightWeekdays = right.Weekdays ?? [];
        if (leftWeekdays.Count != rightWeekdays.Count)
        {
            return false;
        }

        for (var index = 0; index < leftWeekdays.Count; index++)
        {
            if (leftWeekdays[index] != rightWeekdays[index])
            {
                return false;
            }
        }

        if ((left.DateRange is null) != (right.DateRange is null))
        {
            return false;
        }

        if (left.DateRange is not null
            && (left.DateRange.Start != right.DateRange!.Start
                || left.DateRange.End != right.DateRange!.End))
        {
            return false;
        }

        return true;
    }
}
