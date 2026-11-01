using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Zenmux;

/// <summary>Port of zenmux.Config (llm/transformer/zenmux/outbound.go:19-23).</summary>
public sealed class ZenmuxOutboundConfig
{
    /// <summary>The provider base URL, required.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Replaces the default <c>/videos</c> path. The default path normalizes the
    /// base URL to the <c>v1</c> route; a custom path keeps the base URL as-is
    /// (outbound.go:50-62).
    /// </summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>Provides API keys for authentication, required.</summary>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>Port-only audit field; see <see cref="IApiKeyProvenance"/>.</summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of the <c>zenmux.ErrVideoTaskDeleteUnsupported</c> sentinel
/// (outbound.go:17): ZenMux has no native task deletion, so the video service
/// cancels the task locally instead of issuing an upstream DELETE
/// (biz/video.go:113-119).
/// </summary>
public sealed class ZenmuxVideoTaskDeleteUnsupportedException()
    : Exception("zenmux video task deletion is unsupported");

/// <summary>
/// Port of the ZenMux outbound transformer (llm/transformer/zenmux): the native
/// video task API plus the embedded OpenAI chat outbound that serves every
/// non-video request.
/// </summary>
/// <remarks>
/// The reference receives a <c>context.Context</c> in every method; the port's
/// context-free transformer interface drops it. The embedded OpenAI outbound is
/// also the source of the provenance seam: a chat request records its key
/// there, so <see cref="SelectedApiKey"/> falls back to it.
/// </remarks>
public sealed partial class ZenmuxOutboundTransformer : IOutboundTransformer, IApiKeyProvenance, IVideoTaskOutbound
{
    private readonly ZenmuxOutboundConfig _config;
    private readonly OpenAiOutboundTransformer _inner;
    private readonly string _baseUrl;
    private readonly string _videoPath;
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformer (legacy baseURL/apiKey form).</summary>
    public static ZenmuxOutboundTransformer Create(string baseUrl, string apiKey)
        => new(new ZenmuxOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = new StaticApiKeyProvider(apiKey),
        });

    /// <summary>
    /// Builds a transformer with a key provider and its audit index (the
    /// reference's getAPIKeyProvider + channel_api_key_index row).
    /// </summary>
    public static ZenmuxOutboundTransformer Create(
        string baseUrl,
        IApiKeyProvider apiKeyProvider,
        int? channelApiKeyIndex = null)
        => new(new ZenmuxOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = channelApiKeyIndex,
        });

    /// <summary>Port of NewOutboundTransformerWithConfig (outbound.go:39-79).</summary>
    public ZenmuxOutboundTransformer(ZenmuxOutboundConfig? config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid ZenMux transformer configuration: config is nil");
        }

        if (config.ApiKeyProvider is null)
        {
            throw new InvalidOperationException("API key provider is required for ZenMux transformer");
        }

        if (GoStrings.TrimSpace(config.BaseUrl).Length == 0)
        {
            throw new InvalidOperationException("base URL is required for ZenMux transformer");
        }

        _config = config;

        var endpointPath = GoStrings.TrimSpace(config.EndpointPath);
        if (endpointPath.Length == 0)
        {
            endpointPath = "/videos";
        }

        endpointPath = "/" + endpointPath.Trim('/');

        // "##" marks an already-complete base URL: it is only stripped, never
        // version-normalized (outbound.go:55-62).
        var baseUrl = config.BaseUrl;
        if (baseUrl.EndsWith("##", StringComparison.Ordinal))
        {
            baseUrl = baseUrl[..^2];
        }
        else if (endpointPath == "/videos")
        {
            baseUrl = HttpClientUtils.NormalizeBaseUrl(baseUrl, "v1");
        }
        else
        {
            baseUrl = HttpClientUtils.NormalizeBaseUrl(baseUrl, string.Empty);
        }

        try
        {
            _inner = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
            {
                PlatformType = OpenAiPlatformType.OpenAi,
                BaseUrl = baseUrl,
                EndpointPath = "/chat/completions",
                ApiKeyProvider = config.ApiKeyProvider,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create ZenMux chat transformer: " + ex.Message,
                ex);
        }

        _baseUrl = baseUrl;
        _videoPath = endpointPath;
    }

    /// <summary>Port of APIFormat (outbound.go:81-83).</summary>
    public string ApiFormat => ApiFormats.ZenmuxVideo;

    /// <summary>
    /// The key the latest video request used, or the embedded chat outbound's
    /// key when the latest request was delegated to it.
    /// </summary>
    public string? SelectedApiKey => _selectedApiKey ?? _inner.SelectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>The normalized base URL shared by the chat and video endpoints.</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>The normalized video endpoint path, always leading-slashed.</summary>
    public string VideoPath => _videoPath;

    /// <summary>Port of TransformRequest (outbound.go:85-91).</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null || (request.RequestType ?? string.Empty) != RequestTypes.Video)
        {
            return _inner.TransformRequest(request);
        }

        return BuildVideoRequest(request);
    }

    /// <summary>Port of TransformResponse (outbound.go:93-99).</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response?.Request is not null
            && response.Request.ApiFormat == ApiFormats.ZenmuxVideo)
        {
            return ParseCreateResponse(response);
        }

        return _inner.TransformResponse(response);
    }

    /// <summary>Port of the embedded OpenAI outbound (outbound.go:101-103).</summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => _inner.TransformStream(request, stream);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public ResponseError TransformError(HttpClientError? error) => _inner.TransformError(error);

    /// <summary>Port of the embedded OpenAI outbound (outbound.go:105-107).</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _inner.AggregateStreamChunks(request, chunks, out meta);

    /// <summary>Port of buildVideoRequest (video.go:15-104).</summary>
    private HttpRequestData BuildVideoRequest(LlmRequest request)
    {
        if (request.Video is null)
        {
            throw TransformerErrors.InvalidRequest("video request is required");
        }

        if (GoStrings.TrimSpace(request.Model).Length == 0)
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        var video = request.Video;

        if (video.Content is null || video.Content.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("content is required");
        }

        if (GoStrings.TrimSpace(video.ServiceTier ?? string.Empty).Length != 0)
        {
            throw TransformerErrors.InvalidRequest("service_tier is unsupported for ZenMux video");
        }

        if (video.ExecutionExpiresAfter is not null)
        {
            throw TransformerErrors.InvalidRequest("execution_expires_after is unsupported for ZenMux video");
        }

        // ZenMux takes native ratio/resolution; map the OpenAI size only when
        // neither native field was supplied (video.go:31-39).
        var ratio = GoStrings.TrimSpace(video.Ratio ?? string.Empty);
        var resolution = GoStrings.TrimSpace(video.Resolution ?? string.Empty);
        if (ratio.Length == 0 && resolution.Length == 0
            && GoStrings.TrimSpace(video.Size ?? string.Empty).Length != 0)
        {
            if (!TryInferNativeRatioResolution(video.Size!, out ratio, out resolution))
            {
                throw TransformerErrors.InvalidRequest(
                    "size " + GoCast.GoQuote(video.Size!)
                    + " cannot be mapped to ratio/resolution, please set ratio and resolution");
            }
        }

        var content = BuildNativeContent(video.Content);
        var duration = ParseDuration(video.Duration);

        // Go struct declaration order of nativeCreateRequest (video_types.go:16-32).
        var nativeRequest = new ZenmuxNativeCreateRequest
        {
            Model = request.Model,
            Content = content,
            Resolution = OmitIfEmpty(resolution),
            Ratio = OmitIfEmpty(ratio),
            Duration = duration,
            Seed = video.Seed,
            GenerateAudio = video.GenerateAudio,
            Frames = video.Frames,
            CameraFixed = video.CameraFixed,
            Watermark = video.Watermark,
            Draft = video.Draft,
            CallbackUrl = OmitIfEmpty(video.CallbackURL),
            ReturnLastFrame = video.ReturnLastFrame,
            Tools = video.Tools is { Count: > 0 }
                ? video.Tools.Select(GoJson.CompactElement).ToList()
                : null,
        };

        byte[] body;
        try
        {
            body = GoJson.Marshal(nativeRequest);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to marshal ZenMux video request: " + ex.Message,
                ex);
        }

        body = MergeExtraBody(body, request.ExtraBody);

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var req = new HttpRequestData
        {
            Method = "POST",
            Url = _baseUrl + _videoPath,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Accept"] = ["application/json"],
            },
            Body = body,
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.ZenmuxVideo,
        };

        req.TransformerMetadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        req.TransformerMetadata["model"] = JsonSerializer.SerializeToElement(request.Model);

        return req;
    }

    /// <summary>Port of buildNativeContent (video.go:106-137).</summary>
    private static List<ZenmuxNativeContent> BuildNativeContent(List<VideoContent> content)
    {
        var native = new List<ZenmuxNativeContent>(content.Count);

        for (var index = 0; index < content.Count; index++)
        {
            var item = content[index];
            var type = item.Type ?? string.Empty;
            var entry = new ZenmuxNativeContent { Type = type };

            switch (type)
            {
                case "text":
                    if (GoStrings.TrimSpace(item.Text ?? string.Empty).Length == 0
                        || !string.IsNullOrEmpty(item.Role)
                        || item.ImageURL is not null
                        || item.VideoURL is not null
                        || item.AudioURL is not null)
                    {
                        throw TransformerErrors.InvalidRequest(
                            "invalid text content at index " + index);
                    }

                    entry.Text = item.Text;
                    break;

                case "image_url":
                    if (item.ImageURL is null
                        || GoStrings.TrimSpace(item.ImageURL.URL).Length == 0
                        || !string.IsNullOrEmpty(item.Text)
                        || item.VideoURL is not null
                        || item.AudioURL is not null
                        || !ValidImageRole(item.Role))
                    {
                        throw TransformerErrors.InvalidRequest(
                            "invalid image_url content at index " + index);
                    }

                    entry.Role = OmitIfEmpty(item.Role);
                    entry.ImageURL = new ZenmuxNativeMediaUrl { Url = item.ImageURL.URL };
                    break;

                case "video_url":
                    if (item.VideoURL is null
                        || GoStrings.TrimSpace(item.VideoURL.URL).Length == 0
                        || GoStrings.TrimSpace(item.VideoURL.MIMEType ?? string.Empty).Length != 0
                        || !string.IsNullOrEmpty(item.Text)
                        || item.ImageURL is not null
                        || item.AudioURL is not null
                        || item.Role != "reference_video")
                    {
                        throw TransformerErrors.InvalidRequest(
                            "invalid video_url content at index " + index);
                    }

                    entry.Role = OmitIfEmpty(item.Role);
                    entry.VideoURL = new ZenmuxNativeMediaUrl { Url = item.VideoURL.URL };
                    break;

                case "audio_url":
                    if (item.AudioURL is null
                        || GoStrings.TrimSpace(item.AudioURL.URL).Length == 0
                        || !string.IsNullOrEmpty(item.Text)
                        || item.ImageURL is not null
                        || item.VideoURL is not null
                        || item.Role != "reference_audio")
                    {
                        throw TransformerErrors.InvalidRequest(
                            "invalid audio_url content at index " + index);
                    }

                    entry.Role = OmitIfEmpty(item.Role);
                    entry.AudioURL = new ZenmuxNativeMediaUrl { Url = item.AudioURL.URL };
                    break;

                default:
                    throw TransformerErrors.InvalidRequest(
                        "unsupported content type " + GoCast.GoQuote(type));
            }

            native.Add(entry);
        }

        return native;
    }

    /// <summary>Port of validImageRole (video.go:139-146).</summary>
    private static bool ValidImageRole(string? role)
        => role is null or "" or "first_frame" or "last_frame" or "reference_image";

    /// <summary>
    /// Merges extra_body over the native create body; the typed fields stay
    /// authoritative (video.go:69-90).
    /// </summary>
    private static byte[] MergeExtraBody(byte[] body, JsonElement? extraBody)
    {
        if (extraBody is not { } extra || extra.ValueKind == JsonValueKind.Undefined)
        {
            return body;
        }

        // Go unmarshals into a map: any non-object (including null) fails.
        if (extra.ValueKind != JsonValueKind.Object)
        {
            throw TransformerErrors.InvalidRequest("extra_body must be a JSON object");
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in extra.EnumerateObject())
        {
            fields[property.Name] = property.Value;
        }

        foreach (var field in new[] { "service_tier", "execution_expires_after" })
        {
            if (fields.ContainsKey(field))
            {
                throw TransformerErrors.InvalidRequest(field + " is unsupported for ZenMux video");
            }
        }

        Dictionary<string, JsonElement> nativeFields;
        try
        {
            nativeFields = LlmJson.Deserialize<Dictionary<string, JsonElement>>(body)
                ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to decode ZenMux video request: "
                + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        foreach (var (key, value) in nativeFields)
        {
            fields[key] = value;
        }

        // Go re-marshals the map: sorted keys, raw values compacted and
        // HTML-escaped by encoding/json.
        foreach (var key in fields.Keys.ToList())
        {
            fields[key] = GoJson.CompactElement(fields[key]);
        }

        return GoJson.Marshal(fields);
    }

    /// <summary>Port of parseDuration (video_response.go:14-23).</summary>
    private static long? ParseDuration(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!GoCast.TryParseInt64(GoStrings.TrimSpace(value), out var duration) || duration <= 0)
        {
            throw TransformerErrors.InvalidRequest("duration must be a positive integer");
        }

        return duration;
    }

    /// <summary>Port of inferNativeRatioResolution (size.go:8-29).</summary>
    internal static bool TryInferNativeRatioResolution(
        string size,
        out string ratio,
        out string resolution)
    {
        ratio = string.Empty;
        resolution = string.Empty;

        if (!TryParseNativeSize(size, out var width, out var height))
        {
            return false;
        }

        (ratio, resolution) = (width, height) switch
        {
            (256, 256) => ("1:1", "480p"),
            (1280, 720) => ("16:9", "720p"),
            (720, 1280) => ("9:16", "720p"),
            (1920, 1080) => ("16:9", "1080p"),
            (1080, 1920) => ("9:16", "1080p"),
            (640, 480) => ("4:3", "480p"),
            (480, 640) => ("3:4", "480p"),
            _ => (string.Empty, string.Empty),
        };

        return ratio.Length > 0;
    }

    /// <summary>Port of parseSize (size.go:31-50).</summary>
    internal static bool TryParseNativeSize(string size, out int width, out int height)
    {
        width = 0;
        height = 0;

        var normalized = GoStrings.TrimSpace(size).ToLowerInvariant();
        var cut = normalized.IndexOf('x', StringComparison.Ordinal);
        if (cut < 0)
        {
            return false;
        }

        // strconv.Atoi == ParseInt(s, 10, 0): optional sign, decimal digits only.
        if (!int.TryParse(
                GoStrings.TrimSpace(normalized[..cut]),
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out var w)
            || w <= 0)
        {
            return false;
        }

        if (!int.TryParse(
                GoStrings.TrimSpace(normalized[(cut + 1)..]),
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out var h)
            || h <= 0)
        {
            return false;
        }

        width = w;
        height = h;
        return true;
    }

    /// <summary>Port of BuildGetVideoTaskRequest (video.go:156-168).</summary>
    public HttpRequestData BuildGetVideoTaskRequest(string providerTaskId)
    {
        if (GoStrings.TrimSpace(providerTaskId).Length == 0)
        {
            throw TransformerErrors.InvalidRequest("providerTaskID is required");
        }

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "GET",
            Url = _baseUrl + _videoPath + "/" + providerTaskId,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Accept"] = ["application/json"],
            },
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.ZenmuxVideo,
        };
    }

    /// <summary>Port of BuildDeleteVideoTaskRequest (video.go:178-180).</summary>
    public HttpRequestData BuildDeleteVideoTaskRequest(string providerTaskId)
        => throw new ZenmuxVideoTaskDeleteUnsupportedException();

    /// <summary>Go's <c>omitempty</c> for strings.</summary>
    internal static string? OmitIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Port of zenmux.nativeContent (video_types.go:5-12).</summary>
    private sealed class ZenmuxNativeContent
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Text { get; set; }

        [JsonPropertyName("role")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Role { get; set; }

        [JsonPropertyName("image_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ZenmuxNativeMediaUrl? ImageURL { get; set; }

        [JsonPropertyName("video_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ZenmuxNativeMediaUrl? VideoURL { get; set; }

        [JsonPropertyName("audio_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ZenmuxNativeMediaUrl? AudioURL { get; set; }
    }

    /// <summary>Port of zenmux.nativeMediaURL (video_types.go:14-16).</summary>
    private sealed class ZenmuxNativeMediaUrl
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }

    /// <summary>Port of zenmux.nativeCreateRequest (video_types.go:18-34).</summary>
    private sealed class ZenmuxNativeCreateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public List<ZenmuxNativeContent> Content { get; set; } = [];

        [JsonPropertyName("resolution")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Resolution { get; set; }

        [JsonPropertyName("ratio")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Ratio { get; set; }

        [JsonPropertyName("duration")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Duration { get; set; }

        [JsonPropertyName("seed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Seed { get; set; }

        [JsonPropertyName("generate_audio")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? GenerateAudio { get; set; }

        [JsonPropertyName("frames")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Frames { get; set; }

        [JsonPropertyName("camera_fixed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? CameraFixed { get; set; }

        [JsonPropertyName("watermark")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Watermark { get; set; }

        [JsonPropertyName("draft")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Draft { get; set; }

        [JsonPropertyName("callback_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CallbackUrl { get; set; }

        [JsonPropertyName("return_last_frame")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ReturnLastFrame { get; set; }

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<JsonElement>? Tools { get; set; }
    }
}
