using System.Globalization;
using AxonHub.Api.Settings;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>GraphQL AnalyticsFilter after GID parsing (internal/server/gql/analytics.graphql).</summary>
public sealed record AnalyticsFilterModel
{
    public DateTime? StartTime { get; init; }

    public DateTime? EndTime { get; init; }

    public IReadOnlyList<long>? ProjectIds { get; init; }

    public IReadOnlyList<long>? ChannelIds { get; init; }

    public IReadOnlyList<string>? ModelIds { get; init; }

    public IReadOnlyList<long>? ApiKeyIds { get; init; }

    public IReadOnlyList<long>? UserIds { get; init; }
}

/// <summary>AnalyticsOverview resolved to GraphQL Int/Float fields.</summary>
public sealed record AnalyticsOverviewModel(
    int TotalTokens,
    int TotalInputTokens,
    int TotalCachedInputTokens,
    int TotalUncachedInputTokens,
    int TotalOutputTokens,
    int TotalRequests,
    double TotalCost);

/// <summary>AnalyticsDailyStat resolved to GraphQL Int/Float fields.</summary>
public sealed record AnalyticsDailyStatModel(
    string Date,
    int InputTokens,
    int CachedInputTokens,
    int UncachedInputTokens,
    int OutputTokens,
    int TotalTokens,
    int RequestCount,
    double Cost);

/// <summary>AnalyticsDimensionStat resolved to GraphQL Int/Float fields.</summary>
public sealed record AnalyticsDimensionStatModel(
    string Id,
    string Name,
    int RequestCount,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    int TotalTokens,
    double Cost,
    double? TokensPerSecond,
    double? TtftMs);

/// <summary>
/// Port of the analytics root resolvers
/// (internal/server/gql/analytics.resolvers.go + analytics_helpers.go).
/// Overview, daily and dimension queries run under
/// <c>authz.WithScopeDecision(ctx, scopes.ScopeReadDashboard)</c>: an allowed
/// principal bypasses the UsageLog privacy chain entirely, a denied principal
/// fails with the privacy sentinel. Metadata runs under the normal UsageLog
/// query policy, so it is still scoped to the project and to the caller's
/// visible rows.
/// </summary>
/// <remarks>
/// The reference interpolates the configured timezone name straight into the
/// daily bucket SQL; the port binds it as a parameter (C194). Both resolve the
/// same <c>AT TIME ZONE</c> semantics for every loadable zone name.
/// </remarks>
public sealed class AnalyticsService(
    IAnalyticsStore analytics,
    IManagementIdentity identity,
    IUserAdminStore users,
    TracePrivacy privacy,
    ISystemStore systems,
    TimeProvider timeProvider)
{
    public const string ReadDashboard = "read_dashboard";

    private const string NoUserInContext = "no user in context";

    private const string DefaultDeny = "default deny";

    /// <summary>Port of AnalyticsMetadata (analytics.resolvers.go:24-40).</summary>
    public async Task<DateTime?> GetEarliestTimeAsync(CancellationToken cancellationToken = default)
    {
        var access = await privacy.EvaluateUsageLogQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        return await analytics.GetEarliestCreatedAtAsync(
                access?.ProjectId,
                access?.VisibilityUserId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of AnalyticsOverview (analytics.resolvers.go:42-88).</summary>
    public async Task<AnalyticsOverviewModel> GetOverviewAsync(
        AnalyticsFilterModel? filter,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadDashboardAsync(cancellationToken).ConfigureAwait(false);
        var (apiKeyIds, hasUserFilter) = await ResolveFilterApiKeyIdsAsync(filter, cancellationToken)
            .ConfigureAwait(false);

        AnalyticsOverviewRow row;
        try
        {
            row = await analytics.GetOverviewAsync(
                    ToQuery(filter, apiKeyIds, hasUserFilter),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get analytics overview: " + ex.Message,
                ex);
        }

        return new AnalyticsOverviewModel(
            TotalTokens: checked((int)row.TotalTokens),
            TotalInputTokens: checked((int)row.TotalInputTokens),
            TotalCachedInputTokens: checked((int)row.TotalCachedTokens),
            TotalUncachedInputTokens: checked((int)(row.TotalInputTokens - row.TotalCachedTokens)),
            TotalOutputTokens: checked((int)row.TotalOutputTokens),
            TotalRequests: checked((int)row.TotalRequests),
            TotalCost: row.Cost);
    }

    /// <summary>Port of AnalyticsDailyStats (analytics.resolvers.go:90-194).</summary>
    public async Task<IReadOnlyList<AnalyticsDailyStatModel>> GetDailyStatsAsync(
        AnalyticsFilterModel? filter,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadDashboardAsync(cancellationToken).ConfigureAwait(false);
        var (apiKeyIds, hasUserFilter) = await ResolveFilterApiKeyIdsAsync(filter, cancellationToken)
            .ConfigureAwait(false);

        var location = await SystemTimeLocations.ResolveAsync(systems, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<AnalyticsDailyRow> rows;
        try
        {
            rows = await analytics.GetDailyAsync(
                    ToQuery(filter, apiKeyIds, hasUserFilter),
                    location.Name,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get analytics daily stats: " + ex.Message,
                ex);
        }

        var statsByDate = new Dictionary<string, AnalyticsDailyRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            statsByDate[row.Date] = row;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var startDay = filter?.StartTime is { } startTime
            ? StartOfDay(startTime, location.Zone)
            : StartOfDay(nowUtc, location.Zone).AddDays(-29);
        var endDay = filter?.EndTime is { } endTime
            ? StartOfDay(endTime, location.Zone)
            : StartOfDay(nowUtc, location.Zone);

        var response = new List<AnalyticsDailyStatModel>();
        for (var day = startDay; day <= endDay; day = day.AddDays(1))
        {
            var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!statsByDate.TryGetValue(date, out var stats))
            {
                response.Add(new AnalyticsDailyStatModel(date, 0, 0, 0, 0, 0, 0, 0));
                continue;
            }

            response.Add(new AnalyticsDailyStatModel(
                date,
                InputTokens: checked((int)stats.InputTokens),
                CachedInputTokens: checked((int)stats.CachedTokens),
                UncachedInputTokens: checked((int)(stats.InputTokens - stats.CachedTokens)),
                OutputTokens: checked((int)stats.OutputTokens),
                TotalTokens: checked((int)stats.TotalTokens),
                RequestCount: checked((int)stats.RequestCount),
                Cost: stats.Cost));
        }

        return response;
    }

    /// <summary>Port of AnalyticsDimensionStats (analytics.resolvers.go:196-242).</summary>
    public async Task<IReadOnlyList<AnalyticsDimensionStatModel>> GetDimensionStatsAsync(
        AnalyticsFilterModel? filter,
        string dimension,
        CancellationToken cancellationToken = default)
    {
        // The reference resolves the API key filter before it switches on the
        // dimension name, and only then touches the UsageLog query (where the
        // scope decision denies). Keeping that order preserves the
        // "unsupported dimension" error for a denied caller with a bad name.
        var (apiKeyIds, hasUserFilter) = await ResolveFilterApiKeyIdsAsync(filter, cancellationToken)
            .ConfigureAwait(false);

        var parsed = dimension switch
        {
            "channel" => AnalyticsDimension.Channel,
            "model" => AnalyticsDimension.Model,
            "apiKey" => AnalyticsDimension.ApiKey,
            "user" => AnalyticsDimension.User,
            _ => (AnalyticsDimension?)null,
        };
        if (parsed is null)
        {
            throw new InvalidOperationException(
                "unsupported dimension: " + dimension
                + ", must be one of: channel, model, apiKey, user");
        }

        await EnsureReadDashboardAsync(cancellationToken).ConfigureAwait(false);
        var query = ToQuery(filter, apiKeyIds, hasUserFilter);

        IReadOnlyList<AnalyticsDimensionRow> rows;
        try
        {
            rows = await analytics.GetDimensionAsync(query, parsed.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get analytics stats by " + dimension + ": " + ex.Message,
                ex);
        }

        if (parsed.Value == AnalyticsDimension.ApiKey && rows.Count > 0)
        {
            var apiKeyIdsForNames = rows
                .Select(static row => long.Parse(row.Id, CultureInfo.InvariantCulture))
                .ToList();
            IReadOnlyDictionary<long, string> names;
            try
            {
                names = await analytics.GetApiKeyNamesAsync(apiKeyIdsForNames, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to get API key details: " + ex.Message,
                    ex);
            }

            foreach (var row in rows)
            {
                row.Name = names.TryGetValue(
                    long.Parse(row.Id, CultureInfo.InvariantCulture),
                    out var name)
                    ? name
                    : "API Key #" + row.Id;
            }
        }

        IReadOnlyDictionary<string, AnalyticsPerformanceRow> performanceByID;
        try
        {
            performanceByID = await analytics.GetPerformanceAsync(query, parsed.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get analytics performance stats by " + dimension + ": " + ex.Message,
                ex);
        }

        var results = new List<AnalyticsDimensionStatModel>(rows.Count);
        foreach (var row in rows)
        {
            performanceByID.TryGetValue(row.Id, out var performance);
            results.Add(new AnalyticsDimensionStatModel(
                Id: row.Id,
                Name: NameOf(row, parsed.Value),
                RequestCount: checked((int)row.RequestCount),
                InputTokens: checked((int)row.InputTokens),
                CachedInputTokens: checked((int)row.CachedTokens),
                OutputTokens: checked((int)row.OutputTokens),
                TotalTokens: checked((int)row.TotalTokens),
                Cost: row.Cost,
                TokensPerSecond: performance?.TokensPerSecond,
                TtftMs: performance?.TtftMs));
        }

        return results;
    }

    /// <summary>
    /// Port of authz.WithScopeDecision + HasScope for
    /// <c>scopes.ScopeReadDashboard</c> (internal/authz/scope.go:16-136).
    /// </summary>
    private async Task EnsureReadDashboardAsync(CancellationToken cancellationToken)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        // The test principal's decision context bypasses every rule.
        if (current?.IsTest == true)
        {
            return;
        }

        var apiKey = identity.GetCurrentApiKey();
        if (apiKey is not null)
        {
            if (!HasScope(apiKey.Scopes, ReadDashboard))
            {
                throw Deny(
                    "API key " + apiKey.Id.ToString(CultureInfo.InvariantCulture)
                    + " does not have required scope: " + ReadDashboard);
            }

            return;
        }

        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);

        // Owner, direct scopes and system-role scopes.
        if (PermissionValidator.HasSystemScope(loaded, ReadDashboard))
        {
            return;
        }

        // Project membership/role scopes require an explicit project context.
        var projectId = identity.GetCurrentProjectId();
        if (projectId is not null
            && PermissionValidator.UserHasProjectScope(
                loaded,
                projectId.Value,
                ReadDashboard))
        {
            return;
        }

        throw Deny(DefaultDeny);
    }

    /// <summary>Port of resolveFilterAPIKeyIDs (analytics_helpers.go:70-122).</summary>
    private async Task<(IReadOnlyList<long>? ApiKeyIds, bool HasUserFilter)> ResolveFilterApiKeyIdsAsync(
        AnalyticsFilterModel? filter,
        CancellationToken cancellationToken)
    {
        if (filter is null)
        {
            return (null, false);
        }

        var hasExplicitKeys = filter.ApiKeyIds is { Count: > 0 };
        var hasUserFilter = filter.UserIds is { Count: > 0 };
        if (!hasExplicitKeys && !hasUserFilter)
        {
            return (null, false);
        }

        IReadOnlyList<long> userKeyIds = [];
        if (hasUserFilter)
        {
            try
            {
                userKeyIds = await analytics.ResolveApiKeyIdsByUserIdsAsync(
                        filter.UserIds!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The reference swallows the query error and reports a user
                // filter that matched nothing.
                return (null, true);
            }
        }

        if (hasExplicitKeys && hasUserFilter)
        {
            var userKeySet = new HashSet<long>(userKeyIds);
            return (filter.ApiKeyIds!.Where(userKeySet.Contains).ToList(), true);
        }

        return hasExplicitKeys
            ? (filter.ApiKeyIds, false)
            : (userKeyIds, true);
    }

    private static AnalyticsQueryFilter ToQuery(
        AnalyticsFilterModel? filter,
        IReadOnlyList<long>? apiKeyIds,
        bool hasUserFilter)
    {
        if (filter is null)
        {
            return new AnalyticsQueryFilter { HasUserFilter = hasUserFilter };
        }

        return new AnalyticsQueryFilter
        {
            StartTime = filter.StartTime,
            EndTime = filter.EndTime,
            ProjectIds = filter.ProjectIds,
            ChannelIds = filter.ChannelIds,
            ModelIds = filter.ModelIds,
            ApiKeyIds = apiKeyIds,
            HasUserFilter = hasUserFilter,
        };
    }

    /// <summary>
    /// Port of startOfDay (analytics_helpers.go:17-22): local midnight of the
    /// instant in the configured zone, kept as an unspecified calendar date.
    /// </summary>
    private static DateTime StartOfDay(DateTime instant, TimeZoneInfo zone)
    {
        var utc = instant.Kind == DateTimeKind.Utc ? instant : instant.ToUniversalTime();
        return TimeZoneInfo.ConvertTimeFromUtc(utc, zone).Date;
    }

    /// <summary>
    /// Port of the dimStats name composition: the user name is
    /// <c>TrimSpace(first + " " + last)</c> with the email as fallback; every
    /// other dimension carries the name straight from the query.
    /// </summary>
    private static string NameOf(AnalyticsDimensionRow row, AnalyticsDimension dimension)
    {
        if (dimension != AnalyticsDimension.User)
        {
            return row.Name ?? string.Empty;
        }

        var name = GoStrings.TrimSpace((row.FirstName ?? string.Empty) + " " + (row.LastName ?? string.Empty));
        return name.Length == 0 ? row.Email ?? string.Empty : name;
    }

    private static bool HasScope(string? scopesJson, string scope)
        => PermissionValidator.ParseScopes(scopesJson).Contains(scope, StringComparer.Ordinal);

    /// <summary>
    /// Wraps a rule message the way ent reports a deny:
    /// <c>fmt.Errorf("%s: %w", msg, privacy.Deny)</c>.
    /// </summary>
    private static PrivacyDenyException Deny(string message)
        => new(message + ": " + PrivacyDenyException.DenyMessage);
}
