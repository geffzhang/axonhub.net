using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>
/// One planned price mutation from biz.calculatePriceChanges
/// (channel_price.go:315-380). The service computes the ordered plan before
/// opening the store transaction, exactly like the reference.
/// </summary>
public enum ChannelModelPriceChangeType
{
    Create,
    Update,
    Delete,
    Skip,
}

/// <summary>
/// One entry of the ordered price-change plan. <see cref="Existing"/> is only
/// used by <see cref="ChannelModelPriceChangeType.Skip"/>: the reference
/// appends the already-loaded row to the result without writing.
/// </summary>
public sealed record ChannelModelPriceChange(
    ChannelModelPriceChangeType Type,
    string ModelId,
    long? ExistingId,
    string? PriceJson,
    string? ReferenceId,
    ChannelModelPriceRecord? Existing);

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

    /// <summary>
    /// Port of ChannelService.SaveChannelModelPrices' transaction
    /// (channel_price.go:381-520). Delete/update actions archive the active
    /// version, create/update write the current row plus a new active version,
    /// deletes are soft-deletes, and the channel's updated_at is stamped so the
    /// reference's reload hook fires. Returns one result per action (null for
    /// deletes) in plan order.
    /// </summary>
    Task<IReadOnlyList<ChannelModelPriceRecord?>> ApplyAsync(
        long channelId,
        IReadOnlyList<ChannelModelPriceChange> changes,
        DateTime now,
        CancellationToken cancellationToken = default);

    /// <summary>Version rows of one price, ordered by id (test/read seam).</summary>
    Task<IReadOnlyList<ChannelModelPriceVersionRecord>> ListVersionsAsync(
        long channelModelPriceId,
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

    private const string VersionColumns = """
        "id", "created_at", "updated_at", "channel_id", "model_id",
        "channel_model_price_id", "price", "status", "effective_start_at",
        "effective_end_at", "reference_id"
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

    public async Task<IReadOnlyList<ChannelModelPriceRecord?>> ApplyAsync(
        long channelId,
        IReadOnlyList<ChannelModelPriceChange> changes,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var results = new ChannelModelPriceRecord?[changes.Count];

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            switch (change.Type)
            {
                case ChannelModelPriceChangeType.Skip:
                    results[index] = change.Existing;
                    break;

                case ChannelModelPriceChangeType.Delete:
                    await ArchiveActiveVersionsAsync(
                            change.ExistingId!.Value,
                            now,
                            "failed to archive channel model price versions for delete",
                            cancellationToken)
                        .ConfigureAwait(false);

                    // The SoftDeleteMixin turns DeleteOne into an UPDATE:
                    // deleted_at becomes Unix seconds and updated_at refreshes.
                    int deleted;
                    try
                    {
                        deleted = await db.ChannelModelPrices
                            .Where(row => row.Id == change.ExistingId.Value && row.DeletedAt == 0)
                            .ExecuteUpdateAsync(
                                setters => setters
                                    .SetProperty(
                                        row => row.DeletedAt,
                                        DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                                    .SetProperty(row => row.UpdatedAt, DateTime.UtcNow),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new StoreFailureException(
                            "failed to delete channel model price: " + ex.Message,
                            ex);
                    }

                    if (deleted == 0)
                    {
                        throw new StoreFailureException(
                            "failed to delete channel model price: ent: channel model price not found",
                            new InvalidOperationException("ent: channel model price not found"));
                    }

                    results[index] = null;
                    break;

                case ChannelModelPriceChangeType.Create:
                    results[index] = await CreateCurrentAsync(channelId, change, now, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case ChannelModelPriceChangeType.Update:
                    results[index] = await UpdateCurrentAsync(change, now, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException(
                        "unsupported channel model price change: " + change.Type);
            }
        }

        // Force update channel updated_at to trigger reload cache
        // (channel_price.go:503-505). The reference does not wrap this error;
        // UpdateOneID reports a missing (or soft-deleted) channel as not found.
        int touched;
        try
        {
            touched = await db.Channels
                .Where(row => row.Id == channelId && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }

        if (touched == 0)
        {
            throw new StoreFailureException(
                "ent: channel not found",
                new InvalidOperationException("ent: channel not found"));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<IReadOnlyList<ChannelModelPriceVersionRecord>> ListVersionsAsync(
        long channelModelPriceId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + VersionColumns
            + " FROM \"channel_model_price_versions\""
            + " WHERE \"channel_model_price_id\" = @ChannelModelPriceId ORDER BY \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelModelPriceVersionRecord>(
            new CommandDefinition(
                sql,
                new { ChannelModelPriceId = channelModelPriceId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    private async Task<ChannelModelPriceRecord> CreateCurrentAsync(
        long channelId,
        ChannelModelPriceChange change,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var entity = new ChannelModelPriceRecord
        {
            ChannelId = channelId,
            ModelId = change.ModelId,
            Price = change.PriceJson ?? "null",
            ReferenceId = change.ReferenceId ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            db.ChannelModelPrices.Add(entity);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to create channel model price: " + ex.Message,
                ex);
        }

        await InsertActiveVersionAsync(entity, now, cancellationToken).ConfigureAwait(false);
        return entity;
    }

    private async Task<ChannelModelPriceRecord> UpdateCurrentAsync(
        ChannelModelPriceChange change,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await ArchiveActiveVersionsAsync(
                change.ExistingId!.Value,
                now,
                "failed to archive old channel model price versions",
                cancellationToken)
            .ConfigureAwait(false);

        ChannelModelPriceRecord? entity;
        try
        {
            entity = await db.ChannelModelPrices
                .SingleOrDefaultAsync(
                    row => row.Id == change.ExistingId.Value && row.DeletedAt == 0,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to update channel model price: " + ex.Message,
                ex);
        }

        if (entity is null)
        {
            throw new StoreFailureException(
                "failed to update channel model price: ent: channel model price not found",
                new InvalidOperationException("ent: channel model price not found"));
        }

        entity.Price = change.PriceJson ?? "null";
        entity.ReferenceId = change.ReferenceId ?? string.Empty;
        entity.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to update channel model price: " + ex.Message,
                ex);
        }

        await InsertActiveVersionAsync(entity, now, cancellationToken).ConfigureAwait(false);
        return entity;
    }

    private async Task ArchiveActiveVersionsAsync(
        long channelModelPriceId,
        DateTime now,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.ChannelModelPriceVersions
                .Where(row => row.ChannelModelPriceId == channelModelPriceId
                    && row.Status == "active")
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Status, "archived")
                        .SetProperty(row => row.EffectiveEndAt, now)
                        .SetProperty(row => row.UpdatedAt, DateTime.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(errorPrefix + ": " + ex.Message, ex);
        }
    }

    private async Task InsertActiveVersionAsync(
        ChannelModelPriceRecord entity,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var version = new ChannelModelPriceVersionRecord
        {
            ChannelId = entity.ChannelId,
            ModelId = entity.ModelId,
            ChannelModelPriceId = entity.Id,
            Price = entity.Price,
            Status = "active",
            EffectiveStartAt = now,
            EffectiveEndAt = null,
            ReferenceId = entity.ReferenceId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        try
        {
            db.ChannelModelPriceVersions.Add(version);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to create channel model price version: " + ex.Message,
                ex);
        }
    }
}
