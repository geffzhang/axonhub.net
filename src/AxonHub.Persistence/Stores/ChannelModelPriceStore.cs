using Dapper;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>channel_model_prices</c> table.</summary>
public interface IChannelModelPriceStore
{
    Task<long> CreateAsync(
        ChannelModelPriceRecord price,
        CancellationToken cancellationToken = default);

    /// <summary>Live (not soft-deleted) prices of one channel, ordered by id.</summary>
    Task<IReadOnlyList<ChannelModelPriceRecord>> ListByChannelAsync(
        long channelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live prices of a channel that is itself live and enabled. This is the
    /// query preloadModelPrices makes against the enabled-channel snapshot.
    /// </summary>
    Task<IReadOnlyList<ChannelModelPriceRecord>> ListEnabledChannelPricesAsync(
        long channelId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>channel_model_prices</c> table. Writes go through
/// EF Core; reads use Dapper and honour the SoftDeleteMixin predicate
/// (<c>deleted_at = 0</c>).
/// </summary>
public sealed class ChannelModelPriceStore(AxonHubDbContext db) : IChannelModelPriceStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "model_id", "price",
        "reference_id", "channel_id"
        """;

    public async Task<long> CreateAsync(
        ChannelModelPriceRecord price,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(price);

        db.ChannelModelPrices.Add(price);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return price.Id;
    }

    public async Task<IReadOnlyList<ChannelModelPriceRecord>> ListByChannelAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"channel_model_prices\" WHERE \"channel_id\" = @ChannelId"
            + " AND \"deleted_at\" = 0 ORDER BY \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelModelPriceRecord>(
            new CommandDefinition(
                sql,
                new { ChannelId = channelId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ChannelModelPriceRecord>> ListEnabledChannelPricesAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        // GetEnabledChannel only holds channels with status = enabled and a live
        // soft-delete marker; an empty result therefore also covers a disabled
        // channel, matching the reference's lookup miss.
        var sql = """
            SELECT p."id", p."created_at", p."updated_at", p."deleted_at",
                   p."model_id", p."price", p."reference_id", p."channel_id"
            FROM "channel_model_prices" p
            INNER JOIN "channels" c ON c."id" = p."channel_id"
            WHERE p."channel_id" = @ChannelId
              AND p."deleted_at" = 0
              AND c."deleted_at" = 0
              AND c."status" = 'enabled'
            ORDER BY p."id";
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelModelPriceRecord>(
            new CommandDefinition(
                sql,
                new { ChannelId = channelId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }
}
