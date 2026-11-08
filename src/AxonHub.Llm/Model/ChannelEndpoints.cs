using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm;

/// <summary>
/// Port of objects.ChannelEndpoint (internal/objects/channel.go:20-35): one
/// API-format surface exposed by a channel.
/// </summary>
public sealed record ChannelEndpoint
{
    [JsonPropertyName("api_format")]
    public string ApiFormat { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; init; } = string.Empty;

    [JsonPropertyName("transport")]
    public string Transport { get; init; } = string.Empty;
}

/// <summary>Port of objects.ChannelEndpointTransport*.</summary>
public static class ChannelEndpointTransports
{
    public const string Http = "http";
    public const string WebSocket = "websocket";
}

/// <summary>
/// Port of the endpoint-resolution half of biz.Channel
/// (internal/server/biz/channel_endpoint.go): the built-in default endpoint
/// table per channel type and the merge with user-configured overrides
/// (ResolveEndpoints / mergeEndpoints).
/// </summary>
public static class ChannelEndpoints
{
    /// <summary>Port of channel.TypeCodex.</summary>
    public const string CodexChannelType = "codex";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly ChannelEndpoint[] OpenAICompatibleDefaultEndpoints =
    [
        Endpoint(ApiFormats.OpenAIChatCompletion),
        Endpoint(ApiFormats.OpenAIEmbedding),
        Endpoint(ApiFormats.OpenAIImageGeneration),
        Endpoint(ApiFormats.OpenAIImageEdit),
        Endpoint(ApiFormats.OpenAIImageVariation),
        Endpoint(ApiFormats.OpenAIVideo),
        Endpoint(ApiFormats.OpenAIModeration),
    ];

    private static readonly ChannelEndpoint[] OpenAIFullDefaultEndpoints =
    [
        .. OpenAICompatibleDefaultEndpoints,
        Endpoint(ApiFormats.OpenAISpeech),
        Endpoint(ApiFormats.OpenAITranscription),
        Endpoint(ApiFormats.OpenAITranslation),
    ];

    private static readonly ChannelEndpoint[] OpenAIChatOnlyDefaultEndpoints =
    [
        Endpoint(ApiFormats.OpenAIChatCompletion),
    ];

    private static readonly ChannelEndpoint[] AnthropicDefaultEndpoints =
    [
        Endpoint(ApiFormats.AnthropicMessage),
    ];

    private static readonly ChannelEndpoint[] GeminiDefaultEndpoints =
    [
        Endpoint(ApiFormats.GeminiContents),
        Endpoint(ApiFormats.GeminiEmbedding),
    ];

    private static readonly Dictionary<string, ChannelEndpoint[]> DefaultEndpointsByType =
        new(StringComparer.Ordinal)
        {
            ["openai"] = OpenAIFullDefaultEndpoints,
            ["zenmux"] = OpenAIFullDefaultEndpoints,
            ["openai_responses"] = [Endpoint(ApiFormats.OpenAIResponse)],
            ["zenmux_responses"] = [Endpoint(ApiFormats.OpenAIResponse)],
            ["atlascloud"] = OpenAICompatibleDefaultEndpoints,
            ["qiniu"] = [Endpoint(ApiFormats.OpenAIChatCompletion)],
            ["qiniu_anthropic"] = AnthropicDefaultEndpoints,
            ["cline"] = OpenAIChatOnlyDefaultEndpoints,
            ["codex"] =
            [
                Endpoint(ApiFormats.OpenAIResponse),
                Endpoint(ApiFormats.OpenAIResponseWebSocket),
                Endpoint(ApiFormats.OpenAIAlphaSearch),
                Endpoint(ApiFormats.OpenAIImageGeneration),
                Endpoint(ApiFormats.OpenAIImageEdit),
            ],
            ["fenno"] = [Endpoint(ApiFormats.OpenAIResponse)],
            ["vercel"] = OpenAICompatibleDefaultEndpoints,
            ["anthropic"] = AnthropicDefaultEndpoints,
            ["zenmux_anthropic"] = AnthropicDefaultEndpoints,
            ["anthropic_aws"] = AnthropicDefaultEndpoints,
            ["anthropic_gcp"] = AnthropicDefaultEndpoints,
            ["gemini_openai"] = [Endpoint(ApiFormats.OpenAIChatCompletion)],
            ["gemini"] = GeminiDefaultEndpoints,
            ["zenmux_gemini"] = GeminiDefaultEndpoints,
            ["zenmux_video"] = [Endpoint(ApiFormats.ZenmuxVideo)],
            ["gemini_vertex"] = GeminiDefaultEndpoints,
            ["deepseek"] =
            [
                Endpoint(ApiFormats.OpenAIChatCompletion),
                Endpoint(ApiFormats.OpenAICompletion),
            ],
            ["deepseek_anthropic"] = AnthropicDefaultEndpoints,
            ["deepinfra"] = OpenAICompatibleDefaultEndpoints,
            ["fireworks"] = [Endpoint(ApiFormats.OpenAIChatCompletion)],
            ["doubao"] =
            [
                Endpoint(ApiFormats.OpenAIChatCompletion),
                Endpoint(ApiFormats.SeedanceVideo),
            ],
            ["doubao_anthropic"] = AnthropicDefaultEndpoints,
            ["moonshot"] = [Endpoint(ApiFormats.OpenAIChatCompletion)],
            ["moonshot_anthropic"] = AnthropicDefaultEndpoints,
            ["zhipu"] = [Endpoint(ApiFormats.OpenAIChatCompletion)],
            ["zai"] = [Endpoint(ApiFormats.OpenAIChatCompletion)],
            ["zhipu_anthropic"] = AnthropicDefaultEndpoints,
            ["zai_anthropic"] = AnthropicDefaultEndpoints,
            ["anthropic_fake"] = AnthropicDefaultEndpoints,
            ["openai_fake"] = [Endpoint(ApiFormats.OpenAIChatCompletion)],
            ["openrouter"] =
            [
                Endpoint(ApiFormats.OpenAIChatCompletion),
                Endpoint(ApiFormats.OpenAISpeech),
                Endpoint(ApiFormats.OpenAITranscription),
                Endpoint(ApiFormats.OpenAITranslation),
            ],
            ["xiaomi"] = OpenAIChatOnlyDefaultEndpoints,
            ["xiaomi_anthropic"] = AnthropicDefaultEndpoints,
            ["xai"] =
            [
                Endpoint(ApiFormats.OpenAIChatCompletion),
                Endpoint(ApiFormats.OpenAIResponse),
            ],
            ["xai_responses"] = [Endpoint(ApiFormats.OpenAIResponse)],
            ["xai_subscription"] = [Endpoint(ApiFormats.OpenAIResponse)],
            ["ppio"] = OpenAICompatibleDefaultEndpoints,
            ["siliconflow"] = OpenAICompatibleDefaultEndpoints,
            ["volcengine"] = [Endpoint(ApiFormats.OpenAIChatCompletion)],
            ["volcengine_anthropic"] = AnthropicDefaultEndpoints,
            ["longcat"] = [Endpoint(ApiFormats.OpenAIChatCompletion)],
            ["longcat_anthropic"] = AnthropicDefaultEndpoints,
            ["minimax"] =
            [
                Endpoint(ApiFormats.OpenAIChatCompletion),
                Endpoint(ApiFormats.OpenAIImageGeneration),
            ],
            ["minimax_anthropic"] = AnthropicDefaultEndpoints,
            ["aihubmix"] = OpenAICompatibleDefaultEndpoints,
            ["aihubmix_anthropic"] = AnthropicDefaultEndpoints,
            ["burncloud"] = OpenAICompatibleDefaultEndpoints,
            ["modelscope"] =
            [
                Endpoint(ApiFormats.OpenAIChatCompletion),
                Endpoint(ApiFormats.ModelScopeImage),
            ],
            ["bailian"] = [Endpoint(ApiFormats.OpenAIChatCompletion)],
            ["bailian_responses"] = [Endpoint(ApiFormats.OpenAIResponse)],
            ["bailian_anthropic"] = AnthropicDefaultEndpoints,
            ["moonshot_coding"] = AnthropicDefaultEndpoints,
            ["jina"] =
            [
                Endpoint(ApiFormats.JinaRerank),
                Endpoint(ApiFormats.JinaEmbedding),
            ],
            ["github"] = OpenAICompatibleDefaultEndpoints,
            ["github_copilot"] = [Endpoint(ApiFormats.OpenAIChatCompletion)],
            ["claudecode"] = AnthropicDefaultEndpoints,
            ["cerebras"] = [Endpoint(ApiFormats.OpenAIChatCompletion)],
            ["antigravity"] = [Endpoint(ApiFormats.GeminiContents)],
            ["nanogpt"] = OpenAIFullDefaultEndpoints,
            ["nanogpt_responses"] = [Endpoint(ApiFormats.OpenAIResponse)],
            ["opencode_go"] = OpenAIChatOnlyDefaultEndpoints,
            ["opencode_go_anthropic"] = AnthropicDefaultEndpoints,
            ["ollama"] = [Endpoint(ApiFormats.OllamaChat)],
            ["ollama_anthropic"] = AnthropicDefaultEndpoints,
            ["evolink"] = OpenAICompatibleDefaultEndpoints,
            ["evolink_anthropic"] = AnthropicDefaultEndpoints,
            ["groq"] =
            [
                Endpoint(ApiFormats.OpenAIChatCompletion),
                Endpoint(ApiFormats.OpenAISpeech),
                Endpoint(ApiFormats.OpenAITranscription),
                Endpoint(ApiFormats.OpenAITranslation),
            ],
            ["commandcode"] = OpenAIChatOnlyDefaultEndpoints,
            ["commandcode_anthropic"] = AnthropicDefaultEndpoints,
            ["typesafe"] = [Endpoint(ApiFormats.TypeSafeSystemOne)],

            // Live-tree extension used by the migrated management frontend (C139).
            ["cloudflare"] =
            [
                Endpoint(ApiFormats.OpenAIChatCompletion),
                Endpoint(ApiFormats.OpenAIEmbedding),
            ],
        };

    /// <summary>
    /// Port of defaultEndpointsForChannelType (channel_endpoint.go:225-345).
    /// Unknown channel types expose no built-in endpoints.
    /// </summary>
    public static IReadOnlyList<ChannelEndpoint> DefaultsForChannelType(string channelType)
        => DefaultEndpointsByType.TryGetValue(channelType, out var endpoints)
            ? endpoints
            : [];

    /// <summary>
    /// Port of Channel.ResolveEndpoints (channel_endpoint.go:460-470): the
    /// channel's default endpoints (which may already carry the stored
    /// Responses-family intent) merged with the normalized user overrides.
    /// </summary>
    public static IReadOnlyList<ChannelEndpoint> Resolve(
        string channelType,
        string? endpointsJson,
        string? baseUrl)
        => Merge(DefaultEndpoints(channelType, endpointsJson, baseUrl), NormalizeEndpoints(ParseUser(endpointsJson), baseUrl));

    /// <summary>Port of isResponsesEndpoint (channel_endpoint_responses.go:13-15).</summary>
    public static bool IsResponsesEndpoint(string? apiFormat)
        => apiFormat == ApiFormats.OpenAIResponse
            || apiFormat == ApiFormats.OpenAIResponseWebSocket;

    /// <summary>Port of endpointTransport (channel_llm.go:288-302).</summary>
    public static string EndpointTransport(ChannelEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.ApiFormat == ApiFormats.OpenAIResponseWebSocket)
        {
            return ChannelEndpointTransports.WebSocket;
        }

        if (endpoint.Transport.Length != 0)
        {
            return endpoint.Transport;
        }

        var baseUrl = GoStrings.TrimSpace(endpoint.BaseUrl).ToLowerInvariant();
        return baseUrl.StartsWith("ws://", StringComparison.Ordinal)
            || baseUrl.StartsWith("wss://", StringComparison.Ordinal)
                ? ChannelEndpointTransports.WebSocket
                : string.Empty;
    }

    /// <summary>
    /// Port of NormalizeEndpoints (channel_endpoint_responses.go:17-49). Legacy
    /// explicit transport takes precedence over the inherited base URL scheme;
    /// only Responses-family endpoints are rewritten and the returned copies
    /// never mutate persisted input.
    /// </summary>
    public static IReadOnlyList<ChannelEndpoint> NormalizeEndpoints(
        IReadOnlyList<ChannelEndpoint>? endpoints,
        string? baseUrl)
    {
        if (endpoints is null || endpoints.Count == 0)
        {
            return [];
        }

        var normalized = new List<ChannelEndpoint>(endpoints.Count);
        foreach (var source in endpoints)
        {
            var endpoint = source;
            if (!IsResponsesEndpoint(endpoint.ApiFormat))
            {
                normalized.Add(endpoint);
                continue;
            }

            var resolvedBase = endpoint.BaseUrl.Length == 0 ? baseUrl ?? string.Empty : endpoint.BaseUrl;
            if (endpoint.ApiFormat == ApiFormats.OpenAIResponse)
            {
                var legacy = endpoint with { BaseUrl = resolvedBase };
                if (EndpointTransport(legacy) == ChannelEndpointTransports.WebSocket)
                {
                    endpoint = endpoint with { ApiFormat = ApiFormats.OpenAIResponseWebSocket };
                }
                else if (endpoint.Transport == ChannelEndpointTransports.Http)
                {
                    // Keep explicit HTTP authoritative after omitting the legacy field.
                    var trimmed = GoStrings.TrimSpace(resolvedBase);
                    var lowered = trimmed.ToLowerInvariant();
                    if (lowered.StartsWith("wss://", StringComparison.Ordinal))
                    {
                        endpoint = endpoint with { BaseUrl = "https://" + trimmed["wss://".Length..] };
                    }
                    else if (lowered.StartsWith("ws://", StringComparison.Ordinal))
                    {
                        endpoint = endpoint with { BaseUrl = "http://" + trimmed["ws://".Length..] };
                    }
                }
            }

            normalized.Add(endpoint with { Transport = string.Empty });
        }

        return normalized;
    }

    /// <summary>
    /// Port of Channel.DefaultEndpoints (channel_endpoint_responses.go:51-79).
    /// Codex's Responses-family defaults are authoritative and filtered by the
    /// stored intent; every other channel type keeps its defaults but rewrites
    /// the Responses entry to the stored Responses format.
    /// </summary>
    public static IReadOnlyList<ChannelEndpoint> DefaultEndpoints(
        string channelType,
        string? endpointsJson,
        string? baseUrl)
    {
        var defaults = DefaultsForChannelType(channelType).ToArray();
        if (channelType != CodexChannelType)
        {
            for (var index = 0; index < defaults.Length; index++)
            {
                if (defaults[index].ApiFormat == ApiFormats.OpenAIResponse)
                {
                    defaults[index] = defaults[index] with
                    {
                        ApiFormat = LegacyResponsesFormat(endpointsJson, baseUrl),
                    };
                }
            }

            return defaults;
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in NormalizeEndpoints(ParseUser(endpointsJson), baseUrl))
        {
            if (IsResponsesEndpoint(endpoint.ApiFormat))
            {
                allowed.Add(endpoint.ApiFormat);
            }
        }

        if (allowed.Count == 0)
        {
            allowed.Add(ApiFormats.OpenAIResponseWebSocket);
            if (EndpointTransport(new ChannelEndpoint { BaseUrl = baseUrl ?? string.Empty })
                != ChannelEndpointTransports.WebSocket)
            {
                allowed.Add(ApiFormats.OpenAIResponse);
            }
        }

        var filtered = new List<ChannelEndpoint>(defaults.Length);
        foreach (var endpoint in defaults)
        {
            if (!IsResponsesEndpoint(endpoint.ApiFormat) || allowed.Contains(endpoint.ApiFormat))
            {
                filtered.Add(endpoint);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Port of legacyResponsesFormat (channel_endpoint_responses.go:81-93): the
    /// stored Responses-family intent, read from the raw user endpoints before
    /// the normal merge.
    /// </summary>
    public static string LegacyResponsesFormat(string? endpointsJson, string? baseUrl)
    {
        var userEndpoints = ParseUser(endpointsJson);
        foreach (var endpoint in userEndpoints)
        {
            if (endpoint.ApiFormat == ApiFormats.OpenAIResponse)
            {
                return NormalizeEndpoints([endpoint], baseUrl)[0].ApiFormat;
            }
        }

        foreach (var endpoint in userEndpoints)
        {
            if (endpoint.ApiFormat == ApiFormats.OpenAIResponseWebSocket)
            {
                return ApiFormats.OpenAIResponse;
            }
        }

        return NormalizeEndpoints([new ChannelEndpoint { ApiFormat = ApiFormats.OpenAIResponse }], baseUrl)[0].ApiFormat;
    }

    /// <summary>
    /// Port of primaryEndpointTransport (channel_llm.go:304-329): the transport
    /// of the first raw user endpoint for the format, else a stored WebSocket
    /// Responses endpoint for the HTTP Responses format, else the channel base
    /// URL scheme.
    /// </summary>
    public static string PrimaryEndpointTransport(
        string? endpointsJson,
        string? baseUrl,
        string apiFormat)
    {
        var userEndpoints = ParseUser(endpointsJson);
        foreach (var endpoint in userEndpoints)
        {
            if (endpoint.ApiFormat != apiFormat)
            {
                continue;
            }

            var resolved = endpoint.BaseUrl.Length == 0
                ? endpoint with { BaseUrl = baseUrl ?? string.Empty }
                : endpoint;
            return EndpointTransport(resolved);
        }

        if (apiFormat == ApiFormats.OpenAIResponse)
        {
            foreach (var endpoint in userEndpoints)
            {
                if (endpoint.ApiFormat == ApiFormats.OpenAIResponseWebSocket)
                {
                    return ChannelEndpointTransports.WebSocket;
                }
            }
        }

        return EndpointTransport(new ChannelEndpoint { BaseUrl = baseUrl ?? string.Empty });
    }

    /// <summary>
    /// The normalized user endpoint that supplies outbound details for
    /// <paramref name="apiFormat"/>, or <see langword="null"/> when the format
    /// only comes from the built-in defaults. The reference builds a map keyed
    /// by format, so the last stored endpoint wins.
    /// </summary>
    public static ChannelEndpoint? FindUserEndpoint(
        string? endpointsJson,
        string? baseUrl,
        string apiFormat)
    {
        ChannelEndpoint? match = null;
        foreach (var endpoint in NormalizeEndpoints(ParseUser(endpointsJson), baseUrl))
        {
            if (endpoint.ApiFormat == apiFormat)
            {
                match = endpoint;
            }
        }

        return match;
    }

    /// <summary>
    /// Parses the raw <c>channels.endpoints</c> jsonb column. The reference's
    /// Ent scan fails the whole channel load on malformed JSON; the port's
    /// stores keep JSON columns raw, so this degrades to "no user overrides"
    /// exactly like the other port JSON readers.
    /// </summary>
    public static IReadOnlyList<ChannelEndpoint> ParseUser(string? endpointsJson)
    {
        if (string.IsNullOrWhiteSpace(endpointsJson)
            || string.Equals(endpointsJson, "null", StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            var endpoints = JsonSerializer.Deserialize<List<ChannelEndpoint?>>(
                endpointsJson,
                JsonOptions);
            if (endpoints is null)
            {
                return [];
            }

            // Go decodes a null array element as the zero struct.
            return [.. endpoints.Select(static endpoint => endpoint ?? new ChannelEndpoint())];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Port of mergeEndpoints (channel_endpoint.go:386-431): a user override
    /// replaces the built-in endpoint with the same API format in the built-in
    /// position; remaining user endpoints keep their stored order. For a format
    /// seen several times among the user endpoints the last one wins.
    /// </summary>
    public static IReadOnlyList<ChannelEndpoint> Merge(
        IReadOnlyList<ChannelEndpoint>? defaults,
        IReadOnlyList<ChannelEndpoint>? userEndpoints)
    {
        var defaultsCount = defaults?.Count ?? 0;
        var userCount = userEndpoints?.Count ?? 0;
        if (defaultsCount == 0 && userCount == 0)
        {
            return [];
        }

        // Last stored endpoint per API format wins.
        var overrides = new Dictionary<string, ChannelEndpoint>(StringComparer.Ordinal);
        for (var index = 0; index < userCount; index++)
        {
            var endpoint = userEndpoints![index];
            if (string.IsNullOrEmpty(endpoint.ApiFormat))
            {
                continue;
            }

            overrides[endpoint.ApiFormat] = endpoint;
        }

        var merged = new List<ChannelEndpoint>(defaultsCount + overrides.Count);
        for (var index = 0; index < defaultsCount; index++)
        {
            var endpoint = defaults![index];
            if (string.IsNullOrEmpty(endpoint.ApiFormat))
            {
                continue;
            }

            if (overrides.Remove(endpoint.ApiFormat, out var replacement))
            {
                merged.Add(replacement);
            }
            else
            {
                merged.Add(endpoint);
            }
        }

        // Append the user-only endpoints in stored order. Reading the surviving
        // overrides map preserves the reference's "last occurrence wins"
        // ordering: each format is appended where it last appears.
        for (var index = 0; index < userCount; index++)
        {
            var endpoint = userEndpoints![index];
            if (string.IsNullOrEmpty(endpoint.ApiFormat))
            {
                continue;
            }

            if (overrides.Remove(endpoint.ApiFormat, out var appended))
            {
                merged.Add(appended);
            }
        }

        return merged;
    }

    private static ChannelEndpoint Endpoint(string apiFormat) => new()
    {
        ApiFormat = apiFormat,
        Path = string.Empty,
        BaseUrl = string.Empty,
        Transport = string.Empty,
    };
}

/// <summary>
/// Port of llm.CapableAPIFormats (llm/api_capability.go): the outbound API
/// formats a request type may be served with, used by endpoint selection.
/// </summary>
public static class ApiCapabilities
{
    /// <summary>
    /// Returns the allowed formats for a request type, or <see langword="null"/>
    /// when the request type has no dedicated capability set.
    /// </summary>
    public static HashSet<string>? CapableApiFormats(string? requestType)
        => requestType switch
        {
            RequestTypes.Chat => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.OpenAIChatCompletion,
                ApiFormats.OpenAIResponse,
                ApiFormats.OpenAIResponseWebSocket,
                ApiFormats.AnthropicMessage,
                ApiFormats.GeminiContents,
                ApiFormats.OllamaChat,
            },
            RequestTypes.Compact => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.OpenAIResponseCompact,
            },
            RequestTypes.Completion => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.OpenAICompletion,
            },
            RequestTypes.Embedding => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.OpenAIEmbedding,
                ApiFormats.JinaEmbedding,
                ApiFormats.GeminiEmbedding,
            },
            RequestTypes.Moderation => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.OpenAIModeration,
            },
            RequestTypes.AlphaSearch => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.OpenAIAlphaSearch,
            },
            RequestTypes.Decisions => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.OpenAIDecisions,
            },
            RequestTypes.Image => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.OpenAIImageGeneration,
                ApiFormats.OpenAIImageEdit,
                ApiFormats.OpenAIImageVariation,
                ApiFormats.ModelScopeImage,
            },
            RequestTypes.Rerank => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.JinaRerank,
            },
            RequestTypes.Video => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.OpenAIVideo,
                ApiFormats.SeedanceVideo,
                ApiFormats.ZenmuxVideo,
            },
            RequestTypes.Speech => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.OpenAISpeech,
            },
            RequestTypes.Transcription => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.OpenAITranscription,
            },
            RequestTypes.Translation => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.OpenAITranslation,
            },
            RequestTypes.SystemOne => new HashSet<string>(StringComparer.Ordinal)
            {
                ApiFormats.TypeSafeSystemOne,
            },
            _ => null,
        };

    /// <summary>
    /// Port of RequestTypeForModelType (llm/api_capability.go:83-99).
    /// </summary>
    public static string RequestTypeForModelType(string? modelType)
        => modelType switch
        {
            "chat" => RequestTypes.Chat,
            "embedding" => RequestTypes.Embedding,
            "rerank" => RequestTypes.Rerank,
            "image_generation" => RequestTypes.Image,
            "video_generation" => RequestTypes.Video,
            "systemone" or "system_one" => RequestTypes.SystemOne,
            _ => string.Empty,
        };
}
