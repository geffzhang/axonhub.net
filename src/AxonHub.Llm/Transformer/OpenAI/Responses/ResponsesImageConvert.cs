using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of responses/image_request.go and responses/image_response.go: the
/// Responses image tool request builder and the unified image response
/// builder. Difference C059.
/// </summary>
public static class ResponsesImageConvert
{
    /// <summary>Go: <c>ImageGenerationToolModelMetadataKey</c>.</summary>
    public const string ImageGenerationToolModelMetadataKey = "image_generation_tool_model";

    /// <summary>Go: the default system instruction in buildImageToolRequest.</summary>
    public const string DefaultImageSystemInstruction =
        "You are a helpful assistant that can generate images based on user requests. "
        + "Must use the image generation tool.";

    /// <summary>
    /// Port of buildImageToolRequest: converts a unified image request into a
    /// Responses chat request that drives the <c>image_generation</c> tool.
    /// </summary>
    public static LlmRequest BuildImageToolRequest(LlmRequest? src)
    {
        if (src?.Image is null)
        {
            throw TransformerErrors.InvalidRequest("image request is required");
        }

        if (src.ApiFormat != ApiFormats.OpenAIImageGeneration
            && src.ApiFormat != ApiFormats.OpenAIImageEdit)
        {
            throw TransformerErrors.InvalidRequest(
                "responses image tool only supports generation and edit");
        }

        var prompt = src.Image.Prompt ?? string.Empty;
        if (prompt.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("prompt is required");
        }

        if (src.Image.N is > 1)
        {
            throw TransformerErrors.InvalidRequest(
                "responses image tool does not support n greater than 1");
        }

        var action = "generate";
        var imageUrls = ImageDataUrls(src);
        if (src.ApiFormat == ApiFormats.OpenAIImageEdit)
        {
            action = "edit";
            if (imageUrls.Count == 0)
            {
                throw TransformerErrors.InvalidRequest(
                    "at least one image is required for edits");
            }
        }

        var content = new List<MessageContentPart>
        {
            new() { Type = "text", Text = prompt },
        };

        foreach (var imageUrl in imageUrls)
        {
            content.Add(new MessageContentPart
            {
                Type = "image_url",
                ImageUrl = new ImageUrl { Url = imageUrl },
            });
        }

        // Always include a default system instruction and preserve any system
        // messages from the source request so they are emitted as top-level
        // instructions in the Responses API payload.
        var messages = new List<Message>
        {
            new()
            {
                Role = "system",
                Content = MessageContent.FromText(DefaultImageSystemInstruction),
            },
        };

        foreach (var message in src.Messages)
        {
            if (message.Role == "system")
            {
                messages.Add(message);
            }
        }

        messages.Add(new Message
        {
            Role = "user",
            Content = MessageContent.FromParts(content),
        });

        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (src.TransformerMetadata is not null)
        {
            foreach (var (key, value) in src.TransformerMetadata)
            {
                metadata[key] = value;
            }
        }

        var toolModel = src.Model;
        if (ResponsesMetadata.GetStringPtr(metadata, ImageGenerationToolModelMetadataKey) is { } model)
        {
            var trimmed = model.Trim();
            if (trimmed.Length > 0)
            {
                toolModel = trimmed;
            }
        }

        var imageTool = new ImageGeneration
        {
            Model = ResponsesConvertUtil.NullIfEmpty(toolModel),
            Background = ResponsesConvertUtil.NullIfEmpty(src.Image.Background),
            InputFidelity = ResponsesConvertUtil.NullIfEmpty(src.Image.InputFidelity),
            Moderation = ResponsesConvertUtil.NullIfEmpty(src.Image.Moderation),
            OutputCompression = src.Image.OutputCompression,
            OutputFormat = ResponsesConvertUtil.NullIfEmpty(src.Image.OutputFormat),
            PartialImages = src.Image.PartialImages,
            Quality = ResponsesConvertUtil.NullIfEmpty(src.Image.Quality),
            Size = ResponsesConvertUtil.NullIfEmpty(src.Image.Size),
        };

        var maskUrl = MaskDataUrl(src);
        if (maskUrl.Length > 0)
        {
            imageTool.InputImageMask = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["image_url"] = ResponsesConvertUtil.ToElement(maskUrl),
            };
        }

        metadata["image_generation_action"] = ResponsesConvertUtil.ToElement(action);

        // Persist the requested image options so BuildImageResponse can echo
        // them back on the client-facing response.
        if (!string.IsNullOrEmpty(src.Image.OutputFormat))
        {
            metadata["codex_image_output_format"] = ResponsesConvertUtil.ToElement(src.Image.OutputFormat);
        }

        if (!string.IsNullOrEmpty(src.Image.Quality))
        {
            metadata["codex_image_quality"] = ResponsesConvertUtil.ToElement(src.Image.Quality);
        }

        if (!string.IsNullOrEmpty(src.Image.Size))
        {
            metadata["codex_image_size"] = ResponsesConvertUtil.ToElement(src.Image.Size);
        }

        if (!string.IsNullOrEmpty(src.Image.Background))
        {
            metadata["codex_image_background"] = ResponsesConvertUtil.ToElement(src.Image.Background);
        }

        if (toolModel.Length > 0)
        {
            metadata["codex_image_model"] = ResponsesConvertUtil.ToElement(toolModel);
        }

        return new LlmRequest
        {
            Model = src.Model,
            Messages = messages,
            User = !string.IsNullOrEmpty(src.Image.User) ? src.Image.User : src.User,
            Tools =
            [
                new Tool
                {
                    Type = ToolTypes.ImageGeneration,
                    ImageGeneration = imageTool,
                },
            ],
            ToolChoice = new ToolChoice
            {
                ToolChoiceMode = "required",
            },

            // The Responses API has no top-level modalities field; image output
            // is driven by the image_generation tool above, so it is not set.
            Stream = src.Stream,
            StreamOptions = src.StreamOptions,
            Store = src.Store,
            RequestType = RequestTypes.Chat,
            ApiFormat = ApiFormats.OpenAIResponse,
            RawRequest = src.RawRequest,
            TransformerMetadata = metadata,
            TransformOptions = src.TransformOptions,
            ServiceTier = src.ServiceTier,
            SafetyIdentifier = src.SafetyIdentifier,
            PromptCacheKey = src.PromptCacheKey,
            Metadata = src.Metadata,
        };
    }

    /// <summary>Go: <c>imageDataURLs</c>.</summary>
    public static List<string> ImageDataUrls(LlmRequest? req)
    {
        if (req?.Image is null)
        {
            return [];
        }

        var urls = ImageUrlsFromRawJson(req.RawRequest, "image");
        if (urls.Count > 0)
        {
            return urls;
        }

        var result = new List<string>(req.Image.Images?.Count ?? 0);
        foreach (var data in req.Image.Images ?? [])
        {
            result.Add(BytesToImageDataUrl(data));
        }

        return result;
    }

    /// <summary>Go: <c>maskDataURL</c>; empty when there is no mask.</summary>
    public static string MaskDataUrl(LlmRequest? req)
    {
        if (req?.Image is null)
        {
            return string.Empty;
        }

        var urls = ImageUrlsFromRawJson(req.RawRequest, "mask");
        if (urls.Count > 0)
        {
            return urls[0];
        }

        if (req.Image.Mask is not { Length: > 0 } mask)
        {
            return string.Empty;
        }

        return BytesToImageDataUrl(mask);
    }

    /// <summary>
    /// Go: <c>imageURLsFromRawJSON</c>. Reads the raw multipart/JSON request
    /// body and keeps only <c>data:image/</c> URLs; anything else falls back to
    /// the decoded byte arrays.
    /// </summary>
    public static List<string> ImageUrlsFromRawJson(HttpRequestData? raw, string field)
    {
        if (raw?.JsonBody is not { Length: > 0 } jsonBody)
        {
            return [];
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonBody);
        }
        catch (JsonException)
        {
            return [];
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(field, out var rawValue))
            {
                return [];
            }

            // Go: json.Unmarshal into a string succeeds for both strings and
            // null (null leaves the zero value); a non-data URL yields nil.
            if (rawValue.ValueKind is JsonValueKind.String or JsonValueKind.Null)
            {
                var single = rawValue.ValueKind == JsonValueKind.String
                    ? rawValue.GetString()!
                    : string.Empty;

                return single.StartsWith("data:image/", StringComparison.Ordinal)
                    ? [single]
                    : [];
            }

            if (rawValue.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            // Go: json.Unmarshal into []string fails wholesale on a non-string
            // element, so the whole field is rejected.
            var urls = new List<string>();
            foreach (var item in rawValue.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return [];
                }

                var value = item.GetString()!;
                if (value.StartsWith("data:image/", StringComparison.Ordinal))
                {
                    urls.Add(value);
                }
            }

            return urls;
        }
    }

    /// <summary>Go: <c>bytesToImageDataURL</c>.</summary>
    public static string BytesToImageDataUrl(byte[] data)
    {
        var contentType = GoHttpSniff.DetectContentType(data);
        if (!contentType.StartsWith("image/", StringComparison.Ordinal))
        {
            contentType = "image/png";
        }

        return Xurl.BuildDataUrl(contentType, Convert.ToBase64String(data), true);
    }

    /// <summary>
    /// Port of BuildImageResponse: converts a Responses image result to the
    /// unified image response, echoing the request's persisted options.
    /// </summary>
    public static LlmResponse BuildImageResponse(
        ResponsesResponse? upstream,
        IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (upstream is null)
        {
            throw new InvalidOperationException("response is nil");
        }

        if (upstream.Error is not null)
        {
            throw new InvalidOperationException(
                "codex image response error: " + upstream.Error.Message);
        }

        var format = GetMetadataString(metadata, "codex_image_output_format");
        if (string.IsNullOrEmpty(format))
        {
            format = "png";
        }

        var imageResponse = new ImageResponse
        {
            Created = upstream.CreatedAt,
            Data = [],
            OutputFormat = format,
        };

        if (GetMetadataString(metadata, "codex_image_quality") is { } quality)
        {
            imageResponse.Quality = quality;
        }

        if (GetMetadataString(metadata, "codex_image_size") is { } size)
        {
            imageResponse.Size = size;
        }

        if (GetMetadataString(metadata, "codex_image_background") is { } background)
        {
            imageResponse.Background = background;
        }

        foreach (var rawItem in upstream.Output ?? [])
        {
            // Go decodes a null element as the zero Item struct (type "").
            var item = rawItem ?? new ResponsesItem();
            if (item.Type != "image_generation_call" || string.IsNullOrEmpty(item.Result))
            {
                continue;
            }

            imageResponse.Data.Add(new ImageData
            {
                B64JSON = Xurl.ExtractBase64FromDataUrl(item.Result),
            });
        }

        if (imageResponse.Data.Count == 0)
        {
            throw new InvalidOperationException(
                "codex image response did not contain image_generation_call result");
        }

        var result = new LlmResponse
        {
            Id = upstream.Id,
            Object = "image.generation",
            Created = upstream.CreatedAt,
            Model = upstream.Model,
            RequestType = RequestTypes.Image,
            Image = imageResponse,
        };

        if (upstream.Usage is not null)
        {
            result.Usage = upstream.Usage.ToUsage();
        }

        if (GetMetadataString(metadata, "codex_image_model") is { Length: > 0 } model)
        {
            result.Model = model;
        }

        return result;
    }

    /// <summary>
    /// Go: <c>v, ok := metadata[key].(string)</c> — absent and non-string both
    /// yield null, while a present empty string is preserved.
    /// </summary>
    private static string? GetMetadataString(
        IReadOnlyDictionary<string, JsonElement>? metadata,
        string key)
        => metadata is not null
            && metadata.TryGetValue(key, out var element)
            && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
}
