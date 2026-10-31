using System.Globalization;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of the image half of openai.OutboundTransformer
/// (llm/transformer/openai/image_outbound.go): builds the
/// <c>/images/generations</c>, <c>/images/edits</c> and <c>/images/variations</c>
/// requests and decodes the images response into the unified model.
/// </summary>
public sealed partial class OpenAiOutboundTransformer
{
    /// <summary>Go: <c>buildImageGenerationAPIRequest</c>.</summary>
    internal HttpRequestData BuildImageGenerationApiRequest(LlmRequest chatReq)
    {
        chatReq.Stream = false;

        if (chatReq.Image is null)
        {
            throw new InvalidOperationException("image request is required");
        }

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        HttpRequestData rawReq;
        string fmtType;

        switch (chatReq.ApiFormat)
        {
            case ApiFormats.OpenAIImageVariation:
                rawReq = BuildImageVariationRequest(chatReq, apiKey);
                fmtType = ApiFormats.OpenAIImageVariation;
                break;
            case ApiFormats.OpenAIImageEdit:
                rawReq = BuildImageEditRequest(chatReq, apiKey);
                fmtType = ApiFormats.OpenAIImageEdit;
                break;
            default:
                rawReq = BuildImageGenerateRequest(chatReq, apiKey);
                fmtType = ApiFormats.OpenAIImageGeneration;
                break;
        }

        rawReq.RequestType = RequestTypes.Image;
        rawReq.ApiFormat = fmtType;

        // Save model to TransformerMetadata for response transformation.
        rawReq.TransformerMetadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        rawReq.TransformerMetadata["model"] = JsonSerializer.SerializeToElement(chatReq.Model);

        return rawReq;
    }

    /// <summary>Go: <c>supportsImageGenerationResponseFormat</c>.</summary>
    internal static bool SupportsImageGenerationResponseFormat(string model)
        => model is "dall-e-2" or "dall-e-3";

    /// <summary>Go: <c>supportsImageEditResponseFormat</c>.</summary>
    internal static bool SupportsImageEditResponseFormat(string model) => model == "dall-e-2";

    /// <summary>Go: <c>supportsImageVariationResponseFormat</c>.</summary>
    internal static bool SupportsImageVariationResponseFormat(string model)
        => model.Length == 0 || model == "dall-e-2";

    /// <summary>Go: <c>buildImageGenerateRequest</c> (images/generations).</summary>
    internal HttpRequestData BuildImageGenerateRequest(LlmRequest chatReq, string apiKey)
    {
        var model = chatReq.Model;
        var img = chatReq.Image!;

        var prompt = img.Prompt ?? string.Empty;
        if (prompt.Length == 0)
        {
            throw new InvalidOperationException("prompt is required for image generation");
        }

        var reqBody = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["prompt"] = prompt,
            ["model"] = model,
        };

        // Forward input images for image-to-image generation (gpt-image-1).
        if (img.Images is { Count: > 0 } images)
        {
            var dataUrls = new List<string>(images.Count);
            foreach (var data in images)
            {
                dataUrls.Add(EncodeImageBytesToDataUrl(data));
            }

            reqBody["image"] = dataUrls.Count == 1 ? dataUrls[0] : dataUrls;
        }

        if (img.N is { } n)
        {
            reqBody["n"] = n;
        }

        if (!string.IsNullOrEmpty(img.OutputFormat))
        {
            reqBody["output_format"] = img.OutputFormat;
        }

        if (!string.IsNullOrEmpty(img.Size))
        {
            reqBody["size"] = img.Size;
        }

        if (!string.IsNullOrEmpty(img.Quality))
        {
            reqBody["quality"] = img.Quality;
        }

        if (!string.IsNullOrEmpty(img.Background))
        {
            reqBody["background"] = img.Background;
        }

        if (!string.IsNullOrEmpty(img.Moderation))
        {
            reqBody["moderation"] = img.Moderation;
        }

        if (!string.IsNullOrEmpty(img.Style))
        {
            reqBody["style"] = img.Style;
        }

        if (img.OutputCompression is { } outputCompression)
        {
            reqBody["output_compression"] = outputCompression;
        }

        if (img.PartialImages is { } partialImages)
        {
            reqBody["partial_images"] = partialImages;
        }

        if (!string.IsNullOrEmpty(img.ResponseFormat)
            && SupportsImageGenerationResponseFormat(model))
        {
            reqBody["response_format"] = img.ResponseFormat;
        }

        if (SupportsImageGenerationResponseFormat(chatReq.Model)
            && !reqBody.ContainsKey("response_format"))
        {
            reqBody["response_format"] = "b64_json";
        }

        var body = GoJson.Marshal(reqBody);

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        return new HttpRequestData
        {
            Method = "POST",
            Url = ImageUrl("/images/generations"),
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
        };
    }

    /// <summary>Go: <c>buildImageEditRequest</c> (images/edits, multipart).</summary>
    internal HttpRequestData BuildImageEditRequest(LlmRequest chatReq, string apiKey)
    {
        var model = chatReq.Model;
        var img = chatReq.Image!;

        var prompt = img.Prompt ?? string.Empty;
        if (prompt.Length == 0)
        {
            throw new InvalidOperationException("prompt is required for image generation");
        }

        var formFiles = new List<FormFile>();
        foreach (var data in img.Images ?? [])
        {
            formFiles.Add(NewImageFormFile(
                string.Create(CultureInfo.InvariantCulture, $"image_{formFiles.Count + 1}"),
                data));
        }

        FormFile? maskFile = null;
        if (img.Mask is { Length: > 0 } mask)
        {
            maskFile = NewImageFormFile("mask", mask);
        }

        if (formFiles.Count == 0)
        {
            // The reference joins the detail and the sentinel without a colon.
            throw new TransformerException(
                TransformerErrorKind.InvalidRequest,
                "at least one image is required for image editing,invalid request");
        }

        // Build multipart form data and JSONBody together.
        var jsonBody = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["prompt"] = prompt,
            ["formFiles"] = formFiles,
        };

        var writer = new GoMultipartWriter();

        if (model.Length > 0)
        {
            writer.WriteField("model", model);
            jsonBody["model"] = model;
        }

        var imageFieldName = formFiles.Count > 1 ? "image[]" : "image";

        // Add images with proper MIME headers.
        foreach (var formFile in formFiles)
        {
            writer.CreatePart(imageFieldName, formFile.Filename, formFile.ContentType, formFile.Data);
        }

        if (maskFile is not null)
        {
            writer.CreatePart("mask", maskFile.Filename, maskFile.ContentType, maskFile.Data);
            jsonBody["mask"] = maskFile;
        }

        writer.WriteField("prompt", prompt);

        if (img.N is { } n)
        {
            writer.WriteField("n", n.ToString(CultureInfo.InvariantCulture));
            jsonBody["n"] = n;
        }

        if (!string.IsNullOrEmpty(img.OutputFormat))
        {
            writer.WriteField("output_format", img.OutputFormat);
            jsonBody["output_format"] = img.OutputFormat;
        }

        if (!string.IsNullOrEmpty(img.Size))
        {
            writer.WriteField("size", img.Size);
            jsonBody["size"] = img.Size;
        }

        if (!string.IsNullOrEmpty(img.Quality))
        {
            writer.WriteField("quality", img.Quality);
            jsonBody["quality"] = img.Quality;
        }

        if (!string.IsNullOrEmpty(img.Background))
        {
            writer.WriteField("background", img.Background);
            jsonBody["background"] = img.Background;
        }

        if (!string.IsNullOrEmpty(img.InputFidelity))
        {
            writer.WriteField("input_fidelity", img.InputFidelity);
            jsonBody["input_fidelity"] = img.InputFidelity;
        }

        if (img.OutputCompression is { } outputCompression)
        {
            writer.WriteField(
                "output_compression",
                outputCompression.ToString(CultureInfo.InvariantCulture));
            jsonBody["output_compression"] = outputCompression;
        }

        if (img.PartialImages is { } partialImages)
        {
            writer.WriteField(
                "partial_images",
                partialImages.ToString(CultureInfo.InvariantCulture));
            jsonBody["partial_images"] = partialImages;
        }

        if (!string.IsNullOrEmpty(img.ResponseFormat)
            && SupportsImageEditResponseFormat(model))
        {
            writer.WriteField("response_format", img.ResponseFormat);
            jsonBody["response_format"] = img.ResponseFormat;
        }

        if (SupportsImageEditResponseFormat(model)
            && !jsonBody.ContainsKey("response_format"))
        {
            writer.WriteField("response_format", "b64_json");
            jsonBody["response_format"] = "b64_json";
        }

        if (!string.IsNullOrEmpty(img.User))
        {
            writer.WriteField("user", img.User);
            jsonBody["user"] = img.User;
        }

        writer.Close();

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", writer.FormDataContentType());
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        return new HttpRequestData
        {
            Method = "POST",
            Url = ImageUrl("/images/edits"),
            Headers = headers,
            ContentType = writer.FormDataContentType(),
            Body = writer.ToArray(),
            JsonBody = GoJson.Marshal(jsonBody),
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
        };
    }

    /// <summary>Go: <c>buildImageVariationRequest</c> (images/variations, multipart).</summary>
    internal HttpRequestData BuildImageVariationRequest(LlmRequest chatReq, string apiKey)
    {
        var model = chatReq.Model;
        var img = chatReq.Image!;

        var formFiles = new List<FormFile>();
        foreach (var data in img.Images ?? [])
        {
            formFiles.Add(NewImageFormFile(
                string.Create(CultureInfo.InvariantCulture, $"image_{formFiles.Count + 1}"),
                data));
        }

        if (formFiles.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("image is required for image variations");
        }

        if (formFiles.Count > 1)
        {
            throw TransformerErrors.InvalidRequest(
                "only one image is supported for image variations");
        }

        var writer = new GoMultipartWriter();

        var imageFile = formFiles[0];
        writer.CreatePart("image", imageFile.Filename, imageFile.ContentType, imageFile.Data);

        var jsonBody = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["formFiles"] = formFiles,
        };

        if (model.Length > 0)
        {
            writer.WriteField("model", model);
            jsonBody["model"] = model;
        }

        if (img.N is { } n)
        {
            writer.WriteField("n", n.ToString(CultureInfo.InvariantCulture));
            jsonBody["n"] = n;
        }

        if (!string.IsNullOrEmpty(img.Size))
        {
            writer.WriteField("size", img.Size);
            jsonBody["size"] = img.Size;
        }

        if (!string.IsNullOrEmpty(img.ResponseFormat)
            && SupportsImageVariationResponseFormat(model))
        {
            writer.WriteField("response_format", img.ResponseFormat);
            jsonBody["response_format"] = img.ResponseFormat;
        }

        if (SupportsImageVariationResponseFormat(model)
            && !jsonBody.ContainsKey("response_format"))
        {
            writer.WriteField("response_format", "b64_json");
            jsonBody["response_format"] = "b64_json";
        }

        writer.Close();

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", writer.FormDataContentType());
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        return new HttpRequestData
        {
            Method = "POST",
            Url = ImageUrl("/images/variations"),
            Headers = headers,
            ContentType = writer.FormDataContentType(),
            Body = writer.ToArray(),
            JsonBody = GoJson.Marshal(jsonBody),
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
        };
    }

    /// <summary>Port of openai.FormFile.</summary>
    public sealed class FormFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("content_type")]
        public string ContentType { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public byte[] Data { get; set; } = [];

        /// <summary>Image format like "png", "jpeg", etc.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("format")]
        public string Format { get; set; } = string.Empty;
    }

    /// <summary>Go: <c>newImageFormFile</c>.</summary>
    internal static FormFile NewImageFormFile(string name, byte[] data)
    {
        var contentType = GoHttpSniff.DetectContentType(data);
        var extension = "png";
        var format = "png";

        switch (contentType)
        {
            case "image/jpeg":
                extension = "jpg";
                format = "jpg";
                break;
            case "image/gif":
                extension = "gif";
                format = "gif";
                break;
            case "image/webp":
                extension = "webp";
                format = "webp";
                break;
            case "image/png":
                break;
            default:
                contentType = "image/png";
                break;
        }

        return new FormFile
        {
            Filename = name + "." + extension,
            ContentType = contentType,
            Data = data,
            Format = format,
        };
    }

    /// <summary>
    /// Port of the reference's <c>extractFile</c>. Dead code there as well — kept
    /// because its behaviour is covered by the ported tests.
    /// </summary>
    internal static FormFile ExtractFile(string url)
    {
        // Check if it's a data URL (data:image/png;base64,...).
        if (url.Length > 5 && url.StartsWith("data:", StringComparison.Ordinal))
        {
            // Find the base64 data part.
            var comma = url.IndexOf(',');
            if (comma < 0)
            {
                throw TransformerErrors.InvalidRequest(
                    "invalid data URL format: missing comma separator");
            }

            // Extract format from data URL prefix (e.g., "data:image/png;base64,").
            var header = url[..comma];
            var format = "png"; // default format

            // Parse format from header like "data:image/png;base64" or "data:image/jpeg;base64".
            if (header.Length > 11
                && header.StartsWith("data:", StringComparison.Ordinal)
                && string.CompareOrdinal(header, 5, "image/", 0, 6) == 0)
            {
                // Extract format between "image/" and ";base64" or just "image/" if no base64 specified.
                var formatPart = header[11..];
                var semicolonIndex = formatPart.IndexOf(';');
                format = semicolonIndex > 0 ? formatPart[..semicolonIndex] : formatPart;
            }

            byte[] data;
            try
            {
                data = Convert.FromBase64String(url[(comma + 1)..]);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    "failed to decode base64 data: " + ex.Message,
                    ex);
            }

            return new FormFile
            {
                Filename = "image." + format,
                ContentType = "image/" + format,
                Data = data,
                Format = format,
            };
        }

        throw TransformerErrors.InvalidRequest(
            "only data URLs are supported for image editing");
    }

    /// <summary>
    /// Port of transformImageGenerationResponse: the upstream images body to the
    /// unified response. The empty/status checks live in TransformResponse.
    /// </summary>
    internal static LlmResponse TransformImageGenerationResponse(HttpResponseData httpResp)
    {
        var imgResp = DecodeImagesResponse(httpResp.Body ?? []);

        // Read model from request TransformerMetadata.
        var model = "image-generation";
        if (httpResp.Request?.TransformerMetadata is { } metadata
            && metadata.TryGetValue("model", out var element)
            && element.ValueKind == JsonValueKind.String
            && element.GetString() is { Length: > 0 } metadataModel)
        {
            model = metadataModel;
        }

        var resp = new LlmResponse
        {
            Id = "img-" + imgResp.Created.ToString(CultureInfo.InvariantCulture),
            Object = "chat.completion",
            Created = imgResp.Created,
            Model = model,
            RequestType = RequestTypes.Image,
        };

        var imageResponse = new ImageResponse
        {
            Created = imgResp.Created,
            Data = [],
            Background = ImageNullIfEmpty(imgResp.Background),
            OutputFormat = ImageNullIfEmpty(imgResp.OutputFormat),
            Quality = ImageNullIfEmpty(imgResp.Quality),
            Size = ImageNullIfEmpty(imgResp.Size),
        };

        if (imgResp.Usage is { } usage)
        {
            resp.Usage = new Usage
            {
                PromptTokens = usage.InputTokens,
                CompletionTokens = usage.OutputTokens,
                TotalTokens = usage.TotalTokens,
            };

            if (usage.InputTokensDetails is { } inputDetails)
            {
                resp.Usage.PromptTokensDetails = new PromptTokensDetails
                {
                    ImageTokens = inputDetails.ImageTokens,
                    TextTokens = inputDetails.TextTokens,
                    CachedTokens = inputDetails.CachedTokens,
                };
            }

            if (usage.OutputTokensDetails is { } outputDetails)
            {
                resp.Usage.CompletionTokensDetails = new CompletionTokensDetails
                {
                    ReasoningTokens = outputDetails.ReasoningTokens,
                };
            }
        }

        foreach (var image in imgResp.Data ?? [])
        {
            imageResponse.Data.Add(new ImageData
            {
                B64JSON = ImageNullIfEmpty(image.B64JSON),
                URL = ImageNullIfEmpty(image.URL),
                RevisedPrompt = ImageNullIfEmpty(image.RevisedPrompt),
            });
        }

        resp.Image = imageResponse;

        return resp;
    }

    private static ImagesResponse DecodeImagesResponse(ReadOnlySpan<byte> body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal images response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind is not null)
        {
            if (rootKind != "null")
            {
                throw new InvalidOperationException(
                    "failed to unmarshal images response: "
                    + GoStructJson.TypeMismatch(rootKind, "openai.ImagesResponse"));
            }

            return new ImagesResponse();
        }

        try
        {
            return LlmJson.Deserialize<ImagesResponse>(body) ?? new ImagesResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal images response: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }
    }

    /// <summary>Go: <c>encodeImageBytesToDataURL</c>.</summary>
    internal static string EncodeImageBytesToDataUrl(byte[] data)
    {
        var contentType = GoHttpSniff.DetectContentType(data);
        if (!contentType.StartsWith("image/", StringComparison.Ordinal))
        {
            contentType = "image/png";
        }

        return Xurl.BuildDataUrl(contentType, Convert.ToBase64String(data), isBase64: true);
    }

    private string ImageUrl(string defaultPath)
    {
        if (_config.EndpointPath.Length > 0)
        {
            return _config.BaseUrl + _config.EndpointPath;
        }

        return _config.BaseUrl + defaultPath;
    }

    private static string? ImageNullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
