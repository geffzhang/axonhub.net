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

    /// <summary>Decodes a rule's jsonb <c>settings</c> column.</summary>
    public static PromptProtectionSettings? FromJson(string json)
        => JsonSerializer.Deserialize<PromptProtectionSettings>(json, Options);
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
