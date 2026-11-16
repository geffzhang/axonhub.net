using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm;

/// <summary>
/// Port of llm/audio.go: unified audio (TTS/STT) request and response models.
/// The wire struct used for the OpenAI /audio/speech body lives in
/// <see cref="Transformer.OpenAI.SpeechRequestBody"/> (audio_outbound.go), not here.
/// </summary>
public static class AudioModels
{
    /// <summary>Synthetic response ID used for aggregated streaming TTS metadata.</summary>
    public const string SpeechStreamResponseId = "audio.speech.stream";
}

/// <summary>Port of llm.SpeechRequest (llm/audio.go:11-30).</summary>
public sealed class SpeechRequest
{
    [JsonPropertyName("input")]
    public string Input { get; set; } = string.Empty;

    [JsonPropertyName("voice")]
    public string Voice { get; set; } = string.Empty;

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("speed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Speed { get; set; }

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    [JsonPropertyName("stream_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StreamFormat { get; set; }
}

/// <summary>Port of llm.SpeechStreamEvent (llm/audio.go:34-43). Type is always emitted.</summary>
public sealed class SpeechStreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AudioBase64 { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Usage? Usage { get; set; }
}

/// <summary>
/// Port of llm.SpeechAudioChunk (llm/audio.go:47-53): one binary audio chunk from
/// a chunked TTS stream. Never JSON serialized.
/// </summary>
public sealed class SpeechAudioChunk
{
    [JsonIgnore]
    public byte[] Audio { get; set; } = [];

    [JsonIgnore]
    public string ContentType { get; set; } = string.Empty;
}

/// <summary>Port of llm.SpeechResponse (llm/audio.go:57-63): raw audio, never JSON serialized.</summary>
public sealed class SpeechResponse
{
    [JsonIgnore]
    public byte[] Audio { get; set; } = [];

    [JsonIgnore]
    public string ContentType { get; set; } = string.Empty;
}

/// <summary>Port of llm.TranscriptionRequest (llm/audio.go:68-92).</summary>
public sealed class TranscriptionRequest
{
    [JsonIgnore]
    public byte[] File { get; set; } = [];

    [JsonIgnore]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Prompt { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    /// <summary>Unmodeled multipart fields, forwarded with multiplicity preserved.</summary>
    [JsonPropertyName("extra")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Extra { get; set; }
}

/// <summary>Port of llm.TranscriptionStreamEvent (llm/audio.go:96-111).</summary>
public sealed class TranscriptionStreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Delta { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>
    /// Go: <c>json.RawMessage</c>, compacted on marshal. The port compacts at
    /// decode time (GoJson.CompactElement) instead, which is unobservable on
    /// the wire.
    /// </summary>
    [JsonPropertyName("logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Logprobs { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Usage? Usage { get; set; }
}

/// <summary>Port of llm.TranslationRequest (llm/audio.go:115-136): TranscriptionRequest without Language.</summary>
public sealed class TranslationRequest
{
    [JsonIgnore]
    public byte[] File { get; set; } = [];

    [JsonIgnore]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Prompt { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("extra")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Extra { get; set; }
}

/// <summary>Port of llm.TranscriptionResponse (llm/audio.go:140-156).</summary>
public sealed class TranscriptionResponse
{
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }

    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Duration { get; set; }

    /// <summary>Raw provider body for lossless passthrough (segments/words/task, or srt/vtt text).</summary>
    [JsonIgnore]
    public byte[] Raw { get; set; } = [];

    [JsonIgnore]
    public string RawContentType { get; set; } = string.Empty;
}
