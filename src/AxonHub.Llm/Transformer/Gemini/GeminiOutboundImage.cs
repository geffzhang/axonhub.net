using System.Text.Json;
using System.Text.RegularExpressions;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Gemini;

public sealed partial class GeminiOutboundTransformer
{
    /// <summary>
    /// Port of buildImageGenerationRequest (image.go:19-104): Gemini uses the
    /// generateContent endpoint with responseModalities: ["TEXT", "IMAGE"].
    /// </summary>
    public HttpRequestData BuildImageGenerationRequest(LlmRequest llmReq)
    {
        if (llmReq.Image is null)
        {
            throw TransformerErrors.InvalidRequest("image request is required");
        }

        if (string.IsNullOrEmpty(llmReq.Image.Prompt))
        {
            throw TransformerErrors.InvalidRequest("prompt is required for image generation");
        }

        if (llmReq.Model.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        // Build content parts
        var parts = new List<Part>();

        // Add input images first (for image editing scenario)
        foreach (var imgData in llmReq.Image.Images ?? [])
        {
            var mimeType = DetectImageMIMEType(imgData);
            var base64Data = Convert.ToBase64String(imgData);

            parts.Add(new Part
            {
                InlineData = new Blob
                {
                    MIMEType = mimeType,
                    Data = base64Data,
                },
            });
        }

        // Add text prompt
        parts.Add(new Part { Text = llmReq.Image.Prompt });

        // Build Gemini request
        var geminiReq = new GenerateContentRequest
        {
            Contents =
            [
                new Content
                {
                    Role = "user",
                    Parts = parts,
                },
            ],
            GenerationConfig = new GenerationConfig
            {
                ResponseModalities = ["TEXT", "IMAGE"],
                ImageConfig = BuildImageConfig(llmReq.Image),
            },
        };

        byte[] body;
        try
        {
            body = GoJson.Marshal(geminiReq);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to marshal gemini image request: " + ex.Message, ex);
        }

        // Prepare headers
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
            ["Accept"] = ["application/json"],
        };

        // Prepare authentication
        AuthConfig? auth = null;

        var apiKey = _config.ApiKeyProvider?.Get() ?? "";
        if (apiKey.Length > 0)
        {
            auth = new AuthConfig
            {
                Type = AuthTypes.ApiKey,
                ApiKey = apiKey,
                HeaderKey = "x-goog-api-key",
            };
        }

        // Build URL - image generation uses the same generateContent endpoint
        var url = BuildImageRequestUrl(llmReq);

        var rawReq = new HttpRequestData
        {
            Method = "POST",
            Url = url,
            Headers = headers,
            Body = body,
            Auth = auth,
            RequestType = RequestTypes.Image,
            SkipInboundQueryMerge = true,
        };

        // Save model to TransformerMetadata for response transformation
        rawReq.TransformerMetadata = new Dictionary<string, JsonElement>
        {
            ["model"] = JsonSerializer.SerializeToElement(llmReq.Model, LlmJson.Options),
        };

        return rawReq;
    }

    /// <summary>
    /// Port of buildImageRequestURL (image.go:108-126): image generation uses
    /// the generateContent endpoint (not streaming).
    /// </summary>
    public string BuildImageRequestUrl(LlmRequest llmReq)
    {
        var version = _config.ApiVersion ?? "";
        if (version.Length == 0)
        {
            version = GeminiConstants.DefaultAPIVersion;
        }

        // For Vertex AI platform
        if (_config.PlatformType == GeminiConstants.PlatformVertex)
        {
            var baseUrl = TrimSingleTrailingSlash(ResolveBaseUrl());
            if (baseUrl.Contains("/v1/", StringComparison.Ordinal))
            {
                return $"{baseUrl}/publishers/google/models/{llmReq.Model}:generateContent";
            }

            return $"{baseUrl}/v1/publishers/google/models/{llmReq.Model}:generateContent";
        }

        // Standard Gemini API format
        return $"{ResolveBaseUrl()}/{version}/models/{llmReq.Model}:generateContent";
    }

    /// <summary>Port of buildImageConfig (image.go:129-147).</summary>
    public static ImageConfig? BuildImageConfig(ImageRequest? img)
    {
        if (img is null)
        {
            return null;
        }

        var config = new ImageConfig();

        // Map size to aspectRatio if provided
        if (!string.IsNullOrEmpty(img.Size))
        {
            config.AspectRatio = MapSizeToAspectRatio(img.Size!);
        }

        // Map size to imageSize if provided (e.g., "1024x1024" -> "1K", "2048x2048" -> "2K")
        if (!string.IsNullOrEmpty(img.Size))
        {
            config.ImageSize = MapSizeToImageSize(img.Size!);
        }

        return config;
    }

    /// <summary>Port of mapSizeToAspectRatio (image.go:151-174).</summary>
    public static string MapSizeToAspectRatio(string size)
    {
        switch (size)
        {
            case "1024x1024":
            case "512x512":
            case "256x256":
                return "1:1";
            case "1792x1024":
                return "16:9";
            case "1024x1792":
                return "9:16";
            case "1536x1024":
                return "3:2";
            case "1024x1536":
                return "2:3";
            case "1024x768":
                return "4:3";
            case "768x1024":
                return "3:4";
            default:
                // Check if it's already an aspect ratio format
                return size.Contains(':', StringComparison.Ordinal) ? size : "1:1";
        }
    }

    /// <summary>Port of mapSizeToImageSize (image.go:177-204).</summary>
    public static string MapSizeToImageSize(string size)
    {
        switch (size)
        {
            case "256x256":
            case "512x512":
            case "1024x1024":
            case "1024x1536":
            case "1024x1792":
            case "1024x768":
            case "1536x1024":
            case "768x1024":
            case "1792x1024":
                return "1K";
            case "2048x2048":
            case "2048x1536":
            case "2048x1152":
            case "1536x2048":
            case "1152x2048":
                return "2K";
            case "4096x4096":
            case "4096x3072":
            case "4096x2304":
            case "3072x4096":
            case "2304x4096":
                return "4K";
            default:
                // Check if size contains dimensions and calculate approximate pixel
                // count (Go: fmt.Sscanf(size, "%dx%d"), which skips leading
                // whitespace and ignores trailing junk).
                var match = Wxh().Match(size);
                if (match.Success
                    && int.TryParse(match.Groups[1].Value, out var width)
                    && int.TryParse(match.Groups[2].Value, out var height))
                {
                    var pixels = (long)width * height;
                    if (pixels <= 1024 * 1024)
                    {
                        return "1K";
                    }

                    return pixels <= 2048 * 2048 ? "2K" : "4K";
                }

                return "1K"; // default
        }
    }

    /// <summary>Port of detectImageMIMEType (image.go:207-235).</summary>
    public static string DetectImageMIMEType(byte[] data)
    {
        if (data.Length < 4)
        {
            return "image/png"; // default
        }

        // PNG: 89 50 4E 47
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }

        // JPEG: FF D8 FF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        // GIF: GIF87a or GIF89a
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
        {
            return "image/gif";
        }

        // WebP: RIFF....WEBP
        if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return "image/webp";
        }

        return "image/png"; // default
    }

    /// <summary>Port of transformImageGenerationResponse (image.go:239-302).</summary>
    public static LlmResponse TransformImageGenerationResponse(HttpResponseData httpResp)
    {
        if (httpResp.StatusCode >= 400)
        {
            throw new InvalidOperationException($"HTTP error {httpResp.StatusCode}");
        }

        if (httpResp.Body is not { Length: > 0 })
        {
            throw new InvalidOperationException("response body is empty");
        }

        // Go decodes a JSON "null" body into the zero struct; the port maps the
        // null result of the deserializer back to the zero struct.
        GenerateContentResponse geminiResp;
        try
        {
            geminiResp = LlmJson.Deserialize<GenerateContentResponse>(httpResp.Body)
                ?? new GenerateContentResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal gemini image response: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }

        // Read model from request TransformerMetadata
        var model = "image-generation";

        if (httpResp.Request?.TransformerMetadata is { } metadata
            && metadata.TryGetValue("model", out var m)
            && m.ValueKind == JsonValueKind.String
            && m.GetString() is { Length: > 0 } modelValue)
        {
            model = modelValue;
        }

        // Build the base response
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var resp = new LlmResponse
        {
            Id = geminiResp.ResponseId ?? "",
            Object = "chat.completion",
            Created = created,
            Model = model,
            RequestType = RequestTypes.Image,
        };

        // Convert usage information
        if (geminiResp.UsageMetadata is not null)
        {
            resp.Usage = GeminiConvert.ConvertToLlmUsage(geminiResp.UsageMetadata);
        }

        // Build ImageResponse from candidates
        var imageResponse = new ImageResponse
        {
            Created = created,
            Data = [],
        };

        // Extract images from the response candidates
        foreach (var candidate in geminiResp.Candidates ?? [])
        {
            if (candidate.Content is null)
            {
                continue;
            }

            foreach (var part in candidate.Content.Parts ?? [])
            {
                if (part.InlineData is { MIMEType: not null } inlineData
                    && inlineData.MIMEType.StartsWith("image/", StringComparison.Ordinal))
                {
                    imageResponse.Data.Add(new ImageData
                    {
                        B64JSON = inlineData.Data,
                    });
                }
            }
        }

        resp.Image = imageResponse;

        return resp;
    }

    [GeneratedRegex(@"^\s*(\d+)x(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex Wxh();
}
