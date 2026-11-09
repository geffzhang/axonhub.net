using AxonHub.Api.Management;
using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the <c>ChannelProbePoint</c> type (channel_probe.graphql:1-7).</summary>
/// <remarks>
/// The reference's Timestamp and counters are Go ints (64-bit) serialized
/// through the GraphQL Int scalar; HotChocolate's Int is 32-bit, so the
/// projection uses <c>checked</c> narrowing (C190). Timestamps stay within
/// Int32 until 2038.
/// </remarks>
[GraphQLName("ChannelProbePoint")]
public sealed class ChannelProbePointType
{
    public int Timestamp { get; init; }

    public int TotalRequestCount { get; init; }

    public int SuccessRequestCount { get; init; }

    public double? AvgTokensPerSecond { get; init; }

    public double? AvgTimeToFirstTokenMs { get; init; }
}

/// <summary>Port of the <c>ChannelProbeData</c> type (channel_probe.graphql:9-12).</summary>
[GraphQLName("ChannelProbeData")]
public sealed class ChannelProbeDataType
{
    /// <summary>
    /// Port of channelProbeDataResolver.ChannelID
    /// (channel_probe.resolvers.go:15-21): always the Channel GUID type.
    /// </summary>
    [GraphQLName("channelID")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ChannelID { get; init; } = string.Empty;

    public IReadOnlyList<ChannelProbePointType> Points { get; init; } = [];
}

/// <summary>Port of <c>GetChannelProbeDataInput</c> (channel_probe.graphql:14-16).</summary>
public sealed class GetChannelProbeDataInput
{
    [GraphQLName("channelIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string> ChannelIDs { get; set; } = [];
}

/// <summary>Domain to GraphQL projection for the probe read.</summary>
public static class ChannelProbeMapping
{
    public static ChannelProbeDataType ToGraphQL(ChannelProbeDataModel data)
    {
        ArgumentNullException.ThrowIfNull(data);

        List<ChannelProbePointType> points = new(data.Points.Count);
        foreach (var point in data.Points)
        {
            points.Add(new ChannelProbePointType
            {
                Timestamp = checked((int)point.Timestamp),
                TotalRequestCount = checked((int)point.TotalRequestCount),
                SuccessRequestCount = checked((int)point.SuccessRequestCount),
                AvgTokensPerSecond = point.AvgTokensPerSecond,
                AvgTimeToFirstTokenMs = point.AvgTimeToFirstTokenMs,
            });
        }

        return new ChannelProbeDataType
        {
            // channel_probe.resolvers.go:17-20 hard-codes the Channel type.
            ChannelID = new GidValue("Channel", data.ChannelId).ToString(),
            Points = points,
        };
    }
}
