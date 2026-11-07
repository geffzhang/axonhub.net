using System.Text.Json.Serialization;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Overall quota status values (providerquotastatus.Status in the reference).
/// </summary>
public static class ProviderQuotaStatuses
{
    public const string Available = "available";
    public const string Warning = "warning";
    public const string Exhausted = "exhausted";
    public const string Unknown = "unknown";

    /// <summary>Port of provider_quota.IsReadyStatus.</summary>
    public static bool IsReadyStatus(string? status)
        => status is Available or Warning;
}

/// <summary>Port of provider_quota.Reset (types.go:23-30).</summary>
public sealed class QuotaReset
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("grantedAt")]
    [JsonConverter(typeof(GoNullableRfc3339NanoConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? GrantedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    [JsonConverter(typeof(GoNullableRfc3339NanoConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }
}

/// <summary>
/// Port of provider_quota.ResetList (types.go:33-37). Go marshals the struct
/// field-by-field, so <c>resets</c> is always present (a nil slice as
/// <c>null</c>) while <c>error</c> is omitted when empty.
/// </summary>
public sealed class ResetList
{
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    /// <summary>Nil in Go until a provider fills the list; JSON null.</summary>
    [JsonPropertyName("resets")]
    public List<QuotaReset>? Resets { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>
/// Port of provider_quota.QuotaData (types.go:107-115): the unified shape every
/// checker returns.
/// </summary>
public sealed class QuotaData
{
    public string Status { get; set; } = string.Empty;

    public string ProviderType { get; set; } = string.Empty;

    public Dictionary<string, object?> RawData { get; set; } = [];

    public DateTimeOffset? NextResetAt { get; set; }

    public bool Ready { get; set; }

    public List<QuotaLimitStatus> Limits { get; set; } = [];

    public ResetList? Resets { get; set; }
}

/// <summary>Port of provider_quota.QuotaChecker (types.go:14-19).</summary>
public interface IQuotaChecker
{
    /// <summary>Makes a minimal provider request and returns parsed quota data.</summary>
    Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken);

    /// <summary>Whether this checker handles the channel.</summary>
    bool SupportsChannel(ChannelRecord channel);
}

/// <summary>
/// Port of provider_quota.Resetter (types.go:41-44): the optional capability of
/// providers that can list and consume provider-managed quota resets.
/// </summary>
public interface IQuotaResetter
{
    Task<ResetList> ListResetsAsync(ChannelRecord channel, CancellationToken cancellationToken);

    Task ResetAsync(ChannelRecord channel, CancellationToken cancellationToken);
}

/// <summary>
/// Port of provider_quota.ErrInvalidCredentials: credentials that can no longer
/// produce a trustworthy quota response, so cached data must be discarded.
/// </summary>
public class InvalidProviderQuotaCredentialsException()
    : Exception("provider quota credentials are invalid");

/// <summary>Port of provider_quota.ErrResetUnsupported.</summary>
public class ProviderQuotaResetUnsupportedException()
    : Exception("provider quota reset is not supported");

/// <summary>
/// Port of provider_quota/normalization.go, provider_quota/types.go helpers and
/// provider_quota/routing.go:EffectiveStatus. These functions define the
/// checker boundary contract and the per-limit-type display status.
/// </summary>
public static class ProviderQuotaData
{
    /// <summary>Port of WarningThresholdRatio.</summary>
    public const double WarningThresholdRatio = 0.8;

    /// <summary>Display-only limit types (normalization.go:41-46).</summary>
    private static readonly HashSet<string> DisplayOnlyLimitTypes = [QuotaLimitTypes.Search];

    /// <summary>Port of NormalizeQuotaData at an explicit instant.</summary>
    public static QuotaData Normalize(QuotaData data, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);

        data.Limits = NormalizeLimits(data.Limits, now);

        var status = NormalizeOverallStatus(data.Status, data.Limits);

        DateTimeOffset? nextResetAt = null;
        if (data.NextResetAt is { } explicitReset && explicitReset > now)
        {
            nextResetAt = explicitReset;
        }

        foreach (var limit in data.Limits)
        {
            if (IsDisplayOnlyLimit(limit))
            {
                continue;
            }

            if (limit.NextResetAt is { } limitReset
                && limitReset > now
                && (nextResetAt is null || limitReset < nextResetAt))
            {
                nextResetAt = limitReset;
            }
        }

        data.Status = status;
        data.NextResetAt = nextResetAt;
        data.Ready = ProviderQuotaStatuses.IsReadyStatus(status);
        return data;
    }

    /// <summary>Port of isDisplayOnlyLimit: a display-only type in no group.</summary>
    public static bool IsDisplayOnlyLimit(QuotaLimitStatus limit)
    {
        ArgumentNullException.ThrowIfNull(limit);

        return DisplayOnlyLimitTypes.Contains(limit.Type)
            && string.IsNullOrEmpty(limit.AvailabilityGroup);
    }

    /// <summary>Port of normalizeOverallQuotaStatus.</summary>
    public static string NormalizeOverallStatus(string? explicitStatus, IReadOnlyList<QuotaLimitStatus>? limits)
    {
        var status = NormalizeStatus(explicitStatus);
        if (status == ProviderQuotaStatuses.Exhausted)
        {
            return status;
        }

        var bestReadyStatus = string.Empty;
        if (ProviderQuotaStatuses.IsReadyStatus(status))
        {
            bestReadyStatus = status;
        }

        var hasExhausted = false;
        foreach (var limit in limits ?? [])
        {
            if (IsDisplayOnlyLimit(limit))
            {
                continue;
            }

            if (ProviderQuotaStatuses.IsReadyStatus(limit.Status))
            {
                if (bestReadyStatus.Length == 0
                    || StatusRank(limit.Status) > StatusRank(bestReadyStatus))
                {
                    bestReadyStatus = limit.Status;
                }
            }
            else if (limit.Status == ProviderQuotaStatuses.Exhausted)
            {
                hasExhausted = true;
            }
        }

        if (bestReadyStatus.Length > 0)
        {
            return bestReadyStatus;
        }

        return hasExhausted ? ProviderQuotaStatuses.Exhausted : ProviderQuotaStatuses.Unknown;
    }

    /// <summary>Port of normalizeQuotaLimits.</summary>
    public static List<QuotaLimitStatus> NormalizeLimits(
        IReadOnlyList<QuotaLimitStatus>? limits,
        DateTimeOffset now)
    {
        if (limits is null || limits.Count == 0)
        {
            return [];
        }

        var normalized = new List<QuotaLimitStatus>(limits.Count);
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var source in limits)
        {
            var limit = CloneLimit(source);
            if (limit.Type.Length == 0
                || string.IsNullOrEmpty(limit.Window)
                || !IsFiniteRatio(limit.UsageRatio)
                || limit.UsageRatio < 0)
            {
                continue;
            }

            limit.UsageRatio = Math.Min(limit.UsageRatio, 1);
            limit.Status = NormalizeStatus(limit.Status);
            if (limit.UsageRatio >= 1)
            {
                limit.Status = ProviderQuotaStatuses.Exhausted;
            }

            limit.Ready = ProviderQuotaStatuses.IsReadyStatus(limit.Status);
            if (limit.NextResetAt == default(DateTimeOffset))
            {
                limit.NextResetAt = null;
            }

            if (limit.NextResetAt is { } resetAt && resetAt <= now)
            {
                limit.NextResetAt = null;
            }

            if (limit.NextResetAt is not { } liveReset)
            {
                limit.PeriodStart = null;
            }
            else if (limit.PeriodStart is { } periodStart && periodStart >= liveReset)
            {
                limit.PeriodStart = null;
            }

            // Limits of different accounts of one channel share a window, so
            // the account identity stays part of the key.
            var identity = string.Join('\0', new[]
            {
                limit.Type,
                limit.Window ?? string.Empty,
                limit.AvailabilityGroup ?? string.Empty,
                limit.Account ?? string.Empty,
            });
            if (indexes.TryGetValue(identity, out var index))
            {
                MergeLimit(normalized[index], limit);
                continue;
            }

            indexes[identity] = normalized.Count;
            normalized.Add(limit);
        }

        return normalized;
    }

    /// <summary>Port of mergeQuotaLimit.</summary>
    public static void MergeLimit(QuotaLimitStatus existing, QuotaLimitStatus incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);

        if (incoming.UsageRatio > existing.UsageRatio)
        {
            existing.UsageRatio = incoming.UsageRatio;
        }

        if (StatusRank(incoming.Status) > StatusRank(existing.Status))
        {
            existing.Status = incoming.Status;
        }

        if (incoming.NextResetAt is { } incomingReset
            && (existing.NextResetAt is null || incomingReset < existing.NextResetAt))
        {
            existing.NextResetAt = incomingReset;
        }

        if (incoming.PeriodStart is { } incomingStart
            && (existing.PeriodStart is null || incomingStart < existing.PeriodStart))
        {
            existing.PeriodStart = incomingStart;
        }

        if (existing.NextResetAt is not { } existingReset
            || existing.PeriodStart is not { } existingStart
            || existingStart >= existingReset)
        {
            existing.PeriodStart = null;
        }

        existing.Ready = ProviderQuotaStatuses.IsReadyStatus(existing.Status);
    }

    /// <summary>Port of isFiniteRatio.</summary>
    public static bool IsFiniteRatio(double ratio)
        => !double.IsNaN(ratio) && !double.IsInfinity(ratio);

    /// <summary>Port of normalizeQuotaStatus.</summary>
    public static string NormalizeStatus(string? status) => status switch
    {
        ProviderQuotaStatuses.Available => ProviderQuotaStatuses.Available,
        ProviderQuotaStatuses.Warning => ProviderQuotaStatuses.Warning,
        ProviderQuotaStatuses.Exhausted => ProviderQuotaStatuses.Exhausted,
        ProviderQuotaStatuses.Unknown => ProviderQuotaStatuses.Unknown,
        _ => ProviderQuotaStatuses.Unknown,
    };

    /// <summary>Port of normalizedQuotaStatusRank.</summary>
    public static int StatusRank(string? status) => status switch
    {
        ProviderQuotaStatuses.Exhausted => 3,
        ProviderQuotaStatuses.Warning => 2,
        ProviderQuotaStatuses.Available => 1,
        _ => 0,
    };

    /// <summary>
    /// Port of provider_quota.EffectiveStatus (routing.go:163-241): the status
    /// for one limit type. A channel-level exhausted status short-circuits;
    /// otherwise the worst limit outside a group and the best limit inside each
    /// availability group are combined, with OR semantics inside groups.
    /// </summary>
    public static (string Status, bool Ready) EffectiveStatus(
        IReadOnlyList<QuotaLimitStatus>? limits,
        string overallStatus,
        bool overallReady,
        string limitType)
    {
        if (overallStatus == ProviderQuotaStatuses.Exhausted)
        {
            return (ProviderQuotaStatuses.Exhausted, false);
        }

        if (limits is null || limits.Count == 0)
        {
            return (overallStatus, overallReady);
        }

        var groupNames = new HashSet<string>(StringComparer.Ordinal);
        var grouped = new Dictionary<string, List<QuotaLimitStatus>>(StringComparer.Ordinal);

        foreach (var limit in limits)
        {
            if (limit.Type == limitType && !string.IsNullOrEmpty(limit.AvailabilityGroup))
            {
                groupNames.Add(limit.AvailabilityGroup);
            }
        }

        var worstStatus = string.Empty;
        var worstReady = true;
        var found = false;

        foreach (var limit in limits)
        {
            var groupName = limit.AvailabilityGroup;
            if (limit.Type != limitType
                && (string.IsNullOrEmpty(groupName) || !groupNames.Contains(groupName)))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(groupName) && groupNames.Contains(groupName))
            {
                if (!grouped.TryGetValue(groupName, out var group))
                {
                    group = [];
                    grouped[groupName] = group;
                }

                group.Add(limit);
                continue;
            }

            if (!found || StatusRank(limit.Status) > StatusRank(worstStatus))
            {
                worstStatus = limit.Status;
                worstReady = limit.Ready;
                found = true;
            }
            else if (StatusRank(limit.Status) == StatusRank(worstStatus))
            {
                worstReady = worstReady && limit.Ready;
            }
        }

        foreach (var group in grouped.Values)
        {
            var bestStatus = ProviderQuotaStatuses.Unknown;
            var bestReady = false;
            var groupFound = false;
            foreach (var limit in group)
            {
                if (!groupFound
                    || (limit.Ready && !bestReady)
                    || (limit.Ready == bestReady
                        && StatusRank(limit.Status) < StatusRank(bestStatus)))
                {
                    bestStatus = limit.Status;
                    bestReady = limit.Ready;
                    groupFound = true;
                }
                else if (StatusRank(limit.Status) == StatusRank(bestStatus))
                {
                    bestReady = bestReady || limit.Ready;
                }
            }

            if (!found || StatusRank(bestStatus) > StatusRank(worstStatus))
            {
                worstStatus = bestStatus;
                worstReady = bestReady;
                found = true;
            }
            else if (StatusRank(bestStatus) == StatusRank(worstStatus))
            {
                worstReady = worstReady && bestReady;
            }
        }

        return found
            ? (worstStatus, worstReady)
            : (ProviderQuotaStatuses.Unknown, true);
    }

    /// <summary>Port of EstimatePeriodQuota.</summary>
    public static (double Quota, bool Ok) EstimatePeriodQuota(double periodCost, double usageRatio)
    {
        if (periodCost <= 0 || !IsFiniteRatio(periodCost)
            || usageRatio <= 0 || !IsFiniteRatio(usageRatio))
        {
            return (0, false);
        }

        var total = periodCost / usageRatio;
        return IsFiniteRatio(total) ? (total, true) : (0, false);
    }

    /// <summary>Port of QuotaLimitStatus.FillPeriodQuota.</summary>
    public static void FillPeriodQuota(QuotaLimitStatus limit)
    {
        ArgumentNullException.ThrowIfNull(limit);

        limit.PeriodQuota = null;
        if (limit.PeriodCost is not { } periodCost)
        {
            return;
        }

        var (quota, ok) = EstimatePeriodQuota(periodCost, limit.UsageRatio);
        if (ok)
        {
            limit.PeriodQuota = quota;
        }
    }

    /// <summary>Port of QuotaData.FillPeriodQuotas.</summary>
    public static void FillPeriodQuotas(QuotaData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        foreach (var limit in data.Limits)
        {
            FillPeriodQuota(limit);
        }
    }

    /// <summary>Port of NormalizeQuotaWindowLabel.</summary>
    public static string NormalizeQuotaWindowLabel(TimeSpan window) => window switch
    {
        { Ticks: var ticks } when ticks == TimeSpan.FromHours(5).Ticks => QuotaWindows.FiveHours,
        { Ticks: var ticks } when ticks == TimeSpan.FromDays(1).Ticks => QuotaWindows.Daily,
        { Ticks: var ticks } when ticks == TimeSpan.FromDays(7).Ticks => QuotaWindows.SevenDays,
        { Ticks: var ticks } when ticks == TimeSpan.FromDays(30).Ticks => QuotaWindows.ThirtyDays,
        _ => string.Empty,
    };

    /// <summary>Port of PeriodStartFromReset.</summary>
    public static DateTimeOffset? PeriodStartFromReset(DateTimeOffset? nextResetAt, TimeSpan window)
        => nextResetAt is { } reset && window > TimeSpan.Zero ? reset - window : null;

    /// <summary>
    /// Port of PeriodStartFromMonthlyReset: the start of the month-long window
    /// that ends at <paramref name="nextResetAt"/>, clamping the day to the
    /// previous month's last day.
    /// </summary>
    public static DateTimeOffset? PeriodStartFromMonthlyReset(DateTimeOffset? nextResetAt)
    {
        if (nextResetAt is not { } reset)
        {
            return null;
        }

        var previousMonth = reset.AddMonths(-1);
        var lastDayOfPreviousMonth = DateTime.DaysInMonth(previousMonth.Year, previousMonth.Month);
        var day = Math.Min(reset.Day, lastDayOfPreviousMonth);
        return new DateTimeOffset(
            reset.Year,
            reset.Month,
            1,
            reset.Hour,
            reset.Minute,
            reset.Second,
            reset.Millisecond,
            reset.Offset).AddMonths(-1).AddDays(day - 1);
    }

    /// <summary>Port of QuotaLimitStatus.WithWindow (by value in Go).</summary>
    public static QuotaLimitStatus WithWindow(
        QuotaLimitStatus limit,
        string name,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(limit);

        limit.Window = name;
        limit.PeriodStart = PeriodStartFromReset(limit.NextResetAt, window);
        return limit;
    }

    /// <summary>Port of NewTokenLimitStatus.</summary>
    public static QuotaLimitStatus NewTokenLimitStatus(
        string status,
        double usageRatio,
        DateTimeOffset? nextResetAt) => new()
    {
        Type = QuotaLimitTypes.Token,
        Status = status,
        UsageRatio = usageRatio,
        Ready = ProviderQuotaStatuses.IsReadyStatus(status),
        NextResetAt = nextResetAt,
    };

    /// <summary>Port of provider_quota.CloneLimitStatus (provider_quota.go:136-151).</summary>
    public static QuotaLimitStatus CloneLimit(QuotaLimitStatus limit)
    {
        ArgumentNullException.ThrowIfNull(limit);

        return new QuotaLimitStatus
        {
            Type = limit.Type,
            Status = limit.Status,
            UsageRatio = limit.UsageRatio,
            Ready = limit.Ready,
            NextResetAt = limit.NextResetAt,
            AvailabilityGroup = limit.AvailabilityGroup,
            Window = limit.Window,
            Account = limit.Account,
            PeriodStart = limit.PeriodStart,
            PeriodCost = limit.PeriodCost,
            PeriodQuota = limit.PeriodQuota,
        };
    }

    /// <summary>Port of cloneLimits.</summary>
    public static List<QuotaLimitStatus> CloneLimits(IReadOnlyList<QuotaLimitStatus>? limits)
    {
        if (limits is null)
        {
            return [];
        }

        var clones = new List<QuotaLimitStatus>(limits.Count);
        foreach (var limit in limits)
        {
            clones.Add(CloneLimit(limit));
        }

        return clones;
    }
}

/// <summary>Port of provider_quota/model_filter.go.</summary>
public static class ProviderQuotaModelFilter
{
    private const string AntigravityPoolAvailabilityGroupPrefix = "antigravity_pool:";

    /// <summary>Port of antigravityModelPool (antigravity_summary.go:33-42).</summary>
    public static string AntigravityModelPool(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.Contains("gemini", StringComparison.Ordinal))
        {
            return "gemini";
        }

        if (lower.Contains("claude", StringComparison.Ordinal)
            || lower.Contains("gpt", StringComparison.Ordinal)
            || lower.StartsWith("3p-", StringComparison.Ordinal))
        {
            return "claude_gpt";
        }

        return string.Empty;
    }

    /// <summary>
    /// Port of antigravityPoolAvailabilityGroup (antigravity_summary.go:44-49):
    /// limits in the same model pool share an availability group so routing can
    /// map a requested model onto the pool's windows.
    /// </summary>
    public static string AntigravityPoolAvailabilityGroup(string pool)
    {
        ArgumentNullException.ThrowIfNull(pool);

        return pool.Length == 0 ? string.Empty : AntigravityPoolAvailabilityGroupPrefix + pool;
    }

    /// <summary>Port of MatchLimitModel.</summary>
    public static bool MatchLimitModel(QuotaLimitStatus limit, string actualModel)
    {
        ArgumentNullException.ThrowIfNull(limit);

        if (string.IsNullOrEmpty(limit.AvailabilityGroup) || string.IsNullOrEmpty(actualModel))
        {
            return true;
        }

        if (!limit.AvailabilityGroup.StartsWith(
                AntigravityPoolAvailabilityGroupPrefix,
                StringComparison.Ordinal))
        {
            return true;
        }

        var pool = limit.AvailabilityGroup[AntigravityPoolAvailabilityGroupPrefix.Length..];
        var modelPool = AntigravityModelPool(actualModel);
        if (modelPool.Length == 0)
        {
            return true;
        }

        return pool == modelPool;
    }

    /// <summary>
    /// Port of FilterLimitsForModel. Pool-specific AvailabilityGroups are
    /// cleared on the limits that survive so an exhausted window in the
    /// targeted pool triggers exhaustion instead of being OR'd away.
    /// </summary>
    public static List<QuotaLimitStatus> FilterLimitsForModel(
        IReadOnlyList<QuotaLimitStatus>? limits,
        string actualModel)
    {
        if (string.IsNullOrEmpty(actualModel) || limits is null || limits.Count == 0)
        {
            return limits is null ? [] : [.. limits];
        }

        var filtered = new List<QuotaLimitStatus>(limits.Count);
        foreach (var source in limits)
        {
            if (!MatchLimitModel(source, actualModel))
            {
                continue;
            }

            var limit = ProviderQuotaData.CloneLimit(source);
            if (limit.AvailabilityGroup is not null
                && limit.AvailabilityGroup.StartsWith(
                    AntigravityPoolAvailabilityGroupPrefix,
                    StringComparison.Ordinal))
            {
                limit.AvailabilityGroup = string.Empty;
            }

            filtered.Add(limit);
        }

        return filtered;
    }
}

/// <summary>Port of provider_quota/url_detection.go.</summary>
public static class ProviderQuotaUrlDetection
{
    private static readonly (string HostPattern, string ProviderType)[] UrlProviderMap =
    [
        ("api.synthetic.new", "synthetic"),
        ("api.neuralwatt.com", "neuralwatt"),
        ("api.apertis.ai", "apertis"),
        ("hyper.charm.land", "charm_hyper"),
    ];

    /// <summary>Port of URLDetectedProviders.</summary>
    public static IReadOnlySet<string> DetectedProviders
        => UrlProviderMap.Select(static entry => entry.ProviderType)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Port of DetectProviderFromURL.</summary>
    public static string DetectProviderFromUrl(string? baseUrl)
    {
        var trimmed = baseUrl?.Trim() ?? string.Empty;
        if (trimmed.Length == 0
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            return string.Empty;
        }

        var host = parsed.Host.ToLowerInvariant();
        if (host.Length == 0)
        {
            return string.Empty;
        }

        foreach (var (pattern, providerType) in UrlProviderMap)
        {
            if (host == pattern
                || host.EndsWith("." + pattern, StringComparison.Ordinal))
            {
                return providerType;
            }
        }

        return string.Empty;
    }
}
