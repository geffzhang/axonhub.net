using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.DeepSeek;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Transformer.OpenCode;

/// <summary>
/// Port of opencode.route (llm/transformer/opencode/outbound.go:28-40): the
/// OpenCode Go model families speak different upstream protocols.
/// </summary>
public enum OpenCodeRoute
{
    /// <summary>OpenAI chat completions (/v1/chat/completions), the default.</summary>
    Chat,

    /// <summary>DeepSeek's thinking/reasoning_content conventions.</summary>
    Deepseek,

    /// <summary>OpenAI Responses API (/v1/responses).</summary>
    Responses,

    /// <summary>Anthropic messages endpoint (/v1/messages).</summary>
    Anthropic,
}

/// <summary>Port of opencode.Config.</summary>
public sealed class OpenCodeOutboundConfig
{
    /// <summary>The base URL for the OpenCode Go API, required.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Provides API keys for authentication, required.</summary>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>
    /// Port-only audit field: 1-based position of the key the provider serves in
    /// the channel's full key list, or null when the channel has a single key.
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }

    /// <summary>
    /// Port-only seam for <c>shared.GetSessionID(ctx)</c>; the reference's inner
    /// Responses transformer reads it from the context on every request
    /// (difference C062).
    /// </summary>
    public Func<string?>? SessionIdProvider { get; set; }

    /// <summary>Port-only seam for <c>shared.GetSessionScope(ctx)</c> (C062).</summary>
    public Func<string?>? SessionScopeProvider { get; set; }
}

/// <summary>
/// Port of llm/transformer/opencode/outbound.go's OutboundTransformer: exposes
/// several model families through one OpenCode Go base URL and dispatches by
/// model name, recording the chosen route for the response transformers.
/// </summary>
public sealed class OpenCodeOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    /// <summary>Carries the selected route so response/stream dispatch picks the same sub-transformer.</summary>
    public const string RouteMetadataKey = "opencode_route";

    private readonly OpenCodeOutboundConfig _config;
    private readonly OpenAiOutboundTransformer _chat;
    private readonly DeepSeekOutboundTransformer _deepseek;
    private readonly ResponsesOutboundTransformer _responses;
    private readonly AnthropicOutboundTransformer _anthropic;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public OpenCodeOutboundTransformer(OpenCodeOutboundConfig config)
    {
        if (config is null)
        {
            throw new InvalidOperationException("config is nil");
        }

        if (config.ApiKeyProvider is null)
        {
            throw new InvalidOperationException("API key provider is required");
        }

        if (string.IsNullOrEmpty(config.BaseUrl))
        {
            throw new InvalidOperationException("base URL is required");
        }

        _config = config;

        try
        {
            _chat = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
            {
                PlatformType = OpenAiPlatformType.OpenAi,
                BaseUrl = config.BaseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        {
            throw new InvalidOperationException(
                "invalid OpenCode chat transformer configuration: " + ex.Message,
                ex);
        }

        try
        {
            _deepseek = new DeepSeekOutboundTransformer(new DeepSeekOutboundConfig
            {
                BaseUrl = config.BaseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        {
            throw new InvalidOperationException(
                "invalid OpenCode deepseek transformer configuration: " + ex.Message,
                ex);
        }

        try
        {
            _responses = new ResponsesOutboundTransformer(new ResponsesOutboundConfig
            {
                BaseUrl = config.BaseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
                // The reference delegates to the Responses transformer with the
                // same ctx, so it reads the trace session (C062).
                SessionIdProvider = config.SessionIdProvider,
                SessionScopeProvider = config.SessionScopeProvider,
            });
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        {
            throw new InvalidOperationException(
                "invalid OpenCode responses transformer configuration: " + ex.Message,
                ex);
        }

        try
        {
            _anthropic = new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.Direct,
                BaseUrl = config.BaseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        {
            throw new InvalidOperationException(
                "invalid OpenCode anthropic transformer configuration: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of NewOutboundTransformer(baseURL, apiKey).</summary>
    public static OpenCodeOutboundTransformer Create(string baseUrl, string apiKey)
        => Create(baseUrl, new StaticApiKeyProvider(apiKey));

    public static OpenCodeOutboundTransformer Create(
        string baseUrl,
        IApiKeyProvider apiKeyProvider,
        int? channelApiKeyIndex = null)
        => new(new OpenCodeOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = channelApiKeyIndex,
        });

    public string ApiFormat => ApiFormats.OpenAIChatCompletion;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>Port of routeForModel.</summary>
    public static OpenCodeRoute RouteForModel(string model)
    {
        if (model.StartsWith("deepseek", StringComparison.Ordinal))
        {
            return OpenCodeRoute.Deepseek;
        }

        if (model.StartsWith("grok", StringComparison.Ordinal)
            || model.StartsWith("gpt", StringComparison.Ordinal))
        {
            return OpenCodeRoute.Responses;
        }

        if (model.StartsWith("minimax", StringComparison.Ordinal)
            || model.StartsWith("qwen3", StringComparison.Ordinal))
        {
            return OpenCodeRoute.Anthropic;
        }

        return OpenCodeRoute.Chat;
    }

    /// <summary>Port of (*OutboundTransformer).sub.</summary>
    private IOutboundTransformer Sub(OpenCodeRoute route) => route switch
    {
        OpenCodeRoute.Deepseek => _deepseek,
        OpenCodeRoute.Responses => _responses,
        OpenCodeRoute.Anthropic => _anthropic,
        _ => _chat,
    };

    /// <summary>Port of routeFromResponse.</summary>
    private static OpenCodeRoute RouteFromResponse(HttpResponseData? httpResp)
        => RouteFromMetadata(httpResp?.Request?.TransformerMetadata);

    /// <summary>Port of routeFromHTTPRequest.</summary>
    private static OpenCodeRoute RouteFromRequest(HttpRequestData? request)
        => RouteFromMetadata(request?.TransformerMetadata);

    private static OpenCodeRoute RouteFromMetadata(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is not null
            && metadata.TryGetValue(RouteMetadataKey, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } route)
        {
            return route switch
            {
                "deepseek" => OpenCodeRoute.Deepseek,
                "responses" => OpenCodeRoute.Responses,
                "anthropic" => OpenCodeRoute.Anthropic,
                _ => OpenCodeRoute.Chat,
            };
        }

        return OpenCodeRoute.Chat;
    }

    private static string RouteValue(OpenCodeRoute route) => route switch
    {
        OpenCodeRoute.Deepseek => "deepseek",
        OpenCodeRoute.Responses => "responses",
        OpenCodeRoute.Anthropic => "anthropic",
        _ => "chat",
    };

    /// <summary>Port of TransformRequest: dispatch by model, then record the route.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("request is nil");
        }

        var route = RouteForModel(request.Model);
        var sub = Sub(route);
        var httpReq = sub.TransformRequest(request);

        _selectedApiKey = (sub as IApiKeyProvenance)?.SelectedApiKey;

        httpReq.TransformerMetadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        httpReq.TransformerMetadata[RouteMetadataKey] = JsonSerializer.SerializeToElement(RouteValue(route));
        OpenCodeSessionHeader.SetSessionHeader(request, httpReq);

        return httpReq;
    }

    /// <summary>Port of TransformResponse: dispatch to the sub-transformer recorded on the request.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
        => Sub(RouteFromResponse(response)).TransformResponse(response);

    /// <summary>Port of TransformStream.</summary>
    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => Sub(RouteFromRequest(request)).TransformStream(request, stream);

    /// <summary>Port of AggregateStreamChunks.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => Sub(RouteFromRequest(request)).AggregateStreamChunks(request, chunks, out meta);

    /// <summary>Port of TransformError: always the default OpenAI chat error handling.</summary>
    public ResponseError TransformError(HttpClientError? error) => _chat.TransformError(error);

    private static bool IsConfigurationError(Exception ex)
        => ex is ArgumentException or InvalidOperationException or NotSupportedException;
}
