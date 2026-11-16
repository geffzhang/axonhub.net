using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.video_outbound.go: the video generation request/response
/// conversion and the IVideoTaskOutbound query/delete operations on the shared
/// OpenAI outbound transformer.
/// </summary>
public sealed partial class OpenAiOutboundTransformer
{
    /// <summary>Port of buildVideoGenerationAPIRequest (video_outbound.go:26).</summary>
    internal HttpRequestData BuildVideoGenerationApiRequest(LlmRequest llmReq)
    {
        if (llmReq.Video is null)
        {
            throw TransformerErrors.InvalidRequest("video request is required");
        }

        var video = llmReq.Video;

        if (string.IsNullOrWhiteSpace(llmReq.Model))
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        var prompt = FirstVideoText(video.Content);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw TransformerErrors.InvalidRequest("prompt is required");
        }

        llmReq.TransformerMetadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        llmReq.TransformerMetadata["video_prompt"] = JsonSerializer.SerializeToElement(prompt);

        var bodyMap = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = llmReq.Model,
            ["prompt"] = prompt,
        };

        if (FirstVideoImageURL(video.Content) is { Length: > 0 } reference)
        {
            bodyMap["input_reference"] = reference;
        }

        if (video.Duration is { } duration)
        {
            bodyMap["seconds"] = duration;
        }

        if (!string.IsNullOrEmpty(video.Size))
        {
            bodyMap["size"] = video.Size;
        }

        byte[] body;
        try
        {
            body = GoJson.Marshal(bodyMap);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to marshal video request: " + ex.Message, ex);
        }

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var req = new HttpRequestData
        {
            Method = "POST",
            Url = _config.BaseUrl + "/videos",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Accept"] = ["application/json"],
            },
            Body = body,
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.OpenAIVideo,
        };

        req.TransformerMetadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        req.TransformerMetadata["model"] = JsonSerializer.SerializeToElement(llmReq.Model);
        req.TransformerMetadata["video_prompt"] = JsonSerializer.SerializeToElement(prompt);

        return req;
    }

    /// <summary>Port of firstVideoText.</summary>
    internal static string FirstVideoText(List<VideoContent> content)
    {
        foreach (var c in content)
        {
            if (c.Type == "text" && !string.IsNullOrWhiteSpace(c.Text))
            {
                return c.Text;
            }
        }

        return string.Empty;
    }

    /// <summary>Port of firstVideoImageURL.</summary>
    internal static string FirstVideoImageURL(List<VideoContent> content)
    {
        foreach (var c in content)
        {
            if (c.Type == "image_url" && c.ImageURL is not null
                && !string.IsNullOrWhiteSpace(c.ImageURL.URL))
            {
                return c.ImageURL.URL;
            }
        }

        return string.Empty;
    }

    /// <summary>Port of transformVideoResponse (video_outbound.go:123).</summary>
    internal LlmResponse TransformVideoResponse(HttpResponseData httpResp)
    {
        if (httpResp is null)
        {
            throw TransformerErrors.InvalidResponse("http response is nil");
        }

        var syntaxError = GoJsonSyntax.Validate(httpResp.Body!);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal openai video response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(httpResp.Body!);
        if (rootKind is not null && rootKind != "null")
        {
            throw new InvalidOperationException(
                "failed to unmarshal openai video response: "
                + GoStructJson.TypeMismatch(rootKind, "openai.openAIVideoCreateResponse"));
        }

        OpenAIVideoCreateResponse createResp;
        try
        {
            createResp = LlmJson.Deserialize<OpenAIVideoCreateResponse>(httpResp.Body!)
                ?? new OpenAIVideoCreateResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal openai video response: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        if (createResp.ID.Length == 0)
        {
            throw TransformerErrors.InvalidResponse("missing id in openai video response");
        }

        if (createResp.Error is { } createErr && !string.IsNullOrWhiteSpace(createErr.Message))
        {
            throw new ResponseError
            {
                StatusCode = 400,
                Detail = new ErrorDetail
                {
                    Code = createErr.Code ?? string.Empty,
                    Message = createErr.Message,
                    Type = "video_generation_error",
                },
            };
        }

        var model = "video-generation";
        var prompt = string.Empty;

        if (httpResp.Request?.TransformerMetadata is { } metadata)
        {
            if (metadata.TryGetValue("model", out var m)
                && m.ValueKind == JsonValueKind.String
                && m.GetString() is { Length: > 0 } modelValue)
            {
                model = modelValue;
            }

            if (metadata.TryGetValue("video_prompt", out var p)
                && p.ValueKind == JsonValueKind.String
                && p.GetString() is { Length: > 0 } promptValue)
            {
                prompt = promptValue;
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var status = NormalizeVideoStatusOpenAI(createResp.Status);

        return new LlmResponse
        {
            Id = createResp.ID,
            Object = "video",
            Created = now,
            Model = model,
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.OpenAIVideo,
            Video = new VideoResponse
            {
                ID = createResp.ID,
                Status = status,
                Model = model,
                Prompt = VideoInboundTransformer.EmptyToNull(prompt),
                CreatedAt = now,
            },
            Choices = [],
        };
    }

    /// <summary>Port of normalizeVideoStatusOpenAI.</summary>
    internal static string NormalizeVideoStatusOpenAI(string status)
    {
        switch (status.Trim().ToLowerInvariant())
        {
            case "queued":
                return "queued";
            case "in_progress":
                return "running";
            case "completed":
                return "succeeded";
            case "failed":
                return "failed";
            default:
                return status.Length == 0 ? "queued" : status.ToLowerInvariant();
        }
    }

    /// <summary>Port of BuildGetVideoTaskRequest (video_outbound.go:203).</summary>
    public HttpRequestData BuildGetVideoTaskRequest(string providerTaskId)
    {
        if (string.IsNullOrWhiteSpace(providerTaskId))
        {
            throw TransformerErrors.InvalidRequest("providerTaskID is required");
        }

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "GET",
            Url = _config.BaseUrl + "/videos/" + providerTaskId,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Accept"] = ["application/json"],
            },
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.OpenAIVideo,
        };
    }

    /// <summary>Port of ParseGetVideoTaskResponse (video_outbound.go:249).</summary>
    public LlmResponse ParseGetVideoTaskResponse(HttpResponseData? httpResp)
    {
        if (httpResp is null)
        {
            throw TransformerErrors.InvalidResponse("http response is nil");
        }

        var syntaxError = GoJsonSyntax.Validate(httpResp.Body!);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal openai video get response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(httpResp.Body!);
        if (rootKind is not null && rootKind != "null")
        {
            throw new InvalidOperationException(
                "failed to unmarshal openai video get response: "
                + GoStructJson.TypeMismatch(rootKind, "openai.openAIVideoGetResponse"));
        }

        OpenAIVideoGetResponse resp;
        try
        {
            resp = LlmJson.Deserialize<OpenAIVideoGetResponse>(httpResp.Body!)
                ?? new OpenAIVideoGetResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal openai video get response: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        if (resp.ID.Length == 0)
        {
            throw TransformerErrors.InvalidResponse("missing id in openai video response");
        }

        var status = NormalizeVideoStatusOpenAI(resp.Status);

        var v = new VideoResponse
        {
            ID = resp.ID,
            Status = status,
            VideoURL = VideoInboundTransformer.EmptyToNull(resp.VideoURL),
            Progress = resp.Progress,
            Model = VideoInboundTransformer.EmptyToNull(resp.Model),
            Prompt = VideoInboundTransformer.EmptyToNull(resp.Prompt),
            Size = VideoInboundTransformer.EmptyToNull(resp.Size),
            CreatedAt = resp.CreatedAt,
            CompletedAt = resp.CompletedAt ?? 0,
            ExpiresAt = resp.ExpiresAt ?? 0,
        };

        if (!string.IsNullOrWhiteSpace(resp.Seconds))
        {
            v.Duration = resp.Seconds.Trim();
        }

        if (resp.Error is { } getErr && !string.IsNullOrWhiteSpace(getErr.Message))
        {
            v.Error = new VideoError
            {
                Code = getErr.Code ?? string.Empty,
                Message = getErr.Message,
            };
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

    /// <summary>Port of BuildDeleteVideoTaskRequest (video_outbound.go:316).</summary>
    public HttpRequestData BuildDeleteVideoTaskRequest(string providerTaskId)
    {
        if (string.IsNullOrWhiteSpace(providerTaskId))
        {
            throw TransformerErrors.InvalidRequest("providerTaskID is required");
        }

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "DELETE",
            Url = _config.BaseUrl + "/videos/" + providerTaskId,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Accept"] = ["application/json"],
            },
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.OpenAIVideo,
        };
    }

    /// <summary>Port of openAIVideoCreateResponse (video_outbound.go:16).</summary>
    private sealed class OpenAIVideoCreateResponse
    {
        [JsonPropertyName("id")]
        public string ID { get; set; } = string.Empty;

        [JsonPropertyName("object")]
        public string Object { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("error")]
        public OpenAIVideoCreateError? Error { get; set; }
    }

    private sealed class OpenAIVideoCreateError
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>Port of openAIVideoGetResponse (video_outbound.go:226).</summary>
    private sealed class OpenAIVideoGetResponse
    {
        [JsonPropertyName("id")]
        public string ID { get; set; } = string.Empty;

        [JsonPropertyName("object")]
        public string Object { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("seconds")]
        public string? Seconds { get; set; }

        [JsonPropertyName("size")]
        public string? Size { get; set; }

        [JsonPropertyName("progress")]
        public double? Progress { get; set; }

        [JsonPropertyName("video_url")]
        public string? VideoURL { get; set; }

        [JsonPropertyName("created_at")]
        public long CreatedAt { get; set; }

        [JsonPropertyName("completed_at")]
        public long? CompletedAt { get; set; }

        [JsonPropertyName("expires_at")]
        public long? ExpiresAt { get; set; }

        [JsonPropertyName("error")]
        public OpenAIVideoCreateError? Error { get; set; }

        [JsonPropertyName("usage")]
        public OpenAIVideoGetUsage? Usage { get; set; }
    }

    private sealed class OpenAIVideoGetUsage
    {
        [JsonPropertyName("completion_tokens")]
        public long CompletionTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public long TotalTokens { get; set; }
    }
}
