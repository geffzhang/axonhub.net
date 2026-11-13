using AxonHub.Api.Management;
using AxonHub.Persistence;
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

/// <summary>Port of the <c>ChannelProbe</c> object type (ent.graphql:1157-1166).</summary>
/// <remarks>
/// The probe schema has no mixins and no privacy policy, so node(ChannelProbe)
/// reads the row as-is. Timestamp and the counters narrow like
/// <see cref="ChannelProbePointType"/> (C190).
/// </remarks>
[GraphQLName("ChannelProbe")]
public sealed class ChannelProbeType : INode
{
    [GraphQLIgnore]
    public ChannelProbeRecord Record { get; init; } = null!;

    [GraphQLType(typeof(IdType))]
    public string Id => new GidValue("ChannelProbe", Record.Id).ToString();

    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string ChannelID => new GidValue("Channel", Record.ChannelId).ToString();

    public int TotalRequestCount => checked((int)Record.TotalRequestCount);

    public int SuccessRequestCount => checked((int)Record.SuccessRequestCount);

    public double? AvgTokensPerSecond => Record.AvgTokensPerSecond;

    public double? AvgTimeToFirstTokenMs => Record.AvgTimeToFirstTokenMs;

    public int Timestamp => checked((int)Record.Timestamp);

    /// <summary>Port of the entgql Channel edge (gql_edge.go:187-194).</summary>
    [GraphQLName("channel")]
    public async Task<ChannelType> GetChannelAsync(
        [Service] ChannelQueryService channels,
        CancellationToken cancellationToken)
        => await channels.GetNodeAsync(Record.ChannelId, cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>Port of <c>GetChannelProbeDataInput</c> (channel_probe.graphql:14-16).</summary>
public sealed class GetChannelProbeDataInput
{
    [GraphQLName("channelIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string> ChannelIDs { get; set; } = [];
}

/// <summary>
/// Port of <c>ChannelProbeWhereInput</c> (ent.graphql). The probe table has no
/// mixins, so none of the predicates touches a soft-delete column.
/// </summary>
[GraphQLName("ChannelProbeWhereInput")]
public sealed class ChannelProbeWhereInput
{
    public ChannelProbeWhereInput? Not { get; set; }

    public IReadOnlyList<ChannelProbeWhereInput>? And { get; set; }

    public IReadOnlyList<ChannelProbeWhereInput>? Or { get; set; }

    [GraphQLName("id")]
    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLName("idNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? IdNEQ { get; set; }

    [GraphQLName("idIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    [GraphQLName("idNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdNotIn { get; set; }

    [GraphQLName("idGT")]
    [GraphQLType(typeof(IdType))]
    public string? IdGT { get; set; }

    [GraphQLName("idGTE")]
    [GraphQLType(typeof(IdType))]
    public string? IdGTE { get; set; }

    [GraphQLName("idLT")]
    [GraphQLType(typeof(IdType))]
    public string? IdLT { get; set; }

    [GraphQLName("idLTE")]
    [GraphQLType(typeof(IdType))]
    public string? IdLTE { get; set; }

    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelID { get; set; }

    [GraphQLName("channelIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelIDNEQ { get; set; }

    [GraphQLName("channelIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIDIn { get; set; }

    [GraphQLName("channelIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIDNotIn { get; set; }

    public int? TotalRequestCount { get; set; }

    public int? TotalRequestCountNEQ { get; set; }

    public IReadOnlyList<int>? TotalRequestCountIn { get; set; }

    public IReadOnlyList<int>? TotalRequestCountNotIn { get; set; }

    public int? TotalRequestCountGT { get; set; }

    public int? TotalRequestCountGTE { get; set; }

    public int? TotalRequestCountLT { get; set; }

    public int? TotalRequestCountLTE { get; set; }

    public int? SuccessRequestCount { get; set; }

    public int? SuccessRequestCountNEQ { get; set; }

    public IReadOnlyList<int>? SuccessRequestCountIn { get; set; }

    public IReadOnlyList<int>? SuccessRequestCountNotIn { get; set; }

    public int? SuccessRequestCountGT { get; set; }

    public int? SuccessRequestCountGTE { get; set; }

    public int? SuccessRequestCountLT { get; set; }

    public int? SuccessRequestCountLTE { get; set; }

    public double? AvgTokensPerSecond { get; set; }

    public double? AvgTokensPerSecondNEQ { get; set; }

    public IReadOnlyList<double>? AvgTokensPerSecondIn { get; set; }

    public IReadOnlyList<double>? AvgTokensPerSecondNotIn { get; set; }

    public double? AvgTokensPerSecondGT { get; set; }

    public double? AvgTokensPerSecondGTE { get; set; }

    public double? AvgTokensPerSecondLT { get; set; }

    public double? AvgTokensPerSecondLTE { get; set; }

    public bool? AvgTokensPerSecondIsNil { get; set; }

    public bool? AvgTokensPerSecondNotNil { get; set; }

    public double? AvgTimeToFirstTokenMs { get; set; }

    public double? AvgTimeToFirstTokenMsNEQ { get; set; }

    public IReadOnlyList<double>? AvgTimeToFirstTokenMsIn { get; set; }

    public IReadOnlyList<double>? AvgTimeToFirstTokenMsNotIn { get; set; }

    public double? AvgTimeToFirstTokenMsGT { get; set; }

    public double? AvgTimeToFirstTokenMsGTE { get; set; }

    public double? AvgTimeToFirstTokenMsLT { get; set; }

    public double? AvgTimeToFirstTokenMsLTE { get; set; }

    public bool? AvgTimeToFirstTokenMsIsNil { get; set; }

    public bool? AvgTimeToFirstTokenMsNotNil { get; set; }

    public int? Timestamp { get; set; }

    public int? TimestampNEQ { get; set; }

    public IReadOnlyList<int>? TimestampIn { get; set; }

    public IReadOnlyList<int>? TimestampNotIn { get; set; }

    public int? TimestampGT { get; set; }

    public int? TimestampGTE { get; set; }

    public int? TimestampLT { get; set; }

    public int? TimestampLTE { get; set; }

    public bool? HasChannel { get; set; }

    public IReadOnlyList<ChannelWhereInput>? HasChannelWith { get; set; }
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

    public static ChannelProbeType ToGraphQL(ChannelProbeRecord record) => new()
    {
        Record = record,
    };

    public static ChannelProbeListFilter? ToFilter(ChannelProbeWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new ChannelProbeListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdNEQ = where.IdNEQ is null ? null : GidValue.Parse(where.IdNEQ).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            IdNotIn = where.IdNotIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            IdGT = where.IdGT is null ? null : GidValue.Parse(where.IdGT).Id,
            IdGTE = where.IdGTE is null ? null : GidValue.Parse(where.IdGTE).Id,
            IdLT = where.IdLT is null ? null : GidValue.Parse(where.IdLT).Id,
            IdLTE = where.IdLTE is null ? null : GidValue.Parse(where.IdLTE).Id,
            ChannelId = where.ChannelID is null ? null : GidValue.Parse(where.ChannelID).Id,
            ChannelIdNEQ = where.ChannelIDNEQ is null ? null : GidValue.Parse(where.ChannelIDNEQ).Id,
            ChannelIdIn = where.ChannelIDIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            ChannelIdNotIn = where.ChannelIDNotIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            TotalRequestCount = where.TotalRequestCount,
            TotalRequestCountNEQ = where.TotalRequestCountNEQ,
            TotalRequestCountIn = where.TotalRequestCountIn?.Select(static value => (long)value).ToList(),
            TotalRequestCountNotIn = where.TotalRequestCountNotIn?
                .Select(static value => (long)value)
                .ToList(),
            TotalRequestCountGT = where.TotalRequestCountGT,
            TotalRequestCountGTE = where.TotalRequestCountGTE,
            TotalRequestCountLT = where.TotalRequestCountLT,
            TotalRequestCountLTE = where.TotalRequestCountLTE,
            SuccessRequestCount = where.SuccessRequestCount,
            SuccessRequestCountNEQ = where.SuccessRequestCountNEQ,
            SuccessRequestCountIn = where.SuccessRequestCountIn?
                .Select(static value => (long)value)
                .ToList(),
            SuccessRequestCountNotIn = where.SuccessRequestCountNotIn?
                .Select(static value => (long)value)
                .ToList(),
            SuccessRequestCountGT = where.SuccessRequestCountGT,
            SuccessRequestCountGTE = where.SuccessRequestCountGTE,
            SuccessRequestCountLT = where.SuccessRequestCountLT,
            SuccessRequestCountLTE = where.SuccessRequestCountLTE,
            AvgTokensPerSecond = where.AvgTokensPerSecond,
            AvgTokensPerSecondNEQ = where.AvgTokensPerSecondNEQ,
            AvgTokensPerSecondIn = where.AvgTokensPerSecondIn,
            AvgTokensPerSecondNotIn = where.AvgTokensPerSecondNotIn,
            AvgTokensPerSecondGT = where.AvgTokensPerSecondGT,
            AvgTokensPerSecondGTE = where.AvgTokensPerSecondGTE,
            AvgTokensPerSecondLT = where.AvgTokensPerSecondLT,
            AvgTokensPerSecondLTE = where.AvgTokensPerSecondLTE,
            AvgTokensPerSecondIsNil = where.AvgTokensPerSecondIsNil == true,
            AvgTokensPerSecondNotNil = where.AvgTokensPerSecondNotNil == true,
            AvgTimeToFirstTokenMs = where.AvgTimeToFirstTokenMs,
            AvgTimeToFirstTokenMsNEQ = where.AvgTimeToFirstTokenMsNEQ,
            AvgTimeToFirstTokenMsIn = where.AvgTimeToFirstTokenMsIn,
            AvgTimeToFirstTokenMsNotIn = where.AvgTimeToFirstTokenMsNotIn,
            AvgTimeToFirstTokenMsGT = where.AvgTimeToFirstTokenMsGT,
            AvgTimeToFirstTokenMsGTE = where.AvgTimeToFirstTokenMsGTE,
            AvgTimeToFirstTokenMsLT = where.AvgTimeToFirstTokenMsLT,
            AvgTimeToFirstTokenMsLTE = where.AvgTimeToFirstTokenMsLTE,
            AvgTimeToFirstTokenMsIsNil = where.AvgTimeToFirstTokenMsIsNil == true,
            AvgTimeToFirstTokenMsNotNil = where.AvgTimeToFirstTokenMsNotNil == true,
            Timestamp = where.Timestamp,
            TimestampNEQ = where.TimestampNEQ,
            TimestampIn = where.TimestampIn?.Select(static value => (long)value).ToList(),
            TimestampNotIn = where.TimestampNotIn?.Select(static value => (long)value).ToList(),
            TimestampGT = where.TimestampGT,
            TimestampGTE = where.TimestampGTE,
            TimestampLT = where.TimestampLT,
            TimestampLTE = where.TimestampLTE,
            HasChannel = where.HasChannel,
            HasChannelWith = where.HasChannelWith?
                .Select(static channel => ChannelMapping.ToFilter(channel))
                .OfType<ChannelListFilter>()
                .ToList(),
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<ChannelProbeListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<ChannelProbeListFilter>().ToList(),
        };
    }
}
