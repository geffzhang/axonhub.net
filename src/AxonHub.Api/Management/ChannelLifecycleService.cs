using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Billing;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Json;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging;
using GoJson = AxonHub.Llm.Json.GoJson;

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
    ChannelLimiterManager limiters,
    IChannelModelPriceStore prices,
    IProviderQuotaInvalidator? quotaInvalidator = null,
    ILogger<ChannelLifecycleService>? logger = null)
{
    /// <summary>
    /// Port of ChannelService.apiKeyOpsLock: the reference serializes every
    /// credential bookkeeping mutation with one process-wide mutex, so the
    /// check-then-write sequence cannot interleave across requests. The port's
    /// service is scoped, hence the static lock.
    /// </summary>
    private static readonly SemaphoreSlim ApiKeyOpsLock = new(1, 1);

    /// <summary>
    /// Port of <c>ChannelService.invalidateProviderQuota</c>'s warning
    /// (channel_provider_quota_hook.go); the hook never fails the mutation.
    /// </summary>
    private static readonly Action<ILogger, long, Exception?> LogQuotaInvalidationFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "ChannelProviderQuotaInvalidationFailed"),
            "failed to invalidate provider quota after channel provider change "
            + "channel_id={ChannelId}");

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

        var record = PrepareCreate(command);

        ChannelRecord? created;
        try
        {
            // CreateChannel runs ensureChannelModelPrices inside the create
            // transaction (channel.go:647-660): the channel's supported models
            // are priced from the model library before the commit.
            created = await channels.CreateAsync(
                    record,
                    (channelId, token) => prices.EnsureAutoPricesAsync(
                        channelId,
                        command.SupportedModels,
                        ModelCardPricing.ToPriceJson,
                        ChannelModelPriceService.GenerateReferenceId,
                        token),
                    cancellationToken)
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
    /// Port of the validation/normalization half of createChannel
    /// (channel.go:527-631), shared by create, duplicate, bulk create and bulk
    /// import. It mutates the command exactly like the reference (cookie
    /// canonicalization, xai rewrite, settings normalizers). Bulk create hands
    /// the same settings/policies instances to every channel, so these
    /// mutations accumulate across the batch like the reference's shared
    /// pointers.
    /// </summary>
    private ChannelRecord PrepareCreate(CreateChannelCommand command)
    {
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

        return ChannelWriteSerialization.ToRecord(command, DateTime.UtcNow);
    }

    /// <summary>
    /// Port of ChannelService.DuplicateChannel (channel_duplicate.go:17-84):
    /// loads the source in the same transaction, inherits its endpoints when
    /// the input omits them and its zenmux management key when both sides are
    /// zenmux types, checks the live name, creates the channel, copies every
    /// current price with a fresh reference ID and runs the model-library
    /// ensure. Any failure rolls the whole transaction back.
    /// </summary>
    public async Task<ChannelRecord> DuplicateAsync(
        long sourceId,
        CreateChannelCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        ChannelRecord? duplicated;
        try
        {
            duplicated = await channels.DuplicateAsync(
                    sourceId,
                    command.Name,
                    source => ApplyDuplicateInheritance(source, command),
                    () => PrepareCreate(command),
                    async (channelId, token) =>
                    {
                        // createChannelModelPrice per source row, then
                        // ensureChannelModelPrices (channel_duplicate.go:50-68).
                        await prices.CopyChannelPricesAsync(
                                sourceId,
                                channelId,
                                ChannelModelPriceService.GenerateReferenceId,
                                token)
                            .ConfigureAwait(false);

                        await prices.EnsureAutoPricesAsync(
                                channelId,
                                command.SupportedModels,
                                ModelCardPricing.ToPriceJson,
                                ChannelModelPriceService.GenerateReferenceId,
                                token)
                            .ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (duplicated is null)
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

        return duplicated;
    }

    /// <summary>
    /// Port of DuplicateChannel's input fallbacks (channel_duplicate.go:27-32).
    /// Source JSON is decoded here because the port's ent read leaves the
    /// columns raw; decode failures carry the source-read prefix, exactly like
    /// ent scanning the source entity.
    /// </summary>
    private static void ApplyDuplicateInheritance(
        ChannelRecord source,
        CreateChannelCommand command)
    {
        if (command.Endpoints is null)
        {
            try
            {
                command.Endpoints = ChannelWriteSerialization.ParseEndpoints(source.Endpoints);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to get source channel: " + ex.Message,
                    ex);
            }
        }

        if (!ChannelValidation.IsZenmuxChannelType(source.Type)
            || !ChannelValidation.IsZenmuxChannelType(command.Type)
            || !string.IsNullOrEmpty(command.Credentials.ManagementApiKey))
        {
            return;
        }

        ChannelCredentialsDraft sourceCredentials;
        try
        {
            sourceCredentials = ChannelWriteSerialization.ParseCredentials(source.Credentials);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to get source channel: " + ex.Message,
                ex);
        }

        // Go copies the string (empty included); the draft stores null for an
        // empty key so the omitempty wire shape survives.
        command.Credentials.ManagementApiKey =
            string.IsNullOrEmpty(sourceCredentials.ManagementApiKey)
                ? null
                : sourceCredentials.ManagementApiKey;
    }

    /// <summary>
    /// Port of ChannelService.BulkCreateChannels (channel_bulk.go:67-161):
    /// validates before opening the transaction, snapshots every live name,
    /// numbers the generated names while skipping existing ones, resolves the
    /// price templates once inside the transaction, then creates and prices
    /// one channel per API key. The whole batch commits or rolls back as one.
    /// </summary>
    public async Task<IReadOnlyList<ChannelRecord>> BulkCreateChannelsAsync(
        BulkCreateChannelsCommand input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ApiKeys.Count == 0)
        {
            throw new InvalidOperationException("no API keys provided");
        }

        if (input.BaseUrl is null)
        {
            throw new InvalidOperationException("base URL is required");
        }

        if (!ChannelMapping.IsKnownChannelType(input.Type))
        {
            // channel.TypeValidator text (ent/channel/channel.go:287-293).
            throw new InvalidOperationException(
                "invalid channel type '" + input.Type + "': "
                    + "channel: invalid enum value for type field: "
                    + GoCast.GoQuote(input.Type));
        }

        IReadOnlyList<string> liveNames;
        try
        {
            liveNames = await channels.ListLiveNamesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        var existingNames = new HashSet<string>(liveNames, StringComparer.Ordinal);

        // All channels use the numbered format "base - (1)", "base - (2)",
        // and skip any counter already taken by a live channel
        // (channel_bulk.go:92-116).
        var counter = 1;
        var names = new List<string>(input.ApiKeys.Count);
        foreach (var _ in input.ApiKeys)
        {
            var channelName = input.Name + " - (" + counter + ")";
            while (existingNames.Contains(channelName))
            {
                counter++;
                channelName = input.Name + " - (" + counter + ")";
            }

            counter++;
            existingNames.Add(channelName);
            names.Add(channelName);
        }

        // len(tags)==0 falls back to the base name, covering both an omitted
        // list and an explicit empty one (channel_bulk.go:95-98).
        IReadOnlyList<string> tagsToUse =
            input.Tags is { Count: > 0 } ? input.Tags : [input.Name];

        var recordFactories = new List<Func<ChannelRecord>>(input.ApiKeys.Count);
        for (var index = 0; index < input.ApiKeys.Count; index++)
        {
            var apiKey = input.ApiKeys[index];
            var channelName = names[index];
            var command = new CreateChannelCommand
            {
                Type = input.Type,
                BaseUrl = input.BaseUrl,
                Name = channelName,
                Credentials = new ChannelCredentialsDraft { ApiKeys = [apiKey] },
                SupportedModels = input.SupportedModels,
                AutoSyncSupportedModels = input.AutoSyncSupportedModels,
                Tags = tagsToUse,
                DefaultTestModel = input.DefaultTestModel,

                // Same instances for every channel: createChannel mutates
                // them in place (channel_bulk.go:128-129).
                Policies = input.Policies,
                Settings = input.Settings,
                Remark = input.Remark,
            };

            recordFactories.Add(() =>
            {
                try
                {
                    return PrepareCreate(command);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Every createChannel failure, validation included, is
                    // wrapped with the generated name (channel_bulk.go:134-137).
                    throw new InvalidOperationException(
                        "failed to create channel '" + channelName + "': " + ex.Message,
                        ex);
                }
            });
        }

        IReadOnlyList<ChannelModelPriceTemplate> templates = [];
        IReadOnlyList<ChannelRecord> created;
        try
        {
            created = await channels.CreateManyAsync(
                    recordFactories,
                    async token =>
                    {
                        // resolveChannelModelPriceTemplates runs once per batch
                        // inside the transaction (channel_bulk.go:101-104).
                        templates = await prices.ResolveAutoPriceTemplatesAsync(
                                input.SupportedModels,
                                ModelCardPricing.ToPriceJson,
                                token)
                            .ConfigureAwait(false);
                    },
                    async (channel, token) =>
                    {
                        try
                        {
                            await prices.ApplyAutoPriceTemplatesAsync(
                                    channel.Id,
                                    templates,
                                    ChannelModelPriceService.GenerateReferenceId,
                                    token)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            throw new InvalidOperationException(
                                "failed to auto-configure prices for channel '"
                                    + channel.Name + "': " + ex.Message,
                                ex);
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        return created;
    }

    /// <summary>
    /// Port of ChannelService.BulkImportChannels (channel_bulk.go:484-568):
    /// validates each row in order (type, base URL, API key), creates the row
    /// with the raw ent builder plus the model-library ensure in its own
    /// transaction, and keeps going after a failed row. The result carries the
    /// successfully created channels, the per-row error strings and
    /// success = no failures.
    /// </summary>
    public async Task<BulkImportChannelsResult> BulkImportChannelsAsync(
        IReadOnlyList<BulkImportChannelItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var createdChannels = new List<ChannelRecord>();
        var errors = new List<string>();
        var created = 0;
        var failed = 0;

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var row = index + 1;

            if (!ChannelMapping.IsKnownChannelType(item.Type))
            {
                errors.Add("Row " + row + ": Invalid channel type '" + item.Type + "'");
                failed++;
                continue;
            }

            if (string.IsNullOrEmpty(item.BaseUrl))
            {
                errors.Add("Row " + row + " (" + item.Name + "): Base URL is required");
                failed++;
                continue;
            }

            if (string.IsNullOrEmpty(item.ApiKey))
            {
                errors.Add("Row " + row + " (" + item.Name + "): API Key is required");
                failed++;
                continue;
            }

            var supportedModels = item.SupportedModels;
            try
            {
                var channel = await channels.ImportCreateAsync(
                        ChannelWriteSerialization.ToImportRecord(item, DateTime.UtcNow),
                        (channelId, token) => prices.EnsureAutoPricesAsync(
                            channelId,
                            supportedModels,
                            ModelCardPricing.ToPriceJson,
                            ChannelModelPriceService.GenerateReferenceId,
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);

                createdChannels.Add(channel);
                created++;
            }
            catch (StoreFailureException ex)
            {
                // The row's raw error text, no additional prefix
                // (channel_bulk.go:540-544).
                errors.Add("Row " + row + " (" + item.Name + "): " + ex.Message);
                failed++;
            }
        }

        return new BulkImportChannelsResult(
            Success: failed == 0,
            Created: created,
            Failed: failed,
            Errors: errors.Count == 0 ? null : errors,
            Channels: createdChannels);
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

        // A cleared Command Code quota cookie must invalidate the old persisted
        // status, regardless of whether the client sent null or an empty
        // object (channel.go:923-925).
        var quotaCookieCleared = false;

        if (command.Settings is { } inputSettings)
        {
            if (ChannelValidation.IsCommandCodeChannelType(effectiveType)
                && (inputSettings.ProviderQuota is null
                    || ChannelValidation.CommandCodeQuotaCookieIsBlank(inputSettings)))
            {
                ChannelValidation.ClearCommandCodeQuotaSettings(inputSettings);
                quotaCookieCleared = true;
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

        // UpdateChannel only ensures prices when SupportedModels was provided
        // (nil means untouched; an empty list still runs the ensure), and the
        // call shares the update's transaction (channel.go:1163-1167).
        var ensureModels = command.SupportedModels;
        Func<long, CancellationToken, Task>? afterUpdate =
            ensureModels is null
                ? null
                : (channelId, token) => prices.EnsureAutoPricesAsync(
                    channelId,
                    ensureModels,
                    ModelCardPricing.ToPriceJson,
                    ChannelModelPriceService.GenerateReferenceId,
                    token);

        ChannelRecord? updated;
        try
        {
            updated = await channels.UpdateAsync(
                    snapshot,
                    update,
                    afterUpdate,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (updated is null)
        {
            throw new InvalidOperationException(
                "channel was updated concurrently; retry the operation");
        }

        // The saved row is compared against the pre-normalization identity
        // snapshot; the comparison only runs under the identity guard
        // (channel.go:1211-1214).
        var providerIdentityChanged = guardProviderIdentity
            && (updated.Type != snapshot.Type || updated.BaseUrl != snapshot.BaseUrl);

        if (providerIdentityChanged || quotaCookieCleared)
        {
            // runAfterCommit equivalent (channel.go:1230-1234): invalidate the
            // cached provider quota status once the update is committed. The
            // hook only logs on failure and never fails the mutation.
            await InvalidateProviderQuotaAsync(id, cancellationToken)
                .ConfigureAwait(false);
        }

        return updated;
    }

    /// <summary>
    /// Port of <c>ChannelService.invalidateProviderQuota</c>
    /// (channel_provider_quota_hook.go): absent invalidator is a no-op and a
    /// failure is logged, never propagated.
    /// </summary>
    private async Task InvalidateProviderQuotaAsync(long channelId, CancellationToken cancellationToken)
    {
        if (quotaInvalidator is null)
        {
            return;
        }

        try
        {
            await quotaInvalidator.InvalidateChannelQuotaAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (logger is not null)
            {
                LogQuotaInvalidationFailed(logger, channelId, ex);
            }
        }
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

    /// <summary>
    /// Port of ChannelService.BulkUpdateChannelOrdering (channel_bulk.go:23-45).
    /// The reference relies on the GraphQL Transactioner for atomicity; the
    /// store runs the batch in one transaction. The resolver adds the
    /// "failed to bulk update channel ordering" prefix.
    /// </summary>
    public async Task<IReadOnlyList<ChannelRecord>> BulkUpdateOrderingAsync(
        IReadOnlyList<ChannelOrderingItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        try
        {
            return await channels.BulkUpdateOrderingAsync(items, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Port of ChannelService.BulkUpdateChannelAutoDisable
    /// (channel_bulk.go:242-340): validates the action before the transaction,
    /// normalizes write_rules with channel-level allowDelete, then delegates the
    /// count check and the per-channel policy write to the store.
    /// </summary>
    public async Task<IReadOnlyList<ChannelRecord>> BulkUpdateAutoDisableAsync(
        BulkUpdateAutoDisableCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.ChannelIds.Count == 0)
        {
            throw new CodedException(
                CodedException.ValidationFailed,
                "channel IDs are required");
        }

        string mode;
        string? rulesJson = null;

        switch (command.Action)
        {
            case "write_rules":
                if (command.Rules is null || command.Rules.Count == 0)
                {
                    throw new CodedException(
                        CodedException.ValidationFailed,
                        "rules are required when writing auto-disable rules");
                }

                IReadOnlyList<AutoDisableRule> normalized;
                try
                {
                    normalized = RetryPolicyCodec.NormalizeAutoDisableRules(
                        command.Rules.Select(ChannelValidation.ToAutoDisableRule).ToList(),
                        allowDelete: true);
                }
                catch (AutoDisableRuleValidationException ex)
                {
                    throw new CodedException(CodedException.ValidationFailed, ex.Message);
                }

                mode = "custom";
                rulesJson = GoJson.MarshalToString(
                    normalized.Select(ChannelValidation.ToDraft).ToList());
                break;
            case "inherit":
                mode = "inherit";
                break;
            case "off":
                mode = "off";
                break;
            default:
                // Unreachable through the GraphQL enum, kept for parity with the
                // reference switch (channel_bulk.go:274-275).
                throw new CodedException(
                    CodedException.ValidationFailed,
                    "unsupported auto-disable action " + GoCast.GoQuote(command.Action));
        }

        try
        {
            return await channels.BulkUpdateAutoDisableAsync(
                    command.ChannelIds,
                    mode,
                    rulesJson,
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

    /// <summary>
    /// Port of ChannelService.SaveChannelEndpoints (channel.go:1243-1275):
    /// reads the channel, validates the endpoints against the channel type and
    /// the stored model-protocol overrides, then writes only the endpoints
    /// column under the loaded updated_at guard. The live build additionally
    /// normalizes Responses transport intent and rewrites settings; the port
    /// keeps the frozen save semantics (C148).
    /// </summary>
    public async Task<ChannelRecord> SaveEndpointsAsync(
        SaveChannelEndpointsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        ChannelRecord snapshot;
        try
        {
            snapshot = await channels.GetAsync(command.ChannelId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "failed to get channel: ent: channel not found");
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        var error = ChannelValidation.ValidateEndpointsForChannelType(
            snapshot.Type,
            command.Endpoints);
        if (error is not null)
        {
            throw new InvalidOperationException("invalid endpoints: " + error);
        }

        if (snapshot.Type == "xai_subscription")
        {
            throw new InvalidOperationException(
                "xAI subscription channels do not support custom endpoints");
        }

        ChannelSettingsDraft? settings;
        try
        {
            settings = ChannelWriteSerialization.ParseSettings(snapshot.Settings);
        }
        catch (JsonException ex)
        {
            // The reference decodes settings inside ent's Get, so a malformed
            // JSON fails with the read prefix (C002 text difference).
            throw new InvalidOperationException("failed to get channel: " + ex.Message, ex);
        }

        error = ChannelValidation.ValidateModelProtocols(
            settings,
            snapshot.Type,
            command.Endpoints);
        if (error is not null)
        {
            throw new InvalidOperationException(
                "invalid endpoints for configured model protocols: " + error);
        }

        var endpointsJson = GoJson.MarshalToString(command.Endpoints);

        ChannelRecord? updated;
        try
        {
            updated = await channels.SaveEndpointsAsync(
                    snapshot,
                    endpointsJson,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "channel was updated concurrently; retry the operation");
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        return updated;
    }

    /// <summary>
    /// Port of ChannelService.DisableAPIKey (channel_apikey.go:23-144): appends
    /// an active disable record for the credential reference and disables the
    /// whole channel when no enabled credential remains. The reference's
    /// auto-disable event and cache reload have no counterpart in the port
    /// (C149).
    /// </summary>
    public async Task DisableApiKeyAsync(
        long channelId,
        string key,
        int errorCode,
        string reason,
        DateTime? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        if (key.Length == 0)
        {
            throw new InvalidOperationException("api key cannot be empty");
        }

        // The reference mutex (sync.Mutex) is not cancellable; block until it is free.
        await ApiKeyOpsLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var snapshot = await GetChannelForApiKeyOpAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
            var disabledKeys = ParseDisabledKeys(snapshot);
            var now = DateTime.UtcNow;

            // OAuth channels expose the fixed OAuthCredentialRef as their only
            // credential identity, so callers pass the sentinel rather than the
            // rotating access token.
            var allKeys = ChannelCredentials.GetAllCredentialRefs(snapshot.Credentials);
            if (!allKeys.Contains(key, StringComparer.Ordinal))
            {
                return;
            }

            // Elapsed temporary disables are dropped before the new record is
            // appended; they no longer hide the credential.
            var activeKeys = new List<DisabledApiKey>(disabledKeys.Count + 1);
            foreach (var disabled in disabledKeys)
            {
                if (!disabled.IsExpired(now))
                {
                    activeKeys.Add(disabled);
                }
            }

            if (activeKeys.Any(disabled => string.Equals(disabled.Key, key, StringComparison.Ordinal)))
            {
                return;
            }

            activeKeys.Add(new DisabledApiKey
            {
                Key = key,
                DisabledAt = now,
                ErrorCode = errorCode,
                Reason = string.IsNullOrEmpty(reason) ? null : reason,
                ExpiresAt = expiresAt,
            });

            var enabledKeys = ChannelCredentials.GetEnabledCredentialRefs(
                snapshot.Credentials,
                activeKeys,
                new DateTimeOffset(now));

            var state = new ChannelApiKeyStateRecord
            {
                Id = channelId,
                DisabledApiKeysJson =
                    ChannelWriteSerialization.SerializeDisabledApiKeys(activeKeys),
            };

            if (enabledKeys.Count == 0)
            {
                state.Status = "disabled";
                state.ErrorMessage = $"All API keys disabled (last error: {errorCode})";
                state.AutoDisabledAt = now;
                state.ClearAutoDisableExpiresAt = true;
            }

            await SaveApiKeyStateAsync(state, "failed to disable api key", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ApiKeyOpsLock.Release();
        }
    }

    /// <summary>
    /// Port of ChannelService.EnableAPIKey (channel_apikey.go:147-192): removes
    /// the credential's disable record and recovers a channel that was disabled
    /// solely because every credential was unavailable.
    /// </summary>
    public async Task EnableApiKeyAsync(
        long channelId,
        string key,
        CancellationToken cancellationToken = default)
    {
        // The reference mutex (sync.Mutex) is not cancellable; block until it is free.
        await ApiKeyOpsLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var snapshot = await GetChannelForApiKeyOpAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
            var disabledKeys = ParseDisabledKeys(snapshot);
            if (disabledKeys.Count == 0)
            {
                return;
            }

            var newDisabledKeys = new List<DisabledApiKey>(disabledKeys.Count);
            var found = false;
            foreach (var disabled in disabledKeys)
            {
                if (string.Equals(disabled.Key, key, StringComparison.Ordinal))
                {
                    found = true;
                    continue;
                }

                newDisabledKeys.Add(disabled);
            }

            if (!found)
            {
                return;
            }

            await SaveApiKeyStateAsync(
                    BuildEnableState(snapshot, snapshot.Credentials, null, newDisabledKeys),
                    "failed to enable api key",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ApiKeyOpsLock.Release();
        }
    }

    /// <summary>
    /// Port of ChannelService.EnableAllAPIKeys (channel_apikey.go:195-226):
    /// clears disabled_api_keys and recovers the channel when an enabled
    /// credential remains.
    /// </summary>
    public async Task EnableAllApiKeysAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        // The reference mutex (sync.Mutex) is not cancellable; block until it is free.
        await ApiKeyOpsLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var snapshot = await GetChannelForApiKeyOpAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
            var disabledKeys = ParseDisabledKeys(snapshot);
            if (disabledKeys.Count == 0)
            {
                return;
            }

            await SaveApiKeyStateAsync(
                    BuildEnableState(snapshot, snapshot.Credentials, null, []),
                    "failed to enable all api keys",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ApiKeyOpsLock.Release();
        }
    }

    /// <summary>
    /// Port of ChannelService.EnableSelectedAPIKeys (channel_apikey.go:229-278):
    /// removes only the named disable records; a no-op list or an unmatched
    /// selection skips the read entirely.
    /// </summary>
    public async Task EnableSelectedApiKeysAsync(
        long channelId,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return;
        }

        // The reference mutex (sync.Mutex) is not cancellable; block until it is free.
        await ApiKeyOpsLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var snapshot = await GetChannelForApiKeyOpAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
            var disabledKeys = ParseDisabledKeys(snapshot);
            if (disabledKeys.Count == 0)
            {
                return;
            }

            var keysToEnable = new HashSet<string>(keys, StringComparer.Ordinal);
            var newDisabledKeys = new List<DisabledApiKey>(disabledKeys.Count);
            foreach (var disabled in disabledKeys)
            {
                if (!keysToEnable.Contains(disabled.Key))
                {
                    newDisabledKeys.Add(disabled);
                }
            }

            if (newDisabledKeys.Count == disabledKeys.Count)
            {
                return;
            }

            await SaveApiKeyStateAsync(
                    BuildEnableState(snapshot, snapshot.Credentials, null, newDisabledKeys),
                    "failed to enable selected api keys",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ApiKeyOpsLock.Release();
        }
    }

    /// <summary>
    /// Port of ChannelService.DeleteDisabledAPIKeys (channel_apikey.go:288-370):
    /// removes the named keys from both disabled_api_keys and the credentials,
    /// refuses OAuth channels, and restores one submitted key when deletion
    /// would leave the channel without any credential.
    /// </summary>
    public async Task<DeleteDisabledApiKeysResult> DeleteDisabledApiKeysAsync(
        long channelId,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return new DeleteDisabledApiKeysResult(true, string.Empty);
        }

        // The reference mutex (sync.Mutex) is not cancellable; block until it is free.
        await ApiKeyOpsLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var snapshot = await GetChannelForApiKeyOpAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
            var disabledKeys = ParseDisabledKeys(snapshot);

            ChannelCredentialsDraft credentials;
            try
            {
                credentials = ChannelWriteSerialization.ParseCredentials(snapshot.Credentials);
            }
            catch (JsonException ex)
            {
                // ent decodes credentials inside Channel.Get.
                throw new InvalidOperationException("failed to get channel: " + ex.Message, ex);
            }

            if (ChannelCredentials.IsOAuth(snapshot.Credentials))
            {
                throw new InvalidOperationException(
                    "cannot delete API keys for OAuth channels");
            }

            var keysToDelete = new HashSet<string>(keys, StringComparer.Ordinal);
            var newDisabledKeys = new List<DisabledApiKey>(disabledKeys.Count);
            foreach (var disabled in disabledKeys)
            {
                if (!keysToDelete.Contains(disabled.Key))
                {
                    newDisabledKeys.Add(disabled);
                }
            }

            if (credentials.ApiKeys is { Count: > 0 } apiKeys)
            {
                var filtered = new List<string>(apiKeys.Count);
                foreach (var apiKey in apiKeys)
                {
                    if (!keysToDelete.Contains(apiKey))
                    {
                        filtered.Add(apiKey);
                    }
                }

                // Go assigns an empty non-nil slice; omitempty drops it again.
                credentials.ApiKeys = filtered.Count == 0 ? null : filtered;
            }

            if (!string.IsNullOrEmpty(credentials.ApiKey)
                && keysToDelete.Contains(credentials.ApiKey))
            {
                credentials.ApiKey = null;
            }

            var credentialsJson = GoJson.MarshalToString(credentials);

            // The key set is measured before the preserve-last-key restore, so
            // the reference reports ONE_KEY_PRESERVED based on this snapshot.
            var preserveOneKey = ChannelCredentials.GetAllApiKeys(credentialsJson).Count == 0;
            if (preserveOneKey)
            {
                credentials.ApiKeys = [keys[0]];
                credentialsJson = GoJson.MarshalToString(credentials);
            }

            await SaveApiKeyStateAsync(
                    BuildEnableState(snapshot, credentialsJson, credentialsJson, newDisabledKeys),
                    "failed to delete disabled api keys",
                    cancellationToken)
                .ConfigureAwait(false);

            return new DeleteDisabledApiKeysResult(
                true,
                preserveOneKey ? "ONE_KEY_PRESERVED" : string.Empty);
        }
        finally
        {
            ApiKeyOpsLock.Release();
        }
    }

    /// <summary>
    /// Builds the state row shared by the enable/delete operations: writes the
    /// new disabled list, optionally rewrites credentials, and applies
    /// applyRecoveredChannelStatus (channel_apikey.go:372-391).
    /// </summary>
    private static ChannelApiKeyStateRecord BuildEnableState(
        ChannelRecord snapshot,
        string credentialsJsonForRefs,
        string? credentialsJsonToWrite,
        IReadOnlyList<DisabledApiKey> disabledKeys)
    {
        var now = DateTime.UtcNow;
        var state = new ChannelApiKeyStateRecord
        {
            Id = snapshot.Id,
            DisabledApiKeysJson = ChannelWriteSerialization.SerializeDisabledApiKeys(disabledKeys),
            CredentialsJson = credentialsJsonToWrite,
        };

        if (snapshot.Status != "disabled"
            || snapshot.ErrorMessage is null
            || !snapshot.ErrorMessage.StartsWith(
                "All API keys disabled",
                StringComparison.Ordinal)
            || ChannelCredentials.GetEnabledCredentialRefs(
                credentialsJsonForRefs,
                disabledKeys,
                new DateTimeOffset(now)).Count == 0)
        {
            return state;
        }

        state.Status = "enabled";
        state.ClearErrorMessage = true;
        state.ClearAutoDisabledAt = true;
        state.ClearAutoDisableExpiresAt = true;
        return state;
    }

    private async Task<ChannelRecord> GetChannelForApiKeyOpAsync(
        long channelId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await channels.GetAsync(channelId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "failed to get channel: ent: channel not found");
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private static List<DisabledApiKey> ParseDisabledKeys(ChannelRecord snapshot)
    {
        try
        {
            return ChannelWriteSerialization.ParseDisabledApiKeys(snapshot.DisabledApiKeys);
        }
        catch (JsonException ex)
        {
            // ent decodes disabled_api_keys inside Channel.Get.
            throw new InvalidOperationException("failed to get channel: " + ex.Message, ex);
        }
    }

    private async Task SaveApiKeyStateAsync(
        ChannelApiKeyStateRecord state,
        string action,
        CancellationToken cancellationToken)
    {
        bool updated;
        try
        {
            updated = await channels.UpdateApiKeyStateAsync(state, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException(action + ": " + ex.Message, ex);
        }

        if (!updated)
        {
            throw new InvalidOperationException(action + ": ent: channel not found");
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
