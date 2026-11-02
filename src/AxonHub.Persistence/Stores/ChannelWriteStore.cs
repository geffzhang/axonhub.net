using System.Text.Json;
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
    /// <exception cref="StoreFailureException">
    /// The read/update failed; the message already carries "failed to update channel".
    /// </exception>
    Task<ChannelRecord?> UpdateAsync(
        ChannelRecord snapshot,
        ChannelUpdateRecord update,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the create half of ChannelService.CreateChannel: checks the live
    /// name (soft-delete predicate), then inserts the row. Returns null when a
    /// live channel already owns the name (the caller raises the coded
    /// duplicate-name error); a failed insert is a store failure.
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The name check or insert failed; the message already carries the
    /// reference prefix ("failed to check channel name"/"failed to create channel").
    /// </exception>
    Task<ChannelRecord?> CreateAsync(
        ChannelRecord channel,
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
            return await db.Channels
                .AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == id && row.DeletedAt == 0, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to load channel provider identity: " + ex.Message,
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
            // reports both as not found.
            return null;
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

    public async Task<ChannelRecord?> CreateAsync(
        ChannelRecord channel,
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

        try
        {
            db.Channels.Add(channel);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException("failed to create channel: " + ex.Message, ex);
        }

        return channel;
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
