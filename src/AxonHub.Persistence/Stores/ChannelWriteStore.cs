using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>
/// Write seam for the channel lifecycle mutations of biz.ChannelService and
/// biz.ChannelService bulk helpers (channel.go:1205-1290,
/// channel_bulk.go:163-462). Each method owns the reference's checks and its
/// per-mutation transaction; callers only add the biz-layer error prefixes.
/// </summary>
public interface IChannelWriteStore
{
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
