using System.Globalization;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Settings;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>DashboardOverview (internal/server/gql/dashboard.graphql).</summary>
public sealed record DashboardOverviewModel(
    int TotalRequests,
    RequestStatsModel RequestStats,
    int FailedRequests,
    double? AverageResponseTime);

/// <summary>RequestStats.</summary>
public sealed record RequestStatsModel(
    int RequestsToday,
    int RequestsThisWeek,
    int RequestsLastWeek,
    int RequestsThisMonth);

/// <summary>RequestStatsByChannel.</summary>
public sealed record RequestStatsByChannelModel(string ChannelName, int Count);

/// <summary>RequestStatsByModel.</summary>
public sealed record RequestStatsByModelModel(string ModelId, int Count);

/// <summary>RequestStatsByAPIKey.</summary>
public sealed record RequestStatsByApiKeyModel(string ApiKeyId, string ApiKeyName, int Count);

/// <summary>TokenStatsByAPIKey.</summary>
public sealed record TokenStatsByApiKeyModel(
    string ApiKeyId,
    string ApiKeyName,
    long InputTokens,
    long OutputTokens,
    long CachedTokens,
    long ReasoningTokens,
    long TotalTokens);

/// <summary>Input of apiKeyTokenUsageStats after GID parsing.</summary>
public sealed record DashboardApiKeyTokenUsageInput(
    IReadOnlyList<GidValue> ApiKeyIds,
    DateTime? CreatedAtGte,
    DateTime? CreatedAtLte);

/// <summary>APIKeyTokenUsageStats.</summary>
public sealed record ApiKeyTokenUsageStatsModel(
    string ApiKeyId,
    long InputTokens,
    long OutputTokens,
    long CachedTokens,
    long ReasoningTokens,
    IReadOnlyList<ModelTokenUsageStatsModel> TopModels);

/// <summary>ModelTokenUsageStats.</summary>
public sealed record ModelTokenUsageStatsModel(
    string ModelId,
    long InputTokens,
    long OutputTokens,
    long CachedTokens,
    long ReasoningTokens);

/// <summary>DailyRequestStats.</summary>
public sealed record DailyRequestStatsModel(string Date, int Count, int Tokens, double Cost);

/// <summary>TopRequestsProjects.</summary>
public sealed record TopRequestsProjectsModel(
    string ProjectId,
    string ProjectName,
    string ProjectDescription,
    int RequestCount);

/// <summary>
/// TokenStats plus the non-schema flag that drives the resolver's
/// TOKEN_STATS_ALL_TIME_AGG_FAILED error.
/// </summary>
public sealed record TokenStatsModel(
    long TotalInputTokensToday,
    long TotalOutputTokensToday,
    long TotalCachedTokensToday,
    long TotalInputTokensThisWeek,
    long TotalOutputTokensThisWeek,
    long TotalCachedTokensThisWeek,
    long TotalInputTokensThisMonth,
    long TotalOutputTokensThisMonth,
    long TotalCachedTokensThisMonth,
    long TotalInputTokensAllTime,
    long TotalOutputTokensAllTime,
    long TotalCachedTokensAllTime,
    DateTime? LastUpdated,
    bool AllTimeFailed);

/// <summary>ChannelSuccessRate.</summary>
public sealed record ChannelSuccessRateModel(
    string ChannelId,
    string ChannelName,
    string ChannelType,
    bool ChannelDisabled,
    int SuccessCount,
    int FailedCount,
    int TotalCount,
    double SuccessRate);

/// <summary>FastestChannel.</summary>
public sealed record FastestChannelModel(
    string ChannelId,
    string ChannelName,
    string ChannelType,
    double Throughput,
    int TokensCount,
    int LatencyMs,
    int RequestCount,
    string ConfidenceLevel);

/// <summary>FastestModel.</summary>
public sealed record FastestModelModel(
    string ModelId,
    string ModelName,
    double Throughput,
    int TokensCount,
    int LatencyMs,
    int RequestCount,
    string ConfidenceLevel);

/// <summary>ModelPerformanceStat.</summary>
public sealed record ModelPerformanceStatModel(
    string Date,
    string ModelId,
    double? Throughput,
    double? TtftMs,
    int RequestCount);

/// <summary>ChannelPerformanceStat.</summary>
public sealed record ChannelPerformanceStatModel(
    string Date,
    string ChannelId,
    string ChannelName,
    double? Throughput,
    double? TtftMs,
    int RequestCount);

/// <summary>TokenStatsByChannel.</summary>
public sealed record TokenStatsByChannelModel(
    string ChannelId,
    string ChannelName,
    long InputTokens,
    long OutputTokens,
    long CachedTokens,
    long ReasoningTokens,
    long TotalTokens);

/// <summary>TokenStatsByModel.</summary>
public sealed record TokenStatsByModelModel(
    string ModelId,
    long InputTokens,
    long OutputTokens,
    long CachedTokens,
    long ReasoningTokens,
    long TotalTokens);

/// <summary>CostStatsByChannel.</summary>
public sealed record CostStatsByChannelModel(string ChannelName, double Cost);

/// <summary>CostStatsByModel.</summary>
public sealed record CostStatsByModelModel(string ModelId, double Cost);

/// <summary>CostStatsByAPIKey.</summary>
public sealed record CostStatsByApiKeyModel(string ApiKeyId, string ApiKeyName, double Cost);

/// <summary>UsageStatsByUser.</summary>
public sealed record UsageStatsByUserModel(
    string UserId,
    string UserName,
    long RequestCount,
    long TotalTokens,
    double TotalCost);

/// <summary>FastestChannelsInput after the resolver's normalization.</summary>
public readonly record struct DashboardFastestChannelsInput(string TimeWindow, int Limit);

/// <summary>
/// Port of the dashboard root resolvers
/// (internal/server/gql/dashboard.resolvers.go,
/// dashboard_usage_aggregates.go, dashboard_helpers.go and
/// channel_performance_helpers.go).
/// </summary>
/// <remarks>
/// Non-cached resolvers emulate <c>authz.WithScopeDecision(ctx, read_dashboard)</c>:
/// tolerant ones (dashboardOverview, requestStats, tokenStats) report zeros when
/// the principal is denied, topRequestsProjects lets the privacy deny propagate.
/// Cached resolvers check <c>authz.RequireScope</c> before touching the cache.
/// All cached queries use PostgreSQL-only SQL and the configured timezone name
/// as a bound parameter (C195). The channelPerformanceStats resolver prefers
/// the channel_probes aggregation and falls back to request executions only
/// when the probe query returns no rows (C249).
/// </remarks>
public sealed class DashboardService(
    IDashboardStore dashboard,
    IApiKeyStore apiKeys,
    IProjectStore projects,
    IUserAdminStore users,
    IManagementIdentity identity,
    AuthorizationScope authz,
    ApiKeyPrivacy apiKeyPrivacy,
    ISystemStore systems,
    DashboardCache cache,
    DashboardTokenStatsCache tokenCache,
    TimeProvider timeProvider)
{
    public const string ReadDashboard = "read_dashboard";

    private const int DashboardUsageAggregateLimit = 10;

    private const int TopPerformersLimit = 6;

    private static readonly TimeSpan DashboardCacheTtl = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan DashboardSlowCacheTtl = TimeSpan.FromMinutes(5);

    private sealed record ScoredItem<T>(T Stats, string Confidence, int Score);

    private sealed record ChannelInfo(long ChannelId, long RequestCount);

    private sealed record ModelInfo(string ModelId, long RequestCount);

    // ---------------------------------------------------------------- overview

    /// <summary>Port of queryResolver.DashboardOverview.</summary>
    public async Task<DashboardOverviewModel> GetDashboardOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        // Deliberately not cached: a cached partial result would hide a
        // transient database failure from every later viewer.
        var allowed = await HasReadDashboardAsync(cancellationToken).ConfigureAwait(false);

        var totalRequests = 0;
        var failedRequests = 0;
        if (allowed)
        {
            try
            {
                var rows = await dashboard.GetRequestStatusCountsAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var row in rows)
                {
                    totalRequests += checked((int)row.Count);
                    if (string.Equals(row.Status, "failed", StringComparison.Ordinal))
                    {
                        failedRequests = checked((int)row.Count);
                    }
                }
            }
            catch (Exception)
            {
                // The reference logs and keeps the zero defaults.
            }
        }

        var requestStats = await GetRequestStatsCoreAsync(allowed, cancellationToken)
            .ConfigureAwait(false);

        // The reference never fills averageResponseTime.
        return new DashboardOverviewModel(
            totalRequests,
            requestStats,
            failedRequests,
            AverageResponseTime: null);
    }

    /// <summary>Port of queryResolver.RequestStats.</summary>
    public async Task<RequestStatsModel> GetRequestStatsAsync(
        CancellationToken cancellationToken = default)
        => await GetRequestStatsCoreAsync(
                await HasReadDashboardAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<RequestStatsModel> GetRequestStatsCoreAsync(
        bool allowed,
        CancellationToken cancellationToken)
    {
        if (!allowed)
        {
            return new RequestStatsModel(0, 0, 0, 0);
        }

        var location = await SystemTimeLocations.ResolveAsync(systems, cancellationToken)
            .ConfigureAwait(false);
        var periods = CalendarPeriods.Compute(
            timeProvider.GetUtcNow().UtcDateTime,
            location.Zone);

        var today = 0;
        try
        {
            today = checked((int)await dashboard.CountUsageLogsSinceAsync(
                    periods.Today.Start,
                    cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception)
        {
        }

        var thisWeek = 0;
        try
        {
            thisWeek = checked((int)await dashboard.CountUsageLogsSinceAsync(
                    periods.ThisWeek.Start,
                    cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception)
        {
        }

        var lastWeek = 0;
        try
        {
            lastWeek = checked((int)await dashboard.CountUsageLogsBetweenAsync(
                    periods.LastWeek.Start,
                    periods.LastWeek.End,
                    cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception)
        {
        }

        var thisMonth = 0;
        try
        {
            thisMonth = checked((int)await dashboard.CountUsageLogsSinceAsync(
                    periods.ThisMonth.Start,
                    cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception)
        {
        }

        return new RequestStatsModel(today, thisWeek, lastWeek, thisMonth);
    }

    // ------------------------------------------------- shared usage aggregates

    /// <summary>
    /// Port of loadDashboardUsageAggregates: one grouped scan per dimension and
    /// window serves the request, token and cost widgets.
    /// </summary>
    private async Task<IReadOnlyList<DashboardUsageAggregateRow>> LoadUsageAggregatesAsync(
        DashboardUsageDimension dimension,
        string? timeWindow,
        CancellationToken cancellationToken)
    {
        var location = await SystemTimeLocations.ResolveAsync(systems, cancellationToken)
            .ConfigureAwait(false);

        // The canonical window is used for both the cache key and the query so
        // equivalent inputs such as "day" and " day " cannot populate different
        // results under different keys.
        var window = CanonicalDashboardTimeWindow(timeWindow);
        var key = "dashboard-usage:" + DimensionKey(dimension) + ":" + window + ":" + location.Name;

        await authz.RequireScopeAsync(ReadDashboard, cancellationToken).ConfigureAwait(false);

        return await cache.GetOrComputeAsync(
                key,
                DashboardCacheTtl,
                async (store, token) =>
                {
                    var periods = CalendarPeriods.Compute(
                        timeProvider.GetUtcNow().UtcDateTime,
                        location.Zone);
                    DateTime? since = window switch
                    {
                        "day" => periods.Today.Start,
                        "week" => periods.ThisWeek.Start,
                        "month" => periods.ThisMonth.Start,
                        _ => null,
                    };

                    return await store.GetUsageAggregatesAsync(dimension, since, token)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string DimensionKey(DashboardUsageDimension dimension) => dimension switch
    {
        DashboardUsageDimension.Channel => "channel",
        DashboardUsageDimension.Model => "model",
        DashboardUsageDimension.ApiKey => "apiKey",
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, null),
    };

    /// <summary>Port of canonicalDashboardTimeWindow.</summary>
    private static string CanonicalDashboardTimeWindow(string? timeWindow)
    {
        if (timeWindow is null)
        {
            return "allTime";
        }

        return GoStrings.TrimSpace(timeWindow) switch
        {
            "day" => "day",
            "week" => "week",
            "month" => "month",
            _ => "allTime",
        };
    }

    /// <summary>
    /// Port of topDashboardUsageAggregates: stable sort by score descending,
    /// ties by ID then name, top 10.
    /// </summary>
    private static List<DashboardUsageAggregateRow> TopDashboardUsageAggregates(
        IReadOnlyList<DashboardUsageAggregateRow> rows,
        Func<DashboardUsageAggregateRow, double> score)
        => rows
            .OrderByDescending(score)
            .ThenBy(static row => row.Id)
            .ThenBy(static row => row.Name, StringComparer.Ordinal)
            .Take(DashboardUsageAggregateLimit)
            .ToList();

    // ------------------------------------------------------- request statistics

    /// <summary>Port of queryResolver.RequestStatsByChannel.</summary>
    public async Task<IReadOnlyList<RequestStatsByChannelModel>> GetRequestStatsByChannelAsync(
        string? timeWindow,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DashboardUsageAggregateRow> rows;
        try
        {
            rows = await LoadUsageAggregatesAsync(
                    DashboardUsageDimension.Channel,
                    timeWindow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get requests by channel: " + ex.Message,
                ex);
        }

        var top = TopDashboardUsageAggregates(rows, static row => row.RequestCount);

        return top
            .Select(static row => new RequestStatsByChannelModel(
                row.Name,
                checked((int)row.RequestCount)))
            .ToList();
    }

    /// <summary>Port of queryResolver.RequestStatsByModel.</summary>
    public async Task<IReadOnlyList<RequestStatsByModelModel>> GetRequestStatsByModelAsync(
        string? timeWindow,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DashboardUsageAggregateRow> rows;
        try
        {
            rows = await LoadUsageAggregatesAsync(
                    DashboardUsageDimension.Model,
                    timeWindow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get requests by model: " + ex.Message,
                ex);
        }

        var top = TopDashboardUsageAggregates(rows, static row => row.RequestCount);

        return top
            .Select(static row => new RequestStatsByModelModel(
                row.Name,
                checked((int)row.RequestCount)))
            .ToList();
    }

    /// <summary>Port of queryResolver.RequestStatsByAPIKey.</summary>
    public async Task<IReadOnlyList<RequestStatsByApiKeyModel>> GetRequestStatsByApiKeyAsync(
        string? timeWindow,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DashboardUsageAggregateRow> rows;
        try
        {
            rows = await LoadUsageAggregatesAsync(
                    DashboardUsageDimension.ApiKey,
                    timeWindow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get requests by API key: " + ex.Message,
                ex);
        }

        var top = TopDashboardUsageAggregates(rows, static row => row.RequestCount);

        return top
            .Select(static row => new RequestStatsByApiKeyModel(
                ApiKeyGid(row.Id),
                row.Name,
                checked((int)row.RequestCount)))
            .ToList();
    }

    // --------------------------------------------------------- token statistics

    /// <summary>Port of queryResolver.TokenStatsByAPIKey.</summary>
    public async Task<IReadOnlyList<TokenStatsByApiKeyModel>> GetTokenStatsByApiKeyAsync(
        string? timeWindow,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DashboardUsageAggregateRow> rows;
        try
        {
            rows = await LoadUsageAggregatesAsync(
                    DashboardUsageDimension.ApiKey,
                    timeWindow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get tokens by API key: " + ex.Message,
                ex);
        }

        var top = TopDashboardUsageAggregates(rows, static row => TotalTokens(row));

        return top
            .Select(static row => new TokenStatsByApiKeyModel(
                ApiKeyGid(row.Id),
                row.Name,
                row.InputTokens,
                row.OutputTokens,
                row.CachedTokens,
                row.ReasoningTokens,
                TotalTokens(row)))
            .ToList();
    }

    /// <summary>Port of queryResolver.APIKeyTokenUsageStats.</summary>
    public async Task<IReadOnlyList<ApiKeyTokenUsageStatsModel>> GetApiKeyTokenUsageStatsAsync(
        DashboardApiKeyTokenUsageInput? input,
        CancellationToken cancellationToken = default)
    {
        await authz.RequireScopeAsync(ApiKeyPrivacy.ReadApiKeys, cancellationToken)
            .ConfigureAwait(false);

        if (input is null || input.ApiKeyIds.Count == 0)
        {
            throw new InvalidOperationException(
                "apiKeyIds is required and must contain at least one API key");
        }

        if (input.ApiKeyIds.Count > 100)
        {
            throw new InvalidOperationException("apiKeyIds cannot exceed 100 items");
        }

        var requestedIds = new List<long>(input.ApiKeyIds.Count);
        foreach (var guid in input.ApiKeyIds)
        {
            if (!string.Equals(guid.Type, "APIKey", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "invalid GUID type: expected APIKey, got " + guid.Type);
            }

            requestedIds.Add(guid.Id);
        }

        // Validate access through the APIKey privacy policy (read_api_keys plus
        // the project filter); cross-project ids are dropped.
        IReadOnlyList<long> accessibleIds;
        try
        {
            var access = await apiKeyPrivacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            var keys = await apiKeys.ListAllAsync(
                    new ApiKeyListFilter { IdIn = requestedIds },
                    access,
                    cancellationToken)
                .ConfigureAwait(false);
            accessibleIds = keys.Select(static key => key.Id).ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to validate API key access: " + ex.Message,
                ex);
        }

        if (accessibleIds.Count == 0)
        {
            return [];
        }

        IReadOnlyList<DashboardApiKeyUsageRow> rows;
        try
        {
            rows = await dashboard.GetApiKeyTokenUsageAsync(
                    accessibleIds,
                    input.CreatedAtGte,
                    input.CreatedAtLte,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get API key token usage stats: " + ex.Message,
                ex);
        }

        var topModels = await GetTopModelsForApiKeysAsync(
                accessibleIds,
                input.CreatedAtGte,
                input.CreatedAtLte,
                cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new ApiKeyTokenUsageStatsModel(
                ApiKeyGid(row.ApiKeyId),
                row.InputTokens,
                row.OutputTokens,
                row.CachedTokens,
                row.ReasoningTokens,
                topModels.TryGetValue(row.ApiKeyId, out var models)
                    ? models
                    : []))
            .ToList();
    }

    /// <summary>
    /// Port of getTopModelsForAPIKeys: one grouped query for every key, top 3
    /// models by billable total (input + output), errors swallowed to an empty
    /// map.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, IReadOnlyList<ModelTokenUsageStatsModel>>>
        GetTopModelsForApiKeysAsync(
            IReadOnlyList<long> apiKeyIds,
            DateTime? createdAtGte,
            DateTime? createdAtLte,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<ModelTokenUsageStatsModel>>();
        if (apiKeyIds.Count == 0)
        {
            return result;
        }

        IReadOnlyList<DashboardApiKeyModelUsageRow> rows;
        try
        {
            rows = await dashboard.GetApiKeyModelTokenUsageAsync(
                    apiKeyIds,
                    createdAtGte,
                    createdAtLte,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return result;
        }

        var lists = new Dictionary<long, List<ModelTokenUsageStatsModel>>();
        foreach (var row in rows)
        {
            if (!lists.TryGetValue(row.ApiKeyId, out var list))
            {
                list = [];
                lists[row.ApiKeyId] = list;
            }

            if (list.Count < 3)
            {
                list.Add(new ModelTokenUsageStatsModel(
                    row.ModelId,
                    row.InputTokens,
                    row.OutputTokens,
                    row.CachedTokens,
                    row.ReasoningTokens));
            }
        }

        foreach (var (apiKeyId, list) in lists)
        {
            result[apiKeyId] = list;
        }

        return result;
    }

    /// <summary>Port of queryResolver.DailyRequestStats.</summary>
    public async Task<IReadOnlyList<DailyRequestStatsModel>> GetDailyRequestStatsAsync(
        CancellationToken cancellationToken = default)
    {
        var location = await SystemTimeLocations.ResolveAsync(systems, cancellationToken)
            .ConfigureAwait(false);
        var key = "dailyRequestStats:" + location.Name;

        await authz.RequireScopeAsync(ReadDashboard, cancellationToken).ConfigureAwait(false);

        return await cache.GetOrComputeAsync(
                key,
                DashboardCacheTtl,
                async (store, token) =>
                {
                    const int daysCount = 30;

                    var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                    var startDateLocal = CalendarPeriods
                        .StartOfLocalDay(nowUtc, location.Zone)
                        .AddDays(-(daysCount - 1));
                    var startDateUtc = CalendarPeriods.LocalToUtc(
                        startDateLocal,
                        location.Zone);

                    IReadOnlyList<DashboardDailyRequestRow> rows;
                    try
                    {
                        rows = await store.GetDailyRequestStatsAsync(
                                startDateUtc,
                                nowUtc,
                                location.Name,
                                token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "failed to get daily request stats: " + ex.Message,
                            ex);
                    }

                    var statsByDate = new Dictionary<string, DashboardDailyRequestRow>(
                        StringComparer.Ordinal);
                    foreach (var row in rows)
                    {
                        statsByDate[row.Date] = row;
                    }

                    var response = new List<DailyRequestStatsModel>(daysCount);
                    for (var i = 0; i < daysCount; i++)
                    {
                        var dateStr = CalendarPeriods.FormatDate(startDateLocal.AddDays(i));
                        if (statsByDate.TryGetValue(dateStr, out var stats))
                        {
                            response.Add(new DailyRequestStatsModel(
                                dateStr,
                                checked((int)stats.TotalCount),
                                checked((int)stats.TotalTokens),
                                stats.TotalCost));
                        }
                        else
                        {
                            response.Add(new DailyRequestStatsModel(dateStr, 0, 0, 0));
                        }
                    }

                    return (IReadOnlyList<DailyRequestStatsModel>)response;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of queryResolver.TopRequestsProjects.</summary>
    public async Task<IReadOnlyList<TopRequestsProjectsModel>> GetTopRequestsProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await HasReadDashboardAsync(cancellationToken).ConfigureAwait(false))
        {
            // WithScopeDecision(Deny) makes the ent aggregate fail with the
            // privacy sentinel; the error presenter maps it to FORBIDDEN.
            throw new InvalidOperationException(
                "failed to get top projects: " + PrivacyDenyException.DenyMessage,
                new PrivacyDenyException());
        }

        IReadOnlyList<DashboardProjectRequestRow> rows;
        try
        {
            rows = await dashboard.GetTopProjectRequestsAsync(10, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get top projects: " + ex.Message,
                ex);
        }

        if (rows.Count == 0)
        {
            return [];
        }

        var response = new List<TopRequestsProjectsModel>(rows.Count);
        foreach (var row in rows)
        {
            // The request runs under privacy.DecisionContext(Allow), so the
            // reference's project query bypasses the Project privacy rules and
            // reads any non-deleted project by id.
            ProjectRecord? project;
            try
            {
                project = await projects.GetByIdAsync(row.ProjectId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to get project details: " + ex.Message,
                    ex);
            }

            if (project is null)
            {
                continue;
            }

            response.Add(new TopRequestsProjectsModel(
                new GidValue("Project", project.Id).ToString(),
                project.Name,
                project.Description,
                checked((int)row.RequestCount)));
        }

        return response;
    }

    /// <summary>Port of queryResolver.TokenStats.</summary>
    public async Task<TokenStatsModel> GetTokenStatsAsync(
        CancellationToken cancellationToken = default)
    {
        var allowed = await HasReadDashboardAsync(cancellationToken).ConfigureAwait(false);
        var location = await SystemTimeLocations.ResolveAsync(systems, cancellationToken)
            .ConfigureAwait(false);
        var periods = CalendarPeriods.Compute(
            timeProvider.GetUtcNow().UtcDateTime,
            location.Zone);

        var today = await GetTokenSumsAsync(periods.Today.Start, allowed, cancellationToken)
            .ConfigureAwait(false);
        var thisWeek = await GetTokenSumsAsync(periods.ThisWeek.Start, allowed, cancellationToken)
            .ConfigureAwait(false);
        var thisMonth = await GetTokenSumsAsync(periods.ThisMonth.Start, allowed, cancellationToken)
            .ConfigureAwait(false);

        long allTimeInput = 0;
        long allTimeOutput = 0;
        long allTimeCached = 0;
        DateTime? lastUpdated = null;
        var allTimeFailed = false;

        var snapshot = tokenCache.Read();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (snapshot is not null && now - snapshot.CacheTime < tokenCache.HardTtl)
        {
            // Cache is valid within the hard TTL. The reference also starts an
            // async refresh when past the soft TTL, but that context carries no
            // user, so the refresh always fails and the cached values remain
            // (C195); the port simply keeps the snapshot.
            allTimeInput = snapshot.Stats.InputTokens;
            allTimeOutput = snapshot.Stats.OutputTokens;
            allTimeCached = snapshot.Stats.CachedTokens;
            lastUpdated = snapshot.CacheTime;
        }
        else
        {
            var refreshed = await tokenCache.RefreshAsync(
                    async (store, token) =>
                    {
                        // WithScopeDecision(Deny) would make the aggregate fail.
                        if (!allowed)
                        {
                            throw new PrivacyDenyException();
                        }

                        var row = await store.GetTokenSumsAsync(null, token)
                            .ConfigureAwait(false);

                        // An empty table yields SQL NULL; preserve the
                        // reference's all-time aggregation error.
                        if (row.RowCount == 0)
                        {
                            throw new InvalidOperationException(
                                "unexpected aggregate result");
                        }

                        return new DashboardAllTimeTokenStats(
                            row.InputTokens,
                            row.OutputTokens,
                            row.CachedTokens);
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (refreshed is null)
            {
                allTimeFailed = true;
            }
            else
            {
                allTimeInput = refreshed.Stats.InputTokens;
                allTimeOutput = refreshed.Stats.OutputTokens;
                allTimeCached = refreshed.Stats.CachedTokens;
                lastUpdated = refreshed.CacheTime;
            }
        }

        return new TokenStatsModel(
            today.Input,
            today.Output,
            today.Cached,
            thisWeek.Input,
            thisWeek.Output,
            thisWeek.Cached,
            thisMonth.Input,
            thisMonth.Output,
            thisMonth.Cached,
            allTimeInput,
            allTimeOutput,
            allTimeCached,
            lastUpdated,
            allTimeFailed);
    }

    private async Task<(long Input, long Output, long Cached)> GetTokenSumsAsync(
        DateTime since,
        bool allowed,
        CancellationToken cancellationToken)
    {
        if (!allowed)
        {
            return (0, 0, 0);
        }

        try
        {
            var row = await dashboard.GetTokenSumsAsync(since, cancellationToken)
                .ConfigureAwait(false);
            return (row.InputTokens, row.OutputTokens, row.CachedTokens);
        }
        catch (Exception)
        {
            // The reference logs the failure and reports zeros.
            return (0, 0, 0);
        }
    }

    // ------------------------------------------------------- channel success

    /// <summary>Port of queryResolver.ChannelSuccessRates.</summary>
    public async Task<IReadOnlyList<ChannelSuccessRateModel>> GetChannelSuccessRatesAsync(
        string? timeWindow,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var location = await SystemTimeLocations.ResolveAsync(systems, cancellationToken)
            .ConfigureAwait(false);
        var (window, limitCount) = NormalizeChannelSuccessRatesParams(timeWindow, limit);
        var key = "channelSuccessRates:" + location.Name + ":" + window + ":"
            + limitCount.ToString(CultureInfo.InvariantCulture);

        await authz.RequireScopeAsync(ReadDashboard, cancellationToken).ConfigureAwait(false);

        return await cache.GetOrComputeAsync(
                key,
                DashboardCacheTtl,
                async (store, token) =>
                {
                    var since = ParseTimeWindow(
                        window,
                        location.Zone,
                        timeProvider.GetUtcNow().UtcDateTime,
                        out _);

                    IReadOnlyList<DashboardChannelExecutionRow> rows;
                    try
                    {
                        rows = await store.GetChannelExecutionStatsAsync(since, token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "failed to get channel execution stats: " + ex.Message,
                            ex);
                    }

                    if (rows.Count == 0)
                    {
                        return (IReadOnlyList<ChannelSuccessRateModel>)[];
                    }

                    var response = new List<ChannelSuccessRateModel>(rows.Count);
                    foreach (var row in rows)
                    {
                        var totalCount = row.SuccessCount + row.FailedCount;
                        var successRate = totalCount > 0
                            ? (double)row.SuccessCount / totalCount * 100
                            : 0;

                        response.Add(new ChannelSuccessRateModel(
                            new GidValue("Channel", row.ChannelId).ToString(),
                            string.Empty,
                            string.Empty,
                            ChannelDisabled: false,
                            checked((int)row.SuccessCount),
                            checked((int)row.FailedCount),
                            checked((int)totalCount),
                            successRate));
                    }

                    // The reference uses an unstable sort by total count; the
                    // port keeps a deterministic stable order (C195).
                    response = response
                        .OrderByDescending(static item => item.TotalCount)
                        .ToList();

                    if (limitCount > 0 && response.Count > limitCount)
                    {
                        response = response.Take(limitCount).ToList();
                    }

                    var channelIds = response
                        .Select(static item => GidValue.Parse(item.ChannelId).Id)
                        .ToList();

                    // Soft-deleted channels are included in the details lookup
                    // (schematype.SkipSoftDelete).
                    IReadOnlyList<DashboardChannelRow> channels;
                    try
                    {
                        channels = await store.GetChannelsByIdsAsync(
                                channelIds,
                                includeDeleted: true,
                                token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "failed to get channel details: " + ex.Message,
                            ex);
                    }

                    var channelMap = new Dictionary<long, DashboardChannelRow>();
                    foreach (var channel in channels)
                    {
                        channelMap[channel.Id] = channel;
                    }

                    return (IReadOnlyList<ChannelSuccessRateModel>)response
                        .Select(item =>
                        {
                            var id = GidValue.Parse(item.ChannelId).Id;
                            if (!channelMap.TryGetValue(id, out var channel))
                            {
                                return item;
                            }

                            return item with
                            {
                                ChannelName = channel.Name,
                                ChannelType = channel.Type,
                                ChannelDisabled = !string.Equals(
                                    channel.Status,
                                    "enabled",
                                    StringComparison.Ordinal),
                            };
                        })
                        .ToList();
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of normalizeChannelSuccessRatesParams.</summary>
    private static (string Window, int Limit) NormalizeChannelSuccessRatesParams(
        string? timeWindow,
        int? limit)
    {
        var window = "day";
        if (!string.IsNullOrEmpty(timeWindow))
        {
            window = timeWindow switch
            {
                "day" or "week" or "month" => timeWindow,
                _ => "allTime",
            };
        }

        var limitCount = limit is > 0 ? limit.Value : 0;
        return (window, limitCount);
    }

    // -------------------------------------------------------------- throughput

    /// <summary>Port of queryResolver.FastestChannels.</summary>
    public async Task<IReadOnlyList<FastestChannelModel>> GetFastestChannelsAsync(
        DashboardFastestChannelsInput input,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeFastestChannelsInput(input);
        var location = await SystemTimeLocations.ResolveAsync(systems, cancellationToken)
            .ConfigureAwait(false);
        var key = "fastestChannels:" + location.Name + ":" + normalized.TimeWindow + ":"
            + normalized.Limit.ToString(CultureInfo.InvariantCulture);

        await authz.RequireScopeAsync(ReadDashboard, cancellationToken).ConfigureAwait(false);

        return await cache.GetOrComputeAsync(
                key,
                DashboardSlowCacheTtl,
                async (store, token) =>
                {
                    var periods = CalendarPeriods.Compute(
                        timeProvider.GetUtcNow().UtcDateTime,
                        location.Zone);
                    var since = normalized.TimeWindow switch
                    {
                        "week" => periods.ThisWeek.Start,
                        "month" => periods.ThisMonth.Start,
                        _ => periods.Today.Start,
                    };

                    var sqlLimit = Math.Max(normalized.Limit * 4, 20);

                    IReadOnlyList<DashboardFastestChannelRow> rows;
                    try
                    {
                        rows = await store.GetFastestChannelsAsync(since, sqlLimit, token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "failed to query fastest channels: " + ex.Message,
                            ex);
                    }

                    if (rows.Count == 0)
                    {
                        return (IReadOnlyList<FastestChannelModel>)[];
                    }

                    var selected = CalculateConfidenceAndSort(
                        rows,
                        static row => row.RequestCount,
                        static row => row.Throughput,
                        normalized.Limit);

                    return (IReadOnlyList<FastestChannelModel>)selected
                        .Select(item => new FastestChannelModel(
                            new GidValue("Channel", item.Stats.ChannelId).ToString(),
                            item.Stats.ChannelName,
                            item.Stats.ChannelType,
                            item.Stats.Throughput,
                            checked((int)item.Stats.TokensCount),
                            checked((int)item.Stats.LatencyMs),
                            checked((int)item.Stats.RequestCount),
                            item.Confidence))
                        .ToList();
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of queryResolver.FastestModels.</summary>
    public async Task<IReadOnlyList<FastestModelModel>> GetFastestModelsAsync(
        DashboardFastestChannelsInput input,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeFastestChannelsInput(input);
        var location = await SystemTimeLocations.ResolveAsync(systems, cancellationToken)
            .ConfigureAwait(false);
        var key = "fastestModels:" + location.Name + ":" + normalized.TimeWindow + ":"
            + normalized.Limit.ToString(CultureInfo.InvariantCulture);

        await authz.RequireScopeAsync(ReadDashboard, cancellationToken).ConfigureAwait(false);

        return await cache.GetOrComputeAsync(
                key,
                DashboardSlowCacheTtl,
                async (store, token) =>
                {
                    var periods = CalendarPeriods.Compute(
                        timeProvider.GetUtcNow().UtcDateTime,
                        location.Zone);
                    var since = normalized.TimeWindow switch
                    {
                        "week" => periods.ThisWeek.Start,
                        "month" => periods.ThisMonth.Start,
                        _ => periods.Today.Start,
                    };

                    var sqlLimit = Math.Max(normalized.Limit * 4, 20);

                    IReadOnlyList<DashboardFastestModelRow> rows;
                    try
                    {
                        rows = await store.GetFastestModelsAsync(since, sqlLimit, token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "failed to query fastest models: " + ex.Message,
                            ex);
                    }

                    if (rows.Count == 0)
                    {
                        return (IReadOnlyList<FastestModelModel>)[];
                    }

                    var selected = CalculateConfidenceAndSort(
                        rows,
                        static row => row.RequestCount,
                        static row => row.Throughput,
                        normalized.Limit);

                    return (IReadOnlyList<FastestModelModel>)selected
                        .Select(item => new FastestModelModel(
                            item.Stats.ModelId,
                            item.Stats.ModelName,
                            item.Stats.Throughput,
                            checked((int)item.Stats.TokensCount),
                            checked((int)item.Stats.LatencyMs),
                            checked((int)item.Stats.RequestCount),
                            item.Confidence))
                        .ToList();
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of normalizeFastestChannelsInput.</summary>
    private static DashboardFastestChannelsInput NormalizeFastestChannelsInput(
        DashboardFastestChannelsInput input)
    {
        var window = input.TimeWindow is "day" or "week" or "month"
            ? input.TimeWindow
            : "day";

        var limit = input.Limit;
        if (limit <= 0)
        {
            limit = 5;
        }

        if (limit > 100)
        {
            limit = 100;
        }

        return new DashboardFastestChannelsInput(window, limit);
    }

    // ------------------------------------------------------------ performance

    /// <summary>Port of queryResolver.ModelPerformanceStats.</summary>
    public async Task<IReadOnlyList<ModelPerformanceStatModel>> GetModelPerformanceStatsAsync(
        CancellationToken cancellationToken = default)
    {
        var location = await SystemTimeLocations.ResolveAsync(systems, cancellationToken)
            .ConfigureAwait(false);
        var key = "modelPerformanceStats:" + location.Name;

        await authz.RequireScopeAsync(ReadDashboard, cancellationToken).ConfigureAwait(false);

        return await cache.GetOrComputeAsync(
                key,
                DashboardSlowCacheTtl,
                async (store, token) =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));

                    const int daysCount = 30;
                    var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                    var startDateLocal = CalendarPeriods
                        .StartOfLocalDay(nowUtc, location.Zone)
                        .AddDays(-(daysCount - 1));
                    var startUtc = CalendarPeriods.LocalToUtc(
                        startDateLocal,
                        location.Zone);

                    IReadOnlyList<DashboardModelPerformanceRow> rows;
                    try
                    {
                        rows = await store.GetDailyModelPerformanceAsync(
                                startUtc,
                                location.Name,
                                timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "failed to query model performance stats: " + ex.Message,
                            ex);
                    }

                    // Group by model while preserving the SQL order
                    // (date DESC, throughput DESC) inside each bucket.
                    var buckets = new Dictionary<string, (long TotalRequests, List<ModelPerformanceStatModel> Results)>(
                        StringComparer.Ordinal);

                    foreach (var row in rows)
                    {
                        double? ttft = row.FirstTokenMs is > 0 ? row.FirstTokenMs : null;
                        var stat = new ModelPerformanceStatModel(
                            row.Date,
                            row.ModelId,
                            row.Throughput,
                            ttft,
                            checked((int)row.RequestCount));

                        if (!buckets.TryGetValue(row.ModelId, out var bucket))
                        {
                            bucket = (0, []);
                        }

                        bucket.TotalRequests += row.RequestCount;
                        bucket.Results.Add(stat);
                        buckets[row.ModelId] = bucket;
                    }

                    var modelInfos = buckets
                        .Select(static pair => new ModelInfo(pair.Key, pair.Value.TotalRequests))
                        .ToList();

                    // Rank by request volume; throughput is the same value.
                    var topModels = CalculateConfidenceAndSort(
                        modelInfos,
                        static info => info.RequestCount,
                        static info => info.RequestCount,
                        TopPerformersLimit);

                    var statsResults = new List<ModelPerformanceStatModel>();
                    foreach (var item in topModels)
                    {
                        statsResults.AddRange(buckets[item.Stats.ModelId].Results);
                    }

                    return (IReadOnlyList<ModelPerformanceStatModel>)statsResults;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of queryResolver.ChannelPerformanceStats
    /// (channel_performance_helpers.go:1327-1405): the channel_probes
    /// aggregation is preferred; a non-empty result replaces the
    /// request-execution fallback entirely, and only an empty probe result
    /// runs the fallback. A probe query failure propagates wrapped.
    /// </summary>
    public async Task<IReadOnlyList<ChannelPerformanceStatModel>> GetChannelPerformanceStatsAsync(
        CancellationToken cancellationToken = default)
    {
        var location = await SystemTimeLocations.ResolveAsync(systems, cancellationToken)
            .ConfigureAwait(false);
        var key = "channelPerformanceStats:" + location.Name;

        await authz.RequireScopeAsync(ReadDashboard, cancellationToken).ConfigureAwait(false);

        return await cache.GetOrComputeAsync(
                key,
                DashboardSlowCacheTtl,
                async (store, token) =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));

                    const int daysCount = 30;
                    var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                    var startDateLocal = CalendarPeriods
                        .StartOfLocalDay(nowUtc, location.Zone)
                        .AddDays(-(daysCount - 1));
                    var startUtc = CalendarPeriods.LocalToUtc(
                        startDateLocal,
                        location.Zone);

                    IReadOnlyList<DashboardChannelPerformanceRow> rows;
                    try
                    {
                        rows = await store.GetDailyChannelPerformanceFromProbesAsync(
                                startUtc,
                                location.Name,
                                timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // The reference returns the probe error untouched by any
                        // fallback (channel_performance_helpers.go:1330-1334).
                        throw new InvalidOperationException(
                            "failed to get channel performance stats from probes: "
                            + ex.Message,
                            ex);
                    }

                    if (rows.Count == 0)
                    {
                        try
                        {
                            rows = await store.GetDailyChannelPerformanceAsync(
                                    startUtc,
                                    location.Name,
                                    timeout.Token)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                "failed to query channel performance stats from executions: "
                                + ex.Message,
                                ex);
                        }
                    }

                    var statsMap = new Dictionary<string, Dictionary<long, DashboardChannelPerformanceRow>>(
                        StringComparer.Ordinal);
                    var channelTotals = new Dictionary<long, long>();

                    foreach (var row in rows)
                    {
                        if (!statsMap.TryGetValue(row.Date, out var dayStats))
                        {
                            dayStats = [];
                            statsMap[row.Date] = dayStats;
                        }

                        dayStats[row.ChannelId] = row;
                        channelTotals[row.ChannelId] =
                            channelTotals.GetValueOrDefault(row.ChannelId) + row.RequestCount;
                    }

                    var channels = channelTotals
                        .Select(static pair => new ChannelInfo(pair.Key, pair.Value))
                        .ToList();

                    var topChannels = CalculateConfidenceAndSort(
                        channels,
                        static info => info.RequestCount,
                        static info => info.RequestCount,
                        TopPerformersLimit);

                    var topChannelIds = topChannels
                        .Select(static item => item.Stats.ChannelId)
                        .ToHashSet();

                    foreach (var dateStr in statsMap.Keys.ToList())
                    {
                        var dayStats = statsMap[dateStr];
                        foreach (var channelId in dayStats.Keys.ToList())
                        {
                            if (!topChannelIds.Contains(channelId))
                            {
                                dayStats.Remove(channelId);
                            }
                        }

                        if (dayStats.Count == 0)
                        {
                            statsMap.Remove(dateStr);
                        }
                    }

                    // Go iterates a map here; the port keeps a deterministic
                    // ascending order of the same id set (C195).
                    var validChannelIds = topChannelIds.Order().ToList();

                    IReadOnlyList<DashboardChannelRow> queriedChannels;
                    try
                    {
                        queriedChannels = validChannelIds.Count == 0
                            ? []
                            : await store.GetChannelsByIdsAsync(
                                    validChannelIds,
                                    includeDeleted: false,
                                    token)
                                .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "failed to query channel names: " + ex.Message,
                            ex);
                    }

                    var channelNames = new Dictionary<long, string>();
                    foreach (var channel in queriedChannels)
                    {
                        channelNames[channel.Id] = channel.Name;
                    }

                    var response = new List<ChannelPerformanceStatModel>();
                    for (var i = 0; i < daysCount; i++)
                    {
                        var dateStr = CalendarPeriods.FormatDate(startDateLocal.AddDays(i));

                        if (!statsMap.TryGetValue(dateStr, out var dayStats)
                            || dayStats.Count == 0)
                        {
                            continue;
                        }

                        // Go iterates the day map in random order; the port
                        // emits channels in ascending id order (C195).
                        foreach (var channelId in dayStats.Keys.Order())
                        {
                            var stats = dayStats[channelId];
                            var channelName = channelNames.GetValueOrDefault(channelId);
                            if (string.IsNullOrEmpty(channelName))
                            {
                                channelName = "channel-"
                                    + channelId.ToString(CultureInfo.InvariantCulture);
                            }

                            double? throughput = stats.Throughput is > 0
                                ? stats.Throughput
                                : null;
                            double? ttft = stats.FirstTokenMs is > 0
                                ? stats.FirstTokenMs
                                : null;

                            response.Add(new ChannelPerformanceStatModel(
                                dateStr,
                                channelId.ToString(CultureInfo.InvariantCulture),
                                channelName,
                                throughput,
                                ttft,
                                checked((int)stats.RequestCount)));
                        }
                    }

                    return (IReadOnlyList<ChannelPerformanceStatModel>)response;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    // -------------------------------------------------- token/cost by dimension

    /// <summary>Port of queryResolver.TokenStatsByChannel.</summary>
    public async Task<IReadOnlyList<TokenStatsByChannelModel>> GetTokenStatsByChannelAsync(
        string? timeWindow,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DashboardUsageAggregateRow> rows;
        try
        {
            rows = await LoadUsageAggregatesAsync(
                    DashboardUsageDimension.Channel,
                    timeWindow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get tokens by channel: " + ex.Message,
                ex);
        }

        var top = TopDashboardUsageAggregates(rows, static row => TotalTokens(row));

        return top
            .Select(static row => new TokenStatsByChannelModel(
                new GidValue("Channel", row.Id).ToString(),
                row.Name,
                row.InputTokens,
                row.OutputTokens,
                row.CachedTokens,
                row.ReasoningTokens,
                TotalTokens(row)))
            .ToList();
    }

    /// <summary>Port of queryResolver.TokenStatsByModel.</summary>
    public async Task<IReadOnlyList<TokenStatsByModelModel>> GetTokenStatsByModelAsync(
        string? timeWindow,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DashboardUsageAggregateRow> rows;
        try
        {
            rows = await LoadUsageAggregatesAsync(
                    DashboardUsageDimension.Model,
                    timeWindow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get tokens by model: " + ex.Message,
                ex);
        }

        var top = TopDashboardUsageAggregates(rows, static row => TotalTokens(row));

        return top
            .Select(static row => new TokenStatsByModelModel(
                row.Name,
                row.InputTokens,
                row.OutputTokens,
                row.CachedTokens,
                row.ReasoningTokens,
                TotalTokens(row)))
            .ToList();
    }

    /// <summary>Port of queryResolver.CostStatsByChannel.</summary>
    public async Task<IReadOnlyList<CostStatsByChannelModel>> GetCostStatsByChannelAsync(
        string? timeWindow,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DashboardUsageAggregateRow> rows;
        try
        {
            rows = await LoadUsageAggregatesAsync(
                    DashboardUsageDimension.Channel,
                    timeWindow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get cost by channel: " + ex.Message,
                ex);
        }

        var top = TopDashboardUsageAggregates(rows, static row => row.TotalCost);

        return top
            .Select(static row => new CostStatsByChannelModel(row.Name, row.TotalCost))
            .ToList();
    }

    /// <summary>Port of queryResolver.CostStatsByModel.</summary>
    public async Task<IReadOnlyList<CostStatsByModelModel>> GetCostStatsByModelAsync(
        string? timeWindow,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DashboardUsageAggregateRow> rows;
        try
        {
            rows = await LoadUsageAggregatesAsync(
                    DashboardUsageDimension.Model,
                    timeWindow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get cost by model: " + ex.Message,
                ex);
        }

        var top = TopDashboardUsageAggregates(rows, static row => row.TotalCost);

        return top
            .Select(static row => new CostStatsByModelModel(row.Name, row.TotalCost))
            .ToList();
    }

    /// <summary>Port of queryResolver.CostStatsByAPIKey.</summary>
    public async Task<IReadOnlyList<CostStatsByApiKeyModel>> GetCostStatsByApiKeyAsync(
        string? timeWindow,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DashboardUsageAggregateRow> rows;
        try
        {
            rows = await LoadUsageAggregatesAsync(
                    DashboardUsageDimension.ApiKey,
                    timeWindow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get cost by API key: " + ex.Message,
                ex);
        }

        var top = TopDashboardUsageAggregates(rows, static row => row.TotalCost);

        return top
            .Select(static row => new CostStatsByApiKeyModel(
                ApiKeyGid(row.Id),
                row.Name,
                row.TotalCost))
            .ToList();
    }

    // -------------------------------------------------------------- by user

    /// <summary>Port of queryResolver.UsageStatsByUser.</summary>
    public async Task<IReadOnlyList<UsageStatsByUserModel>> GetUsageStatsByUserAsync(
        string? timeWindow,
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException("user not found in context");
        }

        var projectId = identity.GetCurrentProjectId();
        if (projectId is null)
        {
            throw new InvalidOperationException("project ID not found in context");
        }

        // Only project owners or system owners may view usage by user.
        var isProjectOwner = current.IsOwner;
        if (!isProjectOwner)
        {
            var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
            if (loaded is not null
                && loaded.ProjectUsers.Any(projectUser =>
                    projectUser.ProjectId == projectId.Value && projectUser.IsOwner))
            {
                isProjectOwner = true;
            }
        }

        if (!isProjectOwner)
        {
            throw new InvalidOperationException(
                "permission denied: only project owners can view usage statistics");
        }

        // Owners bypass the UsageLog privacy chain entirely; the store query is
        // raw SQL, so there is nothing further to gate.
        DateTime? createdAtGte = null;
        DateTime? createdAtLte = null;

        if (!string.IsNullOrEmpty(timeWindow))
        {
            const string CustomPrefix = "custom:";
            if (timeWindow.StartsWith(CustomPrefix, StringComparison.Ordinal))
            {
                var value = timeWindow[CustomPrefix.Length..];
                var parts = value.Split(',');
                if (parts.Length == 2)
                {
                    if (TryParseRfc3339(parts[0], out var since))
                    {
                        createdAtGte = since;
                    }

                    if (TryParseRfc3339(parts[1], out var until))
                    {
                        createdAtLte = until;
                    }
                }
            }
            else
            {
                var location = await SystemTimeLocations.ResolveAsync(systems, cancellationToken)
                    .ConfigureAwait(false);
                var since = ParseTimeWindow(
                    timeWindow,
                    location.Zone,
                    timeProvider.GetUtcNow().UtcDateTime,
                    out var applyFilter);
                if (applyFilter)
                {
                    createdAtGte = since;
                }
            }
        }

        IReadOnlyList<DashboardUserUsageRow> rows;
        try
        {
            rows = await dashboard.GetUsageByUserAsync(
                    projectId.Value,
                    createdAtGte,
                    createdAtLte,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get usage stats by user: " + ex.Message,
                ex);
        }

        return rows
            .Select(row =>
            {
                var userName = GoStrings.TrimSpace(
                    row.FirstName + " " + row.LastName);
                if (userName.Length == 0)
                {
                    userName = row.Email;
                }

                return new UsageStatsByUserModel(
                    new GidValue("User", row.UserId).ToString(),
                    userName,
                    row.RequestCount,
                    row.TotalTokens,
                    row.TotalCost);
            })
            .ToList();
    }

    // ------------------------------------------------------------ shared bits

    /// <summary>
    /// Port of authz.WithScopeDecision + HasScope for read_dashboard
    /// (internal/authz/scope.go): allowed principals bypass every ent privacy
    /// rule. Delegates to the shared authz emulation so the principal rules
    /// stay in one place.
    /// </summary>
    private async Task<bool> HasReadDashboardAsync(CancellationToken cancellationToken)
        => await authz.HasScopeAsync(ReadDashboard, cancellationToken).ConfigureAwait(false);

    /// <summary>Port of parseTimeWindow (dashboard_helpers.go:249-273).</summary>
    private static DateTime? ParseTimeWindow(
        string? timeWindow,
        TimeZoneInfo zone,
        DateTime utcNow,
        out bool applyFilter)
    {
        applyFilter = false;
        if (timeWindow is null
            || timeWindow.Length == 0
            || string.Equals(timeWindow, "allTime", StringComparison.Ordinal))
        {
            return null;
        }

        applyFilter = true;
        var periods = CalendarPeriods.Compute(utcNow, zone);
        switch (timeWindow)
        {
            case "day":
                return periods.Today.Start;
            case "week":
                return periods.ThisWeek.Start;
            case "month":
                return periods.ThisMonth.Start;
            default:
                // Unknown value - default to allTime behavior (no filtering).
                applyFilter = false;
                return null;
        }
    }

    /// <summary>
    /// Port of calculateConfidenceAndSort (dashboard_helpers.go:115-181).
    /// The reference sorts with an unstable sort; the port's stable LINQ sort
    /// keeps equal-score rows in SQL order (C195).
    /// </summary>
    private static List<ScoredItem<T>> CalculateConfidenceAndSort<T>(
        IReadOnlyList<T> results,
        Func<T, long> getRequestCount,
        Func<T, double> getThroughput,
        int limit)
    {
        if (results.Count == 0)
        {
            return [];
        }

        var requestCounts = results
            .Select(getRequestCount)
            .OrderBy(static value => value)
            .ToList();

        var mid = requestCounts.Count / 2;
        double median = requestCounts.Count % 2 == 0
            ? (requestCounts[mid - 1] + requestCounts[mid]) / 2.0
            : requestCounts[mid];

        var scored = results
            .Select(item =>
            {
                var confidence = CalculateConfidenceLevel(getRequestCount(item), median);
                var score = confidence switch
                {
                    "high" => 3,
                    "medium" => 2,
                    "low" => 1,
                    _ => 0,
                };

                return new ScoredItem<T>(item, confidence, score);
            })
            .ToList();

        var filtered = scored
            .Where(static item => item.Confidence is "high" or "medium")
            .ToList();

        var toShow = filtered.Count >= limit ? filtered : scored;

        toShow = toShow
            .OrderByDescending(static item => item.Score)
            .ThenByDescending(item => getThroughput(item.Stats))
            .ToList();

        if (toShow.Count > limit)
        {
            toShow = toShow.Take(limit).ToList();
        }

        return toShow;
    }

    /// <summary>Port of qb.CalculateConfidenceLevel (qb/throughput.go:261-296).</summary>
    private static string CalculateConfidenceLevel(long requestCount, double median)
    {
        if (median == 0)
        {
            return "low";
        }

        const int MinRequestsForMedium = 100;
        const int MinRequestsForHigh = 500;

        if (requestCount < MinRequestsForMedium)
        {
            return "low";
        }

        var ratio = requestCount / median;
        if (ratio >= 1.5 && requestCount >= MinRequestsForHigh)
        {
            return "high";
        }

        if (ratio >= 0.5)
        {
            return "medium";
        }

        return "low";
    }

    /// <summary>
    /// Port of Go's <c>time.Parse(time.RFC3339, value)</c> for the custom usage
    /// window. Go also accepts fractional seconds with up to nine digits; the
    /// port accepts up to seven (the DateTimeOffset tick resolution, C195).
    /// </summary>
    private static bool TryParseRfc3339(string value, out DateTime utc)
    {
        // The trailing Z is a literal in these formats, so the parser needs
        // AssumeUniversal to place the value in UTC instead of the machine's
        // local offset.
        string[] utcFormats =
        [
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:ss,FFFFFFF'Z'",
        ];

        string[] offsetFormats =
        [
            "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
            "yyyy-MM-dd'T'HH:mm:ss,FFFFFFFzzz",
        ];

        if (DateTimeOffset.TryParseExact(
                value,
                utcFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            || DateTimeOffset.TryParseExact(
                value,
                offsetFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsed))
        {
            utc = parsed.UtcDateTime;
            return true;
        }

        utc = default;
        return false;
    }

    /// <summary>Billable total: reasoning is already inside completion tokens.</summary>
    private static long TotalTokens(DashboardUsageAggregateRow row)
        => row.InputTokens + row.OutputTokens;

    private static string ApiKeyGid(long id) => new GidValue("APIKey", id).ToString();
}
