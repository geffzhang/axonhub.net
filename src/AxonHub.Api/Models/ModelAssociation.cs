using System.Text.Json.Nodes;

namespace AxonHub.Api.Models;

/// <summary>
/// Port of <c>objects.ModelAssociation</c> (internal/objects/model.go:63-80).
/// </summary>
/// <remarks>
/// Developer settings keep the full rule shape. The reference's concrete model
/// type is <c>channel_model</c> / <c>channel_tags_model</c>; the other
/// association kinds are validated and typed here so a stored document decodes
/// exactly like Go's <c>json.Unmarshal</c>.
/// </remarks>
public sealed record ModelAssociation
{
    public const string TypeChannelModel = "channel_model";
    public const string TypeChannelRegex = "channel_regex";
    public const string TypeRegex = "regex";
    public const string TypeModel = "model";
    public const string TypeChannelTagsModel = "channel_tags_model";
    public const string TypeChannelTagsRegex = "channel_tags_regex";

    public string Type { get; init; } = string.Empty;

    /// <summary>Lower value = higher priority, default 0.</summary>
    public int Priority { get; init; }

    public bool Disabled { get; init; }

    public ModelAssociationWhen? When { get; init; }

    public ChannelModelAssociation? ChannelModel { get; init; }

    public ChannelRegexAssociation? ChannelRegex { get; init; }

    public RegexAssociation? Regex { get; init; }

    public ModelIdAssociation? ModelId { get; init; }

    public ChannelTagsModelAssociation? ChannelTagsModel { get; init; }

    public ChannelTagsRegexAssociation? ChannelTagsRegex { get; init; }
}

/// <summary>Port of <c>objects.ModelAssociationWhen</c> (internal/objects/model.go:82-85).</summary>
public sealed record ModelAssociationWhen(bool Enabled, FilterCondition? Condition);

/// <summary>
/// Port of <c>objects.Condition</c> (internal/objects/condition.go:25-32).
/// <see cref="Value"/> stays as JSON because the reference carries it as
/// <c>any</c>; only the validation paths interpret it.
/// </summary>
public sealed record FilterCondition
{
    public const string TypeCondition = "condition";
    public const string TypeGroup = "group";

    public string Type { get; init; } = string.Empty;

    public string Logic { get; init; } = string.Empty;

    public IReadOnlyList<FilterCondition>? Conditions { get; init; }

    public string Field { get; init; } = string.Empty;

    public string Operator { get; init; } = string.Empty;

    public JsonNode? Value { get; init; }
}

/// <summary>Port of <c>objects.ChannelModelAssociation</c>.</summary>
public sealed record ChannelModelAssociation(int ChannelId, string ModelId);

/// <summary>Port of <c>objects.ChannelRegexAssociation</c>.</summary>
public sealed record ChannelRegexAssociation(int ChannelId, string Pattern);

/// <summary>Port of <c>objects.RegexAssociation</c>.</summary>
public sealed record RegexAssociation(string Pattern, IReadOnlyList<ExcludeAssociation?>? Exclude);

/// <summary>Port of <c>objects.ModelIDAssociation</c>.</summary>
public sealed record ModelIdAssociation(string ModelId, IReadOnlyList<ExcludeAssociation?>? Exclude);

/// <summary>Port of <c>objects.ExcludeAssociation</c>.</summary>
public sealed record ExcludeAssociation(
    string ChannelNamePattern,
    IReadOnlyList<int>? ChannelIds,
    IReadOnlyList<string>? ChannelTags);

/// <summary>Port of <c>objects.ChannelTagsModelAssociation</c>.</summary>
public sealed record ChannelTagsModelAssociation(
    IReadOnlyList<string>? ChannelTags,
    string ModelId);

/// <summary>Port of <c>objects.ChannelTagsRegexAssociation</c>.</summary>
public sealed record ChannelTagsRegexAssociation(
    IReadOnlyList<string>? ChannelTags,
    string Pattern);

/// <summary>Condition field names (internal/objects/model.go:50-60).</summary>
public static class ModelAssociationConditionField
{
    public const string PromptTokens = "prompt_tokens";
    public const string Stream = "stream";
    public const string RequestFormat = "request_format";
    public const string ReasoningEffort = "reasoning_effort";
    public const string DailyTime = "daily_time";
    public const string HasImage = "has_image";
    public const string HasVideo = "has_video";
    public const string HasDocument = "has_document";
    public const string HasAudio = "has_audio";
    public const string RequestHeader = "request_header";
    public const string RequestHeaderPrefix = "request_header.";
}
