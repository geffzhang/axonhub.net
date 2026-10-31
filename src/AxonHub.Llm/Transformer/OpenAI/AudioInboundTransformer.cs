using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.AudioInboundTransformer (audio_inbound.go:31-366): the OpenAI
/// audio APIs (TTS speech, STT transcription/translation) turned into the
/// unified request/response model.
/// </summary>
public sealed class AudioInboundTransformer : IInboundTransformer
{
    private readonly string _apiFormat;

    private AudioInboundTransformer(string apiFormat)
    {
        _apiFormat = apiFormat;
    }

    /// <summary>Port of NewSpeechInboundTransformer (POST /v1/audio/speech).</summary>
    public static AudioInboundTransformer Speech() => new(ApiFormats.OpenAISpeech);

    /// <summary>Port of NewTranscriptionInboundTransformer (POST /v1/audio/transcriptions).</summary>
    public static AudioInboundTransformer Transcription() => new(ApiFormats.OpenAITranscription);

    /// <summary>Port of NewTranslationInboundTransformer (POST /v1/audio/translations).</summary>
    public static AudioInboundTransformer Translation() => new(ApiFormats.OpenAITranslation);

    /// <summary>Port of AudioInboundTransformer.APIFormat().</summary>
    public string ApiFormat => _apiFormat;

    /// <summary>Port of TransformRequest (audio_inbound.go:57-77).</summary>
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

        switch (_apiFormat)
        {
            case ApiFormats.OpenAISpeech:
                return TransformSpeechRequest(request);
            case ApiFormats.OpenAITranscription:
                return TransformTranscriptionRequest(request);
            case ApiFormats.OpenAITranslation:
                return TransformTranslationRequest(request);
            default:
                throw TransformerErrors.InvalidRequest("unknown audio api format: " + _apiFormat);
        }
    }

    /// <summary>Port of transformSpeechRequest (audio_inbound.go:79-128).</summary>
    private LlmRequest TransformSpeechRequest(HttpRequestData httpReq)
    {
        var contentType = HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type").ToLowerInvariant();
        if (contentType.Length > 0 && !contentType.Contains("application/json", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("speech requires application/json");
        }

        // Go validates the whole document before decoding, so syntax errors are
        // always reported with encoding/json's own wording.
        var syntaxError = GoJsonSyntax.Validate(httpReq.Body!);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode speech request: " + syntaxError);
        }

        SpeechRequestBody body;
        var rootKind = GoStructJson.RootKind(httpReq.Body!);
        if (rootKind is not null)
        {
            if (rootKind != "null")
            {
                throw TransformerErrors.InvalidRequest(
                    "failed to decode speech request: " + GoStructJson.TypeMismatch(rootKind, "openai.SpeechRequestBody"));
            }

            // null leaves the zero struct in place; required-field checks run below.
            body = new SpeechRequestBody();
        }
        else
        {
            // Go's decoder reports field-level type errors with encoding/json's
            // own wording (C003 walker).
            var typeError = OpenAiRequestTypeCheck.ValidateSpeech(httpReq.Body!, "SpeechRequestBody");
            if (typeError is not null)
            {
                throw TransformerErrors.InvalidRequest("failed to decode speech request: " + typeError);
            }

            try
            {
                body = LlmJson.Deserialize<SpeechRequestBody>(httpReq.Body!)
                    ?? new SpeechRequestBody();
            }
            catch (JsonException ex)
            {
                throw TransformerErrors.InvalidRequest(
                    "failed to decode speech request: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
            }
        }

        if (body.Model.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (body.Input.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("input is required");
        }

        if (body.Voice.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("voice is required");
        }

        // stream_format is opt-in; only explicit "sse" or "audio" engages the
        // streaming pipeline. The error message quotes the ORIGINAL value, like
        // the reference's %q of body.StreamFormat.
        var streamFormat = (body.StreamFormat ?? string.Empty).Trim().ToLowerInvariant();
        if (streamFormat.Length > 0 && streamFormat != "sse" && streamFormat != "audio")
        {
            throw TransformerErrors.InvalidRequest(
                "unsupported stream_format: " + GoCast.GoQuote(body.StreamFormat ?? string.Empty)
                + " (only \"sse\" and \"audio\" are supported)");
        }

        var isStream = streamFormat.Length > 0;

        return new LlmRequest
        {
            Model = body.Model,
            Stream = isStream,
            RawRequest = httpReq,
            RequestType = RequestTypes.Speech,
            ApiFormat = _apiFormat,
            Speech = new SpeechRequest
            {
                Input = body.Input,
                Voice = body.Voice,
                // Go's omitempty drops empty strings on any later marshal;
                // the port models them as null for the same wire shape.
                ResponseFormat = EmptyToNull(body.ResponseFormat),
                Speed = body.Speed,
                Instructions = EmptyToNull(body.Instructions),
                StreamFormat = EmptyToNull(streamFormat),
            },
        };
    }

    /// <summary>Port of transformTranscriptionRequest (audio_inbound.go:130-177).</summary>
    private LlmRequest TransformTranscriptionRequest(HttpRequestData httpReq)
    {
        var form = OpenAiAudioModels.ParseAudioMultipartRequest(httpReq);

        var model = form.First("model").Trim();
        if (model.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (form.File is null || form.File.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("file is required for transcription");
        }

        var temperature = OpenAiAudioModels.ParseOptionalFloat64("temperature", form.First("temperature"));
        var isStream = OpenAiAudioModels.ParseStreamField(form.First("stream"));

        var extra = form.ExtraFields();
        // stream is consumed by the gateway; do not forward it twice via Extra.
        extra?.Remove("stream");

        httpReq.JsonBody = OpenAiAudioModels.BuildAudioJSONBody(form);

        return new LlmRequest
        {
            Model = model,
            Stream = isStream,
            RawRequest = httpReq,
            RequestType = RequestTypes.Transcription,
            ApiFormat = _apiFormat,
            Transcription = new TranscriptionRequest
            {
                File = form.File,
                FileName = form.FileName,
                Language = EmptyToNull(form.First("language").Trim()),
                Prompt = EmptyToNull(form.First("prompt").Trim()),
                ResponseFormat = EmptyToNull(form.First("response_format").Trim()),
                Temperature = temperature,
                Extra = extra,
            },
        };
    }

    /// <summary>Port of transformTranslationRequest (audio_inbound.go:179-224).</summary>
    private LlmRequest TransformTranslationRequest(HttpRequestData httpReq)
    {
        var form = OpenAiAudioModels.ParseAudioMultipartRequest(httpReq);

        var model = form.First("model").Trim();
        if (model.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (form.File is null || form.File.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("file is required for translation");
        }

        var temperature = OpenAiAudioModels.ParseOptionalFloat64("temperature", form.First("temperature"));
        var isStream = OpenAiAudioModels.ParseStreamField(form.First("stream"));

        var extra = form.ExtraFields();
        extra?.Remove("stream");

        httpReq.JsonBody = OpenAiAudioModels.BuildAudioJSONBody(form);

        return new LlmRequest
        {
            Model = model,
            Stream = isStream,
            RawRequest = httpReq,
            RequestType = RequestTypes.Translation,
            ApiFormat = _apiFormat,
            Translation = new TranslationRequest
            {
                File = form.File,
                FileName = form.FileName,
                Prompt = EmptyToNull(form.First("prompt").Trim()),
                ResponseFormat = EmptyToNull(form.First("response_format").Trim()),
                Temperature = temperature,
                Extra = extra,
            },
        };
    }

    /// <summary>Port of TransformResponse (audio_inbound.go:226-297).</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response is null)
        {
            throw TransformerErrors.InvalidResponse("audio response is nil");
        }

        if (_apiFormat == ApiFormats.OpenAISpeech)
        {
            if (response.Speech is null)
            {
                throw TransformerErrors.InvalidResponse("speech response is nil");
            }

            var contentType = response.Speech.ContentType;
            if (contentType.Length == 0)
            {
                contentType = "audio/mpeg";
            }

            return new HttpResponseData
            {
                StatusCode = 200,
                Body = response.Speech.Audio,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = [contentType],
                },
            };
        }

        // Transcription / translation response.
        if (response.Transcription is null)
        {
            throw TransformerErrors.InvalidResponse("transcription response is nil");
        }

        var tr = response.Transcription;

        // When the provider raw body is available, pass it through untouched.
        if (tr.Raw.Length > 0)
        {
            var rawContentType = tr.RawContentType;
            if (rawContentType.Length == 0)
            {
                rawContentType = "application/json";
            }

            return new HttpResponseData
            {
                StatusCode = 200,
                Body = tr.Raw,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = [rawContentType],
                },
            };
        }

        // audio_inbound.go:275-279: the anonymous {text, language, duration}
        // struct — text is always emitted, language/duration follow omitempty.
        var body = GoJson.Marshal(new TranscriptionResponseWire
        {
            Text = tr.Text ?? string.Empty,
            Language = EmptyToNull(tr.Language),
            Duration = tr.Duration,
        });

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = body,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
        };
    }

    /// <summary>
    /// Port of TransformStream (audio_inbound.go:302-351): unified audio stream
    /// back to client-facing events. TTS supports both SSE and raw binary chunk
    /// streams; STT/translation use SSE only.
    /// </summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => new MapStream<LlmResponse, StreamEvent>(stream, TransformStreamChunk);

    /// <summary>Port of the per-response mapping in TransformStream.</summary>
    public StreamEvent? TransformStreamChunk(LlmResponse? resp)
    {
        if (resp is null)
        {
            return null;
        }

        // The DoneResponse sentinel is the pipeline-level end marker. SSE audio
        // streams have their own "*.done" events; binary speech streams need a
        // client-facing binary.done so persistence can mark the stream complete.
        if (ReferenceEquals(resp, LlmResponse.DoneResponse) || resp.Object == "[DONE]")
        {
            if (_apiFormat == ApiFormats.OpenAISpeech && resp.RequestType == RequestTypes.Speech)
            {
                return new StreamEvent { Type = AuthTypes.BinaryStreamDoneEventType };
            }

            // Skip the pipeline sentinel.
            return null;
        }

        if (resp.SpeechStreamEvent is not null)
        {
            byte[] data;
            try
            {
                data = GoJson.Marshal(resp.SpeechStreamEvent);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("failed to marshal speech stream event: " + ex.Message, ex);
            }

            // Propagate the event type so the persistence layer can recognize
            // speech.audio.done and mark the request as completed.
            return new StreamEvent { Type = resp.SpeechStreamEvent.Type, Data = data };
        }

        if (resp.SpeechAudioChunk is not null)
        {
            return new StreamEvent
            {
                Type = resp.SpeechAudioChunk.ContentType,
                Data = resp.SpeechAudioChunk.Audio,
            };
        }

        if (resp.TranscriptionStreamEvent is not null)
        {
            byte[] data;
            try
            {
                data = GoJson.Marshal(resp.TranscriptionStreamEvent);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("failed to marshal transcription stream event: " + ex.Message, ex);
            }

            return new StreamEvent { Type = resp.TranscriptionStreamEvent.Type, Data = data };
        }

        // Unrelated event, skip.
        return null;
    }

    /// <summary>Port of TransformError: delegates to the chat inbound transformer.</summary>
    public HttpResponseData TransformError(Exception? error)
        => new OpenAiInboundTransformer().TransformError(error);

    /// <summary>
    /// Port of AggregateStreamChunks (audio_inbound.go:362-368): the streamed SSE
    /// events reassembled into a single JSON body for persistence/logging.
    /// </summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
        => _apiFormat == ApiFormats.OpenAISpeech
            ? OpenAiOutboundTransformer.AggregateSpeechStreamChunks(chunks, out meta)
            : OpenAiOutboundTransformer.AggregateTranscriptionStreamChunks(chunks, out meta);

    /// <summary>
    /// Go's omitempty drops empty strings on any later marshal; the port's
    /// nullable fields must be null to reproduce the same wire shape.
    /// </summary>
    private static string? EmptyToNull(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Port of the anonymous marshal struct at audio_inbound.go:275-279:
    /// text is always emitted, language/duration follow omitempty.
    /// </summary>
    private sealed class TranscriptionResponseWire
    {
        [System.Text.Json.Serialization.JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("language")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Language { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("duration")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? Duration { get; set; }
    }
}
