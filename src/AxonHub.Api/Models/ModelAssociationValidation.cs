using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Models;

/// <summary>Thrown by <see cref="ModelAssociationValidation"/>; message matches the reference.</summary>
public sealed class ModelSettingsValidationException(string message) : Exception(message);

/// <summary>
/// Port of the developer-settings half of <c>validateModelSettings</c> and the
/// helpers in internal/server/biz/model.go:129-353, used by
/// <c>SetModelSettings</c> (internal/server/biz/system.go:1275-1291).
/// </summary>
public static class ModelAssociationValidation
{
    /// <summary>
    /// Port of <c>validateModelSettings</c> (internal/server/biz/model.go:49-76)
    /// for the per-model settings column. The reference mutates the pointer
    /// (NormalizeRoutingPolicy) and the caller then persists the same pointer,
    /// so the returned record carries the normalized routing policy values.
    /// </summary>
    /// <returns>
    /// <paramref name="settings"/> unchanged when it is null, otherwise the
    /// normalized record.
    /// </returns>
    /// <exception cref="ModelSettingsValidationException">
    /// The routing policy or an association rule is invalid.
    /// </exception>
    public static ModelEntitySettings? ValidateEntitySettings(ModelEntitySettings? settings)
    {
        if (settings is null)
        {
            return null;
        }

        var normalized = settings.NormalizeRoutingPolicy();
        if (!RoutingPolicy.IsValidLoadBalancerStrategy(normalized.LoadBalancerStrategy))
        {
            throw new ModelSettingsValidationException(
                "invalid load balancer strategy "
                + GoCast.GoQuote(normalized.LoadBalancerStrategy));
        }

        if (!RoutingPolicy.IsValidTraceStickyMode(normalized.TraceStickyMode))
        {
            throw new ModelSettingsValidationException(
                "invalid trace sticky mode "
                + GoCast.GoQuote(normalized.TraceStickyMode));
        }

        if (normalized.Associations is { Count: > 0 })
        {
            ValidateModelSettingsAssociations(normalized.Associations);
        }

        return normalized;
    }

    /// <summary>
    /// Port of <c>validateDeveloperAssociations</c>
    /// (internal/server/biz/model_settings_inheritance.go:73-95): only
    /// channel_model / channel_tags_model are allowed, then every association
    /// is checked by the general model-settings validator.
    /// </summary>
    public static void ValidateDeveloperAssociations(IReadOnlyList<ModelAssociation?> associations)
    {
        foreach (var assoc in associations)
        {
            if (assoc is null)
            {
                continue;
            }

            switch (assoc.Type)
            {
                case ModelAssociation.TypeChannelModel:
                    if (assoc.ChannelModel is null || assoc.ChannelModel.ChannelId == 0)
                    {
                        throw new ModelSettingsValidationException(
                            "developer channel association requires channel");
                    }

                    break;
                case ModelAssociation.TypeChannelTagsModel:
                    if (assoc.ChannelTagsModel is null
                        || assoc.ChannelTagsModel.ChannelTags is null
                        || assoc.ChannelTagsModel.ChannelTags.Count == 0)
                    {
                        throw new ModelSettingsValidationException(
                            "developer channel tags association requires channel tags");
                    }

                    break;
                default:
                    throw new ModelSettingsValidationException(
                        "developer association type " + GoCast.GoQuote(assoc.Type)
                        + " is not supported");
            }
        }

        ValidateModelSettingsAssociations(associations);
    }

    /// <summary>
    /// Port of the association loop in <c>validateModelSettings</c>
    /// (internal/server/biz/model.go:74-127). The routing-policy checks are not
    /// repeated: the reference builds an otherwise empty <c>objects.ModelSettings</c>
    /// whose zero routing policy normalizes to valid defaults.
    /// </summary>
    private static void ValidateModelSettingsAssociations(
        IReadOnlyList<ModelAssociation?> associations)
    {
        foreach (var assoc in associations)
        {
            if (assoc is null)
            {
                continue;
            }

            if (assoc.When is not null)
            {
                var error = ValidateAssociationWhen(assoc.When);
                if (error is not null)
                {
                    throw new ModelSettingsValidationException(
                        "invalid when condition: " + error);
                }
            }

            if (!string.IsNullOrEmpty(assoc.ChannelRegex?.Pattern))
            {
                var error = XRegexp.ValidateRegex(assoc.ChannelRegex.Pattern);
                if (error is not null)
                {
                    throw new ModelSettingsValidationException(
                        "invalid regex pattern in channel_regex association: " + error);
                }
            }

            if (!string.IsNullOrEmpty(assoc.ChannelTagsRegex?.Pattern))
            {
                var error = XRegexp.ValidateRegex(assoc.ChannelTagsRegex.Pattern);
                if (error is not null)
                {
                    throw new ModelSettingsValidationException(
                        "invalid regex pattern in channel_tags_regex association: " + error);
                }
            }

            if (!string.IsNullOrEmpty(assoc.Regex?.Pattern))
            {
                var error = XRegexp.ValidateRegex(assoc.Regex.Pattern);
                if (error is not null)
                {
                    throw new ModelSettingsValidationException(
                        "invalid regex pattern in regex association: " + error);
                }
            }

            ValidateExcludePatterns(assoc.Regex?.Exclude);
            ValidateExcludePatterns(assoc.ModelId?.Exclude);
        }
    }

    private static void ValidateExcludePatterns(IReadOnlyList<ExcludeAssociation?>? excludes)
    {
        if (excludes is null || excludes.Count == 0)
        {
            return;
        }

        foreach (var exclude in excludes)
        {
            // Go dereferences nil entries here and panics; the port skips them
            // instead of reproducing a null-reference crash (C130).
            if (exclude is null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(exclude.ChannelNamePattern))
            {
                continue;
            }

            var error = XRegexp.ValidateRegex(exclude.ChannelNamePattern);
            if (error is not null)
            {
                throw new ModelSettingsValidationException(
                    "invalid regex pattern in exclude rule: " + error);
            }
        }
    }

    /// <summary>Port of <c>validateModelAssociationWhen</c> (internal/server/biz/model.go:129-146).</summary>
    private static string? ValidateAssociationWhen(ModelAssociationWhen when)
    {
        if (!when.Enabled)
        {
            return null;
        }

        if (when.Condition is null)
        {
            return "at least one supported when condition is required";
        }

        return ValidateFilterConditionNode(
            when.Condition,
            allowNestedGroups: true,
            maxNestedLevels: 3);
    }

    /// <summary>Port of <c>validateFilterConditionNode</c> (internal/server/biz/model.go:153-205).</summary>
    private static string? ValidateFilterConditionNode(
        FilterCondition condition,
        bool allowNestedGroups,
        int maxNestedLevels)
        => ValidateFilterConditionNodeAtDepth(
            condition,
            allowNestedGroups,
            maxNestedLevels,
            depth: 1,
            requireGroup: true);

    private static string? ValidateFilterConditionNodeAtDepth(
        FilterCondition? condition,
        bool allowNestedGroups,
        int maxNestedLevels,
        int depth,
        bool requireGroup)
    {
        if (condition is null)
        {
            return null;
        }

        var nodeType = condition.Type;
        if (nodeType.Length == 0)
        {
            nodeType = FilterCondition.TypeGroup;
        }

        if (requireGroup && nodeType != FilterCondition.TypeGroup)
        {
            return "root when condition must be a group";
        }

        switch (nodeType)
        {
            case FilterCondition.TypeGroup:
                if (condition.Conditions is null || condition.Conditions.Count == 0)
                {
                    return "condition requires at least one condition or group";
                }

                if (maxNestedLevels > 0 && depth > maxNestedLevels)
                {
                    return "condition nesting depth must not exceed "
                        + maxNestedLevels.ToString(CultureInfo.InvariantCulture);
                }

                foreach (var child in condition.Conditions)
                {
                    if (child.Type == FilterCondition.TypeGroup && !allowNestedGroups)
                    {
                        return "nested condition groups are not allowed";
                    }

                    var error = ValidateFilterConditionNodeAtDepth(
                        child,
                        allowNestedGroups,
                        maxNestedLevels,
                        depth + 1,
                        requireGroup: false);
                    if (error is not null)
                    {
                        return error;
                    }
                }

                return null;
            case "":
            case FilterCondition.TypeCondition:
                return ValidateFilterLeaf(condition);
            default:
                return "unsupported condition type " + GoCast.GoQuote(condition.Type);
        }
    }

    /// <summary>Port of <c>validateFilterLeaf</c> (internal/server/biz/model.go:207-240).</summary>
    private static string? ValidateFilterLeaf(FilterCondition condition)
    {
        if (condition.Field.Length == 0)
        {
            return "condition field is required";
        }

        if (condition.Field.StartsWith(
                ModelAssociationConditionField.RequestHeaderPrefix,
                StringComparison.Ordinal))
        {
            return ValidateRequestHeaderLeaf(condition);
        }

        return condition.Field switch
        {
            ModelAssociationConditionField.PromptTokens => ValidatePromptTokensLeaf(condition),
            ModelAssociationConditionField.Stream => ValidateBoolEqualityLeaf(
                condition,
                ModelAssociationConditionField.Stream),
            ModelAssociationConditionField.RequestFormat
                or ModelAssociationConditionField.ReasoningEffort => ValidateStringEqualityLeaf(
                    condition,
                    condition.Field),
            ModelAssociationConditionField.DailyTime => ValidateDailyTimeLeaf(condition),
            ModelAssociationConditionField.HasImage
                or ModelAssociationConditionField.HasVideo
                or ModelAssociationConditionField.HasDocument
                or ModelAssociationConditionField.HasAudio => ValidateBoolEqualityLeaf(
                    condition,
                    condition.Field),
            _ => "unsupported condition field " + GoCast.GoQuote(condition.Field),
        };
    }

    /// <summary>Port of <c>validatePromptTokensLeaf</c>.</summary>
    private static string? ValidatePromptTokensLeaf(FilterCondition condition)
    {
        switch (condition.Operator)
        {
            case "lt":
            case "lte":
            case "gt":
            case "gte":
            case "<":
            case "<=":
            case ">":
            case ">=":
                break;
            default:
                return "unsupported condition operator " + GoCast.GoQuote(condition.Operator)
                    + " for prompt_tokens";
        }

        if (!TryGetInteger(condition.Value, out var value))
        {
            return "condition value for prompt_tokens must be an integer";
        }

        if (value < 0)
        {
            return "prompt_tokens must be greater than or equal to 0";
        }

        return null;
    }

    /// <summary>Port of <c>validateBoolEqualityLeaf</c>.</summary>
    private static string? ValidateBoolEqualityLeaf(FilterCondition condition, string field)
    {
        switch (condition.Operator)
        {
            case "eq":
            case "ne":
            case "=":
            case "==":
            case "!=":
                break;
            default:
                return "unsupported condition operator " + GoCast.GoQuote(condition.Operator)
                    + " for " + field;
        }

        if (condition.Value is JsonValue value
            && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            return null;
        }

        return "condition value for " + field + " must be a boolean, got "
            + GoTypeName(condition.Value);
    }

    /// <summary>Port of <c>validateStringEqualityLeaf</c>.</summary>
    private static string? ValidateStringEqualityLeaf(FilterCondition condition, string field)
    {
        switch (condition.Operator)
        {
            case "eq":
            case "ne":
            case "=":
            case "==":
            case "!=":
                break;
            default:
                return "unsupported condition operator " + GoCast.GoQuote(condition.Operator)
                    + " for " + field;
        }

        if (!TryGetString(condition.Value, out var value) || value.Length == 0)
        {
            return "condition value for " + field + " must be a non-empty string";
        }

        return null;
    }

    /// <summary>Port of <c>validateDailyTimeLeaf</c>.</summary>
    private static string? ValidateDailyTimeLeaf(FilterCondition condition)
    {
        switch (condition.Operator)
        {
            case "within":
            case "not_within":
                break;
            default:
                return "unsupported condition operator " + GoCast.GoQuote(condition.Operator)
                    + " for daily_time";
        }

        if (!TryGetString(condition.Value, out var value) || value.Length == 0)
        {
            return "condition value for daily_time must be a daily time range";
        }

        return DailyTime.TryParseRange(value, out _, out _, out var error) ? null : error;
    }

    /// <summary>Port of <c>validateRequestHeaderLeaf</c>.</summary>
    private static string? ValidateRequestHeaderLeaf(FilterCondition condition)
    {
        switch (condition.Operator.Trim().ToLowerInvariant())
        {
            case "eq":
            case "ne":
            case "=":
            case "==":
            case "!=":
            case "<>":
            case "contains":
            case "not_contains":
            case "start_with":
            case "end_with":
                break;
            default:
                return "unsupported condition operator " + GoCast.GoQuote(condition.Operator)
                    + " for request_header";
        }

        var headerName = condition.Field[
            ModelAssociationConditionField.RequestHeaderPrefix.Length..].Trim();
        if (headerName.Length == 0)
        {
            return "request header name is required";
        }

        if (headerName.Length > 256)
        {
            return "request header name is too long";
        }

        if (HttpClientUtils.IsSensitiveHeader(headerName))
        {
            return "request header " + GoCast.GoQuote(headerName)
                + " is sensitive and cannot be used in conditions";
        }

        if (!TryGetString(condition.Value, out var value) || value.Length == 0)
        {
            return "condition value for request_header must be a non-empty string";
        }

        return null;
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        if (node is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String)
        {
            value = scalar.GetValue<string>();
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Port of <c>filterValueToInt64</c>: the GraphQL transport decodes JSON
    /// numbers into float64, so any integer-valued number is accepted.
    /// </summary>
    private static bool TryGetInteger(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue scalar || scalar.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        var raw = scalar.ToJsonString();
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        if (number != Math.Truncate(number) || number < long.MinValue || number > long.MaxValue)
        {
            return false;
        }

        value = (long)number;
        return true;
    }

    /// <summary>
    /// Go's <c>%T</c> for a value decoded from GraphQL variables: JSON numbers
    /// become float64, arrays []interface {} and objects map[string]interface {}.
    /// </summary>
    private static string GoTypeName(JsonNode? node)
    {
        if (node is null)
        {
            return "<nil>";
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.True or JsonValueKind.False => "bool",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "float64",
            JsonValueKind.Array => "[]interface {}",
            JsonValueKind.Object => "map[string]interface {}",
            _ => "<nil>",
        };
    }
}

/// <summary>
/// Port of <c>xtime.ParseDailyTimeRange</c> (internal/pkg/xtime/time.go:101-144).
/// </summary>
public static class DailyTime
{
    public static bool TryParseRange(
        string value,
        out TimeSpan start,
        out TimeSpan end,
        out string? error)
    {
        start = default;
        end = default;

        // Go splits on every "-" and requires exactly two parts.
        var parts = value.Split('-');
        if (parts.Length != 2)
        {
            error = "daily_time must use HH:mm-HH:mm format";
            return false;
        }

        if (!TryParseClock(parts[0], out start))
        {
            error = "invalid daily_time start: must use HH:mm";
            return false;
        }

        if (!TryParseClock(parts[1], out end))
        {
            error = "invalid daily_time end: must use HH:mm";
            return false;
        }

        if (start == end)
        {
            error = "daily_time start and end must be different";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Port of <c>parseDailyClock</c>: <c>time.Parse("15:04", strings.TrimSpace(value))</c>.
    /// Go's "15" layout accepts one or two hour digits (0-23) while "04" requires
    /// exactly two minute digits (0-59), and any leftover text fails the parse.
    /// </summary>
    private static bool TryParseClock(string value, out TimeSpan clock)
    {
        clock = default;

        var text = value.Trim();
        var index = 0;
        var hour = 0;
        var hourDigits = 0;
        while (index < text.Length && hourDigits < 2 && text[index] is >= '0' and <= '9')
        {
            hour = (hour * 10) + (text[index] - '0');
            hourDigits++;
            index++;
        }

        if (hourDigits == 0 || index >= text.Length || text[index] != ':')
        {
            return false;
        }

        index++;
        if (index + 2 != text.Length)
        {
            return false;
        }

        var minute = 0;
        for (var digit = 0; digit < 2; digit++)
        {
            var c = text[index + digit];
            if (c is < '0' or > '9')
            {
                return false;
            }

            minute = (minute * 10) + (c - '0');
        }

        if (hour > 23 || minute > 59)
        {
            return false;
        }

        clock = new TimeSpan(hour, minute, 0);
        return true;
    }
}
