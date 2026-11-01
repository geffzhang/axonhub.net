using AxonHub.Api.Orchestration;
using AxonHub.Llm;

namespace AxonHub.Api.PromptProtection;

/// <summary>
/// Port of biz.ApplyPromptProtectionRules
/// (internal/server/biz/prompt_protection_request.go): the ordered rule loop
/// shared by the unified request path and the pass-through raw body patcher.
/// </summary>
internal static class PromptProtectionRules
{
    /// <summary>
    /// Applies the enabled rules to a request, mutating it the same way the
    /// reference does. A rejection short-circuits with the rejecting rule; for
    /// the messages and compact paths the returned request is null, exactly as
    /// the reference's zero-value result.
    /// </summary>
    public static PromptProtectionResult Apply(
        LlmRequest? req,
        IReadOnlyList<PromptProtectionRule> rules)
    {
        if (req is not null && req.Decisions is not null && rules.Count > 0)
        {
            return ApplyDecisions(req, rules);
        }

        if (req is not null && req.Compact is not null && rules.Count > 0)
        {
            return ApplyCompact(req, rules);
        }

        if (req is null || req.Messages.Count == 0 || rules.Count == 0)
        {
            return new PromptProtectionResult { Request = req };
        }

        var messages = req.Messages;
        var matchedRules = new List<PromptProtectionRule>();

        foreach (var rule in rules)
        {
            if (rule?.Settings is null)
            {
                continue;
            }

            var ruleMatches = false;

            for (var index = 0; index < messages.Count; index++)
            {
                var message = messages[index];
                if (!AppliesToRole(rule.Settings.Scopes, message.Role))
                {
                    continue;
                }

                if (!ApplyRuleToMessage(message, rule))
                {
                    continue;
                }

                if (rule.Settings.Action == PromptProtectionActions.Reject)
                {
                    return new PromptProtectionResult
                    {
                        MatchedRules = [rule],
                        Rejected = true,
                    };
                }

                ruleMatches = true;
            }

            if (!ruleMatches)
            {
                continue;
            }

            matchedRules.Add(rule);
        }

        req.Messages = messages;

        return new PromptProtectionResult
        {
            Request = req,
            MatchedRules = matchedRules,
        };
    }

    private static PromptProtectionResult ApplyDecisions(
        LlmRequest req,
        IReadOnlyList<PromptProtectionRule> rules)
    {
        var body = (byte[])req.Decisions!.Body.Clone();
        var matchedRules = new List<PromptProtectionRule>();

        foreach (var rule in rules)
        {
            if (rule?.Settings is null)
            {
                continue;
            }

            var matched = false;
            foreach (var field in DecisionsPromptFields(body))
            {
                if (!AppliesToRole(rule.Settings.Scopes, field.Role)
                    || !PromptProtectionRegex.Match(rule.Pattern, field.Text))
                {
                    continue;
                }

                matched = true;
                if (rule.Settings.Action == PromptProtectionActions.Reject)
                {
                    return new PromptProtectionResult
                    {
                        Request = req,
                        MatchedRules = [rule],
                        Rejected = true,
                    };
                }

                var masked = PromptProtectionRegex.Replace(
                    rule.Pattern,
                    field.Text,
                    rule.Settings.Replacement);
                if (!OverrideJson.TrySet(
                        body,
                        field.Path,
                        new GoStringValue(masked),
                        out var nextBody,
                        out _))
                {
                    return new PromptProtectionResult { Request = req };
                }

                body = nextBody;
            }

            if (matched)
            {
                matchedRules.Add(rule);
            }
        }

        req.Decisions.Body = body;

        return new PromptProtectionResult { Request = req, MatchedRules = matchedRules };
    }

    private readonly record struct DecisionsPromptField(string Path, string Role, string Text);

    /// <summary>Port of decisionsPromptFields.</summary>
    private static List<DecisionsPromptField> DecisionsPromptFields(byte[] body)
    {
        if (PromptProtectionJson.IsString(body, "input"))
        {
            return [new DecisionsPromptField("input", "user", PromptProtectionJson.String(body, "input"))];
        }

        var fields = new List<DecisionsPromptField>();
        var inputLength = PromptProtectionJson.ArrayLength(body, "input");
        for (var i = 0; i < inputLength; i++)
        {
            var itemPath = "input." + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var role = PromptProtectionJson.String(body, itemPath + ".role");
            if (role.Length == 0)
            {
                role = "user";
            }

            var contentLength = PromptProtectionJson.ArrayLength(body, itemPath + ".content");
            for (var j = 0; j < contentLength; j++)
            {
                var partPath = itemPath + ".content." + j.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(
                        PromptProtectionJson.String(body, partPath + ".type"),
                        "input_text",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                fields.Add(new DecisionsPromptField(
                    partPath + ".text",
                    role,
                    PromptProtectionJson.String(body, partPath + ".text")));
            }
        }

        return fields;
    }

    /// <summary>
    /// Port of applyCompactPromptProtectionRules: protects Compact's separate
    /// input and instructions; its Messages slice is empty and cannot carry
    /// these masks.
    /// </summary>
    private static PromptProtectionResult ApplyCompact(
        LlmRequest req,
        IReadOnlyList<PromptProtectionRule> rules)
    {
        var compact = req.Compact!;

        // Go builds a shallow view: `view := *req` then fills a fresh Messages
        // slice with struct copies of the compact input.
        var view = req.ShallowClone();
        view.Compact = null;
        view.Messages = new List<Message>((compact.Input?.Count ?? 0) + 1);

        var inputOffset = 0;
        if (!string.IsNullOrEmpty(compact.Instructions))
        {
            view.Messages.Add(new Message
            {
                Role = "system",
                Content = new MessageContent { Content = compact.Instructions },
            });
            inputOffset = 1;
        }

        if (compact.Input is { } input)
        {
            foreach (var message in input)
            {
                view.Messages.Add(message.ShallowClone());
            }
        }

        var result = Apply(view, rules);
        if (result.Rejected)
        {
            return result;
        }

        if (inputOffset > 0)
        {
            compact.Instructions = view.Messages[0].Content?.Content;
        }

        compact.Input = view.Messages.GetRange(inputOffset, view.Messages.Count - inputOffset);
        result.Request = req;

        return result;
    }

    /// <summary>
    /// Port of applyPromptProtectionRuleToMessage. The message is mutated in
    /// place; the reference's value-copy plus slice assignment is observably the
    /// same because the replaced element is written back immediately.
    /// </summary>
    private static bool ApplyRuleToMessage(Message message, PromptProtectionRule rule)
    {
        var matched = false;
        var settings = rule.Settings!;

        if (message.Content?.Content is { Length: > 0 } content
            && PromptProtectionRegex.Match(rule.Pattern, content))
        {
            if (settings.Action == PromptProtectionActions.Mask)
            {
                var masked = PromptProtectionRegex.Replace(
                    rule.Pattern,
                    content,
                    settings.Replacement);
                // Go replaces the whole MessageContent, dropping any parts.
                message.Content = new MessageContent { Content = masked };
            }

            matched = true;
        }

        if (message.Content?.MultipleContent is { } parts)
        {
            foreach (var part in parts)
            {
                if (!string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(part.Text))
                {
                    continue;
                }

                if (!PromptProtectionRegex.Match(rule.Pattern, part.Text))
                {
                    continue;
                }

                if (settings.Action == PromptProtectionActions.Mask)
                {
                    part.Text = PromptProtectionRegex.Replace(
                        rule.Pattern,
                        part.Text,
                        settings.Replacement);
                }

                matched = true;
            }
        }

        return matched;
    }

    /// <summary>Port of promptProtectionRuleAppliesToRole / ...ToRawRole.</summary>
    public static bool AppliesToRole(List<string>? scopes, string? role)
    {
        if (scopes is null || scopes.Count == 0)
        {
            return true;
        }

        var roleScope = (role ?? string.Empty).ToLowerInvariant();
        return scopes.Contains(roleScope, StringComparer.Ordinal);
    }
}
