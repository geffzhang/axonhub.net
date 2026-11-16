using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Zai;

/// <summary>Port of zai.Config (llm/transformer/zai/outbound.go:23-33).</summary>
public sealed class ZaiOutboundConfig
{
    public string BaseUrl { get; set; } = string.Empty;

    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>API version appended by the default route; empty means "v4".</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Replaces the default <c>/chat/completions</c> path; when set the base URL
    /// is kept as-is without version normalization.
    /// </summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>
    /// Port-only seam for <c>shared.GetSessionID(ctx)</c> (difference C062):
    /// the reference reads the session from the request context, which the
    /// port's request-less transformer signature cannot carry.
    /// </summary>
    public Func<string?>? SessionIdProvider { get; set; }

    /// <summary>Port-only audit index; see <see cref="IApiKeyProvenance"/>.</summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of llm/transformer/zai: the OpenAI outbound with the GLM thinking /
/// reasoning_effort ladder, the 6-128 rune <c>user_id</c> constraint, a
/// request_id taken from the session, and a dedicated image-generation route.
/// </summary>
public sealed partial class ZaiOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    /// <summary>Go: zai.minUserIDLength.</summary>
    public const int MinUserIdLength = 6;

    /// <summary>Go: zai.maxUserIDLength.</summary>
    public const int MaxUserIdLength = 128;

    private readonly ZaiOutboundConfig _config;
    private readonly OpenAiOutboundTransformer _inner;
    private readonly string _baseUrl;
    private readonly string _endpointPath;
    private readonly bool _rawUrl;
    private string? _selectedApiKey;

    public ZaiOutboundTransformer(ZaiOutboundConfig? config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid Zai transformer configuration: config cannot be nil");
        }

        _config = config;

        try
        {
            _inner = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
            {
                PlatformType = OpenAiPlatformType.OpenAi,
                BaseUrl = config.BaseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ReasoningField = ReasoningField.Content,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "invalid Zai transformer configuration: " + ex.Message, ex);
        }

        var version = string.IsNullOrEmpty(config.Version) ? "v4" : config.Version;

        // "##" marks a raw full request URL (same convention as the OpenAI
        // transformer); strip it so it never leaks into the request URL.
        _rawUrl = config.BaseUrl.EndsWith("##", StringComparison.Ordinal);
        if (_rawUrl)
        {
            config.BaseUrl = config.BaseUrl[..^2];
        }

        _endpointPath = config.EndpointPath;

        if (_rawUrl)
        {
            _baseUrl = config.BaseUrl.TrimEnd('/');
        }
        else if (!string.IsNullOrEmpty(config.EndpointPath))
        {
            _baseUrl = HttpClientUtils.NormalizeBaseUrl(config.BaseUrl, string.Empty);
        }
        else
        {
            _baseUrl = HttpClientUtils.NormalizeBaseUrl(config.BaseUrl, version);
        }
    }

    public string ApiFormat => ApiFormats.OpenAIChatCompletion;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>The normalized base URL used for chat and image requests.</summary>
    public string BaseUrl => _baseUrl;

    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("chat completion request is nil");
        }

        if (string.IsNullOrEmpty(request.Model))
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        switch (request.RequestType ?? string.Empty)
        {
            case RequestTypes.Chat:
            case "":
                break;
            case RequestTypes.Image:
                return BuildImageGenerationApiRequest(request);
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

        var oaiRequest = request.RequestFromLlm(ReasoningField.Content)!;

        // Zai doesn't support json_schema, convert to json_object.
        if (oaiRequest.ResponseFormat is { Type: "json_schema" } responseFormat)
        {
            responseFormat.Type = "json_object";
            responseFormat.JsonSchema = null;
        }

        var userId = string.Empty;
        var requestId = string.Empty;
        if (request.Metadata is not null)
        {
            request.Metadata.TryGetValue("user_id", out userId);
            request.Metadata.TryGetValue("request_id", out requestId);
        }

        // GLM/z.ai validate user_id as 6-128 characters (error 1214). Clients
        // like Claude Code send a long JSON blob here; truncate long values and
        // drop short ones. An empty value omits the field entirely.
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

        if (string.IsNullOrEmpty(requestId))
        {
            requestId = _config.SessionIdProvider?.Invoke() ?? string.Empty;
        }

        // zai only supports auto tool choice.
        if (oaiRequest.ToolChoice is not null)
        {
            oaiRequest.ToolChoice = new OpenAiToolChoice { ToolChoice = "auto" };
        }

        // zai requests do not support metadata (extracted to user_id/request_id).
        oaiRequest.Metadata = null;

        // Convert ReasoningEffort to Zhipu thinking / reasoning_effort fields.
        OpenAiThinking? thinking = null;
        if (!string.IsNullOrEmpty(request.ReasoningEffort))
        {
            var (resolved, nativeEffort) = ResolveZaiThinking(
                request.Model,
                request.ReasoningEffort);
            thinking = resolved;
            oaiRequest.ReasoningEffort = nativeEffort;
        }

        byte[] body;
        try
        {
            var node = JsonSerializer.SerializeToNode(oaiRequest, LlmJson.Options)
                as JsonObject ?? new JsonObject();

            // zai.Request embeds openai.Request and declares its own fields
            // after it; Go emits them after the embedded fields in this order.
            if (!string.IsNullOrEmpty(userId))
            {
                node["user_id"] = userId;
            }

            if (!string.IsNullOrEmpty(requestId))
            {
                node["request_id"] = requestId;
            }

            if (thinking is not null)
            {
                node["thinking"] = new JsonObject { ["type"] = thinking.Type };
            }

            body = GoJson.Marshal(node);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "failed to transform request: " + ex.Message, ex);
        }

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var url = _baseUrl;
        if (_rawUrl)
        {
            // Base URL is already the full request URL.
        }
        else if (!string.IsNullOrEmpty(_endpointPath))
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
            ContentType = "application/json",
            ApiFormat = ApiFormats.OpenAIChatCompletion,
        };
    }

    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        if (response.StatusCode >= 400)
        {
            throw new InvalidOperationException(
                "HTTP error " + response.StatusCode.ToString(CultureInfo.InvariantCulture));
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        if (response.Request?.ApiFormat == ApiFormats.OpenAIImageGeneration)
        {
            return TransformImageGenerationResponse(response);
        }

        return _inner.TransformResponse(response);
    }

    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => _inner.TransformStream(request, stream);

    public ResponseError TransformError(HttpClientError? error) => _inner.TransformError(error);

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _inner.AggregateStreamChunks(request, chunks, out meta);

    /// <summary>Go: glmVersionPattern (outbound.go:121-123).</summary>
    private static readonly System.Text.RegularExpressions.Regex GlmVersionPattern = new(
        @"(?i)(?:^|[/:])glm-([0-9]+)\.([0-9]+)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Port of glmVersion.</summary>
    internal static bool TryGlmVersion(string model, out int major, out int minor)
    {
        var match = GlmVersionPattern.Match(model);
        if (!match.Success)
        {
            major = 0;
            minor = 0;
            return false;
        }

        major = 0;
        minor = 0;
        return int.TryParse(
            match.Groups[1].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out major)
            && int.TryParse(
                match.Groups[2].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out minor);
    }

    /// <summary>Port of supportsNativeReasoningEffort.</summary>
    internal static bool SupportsNativeReasoningEffort(string model)
    {
        if (!TryGlmVersion(model, out var major, out var minor))
        {
            return false;
        }

        return major > 5 || (major == 5 && minor >= 2);
    }

    /// <summary>Port of isAlwaysThinkingModel.</summary>
    internal static bool IsAlwaysThinkingModel(string model)
    {
        var lower = model.ToLowerInvariant();
        return lower.StartsWith("glm-5.3", StringComparison.Ordinal)
            || lower.Contains("/glm-5.3", StringComparison.Ordinal);
    }

    /// <summary>Port of normalizeGLM53Effort.</summary>
    internal static string NormalizeGlm53Effort(string effort)
    {
        switch (effort.ToLowerInvariant())
        {
            case "low":
                return "low";
            case "medium":
            case "high":
                return "high";
            case "xhigh":
            case "max":
                return "max";
            default:
                return "low";
        }
    }

    /// <summary>Port of resolveZaiThinking.</summary>
    internal static (OpenAiThinking? Thinking, string? Effort) ResolveZaiThinking(
        string model,
        string reasoningEffort)
    {
        if (reasoningEffort.Length == 0)
        {
            return (null, null);
        }

        if (!SupportsNativeReasoningEffort(model))
        {
            return (new OpenAiThinking
            {
                Type = reasoningEffort == "none" ? "disabled" : "enabled",
            }, null);
        }

        if (IsAlwaysThinkingModel(model))
        {
            return (
                new OpenAiThinking { Type = "enabled" },
                NormalizeGlm53Effort(reasoningEffort));
        }

        if (reasoningEffort == "none")
        {
            return (new OpenAiThinking { Type = "disabled" }, null);
        }

        if (reasoningEffort == "minimal")
        {
            return (new OpenAiThinking { Type = "enabled" }, ReasoningEfforts.Low);
        }

        return (new OpenAiThinking { Type = "enabled" }, reasoningEffort);
    }

    private static string TruncateRunes(string value, int count)
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
