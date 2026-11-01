using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Doubao;

/// <summary>Port of doubao.Config (llm/transformer/doubao/outbound.go:22-31).</summary>
public sealed class DoubaoOutboundConfig
{
    /// <summary>Custom base URL (optional in the reference).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Provides API keys for authentication, required.</summary>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>
    /// Replaces the default <c>/chat/completions</c> path. When set, the base
    /// URL is kept as-is without version normalization (same convention as the
    /// OpenAI transformer).
    /// </summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>Port-only audit field; see <see cref="IApiKeyProvenance"/>.</summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of the Doubao/Volcengine ("Ark") outbound transformer
/// (llm/transformer/doubao/outbound.go): the OpenAI outbound plus the Ark
/// <c>user_id</c>/<c>request_id</c> chat fields, the multimodal embedding API,
/// the image API and the Seedance asynchronous video task API.
/// </summary>
/// <remarks>
/// The reference receives a <c>context.Context</c> in every method; the port's
/// context-free transformer interface drops it (C105). The embedded OpenAI
/// outbound is used for chat response/stream/error conversion only — the Doubao
/// chat request is built from <c>RequestFromLLM</c> directly because the
/// reference's <c>doubao.Request</c> embeds <c>openai.Request</c> and appends its
/// own fields after it.
/// </remarks>
public sealed partial class DoubaoOutboundTransformer : IOutboundTransformer, IApiKeyProvenance, IVideoTaskOutbound
{
    /// <summary>Ark accepts user_id values of 6-128 characters.</summary>
    internal const int MinUserIdLength = 6;

    internal const int MaxUserIdLength = 128;

    private readonly DoubaoOutboundConfig _config;
    private readonly OpenAiOutboundTransformer _inner;
    private readonly string _baseUrl;
    private readonly string _endpointPath;
    private readonly bool _rawUrl;
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformer (legacy baseURL/apiKey form).</summary>
    public static DoubaoOutboundTransformer Create(string baseUrl, string apiKey)
    {
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException("base URL is required for Doubao transformer");
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("API key is required for Doubao transformer");
        }

        return new DoubaoOutboundTransformer(new DoubaoOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = new StaticApiKeyProvider(apiKey),
        });
    }

    /// <summary>
    /// Builds a transformer with a key provider and its audit index
    /// (the reference's getAPIKeyProvider + channel_api_key_index row).
    /// </summary>
    public static DoubaoOutboundTransformer Create(
        string baseUrl,
        IApiKeyProvider apiKeyProvider,
        int? channelApiKeyIndex = null)
        => new(new DoubaoOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = channelApiKeyIndex,
        });

    /// <summary>Port of NewOutboundTransformerWithConfig (outbound.go:70-115).</summary>
    public DoubaoOutboundTransformer(DoubaoOutboundConfig? config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid Doubao transformer configuration: config is nil");
        }

        _config = config;

        if (config.BaseUrl.Length == 0)
        {
            throw new InvalidOperationException("base URL is required for Doubao transformer");
        }

        if (config.ApiKeyProvider is null)
        {
            throw new InvalidOperationException("API key provider is required for Doubao transformer");
        }

        // "##" suffix marks a raw full request URL (same convention as the
        // OpenAI transformer); strip it here so it never leaks into the URL.
        _rawUrl = config.BaseUrl.EndsWith("##", StringComparison.Ordinal);
        var rawConfigUrl = _rawUrl ? config.BaseUrl[..^2] : config.BaseUrl;

        _endpointPath = config.EndpointPath;
        _baseUrl = _rawUrl
            ? rawConfigUrl.TrimEnd('/')
            : config.EndpointPath.Length > 0
                ? HttpClientUtils.NormalizeBaseUrl(rawConfigUrl, string.Empty)
                : HttpClientUtils.NormalizeBaseUrl(rawConfigUrl, "v3");

        try
        {
            _inner = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
            {
                PlatformType = OpenAiPlatformType.OpenAi,
                BaseUrl = _baseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ReasoningField = ReasoningField.Content,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create Doubao outbound transformer: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of the embedded OpenAI outbound's ApiFormat.</summary>
    public string ApiFormat => _inner.ApiFormat;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>The normalized base URL used for every provider endpoint.</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>Port of TransformRequest (outbound.go:132-235).</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("chat completion request is nil");
        }

        if (request.Model.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        switch (request.RequestType ?? string.Empty)
        {
            case RequestTypes.Chat:
            case "":
                break;
            case RequestTypes.Embedding:
                return TransformEmbeddingRequest(request);
            case RequestTypes.Image:
                return BuildImageGenerationApiRequest(request);
            case RequestTypes.Video:
                return BuildVideoGenerationApiRequest(request);
            case RequestTypes.Compact:
                throw TransformerErrors.InvalidRequest(
                    "compact is only supported by OpenAI Responses API");
            default:
                throw TransformerErrors.InvalidRequest(
                    request.RequestType + " is not supported");
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("messages are required");
        }

        // Convert the unified request to the OpenAI wire shape first; the
        // Doubao request type only adds the two Ark fields after it.
        var oaiRequest = request.RequestFromLlm(ReasoningField.Content)!;

        var userId = string.Empty;
        var requestId = string.Empty;
        if (request.Metadata is not null)
        {
            request.Metadata.TryGetValue("user_id", out userId);
            request.Metadata.TryGetValue("request_id", out requestId);
        }

        // Ark validates user_id as 6-128 characters (same constraint family as
        // GLM's error 1214). Clients like Claude Code send a long JSON blob
        // (device_id/session_id) here; truncate long values, drop short ones.
        // An empty value omits the field entirely (json omitempty).
        userId ??= string.Empty;
        var runeCount = userId.EnumerateRunes().Count();
        if (runeCount > MaxUserIdLength)
        {
            userId = TruncateRunes(userId, MaxUserIdLength);
        }
        else if (runeCount < MinUserIdLength)
        {
            userId = string.Empty;
        }

        // Generate request ID if not provided.
        if (string.IsNullOrEmpty(requestId))
        {
            requestId = "req_"
                + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }

        // The Doubao request does not support metadata (extracted above).
        oaiRequest.Metadata = null;

        var body = GoJson.Marshal(oaiRequest);

        // doubao.Request embeds openai.Request and declares its own fields
        // after it; Go emits user_id then request_id after the embedded ones.
        if (userId.Length > 0)
        {
            body = RawJsonPatch.SetTopLevelRaw(body, "user_id"u8, GoJson.Marshal(userId));
        }

        body = RawJsonPatch.SetTopLevelRaw(body, "request_id"u8, GoJson.Marshal(requestId));

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var url = _baseUrl;
        if (_rawUrl)
        {
            // The base URL is already the full request URL.
        }
        else if (_endpointPath.Length > 0)
        {
            url += _endpointPath;
        }
        else
        {
            url += "/chat/completions";
        }

        return new HttpRequestData
        {
            Method = "POST",
            Url = url,
            Headers = headers,
            Body = body,
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            ApiFormat = ApiFormats.OpenAIChatCompletion,
        };
    }

    /// <summary>Port of TransformResponse (outbound.go:352-391).</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response?.Request is not null)
        {
            switch (response.Request.RequestType)
            {
                case RequestTypes.Embedding:
                    return TransformEmbeddingResponse(response);
                case RequestTypes.Video:
                    // Fall through to the Seedance create-response handling below.
                    break;
                default:
                    return _inner.TransformResponse(response);
            }
        }
        else
        {
            return _inner.TransformResponse(response);
        }

        // Seedance create returns only {id}.
        var bodyBytes = response.Body ?? [];
        var createResponse = DecodeSeedanceCreateResponse(bodyBytes);

        if (GoStrings.TrimSpace(createResponse.ID).Length == 0)
        {
            throw TransformerErrors.InvalidResponse("missing id in seedance create response");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var model = LlmRequestModelOrFallback(response);

        return new LlmResponse
        {
            Id = createResponse.ID, // provider task id for persistence
            Object = "video.create",
            Created = now,
            Model = model,
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.SeedanceVideo,
            Choices = [],
            Video = new VideoResponse
            {
                ID = createResponse.ID,
                Status = "queued",
                Model = EmptyToNull(model),
                CreatedAt = now,
            },
        };
    }

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => _inner.TransformStream(request, stream);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public ResponseError TransformError(HttpClientError? error) => _inner.TransformError(error);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _inner.AggregateStreamChunks(request, chunks, out meta);

    /// <summary>Port of llmReqModelOrFallback (video_outbound.go:317-325).</summary>
    internal static string LlmRequestModelOrFallback(HttpResponseData? response)
    {
        if (response?.Request?.TransformerMetadata is { } metadata
            && metadata.TryGetValue("model", out var element)
            && element.ValueKind == JsonValueKind.String
            && element.GetString() is { Length: > 0 } model)
        {
            return model;
        }

        return string.Empty;
    }

    /// <summary>Port of the rune-prefix truncation the Ark user_id rule needs.</summary>
    internal static string TruncateRunes(string value, int count)
    {
        var builder = new StringBuilder(value.Length);
        var taken = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (taken == count)
            {
                break;
            }

            builder.Append(rune.ToString());
            taken++;
        }

        return builder.ToString();
    }
}
