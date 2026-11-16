using AxonHub.Api.Management;
using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of the AnalyticsFilter input (internal/server/gql/analytics.graphql).
/// The GraphQL name is assigned in GraphQLSetup because HotChocolate names
/// input objects from the CLR type when they are discovered through a resolver
/// argument.
/// </summary>
public sealed class AnalyticsFilterInput
{
    /// <summary>Start of the range (inclusive), as an RFC3339 timestamp.</summary>
    [GraphQLName("startTime")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? StartTime { get; set; }

    /// <summary>End of the range (inclusive), as an RFC3339 timestamp.</summary>
    [GraphQLName("endTime")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EndTime { get; set; }

    [GraphQLName("projectIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ProjectIDs { get; set; }

    [GraphQLName("channelIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIDs { get; set; }

    [GraphQLName("modelIDs")]
    [GraphQLType(typeof(ListType<NonNullType<StringType>>))]
    public IReadOnlyList<string>? ModelIDs { get; set; }

    [GraphQLName("apiKeyIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ApiKeyIDs { get; set; }

    [GraphQLName("userIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? UserIDs { get; set; }
}

/// <summary>Port of AnalyticsMetadata (analytics.graphql).</summary>
[GraphQLName("AnalyticsMetadata")]
public sealed class AnalyticsMetadataType
{
    /// <summary>Timestamp of the earliest usage log, null if no data.</summary>
    [GraphQLName("earliestTime")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EarliestTime { get; init; }
}

/// <summary>Port of AnalyticsOverview (analytics.graphql).</summary>
[GraphQLName("AnalyticsOverview")]
public sealed class AnalyticsOverviewType
{
    [GraphQLName("totalTokens")]
    public int TotalTokens { get; init; }

    [GraphQLName("totalInputTokens")]
    public int TotalInputTokens { get; init; }

    [GraphQLName("totalCachedInputTokens")]
    public int TotalCachedInputTokens { get; init; }

    [GraphQLName("totalUncachedInputTokens")]
    public int TotalUncachedInputTokens { get; init; }

    [GraphQLName("totalOutputTokens")]
    public int TotalOutputTokens { get; init; }

    [GraphQLName("totalRequests")]
    public int TotalRequests { get; init; }

    [GraphQLName("totalCost")]
    public double TotalCost { get; init; }
}

/// <summary>Port of AnalyticsDailyStat (analytics.graphql).</summary>
[GraphQLName("AnalyticsDailyStat")]
public sealed class AnalyticsDailyStatType
{
    [GraphQLName("date")]
    public string Date { get; init; } = string.Empty;

    [GraphQLName("inputTokens")]
    public int InputTokens { get; init; }

    [GraphQLName("cachedInputTokens")]
    public int CachedInputTokens { get; init; }

    [GraphQLName("uncachedInputTokens")]
    public int UncachedInputTokens { get; init; }

    [GraphQLName("outputTokens")]
    public int OutputTokens { get; init; }

    [GraphQLName("totalTokens")]
    public int TotalTokens { get; init; }

    [GraphQLName("requestCount")]
    public int RequestCount { get; init; }

    [GraphQLName("cost")]
    public double Cost { get; init; }
}

/// <summary>Port of AnalyticsDimensionStat (analytics.graphql).</summary>
[GraphQLName("AnalyticsDimensionStat")]
public sealed class AnalyticsDimensionStatType
{
    [GraphQLName("id")]
    [GraphQLType(typeof(NonNullType<StringType>))]
    public string Id { get; init; } = string.Empty;

    [GraphQLName("name")]
    [GraphQLType(typeof(NonNullType<StringType>))]
    public string Name { get; init; } = string.Empty;

    [GraphQLName("requestCount")]
    public int RequestCount { get; init; }

    [GraphQLName("inputTokens")]
    public int InputTokens { get; init; }

    [GraphQLName("cachedInputTokens")]
    public int CachedInputTokens { get; init; }

    [GraphQLName("outputTokens")]
    public int OutputTokens { get; init; }

    [GraphQLName("totalTokens")]
    public int TotalTokens { get; init; }

    [GraphQLName("cost")]
    public double Cost { get; init; }

    /// <summary>Output token throughput in tokens per second, null when no valid latency metrics exist.</summary>
    [GraphQLName("tokensPerSecond")]
    public double? TokensPerSecond { get; init; }

    /// <summary>Average time to first token in milliseconds, null when no streaming request recorded a first token.</summary>
    [GraphQLName("ttftMs")]
    public double? TtftMs { get; init; }
}

/// <summary>Mapping between the GraphQL analytics types and the service models.</summary>
public static class AnalyticsMapping
{
    public static AnalyticsFilterModel ToModel(AnalyticsFilterInput filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return new AnalyticsFilterModel
        {
            StartTime = filter.StartTime,
            EndTime = filter.EndTime,
            ProjectIds = ManagementMapping.ParseGidList(filter.ProjectIDs),
            ChannelIds = ManagementMapping.ParseGidList(filter.ChannelIDs),
            ModelIds = filter.ModelIDs,
            ApiKeyIds = ManagementMapping.ParseGidList(filter.ApiKeyIDs),
            UserIds = ManagementMapping.ParseGidList(filter.UserIDs),
        };
    }

    public static AnalyticsOverviewType ToGraphQL(AnalyticsOverviewModel model) => new()
    {
        TotalTokens = model.TotalTokens,
        TotalInputTokens = model.TotalInputTokens,
        TotalCachedInputTokens = model.TotalCachedInputTokens,
        TotalUncachedInputTokens = model.TotalUncachedInputTokens,
        TotalOutputTokens = model.TotalOutputTokens,
        TotalRequests = model.TotalRequests,
        TotalCost = model.TotalCost,
    };

    public static AnalyticsDailyStatType ToGraphQL(AnalyticsDailyStatModel model) => new()
    {
        Date = model.Date,
        InputTokens = model.InputTokens,
        CachedInputTokens = model.CachedInputTokens,
        UncachedInputTokens = model.UncachedInputTokens,
        OutputTokens = model.OutputTokens,
        TotalTokens = model.TotalTokens,
        RequestCount = model.RequestCount,
        Cost = model.Cost,
    };

    public static AnalyticsDimensionStatType ToGraphQL(AnalyticsDimensionStatModel model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        RequestCount = model.RequestCount,
        InputTokens = model.InputTokens,
        CachedInputTokens = model.CachedInputTokens,
        OutputTokens = model.OutputTokens,
        TotalTokens = model.TotalTokens,
        Cost = model.Cost,
        TokensPerSecond = model.TokensPerSecond,
        TtftMs = model.TtftMs,
    };
}
