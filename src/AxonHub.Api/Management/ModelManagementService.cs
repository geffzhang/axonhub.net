using AxonHub.Abstractions;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Models;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of ent.CreateModelInput (ent.graphql:1697-1736).</summary>
public sealed record CreateModelCommand
{
    public required string Developer { get; init; }

    public required string ModelId { get; init; }

    /// <summary>ModelType; null leaves the schema default (chat).</summary>
    public string? Type { get; init; }

    public required string Name { get; init; }

    public required string Icon { get; init; }

    public required string Group { get; init; }

    public required ModelCard ModelCard { get; init; }

    public required ModelEntitySettings Settings { get; init; }

    public string? Remark { get; init; }
}

/// <summary>Port of ent.UpdateModelInput (ent.graphql:6282-6321).</summary>
public sealed record UpdateModelCommand
{
    public required long Id { get; init; }

    public string? Developer { get; init; }

    public string? ModelId { get; init; }

    public string? Type { get; init; }

    public string? Name { get; init; }

    public string? Icon { get; init; }

    public string? Group { get; init; }

    public ModelCard? ModelCard { get; init; }

    public ModelEntitySettings? Settings { get; init; }

    public string? Status { get; init; }

    public string? Remark { get; init; }

    public bool ClearRemark { get; init; }
}

/// <summary>Port of biz.ModelIdentityWithStatus (channel.go:428-432).</summary>
public sealed record ModelIdentityWithStatus(string Id, string Status);

/// <summary>Port of biz.ListModelsInput (channel.go:421-426).</summary>
public sealed record ListModelsCommand
{
    public IReadOnlyList<string>? StatusIn { get; init; }

    public bool IncludeMapping { get; init; }

    public bool IncludePrefix { get; init; }

    public bool IncludeAllChannelModels { get; init; }
}

/// <summary>Port of biz.UnassociatedChannel (model.go:939-942).</summary>
public sealed record UnassociatedChannel(
    ChannelRecord Channel,
    IReadOnlyList<string> Models);

/// <summary>
/// Port of biz.ModelService (internal/server/biz/model.go:356-880) and the
/// model-list half of biz.ChannelService (channel.go:450-523). Every ent call
/// in the reference goes through a privacy-filtered client; the port evaluates
/// the same decisions explicitly before touching the stores (C177).
/// </summary>
/// <remarks>
/// The Model and Channel schemas declare the identical policy
/// (internal/ent/schema/model.go:71-83, channel.go:233-245), so the channel
/// queries in the association helpers use <see cref="ModelPrivacy"/> too.
/// Go map iterations (channel model entries, the ListModels dedupe map, the
/// unassociated map) are determinized by sorting in the port (C177).
/// </remarks>
public sealed class ModelManagementService(
    IModelStore models,
    IChannelAdminStore channels,
    IModelSettingsProvider settings,
    ModelPrivacy privacy,
    AuthorizationScope scope,
    IManagementIdentity identity,
    ModelFetcher fetcher)
{
    /// <summary>Ent's NotFoundError text for the Model label.</summary>
    public const string ModelNotFound = "ent: model not found";

    /// <summary>Port of ModelService.CreateModel (model.go:356-391).</summary>
    public Task<ModelRecord> CreateAsync(
        CreateModelCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return models.RunInTransactionAsync(
            async token =>
            {
                var settingsJson = ValidateAndSerializeSettings(command.Settings);

                await privacy.EvaluateQueryAsync(token).ConfigureAwait(false);

                ModelRecord? existing;
                try
                {
                    existing = await models.GetFirstByModelIdAsync(command.ModelId, token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to check model existence: " + ex.Message,
                        ex);
                }

                if (existing is not null)
                {
                    throw new CodedException(
                        CodedException.DuplicateName,
                        "model name '" + command.ModelId + "' already exists",
                        new Dictionary<string, object?>
                        {
                            ["resource"] = "model",
                            ["field"] = "name",
                            ["value"] = command.ModelId,
                        });
                }

                await privacy.EvaluateMutationAsync(token).ConfigureAwait(false);

                var record = new ModelRecord
                {
                    Developer = command.Developer,
                    ModelId = command.ModelId,
                    Type = command.Type ?? string.Empty,
                    Name = command.Name,
                    Icon = command.Icon,
                    Group = command.Group,
                    ModelCard = ModelCardCodec.Serialize(command.ModelCard),
                    Settings = settingsJson,
                    Remark = command.Remark,
                };

                try
                {
                    await models.CreateAsync(record, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to create model: " + ex.Message,
                        ex);
                }

                return record;
            },
            cancellationToken);
    }

    /// <summary>Port of ModelService.BulkCreateModels (model.go:393-466).</summary>
    public Task<IReadOnlyList<ModelRecord>> BulkCreateAsync(
        IReadOnlyList<CreateModelCommand> commands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);

        return models.RunInTransactionAsync<IReadOnlyList<ModelRecord>>(
            async token =>
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var records = new List<ModelRecord>(commands.Count);
                foreach (var command in commands)
                {
                    var settingsJson = ValidateAndSerializeSettings(command.Settings);

                    var key = command.Developer + ":" + command.ModelId;
                    if (!seen.Add(key))
                    {
                        throw new InvalidOperationException(
                            "duplicate model in input: developer '"
                            + command.Developer + "' and modelId '"
                            + command.ModelId + "'");
                    }

                    records.Add(new ModelRecord
                    {
                        Developer = command.Developer,
                        ModelId = command.ModelId,
                        Type = command.Type ?? string.Empty,
                        Name = command.Name,
                        Icon = command.Icon,
                        Group = command.Group,
                        ModelCard = ModelCardCodec.Serialize(command.ModelCard),
                        Settings = settingsJson,
                        Remark = command.Remark,
                    });
                }

                await privacy.EvaluateQueryAsync(token).ConfigureAwait(false);

                IReadOnlyList<ModelRecord> existing;
                try
                {
                    existing = await models.ListByDeveloperModelIdPairsAsync(
                            [.. commands.Select(static command =>
                                new ModelDeveloperModelId(command.Developer, command.ModelId))],
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to check existing models: " + ex.Message,
                        ex);
                }

                if (existing.Count > 0)
                {
                    // fmt.Errorf("%v", []string) renders "[a:b c:d]".
                    var keys = existing.Select(static record =>
                        record.Developer + ":" + record.ModelId);
                    throw new InvalidOperationException(
                        "models already exist: [" + string.Join(" ", keys) + "]");
                }

                await privacy.EvaluateMutationAsync(token).ConfigureAwait(false);

                try
                {
                    return await models.CreateBulkAsync(records, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to bulk create models: " + ex.Message,
                        ex);
                }
            },
            cancellationToken);
    }

    /// <summary>Port of ModelService.UpdateModel (model.go:468-514).</summary>
    public async Task<ModelRecord> UpdateAsync(
        UpdateModelCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var settingsJson = command.Settings is null
            ? null
            : ValidateAndSerializeSettings(command.Settings);

        await privacy.EvaluateMutationAsync(cancellationToken).ConfigureAwait(false);

        ModelRecord? record;
        try
        {
            record = await models.UpdateAsync(
                    new ModelUpdate
                    {
                        Id = command.Id,
                        Developer = command.Developer,
                        ModelId = command.ModelId,
                        Type = command.Type,
                        Name = command.Name,
                        Icon = command.Icon,
                        Group = command.Group,
                        ModelCard = command.ModelCard is null
                            ? null
                            : ModelCardCodec.Serialize(command.ModelCard),
                        Settings = settingsJson,
                        Status = command.Status,
                        Remark = command.Remark,
                        ClearRemark = command.ClearRemark,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to update model: " + ex.Message,
                ex);
        }

        return record ?? throw new InvalidOperationException(
            "failed to update model: " + ModelNotFound);
    }

    /// <summary>Port of ModelService.UpdateModelStatus (model.go:516-528).</summary>
    public async Task<ModelRecord> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        await privacy.EvaluateMutationAsync(cancellationToken).ConfigureAwait(false);

        ModelRecord? record;
        try
        {
            record = await models.UpdateAsync(
                    new ModelUpdate { Id = id, Status = status },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to update model status: " + ex.Message,
                ex);
        }

        return record ?? throw new InvalidOperationException(
            "failed to update model status: " + ModelNotFound);
    }

    /// <summary>Port of ModelService.DeleteModel (model.go:530-536).</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await privacy.EvaluateMutationAsync(cancellationToken).ConfigureAwait(false);

        int affected;
        try
        {
            affected = await models.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to delete model: " + ex.Message, ex);
        }

        // ent's DeleteOneID returns NotFoundError when the DELETE touched no row.
        if (affected == 0)
        {
            throw new InvalidOperationException("failed to delete model: " + ModelNotFound);
        }
    }

    /// <summary>Port of ModelService.BulkArchiveModels (model.go:538-550).</summary>
    public Task BulkArchiveAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(ids, "archived", "failed to bulk archive models: ", cancellationToken);

    /// <summary>Port of ModelService.BulkDisableModels (model.go:552-564).</summary>
    public Task BulkDisableAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(ids, "disabled", "failed to bulk disable models: ", cancellationToken);

    /// <summary>Port of ModelService.BulkEnableModels (model.go:566-578).</summary>
    public Task BulkEnableAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(ids, "enabled", "failed to bulk enable models: ", cancellationToken);

    /// <summary>Port of ModelService.BulkDeleteModels (model.go:580-596).</summary>
    public async Task BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // The mutation hook runs even when IDIn() renders an always-false
        // predicate, so privacy is evaluated for empty id lists too.
        await privacy.EvaluateMutationAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await models.BulkDeleteAsync(ids, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to bulk delete models: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of the entgql Model connection (ent.resolvers.go:416-424).</summary>
    public async Task<ListPage<ModelRecord>> ListAsync(
        ModelListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);
        return await models.ListAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of the entgql Node lookup for Model (privacy applies).</summary>
    public async Task<ModelRecord> GetNodeAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);
        var record = await models.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return record ?? throw new InvalidOperationException(ModelNotFound);
    }

    /// <summary>
    /// Port of ModelService.QueryModelChannelConnections (model.go:598-624).
    /// The channel query is ordered by ordering_weight DESC; the port adds the
    /// store's id tie-break to make the reference's unspecified tie order
    /// deterministic (C177).
    /// </summary>
    public async Task<IReadOnlyList<ModelChannelConnection>> QueryModelChannelConnectionsAsync(
        IReadOnlyList<ModelAssociation?>? associations,
        CancellationToken cancellationToken = default)
    {
        if (associations is null || associations.Count == 0)
        {
            return [];
        }

        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ChannelRecord> channelRecords;
        try
        {
            channelRecords = await channels.ListAllAsync(
                    new ChannelListQuery
                    {
                        Where = new ChannelListFilter
                        {
                            StatusIn = ["enabled", "disabled"],
                        },
                        Ordered = true,
                        SortField = ChannelSortField.OrderingWeight,
                        Direction = SortDirection.Desc,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to query channels: " + ex.Message, ex);
        }

        return channelRecords.Count == 0
            ? []
            : ModelAssociationMatcher.MatchConnections(associations, channelRecords);
    }

    /// <summary>
    /// Port of ModelService.CountModelAssociatedChannels (model.go:800-803,
    /// 837-857): effective associations (developer inheritance applied) matched
    /// against enabled/disabled channels, counted by unique channel id.
    /// </summary>
    public async Task<int> CountModelAssociatedChannelsAsync(
        ModelRecord model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        var systemSettings = await settings.GetModelSettingsOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var effective = ModelSettingsInheritance.EffectiveModelAssociations(
            systemSettings,
            model.Developer,
            model.ModelId,
            ModelSettingsCodec.DeserializeEntitySettings(model.Settings));

        if (effective.Count == 0)
        {
            return 0;
        }

        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ChannelRecord> channelRecords;
        try
        {
            channelRecords = await channels.ListAllAsync(
                    new ChannelListQuery
                    {
                        Where = new ChannelListFilter
                        {
                            StatusIn = ["enabled", "disabled"],
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to query channels: " + ex.Message, ex);
        }

        if (channelRecords.Count == 0)
        {
            return 0;
        }

        var connections = ModelAssociationMatcher.MatchConnections(effective, channelRecords);
        return connections
            .Select(static connection => connection.Channel.Id)
            .Distinct()
            .Count();
    }

    /// <summary>Port of ModelService.QueryUnassociatedChannels (model.go:805-835).</summary>
    public async Task<IReadOnlyList<UnassociatedChannel>> QueryUnassociatedChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ChannelRecord> channelRecords;
        try
        {
            channelRecords = await channels.ListAllAsync(
                    new ChannelListQuery
                    {
                        Where = new ChannelListFilter
                        {
                            StatusIn = ["enabled", "disabled"],
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to query channels: " + ex.Message, ex);
        }

        if (channelRecords.Count == 0)
        {
            return [];
        }

        // The Model query policy is identical to the Channel one, but the
        // reference evaluates it on the models query specifically.
        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ModelRecord> modelRecords;
        try
        {
            modelRecords = await models.ListAllAsync(
                    new ModelListFilter { StatusIn = ["enabled", "disabled"] },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to query models: " + ex.Message, ex);
        }

        var systemSettings = await settings.GetModelSettingsOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var allAssociations = new List<ModelAssociation>();
        foreach (var model in modelRecords)
        {
            allAssociations.AddRange(
                ModelSettingsInheritance.EffectiveModelAssociations(
                    systemSettings,
                    model.Developer,
                    model.ModelId,
                    ModelSettingsCodec.DeserializeEntitySettings(model.Settings)));
        }

        return FindUnassociatedChannels(channelRecords, allAssociations);
    }

    /// <summary>
    /// Port of queryResolver.QueryModels (model.resolvers.go:160-227): channel
    /// models when the system setting (or the input) asks for all channel
    /// models, otherwise the configured Model rows.
    /// </summary>
    public async Task<IReadOnlyList<ModelIdentityWithStatus>> QueryModelsAsync(
        ListModelsCommand input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var systemSettings = await settings.GetModelSettingsOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        async Task<IReadOnlyList<ModelIdentityWithStatus>> ListAsync(
            Func<bool, CancellationToken, Task<IReadOnlyList<ModelIdentityWithStatus>>> list,
            CancellationToken token)
        {
            if (await scope.HasScopeAsync(AuthorizationScope.ReadChannels, token)
                    .ConfigureAwait(false))
            {
                return await list(false, token).ConfigureAwait(false);
            }

            if (identity.GetCurrentProjectId() is null
                || (!await scope.HasScopeAsync(AuthorizationScope.WriteApiKeys, token)
                        .ConfigureAwait(false)
                    && !await scope.HasScopeAsync(AuthorizationScope.WriteRequests, token)
                        .ConfigureAwait(false)))
            {
                await scope.RequireScopeAsync(AuthorizationScope.ReadChannels, token)
                    .ConfigureAwait(false);
            }

            // authz.RunWithSystemBypass runs the listing under a privacy bypass.
            return await list(true, token).ConfigureAwait(false);
        }

        if (systemSettings.QueryAllChannelModels || input.IncludeAllChannelModels)
        {
            return await ListAsync(
                    (bypass, token) => ChannelListModelsAsync(input, bypass, token),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // model.Status(status.String()) — the same vocabulary as ChannelStatus.
        IReadOnlyList<string> modelStatusIn = input.StatusIn ?? [];

        return await ListAsync(
                (bypass, token) => ListModelsAsync(modelStatusIn, bypass, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of ModelService.ListModels (model.go:626-656).</summary>
    public async Task<IReadOnlyList<ModelIdentityWithStatus>> ListModelsAsync(
        IReadOnlyList<string>? statusIn,
        bool bypassPrivacy,
        CancellationToken cancellationToken = default)
    {
        if (!bypassPrivacy)
        {
            await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ModelRecord> records;
        try
        {
            records = await models.ListAllAsync(
                    statusIn is { Count: > 0 }
                        ? new ModelListFilter { StatusIn = statusIn }
                        : new ModelListFilter { Status = "enabled" },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to query configured models: " + ex.Message,
                ex);
        }

        return
        [
            .. records.Select(static record =>
                new ModelIdentityWithStatus(record.ModelId, record.Status)),
        ];
    }

    /// <summary>
    /// Port of ChannelService.ListModels (channel.go:456-523). The reference
    /// iterates Go maps, so both the per-channel entry order and the final
    /// slice order are unspecified; the port sorts the result by model id to
    /// make it deterministic (C177).
    /// </summary>
    public async Task<IReadOnlyList<ModelIdentityWithStatus>> ChannelListModelsAsync(
        ListModelsCommand input,
        bool bypassPrivacy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!bypassPrivacy)
        {
            await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ChannelRecord> channelRecords;
        try
        {
            channelRecords = await channels.ListAllAsync(
                    new ChannelListQuery
                    {
                        Where = new ChannelListFilter
                        {
                            StatusIn = input.StatusIn is { Count: > 0 }
                                ? input.StatusIn
                                : ["enabled"],
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to query channels: " + ex.Message, ex);
        }

        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var channel in channelRecords)
        {
            var descriptor = ChannelMapping.ToDescriptor(channel);

            if (input.IncludeAllChannelModels)
            {
                foreach (var requestModel in ChannelModelEntries.Build(descriptor).Keys)
                {
                    SetModelStatus(statuses, requestModel, channel.Status);
                }

                continue;
            }

            var supported = ChannelModelEntries.BuildDirect(descriptor).Keys
                .ToList();
            foreach (var modelId in supported)
            {
                SetModelStatus(statuses, modelId, channel.Status);
            }

            if (!input.IncludeMapping && !input.IncludePrefix)
            {
                continue;
            }

            var settingsDraft = ChannelWriteSerialization.ParseSettings(channel.Settings);
            if (settingsDraft is null)
            {
                continue;
            }

            if (input.IncludeMapping)
            {
                foreach (var mapping in settingsDraft.ModelMappings ?? [])
                {
                    if (supported.Contains(mapping.To, StringComparer.Ordinal))
                    {
                        SetModelStatus(statuses, mapping.From, channel.Status);
                    }
                }
            }

            if (input.IncludePrefix && settingsDraft.ExtraModelPrefix.Length != 0)
            {
                foreach (var modelId in supported)
                {
                    SetModelStatus(
                        statuses,
                        settingsDraft.ExtraModelPrefix + "/" + modelId,
                        channel.Status);
                }
            }
        }

        return
        [
            .. statuses
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new ModelIdentityWithStatus(pair.Key, pair.Value)),
        ];
    }

    /// <summary>Port of queryResolver.FetchModels (model.resolvers.go:126-152).</summary>
    public async Task<FetchModelsResult> FetchModelsAsync(
        FetchModelsInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await scope.RequireScopeAsync(AuthorizationScope.WriteChannels, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await fetcher.FetchModelsAsync(input, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to fetch models: " + ex.Message, ex);
        }
    }

    private static string ValidateAndSerializeSettings(ModelEntitySettings value)
    {
        try
        {
            var normalized = ModelAssociationValidation.ValidateEntitySettings(value)!;
            return ModelSettingsCodec.SerializeEntitySettings(normalized);
        }
        catch (ModelSettingsValidationException ex)
        {
            // The reference returns the validator error unwrapped.
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private async Task BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // The mutation hook runs even when IDIn() renders an always-false
        // predicate, so privacy is evaluated for empty id lists too.
        await privacy.EvaluateMutationAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await models.BulkUpdateStatusAsync(ids, status, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(errorPrefix + ex.Message, ex);
        }
    }

    /// <summary>Port of findUnassociatedChannels (model.go:902-960).</summary>
    private static List<UnassociatedChannel> FindUnassociatedChannels(
        IReadOnlyList<ChannelRecord> channelRecords,
        IReadOnlyList<ModelAssociation?> associations)
    {
        if (channelRecords.Count == 0)
        {
            return [];
        }

        var descriptors = channelRecords
            .Select(ChannelMapping.ToDescriptor)
            .ToList();
        var connections = ModelAssociationMatcher.MatchConnections(associations, channelRecords);

        var associated = new HashSet<(long ChannelId, string ModelId)>();
        foreach (var connection in connections)
        {
            foreach (var entry in connection.Models)
            {
                associated.Add((connection.Channel.Id, entry.RequestModel));
            }
        }

        var result = new List<UnassociatedChannel>();
        for (var index = 0; index < channelRecords.Count; index++)
        {
            var channel = channelRecords[index];
            var entries = ChannelModelEntries.Build(descriptors[index]);

            var unassociated = entries.Keys
                .Where(modelId => !associated.Contains((channel.Id, modelId)))
                .OrderBy(static modelId => modelId, StringComparer.Ordinal)
                .ToList();

            if (unassociated.Count > 0)
            {
                result.Add(new UnassociatedChannel(channel, unassociated));
            }
        }

        return result;
    }

    /// <summary>Port of setModelStatus (channel.go:442-454): enabled > disabled > archived.</summary>
    private static void SetModelStatus(
        Dictionary<string, string> statuses,
        string modelId,
        string newStatus)
    {
        if (!statuses.TryGetValue(modelId, out var existing)
            || StatusPriority(newStatus) > StatusPriority(existing))
        {
            statuses[modelId] = newStatus;
        }
    }

    private static int StatusPriority(string status) => status switch
    {
        "enabled" => 3,
        "disabled" => 2,
        "archived" => 1,
        _ => 0,
    };
}
