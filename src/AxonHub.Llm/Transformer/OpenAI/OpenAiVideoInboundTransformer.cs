using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.VideoInboundTransformer (llm/transformer/openai/video_inbound.go):
/// the POST /v1/videos client surface. Accepts JSON or multipart bodies, maps
/// the OpenAI video request into the unified <see cref="VideoRequest"/> and
/// renders unified video responses as <see cref="OpenAIVideoObject"/>.
/// </summary>
public sealed class VideoInboundTransformer : IInboundTransformer
{
    /// <summary>Port of APIFormat.</summary>
    public const string ApiFormat = ApiFormats.OpenAIVideo;

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

        var contentType = HttpClientUtils.HeaderGet(request.Headers, "Content-Type") ?? string.Empty;
        if (contentType.Length == 0)
        {
            contentType = "application/json";
        }

        VideoCreateRequest req;
        if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            req = OpenAiVideoExtensions.ParseVideoJsonRequest(request.Body);
        }
        else if (contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            req = OpenAiVideoInboundMultipart.ParseVideoMultipartRequest(request);
        }
        else
        {
            throw TransformerErrors.InvalidRequest("unsupported content type: " + contentType);
        }

        if (string.IsNullOrWhiteSpace(req.Model))
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if ((req.Content is null || req.Content.Count == 0) && string.IsNullOrWhiteSpace(req.Prompt))
        {
            throw TransformerErrors.InvalidRequest("prompt is required");
        }

        var content = req.Content;
        if (content is null || content.Count == 0)
        {
            content =
            [
                new VideoContent { Type = "text", Text = req.Prompt },
            ];
            if (!string.IsNullOrWhiteSpace(req.InputReference))
            {
                content.Add(new VideoContent
                {
                    Type = "image_url",
                    ImageURL = new VideoImageURL { URL = req.InputReference },
                    Role = "first_frame",
                });
            }
        }

        return new LlmRequest
        {
            Model = req.Model,
            Messages = [],
            Modalities = ["video"],
            Stream = false,
            RawRequest = request,
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.OpenAIVideo,
            Video = new VideoRequest
            {
                Model = req.Model,
                Content = content,
                Duration = req.Seconds,
                Size = EmptyToNull(req.Size),
                Ratio = EmptyToNull(req.Ratio),
                Resolution = EmptyToNull(req.Resolution),
                ServiceTier = EmptyToNull(req.ServiceTier),
                ExecutionExpiresAfter = req.ExecutionExpiresAfter,
                CallbackURL = EmptyToNull(req.CallbackURL),
                ReturnLastFrame = req.ReturnLastFrame,
                Tools = req.Tools,
            },
            ExtraBody = req.ExtraBody,
        };
    }

    /// <summary>Port of TransformResponse.</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response?.Video is null)
        {
            throw TransformerErrors.InvalidResponse("video response is nil");
        }

        var v = response.Video;

        var status = v.Status;
        switch (status)
        {
            case "running":
                status = "in_progress";
                break;
            case "succeeded":
                status = "completed";
                break;
        }

        var createdAt = v.CreatedAt;
        if (createdAt == 0)
        {
            createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        var oai = new OpenAIVideoObject
        {
            ID = v.ID,
            Object = "video",
            Status = status,
            Model = EmptyToNull(v.Model),
            Prompt = EmptyToNull(v.Prompt),
            Seconds = v.Duration,
            Size = EmptyToNull(v.Size),
            Progress = v.Progress,
            VideoURL = EmptyToNull(v.VideoURL),
            LastFrameURL = EmptyToNull(v.LastFrameURL),
            Content = v.Content,
            CreatedAt = createdAt,
            CompletedAt = v.CompletedAt != 0 ? v.CompletedAt : null,
            ExpiresAt = v.ExpiresAt != 0 ? v.ExpiresAt : null,
        };

        if (v.Error is { } videoErr)
        {
            oai.Error = new OpenAIVideoError
            {
                Code = EmptyToNull(videoErr.Code),
                Message = EmptyToNull(videoErr.Message),
            };
        }

        if (response.Usage is { } usage)
        {
            oai.Usage = new OpenAIVideoUsage
            {
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                Cost = usage.Cost,
            };
        }

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = GoJson.Marshal(oai),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Cache-Control"] = ["no-cache"],
            },
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

    /// <summary>Port of TransformError: delegates to the chat inbound.</summary>
    public HttpResponseData TransformError(Exception? error)
        => new OpenAiInboundTransformer().TransformError(error);

    internal static string? EmptyToNull(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
