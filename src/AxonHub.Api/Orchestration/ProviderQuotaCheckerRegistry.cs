namespace AxonHub.Api.Orchestration;

/// <summary>
/// One checker registration: the provider key
/// (<c>ProviderQuotaService.checkers</c> in the reference) plus its checker.
/// Each future provider slice contributes its registrations through DI.
/// </summary>
public sealed record ProviderQuotaCheckerRegistration(string ProviderType, IQuotaChecker Checker);

/// <summary>
/// Read side of <c>ProviderQuotaService.checkers</c>
/// (internal/server/biz/provider_quota.go:351): provider key → checker.
/// </summary>
public interface IProviderQuotaCheckerRegistry
{
    /// <summary>The checker registered for a provider key, if any.</summary>
    bool TryGet(string providerType, out IQuotaChecker checker);

    /// <summary>The registered provider keys (diagnostics only).</summary>
    IReadOnlyCollection<string> ProviderTypes { get; }
}

/// <summary>
/// Registry built from every <see cref="ProviderQuotaCheckerRegistration"/>
/// found in DI. The Claude Code, Codex, Antigravity, xAI subscription, GitHub
/// Copilot, NanoGPT, ZenMux, Cline and Synthetic checkers are registered; the
/// remaining ten provider checkers land in follow-up slices (C182), so a
/// channel of those providers logs the reference's "No checker for provider"
/// error and keeps its previous row.
/// </summary>
public sealed class ProviderQuotaCheckerRegistry : IProviderQuotaCheckerRegistry
{
    private readonly Dictionary<string, IQuotaChecker> _checkers;

    public ProviderQuotaCheckerRegistry(
        IEnumerable<ProviderQuotaCheckerRegistration>? registrations = null)
    {
        _checkers = new Dictionary<string, IQuotaChecker>(StringComparer.Ordinal);
        if (registrations is null)
        {
            return;
        }

        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            _checkers[registration.ProviderType] = registration.Checker;
        }
    }

    public bool TryGet(string providerType, out IQuotaChecker checker)
        => _checkers.TryGetValue(providerType, out checker!);

    public IReadOnlyCollection<string> ProviderTypes => _checkers.Keys;
}
