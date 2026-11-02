using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the channel lifecycle mutations of biz.ChannelService: status changes,
/// delete and the bulk status/delete/tag helpers (channel.go:1205-1290,
/// channel_bulk.go:163-462). The GraphQL permission guard is not ported yet
/// (C014), so only the persistence behaviour and reference error strings remain.
/// </summary>
public sealed class ChannelLifecycleService(
    IChannelWriteStore channels,
    ChannelLimiterManager limiters)
{
    /// <summary>Port of ChannelService.UpdateChannelStatus.</summary>
    public async Task<ChannelRecord> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        ChannelRecord? updated;
        try
        {
            updated = await channels.UpdateStatusAsync(id, status, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to update channel status: " + ex.Message,
                ex);
        }

        return updated
            ?? throw new InvalidOperationException(
                "failed to update channel status: ent: channel not found");
    }

    /// <summary>Port of ChannelService.DeleteChannel.</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        bool deleted;
        try
        {
            deleted = await channels.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to delete channel: " + ex.Message, ex);
        }

        if (!deleted)
        {
            throw new InvalidOperationException("failed to delete channel: ent: channel not found");
        }

        limiters.Forget(id);
    }

    /// <summary>Port of ChannelService.BulkArchiveChannels.</summary>
    public Task BulkArchiveAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(ids, "archived", "archive", false, cancellationToken);

    /// <summary>Port of ChannelService.BulkDisableChannels.</summary>
    public Task BulkDisableAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(ids, "disabled", "disable", false, cancellationToken);

    /// <summary>Port of ChannelService.BulkEnableChannels.</summary>
    public Task BulkEnableAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(ids, "enabled", "enable", false, cancellationToken);

    /// <summary>Port of ChannelService.BulkRecoverChannels.</summary>
    public Task BulkRecoverAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(ids, "enabled", "recover", true, cancellationToken);

    /// <summary>Port of ChannelService.BulkDeleteChannels.</summary>
    public async Task BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            await channels.BulkDeleteAsync(ids, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to bulk delete channels: " + ex.Message,
                ex);
        }

        foreach (var id in ids)
        {
            limiters.Forget(id);
        }
    }

    /// <summary>Port of ChannelService.BulkAddChannelTags.</summary>
    public Task BulkAddTagsAsync(
        IReadOnlyList<long> ids,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default)
        => BulkManageTagsAsync(ids, tags, [], cancellationToken);

    /// <summary>Port of ChannelService.BulkRemoveChannelTags.</summary>
    public Task BulkRemoveTagsAsync(
        IReadOnlyList<long> ids,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default)
        => BulkManageTagsAsync(ids, [], tags, cancellationToken);

    /// <summary>Port of ChannelService.BulkManageChannelTags.</summary>
    public async Task BulkManageTagsAsync(
        IReadOnlyList<long> ids,
        IReadOnlyList<string> addTags,
        IReadOnlyList<string> removeTags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(addTags);
        ArgumentNullException.ThrowIfNull(removeTags);

        // lo.Uniq(ids) preserves first occurrence order.
        var channelIds = new List<long>(ids.Count);
        var seenIds = new HashSet<long>();
        foreach (var id in ids)
        {
            if (seenIds.Add(id))
            {
                channelIds.Add(id);
            }
        }

        var newTags = NormalizeTags(addTags);
        var tagsToRemove = NormalizeTags(removeTags);
        if (channelIds.Count == 0 || (newTags.Count == 0 && tagsToRemove.Count == 0))
        {
            return;
        }

        try
        {
            await channels.ManageTagsAsync(channelIds, newTags, tagsToRemove, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (ExpectedRowsMismatchException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private async Task BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        string action,
        bool clearErrorMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            await channels.BulkUpdateStatusAsync(
                    ids,
                    status,
                    clearErrorMessage,
                    action,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (ExpectedRowsMismatchException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private static List<string> NormalizeTags(IReadOnlyList<string> tags)
    {
        var result = new List<string>(tags.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            var normalized = tag.Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}
