using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Api.Prompts;

/// <summary>Port of objects.PromptActionType (internal/objects/prompt.go).</summary>
public static class PromptActionTypes
{
    public const string Prepend = "prepend";
    public const string Append = "append";
}

/// <summary>Port of objects.PromptActivationConditionType.</summary>
public static class PromptActivationConditionTypes
{
    public const string ModelId = "model_id";
    public const string ModelPattern = "model_pattern";
    public const string ApiKey = "api_key";
}

/// <summary>Port of ent Prompt.Status.</summary>
public static class PromptStatuses
{
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
}

/// <summary>Port of objects.PromptAction: the action to perform when activated.</summary>
public sealed class PromptAction
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>
/// Port of objects.PromptActivationCondition. <c>model_id</c> and
/// <c>model_pattern</c> are Go <c>*string</c>, <c>api_key_id</c> is Go
/// <c>*int</c>: a missing key and an explicit null both arrive as null.
/// </summary>
public sealed class PromptActivationCondition
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }

    [JsonPropertyName("model_pattern")]
    public string? ModelPattern { get; set; }

    [JsonPropertyName("api_key_id")]
    public long? ApiKeyId { get; set; }
}

/// <summary>
/// Port of objects.PromptActivationConditionComposite: at least one condition
/// in the composite must match.
/// </summary>
public sealed class PromptActivationConditionComposite
{
    [JsonPropertyName("conditions")]
    public List<PromptActivationCondition>? Conditions { get; set; }
}

/// <summary>
/// Port of objects.PromptSettings. Go decodes struct fields case-insensitively
/// and ignores unknown keys, which
/// <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/> reproduces.
/// </summary>
/// <remarks>
/// A JSON <c>null</c> element inside <c>conditions</c> decodes to a zero
/// <see cref="PromptActivationConditionComposite"/> in Go (which matches every
/// request); System.Text.Json leaves a null list element, so the matcher
/// treats null as the zero value.
/// </remarks>
public sealed class PromptSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [JsonPropertyName("action")]
    public PromptAction? Action { get; set; }

    [JsonPropertyName("conditions")]
    public List<PromptActivationConditionComposite>? Conditions { get; set; }

    /// <summary>
    /// Decodes a prompt's jsonb <c>settings</c> column the way Ent's JSON field
    /// does. Go's <c>json.Unmarshal(null, &amp;settings)</c> is a no-op, so a
    /// JSON null yields the zero value.
    /// </summary>
    public static PromptSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new PromptSettings();
        }

        return JsonSerializer.Deserialize<PromptSettings>(json, Options) ?? new PromptSettings();
    }
}

/// <summary>The fields of ent.Prompt the injection middleware reads.</summary>
public sealed class Prompt
{
    public long Id { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public string Status { get; init; } = PromptStatuses.Disabled;

    /// <summary>Insertion order; smaller values are inserted first.</summary>
    public long Order { get; init; }

    public long ProjectId { get; init; }

    public PromptSettings Settings { get; init; } = new();
}
