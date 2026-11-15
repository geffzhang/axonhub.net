using System.Globalization;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.Antigravity;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;
using AxonHub.Llm.Transformer.Bailian;
using AxonHub.Llm.Transformer.Cerebras;
using AxonHub.Llm.Transformer.Cline;
using AxonHub.Llm.Transformer.Cloudflare;
using AxonHub.Llm.Transformer.DeepSeek;
using AxonHub.Llm.Transformer.Doubao;
using AxonHub.Llm.Transformer.Fireworks;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.Gemini.OpenAI;
using AxonHub.Llm.Transformer.Longcat;
using AxonHub.Llm.Transformer.Minimax;
using AxonHub.Llm.Transformer.Modelscope;
using AxonHub.Llm.Transformer.Moonshot;
using AxonHub.Llm.Transformer.Nanogpt;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenRouter;
using AxonHub.Llm.Transformer.Jina;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.OpenAI.Copilot;
using AxonHub.Llm.Transformer.OpenAI.Decisions;
using AxonHub.Llm.Transformer.Ollama;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.OpenCode;
using AxonHub.Llm.Transformer.TypeSafe;
using AxonHub.Llm.Transformer.Xai;
using AxonHub.Llm.Transformer.Xai.Subscription;
using AxonHub.Llm.Transformer.Zai;
using AxonHub.Llm.Transformer.Zenmux;
using Orleans;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Builds the outbound transformer for one candidate, the job the reference does
/// once per channel while loading it (internal/server/biz/channel_llm.go:
/// <c>buildChannel</c> → the channel-type switch).
/// </summary>
/// <remarks>
/// Only the <c>openai</c>, <c>openai_responses</c>, <c>anthropic</c>,
/// <c>commandcode</c>, <c>commandcode_anthropic</c>, <c>claudecode</c>,
/// <c>opencode_go</c>, <c>opencode_go_anthropic</c>, <c>jina</c>,
/// <c>typesafe</c>, <c>codex</c>, <c>fenno</c>, <c>deepseek</c>, <c>fireworks</c>,
/// <c>moonshot</c>, <c>longcat</c>, <c>cline</c>, <c>xai</c>, <c>openrouter</c>,
/// <c>cerebras</c>, <c>nanogpt</c>, <c>nanogpt_responses</c>, <c>ollama</c>,
/// <c>ollama_anthropic</c>, <c>zai</c>, <c>zhipu</c>, <c>zai_anthropic</c>,
/// <c>zhipu_anthropic</c>, <c>xiaomi</c>, <c>xiaomi_anthropic</c>,
/// <c>minimax</c>, <c>minimax_anthropic</c>, <c>bailian</c>,
/// <c>bailian_responses</c>, <c>bailian_anthropic</c>,
/// <c>moonshot_coding</c>, <c>modelscope</c>, <c>doubao</c>,
/// <c>volcengine</c>, <c>doubao_anthropic</c>, <c>volcengine_anthropic</c>,
/// <c>zenmux</c>, <c>zenmux_responses</c>, <c>zenmux_anthropic</c>,
/// <c>zenmux_video</c>, <c>github_copilot</c>, <c>gemini_openai</c>,
/// <c>gemini</c>, <c>zenmux_gemini</c>, <c>gemini_vertex</c>,
/// <c>antigravity</c>, <c>atlascloud</c>, <c>vercel</c>, <c>deepinfra</c>,
/// <c>qiniu</c>, <c>ppio</c>, <c>siliconflow</c>, <c>aihubmix</c>,
/// <c>burncloud</c>, <c>github</c>, <c>evolink</c>, <c>groq</c>,
/// <c>qiniu_anthropic</c>, <c>aihubmix_anthropic</c>, <c>evolink_anthropic</c>,
/// <c>longcat_anthropic</c>, <c>moonshot_anthropic</c>, <c>deepseek_anthropic</c>,
/// <c>anthropic_aws</c>, <c>xai_responses</c> and <c>xai_subscription</c>
/// channel types are ported (the vertical slice); the
/// other provider types are registered as a scope cut. Key
/// selection follows getAPIKeyProvider: a single enabled key is static, several
/// enabled keys are picked by rendezvous hashing when the request carries a
/// trace id and at random otherwise (C013).
/// </remarks>
public static class ChannelOutboundFactory
{
    public const string OpenAiChannelType = "openai";

    public const string OpenaiResponsesChannelType = "openai_responses";

    public const string AnthropicChannelType = "anthropic";

    public const string CommandcodeChannelType = "commandcode";

    public const string CommandcodeAnthropicChannelType = "commandcode_anthropic";

    public const string AntigravityChannelType = "antigravity";

    public const string JinaChannelType = "jina";

    public const string TypeSafeChannelType = "typesafe";

    public const string CodexChannelType = "codex";

    public const string FennoChannelType = "fenno";

    public const string ClaudeCodeChannelType = "claudecode";

    public const string OpenCodeGoChannelType = "opencode_go";

    public const string OpenCodeGoAnthropicChannelType = "opencode_go_anthropic";

    public const string DeepSeekChannelType = "deepseek";

    public const string FireworksChannelType = "fireworks";

    public const string MoonshotChannelType = "moonshot";

    public const string LongcatChannelType = "longcat";

    public const string ClineChannelType = "cline";

    public const string XaiChannelType = "xai";

    public const string OpenRouterChannelType = "openrouter";

    public const string CerebrasChannelType = "cerebras";

    public const string NanogptChannelType = "nanogpt";

    public const string NanogptResponsesChannelType = "nanogpt_responses";

    public const string OllamaChannelType = "ollama";

    public const string OllamaAnthropicChannelType = "ollama_anthropic";

    public const string ZaiChannelType = "zai";

    public const string ZhipuChannelType = "zhipu";

    public const string ZaiAnthropicChannelType = "zai_anthropic";

    public const string ZhipuAnthropicChannelType = "zhipu_anthropic";

    public const string XiaomiChannelType = "xiaomi";

    public const string XiaomiAnthropicChannelType = "xiaomi_anthropic";

    public const string MinimaxChannelType = "minimax";

    public const string MinimaxAnthropicChannelType = "minimax_anthropic";

    public const string BailianChannelType = "bailian";

    public const string BailianResponsesChannelType = "bailian_responses";

    public const string BailianAnthropicChannelType = "bailian_anthropic";

    public const string MoonshotCodingChannelType = "moonshot_coding";

    public const string ModelscopeChannelType = "modelscope";

    public const string DoubaoChannelType = "doubao";

    public const string VolcengineChannelType = "volcengine";

    public const string DoubaoAnthropicChannelType = "doubao_anthropic";

    public const string VolcengineAnthropicChannelType = "volcengine_anthropic";

    public const string ZenmuxChannelType = "zenmux";

    public const string ZenmuxResponsesChannelType = "zenmux_responses";

    public const string ZenmuxAnthropicChannelType = "zenmux_anthropic";

    public const string ZenmuxVideoChannelType = "zenmux_video";

    public const string GithubCopilotChannelType = "github_copilot";

    public const string GeminiOpenAiChannelType = "gemini_openai";

    public const string GeminiChannelType = "gemini";

    public const string GeminiVertexChannelType = "gemini_vertex";

    public const string ZenmuxGeminiChannelType = "zenmux_gemini";

    // channel_llm.go:1277-1280: OpenAI-compatible alias channels sharing the
    // plain OpenAI outbound (PlatformOpenAI).
    public const string AtlascloudChannelType = "atlascloud";

    public const string VercelChannelType = "vercel";

    public const string DeepinfraChannelType = "deepinfra";

    public const string QiniuChannelType = "qiniu";

    public const string PpioChannelType = "ppio";

    public const string SiliconflowChannelType = "siliconflow";

    public const string AihubmixChannelType = "aihubmix";

    public const string BurncloudChannelType = "burncloud";

    public const string GithubChannelType = "github";

    public const string EvolinkChannelType = "evolink";

    public const string GroqChannelType = "groq";

    /// <summary>Cloudflare Workers AI: OpenAI-compatible surface plus native /run.</summary>
    public const string CloudflareChannelType = "cloudflare";

    // channel_llm.go:898, 885, 990, 1016, 1056: Anthropic-format aliases
    // (qiniu/aihubmix/evolink share the plain direct platform).
    public const string QiniuAnthropicChannelType = "qiniu_anthropic";

    public const string AihubmixAnthropicChannelType = "aihubmix_anthropic";

    public const string EvolinkAnthropicChannelType = "evolink_anthropic";

    public const string LongcatAnthropicChannelType = "longcat_anthropic";

    public const string MoonshotAnthropicChannelType = "moonshot_anthropic";

    public const string DeepseekAnthropicChannelType = "deepseek_anthropic";

    public const string AnthropicAwsChannelType = "anthropic_aws";

    // channel_llm.go:677-680, 1069-1085: anthropic_gcp authenticates with GCP
    // credentials and has no API keys.
    public const string AnthropicGcpChannelType = "anthropic_gcp";

    // channel_llm.go:1089-1095, channel_endpoint.go:282-283: test-only fake
    // channels serve embedded fixtures and take no credentials.
    public const string AnthropicFakeChannelType = "anthropic_fake";

    public const string OpenaiFakeChannelType = "openai_fake";

    // channel_llm.go:855-863: the xAI responses channel has exactly one
    // endpoint, the responses API (channel_endpoint.go:296).
    public const string XaiResponsesChannelType = "xai_responses";

    // channel_llm.go:867-884: xai_subscription is OAuth-only and speaks the
    // Responses protocol through the Grok CLI endpoint with a refreshing token
    // provider.
    public const string XaiSubscriptionChannelType = "xai_subscription";

    // channel_llm.go:56-58: the default base URLs applied when a ZenMux
    // channel has none stored (channel_llm.go:695-705).
    public const string ZenmuxOpenAiBaseUrl = "https://zenmux.ai/api/v1";

    public const string ZenmuxAnthropicBaseUrl = "https://zenmux.ai/api/anthropic";

    public const string ZenmuxGeminiBaseUrl = "https://zenmux.ai/api/vertex-ai";

    /// <param name="channelExecutor">
    /// The channel's proxy-aware HTTP executor, the reference's
    /// <c>channel.HTTPClient</c>. It serves the OAuth token refreshers and the
    /// transformers that perform their own HTTP calls (Modelscope task polling
    /// and image download). Callers that hold the per-channel executor (the
    /// orchestrator through <see cref="ChannelProxyExecutors"/>) pass it here,
    /// while direct callers fall back to their shared executor.
    /// </param>
    /// <param name="grainFactory">
    /// When present, the codex, fenno, claudecode, xai_subscription and
    /// antigravity OAuth channels take their token from
    /// <see cref="IChannelTokenGrain"/> instead of a throwaway per-attempt
    /// <see cref="TokenProvider"/> (§146, §194). Direct callers (endpoint
    /// detection, tests) leave it null and keep the local provider.
    /// </param>
    /// <param name="apiKeySelection">
    /// The process-wide multi-key selection state
    /// (<c>ChannelService.apiKeyStates</c>). Direct callers and tests leave it
    /// null and keep the per-provider historical behavior.
    /// </param>
    /// <param name="copilotTokens">
    /// The process-wide per-channel Copilot exchanger cache, standing in for the
    /// reference's exchanger on the enabled-channel cache entry (§194). Direct
    /// callers and tests leave it null and get a throwaway exchanger per build.
    /// </param>
    public static IOutboundTransformer Create(
        ChannelCandidate candidate,
        LlmRequest request,
        DateTimeOffset now,
        IExecutor? channelExecutor = null,
        IGrainFactory? grainFactory = null,
        ChannelApiKeySelectionRegistry? apiKeySelection = null,
        ChannelCopilotTokenCache? copilotTokens = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(request);

        var enabledKeys = ChannelCredentials.GetEnabledApiKeys(
            candidate.Credentials,
            candidate.DisabledApiKeys,
            now);

        // channel_llm.go:655-658: the channel key test flow's forced key is
        // trimmed once and then wins over every configured key.
        var overrideApiKey = (candidate.ApiKeyOverride ?? string.Empty).Trim();
        var isOAuth = ChannelCredentials.IsOAuth(candidate.Credentials);

        // channel_llm.go:660-693: fail fast without constructing HTTP clients.
        // The fake and GCP channel types use no API keys; Ollama may run
        // keyless; Command Code accepts a test override.
        switch (candidate.Type)
        {
            case CodexChannelType or ClaudeCodeChannelType:
                if (!isOAuth && enabledKeys.Count == 0)
                {
                    throw new InvalidOperationException(
                        "missing credentials: oauth or api key required for channel " + candidate.ChannelName);
                }

                break;
            case GithubCopilotChannelType or XaiSubscriptionChannelType:
                if (!isOAuth)
                {
                    throw new InvalidOperationException(
                        "missing oauth credentials for channel " + candidate.ChannelName);
                }

                break;
            case AntigravityChannelType:
                if (ChannelCredentials.GetLegacyApiKey(candidate.Credentials).Trim().Length == 0)
                {
                    throw new InvalidOperationException(
                        "missing api key for channel " + candidate.ChannelName);
                }

                break;
            case AnthropicGcpChannelType or AnthropicFakeChannelType or OpenaiFakeChannelType:
                // anthropic_gcp uses GCP credentials; *_fake are test-only.
                break;
            case OllamaChannelType or OllamaAnthropicChannelType:
                // Often run locally without keys; an override may supply one.
                break;
            case CommandcodeChannelType or CommandcodeAnthropicChannelType:
                if (enabledKeys.Count == 0 && overrideApiKey.Length == 0)
                {
                    throw new InvalidOperationException(
                        "missing api key for channel " + candidate.ChannelName);
                }

                break;
            default:
                if (enabledKeys.Count == 0)
                {
                    throw new InvalidOperationException(
                        "missing api key for channel " + candidate.ChannelName);
                }

                break;
        }

        // channel_llm.go:640-647, 705-711: both Command Code channel types
        // require an HTTPS base URL. The reference also hands them an
        // HTTPS-downgrade-rejecting client; the port applies that policy where
        // the executor is selected instead — PersistentOutboundTransformer for
        // inference, ChannelEndpointDetectService for endpoint detection —
        // since the channel here carries no client.
        var commandCodeChannel = candidate.Type is CommandcodeChannelType or CommandcodeAnthropicChannelType;
        if (commandCodeChannel)
        {
            var parsed = Uri.TryCreate(
                (candidate.BaseUrl ?? string.Empty).Trim(),
                UriKind.Absolute,
                out var commandCodeUri);
            if (!parsed || commandCodeUri!.Scheme != Uri.UriSchemeHttps || commandCodeUri.Host.Length == 0)
            {
                throw new InvalidOperationException("Command Code base URL must use HTTPS");
            }
        }

        var allKeys = ChannelCredentials.GetAllApiKeys(candidate.Credentials);
        var configuredApiKeys = ChannelCredentials.GetApiKeys(candidate.Credentials);

        // getAPIKeyProvider (channel_llm.go:169-184): the override is a static
        // key. Otherwise the channel keeps its configured policy active whenever
        // it holds more than one key — even when only one is selectable right
        // now — so the fixed cursor and the round_robin_success counter survive
        // a key being disabled. A single key stays static; several keys dispatch
        // on the channel's apiKeyStrategy.
        //
        // The shared state is attached at build time for every strategy that
        // remembers something (channel_llm.go:770), even when this build ends up
        // with a single selectable key, so the success report keeps counting.
        var settings = ChannelTransformOptions.SettingsOf(candidate);
        var strategy = ChannelApiKeyStrategies.StrategyOf(settings);
        var apiKeyState = apiKeySelection?.StateFor(
            candidate.ChannelId,
            strategy,
            ChannelApiKeyStrategies.SwitchAfterOf(settings));

        var traceId = request.RawRequest?.SessionId;
        var provider = overrideApiKey.Length > 0
            ? ChannelApiKeyProvider.ForOverride(overrideApiKey, allKeys)
            : enabledKeys.Count > 1 || configuredApiKeys.Count > 1
                ? ChannelApiKeyProvider.ForSelection(
                    ChannelApiKeyStrategies.Select(
                        settings,
                        apiKeyState,
                        enabledKeys,
                        configuredApiKeys,
                        allKeys,
                        ChannelCredentials.GetDisabledApiKeySet(candidate.DisabledApiKeys),
                        traceId),
                    allKeys)
                : new ChannelApiKeyProvider(enabledKeys, allKeys, traceId);

        // The reference keys each channel's outbounds by API format (the channel
        // endpoint map) and picks the map entry the candidate selected
        // (outbound.go:450-462). A candidate selected by the channel tester
        // carries that choice; pool-selected candidates leave it null and the
        // slice dispatches on the inbound format instead: a Responses request to
        // an openai channel uses the Responses transformer, everything else the
        // chat one.
        var apiFormat = (string.IsNullOrEmpty(candidate.ApiFormat)
            ? request.ApiFormat
            : candidate.ApiFormat) ?? string.Empty;

        // The reference keys each channel's outbounds by API format and builds
        // a separate transformer per user-configured endpoint
        // (buildChannelWithOutbounds / buildNonDefaultEndpointOutbound,
        // channel_llm.go:431-620). The port re-dispatches per attempt, so the
        // selected user endpoint supplies the base URL override, path and
        // transport; null means the built-in default endpoint and the primary
        // outbound rules apply.
        var selectedEndpoint = SelectUserEndpoint(candidate, apiFormat);
        var endpointBaseUrl = selectedEndpoint is { BaseUrl.Length: > 0 }
            ? selectedEndpoint.BaseUrl
            : candidate.BaseUrl ?? string.Empty;
        var endpointPath = selectedEndpoint?.Path ?? string.Empty;
        var endpointTransport = selectedEndpoint is null
            ? string.Empty
            : ChannelEndpoints.EndpointTransport(selectedEndpoint);

        // channel_llm.go:236-251: user-configured endpoints are processed after
        // the type defaults and replace the outbound registered for their API
        // format, so a user endpoint always wins over the channel-type dispatch
        // below and is built by buildNonDefaultEndpointOutbound (:431-616) — by
        // format, not by channel type. Opencode wraps every user endpoint with
        // its session header (:247-249). An empty stored format is skipped by
        // the reference loop and keeps the type dispatch.
        if (selectedEndpoint is { ApiFormat.Length: > 0 })
        {
            var endpointOutbound = CreateUserEndpointOutbound(
                candidate,
                request,
                selectedEndpoint,
                provider,
                channelExecutor,
                grainFactory);

            return candidate.Type is OpenCodeGoChannelType or OpenCodeGoAnthropicChannelType
                ? OpenCodeSessionHeader.WithSessionHeader(endpointOutbound)!
                : endpointOutbound;
        }

        return candidate.Type switch
        {
            // channel_llm.go:454/465/497: codex responses, alpha search and the
            // image tool all run through the codex outbound. Fenno shares the
            // responses and image branches but not alpha search.
            CodexChannelType when apiFormat is ApiFormats.OpenAIResponse
                or ApiFormats.OpenAIResponseWebSocket
                or ApiFormats.OpenAIAlphaSearch
                or ApiFormats.OpenAIImageGeneration
                or ApiFormats.OpenAIImageEdit
                => CreateCodexOutbound(
                    candidate,
                    request,
                    provider,
                    channelExecutor,
                    grainFactory,
                    apiFormat,
                    selectedEndpoint),
            FennoChannelType when apiFormat is ApiFormats.OpenAIResponse
                or ApiFormats.OpenAIResponseWebSocket
                or ApiFormats.OpenAIImageGeneration
                or ApiFormats.OpenAIImageEdit
                => CreateCodexOutbound(
                    candidate,
                    request,
                    provider,
                    channelExecutor,
                    grainFactory,
                    apiFormat,
                    selectedEndpoint),
            OpenAiChannelType when apiFormat is ApiFormats.OpenAIResponse
                or ApiFormats.OpenAIResponseWebSocket
                or ApiFormats.OpenAIResponseCompact
                => CreateResponsesOutbound(
                    request,
                    endpointBaseUrl,
                    provider,
                    endpointTransport,
                    endpointPath,
                    candidate.ChannelId),
            OpenAiChannelType when apiFormat == ApiFormats.OpenAIDecisions
                => new DecisionsOutboundTransformer(new DecisionsOutboundConfig
                {
                    BaseUrl = endpointBaseUrl,
                    EndpointPath = endpointPath,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            // channel_llm.go:445: the legacy /completions outbound shares the
            // openai channel's credentials but targets its own path.
            OpenAiChannelType when apiFormat == ApiFormats.OpenAICompletion
                => new CompletionOutboundTransformer(new OpenAiOutboundConfig
                {
                    BaseUrl = endpointBaseUrl,
                    EndpointPath = endpointPath,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            OpenAiChannelType => selectedEndpoint is null
                ? OpenAiOutboundTransformer.Create(
                    endpointBaseUrl,
                    provider,
                    provider.KeyIndex)
                : new OpenAiOutboundTransformer(new OpenAiOutboundConfig
                {
                    BaseUrl = endpointBaseUrl,
                    EndpointPath = endpointPath,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            AnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                BaseUrl = endpointBaseUrl,
                EndpointPath = endpointPath,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1195-1204: the commandcode channel's primary
            // outbound is the plain OpenAI chat outbound; the default endpoint
            // set is chat only (channel_endpoint.go:339). A custom
            // Anthropic-format endpoint would be served by the Command Code
            // anthropic outbound with Bearer auth (channel_llm.go:527-529),
            // but the slice has no endpoint records (C082). The credential
            // check above mirrors channel_llm.go:683-688: enabled keys or the
            // key-test override (channel_llm.go:655-658), unlike the default
            // case. The reference's error wrap is unreachable after the
            // credential check.
            CommandcodeChannelType => OpenAiOutboundTransformer.Create(
                candidate.BaseUrl ?? string.Empty,
                provider,
                provider.KeyIndex),
            // channel_llm.go:911-918: commandcode_anthropic speaks Anthropic
            // Messages through the Command Code platform — Bearer auth instead
            // of X-API-Key, with Direct-like signature decoding and thinking
            // support (outbound.go:251, outbound_convert.go:96, thinking.go).
            // The default endpoint set is a single messages endpoint
            // (channel_endpoint.go:340).
            CommandcodeAnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.CommandCode,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:855-863: the xAI responses channel has exactly
            // one endpoint, the responses API (channel_endpoint.go:296),
            // served by the shared Responses outbound.
            XaiResponsesChannelType => CreateResponsesOutbound(
                request,
                endpointBaseUrl,
                provider,
                endpointTransport,
                endpointPath,
                candidate.ChannelId),
            // channel_llm.go:867-884: xai_subscription wraps the Responses
            // outbound with a refreshing OAuth token provider pinned to the
            // Grok CLI endpoints. The grain-backed provider runs the refresh
            // loop and the OnRefreshed persistence callback (§146); the local
            // fallback used without a grain factory has neither.
            XaiSubscriptionChannelType => CreateXaiSubscriptionOutbound(
                candidate,
                channelExecutor,
                request,
                grainFactory),
            // channel_llm.go:885-893: longcat_anthropic speaks Anthropic
            // Messages through the LongCat platform (Bearer auth).
            LongcatAnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.LongCat,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:898-909: qiniu/aihubmix/evolink anthropic variants
            // share the plain Anthropic direct platform (X-API-Key).
            QiniuAnthropicChannelType or AihubmixAnthropicChannelType or EvolinkAnthropicChannelType
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Direct,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            // channel_llm.go:990-998: deepseek_anthropic uses the DeepSeek
            // platform (output_config support, no adaptive thinking).
            DeepseekAnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.DeepSeek,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1016-1024: moonshot_anthropic uses the Moonshot
            // platform.
            MoonshotAnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.Moonshot,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1056-1064: anthropic_aws speaks Anthropic Messages
            // through the Bedrock platform (Bearer auth, /model/{model}/invoke
            // wire envelope; the Vertex sibling stays a gap, C056).
            AnthropicAwsChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.Bedrock,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1069-1085: anthropic_gcp authenticates with the
            // channel's GCP credentials and rewrites requests for Vertex AI.
            AnthropicGcpChannelType => CreateAnthropicGcpOutbound(candidate),
            // channel_llm.go:1089-1095: test-only fake channels wrap the real
            // provider outbound but replace the transport with embedded
            // testdata fixtures; credentials and base URL are ignored.
            AnthropicFakeChannelType => new AnthropicFakeTransformer(),
            OpenaiFakeChannelType => new OpenAiFakeTransformer(),
            // channel_llm.go:1277-1280: the OpenAI-compatible alias channels
            // share the plain OpenAI outbound (PlatformOpenAI). Their default
            // endpoint sets — openAICompatibleDefaultEndpoints
            // (channel_endpoint.go:189-197), qiniu's chat-only (:236) and
            // groq's chat + audio (:333-337) — are all formats the OpenAI
            // outbound serves internally, so no ApiFormat branching is needed;
            // formats outside the set error in the reference's endpoint
            // lookup (dispatch model difference C094).
            AtlascloudChannelType or VercelChannelType or DeepinfraChannelType
                or QiniuChannelType or PpioChannelType or SiliconflowChannelType
                or AihubmixChannelType or BurncloudChannelType or GithubChannelType
                or EvolinkChannelType or GroqChannelType or CloudflareChannelType
                => OpenAiOutboundTransformer.Create(
                    candidate.BaseUrl ?? string.Empty,
                    provider,
                    provider.KeyIndex),
            // channel_llm.go:924-989: OAuth channels get a JSON-refreshing
            // Claude token provider; plain key channels rotate through the
            // per-attempt key provider.
            ClaudeCodeChannelType => CreateClaudeCodeOutbound(
                candidate,
                provider,
                channelExecutor,
                grainFactory),
            // channel_llm.go:1183-1194: one outbound dispatching by model family.
            OpenCodeGoChannelType => new OpenCodeOutboundTransformer(new OpenCodeOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
                SessionIdProvider = () => request.RawRequest?.SessionId,
                SessionScopeProvider = () => request.RawRequest?.SessionScope,
            }),
            // channel_llm.go:1170-1181: the anthropic outbound plus OpenCode
            // session affinity.
            OpenCodeGoAnthropicChannelType => OpenCodeSessionHeader.WithSessionHeader(
                AnthropicOutboundTransformer.Create(
                    candidate.BaseUrl ?? string.Empty,
                    provider,
                    provider.KeyIndex))!,
            JinaChannelType => JinaOutboundTransformer.Create(
                candidate.BaseUrl ?? string.Empty,
                provider,
                provider.KeyIndex),
            // channel_endpoint.go:342: a typesafe channel has exactly one
            // endpoint (typesafe/systemone) and channel_llm.go routes every
            // request type through the same outbound.
            TypeSafeChannelType => TypeSafeOutboundTransformer.Create(
                candidate.BaseUrl ?? string.Empty,
                provider,
                provider.KeyIndex),
            // channel_llm.go:819-829: DeepSeek is OpenAI-compatible but owns the
            // /beta/completions route, json_schema downgrade and thinking field.
            DeepSeekChannelType => new DeepSeekOutboundTransformer(new DeepSeekOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:729-739: OpenAI-compatible; the provider package
            // supplies the default inference base URL and reasoning_content.
            FireworksChannelType => FireworksOutboundTransformer.Create(new FireworksOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:741-751: OpenAI-compatible with the Cline
            // {success,data,error,errors} envelope; the default endpoint set is
            // chat only (channel_endpoint.go:238).
            ClineChannelType => new ClineOutboundTransformer(new ClineOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:753-763: OpenRouter is OpenAI-compatible with a
            // top-level reasoning field and its own /images route; the default
            // endpoint set is chat plus speech/transcription/translation
            // (channel_endpoint.go:284-294).
            OpenRouterChannelType => new OpenRouterOutboundTransformer(new OpenRouterOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:764-774: Cerebras wraps the OpenRouter outbound and
            // strips the unsupported `store` field; the default endpoint set is
            // chat only (channel_endpoint.go:324).
            CerebrasChannelType => new CerebrasOutboundTransformer(new CerebrasOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:775-785: the OpenAI outbound plus top-level
            // reasoning and XML tool-call extraction; the default endpoint set
            // is the full OpenAI one (channel_endpoint.go:326).
            NanogptChannelType => new NanogptOutboundTransformer(new NanogptOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:789-799: the nanoGPT responses channel has exactly
            // one endpoint, the responses API, served by the shared Responses
            // outbound (channel_endpoint.go:327).
            NanogptResponsesChannelType => CreateResponsesOutbound(
                request,
                endpointBaseUrl,
                provider,
                endpointTransport,
                endpointPath,
                candidate.ChannelId),
            // channel_llm.go:831-841: OpenAI-compatible with a custom request
            // path (chat only, json_schema downgraded to json_object).
            MoonshotChannelType => new MoonshotOutboundTransformer(new MoonshotOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:843-853: xAI is the OpenAI outbound with
            // reasoning_content and an empty-delta stream filter. The channel
            // also owns a peer responses endpoint (channel_endpoint.go:292-296),
            // served by the Responses transformer (channel_llm.go:437-455).
            XaiChannelType when apiFormat is ApiFormats.OpenAIResponse
                or ApiFormats.OpenAIResponseWebSocket
                or ApiFormats.OpenAIResponseCompact
                => CreateResponsesOutbound(
                    request,
                    endpointBaseUrl,
                    provider,
                    endpointTransport,
                    endpointPath,
                    candidate.ChannelId),
            XaiChannelType => new XaiOutboundTransformer(new XaiOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1121-1131: the OpenAI outbound plus the forced
            // array content required by LongCat models.
            LongcatChannelType => new LongcatOutboundTransformer(new LongcatOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1392-1405: the provider is only installed when the
            // channel has enabled keys or a key-test override, so a keyless or
            // fully-disabled channel sends no Authorization header at all.
            OllamaChannelType => new OllamaOutboundTransformer(new OllamaOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = enabledKeys.Count > 0 || overrideApiKey.Length > 0 ? provider : null,
                ChannelApiKeyIndex = enabledKeys.Count > 0 || overrideApiKey.Length > 0
                    ? provider.KeyIndex
                    : null,
            }),
            // channel_llm.go:1406-1428: Anthropic Messages wire format against
            // Ollama, with Bearer auth instead of x-api-key.
            OllamaAnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.Ollama,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = enabledKeys.Count > 0 || overrideApiKey.Length > 0 ? provider : null,
                ChannelApiKeyIndex = enabledKeys.Count > 0 || overrideApiKey.Length > 0
                    ? provider.KeyIndex
                    : null,
            }),
            // channel_llm.go:582-589: zai/zhipu chat runs through the Zai
            // outbound (GLM thinking ladder, user_id rules) on the v4 route.
            ZaiChannelType or ZhipuChannelType => CreateZaiOutbound(candidate, request, provider, "v4"),
            // channel_llm.go:590-595: Xiaomi keeps the family request handling
            // on its v1 route (providerChatEndpointUsesFamilyVersion).
            XiaomiChannelType => CreateZaiOutbound(candidate, request, provider, "v1"),
            // channel_llm.go:1029-1052: the *Anthropic channel types serve
            // anthropic/messages through the anthropic outbound and, for a
            // chat-format endpoint, the family chat outbound.
            ZaiAnthropicChannelType when apiFormat == ApiFormats.AnthropicMessage
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Zai,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            ZaiAnthropicChannelType => CreateZaiOutbound(candidate, request, provider, "v4"),
            ZhipuAnthropicChannelType when apiFormat == ApiFormats.AnthropicMessage
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Zhipu,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            ZhipuAnthropicChannelType => CreateZaiOutbound(candidate, request, provider, "v4"),
            // channel_llm.go:898: xiaomi_anthropic and minimax_anthropic use the
            // plain Anthropic direct platform.
            XiaomiAnthropicChannelType when apiFormat == ApiFormats.AnthropicMessage
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Direct,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            XiaomiAnthropicChannelType => CreateZaiOutbound(candidate, request, provider, "v1"),
            // channel_llm.go:1303-1315: MiniMax dispatches images to its own
            // /image_generation API and delegates chat to the OpenAI outbound.
            MinimaxChannelType => new MinimaxOutboundTransformer(new MinimaxOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            MinimaxAnthropicChannelType when apiFormat == ApiFormats.AnthropicMessage
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Direct,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            MinimaxAnthropicChannelType => new MinimaxOutboundTransformer(new MinimaxOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1313-1322: the openai_responses channel has exactly
            // one default endpoint, the responses API (channel_endpoint.go:233),
            // served by the shared Responses outbound whatever the inbound
            // format. The transport comes from the base URL's ws/wss scheme:
            // primaryEndpointTransport falls through to endpointTransport on
            // the channel base URL (channel_llm.go:276-292). The slice has no
            // endpoint records, so the scheme is the only source (C082). The
            // constructor only fails on a missing key provider, which the key
            // check above rules out, so the reference's error wrap is
            // unreachable here too.
            OpenaiResponsesChannelType => CreateResponsesOutbound(
                request,
                endpointBaseUrl,
                provider,
                selectedEndpoint is null
                    ? ChannelEndpoints.PrimaryEndpointTransport(
                        candidate.Endpoints,
                        candidate.BaseUrl,
                        ApiFormats.OpenAIResponse)
                    : endpointTransport,
                endpointPath,
                candidate.ChannelId),
            // channel_llm.go:1133-1143: DashScope's OpenAI-compatible outbound
            // with the tool-call merge, enable_thinking mapping and stream
            // filter; the default endpoint set is chat only
            // (channel_endpoint.go:313).
            BailianChannelType => new BailianOutboundTransformer(new BailianOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1145-1155: the Bailian responses channel has
            // exactly one endpoint (channel_endpoint.go:314), served by the
            // shared Responses outbound.
            BailianResponsesChannelType => CreateResponsesOutbound(
                request,
                endpointBaseUrl,
                provider,
                endpointTransport,
                endpointPath,
                candidate.ChannelId),
            // channel_llm.go:1157-1167: bailian_anthropic and moonshot_coding
            // share the plain Anthropic direct outbound.
            BailianAnthropicChannelType or MoonshotCodingChannelType
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Direct,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            // channel_llm.go:1096-1108: chat plus the asynchronous image task
            // protocol (channel_endpoint.go:309-312). The reference puts the
            // channel's proxy-aware HTTP client on the config, and the image
            // task protocol uses it for polling and downloads; the inference
            // request itself follows the same client through the pipeline
            // executor (ChannelProxyExecutors, §177).
            ModelscopeChannelType => new ModelscopeOutboundTransformer(new ModelscopeOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
                HttpClient = channelExecutor,
            }),
            // channel_llm.go:720-728: doubao/volcengine install the Doubao
            // outbound as the channel's primary transformer; every default
            // endpoint (chat plus seedance/video, channel_endpoint.go:271-275)
            // shares it.
            DoubaoChannelType or VolcengineChannelType
                => CreateDoubaoOutbound(candidate, provider),
            // channel_llm.go:1003-1015: doubao_anthropic speaks Anthropic
            // Messages through the Doubao platform; the family chat outbound
            // serves the other endpoints (channel_llm.go:598-604).
            DoubaoAnthropicChannelType when apiFormat == ApiFormats.AnthropicMessage
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Doubao,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            DoubaoAnthropicChannelType => CreateDoubaoOutbound(candidate, provider),
            // channel_llm.go:898: volcengine_anthropic uses the plain Anthropic
            // direct platform for Messages.
            VolcengineAnthropicChannelType when apiFormat == ApiFormats.AnthropicMessage
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Direct,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            VolcengineAnthropicChannelType => CreateDoubaoOutbound(candidate, provider),
            // channel_llm.go:511-520: a zenmux/video endpoint is built for
            // every ZenMux channel type, but only when the channel lists it
            // (channel_endpoint.go). The slice has no endpoint records, so the
            // persisted-format lookup reaches the native transformer directly
            // (C082).
            ZenmuxChannelType or ZenmuxResponsesChannelType
                or ZenmuxAnthropicChannelType or ZenmuxVideoChannelType
                when apiFormat == ApiFormats.ZenmuxVideo
                => CreateZenmuxOutbound(candidate, provider),
            // channel_llm.go:1269-1279: a plain ZenMux channel's primary
            // outbound is the shared OpenAI one; a responses-format request is
            // served by the Responses outbound, like the openai channel type.
            ZenmuxChannelType when apiFormat is ApiFormats.OpenAIResponse
                or ApiFormats.OpenAIResponseWebSocket
                or ApiFormats.OpenAIResponseCompact
                => CreateResponsesOutbound(
                    request,
                    selectedEndpoint is null ? ZenmuxBaseUrl(candidate) : endpointBaseUrl,
                    provider,
                    endpointTransport,
                    endpointPath,
                    candidate.ChannelId),
            ZenmuxChannelType => OpenAiOutboundTransformer.Create(
                ZenmuxBaseUrl(candidate),
                provider,
                provider.KeyIndex),
            // channel_llm.go:789-799: zenmux_responses has exactly one
            // endpoint, the responses API (channel_endpoint.go:279-281).
            ZenmuxResponsesChannelType => CreateResponsesOutbound(
                request,
                selectedEndpoint is null ? ZenmuxBaseUrl(candidate) : endpointBaseUrl,
                provider,
                endpointTransport,
                endpointPath,
                candidate.ChannelId),
            // channel_llm.go:898: zenmux_anthropic is the plain Anthropic
            // direct platform on the ZenMux Anthropic base URL.
            ZenmuxAnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.Direct,
                BaseUrl = ZenmuxBaseUrl(candidate),
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1293-1302: the zenmux_video channel's primary
            // outbound is the native transformer, which also serves chat
            // through its embedded OpenAI outbound.
            ZenmuxVideoChannelType => CreateZenmuxOutbound(candidate, provider),
            // channel_llm.go:1218-1276: github_copilot's transformer is
            // OAuth-driven and routes GPT-5+/Grok models to the Responses API
            // internally, so no ApiFormat branching is needed here
            // (channel_endpoint.go:322).
            GithubCopilotChannelType
                => CreateGithubCopilotOutbound(
                    candidate,
                    channelExecutor,
                    request,
                    copilotTokens),
            // channel_llm.go:1109-1118: Gemini's OpenAI-compatible endpoint;
            // the default endpoint set is chat only (channel_endpoint.go:253).
            GeminiOpenAiChannelType => CreateGeminiOpenAiOutbound(candidate, provider),
            // channel_llm.go:1327-1335: gemini and zenmux_gemini share the
            // native Gemini transformer; zenmux_gemini falls back to the
            // ZenMux Vertex base URL when the channel stores none
            // (channel_llm.go:695-705). The constructor cannot fail, so no
            // error wrapping is needed.
            GeminiChannelType or ZenmuxGeminiChannelType => new GeminiOutboundTransformer(new GeminiOutboundConfig
            {
                BaseUrl = candidate.Type == ZenmuxGeminiChannelType
                    ? ZenmuxBaseUrl(candidate)
                    : candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1336-1346: the gemini_vertex channel pins the
            // Vertex platform (URL shape + function ID clearing).
            GeminiVertexChannelType => new GeminiOutboundTransformer(new GeminiOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                BaseUrlProvider = CreateVertexBaseUrlProvider(
                    candidate,
                    candidate.BaseUrl ?? string.Empty,
                    provider),
                ApiKeyProvider = provider,
                PlatformType = GeminiConstants.PlatformVertex,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1375-1391: antigravity wraps the native Gemini
            // outbound in the project envelope and authenticates exclusively
            // through OAuth tokens minted from the legacy refreshToken|projectID
            // key (channel_endpoint.go:325: a single Gemini-contents endpoint).
            AntigravityChannelType => CreateAntigravityOutbound(
                candidate,
                channelExecutor,
                grainFactory),
            _ => throw new InvalidOperationException(
                "unsupported channel type: " + candidate.Type + " for channel " + candidate.ChannelName),
        };
    }

    /// <summary>
    /// The primary outbound API format of a channel type: the first entry of
    /// <c>defaultEndpointsForChannelType</c> (channel_endpoint.go:225-345).
    /// Returns null for the types the reference registers no default endpoint
    /// for (opencode_go_anthropic) or does not model.
    /// </summary>
    public static string? PrimaryApiFormat(string channelType)
        => channelType switch
        {
            OpenAiChannelType or ZenmuxChannelType or AtlascloudChannelType
                or VercelChannelType or DeepinfraChannelType or QiniuChannelType
                or PpioChannelType or SiliconflowChannelType or AihubmixChannelType
                or BurncloudChannelType or GithubChannelType or EvolinkChannelType
                or GroqChannelType or ClineChannelType or GeminiOpenAiChannelType
                or DeepSeekChannelType or FireworksChannelType or DoubaoChannelType
                or VolcengineChannelType or MoonshotChannelType or ZhipuChannelType
                or ZaiChannelType or XiaomiChannelType or OpenRouterChannelType
                or LongcatChannelType or MinimaxChannelType or ModelscopeChannelType
                or BailianChannelType or GithubCopilotChannelType or CerebrasChannelType
                or NanogptChannelType or OpenCodeGoChannelType or CommandcodeChannelType
                or XaiChannelType or CloudflareChannelType
                => ApiFormats.OpenAIChatCompletion,
            OpenaiResponsesChannelType or ZenmuxResponsesChannelType or CodexChannelType
                or FennoChannelType or BailianResponsesChannelType
                or NanogptResponsesChannelType or XaiResponsesChannelType
                or XaiSubscriptionChannelType
                => ApiFormats.OpenAIResponse,
            AnthropicChannelType or ZenmuxAnthropicChannelType or AnthropicAwsChannelType
                or AnthropicGcpChannelType or QiniuAnthropicChannelType
                or AihubmixAnthropicChannelType or EvolinkAnthropicChannelType
                or DeepseekAnthropicChannelType or MoonshotAnthropicChannelType
                or ZhipuAnthropicChannelType or ZaiAnthropicChannelType
                or XiaomiAnthropicChannelType or LongcatAnthropicChannelType
                or MinimaxAnthropicChannelType or DoubaoAnthropicChannelType
                or VolcengineAnthropicChannelType or BailianAnthropicChannelType
                or MoonshotCodingChannelType or ClaudeCodeChannelType
                or OllamaAnthropicChannelType or CommandcodeAnthropicChannelType
                => ApiFormats.AnthropicMessage,
            GeminiChannelType or ZenmuxGeminiChannelType or GeminiVertexChannelType
                or AntigravityChannelType
                => ApiFormats.GeminiContents,
            JinaChannelType => ApiFormats.JinaRerank,
            OllamaChannelType => ApiFormats.OllamaChat,
            TypeSafeChannelType => ApiFormats.TypeSafeSystemOne,
            ZenmuxVideoChannelType => ApiFormats.ZenmuxVideo,
            _ => null,
        };

    /// <summary>
    /// Port of <c>buildNonDefaultEndpointOutbound</c>'s three auto-detection
    /// branches (channel_llm.go:393-560): the generic transformer a channel
    /// gets for an API format outside its default endpoint set. Codex/Fenno
    /// responses and Command Code messages keep their family transformers,
    /// exactly like the reference switch; chat falls back to the family
    /// transformer only when the base URL names the provider version
    /// (channel_llm.go:610-628).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The format is not one of the three detection surfaces.
    /// </exception>
    /// <param name="apiKeySelection">
    /// The shared multi-key selection state; endpoint detection passes it so the
    /// probe authenticates the same way a real request would, but never
    /// publishes a snapshot, exactly like the reference's other channel builds.
    /// </param>
    public static IOutboundTransformer CreateNonDefaultEndpointOutbound(
        ChannelCandidate candidate,
        string apiFormat,
        DateTimeOffset now,
        ChannelApiKeySelectionRegistry? apiKeySelection = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var baseUrl = ResolveChannelBaseUrl(candidate);

        // channel_llm.go:408-412: every non-default outbound of a Command Code
        // channel validates the base URL before the format switch.
        if (candidate.Type is CommandcodeChannelType or CommandcodeAnthropicChannelType)
        {
            var parsed = Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var commandCodeUri);
            if (!parsed || commandCodeUri!.Scheme != Uri.UriSchemeHttps || commandCodeUri.Host.Length == 0)
            {
                throw new InvalidOperationException("Command Code base URL must use HTTPS");
            }
        }

        var overrideApiKey = (candidate.ApiKeyOverride ?? string.Empty).Trim();
        var enabledKeys = ChannelCredentials.GetEnabledApiKeys(
            candidate.Credentials,
            candidate.DisabledApiKeys,
            now);
        var allKeys = ChannelCredentials.GetAllApiKeys(candidate.Credentials);
        var configuredApiKeys = ChannelCredentials.GetApiKeys(candidate.Credentials);

        var settings = ChannelTransformOptions.SettingsOf(candidate);
        var strategy = ChannelApiKeyStrategies.StrategyOf(settings);
        var apiKeyState = apiKeySelection?.StateFor(
            candidate.ChannelId,
            strategy,
            ChannelApiKeyStrategies.SwitchAfterOf(settings));

        var provider = overrideApiKey.Length > 0
            ? ChannelApiKeyProvider.ForOverride(overrideApiKey, allKeys)
            : enabledKeys.Count > 1 || configuredApiKeys.Count > 1
                ? ChannelApiKeyProvider.ForSelection(
                    ChannelApiKeyStrategies.Select(
                        settings,
                        apiKeyState,
                        enabledKeys,
                        configuredApiKeys,
                        allKeys,
                        ChannelCredentials.GetDisabledApiKeySet(candidate.DisabledApiKeys),
                        traceId: null),
                    allKeys)
                : new ChannelApiKeyProvider(enabledKeys, allKeys);

        // channel_llm.go:427-435: detection probes a synthetic endpoint that
        // carries only the format, so it inherits the channel base URL and has
        // no path; the transport comes from that resolved base URL's scheme,
        // exactly like the reference's ep.BaseURL assignment before the switch.
        return CreateUserEndpointOutbound(
            candidate,
            new LlmRequest { ApiFormat = apiFormat },
            new ChannelEndpoint { ApiFormat = apiFormat, BaseUrl = baseUrl },
            provider,
            channelExecutor: null,
            grainFactory: null);
    }

    /// <summary>
    /// Port of <c>buildNonDefaultEndpointOutbound</c> (channel_llm.go:431-616):
    /// every user-configured endpoint is built by API format, not by channel
    /// type. Only the family formats re-enter the channel family (Codex/Fenno
    /// responses and alpha search, Command Code messages, Minimax images,
    /// ZenMux video), exactly like the reference switch.
    /// </summary>
    private static IOutboundTransformer CreateUserEndpointOutbound(
        ChannelCandidate candidate,
        LlmRequest request,
        ChannelEndpoint endpoint,
        ChannelApiKeyProvider apiKeyProvider,
        IExecutor? channelExecutor,
        IGrainFactory? grainFactory)
    {
        // channel_llm.go:438-444: an endpoint base URL wins over the channel's;
        // an empty one inherits it before the transport is inferred, so a ws/wss
        // channel base URL also sets the endpoint transport. buildChannelWithTransformer
        // fills an empty ZenMux base URL (:695-705) before the endpoint loop
        // runs, so that default is part of the inherited channel base too.
        var channelBaseUrl = candidate.BaseUrl ?? string.Empty;
        if (channelBaseUrl.Length == 0
            && candidate.Type is ZenmuxChannelType or ZenmuxResponsesChannelType
                or ZenmuxAnthropicChannelType or ZenmuxGeminiChannelType or ZenmuxVideoChannelType)
        {
            channelBaseUrl = ZenmuxBaseUrl(candidate);
        }

        var baseUrl = endpoint.BaseUrl.Length > 0
            ? endpoint.BaseUrl
            : channelBaseUrl;
        var path = endpoint.Path;
        var transport = ChannelEndpoints.EndpointTransport(endpoint with { BaseUrl = baseUrl });

        // channel_llm.go:445-449: every non-default outbound of a Command Code
        // channel validates the resolved base URL, endpoint override included.
        if (candidate.Type is CommandcodeChannelType or CommandcodeAnthropicChannelType)
        {
            var parsed = Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var commandCodeUri);
            if (!parsed || commandCodeUri!.Scheme != Uri.UriSchemeHttps || commandCodeUri.Host.Length == 0)
            {
                throw new InvalidOperationException("Command Code base URL must use HTTPS");
            }
        }

        // channel_llm.go:450-452: only the Responses family may use WebSocket.
        if (transport == ChannelEndpointTransports.WebSocket
            && !ChannelEndpoints.IsResponsesEndpoint(endpoint.ApiFormat))
        {
            throw new InvalidOperationException(
                "websocket transport only supports api_format \"" + ApiFormats.OpenAIResponse + "\"");
        }

        switch (endpoint.ApiFormat)
        {
            case ApiFormats.OpenAIChatCompletion:
                // channel_llm.go:453-459: Cline always keeps its envelope.
                if (candidate.Type == ClineChannelType)
                {
                    return new ClineOutboundTransformer(new ClineOutboundConfig
                    {
                        BaseUrl = baseUrl,
                        EndpointPath = path,
                        ApiKeyProvider = apiKeyProvider,
                        ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
                    });
                }

                // channel_llm.go:461-478: a custom endpoint with an explicit
                // path (or a family base URL carrying its version) keeps the
                // family transformer; an empty path on a plain base URL stays
                // on the generic OpenAI route that appends /v1.
                if (path.Length > 0 || ProviderChatEndpointUsesFamilyVersion(candidate.Type, baseUrl))
                {
                    if (candidate.Type is ZaiChannelType or ZhipuChannelType
                        or ZhipuAnthropicChannelType or ZaiAnthropicChannelType)
                    {
                        return CreateZaiOutbound(
                            candidate,
                            request,
                            apiKeyProvider,
                            "v4",
                            baseUrl,
                            path);
                    }

                    if (candidate.Type is XiaomiChannelType or XiaomiAnthropicChannelType)
                    {
                        return CreateZaiOutbound(
                            candidate,
                            request,
                            apiKeyProvider,
                            "v1",
                            baseUrl,
                            path);
                    }

                    if (candidate.Type is DoubaoChannelType or VolcengineChannelType
                        or DoubaoAnthropicChannelType or VolcengineAnthropicChannelType)
                    {
                        return CreateDoubaoOutbound(candidate, apiKeyProvider, baseUrl, path);
                    }
                }

                return new OpenAiOutboundTransformer(new OpenAiOutboundConfig
                {
                    BaseUrl = baseUrl,
                    EndpointPath = path,
                    ApiKeyProvider = apiKeyProvider,
                    ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
                });
            case ApiFormats.OpenAICompletion:
                return new CompletionOutboundTransformer(new OpenAiOutboundConfig
                {
                    BaseUrl = baseUrl,
                    EndpointPath = path,
                    ApiKeyProvider = apiKeyProvider,
                    ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
                });
            case ApiFormats.OpenAIResponse:
            case ApiFormats.OpenAIResponseWebSocket:
            case ApiFormats.OpenAIResponseCompact:
                // channel_llm.go:490-502: codex and fenno keep their family
                // responses outbound (the port threads the endpoint path there
                // too, matching the live tree's buildCodexOutbound).
                if (candidate.Type is CodexChannelType or FennoChannelType
                    && ChannelEndpoints.IsResponsesEndpoint(endpoint.ApiFormat))
                {
                    return CreateCodexOutbound(
                        candidate,
                        request,
                        apiKeyProvider,
                        channelExecutor,
                        grainFactory,
                        endpoint.ApiFormat,
                        endpoint with { BaseUrl = baseUrl });
                }

                return CreateResponsesOutbound(
                    request,
                    baseUrl,
                    apiKeyProvider,
                    transport,
                    path,
                    candidate.ChannelId);
            case ApiFormats.OpenAIAlphaSearch:
                // channel_llm.go:504-514: only Codex owns alpha search.
                if (candidate.Type == CodexChannelType)
                {
                    return CreateCodexOutbound(
                        candidate,
                        request,
                        apiKeyProvider,
                        channelExecutor,
                        grainFactory,
                        endpoint.ApiFormat,
                        endpoint with { BaseUrl = baseUrl });
                }

                return new OpenAiOutboundTransformer(new OpenAiOutboundConfig
                {
                    BaseUrl = baseUrl,
                    EndpointPath = path,
                    ApiKeyProvider = apiKeyProvider,
                    ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
                });
            case ApiFormats.OpenAIDecisions:
                return new DecisionsOutboundTransformer(new DecisionsOutboundConfig
                {
                    BaseUrl = baseUrl,
                    EndpointPath = path,
                    ApiKeyProvider = apiKeyProvider,
                    ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
                });
            case ApiFormats.OpenAIEmbedding:
            case ApiFormats.OpenAIModeration:
            case ApiFormats.OpenAIImageGeneration:
            case ApiFormats.OpenAIImageEdit:
            case ApiFormats.OpenAIImageVariation:
            case ApiFormats.OpenAIVideo:
            case ApiFormats.OpenAISpeech:
            case ApiFormats.OpenAITranscription:
            case ApiFormats.OpenAITranslation:
                // channel_llm.go:516-543: MiniMax images use their own API;
                // Codex/Fenno image endpoints keep the codex outbound; every
                // other capability falls back to the generic OpenAI one.
                if (candidate.Type == MinimaxChannelType
                    && endpoint.ApiFormat == ApiFormats.OpenAIImageGeneration)
                {
                    return new MinimaxOutboundTransformer(new MinimaxOutboundConfig
                    {
                        BaseUrl = baseUrl,
                        EndpointPath = path,
                        ApiKeyProvider = apiKeyProvider,
                        ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
                    });
                }

                if (candidate.Type is CodexChannelType or FennoChannelType
                    && endpoint.ApiFormat is ApiFormats.OpenAIImageGeneration or ApiFormats.OpenAIImageEdit)
                {
                    return CreateCodexOutbound(
                        candidate,
                        request,
                        apiKeyProvider,
                        channelExecutor,
                        grainFactory,
                        endpoint.ApiFormat,
                        endpoint with { BaseUrl = baseUrl });
                }

                return new OpenAiOutboundTransformer(new OpenAiOutboundConfig
                {
                    BaseUrl = baseUrl,
                    EndpointPath = path,
                    ApiKeyProvider = apiKeyProvider,
                    ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
                });
            case ApiFormats.ZenmuxVideo:
                // channel_llm.go:544-553: the video surface only exists for
                // the ZenMux channel family.
                if (candidate.Type is not (ZenmuxChannelType or ZenmuxResponsesChannelType
                    or ZenmuxAnthropicChannelType or ZenmuxGeminiChannelType or ZenmuxVideoChannelType))
                {
                    throw new InvalidOperationException(
                        "api_format \"" + endpoint.ApiFormat + "\" is only supported by ZenMux channel types");
                }

                return CreateZenmuxOutbound(candidate, apiKeyProvider, baseUrl, path);
            case ApiFormats.AnthropicMessage:
                // channel_llm.go:554-573: Command Code accepts only Bearer auth,
                // for both its channel types; every other channel keeps the
                // direct X-API-Key platform.
                return new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = candidate.Type is CommandcodeChannelType or CommandcodeAnthropicChannelType
                        ? AnthropicPlatformType.CommandCode
                        : AnthropicPlatformType.Direct,
                    BaseUrl = baseUrl,
                    EndpointPath = path,
                    ApiKeyProvider = apiKeyProvider,
                    ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
                });
            case ApiFormats.GeminiContents:
            case ApiFormats.GeminiEmbedding:
                var geminiConfig = new GeminiOutboundConfig
                {
                    BaseUrl = baseUrl,
                    EndpointPath = path,
                    PlatformType = candidate.Type == GeminiVertexChannelType
                        ? GeminiConstants.PlatformVertex
                        : null,
                    ApiKeyProvider = apiKeyProvider,
                    ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
                };
                if (candidate.Type == GeminiVertexChannelType)
                {
                    geminiConfig.BaseUrlProvider = CreateVertexBaseUrlProvider(
                        candidate,
                        baseUrl,
                        apiKeyProvider);
                }

                return new GeminiOutboundTransformer(geminiConfig);
            case ApiFormats.JinaRerank:
            case ApiFormats.JinaEmbedding:
                return new JinaOutboundTransformer(new JinaOutboundConfig
                {
                    BaseUrl = baseUrl,
                    EndpointPath = path,
                    ApiKeyProvider = apiKeyProvider,
                    ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
                });
            case ApiFormats.TypeSafeSystemOne:
                if (candidate.Type == CloudflareChannelType)
                {
                    return new CloudflareSystemOneOutboundTransformer(
                        new CloudflareSystemOneConfig
                        {
                            BaseUrl = baseUrl,
                            ApiKeyProvider = apiKeyProvider,
                            EndpointPath = path,
                            ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
                        });
                }

                return TypeSafeOutboundTransformer.Create(
                    baseUrl,
                    apiKeyProvider,
                    apiKeyProvider.KeyIndex,
                    path);
            default:
                throw new InvalidOperationException(
                    "unsupported api_format \"" + endpoint.ApiFormat + "\"");
        }
    }

    /// <summary>
    /// The base URL <c>buildNonDefaultEndpointOutbound</c> starts from: the
    /// entity's raw <c>base_url</c> (channel_llm.go:402). It deliberately skips
    /// <c>buildChannelWithTransformer</c>'s ZenMux defaults, so a ZenMux channel
    /// without an explicit base URL gets the reference's invalid_request probe
    /// instead of its primary default route.
    /// </summary>
    public static string ResolveChannelBaseUrl(ChannelCandidate candidate)
        => candidate.BaseUrl ?? string.Empty;

    /// <summary>
    /// Port of providerChatEndpointUsesFamilyVersion (channel_llm.go:610-628):
    /// Xiaomi always uses its family transformer; Zai/Zhipu/Bailian-family
    /// channels do only when the base URL carries v4/v3.
    /// </summary>
    private static bool ProviderChatEndpointUsesFamilyVersion(string channelType, string baseUrl)
    {
        if (channelType is XiaomiChannelType or XiaomiAnthropicChannelType)
        {
            return true;
        }

        var version = channelType switch
        {
            ZaiChannelType or ZhipuChannelType or ZhipuAnthropicChannelType
                or ZaiAnthropicChannelType => "v4",
            DoubaoChannelType or VolcengineChannelType or DoubaoAnthropicChannelType
                or VolcengineAnthropicChannelType => "v3",
            _ => null,
        };

        return version is not null && UrlPathContainsSegment(baseUrl, version);
    }

    /// <summary>Port of urlPathContainsSegment (channel_llm.go:630-638).</summary>
    private static bool UrlPathContainsSegment(string rawUrl, string segment)
    {
        var trimmed = rawUrl.EndsWith("##", StringComparison.Ordinal)
            ? rawUrl[..^2]
            : rawUrl;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.AbsolutePath
            .Trim('/')
            .Split('/')
            .Contains(segment, StringComparer.Ordinal);
    }

    /// <summary>
    /// Port of the zai/xiaomi branch of <c>newProviderChatOutbound</c>
    /// (channel_llm.go:575-597). The request_id fallback reads the shared
    /// session id, which the endpoint threads onto the wire request (C062).
    /// </summary>
    private static ZaiOutboundTransformer CreateZaiOutbound(
        ChannelCandidate candidate,
        LlmRequest request,
        ChannelApiKeyProvider apiKeyProvider,
        string version,
        string? baseUrl = null,
        string endpointPath = "")
    {
        return new ZaiOutboundTransformer(new ZaiOutboundConfig
        {
            BaseUrl = baseUrl ?? candidate.BaseUrl ?? string.Empty,
            Version = version,
            EndpointPath = endpointPath,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
            SessionIdProvider = () => request.RawRequest?.SessionId,
        });
    }

    /// <summary>
    /// Port of the doubao/volcengine branch of <c>newProviderChatOutbound</c>
    /// (channel_llm.go:598-604). The default chat endpoint passes an empty
    /// endpoint path, so the transformer normalizes the base URL to the Ark v3
    /// route. The reference's Doubao config carries no HTTP client: the request
    /// runs through the channel's customized executor, which the port resolves
    /// from <c>settings.proxy</c> (C105's proxy residual is closed through
    /// <see cref="ChannelProxyExecutors"/>).
    /// </summary>
    private static DoubaoOutboundTransformer CreateDoubaoOutbound(
        ChannelCandidate candidate,
        ChannelApiKeyProvider apiKeyProvider,
        string? baseUrl = null,
        string endpointPath = "")
        => new(new DoubaoOutboundConfig
        {
            BaseUrl = baseUrl ?? candidate.BaseUrl ?? string.Empty,
            EndpointPath = endpointPath,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
        });

    /// <summary>
    /// Port of the empty-base-URL defaulting in <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:695-705). Only the ported ZenMux types carry a default.
    /// </summary>
    private static string ZenmuxBaseUrl(ChannelCandidate candidate)
    {
        var baseUrl = candidate.BaseUrl ?? string.Empty;
        if (baseUrl.Length != 0)
        {
            return baseUrl;
        }

        return candidate.Type switch
        {
            ZenmuxAnthropicChannelType => ZenmuxAnthropicBaseUrl,
            ZenmuxGeminiChannelType => ZenmuxGeminiBaseUrl,
            _ => ZenmuxOpenAiBaseUrl,
        };
    }

    /// <summary>
    /// Port of the ZenMux branches of <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:1293-1302) and the zenmux/video case of
    /// <c>newProviderChatOutbound</c> (channel_llm.go:511-520). The endpoint
    /// path defaults to /videos; the slice has no endpoint records, so a
    /// custom path is not modelled yet (C082).
    /// </summary>
    private static ZenmuxOutboundTransformer CreateZenmuxOutbound(
        ChannelCandidate candidate,
        ChannelApiKeyProvider apiKeyProvider,
        string? baseUrl = null,
        string endpointPath = "")
        => new(new ZenmuxOutboundConfig
        {
            BaseUrl = baseUrl ?? ZenmuxBaseUrl(candidate),
            EndpointPath = endpointPath,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
        });

    /// <summary>
    /// Port of the gemini_openai branch of <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:1109-1118). The reference wraps the constructor error
    /// with <c>failed to create outbound transformer</c>, including the
    /// missing-base-URL validation.
    /// </summary>
    private static GeminiOpenAiOutboundTransformer CreateGeminiOpenAiOutbound(
        ChannelCandidate candidate,
        ChannelApiKeyProvider apiKeyProvider)
    {
        try
        {
            return new GeminiOpenAiOutboundTransformer(new GeminiOpenAiOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = apiKeyProvider,
                ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create outbound transformer: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of the anthropic_gcp branch of <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:1069-1085): the reference wraps the constructor error
    /// with <c>failed to create outbound transformer</c>, and reports missing
    /// GCP credentials before calling it.
    /// </summary>
    private static IOutboundTransformer CreateAnthropicGcpOutbound(ChannelCandidate candidate)
    {
        var gcp = ChannelCredentials.GetGcpCredential(candidate.Credentials);
        if (gcp is null)
        {
            throw new InvalidOperationException(
                "GCP credentials are required for anthropic_vertex channel");
        }

        try
        {
            return AnthropicOutboundTransformer.CreateWithConfig(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.Vertex,
                Region = gcp.Region,
                ProjectId = gcp.ProjectId,
                JsonData = gcp.JsonData,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create outbound transformer: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of the antigravity branch of <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:1375-1391). The transformer consumes the single legacy
    /// APIKey field (refreshToken|projectID) directly. The reference registers
    /// <c>onTokenRefreshed</c> and <c>setupAutoRefresh</c> on the provider the
    /// transformer builds (<c>GetTokenProvider()</c> is non-null exactly when
    /// the key carries a refresh token). With a grain factory (§194) the refresh
    /// and its write-back run in the channel's token grain instead; the local
    /// provider still exists, so the transformer's own behavior is unchanged.
    /// </summary>
    private static AntigravityTransformer CreateAntigravityOutbound(
        ChannelCandidate candidate,
        IExecutor? channelExecutor,
        IGrainFactory? grainFactory)
    {
        try
        {
            var apiKey = ChannelCredentials.GetLegacyApiKey(candidate.Credentials);

            return AntigravityTransformer.Create(
                new AntigravityConfig
                {
                    BaseURL = candidate.BaseUrl ?? string.Empty,
                    APIKey = apiKey,
                },
                new AntigravityTransformerOptions
                {
                    HttpClient = channelExecutor,
                    TokenProvider = CreateAntigravityTokenProvider(
                        candidate,
                        apiKey,
                        grainFactory),
                });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create antigravity outbound transformer: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// The grain-backed getter seeded from the parsed refresh token
    /// (transformer.go:94-122). Null keeps the transformer's local provider:
    /// without a grain factory, or when the legacy key has no refresh token
    /// (the reference's <c>initTokenProvider</c> then leaves the provider nil
    /// and Auth reports <c>no OAuth token provider configured</c>).
    /// </summary>
    private static ITokenGetter? CreateAntigravityTokenProvider(
        ChannelCandidate candidate,
        string apiKey,
        IGrainFactory? grainFactory)
    {
        if (grainFactory is null)
        {
            return null;
        }

        var (refreshToken, _) = AntigravityTransformer.ParseCredentials(apiKey);
        if (refreshToken.Length == 0)
        {
            return null;
        }

        // Same seed initTokenProvider builds: the Antigravity client id and
        // scopes, no access token — the first Get refreshes through the
        // channel's proxy-aware client in ChannelTokenSource (§194).
        return new GrainTokenGetter(grainFactory, CreateTokenSeed(candidate, new OAuthCredentials
        {
            RefreshToken = refreshToken,
            ClientId = AntigravityConstants.ClientID,
            Scopes = [.. AntigravityConstants.Scopes],
        }));
    }

    /// <summary>Port of the claudecode branch of buildChannelWithTransformer.</summary>
    private static ClaudeCodeOutboundTransformer CreateClaudeCodeOutbound(
        ChannelCandidate candidate,
        ChannelApiKeyProvider apiKeyProvider,
        IExecutor? channelExecutor,
        IGrainFactory? grainFactory)
    {
        var baseUrl = candidate.BaseUrl ?? string.Empty;
        // strconv.Itoa(c.ID): the stable identity behind the deterministic
        // fake user id.
        var accountIdentity = candidate.ChannelId.ToString(CultureInfo.InvariantCulture);

        if (!ChannelCredentials.IsOAuth(candidate.Credentials))
        {
            // Non-OAuth: use the API key provider for multi-key rotation. The
            // reference wraps this provider in ChannelAPIKeyContextProvider, so
            // the execution audit records the key actually sent (C096).
            return new ClaudeCodeOutboundTransformer(new ClaudeCodeParams
            {
                TokenProvider = new ApiKeyTokenProvider(_ => apiKeyProvider.Get()),
                BaseUrl = baseUrl,
                IsOfficial = false,
                AccountIdentity = accountIdentity,
                AuditApiKey = true,
                ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
            });
        }

        var credentials = ParseOAuthCredentials(candidate, "claudecode");

        // The reference owns one provider per channel; the grain owns it here
        // (§146). Without a grain factory (endpoint detection, tests) the
        // throwaway provider keeps the direct path working.
        ITokenGetter tokenProvider = grainFactory is not null
            ? new GrainTokenGetter(grainFactory, CreateTokenSeed(candidate, credentials))
            : ClaudeCodeTokenProvider.Create(new TokenProviderParams
            {
                Credentials = credentials,
                HttpClient = channelExecutor,
            });

        return new ClaudeCodeOutboundTransformer(new ClaudeCodeParams
        {
            TokenProvider = tokenProvider,
            BaseUrl = baseUrl,
            IsOfficial = true,
            AccountIdentity = accountIdentity,
        });
    }

    /// <summary>
    /// Re-marshals the channel's typed <c>oauth</c> object (canonical field
    /// order, empty fields omitted, <c>id_token</c> dropped like the
    /// reference's struct copy), falling back to parsing the legacy apiKey
    /// string as OAuth JSON, then parses the credentials document. The two
    /// stages keep the reference's error copy (channel_llm.go:938-949, :345-356,
    /// :1239-1250).
    /// </summary>
    private static OAuthCredentials ParseOAuthCredentials(
        ChannelCandidate candidate,
        string channelType)
    {
        var credentialsJson = ChannelCredentials.GetLegacyApiKey(candidate.Credentials);
        var oauthJson = ChannelCredentials.GetOAuthObjectJson(candidate.Credentials);
        if (oauthJson is not null)
        {
            try
            {
                // The reference builds a fresh oauth.OAuthCredentials without
                // id_token; failing to decode or marshal it here is that copy's
                // encode stage.
                var oauth = OAuthCredentialsJson.ParseOAuthObject(oauthJson);
                credentialsJson = new OAuthCredentials
                {
                    AccessToken = oauth.AccessToken,
                    RefreshToken = oauth.RefreshToken,
                    ClientId = oauth.ClientId,
                    ExpiresAt = oauth.ExpiresAt,
                    TokenType = oauth.TokenType,
                    Scopes = oauth.Scopes,
                }.ToJson();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "failed to encode " + channelType + " oauth credentials: " + ex.Message,
                    ex);
            }
        }

        try
        {
            return OAuthCredentialsJson.ParseCredentialsJson(credentialsJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to parse " + channelType + " oauth credentials: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of the github_copilot branch of <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:1218-1276). The typed <c>oauth</c> object is re-marshalled
    /// without <c>id_token</c>, parsed, and wrapped in the device-flow token
    /// provider that mints Copilot tokens.
    /// </summary>
    /// <remarks>
    /// The reference also registers the provider's auto-refresh
    /// (channel_llm.go:1270-1275), but with the exchanger configured
    /// <c>GetToken</c> always goes through the exchange cache and never checks
    /// or refreshes the OAuth credentials, so that loop only re-runs the
    /// exchange. The port reproduces the observable state — the per-channel
    /// exchanger and its SHA-256 keyed token cache — through
    /// <see cref="ChannelCopilotTokenCache"/> and deliberately leaves the
    /// credentials unrefreshed (a token grain would synthesize an expiry and
    /// run a GitHub refresh grant the reference never issues, §194).
    /// </remarks>
    private static CopilotOutboundTransformer CreateGithubCopilotOutbound(
        ChannelCandidate candidate,
        IExecutor? channelExecutor,
        LlmRequest request,
        ChannelCopilotTokenCache? copilotTokens)
    {
        if (!ChannelCredentials.IsOAuth(candidate.Credentials))
        {
            throw new InvalidOperationException(
                "missing oauth credentials for channel " + candidate.ChannelName);
        }

        var credentialsJson = ChannelCredentials.GetLegacyApiKey(candidate.Credentials).Trim();
        if (credentialsJson.Length == 0)
        {
            throw new InvalidOperationException(
                "github_copilot channel " + candidate.ChannelName + " has no credentials");
        }

        var oauthJson = ChannelCredentials.GetOAuthObjectJson(candidate.Credentials);
        if (oauthJson is not null)
        {
            try
            {
                // Same shape as the reference's re-marshal: id_token is not
                // part of the Copilot credential document.
                var oauth = OAuthCredentialsJson.ParseOAuthObject(oauthJson);
                credentialsJson = new OAuthCredentials
                {
                    AccessToken = oauth.AccessToken,
                    RefreshToken = oauth.RefreshToken,
                    ClientId = oauth.ClientId,
                    ExpiresAt = oauth.ExpiresAt,
                    TokenType = oauth.TokenType,
                    Scopes = oauth.Scopes,
                }.ToJson();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to encode github_copilot oauth credentials for channel "
                    + candidate.ChannelName + ": " + ex.Message,
                    ex);
            }
        }

        OAuthCredentials credentials;
        try
        {
            credentials = OAuthCredentialsJson.ParseCredentialsJson(credentialsJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "github_copilot channel " + candidate.ChannelName
                + " has invalid credentials: " + ex.Message,
                ex);
        }

        CopilotTokenProvider tokenProvider;
        try
        {
            tokenProvider = new CopilotTokenProvider(new CopilotTokenProviderParams
            {
                Credentials = credentials,
                HttpClient = channelExecutor,
                TokenExchanger = copilotTokens?.ForChannel(
                    candidate.ChannelId,
                    candidate.UpdatedAt,
                    channelExecutor),
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create CopilotTokenProvider: " + ex.Message,
                ex);
        }

        try
        {
            return new CopilotOutboundTransformer(new CopilotOutboundParams
            {
                TokenProvider = tokenProvider,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                // The reference's Copilot outbound forwards the ctx to its inner
                // Responses transformer, so the trace session reaches it (C062).
                SessionIdProvider = () => request.RawRequest?.SessionId,
                SessionScopeProvider = () => request.RawRequest?.SessionScope,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create github_copilot outbound transformer: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of the xai_subscription branch of <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:867-884): resolve the OAuth credentials, build the
    /// refreshing token provider over the xAI endpoints, and wrap the Responses
    /// outbound. The grain-backed provider runs the refresh loop and the
    /// <c>OnRefreshed</c> persistence callback (§146); the local fallback used
    /// without a grain factory has neither.
    /// </summary>
    private static SubscriptionOutboundTransformer CreateXaiSubscriptionOutbound(
        ChannelCandidate candidate,
        IExecutor? channelExecutor,
        LlmRequest request,
        IGrainFactory? grainFactory)
    {
        OAuthCredentials credentials;
        try
        {
            credentials = ResolveOAuthCredentials(candidate);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "xAI subscription channel " + candidate.ChannelName
                + " has invalid credentials: " + ex.Message,
                ex);
        }

        ITokenGetter tokens = grainFactory is not null
            ? new GrainTokenGetter(grainFactory, CreateTokenSeed(candidate, credentials))
            : SubscriptionToken.NewTokenProvider(new SubscriptionTokenProviderParams
            {
                Credentials = credentials,
                HttpClient = channelExecutor,
            });

        try
        {
            // The wrapper forwards the request to its inner Responses outbound,
            // which reads the trace session from the request context (C062).
            return SubscriptionOutboundTransformer.NewOutboundTransformer(
                tokens,
                () => request.RawRequest?.SessionId,
                () => request.RawRequest?.SessionScope);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create xAI subscription outbound transformer: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of <c>(*ChannelCredentials).ResolveOAuthCredentials</c>
    /// (internal/objects/channel.go:460-468): a typed <c>oauth</c> object with a
    /// non-blank access token wins; otherwise the legacy <c>apiKey</c> string is
    /// parsed as the credentials document.
    /// </summary>
    private static OAuthCredentials ResolveOAuthCredentials(ChannelCandidate candidate)
        => OAuthCredentialsJson.ResolveChannelCredentials(candidate.Credentials);

    /// <summary>Port of <c>buildCodexOutbound</c> (channel_llm.go:331-425).</summary>
    private static CodexOutboundTransformer CreateCodexOutbound(
        ChannelCandidate candidate,
        LlmRequest request,
        ChannelApiKeyProvider apiKeyProvider,
        IExecutor? channelExecutor,
        IGrainFactory? grainFactory,
        string apiFormat,
        ChannelEndpoint? endpoint)
    {
        var baseUrl = endpoint is { BaseUrl.Length: > 0 }
            ? endpoint.BaseUrl
            : candidate.BaseUrl ?? string.Empty;

        // channel_llm.go:288-329: a user endpoint carries its own transport; a
        // default HTTP Responses endpoint uses primaryEndpointTransport; the
        // default WebSocket entry is explicit; alpha/image defaults inherit no
        // transport.
        var transport = endpoint is not null
            ? ChannelEndpoints.EndpointTransport(endpoint)
            : apiFormat == ApiFormats.OpenAIResponseWebSocket
                ? ChannelEndpointTransports.WebSocket
                : apiFormat == ApiFormats.OpenAIResponse
                    ? ChannelEndpoints.PrimaryEndpointTransport(
                        candidate.Endpoints,
                        candidate.BaseUrl,
                        ApiFormats.OpenAIResponse)
                    : string.Empty;

        // buildNonDefaultEndpointOutbound passes the endpoint path into the
        // Codex outbound for responses-family formats (channel_llm.go:502) and
        // as the alpha-search path for alpha search (channel_llm.go:513).
        var endpointPath = endpoint?.Path ?? string.Empty;
        var alphaSearchPath = apiFormat == ApiFormats.OpenAIAlphaSearch ? endpointPath : string.Empty;
        var responsesPath = apiFormat == ApiFormats.OpenAIAlphaSearch ? string.Empty : endpointPath;

        // Only key channels wrap the per-attempt key provider; OAuth channels
        // refresh their own token and have no channel key to audit (C096).
        var auditsApiKey = !ChannelCredentials.IsOAuth(candidate.Credentials);

        return new CodexOutboundTransformer(new CodexOutboundParams
        {
            TokenProvider = CreateCodexTokenProvider(
                candidate,
                apiKeyProvider,
                channelExecutor,
                grainFactory),
            BaseUrl = baseUrl,
            Transport = transport,
            AlphaSearchPath = alphaSearchPath,
            EndpointPath = responsesPath,
            ImageMainModel = candidate.DefaultTestModel,
            // Port-only: keeps the WebSocket pool scoped to the owning channel
            // like the reference's per-channel outbound cache (C072).
            ChannelId = candidate.ChannelId,
            // The reference reads the trace session from the request context;
            // the port threads the captured session through the wire request
            // (C062). Both are null until the inbound layer captures one.
            SessionIdProvider = () => request.RawRequest?.SessionId,
            SessionScopeProvider = () => request.RawRequest?.SessionScope,
            AuditApiKey = auditsApiKey,
            ChannelApiKeyIndex = auditsApiKey ? apiKeyProvider.KeyIndex : null,
        });
    }

    /// <summary>
    /// The normalized user endpoint that supplies the outbound for the selected
    /// format, honoring the reference's raw-format alias: a stored HTTP
    /// Responses endpoint that normalizes to WebSocket is registered under both
    /// keys when no stored WebSocket entry exists
    /// (buildChannelWithOutbounds, channel_llm.go:257-277).
    /// </summary>
    private static ChannelEndpoint? SelectUserEndpoint(
        ChannelCandidate candidate,
        string apiFormat)
    {
        var endpoint = ChannelEndpoints.FindUserEndpoint(
            candidate.Endpoints,
            candidate.BaseUrl,
            apiFormat);
        if (endpoint is not null || apiFormat != ApiFormats.OpenAIResponse)
        {
            return endpoint;
        }

        var rawEndpoints = ChannelEndpoints.ParseUser(candidate.Endpoints);
        var hasRawHttp = rawEndpoints.Any(static ep => ep.ApiFormat == ApiFormats.OpenAIResponse);
        var hasRawWebSocket = rawEndpoints.Any(static ep => ep.ApiFormat == ApiFormats.OpenAIResponseWebSocket);
        return hasRawHttp && !hasRawWebSocket
            ? ChannelEndpoints.FindUserEndpoint(
                candidate.Endpoints,
                candidate.BaseUrl,
                ApiFormats.OpenAIResponseWebSocket)
            : null;
    }

    /// <summary>
    /// Port of the credential half of buildCodexOutbound: an OAuth channel gets a
    /// refreshing token provider seeded from the channel's credentials, a plain
    /// key channel wraps the per-attempt key selection.
    /// </summary>
    private static ITokenGetter CreateCodexTokenProvider(
        ChannelCandidate candidate,
        ChannelApiKeyProvider apiKeyProvider,
        IExecutor? channelExecutor,
        IGrainFactory? grainFactory)
    {
        if (!ChannelCredentials.IsOAuth(candidate.Credentials))
        {
            return new ApiKeyTokenProvider(_ => apiKeyProvider.Get());
        }

        // channel_llm.go:1208: codex and fenno share buildCodexOutbound — the
        // same credentials parse and the same codex token recipe — so both take
        // the grain (§194).
        var credentials = ParseOAuthCredentials(candidate, "codex");

        if (grainFactory is not null && candidate.Type is CodexChannelType or FennoChannelType)
        {
            return new GrainTokenGetter(grainFactory, CreateTokenSeed(candidate, credentials));
        }

        return new TokenProvider(new TokenProviderParams
        {
            Credentials = credentials,
            HttpClient = channelExecutor,
        });
    }

    /// <summary>
    /// The snapshot handed to the per-channel token grain: the credentials the
    /// outbound resolved, the raw credentials document they came from, the row
    /// timestamp the refresh's optimistic write is based on (§146), and the raw
    /// settings the refresh resolves its proxy-aware HTTP client from (§178).
    /// </summary>
    private static ChannelTokenSeed CreateTokenSeed(
        ChannelCandidate candidate,
        OAuthCredentials credentials)
        => new()
        {
            ChannelId = candidate.ChannelId,
            ChannelType = candidate.Type,
            Credentials = ChannelOAuthTokenMapping.ToToken(credentials),
            CredentialsJson = candidate.Credentials,
            UpdatedAt = candidate.UpdatedAt,
            Settings = candidate.Settings,
        };

    /// <summary>
    /// Builds the shared Responses outbound with the per-attempt session seam.
    /// The reference's Responses transformer reads <c>shared.GetSessionID(ctx)</c>
    /// / <c>GetSessionScope(ctx)</c> on every request; the port threads the trace
    /// session the chat endpoint captured onto the wire request and reads it
    /// here (difference C062). Every Responses channel type and every wrapper
    /// that delegates to this transformer uses this helper.
    /// </summary>
    private static ResponsesOutboundTransformer CreateResponsesOutbound(
        LlmRequest request,
        string baseUrl,
        ChannelApiKeyProvider apiKeyProvider,
        string transport = "",
        string endpointPath = "",
        long channelId = 0)
        => new(new ResponsesOutboundConfig
        {
            BaseUrl = baseUrl,
            EndpointPath = endpointPath,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
            Transport = transport,
            // Port-only: keeps the WebSocket pool scoped to the owning channel
            // like the reference's per-channel outbound cache (C072).
            ChannelId = channelId,
            SessionIdProvider = () => request.RawRequest?.SessionId,
            SessionScopeProvider = () => request.RawRequest?.SessionScope,
        });

    /// <summary>
    /// channel_llm.go:1599-1623: resolves the Vertex resource root for the key
    /// the outbound selected. Each vertexApiKeys entry carries its own project
    /// and region; an unmatched key falls back to the configured base URL.
    /// </summary>
    private static Func<string?>? CreateVertexBaseUrlProvider(
        ChannelCandidate candidate,
        string baseUrl,
        IApiKeyProvider apiKeyProvider)
    {
        var bindings = ChannelCredentials.GetVertexApiKeys(candidate.Credentials);
        if (bindings.Count == 0)
        {
            return null;
        }

        return () =>
        {
            var apiKey = apiKeyProvider.Get();
            foreach (var binding in bindings)
            {
                if (string.Equals(binding.ApiKey, apiKey, StringComparison.Ordinal))
                {
                    return VertexServiceBaseUrl(baseUrl)
                        + "/v1/projects/" + binding.ProjectId.Trim()
                        + "/locations/" + binding.Region.Trim();
                }
            }

            return null;
        };
    }

    /// <summary>
    /// channel_llm.go:1566-1579: reduce a channel Base URL to the Vertex
    /// service root, dropping a stored projects/locations path and a trailing
    /// /v1.
    /// </summary>
    private static string VertexServiceBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        var projects = trimmed.IndexOf("/projects/", StringComparison.Ordinal);
        if (projects >= 0)
        {
            trimmed = trimmed[..projects];
        }

        return trimmed.EndsWith("/v1", StringComparison.Ordinal)
            ? trimmed[..^"/v1".Length]
            : trimmed;
    }
}
