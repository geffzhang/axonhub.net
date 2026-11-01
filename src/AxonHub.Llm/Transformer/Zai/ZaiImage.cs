using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Zai;

/// <summary>
/// Port of llm/transformer/zai/image.go: the ZAI image-generation request and
/// response, including downloading the returned image into a base64 data URL.
/// </summary>
public sealed partial class ZaiOutboundTransformer
{
    /// <summary>Port of ZAIImagesResponse (image.go:250-253).</summary>
    private sealed class ZaiImagesResponse
    {
        [JsonPropertyName("created")]
        public long Created { get; set; }

        [JsonPropertyName("data")]
        public List<ZaiImageData>? Data { get; set; }
    }

    /// <summary>Port of ZAIImageData (image.go:255-258).</summary>
    private sealed class ZaiImageData
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    /// <summary>Go: buildImageGenerationAPIRequest (image.go:21-53).</summary>
    private HttpRequestData BuildImageGenerationApiRequest(LlmRequest chatReq)
    {
        if (chatReq.Image is null)
        {
            throw TransformerErrors.InvalidRequest("image request is required");
        }

        if (!string.IsNullOrEmpty(chatReq.ApiFormat)
            && chatReq.ApiFormat != ApiFormats.OpenAIImageGeneration)
        {
            throw TransformerErrors.InvalidRequest("ZAI only supports image generation");
        }

        if (chatReq.Image.Images is { Count: > 0 } || chatReq.Image.Mask is { Length: > 0 })
        {
            throw TransformerErrors.InvalidRequest(
                "ZAI does not support image editing with input images");
        }

        var rawReq = BuildImageGenerateRequest(chatReq);

        rawReq.RequestType = RequestTypes.Image;
        rawReq.ApiFormat = ApiFormats.OpenAIImageGeneration;

        // Save model to TransformerMetadata for response transformation.
        rawReq.TransformerMetadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        rawReq.TransformerMetadata["model"] = JsonSerializer.SerializeToElement(chatReq.Model);

        return rawReq;
    }

    /// <summary>Go: buildImageGenerateRequest (image.go:56-118).</summary>
    private HttpRequestData BuildImageGenerateRequest(LlmRequest chatReq)
    {
        var prompt = chatReq.Image!.Prompt ?? string.Empty;
        if (prompt.Length == 0)
        {
            throw new InvalidOperationException("prompt is required for image generation");
        }

        // Go builds a map[string]any, so keys are emitted in sorted order.
        var reqBody = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = chatReq.Model,
            ["prompt"] = prompt,
        };

        var quality = chatReq.Image.Quality ?? string.Empty;
        quality = quality switch
        {
            "high" => "hd",
            "low" or "" => "standard",
            _ => quality,
        };

        if (quality.Length == 0)
        {
            quality = "standard";
        }

        reqBody["quality"] = quality;
        reqBody["size"] = string.IsNullOrEmpty(chatReq.Image.Size)
            ? "1024x1024"
            : chatReq.Image.Size;
        reqBody["watermark_enabled"] = false;

        // User ID from metadata (following the pattern from TransformRequest).
        if (!string.IsNullOrEmpty(chatReq.Image.User))
        {
            reqBody["user_id"] = chatReq.Image.User;
        }

        byte[] body;
        try
        {
            body = GoJson.Marshal(reqBody);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "failed to marshal request body: " + ex.Message, ex);
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "POST",
            Url = BaseUrl + "/images/generations",
            Headers = headers,
            Body = body,
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            ContentType = "application/json",
        };
    }

    /// <summary>Go: transformImageGenerationResponse (image.go:122-168).</summary>
    internal static LlmResponse TransformImageGenerationResponse(HttpResponseData httpResp)
    {
        ZaiImagesResponse zaiResp;
        try
        {
            zaiResp = LlmJson.Deserialize<ZaiImagesResponse>(httpResp.Body!)
                ?? throw new InvalidOperationException(
                    "failed to unmarshal images response: unexpected null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal images response: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        // Read model from request metadata.
        var model = "image-generation";
        if (httpResp.Request?.Metadata is { } metadata
            && metadata.TryGetValue("model", out var m)
            && !string.IsNullOrEmpty(m))
        {
            model = m;
        }

        var llmResp = new LlmResponse
        {
            Id = "zai-img-" + Guid.NewGuid().ToString(),
            Object = "chat.completion",
            Created = zaiResp.Created,
            Model = model,
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
        };

        var imageResp = new ImageResponse
        {
            Created = zaiResp.Created,
            Data = new List<ImageData>(zaiResp.Data?.Count ?? 0),
        };

        if (zaiResp.Data is not null)
        {
            foreach (var img in zaiResp.Data)
            {
                var imageDataUrl = DownloadImageToDataUrl(img.Url ?? string.Empty);
                var index = imageDataUrl.IndexOf("base64,", StringComparison.Ordinal);
                var b64 = index >= 0 ? imageDataUrl[(index + "base64,".Length)..] : string.Empty;

                imageResp.Data.Add(new ImageData
                {
                    B64JSON = b64,
                    URL = img.Url ?? string.Empty,
                });
            }
        }

        llmResp.Image = imageResp;

        return llmResp;
    }

    /// <summary>Go: downloadImageToDataURL (image.go:171-211).</summary>
    private static string DownloadImageToDataUrl(string imageUrl)
    {
        try
        {
            // Go: isDataURL requires len(url) > 5 && url[:5] == "data:".
            if (imageUrl.Length > 5 && imageUrl.StartsWith("data:", StringComparison.Ordinal))
            {
                return imageUrl;
            }

            using var response = DownloadClient.GetAsync(imageUrl).GetAwaiter().GetResult();

            if ((int)response.StatusCode != 200)
            {
                throw new InvalidOperationException(
                    "failed to download image: HTTP "
                    + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            byte[] imageData;
            try
            {
                imageData = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                throw new InvalidOperationException(
                    "failed to read image data: " + ex.Message, ex);
            }

            // Go: resp.Header.Get("Content-Type") — the raw header value.
            var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
            if (contentType.Length == 0)
            {
                // Default to png if content type is not available.
                contentType = "image/png";
            }

            var base64Data = Convert.ToBase64String(imageData);
            return Xurl.BuildDataUrl(contentType, base64Data, true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            throw new InvalidOperationException(
                "failed to download and convert image: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Go uses <c>http.DefaultClient</c> with the caller's context; the port has
    /// no context, so a shared client stands in. See difference C102.
    /// </summary>
    private static readonly HttpClient DownloadClient = new();
}
