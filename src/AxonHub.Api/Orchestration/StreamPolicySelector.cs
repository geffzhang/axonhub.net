using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.StreamPolicySelector
/// (internal/server/orchestrator/candidates_stream_policy.go:12-84). Streaming
/// requests drop channels whose stream policy is <c>forbid</c>; non-streaming
/// requests prefer channels that do not <c>require</c> streaming. When every
/// remaining channel requires streaming, request types that support automatic
/// aggregation keep them, Decisions raises a stream-policy conflict, and other
/// request types yield no candidates.
/// </summary>
public static class StreamPolicySelector
{
    private const string Unlimited = "unlimited";
    private const string Require = "require";
    private const string Forbid = "forbid";

    /// <summary>
    /// Applies the selector to the candidate list. Throws
    /// <see cref="StreamPolicyConflictException"/> for a Decisions request whose
    /// only candidates require streaming.
    /// </summary>
    public static IReadOnlyList<ChannelCandidate> ApplyToCandidates(
        IReadOnlyList<ChannelCandidate> candidates,
        LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(request);

        if (candidates.Count == 0)
        {
            return candidates;
        }

        if (request.Stream == true)
        {
            return [.. candidates.Where(static candidate => StreamPolicyOf(candidate) != Forbid)];
        }

        var nativeCandidates = candidates
            .Where(static candidate => StreamPolicyOf(candidate) != Require)
            .ToArray();

        if (nativeCandidates.Length > 0)
        {
            return nativeCandidates;
        }

        if (SupportsAutoAggregateRequest(request))
        {
            return candidates;
        }

        if (request.RequestType == RequestTypes.Decisions)
        {
            throw new StreamPolicyConflictException(request.Model);
        }

        return [];
    }

    /// <summary>
    /// Port of <c>streamPolicyOf</c>: an absent channel or an empty policy is
    /// unlimited. Malformed policies JSON falls back to unlimited, matching the
    /// port's lenient channel load (C157).
    /// </summary>
    internal static string StreamPolicyOf(ChannelCandidate? candidate)
    {
        var raw = candidate?.Policies;
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return Unlimited;
        }

        try
        {
            var policies = LlmJson.Deserialize<ChannelPoliciesDraft>(raw);
            return string.IsNullOrEmpty(policies?.Stream) ? Unlimited : policies.Stream;
        }
        catch (JsonException)
        {
            return Unlimited;
        }
    }

    /// <summary>
    /// Port of <c>supportsAutoAggregateRequest</c>: chat and completion formats
    /// can be aggregated after a streaming upstream, so a require-streaming
    /// channel remains usable for a non-streaming client. Shared with the
    /// per-candidate forced-streaming check in <see cref="ChannelTransformOptions"/>.
    /// </summary>
    internal static bool SupportsAutoAggregateRequest(LlmRequest request)
    {
        switch (request.RequestType)
        {
            case null or "" or RequestTypes.Chat:
                return request.ApiFormat is null or ""
                    or ApiFormats.OpenAIChatCompletion
                    or ApiFormats.OpenAIResponse
                    or ApiFormats.AnthropicMessage
                    or ApiFormats.GeminiContents
                    or ApiFormats.OllamaChat;
            case RequestTypes.Completion:
                return request.ApiFormat is null or "" or ApiFormats.OpenAICompletion;
            default:
                return false;
        }
    }
}

/// <summary>
/// Port of orchestrator.StreamPolicyConflictError
/// (internal/server/orchestrator/errors.go:17-33): every matching channel
/// requires streaming, and Decisions requests cannot be aggregated.
/// </summary>
public sealed class StreamPolicyConflictException(string modelName) : Exception(
    "no eligible channel for model "
    + modelName
    + ": matching channels require streaming, but Decisions requests are non-streaming")
{
    public string ModelName { get; } = modelName;
}
