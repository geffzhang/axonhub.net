using AxonHub.Abstractions;
using AxonHub.Llm;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of internal/server/orchestrator/select_endpoints.go: picks the outbound
/// API format for a candidate from the channel's resolved endpoints, honoring
/// per-model protocol overrides (ChannelSettings.ModelProtocols).
/// </summary>
public static class ChannelEndpointSelection
{
    /// <summary>
    /// Port of SelectAPIFormat (select_endpoints.go:16-45). Prefers the inbound
    /// format so pass-through can be enabled when both sides match, falls back
    /// to the first capable endpoint, then to the first endpoint. The reference
    /// treats a nil endpoint list as empty; the port's
    /// <see cref="ApplyForcedApiFormatsForRequest"/> may return null for an
    /// explicit-endpoint request whose overrides match nothing.
    /// </summary>
    public static string SelectApiFormat(
        IReadOnlyList<ChannelEndpoint>? endpoints,
        LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (endpoints is null || endpoints.Count == 0)
        {
            return string.Empty;
        }

        var preferredFormat = request.ApiFormat ?? string.Empty;
        var allowed = ApiCapabilities.CapableApiFormats(request.RequestType);

        if (allowed is not null)
        {
            if (preferredFormat.Length != 0)
            {
                foreach (var endpoint in endpoints)
                {
                    if (allowed.Contains(endpoint.ApiFormat) && endpoint.ApiFormat == preferredFormat)
                    {
                        return endpoint.ApiFormat;
                    }
                }
            }

            foreach (var endpoint in endpoints)
            {
                if (allowed.Contains(endpoint.ApiFormat))
                {
                    return endpoint.ApiFormat;
                }
            }

            if (RequiresExplicitEndpoint(request.RequestType))
            {
                return string.Empty;
            }
        }

        return endpoints[0].ApiFormat;
    }

    /// <summary>Port of requiresExplicitEndpoint (select_endpoints.go:47-51).</summary>
    public static bool RequiresExplicitEndpoint(string? requestType)
        => requestType is RequestTypes.AlphaSearch or RequestTypes.Decisions or RequestTypes.SystemOne;

    /// <summary>
    /// Port of FilterEndpointsByAPIFormats (select_endpoints.go:56-82): keeps
    /// the allowed-format priority order and the relative order of endpoints
    /// sharing a format. Returns null when nothing matches.
    /// </summary>
    public static List<ChannelEndpoint>? FilterEndpointsByApiFormats(
        IReadOnlyList<ChannelEndpoint> endpoints,
        IReadOnlyList<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(allowed);

        if (allowed.Count == 0 || endpoints.Count == 0)
        {
            return null;
        }

        var byFormat = new Dictionary<string, List<ChannelEndpoint>>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            if (!byFormat.TryGetValue(endpoint.ApiFormat, out var list))
            {
                list = [];
                byFormat[endpoint.ApiFormat] = list;
            }

            list.Add(endpoint);
        }

        var filtered = new List<ChannelEndpoint>(endpoints.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var format in allowed)
        {
            if (!seen.Add(format))
            {
                continue;
            }

            if (byFormat.TryGetValue(format, out var matching))
            {
                filtered.AddRange(matching);
            }
        }

        return filtered.Count == 0 ? null : filtered;
    }

    /// <summary>
    /// Port of forcedAPIFormatsForCandidate (select_endpoints.go:88-125): the
    /// union of ModelProtocols overrides keyed by the request model, any
    /// channel-side model entry, or its mapped actual model. Formats keep
    /// configuration order and duplicates are removed.
    /// </summary>
    public static List<string> ForcedApiFormats(
        ChannelDescriptor channel,
        IReadOnlyList<ChannelModelEntry> entries,
        string requestModel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(requestModel);

        // The reference short-circuits when no override is configured; the
        // per-name lookup below returns nothing in that case anyway.
        var names = new List<string>(entries.Count * 2 + 1);
        if (requestModel.Length != 0)
        {
            names.Add(requestModel);
        }

        foreach (var entry in entries)
        {
            if (entry.RequestModel.Length != 0 && entry.RequestModel != requestModel)
            {
                names.Add(entry.RequestModel);
            }

            if (entry.ActualModel.Length == 0 || entry.ActualModel == requestModel)
            {
                continue;
            }

            names.Add(entry.ActualModel);
        }

        // normalizeModelProtocols (channel_endpoint_responses.go:95-110): a
        // channel whose stored Responses intent is WebSocket rewrites pinned
        // HTTP Responses formats before they reach endpoint selection.
        var responsesOverWebSocket =
            ChannelEndpoints.LegacyResponsesFormat(channel.Endpoints, channel.BaseUrl)
                == ApiFormats.OpenAIResponseWebSocket;

        var forced = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            foreach (var configuredFormat in ChannelModelEntries.ForcedApiFormats(channel, name))
            {
                var format = responsesOverWebSocket && configuredFormat == ApiFormats.OpenAIResponse
                    ? ApiFormats.OpenAIResponseWebSocket
                    : configuredFormat;
                if (seen.Add(format))
                {
                    forced.Add(format);
                }
            }
        }

        return forced;
    }

    /// <summary>
    /// Port of applyForcedAPIFormats (select_endpoints.go:131-154): narrows the
    /// endpoint surface to the overrides. Configuration drift (no override
    /// format names a configured endpoint) keeps every endpoint.
    /// </summary>
    public static IReadOnlyList<ChannelEndpoint> ApplyForcedApiFormats(
        ChannelDescriptor channel,
        IReadOnlyList<ChannelModelEntry> entries,
        string requestModel,
        IReadOnlyList<ChannelEndpoint> endpoints)
    {
        var forced = ForcedApiFormats(channel, entries, requestModel);
        if (forced.Count == 0)
        {
            return endpoints;
        }

        var filtered = FilterEndpointsByApiFormats(endpoints, forced);
        return filtered is { Count: > 0 } ? filtered : endpoints;
    }

    /// <summary>
    /// Port of applyForcedAPIFormatsForRequest (select_endpoints.go:156-171):
    /// when an explicit-endpoint request type has overrides that match none of
    /// the endpoints, the candidate is rejected (null) instead of drifting back
    /// to the full surface.
    /// </summary>
    public static IReadOnlyList<ChannelEndpoint>? ApplyForcedApiFormatsForRequest(
        ChannelDescriptor channel,
        IReadOnlyList<ChannelModelEntry> entries,
        string requestModel,
        string? requestType,
        IReadOnlyList<ChannelEndpoint> endpoints)
    {
        var forced = ForcedApiFormats(channel, entries, requestModel);
        var filtered = ApplyForcedApiFormats(channel, entries, requestModel, endpoints);
        if (forced.Count > 0
            && filtered.Count == endpoints.Count
            && RequiresExplicitEndpoint(requestType)
            && FilterEndpointsByApiFormats(endpoints, forced) is null)
        {
            return null;
        }

        return filtered;
    }
}
