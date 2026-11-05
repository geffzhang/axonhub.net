namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of provider_quota.QuotaLimitType (internal/server/biz/provider_quota/types.go):
/// the limit dimension a request consumes.
/// </summary>
public static class QuotaLimitTypes
{
    public const string Image = "image";
    public const string Token = "token";
    public const string SubscriptionCycle = "subscription_cycle";
}

/// <summary>
/// Well-known quota window labels (provider_quota/types.go:QuotaWindow*). Only
/// the two balance windows affect routing; the rest are display labels that
/// still tell limits apart inside a status.
/// </summary>
public static class QuotaWindows
{
    public const string FiveHours = "5h";
    public const string SevenDays = "7d";
    public const string ThirtyDays = "30d";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Primary = "primary";
    public const string Secondary = "secondary";
    public const string PayAsYouGo = "pay_as_you_go";
    public const string Credits = "credits";
    public const string Cycle = "cycle";
}

/// <summary>
/// Port of provider_quota.QuotaLimitStatus (types.go:69-104). The routing gate
/// reads <see cref="Type"/>, <see cref="Status"/>, <see cref="UsageRatio"/>,
/// <see cref="AvailabilityGroup"/>, <see cref="Window"/>, <see cref="PeriodStart"/>
/// and <see cref="NextResetAt"/>; the remaining fields are carried for the
/// provider quota display surface.
/// </summary>
public sealed class QuotaLimitStatus
{
    public string Type { get; set; } = QuotaLimitTypes.Token;

    /// <summary><c>available</c>, <c>warning</c>, <c>exhausted</c> or <c>unknown</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public double UsageRatio { get; set; }

    public bool Ready { get; set; }

    public DateTimeOffset? NextResetAt { get; set; }

    /// <summary>
    /// Alternative capacity sources sharing this label are aggregated with OR
    /// semantics for routing decisions (provider_quota/types.go:82-84).
    /// </summary>
    public string? AvailabilityGroup { get; set; }

    public string? Window { get; set; }

    public string? Account { get; set; }

    public DateTimeOffset? PeriodStart { get; set; }

    public double? PeriodCost { get; set; }

    public double? PeriodQuota { get; set; }
}

/// <summary>
/// Port of biz.QuotaChannelStatus (internal/server/biz/provider_quota.go:128-133):
/// the cached status a <see cref="IProviderQuotaStatusProvider"/> reports for one
/// channel.
/// </summary>
public sealed class QuotaChannelStatus
{
    public string ProviderType { get; set; } = string.Empty;

    /// <summary><c>available</c>, <c>warning</c>, <c>exhausted</c> or <c>unknown</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public bool Ready { get; set; }

    public IReadOnlyList<QuotaLimitStatus> Limits { get; set; } = [];
}

/// <summary>
/// Port of provider_quota.RoutingState (internal/server/biz/provider_quota/routing.go:8-15).
/// </summary>
public static class QuotaRoutingStates
{
    public const string Open = "open";
    public const string StickyOnly = "sticky_only";
    public const string Exhausted = "exhausted";
    public const string Unknown = "unknown";
}

/// <summary>
/// Port of provider_quota/routing.go:13-241. Only the routing evaluation is
/// modelled; <c>EffectiveStatus</c> belongs to the quota display surface.
/// </summary>
public static class ProviderQuotaRouting
{
    private const string RoutingStatusExhausted = "exhausted";
    private const string RoutingStatusAvailable = "available";
    private const string RoutingStatusWarning = "warning";

    /// <summary>Port of provider_quota.RequestModality (types.go:249-254).</summary>
    public static string RequestModality(bool isImageRequest)
        => isImageRequest ? QuotaLimitTypes.Image : QuotaLimitTypes.Token;

    /// <summary>Port of provider_quota.IsBalanceLimit.</summary>
    public static bool IsBalanceLimit(QuotaLimitStatus limit)
    {
        ArgumentNullException.ThrowIfNull(limit);

        return limit.Window == QuotaWindows.PayAsYouGo || limit.Window == QuotaWindows.Credits;
    }

    /// <summary>
    /// Port of provider_quota.ElapsedRatio: the fraction of the window that has
    /// elapsed, or false when the limit has no live window to compare against.
    /// </summary>
    public static (double Ratio, bool Ok) ElapsedRatio(QuotaLimitStatus limit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(limit);

        if (limit.PeriodStart is not { } start
            || limit.NextResetAt is not { } reset
            || start >= now
            || now >= reset
            || reset <= start)
        {
            return (0, false);
        }

        var ratio = (now - start) / (reset - start);
        if (ratio < 0)
        {
            return (0, true);
        }

        if (ratio > 1)
        {
            return (1, true);
        }

        return (ratio, true);
    }

    /// <summary>
    /// Port of provider_quota.EvaluateQuotaRouting (routing.go:42-118). Returns
    /// the routing state and the reason string the gate logs.
    /// </summary>
    public static (string State, string Reason) EvaluateQuotaRouting(
        IReadOnlyList<QuotaLimitStatus>? limits,
        string? overallStatus,
        string limitType,
        DateTimeOffset now)
    {
        if (overallStatus == RoutingStatusExhausted)
        {
            return (QuotaRoutingStates.Exhausted, string.Empty);
        }

        var groups = RoutingGroups(limits, limitType);
        if (groups.Count == 0)
        {
            if (overallStatus is RoutingStatusAvailable or RoutingStatusWarning)
            {
                return (QuotaRoutingStates.Open, string.Empty);
            }

            return (QuotaRoutingStates.Unknown, string.Empty);
        }

        var allGroupsExhausted = true;
        var hasWindow = false;
        var allWindowsExhausted = true;
        var balanceAvailable = false;
        foreach (var group in groups)
        {
            // Ungrouped windows start available and any exhausted limit makes
            // them unavailable; grouped availability starts false and any
            // non-exhausted limit makes it available.
            var groupAvailable = group.Count > 0 && string.IsNullOrEmpty(group[0].AvailabilityGroup);
            foreach (var limit in group)
            {
                if (string.IsNullOrEmpty(group[0].AvailabilityGroup))
                {
                    if (RoutingLimitExhausted(limit))
                    {
                        groupAvailable = false;
                    }
                }
                else if (!RoutingLimitExhausted(limit))
                {
                    groupAvailable = true;
                }

                if (IsBalanceLimit(limit))
                {
                    if (!RoutingLimitExhausted(limit))
                    {
                        balanceAvailable = true;
                    }

                    continue;
                }

                hasWindow = true;
                if (!RoutingLimitExhausted(limit))
                {
                    allWindowsExhausted = false;
                }
            }

            if (groupAvailable)
            {
                allGroupsExhausted = false;
            }
        }

        if (allGroupsExhausted)
        {
            return (QuotaRoutingStates.Exhausted, string.Empty);
        }

        if (hasWindow && allWindowsExhausted && balanceAvailable)
        {
            return (QuotaRoutingStates.StickyOnly, "window_exhausted_balance_fallback");
        }

        foreach (var group in groups)
        {
            foreach (var limit in group)
            {
                if (IsBalanceLimit(limit) || RoutingLimitExhausted(limit))
                {
                    continue;
                }

                var (elapsedRatio, ok) = ElapsedRatio(limit, now);
                if (ok && limit.UsageRatio > elapsedRatio)
                {
                    return (QuotaRoutingStates.StickyOnly, "window_pressure");
                }
            }
        }

        return (QuotaRoutingStates.Open, string.Empty);
    }

    private static bool RoutingLimitExhausted(QuotaLimitStatus limit)
        => limit.Status == RoutingStatusExhausted || limit.UsageRatio >= 1;

    /// <summary>Port of provider_quota.routingGroups (routing.go:120-168).</summary>
    private static List<List<QuotaLimitStatus>> RoutingGroups(
        IReadOnlyList<QuotaLimitStatus>? limits,
        string limitType)
    {
        var sources = limits ?? [];
        var groupNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var limit in sources)
        {
            if (limit.Type == limitType && !string.IsNullOrEmpty(limit.AvailabilityGroup))
            {
                groupNames.Add(limit.AvailabilityGroup);
            }
        }

        var grouped = new Dictionary<string, List<QuotaLimitStatus>>(StringComparer.Ordinal);
        var ungroupedWindows = new List<QuotaLimitStatus>();
        var ungroupedBalances = new List<QuotaLimitStatus>();
        foreach (var limit in sources)
        {
            var groupName = limit.AvailabilityGroup;
            var inGroup = !string.IsNullOrEmpty(groupName) && groupNames.Contains(groupName);
            if (limit.Type != limitType && !inGroup)
            {
                continue;
            }

            if (inGroup)
            {
                if (!grouped.TryGetValue(groupName!, out var group))
                {
                    group = [];
                    grouped[groupName!] = group;
                }

                group.Add(limit);
                continue;
            }

            if (IsBalanceLimit(limit))
            {
                ungroupedBalances.Add(limit);
            }
            else
            {
                ungroupedWindows.Add(limit);
            }
        }

        var groups = new List<List<QuotaLimitStatus>>(grouped.Count + 2);
        groups.AddRange(grouped.Values);
        if (ungroupedWindows.Count > 0)
        {
            groups.Add(ungroupedWindows);
        }

        if (ungroupedBalances.Count > 0)
        {
            groups.Add(ungroupedBalances);
        }

        return groups;
    }
}
