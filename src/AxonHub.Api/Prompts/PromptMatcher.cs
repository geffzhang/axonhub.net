using AxonHub.Api.Models;
using AxonHub.Llm;

namespace AxonHub.Api.Prompts;

/// <summary>
/// Port of biz.PromptMatcher (internal/server/biz/prompt_matcher.go): evaluates
/// a prompt's activation conditions and applies matching prompts to a request.
/// </summary>
/// <remarks>
/// Conditions are an AND of composites, each composite an OR of conditions; a
/// missing or empty list matches everything. A condition is only satisfied by
/// a known type with its value present.
/// </remarks>
internal static class PromptMatcher
{
    /// <summary>Port of PromptMatcher.MatchPrompt.</summary>
    public static bool MatchPrompt(Prompt? prompt, string model, long apiKeyId)
        => prompt is not null && MatchConditions(prompt.Settings.Conditions, model, apiKeyId);

    /// <summary>Port of PromptMatcher.MatchConditions.</summary>
    public static bool MatchConditions(
        IReadOnlyList<PromptActivationConditionComposite>? conditions,
        string model,
        long apiKeyId)
    {
        if (conditions is null || conditions.Count == 0)
        {
            return true;
        }

        foreach (var composite in conditions)
        {
            if (!MatchCompositeCondition(composite, model, apiKeyId))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Port of PromptMatcher.FilterMatchingPrompts: preserves the provider's
    /// order, like lo.Filter.
    /// </summary>
    public static List<Prompt> FilterMatchingPrompts(
        IReadOnlyList<Prompt> prompts,
        string model,
        long apiKeyId)
    {
        var matching = new List<Prompt>(prompts.Count);
        foreach (var prompt in prompts)
        {
            if (MatchPrompt(prompt, model, apiKeyId))
            {
                matching.Add(prompt);
            }
        }

        return matching;
    }

    /// <summary>
    /// Port of PromptMatcher.ApplyPrompts: sorted by Order ascending with
    /// CreatedAt as the stable tiebreaker, prepends before the existing
    /// messages, appends after them. An unknown action type prepends.
    /// </summary>
    public static LlmRequest ApplyPrompts(LlmRequest request, IReadOnlyList<Prompt> prompts)
    {
        if (prompts.Count == 0)
        {
            return request;
        }

        // sort.SliceStable(prompts, Order then CreatedAt.Before); LINQ's
        // OrderBy/ThenBy is stable, so equal keys keep the provider order.
        var sorted = prompts
            .OrderBy(prompt => prompt.Order)
            .ThenBy(prompt => prompt.CreatedAt)
            .ToList();

        var prependMessages = new List<Message>();
        var appendMessages = new List<Message>();

        foreach (var prompt in sorted)
        {
            var message = new Message
            {
                Role = prompt.Role,
                Content = MessageContent.FromText(prompt.Content),
            };

            if (string.Equals(prompt.Settings.Action?.Type, PromptActionTypes.Append, StringComparison.Ordinal))
            {
                appendMessages.Add(message);
            }
            else
            {
                // PromptActionTypePrepend and the Go switch's default branch.
                prependMessages.Add(message);
            }
        }

        var messages = new List<Message>(
            prependMessages.Count + request.Messages.Count + appendMessages.Count);
        messages.AddRange(prependMessages);
        messages.AddRange(request.Messages);
        messages.AddRange(appendMessages);

        request.Messages = messages;
        return request;
    }

    /// <summary>
    /// Port of PromptMatcher.matchCompositeCondition: an empty composite
    /// matches; a null element is Go's zero composite and also matches.
    /// </summary>
    private static bool MatchCompositeCondition(
        PromptActivationConditionComposite? composite,
        string model,
        long apiKeyId)
    {
        if (composite?.Conditions is not { Count: > 0 })
        {
            return true;
        }

        foreach (var condition in composite.Conditions)
        {
            if (MatchCondition(condition, model, apiKeyId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Port of PromptMatcher.matchCondition. A null element is Go's zero
    /// condition (empty type) and lands in the default branch.
    /// </summary>
    private static bool MatchCondition(
        PromptActivationCondition? condition,
        string model,
        long apiKeyId)
    {
        switch (condition?.Type)
        {
            case PromptActivationConditionTypes.ModelId:
                return condition.ModelId is not null
                    && string.Equals(condition.ModelId, model, StringComparison.Ordinal);
            case PromptActivationConditionTypes.ModelPattern:
                return !string.IsNullOrEmpty(condition.ModelPattern)
                    && XRegexp.MatchString(condition.ModelPattern, model);
            case PromptActivationConditionTypes.ApiKey:
                return condition.ApiKeyId is not null && condition.ApiKeyId == apiKeyId;
            default:
                return false;
        }
    }
}
