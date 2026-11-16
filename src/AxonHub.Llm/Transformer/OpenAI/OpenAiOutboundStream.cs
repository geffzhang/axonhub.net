using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>Port of the chat-completion half of OutboundTransformer.TransformStream.</summary>
public sealed partial class OpenAiOutboundTransformer
{
    /// <summary>
    /// Port of TransformStream: upstream SSE events to a unified chat stream.
    /// Events the reference filters (null chunks) are dropped. Audio APIs use
    /// dedicated SSE event schemas; they are routed to the audio decoders so
    /// chat-completion parsing is not applied to transcript.text.delta /
    /// speech.audio.delta.
    /// </summary>
    public Streams.IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        Streams.IStream<StreamEvent> stream)
    {
        if (request is not null)
        {
            switch (request.ApiFormat)
            {
                case ApiFormats.OpenAISpeech:
                    return new Streams.MapStream<StreamEvent, LlmResponse>(stream, SpeechStreamChunkTransform);
                case ApiFormats.OpenAITranscription:
                    return new Streams.MapStream<StreamEvent, LlmResponse>(
                        stream, e => TransformTranscriptionStreamChunk(e, ApiFormats.OpenAITranscription));
                case ApiFormats.OpenAITranslation:
                    return new Streams.MapStream<StreamEvent, LlmResponse>(
                        stream, e => TransformTranscriptionStreamChunk(e, ApiFormats.OpenAITranslation));
            }
        }

        var indexer = new ToolCallIndexer();
        return new Streams.MapStream<StreamEvent, LlmResponse>(
            stream,
            streamEvent =>
            {
                var response = TransformStreamChunk(streamEvent);
                if (response is not null
                    && !ReferenceEquals(response, LlmResponse.DoneResponse)
                    && streamEvent is not null)
                {
                    indexer.ApplyToResponse(streamEvent.Data, response);
                }

                return response;
            });
    }

    /// <summary>
    /// Port of TransformStream for chat completions: one SSE event in, one
    /// unified response out. Returns null for events the reference filters
    /// (<c>streams.NoNil</c>).
    /// </summary>
    public LlmResponse? TransformStreamChunk(StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            throw new InvalidOperationException("stream event is nil");
        }

        if (StartsWith(streamEvent.Data, "[DONE]"))
        {
            return LlmResponse.Done();
        }

        // Some providers emit structured error events in-stream (SSE `event: error`,
        // or payloads like {"event":"error","data":{...}}).
        var streamErr = ParseStreamErrorEvent(streamEvent);
        if (streamErr is not null)
        {
            throw streamErr;
        }

        // Synthetic response, mirroring the reference's reuse of TransformResponse.
        var resp = TransformResponse(new HttpResponseData { Body = streamEvent.Data });

        // Normalize empty finish_reason to nil: some providers send "" on every
        // chunk, which is semantically "not finished".
        foreach (var choice in resp.Choices)
        {
            if (choice.FinishReason is not null && choice.FinishReason.Length == 0)
            {
                choice.FinishReason = null;
            }
        }

        // Skip non-standard events with an explicit empty choices array and no
        // usage (e.g. inference-cost). The OpenAI include_usage terminal chunk is
        // choices:[] with a non-null usage, and must be kept.
        using var doc = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(streamEvent.Data) ?? streamEvent.Data);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() == 0)
        {
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
        }

        return resp;
    }

    /// <summary>Port of parseStreamErrorEvent.</summary>
    public static ResponseError? ParseStreamErrorEvent(StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            return null;
        }

        // A provider may emit `event: error` with an empty payload.
        if (streamEvent.Type == "error" && streamEvent.Data.Length == 0)
        {
            return new ResponseError
            {
                Detail = new ErrorDetail { Message = "stream error", Type = "stream_error" },
            };
        }

        if (streamEvent.Data.Length == 0)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(streamEvent.Data) ?? streamEvent.Data);
        var root = doc.RootElement;

        // Prefer the explicit SSE event type when present.
        if (streamEvent.Type == "error" || GJsonString(Get(root, "event")) == "error")
        {
            // Zai-style: {"error":{...},"request_id":"..."}; also tolerate the
            // wrapped form {"event":"error","data":{"error":{...}}}.
            var errObj = Get(root, "error");
            if (errObj is null && Get(root, "data") is { } wrapped)
            {
                errObj = Get(wrapped, "error");
            }

            var detail = new ErrorDetail
            {
                Message = GJsonString(Get(errObj, "message")),
                Type = GJsonString(Get(errObj, "type")),
                Code = NullIfEmpty(GJsonString(Get(errObj, "code"))),
                Param = NullIfEmpty(GJsonString(Get(errObj, "param"))),
            };

            if (detail.Message.Length == 0 && errObj is not null)
            {
                detail.Message = GJsonString(errObj);
            }

            if (detail.Message.Length == 0)
            {
                detail.Message = "stream error";
            }

            var requestId = NullIfEmpty(GJsonString(Get(root, "request_id")))
                ?? NullIfEmpty(GJsonString(Get(Get(root, "data"), "request_id")))
                ?? NullIfEmpty(GJsonString(Get(errObj, "request_id")));
            detail.RequestId = requestId;

            return new ResponseError { Detail = detail };
        }

        // OpenAI-style: {"error":{...}} or {"error":"..."}
        var errorProp = Get(root, "error");
        if (errorProp is null)
        {
            return null;
        }

        var openAiDetail = new ErrorDetail
        {
            Message = GJsonString(Get(errorProp, "message")),
            Type = GJsonString(Get(errorProp, "type")),
            Code = NullIfEmpty(GJsonString(Get(errorProp, "code"))),
            Param = NullIfEmpty(GJsonString(Get(errorProp, "param"))),
        };

        if (openAiDetail.Message.Length == 0)
        {
            openAiDetail.Message = GJsonString(errorProp);
        }

        openAiDetail.RequestId = NullIfEmpty(GJsonString(Get(root, "request_id")))
            ?? NullIfEmpty(GJsonString(Get(errorProp, "request_id")));

        return new ResponseError { Detail = openAiDetail };
    }

    private static bool StartsWith(byte[] data, string prefix)
    {
        if (data.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != (byte)prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>gjson <c>Get</c>: a missing path or a non-object parent yields no value.</summary>
    private static JsonElement? Get(JsonElement? root, string name)
        => root is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty(name, out var value)
            ? value
            : null;

    /// <summary>gjson <c>Result.String()</c>.</summary>
    private static string GJsonString(JsonElement? element)
    {
        if (element is not { } e)
        {
            return string.Empty;
        }

        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => e.GetRawText(),
        };
    }
}
