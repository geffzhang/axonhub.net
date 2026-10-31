using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the OpenAI audio slice: one-for-one port of
/// audio_inbound_test.go (TestAudioInboundTransformer_Speech/Transcription/
/// Translation) and audio_outbound_test.go (TestOutbound_BuildSpeechRequest,
/// BuildTranscriptionRequest, BuildTranslationRequest_URL, TransformSpeechResponse,
/// TransformTranscriptionResponse, TestAudioRoundTrip_Speech,
/// TestAudioStreaming_Speech/SpeechBinaryChunks/Transcription/PropagatesErrorEvent).
/// </summary>
public sealed class OpenAiAudioTransformerTests
{
    // ---------------------------------------------------------------- inbound speech

    [Fact]
    public void AudioInboundSpeech_ApiFormat()
    {
        Assert.Equal(ApiFormats.OpenAISpeech, AudioInboundTransformer.Speech().ApiFormat);
    }

    [Fact]
    public void AudioInboundSpeech_ValidRequest()
    {
        var tr = AudioInboundTransformer.Speech();
        var body = GoJson.Marshal(new Dictionary<string, object?>
        {
            ["model"] = "tts-1",
            ["input"] = "Hello world",
            ["voice"] = "alloy",
            ["response_format"] = "mp3",
            ["speed"] = 1.25,
        });

        var llmReq = tr.TransformRequest(JsonRequest(body));

        Assert.Equal("tts-1", llmReq.Model);
        Assert.Equal(RequestTypes.Speech, llmReq.RequestType);
        Assert.Equal(ApiFormats.OpenAISpeech, llmReq.ApiFormat);
        Assert.NotNull(llmReq.Speech);
        Assert.Equal("Hello world", llmReq.Speech.Input);
        Assert.Equal("alloy", llmReq.Speech.Voice);
        Assert.Equal("mp3", llmReq.Speech.ResponseFormat);
        Assert.NotNull(llmReq.Speech.Speed);
        Assert.Equal(1.25, llmReq.Speech.Speed!.Value, 9);
    }

    [Fact]
    public void AudioInboundSpeech_MissingInput()
    {
        var tr = AudioInboundTransformer.Speech();
        var body = GoJson.Marshal(new Dictionary<string, object?> { ["model"] = "tts-1" });

        Assert.Throws<TransformerException>(() => tr.TransformRequest(JsonRequest(body)));
    }

    [Fact]
    public void AudioInboundSpeech_MissingVoice()
    {
        var tr = AudioInboundTransformer.Speech();
        var body = GoJson.Marshal(new Dictionary<string, object?> { ["model"] = "tts-1", ["input"] = "hello" });

        var ex = Assert.Throws<TransformerException>(() => tr.TransformRequest(JsonRequest(body)));
        Assert.Contains("voice", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioInboundSpeech_EmptyBody()
    {
        var tr = AudioInboundTransformer.Speech();

        Assert.Throws<TransformerException>(
            () => tr.TransformRequest(new HttpRequestData { Headers = JsonHeaders() }));
    }

    [Fact]
    public void AudioInboundSpeech_ResponseBinaryPassthrough()
    {
        var tr = AudioInboundTransformer.Speech();
        var audio = new byte[] { 0x49, 0x44, 0x33, 0x04, 0x00 };

        var resp = tr.TransformResponse(new LlmResponse
        {
            Speech = new SpeechResponse { Audio = audio, ContentType = "audio/mpeg" },
        });

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(audio, resp.Body);
        Assert.Equal("audio/mpeg", HttpClientUtils.HeaderGet(resp.Headers, "Content-Type"));
    }

    [Fact]
    public void AudioInboundSpeech_UnsupportedStreamFormatRejected()
    {
        var tr = AudioInboundTransformer.Speech();
        var body = GoJson.Marshal(new Dictionary<string, object?>
        {
            ["model"] = "gpt-4o-mini-tts",
            ["input"] = "hi",
            ["voice"] = "alloy",
            ["stream_format"] = "json",
        });

        var ex = Assert.Throws<TransformerException>(() => tr.TransformRequest(JsonRequest(body)));
        Assert.Contains("stream_format", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioInboundSpeech_SseStreamFormatEnablesStreaming()
    {
        var tr = AudioInboundTransformer.Speech();
        var body = GoJson.Marshal(new Dictionary<string, object?>
        {
            ["model"] = "gpt-4o-mini-tts",
            ["input"] = "hi",
            ["voice"] = "alloy",
            ["stream_format"] = "sse",
        });

        var req = tr.TransformRequest(JsonRequest(body));

        Assert.True(req.Stream);
        Assert.Equal("sse", req.Speech!.StreamFormat);
    }

    [Fact]
    public void AudioInboundSpeech_AudioStreamFormatEnablesBinaryStreaming()
    {
        var tr = AudioInboundTransformer.Speech();
        var body = GoJson.Marshal(new Dictionary<string, object?>
        {
            ["model"] = "gpt-4o-mini-tts",
            ["input"] = "hi",
            ["voice"] = "alloy",
            ["stream_format"] = "audio",
        });

        var req = tr.TransformRequest(JsonRequest(body));

        Assert.True(req.Stream);
        Assert.Equal("audio", req.Speech!.StreamFormat);
    }

    [Fact]
    public void AudioInboundSpeech_MissingStreamFormatStaysNonStreaming()
    {
        var tr = AudioInboundTransformer.Speech();
        var body = GoJson.Marshal(new Dictionary<string, object?>
        {
            ["model"] = "gpt-4o-mini-tts",
            ["input"] = "hi",
            ["voice"] = "alloy",
        });

        var req = tr.TransformRequest(JsonRequest(body));

        Assert.False(req.Stream);
        Assert.Null(req.Speech!.StreamFormat);
    }

    // ---------------------------------------------------------------- inbound transcription

    [Fact]
    public void AudioInboundTranscription_ApiFormat()
    {
        Assert.Equal(ApiFormats.OpenAITranscription, AudioInboundTransformer.Transcription().ApiFormat);
    }

    [Fact]
    public void AudioInboundTranscription_ValidMultipartRequest()
    {
        var tr = AudioInboundTransformer.Transcription();
        var audio = Encoding.UTF8.GetBytes("FAKE_AUDIO_BYTES");
        var (contentType, body) = BuildMultipartAudio(audio, "speech.mp3",
            ("model", "whisper-1"),
            ("language", "en"),
            ("response_format", "json"),
            ("temperature", "0.2"));

        var httpReq = MultipartRequest(contentType, body);
        var llmReq = tr.TransformRequest(httpReq);

        Assert.Equal("whisper-1", llmReq.Model);
        Assert.Equal(RequestTypes.Transcription, llmReq.RequestType);
        Assert.NotNull(llmReq.Transcription);
        Assert.Equal(audio, llmReq.Transcription.File);
        Assert.Equal("speech.mp3", llmReq.Transcription.FileName);
        Assert.Equal("en", llmReq.Transcription.Language);
        Assert.Equal("json", llmReq.Transcription.ResponseFormat);
        Assert.NotNull(llmReq.Transcription.Temperature);
        Assert.Equal(0.2, llmReq.Transcription.Temperature!.Value, 9);

        // JSONBody should be populated for logging and must not contain the raw audio.
        Assert.NotEmpty(httpReq.JsonBody!);
        Assert.DoesNotContain("FAKE_AUDIO_BYTES", Text(httpReq.JsonBody!), StringComparison.Ordinal);
    }

    [Fact]
    public void AudioInboundTranscription_MissingFile()
    {
        var tr = AudioInboundTransformer.Transcription();
        var (contentType, body) = BuildMultipartAudio(null, string.Empty, ("model", "whisper-1"));

        Assert.Throws<TransformerException>(() => tr.TransformRequest(MultipartRequest(contentType, body)));
    }

    [Fact]
    public void AudioInboundTranscription_DuplicateFilePartsRejected()
    {
        var tr = AudioInboundTransformer.Transcription();
        var writer = new GoMultipartWriter();
        writer.CreatePart("file", "a.mp3", "application/octet-stream", Encoding.UTF8.GetBytes("AUDIO"));
        writer.CreatePart("file", "b.mp3", "application/octet-stream", Encoding.UTF8.GetBytes("AUDIO"));
        writer.WriteField("model", "whisper-1");
        writer.Close();

        var ex = Assert.Throws<TransformerException>(
            () => tr.TransformRequest(MultipartRequest(writer.FormDataContentType(), writer.ToArray())));
        Assert.Contains("multiple file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioInboundTranscription_ExtraFieldsPreserved()
    {
        var tr = AudioInboundTransformer.Transcription();
        var writer = new GoMultipartWriter();
        writer.CreatePart("file", "a.mp3", "application/octet-stream", Encoding.UTF8.GetBytes("AUDIO"));
        writer.WriteField("model", "whisper-1");
        writer.WriteField("response_format", "verbose_json");
        // Repeated unmodeled field must keep multiplicity.
        writer.WriteField("timestamp_granularities[]", "word");
        writer.WriteField("timestamp_granularities[]", "segment");
        writer.Close();

        var llmReq = tr.TransformRequest(MultipartRequest(writer.FormDataContentType(), writer.ToArray()));

        Assert.NotNull(llmReq.Transcription!.Extra);
        Assert.Single(llmReq.Transcription.Extra);
        Assert.Equal(
            new List<string> { "word", "segment" },
            llmReq.Transcription.Extra["timestamp_granularities[]"]);
    }

    [Fact]
    public void AudioInboundTranscription_InvalidTemperature()
    {
        var tr = AudioInboundTransformer.Transcription();
        var (contentType, body) = BuildMultipartAudio(
            Encoding.UTF8.GetBytes("AUDIO"), "a.mp3", ("model", "whisper-1"), ("temperature", "abc"));

        var ex = Assert.Throws<TransformerException>(() => tr.TransformRequest(MultipartRequest(contentType, body)));
        Assert.Contains("temperature", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioInboundTranscription_NotMultipart()
    {
        var tr = AudioInboundTransformer.Transcription();

        Assert.Throws<TransformerException>(() => tr.TransformRequest(
            JsonRequest(Encoding.UTF8.GetBytes("{\"model\":\"whisper-1\"}"))));
    }

    [Fact]
    public void AudioInboundTranscription_ResponseJson()
    {
        var tr = AudioInboundTransformer.Transcription();

        var resp = tr.TransformResponse(new LlmResponse
        {
            Transcription = new TranscriptionResponse { Text = "hello" },
        });

        Assert.Equal("application/json", HttpClientUtils.HeaderGet(resp.Headers, "Content-Type"));

        using var doc = JsonDocument.Parse(resp.Body!);
        Assert.Equal("hello", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void AudioInboundTranscription_ResponseRawVerboseJsonPassthroughKeepsExtraFields()
    {
        var tr = AudioInboundTransformer.Transcription();
        var raw = Encoding.UTF8.GetBytes(
            "{\"task\":\"transcribe\",\"text\":\"hello\",\"segments\":[{\"id\":0,\"text\":\"hello\"}]}");

        var resp = tr.TransformResponse(new LlmResponse
        {
            Transcription = new TranscriptionResponse { Text = "hello", Raw = raw, RawContentType = "application/json" },
        });

        Assert.Equal(raw, resp.Body);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(resp.Headers, "Content-Type"));
    }

    [Fact]
    public void AudioInboundTranscription_ResponseRawSrtPassthrough()
    {
        var tr = AudioInboundTransformer.Transcription();
        var raw = Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:01,000\nhello\n");

        var resp = tr.TransformResponse(new LlmResponse
        {
            Transcription = new TranscriptionResponse { Raw = raw, RawContentType = "text/plain" },
        });

        Assert.Equal(raw, resp.Body);
        Assert.Equal("text/plain", HttpClientUtils.HeaderGet(resp.Headers, "Content-Type"));
    }

    // ---------------------------------------------------------------- inbound translation

    [Fact]
    public void AudioInboundTranslation_ApiFormat()
    {
        Assert.Equal(ApiFormats.OpenAITranslation, AudioInboundTransformer.Translation().ApiFormat);
    }

    [Fact]
    public void AudioInboundTranslation_Basic()
    {
        var tr = AudioInboundTransformer.Translation();
        var audio = Encoding.UTF8.GetBytes("FAKE_AUDIO");
        var (contentType, body) = BuildMultipartAudio(audio, "de.mp3", ("model", "whisper-1"));

        var llmReq = tr.TransformRequest(MultipartRequest(contentType, body));

        Assert.Equal(RequestTypes.Translation, llmReq.RequestType);
        Assert.NotNull(llmReq.Translation);
        Assert.Equal(audio, llmReq.Translation.File);
        Assert.Equal("de.mp3", llmReq.Translation.FileName);
    }

    // ---------------------------------------------------------------- outbound

    private static OpenAiOutboundTransformer NewOutbound()
        => OpenAiOutboundTransformer.Create("https://api.openai.com", "sk-test");

    [Fact]
    public void OutboundAudio_BuildSpeechRequest()
    {
        var outbound = NewOutbound();

        var llmReq = new LlmRequest
        {
            Model = "tts-1",
            Stream = false,
            RequestType = RequestTypes.Speech,
            ApiFormat = ApiFormats.OpenAISpeech,
            Speech = new SpeechRequest
            {
                Input = "Hello",
                Voice = "nova",
                ResponseFormat = "wav",
                Speed = 1.5,
            },
        };

        var httpReq = outbound.TransformRequest(llmReq);

        Assert.Equal("POST", httpReq.Method);
        Assert.Equal("https://api.openai.com/v1/audio/speech", httpReq.Url);
        Assert.Equal(RequestTypes.Speech, httpReq.RequestType);
        Assert.Equal(ApiFormats.OpenAISpeech, httpReq.ApiFormat);

        using var doc = JsonDocument.Parse(httpReq.Body!);
        Assert.Equal("tts-1", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("Hello", doc.RootElement.GetProperty("input").GetString());
        Assert.Equal("nova", doc.RootElement.GetProperty("voice").GetString());
        Assert.Equal("wav", doc.RootElement.GetProperty("response_format").GetString());
        Assert.Equal(1.5, doc.RootElement.GetProperty("speed").GetDouble(), 9);
        Assert.False(doc.RootElement.TryGetProperty("stream", out _));
    }

    [Fact]
    public void OutboundAudio_BuildTranscriptionRequest()
    {
        var outbound = NewOutbound();

        var httpReq = outbound.TransformRequest(new LlmRequest
        {
            Model = "whisper-1",
            RequestType = RequestTypes.Transcription,
            ApiFormat = ApiFormats.OpenAITranscription,
            Transcription = new TranscriptionRequest
            {
                File = Encoding.UTF8.GetBytes("AUDIO_DATA"),
                FileName = "a.mp3",
                Language = "en",
                ResponseFormat = "json",
            },
        });

        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", httpReq.Url);

        Assert.True(GoMimeTypes.TryParseMediaType(
            HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type"), out var mediaType, out var parameters));
        Assert.Equal("multipart/form-data", mediaType);
        Assert.NotEmpty(parameters["boundary"]);

        // Raw audio is in the wire body, but JSONBody (for logging) replaces it with a placeholder.
        Assert.Contains("AUDIO_DATA", Text(httpReq.Body!), StringComparison.Ordinal);
        Assert.NotEmpty(httpReq.JsonBody!);
        Assert.DoesNotContain("AUDIO_DATA", Text(httpReq.JsonBody!), StringComparison.Ordinal);
        Assert.Contains("audio bytes", Text(httpReq.JsonBody!), StringComparison.Ordinal);
    }

    [Fact]
    public void OutboundAudio_BuildTranscriptionRequest_ExtraFieldsAndFileName()
    {
        var outbound = NewOutbound();

        var httpReq = outbound.TransformRequest(new LlmRequest
        {
            Model = "whisper-1",
            RequestType = RequestTypes.Transcription,
            ApiFormat = ApiFormats.OpenAITranscription,
            Transcription = new TranscriptionRequest
            {
                File = Encoding.UTF8.GetBytes("AUDIO"),
                // Malicious filename attempting multipart header injection.
                FileName = "a\r\nContent-Type: text/evil\r\n.mp3",
                Extra = new Dictionary<string, List<string>>
                {
                    ["timestamp_granularities[]"] = ["word", "segment"],
                },
            },
        });

        Assert.True(GoMimeTypes.TryParseMediaType(
            HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type"), out _, out var parameters));

        // Parse the generated multipart body back and verify fields/filename.
        var reader = new GoMultipartReader(httpReq.Body!, parameters["boundary"]);
        var fileParts = 0;
        var granularities = new List<string>();
        while (reader.NextPart() is { } part)
        {
            if (part.Name == "file")
            {
                fileParts++;
            }
            else if (part.Name == "timestamp_granularities[]")
            {
                granularities.Add(Text(part.Data));
            }
        }

        Assert.Equal(new List<string> { "word", "segment" }, granularities);
        Assert.Equal(1, fileParts);
        // The multipart body parses cleanly (the injected header did not break it) and
        // no raw CR/LF from the filename leaked into the generated body.
        Assert.DoesNotContain("\r\nContent-Type: text/evil", Text(httpReq.Body!), StringComparison.Ordinal);
    }

    [Fact]
    public void OutboundAudio_BuildTranslationRequest_URL()
    {
        var outbound = NewOutbound();

        var httpReq = outbound.TransformRequest(new LlmRequest
        {
            Model = "whisper-1",
            RequestType = RequestTypes.Translation,
            ApiFormat = ApiFormats.OpenAITranslation,
            Translation = new TranslationRequest { File = [0x78], FileName = "a.mp3" },
        });

        Assert.Equal("https://api.openai.com/v1/audio/translations", httpReq.Url);
        Assert.Equal(ApiFormats.OpenAITranslation, httpReq.ApiFormat);
    }

    [Fact]
    public void OutboundAudio_TransformSpeechResponse()
    {
        var outbound = NewOutbound();

        var audio = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        var httpResp = new HttpResponseData
        {
            StatusCode = 200,
            Body = audio,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["audio/wav"],
            },
            Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAISpeech },
        };

        var llmResp = outbound.TransformResponse(httpResp);

        Assert.NotNull(llmResp.Speech);
        Assert.Equal(audio, llmResp.Speech.Audio);
        Assert.Equal("audio/wav", llmResp.Speech.ContentType);
    }

    [Fact]
    public void OutboundAudio_TransformTranscriptionResponse_Json()
    {
        var outbound = NewOutbound();
        var body = Encoding.UTF8.GetBytes("{\"text\":\"hello world\"}");
        var httpResp = TranscriptionResponse(body, "application/json");

        var llmResp = outbound.TransformResponse(httpResp);

        Assert.NotNull(llmResp.Transcription);
        Assert.Equal("hello world", llmResp.Transcription.Text);
        // Raw JSON is preserved for lossless passthrough back to the client.
        Assert.Equal(body, llmResp.Transcription.Raw);
        Assert.Equal("application/json", llmResp.Transcription.RawContentType);
    }

    [Fact]
    public void OutboundAudio_TransformTranscriptionResponse_VerboseJsonKeepsRawBody()
    {
        var outbound = NewOutbound();
        var raw = Encoding.UTF8.GetBytes(
            "{\"task\":\"transcribe\",\"language\":\"en\",\"duration\":1.5,\"text\":\"hi\",\"segments\":[{\"id\":0}]}");

        var llmResp = outbound.TransformResponse(TranscriptionResponse(raw, "application/json"));

        Assert.NotNull(llmResp.Transcription);
        Assert.Equal(raw, llmResp.Transcription.Raw);
    }

    [Fact]
    public void OutboundAudio_TransformTranscriptionResponse_BracketTextIsNotParsedAsJson()
    {
        // response_format=text can yield transcripts like "[Music] hello" which start
        // with '[' but are not JSON; they must pass through raw instead of failing.
        var outbound = NewOutbound();
        var raw = Encoding.UTF8.GetBytes("[Music] hello world");

        var llmResp = outbound.TransformResponse(
            TranscriptionResponse(raw, "text/plain; charset=utf-8"));

        Assert.NotNull(llmResp.Transcription);
        Assert.Equal("[Music] hello world", Text(llmResp.Transcription.Raw));
    }

    [Fact]
    public void OutboundAudio_TransformTranscriptionResponse_MissingContentTypeInvalidJsonPassesThroughRaw()
    {
        var outbound = NewOutbound();
        var raw = Encoding.UTF8.GetBytes("{not json at all");

        var llmResp = outbound.TransformResponse(TranscriptionResponse(raw, contentType: null));

        Assert.NotNull(llmResp.Transcription);
        Assert.Equal("{not json at all", Text(llmResp.Transcription.Raw));
    }

    [Fact]
    public void OutboundAudio_TransformTranscriptionResponse_MissingContentTypeValidJsonIsParsed()
    {
        var outbound = NewOutbound();

        var llmResp = outbound.TransformResponse(
            TranscriptionResponse(Encoding.UTF8.GetBytes("{\"text\":\"hi\"}"), contentType: null));

        Assert.Equal("hi", llmResp.Transcription!.Text);
    }

    [Fact]
    public void OutboundAudio_TransformTranscriptionResponse_RawSrt()
    {
        var outbound = NewOutbound();
        var raw = Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:01,000\nhi\n");

        var llmResp = outbound.TransformResponse(TranscriptionResponse(raw, "text/plain; charset=utf-8"));

        Assert.NotNull(llmResp.Transcription);
        Assert.Equal(raw, llmResp.Transcription.Raw);
    }

    // ---------------------------------------------------------------- round trip + streaming

    [Fact]
    public void AudioRoundTrip_Speech()
    {
        var inbound = AudioInboundTransformer.Speech();
        var outbound = NewOutbound();

        var clientBody = GoJson.Marshal(new Dictionary<string, object?>
        {
            ["model"] = "tts-1",
            ["input"] = "Hi",
            ["voice"] = "alloy",
        });

        var llmReq = inbound.TransformRequest(JsonRequest(clientBody));
        var providerReq = outbound.TransformRequest(llmReq);

        Assert.EndsWith("/audio/speech", providerReq.Url, StringComparison.Ordinal);

        // Simulate the provider returning binary audio.
        var audio = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var providerResp = new HttpResponseData
        {
            StatusCode = 200,
            Body = audio,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["audio/mpeg"],
            },
            Request = providerReq,
        };

        var llmResp = outbound.TransformResponse(providerResp);
        var clientResp = inbound.TransformResponse(llmResp);

        Assert.Equal(audio, clientResp.Body);
        Assert.Equal("audio/mpeg", HttpClientUtils.HeaderGet(clientResp.Headers, "Content-Type"));
    }

    [Fact]
    public void AudioStreaming_Speech()
    {
        var inbound = AudioInboundTransformer.Speech();
        var outbound = NewOutbound();

        // Client requests stream_format=sse.
        var clientBody = GoJson.Marshal(new Dictionary<string, object?>
        {
            ["model"] = "gpt-4o-mini-tts",
            ["input"] = "Hi",
            ["voice"] = "alloy",
            ["stream_format"] = "sse",
        });

        var llmReq = inbound.TransformRequest(JsonRequest(clientBody));
        Assert.True(llmReq.Stream);

        var providerReq = outbound.TransformRequest(llmReq);
        Assert.Equal("text/event-stream", HttpClientUtils.HeaderGet(providerReq.Headers, "Accept"));

        // stream_format passed through to the provider body.
        using (var doc = JsonDocument.Parse(providerReq.Body!))
        {
            Assert.Equal("sse", doc.RootElement.GetProperty("stream_format").GetString());
        }

        // Simulate the provider's SSE events flowing through the outbound transformer.
        StreamEvent[] events =
        [
            SseEvent("{\"type\":\"speech.audio.delta\",\"audio\":\"YWJjZA==\"}"), // "abcd" → 4 bytes
            SseEvent("{\"type\":\"speech.audio.delta\",\"audio\":\"ZWZnaA==\"}"), // "efgh" → 4 bytes
            SseEvent("{\"type\":\"speech.audio.done\",\"usage\":{\"total_tokens\":12}}"),
            SseEvent("[DONE]"),
        ];

        var llmStream = outbound.TransformStream(providerReq, new SliceStream<StreamEvent>(events));
        var clientStream = inbound.TransformStream(llmStream);

        var collected = new List<string>();
        StreamEvent? lastTyped = null;
        while (clientStream.Next())
        {
            var ev = clientStream.Current;
            Assert.NotNull(ev);
            collected.Add(Text(ev.Data));
            lastTyped = ev;
        }

        Assert.Null(clientStream.Err);
        // 3 events forwarded (delta, delta, done); [DONE] sentinel swallowed by inbound.
        Assert.Equal(3, collected.Count);
        Assert.Contains("\"speech.audio.delta\"", collected[0], StringComparison.Ordinal);
        Assert.Contains("\"speech.audio.done\"", collected[2], StringComparison.Ordinal);

        // Terminal *.done event must carry its Type so the persistence layer can detect completion.
        Assert.NotNull(lastTyped);
        Assert.Equal("speech.audio.done", lastTyped.Type);

        // Aggregation should track audio byte count and surface usage.
        var body = inbound.AggregateStreamChunks(events, out var meta);
        Assert.Contains("\"audio_bytes\":8", Text(body), StringComparison.Ordinal);
        Assert.True(meta.Completed);
        Assert.NotNull(meta.Usage);
        Assert.Equal(12, meta.Usage.TotalTokens);
    }

    [Fact]
    public void AudioStreaming_SpeechBinaryChunks()
    {
        var inbound = AudioInboundTransformer.Speech();
        var outbound = NewOutbound();

        var clientBody = GoJson.Marshal(new Dictionary<string, object?>
        {
            ["model"] = "gpt-4o-mini-tts",
            ["input"] = "Hi",
            ["voice"] = "alloy",
            ["stream_format"] = "audio",
        });

        var llmReq = inbound.TransformRequest(JsonRequest(clientBody));
        Assert.True(llmReq.Stream);

        var providerReq = outbound.TransformRequest(llmReq);
        Assert.Equal("*/*", HttpClientUtils.HeaderGet(providerReq.Headers, "Accept"));

        using (var doc = JsonDocument.Parse(providerReq.Body!))
        {
            Assert.Equal("audio", doc.RootElement.GetProperty("stream_format").GetString());
            Assert.False(doc.RootElement.TryGetProperty("stream", out _));
        }

        StreamEvent[] events =
        [
            new() { Type = "audio/mpeg", Data = [0x7b, 0x01, 0x02] },
            new() { Type = "audio/mpeg", Data = [0x04, 0x05] },
            new() { Type = AuthTypes.BinaryStreamDoneEventType },
        ];

        var llmStream = outbound.TransformStream(providerReq, new SliceStream<StreamEvent>(events));
        var clientStream = inbound.TransformStream(llmStream);

        var chunks = GoStreams.All(clientStream, out var streamErr);
        Assert.Null(streamErr);
        Assert.Equal(3, chunks.Count);
        Assert.Equal("audio/mpeg", chunks[0].Type);
        Assert.Equal(new byte[] { 0x7b, 0x01, 0x02 }, chunks[0].Data);
        Assert.Equal(new byte[] { 0x04, 0x05 }, chunks[1].Data);
        Assert.Equal(AuthTypes.BinaryStreamDoneEventType, chunks[2].Type);

        var body = inbound.AggregateStreamChunks(chunks, out var meta);
        Assert.Contains("\"audio_bytes\":5", Text(body), StringComparison.Ordinal);
        Assert.Equal(AudioModels.SpeechStreamResponseId, meta.Id);
        Assert.True(meta.Completed);
        Assert.Null(meta.Usage);
    }

    [Fact]
    public void AudioStreaming_Transcription()
    {
        var inbound = AudioInboundTransformer.Transcription();
        var outbound = NewOutbound();

        // Multipart upload with stream=true.
        var writer = new GoMultipartWriter();
        writer.CreatePart("file", "a.mp3", "application/octet-stream", Encoding.UTF8.GetBytes("AUDIO"));
        writer.WriteField("model", "gpt-4o-transcribe");
        writer.WriteField("stream", "true");
        writer.Close();

        var llmReq = inbound.TransformRequest(MultipartRequest(writer.FormDataContentType(), writer.ToArray()));
        Assert.True(llmReq.Stream);
        // stream consumed by gateway, must not leak into Extra (would be forwarded twice).
        IEnumerable<string> extraKeys = llmReq.Transcription!.Extra is null
            ? Array.Empty<string>()
            : llmReq.Transcription.Extra.Keys;
        Assert.DoesNotContain("stream", extraKeys);

        var providerReq = outbound.TransformRequest(llmReq);
        Assert.Equal("text/event-stream", HttpClientUtils.HeaderGet(providerReq.Headers, "Accept"));
        // stream=true must appear once in the outbound multipart body.
        Assert.Equal(1, CountOccurrences(Text(providerReq.Body!), "name=\"stream\""));

        StreamEvent[] events =
        [
            SseEvent("{\"type\":\"transcript.text.delta\",\"delta\":\"hello \"}"),
            SseEvent("{\"type\":\"transcript.text.delta\",\"delta\":\"world\"}"),
            SseEvent("{\"type\":\"transcript.text.done\",\"text\":\"hello world\",\"usage\":{\"total_tokens\":5}}"),
            SseEvent("[DONE]"),
        ];

        var llmStream = outbound.TransformStream(providerReq, new SliceStream<StreamEvent>(events));
        var clientStream = inbound.TransformStream(llmStream);

        var collected = new List<string>();
        StreamEvent? lastTyped = null;
        while (clientStream.Next())
        {
            var ev = clientStream.Current;
            Assert.NotNull(ev);
            collected.Add(Text(ev.Data));
            lastTyped = ev;
        }

        Assert.Null(clientStream.Err);
        Assert.Equal(3, collected.Count);
        Assert.Contains("\"transcript.text.delta\"", collected[0], StringComparison.Ordinal);
        Assert.Contains("\"transcript.text.done\"", collected[2], StringComparison.Ordinal);

        // Terminal *.done event must carry its Type so the persistence layer can detect completion.
        Assert.NotNull(lastTyped);
        Assert.Equal("transcript.text.done", lastTyped.Type);

        var body = inbound.AggregateStreamChunks(events, out var meta);
        Assert.Contains("\"text\":\"hello world\"", Text(body), StringComparison.Ordinal);
        Assert.True(meta.Completed);
        Assert.NotNull(meta.Usage);
        Assert.Equal(5, meta.Usage.TotalTokens);
    }

    [Fact]
    public void AudioStreaming_PropagatesErrorEvent_Speech()
    {
        var outbound = NewOutbound();
        var request = new HttpRequestData { ApiFormat = ApiFormats.OpenAISpeech };
        StreamEvent[] events =
        [
            SseEvent("{\"type\":\"speech.audio.delta\",\"audio\":\"YWJjZA==\"}"),
            new() { Type = "error", Data = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"upstream broke\",\"type\":\"server_error\"}}") },
        ];

        var llmStream = outbound.TransformStream(request, new SliceStream<StreamEvent>(events));
        while (llmStream.Next())
        {
        }

        Assert.NotNull(llmStream.Err);
        Assert.Contains("upstream broke", llmStream.Err?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AudioStreaming_PropagatesErrorEvent_Transcription()
    {
        var outbound = NewOutbound();
        var request = new HttpRequestData { ApiFormat = ApiFormats.OpenAITranscription };
        StreamEvent[] events =
        [
            SseEvent("{\"type\":\"transcript.text.delta\",\"delta\":\"hi\"}"),
            SseEvent("{\"error\":{\"message\":\"rate limited\",\"code\":\"rate_limit\"}}"),
        ];

        var llmStream = outbound.TransformStream(request, new SliceStream<StreamEvent>(events));
        while (llmStream.Next())
        {
        }

        Assert.NotNull(llmStream.Err);
        Assert.Contains("rate limited", llmStream.Err?.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static StreamEvent SseEvent(string json) => new() { Data = Encoding.UTF8.GetBytes(json) };

    private static string Text(byte[] data) => Encoding.UTF8.GetString(data);

    private static Dictionary<string, List<string>> JsonHeaders()
        => new(StringComparer.Ordinal) { ["Content-Type"] = ["application/json"] };

    private static HttpRequestData JsonRequest(byte[] body)
        => new()
        {
            Method = "POST",
            Url = "https://gateway.test/v1/audio/speech",
            Headers = JsonHeaders(),
            Body = body,
        };

    private static HttpRequestData MultipartRequest(string contentType, byte[] body)
        => new()
        {
            Method = "POST",
            Url = "https://gateway.test/v1/audio/transcriptions",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = [contentType],
            },
            Body = body,
        };

    /// <summary>
    /// Port of buildMultipartAudio: a multipart/form-data body with an optional
    /// file part and fields.
    /// </summary>
    private static (string ContentType, byte[] Body) BuildMultipartAudio(
        byte[]? file,
        string fileName,
        params (string Name, string Value)[] fields)
    {
        var writer = new GoMultipartWriter();
        if (file is { Length: > 0 })
        {
            writer.CreatePart("file", fileName, "application/octet-stream", file);
        }

        foreach (var (name, value) in fields)
        {
            writer.WriteField(name, value);
        }

        writer.Close();
        return (writer.FormDataContentType(), writer.ToArray());
    }

    private static HttpResponseData TranscriptionResponse(byte[] body, string? contentType)
        => new()
        {
            StatusCode = 200,
            Body = body,
            Headers = contentType is null
                ? new Dictionary<string, List<string>>(StringComparer.Ordinal)
                : new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = [contentType],
                },
            Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAITranscription },
        };

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
