using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Llm;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Models;

/// <summary>
/// Port of biz.ModelFacade: one model exposed to clients. The reference builds
/// it from either a configured Model entity or a channel's model entry.
/// </summary>
public sealed record ModelFacade(
    string Id,
    string DisplayName,
    DateTime CreatedAt,
    long Created,
    string OwnedBy);

/// <summary>
/// Port of biz.ModelService.ListEnabledModels
/// (internal/server/biz/model.go:646-755) and queryConfiguredModelFacades
/// (model.go:744-799).
/// </summary>
/// <remarks>
/// The reference reads the enabled-channel cache (<c>GetEnabledChannels</c>),
/// applies the <c>read_channels</c> scope decision, narrows the channels by the
/// API key's project and key profiles, then lists the enabled configured Model
/// entities whose associations match those channels. When
/// <c>QueryAllChannelModels</c> is set it merges the channel-derived entries
/// behind the configured ones, applying <c>ModelBlacklistRegex</c> to
/// channel-derived IDs only.<para />
/// The port reads the enabled channels live instead of through the reference's
/// live cache (C149), and evaluates the scope decision explicitly instead of
/// letting the ent privacy policy deny the configured-model query (the deny is
/// surfaced with the reference's wrapped text, C178).
/// </remarks>
public sealed class ModelService(
    IChannelStore channels,
    IModelStore models,
    IModelSettingsProvider settings,
    IManagementIdentity identity,
    AuthorizationScope scope)
{
    /// <summary>Port of ModelService.ListEnabledModels.</summary>
    public async Task<IReadOnlyList<ModelFacade>> ListEnabledModelsAsync(
        CancellationToken cancellationToken = default)
    {
        // authz.WithScopeDecision runs before the configured-model query; a
        // denying decision makes that query return ent/privacy.Deny. The port
        // checks the decision up front so no store read happens under a deny
        // and the surfaced error carries the same wrap (C178).
        if (!await scope.HasScopeAsync(AuthorizationScope.ReadChannels, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new PrivacyDenyException(
                "failed to list configured models: " + PrivacyDenyException.DenyMessage);
        }

        var enabledChannels = await channels.ListEnabledAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<ChannelRecord> scopedChannels = enabledChannels;
        IReadOnlyList<string>? allowedModelIds = null;

        // contexts.GetAPIKey: the key and its project edge were loaded during
        // authentication (C178).
        var apiKey = identity.GetCurrentApiKey();
        if (apiKey is not null)
        {
            // Project-level profile filtering (upper boundary).
            var projectProfile = ActiveProjectProfile(identity.GetCurrentProject());
            if (projectProfile is not null)
            {
                if (projectProfile.ChannelIDs is { Count: > 0 } projectChannelIds)
                {
                    scopedChannels = [.. scopedChannels.Where(
                        channel => projectChannelIds.Any(id => id == channel.Id))];
                }

                if (projectProfile.ChannelTags is { Count: > 0 } projectTags)
                {
                    scopedChannels = [.. scopedChannels.Where(
                        channel => ChannelTagsCodec.Match(
                            projectTags,
                            projectProfile.ChannelTagsMatchMode,
                            ChannelTagsCodec.Parse(channel.Tags)))];
                }
            }

            // Key-level profile filtering (narrows further within project scope).
            var profile = ActiveApiKeyProfile(apiKey);
            if (profile is not null)
            {
                if (profile.ChannelIDs is { Count: > 0 } channelIds)
                {
                    scopedChannels = [.. scopedChannels.Where(
                        channel => channelIds.Any(id => id == channel.Id))];
                }

                if (profile.ChannelTags is { Count: > 0 } channelTags)
                {
                    scopedChannels = [.. scopedChannels.Where(
                        channel => ChannelTagsCodec.Match(
                            channelTags,
                            profile.ChannelTagsMatchMode,
                            ChannelTagsCodec.Parse(channel.Tags)))];
                }
            }

            if (profile is not null && profile.ModelIDs is { Count: > 0 } modelIds)
            {
                allowedModelIds = modelIds;
            }
        }

        var (configuredModels, suppressedIds) =
            await QueryConfiguredModelFacadesAsync(allowedModelIds, scopedChannels, cancellationToken)
                .ConfigureAwait(false);

        var modelSettings = await settings.GetModelSettingsOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!modelSettings.QueryAllChannelModels)
        {
            return configuredModels;
        }

        // QueryAllChannelModels=true: merge configured models (higher priority)
        // with channel models.
        var merged = new List<ModelFacade>(configuredModels);
        var modelSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configured in configuredModels)
        {
            modelSet.Add(configured.Id);
        }

        // Hidden configured IDs must not reappear as channel-derived facades.
        // Routing still binds those IDs to the configured associations.
        foreach (var suppressedId in suppressedIds)
        {
            modelSet.Add(suppressedId);
        }

        var blacklist = modelSettings.ModelBlacklistRegex;
        foreach (var channel in scopedChannels)
        {
            // ch.GetModelEntries() is the same derivation the load balancer
            // uses. The reference iterates a Go map (unspecified order); the
            // port sorts each channel's entries (C088).
            var entries = ChannelModelEntries.Build(ChannelMapping.ToDescriptor(channel)).Values
                .OrderBy(static entry => entry.RequestModel, StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                var requestModel = entry.RequestModel;
                if (modelSet.Contains(requestModel))
                {
                    continue;
                }

                // Channel-derived models matching the blacklist regex are
                // excluded. Configured Model entities above are not affected.
                // Cache the decision in modelSet so the same model ID coming
                // from another channel skips the regex match.
                if (blacklist.Length > 0 && XRegexp.MatchString(blacklist, requestModel))
                {
                    modelSet.Add(requestModel);
                    continue;
                }

                modelSet.Add(requestModel);

                merged.Add(new ModelFacade(
                    requestModel,
                    requestModel,
                    channel.CreatedAt,
                    GoTime.UnixSeconds(channel.CreatedAt),
                    channel.Type));
            }
        }

        // Apply model filtering from the key profile.
        if (allowedModelIds is { Count: > 0 })
        {
            merged = [.. merged.Where(model => allowedModelIds.Contains(model.Id, StringComparer.Ordinal))];
        }

        return merged;
    }

    /// <summary>
    /// Port of queryConfiguredModelFacades: enabled Model entities whose
    /// effective associations match a channel, with structurally unroutable IDs
    /// suppressed when <c>HideUnroutableModelsInList</c> is set.
    /// </summary>
    private async Task<(IReadOnlyList<ModelFacade> Models, IReadOnlyCollection<string> SuppressedIds)>
        QueryConfiguredModelFacadesAsync(
            IReadOnlyList<string>? allowedModelIds,
            IReadOnlyList<ChannelRecord> scopedChannels,
            CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelRecord> enabledModels;
        try
        {
            enabledModels = await models.ListAllAsync(
                    new ModelListFilter
                    {
                        Status = "enabled",
                        // ent's ModelIDIn is skipped for an empty set: the
                        // caller passes nil when the profile has no ModelIDs.
                        ModelIdIn = allowedModelIds is { Count: > 0 } ? allowedModelIds : null,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to list configured models: " + ex.Message,
                ex);
        }

        var systemSettings = await settings.GetModelSettingsOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var facadeModels = new List<ModelFacade>();
        var suppressedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var model in enabledModels)
        {
            // ent scans model_card and settings while decoding the row; a
            // malformed document fails the whole query with these wraps.
            var entitySettings = DeserializeRow(
                () => ModelSettingsCodec.DeserializeEntitySettings(model.Settings),
                "unmarshal field settings: ");
            _ = DeserializeRow(() => ModelCardCodec.Deserialize(model.ModelCard), "unmarshal field model_card: ");

            var effectiveAssociations = ModelSettingsInheritance.EffectiveModelAssociations(
                systemSettings,
                model.Developer,
                model.ModelId,
                entitySettings);
            var connections = ModelAssociationMatcher.MatchConnections(
                effectiveAssociations,
                scopedChannels);
            if (connections.Count == 0)
            {
                continue;
            }

            if (systemSettings.HideUnroutableModelsInList
                && !HasCapableEndpointForModel(model, connections))
            {
                suppressedIds.Add(model.ModelId);
                continue;
            }

            facadeModels.Add(new ModelFacade(
                model.ModelId,
                model.ModelId,
                model.CreatedAt,
                GoTime.UnixSeconds(model.CreatedAt),
                "configured"));
        }

        return (facadeModels, suppressedIds);
    }

    private static T DeserializeRow<T>(Func<T> decode, string prefix)
    {
        try
        {
            return decode();
        }
        catch (JsonException ex)
        {
            // queryConfiguredModelFacades wraps the ent query error (C178).
            throw new InvalidOperationException(
                "failed to list configured models: " + prefix + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of hasCapableEndpointForModel (model_routability.go): a list-time
    /// capability check that ignores the When conditions, stream policy and
    /// quota so /v1/models stays stable.
    /// </summary>
    private static bool HasCapableEndpointForModel(
        ModelRecord model,
        IReadOnlyList<ModelChannelConnection> connections)
    {
        var allowed = ApiCapabilities.CapableApiFormats(
            ApiCapabilities.RequestTypeForModelType(model.Type));
        if (allowed is null || allowed.Count == 0)
        {
            // Unknown model types keep the association-only visibility.
            return true;
        }

        foreach (var connection in connections)
        {
            var endpoints = ChannelEndpoints.Resolve(
                connection.Channel.Type,
                connection.Channel.Endpoints);
            foreach (var endpoint in endpoints)
            {
                if (allowed.Contains(endpoint.ApiFormat))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Port of Project.GetActiveProfile (ent/extra.go:16-28).</summary>
    private static ProjectProfileModel? ActiveProjectProfile(ProjectRecord? project)
    {
        if (project is null)
        {
            return null;
        }

        var profiles = ProjectProfilesCodec.Deserialize(project.Profiles);
        if (profiles.ActiveProfile.Length == 0)
        {
            return null;
        }

        foreach (var profile in profiles.Profiles ?? [])
        {
            if (string.Equals(profile.Name, profiles.ActiveProfile, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>Port of APIKey.GetActiveProfile (ent/extra.go:30-42).</summary>
    private static ApiKeyProfileModel? ActiveApiKeyProfile(ApiKeyRecord apiKey)
        => ApiKeyProfilesCodec.ActiveProfile(apiKey.Profiles);
}
