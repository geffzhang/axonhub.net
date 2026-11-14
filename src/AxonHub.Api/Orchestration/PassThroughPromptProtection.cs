using System.Text;
using AxonHub.Api.PromptProtection;
using AxonHub.Llm;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// A scalar string or structured JSON value in an API-native request body, and
/// the unified message role it belongs to.
/// </summary>
internal readonly record struct RawPromptTextField(
    string Path,
    string Role,
    bool StructuredJson = false);

/// <summary>
/// Port of orchestrator/pass_through_prompt_protection.go: replaces only the
/// text fields the unified request represents, so a matched rule can never be
/// bypassed by replaying the original provider body.
/// </summary>
internal static class PassThroughPromptProtection
{
    /// <summary>
    /// Port of patchPassThroughPromptProtection. On failure the caller keeps the
    /// transformed, already-protected outbound request body.
    /// </summary>
    public static bool TryPatch(
        byte[] body,
        string? apiFormat,
        IReadOnlyList<PromptProtectionRule> rules,
        out byte[] patched,
        out string? error)
    {
        patched = body;
        error = null;

        if (!OverrideJson.IsValid(body))
        {
            error = "invalid pass-through JSON body";
            return false;
        }

        if (!TryRawPromptTextFields(body, apiFormat, out var fields, out error))
        {
            return false;
        }

        var current = (byte[])body.Clone();
        var matchCount = 0;

        foreach (var field in fields)
        {
            if (!TryPatchField(current, field, rules, out var next, out var changed, out error))
            {
                return false;
            }

            if (!changed)
            {
                continue;
            }

            current = next;
            matchCount++;
        }

        if (matchCount == 0)
        {
            error = "matched prompt protection rules did not match a raw request text field";
            return false;
        }

        patched = current;
        return true;
    }

    /// <summary>
    /// Applies mask rules to one raw string or the JSON object used as Gemini's
    /// structured function-response content.
    /// </summary>
    private static bool TryPatchField(
        byte[] body,
        RawPromptTextField field,
        IReadOnlyList<PromptProtectionRule> rules,
        out byte[] next,
        out bool changed,
        out string? error)
    {
        next = body;
        changed = false;
        error = null;

        var value = OverrideJson.Get(body, field.Path);
        if (!value.Exists)
        {
            error = $"prompt text field {QuotePath(field.Path)} is missing";
            return false;
        }

        if (field.StructuredJson)
        {
            return TryPatchJsonField(body, field, value.Node!, rules, out next, out changed, out error);
        }

        if (!value.IsString)
        {
            error = $"prompt text field {QuotePath(field.Path)} is not a string";
            return false;
        }

        var replacement = ReplacePassThroughPromptText(
            value.StringValue,
            field.Role,
            rules,
            out changed);
        if (!changed)
        {
            return true;
        }

        if (!OverrideJson.TrySet(
                body,
                field.Path,
                new GoStringValue(replacement),
                out next,
                out var setError))
        {
            error = $"replace protected prompt at {QuotePath(field.Path)}: {setError}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies tool-scope masks to string leaves in Gemini's structured
    /// function-response object without changing its keys or numbers.
    /// </summary>
    private static bool TryPatchJsonField(
        byte[] body,
        RawPromptTextField field,
        OverrideJsonNode value,
        IReadOnlyList<PromptProtectionRule> rules,
        out byte[] next,
        out bool changed,
        out string? error)
    {
        next = body;
        changed = false;
        error = null;

        var replacement = MaskJsonStrings(value, field.Role, rules, ref changed);
        if (!changed)
        {
            return true;
        }

        var node = OverrideJsonParser.ParseRaw(replacement);
        if (node is null)
        {
            error = $"replace protected JSON at {QuotePath(field.Path)}: invalid JSON";
            return false;
        }

        if (!OverrideJson.TrySetNode(body, field.Path, node, out next, out var setError))
        {
            error = $"replace protected JSON at {QuotePath(field.Path)}: {setError}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Port of maskPromptProtectionJSONStrings plus json.Marshal: only string
    /// values are masked, a rule cannot alter property names, and numbers keep
    /// their exact tokens. Objects marshal with sorted keys and last duplicate
    /// winning, matching Go's decode-into-map then marshal.
    /// </summary>
    private static string MaskJsonStrings(
        OverrideJsonNode node,
        string role,
        IReadOnlyList<PromptProtectionRule> rules,
        ref bool changed)
    {
        switch (node)
        {
            case OverrideJsonScalar { Kind: OverrideJsonKind.String } scalar:
                {
                    var replacement = ReplacePassThroughPromptText(
                        scalar.StringValue,
                        role,
                        rules,
                        out var matched);
                    if (matched)
                    {
                        changed = true;
                    }

                    return GoJson.Quote(replacement);
                }

            case OverrideJsonArray array:
                {
                    var builder = new StringBuilder();
                    builder.Append('[');
                    for (var index = 0; index < array.Items.Count; index++)
                    {
                        if (index > 0)
                        {
                            builder.Append(',');
                        }

                        builder.Append(MaskJsonStrings(array.Items[index], role, rules, ref changed));
                    }

                    builder.Append(']');
                    return builder.ToString();
                }

            case OverrideJsonObject obj:
                {
                    // json.Unmarshal into map[string]any: the last duplicate key wins.
                    var members = new Dictionary<string, OverrideJsonNode>(StringComparer.Ordinal);
                    foreach (var member in obj.Members)
                    {
                        members[member.Key] = member.Value;
                    }

                    // encoding/json sorts map keys.
                    var keys = members.Keys.ToList();
                    keys.Sort(StringComparer.Ordinal);

                    var builder = new StringBuilder();
                    builder.Append('{');
                    for (var index = 0; index < keys.Count; index++)
                    {
                        if (index > 0)
                        {
                            builder.Append(',');
                        }

                        builder.Append(GoJson.Quote(keys[index])).Append(':');
                        builder.Append(MaskJsonStrings(members[keys[index]], role, rules, ref changed));
                    }

                    builder.Append('}');
                    return builder.ToString();
                }

            default:
                // json.Number keeps its exact token; true/false/null are literals.
                return node.Raw;
        }
    }

    /// <summary>
    /// Port of replacePassThroughPromptText: the same ordered mask rules the
    /// unified prompt-protection service applies, limited to one text field.
    /// </summary>
    internal static string ReplacePassThroughPromptText(
        string text,
        string role,
        IReadOnlyList<PromptProtectionRule> rules,
        out bool changed)
    {
        changed = false;

        foreach (var rule in rules)
        {
            if (rule?.Settings is null
                || rule.Settings.Action != PromptProtectionActions.Mask)
            {
                continue;
            }

            if (!PromptProtectionRules.AppliesToRole(rule.Settings.Scopes, role)
                || !PromptProtectionRegex.Match(rule.Pattern, text))
            {
                continue;
            }

            text = PromptProtectionRegex.Replace(rule.Pattern, text, rule.Settings.Replacement);
            changed = true;
        }

        return text;
    }

    /// <summary>Port of rawPromptTextFields; the format switch fails safe.</summary>
    private static bool TryRawPromptTextFields(
        byte[] body,
        string? apiFormat,
        out List<RawPromptTextField> fields,
        out string? error)
    {
        error = null;

        switch (apiFormat)
        {
            case ApiFormats.OpenAIChatCompletion:
            case ApiFormats.OllamaChat:
                fields = OpenAIChatPromptTextFields(body);
                return true;

            case ApiFormats.OpenAIResponse:
            case ApiFormats.OpenAIResponseWebSocket:
            case ApiFormats.OpenAIResponseCompact:
                fields = OpenAIResponsesPromptTextFields(body);
                return true;

            case ApiFormats.AnthropicMessage:
                fields = AnthropicPromptTextFields(body);
                return true;

            case ApiFormats.GeminiContents:
                fields = GeminiPromptTextFields(body);
                return true;

            case ApiFormats.OpenAIDecisions:
                fields = DecisionsPromptTextFields(body);
                return true;

            default:
                fields = [];
                error = $"API format {QuotePath(apiFormat ?? string.Empty)}"
                    + " does not support raw prompt protection patches";
                return false;
        }
    }

    private static List<RawPromptTextField> DecisionsPromptTextFields(byte[] body)
    {
        if (PromptProtectionJson.IsString(body, "input"))
        {
            return [new RawPromptTextField("input", "user")];
        }

        var fields = new List<RawPromptTextField>();
        var inputLength = PromptProtectionJson.ArrayLength(body, "input");
        for (var i = 0; i < inputLength; i++)
        {
            var itemPath = "input." + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var role = PromptProtectionJson.String(body, itemPath + ".role");
            if (role.Length == 0)
            {
                role = "user";
            }

            fields.AddRange(RawContentTextFields(
                body,
                itemPath + ".content",
                role,
                "input_text",
                "text"));
            fields.AddRange(RawStringField(body, itemPath + ".text", role));
        }

        return fields;
    }

    /// <summary>String and text-part content from chat-style bodies.</summary>
    private static List<RawPromptTextField> OpenAIChatPromptTextFields(byte[] body)
    {
        var fields = new List<RawPromptTextField>();
        var messageCount = PromptProtectionJson.ArrayLength(body, "messages");

        for (var i = 0; i < messageCount; i++)
        {
            var messagePath = "messages." + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var role = PromptProtectionJson.String(body, messagePath + ".role");
            fields.AddRange(RawContentTextFields(body, messagePath + ".content", role, "text"));
        }

        return fields;
    }

    /// <summary>Instruction, input, and tool-output text fields.</summary>
    private static List<RawPromptTextField> OpenAIResponsesPromptTextFields(byte[] body)
    {
        var fields = RawStringField(body, "instructions", "system");
        if (PromptProtectionJson.IsString(body, "input"))
        {
            fields.Add(new RawPromptTextField("input", "user"));
            return fields;
        }

        var inputLength = PromptProtectionJson.ArrayLength(body, "input");
        for (var i = 0; i < inputLength; i++)
        {
            var itemPath = "input." + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var itemType = PromptProtectionJson.String(body, itemPath + ".type").ToLowerInvariant();
            var role = PromptProtectionJson.String(body, itemPath + ".role");

            switch (itemType)
            {
                case "message":
                case "input_text":
                case "":
                    // Inbound prefers non-null content over text for all three
                    // message forms. Missing or null content must still patch the
                    // text fallback after other matches.
                    if (!PromptProtectionJson.IsNull(body, itemPath + ".content"))
                    {
                        fields.AddRange(RawContentTextFields(
                            body,
                            itemPath + ".content",
                            role,
                            "input_text",
                            "output_text",
                            "text"));
                    }
                    else
                    {
                        fields.AddRange(RawStringField(body, itemPath + ".text", role));
                    }

                    break;

                case "function_call_output":
                case "custom_tool_call_output":
                    fields.AddRange(RawContentTextFields(
                        body,
                        itemPath + ".output",
                        "tool",
                        "input_text",
                        "output_text",
                        "text"));
                    break;
            }
        }

        return fields;
    }

    /// <summary>Top-level system text, message text, and tool-result text.</summary>
    private static List<RawPromptTextField> AnthropicPromptTextFields(byte[] body)
    {
        var fields = RawContentTextFields(body, "system", "system", "text");

        var messageCount = PromptProtectionJson.ArrayLength(body, "messages");
        for (var i = 0; i < messageCount; i++)
        {
            var messagePath = "messages." + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var role = PromptProtectionJson.String(body, messagePath + ".role");
            var contentPath = messagePath + ".content";
            fields.AddRange(RawContentTextFields(body, contentPath, role, "text"));

            var blockCount = PromptProtectionJson.ArrayLength(body, contentPath);
            for (var j = 0; j < blockCount; j++)
            {
                var blockPath = contentPath + "." + j.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(
                        PromptProtectionJson.String(body, blockPath + ".type"),
                        "tool_result",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                fields.AddRange(RawContentTextFields(body, blockPath + ".content", "tool", "text"));
            }
        }

        return fields;
    }

    /// <summary>System instruction and content-part text fields.</summary>
    private static List<RawPromptTextField> GeminiPromptTextFields(byte[] body)
    {
        var fields = GeminiContentPromptTextFields(body, "systemInstruction", "system");

        var contentCount = PromptProtectionJson.ArrayLength(body, "contents");
        for (var i = 0; i < contentCount; i++)
        {
            var contentPath = "contents." + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var role = GeminiRawRoleToPromptRole(
                PromptProtectionJson.String(body, contentPath + ".role"));
            fields.AddRange(GeminiContentPromptTextFields(body, contentPath, role));
        }

        return fields;
    }

    /// <summary>Text fields from one Gemini Content object.</summary>
    private static List<RawPromptTextField> GeminiContentPromptTextFields(
        byte[] body,
        string contentPath,
        string role)
    {
        var fields = new List<RawPromptTextField>();
        var partCount = PromptProtectionJson.ArrayLength(body, contentPath + ".parts");

        for (var i = 0; i < partCount; i++)
        {
            var partPath = contentPath + ".parts."
                + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields.AddRange(RawStringField(body, partPath + ".text", role));
            fields.AddRange(RawJsonField(body, partPath + ".functionResponse.response", "tool"));
        }

        return fields;
    }

    /// <summary>Mirrors Gemini inbound role normalization.</summary>
    private static string GeminiRawRoleToPromptRole(string role) => role switch
    {
        "model" => "assistant",
        "" or "user" => "user",
        _ => role,
    };

    /// <summary>Either a scalar content string or typed text parts.</summary>
    private static List<RawPromptTextField> RawContentTextFields(
        byte[] body,
        string contentPath,
        string role,
        params string[] allowedTypes)
    {
        if (PromptProtectionJson.IsString(body, contentPath))
        {
            return [new RawPromptTextField(contentPath, role)];
        }

        var fields = new List<RawPromptTextField>();
        var partCount = PromptProtectionJson.ArrayLength(body, contentPath);
        for (var i = 0; i < partCount; i++)
        {
            var partPath = contentPath + "." + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var partType = PromptProtectionJson.String(body, partPath + ".type").ToLowerInvariant();
            if (!allowedTypes.Contains(partType, StringComparer.Ordinal))
            {
                continue;
            }

            fields.AddRange(RawStringField(body, partPath + ".text", role));
        }

        return fields;
    }

    /// <summary>A field only when the supplied JSON path is a string.</summary>
    private static List<RawPromptTextField> RawStringField(byte[] body, string path, string role)
        => PromptProtectionJson.IsString(body, path)
            ? [new RawPromptTextField(path, role)]
            : [];

    /// <summary>
    /// One structured field when a provider stores protected tool content as an
    /// object instead of a JSON string.
    /// </summary>
    private static List<RawPromptTextField> RawJsonField(byte[] body, string path, string role)
        => PromptProtectionJson.IsJson(body, path)
            ? [new RawPromptTextField(path, role, StructuredJson: true)]
            : [];

    /// <summary>Go's %q for the ASCII path and format strings used here.</summary>
    private static string QuotePath(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
