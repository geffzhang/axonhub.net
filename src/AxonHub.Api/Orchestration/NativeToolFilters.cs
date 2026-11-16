using AxonHub.Grains.Abstractions;
using AxonHub.Llm;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the native-tool half of the selectCandidates middleware
/// (internal/server/orchestrator/select_candidates.go:53-61 and
/// candidates_google.go / candidates_anthropic.go). When the request carries a
/// Google or Anthropic native tool, only candidates whose channel type supports
/// that tool survive; if none is compatible the original list is returned so
/// downstream fallback logic can still run.
/// </summary>
public static class NativeToolFilters
{
    /// <summary>
    /// Applies the two decorators in the reference's order. Each decorator is
    /// installed only for its matching inbound API format, and its filter only
    /// engages when the request actually contains a native tool.
    /// </summary>
    public static IReadOnlyList<ChannelCandidate> ApplyToCandidates(
        IReadOnlyList<ChannelCandidate> candidates,
        LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<ChannelCandidate> scoped = candidates;

        // select_candidates.go:53-55: Google native tools filter, only for the
        // native Gemini contents format.
        if (request.ApiFormat == ApiFormats.GeminiContents)
        {
            scoped = Prioritize(
                scoped,
                GoogleToolFilter.ContainsGoogleNativeTools(request.Tools),
                static candidate => ChannelTypeCapabilities.SupportsGoogleNativeTools(candidate.Type));
        }

        // select_candidates.go:57-61: Anthropic native tools filter, only for
        // the Anthropic Messages format.
        if (request.ApiFormat == ApiFormats.AnthropicMessage)
        {
            scoped = Prioritize(
                scoped,
                AnthropicToolFilter.ContainsAnthropicNativeTools(request.Tools),
                static candidate => ChannelTypeCapabilities.SupportsAnthropicNativeTools(candidate.Type));
        }

        return scoped;
    }

    private static IReadOnlyList<ChannelCandidate> Prioritize(
        IReadOnlyList<ChannelCandidate> candidates,
        bool active,
        Func<ChannelCandidate, bool> supports)
    {
        if (!active)
        {
            return candidates;
        }

        var compatible = candidates.Where(supports).ToArray();

        // candidates_google.go:46-53 / candidates_anthropic.go:54-61: no
        // compatible candidate means falling back to the whole list.
        return compatible.Length > 0 ? compatible : candidates;
    }
}
