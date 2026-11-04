using AxonHub.Api.Models;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the model-association half of
/// internal/server/biz/model_settings_inheritance.go: a developer-level rule is
/// inherited by every model of that developer, with the concrete model id
/// filled in at inheritance time, unless the model disables inheritance.
/// </summary>
public static class ModelSettingsInheritance
{
    /// <summary>Port of EffectiveModelAssociations.</summary>
    public static IReadOnlyList<ModelAssociation> EffectiveModelAssociations(
        ModelSettings? systemSettings,
        string developer,
        string modelId,
        ModelEntitySettings? modelSettings)
    {
        var modelAssociations = modelSettings?.Associations;

        if (modelSettings is { DisableDeveloperSettingsInheritance: true })
        {
            return Merge(null, modelAssociations);
        }

        return Merge(
            InheritDeveloperAssociationsForModel(
                DeveloperAssociationsForDeveloper(systemSettings, developer),
                modelId),
            modelAssociations);
    }

    /// <summary>Port of developerAssociationsForDeveloper: first exact match.</summary>
    public static IReadOnlyList<ModelAssociation?>? DeveloperAssociationsForDeveloper(
        ModelSettings? settings,
        string developer)
    {
        if (settings is null || developer.Length == 0)
        {
            return null;
        }

        foreach (var developerSettings in settings.DeveloperSettings)
        {
            if (developerSettings is null
                || !string.Equals(
                    developerSettings.Developer,
                    developer,
                    StringComparison.Ordinal))
            {
                continue;
            }

            return developerSettings.Associations;
        }

        return null;
    }

    /// <summary>Port of inheritDeveloperAssociationsForModel.</summary>
    public static IReadOnlyList<ModelAssociation?>? InheritDeveloperAssociationsForModel(
        IReadOnlyList<ModelAssociation?>? developerAssociations,
        string modelId)
    {
        if (modelId.Length == 0)
        {
            return null;
        }

        var associations = new List<ModelAssociation?>();
        foreach (var association in developerAssociations ?? [])
        {
            var inherited = InheritDeveloperAssociationForModel(association, modelId);
            if (inherited is not null)
            {
                associations.Add(inherited);
            }
        }

        return associations;
    }

    /// <summary>Port of mergeInheritedModelAssociations.</summary>
    public static IReadOnlyList<ModelAssociation> Merge(
        IReadOnlyList<ModelAssociation?>? developerAssociations,
        IReadOnlyList<ModelAssociation?>? modelAssociations)
    {
        // Lower priority values run first. When priorities are equal,
        // model-level rules are placed before inherited developer rules so
        // local configuration can refine the shared defaults.
        var items = new List<(ModelAssociation Association, int SourceRank, int Order)>();

        var order = 0;
        foreach (var association in modelAssociations ?? [])
        {
            if (association is null)
            {
                continue;
            }

            items.Add((association, 0, order++));
        }

        order = 0;
        foreach (var association in developerAssociations ?? [])
        {
            if (association is null)
            {
                continue;
            }

            items.Add((association, 1, order++));
        }

        return
        [
            .. items
                .OrderBy(static item => item.Association.Priority)
                .ThenBy(static item => item.SourceRank)
                .ThenBy(static item => item.Order)
                .Select(static item => item.Association),
        ];
    }

    private static ModelAssociation? InheritDeveloperAssociationForModel(
        ModelAssociation? association,
        string modelId)
    {
        // Go returns nil for a nil entry; the reference slice keeps nils from
        // JSON but inheritance drops them (C130 covers the matcher's panic).
        if (association is null)
        {
            return null;
        }

        // Developer rules only choose channels or channel tags. The concrete
        // model id is filled in at inheritance time so sibling models do not
        // share one fixed actual model.
        var inherited = Clone(association);
        switch (inherited.Type)
        {
            case ModelAssociation.TypeChannelModel:
                if (inherited.ChannelModel is null)
                {
                    return null;
                }

                inherited = inherited with
                {
                    ChannelModel = inherited.ChannelModel with { ModelId = modelId },
                };
                break;
            case ModelAssociation.TypeChannelTagsModel:
                if (inherited.ChannelTagsModel is null)
                {
                    return null;
                }

                inherited = inherited with
                {
                    ChannelTagsModel = inherited.ChannelTagsModel with { ModelId = modelId },
                };
                break;
            default:
                return null;
        }

        return inherited;
    }

    /// <summary>Port of cloneModelAssociation.</summary>
    private static ModelAssociation Clone(ModelAssociation association) => association with
    {
        When = association.When is null
            ? null
            : association.When with
            {
                Condition = association.When.Condition is null
                    ? null
                    : CloneCondition(association.When.Condition),
            },
        ChannelModel = association.ChannelModel,
        ChannelRegex = association.ChannelRegex,
        Regex = association.Regex is null
            ? null
            : association.Regex with
            {
                Exclude = CloneExcludes(association.Regex.Exclude),
            },
        ModelId = association.ModelId is null
            ? null
            : association.ModelId with
            {
                Exclude = CloneExcludes(association.ModelId.Exclude),
            },
        // append([]string(nil), tags...) yields nil for both a nil and an empty
        // source slice, so the clone only keeps a copy when there is a tag.
        ChannelTagsModel = association.ChannelTagsModel is null
            ? null
            : association.ChannelTagsModel with
            {
                ChannelTags = association.ChannelTagsModel.ChannelTags is { Count: > 0 } tags
                    ? tags.ToList()
                    : null,
            },
        ChannelTagsRegex = association.ChannelTagsRegex is null
            ? null
            : association.ChannelTagsRegex with
            {
                ChannelTags = association.ChannelTagsRegex.ChannelTags is { Count: > 0 } regexTags
                    ? regexTags.ToList()
                    : null,
            },
    };

    /// <summary>Port of cloneCondition (value semantics, copies the children).</summary>
    private static FilterCondition CloneCondition(FilterCondition condition)
        => condition.Conditions is { Count: > 0 }
            ? condition with
            {
                Conditions = condition.Conditions.Select(CloneCondition).ToList(),
            }
            : condition;

    /// <summary>Port of cloneExcludeAssociations: len==0 returns nil, nils drop.</summary>
    private static List<ExcludeAssociation?>? CloneExcludes(
        IReadOnlyList<ExcludeAssociation?>? excludes)
    {
        if (excludes is null || excludes.Count == 0)
        {
            return null;
        }

        var clones = new List<ExcludeAssociation?>(excludes.Count);
        foreach (var exclude in excludes)
        {
            if (exclude is null)
            {
                continue;
            }

            clones.Add(exclude with
            {
                ChannelIds = exclude.ChannelIds?.ToList(),
                ChannelTags = exclude.ChannelTags?.ToList(),
            });
        }

        return clones;
    }
}
