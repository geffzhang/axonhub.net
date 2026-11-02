using AxonHub.Api.GraphQL;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the channel lifecycle mutations of biz.ChannelService: create, status
/// changes, delete and the bulk status/delete/tag helpers (channel.go:525-1290,
/// channel_bulk.go:163-462). The GraphQL permission guard is not ported yet
/// (C014), so only the persistence behaviour and reference error strings remain.
/// </summary>
public sealed class ChannelLifecycleService(
    IChannelWriteStore channels,
    ChannelLimiterManager limiters)
{
    /// <summary>
    /// Port of ChannelService.CreateChannel (channel.go:633-671) over the frozen
    /// createChannel pipeline (channel.go:547-631), extended with the live
    /// validators the migrated frontend relies on (C139/C144).
    /// </summary>
    public async Task<ChannelRecord> CreateAsync(
        CreateChannelCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // normalizeCommandCodeQuotaCookieSettings: canonicalize the browser
        // cookie on Command Code channels, clear the block everywhere else.
        var error = ChannelValidation.NormalizeCommandCodeQuotaCookieSettings(
            command.Settings,
            command.Type);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        // xai_subscription always talks to the official proxy and drops any
        // caller-supplied endpoint list.
        if (command.Type == "xai_subscription")
        {
            command.BaseUrl = ChannelValidation.XaiSubscriptionBaseUrl;
            command.Endpoints = null;
        }

        if (ChannelValidation.IsCommandCodeChannelType(command.Type))
        {
            error = ChannelValidation.ValidateCommandCodeBaseUrl(command.BaseUrl ?? string.Empty);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }
        }

        error = ChannelValidation.NormalizeApiKeyAutoDisableRules(command.Policies);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        if (command.Settings is { } settings)
        {
            // A new channel may intentionally be created before its model list
            // exists; an empty list never prunes protocol overrides.
            if (command.SupportedModels.Count > 0)
            {
                ChannelValidation.RemoveRemovedModelProtocolOverrides(
                    settings,
                    command.SupportedModels);
            }

            if (settings.BodyOverrideOperations is { } bodyOperations)
            {
                error = ChannelValidation.ValidateBodyOverrideOperations(bodyOperations);
                if (error is not null)
                {
                    throw new InvalidOperationException(
                        "invalid body override operations: " + error);
                }
            }

            if (settings.HeaderOverrideOperations is { } headerOperations)
            {
                error = ChannelValidation.ValidateHeaderOverrideOperations(headerOperations);
                if (error is not null)
                {
                    throw new InvalidOperationException(
                        "invalid header override operations: " + error);
                }
            }

            error = ChannelValidation.ValidateRateLimit(settings.RateLimit);
            if (error is not null)
            {
                throw new InvalidOperationException("invalid rate limit: " + error);
            }

            // Live order: the response timeout is normalized between the rate
            // limit and the retryable-code normalizers (live channel.go:600).
            error = ChannelValidation.NormalizeResponseTimeout(settings);
            if (error is not null)
            {
                throw new InvalidOperationException("invalid response timeout: " + error);
            }

            error = ChannelValidation.NormalizeRetryableStatusCodes(settings);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            error = ChannelValidation.NormalizeRetryableErrorPatterns(settings);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            error = ChannelValidation.ValidateMaxSingleChannelRetries(settings);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            error = ChannelValidation.ValidateModelProtocols(
                settings,
                command.Type,
                command.Endpoints);
            if (error is not null)
            {
                throw new InvalidOperationException("invalid model protocols: " + error);
            }
        }

        // Frozen createChannel validates the supplied endpoints after the
        // settings block (channel.go:611-615).
        if (command.Endpoints is { } endpoints)
        {
            error = ChannelValidation.ValidateEndpointsForChannelType(command.Type, endpoints);
            if (error is not null)
            {
                throw new InvalidOperationException("invalid endpoints: " + error);
            }
        }

        error = ChannelValidation.ValidateVertexApiKeys(command.Type, command.Credentials);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        var record = ChannelWriteSerialization.ToRecord(command, DateTime.UtcNow);

        ChannelRecord? created;
        try
        {
            created = await channels.CreateAsync(record, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (created is null)
        {
            throw new CodedException(
                CodedException.DuplicateName,
                $"channel name '{command.Name}' already exists",
                new Dictionary<string, object?>
                {
                    ["resource"] = "channel",
                    ["field"] = "name",
                    ["value"] = command.Name,
                });
        }

        // The reference reloads its in-memory channel cache here; the port
        // builds limiters lazily, so there is nothing to refresh.
        return created;
    }

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
