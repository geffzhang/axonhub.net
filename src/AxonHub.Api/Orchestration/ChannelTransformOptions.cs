using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of internal/server/orchestrator/transform_options.go and the
/// per-candidate request shaping in outbound.go:434-598. Every method returns
/// the original request when nothing applies, and a shallow clone when a field
/// must be changed, mirroring Go's struct-copy semantics.
/// </summary>
internal static class ChannelTransformOptions
{
    private const string DeveloperRole = "developer";
    private const string SystemRole = "system";

    /// <summary>
    /// Reads the candidate's raw <c>channels.settings</c> column. Malformed
    /// settings are treated as absent, matching the port's lenient channel
    /// load (C157); the reference would have failed the channel load.
    /// </summary>
    internal static ChannelSettingsDraft? SettingsOf(ChannelCandidate candidate)
    {
        var raw = candidate.Settings;
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return null;
        }

        try
        {
            return LlmJson.Deserialize<ChannelSettingsDraft>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Port of <c>applyTransformOptions</c> (transform_options.go:16-44): the
    /// channel's three boolean transform options become unified-request
    /// options. Go's inline TransformOptions struct is copied with the request,
    /// so the port clones the class before writing into it.
    /// </summary>
    internal static LlmRequest Apply(LlmRequest request, ChannelSettingsDraft? settings)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = settings?.TransformOptions;
        if (options is null
            || (!options.ForceArrayInstructions
                && !options.ForceArrayInputs
                && !options.ReplaceDeveloperRoleWithSystem))
        {
            return request;
        }

        var clone = request.ShallowClone();

        if (options.ForceArrayInstructions || options.ForceArrayInputs)
        {
            var transformOptions = clone.TransformOptions is null
                ? new TransformOptions()
                : new TransformOptions
                {
                    ArrayInstructions = clone.TransformOptions.ArrayInstructions,
                    ArrayInputs = clone.TransformOptions.ArrayInputs,
                    DefaultMaxTokens = clone.TransformOptions.DefaultMaxTokens,
                };

            if (options.ForceArrayInstructions)
            {
                transformOptions.ArrayInstructions = true;
            }

            if (options.ForceArrayInputs)
            {
                transformOptions.ArrayInputs = true;
            }

            clone.TransformOptions = transformOptions;
        }

        if (options.ReplaceDeveloperRoleWithSystem)
        {
            clone.Messages = ReplaceDeveloperRoleWithSystem(clone.Messages);
        }

        return clone;
    }

    /// <summary>
    /// Port of <c>applyReasoningEffortMapping</c> (transform_options.go:76-105):
    /// map the unified request's effort centrally, and keep the Anthropic
    /// <c>output_config.effort</c> marker in sync because the messages outbound
    /// rebuilds native effort from it.
    /// </summary>
    internal static LlmRequest ApplyReasoningEffortMapping(
        LlmRequest request,
        ChannelSettingsDraft? settings)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mappings = settings?.TransformOptions?.ReasoningEffortMapping;
        if (mappings is not { Count: > 0 })
        {
            return request;
        }

        var effort = request.ReasoningEffort;
        if (string.IsNullOrEmpty(effort))
        {
            return request;
        }

        var mapped = effort;
        foreach (var mapping in mappings)
        {
            if (mapping.From == effort)
            {
                mapped = mapping.To ?? string.Empty;
                break;
            }
        }

        if (mapped == effort)
        {
            return request;
        }

        var clone = request.ShallowClone();

        // The shallow request copy shares the metadata dictionary with the
        // original request; clone it before writing the mapped effort.
        if (clone.TransformerMetadata is { } metadata
            && metadata.ContainsKey(AnthropicOutboundRequest.TransformerMetadataKeyOutputConfigEffort))
        {
            var copied = new Dictionary<string, JsonElement>(metadata, StringComparer.Ordinal)
            {
                [AnthropicOutboundRequest.TransformerMetadataKeyOutputConfigEffort] =
                    JsonSerializer.SerializeToElement(mapped, LlmJson.Options),
            };
            clone.TransformerMetadata = copied;
        }

        clone.ReasoningEffort = mapped;

        return clone;
    }

    /// <summary>
    /// Port of <c>filterResponseCustomToolMessagesForNonResponsesOutbound</c>
    /// (outbound.go:597-628): a Responses client replayed against a channel
    /// that does not speak Responses must not see Responses custom-tool
    /// messages.
    /// </summary>
    internal static LlmRequest FilterResponseCustomToolMessagesForNonResponsesOutbound(
        LlmRequest request,
        string? outboundFormat)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsResponsesFormat(request.ApiFormat)
            || IsResponsesFormat(outboundFormat)
            || !ContainsResponseCustomToolMessages(request.Messages))
        {
            return request;
        }

        var clone = request.ShallowClone();
        clone.Messages = SharedMessageFilter.FilterOutResponseCustomToolMessages(request.Messages);

        return clone;
    }

    /// <summary>Port of <c>isResponsesFormat</c> (outbound.go:838-840).</summary>
    internal static bool IsResponsesFormat(string? format)
        => format is ApiFormats.OpenAIResponse
            or ApiFormats.OpenAIResponseWebSocket
            or ApiFormats.OpenAIResponseCompact;

    /// <summary>Port of <c>containsResponseCustomToolMessages</c> (outbound.go:635-645).</summary>
    internal static bool ContainsResponseCustomToolMessages(IReadOnlyList<Message> messages)
    {
        foreach (var message in messages)
        {
            if (message.ToolCalls is not { } toolCalls)
            {
                continue;
            }

            foreach (var toolCall in toolCalls)
            {
                if (toolCall.Type == ToolTypes.ResponsesCustomTool
                    || toolCall.ResponseCustomToolCall is not null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Port of <c>shouldForceStreamingForCandidate</c> (outbound.go:434-450): a
    /// require-streaming channel may serve a non-streaming client only when
    /// the request type can be aggregated back from a streaming upstream.
    /// </summary>
    internal static bool ShouldForceStreamingForCandidate(
        ChannelCandidate? candidate,
        LlmRequest? request)
    {
        if (candidate is null || request is null)
        {
            return false;
        }

        if (request.Stream is true)
        {
            return false;
        }

        if (StreamPolicySelector.StreamPolicyOf(candidate) != "require")
        {
            return false;
        }

        return StreamPolicySelector.SupportsAutoAggregateRequest(request);
    }

    /// <summary>Port of <c>replaceDeveloperRoleWithSystem</c> (transform_options.go:108-131).</summary>
    private static List<Message> ReplaceDeveloperRoleWithSystem(List<Message> messages)
    {
        if (messages.Count == 0)
        {
            return messages;
        }

        var replaced = false;
        var result = new List<Message>(messages.Count);
        foreach (var message in messages)
        {
            if (string.Equals(message.Role, DeveloperRole, StringComparison.OrdinalIgnoreCase))
            {
                var copy = message.ShallowClone();
                copy.Role = SystemRole;
                result.Add(copy);
                replaced = true;
                continue;
            }

            result.Add(message);
        }

        return replaced ? result : messages;
    }
}
