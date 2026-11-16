using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of the audio half of llm/transformer/openai/audio_outbound.go:
/// request building for /audio/speech (TTS), /audio/transcriptions and
/// /audio/translations (STT), plus the streaming decoders, response
/// transforms and stream aggregators for all three endpoints.
/// </summary>
public sealed partial class OpenAiOutboundTransformer
{
    /// <summary>Port of buildSpeechRequest (audio_outbound.go:33-75).</summary>
    private HttpRequestData BuildSpeechRequest(LlmRequest request)
    {
        if (request.Speech is null)
        {
            throw TransformerErrors.InvalidRequest("speech request is nil in llm.Request");
        }

        if (request.Speech.Input.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("input is required for speech");
        }

        if (request.Speech.Voice.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("voice is required for speech");
        }

        byte[] body;
        try
        {
            // Go's omitempty drops empty strings; the port nulls them out for
            // the same wire shape (inbound empty values become null).
            body = GoJson.Marshal(new SpeechRequestBody
            {
                Model = request.Model,
                Input = request.Speech.Input,
                Voice = request.Speech.Voice,
                ResponseFormat = EmptyToNullAudio(request.Speech.ResponseFormat),
                Speed = request.Speech.Speed,
                Instructions = EmptyToNullAudio(request.Speech.Instructions),
                StreamFormat = EmptyToNullAudio(request.Speech.StreamFormat),
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to marshal speech request: " + ex.Message, ex);
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        if (request.Speech.StreamFormat == "sse")
        {
            // Streaming TTS returns text/event-stream.
            HttpClientUtils.HeaderSet(headers, "Accept", "text/event-stream");
        }
        else
        {
            // Non-streaming audio is returned as a binary stream; accept anything.
            HttpClientUtils.HeaderSet(headers, "Accept", "*/*");
        }

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "POST",
            Url = BuildAudioUrl("/audio/speech"),
            Headers = headers,
            Body = body,
            ContentType = "application/json",
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            RequestType = RequestTypes.Speech,
            ApiFormat = ApiFormats.OpenAISpeech,
        };
    }

    /// <summary>Port of buildTranscriptionRequest (audio_outbound.go:77-122).</summary>
    private HttpRequestData BuildTranscriptionRequest(LlmRequest request)
    {
        if (request.Transcription is null)
        {
            throw TransformerErrors.InvalidRequest("transcription request is nil in llm.Request");
        }

        var tr = request.Transcription;
        if (tr.File.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("file is required for transcription");
        }

        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["model"] = [request.Model],
        };

        if (!string.IsNullOrEmpty(tr.Language))
        {
            fields["language"] = [tr.Language];
        }

        if (!string.IsNullOrEmpty(tr.Prompt))
        {
            fields["prompt"] = [tr.Prompt];
        }

        if (!string.IsNullOrEmpty(tr.ResponseFormat))
        {
            fields["response_format"] = [tr.ResponseFormat];
        }

        if (tr.Temperature is not null)
        {
            fields["temperature"] = [GoCast.GoFloatToString(tr.Temperature.Value)];
        }

        // Forward unmodeled fields (e.g. timestamp_granularities[], include[])
        // as-is, overriding any modeled field the client sent twice.
        if (tr.Extra is not null)
        {
            foreach (var (key, values) in tr.Extra)
            {
                fields[key] = values;
            }
        }

        var stream = request.Stream == true;
        if (stream)
        {
            fields["stream"] = ["true"];
        }

        return BuildAudioMultipartRequest(
            "/audio/transcriptions",
            RequestTypes.Transcription,
            ApiFormats.OpenAITranscription,
            tr.File,
            tr.FileName,
            fields,
            stream);
    }

    /// <summary>Port of buildTranslationRequest (audio_outbound.go:124-155).</summary>
    private HttpRequestData BuildTranslationRequest(LlmRequest request)
    {
        if (request.Translation is null)
        {
            throw TransformerErrors.InvalidRequest("translation request is nil in llm.Request");
        }

        var tr = request.Translation;
        if (tr.File.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("file is required for translation");
        }

        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["model"] = [request.Model],
        };

        if (!string.IsNullOrEmpty(tr.Prompt))
        {
            fields["prompt"] = [tr.Prompt];
        }

        if (!string.IsNullOrEmpty(tr.ResponseFormat))
        {
            fields["response_format"] = [tr.ResponseFormat];
        }

        if (tr.Temperature is not null)
        {
            fields["temperature"] = [GoCast.GoFloatToString(tr.Temperature.Value)];
        }

        if (tr.Extra is not null)
        {
            foreach (var (key, values) in tr.Extra)
            {
                fields[key] = values;
            }
        }

        var stream = request.Stream == true;
        if (stream)
        {
            fields["stream"] = ["true"];
        }

        return BuildAudioMultipartRequest(
            "/audio/translations",
            RequestTypes.Translation,
            ApiFormats.OpenAITranslation,
            tr.File,
            tr.FileName,
            fields,
            stream);
    }

    /// <summary>
    /// Port of buildAudioMultipartRequest (audio_outbound.go:157-230). The
    /// reference's write errors (create/write/close) cannot happen against an
    /// in-memory writer and are omitted.
    /// </summary>
    private HttpRequestData BuildAudioMultipartRequest(
        string path,
        string requestType,
        string apiFormat,
        byte[] file,
        string fileName,
        Dictionary<string, List<string>> fields,
        bool stream)
    {
        fileName = OpenAiAudioModels.SanitizeAudioFileName(fileName);
        if (fileName.Length == 0)
        {
            fileName = "audio.mp3";
        }

        var writer = new GoMultipartWriter();
        writer.CreatePart("file", fileName, "application/octet-stream", file);

        foreach (var (key, values) in fields)
        {
            foreach (var value in values)
            {
                writer.WriteField(key, value);
            }
        }

        writer.Close();
        var body = writer.ToArray();

        // Build JSONBody for logging: replace binary audio with a size
        // placeholder. Unlike the inbound representation, empty single-valued
        // fields are kept (audio_outbound.go:204-215).
        var jsonBody = new Dictionary<string, object>(fields.Count + 1);
        foreach (var (key, values) in fields)
        {
            jsonBody[key] = values.Count == 1 ? values[0] : values;
        }

        jsonBody["file"] = $"<audio bytes: {file.Length}, filename: {fileName}>";

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", writer.FormDataContentType());
        if (stream)
        {
            HttpClientUtils.HeaderSet(headers, "Accept", "text/event-stream");
        }
        else
        {
            HttpClientUtils.HeaderSet(headers, "Accept", "application/json");
        }

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "POST",
            Url = BuildAudioUrl(path),
            Headers = headers,
            ContentType = writer.FormDataContentType(),
            Body = body,
            JsonBody = GoJson.Marshal(jsonBody),
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            RequestType = requestType,
            ApiFormat = apiFormat,
        };
    }

    /// <summary>Port of buildAudioURL (audio_outbound.go:238-243).</summary>
    private string BuildAudioUrl(string defaultPath)
        => _config.EndpointPath.Length > 0
            ? _config.BaseUrl + _config.EndpointPath
            : _config.BaseUrl + defaultPath;

    /// <summary>Port of transformSpeechResponse (audio_outbound.go:378-392).</summary>
    private LlmResponse TransformSpeechResponse(HttpResponseData response)
    {
        var contentType = HttpClientUtils.HeaderGet(response.Headers, "Content-Type");
        if (contentType.Length == 0)
        {
            contentType = "audio/mpeg";
        }

        return new LlmResponse
        {
            RequestType = RequestTypes.Speech,
            ApiFormat = ApiFormats.OpenAISpeech,
            Speech = new SpeechResponse
            {
                Audio = response.Body ?? [],
                ContentType = contentType,
            },
        };
    }

    /// <summary>
    /// Port of transformTranscriptionResponse (audio_outbound.go:394-434): JSON
    /// formats are parsed, other formats (text/srt/vtt) pass through raw.
    /// </summary>
    private LlmResponse TransformTranscriptionResponse(HttpResponseData response, string apiFormat)
    {
        var contentType = HttpClientUtils.HeaderGet(response.Headers, "Content-Type");
        var body = response.Body ?? [];

        var resp = new LlmResponse
        {
            RequestType = OpenAiAudioModels.RequestTypeForAudioFormat(apiFormat),
            ApiFormat = apiFormat,
        };

        // Strictly parse only when the provider declares JSON; otherwise sniff
        // with json.Valid so plain-text transcripts starting with '[' or '{'
        // are not misclassified and fed to the decoder.
        var isJson = contentType.ToLowerInvariant().Contains("application/json", StringComparison.Ordinal);
        if (!isJson && contentType.Length == 0)
        {
            isJson = OpenAiAudioModels.LooksLikeJson(body) && GoJsonSyntax.Validate(body) is null;
        }

        if (isJson)
        {
            // Go validates the whole document before decoding; syntax errors
            // keep encoding/json's own wording.
            var syntaxError = GoJsonSyntax.Validate(body);
            if (syntaxError is not null)
            {
                throw new InvalidOperationException(
                    "failed to unmarshal transcription response: " + syntaxError);
            }

            TranscriptionResponse parsed;
            var rootKind = GoStructJson.RootKind(body);
            if (rootKind is not null)
            {
                if (rootKind != "null")
                {
                    throw new InvalidOperationException(
                        "failed to unmarshal transcription response: "
                        + GoStructJson.TypeMismatch(rootKind, "llm.TranscriptionResponse"));
                }

                // null leaves the zero struct in place.
                parsed = new TranscriptionResponse();
            }
            else
            {
                // Field-level type errors use encoding/json's wording (walker).
                var typeError = OpenAiRequestTypeCheck.ValidateTranscriptionResponse(body);
                if (typeError is not null)
                {
                    throw new InvalidOperationException(
                        "failed to unmarshal transcription response: " + typeError);
                }

                try
                {
                    parsed = LlmJson.Deserialize<TranscriptionResponse>(body)
                        ?? new TranscriptionResponse();
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        "failed to unmarshal transcription response: "
                        + OpenAiInboundTransformer.JsonErrorText(ex), ex);
                }
            }

            // Preserve the raw JSON so extra fields (segments, words, task for
            // verbose_json) are returned to the client untouched.
            parsed.Raw = body;
            parsed.RawContentType = "application/json";
            resp.Transcription = parsed;
            return resp;
        }

        // Non-JSON format (text/srt/vtt): pass through raw.
        if (contentType.Length == 0)
        {
            contentType = "text/plain";
        }

        resp.Transcription = new TranscriptionResponse
        {
            Text = Encoding.UTF8.GetString(body),
            Raw = body,
            RawContentType = contentType,
        };

        return resp;
    }

    /// <summary>
    /// Port of transformSpeechStreamChunk (audio_outbound.go:245-277): one SSE
    /// event from a streaming TTS response.
    /// </summary>
    private static LlmResponse? TransformSpeechStreamChunk(StreamEvent? streamEvent)
    {
        if (streamEvent is null || streamEvent.Data.Length == 0)
        {
            // Skip empty heartbeats.
            return null;
        }

        if (StartsWith(streamEvent.Data, "[DONE]"))
        {
            return LlmResponse.Done();
        }

        // Reuse the chat-stream error-event detector so upstream `event: error`
        // or {"error":{...}} payloads arriving after a 200 are surfaced as
        // stream errors rather than silently decoded into empty audio events.
        if (ParseStreamErrorEvent(streamEvent) is { } streamErr)
        {
            throw streamErr;
        }

        var ev = DecodeSpeechStreamEvent(streamEvent.Data);

        return new LlmResponse
        {
            RequestType = RequestTypes.Speech,
            ApiFormat = ApiFormats.OpenAISpeech,
            SpeechStreamEvent = ev,
            Usage = ev.Usage,
        };
    }

    /// <summary>Port of transformSpeechBinaryChunk (audio_outbound.go:279-309).</summary>
    private static LlmResponse? TransformSpeechBinaryChunk(StreamEvent? streamEvent)
    {
        if (streamEvent is not null && streamEvent.Type == AuthTypes.BinaryStreamDoneEventType)
        {
            return new LlmResponse
            {
                Object = "[DONE]",
                RequestType = RequestTypes.Speech,
                ApiFormat = ApiFormats.OpenAISpeech,
            };
        }

        if (streamEvent is null || streamEvent.Data.Length == 0)
        {
            // Skip empty chunks.
            return null;
        }

        var contentType = streamEvent.Type.Trim();
        if (contentType.Length == 0)
        {
            contentType = "application/octet-stream";
        }

        return new LlmResponse
        {
            RequestType = RequestTypes.Speech,
            ApiFormat = ApiFormats.OpenAISpeech,
            SpeechAudioChunk = new SpeechAudioChunk
            {
                Audio = [.. streamEvent.Data],
                ContentType = contentType,
            },
        };
    }

    /// <summary>Port of isSpeechBinaryStreamEvent (audio_outbound.go:311-314).</summary>
    private static bool IsSpeechBinaryStreamEvent(StreamEvent? streamEvent)
        => streamEvent is not null
            && (streamEvent.Type == AuthTypes.BinaryStreamDoneEventType || streamEvent.IsBinaryAudioChunk());

    /// <summary>Port of speechStreamChunkTransformFor (audio_outbound.go:363-371).</summary>
    private static LlmResponse? SpeechStreamChunkTransform(StreamEvent? streamEvent)
        => IsSpeechBinaryStreamEvent(streamEvent)
            ? TransformSpeechBinaryChunk(streamEvent)
            : TransformSpeechStreamChunk(streamEvent);

    /// <summary>
    /// Port of transformTranscriptionStreamChunkFor (audio_outbound.go:316-346):
    /// one SSE event from a streaming STT/translation response.
    /// </summary>
    private static LlmResponse? TransformTranscriptionStreamChunk(StreamEvent? streamEvent, string apiFormat)
    {
        if (streamEvent is null || streamEvent.Data.Length == 0)
        {
            // Skip empty heartbeats.
            return null;
        }

        if (StartsWith(streamEvent.Data, "[DONE]"))
        {
            return LlmResponse.Done();
        }

        if (ParseStreamErrorEvent(streamEvent) is { } streamErr)
        {
            throw streamErr;
        }

        var ev = DecodeTranscriptionStreamEvent(streamEvent.Data);

        return new LlmResponse
        {
            RequestType = OpenAiAudioModels.RequestTypeForAudioFormat(apiFormat),
            ApiFormat = apiFormat,
            TranscriptionStreamEvent = ev,
            Usage = ev.Usage,
        };
    }

    /// <summary>
    /// Port of the stream-event decode in transformSpeechStreamChunk: the same
    /// full-document validation as json.Unmarshal (null leaves the zero struct).
    /// Field-level type errors use System.Text.Json's wording (difference C079).
    /// </summary>
    private static SpeechStreamEvent DecodeSpeechStreamEvent(byte[] data)
    {
        var syntaxError = GoJsonSyntax.Validate(data);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException("failed to decode speech stream event: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(data);
        if (rootKind is not null)
        {
            if (rootKind != "null")
            {
                throw new InvalidOperationException(
                    "failed to decode speech stream event: "
                    + GoStructJson.TypeMismatch(rootKind, "llm.SpeechStreamEvent"));
            }

            return new SpeechStreamEvent();
        }

        try
        {
            return LlmJson.Deserialize<SpeechStreamEvent>(data) ?? new SpeechStreamEvent();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to decode speech stream event: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }
    }

    /// <summary>Port of the stream-event decode in transformTranscriptionStreamChunkFor.</summary>
    private static TranscriptionStreamEvent DecodeTranscriptionStreamEvent(byte[] data)
    {
        var syntaxError = GoJsonSyntax.Validate(data);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException("failed to decode transcription stream event: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(data);
        if (rootKind is not null)
        {
            if (rootKind != "null")
            {
                throw new InvalidOperationException(
                    "failed to decode transcription stream event: "
                    + GoStructJson.TypeMismatch(rootKind, "llm.TranscriptionStreamEvent"));
            }

            return new TranscriptionStreamEvent();
        }

        try
        {
            return LlmJson.Deserialize<TranscriptionStreamEvent>(data) ?? new TranscriptionStreamEvent();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to decode transcription stream event: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }
    }

    /// <summary>
    /// Port of aggregateSpeechStreamChunks (audio_inbound.go:370-443): a
    /// metadata summary for persistence/logging. Audio bytes are tracked
    /// separately and never written into the JSON column.
    /// </summary>
    public static byte[] AggregateSpeechStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        var audioBytes = 0;
        Usage? usage = null;
        var completed = false;

        foreach (var chunk in chunks)
        {
            if (chunk is null)
            {
                continue;
            }

            if (chunk.Type == AuthTypes.BinaryStreamDoneEventType)
            {
                completed = true;
                continue;
            }

            if (IsSpeechBinaryStreamEvent(chunk))
            {
                // Persistence layer may have replaced the binary payload with a
                // size summary; fall back to chunk.Size when Data is empty.
                if (chunk.Data.Length > 0)
                {
                    audioBytes += chunk.Data.Length;
                }
                else
                {
                    audioBytes += chunk.Size;
                }

                continue;
            }

            if (chunk.Data.Length == 0)
            {
                continue;
            }

            if (StartsWith(chunk.Data, "[DONE]"))
            {
                completed = true;
                continue;
            }

            SpeechStreamEvent? ev;
            try
            {
                ev = LlmJson.Deserialize<SpeechStreamEvent>(chunk.Data);
            }
            catch (JsonException)
            {
                continue;
            }

            if (ev is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(ev.AudioBase64))
            {
                // Estimate decoded byte count without actually decoding to keep
                // aggregation cheap.
                audioBytes += Base64DecodedLen(ev.AudioBase64);
            }

            if (ev.Type == "speech.audio.done")
            {
                completed = true;
            }

            if (ev.Usage is not null)
            {
                usage = ev.Usage;
            }
        }

        // Go's json.Marshal sorts map keys: audio_bytes, chunks, object.
        var body = GoJson.Marshal(new Dictionary<string, object>
        {
            ["object"] = AudioModels.SpeechStreamResponseId,
            ["audio_bytes"] = audioBytes,
            ["chunks"] = chunks.Count,
        });

        meta = new ResponseMeta
        {
            Id = AudioModels.SpeechStreamResponseId,
            Usage = usage,
            Completed = completed,
        };

        return body;
    }

    /// <summary>
    /// Port of aggregateTranscriptionStreamChunks (audio_inbound.go:445-489):
    /// the equivalent of a non-streamed JSON response.
    /// </summary>
    public static byte[] AggregateTranscriptionStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        var deltaBuilder = new StringBuilder();
        var finalText = string.Empty;
        Usage? usage = null;
        var completed = false;

        foreach (var chunk in chunks)
        {
            if (chunk is null || chunk.Data.Length == 0)
            {
                continue;
            }

            if (StartsWith(chunk.Data, "[DONE]"))
            {
                completed = true;
                continue;
            }

            TranscriptionStreamEvent? ev;
            try
            {
                ev = LlmJson.Deserialize<TranscriptionStreamEvent>(chunk.Data);
            }
            catch (JsonException)
            {
                continue;
            }

            if (ev is null)
            {
                continue;
            }

            switch (ev.Type)
            {
                case "transcript.text.delta":
                    deltaBuilder.Append(ev.Delta ?? string.Empty);
                    break;
                case "transcript.text.done":
                    completed = true;
                    if (!string.IsNullOrEmpty(ev.Text))
                    {
                        finalText = ev.Text;
                    }

                    if (ev.Usage is not null)
                    {
                        usage = ev.Usage;
                    }

                    break;
            }
        }

        var text = finalText;
        if (text.Length == 0)
        {
            text = deltaBuilder.ToString();
        }

        var body = GoJson.Marshal(new Dictionary<string, object> { ["text"] = text });

        meta = new ResponseMeta { Usage = usage, Completed = completed };
        return body;
    }

    /// <summary>
    /// Port of base64DecodedLen (audio_inbound.go:491-494): decoded byte length
    /// without allocating; tolerates missing padding by trimming trailing '='.
    /// </summary>
    private static int Base64DecodedLen(string value)
        => value.TrimEnd('=').Length * 3 / 4;

    /// <summary>
    /// Go's omitempty drops empty strings on marshal; the port's nullable
    /// fields must be null to reproduce the same wire shape.
    /// </summary>
    private static string? EmptyToNullAudio(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
