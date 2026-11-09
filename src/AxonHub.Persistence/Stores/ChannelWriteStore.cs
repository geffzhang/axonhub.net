using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>
/// Port of the ent.UpdateChannelInputBuilder used by
/// ChannelService.UpdateChannel (channel.go:849-1203). A null JSON column means
/// "not set" (ent's SetX only runs for a non-nil input), while the clear flags
/// are applied after the corresponding set, mirroring the reference's builder
/// order. JSON strings are serialized by the caller so this layer stays free of
/// Go wire-format knowledge.
/// </summary>
public sealed class ChannelUpdateRecord
{
    public string? Type { get; init; }

    public string? BaseUrl { get; init; }

    public string? Name { get; init; }

    public string? DefaultTestModel { get; init; }

    public int? OrderingWeight { get; init; }

    public bool? AutoSyncSupportedModels { get; init; }

    public string? SupportedModelsJson { get; init; }

    public string? ManualModelsJson { get; init; }

    public string? TagsJson { get; init; }

    public string? SettingsJson { get; init; }

    public string? PoliciesJson { get; init; }

    public string? CredentialsJson { get; init; }

    public string? Remark { get; init; }

    public bool ClearRemark { get; init; }

    public string? AutoSyncModelPattern { get; init; }

    public bool ClearAutoSyncModelPattern { get; init; }

    public string? EndpointsJson { get; init; }

    public bool ClearErrorMessage { get; init; }

    /// <summary>
    /// Set for identity/endpoint/Command Code changes: the update only matches
    /// while updated_at still equals the value loaded before validation.
    /// </summary>
    public DateTime? ExpectedUpdatedAt { get; init; }
}

/// <summary>
/// Port of the Channel.UpdateOneID builder used by the API key bookkeeping
/// (channel_apikey.go:84-108, 181-189, 211-217, 262-268, 347-353):
/// disabled_api_keys is always written; credentials only when set; the recovery
/// columns follow their clear flags. The reference applies no updated_at guard
/// to these writes.
/// </summary>
public sealed class ChannelApiKeyStateRecord
{
    public long Id { get; init; }

    public required string DisabledApiKeysJson { get; init; }

    public string? CredentialsJson { get; init; }

    public string? Status { get; set; }

    public string? ErrorMessage { get; set; }

    public bool ClearErrorMessage { get; set; }

    public DateTime? AutoDisabledAt { get; set; }

    public bool ClearAutoDisabledAt { get; set; }

    public bool ClearAutoDisableExpiresAt { get; set; }
}

/// <summary>
/// Port of biz.ChannelOrderingItem (channel_bulk.go:18-21): one channel's new
/// failover priority.
/// </summary>
public sealed record ChannelOrderingItem(long Id, int OrderingWeight);

/// <summary>
/// Port of the settings/version projection read by
/// ChannelService.UpdateChannelSettings (channel_settings.go:160-166):
/// <c>FieldSettings</c> plus <c>FieldUpdatedAt</c> over the soft-delete
/// predicate. The merge is guarded on <see cref="UpdatedAt"/>.
/// </summary>
public sealed record ChannelSettingsVersion(string? Settings, DateTime UpdatedAt);

/// <summary>
/// Write seam for the channel lifecycle mutations of biz.ChannelService and
/// biz.ChannelService bulk helpers (channel.go:1205-1290,
/// channel_bulk.go:163-462). Each method owns the reference's checks and its
/// per-mutation transaction; callers only add the biz-layer error prefixes.
/// </summary>
public interface IChannelWriteStore
{
    /// <summary>
    /// Row snapshot for ChannelService.UpdateChannel's identity read. Returns
    /// null when no live row exists.
    /// </summary>
    Task<ChannelRecord?> GetForUpdateAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the ent <c>Channel.Get</c> read used by SaveChannelEndpoints
    /// (channel.go:1246-1250): returns null when no live row exists. Unlike
    /// <see cref="GetForUpdateAsync"/>, failures carry the reference's
    /// "failed to get channel" prefix.
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The read failed; the message already carries "failed to get channel".
    /// </exception>
    Task<ChannelRecord?> GetAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the settings merge read of ChannelService.UpdateChannelSettings
    /// (channel_settings.go:160-166): projects only settings and updated_at for
    /// a live row. Returns null when no live row exists; the caller raises
    /// "failed to load channel settings: ent: channel not found".
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The read failed; the message already carries "failed to load channel settings".
    /// </exception>
    Task<ChannelSettingsVersion?> GetSettingsVersionAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the update name check: <c>Name(...) AND IDNEQ(id)</c> over the
    /// soft-delete predicate. The caller raises the coded duplicate error.
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The query failed; the message carries "failed to check channel name".
    /// </exception>
    Task<bool> NameExistsAsync(
        long id,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the UpdateChannel builder + Save: applies only the provided
    /// columns, stamps updated_at and honours the loaded-version guard. Returns
    /// null when the row vanished or the guard no longer matches (both surface
    /// as ent's not-found -> "channel was updated concurrently"). The snapshot
    /// supplies the identity and the untouched columns of the returned row.
    /// </summary>
    /// <param name="afterUpdate">
    /// Runs inside the same transaction as the update, exactly like the
    /// reference's nested ensureChannelModelPrices call (channel.go:1163-1167);
    /// its failures roll the update back and are rethrown unwrapped.
    /// </param>
    /// <exception cref="StoreFailureException">
    /// The read/update failed; the message already carries "failed to update channel".
    /// </exception>
    Task<ChannelRecord?> UpdateAsync(
        ChannelRecord snapshot,
        ChannelUpdateRecord update,
        Func<long, CancellationToken, Task>? afterUpdate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ChannelService.SaveChannelEndpoints' save half
    /// (channel.go:1262-1276): writes only the endpoints column under the
    /// loaded updated_at guard and returns the saved row. A null result means
    /// the row vanished or the guard no longer matches (ent not-found, surfaced
    /// by the caller as "channel was updated concurrently").
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The update failed; the message already carries "failed to update channel endpoints".
    /// </exception>
    Task<ChannelRecord?> SaveEndpointsAsync(
        ChannelRecord snapshot,
        string endpointsJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the Channel.UpdateOneID(...).Save writes of the API key
    /// bookkeeping (channel_apikey.go): always writes disabled_api_keys, writes
    /// credentials only when provided, and applies the recovery-column clear
    /// flags. Returns false when the live row no longer exists (ent not-found).
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The update failed; the message is the raw provider error (the caller
    /// adds the reference's per-operation prefix).
    /// </exception>
    Task<bool> UpdateApiKeyStateAsync(
        ChannelApiKeyStateRecord state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the create half of ChannelService.CreateChannel: checks the live
    /// name (soft-delete predicate), then inserts the row. Returns null when a
    /// live channel already owns the name (the caller raises the coded
    /// duplicate-name error); a failed insert is a store failure.
    /// </summary>
    /// <param name="afterInsert">
    /// Runs inside the same transaction as the insert, exactly like the
    /// reference's nested ensureChannelModelPrices call (channel.go:647-660);
    /// its failures roll the insert back and are rethrown unwrapped.
    /// </param>
    /// <exception cref="StoreFailureException">
    /// The name check or insert failed; the message already carries the
    /// reference prefix ("failed to check channel name"/"failed to create channel").
    /// </exception>
    Task<ChannelRecord?> CreateAsync(
        ChannelRecord channel,
        Func<long, CancellationToken, Task>? afterInsert = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of BulkCreateChannels' existing-name snapshot
    /// (channel_bulk.go:82-90): every live channel name, in storage order. The
    /// caller builds the numbering set from it.
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The query failed; the message already carries
    /// "failed to query existing channels".
    /// </exception>
    Task<IReadOnlyList<string>> ListLiveNamesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ChannelService.DuplicateChannel's transaction
    /// (channel_duplicate.go:17-84). Opens one transaction for the whole
    /// mutation and runs the reference's exact order: load the live source
    /// ("failed to get source channel"), let <paramref name="inherit"/> apply
    /// the endpoint/credential fallbacks, check the live name ("failed to
    /// check channel name"), build the validated record, insert it ("failed to
    /// create channel"), then run <paramref name="afterInsert"/> (price copy
    /// and ensure) inside the same transaction.
    /// </summary>
    /// <returns>
    /// Null when a live channel already owns <paramref name="name"/>; the
    /// caller raises the coded duplicate-name error. A missing source is a
    /// store failure so the reference's prefix survives.
    /// </returns>
    /// <exception cref="StoreFailureException">
    /// The source read, name check or insert failed; the message already
    /// carries the reference prefix.
    /// </exception>
    Task<ChannelRecord?> DuplicateAsync(
        long sourceId,
        string name,
        Action<ChannelRecord> inherit,
        Func<ChannelRecord> buildRecord,
        Func<long, CancellationToken, Task>? afterInsert = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of BulkCreateChannels' transaction (channel_bulk.go:100-155): runs
    /// <paramref name="beforeInsert"/> (the once-per-batch price-template
    /// resolution) before the first insert, then builds one record per factory,
    /// inserts it and runs <paramref name="afterEachInsert"/> with the same
    /// transaction. Every insert failure is wrapped with
    /// "failed to create channel '&lt;name&gt;'" like the reference's loop; a
    /// failure in any step rolls the whole batch back.
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The template resolution or an insert failed; the message already carries
    /// the reference prefix.
    /// </exception>
    Task<IReadOnlyList<ChannelRecord>> CreateManyAsync(
        IReadOnlyList<Func<ChannelRecord>> recordFactories,
        Func<CancellationToken, Task>? beforeInsert = null,
        Func<ChannelRecord, CancellationToken, Task>? afterEachInsert = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of one BulkImportChannels row (channel_bulk.go:518-539): a direct
    /// ent <c>Channel.Create()</c> with no name pre-check and no normalization,
    /// followed by the row's price ensure in the same transaction. The failing
    /// row's raw error text is what the caller appends to the result, so the
    /// store never adds a prefix. The context's change tracker is cleared after
    /// a rollback so the next row starts from the reference's clean state.
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The insert or <paramref name="afterInsert"/> failed; the message is the
    /// unwrapped provider/ensure error.
    /// </exception>
    Task<ChannelRecord> ImportCreateAsync(
        ChannelRecord channel,
        Func<long, CancellationToken, Task>? afterInsert = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ChannelService.UpdateChannelStatus: sets the status and clears the
    /// auto-disable markers. Returns null when the live row does not exist.
    /// </summary>
    Task<ChannelRecord?> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ChannelService.DeleteChannel: soft delete (deleted_at = Unix
    /// seconds). Returns false when no live row matched.
    /// </summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ChannelService.bulkUpdateChannelStatus: verifies every id exists
    /// and updates all of them inside one transaction.
    /// </summary>
    /// <exception cref="ExpectedRowsMismatchException">
    /// The live row count differs from <paramref name="ids"/>'s count.
    /// </exception>
    /// <exception cref="StoreFailureException">
    /// The count/update failed; the message already carries the reference prefix.
    /// </exception>
    Task BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        bool clearErrorMessage,
        string action,
        CancellationToken cancellationToken = default);

    /// <summary>Port of ChannelService.BulkDeleteChannels (soft delete, no id check).</summary>
    Task BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ChannelService.BulkManageChannelTags: removes, then appends tags in
    /// one transaction, updating each changed channel under its loaded
    /// updated_at guard.
    /// </summary>
    /// <remarks>
    /// The reference receives already-normalized tag lists and applies
    /// <c>normalizeChannelTags</c> idempotently; the caller normalizes once.
    /// </remarks>
    /// <exception cref="ExpectedRowsMismatchException">The id count differs from the live row count.</exception>
    /// <exception cref="StoreFailureException">
    /// A query or per-channel update failed; the message already carries the
    /// reference prefix.
    /// </exception>
    Task ManageTagsAsync(
        IReadOnlyList<long> ids,
        IReadOnlyList<string> addTags,
        IReadOnlyList<string> removeTags,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ChannelService.BulkUpdateChannelOrdering (channel_bulk.go:23-45):
    /// sets each channel's ordering_weight and returns the saved entities. The
    /// reference relies on the GraphQL Transactioner for atomicity; the store
    /// runs the whole batch in one transaction.
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// A row is missing or an update failed; the message already carries
    /// "failed to update channel %d".
    /// </exception>
    Task<IReadOnlyList<ChannelRecord>> BulkUpdateOrderingAsync(
        IReadOnlyList<ChannelOrderingItem> items,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ChannelService.BulkUpdateChannelAutoDisable
    /// (channel_bulk.go:263-340): verifies the live row count, then writes the
    /// auto-disable mode (and, for <c>custom</c>, the pre-normalized rules
    /// JSON) to every channel's policies. Stream and other policy fields stay.
    /// </summary>
    /// <exception cref="ExpectedRowsMismatchException">The id count differs from the live row count.</exception>
    /// <exception cref="StoreFailureException">
    /// A query or per-channel update failed; the message already carries the
    /// reference prefix.
    /// </exception>
    Task<IReadOnlyList<ChannelRecord>> BulkUpdateAutoDisableAsync(
        IReadOnlyList<long> ids,
        string mode,
        string? rulesJson,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the channel lifecycle writes. Writes go through EF Core (change
/// tracking + <c>SaveChanges</c>); every query honours the reference's
/// SoftDeleteMixin predicate (<c>deleted_at = 0</c>).
/// </summary>
public sealed class ChannelWriteStore(AxonHubDbContext db) : IChannelWriteStore
{
    public async Task<ChannelRecord?> GetForUpdateAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to load channel provider identity: " + ex.Message,
                ex);
        }
    }

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
            throw new StoreFailureException("failed to get channel: " + ex.Message, ex);
        }
    }

    private Task<ChannelRecord?> LoadAsync(long id, CancellationToken cancellationToken)
        => db.Channels
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id && row.DeletedAt == 0, cancellationToken);

    public async Task<ChannelSettingsVersion?> GetSettingsVersionAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // ent: Select(channel.FieldSettings, channel.FieldUpdatedAt).Get(ctx, id)
            // with the SoftDeleteMixin predicate; a missing row is not an error.
            return await db.Channels
                .AsNoTracking()
                .Where(row => row.Id == id && row.DeletedAt == 0)
                .Select(row => new ChannelSettingsVersion(row.Settings, row.UpdatedAt))
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to load channel settings: " + ex.Message,
                ex);
        }
    }

    public async Task<bool> NameExistsAsync(
        long id,
        string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Channel.Query().Where(channel.Name(...), channel.IDNEQ(id)).First:
            // the SoftDeleteMixin predicate only considers live rows.
            return await db.Channels
                .AnyAsync(row => row.Name == name && row.Id != id && row.DeletedAt == 0, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException("failed to check channel name: " + ex.Message, ex);
        }
    }

    public async Task<ChannelRecord?> UpdateAsync(
        ChannelRecord snapshot,
        ChannelUpdateRecord update,
        Func<long, CancellationToken, Task>? afterUpdate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(update);

        var query = db.Channels.Where(row => row.Id == snapshot.Id && row.DeletedAt == 0);
        if (update.ExpectedUpdatedAt is { } expectedUpdatedAt)
        {
            // ent's builder uses Where(UpdatedAtEQ(...)) only for identity,
            // endpoint and Command Code settings changes (channel.go:1074-1076).
            query = query.Where(row => row.UpdatedAt == expectedUpdatedAt);
        }

        // Every column is assigned either the caller's value or itself, so the
        // single UPDATE only changes provided fields while the version guard
        // stays in the same statement (ent mut.Save).
        bool? autoSync = update.AutoSyncSupportedModels;
        long? orderingWeight = update.OrderingWeight;
        var setRemark = update.ClearRemark || update.Remark is not null;
        var remark = update.ClearRemark ? null : update.Remark;
        var setAutoSyncModelPattern =
            update.ClearAutoSyncModelPattern || update.AutoSyncModelPattern is not null;
        var autoSyncModelPattern = update.ClearAutoSyncModelPattern
            ? null
            : update.AutoSyncModelPattern;
        var now = DateTime.UtcNow;

        // UpdateChannel wraps the save and the follow-up ensureChannelModelPrices
        // in one RunInTransaction (channel.go:1072-1172); the hook joins it.
        // ent's RunInTransaction reuses an enclosing transaction, so when an
        // ambient one exists (the quota routing startup migration) it owns the
        // commit and this call must not begin or commit a nested transaction.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        int affected;
        try
        {
            affected = await query
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Type, row => update.Type ?? row.Type)
                        .SetProperty(row => row.BaseUrl, row => update.BaseUrl ?? row.BaseUrl)
                        .SetProperty(row => row.Name, row => update.Name ?? row.Name)
                        .SetProperty(
                            row => row.DefaultTestModel,
                            row => update.DefaultTestModel ?? row.DefaultTestModel)
                        .SetProperty(
                            row => row.OrderingWeight,
                            row => orderingWeight ?? row.OrderingWeight)
                        .SetProperty(
                            row => row.AutoSyncSupportedModels,
                            row => autoSync ?? row.AutoSyncSupportedModels)
                        .SetProperty(
                            row => row.SupportedModels,
                            row => update.SupportedModelsJson ?? row.SupportedModels)
                        .SetProperty(
                            row => row.ManualModels,
                            row => update.ManualModelsJson ?? row.ManualModels)
                        .SetProperty(row => row.Tags, row => update.TagsJson ?? row.Tags)
                        .SetProperty(row => row.Settings, row => update.SettingsJson ?? row.Settings)
                        .SetProperty(row => row.Policies, row => update.PoliciesJson ?? row.Policies)
                        .SetProperty(
                            row => row.Credentials,
                            row => update.CredentialsJson ?? row.Credentials)
                        .SetProperty(row => row.Remark, row => setRemark ? remark : row.Remark)
                        .SetProperty(
                            row => row.AutoSyncModelPattern,
                            row => setAutoSyncModelPattern
                                ? autoSyncModelPattern
                                : row.AutoSyncModelPattern)
                        .SetProperty(
                            row => row.Endpoints,
                            row => update.EndpointsJson ?? row.Endpoints)
                        .SetProperty(
                            row => row.ErrorMessage,
                            row => update.ClearErrorMessage ? (string?)null : row.ErrorMessage)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException("failed to update channel: " + ex.Message, ex);
        }

        if (affected == 0)
        {
            // The row vanished or the loaded updated_at no longer matches; ent
            // reports both as not found. None of this store's statements wrote
            // anything; disposing the owned transaction rolls it back, while an
            // ambient owner decides its own fate.
            return null;
        }

        if (afterUpdate is not null)
        {
            await afterUpdate(snapshot.Id, cancellationToken).ConfigureAwait(false);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // ent returns the saved entity. The port projects the loaded snapshot
        // instead of reloading; concurrent edits of untouched columns are
        // therefore not reflected in this response (C146).
        return Project(snapshot, update, now);
    }

    private static ChannelRecord Project(
        ChannelRecord row,
        ChannelUpdateRecord update,
        DateTime now)
        => new()
        {
            Id = row.Id,
            CreatedAt = row.CreatedAt,
            UpdatedAt = now,
            DeletedAt = row.DeletedAt,
            Type = update.Type ?? row.Type,
            BaseUrl = update.BaseUrl ?? row.BaseUrl,
            Name = update.Name ?? row.Name,
            Status = row.Status,
            Credentials = update.CredentialsJson ?? row.Credentials,
            DisabledApiKeys = row.DisabledApiKeys,
            SupportedModels = update.SupportedModelsJson ?? row.SupportedModels,
            ManualModels = update.ManualModelsJson ?? row.ManualModels,
            AutoSyncSupportedModels = update.AutoSyncSupportedModels ?? row.AutoSyncSupportedModels,
            AutoSyncModelPattern = update.ClearAutoSyncModelPattern
                ? null
                : update.AutoSyncModelPattern ?? row.AutoSyncModelPattern,
            Tags = update.TagsJson ?? row.Tags,
            DefaultTestModel = update.DefaultTestModel ?? row.DefaultTestModel,
            Policies = update.PoliciesJson ?? row.Policies,
            Settings = update.SettingsJson ?? row.Settings,
            OrderingWeight = update.OrderingWeight ?? row.OrderingWeight,
            ErrorMessage = update.ClearErrorMessage ? null : row.ErrorMessage,
            AutoDisabledAt = row.AutoDisabledAt,
            AutoDisableExpiresAt = row.AutoDisableExpiresAt,
            Remark = update.ClearRemark ? null : update.Remark ?? row.Remark,
            Endpoints = update.EndpointsJson ?? row.Endpoints,
        };

    public async Task<ChannelRecord?> SaveEndpointsAsync(
        ChannelRecord snapshot,
        string endpointsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(endpointsJson);

        var now = DateTime.UtcNow;

        int affected;
        try
        {
            // UpdateOneID(...).Where(UpdatedAtEQ(...)).SetEndpoints(...).Save:
            // the loaded version guards the write in the same statement.
            affected = await db.Channels
                .Where(row => row.Id == snapshot.Id
                    && row.DeletedAt == 0
                    && row.UpdatedAt == snapshot.UpdatedAt)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Endpoints, endpointsJson)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to update channel endpoints: " + ex.Message,
                ex);
        }

        if (affected == 0)
        {
            // The row vanished or the loaded updated_at no longer matches; ent
            // reports both as not found.
            return null;
        }

        // ent returns the saved entity: only endpoints and updated_at changed.
        snapshot.Endpoints = endpointsJson;
        snapshot.UpdatedAt = now;
        return snapshot;
    }

    public async Task<bool> UpdateApiKeyStateAsync(
        ChannelApiKeyStateRecord state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.DisabledApiKeysJson);

        var setErrorMessage = state.ClearErrorMessage || state.ErrorMessage is not null;
        var errorMessage = state.ClearErrorMessage ? null : state.ErrorMessage;
        var setAutoDisabledAt = state.ClearAutoDisabledAt || state.AutoDisabledAt is not null;
        DateTime? autoDisabledAt = state.ClearAutoDisabledAt ? null : state.AutoDisabledAt;
        var now = DateTime.UtcNow;

        int affected;
        try
        {
            // UpdateOneID(...).Save: the SoftDeleteMixin predicate applies, and
            // the time mixin stamps updated_at. No version guard is used here.
            affected = await db.Channels
                .Where(row => row.Id == state.Id && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.DisabledApiKeys, state.DisabledApiKeysJson)
                        .SetProperty(
                            row => row.Credentials,
                            row => state.CredentialsJson ?? row.Credentials)
                        .SetProperty(row => row.Status, row => state.Status ?? row.Status)
                        .SetProperty(
                            row => row.ErrorMessage,
                            row => setErrorMessage ? errorMessage : row.ErrorMessage)
                        .SetProperty(
                            row => row.AutoDisabledAt,
                            row => setAutoDisabledAt ? autoDisabledAt : row.AutoDisabledAt)
                        .SetProperty(
                            row => row.AutoDisableExpiresAt,
                            row => state.ClearAutoDisableExpiresAt
                                ? (DateTime?)null
                                : row.AutoDisableExpiresAt)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The caller wraps this with the operation-specific reference
            // prefix ("failed to disable api key: ...").
            throw new StoreFailureException(ex.Message, ex);
        }

        return affected > 0;
    }

    public async Task<ChannelRecord?> CreateAsync(
        ChannelRecord channel,
        Func<long, CancellationToken, Task>? afterInsert = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        bool exists;
        try
        {
            // Channel.Query().Where(Name(...)).First applies the ent
            // SoftDeleteMixin predicate, so only live rows can collide.
            exists = await db.Channels
                .AnyAsync(row => row.Name == channel.Name && row.DeletedAt == 0, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException("failed to check channel name: " + ex.Message, ex);
        }

        if (exists)
        {
            return null;
        }

        // CreateChannel wraps createChannel and ensureChannelModelPrices in one
        // RunInTransaction (channel.go:647-660).
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            db.Channels.Add(channel);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ent's Save returns the driver error verbatim; EF wraps it in a
            // DbUpdateException, so the reference-visible text is the base one.
            throw new StoreFailureException(
                "failed to create channel: " + ex.GetBaseException().Message,
                ex);
        }

        if (afterInsert is not null)
        {
            // A StoreFailureException from the pricing hook keeps its own
            // prefix, exactly like the reference returning the ensure error
            // unchanged from CreateChannel's transaction.
            await afterInsert(channel.Id, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return channel;
    }

    public async Task<IReadOnlyList<string>> ListLiveNamesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Channel.Query().Select(channel.FieldName).All(ctx): the
            // SoftDeleteMixin predicate keeps only live rows.
            return await db.Channels
                .AsNoTracking()
                .Where(row => row.DeletedAt == 0)
                .Select(row => row.Name)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to query existing channels: " + ex.Message,
                ex);
        }
    }

    public async Task<ChannelRecord?> DuplicateAsync(
        long sourceId,
        string name,
        Action<ChannelRecord> inherit,
        Func<ChannelRecord> buildRecord,
        Func<long, CancellationToken, Task>? afterInsert = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(inherit);
        ArgumentNullException.ThrowIfNull(buildRecord);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        ChannelRecord source;
        try
        {
            // db.Channel.Get applies the SoftDeleteMixin predicate; ent's
            // not-found error flows through the duplicate prefix.
            source = await db.Channels
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.Id == sourceId && row.DeletedAt == 0,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new StoreFailureException(
                    "failed to get source channel: ent: channel not found",
                    new InvalidOperationException("ent: channel not found"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            and not StoreFailureException)
        {
            throw new StoreFailureException(
                "failed to get source channel: " + ex.Message,
                ex);
        }

        // Endpoint and zenmux-credential inheritance happen before the name
        // check, exactly like channel_duplicate.go:27-32.
        inherit(source);

        bool exists;
        try
        {
            exists = await db.Channels
                .AnyAsync(
                    row => row.Name == name && row.DeletedAt == 0,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to check channel name: " + ex.Message,
                ex);
        }

        if (exists)
        {
            return null;
        }

        // createChannel's validators run only after the duplicate-name check.
        var channel = buildRecord();

        try
        {
            db.Channels.Add(channel);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ent's Save returns the driver error verbatim; EF wraps it in a
            // DbUpdateException, so the reference-visible text is the base one.
            throw new StoreFailureException(
                "failed to create channel: " + ex.GetBaseException().Message,
                ex);
        }

        if (afterInsert is not null)
        {
            // Price-copy and ensure failures carry their own reference
            // prefixes and roll the channel insert back.
            await afterInsert(channel.Id, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return channel;
    }

    public async Task<IReadOnlyList<ChannelRecord>> CreateManyAsync(
        IReadOnlyList<Func<ChannelRecord>> recordFactories,
        Func<CancellationToken, Task>? beforeInsert = null,
        Func<ChannelRecord, CancellationToken, Task>? afterEachInsert = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordFactories);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // resolveChannelModelPriceTemplates runs once at the top of the
        // reference transaction (channel_bulk.go:101-104), before the first
        // createChannel.
        if (beforeInsert is not null)
        {
            await beforeInsert(cancellationToken).ConfigureAwait(false);
        }

        var created = new List<ChannelRecord>(recordFactories.Count);
        foreach (var factory in recordFactories)
        {
            // createChannel validation/normalization is deferred to this
            // point so it runs in the reference's per-channel order; a
            // validation failure aborts the batch before the insert.
            var channel = factory();

            try
            {
                db.Channels.Add(channel);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // BulkCreateChannels wraps every createChannel failure with
                // the generated name (channel_bulk.go:134-137). The inner
                // prefix comes from createChannel's own Save wrapping.
                throw new StoreFailureException(
                    "failed to create channel '" + channel.Name + "': "
                        + "failed to create channel: " + ex.GetBaseException().Message,
                    ex);
            }

            if (afterEachInsert is not null)
            {
                // The caller wraps applyChannelModelPriceTemplates failures
                // with "failed to auto-configure prices for channel '...'".
                await afterEachInsert(channel, cancellationToken).ConfigureAwait(false);
            }

            created.Add(channel);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<ChannelRecord> ImportCreateAsync(
        ChannelRecord channel,
        Func<long, CancellationToken, Task>? afterInsert = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // Channel.Create().Save(ctx): no name pre-check, no validators.
            db.Channels.Add(channel);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (afterInsert is not null)
            {
                await afterInsert(channel.Id, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return channel;
        }
        catch (StoreFailureException)
        {
            // The row's ensure already carries the reference message
            // ("failed to auto-configure channel model price: ..."); the caller
            // appends it verbatim.
            db.ChangeTracker.Clear();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reference's per-row transaction leaves the ent client clean;
            // EF keeps added entities tracked, which would poison the next row.
            db.ChangeTracker.Clear();

            // BulkImportChannels appends err.Error() verbatim, so the row
            // carries the driver text rather than EF's wrapper.
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    public async Task<ChannelRecord?> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var channel = await db.Channels
            .SingleOrDefaultAsync(row => row.Id == id && row.DeletedAt == 0, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            return null;
        }

        // A manual status change takes the channel out of the auto-disable
        // lifecycle, so the auto-enable schedule no longer applies (channel.go:1205-1215).
        var now = DateTime.UtcNow;
        channel.Status = status;
        channel.AutoDisabledAt = null;
        channel.AutoDisableExpiresAt = null;
        channel.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Ent returns the mutated entity; UpdatedAt is the in-memory write
        // timestamp, not the storage-truncated value.
        return channel;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var channel = await db.Channels
            .SingleOrDefaultAsync(row => row.Id == id && row.DeletedAt == 0, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            return false;
        }

        // SoftDeleteMixin.Sets deleted_at to time.Now().Unix() and keeps the
        // time mixin's updated_at update (soft_delete.go:84).
        channel.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        channel.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        bool clearErrorMessage,
        string action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // The reference verifies the exact id count before touching any row;
        // duplicates therefore fail the check even though they name one row.
        var idsArray = ids.ToArray();
        int count;
        try
        {
            count = await db.Channels
                .CountAsync(row => idsArray.Contains(row.Id) && row.DeletedAt == 0, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new StoreFailureException("failed to query channels: " + ex.Message, ex);
        }

        if (count != ids.Count)
        {
            throw new ExpectedRowsMismatchException(ids.Count, count);
        }

        var now = DateTime.UtcNow;
        try
        {
            await db.Channels
                .Where(row => idsArray.Contains(row.Id) && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters =>
                    {
                        setters
                            .SetProperty(row => row.Status, status)
                            .SetProperty(row => row.AutoDisabledAt, (DateTime?)null)
                            .SetProperty(row => row.AutoDisableExpiresAt, (DateTime?)null)
                            .SetProperty(row => row.UpdatedAt, now);

                        if (clearErrorMessage)
                        {
                            setters.SetProperty(row => row.ErrorMessage, (string?)null);
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException($"failed to {action} channels: {ex.Message}", ex);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        var idsArray = ids.ToArray();
        var now = DateTime.UtcNow;

        // Delete().Where(IDIn(...)) drives the same soft-delete hook: one UPDATE
        // over every live matching row (channel_bulk.go:344-352).
        await db.Channels
            .Where(row => idsArray.Contains(row.Id) && row.DeletedAt == 0)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.DeletedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    .SetProperty(row => row.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ManageTagsAsync(
        IReadOnlyList<long> ids,
        IReadOnlyList<string> addTags,
        IReadOnlyList<string> removeTags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0 || (addTags.Count == 0 && removeTags.Count == 0))
        {
            return;
        }

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var idsArray = ids.ToArray();
        List<ChannelRecord> channels;
        try
        {
            channels = await db.Channels
                .Where(row => idsArray.Contains(row.Id) && row.DeletedAt == 0)
                .OrderBy(row => row.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new StoreFailureException(
                "failed to query channels for bulk tag management: " + ex.Message,
                ex);
        }

        if (channels.Count != ids.Count)
        {
            throw new ExpectedRowsMismatchException(ids.Count, channels.Count);
        }

        // Ent scans every loaded row before any update; a malformed tags column
        // aborts the whole query, so parse all rows up front.
        var parsed = new List<(ChannelRecord Channel, List<string> Tags)>(channels.Count);
        foreach (var channel in channels)
        {
            try
            {
                parsed.Add((channel, ParseTags(channel.Tags)));
            }
            catch (JsonException ex)
            {
                throw new StoreFailureException(
                    "failed to query channels for bulk tag management: " + ex.Message,
                    ex);
            }
        }

        var removeSet = new HashSet<string>(removeTags, StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        foreach (var (channel, currentTags) in parsed)
        {
            var managedTags = new List<string>(currentTags.Count + addTags.Count);
            var existingTags = new HashSet<string>(currentTags.Count + addTags.Count, StringComparer.Ordinal);
            var changed = false;

            foreach (var tag in currentTags)
            {
                if (removeSet.Contains(tag))
                {
                    changed = true;
                    continue;
                }

                managedTags.Add(tag);
                existingTags.Add(tag);
            }

            foreach (var tag in addTags)
            {
                if (!existingTags.Add(tag))
                {
                    continue;
                }

                managedTags.Add(tag);
                changed = true;
            }

            if (!changed)
            {
                continue;
            }

            var tagsJson = JsonSerializer.Serialize(managedTags);
            var loadedUpdatedAt = channel.UpdatedAt;

            // UpdateOneID(...).Where(UpdatedAtEQ(ch.UpdatedAt)): a concurrent
            // writer invalidates the guard and the reference reports it as a
            // concurrency conflict.
            int updated;
            try
            {
                updated = await db.Channels
                    .Where(row => row.Id == channel.Id
                        && row.DeletedAt == 0
                        && row.UpdatedAt == loadedUpdatedAt)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(row => row.Tags, tagsJson)
                            .SetProperty(row => row.UpdatedAt, now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    $"failed to manage tags for channel {channel.Id}: {ex.Message}",
                    ex);
            }

            if (updated == 0)
            {
                var notFound = new InvalidOperationException("ent: channel not found");
                throw new StoreFailureException(
                    $"channel {channel.Id} was modified while managing tags; please retry: {notFound.Message}",
                    notFound);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChannelRecord>> BulkUpdateOrderingAsync(
        IReadOnlyList<ChannelOrderingItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var updatedChannels = new List<ChannelRecord>(items.Count);
        if (items.Count == 0)
        {
            return updatedChannels;
        }

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in items)
        {
            // UpdateOneID applies the SoftDeleteMixin predicate, so a missing or
            // deleted row fails the whole batch.
            ChannelRecord? channel;
            try
            {
                channel = await db.Channels
                    .SingleOrDefaultAsync(
                        row => row.Id == item.Id && row.DeletedAt == 0,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    $"failed to update channel {item.Id}: {ex.Message}",
                    ex);
            }

            if (channel is null)
            {
                throw new StoreFailureException(
                    $"failed to update channel {item.Id}: ent: channel not found",
                    new InvalidOperationException("ent: channel not found"));
            }

            channel.OrderingWeight = item.OrderingWeight;
            channel.UpdatedAt = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    $"failed to update channel {item.Id}: {ex.Message}",
                    ex);
            }

            updatedChannels.Add(channel);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updatedChannels;
    }

    public async Task<IReadOnlyList<ChannelRecord>> BulkUpdateAutoDisableAsync(
        IReadOnlyList<long> ids,
        string mode,
        string? rulesJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(mode);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var idsArray = ids.ToArray();
        List<ChannelRecord> channels;
        try
        {
            channels = await db.Channels
                .Where(row => idsArray.Contains(row.Id) && row.DeletedAt == 0)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException("failed to query channels: " + ex.Message, ex);
        }

        // The reference compares the loaded entity count to the id count;
        // duplicates therefore fail even though they name one row.
        if (channels.Count != ids.Count)
        {
            throw new ExpectedRowsMismatchException(ids.Count, channels.Count);
        }

        var parsed = new List<(ChannelRecord Channel, JsonObject Policies)>(channels.Count);
        foreach (var channel in channels)
        {
            try
            {
                parsed.Add((channel, ParsePolicies(channel.Policies)));
            }
            catch (JsonException ex)
            {
                throw new StoreFailureException("failed to query channels: " + ex.Message, ex);
            }
        }

        var custom = string.Equals(mode, "custom", StringComparison.Ordinal);
        var inherit = string.Equals(mode, "inherit", StringComparison.Ordinal);
        JsonNode? rules = null;
        if (custom && rulesJson is not null)
        {
            try
            {
                rules = JsonNode.Parse(rulesJson);
            }
            catch (JsonException ex)
            {
                throw new StoreFailureException(
                    "failed to update auto-disable policies: " + ex.Message,
                    ex);
            }
        }

        foreach (var (channel, policies) in parsed)
        {
            // SetPolicies re-marshals the whole struct: the mode is always
            // written (non-empty), custom carries the normalized rules, inherit
            // clears them, and off keeps the stored rules untouched.
            policies["apiKeyAutoDisableMode"] = mode;
            if (custom)
            {
                // A JsonNode owns a single parent; each channel gets its own
                // copy of the shared normalized rules tree.
                policies["apiKeyAutoDisableRules"] = rules?.DeepClone();
            }
            else if (inherit)
            {
                policies.Remove("apiKeyAutoDisableRules");
            }

            channel.Policies = policies.ToJsonString();
            channel.UpdatedAt = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    $"failed to update auto-disable policies for channel "
                    + $"{GoQuote(channel.Name)} ({channel.Id}): {ex.Message}",
                    ex);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return channels;
    }

    private static JsonObject ParsePolicies(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return new JsonObject();
        }

        if (JsonNode.Parse(raw) is not JsonObject policies)
        {
            throw new JsonException("json: cannot unmarshal into objects.ChannelPolicies");
        }

        return policies;
    }

    /// <summary>Go <c>%q</c> for the channel-name segment of the update error.</summary>
    private static string GoQuote(string value) => JsonSerializer.Serialize(value);

    private static List<string> ParseTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<string?>>(raw)?
            .Select(static tag => tag ?? string.Empty)
            .ToList() ?? [];
    }
}
