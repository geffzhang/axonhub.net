using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.OpenRouter;

/// <summary>Port of openrouter.Config.</summary>
public sealed class OpenRouterOutboundConfig
{
    public string BaseUrl { get; set; } = string.Empty;

    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>Port-only audit index; see <see cref="IApiKeyProvenance"/>.</summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of llm/transformer/openrouter/outbound.go. OpenRouter is mostly
/// OpenAI-compatible but carries reasoning in a separate top-level field, has
/// its own image router at <c>/images</c>, and wraps provider errors in a
/// dedicated envelope.
/// </summary>
public sealed class OpenRouterOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly OpenRouterOutboundConfig _config;
    private readonly OpenAiOutboundTransformer _inner;
    private readonly string _baseUrl;
    private string? _selectedApiKey;

    public OpenRouterOutboundTransformer(OpenRouterOutboundConfig? config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid OpenRouter transformer configuration: config cannot be nil");
        }

        _config = config;

        try
        {
            _inner = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
            {
                PlatformType = OpenAiPlatformType.OpenAi,
                BaseUrl = config.BaseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ReasoningField = ReasoningField.Reasoning,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                "invalid OpenRouter transformer configuration: " + ex.Message, ex);
        }

        // The reference trims exactly one trailing slash and keeps this URL
        // separate from the OpenAI transformer's normalized one.
        _baseUrl = config.BaseUrl.EndsWith('/') ? config.BaseUrl[..^1] : config.BaseUrl;
    }

    public string ApiFormat => ApiFormats.OpenAIChatCompletion;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>The URL OpenRouter endpoints are built from.</summary>
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

        switch (request.RequestType)
        {
            case RequestTypes.Chat:
            case null:
            case "":
                break;
            case RequestTypes.Image:
                return BuildImageGenerationRequest(request);
            case RequestTypes.Embedding:
            case RequestTypes.Speech:
            case RequestTypes.Transcription:
            case RequestTypes.Translation:
                var delegated = _inner.TransformRequest(request);
                _selectedApiKey = _inner.SelectedApiKey;
                return delegated;
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

        byte[] body;
        try
        {
            body = GoJson.Marshal(request.RequestFromLlm(ReasoningField.Reasoning));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to transform request: " + ex.Message, ex);
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "POST",
            Url = _baseUrl + "/chat/completions",
            Headers = headers,
            Body = body,
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            ContentType = "application/json",
            ApiFormat = ApiFormats.OpenAIChatCompletion,
        };
    }

    /// <summary>
    /// Port of buildImageGenerationRequest. OpenRouter's image router accepts
    /// the same image_url content-part shape as input references; moderation,
    /// response_format and user are intentionally dropped by the wire struct.
    /// </summary>
    private HttpRequestData BuildImageGenerationRequest(LlmRequest request)
    {
        if (string.IsNullOrEmpty(request.Model))
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (request.Image is null)
        {
            throw TransformerErrors.InvalidRequest("image request is required");
        }

        if (string.IsNullOrEmpty(request.Image.Prompt))
        {
            throw TransformerErrors.InvalidRequest(
                "prompt is required for image generation");
        }

        var images = request.Image.Images ?? [];
        if (images.Count > 16)
        {
            throw TransformerErrors.InvalidRequest(
                "at most 16 input references are supported");
        }

        if (request.Image.Mask is { Length: > 0 })
        {
            throw TransformerErrors.InvalidRequest(
                "masks are not supported by OpenRouter image generation");
        }

        var inputReferences = images
            .Select(image => new OpenAiMessageContentPart
            {
                Type = "image_url",
                ImageUrl = new OpenAiImageUrl { Url = EncodeImageToBase64(image) },
            })
            .ToList();

        var wireRequest = new OpenRouterImageGenerationRequest
        {
            Model = request.Model,
            Prompt = request.Image.Prompt!,
            N = request.Image.N,
            Size = request.Image.Size,
            Quality = request.Image.Quality,
            Background = request.Image.Background,
            OutputFormat = request.Image.OutputFormat,
            OutputCompression = request.Image.OutputCompression,
            Seed = request.Seed,
            InputReferences = inputReferences.Count > 0 ? inputReferences : null,
        };

        byte[] body;
        try
        {
            body = GoJson.Marshal(wireRequest);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to marshal request: " + ex.Message, ex);
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "POST",
            Url = _baseUrl + "/images",
            Headers = headers,
            Body = body,
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            ContentType = "application/json",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["model"] = JsonSerializer.SerializeToElement(request.Model),
            },
        };
    }

    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        if (response.Request?.RequestType == RequestTypes.Image)
        {
            return TransformImageGenerationResponse(response);
        }

        if (response.Request is not null)
        {
            switch (response.Request.RequestType)
            {
                case RequestTypes.Speech:
                case RequestTypes.Transcription:
                case RequestTypes.Translation:
                    return _inner.TransformResponse(response);
            }
        }

        if (response.StatusCode >= 400)
        {
            throw new InvalidOperationException("HTTP error " + response.StatusCode);
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        var chatResponse = DecodeChatResponse(response.Body);

        return chatResponse.ToOpenAiResponse().ToLlmResponse()!;
    }

    private static OpenRouterResponse DecodeChatResponse(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal chat completion response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind is not null)
        {
            if (rootKind == "null")
            {
                return new OpenRouterResponse();
            }

            throw new InvalidOperationException(
                "failed to unmarshal chat completion response: "
                + GoStructJson.TypeMismatch(rootKind, "openrouter.Response"));
        }

        try
        {
            return LlmJson.Deserialize<OpenRouterResponse>(body) ?? new OpenRouterResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal chat completion response: "
                + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }
    }

    /// <summary>Port of transformImageGenerationResponse.</summary>
    private LlmResponse TransformImageGenerationResponse(HttpResponseData response)
    {
        if (response.StatusCode >= 400)
        {
            throw new InvalidOperationException("HTTP error " + response.StatusCode);
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        var model = "image-generation";
        if (response.Request?.TransformerMetadata is { } metadata
            && metadata.TryGetValue("model", out var modelElement)
            && modelElement.ValueKind == JsonValueKind.String
            && modelElement.GetString() is { Length: > 0 } metadataModel)
        {
            model = metadataModel;
        }

        var imageResponse = DecodeImageGenerationResponse(response.Body);

        var result = new LlmResponse
        {
            Id = "img-" + imageResponse.Created,
            Object = "chat.completion",
            Created = imageResponse.Created,
            Model = model,
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
        };

        if (imageResponse.Usage is not null)
        {
            result.Usage = imageResponse.Usage.ToLlmUsage();
        }

        var image = new ImageResponse
        {
            Created = imageResponse.Created,
            Data = [],
        };

        // OpenRouter reports the encoding per image rather than once per
        // response, while OutputFormat is response-scoped. The first image with
        // a usable media type wins.
        foreach (var item in imageResponse.Data ?? [])
        {
            var data = new ImageData { B64JSON = item.B64JSON };
            if (item.B64JSON.Length > 0)
            {
                data.URL = BuildImageDataUrl(item.MediaType, item.B64JSON);

                if (string.IsNullOrEmpty(image.OutputFormat))
                {
                    image.OutputFormat = ImageFormatFromMediaType(item.MediaType);
                }
            }

            image.Data.Add(data);
        }

        result.Image = image;

        return result;
    }

    private static OpenRouterImageGenerationResponse DecodeImageGenerationResponse(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal image generation response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind is not null)
        {
            if (rootKind == "null")
            {
                return new OpenRouterImageGenerationResponse();
            }

            throw new InvalidOperationException(
                "failed to unmarshal image generation response: "
                + GoStructJson.TypeMismatch(rootKind, "openrouter.ImageGenerationResponse"));
        }

        try
        {
            var decoded = LlmJson.Deserialize<OpenRouterImageGenerationResponse>(body);
            if (decoded is null)
            {
                return new OpenRouterImageGenerationResponse();
            }

            if (decoded.Data is not null)
            {
                for (var i = 0; i < decoded.Data.Count; i++)
                {
                    decoded.Data[i] ??= new OpenRouterImageGenerationData();
                }
            }

            return decoded;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal image generation response: "
                + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }
    }

    private static string EncodeImageToBase64(byte[] data)
    {
        var format = DetectImageFormat(data);
        var base64 = Convert.ToBase64String(data);
        return Xurl.BuildDataUrl("image/" + format, base64, true);
    }

    /// <summary>Port of detectImageFormat.</summary>
    public static string DetectImageFormat(byte[] data)
    {
        if (data.Length < 4)
        {
            return "png";
        }

        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "png";
        }

        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "jpeg";
        }

        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
        {
            return "gif";
        }

        if (data.Length >= 12
            && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return "webp";
        }

        return "png";
    }

    /// <summary>Port of buildImageDataURL.</summary>
    public static string BuildImageDataUrl(string mediaType, string b64Json)
    {
        if (b64Json.StartsWith("data:", StringComparison.Ordinal))
        {
            return b64Json;
        }

        if (mediaType.Length == 0)
        {
            mediaType = "image/png";
        }

        return Xurl.BuildDataUrl(mediaType, b64Json, true);
    }

    /// <summary>Port of imageFormatFromMediaType.</summary>
    public static string ImageFormatFromMediaType(string mediaType)
    {
        mediaType = mediaType.Trim().ToLowerInvariant();
        if (mediaType == "image/svg+xml")
        {
            return "svg";
        }

        const string prefix = "image/";
        return mediaType.StartsWith(prefix, StringComparison.Ordinal)
            ? mediaType[prefix.Length..]
            : string.Empty;
    }

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
    {
        if (request is not null)
        {
            switch (request.ApiFormat)
            {
                case ApiFormats.OpenAISpeech:
                case ApiFormats.OpenAITranscription:
                case ApiFormats.OpenAITranslation:
                    return _inner.AggregateStreamChunks(request, chunks, out meta);
            }
        }

        return OpenAiAggregator.AggregateStreamChunks(chunks, TransformChunk, out meta);
    }

    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
    {
        if (request is not null)
        {
            switch (request.ApiFormat)
            {
                case ApiFormats.OpenAISpeech:
                case ApiFormats.OpenAITranscription:
                case ApiFormats.OpenAITranslation:
                    return _inner.TransformStream(request, stream);
            }
        }

        var filtered = new FilterStream<StreamEvent>(
            stream, e => !StartsWith(e.Data, "[DONE]"));

        var transformed = new MapStream<StreamEvent, LlmResponse>(filtered, TransformStreamChunk);

        return new AppendStream<LlmResponse>(transformed, LlmResponse.DoneResponse);
    }

    public LlmResponse TransformStreamChunk(StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            throw new InvalidOperationException("stream event is nil");
        }

        if (GjsonErrorProbe.TryGetErrorString(streamEvent.Data, out var message))
        {
            throw new ResponseError
            {
                Detail = new ErrorDetail { Message = message },
            };
        }

        return TransformResponse(new HttpResponseData { Body = streamEvent.Data });
    }

    /// <summary>Port of openrouter.TransformChunk used by the aggregator.</summary>
    public static OpenAiResponse TransformChunk(StreamEvent chunk)
    {
        var chatResponse = DecodeChatResponse(chunk.Data);
        return chatResponse.ToOpenAiResponse();
    }

    public ResponseError TransformError(HttpClientError? error)
    {
        if (error is null)
        {
            return new ResponseError
            {
                StatusCode = 500,
                Detail = new ErrorDetail
                {
                    Message = GoHttpStatus.Text(500),
                    Type = "api_error",
                },
            };
        }

        if (TryParseError(error.Body, out var detail))
        {
            return new ResponseError { StatusCode = error.StatusCode, Detail = detail };
        }

        return new ResponseError
        {
            StatusCode = error.StatusCode,
            Detail = new ErrorDetail
            {
                Message = GoHttpStatus.Text(error.StatusCode),
                Type = "api_error",
            },
        };
    }

    /// <summary>
    /// Lenient port of <c>json.Unmarshal(body, &amp;openRouterError)</c>. A null
    /// root is valid for a Go struct target and yields the zero error; any
    /// other root type or field type mismatch counts as a decode failure.
    /// </summary>
    private static bool TryParseError(byte[] body, out ErrorDetail detail)
    {
        detail = new ErrorDetail();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                detail = MakeErrorDetail(string.Empty, 0);
                return true;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Go decodes repeated objects into the same struct cumulatively and
            // treats JSON null for struct/scalar fields as a no-op. The raw
            // metadata value is an `any`, so a null raw does clear it.
            var message = string.Empty;
            var code = 0L;
            JsonElement? raw = null;

            foreach (var property in root.EnumerateObject())
            {
                if (!NameEquals(property.Name, "error"))
                {
                    continue;
                }

                var errorValue = property.Value;
                if (errorValue.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (errorValue.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                foreach (var errorProperty in errorValue.EnumerateObject())
                {
                    if (NameEquals(errorProperty.Name, "message"))
                    {
                        if (errorProperty.Value.ValueKind == JsonValueKind.Null)
                        {
                            continue;
                        }

                        if (errorProperty.Value.ValueKind != JsonValueKind.String)
                        {
                            return false;
                        }

                        message = errorProperty.Value.GetString() ?? string.Empty;
                    }
                    else if (NameEquals(errorProperty.Name, "code"))
                    {
                        if (errorProperty.Value.ValueKind == JsonValueKind.Null)
                        {
                            continue;
                        }

                        if (errorProperty.Value.ValueKind != JsonValueKind.Number
                            || !errorProperty.Value.TryGetInt64(out var intCode))
                        {
                            return false;
                        }

                        code = intCode;
                    }
                    else if (NameEquals(errorProperty.Name, "metadata"))
                    {
                        if (errorProperty.Value.ValueKind == JsonValueKind.Null)
                        {
                            continue;
                        }

                        if (errorProperty.Value.ValueKind != JsonValueKind.Object)
                        {
                            return false;
                        }

                        foreach (var metadataProperty in errorProperty.Value.EnumerateObject())
                        {
                            if (!NameEquals(metadataProperty.Name, "raw"))
                            {
                                continue;
                            }

                            raw = metadataProperty.Value.ValueKind == JsonValueKind.Null
                                ? null
                                : metadataProperty.Value;
                        }
                    }
                }
            }

            var resolved = raw is { } rawValue ? GoCast.ToString(rawValue) : string.Empty;
            if (resolved.Length == 0)
            {
                resolved = message;
            }

            if (resolved.Length == 0 && code != 0)
            {
                resolved = code is >= int.MinValue and <= int.MaxValue
                    ? GoHttpStatus.Text((int)code)
                    : string.Empty;
            }

            detail = MakeErrorDetail(resolved, code);
            return true;
        }
    }

    /// <summary>Go struct field matching is case-insensitive.</summary>
    private static bool NameEquals(string name, string field)
        => string.Equals(name, field, StringComparison.OrdinalIgnoreCase);

    private static ErrorDetail MakeErrorDetail(string message, long code) => new()
    {
        Message = message,
        Code = code.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Type = "api_error",
    };

    private static bool StartsWith(byte[] data, string prefix)
    {
        if (data.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}
