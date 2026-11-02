using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the channel lifecycle mutations of biz.ChannelService: create, update,
/// status changes, delete and the bulk status/delete/tag helpers
/// (channel.go:525-1290, channel_bulk.go:163-462). The GraphQL permission guard
/// is not ported yet (C014), so only the persistence behaviour and reference
/// error strings remain.
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

    /// <summary>
    /// Port of ChannelService.UpdateChannel (channel.go:849-1203): the frozen
    /// validation order plus the live max-retries, response-timeout and Vertex
    /// validators (C144/C146).
    /// </summary>
    public async Task<ChannelRecord> UpdateAsync(
        long id,
        UpdateChannelCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        ChannelRecord snapshot;
        try
        {
            snapshot = await channels.GetForUpdateAsync(id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "failed to load channel provider identity: ent: channel not found");
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        var effectiveType = command.Type ?? snapshot.Type;

        // The guard is decided from the caller's input before normalization and
        // the xai rewrite, exactly like the reference (channel.go:869-872).
        var commandCodeQuotaSettings = command.Settings is not null
            && (ChannelValidation.IsCommandCodeChannelType(effectiveType)
                || command.Settings.ProviderQuota?.CommandCode is not null);
        var guardProviderIdentity = command.Type is not null
            || command.BaseUrl is not null
            || command.Endpoints is not null
            || commandCodeQuotaSettings;

        if (command.Settings is { } inputSettings)
        {
            // A cleared Command Code cookie must not survive on the channel.
            // The reference additionally invalidates the cached provider quota
            // status after commit; that subsystem is not ported (C139).
            if (ChannelValidation.IsCommandCodeChannelType(effectiveType)
                && (inputSettings.ProviderQuota is null
                    || ChannelValidation.CommandCodeQuotaCookieIsBlank(inputSettings)))
            {
                ChannelValidation.ClearCommandCodeQuotaSettings(inputSettings);
            }

            var error = ChannelValidation.NormalizeCommandCodeQuotaCookieSettings(
                command.Settings,
                effectiveType);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            // Live order: the retry bound follows the cookie normalization
            // (live channel.go:946-948).
            error = ChannelValidation.ValidateMaxSingleChannelRetries(command.Settings);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }
        }

        var autoDisableError = ChannelValidation.NormalizeApiKeyAutoDisableRules(command.Policies);
        if (autoDisableError is not null)
        {
            throw new InvalidOperationException(autoDisableError);
        }

        if (command.Type == "xai_subscription")
        {
            command.BaseUrl = ChannelValidation.XaiSubscriptionBaseUrl;
            command.Endpoints = [];
        }
        else if (command.Type is null && (command.BaseUrl is not null || command.Endpoints is not null)
            && snapshot.Type == "xai_subscription")
        {
            command.BaseUrl = ChannelValidation.XaiSubscriptionBaseUrl;
            command.Endpoints = [];
        }

        var effectiveBaseUrl = command.BaseUrl ?? snapshot.BaseUrl;
        if (ChannelValidation.IsCommandCodeChannelType(effectiveType))
        {
            var error = ChannelValidation.ValidateCommandCodeBaseUrl(effectiveBaseUrl ?? string.Empty);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }
        }

        if (command.Name is { } name)
        {
            bool exists;
            try
            {
                exists = await channels.NameExistsAsync(id, name, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (StoreFailureException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }

            if (exists)
            {
                throw new CodedException(
                    CodedException.DuplicateName,
                    $"channel name '{name}' already exists",
                    new Dictionary<string, object?>
                    {
                        ["resource"] = "channel",
                        ["field"] = "name",
                        ["value"] = name,
                    });
            }
        }

        // The frozen service re-queries each needed column; the port decodes
        // the single snapshot on first use and keeps the enclosing error
        // prefix of whichever step needed it first (C146).
        ChannelSettingsDraft? storedSettings = null;
        var storedSettingsLoaded = false;
        ChannelSettingsDraft? StoredSettings(string prefix)
        {
            if (storedSettingsLoaded)
            {
                return storedSettings;
            }

            try
            {
                storedSettings = ChannelWriteSerialization.ParseSettings(snapshot.Settings);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(prefix + ex.Message, ex);
            }

            storedSettingsLoaded = true;
            return storedSettings;
        }

        IReadOnlyList<ChannelEndpointDraft>? storedEndpoints = null;
        var storedEndpointsLoaded = false;
        IReadOnlyList<ChannelEndpointDraft>? StoredEndpoints(string prefix)
        {
            if (storedEndpointsLoaded)
            {
                return storedEndpoints;
            }

            try
            {
                storedEndpoints = ChannelWriteSerialization.ParseEndpoints(snapshot.Endpoints);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(prefix + ex.Message, ex);
            }

            storedEndpointsLoaded = true;
            return storedEndpoints;
        }

        if (command.SupportedModels is { } supportedModels)
        {
            var syncSettings = command.Settings
                ?? StoredSettings("failed to load channel settings for model protocol sync: ");
            if (syncSettings is not null
                && ChannelValidation.RemoveRemovedModelProtocolOverrides(syncSettings, supportedModels)
                && command.Settings is null)
            {
                command.Settings = syncSettings;
            }
        }

        if (command.Settings is { } validatedSettings)
        {
            if (validatedSettings.BodyOverrideOperations is { } bodyOperations)
            {
                var bodyError = ChannelValidation.ValidateBodyOverrideOperations(bodyOperations);
                if (bodyError is not null)
                {
                    throw new InvalidOperationException(
                        "invalid body override operations: " + bodyError);
                }
            }

            if (validatedSettings.HeaderOverrideOperations is { } headerOperations)
            {
                var headerError =
                    ChannelValidation.ValidateHeaderOverrideOperations(headerOperations);
                if (headerError is not null)
                {
                    throw new InvalidOperationException(
                        "invalid header override operations: " + headerError);
                }
            }

            var error = ChannelValidation.ValidateRateLimit(validatedSettings.RateLimit);
            if (error is not null)
            {
                throw new InvalidOperationException("invalid rate limit: " + error);
            }

            // Live addition, placed between the rate limit and the retryable
            // normalizers like the reference (live channel.go:1036-1038).
            error = ChannelValidation.NormalizeResponseTimeout(validatedSettings);
            if (error is not null)
            {
                throw new InvalidOperationException("invalid response timeout: " + error);
            }

            error = ChannelValidation.NormalizeRetryableStatusCodes(validatedSettings);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            error = ChannelValidation.NormalizeRetryableErrorPatterns(validatedSettings);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }
        }

        // Per-model protocol overrides must reference the effective endpoint
        // surface; settings may come from the input or the stored snapshot.
        var protocolSettings = command.Settings;
        if (protocolSettings is null && (command.Endpoints is not null || command.Type is not null))
        {
            protocolSettings = StoredSettings(
                "failed to load channel for model protocol validation: ");
        }

        if (protocolSettings is { ModelProtocols.Count: > 0 })
        {
            var channelType = command.Type ?? snapshot.Type;
            var endpoints = command.Endpoints
                ?? StoredEndpoints("failed to load channel for model protocol validation: ");

            var error = ChannelValidation.ValidateModelProtocols(
                protocolSettings,
                channelType,
                endpoints);
            if (error is not null)
            {
                throw new InvalidOperationException("invalid model protocols: " + error);
            }
        }

        if (command.Endpoints is not null || command.Type is not null)
        {
            var channelType = command.Type ?? snapshot.Type;
            var endpoints = command.Endpoints
                ?? StoredEndpoints("failed to load channel for endpoint validation: ");

            var error = ChannelValidation.ValidateEndpointsForChannelType(channelType, endpoints);
            if (error is not null)
            {
                throw new InvalidOperationException("invalid endpoints: " + error);
            }
        }

        var clearStaleQuotaSettings = command.Settings is null
            && command.Type is not null
            && !ChannelValidation.IsCommandCodeChannelType(command.Type);

        string? settingsJson = null;
        if (command.Settings is { } finalSettings)
        {
            settingsJson = GoJson.MarshalToString(finalSettings);
        }
        else if (clearStaleQuotaSettings)
        {
            var staleSettings = StoredSettings("failed to load channel settings: ");
            if (staleSettings is not null)
            {
                ChannelValidation.ClearCommandCodeQuotaSettings(staleSettings);
            }

            // SetSettings(nil) stores JSON null.
            settingsJson = staleSettings is null
                ? "null"
                : GoJson.MarshalToString(staleSettings);
        }

        var policiesJson = command.Policies is null
            ? null
            : GoJson.MarshalToString(command.Policies);

        string? credentialsJson = null;
        if (command.Credentials is { } credentials)
        {
            ChannelCredentialsDraft storedCredentials;
            try
            {
                storedCredentials = ChannelWriteSerialization.ParseCredentials(snapshot.Credentials);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to load existing channel credentials: " + ex.Message,
                    ex);
            }

            var credentialType = command.Type ?? snapshot.Type;
            if (string.IsNullOrEmpty(credentials.ManagementApiKey)
                && ChannelValidation.IsZenmuxChannelType(credentialType)
                && !string.IsNullOrEmpty(storedCredentials.ManagementApiKey))
            {
                credentials.ManagementApiKey = storedCredentials.ManagementApiKey;
            }

            var vertexError = ChannelValidation.ValidateVertexApiKeys(credentialType, credentials);
            if (vertexError is not null)
            {
                throw new InvalidOperationException(vertexError);
            }

            credentialsJson = GoJson.MarshalToString(credentials);
        }

        var update = new ChannelUpdateRecord
        {
            Type = command.Type,
            BaseUrl = command.BaseUrl,
            Name = command.Name,
            DefaultTestModel = command.DefaultTestModel,
            OrderingWeight = command.OrderingWeight,
            AutoSyncSupportedModels = command.AutoSyncSupportedModels,
            SupportedModelsJson = command.SupportedModels is null
                ? null
                : GoJson.MarshalToString(command.SupportedModels),
            ManualModelsJson = command.ManualModels is null
                ? null
                : GoJson.MarshalToString(command.ManualModels),
            TagsJson = command.Tags is null ? null : GoJson.MarshalToString(command.Tags),
            SettingsJson = settingsJson,
            PoliciesJson = policiesJson,
            CredentialsJson = credentialsJson,
            Remark = command.Remark,
            ClearRemark = command.ClearRemark,
            AutoSyncModelPattern = command.AutoSyncModelPattern,
            ClearAutoSyncModelPattern = command.ClearAutoSyncModelPattern,
            EndpointsJson = command.Endpoints is null
                ? null
                : GoJson.MarshalToString(command.Endpoints),
            ClearErrorMessage = command.ClearErrorMessage,
            ExpectedUpdatedAt = guardProviderIdentity ? snapshot.UpdatedAt : null,
        };

        ChannelRecord? updated;
        try
        {
            updated = await channels.UpdateAsync(snapshot, update, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        return updated ?? throw new InvalidOperationException(
            "channel was updated concurrently; retry the operation");
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
