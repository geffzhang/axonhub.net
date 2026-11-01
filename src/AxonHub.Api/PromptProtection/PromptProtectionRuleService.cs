using AxonHub.Llm;
using AxonHub.Persistence;

namespace AxonHub.Api.PromptProtection;

/// <summary>
/// Read side of the reference's enabled prompt-protection rule cache
/// (biz.PromptProtectionRuleService.ListEnabledRules). The reference serves the
/// list from a 30-second live cache; the port reads the database once per
/// request (difference C092).
/// </summary>
public interface IPromptProtectionRuleProvider
{
    Task<IReadOnlyList<PromptProtectionRule>> ListEnabledAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Loads the live, enabled rules in id order, which is the order the reference
/// applies them in. A malformed settings column is an error: the reference's
/// Ent scan fails the whole query, and the orchestrator then fails open.
/// </summary>
public sealed class PromptProtectionRuleService(IPromptProtectionRuleStore store)
    : IPromptProtectionRuleProvider
{
    public async Task<IReadOnlyList<PromptProtectionRule>> ListEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await store.ListEnabledAsync(cancellationToken).ConfigureAwait(false);

        var rules = new List<PromptProtectionRule>(records.Count);
        foreach (var record in records)
        {
            PromptProtectionSettings? settings = null;
            if (!string.IsNullOrWhiteSpace(record.Settings))
            {
                settings = PromptProtectionSettings.FromJson(record.Settings);
            }

            rules.Add(new PromptProtectionRule
            {
                Id = record.Id,
                Name = record.Name,
                Pattern = record.Pattern,
                Settings = settings,
            });
        }

        return rules;
    }
}

/// <summary>
/// Port of the migration from the legacy <see cref="IPromptProtecter"/> seam to
/// the result-provider seam: one protector snapshot per request, carrying the
/// rules loaded from the database.
/// </summary>
public sealed class PromptProtectionRuleProtector(IReadOnlyList<PromptProtectionRule>? rules)
    : IPromptProtecter, IPromptProtectionResultProvider
{
    private readonly IReadOnlyList<PromptProtectionRule> _rules = rules ?? [];

    public LlmRequest? Protect(LlmRequest request) => ProtectWithResult(request).Request;

    public PromptProtectionResult ProtectWithResult(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_rules.Count == 0)
        {
            return new PromptProtectionResult { Request = request };
        }

        var result = PromptProtectionRules.Apply(request, _rules);
        if (result.Rejected)
        {
            // biz.ErrPromptProtectionRejected: the middleware turns this into
            // transformer.ErrInvalidRequest with a fixed, content-free message.
            throw new PromptProtectionRejectedException();
        }

        return result;
    }
}

/// <summary>
/// A provider that always serves the same snapshot, for orchestrators built
/// without a store (tests and direct constructions).
/// </summary>
public sealed class StaticPromptProtectionRuleProvider(IReadOnlyList<PromptProtectionRule> rules)
    : IPromptProtectionRuleProvider
{
    private readonly IReadOnlyList<PromptProtectionRule> _rules = rules;

    public Task<IReadOnlyList<PromptProtectionRule>> ListEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(_rules);
    }
}
