using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// Port of Channel.ResolveEndpoints: built-in defaults merged with the
    /// stored user overrides (channel_endpoint.go:440-446).
    /// </summary>
    public static IReadOnlyList<ChannelEndpoint> Resolve(
        string channelType,
        string? endpointsJson)
        => Merge(DefaultsForChannelType(channelType), ParseUser(endpointsJson));

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
