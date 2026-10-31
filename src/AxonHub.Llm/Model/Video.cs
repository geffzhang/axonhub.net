using System.Text.Json.Serialization;

namespace AxonHub.Llm;

/// <summary>
/// Port of llm.VideoRequest (llm/video.go): the unified video generation
/// sub-request (async task model), designed on Seedance's content[] structure
/// for extensibility. Common fields like Model live on the parent
/// <see cref="LlmRequest"/>.
/// </summary>
public sealed class VideoRequest
{
    /// <summary>The model ID.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>The input list (text prompt and reference media inputs).</summary>
    [JsonPropertyName("content")]
    public List<VideoContent> Content { get; set; } = [];

    /// <summary>
    /// The video duration in seconds as a string to preserve fractional values
    /// (e.g. "3.4") returned by some providers.
    /// </summary>
    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Duration { get; set; }

    /// <summary>The aspect ratio, e.g. "16:9", "9:16".</summary>
    [JsonPropertyName("ratio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ratio { get; set; }

    /// <summary>The resolution level, e.g. "480p", "720p", "1080p".</summary>
    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Resolution { get; set; }

    /// <summary>OpenAI-style resolution string, e.g. "1280x720", "720x1280".</summary>
    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; set; }

    /// <summary>The total frames (Seedance supports frames vs duration).</summary>
    [JsonPropertyName("frames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Frames { get; set; }

    /// <summary>The random seed.</summary>
    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Seed { get; set; }

    /// <summary>Whether to generate audio (Seedance 1.5 pro).</summary>
    [JsonPropertyName("generate_audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? GenerateAudio { get; set; }

    /// <summary>Whether to fix the camera.</summary>
    [JsonPropertyName("camera_fixed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CameraFixed { get; set; }

    /// <summary>Whether to add a watermark.</summary>
    [JsonPropertyName("watermark")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Watermark { get; set; }

    /// <summary>Preview mode (Seedance 1.5 pro).</summary>
    [JsonPropertyName("draft")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Draft { get; set; }

    /// <summary>The service tier, e.g. "default" or "flex".</summary>
    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }

    /// <summary>The task execution timeout in seconds.</summary>
    [JsonPropertyName("execution_expires_after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ExecutionExpiresAfter { get; set; }

    /// <summary>URL notified when the video task finishes.</summary>
    [JsonPropertyName("callback_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallbackURL { get; set; }

    /// <summary>Requests the generated video's last-frame URL.</summary>
    [JsonPropertyName("return_last_frame")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReturnLastFrame { get; set; }

    /// <summary>Provider-native video tools as opaque JSON.</summary>
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull | JsonIgnoreCondition.WhenWritingDefault)]
    public List<System.Text.Json.JsonElement>? Tools { get; set; }
}

/// <summary>Port of llm.VideoContent (llm/video.go): one input item.</summary>
public sealed class VideoContent
{
    /// <summary>One of "text", "image_url", "video_url" or "audio_url".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>The prompt (when <see cref="Type"/> == "text").</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>The image input (when <see cref="Type"/> == "image_url").</summary>
    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoImageURL? ImageURL { get; set; }

    /// <summary>The video input (when <see cref="Type"/> == "video_url").</summary>
    [JsonPropertyName("video_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoURL? VideoURL { get; set; }

    /// <summary>The audio input (when <see cref="Type"/> == "audio_url").</summary>
    [JsonPropertyName("audio_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AudioURL? AudioURL { get; set; }

    /// <summary>Describes how the reference media should be used.</summary>
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }
}

/// <summary>Port of llm.VideoImageURL (llm/video.go).</summary>
public sealed class VideoImageURL
{
    [JsonPropertyName("url")]
    public string URL { get; set; } = string.Empty;
}

/// <summary>Port of llm.VideoURL (llm/model.go:586).</summary>
public sealed class VideoURL
{
    /// <summary>The URL of the video.</summary>
    [JsonPropertyName("url")]
    public string URL { get; set; } = string.Empty;

    /// <summary>The MIME type of the video when provided by the source protocol.</summary>
    [JsonPropertyName("mime_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MIMEType { get; set; }
}

/// <summary>Port of llm.AudioURL (llm/video.go:84).</summary>
public sealed class AudioURL
{
    [JsonPropertyName("url")]
    public string URL { get; set; } = string.Empty;
}

/// <summary>
/// Port of llm.VideoResponse (llm/video.go): the unified video response model.
/// Common fields like Usage live on the parent <see cref="LlmResponse"/>.
/// </summary>
public sealed class VideoResponse
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = string.Empty;

    /// <summary>Unified status: "queued", "running", "succeeded" or "failed".</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("video_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VideoURL { get; set; }

    [JsonPropertyName("last_frame_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastFrameURL { get; set; }

    /// <summary>Opaque provider content payload.</summary>
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.JsonElement? Content { get; set; }

    /// <summary>Progress 0-100 (OpenAI-style).</summary>
    [JsonPropertyName("progress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Progress { get; set; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    /// <summary>A human-readable prompt for convenience.</summary>
    [JsonPropertyName("prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Prompt { get; set; }

    /// <summary>
    /// The video duration in seconds as a string to preserve fractional values
    /// (e.g. "3.4") returned by some providers.
    /// </summary>
    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Duration { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; set; }

    [JsonPropertyName("ratio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ratio { get; set; }

    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Resolution { get; set; }

    [JsonPropertyName("fps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FPS { get; set; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Seed { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoError? Error { get; set; }

    [JsonPropertyName("created_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long CreatedAt { get; set; }

    [JsonPropertyName("completed_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long CompletedAt { get; set; }

    [JsonPropertyName("expires_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ExpiresAt { get; set; }
}

/// <summary>Port of llm.VideoError (llm/video.go).</summary>
public sealed class VideoError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
