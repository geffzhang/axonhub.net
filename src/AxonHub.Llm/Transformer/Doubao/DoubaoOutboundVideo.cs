using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Doubao;

/// <summary>
/// Port of llm/transformer/doubao/video_outbound.go: the Seedance asynchronous
/// video task API (create, query, delete) and the create/get response maps.
/// </summary>
public sealed partial class DoubaoOutboundTransformer
{
    /// <summary>Port of buildVideoGenerationAPIRequest (video_outbound.go:22-136).</summary>
    private HttpRequestData BuildVideoGenerationApiRequest(LlmRequest? llmReq)
    {
        if (llmReq?.Video is null)
        {
            throw TransformerErrors.InvalidRequest("video request is required");
        }

        var video = llmReq.Video;

        if (GoStrings.TrimSpace(llmReq.Model).Length == 0)
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (video.Content.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("content is required");
        }

        // Seedance does not accept size; map common sizes to ratio+resolution if needed.
        var ratio = GoStrings.TrimSpace(video.Ratio ?? string.Empty);
        var resolution = GoStrings.TrimSpace(video.Resolution ?? string.Empty);
        if (ratio.Length == 0 && resolution.Length == 0
            && GoStrings.TrimSpace(video.Size ?? string.Empty).Length > 0)
        {
            if (!TryInferSeedanceRatioResolution(video.Size!, out ratio, out resolution))
            {
                throw TransformerErrors.InvalidRequest(
                    "size " + GoCast.GoQuote(video.Size!)
                    + " cannot be mapped to ratio/resolution, please set ratio and resolution");
            }
        }

        // Go marshals a map[string]any with sorted keys; GoSortedMapConverterFactory
        // reproduces that order for this Dictionary.
        var reqBody = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = llmReq.Model,
            ["content"] = video.Content,
        };

        if (video.Duration is { } durationText)
        {
            if (GoCast.TryParseFloat(GoStrings.TrimSpace(durationText), out var duration))
            {
                reqBody["duration"] = (long)Math.Round(duration, MidpointRounding.AwayFromZero);
            }
        }

        if (ratio.Length > 0)
        {
            reqBody["ratio"] = ratio;
        }

        if (resolution.Length > 0)
        {
            reqBody["resolution"] = resolution;
        }

        if (video.Frames is { } frames)
        {
            reqBody["frames"] = frames;
        }

        if (video.Seed is { } seed)
        {
            reqBody["seed"] = seed;
        }

        if (video.GenerateAudio is { } generateAudio)
        {
            reqBody["generate_audio"] = generateAudio;
        }

        if (video.CameraFixed is { } cameraFixed)
        {
            reqBody["camera_fixed"] = cameraFixed;
        }

        if (video.Watermark is { } watermark)
        {
            reqBody["watermark"] = watermark;
        }

        if (video.Draft is { } draft)
        {
            reqBody["draft"] = draft;
        }

        if (GoStrings.TrimSpace(video.ServiceTier ?? string.Empty).Length > 0)
        {
            // The reference assigns the untrimmed value.
            reqBody["service_tier"] = video.ServiceTier;
        }

        if (video.ExecutionExpiresAfter is { } executionExpiresAfter)
        {
            reqBody["execution_expires_after"] = executionExpiresAfter;
        }

        byte[] body;
        try
        {
            body = GoJson.Marshal(reqBody);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to marshal seedance video request: " + ex.Message,
                ex);
        }

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var req = new HttpRequestData
        {
            Method = "POST",
            Url = _baseUrl + "/contents/generations/tasks",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Accept"] = ["application/json"],
            },
            Body = body,
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.SeedanceVideo,
        };

        req.TransformerMetadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        req.TransformerMetadata["model"] = JsonSerializer.SerializeToElement(llmReq.Model);

        return req;
    }

    /// <summary>Port of inferSeedanceRatioResolution (video_outbound.go:138-160).</summary>
    internal static bool TryInferSeedanceRatioResolution(string size, out string ratio, out string resolution)
    {
        ratio = string.Empty;
        resolution = string.Empty;

        if (!TryParseVideoSize(size, out var width, out var height))
        {
            return false;
        }

        (ratio, resolution) = (width, height) switch
        {
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

    /// <summary>Port of parseSize (video_outbound.go:162-181).</summary>
    internal static bool TryParseVideoSize(string size, out int width, out int height)
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
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var w)
            || w <= 0)
        {
            return false;
        }

        if (!int.TryParse(
                GoStrings.TrimSpace(normalized[(cut + 1)..]),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var h)
            || h <= 0)
        {
            return false;
        }

        width = w;
        height = h;
        return true;
    }

    /// <summary>Port of BuildGetVideoTaskRequest (video_outbound.go:184-203).</summary>
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
            Url = _baseUrl + "/contents/generations/tasks/" + providerTaskId,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Accept"] = ["application/json"],
            },
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.SeedanceVideo,
        };
    }

    /// <summary>Port of ParseGetVideoTaskResponse (video_outbound.go:230-293).</summary>
    public LlmResponse ParseGetVideoTaskResponse(HttpResponseData? httpResp)
    {
        if (httpResp is null)
        {
            throw TransformerErrors.InvalidResponse("http response is nil");
        }

        var bodyBytes = httpResp.Body ?? [];
        var resp = DecodeSeedanceGetResponse(bodyBytes);

        if (GoStrings.TrimSpace(resp.ID).Length == 0)
        {
            throw TransformerErrors.InvalidResponse("missing id in seedance response");
        }

        var status = GoStrings.TrimSpace(resp.Status ?? string.Empty).ToLowerInvariant();
        if (status.Length == 0)
        {
            status = "queued";
        }

        long completedAt = 0;
        if (status is "succeeded" or "failed")
        {
            completedAt = resp.UpdatedAt != 0
                ? resp.UpdatedAt
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        var v = new VideoResponse
        {
            ID = resp.ID,
            Status = status,
            Model = EmptyToNull(resp.Model),
            CreatedAt = resp.CreatedAt,
            CompletedAt = completedAt,
            Ratio = EmptyToNull(resp.Ratio),
            Resolution = EmptyToNull(resp.Resolution),
            FPS = resp.FramesPerSecond,
            Seed = resp.Seed,
        };

        if (resp.Duration is { } duration)
        {
            v.Duration = duration.ToString(CultureInfo.InvariantCulture);
        }

        if (resp.Content is { } content)
        {
            v.VideoURL = EmptyToNull(content.VideoUrl);
        }

        var llmResp = new LlmResponse
        {
            RequestType = RequestTypes.Video,
            Video = v,
        };

        if (resp.Usage is { } usage)
        {
            llmResp.Usage = new Usage
            {
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
            };
        }

        return llmResp;
    }

    /// <summary>Port of BuildDeleteVideoTaskRequest (video_outbound.go:296-315).</summary>
    public HttpRequestData BuildDeleteVideoTaskRequest(string providerTaskId)
    {
        if (GoStrings.TrimSpace(providerTaskId).Length == 0)
        {
            throw TransformerErrors.InvalidRequest("providerTaskID is required");
        }

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "DELETE",
            Url = _baseUrl + "/contents/generations/tasks/" + providerTaskId,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Accept"] = ["application/json"],
            },
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.SeedanceVideo,
        };
    }

    /// <summary>Go's <c>omitempty</c> for strings.</summary>
    internal static string? EmptyToNull(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static SeedanceCreateResponse DecodeSeedanceCreateResponse(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal seedance create response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind == "null")
        {
            return new SeedanceCreateResponse();
        }

        if (rootKind is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal seedance create response: "
                + GoStructJson.TypeMismatch(rootKind, "doubao.seedanceCreateResponse"));
        }

        try
        {
            return LlmJson.Deserialize<SeedanceCreateResponse>(body)
                ?? new SeedanceCreateResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal seedance create response: "
                + Transformer.OpenAI.OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }

    private static SeedanceGetResponse DecodeSeedanceGetResponse(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal seedance video response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind == "null")
        {
            return new SeedanceGetResponse();
        }

        if (rootKind is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal seedance video response: "
                + GoStructJson.TypeMismatch(rootKind, "doubao.seedanceGetResponse"));
        }

        try
        {
            return LlmJson.Deserialize<SeedanceGetResponse>(body)
                ?? new SeedanceGetResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal seedance video response: "
                + Transformer.OpenAI.OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }
}

/// <summary>Port of doubao.seedanceCreateResponse (video_outbound.go:18-20).</summary>
internal sealed class SeedanceCreateResponse
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = string.Empty;
}

/// <summary>Port of doubao.seedanceGetResponse (video_outbound.go:205-227).</summary>
internal sealed class SeedanceGetResponse
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("content")]
    public SeedanceGetContent? Content { get; set; }

    [JsonPropertyName("usage")]
    public SeedanceGetUsage? Usage { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public long UpdatedAt { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("ratio")]
    public string? Ratio { get; set; }

    [JsonPropertyName("duration")]
    public long? Duration { get; set; }

    [JsonPropertyName("framespersecond")]
    public long? FramesPerSecond { get; set; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }
}

/// <summary>Anonymous content struct of doubao.seedanceGetResponse.</summary>
internal sealed class SeedanceGetContent
{
    [JsonPropertyName("video_url")]
    public string? VideoUrl { get; set; }
}

/// <summary>Anonymous usage struct of doubao.seedanceGetResponse.</summary>
internal sealed class SeedanceGetUsage
{
    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }
}
