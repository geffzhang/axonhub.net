using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Modelscope;

/// <summary>
/// Port of llm/transformer/modelscope/image.go: the asynchronous image task
/// submission, polling, output-image parsing and base64 materialization.
/// </summary>
public sealed partial class ModelscopeOutboundTransformer
{
    private const string ImagePath = "/images/generations";
    private const string TasksPath = "/tasks/";
    private const string ImageTaskApiKeyMeta = "api_key";
    private const string DefaultImageSize = "2048x2048";

    private static readonly TimeSpan DefaultImageTaskPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultImageTaskTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DefaultImageDownloadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Go: the anonymous struct in parseOutputImages (image.go:401-404).</summary>
    private const string OutputImageGoType =
        "struct { URL string \"json:\\\"url\\\"\"; B64JSON string \"json:\\\"b64_json\\\"\" }";

    /// <summary>Stand-in for the reference's <c>http.DefaultClient</c> (C104).</summary>
    private static readonly HttpClient StaticHttpClient = new();

    /// <summary>Port of imageResponse (image.go:56-68); task and query share it.</summary>
    private sealed class ImageResponsePayload
    {
        [JsonPropertyName("created")]
        public long Created { get; set; }

        [JsonPropertyName("data")]
        public List<ImageData>? Data { get; set; }

        [JsonPropertyName("task_id")]
        public string? TaskId { get; set; }

        [JsonPropertyName("task_status")]
        public string? TaskStatus { get; set; }

        [JsonPropertyName("output_images")]
        public List<JsonElement>? OutputImages { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("errors")]
        public ImageTaskErrors? Errors { get; set; }
    }

    private sealed class ImageTaskErrors
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>Port of imageSizeOrDefault (image.go:38-44).</summary>
    internal static string ImageSizeOrDefault(string? size)
    {
        var trimmed = GoStrings.TrimSpace(size ?? string.Empty);
        if (trimmed.Length > 0 && !string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return DefaultImageSize;
    }

    /// <summary>Port of buildImageRequest (image.go:78-155).</summary>
    private HttpRequestData BuildImageRequest(LlmRequest request)
    {
        if (request.Image is null)
        {
            throw TransformerErrors.InvalidRequest("image request is required");
        }

        switch (request.ApiFormat)
        {
            case ApiFormats.OpenAIImageGeneration:
            case ApiFormats.OpenAIImageEdit:
                break;
            default:
                throw TransformerErrors.InvalidRequest(
                    "ModelScope does not support image api format "
                    + GoCast.GoQuote(request.ApiFormat ?? string.Empty));
        }

        var prompt = GoStrings.TrimSpace(request.Image.Prompt ?? string.Empty);
        if (prompt.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("prompt is required for image requests");
        }

        if (request.Image.Mask is { Length: > 0 })
        {
            throw TransformerErrors.InvalidRequest(
                "ModelScope image requests do not support masks");
        }

        // Go builds a map[string]any, so keys are emitted in sorted order.
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
            ["prompt"] = prompt,
            ["size"] = ImageSizeOrDefault(request.Image.Size),
        };

        if (request.Image.Images is { Count: > 0 } images)
        {
            var encoded = new List<string>(images.Count);
            foreach (var data in images)
            {
                encoded.Add(EncodeImageBytesToDataUrl(data));
            }

            body["image_url"] = encoded.Count == 1 ? encoded[0] : encoded;
        }

        byte[] payload;
        try
        {
            payload = GoJson.Marshal(body);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "failed to marshal ModelScope image request: " + ex.Message,
                ex);
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "X-ModelScope-Async-Mode", "true");

        // Resolve the key once: the task is created with it, so polling and
        // download must reuse exactly this key rather than resolving a new one.
        var apiKey = _config.ApiKeyProvider!.Get();
        if (apiKey.Length == 0)
        {
            throw TransformerErrors.InvalidRequest(
                "no API key available for ModelScope image request");
        }

        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "POST",
            Url = _baseUrl + ImagePath,
            Headers = headers,
            Body = payload,
            ContentType = "application/json",
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            // The provider-facing format is the ModelScope one so the async
            // task response is never mistaken for an OpenAI image response.
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.ModelScopeImage,
            TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["model"] = JsonSerializer.SerializeToElement(request.Model),
                [ImageTaskApiKeyMeta] = JsonSerializer.SerializeToElement(apiKey),
            },
        };
    }

    /// <summary>Port of transformImageResponse (image.go:171-212).</summary>
    private LlmResponse TransformImageResponse(HttpResponseData httpResponse)
    {
        if (httpResponse.Body is null || httpResponse.Body.Length == 0)
        {
            throw new InvalidOperationException("ModelScope image response body is empty");
        }

        // Reuse the key that created the task for the whole round trip.
        var apiKey = ImageTaskApiKey(httpResponse, _config.ApiKeyProvider!.Get());
        var payload = DecodeImageResponse(httpResponse.Body);

        if (payload.Data is { Count: > 0 } inlineData)
        {
            return ImageResponseFromData(httpResponse, payload.Created, inlineData);
        }

        if (payload.OutputImages is { Count: > 0 } outputImages)
        {
            return ImageResponseFromData(
                httpResponse,
                payload.Created,
                ParseOutputImages(outputImages));
        }

        if (string.IsNullOrEmpty(payload.TaskId))
        {
            throw new InvalidOperationException(
                "ModelScope image response has neither image data nor task_id: "
                + GoStrings.TrimSpace(Encoding.UTF8.GetString(httpResponse.Body)));
        }

        var task = WaitImageTask(
            httpResponse.Request?.RequestCancellationToken ?? CancellationToken.None,
            apiKey,
            payload.TaskId!);

        return ImageResponseFromData(
            httpResponse,
            task.Created,
            ParseOutputImages(task.OutputImages ?? []));
    }

    /// <summary>Port of waitImageTask (image.go:214-261).</summary>
    private ImageResponsePayload WaitImageTask(
        CancellationToken callerToken,
        string apiKey,
        string taskId)
    {
        // The caller's deadline is the outer bound (the pipeline already limits
        // non-streaming requests); TaskTimeout is the fallback budget for
        // callers without one.
        var timeout = _config.TaskTimeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : DefaultImageTaskTimeout;
        var deadline = DateTimeOffset.UtcNow + timeout;

        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadlineSource.CancelAfter(timeout);
        var pollToken = deadlineSource.Token;

        var taskUrl = _baseUrl + TasksPath + GoUrl.PathEscape(taskId);

        while (true)
        {
            var task = GetImageTask(pollToken, apiKey, taskUrl);

            switch ((task.TaskStatus ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "SUCCEED":
                case "SUCCESS":
                    return task;
                case "FAILED":
                    throw new InvalidOperationException(
                        "ModelScope image task " + taskId + " failed: "
                        + ImageTaskErrorMessage(task));
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw ImageTaskTimedOut(taskId);
            }

            var interval = _config.PollInterval is { } configuredInterval
                && configuredInterval > TimeSpan.Zero
                ? configuredInterval
                : DefaultImageTaskPollInterval;

            if (pollToken.WaitHandle.WaitOne(interval))
            {
                callerToken.ThrowIfCancellationRequested();

                throw ImageTaskTimedOut(taskId);
            }
        }
    }

    /// <summary>Port of getImageTask (image.go:263-311).</summary>
    private ImageResponsePayload GetImageTask(
        CancellationToken cancellationToken,
        string apiKey,
        string taskUrl)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");
        HttpClientUtils.HeaderSet(headers, "X-ModelScope-Task-Type", "image_generation");

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = taskUrl,
            Headers = headers,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
        };

        if (_config.HttpClient is { } client)
        {
            HttpResponseData injectedResponse;
            try
            {
                injectedResponse = client.Do(cancellationToken, request);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to query ModelScope image task: " + ex.Message,
                    ex);
            }

            return DecodeImageTask(injectedResponse.Body ?? []);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, taskUrl);
        httpRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
        httpRequest.Headers.TryAddWithoutValidation("X-ModelScope-Task-Type", "image_generation");
        httpRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);

        HttpResponseMessage response;
        try
        {
            response = StaticHttpClient.Send(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to query ModelScope image task: " + ex.Message,
                ex);
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = ReadAll(response.Content.ReadAsStream(cancellationToken));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to read ModelScope image task response: " + ex.Message,
                    ex);
            }

            if ((int)response.StatusCode >= 400)
            {
                throw new InvalidOperationException(
                    "ModelScope image task query failed with status "
                    + (int)response.StatusCode + ": "
                    + GoStrings.TrimSpace(Encoding.UTF8.GetString(body)));
            }

            return DecodeImageTask(body);
        }
    }

    /// <summary>Port of imageTaskAPIKey (image.go:316-324).</summary>
    private static string ImageTaskApiKey(HttpResponseData httpResponse, string fallback)
    {
        if (httpResponse.Request?.TransformerMetadata is { } metadata
            && metadata.TryGetValue(ImageTaskApiKeyMeta, out var key)
            && key.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(key.GetString()))
        {
            return key.GetString()!;
        }

        return fallback;
    }

    /// <summary>Port of decodeImageTask (image.go:326-333).</summary>
    private static ImageResponsePayload DecodeImageTask(byte[] body)
    {
        try
        {
            return LlmJson.Deserialize<ImageResponsePayload>(body) ?? new ImageResponsePayload();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to decode ModelScope image task response: "
                + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }

    /// <summary>Port of the initial imageResponse decode (image.go:179-182).</summary>
    private static ImageResponsePayload DecodeImageResponse(byte[] body)
    {
        try
        {
            return LlmJson.Deserialize<ImageResponsePayload>(body) ?? new ImageResponsePayload();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to decode ModelScope image response: "
                + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }

    /// <summary>Port of imageResponseFromData (image.go:335-388).</summary>
    private LlmResponse ImageResponseFromData(
        HttpResponseData httpResponse,
        long created,
        List<ImageData> data)
    {
        if (data.Count == 0)
        {
            throw new InvalidOperationException("ModelScope image response contains no images");
        }

        if (created == 0)
        {
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        var model = "image-generation";
        if (httpResponse.Request?.TransformerMetadata is { } metadata
            && metadata.TryGetValue("model", out var configuredModel)
            && configuredModel.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(configuredModel.GetString()))
        {
            model = configuredModel.GetString()!;
        }

        var token = httpResponse.Request?.RequestCancellationToken ?? CancellationToken.None;
        var imageResponse = new ImageResponse
        {
            Created = created,
            Data = new List<ImageData>(data.Count),
        };

        foreach (var item in data)
        {
            var base64 = item.B64JSON ?? string.Empty;
            if (base64.Length == 0 && !string.IsNullOrEmpty(item.URL))
            {
                base64 = DownloadImageBase64(token, item.URL);
            }

            imageResponse.Data.Add(new ImageData
            {
                B64JSON = EmptyToNull(base64),
                URL = item.URL,
                RevisedPrompt = item.RevisedPrompt,
            });
        }

        return new LlmResponse
        {
            Id = "modelscope-image-" + created.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Object = "chat.completion",
            Created = created,
            Model = model,
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.ModelScopeImage,
            Image = imageResponse,
        };
    }

    /// <summary>Port of parseOutputImages (image.go:390-421).</summary>
    internal static List<ImageData> ParseOutputImages(IReadOnlyList<JsonElement> raw)
    {
        var images = new List<ImageData>(raw.Count);

        foreach (var item in raw)
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var imageUrl = item.GetString()!;
                if (imageUrl.Length > 0)
                {
                    images.Add(new ImageData { URL = imageUrl });
                    continue;
                }

                // Go's first decode succeeds but is rejected by the non-empty
                // check; the object decode then fails on the string kind.
                throw OutputImageDecodeError("string");
            }

            // Go's object decode leaves a JSON null as the zero struct.
            if (item.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw OutputImageDecodeError(GoKind(item.ValueKind));
            }

            var imageUrlValue = GetOptionalString(item, "url");
            var base64 = GetOptionalString(item, "b64_json");
            if (imageUrlValue.Length == 0 && base64.Length == 0)
            {
                continue;
            }

            images.Add(new ImageData
            {
                URL = EmptyToNull(imageUrlValue),
                B64JSON = EmptyToNull(base64),
            });
        }

        if (images.Count == 0)
        {
            throw new InvalidOperationException("ModelScope image response contains no output images");
        }

        return images;
    }

    /// <summary>Reads an optional string field with Go's field-name matching.</summary>
    private static string GetOptionalString(JsonElement element, string name)
    {
        JsonElement? matched = null;
        foreach (var property in element.EnumerateObject())
        {
            // encoding/json prefers an exact tag match but accepts a
            // case-insensitive one, and later duplicates overwrite.
            if (property.NameEquals(name)
                || string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                matched = property.Value;
            }
        }

        if (matched is null)
        {
            return string.Empty;
        }

        return matched.Value.ValueKind switch
        {
            JsonValueKind.String => matched.Value.GetString()!,
            JsonValueKind.Null => string.Empty,
            _ => throw new InvalidOperationException(
                "failed to decode ModelScope output image: json: cannot unmarshal "
                + GoKind(matched.Value.ValueKind)
                + " into Go value of type string"),
        };
    }

    private static InvalidOperationException OutputImageDecodeError(string kind)
        => new(
            "failed to decode ModelScope output image: json: cannot unmarshal "
            + kind + " into Go value of type " + OutputImageGoType);

    private static string GoKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.String => "string",
        _ => "null",
    };

    /// <summary>Port of downloadImageBase64 (image.go:426-480).</summary>
    private string DownloadImageBase64(CancellationToken callerToken, string imageUrl)
    {
        if (Xurl.IsDataUrl(imageUrl))
        {
            var parsed = Xurl.ParseDataUrl(imageUrl);
            if (parsed is null || !parsed.IsBase64)
            {
                throw new InvalidOperationException("invalid image data URL");
            }

            return parsed.Data;
        }

        using var downloadSource = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        downloadSource.CancelAfter(DefaultImageDownloadTimeout);
        var downloadToken = downloadSource.Token;

        if (_config.HttpClient is { } client)
        {
            HttpResponseData response;
            try
            {
                response = client.Do(downloadToken, new HttpRequestData
                {
                    Method = "GET",
                    Url = imageUrl,
                });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to download ModelScope output image: " + ex.Message,
                    ex);
            }

            if (response.Body is null || response.Body.Length == 0)
            {
                throw new InvalidOperationException("downloaded ModelScope output image is empty");
            }

            return Convert.ToBase64String(response.Body);
        }

        HttpRequestMessage request;
        try
        {
            request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to create image download request: " + ex.Message,
                ex);
        }

        using (request)
        {
            HttpResponseMessage response;
            try
            {
                response = StaticHttpClient.Send(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    downloadToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to download ModelScope output image: " + ex.Message,
                    ex);
            }

            using (response)
            {
                if ((int)response.StatusCode >= 400)
                {
                    throw new InvalidOperationException(
                        "failed to download ModelScope output image: HTTP "
                        + (int)response.StatusCode);
                }

                byte[] body;
                try
                {
                    body = ReadAll(response.Content.ReadAsStream(downloadToken));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "failed to read ModelScope output image: " + ex.Message,
                        ex);
                }

                if (body.Length == 0)
                {
                    throw new InvalidOperationException("downloaded ModelScope output image is empty");
                }

                return Convert.ToBase64String(body);
            }
        }
    }

    /// <summary>Port of encodeImageBytesToDataURL (image.go:482-489).</summary>
    private static string EncodeImageBytesToDataUrl(byte[] data)
    {
        var mediaType = GoHttpSniff.DetectContentType(data);
        if (!mediaType.StartsWith("image/", StringComparison.Ordinal))
        {
            mediaType = "image/png";
        }

        return Xurl.BuildDataUrl(mediaType, Convert.ToBase64String(data), true);
    }

    /// <summary>Port of imageTaskErrorMessage (image.go:491-505).</summary>
    private static string ImageTaskErrorMessage(ImageResponsePayload? task)
    {
        if (task is null)
        {
            return "unknown error";
        }

        if (!string.IsNullOrEmpty(task.Message))
        {
            return task.Message;
        }

        if (task.Errors is not null && !string.IsNullOrEmpty(task.Errors.Message))
        {
            return task.Errors.Message;
        }

        return "unknown error";
    }

    private static InvalidOperationException ImageTaskTimedOut(string taskId)
        => new("ModelScope image task " + taskId + " timed out");

    private static string? EmptyToNull(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
