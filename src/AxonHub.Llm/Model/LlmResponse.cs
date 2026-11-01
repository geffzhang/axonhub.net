using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm;

/// <summary>
/// Port of llm.Response (llm/model.go:682-780) for the chat subset.
/// Stream and non-stream responses share this shape, as in the reference.
/// </summary>
public sealed class LlmResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<Choice> Choices { get; set; } = [];

    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("previous_response_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousResponseId { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Usage? Usage { get; set; }

    [JsonPropertyName("system_fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemFingerprint { get; set; }

    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseError? Error { get; set; }

    /// <summary>Embedding sub-response; present when the response is an embedding response (llm/model.go:721).</summary>
    [JsonPropertyName("embedding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmbeddingResponse? Embedding { get; set; }

    [JsonPropertyName("request_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestType { get; set; }

    [JsonPropertyName("api_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiFormat { get; set; }

    [JsonPropertyName("transformer_metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? TransformerMetadata { get; set; }

    /// <summary>Compact sub-response; present when the request was a compact request (llm/model.go:736).</summary>
    [JsonPropertyName("compact")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompactResponse? Compact { get; set; }

    /// <summary>Completion sub-response; present when the request was a legacy completion request (llm/model.go:739).</summary>
    [JsonPropertyName("completion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompletionResponse? Completion { get; set; }

    /// <summary>Image sub-response; present when the response is an image response (llm/model.go:730).</summary>
    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageResponse? Image { get; set; }

    /// <summary>Moderation sub-response; present when the response is a moderation response (llm/model.go:758).</summary>
    [JsonPropertyName("moderation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModerationResponse? Moderation { get; set; }

    /// <summary>Alpha search sub-response; the raw opaque body (llm/model.go:761).</summary>
    [JsonPropertyName("alpha_search_response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlphaSearchResponse? AlphaSearch { get; set; }

    /// <summary>Decisions sub-response; present when the response is a decisions response (llm/model.go:764).</summary>
    [JsonPropertyName("decisions_response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DecisionsResponse? Decisions { get; set; }

    /// <summary>Speech sub-response; present when the response is a TTS response (llm/model.go:751).</summary>
    [JsonPropertyName("speech")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SpeechResponse? Speech { get; set; }

    /// <summary>Transcription sub-response; present for transcription/translation responses (llm/model.go:751).</summary>
    [JsonPropertyName("transcription")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TranscriptionResponse? Transcription { get; set; }

    /// <summary>Rerank sub-response; present when the response is a rerank response (llm/model.go:724).</summary>
    [JsonPropertyName("rerank")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RerankResponse? Rerank { get; set; }

    /// <summary>System One sub-response; present for a systemone response (llm/model.go:727).</summary>
    [JsonPropertyName("systemone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SystemOneResponse? SystemOne { get; set; }

    /// <summary>Video sub-response; present when the response is a video response (llm/model.go:733).</summary>
    [JsonPropertyName("video")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoResponse? Video { get; set; }

    /// <summary>SSE event from a streaming TTS provider (llm/model.go:748).</summary>
    [JsonPropertyName("speech_stream_event")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SpeechStreamEvent? SpeechStreamEvent { get; set; }

    /// <summary>Binary audio chunk from a chunked TTS provider; never serialized (llm/model.go:752).</summary>
    [JsonIgnore]
    public SpeechAudioChunk? SpeechAudioChunk { get; set; }

    /// <summary>SSE event from a streaming STT provider (llm/model.go:755).</summary>
    [JsonPropertyName("transcription_stream_event")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TranscriptionStreamEvent? TranscriptionStreamEvent { get; set; }

    [JsonIgnore]
    public StreamCompletionEvidence StreamCompletionEvidence { get; set; }

    /// <summary>
    /// Go: <c>llm.DoneResponse</c>. The reference compares this one shared
    /// pointer in several places (empty-response detection, metadata
    /// propagation), so the port keeps a single instance as well.
    /// </summary>
    public static LlmResponse DoneResponse { get; } = new() { Object = "[DONE]" };

    /// <summary>Go: <c>llm.DoneResponse</c>.</summary>
    public static LlmResponse Done() => DoneResponse;

    /// <summary>
    /// Go: <c>result := *response</c> — a shallow copy that shares nested
    /// objects with the original.
    /// </summary>
    public LlmResponse ShallowClone() => (LlmResponse)MemberwiseClone();
}

/// <summary>Port of llm.Choice.</summary>
public sealed class Choice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Message? Message { get; set; }

    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Message? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishReason { get; set; }

    [JsonPropertyName("logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LogprobsContent? Logprobs { get; set; }

    [JsonPropertyName("transformer_metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? TransformerMetadata { get; set; }

    /// <summary>Go struct copy semantics: <c>slices.Clone(src.Choices)</c>.</summary>
    public Choice ShallowClone() => (Choice)MemberwiseClone();
}

/// <summary>Port of llm.LogprobsContent.</summary>
public sealed class LogprobsContent
{
    [JsonPropertyName("content")]
    public List<TokenLogprob> Content { get; set; } = [];
}

/// <summary>Port of llm.TokenLogprob.</summary>
public sealed class TokenLogprob
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("logprob")]
    public double Logprob { get; set; }

    [JsonPropertyName("bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Bytes { get; set; }

    [JsonPropertyName("top_logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TopLogprob>? TopLogprobs { get; set; }
}

/// <summary>Port of llm.TopLogprob.</summary>
public sealed class TopLogprob
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("logprob")]
    public double Logprob { get; set; }

    [JsonPropertyName("bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Bytes { get; set; }
}

/// <summary>Port of llm.ResponseMeta.</summary>
public sealed class ResponseMeta
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("usage")]
    public Usage? Usage { get; set; }

    [JsonPropertyName("completed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Completed { get; set; }
}

/// <summary>
/// Port of llm.Usage. Detail objects are emitted even when null, matching the
/// reference's tags that omit the omitempty flag.
/// </summary>
public sealed class Usage
{
    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }

    [JsonPropertyName("prompt_tokens_details")]
    public PromptTokensDetails? PromptTokensDetails { get; set; }

    [JsonPropertyName("completion_tokens_details")]
    public CompletionTokensDetails? CompletionTokensDetails { get; set; }

    [JsonPropertyName("prompt_modality_token_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ModalityTokenCount>? PromptModalityTokenDetails { get; set; }

    [JsonPropertyName("completion_modality_token_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ModalityTokenCount>? CompletionModalityTokenDetails { get; set; }

    [JsonPropertyName("cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Cost { get; set; }
}

/// <summary>Port of llm.CompletionTokensDetails.</summary>
public sealed class CompletionTokensDetails
{
    [JsonPropertyName("audio_tokens")]
    public long AudioTokens { get; set; }

    [JsonPropertyName("reasoning_tokens")]
    public long ReasoningTokens { get; set; }

    [JsonPropertyName("accepted_prediction_tokens")]
    public long AcceptedPredictionTokens { get; set; }

    [JsonPropertyName("rejected_prediction_tokens")]
    public long RejectedPredictionTokens { get; set; }
}

/// <summary>Port of llm.PromptTokensDetails.</summary>
public sealed class PromptTokensDetails
{
    [JsonPropertyName("audio_tokens")]
    public long AudioTokens { get; set; }

    [JsonPropertyName("cached_tokens")]
    public long CachedTokens { get; set; }

    [JsonPropertyName("write_cached_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long WriteCachedTokens { get; set; }

    [JsonPropertyName("write_cached_5min_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long WriteCached5MinTokens { get; set; }

    [JsonPropertyName("write_cached_1hour_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long WriteCached1HourTokens { get; set; }

    [JsonPropertyName("image_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ImageTokens { get; set; }

    [JsonPropertyName("text_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TextTokens { get; set; }
}

/// <summary>Port of llm.ModalityTokenCount.</summary>
public sealed class ModalityTokenCount
{
    [JsonPropertyName("modality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Modality { get; set; }

    [JsonPropertyName("token_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TokenCount { get; set; }
}

/// <summary>
/// Port of llm.ResponseError: the detail is nested under "error", status code
/// and cause stay off the wire.
/// </summary>
[JsonConverter(typeof(ResponseErrorJsonConverter))]
public sealed class ResponseError : Exception
{
    [JsonIgnore]
    public int StatusCode { get; set; }

    [JsonPropertyName("error")]
    public ErrorDetail Detail { get; set; } = new();

    [JsonIgnore]
    public Exception? CauseException { get; set; }

    public ResponseError()
    {
    }

    public ResponseError(string message) : base(message)
    {
    }

    public ResponseError(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Port of ResponseError.Error().</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (StatusCode != 0)
        {
            sb.Append("Request failed: ").Append(Http.GoHttpStatus.Text(StatusCode)).Append(", ");
        }

        if (!string.IsNullOrEmpty(Detail.Message))
        {
            sb.Append("error: ").Append(Detail.Message);
        }

        if (!string.IsNullOrEmpty(Detail.Code))
        {
            sb.Append(", code: ").Append(Detail.Code);
        }

        if (!string.IsNullOrEmpty(Detail.Type))
        {
            sb.Append(", type: ").Append(Detail.Type);
        }

        if (!string.IsNullOrEmpty(Detail.RequestId))
        {
            sb.Append(", request_id: ").Append(Detail.RequestId);
        }

        return sb.ToString();
    }

}

/// <summary>
/// Serializes a <see cref="ResponseError"/> as <c>{"error": {...}}</c> only.
/// Without this converter, System.Text.Json would also emit the inherited
/// <see cref="Exception"/> surface (StackTrace, Source, ...), which the
/// reference never writes.
/// </summary>
public sealed class ResponseErrorJsonConverter : JsonConverter<ResponseError>
{
    public override ResponseError? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var error = new ResponseError();
        if (root.TryGetProperty("error", out var detail))
        {
            error.Detail = detail.Deserialize<ErrorDetail>(options) ?? new ErrorDetail();
        }

        return error;
    }

    public override void Write(Utf8JsonWriter writer, ResponseError value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("error");
        JsonSerializer.Serialize(writer, value.Detail, options);
        writer.WriteEndObject();
    }
}

/// <summary>Port of llm.ErrorDetail.</summary>
public sealed class ErrorDetail
{
    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("param")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Param { get; set; }

    [JsonPropertyName("request_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; set; }
}
