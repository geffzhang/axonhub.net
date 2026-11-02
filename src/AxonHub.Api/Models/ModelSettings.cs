namespace AxonHub.Api.Models;

/// <summary>
/// Port of biz.SystemModelSettings (internal/server/biz/system.go): the
/// <c>system_model_settings</c> row. The models API reads
/// <see cref="QueryAllChannelModels"/>, <see cref="ModelBlacklistRegex"/> and
/// <see cref="DefaultModelApiIncludeAll"/>; the remaining fields are decoded
/// with their Go types so a mistyped member still fails the whole document and
/// falls back to <see cref="Default"/>, exactly like the reference's
/// <c>json.Unmarshal</c>.
/// </summary>
public sealed record ModelSettings
{
    /// <summary>biz.defaultModelSettings (internal/server/biz/system_default.go:91).</summary>
    public static ModelSettings Default { get; } = new()
    {
        FallbackToChannelsOnModelNotFound = true,
        QueryAllChannelModels = true,
        DefaultModelApiIncludeAll = false,
        AutoReasoningEffort = false,
        ModelBlacklistRegex = string.Empty,
        HideUnroutableModelsInList = false,
        DeveloperSettings = [],
    };

    /// <summary>Fall back to channel selection when a model has no association.</summary>
    public bool FallbackToChannelsOnModelNotFound { get; init; }

    /// <summary>
    /// When true the models API returns every model exposed by the enabled
    /// channels; when false it returns only configured Model entities.
    /// </summary>
    public bool QueryAllChannelModels { get; init; }

    /// <summary><c>/v1/models</c> behaves like <c>?include=all</c> by default.</summary>
    public bool DefaultModelApiIncludeAll { get; init; }

    /// <summary>Strip reasoning-effort suffixes from model names.</summary>
    public bool AutoReasoningEffort { get; init; }

    /// <summary>
    /// Regex filtering channel-derived model IDs; empty disables the filter.
    /// Only effective while <see cref="QueryAllChannelModels"/> is true.
    /// </summary>
    public string ModelBlacklistRegex { get; init; } = string.Empty;

    /// <summary>Hide configured models without a routable channel.</summary>
    public bool HideUnroutableModelsInList { get; init; }

    /// <summary>
    /// Reusable association rules keyed by model developer. Null elements are
    /// preserved because Go keeps <c>[]*DeveloperModelSettings</c> entries and
    /// only skips them while normalizing.
    /// </summary>
    public IReadOnlyList<DeveloperModelSettings?> DeveloperSettings { get; init; } = [];
}

/// <summary>Port of biz.DeveloperModelSettings.</summary>
/// <remarks>
/// Associations are <c>[]*objects.ModelAssociation</c> in Go, so null elements
/// survive decoding (the reference serializes them back as null) and are
/// skipped by normalization and validation.
/// </remarks>
public sealed record DeveloperModelSettings(
    string Developer,
    IReadOnlyList<ModelAssociation?> Associations);
