using Dapper;

namespace AxonHub.Persistence;

/// <summary>
/// Persistence seam for the reference <c>channel_probes</c> table
/// (internal/server/biz/channel_probe.go). The table has no mixin, no privacy
/// policy and no writer in the port yet, so this seam only exposes the batch
/// read behind the <c>channelProbeData</c> root.
/// </summary>
public interface IChannelProbeStore
{
    /// <summary>
    /// Port of <c>client.ChannelProbe.Query().Where(ChannelIDIn(...),
    /// TimestampGTE(start), TimestampLTE(end)).Order(ent.Asc(Timestamp))</c>
    /// (channel_probe.go:375-382): inclusive bounds, ascending timestamp and no
    /// other ordering.
    /// </summary>
    Task<IReadOnlyList<ChannelProbeRecord>> QueryAsync(
        IReadOnlyList<long> channelIds,
        long startUnix,
        long endUnix,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>channel_probes</c> table. Reads use Dapper like
/// the other read-side seams; the port has no probe writer yet (C249).
/// </summary>
public sealed class ChannelProbeStore(AxonHubDbContext db) : IChannelProbeStore
{
    private const string Columns = """
        "id", "total_request_count", "success_request_count",
        "avg_tokens_per_second", "avg_time_to_first_token_ms", "timestamp", "channel_id"
        """;

    public async Task<IReadOnlyList<ChannelProbeRecord>> QueryAsync(
        IReadOnlyList<long> channelIds,
        long startUnix,
        long endUnix,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelIds);
        if (channelIds.Count == 0)
        {
            return [];
        }

        var sql = "SELECT " + Columns
            + " FROM \"channel_probes\""
            + " WHERE \"channel_id\" IN @ChannelIds"
            + " AND \"timestamp\" >= @StartUnix"
            + " AND \"timestamp\" <= @EndUnix"
            + " ORDER BY \"timestamp\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelProbeRecord>(
            new CommandDefinition(
                sql,
                new { ChannelIds = channelIds.ToArray(), StartUnix = startUnix, EndUnix = endUnix },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }
}
