using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm;

namespace AxonHub.Api.PromptProtection;

/// <summary>Port of objects.PromptProtectionAction (internal/objects/prompt_protection.go).</summary>
public static class PromptProtectionActions
{
    public const string Mask = "mask";
    public const string Reject = "reject";
}

/// <summary>Port of objects.PromptProtectionScope.</summary>
public static class PromptProtectionScopes
{
    public const string System = "system";
    public const string Developer = "developer";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

/// <summary>
/// Port of objects.PromptProtectionSettings. Go decodes struct fields
/// case-insensitively and ignores unknown keys, which
/// <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/> reproduces.
/// </summary>
public sealed class PromptProtectionSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("replacement")]
    public string Replacement { get; set; } = string.Empty;

    [JsonPropertyName("scopes")]
    public List<string>? Scopes { get; set; }

    /// <summary>
    /// Decodes a rule's jsonb <c>settings</c> column. Go's decoder leaves a
    /// string field at its zero value when the JSON value is null, so a null
    /// replacement decodes to "" rather than null.
    /// </summary>
    public static PromptProtectionSettings? FromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<PromptProtectionSettings>(json, Options);
        if (settings is not null)
        {
            settings.Replacement ??= string.Empty;
        }

        return settings;
    }

    /// <summary>
    /// Encodes the settings the way Go's <c>json.Marshal</c> does for the ent
    /// JSON field: fields in struct order, <c>replacement</c> omitted when
    /// empty and <c>scopes</c> omitted when nil or empty (<c>omitempty</c>).
    /// </summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("action", Action);
            if (Replacement.Length > 0)
            {
                writer.WriteString("replacement", Replacement);
            }

            if (Scopes is { Count: > 0 })
            {
                writer.WritePropertyName("scopes");
                writer.WriteStartArray();
                foreach (var scope in Scopes)
                {
                    writer.WriteStringValue(scope);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>
/// The protection-relevant slice of ent.PromptProtectionRule
/// (internal/ent/schema/prompt_protection_rule.go): the rule service and the
/// raw body patcher read only these fields.
/// </summary>
public sealed class PromptProtectionRule
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Pattern { get; init; } = string.Empty;

    /// <summary>Nil when the stored JSON is <c>null</c>; such rules are skipped.</summary>
    public PromptProtectionSettings? Settings { get; init; }
}

/// <summary>
/// Port of biz.PromptProtectionPreviewResult
/// (internal/server/biz/prompt_protection_preview.go).
/// </summary>
public sealed class PromptProtectionPreviewResult
{
    public string Result { get; init; } = string.Empty;

    public bool HasMatch { get; init; }
}

/// <summary>Port of biz.PromptProtectionResult (prompt_protection_request.go).</summary>
public sealed class PromptProtectionResult
{
    public LlmRequest? Request { get; set; }

    public IReadOnlyList<PromptProtectionRule> MatchedRules { get; set; } = [];

    public bool Rejected { get; set; }
}

/// <summary>
/// Port of biz.ErrPromptProtectionRejected: the sentinel the middleware maps to
/// <c>transformer.ErrInvalidRequest</c>.
/// </summary>
public sealed class PromptProtectionRejectedException : Exception
{
    public PromptProtectionRejectedException()
        : base("prompt protection rejected request")
    {
    }
}

/// <summary>Port of orchestrator.PromptProtecter.</summary>
public interface IPromptProtecter
{
    LlmRequest? Protect(LlmRequest request);
}

/// <summary>
/// Port of orchestrator.PromptProtectionResultProvider: exposes matched rules so
/// the pass-through body can be patched with exactly the rules that masked the
/// unified request.
/// </summary>
public interface IPromptProtectionResultProvider
{
    PromptProtectionResult ProtectWithResult(LlmRequest request);
}
