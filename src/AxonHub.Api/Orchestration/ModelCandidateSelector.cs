using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Persistence;
using RoutingPolicy = AxonHub.Api.Models.RoutingPolicy;

namespace AxonHub.Api.Orchestration;

/// <summary>Port of orchestrator.ModelRoutingPolicy (candidates.go:45-48).</summary>
public sealed record ModelRoutingPolicy(string LoadBalancerStrategy, string TraceStickyMode);

/// <summary>
/// The selector's output: the candidate list plus the model-level routing
/// policy the reference carries on every candidate
/// (<c>ChannelModelsCandidate.ModelRoutingPolicy</c>). Null for the legacy
/// channel fallback, whose candidates carry no policy.
/// </summary>
public sealed record CandidateSelection(
    IReadOnlyList<ChannelCandidate> Candidates,
    ModelRoutingPolicy? RoutingPolicy);

/// <summary>Port of orchestrator.CandidateSelector (candidates.go:60-63).</summary>
public interface IModelCandidateSelector
{
    Task<CandidateSelection> SelectAsync(
        LlmRequest request,
        RequestIdentity identity,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of orchestrator.DefaultSelector
/// (internal/server/orchestrator/candidates.go:110-615): routes a request model
/// through AxonHub Model associations first, and falls back to the legacy
/// channel-only selection when the model row does not exist and the system
/// setting allows it.
/// </summary>
/// <remarks>
/// <para>
/// The reference resolves associations through a five-minute cache keyed by the
/// model id, the channel cache version and the association signature
/// (resolveAssociations). The port keeps the same cache in the shared
/// <see cref="ModelAssociationCache"/> singleton, so scoped selector instances
/// and the cache diagnostics export observe one process-wide map like the
/// reference (§245).
/// </para>
/// <para>
/// <b>Test bypass.</b> The reference's tests call <c>authz.WithTestBypass(ctx)</c>
/// (internal/authz/test.go:12-17), which makes every privacy rule allow the
/// query. The port's test fixture emulates that marker by constructing this
/// selector with <c>bypassModelPrivacy: true</c>; production DI never sets it.
/// </para>
/// </remarks>
public sealed class ModelCandidateSelector : IModelCandidateSelector
{
    private readonly IModelStore _models;
    private readonly IModelSettingsProvider _settings;
    private readonly IChannelStore _channels;
    private readonly IEnabledChannelProvider _enabledChannels;
    private readonly IEnabledChannelCacheSnapshotProvider? _enabledChannelSnapshots;
    private readonly ModelAssociationCache? _associationCache;
    private readonly TimeProvider _timeProvider;
    private readonly bool _bypassModelPrivacy;

    public ModelCandidateSelector(
        IModelStore models,
        IModelSettingsProvider settings,
        IChannelStore channels,
        IEnabledChannelProvider enabledChannels,
        TimeProvider timeProvider,
        bool bypassModelPrivacy = false,
        IEnabledChannelCacheSnapshotProvider? enabledChannelSnapshots = null,
        ModelAssociationCache? associationCache = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(enabledChannels);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _models = models;
        _settings = settings;
        _channels = channels;
        _enabledChannels = enabledChannels;
        _enabledChannelSnapshots = enabledChannelSnapshots;
        _associationCache = associationCache;
        _timeProvider = timeProvider;
        _bypassModelPrivacy = bypassModelPrivacy;
    }

    /// <summary>
    /// Port of DefaultSelector.Select (candidates.go:110-127). A model row that
    /// exists but has no effective association returns an empty candidate list
    /// without consulting the legacy fallback; only a missing row may fall back.
    /// </summary>
    public async Task<CandidateSelection> SelectAsync(
        LlmRequest request,
        RequestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);

        try
        {
            return await SelectModelCandidatesAsync(request, identity, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ModelNotFoundException)
        {
            var settings = await _settings
                .GetModelSettingsOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (settings.FallbackToChannelsOnModelNotFound)
            {
                var legacy = await SelectChannelCandidatesAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                return new CandidateSelection(legacy, RoutingPolicy: null);
            }

            // fmt.Errorf("%w: %q", biz.ErrInvalidModel, req.Model).
            throw TransformerErrors.InvalidModel(GoCast.GoQuote(request.Model));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // fmt.Errorf("%w: %q", err, req.Model) preserves the query error text
            // and appends the quoted request model. A privacy deny stays typed so
            // the GraphQL presenter can still report FORBIDDEN (C179).
            var message = ex.Message + ": " + GoCast.GoQuote(request.Model);
            throw ex is PrivacyDenyException
                ? new PrivacyDenyException(message)
                : new InvalidOperationException(message, ex);
        }
    }

    /// <summary>Port of selectModelCandidates (candidates.go:167-243).</summary>
    private async Task<CandidateSelection> SelectModelCandidatesAsync(
        LlmRequest request,
        RequestIdentity identity,
        CancellationToken cancellationToken)
    {
        ModelRecord? record;
        try
        {
            if (!_bypassModelPrivacy)
            {
                EvaluateQueryPrivacy(identity);
            }

            record = await _models
                .GetFirstByModelIdAndStatusAsync(request.Model, ModelStatusEnabled, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PrivacyDenyException ex)
        {
            // ent evaluates the policy inside Query.First (model_query.go:337),
            // so the deny surfaces as a query error and selectModelCandidates
            // wraps it like any other query failure.
            throw new PrivacyDenyException("failed to query AxonHub Model: " + ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to query AxonHub Model: " + ex.Message,
                ex);
        }

        if (record is null)
        {
            // ent's Query().First returns *ent.NotFoundError for zero rows.
            throw new ModelNotFoundException();
        }

        var (card, entitySettings) = DecodeModelRow(record);

        var systemSettings = await _settings
            .GetModelSettingsOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var associations = ModelSettingsInheritance.EffectiveModelAssociations(
            systemSettings,
            record.Developer,
            record.ModelId,
            entitySettings);

        if (associations.Count == 0)
        {
            // A configured model without associations does NOT fall back to the
            // channel pool; the fallback only covers a missing model row.
            return new CandidateSelection([], RoutingPolicy: null);
        }

        IReadOnlyList<ChannelRecord> channels;
        long channelCacheVersion;
        try
        {
            if (_enabledChannelSnapshots is not null)
            {
                // One activation returns the enabled-channel contents and the
                // cache version that belong together. The reference reads the
                // version before GetEnabledChannels (candidates.go:255-259);
                // the snapshot is the same pair with no interleaving at all.
                var snapshot = await _enabledChannelSnapshots
                    .GetSnapshotAsync(cancellationToken)
                    .ConfigureAwait(false);
                channelCacheVersion = snapshot.Version;
                var rows = new List<ChannelRecord>(snapshot.Channels.Count);
                foreach (var channel in snapshot.Channels)
                {
                    rows.Add(ToRecord(channel.ToDescriptor()));
                }

                channels = rows;
            }
            else
            {
                // Manual constructions (tests) without the snapshot seam keep
                // the live store path; production always supplies the snapshot.
                channelCacheVersion = 0;
                channels = await _channels.ListEnabledAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to resolve associations: " + ex.Message,
                ex);
        }

        var resolved = ResolveAssociations(
            record,
            associations,
            channels,
            channelCacheVersion);
        var candidates = FilterResolvedCandidatesForRequest(request, resolved);
        if (candidates.Count == 0)
        {
            return new CandidateSelection([], RoutingPolicy: null);
        }

        var strategy = RoutingPolicy.Default;
        var traceStickyMode = RoutingPolicy.Default;
        if (entitySettings is not null)
        {
            strategy = RoutingPolicy.NormalizeValue(entitySettings.LoadBalancerStrategy);
            traceStickyMode = RoutingPolicy.NormalizeValue(entitySettings.TraceStickyMode);
        }

        var defaultMaxTokens = ModelCardOutputLimit(card);
        foreach (var candidate in candidates)
        {
            candidate.DefaultMaxTokens = defaultMaxTokens;
        }

        return new CandidateSelection(
            candidates,
            new ModelRoutingPolicy(strategy, traceStickyMode));
    }

    /// <summary>
    /// Port of resolveAssociations (candidates.go:250-394): resolve the
    /// association matches into the cached intermediate form, deferring the
    /// request-specific <c>When</c> filter to the caller. A hit requires the
    /// same association signature, channel count, latest channel update time,
    /// latest model update time and channel cache version, and a cachedAt
    /// younger than five minutes.
    /// </summary>
    private IReadOnlyList<ResolvedAssociationCandidate> ResolveAssociations(
        ModelRecord model,
        IReadOnlyList<ModelAssociation> associations,
        IReadOnlyList<ChannelRecord> channels,
        long channelCacheVersion)
    {
        if (channels.Count == 0)
        {
            return [];
        }

        var signature = ModelAssociationSignature.Compute(associations);
        var latestChannelUpdateTime = GetLatestChannelUpdateTime(channels);
        var now = _timeProvider.GetUtcNow();
        var key = new AssociationCacheKey(
            signature,
            channels.Count,
            ModelAssociationCache.ToUtc(latestChannelUpdateTime),
            ModelAssociationCache.ToUtc(model.UpdatedAt),
            channelCacheVersion);

        if (_associationCache is not null
            && _associationCache.TryGet(model.ModelId, key, now, out var cached))
        {
            return cached;
        }

        var resolved = MatchAssociations(associations, channels);

        _associationCache?.Set(model.ModelId, key, associations, resolved, now);
        return resolved;
    }

    /// <summary>
    /// Port of resolveAssociations' matching half (candidates.go:300-357): the
    /// structural match against the enabled channels, retaining each
    /// association's <c>When</c> for the post-cache request filter.
    /// </summary>
    private static List<ResolvedAssociationCandidate> MatchAssociations(
        IReadOnlyList<ModelAssociation> associations,
        IReadOnlyList<ChannelRecord> channels)
    {
        var matches = ModelAssociationMatcher.MatchAssociations(associations, channels);
        var channelMap = new Dictionary<long, ChannelRecord>(channels.Count);
        foreach (var channel in channels)
        {
            channelMap[channel.Id] = channel;
        }

        var resolved = new List<ResolvedAssociationCandidate>(matches.Count);
        foreach (var match in matches)
        {
            foreach (var connection in match.Connections)
            {
                if (!channelMap.TryGetValue(connection.Channel.Id, out var channel))
                {
                    continue;
                }

                resolved.Add(new ResolvedAssociationCandidate(
                    channel,
                    connection.Priority,
                    [.. connection.Models],
                    match.Association.When));
            }
        }

        return resolved;
    }

    /// <summary>
    /// Port of getLatestChannelUpdateTime (candidates.go:617-631). Empty
    /// channels return Go's zero time.
    /// </summary>
    private static DateTime GetLatestChannelUpdateTime(IReadOnlyList<ChannelRecord> channels)
    {
        if (channels.Count == 0)
        {
            return default;
        }

        var latest = channels[0].UpdatedAt;
        for (var index = 1; index < channels.Count; index++)
        {
            if (channels[index].UpdatedAt > latest)
            {
                latest = channels[index].UpdatedAt;
            }
        }

        return latest;
    }

    /// <summary>
    /// The enabled-channel snapshot carries the fields the association matcher
    /// and candidate builder read; this restores the row shape they consume.
    /// </summary>
    private static ChannelRecord ToRecord(ChannelDescriptor channel) => new()
    {
        Id = channel.Id,
        Name = channel.Name,
        OrderingWeight = channel.OrderingWeight,
        CreatedAt = channel.CreatedAt,
        UpdatedAt = channel.UpdatedAt,
        Type = channel.Type,
        BaseUrl = channel.BaseUrl,
        Status = "enabled",
        Credentials = channel.Credentials,
        DisabledApiKeys = channel.DisabledApiKeys,
        SupportedModels = channel.SupportedModels,
        Settings = channel.Settings,
        Endpoints = channel.Endpoints,
        DefaultTestModel = channel.DefaultTestModel,
        Tags = channel.Tags,
        Policies = channel.Policies,
    };

    /// <summary>
    /// Port of filterResolvedCandidatesForRequest (candidates_condition.go:19-71).
    /// </summary>
    private List<ChannelCandidate> FilterResolvedCandidatesForRequest(
        LlmRequest request,
        IReadOnlyList<ResolvedAssociationCandidate> resolved)
    {
        if (resolved.Count == 0)
        {
            return [];
        }

        var hasConditionalCandidates = false;
        foreach (var candidate in resolved)
        {
            if (candidate.When is not null)
            {
                hasConditionalCandidates = true;
                break;
            }
        }

        IReadOnlyList<ResolvedAssociationCandidate> filtered;
        if (!hasConditionalCandidates)
        {
            filtered = resolved;
        }
        else
        {
            // Every request-shape input is computed once, exactly like the
            // reference; time.Now() becomes the injected clock.
            var promptTokens = CandidateRequestFeatures.EstimatePromptTokens(request);
            var stream = CandidateRequestFeatures.RequestStream(request);
            var requestFormat = CandidateRequestFeatures.RequestApiFormat(request);
            var reasoningEffort = CandidateRequestFeatures.RequestReasoningEffort(request);
            var features = CandidateRequestFeatures.DetectRequestContentFeatures(request);
            var requestHeaders = CandidateRequestFeatures.BuildRequestHeaderMap(request);
            var now = _timeProvider.GetUtcNow();

            var matched = new List<ResolvedAssociationCandidate>(resolved.Count);
            foreach (var candidate in resolved)
            {
                if (!MatchesAssociationWhen(
                        candidate.When,
                        promptTokens,
                        stream,
                        requestFormat,
                        reasoningEffort,
                        features,
                        requestHeaders,
                        now))
                {
                    continue;
                }

                matched.Add(candidate);
            }

            filtered = matched;
        }

        var candidates = AggregateChannelModelCandidates(filtered);
        return PopulateApiFormat(candidates, request);
    }

    /// <summary>Port of matchesAssociationWhen (candidates_condition.go:73-89).</summary>
    private static bool MatchesAssociationWhen(
        ModelAssociationWhen? when,
        long promptTokens,
        bool stream,
        string requestFormat,
        string reasoningEffort,
        CandidateRequestFeatures.RequestContentFeatures features,
        IReadOnlyDictionary<string, string> requestHeaders,
        DateTimeOffset now)
    {
        if (when is null || !when.Enabled || when.Condition is null)
        {
            return true;
        }

        return AssociationConditionEvaluator.Evaluate(
            when.Condition,
            new AssociationConditionData(
                promptTokens,
                stream,
                requestFormat,
                reasoningEffort,
                features.HasImage,
                features.HasVideo,
                features.HasDocument,
                features.HasAudio,
                requestHeaders,
                now));
    }

    /// <summary>
    /// Port of aggregateChannelModelCandidates (candidates.go:551-607): merge
    /// connections of the same (channel, priority) into one candidate while a
    /// global (channel, actual model) set drops models already claimed by an
    /// earlier connection.
    /// </summary>
    private static List<ChannelCandidate> AggregateChannelModelCandidates(
        IReadOnlyList<ResolvedAssociationCandidate> resolved)
    {
        var candidates = new List<ChannelCandidate>(resolved.Count);
        var candidateIndexByKey = new Dictionary<(long ChannelId, int Priority), int>();
        var seenChannelModels = new HashSet<(long ChannelId, string ActualModel)>();

        foreach (var candidate in resolved)
        {
            var key = (candidate.Channel.Id, candidate.Priority);
            var modelsToAppend = new List<ChannelModelEntry>(candidate.Models.Count);
            foreach (var entry in candidate.Models)
            {
                var modelKey = (candidate.Channel.Id, entry.ActualModel);
                if (!seenChannelModels.Add(modelKey))
                {
                    continue;
                }

                modelsToAppend.Add(entry);
            }

            if (modelsToAppend.Count == 0)
            {
                continue;
            }

            if (!candidateIndexByKey.TryGetValue(key, out var index))
            {
                candidates.Add(CreateCandidate(
                    candidate.Channel,
                    candidate.Priority,
                    [],
                    apiFormat: null,
                    modelApiFormats: null,
                    defaultMaxTokens: 0));
                index = candidates.Count - 1;
                candidateIndexByKey[key] = index;
            }

            var target = candidates[index];
            foreach (var entry in modelsToAppend)
            {
                target.Models.Add(new ChannelCandidateModel
                {
                    RequestModel = entry.RequestModel,
                    ActualModel = entry.ActualModel,
                    Source = entry.Source,
                });
            }
        }

        return candidates;
    }

    /// <summary>
    /// Port of populateAPIFormat (candidates_condition.go:73-126). Protocol
    /// overrides are applied one model at a time so a multi-model candidate does
    /// not merge unrelated overrides; explicit-endpoint request types drop
    /// entries and candidates whose forced formats cannot serve the request.
    /// </summary>
    private static List<ChannelCandidate> PopulateApiFormat(
        List<ChannelCandidate> candidates,
        LlmRequest request)
    {
        var filtered = new List<ChannelCandidate>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var descriptor = DescriptorFor(candidate);
            var baseEndpoints = ChannelEndpoints.Resolve(descriptor.Type, descriptor.Endpoints, descriptor.BaseUrl);

            if (candidate.Models.Count > 0)
            {
                var selectedModels = new List<ChannelCandidateModel>(candidate.Models.Count);
                var selectedFormats = new List<string>(candidate.Models.Count);

                foreach (var model in candidate.Models)
                {
                    var entry = new ChannelModelEntry(
                        model.RequestModel,
                        model.ActualModel,
                        model.Source);
                    var endpoints = ChannelEndpointSelection.ApplyForcedApiFormatsForRequest(
                        descriptor,
                        [entry],
                        request.Model,
                        request.RequestType,
                        baseEndpoints);
                    var format = ChannelEndpointSelection.SelectApiFormat(endpoints, request);

                    if (ChannelEndpointSelection.RequiresExplicitEndpoint(request.RequestType)
                        && format.Length == 0)
                    {
                        continue;
                    }

                    selectedModels.Add(model);
                    selectedFormats.Add(format);
                }

                if (selectedModels.Count == 0)
                {
                    continue;
                }

                if (ChannelEndpointSelection.RequiresExplicitEndpoint(request.RequestType))
                {
                    candidate.Models = selectedModels;
                }

                candidate.ModelApiFormats = selectedFormats;
                candidate.ApiFormat = selectedFormats[0];
            }
            else
            {
                candidate.ModelApiFormats = null;
                var endpoints = ChannelEndpointSelection.ApplyForcedApiFormatsForRequest(
                    descriptor,
                    [],
                    request.Model,
                    request.RequestType,
                    baseEndpoints);
                candidate.ApiFormat = ChannelEndpointSelection.SelectApiFormat(endpoints, request);
            }

            if (ChannelEndpointSelection.RequiresExplicitEndpoint(request.RequestType)
                && string.IsNullOrEmpty(candidate.ApiFormat))
            {
                continue;
            }

            filtered.Add(candidate);
        }

        return filtered;
    }

    /// <summary>
    /// Port of selectChannelCadidates (candidates.go:130-165): the original
    /// channel-only selection, one candidate per enabled channel supporting the
    /// request model, at priority 0, without a model routing policy or output
    /// limit.
    /// </summary>
    private async Task<List<ChannelCandidate>> SelectChannelCandidatesAsync(
        LlmRequest request,
        CancellationToken cancellationToken)
    {
        var channels = await _enabledChannels.ListEnabledAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = new List<ChannelCandidate>(channels.Length);
        foreach (var channel in channels)
        {
            var entries = ChannelModelEntries.Build(channel);
            if (!entries.TryGetValue(request.Model, out var entry))
            {
                continue;
            }

            var endpoints = ChannelEndpointSelection.ApplyForcedApiFormatsForRequest(
                channel,
                [entry],
                request.Model,
                request.RequestType,
                ChannelEndpoints.Resolve(channel.Type, channel.Endpoints, channel.BaseUrl));
            var apiFormat = ChannelEndpointSelection.SelectApiFormat(endpoints, request);
            if (ChannelEndpointSelection.RequiresExplicitEndpoint(request.RequestType)
                && apiFormat.Length == 0)
            {
                continue;
            }

            candidates.Add(CreateCandidate(
                channel,
                priority: 0,
                [entry],
                apiFormat,
                modelApiFormats: null,
                defaultMaxTokens: 0));
        }

        return candidates;
    }

    /// <summary>Descriptor-based candidate construction from a channel row.</summary>
    private static ChannelCandidate CreateCandidate(
        ChannelRecord channel,
        int priority,
        IReadOnlyList<ChannelModelEntry> models,
        string? apiFormat,
        List<string>? modelApiFormats,
        long defaultMaxTokens)
        => CreateCandidate(
            ChannelMapping.ToDescriptor(channel),
            priority,
            models,
            apiFormat,
            modelApiFormats,
            defaultMaxTokens);

    /// <summary>
    /// Builds the port's candidate from a channel descriptor, hydrating the
    /// settings the outbound path reads. The reference builds these once per
    /// channel in <c>buildChannel</c>; the descriptor is the same projection.
    /// </summary>
    private static ChannelCandidate CreateCandidate(
        ChannelDescriptor channel,
        int priority,
        IReadOnlyList<ChannelModelEntry> models,
        string? apiFormat,
        List<string>? modelApiFormats,
        long defaultMaxTokens)
    {
        var candidate = new ChannelCandidate
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            OrderingWeight = channel.OrderingWeight,
            UpdatedAt = channel.UpdatedAt,
            Type = channel.Type,
            BaseUrl = channel.BaseUrl,
            Credentials = channel.Credentials,
            DisabledApiKeys = channel.DisabledApiKeys,
            RetrySettings = ChannelRetrySettings.Parse(channel.Settings),
            OverrideSettings = ChannelOverrideSettings.Parse(channel.Settings),
            RateLimitSettings = ChannelRateLimitSettings.Parse(channel.Settings),
            PassThroughSettings = ChannelPassThroughSettings.Parse(channel.Settings),
            DefaultTestModel = ResolveDefaultTestModel(channel),
            ApiFormat = apiFormat,
            ModelApiFormats = modelApiFormats,
            Priority = priority,
            DefaultMaxTokens = defaultMaxTokens,
            Settings = channel.Settings,
            Endpoints = channel.Endpoints,
            Tags = channel.Tags,
            Policies = channel.Policies,
        };

        foreach (var entry in models)
        {
            candidate.Models.Add(new ChannelCandidateModel
            {
                RequestModel = entry.RequestModel,
                ActualModel = entry.ActualModel,
                Source = entry.Source,
            });
        }

        return candidate;
    }

    /// <summary>
    /// Descriptor view of an intermediate candidate, used to resolve its
    /// endpoints and per-model protocol overrides.
    /// </summary>
    private static ChannelDescriptor DescriptorFor(ChannelCandidate candidate) => new()
    {
        Id = candidate.ChannelId,
        Name = candidate.ChannelName,
        OrderingWeight = candidate.OrderingWeight,
        UpdatedAt = candidate.UpdatedAt,
        Type = candidate.Type,
        BaseUrl = candidate.BaseUrl,
        Credentials = candidate.Credentials,
        DisabledApiKeys = candidate.DisabledApiKeys,
        Settings = candidate.Settings,
        Endpoints = candidate.Endpoints,
        Tags = candidate.Tags,
        Policies = candidate.Policies,
    };

    /// <summary>
    /// Port of the default-test-model resolution at the start of
    /// buildCodexOutbound (channel_llm.go:304-317): trim, then map through the
    /// channel's model entries; an unsupported model keeps its trimmed value.
    /// </summary>
    internal static string ResolveDefaultTestModel(ChannelDescriptor channel)
    {
        var model = channel.DefaultTestModel.Trim();
        if (model.Length == 0)
        {
            return model;
        }

        return ChannelModelEntries.ChooseModel(channel, model) ?? model;
    }

    /// <summary>
    /// Port of the Model schema Query policy (internal/ent/schema/model.go:79-90)
    /// for the chat context: an API-key principal passes with
    /// <c>read_channels</c>; without a key the user rule denies because there is
    /// no user. Deny texts carry the ent sentinel suffix
    /// (<c>privacy.Denyf</c> → <c>fmt.Errorf("%s: %w", msg, Deny)</c>).
    /// </summary>
    private static void EvaluateQueryPrivacy(RequestIdentity identity)
    {
        if (identity.ApiKey is { } apiKey)
        {
            if (PermissionValidator.ParseScopes(apiKey.Scopes)
                .Contains(AuthorizationScope.ReadChannels, StringComparer.Ordinal))
            {
                return;
            }

            throw new PrivacyDenyException(
                "API key does not have required scope: "
                + AuthorizationScope.ReadChannels
                + ": "
                + PrivacyDenyException.DenyMessage);
        }

        throw new PrivacyDenyException(
            "no user in context: " + PrivacyDenyException.DenyMessage);
    }

    /// <summary>ent model.StatusEnabled.</summary>
    private const string ModelStatusEnabled = "enabled";

    /// <summary>
    /// Decode the JSON columns in ent's generated field order: model_card is
    /// scanned before settings, and its decode error wins when both are broken.
    /// </summary>
    private static (ModelCard? Card, ModelEntitySettings? Settings) DecodeModelRow(
        ModelRecord record)
    {
        ModelCard? card;
        try
        {
            card = ModelCardCodec.Deserialize(record.ModelCard);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to query AxonHub Model: unmarshal field model_card: " + ex.Message,
                ex);
        }

        ModelEntitySettings? settings;
        try
        {
            settings = ModelSettingsCodec.DeserializeEntitySettings(record.Settings);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to query AxonHub Model: unmarshal field settings: " + ex.Message,
                ex);
        }

        return (card, settings);
    }

    /// <summary>Port of modelCardOutputLimit (candidates.go:609-615).</summary>
    private static long ModelCardOutputLimit(ModelCard? card)
    {
        if (card is null || card.Limit.Output <= 0)
        {
            return 0;
        }

        return card.Limit.Output;
    }

    /// <summary>The reference's ent NotFoundError; the caller maps it to IsNotFound.</summary>
    private sealed class ModelNotFoundException : Exception;
}
