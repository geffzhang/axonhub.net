using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>
/// Raw channel reads and credential writes used by the model fetcher
/// (internal/server/biz/model_fetcher.go:404-453, 634-644 and
/// channel_llm.go:1451-1481). Unlike <see cref="IChannelWriteStore"/>, failures
/// are not prefixed here: the biz layer adds its own "failed to get channel"
/// wording, and the OAuth refresh callback writes the refreshed credentials
/// synchronously because the reference's token provider calls it from a
/// goroutine-free path.
/// </summary>
public interface IChannelModelCatalogStore
{
    /// <summary>
    /// Port of <c>Channel.Get(ctx, id)</c>: null when no live row exists.
    /// </summary>
    Task<ChannelRecord?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of refreshOAuthToken's <c>SetCredentials(updated).Save</c>.
    /// </summary>
    void UpdateCredentials(long id, string credentialsJson);
}

/// <summary>
/// The model-sync half of ChannelService (channel_model_sync.go): the
/// auto-sync channel query and the transactional re-read/merge/write/ensure
/// sequence.
/// </summary>
public interface IChannelModelSyncStore : IChannelModelCatalogStore
{
    /// <summary>
    /// Port of syncChannelModels' query (channel_model_sync.go:19-25): every
    /// live channel with <c>status == "enabled" AND
    /// auto_sync_supported_models == true</c>.
    /// </summary>
    Task<IReadOnlyList<ChannelRecord>> ListAutoSyncEnabledAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of syncChannelModelsForChannel's RunInTransaction
    /// (channel_model_sync.go:126-185). The channel is re-read inside the
    /// transaction (the caller's snapshot may predate a concurrent edit), the
    /// plan is built from that row, the supported models (and, when the plan
    /// pruned protocol overrides, the settings) are written, and the price
    /// ensure runs in the same transaction.
    /// </summary>
    /// <param name="buildPlan">
    /// Returns the merge result, or null when the merged list is empty (the
    /// reference logs and commits nothing).
    /// </param>
    /// <param name="ensurePrices">
    /// Runs inside the transaction; its failure rolls the whole sync back and
    /// is rethrown unwrapped, like the reference's nested
    /// ensureChannelModelPrices call.
    /// </param>
    /// <returns>
    /// The saved or re-read row and whether anything changed. The mutable row
    /// snapshot is returned even when no update was needed, matching ent's
    /// <c>updatedCh = current</c>.
    /// </returns>
    /// <exception cref="StoreFailureException">
    /// A read/update failed; the message already carries the reference prefix.
    /// </exception>
    Task<(ChannelRecord? Channel, bool Changed)> SyncAsync(
        long channelId,
        Func<ChannelRecord, ChannelModelSyncPlan?> buildPlan,
        Func<long, IReadOnlyList<string>, CancellationToken, Task<bool>> ensurePrices,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The merge decision built from the re-read channel row. Mirrors the locals
/// computed inside the reference's transaction.
/// </summary>
public sealed record ChannelModelSyncPlan(
    IReadOnlyList<string> MergedModels,
    string SupportedModelsJson,
    bool ModelsChanged,
    bool ModelProtocolsChanged,
    string? SettingsJson);

/// <summary>
/// Store for the model-sync slice of the reference <c>channels</c> table.
/// </summary>
public sealed class ChannelModelSyncStore(AxonHubDbContext db) : IChannelModelSyncStore
{
    public async Task<ChannelRecord?> GetAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }
    }

    public void UpdateCredentials(long id, string credentialsJson)
    {
        ArgumentNullException.ThrowIfNull(credentialsJson);

        // The reference's OnRefreshed callback is synchronous; bridge to the
        // async provider exactly like HttpExecutor does for its transport.
        db.Channels
            .Where(row => row.Id == id && row.DeletedAt == 0)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Credentials, credentialsJson)
                .SetProperty(row => row.UpdatedAt, DateTime.UtcNow))
            .GetAwaiter()
            .GetResult();
    }

    public async Task<IReadOnlyList<ChannelRecord>> ListAutoSyncEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Channels
                .AsNoTracking()
                .Where(row => row.DeletedAt == 0
                    && row.Status == "enabled"
                    && row.AutoSyncSupportedModels)
                .OrderBy(row => row.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }
    }

    public async Task<(ChannelRecord? Channel, bool Changed)> SyncAsync(
        long channelId,
        Func<ChannelRecord, ChannelModelSyncPlan?> buildPlan,
        Func<long, IReadOnlyList<string>, CancellationToken, Task<bool>> ensurePrices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildPlan);
        ArgumentNullException.ThrowIfNull(ensurePrices);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Re-read inside the transaction: the provider fetch that preceded this
        // call can take seconds, so the caller's snapshot may be stale.
        ChannelRecord? current;
        try
        {
            current = await LoadAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to reload channel for model sync: " + ex.Message,
                ex);
        }

        if (current is null)
        {
            throw new StoreFailureException(
                "failed to reload channel for model sync: ent: channel not found",
                new InvalidOperationException("channel not found"));
        }

        var plan = buildPlan(current);
        if (plan is null)
        {
            // Both fetched and manual models are empty: the reference logs and
            // returns nil without touching prices.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (current, false);
        }

        var now = DateTime.UtcNow;
        var result = current;
        if (plan.ModelsChanged)
        {
            var supportedModelsJson = plan.SupportedModelsJson;
            var settingsJson = plan.SettingsJson;
            var query = db.Channels.Where(row => row.Id == channelId && row.DeletedAt == 0);
            int affected;
            try
            {
                // Two expression trees because ExecuteUpdate's setter is an
                // Expression<Func<...>>; a statement-bodied lambda would bind
                // to the void-returning overload instead.
                affected = settingsJson is null
                    ? await query.ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(row => row.SupportedModels, supportedModelsJson)
                                .SetProperty(row => row.UpdatedAt, now),
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await query.ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(row => row.SupportedModels, supportedModelsJson)
                                .SetProperty(row => row.Settings, settingsJson)
                                .SetProperty(row => row.UpdatedAt, now),
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    "failed to update channel supported models: " + ex.Message,
                    ex);
            }

            if (affected == 0)
            {
                throw new StoreFailureException(
                    "failed to update channel supported models: ent: channel not found",
                    new InvalidOperationException("channel not found"));
            }

            result = Copy(current);
            result.SupportedModels = supportedModelsJson;
            result.Settings = plan.SettingsJson ?? current.Settings;
            result.UpdatedAt = now;
        }

        var pricesChanged = await ensurePrices(channelId, plan.MergedModels, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var changed = plan.ModelsChanged || plan.ModelProtocolsChanged || pricesChanged;
        return (result, changed);
    }

    private Task<ChannelRecord?> LoadAsync(long id, CancellationToken cancellationToken)
        => db.Channels
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id && row.DeletedAt == 0, cancellationToken);

    private static ChannelRecord Copy(ChannelRecord source) => new()
    {
        Id = source.Id,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        DeletedAt = source.DeletedAt,
        Type = source.Type,
        BaseUrl = source.BaseUrl,
        Name = source.Name,
        Status = source.Status,
        Credentials = source.Credentials,
        DisabledApiKeys = source.DisabledApiKeys,
        SupportedModels = source.SupportedModels,
        ManualModels = source.ManualModels,
        AutoSyncSupportedModels = source.AutoSyncSupportedModels,
        AutoSyncModelPattern = source.AutoSyncModelPattern,
        Tags = source.Tags,
        DefaultTestModel = source.DefaultTestModel,
        Policies = source.Policies,
        Settings = source.Settings,
        OrderingWeight = source.OrderingWeight,
        ErrorMessage = source.ErrorMessage,
        AutoDisabledAt = source.AutoDisabledAt,
        AutoDisableExpiresAt = source.AutoDisableExpiresAt,
        Remark = source.Remark,
        Endpoints = source.Endpoints,
    };
}
