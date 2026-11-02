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

/// <summary>
/// Maps a raw <c>models.model_card</c> JSON document to the objects.ModelPrice
/// JSON to store, or null when the card yields no priced items
/// (modelCardToChannelModelPrice, channel_price.go:58-88). The Go wire shape
/// stays in the API layer's billing code.
/// </summary>
public delegate string? ModelCardPriceJsonFactory(string modelCardJson);

/// <summary>Port of generateReferenceID (channel_price.go:534-544).</summary>
public delegate string ChannelPriceReferenceIdFactory();

/// <summary>One resolved auto-price candidate (channelModelPriceTemplate).</summary>
public sealed record ChannelModelPriceTemplate(string ModelId, string PriceJson);

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

    /// <summary>
    /// Port of ensureChannelModelPrices (channel_price.go:262-315): fills prices
    /// for the given model IDs that have no live price yet, reading the costs
    /// from the model library. When a transaction is already open the writes
    /// join it (the reference's nested RunInTransaction); a standalone call
    /// opens and commits its own, like the reference. Returns whether any price
    /// row was created by this call.
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The message already carries the reference prefix ("failed to query
    /// existing channel model prices", "failed to query model prices from model
    /// library", or "failed to auto-configure channel model price: model_id=...").
    /// </exception>
    Task<bool> EnsureAutoPricesAsync(
        long channelId,
        IReadOnlyList<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        ChannelPriceReferenceIdFactory referenceIdFactory,
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

    /// <summary>channelModelPriceQueryBatchSize (channel_price.go:45).</summary>
    private const int QueryBatchSize = 500;

    public async Task<bool> EnsureAutoPricesAsync(
        long channelId,
        IReadOnlyList<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        ArgumentNullException.ThrowIfNull(modelCardToPriceJson);
        ArgumentNullException.ThrowIfNull(referenceIdFactory);

        // uniqueNonEmptyModelIDs keeps the first occurrence order.
        var uniqueModelIds = new List<string>(modelIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modelId in modelIds)
        {
            if (modelId.Length > 0 && seen.Add(modelId))
            {
                uniqueModelIds.Add(modelId);
            }
        }

        if (uniqueModelIds.Count == 0)
        {
            return false;
        }

        // ent's RunInTransaction reuses the enclosing logical transaction and
        // only opens one for a standalone call (channel_price.go:272).
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var changed = await EnsureAutoPricesCoreAsync(
                channelId,
                uniqueModelIds,
                modelCardToPriceJson,
                referenceIdFactory,
                cancellationToken)
            .ConfigureAwait(false);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    private async Task<bool> EnsureAutoPricesCoreAsync(
        long channelId,
        List<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken)
    {
        var existingModelIds = await ListExistingModelIdsAsync(channelId, modelIds, cancellationToken)
            .ConfigureAwait(false);
        var missingModelIds = new List<string>(modelIds.Count);
        foreach (var modelId in modelIds)
        {
            if (!existingModelIds.Contains(modelId))
            {
                missingModelIds.Add(modelId);
            }
        }

        if (missingModelIds.Count == 0)
        {
            return false;
        }

        var templates = await ResolveTemplatesAsync(
                missingModelIds,
                modelCardToPriceJson,
                cancellationToken)
            .ConfigureAwait(false);

        // applyChannelModelPriceTemplates stamps one now for the whole plan.
        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var template in templates)
        {
            try
            {
                var created = await CreatePriceIfMissingAsync(
                        channelId,
                        template,
                        referenceIdFactory(),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                changed = changed || created;
            }
            catch (StoreFailureException ex)
            {
                throw new StoreFailureException(
                    "failed to auto-configure channel model price: "
                        + "model_id=" + template.ModelId + ": " + ex.Message,
                    ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    "failed to auto-configure channel model price: "
                        + "model_id=" + template.ModelId + ": " + ex.Message,
                    ex);
            }
        }

        return changed;
    }

    private async Task<HashSet<string>> ListExistingModelIdsAsync(
        long channelId,
        List<string> modelIds,
        CancellationToken cancellationToken)
    {
        var existingModelIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            for (var start = 0; start < modelIds.Count; start += QueryBatchSize)
            {
                var batch = modelIds.Skip(start).Take(QueryBatchSize).ToArray();
                var rows = await connection.QueryAsync<string>(new CommandDefinition(
                    """
                    SELECT "model_id" FROM "channel_model_prices"
                    WHERE "channel_id" = @ChannelId
                      AND "model_id" = ANY(@ModelIds)
                      AND "deleted_at" = 0;
                    """,
                    new { ChannelId = channelId, ModelIds = batch },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                foreach (var modelId in rows)
                {
                    existingModelIds.Add(modelId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to query existing channel model prices: " + ex.Message,
                ex);
        }

        return existingModelIds;
    }

    private async Task<IReadOnlyList<ChannelModelPriceTemplate>> ResolveTemplatesAsync(
        List<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        CancellationToken cancellationToken)
    {
        // resolveChannelModelPriceTemplates keeps one card per model id; the
        // live-row unique index makes duplicates impossible.
        var modelCards = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            for (var start = 0; start < modelIds.Count; start += QueryBatchSize)
            {
                var batch = modelIds.Skip(start).Take(QueryBatchSize).ToArray();
                var rows = await connection.QueryAsync<ModelCardRow>(new CommandDefinition(
                    """
                    SELECT "model_id", "model_card" FROM "models"
                    WHERE "model_id" = ANY(@ModelIds)
                      AND "status" IN ('enabled', 'disabled')
                      AND "deleted_at" = 0;
                    """,
                    new { ModelIds = batch },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    modelCards[row.ModelId] = row.ModelCard;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to query model prices from model library: " + ex.Message,
                ex);
        }

        var templates = new List<ChannelModelPriceTemplate>(modelCards.Count);
        foreach (var modelId in modelIds)
        {
            if (!modelCards.TryGetValue(modelId, out var modelCard))
            {
                continue;
            }

            string? priceJson;
            try
            {
                priceJson = modelCardToPriceJson(modelCard);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ent decodes model_card while scanning the query rows, so a
                // malformed card fails under the same query prefix.
                throw new StoreFailureException(
                    "failed to query model prices from model library: " + ex.Message,
                    ex);
            }

            if (priceJson is null)
            {
                continue;
            }

            templates.Add(new ChannelModelPriceTemplate(modelId, priceJson));
        }

        return templates;
    }

    private async Task<bool> CreatePriceIfMissingAsync(
        long channelId,
        ChannelModelPriceTemplate template,
        string candidateReferenceId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO "channel_model_prices"
                    ("created_at", "updated_at", "deleted_at", "model_id", "price",
                     "reference_id", "channel_id")
                VALUES (@Now, @Now, 0, @ModelId, CAST(@Price AS jsonb), @ReferenceId, @ChannelId)
                ON CONFLICT ("channel_id", "model_id", "deleted_at") DO NOTHING;
                """,
                new
                {
                    Now = now,
                    ModelId = template.ModelId,
                    Price = template.PriceJson,
                    ReferenceId = candidateReferenceId,
                    ChannelId = channelId,
                },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to create channel model price: " + ex.Message,
                ex);
        }

        ChannelModelPriceRecord? entity;
        try
        {
            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            entity = await connection.QuerySingleOrDefaultAsync<ChannelModelPriceRecord>(
                new CommandDefinition(
                    "SELECT " + Columns
                        + " FROM \"channel_model_prices\""
                        + " WHERE \"channel_id\" = @ChannelId AND \"model_id\" = @ModelId"
                        + " AND \"deleted_at\" = 0;",
                    new { ChannelId = channelId, ModelId = template.ModelId },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to load channel model price after upsert: " + ex.Message,
                ex);
        }

        if (entity is null)
        {
            throw new StoreFailureException(
                "failed to load channel model price after upsert: ent: channel model price not found",
                new InvalidOperationException("ent: channel model price not found"));
        }

        // The candidate reference id identifies whether this transaction won
        // the upsert race; only the winner creates the initial version
        // (channel_price.go:131-139).
        if (entity.ReferenceId != candidateReferenceId)
        {
            return false;
        }

        await InsertActiveVersionAsync(entity, now, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private sealed class ModelCardRow
    {
        public string ModelId { get; set; } = string.Empty;

        public string ModelCard { get; set; } = "{}";
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
