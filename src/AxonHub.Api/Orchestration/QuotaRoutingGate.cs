using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.ProviderQuotaStatusProvider
/// (internal/server/orchestrator/provider_quota_provider.go): quota status per
/// channel, read from the provider quota service's cache.
/// </summary>
public interface IProviderQuotaStatusProvider
{
    /// <summary>
    /// The cached status for a channel, or <see langword="null"/> when the
    /// channel has no quota status (the reference's nil pointer).
    /// </summary>
    QuotaChannelStatus? GetQuotaStatus(long channelId);
}

/// <summary>
/// Port of orchestrator.QuotaRoutingGate
/// (internal/server/orchestrator/candidates_quota.go:14-166): applies the quota
/// routing policy to one request's candidates, in two phases. Phase one keeps
/// the channels that may serve the request; when it is empty, phase two keeps
/// the <c>sticky_only</c> channels as the last resort.
/// </summary>
public sealed class QuotaRoutingGate
{
    private const string RemoveOnExhausted = QuotaRoutingModes.RemoveOnExhausted;
    private const string Backpressure = QuotaRoutingModes.Backpressure;
    private const string IgnoreQuota = QuotaRoutingModes.IgnoreQuota;

    private readonly IProviderQuotaStatusProvider? _provider;
    private readonly string _defaultMode;
    private readonly TimeProvider _timeProvider;

    public QuotaRoutingGate(
        IProviderQuotaStatusProvider? provider,
        string? defaultMode,
        TimeProvider? timeProvider = null)
    {
        _provider = provider;
        _defaultMode = string.IsNullOrEmpty(defaultMode) ? RemoveOnExhausted : defaultMode;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// How many candidates the last <see cref="Filter"/> call removed. The
    /// selectCandidates middleware uses it to choose between the quota error and
    /// the invalid-model error when the result is empty.
    /// </summary>
    public int DroppedCount { get; private set; }

    /// <summary>
    /// Port of <c>QuotaRoutingGate.Filter</c> (candidates_quota.go:38-80).
    /// </summary>
    public IReadOnlyList<ChannelCandidate> Filter(
        IReadOnlyList<ChannelCandidate> candidates,
        LlmRequest request,
        long stickyId)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(request);

        DroppedCount = 0;
        if (_provider is null || candidates.Count == 0)
        {
            return candidates;
        }

        var limitType = ProviderQuotaRouting.RequestModality(request.Image is not null);
        var decisions = new List<QuotaRoutingDecision>(candidates.Count);
        var phaseOne = new List<ChannelCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var decision = Evaluate(candidate, limitType, stickyId);
            decisions.Add(decision);
            if (decision.KeepPhaseOne)
            {
                phaseOne.Add(candidate);
            }
        }

        List<ChannelCandidate> filtered = phaseOne;
        if (phaseOne.Count == 0)
        {
            var phaseTwo = new List<ChannelCandidate>(decisions.Count);
            foreach (var decision in decisions)
            {
                if (decision.KeepPhaseTwo)
                {
                    phaseTwo.Add(decision.Candidate);
                }
            }

            filtered = phaseTwo;
        }

        DroppedCount = candidates.Count - filtered.Count;
        return filtered;
    }

    /// <summary>Port of <c>QuotaRoutingGate.evaluate</c> (candidates_quota.go:90-148).</summary>
    private QuotaRoutingDecision Evaluate(
        ChannelCandidate candidate,
        string limitType,
        long stickyId)
    {
        var mode = _defaultMode;
        var channelMode = QuotaRoutingModeOf(candidate);
        if (!string.IsNullOrEmpty(channelMode))
        {
            mode = channelMode;
        }

        if (mode is not (RemoveOnExhausted or Backpressure or IgnoreQuota))
        {
            // Inherit / unknown channel modes fail closed to the strictest
            // policy, exactly like the reference's default branch.
            mode = RemoveOnExhausted;
        }

        var decision = new QuotaRoutingDecision(candidate);
        if (mode == IgnoreQuota)
        {
            decision.KeepPhaseOne = true;
            decision.KeepPhaseTwo = true;
            return decision;
        }

        var status = _provider?.GetQuotaStatus(candidate.ChannelId);
        if (status is null)
        {
            // No quota data is neutral: the channel stays usable in both phases.
            decision.KeepPhaseOne = true;
            decision.KeepPhaseTwo = true;
            return decision;
        }

        var (state, _) = ProviderQuotaRouting.EvaluateQuotaRouting(
            status.Limits,
            status.Status,
            limitType,
            _timeProvider.GetUtcNow());

        if (state == QuotaRoutingStates.Exhausted)
        {
            return decision;
        }

        switch (mode)
        {
            case RemoveOnExhausted:
                decision.KeepPhaseOne = true;
                decision.KeepPhaseTwo = true;
                break;
            case Backpressure:
                decision.KeepPhaseOne =
                    state is QuotaRoutingStates.Open or QuotaRoutingStates.Unknown
                    || (state == QuotaRoutingStates.StickyOnly && stickyId != 0 && candidate.ChannelId == stickyId);
                decision.KeepPhaseTwo = state == QuotaRoutingStates.StickyOnly;
                break;
            default:
                decision.KeepPhaseOne = true;
                decision.KeepPhaseTwo = true;
                break;
        }

        return decision;
    }

    /// <summary>
    /// Reads the candidate's <c>settings.quotaRoutingMode</c>. Malformed
    /// settings are treated as absent (the port's lenient channel load, C157).
    /// </summary>
    internal static string? QuotaRoutingModeOf(ChannelCandidate candidate)
    {
        var raw = candidate.Settings;
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return null;
        }

        try
        {
            return LlmJson.Deserialize<ChannelSettingsDraft>(raw)?.QuotaRoutingMode;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class QuotaRoutingDecision(ChannelCandidate candidate)
    {
        public ChannelCandidate Candidate { get; } = candidate;

        public bool KeepPhaseOne { get; set; }

        public bool KeepPhaseTwo { get; set; }
    }
}

/// <summary>Port of objects.QuotaRoutingMode values (internal/objects/quota_routing.go:9-13).</summary>
public static class QuotaRoutingModes
{
    public const string IgnoreQuota = "ignore_quota";
    public const string RemoveOnExhausted = "remove_on_exhausted";
    public const string Backpressure = "backpressure";

    public static bool IsValid(string? mode)
        => mode is IgnoreQuota or RemoveOnExhausted or Backpressure;
}

/// <summary>
/// Port of orchestrator.QuotaExhaustedError (internal/server/orchestrator/errors.go:5-16):
/// every candidate was removed by the quota gate.
/// </summary>
public sealed class QuotaExhaustedException(string modelName) : Exception(
    "all channels quota exhausted for model " + modelName)
{
    public string ModelName { get; } = modelName;
}
