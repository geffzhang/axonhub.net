using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.ImageInboundTransformer (llm/transformer/openai/image_inbound.go):
/// accepts the OpenAI images endpoints (generations, edits, variations) and
/// produces the unified <see cref="ImageRequest"/>.
/// </summary>
/// <remarks>
/// The multipart reader approximates Go's <c>mime/multipart</c> semantics; see
/// difference C074 for the accepted deviations (all of them on malformed input).
/// </remarks>
public sealed class ImageInboundTransformer : IInboundTransformer
{
    /// <summary>Go: <c>defaultMaxImageFileSize</c> (50 MiB).</summary>
    internal const int DefaultMaxImageFileSize = 50 * 1024 * 1024;

    /// <summary>Go: <c>maxImageCount</c>.</summary>
    internal const int MaxImageCount = 16;

    private static readonly string[] AllowedImageTypes =
    [
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
    ];

    /// <summary>
    /// Go computes the body limit once at init from the initial file-size limit;
    /// tests mutate both package-level variables independently.
    /// </summary>
    private static readonly int InitialMaxImageFileSize = InitMaxImageFileSize();

    /// <summary>Go: <c>var maxImageFileSize</c>; mutable for test parity.</summary>
    internal static int MaxImageFileSize { get; set; } = InitialMaxImageFileSize;

    /// <summary>Go: <c>var maxImageBodySize</c>; mutable for test parity.</summary>
    internal static long MaxImageBodySize { get; set; } =
        ((long)InitialMaxImageFileSize * MaxImageCount) + (16L * 1024 * 1024);

    private readonly string _apiFormat;

    private ImageInboundTransformer(string apiFormat)
    {
        _apiFormat = apiFormat;
    }

    /// <summary>Port of NewImageGenerationInboundTransformer.</summary>
    public static ImageInboundTransformer Generation()
        => new(ApiFormats.OpenAIImageGeneration);

    /// <summary>Port of NewImageEditInboundTransformer.</summary>
    public static ImageInboundTransformer Edit() => new(ApiFormats.OpenAIImageEdit);

    /// <summary>Port of NewImageVariationInboundTransformer.</summary>
    public static ImageInboundTransformer Variation()
        => new(ApiFormats.OpenAIImageVariation);

    /// <summary>Port of <c>ImageInboundTransformer.APIFormat()</c>.</summary>
    public string ApiFormat => _apiFormat;

    /// <summary>Port of TransformRequest.</summary>
    public LlmRequest TransformRequest(HttpRequestData? request)
    {
        if (request is null)
        {
            throw TransformerErrors.InvalidRequest("http request is nil");
        }

        if (request.Body is null || request.Body.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("request body is empty");
        }

        return _apiFormat switch
        {
            ApiFormats.OpenAIImageGeneration => TransformGenerationRequest(request),
            ApiFormats.OpenAIImageEdit => TransformEditRequest(request),
            ApiFormats.OpenAIImageVariation => TransformVariationRequest(request),
            _ => throw TransformerErrors.InvalidRequest("unknown image api format: " + _apiFormat),
        };
    }

    /// <summary>Port of TransformResponse: unified image response to the client body.</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response is null || response.Image is null)
        {
            throw TransformerErrors.InvalidResponse("image response is nil");
        }

        var img = response.Image;

        // The reference computes a time.Now() fallback and then overwrites it
        // unconditionally with img.Created; only the latter is observable.
        var oaiResp = new ImagesResponse
        {
            Created = img.Created,
            Data = [],
            Background = NullIfEmpty(img.Background),
            OutputFormat = NullIfEmpty(img.OutputFormat),
            Quality = NullIfEmpty(img.Quality),
            Size = NullIfEmpty(img.Size),
        };

        if (response.Usage is { } usage)
        {
            oaiResp.Usage = new ImagesResponseUsage
            {
                InputTokens = usage.PromptTokens,
                OutputTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                Cost = usage.Cost,
            };

            if (usage.PromptTokensDetails is { } promptDetails)
            {
                oaiResp.Usage.InputTokensDetails = new ImagesResponseUsageInputTokensDetails
                {
                    ImageTokens = promptDetails.ImageTokens,
                    TextTokens = promptDetails.TextTokens,
                    CachedTokens = promptDetails.CachedTokens,
                };
            }

            if (usage.CompletionTokensDetails is { } completionDetails)
            {
                oaiResp.Usage.OutputTokensDetails = new ImagesResponseUsageOutputTokensDetails
                {
                    ReasoningTokens = completionDetails.ReasoningTokens,
                };
            }
        }

        foreach (var data in img.Data)
        {
            oaiResp.Data.Add(new ImagesResponseData
            {
                B64JSON = NullIfEmpty(data.B64JSON),
                URL = NullIfEmpty(data.URL),
                RevisedPrompt = NullIfEmpty(data.RevisedPrompt),
            });
        }

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = GoJson.Marshal(oaiResp),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Cache-Control"] = ["no-cache"],
            },
        };
    }

    /// <summary>Go: image endpoints never stream.</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => throw TransformerErrors.InvalidRequest("image request does not support streaming");

    /// <summary>Go: image endpoints never stream.</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        throw TransformerErrors.InvalidRequest("image request does not support streaming");
    }

    /// <summary>Port of TransformError: the images endpoints reuse the chat error envelope.</summary>
    public HttpResponseData TransformError(Exception? error)
        => new OpenAiInboundTransformer().TransformError(error);

    private LlmRequest TransformGenerationRequest(HttpRequestData request)
    {
        var contentType = HttpClientUtils.HeaderGet(request.Headers, "Content-Type").ToLowerInvariant();
        if (!contentType.Contains("application/json", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("generations requires application/json");
        }

        var genReq = DecodeGenerationRequest(request.Body!);

        if (genReq.Stream == true)
        {
            throw TransformerErrors.InvalidRequest("image generation does not support streaming");
        }

        var model = string.IsNullOrEmpty(genReq.Model) ? "dall-e-2" : genReq.Model;

        if (string.IsNullOrEmpty(genReq.Prompt))
        {
            throw TransformerErrors.InvalidRequest("prompt is required");
        }

        var images = ParseGenerationImageField(genReq.Image);

        var imageReq = new ImageRequest
        {
            ModelSpecified = !string.IsNullOrEmpty(genReq.Model),
            Prompt = genReq.Prompt,
            Images = images,
            N = genReq.N,
            Size = NullIfEmpty(genReq.Size),
            Quality = NullIfEmpty(genReq.Quality),
            ResponseFormat = NullIfEmpty(genReq.ResponseFormat),
            User = NullIfEmpty(genReq.User),
            Background = NullIfEmpty(genReq.Background),
            OutputFormat = NullIfEmpty(genReq.OutputFormat),
            OutputCompression = genReq.OutputCompression,
            Moderation = NullIfEmpty(genReq.Moderation),
            PartialImages = genReq.PartialImages,
            Style = NullIfEmpty(genReq.Style),
            SubjectReference = OptionalRaw(genReq.SubjectReference),
            AspectRatio = NullIfEmpty(genReq.AspectRatio),
            Width = genReq.Width,
            Height = genReq.Height,
            PromptOptimizer = genReq.PromptOptimizer,
        };

        return new LlmRequest
        {
            Model = model,
            Seed = genReq.Seed,
            Modalities = ["image"],
            Stream = false,
            RawRequest = request,
            RequestType = RequestTypes.Image,
            ApiFormat = _apiFormat,
            Image = imageReq,
        };
    }

    private LlmRequest TransformEditRequest(HttpRequestData request)
    {
        // Some providers (e.g. sensenova) accept image edits as application/json with
        // base64 data URLs instead of multipart/form-data.
        if (!GoMimeTypes.TryParseMediaType(
                HttpClientUtils.HeaderGet(request.Headers, "Content-Type"),
                out var mediaType,
                out _))
        {
            throw TransformerErrors.InvalidRequest("invalid content-type");
        }

        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return TransformEditJsonRequest(request);
        }

        var formData = ParseMultipartRequest(request);

        if (FieldIsTrue(formData.Fields, "stream"))
        {
            throw TransformerErrors.InvalidRequest("image edit does not support streaming");
        }

        var prompt = FieldTrimmed(formData.Fields, "prompt");
        if (prompt.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("prompt is required for image edits");
        }

        var model = FieldTrimmed(formData.Fields, "model");
        if (model.Length == 0)
        {
            model = "dall-e-2";
        }

        if (formData.Images.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("at least one image is required for edits");
        }

        // Build JSONBody for logging: replace binary image data with metadata descriptions.
        request.JsonBody = BuildMultipartJsonBody(formData.Fields, formData.Images, formData.Mask);

        var images = new List<byte[]>(formData.Images.Count);
        foreach (var img in formData.Images)
        {
            images.Add(img.Data);
        }

        var imageReq = new ImageRequest
        {
            Prompt = prompt,
            Images = images,
            Mask = formData.Mask?.Data,
            N = ParseOptionalInt64(formData.Fields, "n"),
            Size = NullIfEmpty(FieldTrimmed(formData.Fields, "size")),
            Quality = NullIfEmpty(FieldTrimmed(formData.Fields, "quality")),
            ResponseFormat = NullIfEmpty(FieldTrimmed(formData.Fields, "response_format")),
            User = NullIfEmpty(FieldTrimmed(formData.Fields, "user")),
            Background = NullIfEmpty(FieldTrimmed(formData.Fields, "background")),
            OutputFormat = NullIfEmpty(FieldTrimmed(formData.Fields, "output_format")),
            OutputCompression = ParseOptionalInt64(formData.Fields, "output_compression"),
            InputFidelity = NullIfEmpty(FieldTrimmed(formData.Fields, "input_fidelity")),
            PartialImages = ParseOptionalInt64(formData.Fields, "partial_images"),
        };

        return new LlmRequest
        {
            Model = model,
            Modalities = ["image"],
            Stream = false,
            RawRequest = request,
            RequestType = RequestTypes.Image,
            ApiFormat = _apiFormat,
            Image = imageReq,
        };
    }

    private LlmRequest TransformEditJsonRequest(HttpRequestData request)
    {
        // Mirror the multipart path's guard: ReadHTTPRequest reads the body without a
        // limit, and base64 payloads amplify memory further once decoded.
        if (request.Body!.Length > MaxImageBodySize)
        {
            throw TransformerErrors.InvalidRequest("request body too large");
        }

        var editReq = DecodeEditJsonRequest(request.Body);

        if (editReq.Stream == true)
        {
            throw TransformerErrors.InvalidRequest("image edit does not support streaming");
        }

        var prompt = (editReq.Prompt ?? string.Empty).Trim();
        if (prompt.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("prompt is required for image edits");
        }

        var model = (editReq.Model ?? string.Empty).Trim();
        if (model.Length == 0)
        {
            model = "dall-e-2";
        }

        var images = ParseEditImagesField(editReq.Image, editReq.Images);

        if (images is null || images.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("at least one image is required for edits");
        }

        if (images.Count > MaxImageCount)
        {
            throw TransformerErrors.InvalidRequest("too many images");
        }

        byte[]? mask = null;
        var maskDataUrl = (editReq.Mask ?? string.Empty).Trim();
        if (maskDataUrl.Length > 0)
        {
            mask = DecodeDataUrlToBytes(maskDataUrl);
        }

        var imageReq = new ImageRequest
        {
            Prompt = prompt,
            Images = images,
            Mask = mask,
            N = editReq.N,
            Size = NullIfEmpty((editReq.Size ?? string.Empty).Trim()),
            Quality = NullIfEmpty((editReq.Quality ?? string.Empty).Trim()),
            ResponseFormat = NullIfEmpty((editReq.ResponseFormat ?? string.Empty).Trim()),
            User = NullIfEmpty((editReq.User ?? string.Empty).Trim()),
            Background = NullIfEmpty((editReq.Background ?? string.Empty).Trim()),
            OutputFormat = NullIfEmpty((editReq.OutputFormat ?? string.Empty).Trim()),
            OutputCompression = editReq.OutputCompression,
            InputFidelity = NullIfEmpty((editReq.InputFidelity ?? string.Empty).Trim()),
            PartialImages = editReq.PartialImages,
        };

        return new LlmRequest
        {
            Model = model,
            Modalities = ["image"],
            Stream = false,
            RawRequest = request,
            RequestType = RequestTypes.Image,
            ApiFormat = _apiFormat,
            Image = imageReq,
        };
    }

    private LlmRequest TransformVariationRequest(HttpRequestData request)
    {
        var formData = ParseMultipartRequest(request);

        if (FieldIsTrue(formData.Fields, "stream"))
        {
            throw TransformerErrors.InvalidRequest("image variation does not support streaming");
        }

        if (FieldTrimmed(formData.Fields, "prompt").Length != 0)
        {
            throw TransformerErrors.InvalidRequest("prompt is not allowed for variations");
        }

        var model = FieldTrimmed(formData.Fields, "model");
        if (model.Length == 0)
        {
            model = "dall-e-2";
        }

        if (formData.Images.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("image is required for variations");
        }

        if (formData.Images.Count > 1)
        {
            throw TransformerErrors.InvalidRequest("variations supports a single image");
        }

        // Build JSONBody for logging: replace binary image data with metadata descriptions.
        request.JsonBody = BuildMultipartJsonBody(formData.Fields, formData.Images, mask: null);

        var imageReq = new ImageRequest
        {
            Images = [formData.Images[0].Data],
            N = ParseOptionalInt64(formData.Fields, "n"),
            Size = NullIfEmpty(FieldTrimmed(formData.Fields, "size")),
            ResponseFormat = NullIfEmpty(FieldTrimmed(formData.Fields, "response_format")),
            User = NullIfEmpty(FieldTrimmed(formData.Fields, "user")),
        };

        return new LlmRequest
        {
            Model = model,
            Modalities = ["image"],
            Stream = false,
            RawRequest = request,
            RequestType = RequestTypes.Image,
            ApiFormat = _apiFormat,
            Image = imageReq,
        };
    }

    private static ImageGenerationRequest DecodeGenerationRequest(ReadOnlySpan<byte> body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode generation request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind is not null)
        {
            // null leaves the zero struct in place; other roots are a type error.
            if (rootKind != "null")
            {
                throw TransformerErrors.InvalidRequest(
                    "failed to decode generation request: "
                    + GoStructJson.TypeMismatch(rootKind, "openai.ImageGenerationRequest"));
            }

            return new ImageGenerationRequest();
        }

        var typeError = OpenAiRequestTypeCheck.ValidateImageGeneration(body);
        if (typeError is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode generation request: " + typeError);
        }

        try
        {
            return LlmJson.Deserialize<ImageGenerationRequest>(body)
                ?? new ImageGenerationRequest();
        }
        catch (JsonException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode generation request: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }
    }

    private static ImageEditJsonRequest DecodeEditJsonRequest(ReadOnlySpan<byte> body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode image edit request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind is not null)
        {
            if (rootKind != "null")
            {
                throw TransformerErrors.InvalidRequest(
                    "failed to decode image edit request: "
                    + GoStructJson.TypeMismatch(rootKind, "openai.ImageEditJSONRequest"));
            }

            return new ImageEditJsonRequest();
        }

        var typeError = OpenAiRequestTypeCheck.ValidateImageEdit(body);
        if (typeError is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode image edit request: " + typeError);
        }

        try
        {
            return LlmJson.Deserialize<ImageEditJsonRequest>(body)
                ?? new ImageEditJsonRequest();
        }
        catch (JsonException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode image edit request: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }
    }

    private static ImageFormData ParseMultipartRequest(HttpRequestData request)
    {
        var body = request.Body!;
        if (body.Length > MaxImageBodySize)
        {
            throw TransformerErrors.InvalidRequest("request body too large");
        }

        if (!GoMimeTypes.TryParseMediaType(
                HttpClientUtils.HeaderGet(request.Headers, "Content-Type"),
                out var mediaType,
                out var parameters))
        {
            throw TransformerErrors.InvalidRequest("invalid content-type");
        }

        if (!mediaType.StartsWith("multipart/", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("expected multipart/form-data");
        }

        if (!parameters.TryGetValue("boundary", out var boundary) || boundary.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("missing boundary in content-type");
        }

        var reader = new GoMultipartReader(body, boundary);
        var formData = new ImageFormData();

        var imageCount = 0;

        while (true)
        {
            GoMultipartPart? part;
            try
            {
                part = reader.NextPart();
            }
            catch (GoMultipartFormatException)
            {
                throw TransformerErrors.InvalidRequest("failed to read multipart");
            }

            if (part is null)
            {
                break;
            }

            var fieldName = part.Name;
            var filename = part.FileName;

            if (filename.Length == 0)
            {
                if (part.Data.Length > MaxImageFileSize)
                {
                    throw TransformerErrors.InvalidRequest("multipart field too large");
                }

                formData.Fields[fieldName] = Encoding.UTF8.GetString(part.Data);

                continue;
            }

            imageCount++;
            if (imageCount > MaxImageCount)
            {
                throw TransformerErrors.InvalidRequest("too many images");
            }

            var contentType = (part.ContentType ?? string.Empty).Trim();

            if (part.Data.Length > MaxImageFileSize)
            {
                throw TransformerErrors.InvalidRequest("file too large");
            }

            if (contentType.Length == 0)
            {
                contentType = GoHttpSniff.DetectContentType(part.Data);
            }

            if (!IsAllowedImageType(contentType))
            {
                throw TransformerErrors.InvalidRequest("unsupported image type");
            }

            var file = new ParsedMultipartFile
            {
                ContentType = contentType,
                Data = part.Data,
            };

            switch (fieldName)
            {
                case "image":
                case "image[]":
                    formData.Images.Add(file);
                    break;
                case "mask":
                    formData.Mask = file;
                    break;
                default:
                    break;
            }
        }

        return formData;
    }

    /// <summary>
    /// Port of buildMultipartJSONBody: a logging-friendly JSON rendering of the
    /// multipart request, with image/mask bytes expanded to data URLs.
    /// </summary>
    private static byte[] BuildMultipartJsonBody(
        Dictionary<string, string> fields,
        List<ParsedMultipartFile> images,
        ParsedMultipartFile? mask)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in fields)
        {
            if (value.Length != 0)
            {
                body[key] = value;
            }
        }

        switch (images.Count)
        {
            case 1:
                body["image"] = MultipartFileToDataUrl(images[0]);
                break;
            case 0:
                break;
            default:
                var urls = new List<string>(images.Count);
                foreach (var img in images)
                {
                    urls.Add(MultipartFileToDataUrl(img));
                }

                body["image"] = urls;
                break;
        }

        if (mask is not null)
        {
            body["mask"] = MultipartFileToDataUrl(mask);
        }

        return GoJson.Marshal(body);
    }

    private static string MultipartFileToDataUrl(ParsedMultipartFile file)
        => Xurl.BuildDataUrl(file.ContentType, Convert.ToBase64String(file.Data), isBase64: true);

    private static bool IsAllowedImageType(string contentType)
    {
        foreach (var allowed in AllowedImageTypes)
        {
            if (string.Equals(contentType, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static long? ParseOptionalInt64(Dictionary<string, string> fields, string key)
    {
        var value = (fields.TryGetValue(key, out var raw) ? raw : string.Empty).Trim();
        if (value.Length == 0)
        {
            return null;
        }

        return long.TryParse(
            value,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Port of parseGenerationImageField: the "image" field is a single data URL
    /// string or an array of them. A present JSON null reaches the data-URL
    /// validator exactly like Go's zero string.
    /// </summary>
    private static List<byte[]>? ParseGenerationImageField(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (raw.ValueKind == JsonValueKind.String)
        {
            return [DecodeDataUrlToBytes(raw.GetString()!)];
        }

        if (raw.ValueKind == JsonValueKind.Null)
        {
            return [DecodeDataUrlToBytes(string.Empty)];
        }

        if (raw.ValueKind != JsonValueKind.Array)
        {
            throw TransformerErrors.InvalidRequest(
                "image field must be a string or array of strings");
        }

        var entries = raw.EnumerateArray().ToList();
        if (entries.Count > MaxImageCount)
        {
            throw TransformerErrors.InvalidRequest("too many images");
        }

        var images = new List<byte[]>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                images.Add(DecodeDataUrlToBytes(entry.GetString()!));
            }
            else if (entry.ValueKind == JsonValueKind.Null)
            {
                images.Add(DecodeDataUrlToBytes(string.Empty));
            }
            else
            {
                throw TransformerErrors.InvalidRequest(
                    "image field must be a string or array of strings");
            }
        }

        return images;
    }

    /// <summary>
    /// Port of parseEditImagesField. The legacy "image" field wins whenever it
    /// yields at least one image; "images" is consulted only when "image" is
    /// absent or empty, and a malformed "image" errors instead of falling back.
    /// </summary>
    private static List<byte[]>? ParseEditImagesField(JsonElement rawImage, JsonElement rawImages)
    {
        var images = ParseGenerationImageField(rawImage);
        if (images is { Count: > 0 })
        {
            return images;
        }

        if (rawImages.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        List<JsonElement> entries;
        if (rawImages.ValueKind == JsonValueKind.Array)
        {
            entries = rawImages.EnumerateArray().ToList();
        }
        else if (rawImages.ValueKind == JsonValueKind.Null)
        {
            // Go: unmarshalling null into []json.RawMessage yields a nil slice.
            entries = [];
        }
        else
        {
            throw TransformerErrors.InvalidRequest("images field must be an array");
        }

        if (entries.Count > MaxImageCount)
        {
            throw TransformerErrors.InvalidRequest("too many images");
        }

        var result = new List<byte[]>(entries.Count);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            string dataUrl;

            if (entry.ValueKind == JsonValueKind.String)
            {
                dataUrl = entry.GetString()!;
            }
            else if (entry.ValueKind == JsonValueKind.Null)
            {
                dataUrl = string.Empty;
            }
            else if (entry.ValueKind == JsonValueKind.Object
                && TryGetImageUrl(entry, out var imageUrl)
                && imageUrl.Length > 0)
            {
                dataUrl = imageUrl;
            }
            else
            {
                throw TransformerErrors.InvalidRequest(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"images[{i}] must be a data URL or an object with image_url"));
            }

            try
            {
                result.Add(DecodeDataUrlToBytes(dataUrl));
            }
            catch (TransformerException ex)
            {
                // Go wraps with fmt.Errorf("images[%d]: %w", ...), which keeps the
                // invalid-request sentinel and puts the index before the detail.
                throw new TransformerException(
                    TransformerErrorKind.InvalidRequest,
                    string.Create(CultureInfo.InvariantCulture, $"images[{i}]: {ex.Message}"));
            }
        }

        return result;
    }

    /// <summary>
    /// Go decodes the object into <c>struct { ImageURL string `json:"image_url"` }</c>:
    /// a non-string image_url fails the whole decode, while a missing/null one
    /// leaves the empty string.
    /// </summary>
    private static bool TryGetImageUrl(JsonElement entry, out string imageUrl)
    {
        imageUrl = string.Empty;

        foreach (var property in entry.EnumerateObject())
        {
            if (!string.Equals(property.Name, "image_url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                imageUrl = property.Value.GetString()!;
            }
            else if (property.Value.ValueKind == JsonValueKind.Null)
            {
                imageUrl = string.Empty;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Port of decodeDataURLToBytes (internal so the ported tests can call it).</summary>
    internal static byte[] DecodeDataUrlToBytes(string dataUrl)
    {
        if (!Xurl.IsDataUrl(dataUrl))
        {
            throw TransformerErrors.InvalidRequest("image must be a data URL");
        }

        var parsed = Xurl.ParseDataUrl(dataUrl);
        if (parsed is null)
        {
            throw TransformerErrors.InvalidRequest("invalid data URL format");
        }

        if (!parsed.IsBase64)
        {
            throw TransformerErrors.InvalidRequest("image data URL must be base64-encoded");
        }

        if (!IsAllowedImageType(parsed.MediaType))
        {
            throw TransformerErrors.InvalidRequest(
                "unsupported image content type " + GoQuote(parsed.MediaType));
        }

        if (parsed.Data.Length > EncodedLen(MaxImageFileSize))
        {
            throw TransformerErrors.InvalidRequest("image file too large");
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(parsed.Data);
        }
        catch (FormatException)
        {
            throw TransformerErrors.InvalidRequest("failed to decode base64 image data");
        }

        if (data.Length > MaxImageFileSize)
        {
            throw TransformerErrors.InvalidRequest("image file too large");
        }

        return data;
    }

    private static long EncodedLen(int length) => ((long)length + 2) / 3 * 4;

    /// <summary>
    /// Approximates Go's <c>%q</c> for the media type in the unsupported-type
    /// error: double quotes, backslash and control characters escaped.
    /// </summary>
    private static string GoQuote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\a':
                    builder.Append("\\a");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\v':
                    builder.Append("\\v");
                    break;
                default:
                    if (c < 0x20 || c == 0x7F)
                    {
                        builder.Append("\\x");
                        builder.Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static JsonElement? OptionalRaw(JsonElement element)
        => element.ValueKind == JsonValueKind.Undefined ? null : element;

    private static bool FieldIsTrue(Dictionary<string, string> fields, string key)
        => string.Equals(
            FieldValue(fields, key).Trim(),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static string FieldTrimmed(Dictionary<string, string> fields, string key)
        => FieldValue(fields, key).Trim();

    private static string FieldValue(Dictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value) ? value : string.Empty;

    private static int InitMaxImageFileSize()
    {
        var raw = Environment.GetEnvironmentVariable("AXONHUB_MAX_IMAGE_FILE_SIZE");
        if (!string.IsNullOrEmpty(raw)
            && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            && n > 0
            && n <= int.MaxValue)
        {
            return (int)n;
        }

        return DefaultMaxImageFileSize;
    }

    private sealed class ParsedMultipartFile
    {
        public string ContentType { get; init; } = string.Empty;

        public byte[] Data { get; init; } = [];
    }

    private sealed class ImageFormData
    {
        public List<ParsedMultipartFile> Images { get; } = [];

        public ParsedMultipartFile? Mask { get; set; }

        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
    }
}
