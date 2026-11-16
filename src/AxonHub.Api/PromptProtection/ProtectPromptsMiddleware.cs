using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;

namespace AxonHub.Api.PromptProtection;

/// <summary>
/// Port of orchestrator.protectPrompts (internal/server/orchestrator/prompt_protection.go):
/// masks or rejects prompts and snapshots the result for safe raw replay.
/// </summary>
/// <remarks>
/// The reference registers this as an inbound middleware after prompt
/// injection; the port registers it first because its outbound chain has no
/// other inbound hooks.
/// </remarks>
internal sealed class ProtectPromptsMiddleware(
    PersistentOutboundTransformer outbound,
    IInboundTransformer inbound,
    IPromptProtecter? protecter)
    : DummyMiddleware("protect-prompts")
{
    /// <summary>orchestrator.promptProtectionRejectedMessage.</summary>
    public const string RejectedMessage = "request blocked by prompt protection policy";

    public override LlmRequest TransformInboundLlmRequest(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (protecter is null)
        {
            return request;
        }

        var originalTexts = PromptProtectionTexts.Capture(request);

        LlmRequest? protectedRequest;
        IReadOnlyList<PromptProtectionRule> matchedRules;
        try
        {
            (protectedRequest, matchedRules) = ProtectPromptRequest(protecter, request);
        }
        catch (PromptProtectionRejectedException)
        {
            // transformer.ErrInvalidRequest wrapped with the fixed message; the
            // matched rule never leaks into the client-visible error.
            throw TransformerErrors.InvalidRequest(RejectedMessage);
        }
        catch (Exception)
        {
            // Provider errors are logged and the original request is kept.
            return request;
        }

        outbound.PromptProtectionMaskRules = matchedRules;

        if (protectedRequest is null)
        {
            protectedRequest = request;
        }

        if (protectedRequest.RawRequest is null)
        {
            // A legacy protector may return a new request without transport
            // metadata. Keep the inbound source available for safe replay and
            // model mapping.
            protectedRequest.RawRequest = request.RawRequest;
        }

        var protectedTexts = PromptProtectionTexts.Capture(protectedRequest);
        if (!string.Equals(request.ApiFormat, ApiFormats.OpenAIDecisions, StringComparison.Ordinal)
            && (matchedRules.Count > 0 || !originalTexts.SequenceEqual(protectedTexts)))
        {
            // Legacy protectors can change prompts without reporting rules. They
            // must also validate raw replay instead of silently restoring the
            // original text.
            outbound.PromptProtectionBodyCheck = new PromptProtectionBodyCheck(
                inbound,
                request.RawRequest,
                protectedTexts);
        }

        return protectedRequest;
    }

    /// <summary>Port of orchestrator.protectPromptRequest.</summary>
    private static (LlmRequest?, IReadOnlyList<PromptProtectionRule>) ProtectPromptRequest(
        IPromptProtecter current,
        LlmRequest request)
    {
        if (current is IPromptProtectionResultProvider resultProvider)
        {
            var result = resultProvider.ProtectWithResult(request);
            return (result.Request, result.MatchedRules);
        }

        // A legacy protector reports no rules; the middleware falls back to the
        // text snapshot to decide whether a body check is needed.
        return (current.Protect(request), []);
    }
}
