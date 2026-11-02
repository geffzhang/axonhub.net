using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Persistence;
using HotChocolate;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Domain ↔ GraphQL mapping for the channels read slice. Mirrors the gqlgen
/// resolvers under internal/server/gql plus the reference object shapes in
/// internal/objects/channel.go.
/// </summary>
public static class ChannelMapping
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Dictionary<string, ChannelTypeValue> ChannelTypesByWire =
        BuildWireMap<ChannelTypeValue>();

    private static readonly Dictionary<ChannelTypeValue, string> ChannelTypeWires =
        ChannelTypesByWire.ToDictionary(static pair => pair.Value, static pair => pair.Key);

    private static readonly Dictionary<string, ChannelStatusValue> ChannelStatusesByWire =
        BuildWireMap<ChannelStatusValue>();

    private static readonly Dictionary<ChannelStatusValue, string> ChannelStatusWires =
        ChannelStatusesByWire.ToDictionary(static pair => pair.Value, static pair => pair.Key);

    private static readonly ChannelEndpointType[] OpenAICompatibleDefaultEndpoints =
    [
        Endpoint(ApiFormats.OpenAIChatCompletion),
        Endpoint(ApiFormats.OpenAIEmbedding),
        Endpoint(ApiFormats.OpenAIImageGeneration),
        Endpoint(ApiFormats.OpenAIImageEdit),
        Endpoint(ApiFormats.OpenAIImageVariation),
        Endpoint(ApiFormats.OpenAIVideo),
        Endpoint(ApiFormats.OpenAIModeration),
    ];

    private static readonly ChannelEndpointType[] OpenAIFullDefaultEndpoints =
    [
        .. OpenAICompatibleDefaultEndpoints,
        Endpoint(ApiFormats.OpenAISpeech),
        Endpoint(ApiFormats.OpenAITranscription),
        Endpoint(ApiFormats.OpenAITranslation),
    ];

    private static readonly ChannelEndpointType[] OpenAIChatOnlyDefaultEndpoints =
    [
        Endpoint(ApiFormats.OpenAIChatCompletion),
    ];

    private static readonly ChannelEndpointType[] AnthropicDefaultEndpoints =
    [
        Endpoint(ApiFormats.AnthropicMessage),
    ];

    private static readonly ChannelEndpointType[] GeminiDefaultEndpoints =
    [
        Endpoint(ApiFormats.GeminiContents),
        Endpoint(ApiFormats.GeminiEmbedding),
    ];

    private static readonly Dictionary<string, ChannelEndpointType[]> DefaultEndpointsByType =
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

    private static readonly DateTime ZeroTime = new(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static ChannelType ToGraphQL(ChannelRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new ChannelType
        {
            DbId = record.Id,
            Id = new GidValue("Channel", record.Id).ToString(),
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            RawType = record.Type,
            Type = ToChannelTypeValue(record.Type),
            BaseUrl = record.BaseUrl,
            Name = record.Name,
            Status = ToChannelStatusValue(record.Status),
            SupportedModelsJson = record.SupportedModels,
            SupportedModels = ParseStringList(record.SupportedModels) ?? [],
            ManualModels = ParseStringList(record.ManualModels),
            AutoSyncSupportedModels = record.AutoSyncSupportedModels,
            AutoSyncModelPattern = record.AutoSyncModelPattern,
            Tags = ParseStringList(record.Tags),
            DefaultTestModel = record.DefaultTestModel,
            Policies = ToPolicies(record.Policies),
            SettingsJson = record.Settings,
            Settings = ToSettings(record.Settings),
            OrderingWeight = ToInt32(record.OrderingWeight),
            ErrorMessage = record.ErrorMessage,
            AutoDisabledAt = record.AutoDisabledAt,
            AutoDisableExpiresAt = record.AutoDisableExpiresAt,
            Remark = record.Remark,
            Endpoints = ParseEndpoints(record.Endpoints),
            CredentialsJson = record.Credentials,
            DisabledApiKeysJson = record.DisabledApiKeys,
        };
    }

    public static ChannelDescriptor ToDescriptor(ChannelRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new ChannelDescriptor
        {
            Id = record.Id,
            Name = record.Name,
            OrderingWeight = record.OrderingWeight,
            CreatedAt = record.CreatedAt,
            Type = record.Type,
            BaseUrl = record.BaseUrl,
            Credentials = record.Credentials,
            DisabledApiKeys = record.DisabledApiKeys,
            SupportedModels = record.SupportedModels,
            Settings = record.Settings,
            DefaultTestModel = record.DefaultTestModel,
        };
    }

    public static ChannelConnectionType BuildConnection(
        ListPage<ChannelRecord> page,
        ChannelSortField sortField)
    {
        ArgumentNullException.ThrowIfNull(page);

        var edges = page.Nodes
            .Select(node => new ChannelEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = ToCursor(node, sortField),
            })
            .ToList();

        return new ChannelConnectionType
        {
            Edges = edges,
            PageInfo = BuildPageInfo(edges, page.HasNextPage, page.HasPreviousPage),
            TotalCount = page.TotalCount,
        };
    }

    /// <summary>Port of biz.ChannelService.buildConnectionInMemory.</summary>
    public static ChannelConnectionType BuildModelConnection(
        IReadOnlyList<ChannelRecord> nodes,
        ChannelSortField sortField)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var edges = nodes
            .Select(node => new ChannelEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = ToCursor(node, sortField),
            })
            .ToList();

        return new ChannelConnectionType
        {
            Edges = edges,
            PageInfo = BuildPageInfo(edges, hasNextPage: false, hasPreviousPage: false),
            TotalCount = edges.Count,
        };
    }

    public static ChannelListFilter? ToFilter(ChannelWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new ChannelListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            Type = where.Type is null ? null : ToWire(where.Type.Value),
            TypeIn = where.TypeIn?
                .Select(static value => ToWire(value))
                .ToList(),
            Name = where.Name,
            NameIn = where.NameIn,
            NameContainsFold = where.NameContainsFold,
            Status = where.Status is null ? null : ToWire(where.Status.Value),
            StatusIn = where.StatusIn?
                .Select(static value => ToWire(value))
                .ToList(),
            ErrorMessageIsNil = where.ErrorMessageIsNil == true,
            ErrorMessageNotNil = where.ErrorMessageNotNil == true,
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<ChannelListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<ChannelListFilter>().ToList(),
        };
    }

    /// <summary>
    /// CREATED_AT is remapped to ent.DefaultChannelOrder before the query runs,
    /// so the generated edge cursor only carries the id (C134).
    /// </summary>
    public static ChannelSortField ToSortField(ChannelOrder? orderBy) => orderBy?.Field switch
    {
        ChannelOrderFieldValue.UpdatedAt => ChannelSortField.UpdatedAt,
        ChannelOrderFieldValue.Type => ChannelSortField.Type,
        ChannelOrderFieldValue.Name => ChannelSortField.Name,
        ChannelOrderFieldValue.Status => ChannelSortField.Status,
        ChannelOrderFieldValue.OrderingWeight => ChannelSortField.OrderingWeight,
        _ => ChannelSortField.Id,
    };

    public static SortDirection ToSortDirection(ChannelOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    /// <summary>Port of channelResolver.DefaultEndpoints.</summary>
    public static IReadOnlyList<ChannelEndpointType> DefaultEndpoints(string channelType)
        => DefaultEndpointsByType.TryGetValue(channelType, out var endpoints)
            ? endpoints
            : [];

    /// <summary>Port of channelResolver.AllModelEntries.</summary>
    public static IReadOnlyList<ChannelModelEntryType> AllModelEntries(ChannelType channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var entries = ChannelModelEntries.Build(new ChannelDescriptor
        {
            Id = channel.DbId,
            Name = channel.Name,
            OrderingWeight = channel.OrderingWeight,
            CreatedAt = channel.CreatedAt,
            Type = channel.RawType,
            BaseUrl = channel.BaseUrl,
            Credentials = channel.CredentialsJson ?? "{}",
            DisabledApiKeys = channel.DisabledApiKeysJson,
            SupportedModels = channel.SupportedModelsJson ?? "null",
            Settings = channel.SettingsJson,
            DefaultTestModel = channel.DefaultTestModel,
        });

        return
        [
            .. entries.Values
                .OrderBy(static entry => entry.RequestModel, StringComparer.Ordinal)
                .Select(static entry => new ChannelModelEntryType
                {
                    RequestModel = entry.RequestModel,
                    ActualModel = entry.ActualModel,
                    Source = entry.Source,
                }),
        ];
    }

    /// <summary>Port of the live channelResolver.Credentials.</summary>
    public static ChannelCredentialsType? ToCredentials(string? raw, string channelType)
    {
        var credentials = ParseCredentials(raw);

        if (string.Equals(channelType, "antigravity", StringComparison.Ordinal)
            || IsOAuth(credentials))
        {
            // OAuth channels expose only the single stored credential blob.
            return new ChannelCredentialsType
            {
                ApiKey = credentials.ApiKey ?? string.Empty,
                ApiKeys = null,
                VertexApiKeys = ToVertexApiKeys(credentials.VertexApiKeys),
                Gcp = ToGcpCredential(credentials.Gcp),
                OAuth = ToOAuthCredentials(credentials.OAuth),
            };
        }

        var apiKeys = new List<string>();
        if (!string.IsNullOrEmpty(credentials.ApiKey))
        {
            apiKeys.Add(credentials.ApiKey);
        }

        if (credentials.ApiKeys is not null)
        {
            apiKeys.AddRange(credentials.ApiKeys.Select(static key => key ?? string.Empty));
        }

        if (credentials.VertexApiKeys is not null)
        {
            foreach (var credential in credentials.VertexApiKeys)
            {
                if (!string.IsNullOrEmpty(credential?.ApiKey))
                {
                    apiKeys.Add(credential.ApiKey);
                }
            }
        }

        if (credentials.VertexApiKeys is { Count: > 0 })
        {
            // Vertex keys are surfaced as project-bound triples only; drop them
            // from the flat list so the edit form cannot lose the binding.
            var vertexKeys = credentials.VertexApiKeys
                .Select(static credential => credential?.ApiKey ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal);
            apiKeys = [.. apiKeys.Where(key => !vertexKeys.Contains(key))];
        }

        var vertexApiKeys = string.Equals(channelType, "gemini_vertex", StringComparison.Ordinal)
            ? ToVertexApiKeys(credentials.VertexApiKeys)
            : null;

        return new ChannelCredentialsType
        {
            ApiKey = string.Empty,
            ApiKeys = apiKeys.Count == 0 ? null : apiKeys,
            VertexApiKeys = vertexApiKeys,
            Gcp = ToGcpCredential(credentials.Gcp),
            OAuth = ToOAuthCredentials(credentials.OAuth),
        };
    }

    /// <summary>Port of channelResolver.DisabledAPIKeys (scope gate omitted, C014).</summary>
    public static IReadOnlyList<DisabledApiKeyType> ToDisabledApiKeys(string? raw)
    {
        var items = ParseDisabledApiKeys(raw);
        if (items.Count == 0)
        {
            return [];
        }

        return
        [
            .. items.Select(static item => new DisabledApiKeyType
            {
                Key = item?.Key ?? string.Empty,
                DisabledAt = ParseGoTime(item?.DisabledAt),
                ErrorCode = item?.ErrorCode ?? 0,
                Reason = item?.Reason ?? string.Empty,
                ExpiresAt = ParseNullableGoTime(item?.ExpiresAt),
            }),
        ];
    }

    /// <summary>Port of channelResolver.LiveLimiterStats.</summary>
    public static ChannelLimiterStatsType? LiveLimiterStats(
        string? settingsJson,
        long channelId,
        ChannelLimiterManager limiters)
    {
        ArgumentNullException.ThrowIfNull(limiters);

        var settings = ParseSettingsDto(settingsJson);
        var rateLimit = settings?.RateLimit;
        if (rateLimit?.MaxConcurrent is not { } maxConcurrent || maxConcurrent <= 0)
        {
            return null;
        }

        var queueSize = rateLimit.QueueSize is { } queue && queue > 0
            ? ToInt32(queue)
            : 0;

        _ = limiters.TryGetStats(channelId, out var inFlight, out var waiting);

        return new ChannelLimiterStatsType
        {
            InFlight = inFlight,
            Waiting = waiting,
            Capacity = ToInt32(maxConcurrent),
            QueueSize = queueSize,
        };
    }

    public static ChannelTypeValue ToChannelTypeValue(string value)
    {
        if (ChannelTypesByWire.TryGetValue(value, out var type))
        {
            return type;
        }

        throw new GraphQLException(
            "Enum \"ChannelType\" cannot represent value: \"" + value + "\"");
    }

    public static ChannelStatusValue ToChannelStatusValue(string value)
    {
        if (ChannelStatusesByWire.TryGetValue(value, out var status))
        {
            return status;
        }

        throw new GraphQLException(
            "Enum \"ChannelStatus\" cannot represent value: \"" + value + "\"");
    }

    /// <summary>
    /// Port of channel.TypeValidator's membership half (channel.go:287-293):
    /// true when the wire spelling names a known channel type. Bulk create and
    /// bulk import receive the type as a plain string and must reject unknown
    /// values with the ent validator text.
    /// </summary>
    public static bool IsKnownChannelType(string value)
        => ChannelTypesByWire.ContainsKey(value);

    public static string ToWire(ChannelTypeValue value) => ChannelTypeWires[value];

    public static string ToWire(ChannelStatusValue value) => ChannelStatusWires[value];

    private static ChannelEndpointType Endpoint(string apiFormat) => new()
    {
        ApiFormat = apiFormat,
        Path = string.Empty,
        BaseUrl = string.Empty,
        Transport = string.Empty,
    };

    private static CursorValue ToCursor(ChannelRecord record, ChannelSortField sortField) => new(
        record.Id,
        sortField switch
        {
            ChannelSortField.UpdatedAt => record.UpdatedAt,
            ChannelSortField.Type => record.Type,
            ChannelSortField.Name => record.Name,
            ChannelSortField.Status => record.Status,
            ChannelSortField.OrderingWeight => record.OrderingWeight,
            _ => null,
        });

    private static PageInfoType BuildPageInfo(
        List<ChannelEdgeType> edges,
        bool hasNextPage,
        bool hasPreviousPage) => new()
        {
            HasNextPage = hasNextPage,
            HasPreviousPage = hasPreviousPage,
            StartCursor = edges.Count > 0 ? edges[0].Cursor : null,
            EndCursor = edges.Count > 0 ? edges[^1].Cursor : null,
        };

    private static ChannelPoliciesType? ToPolicies(string? raw)
    {
        var policies = ParsePoliciesDto(raw);
        if (policies is null)
        {
            return null;
        }

        return new ChannelPoliciesType
        {
            Stream = ToCapabilityPolicy(policies.Stream),
            ApiKeyAutoDisableMode = ToApiKeyAutoDisableMode(policies.ApiKeyAutoDisableMode),
            ApiKeyAutoDisableRules = policies.ApiKeyAutoDisableRules?
                .Select(static rule => new ApiKeyAutoDisableRuleType
                {
                    StatusCodes = rule?.StatusCodes?.Select(static code => code ?? 0).ToList(),
                    KeywordPatterns = rule?.KeywordPatterns?
                        .Select(static pattern => pattern ?? string.Empty)
                        .ToList(),
                    Times = rule?.Times ?? 0,
                    Action = ToApiKeyAutoDisableAction(rule?.Action),
                    DisableDurationMinutes = ToNullableInt32(rule?.DisableDurationMinutes),
                    DisableUntilCron = rule?.DisableUntilCron ?? string.Empty,
                    DisableUntilTimezone = rule?.DisableUntilTimezone ?? string.Empty,
                })
                .ToList(),
        };
    }

    private static ChannelSettingsType? ToSettings(string? raw)
    {
        var settings = ParseSettingsDto(raw);
        if (settings is null)
        {
            return null;
        }

        var overrides = ChannelOverrideSettings.Parse(raw);

        return new ChannelSettingsType
        {
            ExtraModelPrefix = settings.ExtraModelPrefix ?? string.Empty,
            ModelMappings = settings.ModelMappings?
                .Select(static mapping => new ModelMappingType
                {
                    From = mapping?.From ?? string.Empty,
                    To = mapping?.To ?? string.Empty,
                })
                .ToList(),
            AutoTrimedModelPrefixes = settings.AutoTrimedModelPrefixes?
                .Select(static prefix => prefix ?? string.Empty)
                .ToList(),
            HideOriginalModels = settings.HideOriginalModels ?? false,
            HideMappedModels = settings.HideMappedModels ?? false,
            LowercaseModelId = settings.LowercaseModelId ?? false,
            Proxy = ToProxy(settings.Proxy),
            TransformOptions = new TransformOptionsType
            {
                ForceArrayInstructions = settings.TransformOptions?.ForceArrayInstructions ?? false,
                ForceArrayInputs = settings.TransformOptions?.ForceArrayInputs ?? false,
                ReplaceDeveloperRoleWithSystem =
                    settings.TransformOptions?.ReplaceDeveloperRoleWithSystem ?? false,
                ReasoningEffortMapping = settings.TransformOptions?.ReasoningEffortMapping?
                    .Select(static mapping => new ReasoningEffortMappingType
                    {
                        From = mapping?.From ?? string.Empty,
                        To = mapping?.To ?? string.Empty,
                    })
                    .ToList(),
            },
            HeaderOverrideOperations = ToOverrideOperations(overrides.HeaderOperations),
            BodyOverrideOperations = ToOverrideOperations(overrides.BodyOperations),
            PassThroughUserAgent = settings.PassThroughUserAgent,
            PassThroughBody = settings.PassThroughBody,
            RateLimit = ToRateLimit(settings.RateLimit),
            RetryableStatusCodes = settings.RetryableStatusCodes?
                .Select(static code => code ?? 0)
                .ToList(),
            RetryableErrorPatterns = settings.RetryableErrorPatterns?
                .Select(static pattern => new RetryableErrorPatternType
                {
                    Pattern = pattern?.Pattern ?? string.Empty,
                    Regex = pattern?.Regex ?? false,
                })
                .ToList(),
            MaxSingleChannelRetries = ToNullableInt32(settings.MaxSingleChannelRetries),
            ResponseTimeout = ToResponseTimeout(settings.ResponseTimeout),
            ModelProtocols = settings.ModelProtocols?
                .Select(static protocol => new ModelProtocolType
                {
                    Model = protocol?.Model ?? string.Empty,
                    ApiFormats = protocol?.ApiFormats?
                        .Select(static format => format ?? string.Empty)
                        .ToList() ?? [],
                    Enabled = protocol?.Enabled,
                })
                .ToList(),
            ApiKeyStrategy = settings.ApiKeyStrategy,
            ApiKeyRoundRobinSwitchAfter =
                ToNullableInt32(settings.ApiKeyRoundRobinSwitchAfter),
            ProviderQuota = ToProviderQuota(settings.ProviderQuota),
            QuotaRoutingMode = ToQuotaRoutingMode(settings.QuotaRoutingMode),
        };
    }

    private static ProxyConfigType? ToProxy(ProxyDto? proxy)
    {
        if (proxy is null)
        {
            return null;
        }

        var type = proxy.Type ?? string.Empty;
        var value = type switch
        {
            "disabled" => ProxyTypeValue.Disabled,
            "environment" => ProxyTypeValue.Environment,
            "url" => ProxyTypeValue.Url,
            _ => throw new GraphQLException(
                "Enum \"ProxyType\" cannot represent value: \"" + type + "\""),
        };

        return new ProxyConfigType
        {
            Type = value,
            Url = proxy.Url ?? string.Empty,
            Username = proxy.Username ?? string.Empty,
            Password = proxy.Password ?? string.Empty,
            DisableConnectionReuse = proxy.DisableConnectionReuse ?? false,
        };
    }

    private static ChannelRateLimitType? ToRateLimit(RateLimitDto? rateLimit) => rateLimit is null
        ? null
        : new ChannelRateLimitType
        {
            Rpm = ToNullableInt32(rateLimit.Rpm),
            Tpm = ToNullableInt32(rateLimit.Tpm),
            MaxConcurrent = ToNullableInt32(rateLimit.MaxConcurrent),
            QueueSize = ToNullableInt32(rateLimit.QueueSize),
            QueueTimeoutMs = ToNullableInt32(rateLimit.QueueTimeoutMs),
        };

    private static ChannelResponseTimeoutType? ToResponseTimeout(ResponseTimeoutDto? timeout)
        => timeout is null
            ? null
            : new ChannelResponseTimeoutType
            {
                // The zero value marshals as INHERIT; any non-custom value does too.
                Mode = string.Equals(timeout.Mode, "custom", StringComparison.Ordinal)
                    ? ChannelResponseTimeoutModeValue.Custom
                    : ChannelResponseTimeoutModeValue.Inherit,
                StreamFirstEventTimeoutSeconds =
                    ToNullableInt32(timeout.StreamFirstEventTimeoutSeconds),
                NonStreamResponseTimeoutSeconds =
                    ToNullableInt32(timeout.NonStreamResponseTimeoutSeconds),
            };

    private static ChannelProviderQuotaSettingsType? ToProviderQuota(ProviderQuotaDto? quota)
        => quota is null
            ? null
            : new ChannelProviderQuotaSettingsType
            {
                CommandCode = quota.CommandCode is null
                    ? null
                    : new CommandCodeQuotaSettingsType
                    {
                        AuthCookie = quota.CommandCode.AuthCookie ?? string.Empty,
                    },
                Ollama = quota.Ollama is null
                    ? null
                    : new OllamaQuotaSettingsType
                    {
                        AuthCookie = quota.Ollama.AuthCookie ?? string.Empty,
                    },
            };

    private static IReadOnlyList<OverrideOperationType> ToOverrideOperations(
        IReadOnlyList<ChannelOverrideOperation> operations) =>
    [
        .. operations.Select(static operation => new OverrideOperationType
        {
            Op = operation.Op,
            Path = operation.Path,
            From = operation.From,
            To = operation.To,
            Value = operation.Value,
            Condition = operation.Condition,
            Match = operation.Match is null
                ? null
                : new OverrideMatchType
                {
                    Path = operation.Match.Path,
                    Eq = operation.Match.Eq,
                },
            Index = ToNullableInt32(operation.Index),
            Splat = operation.Splat,
        }),
    ];

    private static CapabilityPolicyValue ToCapabilityPolicy(string? value) => value switch
    {
        null or "" or "unlimited" => CapabilityPolicyValue.Unlimited,
        "require" => CapabilityPolicyValue.Require,
        "forbid" => CapabilityPolicyValue.Forbid,
        _ => throw new GraphQLException(
            "Enum \"CapabilityPolicy\" cannot represent value: \"" + value + "\""),
    };

    private static ApiKeyAutoDisableModeValue? ToApiKeyAutoDisableMode(string? value) => value switch
    {
        null or "" => null,
        "inherit" => ApiKeyAutoDisableModeValue.Inherit,
        "custom" => ApiKeyAutoDisableModeValue.Custom,
        "off" => ApiKeyAutoDisableModeValue.Off,
        _ => throw new GraphQLException(
            "Enum \"APIKeyAutoDisableMode\" cannot represent value: \"" + value + "\""),
    };

    private static ApiKeyAutoDisableActionValue ToApiKeyAutoDisableAction(string? value) => value switch
    {
        "temporary_disable" => ApiKeyAutoDisableActionValue.TemporaryDisable,
        "disable_until_cron" => ApiKeyAutoDisableActionValue.DisableUntilCron,
        "permanent_disable" => ApiKeyAutoDisableActionValue.PermanentDisable,
        "permanent_disable_delete" => ApiKeyAutoDisableActionValue.PermanentDisableDelete,
        _ => throw new GraphQLException(
            "Enum \"APIKeyAutoDisableAction\" cannot represent value: \""
            + (value ?? string.Empty) + "\""),
    };

    private static ChannelQuotaRoutingModeValue ToQuotaRoutingMode(string? value) => value switch
    {
        null or "" or "inherit" => ChannelQuotaRoutingModeValue.Inherit,
        "ignore_quota" => ChannelQuotaRoutingModeValue.IgnoreQuota,
        "remove_on_exhausted" => ChannelQuotaRoutingModeValue.RemoveOnExhausted,
        "backpressure" => ChannelQuotaRoutingModeValue.Backpressure,
        // The reference collapses unknown saved modes to REMOVE_ON_EXHAUSTED.
        _ => ChannelQuotaRoutingModeValue.RemoveOnExhausted,
    };

    private static List<string>? ParseStringList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var values = JsonSerializer.Deserialize<string?[]>(raw, JsonOptions);
            return values?
                .Select(static value => value ?? string.Empty)
                .ToList();
        }
        catch (JsonException)
        {
            // Ent would reject the row during scan; the port degrades to null (C141).
            return null;
        }
    }

    private static List<ChannelEndpointType>? ParseEndpoints(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var endpoints = JsonSerializer.Deserialize<List<EndpointDto?>>(raw, JsonOptions);
            return endpoints?
                .Select(static endpoint => new ChannelEndpointType
                {
                    ApiFormat = endpoint?.ApiFormat ?? string.Empty,
                    Path = endpoint?.Path ?? string.Empty,
                    BaseUrl = endpoint?.BaseUrl ?? string.Empty,
                    Transport = endpoint?.Transport ?? string.Empty,
                })
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChannelPoliciesDto? ParsePoliciesDto(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return new ChannelPoliciesDto();
        }

        try
        {
            return JsonSerializer.Deserialize<ChannelPoliciesDto>(raw, JsonOptions)
                ?? new ChannelPoliciesDto();
        }
        catch (JsonException)
        {
            return new ChannelPoliciesDto();
        }
    }

    private static ChannelSettingsDto? ParseSettingsDto(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ChannelSettingsDto>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChannelCredentialsDto ParseCredentials(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return new ChannelCredentialsDto();
        }

        try
        {
            return JsonSerializer.Deserialize<ChannelCredentialsDto>(raw, JsonOptions)
                ?? new ChannelCredentialsDto();
        }
        catch (JsonException)
        {
            return new ChannelCredentialsDto();
        }
    }

    private static List<DisabledApiKeyDto?> ParseDisabledApiKeys(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<DisabledApiKeyDto?>>(raw, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsOAuth(ChannelCredentialsDto credentials)
    {
        if (credentials.OAuth is not null
            && !string.IsNullOrEmpty(credentials.OAuth.AccessToken))
        {
            return true;
        }

        // Backward compatibility: legacy channels store the OAuth JSON in apiKey.
        var apiKey = (credentials.ApiKey ?? string.Empty).Trim();
        return apiKey.StartsWith('{')
            && apiKey.Contains("access_token", StringComparison.Ordinal);
    }

    private static List<VertexApiKeyType>? ToVertexApiKeys(
        List<VertexApiKeyDto?>? vertexApiKeys)
        => vertexApiKeys?
            .Select(static credential => new VertexApiKeyType
            {
                ProjectId = credential?.ProjectId ?? string.Empty,
                ApiKey = credential?.ApiKey ?? string.Empty,
                Region = credential?.Region ?? string.Empty,
            })
            .ToList();

    private static GcpCredentialType? ToGcpCredential(GcpDto? gcp) => gcp is null
        ? null
        : new GcpCredentialType
        {
            Region = gcp.Region ?? string.Empty,
            ProjectId = gcp.ProjectId ?? string.Empty,
            JsonData = gcp.JsonData ?? string.Empty,
        };

    private static OAuthCredentialsType? ToOAuthCredentials(OAuthDto? oauth) => oauth is null
        ? null
        : new OAuthCredentialsType
        {
            AccessToken = oauth.AccessToken ?? string.Empty,
            RefreshToken = oauth.RefreshToken ?? string.Empty,
            ClientId = oauth.ClientId ?? string.Empty,
            // Go's time.Time is not a pointer: a missing/invalid value is the
            // zero time, which the Time scalar renders as 0001-01-01T00:00:00Z.
            ExpiresAt = ParseGoTime(oauth.ExpiresAt),
            TokenType = oauth.TokenType ?? string.Empty,
            Scopes = oauth.Scopes?
                .Select(static scope => scope ?? string.Empty)
                .ToList(),
        };

    private static DateTime ParseGoTime(string? text)
    {
        if (string.IsNullOrEmpty(text)
            || !DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return ZeroTime;
        }

        return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
    }

    private static DateTime? ParseNullableGoTime(string? text)
    {
        if (string.IsNullOrEmpty(text)
            || !DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return null;
        }

        return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
    }

    private static int ToInt32(long value)
        => value > int.MaxValue
            ? int.MaxValue
            : value < int.MinValue ? int.MinValue : (int)value;

    private static int? ToNullableInt32(long? value)
        => value is null ? null : ToInt32(value.Value);

    private static Dictionary<string, TEnum> BuildWireMap<TEnum>()
        where TEnum : struct, Enum
    {
        var map = new Dictionary<string, TEnum>(StringComparer.Ordinal);
        foreach (var value in Enum.GetValues<TEnum>())
        {
            var name = typeof(TEnum)
                .GetField(value.ToString())?
                .GetCustomAttribute<GraphQLNameAttribute>()?
                .Name;

            if (name is null)
            {
                throw new InvalidOperationException(
                    "Enum member " + typeof(TEnum).Name + "." + value + " has no GraphQLName");
            }

            map.Add(name, value);
        }

        return map;
    }

    private sealed class ChannelSettingsDto
    {
        public string? ExtraModelPrefix { get; set; }

        public List<ModelMappingDto?>? ModelMappings { get; set; }

        public List<string?>? AutoTrimedModelPrefixes { get; set; }

        public bool? HideOriginalModels { get; set; }

        public bool? HideMappedModels { get; set; }

        public bool? LowercaseModelId { get; set; }

        public ProxyDto? Proxy { get; set; }

        public TransformOptionsDto? TransformOptions { get; set; }

        public bool? PassThroughUserAgent { get; set; }

        public bool? PassThroughBody { get; set; }

        public RateLimitDto? RateLimit { get; set; }

        public List<int?>? RetryableStatusCodes { get; set; }

        public List<RetryableErrorPatternDto?>? RetryableErrorPatterns { get; set; }

        public int? MaxSingleChannelRetries { get; set; }

        public ResponseTimeoutDto? ResponseTimeout { get; set; }

        public List<ModelProtocolDto?>? ModelProtocols { get; set; }

        public string? ApiKeyStrategy { get; set; }

        public int? ApiKeyRoundRobinSwitchAfter { get; set; }

        public ProviderQuotaDto? ProviderQuota { get; set; }

        public string? QuotaRoutingMode { get; set; }
    }

    private sealed class ModelMappingDto
    {
        public string? From { get; set; }

        public string? To { get; set; }
    }

    private sealed class TransformOptionsDto
    {
        public bool? ForceArrayInstructions { get; set; }

        public bool? ForceArrayInputs { get; set; }

        public bool? ReplaceDeveloperRoleWithSystem { get; set; }

        public List<ModelMappingDto?>? ReasoningEffortMapping { get; set; }
    }

    private sealed class ProxyDto
    {
        public string? Type { get; set; }

        public string? Url { get; set; }

        public string? Username { get; set; }

        public string? Password { get; set; }

        public bool? DisableConnectionReuse { get; set; }
    }

    private sealed class RateLimitDto
    {
        public long? Rpm { get; set; }

        public long? Tpm { get; set; }

        public long? MaxConcurrent { get; set; }

        public long? QueueSize { get; set; }

        public long? QueueTimeoutMs { get; set; }
    }

    private sealed class RetryableErrorPatternDto
    {
        public string? Pattern { get; set; }

        public bool? Regex { get; set; }
    }

    private sealed class ResponseTimeoutDto
    {
        public string? Mode { get; set; }

        public int? StreamFirstEventTimeoutSeconds { get; set; }

        public int? NonStreamResponseTimeoutSeconds { get; set; }
    }

    private sealed class ModelProtocolDto
    {
        public string? Model { get; set; }

        public List<string?>? ApiFormats { get; set; }

        public bool? Enabled { get; set; }
    }

    private sealed class ProviderQuotaDto
    {
        public CommandCodeQuotaDto? CommandCode { get; set; }

        public OllamaQuotaDto? Ollama { get; set; }
    }

    private sealed class CommandCodeQuotaDto
    {
        public string? AuthCookie { get; set; }
    }

    private sealed class OllamaQuotaDto
    {
        public string? AuthCookie { get; set; }
    }

    private sealed class ChannelPoliciesDto
    {
        public string? Stream { get; set; }

        public string? ApiKeyAutoDisableMode { get; set; }

        public List<ApiKeyAutoDisableRuleDto?>? ApiKeyAutoDisableRules { get; set; }
    }

    private sealed class ApiKeyAutoDisableRuleDto
    {
        public List<int?>? StatusCodes { get; set; }

        public List<string?>? KeywordPatterns { get; set; }

        public int Times { get; set; }

        public string? Action { get; set; }

        public int? DisableDurationMinutes { get; set; }

        public string? DisableUntilCron { get; set; }

        public string? DisableUntilTimezone { get; set; }
    }

    private sealed class ChannelCredentialsDto
    {
        public string? ApiKey { get; set; }

        public List<string?>? ApiKeys { get; set; }

        public List<VertexApiKeyDto?>? VertexApiKeys { get; set; }

        public GcpDto? Gcp { get; set; }

        public OAuthDto? OAuth { get; set; }
    }

    private sealed class VertexApiKeyDto
    {
        public string? ProjectId { get; set; }

        public string? ApiKey { get; set; }

        public string? Region { get; set; }
    }

    private sealed class GcpDto
    {
        public string? Region { get; set; }

        public string? ProjectId { get; set; }

        public string? JsonData { get; set; }
    }

    private sealed class OAuthDto
    {
        public string? AccessToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("client_id")]
        public string? ClientId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        public List<string?>? Scopes { get; set; }
    }

    private sealed class DisabledApiKeyDto
    {
        public string? Key { get; set; }

        public string? DisabledAt { get; set; }

        public int? ErrorCode { get; set; }

        public string? Reason { get; set; }

        public string? ExpiresAt { get; set; }
    }

    private sealed class EndpointDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("api_format")]
        public string? ApiFormat { get; set; }

        public string? Path { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("base_url")]
        public string? BaseUrl { get; set; }

        public string? Transport { get; set; }
    }
}
