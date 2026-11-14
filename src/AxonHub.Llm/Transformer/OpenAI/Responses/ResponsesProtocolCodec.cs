using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of llm/transformer/openai/responses/protocol_codec.go: the shared codec
/// that changes only the Responses transport envelope, keeping provider item
/// IDs, unknown fields and raw item bytes verbatim.
/// </summary>
/// <remarks>
/// The Go implementation decodes into <c>map[string]json.RawMessage</c> and
/// small structs. Those decodes have observable failure modes (Go's
/// <c>UnmarshalTypeError</c> wording, first-error wins while decoding
/// continues, case-insensitive field folding), so the port walks the bytes with
/// <see cref="Utf8JsonReader"/> instead of System.Text.Json's differently
/// worded errors.
/// </remarks>
public static class ResponsesProtocolCodec
{
    private const string MapRawMessageType = "map[string]json.RawMessage";
    private const string ProtocolResponseBodyType = "responses.protocolResponseBody";

    private const string RequestEnvelopeType =
        "struct { Model string \"json:\\\"model\\\"\"; Type string \"json:\\\"type\\\"\"; Generate *bool \"json:\\\"generate\\\"\" }";

    private const string EventEnvelopeType =
        "struct { Type string \"json:\\\"type\\\"\"; Response *responses.protocolResponseBody \"json:\\\"response\\\"\" }";

    /// <summary>
    /// Port of IsResponsesFormat: compact and native Images are excluded from
    /// transport adaptation.
    /// </summary>
    public static bool IsResponsesFormat(string? format)
        => string.Equals(format, ApiFormats.OpenAIResponse, StringComparison.Ordinal)
           || string.Equals(format, ApiFormats.OpenAIResponseWebSocket, StringComparison.Ordinal);

    /// <summary>
    /// Port of AdaptRequestBody: preserves Responses fields without a
    /// generic-model roundtrip. Accepts normalized WS bodies or
    /// <c>response.create</c> frames; <c>stream_id</c> is never forwarded
    /// upstream.
    /// </summary>
    public static byte[] AdaptRequestBody(byte[]? body, string? source, string? target)
    {
        CheckResponsesFormats(source, target);

        var data = body ?? [];
        if (GoJsonSyntax.Validate(data) is { } syntax)
        {
            throw TransformerErrors.InvalidRequest("invalid Responses object: " + syntax);
        }

        // Go's decoder repairs invalid UTF-8 while unquoting; the reader here
        // rejects it, so parse the repaired copy (difference C008).
        var parsed = GoJsonUtf8.SanitizeStringLiterals(data) ?? data;
        var fields = DecodeRawObject(parsed);
        var envelope = DecodeRequestEnvelope(parsed);

        if (envelope.Model.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (envelope.Type.Length > 0
            && (!string.Equals(source, ApiFormats.OpenAIResponseWebSocket, StringComparison.Ordinal)
                || envelope.Type != "response.create"))
        {
            throw TransformerErrors.UnsupportedConversion("response.create is required");
        }

        if (string.Equals(target, ApiFormats.OpenAIResponse, StringComparison.Ordinal)
            && envelope.Generate == false)
        {
            throw TransformerErrors.UnsupportedConversion("HTTP cannot perform WebSocket warmup");
        }

        fields.Remove("type");
        fields.Remove("stream_id");
        fields.Remove("generate");

        if (string.Equals(target, ApiFormats.OpenAIResponseWebSocket, StringComparison.Ordinal))
        {
            fields["type"] = "\"response.create\""u8.ToArray();
            if (envelope.Generate == false)
            {
                fields["generate"] = "false"u8.ToArray();
            }
        }

        return MarshalRawObject(fields);
    }

    /// <summary>
    /// Port of AdaptResponseBody: keeps provider response and item IDs and
    /// unknown fields verbatim. A valid object is not evidence of complete or
    /// recoverable continuation history.
    /// </summary>
    public static byte[] AdaptResponseBody(byte[]? body, string? source, string? target)
    {
        CheckResponsesFormats(source, target);

        var data = body ?? [];
        if (GoJsonSyntax.Validate(data) is { } syntax)
        {
            throw TransformerErrors.InvalidResponse("invalid Responses response: " + syntax);
        }

        var parsed = GoJsonUtf8.SanitizeStringLiterals(data) ?? data;
        var response = DecodeProtocolResponseBody(parsed, fieldPrefix: null, out var typeError);
        if (typeError is not null)
        {
            throw TransformerErrors.InvalidResponse("invalid Responses response: " + typeError);
        }

        if (response.Id.Length == 0)
        {
            throw TransformerErrors.InvalidResponse("provider response ID is required");
        }

        return (byte[])data.Clone();
    }

    /// <summary>
    /// Port of AdaptStreamEvent: adapts decoded SSE/WS events without
    /// rebuilding provider items. The executor/handler remains responsible for
    /// SSE framing and WS lane envelopes.
    /// </summary>
    public static StreamEvent? AdaptStreamEvent(StreamEvent? @event, string? source, string? target)
    {
        CheckResponsesFormats(source, target);

        if (@event is null)
        {
            throw TransformerErrors.InvalidResponse("event is nil");
        }

        var data = @event.Data ?? [];
        if (GoJsonSyntax.Validate(data) is { } syntax)
        {
            throw TransformerErrors.InvalidResponse("invalid Responses event: " + syntax);
        }

        var parsed = GoJsonUtf8.SanitizeStringLiterals(data) ?? data;
        var rootKind = GoStructJson.RootKind(parsed);
        if (rootKind is not null && rootKind != "null")
        {
            throw TransformerErrors.InvalidResponse(
                "invalid Responses event: " + GoStructJson.TypeMismatch(rootKind, EventEnvelopeType));
        }

        var type = string.Empty;
        ProtocolResponseBody? response = null;
        string? typeError = null;

        if (rootKind is null)
        {
            var reader = new Utf8JsonReader(parsed, ReaderOptions);
            _ = reader.Read(); // StartObject

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString() ?? string.Empty;
                if (!reader.Read())
                {
                    break;
                }

                if (MatchesField(key, "type"))
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        type = reader.GetString() ?? string.Empty;
                        continue;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        type = string.Empty;
                        continue;
                    }

                    typeError ??= StructFieldError(
                        string.Empty, "type", KindWord(ref reader), "string");
                    SkipContainer(ref reader);
                    continue;
                }

                if (MatchesField(key, "response"))
                {
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        response = null;
                        continue;
                    }

                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        var start = checked((int)reader.TokenStartIndex);
                        reader.Skip();
                        var end = checked((int)reader.BytesConsumed);
                        response = DecodeProtocolResponseBody(
                            parsed.AsSpan(start, end - start), "response", out var nestedError);
                        typeError ??= nestedError;
                        continue;
                    }

                    typeError ??= StructFieldError(
                        string.Empty,
                        "response",
                        KindWord(ref reader),
                        ProtocolResponseBodyType);
                    SkipContainer(ref reader);
                    continue;
                }

                SkipContainer(ref reader);
            }
        }

        if (typeError is not null)
        {
            throw TransformerErrors.InvalidResponse("invalid Responses event: " + typeError);
        }

        if (type.Length == 0
            || (@event.Type.Length > 0 && !string.Equals(@event.Type, type, StringComparison.Ordinal)))
        {
            throw TransformerErrors.InvalidResponse("event type is missing or inconsistent");
        }

        if (type == "response.completed"
            && (response is null || response.Id.Length == 0 || response.Status != "completed"))
        {
            throw TransformerErrors.InvalidResponse(
                "completed event requires a completed provider response");
        }

        return new StreamEvent
        {
            LastEventId = @event.LastEventId,
            Type = type,
            Data = (byte[])data.Clone(),
            CleanEofCompletionEvidence = @event.CleanEofCompletionEvidence,
            Size = @event.Size,
            Headers = CloneHeaders(@event.Headers),
        };
    }

    private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 10_000 };

    private static void CheckResponsesFormats(string? source, string? target)
    {
        if (!IsResponsesFormat(source) || !IsResponsesFormat(target))
        {
            throw TransformerErrors.UnsupportedConversion("Responses transport formats required");
        }
    }

    /// <summary>
    /// Port of <c>json.Unmarshal(body, &amp;map[string]json.RawMessage)</c>: a
    /// null root is an empty map, any other non-object root is the Go type
    /// error, and duplicate keys keep the last value.
    /// </summary>
    private static Dictionary<string, byte[]> DecodeRawObject(ReadOnlySpan<byte> data)
    {
        var fields = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var rootKind = GoStructJson.RootKind(data);
        if (rootKind == "null")
        {
            return fields;
        }

        if (rootKind is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "invalid Responses object: " + GoStructJson.TypeMismatch(rootKind, MapRawMessageType));
        }

        var reader = new Utf8JsonReader(data, ReaderOptions);
        _ = reader.Read(); // StartObject

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString() ?? string.Empty;
            if (!reader.Read())
            {
                break;
            }

            var start = checked((int)reader.TokenStartIndex);
            SkipContainer(ref reader);
            var end = checked((int)reader.BytesConsumed);
            fields[key] = data[start..end].ToArray();
        }

        return fields;
    }

    private sealed class RequestEnvelope
    {
        public string Model = string.Empty;

        public string Type = string.Empty;

        public bool? Generate;
    }

    /// <summary>
    /// Port of the anonymous request envelope decode. Go saves the first type
    /// error, keeps decoding the remaining keys, and reports the saved error
    /// after the whole object was walked.
    /// </summary>
    private static RequestEnvelope DecodeRequestEnvelope(ReadOnlySpan<byte> data)
    {
        var envelope = new RequestEnvelope();
        var rootKind = GoStructJson.RootKind(data);
        if (rootKind == "null")
        {
            return envelope;
        }

        if (rootKind is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "invalid Responses fields: " + GoStructJson.TypeMismatch(rootKind, RequestEnvelopeType));
        }

        string? typeError = null;
        var reader = new Utf8JsonReader(data, ReaderOptions);
        _ = reader.Read(); // StartObject

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString() ?? string.Empty;
            if (!reader.Read())
            {
                break;
            }

            var isModel = MatchesField(key, "model");
            var isType = !isModel && MatchesField(key, "type");
            var isGenerate = !isModel && !isType && MatchesField(key, "generate");

            if (!isModel && !isType && !isGenerate)
            {
                SkipContainer(ref reader);
                continue;
            }

            if (isGenerate)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.True:
                        envelope.Generate = true;
                        break;
                    case JsonTokenType.False:
                        envelope.Generate = false;
                        break;
                    case JsonTokenType.Null:
                        envelope.Generate = null;
                        break;
                    default:
                        typeError ??= StructFieldError(
                            string.Empty, "generate", KindWord(ref reader), "bool");
                        SkipContainer(ref reader);
                        break;
                }

                continue;
            }

            var field = isModel ? "model" : "type";
            if (reader.TokenType == JsonTokenType.String)
            {
                var value = reader.GetString() ?? string.Empty;
                if (isModel)
                {
                    envelope.Model = value;
                }
                else
                {
                    envelope.Type = value;
                }
            }
            else if (reader.TokenType == JsonTokenType.Null)
            {
                if (isModel)
                {
                    envelope.Model = string.Empty;
                }
                else
                {
                    envelope.Type = string.Empty;
                }
            }
            else
            {
                typeError ??= StructFieldError(
                    string.Empty, field, KindWord(ref reader), "string");
                SkipContainer(ref reader);
            }
        }

        if (typeError is not null)
        {
            throw TransformerErrors.InvalidRequest("invalid Responses fields: " + typeError);
        }

        return envelope;
    }

    private sealed class ProtocolResponseBody
    {
        public string Id = string.Empty;

        public string Status = string.Empty;

        public bool HasOutput;
    }

    /// <summary>
    /// Port of <c>json.Unmarshal(..., &amp;protocolResponseBody)</c>. The field
    /// prefix carries the enclosing field path for nested decodes, matching
    /// Go's <c>protocolResponseBody.response.id</c> error text.
    /// </summary>
    private static ProtocolResponseBody DecodeProtocolResponseBody(
        ReadOnlySpan<byte> data,
        string? fieldPrefix,
        out string? typeError)
    {
        var body = new ProtocolResponseBody();
        typeError = null;

        var rootKind = GoStructJson.RootKind(data);
        if (rootKind == "null")
        {
            return body;
        }

        if (rootKind is not null)
        {
            typeError = GoStructJson.TypeMismatch(rootKind, ProtocolResponseBodyType);
            return body;
        }

        var reader = new Utf8JsonReader(data, ReaderOptions);
        _ = reader.Read(); // StartObject

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString() ?? string.Empty;
            if (!reader.Read())
            {
                break;
            }

            if (MatchesField(key, "id") || MatchesField(key, "status"))
            {
                var field = MatchesField(key, "id") ? "id" : "status";
                var path = fieldPrefix is null ? field : fieldPrefix + "." + field;

                if (reader.TokenType == JsonTokenType.String)
                {
                    var value = reader.GetString() ?? string.Empty;
                    if (field == "id")
                    {
                        body.Id = value;
                    }
                    else
                    {
                        body.Status = value;
                    }
                }
                else if (reader.TokenType == JsonTokenType.Null)
                {
                    if (field == "id")
                    {
                        body.Id = string.Empty;
                    }
                    else
                    {
                        body.Status = string.Empty;
                    }
                }
                else
                {
                    typeError ??= StructFieldError(
                        "protocolResponseBody", path, KindWord(ref reader), "string");
                    SkipContainer(ref reader);
                }

                continue;
            }

            if (MatchesField(key, "output"))
            {
                var path = fieldPrefix is null ? "output" : fieldPrefix + ".output";
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    body.HasOutput = true;
                    reader.Skip();
                }
                else if (reader.TokenType == JsonTokenType.Null)
                {
                    body.HasOutput = false;
                }
                else
                {
                    typeError ??= StructFieldError(
                        "protocolResponseBody", path, KindWord(ref reader), "[]json.RawMessage");
                    SkipContainer(ref reader);
                }

                continue;
            }

            SkipContainer(ref reader);
        }

        return body;
    }

    /// <summary>
    /// Port of <c>json.Marshal(map[string]json.RawMessage)</c>: keys sort in
    /// ordinal order and every raw value is compacted with Go's HTML escaping.
    /// </summary>
    private static byte[] MarshalRawObject(Dictionary<string, byte[]> fields)
    {
        var keys = fields.Keys.ToArray();
        Array.Sort(keys, StringComparer.Ordinal);

        var output = new List<byte>(64);
        output.Add((byte)'{');
        for (var index = 0; index < keys.Length; index++)
        {
            if (index > 0)
            {
                output.Add((byte)',');
            }

            output.AddRange(GoJson.Marshal(keys[index]));
            output.Add((byte)':');
            output.AddRange(GoJson.Compact(fields[keys[index]]));
        }

        output.Add((byte)'}');
        return output.ToArray();
    }

    /// <summary>
    /// Go struct field matching: the exact tag name first, then the folded
    /// (case-insensitive) name, so <c>Model</c> and <c>MODEL</c> both fill
    /// <c>model</c> while unknown keys are ignored.
    /// </summary>
    private static bool MatchesField(string key, string tag)
        => string.Equals(key, tag, StringComparison.Ordinal)
           || string.Equals(FoldFieldName(key), tag, StringComparison.Ordinal);

    /// <summary>Port of encoding/json's <c>foldName</c> for the ASCII tags used here.</summary>
    private static string FoldFieldName(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            builder.Append(c switch
            {
                'ſ' => 's', // LATIN SMALL LETTER LONG S
                'K' => 'k', // KELVIN SIGN
                _ => char.ToLowerInvariant(c),
            });
        }

        return builder.ToString();
    }

    private static string KindWord(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => "string",
        JsonTokenType.Number => "number",
        JsonTokenType.True or JsonTokenType.False => "bool",
        JsonTokenType.StartObject => "object",
        JsonTokenType.StartArray => "array",
        JsonTokenType.Null => "null",
        _ => "value",
    };

    private static string StructFieldError(
        string structName,
        string field,
        string kind,
        string goType)
        => "json: cannot unmarshal " + kind + " into Go struct field " + structName + "."
           + field + " of type " + goType;

    private static void SkipContainer(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

    private static Dictionary<string, List<string>>? CloneHeaders(
        Dictionary<string, List<string>>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        var clone = new Dictionary<string, List<string>>(headers.Count, StringComparer.Ordinal);
        foreach (var (key, values) in headers)
        {
            clone[key] = [.. values];
        }

        return clone;
    }
}
