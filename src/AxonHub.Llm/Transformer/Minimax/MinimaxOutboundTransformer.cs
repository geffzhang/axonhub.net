using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Minimax;

/// <summary>Port of minimax.Config (llm/transformer/minimax/outbound.go:20-24).</summary>
public sealed class MinimaxOutboundConfig
{
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Replaces the default <c>/image_generation</c> path.</summary>
    public string EndpointPath { get; set; } = string.Empty;

    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>Port-only audit index; see <see cref="IApiKeyProvenance"/>.</summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of llm/transformer/minimax: chat passes through the OpenAI-compatible
/// outbound while images use MiniMax's own <c>/image_generation</c> API.
/// </summary>
public sealed class MinimaxOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly MinimaxOutboundConfig _config;
    private readonly OpenAiOutboundTransformer _inner;
    private readonly string _baseUrl;
    private readonly string _endpointPath;
    private string? _selectedApiKey;

    public MinimaxOutboundTransformer(MinimaxOutboundConfig? config)
    {
        if (config is null || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            throw new InvalidOperationException(
                "base URL is required for MiniMax transformer");
        }

        if (config.ApiKeyProvider is null)
        {
            throw new InvalidOperationException(
                "API key provider is required for MiniMax transformer");
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
                "invalid MiniMax transformer configuration: " + ex.Message, ex);
        }

        _baseUrl = HttpClientUtils.NormalizeBaseUrl(config.BaseUrl, "v1");
        _endpointPath = config.EndpointPath;
    }

    public string ApiFormat => ApiFormats.OpenAIChatCompletion;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is not null && request.RequestType == RequestTypes.Image)
        {
            return BuildImageRequest(request);
        }

        var delegated = _inner.TransformRequest(request);
        _selectedApiKey = _inner.SelectedApiKey;
        return delegated;
    }

    /// <summary>Go: buildImageRequest (outbound.go:64-148).</summary>
    private HttpRequestData BuildImageRequest(LlmRequest request)
    {
        if (request.Image is null)
        {
            throw TransformerErrors.InvalidRequest("image request is required");
        }

        var prompt = request.Image.Prompt ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw TransformerErrors.InvalidRequest("prompt is required for image generation");
        }

        var model = request.Model;
        if (string.IsNullOrEmpty(model)
            || (model == "dall-e-2" && !request.Image.ModelSpecified))
        {
            model = "image-01";
        }

        // Go builds a map[string]any, so keys are emitted in sorted order.
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["prompt"] = request.Image.Prompt,
        };

        if (request.Image.SubjectReference is { } subjectReference
            && subjectReference.ValueKind != JsonValueKind.Undefined)
        {
            // Go unmarshals into any and re-marshals (sorted keys, Go number
            // formatting); GoAnyJson reproduces that.
            body["subject_reference"] = new GoAnyValue(subjectReference);
        }

        if (!string.IsNullOrEmpty(request.Image.AspectRatio))
        {
            body["aspect_ratio"] = request.Image.AspectRatio;
        }

        if (!string.IsNullOrEmpty(request.Image.Size)
            && (request.Image.Width is null || request.Image.Height is null))
        {
            var size = request.Image.Size;
            var cut = size.IndexOf('x');
            var widthText = cut >= 0 ? size[..cut] : size;
            var heightText = cut >= 0 ? size[(cut + 1)..] : string.Empty;
            var widthOk = long.TryParse(
                widthText,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var width);
            var heightOk = long.TryParse(
                heightText,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var height);

            if (cut < 0 || !widthOk || !heightOk
                || width < 512 || width > 2048 || width % 8 != 0
                || height < 512 || height > 2048 || height % 8 != 0)
            {
                throw TransformerErrors.InvalidRequest(
                    "unsupported MiniMax image size " + GoCast.GoQuote(size));
            }

            if (request.Image.Width is null)
            {
                body["width"] = width;
            }

            if (request.Image.Height is null)
            {
                body["height"] = height;
            }
        }

        if (request.Image.Width is { } explicitWidth)
        {
            body["width"] = explicitWidth;
        }

        if (request.Image.Height is { } explicitHeight)
        {
            body["height"] = explicitHeight;
        }

        if (request.Seed is { } seed)
        {
            body["seed"] = seed;
        }

        if (request.Image.N is { } n)
        {
            body["n"] = n;
        }

        if (request.Image.PromptOptimizer is { } promptOptimizer)
        {
            body["prompt_optimizer"] = promptOptimizer;
        }

        var format = request.Image.ResponseFormat ?? string.Empty;
        if (format == "b64_json")
        {
            format = "base64";
        }

        if (format.Length == 0)
        {
            format = "url";
        }

        body["response_format"] = format;

        byte[] raw;
        try
        {
            raw = GoJson.Marshal(body);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "failed to marshal MiniMax image request: " + ex.Message, ex);
        }

        var path = _endpointPath;
        if (path.Length == 0)
        {
            path = "/image_generation";
        }

        var url = _baseUrl + path;
        if (path.StartsWith("/v1/", StringComparison.Ordinal)
            && _baseUrl.EndsWith("/v1", StringComparison.Ordinal))
        {
            url = _baseUrl[..^"/v1".Length] + path;
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "POST",
            Url = url,
            Headers = headers,
            Body = raw,
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            ContentType = "application/json",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["model"] = JsonSerializer.SerializeToElement(model),
            },
        };
    }

    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response?.Request is null || response.Request.RequestType != RequestTypes.Image)
        {
            return _inner.TransformResponse(response);
        }

        if (response.StatusCode >= 400)
        {
            throw new InvalidOperationException(
                "MiniMax image API returned HTTP "
                + response.StatusCode.ToString(CultureInfo.InvariantCulture));
        }

        MinimaxImagePayload payload;
        try
        {
            payload = LlmJson.Deserialize<MinimaxImagePayload>(response.Body!)
                ?? throw new InvalidOperationException(
                    "failed to decode MiniMax image response: unexpected null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to decode MiniMax image response: "
                + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        if (payload.BaseResp is { StatusCode: not 0 } baseResp)
        {
            throw TransformerErrors.InvalidResponse(
                "MiniMax image API status "
                + baseResp.StatusCode.ToString(CultureInfo.InvariantCulture)
                + ": " + baseResp.StatusMsg);
        }

        var created = payload.Created;
        if (created == 0)
        {
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        var model = "image-01";
        if (response.Request.TransformerMetadata is { } metadata
            && metadata.TryGetValue("model", out var modelElement)
            && modelElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(modelElement.GetString()))
        {
            model = modelElement.GetString()!;
        }

        var urls = payload.Data?.ImageUrls ?? [];
        var base64Images = payload.Data?.ImageBase64 ?? [];
        var data = new List<ImageData>(urls.Count + base64Images.Count);
        foreach (var url in urls)
        {
            data.Add(new ImageData { URL = url });
        }

        foreach (var b64 in base64Images)
        {
            data.Add(new ImageData { B64JSON = b64 });
        }

        if (data.Count == 0)
        {
            throw TransformerErrors.InvalidResponse(
                "MiniMax image response contained no images");
        }

        var id = payload.Id;
        if (string.IsNullOrEmpty(id))
        {
            id = "minimax-img-" + created.ToString(CultureInfo.InvariantCulture);
        }

        return new LlmResponse
        {
            Id = id,
            Object = "image.generation",
            Created = created,
            Model = model,
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Image = new ImageResponse { Created = created, Data = data },
        };
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

    /// <summary>Port of the anonymous response payload in TransformResponse.</summary>
    private sealed class MinimaxImagePayload
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("created")]
        public long Created { get; set; }

        [JsonPropertyName("base_resp")]
        public MinimaxBaseResp? BaseResp { get; set; }

        [JsonPropertyName("data")]
        public MinimaxImagePayloadData? Data { get; set; }
    }

    private sealed class MinimaxBaseResp
    {
        [JsonPropertyName("status_code")]
        public long StatusCode { get; set; }

        [JsonPropertyName("status_msg")]
        public string? StatusMsg { get; set; }
    }

    private sealed class MinimaxImagePayloadData
    {
        [JsonPropertyName("image_urls")]
        public List<string>? ImageUrls { get; set; }

        [JsonPropertyName("image_base64")]
        public List<string>? ImageBase64 { get; set; }
    }

    /// <summary>Carries a Go <c>any</c> value through the sorted-map marshal.</summary>
    [JsonConverter(typeof(GoAnyValueConverter))]
    private sealed class GoAnyValue
    {
        public GoAnyValue(JsonElement value) => Value = value;

        public JsonElement Value { get; }
    }

    private sealed class GoAnyValueConverter : JsonConverter<GoAnyValue>
    {
        public override GoAnyValue Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            GoAnyValue value,
            JsonSerializerOptions options)
            => GoAnyJson.Write(writer, value.Value);
    }
}
