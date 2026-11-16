using System.Globalization;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Doubao;

/// <summary>
/// Port of doubao.VideoInboundTransformer (llm/transformer/doubao/video_inbound.go):
/// the Seedance-compatible client surface mounted under <c>/doubao/v3</c>.
/// </summary>
public sealed class DoubaoVideoInboundTransformer : IInboundTransformer
{
    /// <summary>Port of APIFormat.</summary>
    public const string ApiFormat = ApiFormats.SeedanceVideo;

    /// <summary>Port of TransformRequest (video_inbound.go:52-113).</summary>
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

        var contentType = HttpClientUtils.HeaderGet(request.Headers, "Content-Type") ?? string.Empty;
        if (contentType.Length == 0)
        {
            contentType = "application/json";
        }

        if (!contentType.ToLowerInvariant().Contains("application/json", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("unsupported content type: " + contentType);
        }

        var req = DecodeCreateRequest(request.Body);

        if (GoStrings.TrimSpace(req.Model).Length == 0)
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (req.Content is null || req.Content.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("content is required");
        }

        var videoReq = new VideoRequest
        {
            Model = req.Model,
            Content = req.Content,
            Ratio = EmptyToNull(req.Ratio),
            Resolution = EmptyToNull(req.Resolution),
            Frames = req.Frames,
            Seed = req.Seed,
            GenerateAudio = req.GenerateAudio,
            CameraFixed = req.CameraFixed,
            Watermark = req.Watermark,
            Draft = req.Draft,
            ServiceTier = EmptyToNull(req.ServiceTier),
            ExecutionExpiresAfter = req.ExecutionExpiresAfter,
        };

        if (req.Duration is { } duration)
        {
            videoReq.Duration = duration.ToString(CultureInfo.InvariantCulture);
        }

        return new LlmRequest
        {
            Model = req.Model,
            Messages = [],
            Modalities = ["video"],
            Stream = false,
            RawRequest = request,
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.SeedanceVideo,
            Video = videoReq,
        };
    }

    /// <summary>Port of TransformResponse (video_inbound.go:143-219).</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response?.Video is null)
        {
            throw TransformerErrors.InvalidResponse("video response is nil");
        }

        var v = response.Video;

        // For Seedance endpoints, create response returns only {id}.
        if (response.Object == "video.create")
        {
            byte[] createBody;
            try
            {
                createBody = GoJson.Marshal(new SeedanceCreateResponseInbound { ID = v.ID });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to marshal seedance create response: " + ex.Message,
                    ex);
            }

            return new HttpResponseData
            {
                StatusCode = 200,
                Body = createBody,
                Headers = SeedanceJsonHeaders(),
            };
        }

        var resp = new SeedanceGetResponseInbound
        {
            ID = v.ID,
            Model = EmptyToNull(v.Model),
            Status = EmptyToNull(v.Status),
            CreatedAt = v.CreatedAt,
            UpdatedAt = v.CompletedAt != 0 ? v.CompletedAt : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Seed = v.Seed,
            Resolution = EmptyToNull(v.Resolution),
            Ratio = EmptyToNull(v.Ratio),
            ServiceTier = null,
        };

        if (v.Duration is { } durationText
            && GoCast.TryParseFloat(GoStrings.TrimSpace(durationText), out var duration))
        {
            resp.Duration = (long)Math.Round(duration, MidpointRounding.AwayFromZero);
        }

        if (!string.IsNullOrEmpty(v.VideoURL))
        {
            resp.Content = new SeedanceInboundContent { VideoUrl = v.VideoURL };
        }

        if (response.Usage is { } usage)
        {
            resp.Usage = new SeedanceInboundUsage
            {
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
            };
        }

        if (v.FPS is { } fps)
        {
            resp.FramesPerSecond = fps;
        }

        byte[] body;
        try
        {
            body = GoJson.Marshal(resp);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to marshal seedance get response: " + ex.Message,
                ex);
        }

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = body,
            Headers = SeedanceJsonHeaders(),
        };
    }

    /// <summary>Port of TransformStream: video has no streaming form.</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => throw TransformerErrors.InvalidRequest("video request does not support streaming");

    /// <summary>Port of AggregateStreamChunks: video has no streaming form.</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        throw TransformerErrors.InvalidRequest("video request does not support streaming");
    }

    /// <summary>Port of TransformError: reuses the OpenAI error format for now.</summary>
    public HttpResponseData TransformError(Exception? error)
        => new OpenAiInboundTransformer().TransformError(error);

    /// <summary>Go's <c>omitempty</c> for strings.</summary>
    internal static string? EmptyToNull(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static Dictionary<string, List<string>> SeedanceJsonHeaders() => new(StringComparer.Ordinal)
    {
        ["Content-Type"] = ["application/json"],
        ["Cache-Control"] = ["no-cache"],
    };

    /// <summary>Port of the <c>json.Unmarshal(httpReq.Body, &amp;req)</c> block.</summary>
    private static SeedanceCreateRequest DecodeCreateRequest(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode seedance video request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind == "null")
        {
            return new SeedanceCreateRequest();
        }

        if (rootKind is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode seedance video request: "
                + GoStructJson.TypeMismatch(rootKind, "doubao.seedanceCreateRequest"));
        }

        try
        {
            return LlmJson.Deserialize<SeedanceCreateRequest>(body) ?? new SeedanceCreateRequest();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode seedance video request: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }
}

/// <summary>Port of doubao.seedanceCreateRequest (video_inbound.go:34-50).</summary>
internal sealed class SeedanceCreateRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public List<VideoContent>? Content { get; set; }

    [JsonPropertyName("duration")]
    public long? Duration { get; set; }

    [JsonPropertyName("ratio")]
    public string? Ratio { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("frames")]
    public long? Frames { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    [JsonPropertyName("generate_audio")]
    public bool? GenerateAudio { get; set; }

    [JsonPropertyName("camera_fixed")]
    public bool? CameraFixed { get; set; }

    [JsonPropertyName("watermark")]
    public bool? Watermark { get; set; }

    [JsonPropertyName("draft")]
    public bool? Draft { get; set; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("execution_expires_after")]
    public long? ExecutionExpiresAfter { get; set; }
}

/// <summary>Port of doubao.seedanceCreateResponseInbound (video_inbound.go:115-117).</summary>
internal sealed class SeedanceCreateResponseInbound
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = string.Empty;
}

/// <summary>Port of doubao.seedanceGetResponseInbound (video_inbound.go:119-141).</summary>
internal sealed class SeedanceGetResponseInbound
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SeedanceInboundContent? Content { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SeedanceInboundUsage? Usage { get; set; }

    [JsonPropertyName("created_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long UpdatedAt { get; set; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Seed { get; set; }

    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Resolution { get; set; }

    [JsonPropertyName("ratio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ratio { get; set; }

    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Duration { get; set; }

    [JsonPropertyName("framespersecond")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FramesPerSecond { get; set; }

    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }
}

/// <summary>Anonymous content struct of doubao.seedanceGetResponseInbound.</summary>
internal sealed class SeedanceInboundContent
{
    [JsonPropertyName("video_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VideoUrl { get; set; }
}

/// <summary>Anonymous usage struct of doubao.seedanceGetResponseInbound.</summary>
internal sealed class SeedanceInboundUsage
{
    [JsonPropertyName("completion_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TotalTokens { get; set; }
}
