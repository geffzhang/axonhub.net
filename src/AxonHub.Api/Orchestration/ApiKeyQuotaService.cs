using System.Globalization;
using System.Text.Json;
using AxonHub.Api.Billing;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>Port of biz.QuotaWindow (internal/server/biz/quota.go:18-22).</summary>
public sealed record QuotaWindow(DateTime? Start, DateTime? End, bool EndInclusive);

/// <summary>Port of biz.QuotaUsage (internal/server/biz/quota.go:24-28).</summary>
public sealed record QuotaUsage(long RequestCount, long TotalTokens, decimal TotalCost);

/// <summary>Port of biz.QuotaCheckResult (internal/server/biz/quota.go:30-34).</summary>
public sealed record QuotaCheckResult(bool Allowed, string Message, QuotaWindow Window);

/// <summary>
/// Read side of <c>biz.QuotaService.CheckAPIKeyQuota</c> used by the
/// orchestrator's enforceQuota middleware.
/// </summary>
public interface IApiKeyQuotaChecker
{
    Task<QuotaCheckResult> CheckApiKeyQuotaAsync(
        long apiKeyId,
        ApiKeyQuotaModel quota,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of biz.QuotaService.CheckAPIKeyQuota / quotaWindow / requestCount /
/// usageAgg (internal/server/biz/quota.go:50-395) for the API-key quota gate.
/// </summary>
/// <remarks>
/// The reference reads the timezone through <c>SystemService.TimeLocation</c>,
/// which caches the parsed location until the general settings change; the port
/// reads the row per request (difference C186). The aggregate scans
/// <c>SUM(total_cost)</c> as a float and converts it with
/// <c>decimal.NewFromFloat</c>; the port performs the same shortest-round-trip
/// conversion.
/// </remarks>
public sealed class ApiKeyQuotaService(
    IUsageLogStore usageLogs,
    ISystemStore systems,
    TimeProvider timeProvider) : IApiKeyQuotaChecker
{
    /// <summary>biz.SystemKeyGeneralSettings (internal/server/biz/system.go:96).</summary>
    public const string GeneralSettingsKey = "system_general_settings";

    public async Task<QuotaCheckResult> CheckApiKeyQuotaAsync(
        long apiKeyId,
        ApiKeyQuotaModel quota,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quota);

        var location = await ReadTimeZoneAsync(cancellationToken).ConfigureAwait(false);
        var window = ApiKeyQuotaWindows.Compute(timeProvider.GetUtcNow(), quota.Period, location);

        if (quota.Requests is { } requestLimit)
        {
            var requestCount = await usageLogs
                .CountByApiKeyAsync(
                    apiKeyId,
                    window.Start,
                    window.End,
                    window.EndInclusive,
                    cancellationToken)
                .ConfigureAwait(false);

            if (requestCount >= requestLimit)
            {
                return new QuotaCheckResult(
                    false,
                    FormattableString.Invariant(
                        $"requests quota exceeded: {requestCount}/{requestLimit}"),
                    window);
            }
        }

        if (quota.TotalTokens is null && quota.Cost is null)
        {
            return new QuotaCheckResult(true, string.Empty, window);
        }

        var usage = await usageLogs
            .AggregateByApiKeyAsync(
                apiKeyId,
                window.Start,
                window.End,
                window.EndInclusive,
                needTokens: quota.TotalTokens is not null,
                needCost: quota.Cost is not null,
                cancellationToken)
            .ConfigureAwait(false);

        if (quota.TotalTokens is { } tokenLimit && usage.TotalTokens >= tokenLimit)
        {
            return new QuotaCheckResult(
                false,
                FormattableString.Invariant(
                    $"total_tokens quota exceeded: {usage.TotalTokens}/{tokenLimit}"),
                window);
        }

        if (quota.Cost is { } costLimit)
        {
            // usageAggResult.TotalCost is the decimal.NewFromFloat conversion of
            // the float SUM(total_cost) scan; the store returns the raw float.
            var totalCost = GoFloatDecimal.FromFloat(usage.TotalCost);
            if (totalCost >= costLimit)
            {
                return new QuotaCheckResult(
                    false,
                    "cost quota exceeded: " + GoDecimal.Format(totalCost)
                        + "/" + GoDecimal.Format(costLimit),
                    window);
            }
        }

        return new QuotaCheckResult(true, string.Empty, window);
    }

    /// <summary>
    /// Port of SystemService.TimeLocation (internal/server/biz/system.go:1499-1542)
    /// minus the process-wide cache: a missing row, a lookup failure, malformed
    /// JSON, an empty timezone or an unknown zone all resolve to UTC.
    /// </summary>
    private async Task<TimeZoneInfo> ReadTimeZoneAsync(CancellationToken cancellationToken)
    {
        SystemRecord? row;
        try
        {
            row = await systems.GetByKeyAsync(GeneralSettingsKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return TimeZoneInfo.Utc;
        }

        if (row is null)
        {
            // GeneralSettings returns defaultGeneralSettings {Timezone: "UTC"}.
            return TimeZoneInfo.Utc;
        }

        string timezone;
        try
        {
            timezone = GeneralSettingsTimezone(row.Value);
        }
        catch (JsonException)
        {
            return TimeZoneInfo.Utc;
        }

        if (timezone.Length == 0)
        {
            return TimeZoneInfo.Utc;
        }

        // time.LoadLocation("Local") returns the process's local location.
        if (string.Equals(timezone, "Local", StringComparison.Ordinal))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Port of <c>json.Unmarshal</c> into biz.SystemGeneralSettings
    /// (internal/server/biz/system.go:138-142): both fields carry string types,
    /// a JSON null document leaves the zero value, and a non-string member makes
    /// the whole document invalid.
    /// </summary>
    internal static string GeneralSettingsTimezone(string? json)
    {
        using var document = JsonDocument.Parse(json ?? string.Empty);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal " + root.ValueKind
                + " into Go value of type biz.SystemGeneralSettings");
        }

        // Decode both known members so a mistyped currency_code invalidates the
        // document exactly like the reference's struct unmarshal.
        _ = ReadStringMember(root, "currency_code");
        return ReadStringMember(root, "timezone");
    }

    private static string ReadStringMember(JsonElement root, string name)
    {
        JsonElement? matched = null;
        if (root.TryGetProperty(name, out var exact))
        {
            matched = exact;
        }
        else
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    matched = property.Value;
                    break;
                }
            }
        }

        if (matched is null)
        {
            return string.Empty;
        }

        return matched.Value.ValueKind switch
        {
            JsonValueKind.String => matched.Value.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => throw new JsonException(
                "json: cannot unmarshal " + matched.Value.ValueKind
                + " into Go struct field SystemGeneralSettings." + name + " of type string"),
        };
    }
}

/// <summary>
/// Port of biz.quotaWindow (internal/server/biz/quota.go:193-256): resolves the
/// all-time, past-duration or calendar-duration window. Calendar boundaries are
/// local midnights in the system timezone, converted back to UTC; the end is
/// exclusive there, while all-time and past-duration include both ends.
/// </summary>
public static class ApiKeyQuotaWindows
{
    public static QuotaWindow Compute(
        DateTimeOffset now,
        ApiKeyQuotaPeriodModel period,
        TimeZoneInfo? location = null)
    {
        ArgumentNullException.ThrowIfNull(period);

        location ??= TimeZoneInfo.Utc;
        var nowUtc = now.UtcDateTime;

        switch (period.Type)
        {
            case "all_time":
                return new QuotaWindow(null, nowUtc, EndInclusive: true);

            case "past_duration":
            {
                var past = period.PastDuration
                    ?? throw new InvalidOperationException("pastDuration is required");

                if (past.Value <= 0)
                {
                    throw new InvalidOperationException("pastDuration.value must be positive");
                }

                // Go computes the duration in int64 nanoseconds and wraps on
                // overflow; mirror that so an extreme value cannot crash the
                // process where Go silently produces a far-off window.
                long nanoseconds;
                switch (past.Unit)
                {
                    case "minute":
                        nanoseconds = unchecked(past.Value * 60_000_000_000L);
                        break;
                    case "hour":
                        nanoseconds = unchecked(past.Value * 3_600_000_000_000L);
                        break;
                    case "day":
                        nanoseconds = unchecked(past.Value * 24L * 3_600_000_000_000L);
                        break;
                    default:
                        throw new InvalidOperationException(
                            "unknown pastDuration.unit: " + past.Unit);
                }

                var start = nowUtc.AddTicks(-(nanoseconds / 100));
                return new QuotaWindow(start, nowUtc, EndInclusive: true);
            }

            case "calendar_duration":
            {
                var calendar = period.CalendarDuration
                    ?? throw new InvalidOperationException("calendarDuration is required");

                var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, location);

                switch (calendar.Unit)
                {
                    case "day":
                    {
                        var startLocal = new DateTime(
                            localNow.Year,
                            localNow.Month,
                            localNow.Day,
                            0,
                            0,
                            0,
                            DateTimeKind.Unspecified);
                        var endLocal = startLocal.AddDays(1);
                        return new QuotaWindow(
                            LocalToUtc(startLocal, location),
                            LocalToUtc(endLocal, location),
                            EndInclusive: false);
                    }

                    case "month":
                    {
                        var startLocal = new DateTime(
                            localNow.Year,
                            localNow.Month,
                            1,
                            0,
                            0,
                            0,
                            DateTimeKind.Unspecified);
                        var endLocal = startLocal.AddMonths(1);
                        return new QuotaWindow(
                            LocalToUtc(startLocal, location),
                            LocalToUtc(endLocal, location),
                            EndInclusive: false);
                    }

                    default:
                        throw new InvalidOperationException(
                            "unknown calendarDuration.unit: " + calendar.Unit);
                }
            }

            default:
                throw new InvalidOperationException("unknown period.type: " + period.Type);
        }
    }

    /// <summary>
    /// Converts a wall-clock time in <paramref name="location"/> to UTC. A local
    /// midnight inside a DST spring-forward gap is advanced to the first valid
    /// instant; an ambiguous midnight uses the pre-transition (first) offset,
    /// like Go's time.Date lookup.
    /// </summary>
    private static DateTime LocalToUtc(DateTime local, TimeZoneInfo location)
    {
        if (location.IsInvalidTime(local))
        {
            do
            {
                local = local.AddMinutes(1);
            }
            while (location.IsInvalidTime(local));
        }

        var offset = location.GetUtcOffset(local);
        if (location.IsAmbiguousTime(local))
        {
            offset = location.GetAmbiguousTimeOffsets(local).Max();
        }

        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }
}

/// <summary>
/// Port of the numeric half of shopspring decimal.NewFromFloat: NaN and the
/// infinities become zero; every other value round-trips through Go's shortest
/// float formatting before the decimal parse. The .NET decimal domain is
/// narrower than shopspring's (C046).
/// </summary>
public static class GoFloatDecimal
{
    public static decimal FromFloat(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0m;
        }

        return decimal.Parse(
            value.ToString("R", CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
    }
}
