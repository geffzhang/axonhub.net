using AxonHub.Api.Models;
using AxonHub.Llm;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.splitAutoReasoningEffortModel / isQwenMaxModel
/// (internal/server/orchestrator/auto_reasoning_effort.go:65-99): a model name
/// ending in a supported reasoning-effort suffix is split into the base model
/// and the effort, except Qwen's own <c>-max</c> model names.
/// </summary>
public static class AutoReasoningEffort
{
    private static readonly HashSet<string> SupportedEfforts = new(StringComparer.Ordinal)
    {
        "max",
        "xhigh",
        "high",
        "medium",
        "low",
    };

    /// <summary>
    /// Port of splitAutoReasoningEffortModel. The suffix must follow a dash that
    /// is neither the first nor the last character; the suffix is compared
    /// case-insensitively, and Qwen <c>-max</c> models keep their full name.
    /// </summary>
    public static bool TrySplit(string model, out string baseModel, out string reasoningEffort)
    {
        ArgumentNullException.ThrowIfNull(model);

        baseModel = string.Empty;
        reasoningEffort = string.Empty;

        var lastDash = model.LastIndexOf('-');
        if (lastDash <= 0 || lastDash == model.Length - 1)
        {
            return false;
        }

        var effort = model[(lastDash + 1)..].ToLowerInvariant();
        if (!SupportedEfforts.Contains(effort))
        {
            return false;
        }

        if (effort == "max" && IsQwenMaxModel(model))
        {
            return false;
        }

        var baseName = model[..lastDash];
        if (baseName.Length == 0)
        {
            return false;
        }

        baseModel = baseName;
        reasoningEffort = effort;
        return true;
    }

    /// <summary>Port of isQwenMaxModel: trim, lowercase, require <c>-max</c>, then look only at the segment after the last slash.</summary>
    public static bool IsQwenMaxModel(string model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var normalized = model.Trim().ToLowerInvariant();
        if (!normalized.EndsWith("-max", StringComparison.Ordinal))
        {
            return false;
        }

        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            normalized = normalized[(lastSlash + 1)..];
        }

        return normalized.StartsWith("qwen", StringComparison.Ordinal);
    }

    /// <summary>
    /// Port of the middleware's inbound hook: when the setting is on and the
    /// model carries a supported suffix, rewrite the model and set the effort.
    /// </summary>
    public static void Apply(LlmRequest request, ModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);

        if (request.Model.Length == 0 || !settings.AutoReasoningEffort)
        {
            return;
        }

        if (!TrySplit(request.Model, out var baseModel, out var reasoningEffort))
        {
            return;
        }

        request.Model = baseModel;
        request.ReasoningEffort = reasoningEffort;
    }
}

/// <summary>
/// Port of orchestrator.autoReasoningEffortMiddleware
/// (internal/server/orchestrator/auto_reasoning_effort.go:27-63). The settings
/// are a per-request snapshot of <c>SystemService.ModelSettingsOrDefault</c>;
/// the port reads them before candidate selection so the selector sees the
/// normalized model, then mounts this instance in the pipeline so the pipeline's
/// own parse of the raw body produces the same request.
/// </summary>
internal sealed class AutoReasoningEffortMiddleware(ModelSettings settings)
    : DummyMiddleware("auto-reasoning-effort")
{
    public override LlmRequest TransformInboundLlmRequest(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        AutoReasoningEffort.Apply(request, settings);
        return request;
    }
}
