using AxonHub.Api.Management;
using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of DashboardOverview (dashboard.graphql).</summary>
[GraphQLName("DashboardOverview")]
public sealed class DashboardOverviewType
{
    [GraphQLName("totalRequests")]
    public int TotalRequests { get; init; }

    [GraphQLName("requestStats")]
    public RequestStatsType RequestStats { get; init; } = new();

    [GraphQLName("failedRequests")]
    public int FailedRequests { get; init; }

    [GraphQLName("averageResponseTime")]
    public double? AverageResponseTime { get; init; }
}

/// <summary>Port of RequestStats (dashboard.graphql).</summary>
[GraphQLName("RequestStats")]
public sealed class RequestStatsType
{
    [GraphQLName("requestsToday")]
    public int RequestsToday { get; init; }

    [GraphQLName("requestsThisWeek")]
    public int RequestsThisWeek { get; init; }

    [GraphQLName("requestsLastWeek")]
    public int RequestsLastWeek { get; init; }

    [GraphQLName("requestsThisMonth")]
    public int RequestsThisMonth { get; init; }
}

/// <summary>Port of RequestStatsByChannel (dashboard.graphql).</summary>
[GraphQLName("RequestStatsByChannel")]
public sealed class RequestStatsByChannelType
{
    [GraphQLName("channelName")]
    public string ChannelName { get; init; } = string.Empty;

    [GraphQLName("count")]
    public int Count { get; init; }
}

/// <summary>Port of RequestStatsByModel (dashboard.graphql).</summary>
[GraphQLName("RequestStatsByModel")]
public sealed class RequestStatsByModelType
{
    [GraphQLName("modelId")]
    public string ModelId { get; init; } = string.Empty;

    [GraphQLName("count")]
    public int Count { get; init; }
}

/// <summary>Port of RequestStatsByAPIKey (dashboard.graphql).</summary>
[GraphQLName("RequestStatsByAPIKey")]
public sealed class RequestStatsByApiKeyType
{
    [GraphQLName("apiKeyId")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ApiKeyId { get; init; } = string.Empty;

    [GraphQLName("apiKeyName")]
    public string ApiKeyName { get; init; } = string.Empty;

    [GraphQLName("count")]
    public int Count { get; init; }
}

/// <summary>Port of TokenStatsByAPIKey (dashboard.graphql).</summary>
[GraphQLName("TokenStatsByAPIKey")]
public sealed class TokenStatsByApiKeyType
{
    [GraphQLName("apiKeyId")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ApiKeyId { get; init; } = string.Empty;

    [GraphQLName("apiKeyName")]
    public string ApiKeyName { get; init; } = string.Empty;

    [GraphQLName("inputTokens")]
    public int InputTokens { get; init; }

    [GraphQLName("outputTokens")]
    public int OutputTokens { get; init; }

    [GraphQLName("cachedTokens")]
    public int CachedTokens { get; init; }

    [GraphQLName("reasoningTokens")]
    public int ReasoningTokens { get; init; }

    [GraphQLName("totalTokens")]
    public int TotalTokens { get; init; }
}

/// <summary>Port of APIKeyTokenUsageStatsInput (dashboard.graphql).</summary>
[GraphQLName("APIKeyTokenUsageStatsInput")]
public sealed class ApiKeyTokenUsageStatsInput
{
    /// <summary>
    /// <c>apiKeyIds: [ID!]</c> - the list itself is nullable in the SDL; the
    /// resolver reports the reference's validation error for null or empty.
    /// </summary>
    [GraphQLName("apiKeyIds")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ApiKeyIds { get; set; }

    [GraphQLName("createdAtGTE")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtGTE { get; set; }

    [GraphQLName("createdAtLTE")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtLTE { get; set; }
}

/// <summary>Port of APIKeyTokenUsageStats (dashboard.graphql).</summary>
[GraphQLName("APIKeyTokenUsageStats")]
public sealed class ApiKeyTokenUsageStatsType
{
    [GraphQLName("apiKeyId")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ApiKeyId { get; init; } = string.Empty;

    [GraphQLName("inputTokens")]
    public int InputTokens { get; init; }

    [GraphQLName("outputTokens")]
    public int OutputTokens { get; init; }

    [GraphQLName("cachedTokens")]
    public int CachedTokens { get; init; }

    [GraphQLName("reasoningTokens")]
    public int ReasoningTokens { get; init; }

    [GraphQLName("topModels")]
    public IReadOnlyList<ModelTokenUsageStatsType> TopModels { get; init; } = [];
}

/// <summary>Port of ModelTokenUsageStats (dashboard.graphql).</summary>
[GraphQLName("ModelTokenUsageStats")]
public sealed class ModelTokenUsageStatsType
{
    [GraphQLName("modelId")]
    public string ModelId { get; init; } = string.Empty;

    [GraphQLName("inputTokens")]
    public int InputTokens { get; init; }

    [GraphQLName("outputTokens")]
    public int OutputTokens { get; init; }

    [GraphQLName("cachedTokens")]
    public int CachedTokens { get; init; }

    [GraphQLName("reasoningTokens")]
    public int ReasoningTokens { get; init; }
}

/// <summary>Port of DailyRequestStats (dashboard.graphql).</summary>
[GraphQLName("DailyRequestStats")]
public sealed class DailyRequestStatsType
{
    [GraphQLName("date")]
    public string Date { get; init; } = string.Empty;

    [GraphQLName("count")]
    public int Count { get; init; }

    [GraphQLName("tokens")]
    public int Tokens { get; init; }

    [GraphQLName("cost")]
    public double Cost { get; init; }
}

/// <summary>
/// Port of HourlyRequestStats (dashboard.graphql). The reference defines the
/// type but no root field returns it; it stays registered so the schema keeps
/// the same named type.
/// </summary>
[GraphQLName("HourlyRequestStats")]
public sealed class HourlyRequestStatsType
{
    [GraphQLName("hour")]
    public int Hour { get; init; }

    [GraphQLName("count")]
    public int Count { get; init; }
}

/// <summary>Port of TokenStats (dashboard.graphql).</summary>
[GraphQLName("TokenStats")]
public sealed class TokenStatsType
{
    [GraphQLName("totalInputTokensToday")]
    public int TotalInputTokensToday { get; init; }

    [GraphQLName("totalOutputTokensToday")]
    public int TotalOutputTokensToday { get; init; }

    [GraphQLName("totalCachedTokensToday")]
    public int TotalCachedTokensToday { get; init; }

    [GraphQLName("totalInputTokensThisWeek")]
    public int TotalInputTokensThisWeek { get; init; }

    [GraphQLName("totalOutputTokensThisWeek")]
    public int TotalOutputTokensThisWeek { get; init; }

    [GraphQLName("totalCachedTokensThisWeek")]
    public int TotalCachedTokensThisWeek { get; init; }

    [GraphQLName("totalInputTokensThisMonth")]
    public int TotalInputTokensThisMonth { get; init; }

    [GraphQLName("totalOutputTokensThisMonth")]
    public int TotalOutputTokensThisMonth { get; init; }

    [GraphQLName("totalCachedTokensThisMonth")]
    public int TotalCachedTokensThisMonth { get; init; }

    [GraphQLName("totalInputTokensAllTime")]
    public int TotalInputTokensAllTime { get; init; }

    [GraphQLName("totalOutputTokensAllTime")]
    public int TotalOutputTokensAllTime { get; init; }

    [GraphQLName("totalCachedTokensAllTime")]
    public int TotalCachedTokensAllTime { get; init; }

    [GraphQLName("lastUpdated")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? LastUpdated { get; init; }
}

/// <summary>Port of TopRequestsProjects (dashboard.graphql).</summary>
[GraphQLName("TopRequestsProjects")]
public sealed class TopRequestsProjectsType
{
    [GraphQLName("projectId")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ProjectId { get; init; } = string.Empty;

    [GraphQLName("projectName")]
    public string ProjectName { get; init; } = string.Empty;

    [GraphQLName("projectDescription")]
    public string ProjectDescription { get; init; } = string.Empty;

    [GraphQLName("requestCount")]
    public int RequestCount { get; init; }
}

/// <summary>Port of ChannelSuccessRate (dashboard.graphql).</summary>
[GraphQLName("ChannelSuccessRate")]
public sealed class ChannelSuccessRateType
{
    [GraphQLName("channelId")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ChannelId { get; init; } = string.Empty;

    [GraphQLName("channelName")]
    public string ChannelName { get; init; } = string.Empty;

    [GraphQLName("channelType")]
    public string ChannelType { get; init; } = string.Empty;

    [GraphQLName("channelDisabled")]
    public bool ChannelDisabled { get; init; }

    [GraphQLName("successCount")]
    public int SuccessCount { get; init; }

    [GraphQLName("failedCount")]
    public int FailedCount { get; init; }

    [GraphQLName("totalCount")]
    public int TotalCount { get; init; }

    [GraphQLName("successRate")]
    public double SuccessRate { get; init; }
}

/// <summary>Port of FastestChannel (dashboard.graphql).</summary>
[GraphQLName("FastestChannel")]
public sealed class FastestChannelType
{
    [GraphQLName("channelId")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ChannelId { get; init; } = string.Empty;

    [GraphQLName("channelName")]
    public string ChannelName { get; init; } = string.Empty;

    [GraphQLName("channelType")]
    public string ChannelType { get; init; } = string.Empty;

    [GraphQLName("throughput")]
    public double Throughput { get; init; }

    [GraphQLName("tokensCount")]
    public int TokensCount { get; init; }

    [GraphQLName("latencyMs")]
    public int LatencyMs { get; init; }

    [GraphQLName("requestCount")]
    public int RequestCount { get; init; }

    [GraphQLName("confidenceLevel")]
    public string ConfidenceLevel { get; init; } = string.Empty;
}

/// <summary>Port of FastestModel (dashboard.graphql).</summary>
[GraphQLName("FastestModel")]
public sealed class FastestModelType
{
    [GraphQLName("modelId")]
    public string ModelId { get; init; } = string.Empty;

    [GraphQLName("modelName")]
    public string ModelName { get; init; } = string.Empty;

    [GraphQLName("throughput")]
    public double Throughput { get; init; }

    [GraphQLName("tokensCount")]
    public int TokensCount { get; init; }

    [GraphQLName("latencyMs")]
    public int LatencyMs { get; init; }

    [GraphQLName("requestCount")]
    public int RequestCount { get; init; }

    [GraphQLName("confidenceLevel")]
    public string ConfidenceLevel { get; init; } = string.Empty;
}

/// <summary>Port of ModelPerformanceStat (dashboard.graphql).</summary>
[GraphQLName("ModelPerformanceStat")]
public sealed class ModelPerformanceStatType
{
    [GraphQLName("date")]
    public string Date { get; init; } = string.Empty;

    [GraphQLName("modelId")]
    public string ModelId { get; init; } = string.Empty;

    [GraphQLName("throughput")]
    public double? Throughput { get; init; }

    [GraphQLName("ttftMs")]
    public double? TtftMs { get; init; }

    [GraphQLName("requestCount")]
    public int RequestCount { get; init; }
}

/// <summary>Port of ChannelPerformanceStat (dashboard.graphql).</summary>
[GraphQLName("ChannelPerformanceStat")]
public sealed class ChannelPerformanceStatType
{
    [GraphQLName("date")]
    public string Date { get; init; } = string.Empty;

    [GraphQLName("channelId")]
    public string ChannelId { get; init; } = string.Empty;

    [GraphQLName("channelName")]
    public string ChannelName { get; init; } = string.Empty;

    [GraphQLName("throughput")]
    public double? Throughput { get; init; }

    [GraphQLName("ttftMs")]
    public double? TtftMs { get; init; }

    [GraphQLName("requestCount")]
    public int RequestCount { get; init; }
}

/// <summary>Port of FastestChannelsInput (dashboard.graphql).</summary>
public sealed class FastestChannelsInput
{
    [GraphQLName("timeWindow")]
    [GraphQLType(typeof(NonNullType<StringType>))]
    public string TimeWindow { get; set; } = string.Empty;

    [GraphQLName("limit")]
    [DefaultValue(5)]
    public int? Limit { get; set; }
}

/// <summary>Port of TokenStatsByChannel (dashboard.graphql).</summary>
[GraphQLName("TokenStatsByChannel")]
public sealed class TokenStatsByChannelType
{
    [GraphQLName("channelId")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ChannelId { get; init; } = string.Empty;

    [GraphQLName("channelName")]
    public string ChannelName { get; init; } = string.Empty;

    [GraphQLName("inputTokens")]
    public int InputTokens { get; init; }

    [GraphQLName("outputTokens")]
    public int OutputTokens { get; init; }

    [GraphQLName("cachedTokens")]
    public int CachedTokens { get; init; }

    [GraphQLName("reasoningTokens")]
    public int ReasoningTokens { get; init; }

    [GraphQLName("totalTokens")]
    public int TotalTokens { get; init; }
}

/// <summary>Port of TokenStatsByModel (dashboard.graphql).</summary>
[GraphQLName("TokenStatsByModel")]
public sealed class TokenStatsByModelType
{
    [GraphQLName("modelId")]
    public string ModelId { get; init; } = string.Empty;

    [GraphQLName("inputTokens")]
    public int InputTokens { get; init; }

    [GraphQLName("outputTokens")]
    public int OutputTokens { get; init; }

    [GraphQLName("cachedTokens")]
    public int CachedTokens { get; init; }

    [GraphQLName("reasoningTokens")]
    public int ReasoningTokens { get; init; }

    [GraphQLName("totalTokens")]
    public int TotalTokens { get; init; }
}

/// <summary>Port of CostStatsByChannel (dashboard.graphql).</summary>
[GraphQLName("CostStatsByChannel")]
public sealed class CostStatsByChannelType
{
    [GraphQLName("channelName")]
    public string ChannelName { get; init; } = string.Empty;

    [GraphQLName("cost")]
    public double Cost { get; init; }
}

/// <summary>Port of CostStatsByModel (dashboard.graphql).</summary>
[GraphQLName("CostStatsByModel")]
public sealed class CostStatsByModelType
{
    [GraphQLName("modelId")]
    public string ModelId { get; init; } = string.Empty;

    [GraphQLName("cost")]
    public double Cost { get; init; }
}

/// <summary>Port of CostStatsByAPIKey (dashboard.graphql).</summary>
[GraphQLName("CostStatsByAPIKey")]
public sealed class CostStatsByApiKeyType
{
    [GraphQLName("apiKeyId")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ApiKeyId { get; init; } = string.Empty;

    [GraphQLName("apiKeyName")]
    public string ApiKeyName { get; init; } = string.Empty;

    [GraphQLName("cost")]
    public double Cost { get; init; }
}

/// <summary>Port of UsageStatsByUser (dashboard.graphql).</summary>
[GraphQLName("UsageStatsByUser")]
public sealed class UsageStatsByUserType
{
    [GraphQLName("userId")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string UserId { get; init; } = string.Empty;

    [GraphQLName("userName")]
    public string UserName { get; init; } = string.Empty;

    [GraphQLName("requestCount")]
    public long RequestCount { get; init; }

    [GraphQLName("totalTokens")]
    public long TotalTokens { get; init; }

    [GraphQLName("totalCost")]
    public double TotalCost { get; init; }
}

/// <summary>Maps the dashboard service models onto the GraphQL types.</summary>
public static class DashboardMapping
{
    public static DashboardOverviewType ToGraphQL(DashboardOverviewModel model)
        => new()
        {
            TotalRequests = model.TotalRequests,
            RequestStats = ToGraphQL(model.RequestStats),
            FailedRequests = model.FailedRequests,
            AverageResponseTime = model.AverageResponseTime,
        };

    public static RequestStatsType ToGraphQL(RequestStatsModel model)
        => new()
        {
            RequestsToday = model.RequestsToday,
            RequestsThisWeek = model.RequestsThisWeek,
            RequestsLastWeek = model.RequestsLastWeek,
            RequestsThisMonth = model.RequestsThisMonth,
        };

    public static RequestStatsByChannelType ToGraphQL(RequestStatsByChannelModel model)
        => new()
        {
            ChannelName = model.ChannelName,
            Count = model.Count,
        };

    public static RequestStatsByModelType ToGraphQL(RequestStatsByModelModel model)
        => new()
        {
            ModelId = model.ModelId,
            Count = model.Count,
        };

    public static RequestStatsByApiKeyType ToGraphQL(RequestStatsByApiKeyModel model)
        => new()
        {
            ApiKeyId = model.ApiKeyId,
            ApiKeyName = model.ApiKeyName,
            Count = model.Count,
        };

    public static TokenStatsByApiKeyType ToGraphQL(TokenStatsByApiKeyModel model)
        => new()
        {
            ApiKeyId = model.ApiKeyId,
            ApiKeyName = model.ApiKeyName,
            InputTokens = model.InputTokens,
            OutputTokens = model.OutputTokens,
            CachedTokens = model.CachedTokens,
            ReasoningTokens = model.ReasoningTokens,
            TotalTokens = model.TotalTokens,
        };

    public static ApiKeyTokenUsageStatsType ToGraphQL(ApiKeyTokenUsageStatsModel model)
        => new()
        {
            ApiKeyId = model.ApiKeyId,
            InputTokens = model.InputTokens,
            OutputTokens = model.OutputTokens,
            CachedTokens = model.CachedTokens,
            ReasoningTokens = model.ReasoningTokens,
            TopModels = model.TopModels.Select(ToGraphQL).ToList(),
        };

    public static ModelTokenUsageStatsType ToGraphQL(ModelTokenUsageStatsModel model)
        => new()
        {
            ModelId = model.ModelId,
            InputTokens = model.InputTokens,
            OutputTokens = model.OutputTokens,
            CachedTokens = model.CachedTokens,
            ReasoningTokens = model.ReasoningTokens,
        };

    public static DailyRequestStatsType ToGraphQL(DailyRequestStatsModel model)
        => new()
        {
            Date = model.Date,
            Count = model.Count,
            Tokens = model.Tokens,
            Cost = model.Cost,
        };

    public static TokenStatsType ToGraphQL(TokenStatsModel model)
        => new()
        {
            TotalInputTokensToday = model.TotalInputTokensToday,
            TotalOutputTokensToday = model.TotalOutputTokensToday,
            TotalCachedTokensToday = model.TotalCachedTokensToday,
            TotalInputTokensThisWeek = model.TotalInputTokensThisWeek,
            TotalOutputTokensThisWeek = model.TotalOutputTokensThisWeek,
            TotalCachedTokensThisWeek = model.TotalCachedTokensThisWeek,
            TotalInputTokensThisMonth = model.TotalInputTokensThisMonth,
            TotalOutputTokensThisMonth = model.TotalOutputTokensThisMonth,
            TotalCachedTokensThisMonth = model.TotalCachedTokensThisMonth,
            TotalInputTokensAllTime = model.TotalInputTokensAllTime,
            TotalOutputTokensAllTime = model.TotalOutputTokensAllTime,
            TotalCachedTokensAllTime = model.TotalCachedTokensAllTime,
            LastUpdated = model.LastUpdated,
        };

    public static TopRequestsProjectsType ToGraphQL(TopRequestsProjectsModel model)
        => new()
        {
            ProjectId = model.ProjectId,
            ProjectName = model.ProjectName,
            ProjectDescription = model.ProjectDescription,
            RequestCount = model.RequestCount,
        };

    public static ChannelSuccessRateType ToGraphQL(ChannelSuccessRateModel model)
        => new()
        {
            ChannelId = model.ChannelId,
            ChannelName = model.ChannelName,
            ChannelType = model.ChannelType,
            ChannelDisabled = model.ChannelDisabled,
            SuccessCount = model.SuccessCount,
            FailedCount = model.FailedCount,
            TotalCount = model.TotalCount,
            SuccessRate = model.SuccessRate,
        };

    public static FastestChannelType ToGraphQL(FastestChannelModel model)
        => new()
        {
            ChannelId = model.ChannelId,
            ChannelName = model.ChannelName,
            ChannelType = model.ChannelType,
            Throughput = model.Throughput,
            TokensCount = model.TokensCount,
            LatencyMs = model.LatencyMs,
            RequestCount = model.RequestCount,
            ConfidenceLevel = model.ConfidenceLevel,
        };

    public static FastestModelType ToGraphQL(FastestModelModel model)
        => new()
        {
            ModelId = model.ModelId,
            ModelName = model.ModelName,
            Throughput = model.Throughput,
            TokensCount = model.TokensCount,
            LatencyMs = model.LatencyMs,
            RequestCount = model.RequestCount,
            ConfidenceLevel = model.ConfidenceLevel,
        };

    public static ModelPerformanceStatType ToGraphQL(ModelPerformanceStatModel model)
        => new()
        {
            Date = model.Date,
            ModelId = model.ModelId,
            Throughput = model.Throughput,
            TtftMs = model.TtftMs,
            RequestCount = model.RequestCount,
        };

    public static ChannelPerformanceStatType ToGraphQL(ChannelPerformanceStatModel model)
        => new()
        {
            Date = model.Date,
            ChannelId = model.ChannelId,
            ChannelName = model.ChannelName,
            Throughput = model.Throughput,
            TtftMs = model.TtftMs,
            RequestCount = model.RequestCount,
        };

    public static TokenStatsByChannelType ToGraphQL(TokenStatsByChannelModel model)
        => new()
        {
            ChannelId = model.ChannelId,
            ChannelName = model.ChannelName,
            InputTokens = model.InputTokens,
            OutputTokens = model.OutputTokens,
            CachedTokens = model.CachedTokens,
            ReasoningTokens = model.ReasoningTokens,
            TotalTokens = model.TotalTokens,
        };

    public static TokenStatsByModelType ToGraphQL(TokenStatsByModelModel model)
        => new()
        {
            ModelId = model.ModelId,
            InputTokens = model.InputTokens,
            OutputTokens = model.OutputTokens,
            CachedTokens = model.CachedTokens,
            ReasoningTokens = model.ReasoningTokens,
            TotalTokens = model.TotalTokens,
        };

    public static CostStatsByChannelType ToGraphQL(CostStatsByChannelModel model)
        => new()
        {
            ChannelName = model.ChannelName,
            Cost = model.Cost,
        };

    public static CostStatsByModelType ToGraphQL(CostStatsByModelModel model)
        => new()
        {
            ModelId = model.ModelId,
            Cost = model.Cost,
        };

    public static CostStatsByApiKeyType ToGraphQL(CostStatsByApiKeyModel model)
        => new()
        {
            ApiKeyId = model.ApiKeyId,
            ApiKeyName = model.ApiKeyName,
            Cost = model.Cost,
        };

    public static UsageStatsByUserType ToGraphQL(UsageStatsByUserModel model)
        => new()
        {
            UserId = model.UserId,
            UserName = model.UserName,
            RequestCount = model.RequestCount,
            TotalTokens = model.TotalTokens,
            TotalCost = model.TotalCost,
        };
}
