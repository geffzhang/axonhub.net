using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Models;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of biz.ModelChannelConnection (model_association_matcher.go:13-18).</summary>
public sealed record ModelChannelConnection(
    ChannelRecord Channel,
    IReadOnlyList<ChannelModelEntry> Models,
    int Priority);

/// <summary>Port of biz.AssociationMatch (model_association_matcher.go:20-24).</summary>
public sealed record ModelAssociationMatch(
    ModelAssociation Association,
    IReadOnlyList<ModelChannelConnection> Connections);

/// <summary>
/// Port of internal/server/biz/model_association_matcher.go. Matches model
/// association rules against channels and their derived model entries, with the
/// reference's shared/inner duplicate-key tracking.
/// </summary>
/// <remarks>
/// The reference iterates each channel's model entries through a Go map, so the
/// order of models inside a connection is unspecified there; the port sorts by
/// request model id to make it deterministic (C177). Channel order follows the
/// caller's channel slice, which the port queries by id.
/// </remarks>
public static class ModelAssociationMatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Port of MatchConnections.</summary>
    public static IReadOnlyList<ModelChannelConnection> MatchConnections(
        IReadOnlyList<ModelAssociation?>? associations,
        IReadOnlyList<ChannelRecord> channels)
    {
        var result = new List<ModelChannelConnection>();
        var tracker = new DuplicateKeyTracker();

        foreach (var association in associations ?? [])
        {
            // Go dereferences the pointer without a nil check (C130).
            if (association is null || association.Disabled)
            {
                continue;
            }

            result.AddRange(MatchSingleAssociation(association, channels, tracker));
        }

        return result;
    }

    /// <summary>Port of MatchAssociations.</summary>
    public static IReadOnlyList<ModelAssociationMatch> MatchAssociations(
        IReadOnlyList<ModelAssociation?>? associations,
        IReadOnlyList<ChannelRecord> channels)
    {
        var result = new List<ModelAssociationMatch>();

        foreach (var association in associations ?? [])
        {
            if (association is null || association.Disabled)
            {
                continue;
            }

            var tracker = new DuplicateKeyTracker();
            var connections = MatchSingleAssociation(association, channels, tracker);
            if (connections.Count == 0)
            {
                continue;
            }

            result.Add(new ModelAssociationMatch(association, connections));
        }

        return result;
    }

    /// <summary>Port of shouldExcludeChannel.</summary>
    public static bool ShouldExcludeChannel(
        ChannelRecord channel,
        IReadOnlyList<ExcludeAssociation?>? excludes)
    {
        if (excludes is null || excludes.Count == 0)
        {
            return false;
        }

        var tags = ParseTags(channel.Tags);

        foreach (var exclude in excludes)
        {
            // Go dereferences nil entries and panics (C130).
            if (exclude is null)
            {
                continue;
            }

            if (exclude.ChannelNamePattern.Length != 0
                && XRegexp.MatchString(exclude.ChannelNamePattern, channel.Name))
            {
                return true;
            }

            if (exclude.ChannelIds is { Count: > 0 }
                && exclude.ChannelIds.Any(id => id == channel.Id))
            {
                return true;
            }

            if (exclude.ChannelTags is { Count: > 0 }
                && exclude.ChannelTags.Any(tags.Contains))
            {
                return true;
            }
        }

        return false;
    }

    private static List<ModelChannelConnection> MatchSingleAssociation(
        ModelAssociation association,
        IReadOnlyList<ChannelRecord> channels,
        DuplicateKeyTracker tracker)
        => association.Type switch
        {
            ModelAssociation.TypeChannelModel => MatchChannelModel(association, channels, tracker),
            ModelAssociation.TypeChannelRegex => MatchChannelRegex(association, channels, tracker),
            ModelAssociation.TypeRegex => MatchRegex(association, channels, tracker),
            ModelAssociation.TypeModel => MatchModel(association, channels, tracker),
            ModelAssociation.TypeChannelTagsModel
                => MatchChannelTagsModel(association, channels, tracker),
            ModelAssociation.TypeChannelTagsRegex
                => MatchChannelTagsRegex(association, channels, tracker),
            _ => [],
        };

    private static List<ModelChannelConnection> MatchChannelModel(
        ModelAssociation association,
        IReadOnlyList<ChannelRecord> channels,
        DuplicateKeyTracker tracker)
    {
        if (association.ChannelModel is null)
        {
            return [];
        }

        var channel = channels.FirstOrDefault(
            candidate => candidate.Id == association.ChannelModel.ChannelId);
        if (channel is null)
        {
            return [];
        }

        var entries = ChannelModelEntries.Build(ToDescriptor(channel));
        if (!entries.TryGetValue(association.ChannelModel.ModelId, out var entry))
        {
            return [];
        }

        if (!tracker.Add(channel.Id, association.ChannelModel.ModelId))
        {
            return [];
        }

        return
        [
            new ModelChannelConnection(channel, [entry], association.Priority),
        ];
    }

    private static List<ModelChannelConnection> MatchChannelRegex(
        ModelAssociation association,
        IReadOnlyList<ChannelRecord> channels,
        DuplicateKeyTracker tracker)
    {
        if (association.ChannelRegex is null)
        {
            return [];
        }

        var channel = channels.FirstOrDefault(
            candidate => candidate.Id == association.ChannelRegex.ChannelId);
        if (channel is null)
        {
            return [];
        }

        var entries = ChannelModelEntries.Build(ToDescriptor(channel));
        var models = new List<ChannelModelEntry>();
        foreach (var (modelId, entry) in entries.OrderBy(
                     static pair => pair.Key,
                     StringComparer.Ordinal))
        {
            if (XRegexp.MatchString(association.ChannelRegex.Pattern, modelId)
                && tracker.Add(channel.Id, modelId))
            {
                models.Add(entry);
            }
        }

        return models.Count == 0
            ? []
            : [new ModelChannelConnection(channel, models, association.Priority)];
    }

    private static List<ModelChannelConnection> MatchRegex(
        ModelAssociation association,
        IReadOnlyList<ChannelRecord> channels,
        DuplicateKeyTracker tracker)
    {
        if (association.Regex is null)
        {
            return [];
        }

        var connections = new List<ModelChannelConnection>();
        foreach (var channel in channels)
        {
            if (ShouldExcludeChannel(channel, association.Regex.Exclude))
            {
                continue;
            }

            var entries = ChannelModelEntries.Build(ToDescriptor(channel));
            var models = new List<ChannelModelEntry>();
            foreach (var (modelId, entry) in entries.OrderBy(
                         static pair => pair.Key,
                         StringComparer.Ordinal))
            {
                if (XRegexp.MatchString(association.Regex.Pattern, modelId)
                    && tracker.Add(channel.Id, modelId))
                {
                    models.Add(entry);
                }
            }

            if (models.Count == 0)
            {
                continue;
            }

            connections.Add(new ModelChannelConnection(channel, models, association.Priority));
        }

        return connections;
    }

    private static List<ModelChannelConnection> MatchModel(
        ModelAssociation association,
        IReadOnlyList<ChannelRecord> channels,
        DuplicateKeyTracker tracker)
    {
        if (association.ModelId is null)
        {
            return [];
        }

        var modelId = association.ModelId.ModelId;
        var connections = new List<ModelChannelConnection>();
        foreach (var channel in channels)
        {
            if (ShouldExcludeChannel(channel, association.ModelId.Exclude))
            {
                continue;
            }

            var entries = ChannelModelEntries.Build(ToDescriptor(channel));
            if (!entries.TryGetValue(modelId, out var entry))
            {
                continue;
            }

            if (!tracker.Add(channel.Id, modelId))
            {
                continue;
            }

            connections.Add(new ModelChannelConnection(channel, [entry], association.Priority));
        }

        return connections;
    }

    private static List<ModelChannelConnection> MatchChannelTagsModel(
        ModelAssociation association,
        IReadOnlyList<ChannelRecord> channels,
        DuplicateKeyTracker tracker)
    {
        // len(nil) == 0 in Go, so both a missing block and an empty tag list
        // skip the rule.
        var channelTagsModel = association.ChannelTagsModel;
        if (channelTagsModel?.ChannelTags is not { Count: > 0 } channelTags)
        {
            return [];
        }

        var modelId = channelTagsModel.ModelId;
        var connections = new List<ModelChannelConnection>();
        foreach (var channel in channels)
        {
            if (!HasAnyTag(channel, channelTags))
            {
                continue;
            }

            var entries = ChannelModelEntries.Build(ToDescriptor(channel));
            if (!entries.TryGetValue(modelId, out var entry))
            {
                continue;
            }

            if (!tracker.Add(channel.Id, modelId))
            {
                continue;
            }

            connections.Add(new ModelChannelConnection(channel, [entry], association.Priority));
        }

        return connections;
    }

    private static List<ModelChannelConnection> MatchChannelTagsRegex(
        ModelAssociation association,
        IReadOnlyList<ChannelRecord> channels,
        DuplicateKeyTracker tracker)
    {
        var channelTagsRegex = association.ChannelTagsRegex;
        if (channelTagsRegex?.ChannelTags is not { Count: > 0 } channelTags)
        {
            return [];
        }

        var connections = new List<ModelChannelConnection>();
        foreach (var channel in channels)
        {
            if (!HasAnyTag(channel, channelTags))
            {
                continue;
            }

            var entries = ChannelModelEntries.Build(ToDescriptor(channel));
            var models = new List<ChannelModelEntry>();
            foreach (var (modelId, entry) in entries.OrderBy(
                         static pair => pair.Key,
                         StringComparer.Ordinal))
            {
                if (XRegexp.MatchString(channelTagsRegex.Pattern, modelId)
                    && tracker.Add(channel.Id, modelId))
                {
                    models.Add(entry);
                }
            }

            if (models.Count == 0)
            {
                continue;
            }

            connections.Add(new ModelChannelConnection(channel, models, association.Priority));
        }

        return connections;
    }

    private static bool HasAnyTag(ChannelRecord channel, IReadOnlyList<string> requiredTags)
    {
        var tags = ParseTags(channel.Tags);
        foreach (var tag in requiredTags)
        {
            if (tags.Contains(tag))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> ParseTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            var values = JsonSerializer.Deserialize<string?[]>(raw, JsonOptions);
            return values is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(
                    values.Select(static value => value ?? string.Empty),
                    StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static ChannelDescriptor ToDescriptor(ChannelRecord record)
        => ChannelMapping.ToDescriptor(record);

    private sealed class DuplicateKeyTracker
    {
        private readonly HashSet<(long ChannelId, string ModelId)> _seen = [];

        /// <summary>Port of DuplicateKeyTracker.Add.</summary>
        public bool Add(long channelId, string modelId) => _seen.Add((channelId, modelId));
    }
}
