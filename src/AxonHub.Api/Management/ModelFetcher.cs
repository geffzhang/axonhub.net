using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AxonHub.Abstractions;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Antigravity;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;
using AxonHub.Llm.Transformer.Cline;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.OpenAI.Copilot;
using AxonHub.Llm.Transformer.Xai.Subscription;
using AxonHub.Persistence;
using GoJson = AxonHub.Llm.Json.GoJson;

namespace AxonHub.Api.Management;

/// <summary>Port of biz.ModelIdentify (model.go:935-937).</summary>
public sealed class ModelIdentify
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

/// <summary>Port of biz.FetchModelsInput (model_fetcher.go:161-167).</summary>
public sealed class FetchModelsInput
{
    public string ChannelType { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key override; null means "use the saved channel credentials".</summary>
    public string? ApiKey { get; set; }

    public long? ChannelId { get; set; }
}

/// <summary>Port of biz.FetchModelsResult (model_fetcher.go:169-174).</summary>
public sealed class FetchModelsResult
{
    public IReadOnlyList<ModelIdentify> Models { get; init; } = [];

    public string? Error { get; init; }

    public bool Fallback { get; init; }
}

/// <summary>
/// Port of biz.ModelFetcher (internal/server/biz/model_fetcher.go and
/// model_fetcher_antigravity.go): provider model discovery for the channel
/// editor and the periodic model sync.
/// </summary>
/// <remarks>
/// The reference builds a new fetcher per sync channel, so the provider-conf
/// caches live on the instance. Per-channel proxies and the HTTPS-downgrade
/// guard are not ported (C158); the live-only Vertex binding and third-party
/// Codex catalog branches are not ported (C158).
/// </remarks>
public sealed class ModelFetcher
{
    private static readonly TimeSpan ProviderConfCacheDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan CodexModelsTimeout = TimeSpan.FromSeconds(15);

    private static readonly HashSet<string> ValidChannelTypes = new(StringComparer.Ordinal)
    {
        "openai", "openai_responses", "atlascloud", "cline", "codex", "vercel",
        "anthropic", "anthropic_aws", "anthropic_gcp", "gemini_openai", "gemini",
        "gemini_vertex", "deepseek", "deepseek_anthropic", "deepinfra", "qiniu",
        "fireworks", "doubao", "doubao_anthropic", "moonshot", "moonshot_anthropic",
        "zhipu", "zai", "zhipu_anthropic", "zai_anthropic", "anthropic_fake",
        "openai_fake", "openrouter", "xiaomi", "xiaomi_anthropic", "xai",
        "xai_responses", "xai_subscription", "ppio", "siliconflow", "volcengine",
        "volcengine_anthropic", "longcat", "longcat_anthropic", "minimax",
        "minimax_anthropic", "aihubmix", "aihubmix_anthropic", "burncloud",
        "modelscope", "bailian", "bailian_responses", "bailian_anthropic",
        "moonshot_coding", "jina", "github", "github_copilot", "claudecode",
        "cerebras", "antigravity", "nanogpt", "nanogpt_responses", "opencode_go",
        "opencode_go_anthropic", "ollama", "ollama_anthropic", "evolink",
        "evolink_anthropic", "groq", "qiniu_anthropic", "fenno", "zenmux",
        "zenmux_responses", "zenmux_anthropic", "zenmux_gemini", "zenmux_video",
        "commandcode", "commandcode_anthropic", "typesafe",
    };

    private static readonly Regex JsonArrayRegex =
        new(@"\[[^\]]*\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IExecutor _executor;
    private readonly IChannelModelCatalogStore _channels;
    private readonly TimeProvider _time;
    private readonly ProviderConfFetcher _copilotFetcher;
    private readonly ProviderConfFetcher _geminiVertexFetcher;

    public ModelFetcher(
        IExecutor executor,
        IChannelModelCatalogStore channels,
        TimeProvider time)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _copilotFetcher = new ProviderConfFetcher(
            CopilotConstants.ProviderConfUrl,
            ProviderConfCacheDuration,
            executor,
            time);
        _geminiVertexFetcher = new ProviderConfFetcher(
            GeminiVertexConstants.ProviderConfURL,
            ProviderConfCacheDuration,
            executor,
            time);
    }

    /// <summary>
    /// Cline's discovery endpoint. The reference tests replace the private field;
    /// tests here do the same through the internal setter.
    /// </summary>
    internal string ClineRecommendedModelsUrl { get; set; } = ClineConstants.RecommendedModelsUrl;

    /// <summary>Port of ModelFetcher.FetchModels (model_fetcher.go:360-612).</summary>
    public async Task<FetchModelsResult> FetchModelsAsync(
        FetchModelsInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var channelType = input.ChannelType ?? string.Empty;
        var baseUrl = input.BaseUrl ?? string.Empty;

        if (channelType == "antigravity")
        {
            try
            {
                var models = await FetchAntigravityModelsAsync(input, cancellationToken)
                    .ConfigureAwait(false);
                return new FetchModelsResult { Models = models };
            }
            catch (Exception ex)
            {
                // The reference converts every discovery failure into result data.
                return new FetchModelsResult { Models = [], Error = ex.Message };
            }
        }

        if (channelType == "volcengine")
        {
            return new FetchModelsResult { Models = [] };
        }

        if (channelType == "cline")
        {
            if (input.ChannelId is { } clineChannelId)
            {
                var (clineChannel, clineError) = await LoadChannelAsync(clineChannelId, cancellationToken)
                    .ConfigureAwait(false);
                if (clineError is not null)
                {
                    return new FetchModelsResult { Models = [], Error = clineError };
                }
            }

            var (clineModels, clineFallback) = FetchClineRecommendedModels(cancellationToken);
            return new FetchModelsResult { Models = clineModels, Fallback = clineFallback };
        }

        if (TryReturnDefaultModels(channelType, cancellationToken) is { } defaultResult)
        {
            return defaultResult;
        }

        var apiKey = input.ApiKey is { Length: > 0 } keyOverride ? keyOverride : string.Empty;
        ChannelRecord? codexChannel = null;
        List<OverrideOperationDraft>? headerOverrideOps = null;

        if (input.ChannelId is { } channelId)
        {
            var (channel, loadError) = await LoadChannelAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
            if (loadError is not null)
            {
                return new FetchModelsResult { Models = [], Error = loadError };
            }

            ChannelCredentialsDraft credentials;
            ChannelSettingsDraft? settings;
            try
            {
                credentials = ChannelWriteSerialization.ParseCredentials(channel!.Credentials);
                settings = ChannelWriteSerialization.ParseSettings(channel.Settings);
            }
            catch (JsonException ex)
            {
                // ent would have failed the whole channel load.
                return new FetchModelsResult { Models = [], Error = "failed to get channel: " + ex.Message };
            }

            if (ChannelCredentials.IsOAuth(channel.Credentials)
                && (channel.Type != "codex"
                    || (apiKey.Length == 0 && InputMatches(input, channel))))
            {
                if (channel.Type == "codex" && CodexModels.SupportsModelCatalog(channel.BaseUrl))
                {
                    codexChannel = channel;
                }
                else if (GetDefaultModelsByType(channel.Type, cancellationToken) is { } models)
                {
                    return new FetchModelsResult { Models = models };
                }
            }

            if (apiKey.Length == 0)
            {
                if (!InputMatches(input, channel))
                {
                    return new FetchModelsResult
                    {
                        Models = [],
                        Error = "API key is required when channel type or base URL is changed",
                    };
                }

                apiKey = credentials.ApiKey ?? string.Empty;
                if (apiKey.Length == 0 && credentials.ApiKeys is { Count: > 0 } apiKeys)
                {
                    apiKey = apiKeys[0];
                }

                channelType = channel.Type;
                baseUrl = channel.BaseUrl ?? string.Empty;
            }

            if (settings is not null)
            {
                // The new schema field takes precedence; the legacy
                // OverrideHeaders list is converted only when it is absent.
                headerOverrideOps = HasHeaderOverrideOperations(channel.Settings)
                    ? [.. settings.HeaderOverrideOperations ?? []]
                    : HeaderEntriesToOverrideOperations(settings.OverrideHeaders);
            }
        }

        if (apiKey.Length == 0 && codexChannel is null)
        {
            if (IsQiniuChannelType(channelType))
            {
                return new FetchModelsResult { Models = QiniuFallbackModels() };
            }

            if (IsOfficialOnlyType(channelType)
                && GetDefaultModelsByType(channelType, cancellationToken) is { } officialModels)
            {
                return new FetchModelsResult { Models = officialModels };
            }

            return new FetchModelsResult { Models = [], Error = "API key is required" };
        }

        if (codexChannel is null && IsOAuthJson(apiKey))
        {
            if (channelType == "codex" && CodexModels.SupportsModelCatalog(baseUrl))
            {
                codexChannel = new ChannelRecord
                {
                    Type = channelType,
                    BaseUrl = baseUrl,
                    Credentials = GoJson.MarshalToString(
                        new ChannelCredentialsDraft { ApiKey = apiKey }),
                };
            }
            else if (GetDefaultModelsByType(channelType, cancellationToken) is { } oauthModels)
            {
                return new FetchModelsResult { Models = oauthModels };
            }
        }

        if (!ValidChannelTypes.Contains(channelType))
        {
            return new FetchModelsResult
            {
                Models = [],
                Error = "invalid channel type: channel: invalid enum value for type field: "
                    + GoCast.GoQuote(channelType),
            };
        }

        if (IsCommandCodeChannelType(channelType))
        {
            var baseUrlError = ChannelValidation.ValidateCommandCodeBaseUrl(baseUrl);
            if (baseUrlError is not null)
            {
                return new FetchModelsResult { Models = [], Error = baseUrlError };
            }
        }

        var (modelsUrl, authHeaders) = PrepareModelsEndpoint(channelType, baseUrl);

        // GitHub Copilot uses cached provider conf instead of an API endpoint.
        if (channelType == "github_copilot")
        {
            var copilotModels = FetchCopilotModels(cancellationToken);
            return copilotModels is null
                ? new FetchModelsResult { Models = [], Error = "failed to fetch copilot models" }
                : new FetchModelsResult { Models = copilotModels };
        }

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = modelsUrl,
            Headers = authHeaders,
        };

        // model_fetcher.go:542-548: the channel proxy (still unported, C158) is
        // followed by a client that refuses HTTPS-to-HTTP redirects, so the API
        // key is never replayed over cleartext by a Command Code or Codex
        // catalog request.
        var executor = _executor;
        if (IsCommandCodeChannelType(channelType) || codexChannel is not null)
        {
            executor = executor.WithRejectHttpsDowngrade();
        }

        Func<byte[], List<ModelIdentify>> parse = ParseModelsResponse;
        CancellationTokenSource? codexTimeout = null;
        var effectiveToken = cancellationToken;

        // The Anthropic retry mutates the same request object, like the
        // reference's closure over req.
        void RetryWithBearerAuth()
        {
            HttpClientUtils.HeaderDel(request.Headers!, "X-Api-Key");
            HttpClientUtils.HeaderSet(request.Headers!, "Authorization", "Bearer " + apiKey);
        }

        try
        {
            if (codexChannel is not null)
            {
                codexTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                codexTimeout.CancelAfter(CodexModelsTimeout);
                effectiveToken = codexTimeout.Token;

                try
                {
                    request = PrepareCodexModelsRequest(effectiveToken, codexChannel, executor);
                }
                catch (Exception)
                {
                    return CodexFallbackModels();
                }

                parse = ParseCodexModels;
            }
            else if (IsCommandCodeChannelType(channelType))
            {
                // Command Code authenticates with a Bearer API key, never X-Api-Key.
                HttpClientUtils.HeaderSet(request.Headers!, "Authorization", "Bearer " + apiKey);
            }
            else if (UsesAnthropicModelApi(channelType))
            {
                HttpClientUtils.HeaderSet(request.Headers!, "X-Api-Key", apiKey);
            }
            else if (IsGemini(channelType))
            {
                HttpClientUtils.HeaderSet(request.Headers!, "X-Goog-Api-Key", apiKey);
            }
            else
            {
                HttpClientUtils.HeaderSet(request.Headers!, "Authorization", "Bearer " + apiKey);
            }

            request.Headers ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);

            // Channel header overrides win over the standard auth headers,
            // matching the chat/completion path.
            ChannelOverrideApplier.ApplyModelFetchHeaderOverrides(request.Headers, headerOverrideOps);

            if (IsGemini(channelType))
            {
                try
                {
                    var geminiModels = FetchGeminiModels(effectiveToken, request, executor);
                    return new FetchModelsResult { Models = Uniq(geminiModels) };
                }
                catch (Exception ex)
                {
                    return new FetchModelsResult
                    {
                        Models = [],
                        Error = "failed to fetch models: " + ex.Message,
                    };
                }
            }

            Func<HttpResponseData> execute = () => executor.Do(effectiveToken, request);
            if (UsesAnthropicModelApi(channelType) && !IsCommandCodeChannelType(channelType))
            {
                execute = () =>
                {
                    HttpResponseData response;
                    try
                    {
                        response = executor.Do(effectiveToken, request);
                    }
                    catch (Exception)
                    {
                        RetryWithBearerAuth();
                        return executor.Do(effectiveToken, request);
                    }

                    if (response.StatusCode != 200)
                    {
                        RetryWithBearerAuth();
                        return executor.Do(effectiveToken, request);
                    }

                    return response;
                };
            }

            List<ModelIdentify> models;
            try
            {
                models = FetchModelsViaRequest(execute, parse);
            }
            catch (Exception ex)
            {
                if (codexChannel is not null)
                {
                    return CodexFallbackModels();
                }

                if (IsQiniuChannelType(channelType))
                {
                    return new FetchModelsResult { Models = QiniuFallbackModels() };
                }

                return new FetchModelsResult
                {
                    Models = [],
                    Error = "failed to fetch models: " + ex.Message,
                };
            }

            if (IsCommandCodeChannelType(channelType))
            {
                models = FilterCommandCodeModels(channelType, models);
            }

            return new FetchModelsResult { Models = models };
        }
        finally
        {
            codexTimeout?.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Provider-conf defaults (model_fetcher.go:41-240)
    // ------------------------------------------------------------------

    private sealed class ProviderConfFetcher(
        string providerUrl,
        TimeSpan cacheDuration,
        IExecutor executor,
        TimeProvider time)
    {
        private readonly object _gate = new();
        private List<ModelIdentify>? _modelsCache;
        private DateTimeOffset _cacheTimestamp;

        /// <summary>Port of providerConfFetcher.fetch with double-checked locking.</summary>
        public List<ModelIdentify>? Fetch(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_modelsCache is { Count: > 0 }
                    && time.GetUtcNow() - _cacheTimestamp < cacheDuration)
                {
                    return [.. _modelsCache];
                }

                var models = FetchFromSource(cancellationToken);
                if (models is null)
                {
                    // A failed fetch keeps the previous cache (defensive copy);
                    // a successful empty fetch returns null without caching.
                    return _modelsCache is { Count: > 0 } ? [.. _modelsCache] : null;
                }

                if (models.Count > 0)
                {
                    _modelsCache = [.. models];
                    _cacheTimestamp = time.GetUtcNow();
                    return [.. models];
                }

                return null;
            }
        }

        private List<ModelIdentify>? FetchFromSource(CancellationToken cancellationToken)
        {
            var request = new HttpRequestData
            {
                Method = "GET",
                Url = providerUrl,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Accept"] = ["application/json"],
                },
            };

            HttpResponseData response;
            try
            {
                response = executor.Do(cancellationToken, request);
            }
            catch (Exception)
            {
                return null;
            }

            if (response.StatusCode != 200)
            {
                return null;
            }

            ProviderConfResponse? conf;
            try
            {
                conf = LlmJson.Deserialize<ProviderConfResponse>(response.Body ?? []);
            }
            catch (JsonException)
            {
                return null;
            }

            conf ??= new ProviderConfResponse();
            if (conf.Id.Length == 0)
            {
                return null;
            }

            var models = new List<ModelIdentify>(conf.Models?.Count ?? 0);
            foreach (var model in conf.Models ?? [])
            {
                if (model is { Id.Length: > 0 })
                {
                    models.Add(new ModelIdentify { Id = model.Id });
                }
            }

            return models;
        }
    }

    private sealed class ProviderConfResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("models")]
        public List<ProviderConfModel?>? Models { get; set; }
    }

    private sealed class ProviderConfModel
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Port of getDefaultModelsByType (model_fetcher.go:207-223).</summary>
    internal List<ModelIdentify>? GetDefaultModelsByType(
        string channelType,
        CancellationToken cancellationToken)
    {
        switch (channelType)
        {
            case "codex":
                return [.. CodexConstants.DefaultModels().Select(static id => new ModelIdentify { Id = id })];
            case "claudecode":
                return [.. ClaudeCodeConstants.DefaultModels().Select(static id => new ModelIdentify { Id = id })];
            case "xai_subscription":
                return [.. SubscriptionConstants.DefaultModels().Select(static id => new ModelIdentify { Id = id })];
            case "github_copilot":
                return FetchCopilotModels(cancellationToken);
            case "gemini_vertex":
                return _geminiVertexFetcher.Fetch(cancellationToken);
            default:
                return null;
        }
    }

    /// <summary>Port of isOfficialOnlyType (model_fetcher.go:228-230).</summary>
    private static bool IsOfficialOnlyType(string channelType)
        => channelType is "claudecode" or "codex" or "xai_subscription";

    private List<ModelIdentify>? FetchCopilotModels(CancellationToken cancellationToken)
        => _copilotFetcher.Fetch(cancellationToken);

    /// <summary>Port of tryReturnDefaultModels (model_fetcher.go:334-349).</summary>
    private FetchModelsResult? TryReturnDefaultModels(
        string channelType,
        CancellationToken cancellationToken)
    {
        if (IsOfficialOnlyType(channelType))
        {
            return null;
        }

        var models = GetDefaultModelsByType(channelType, cancellationToken);
        return models is null ? null : new FetchModelsResult { Models = models };
    }

    // ------------------------------------------------------------------
    // Cline discovery (model_fetcher.go:242-332)
    // ------------------------------------------------------------------

    private sealed class ClineRecommendedModel
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    private sealed class ClineRecommendedModelsResponse
    {
        [JsonPropertyName("recommended")]
        public List<ClineRecommendedModel?>? Recommended { get; set; }

        [JsonPropertyName("free")]
        public List<ClineRecommendedModel?>? Free { get; set; }

        [JsonPropertyName("clinePass")]
        public List<ClineRecommendedModel?>? ClinePass { get; set; }
    }

    private (List<ModelIdentify> Models, bool Fallback) FetchClineRecommendedModels(
        CancellationToken cancellationToken)
    {
        var fallback = ClineFallbackModels();
        var request = new HttpRequestData
        {
            Method = "GET",
            Url = ClineRecommendedModelsUrl,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Accept"] = ["application/json"],
            },
        };

        HttpResponseData response;
        try
        {
            response = _executor.Do(cancellationToken, request);
        }
        catch (Exception)
        {
            return (fallback, true);
        }

        if (response.StatusCode != 200)
        {
            return (fallback, true);
        }

        ClineRecommendedModelsResponse? parsed;
        try
        {
            parsed = LlmJson.Deserialize<ClineRecommendedModelsResponse>(response.Body ?? []);
        }
        catch (JsonException)
        {
            return (fallback, true);
        }

        parsed ??= new ClineRecommendedModelsResponse();

        var models = new List<ModelIdentify>(
            (parsed.Recommended?.Count ?? 0) + (parsed.Free?.Count ?? 0) + (parsed.ClinePass?.Count ?? 0));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AppendUniqueClineModels(models, seen, parsed.Recommended);
        AppendUniqueClineModels(models, seen, parsed.Free);
        AppendUniqueClineModels(models, seen, parsed.ClinePass);

        var usedFallback = !HasUsableClineModel(parsed.ClinePass);
        if (usedFallback)
        {
            AppendUniqueClineModels(
                models,
                seen,
                fallback.Select(static model => (ClineRecommendedModel?)new ClineRecommendedModel
                {
                    Id = model.Id,
                }).ToList());
        }

        if (models.Count == 0)
        {
            return (fallback, true);
        }

        return (models, usedFallback);
    }

    private static void AppendUniqueClineModels(
        List<ModelIdentify> models,
        HashSet<string> seen,
        List<ClineRecommendedModel?>? entries)
    {
        foreach (var entry in entries ?? [])
        {
            var id = (entry?.Id ?? string.Empty).Trim();
            if (id.Length == 0 || !seen.Add(id))
            {
                continue;
            }

            models.Add(new ModelIdentify { Id = id });
        }
    }

    private static bool HasUsableClineModel(List<ClineRecommendedModel?>? entries)
        => entries is not null
            && entries.Any(static entry => (entry?.Id ?? string.Empty).Trim().Length > 0);

    private static List<ModelIdentify> ClineFallbackModels()
        => [.. ClineConstants.DefaultModels().Select(static id => new ModelIdentify { Id = id })];

    // ------------------------------------------------------------------
    // Command Code filters (model_fetcher.go:182-205)
    // ------------------------------------------------------------------

    private static bool IsQiniuChannelType(string channelType)
        => channelType is "qiniu" or "qiniu_anthropic";

    private static bool IsCommandCodeChannelType(string channelType)
        => channelType is "commandcode" or "commandcode_anthropic";

    private static bool IsCommandCodeAnthropicModel(string modelId)
    {
        var normalized = modelId.Trim().ToLowerInvariant();
        return normalized == "claude"
            || normalized.StartsWith("claude-", StringComparison.Ordinal)
            || normalized.StartsWith("anthropic/claude-", StringComparison.Ordinal);
    }

    private static List<ModelIdentify> FilterCommandCodeModels(
        string channelType,
        List<ModelIdentify> models)
    {
        if (!IsCommandCodeChannelType(channelType))
        {
            return models;
        }

        var wantAnthropic = channelType == "commandcode_anthropic";
        return [.. models.Where(model => IsCommandCodeAnthropicModel(model.Id) == wantAnthropic)];
    }

    private static List<ModelIdentify> QiniuFallbackModels()
        => [new ModelIdentify { Id = "deepseek-v3" }];

    // ------------------------------------------------------------------
    // FetchModels common helpers (model_fetcher.go:351-358, 614-652)
    // ------------------------------------------------------------------

    private static bool InputMatches(FetchModelsInput input, ChannelRecord channel)
        => string.Equals(input.ChannelType, channel.Type, StringComparison.Ordinal)
            && string.Equals(
                (input.BaseUrl ?? string.Empty).TrimEnd('/'),
                (channel.BaseUrl ?? string.Empty).TrimEnd('/'),
                StringComparison.Ordinal);

    private static bool IsOAuthJson(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith('{') && value.Contains("access_token", StringComparison.Ordinal);
    }

    private async Task<(ChannelRecord? Channel, string? Error)> LoadChannelAsync(
        long channelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var channel = await _channels.GetAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
            return channel is null
                ? (null, "failed to get channel: ent: channel not found")
                : (channel, null);
        }
        catch (Exception ex)
        {
            return (null, "failed to get channel: " + ex.Message);
        }
    }

    private FetchModelsResult CodexFallbackModels()
        => new()
        {
            Models = GetDefaultModelsByType("codex", CancellationToken.None) ?? [],
            Fallback = true,
        };

    private static List<ModelIdentify> FetchModelsViaRequest(
        Func<HttpResponseData> execute,
        Func<byte[], List<ModelIdentify>> parse)
    {
        var response = execute();
        if (response.StatusCode != 200)
        {
            throw new InvalidOperationException("unexpected status: " + response.StatusCode);
        }

        try
        {
            return Uniq(parse(response.Body ?? []));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to parse models response: " + ex.Message,
                ex);
        }
    }

    private HttpRequestData PrepareCodexModelsRequest(
        CancellationToken cancellationToken,
        ChannelRecord channel,
        IExecutor executor)
    {
        var credentials = ResolveOAuthCredentials(channel);
        var parameters = new CodexTokenProviderParams
        {
            Credentials = credentials,
            // model_fetcher.go:556 passes the same policy client to the token
            // provider, so a token refresh cannot be redirected to cleartext.
            HttpClient = executor,
        };

        if (channel.Id != 0)
        {
            parameters.OnRefreshed = BuildTokenRefreshedCallback(channel);
        }

        return CodexModels.ModelsRequest(
            cancellationToken,
            CodexToken.CreateTokenProvider(parameters),
            channel.BaseUrl);
    }

    private static OAuthCredentials ResolveOAuthCredentials(ChannelRecord channel)
        => OAuthCredentialsJson.ResolveChannelCredentials(channel.Credentials);

    private static List<ModelIdentify> ParseCodexModels(byte[] body)
        => [.. CodexModels.ParseModelCatalog(body).Select(static id => new ModelIdentify { Id = id })];

    // ------------------------------------------------------------------
    // Gemini pagination (model_fetcher.go:654-733)
    // ------------------------------------------------------------------

    private sealed class GeminiModelResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class GeminiListModelsResponse
    {
        [JsonPropertyName("models")]
        public List<GeminiModelResponse?>? Models { get; set; }

        [JsonPropertyName("nextPageToken")]
        public string NextPageToken { get; set; } = string.Empty;
    }

    private List<ModelIdentify> FetchGeminiModels(
        CancellationToken cancellationToken,
        HttpRequestData request,
        IExecutor executor)
    {
        const int maxPages = 50;
        const int pageSize = 1000;

        var allModels = new List<ModelIdentify>(128);
        var pageToken = string.Empty;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);

        for (var pageIndex = 0; pageIndex < maxPages; pageIndex++)
        {
            request.Url = WithGeminiModelsPagination(request.Url, pageSize, pageToken);

            var response = executor.Do(cancellationToken, request);
            if (response.StatusCode != 200)
            {
                throw new InvalidOperationException(
                    "unexpected status: " + FormatRawStatus(response));
            }

            GeminiListModelsResponse? page;
            try
            {
                page = LlmJson.Deserialize<GeminiListModelsResponse>(response.Body ?? []);
                page ??= new GeminiListModelsResponse();
            }
            catch (JsonException ex)
            {
                List<ModelIdentify> fallbackModels;
                try
                {
                    fallbackModels = ParseModelsResponse(response.Body ?? []);
                }
                catch (Exception parseError)
                {
                    throw new InvalidOperationException(
                        "failed to parse models response: paginated unmarshal: "
                        + ex.Message + "; fallback parse: " + parseError.Message,
                        ex);
                }

                allModels.AddRange(fallbackModels);
                return allModels;
            }

            foreach (var model in page.Models ?? [])
            {
                allModels.Add(new ModelIdentify
                {
                    Id = TrimModelsPrefix(model?.Name ?? string.Empty),
                });
            }

            if (page.NextPageToken.Length == 0)
            {
                return allModels;
            }

            if (!seenTokens.Add(page.NextPageToken))
            {
                return allModels;
            }

            pageToken = page.NextPageToken;
        }

        return allModels;
    }

    private static string TrimModelsPrefix(string name)
        => name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;

    private static string FormatRawStatus(HttpResponseData response)
    {
        var code = response.RawResponse is { } raw ? (int)raw.StatusCode : response.StatusCode;
        var text = response.RawResponse?.ReasonPhrase;
        if (string.IsNullOrEmpty(text))
        {
            text = GoHttpStatus.Text(code);
        }

        // http.Response.Status is always "<code> <reason>", including the
        // trailing space for an unknown code with no reason phrase.
        return code + " " + text;
    }

    /// <summary>Port of withGeminiModelsPagination (model_fetcher.go:715-733).</summary>
    private static string WithGeminiModelsPagination(string modelsUrl, int pageSize, string pageToken)
    {
        // Split the fragment first, then the query, like url.Parse.
        var fragment = string.Empty;
        var hash = modelsUrl.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            fragment = modelsUrl[hash..];
            modelsUrl = modelsUrl[..hash];
        }

        var queryString = string.Empty;
        var question = modelsUrl.IndexOf('?', StringComparison.Ordinal);
        if (question >= 0)
        {
            queryString = modelsUrl[(question + 1)..];
            modelsUrl = modelsUrl[..question];
        }

        var query = HttpClientUtils.ParseQuery(queryString);

        if (pageSize > 0)
        {
            query["pageSize"] = [pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        }

        if (pageToken.Length > 0)
        {
            query["pageToken"] = [pageToken];
        }
        else
        {
            query.Remove("pageToken");
        }

        var encoded = HttpClientUtils.EncodeQuery(query);
        return encoded.Length > 0 ? modelsUrl + "?" + encoded + fragment : modelsUrl + fragment;
    }

    // ------------------------------------------------------------------
    // Endpoint preparation (model_fetcher.go:735-862)
    // ------------------------------------------------------------------

    private static bool IsAnthropic(string channelType) => channelType == "anthropic";

    private static bool IsAnthropicLike(string channelType)
        => channelType.EndsWith("_anthropic", StringComparison.Ordinal);

    private static bool UsesAnthropicModelApi(string channelType)
        => IsAnthropic(channelType) || IsAnthropicLike(channelType) || channelType == "claudecode";

    private static bool IsGemini(string channelType) => channelType == "gemini";

    internal static (string Url, Dictionary<string, List<string>> Headers) PrepareModelsEndpoint(
        string channelType,
        string baseUrl)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (baseUrl.EndsWith('/'))
        {
            baseUrl = baseUrl[..^1];
        }

        var useRawUrl = false;
        if (baseUrl.EndsWith('#'))
        {
            baseUrl = baseUrl[..^1];
            useRawUrl = true;
        }

        baseUrl = HttpModelsBaseUrl(baseUrl);

        string url;
        if (IsAnthropic(channelType) || channelType == "claudecode")
        {
            HttpClientUtils.HeaderSet(headers, "Anthropic-Version", "2023-06-01");

            baseUrl = TrimSuffix(baseUrl, "/anthropic");
            baseUrl = TrimSuffix(baseUrl, "/claude");

            url = useRawUrl
                ? baseUrl + "/models"
                : baseUrl.EndsWith("/v1", StringComparison.Ordinal)
                    ? baseUrl + "/models"
                    : baseUrl + "/v1/models";
        }
        else if (channelType is "zhipu_anthropic" or "zai_anthropic")
        {
            if (useRawUrl)
            {
                url = baseUrl + "/models";
            }
            else
            {
                baseUrl = TrimSuffix(baseUrl, "/anthropic");
                url = baseUrl.EndsWith("/v1", StringComparison.Ordinal)
                    ? baseUrl + "/models"
                    : baseUrl + "/paas/v4/models";
            }
        }
        else if (channelType is "zai" or "zhipu")
        {
            if (useRawUrl)
            {
                url = baseUrl + "/models";
            }
            else
            {
                baseUrl = TrimSuffix(baseUrl, "/v4");
                url = baseUrl.EndsWith("/v1", StringComparison.Ordinal)
                    ? baseUrl + "/models"
                    : baseUrl + "/v4/models";
            }
        }
        else if (channelType is "doubao" or "volcengine")
        {
            baseUrl = TrimSuffix(baseUrl, "/v3");
            url = baseUrl + "/v3/models";
        }
        else if (channelType == "doubao_anthropic")
        {
            baseUrl = TrimSuffix(baseUrl, "/compatible");
            url = baseUrl + "/v3/models";
        }
        else if (IsCommandCodeChannelType(channelType))
        {
            baseUrl = TrimSuffix(baseUrl, "/anthropic");
            baseUrl = TrimSuffix(baseUrl, "/claude");

            url = useRawUrl
                ? baseUrl + "/models"
                : baseUrl.EndsWith("/v1", StringComparison.Ordinal)
                    ? baseUrl + "/models"
                    : baseUrl + "/v1/models";
        }
        else if (IsAnthropicLike(channelType))
        {
            baseUrl = TrimSuffix(baseUrl, "/anthropic");
            baseUrl = TrimSuffix(baseUrl, "/claude");

            url = baseUrl.EndsWith("/v1", StringComparison.Ordinal)
                ? baseUrl + "/models"
                : baseUrl + "/v1/models";
        }
        else if (IsGemini(channelType))
        {
            url = baseUrl.Contains("/v1", StringComparison.Ordinal)
                ? baseUrl + "/models"
                : baseUrl + "/v1beta/models";
        }
        else if (channelType == "github")
        {
            // GitHub Models uses a separate catalog endpoint.
            url = "https://models.github.ai/catalog/models";
        }
        else if (channelType == "github_copilot")
        {
            // Copilot models come from the cached provider conf, not an endpoint.
            url = string.Empty;
        }
        else
        {
            url = useRawUrl
                ? baseUrl + "/models"
                : baseUrl.Contains("/v1", StringComparison.Ordinal)
                    ? baseUrl + "/models"
                    : baseUrl + "/v1/models";
        }

        return (url, headers);
    }

    /// <summary>Port of httpModelsBaseURL (model_fetcher.go:848-862).</summary>
    private static string HttpModelsBaseUrl(string baseUrl)
    {
        var separator = baseUrl.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return baseUrl;
        }

        var scheme = baseUrl[..separator].ToLowerInvariant();
        return scheme switch
        {
            "ws" => "http" + baseUrl[separator..],
            "wss" => "https" + baseUrl[separator..],
            _ => baseUrl,
        };
    }

    private static string TrimSuffix(string value, string suffix)
        => value.EndsWith(suffix, StringComparison.Ordinal)
            ? value[..^suffix.Length]
            : value;

    // ------------------------------------------------------------------
    // Response parsing (model_fetcher.go:864-920)
    // ------------------------------------------------------------------

    private sealed class CommonModelsResponse
    {
        [JsonPropertyName("data")]
        public List<ModelIdentify?>? Data { get; set; }

        [JsonPropertyName("models")]
        public List<GeminiModelResponse?>? Models { get; set; }
    }

    /// <summary>Port of jsonArrayRegex + ExtractJSONArray (model_fetcher.go:877-893).</summary>
    internal static List<ModelIdentify> ExtractJsonArray(string body)
    {
        var matches = JsonArrayRegex.Matches(body);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException("no JSON array found in response");
        }

        foreach (Match match in matches)
        {
            try
            {
                var decoded = LlmJson.Deserialize<List<ModelIdentify?>>(match.Value);
                if (decoded is not null)
                {
                    return NormalizeModels(decoded);
                }
            }
            catch (JsonException)
            {
                // Try the next bracketed substring.
            }
        }

        throw new InvalidOperationException("failed to unmarshal any JSON array");
    }

    /// <summary>Port of parseModelsResponse (model_fetcher.go:896-920).</summary>
    private static List<ModelIdentify> ParseModelsResponse(byte[] body)
    {
        // First try the direct-array shape (e.g. GitHub Models).
        try
        {
            var direct = LlmJson.Deserialize<List<ModelIdentify?>>(body);
            if (direct is { Count: > 0 })
            {
                return NormalizeModels(direct);
            }
        }
        catch (JsonException)
        {
            // Fall through to the object shape.
        }

        CommonModelsResponse? response;
        try
        {
            response = LlmJson.Deserialize<CommonModelsResponse>(body);
            response ??= new CommonModelsResponse();
        }
        catch (JsonException)
        {
            List<ModelIdentify> extracted;
            try
            {
                extracted = ExtractJsonArray(Encoding.UTF8.GetString(body));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("failed to parse response: " + ex.Message, ex);
            }

            response = new CommonModelsResponse { Data = [.. extracted.Select(static m => (ModelIdentify?)m)] };
        }

        if (response.Models is { Count: > 0 })
        {
            response.Data ??= [];
            foreach (var model in response.Models)
            {
                response.Data.Add(new ModelIdentify
                {
                    Id = TrimModelsPrefix(model?.Name ?? string.Empty),
                });
            }
        }

        return NormalizeModels(response.Data);
    }

    private static List<ModelIdentify> NormalizeModels(List<ModelIdentify?>? models)
    {
        if (models is null)
        {
            return [];
        }

        var result = new List<ModelIdentify>(models.Count);
        foreach (var model in models)
        {
            result.Add(model ?? new ModelIdentify());
        }

        return result;
    }

    /// <summary>Port of lo.Uniq: first occurrence order.</summary>
    private static List<ModelIdentify> Uniq(List<ModelIdentify> models)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ModelIdentify>(models.Count);
        foreach (var model in models)
        {
            if (seen.Add(model.Id))
            {
                result.Add(model);
            }
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Header overrides (channel_override.go:313-365, objects/channel.go:89-105)
    // ------------------------------------------------------------------

    /// <summary>
    /// Detects whether the stored settings JSON contains a non-null
    /// <c>headerOverrideOperations</c> member. Go's nil check distinguishes an
    /// absent/null slice (legacy fallback) from an empty array (authoritative).
    /// </summary>
    private static bool HasHeaderOverrideOperations(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)
            || string.Equals(settingsJson, "null", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (FoldAscii(property.Name) == "headeroverrideoperations")
                {
                    return property.Value.ValueKind != JsonValueKind.Null;
                }
            }
        }
        catch (JsonException)
        {
            // ChannelWriteSerialization.ParseSettings already rejected it.
        }

        return false;
    }

    private static List<OverrideOperationDraft>? HeaderEntriesToOverrideOperations(
        IReadOnlyList<HeaderEntryDraft>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var operations = new List<OverrideOperationDraft>(headers.Count);
        foreach (var header in headers)
        {
            if (string.Equals(header.Value, ChannelOverrideOps.ClearDirective, StringComparison.Ordinal))
            {
                operations.Add(new OverrideOperationDraft
                {
                    Op = ChannelOverrideOps.Delete,
                    Path = header.Key,
                });
                continue;
            }

            operations.Add(new OverrideOperationDraft
            {
                Op = ChannelOverrideOps.Set,
                Path = header.Key,
                Value = header.Value,
            });
        }

        return operations;
    }

    private static string FoldAscii(string name)
    {
        var chars = name.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] is >= 'A' and <= 'Z')
            {
                chars[index] = (char)(chars[index] + ('a' - 'A'));
            }
        }

        return new string(chars);
    }

    // ------------------------------------------------------------------
    // OAuth refresh persistence (channel_llm.go:1438-1481, channel_internal.go:66-71)
    // ------------------------------------------------------------------

    private Func<CancellationToken, OAuthCredentials, Exception?> BuildTokenRefreshedCallback(
        ChannelRecord channel)
        => (_, refreshed) =>
        {
            try
            {
                PersistRefreshedCredentials(channel, refreshed);
                return null;
            }
            catch (Exception ex)
            {
                // The token provider logs persistence failures and keeps the
                // refreshed credentials in memory.
                return ex;
            }
        };

    private void PersistRefreshedCredentials(ChannelRecord channel, OAuthCredentials refreshed)
    {
        if (channel.Type == "antigravity")
        {
            var projectId = ExtractProjectIdFromAntigravityCredentials(
                ChannelCredentials.GetLegacyApiKey(channel.Credentials));
            if (projectId is null)
            {
                throw new InvalidOperationException(
                    "failed to extract project ID from antigravity credentials: "
                    + "api key does not contain project ID (expected format: \"<refreshToken>|<projectID>\")");
            }

            var updated = ChannelWriteSerialization.ParseCredentials(channel.Credentials);
            updated.ApiKey = refreshed.RefreshToken + "|" + projectId;
            updated.OAuth = ToOAuthDraft(refreshed);
            _channels.UpdateCredentials(channel.Id, GoJson.MarshalToString(updated));
            return;
        }

        var draft = ChannelWriteSerialization.ParseCredentials(channel.Credentials);
        draft.ApiKey = refreshed.ToJson();
        draft.OAuth = ToOAuthDraft(refreshed);
        _channels.UpdateCredentials(channel.Id, GoJson.MarshalToString(draft));
    }

    /// <summary>Port of extractProjectIDFromAntigravityCreds (channel_llm.go:1444-1449).</summary>
    private static string? ExtractProjectIdFromAntigravityCredentials(string apiKey)
    {
        var parts = apiKey.Split('|');
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static OAuthCredentialsDraft ToOAuthDraft(OAuthCredentials credentials) => new()
    {
        ClientId = credentials.ClientId.Length > 0 ? credentials.ClientId : null,
        AccessToken = credentials.AccessToken,
        RefreshToken = credentials.RefreshToken,
        IdToken = credentials.IdToken.Length > 0 ? credentials.IdToken : null,
        ExpiresAt = GoTime.Rfc3339Nano(credentials.ExpiresAt.UtcDateTime),
        TokenType = credentials.TokenType.Length > 0 ? credentials.TokenType : null,
        Scopes = credentials.Scopes is { Count: > 0 } ? [.. credentials.Scopes] : null,
    };

    // ------------------------------------------------------------------
    // Antigravity catalog (model_fetcher_antigravity.go)
    // ------------------------------------------------------------------

    private sealed class AntigravityProject
    {
        [JsonPropertyName("project_id")]
        public string ProjectId { get; set; } = string.Empty;
    }

    private sealed class AntigravityCatalogResponse
    {
        [JsonPropertyName("models")]
        public Dictionary<string, AntigravityCatalogModel?>? Models { get; set; }
    }

    private sealed class AntigravityCatalogModel
    {
        [JsonPropertyName("isInternal")]
        public bool IsInternal { get; set; }
    }

    private async Task<List<ModelIdentify>> FetchAntigravityModelsAsync(
        FetchModelsInput input,
        CancellationToken cancellationToken)
    {
        ChannelRecord? channel = null;
        OAuthCredentials? credentials = null;
        Func<CancellationToken, OAuthCredentials, Exception?>? onRefreshed = null;
        var raw = (input.ApiKey ?? string.Empty).Trim();

        if (input.ChannelId is { } channelId)
        {
            try
            {
                channel = await _channels.GetAsync(channelId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to get Antigravity channel: " + ex.Message,
                    ex);
            }

            if (channel is null)
            {
                throw new InvalidOperationException(
                    "failed to get Antigravity channel: ent: channel not found");
            }

            // The reference's per-channel proxy is not ported (C158).
            var legacyApiKey = ChannelCredentials.GetLegacyApiKey(channel.Credentials);
            if (raw.Length == 0)
            {
                if (!InputMatches(input, channel))
                {
                    throw new InvalidOperationException(
                        "credentials are required when channel type or base URL is changed");
                }

                raw = legacyApiKey;
            }

            // Edited credentials must not refresh or overwrite the saved account.
            if (string.Equals(raw, legacyApiKey, StringComparison.Ordinal)
                && InputMatches(input, channel))
            {
                var refreshToken = CutBefore(raw.Trim(), '|');
                var saved = ParseSavedOAuth(channel);

                // Create/update/restore may retain OAuth data from a different
                // account; only reuse and persist a verified pair.
                if (saved is null
                    || (saved.AccessToken.Length == 0 && saved.RefreshToken.Length == 0)
                    || (refreshToken.Length > 0
                        && string.Equals(saved.RefreshToken, refreshToken, StringComparison.Ordinal))
                    || legacyApiKey.Length == 0)
                {
                    credentials = saved;
                    onRefreshed = BuildTokenRefreshedCallback(channel);
                }
            }
        }

        var projectId = string.Empty;
        if (raw.Trim().StartsWith('{'))
        {
            OAuthCredentials parsed;
            try
            {
                parsed = OAuthCredentialsJson.ParseCredentialsJson(raw);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to parse Antigravity OAuth credentials: " + ex.Message,
                    ex);
            }

            credentials = parsed;

            AntigravityProject? project;
            try
            {
                project = LlmJson.Deserialize<AntigravityProject>(raw);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to parse Antigravity project: " + ex.Message,
                    ex);
            }

            projectId = project?.ProjectId ?? string.Empty;

            // Legacy Antigravity persistence requires refreshToken|projectID.
            onRefreshed = null;
        }
        else
        {
            var parts = raw.Trim().Split('|', 2);
            if (parts.Length == 2)
            {
                projectId = parts[1];
            }

            credentials ??= new OAuthCredentials { RefreshToken = parts[0] };
        }

        var credentialsCopy = credentials.Clone();
        if (credentialsCopy.RefreshToken.Length == 0 && !raw.StartsWith('{'))
        {
            credentialsCopy.RefreshToken = raw.Split('|', 2)[0];
        }

        if (credentialsCopy.AccessToken.Length == 0 && credentialsCopy.RefreshToken.Length == 0)
        {
            throw new InvalidOperationException(
                "Antigravity OAuth credentials are required to fetch models");
        }

        if (projectId.Length == 0)
        {
            onRefreshed = null;
        }

        if (credentialsCopy.ClientId.Length == 0)
        {
            credentialsCopy.ClientId = AntigravityConstants.ClientID;
        }

        if (credentialsCopy.Scopes is not { Count: > 0 })
        {
            credentialsCopy.Scopes = [.. AntigravityConstants.Scopes];
        }

        var tokens = AntigravityTokenProviderFactory.NewTokenProvider(new TokenProviderParams
        {
            Credentials = credentialsCopy,
            HttpClient = _executor,
            OnRefreshed = onRefreshed,
        });

        OAuthCredentials refreshed;
        try
        {
            refreshed = tokens.Get(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to resolve Antigravity OAuth token: " + ex.Message,
                ex);
        }

        var body = new Dictionary<string, string>(StringComparer.Ordinal);
        if (projectId.Length > 0)
        {
            body["project"] = projectId;
        }

        var encoded = LlmJson.SerializeToUtf8Bytes(body);

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");
        AntigravityVersion.SetClientHeaders(headers);
        HttpClientUtils.HeaderSet(headers, "Authorization", "Bearer " + refreshed.AccessToken);

        HttpResponseData response;
        try
        {
            response = _executor.Do(cancellationToken, new HttpRequestData
            {
                Method = "POST",
                Url = AntigravityConstants.EndpointProd + "/v1internal:fetchAvailableModels",
                Headers = headers,
                Body = encoded,
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to fetch Antigravity model catalog: " + ex.Message,
                ex);
        }

        if (response.StatusCode != 200)
        {
            throw new InvalidOperationException(
                "failed to fetch Antigravity model catalog: status " + response.StatusCode);
        }

        AntigravityCatalogResponse? catalog;
        try
        {
            catalog = LlmJson.Deserialize<AntigravityCatalogResponse>(response.Body ?? []);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to parse Antigravity model catalog: " + ex.Message,
                ex);
        }

        var models = new List<ModelIdentify>(catalog?.Models?.Count ?? 0);
        foreach (var (id, model) in catalog?.Models ?? [])
        {
            if (id.Trim().Length > 0 && model is not { IsInternal: true })
            {
                models.Add(new ModelIdentify { Id = id });
            }
        }

        models.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        if (models.Count == 0)
        {
            throw new InvalidOperationException(
                "Antigravity model catalog contains no available public models");
        }

        return models;
    }

    private static OAuthCredentials? ParseSavedOAuth(ChannelRecord channel)
    {
        var oauthJson = ChannelCredentials.GetOAuthObjectJson(channel.Credentials);
        return oauthJson is null ? null : OAuthCredentialsJson.ParseOAuthObject(oauthJson);
    }

    /// <summary>Port of strings.Cut: the part before the first separator.</summary>
    private static string CutBefore(string value, char separator)
    {
        var index = value.IndexOf(separator);
        return index < 0 ? value : value[..index];
    }
}
