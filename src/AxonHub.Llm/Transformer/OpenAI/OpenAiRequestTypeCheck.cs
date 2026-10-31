using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of encoding/json's field-level type errors for the OpenAI request wire
/// model (difference ledger C003).
/// </summary>
/// <remarks>
/// Go saves the first type mismatch in document order and keeps decoding, but a
/// failing <c>UnmarshalJSON</c> aborts the whole decode and overrides the saved
/// error. This walker mirrors both rules so <c>failed to decode openai
/// request: ...</c> keeps Go's wording. Syntax and root-value errors are
/// reported by <see cref="GoJsonSyntax"/> and <see cref="GoStructJson"/> before
/// this runs.
/// </remarks>
public static class OpenAiRequestTypeCheck
{
    /// <summary>
    /// Returns Go's decode error text for the first type mismatch, or
    /// <see langword="null"/> when every field decodes.
    /// </summary>
    public static string? Validate(ReadOnlySpan<byte> utf8Json)
        => ValidateAgainst(RequestSchema, utf8Json);

    /// <summary>
    /// Port of the type errors <c>json.Unmarshal</c> reports for
    /// openai.ImageGenerationRequest (image_inbound.go:56-79).
    /// </summary>
    public static string? ValidateImageGeneration(ReadOnlySpan<byte> utf8Json)
        => ValidateAgainst(ImageGenerationRequestSchema, utf8Json);

    /// <summary>
    /// Port of the type errors <c>json.Unmarshal</c> reports for
    /// openai.ImageEditJSONRequest (image_inbound.go:361-378).
    /// </summary>
    public static string? ValidateImageEdit(ReadOnlySpan<byte> utf8Json)
        => ValidateAgainst(ImageEditJsonRequestSchema, utf8Json);

    private static string? ValidateAgainst(Schema schema, ReadOnlySpan<byte> utf8Json)
    {
        var sanitized = GoJsonUtf8.SanitizeStringLiterals(utf8Json);
        ReadOnlySpan<byte> data = sanitized is null ? utf8Json : sanitized;

        try
        {
            var reader = new Utf8JsonReader(data, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            return new Check().CheckRoot(schema, ref reader);
        }
        catch (JsonException)
        {
            // The syntax pass accepted the document but the reader cannot walk
            // it (for example it exceeds the reader's depth limit); leave the
            // report to the real deserializer.
            return null;
        }
    }

    private sealed class Check
    {
        private string? _firstError;

        public string? CheckRoot(Schema schema, ref Utf8JsonReader reader)
        {
            var hard = CheckObject(schema, path: string.Empty, ref reader);
            return hard ?? _firstError;
        }

        /// <summary>
        /// Walks an object in document order. Returns a hard
        /// <c>UnmarshalJSON</c> error, which Go lets override any earlier type
        /// error, or <see langword="null"/>.
        /// </summary>
        private string? CheckObject(Schema schema, string path, ref Utf8JsonReader reader)
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return null;
                }

                var name = reader.GetString()!;
                if (!reader.Read())
                {
                    return null;
                }

                var field = schema.Find(name);
                if (field is null)
                {
                    // Go skips unknown fields without decoding them.
                    SkipValue(ref reader);
                    continue;
                }

                var fieldPath = path.Length == 0 ? field.Name : path + "." + field.Name;
                var hard = CheckValue(ref reader, field.Type, schema.GoName, fieldPath);
                if (hard is not null)
                {
                    return hard;
                }
            }

            return null;
        }

        /// <summary>
        /// Checks one value against its Go type. The reader must be positioned
        /// on the value's first token and is left on its last token.
        /// </summary>
        private string? CheckValue(ref Utf8JsonReader reader, GoType type, string structName, string fieldPath)
        {
            // null leaves the Go zero value (or nil pointer) in place.
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            switch (type.Kind)
            {
                case GoKind.String:
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        SaveMismatch(ref reader, structName, fieldPath, type.Name);
                    }

                    return null;

                case GoKind.Bool:
                    if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
                    {
                        SaveMismatch(ref reader, structName, fieldPath, type.Name);
                    }

                    return null;

                case GoKind.Int64:
                case GoKind.Int:
                    if (reader.TokenType != JsonTokenType.Number)
                    {
                        SaveMismatch(ref reader, structName, fieldPath, type.Name);
                    }
                    else if (!FitsInt64(ref reader))
                    {
                        SaveNumberMismatch(ref reader, structName, fieldPath, type.Name);
                    }

                    return null;

                case GoKind.Double:
                    if (reader.TokenType != JsonTokenType.Number)
                    {
                        SaveMismatch(ref reader, structName, fieldPath, type.Name);
                    }
                    else if (!FitsFloat64(ref reader))
                    {
                        SaveNumberMismatch(ref reader, structName, fieldPath, type.Name);
                    }

                    return null;

                case GoKind.Raw:
                    // json.RawMessage accepts any value, including null.
                    SkipValue(ref reader);
                    return null;

                case GoKind.Struct:
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        return CheckObject(type.Struct!, fieldPath, ref reader);
                    }

                    SaveMismatch(ref reader, structName, fieldPath, type.Name);
                    return null;

                case GoKind.Slice:
                    return CheckArray(ref reader, type, structName, fieldPath);

                case GoKind.Map:
                    return CheckMap(ref reader, type, structName, fieldPath);

                case GoKind.Stop:
                    return StopAttempt(ref reader) ? null : "invalid stop type";

                case GoKind.Content:
                    return ContentAttempt(ref reader) ? null : "invalid content type";

                case GoKind.ToolChoice:
                    return ToolChoiceAttempt(ref reader) ? null : "invalid tool choice type";

                default:
                    return null;
            }
        }

        private string? CheckArray(ref Utf8JsonReader reader, GoType type, string structName, string fieldPath)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                SaveMismatch(ref reader, structName, fieldPath, type.Name);
                return null;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                // An element mismatch reports the enclosing struct field and
                // the element's Go type, exactly like Go's array decoder.
                var hard = CheckValue(ref reader, type.Element!, structName, fieldPath);
                if (hard is not null)
                {
                    return hard;
                }
            }

            return null;
        }

        private string? CheckMap(ref Utf8JsonReader reader, GoType type, string structName, string fieldPath)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                SaveMismatch(ref reader, structName, fieldPath, type.Name);
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!reader.Read())
                {
                    return null;
                }

                // Map keys never join the Go field path; a value mismatch
                // reports the map field and the value's Go type.
                var hard = CheckValue(ref reader, type.Value!, structName, fieldPath);
                if (hard is not null)
                {
                    return hard;
                }
            }

            return null;
        }

        private void SaveMismatch(ref Utf8JsonReader reader, string structName, string fieldPath, string goType)
        {
            Save(structName, fieldPath, KindWord(ref reader), goType);
            SkipValue(ref reader);
        }

        private void SaveNumberMismatch(ref Utf8JsonReader reader, string structName, string fieldPath, string goType)
        {
            // Go quotes the literal for number conversion failures.
            Save(structName, fieldPath, "number " + Literal(ref reader), goType);
        }

        private void Save(string structName, string fieldPath, string value, string goType)
        {
            _firstError ??= "json: cannot unmarshal " + value
                + " into Go struct field " + structName + "." + fieldPath
                + " of type " + goType;
        }
    }

    /// <summary>Go: json.Unmarshal(data, &amp;s) for a string or []string.</summary>
    private static bool StopAttempt(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return true;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return false;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            // Go decodes a null element as the zero string.
            if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Go: try string, then []MessageContentPart; any part failure is folded
    /// into the sentinel error.
    /// </summary>
    private static bool ContentAttempt(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return true;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return false;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartObject
                || !ObjectMatches(ref reader, ContentPartSchema))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Go: try string, then the allowed_tools envelope (kept only when its
    /// type and nested object are present), then a plain named choice.
    /// </summary>
    private static bool ToolChoiceAttempt(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return true;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        // Attempt 2 runs on a copy so a failed envelope leaves the original
        // reader untouched for the named-choice attempt.
        var copy = reader;
        if (AllowedToolsEnvelopeMatches(ref copy))
        {
            reader.Skip();
            return true;
        }

        return ObjectMatches(ref reader, NamedToolChoiceSchema);
    }

    private static bool AllowedToolsEnvelopeMatches(ref Utf8JsonReader reader)
    {
        var typeOk = true;
        string? type = null;
        var allowedPresent = false;
        var allowedIsObject = false;
        var allowedValid = true;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return typeOk && allowedValid
                    && type == "allowed_tools"
                    && allowedPresent
                    && allowedIsObject;
            }

            var name = reader.GetString()!;
            if (!reader.Read())
            {
                return false;
            }

            if (string.Equals(name, "type", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    type = reader.GetString();
                }
                else if (reader.TokenType != JsonTokenType.Null)
                {
                    // Go fails the whole envelope decode on a non-string type.
                    typeOk = false;
                    SkipValue(ref reader);
                }
            }
            else if (string.Equals(name, "allowed_tools", StringComparison.OrdinalIgnoreCase))
            {
                // A later null clears the pointer, so only the last value counts.
                allowedPresent = true;
                allowedIsObject = reader.TokenType == JsonTokenType.StartObject;
                if (allowedIsObject)
                {
                    allowedValid &= ObjectMatches(ref reader, AllowedToolsSchema);
                }
                else
                {
                    allowedValid &= reader.TokenType == JsonTokenType.Null;
                    SkipValue(ref reader);
                }
            }
            else
            {
                SkipValue(ref reader);
            }
        }

        return false;
    }

    /// <summary>
    /// Validation-only object walk used by the three UnmarshalJSON attempts:
    /// it mirrors json.Unmarshal (unknown fields ignored, null accepted,
    /// number conversion rules) without recording errors.
    /// </summary>
    private static bool ObjectMatches(ref Utf8JsonReader reader, Schema schema)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return true;
            }

            var name = reader.GetString()!;
            if (!reader.Read())
            {
                return false;
            }

            var field = schema.Find(name);
            if (field is null)
            {
                SkipValue(ref reader);
                continue;
            }

            if (!ValueMatches(ref reader, field.Type))
            {
                return false;
            }
        }

        return false;
    }

    private static bool ValueMatches(ref Utf8JsonReader reader, GoType type)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return true;
        }

        switch (type.Kind)
        {
            case GoKind.String:
                return reader.TokenType == JsonTokenType.String;

            case GoKind.Bool:
                return reader.TokenType is JsonTokenType.True or JsonTokenType.False;

            case GoKind.Int64:
            case GoKind.Int:
                return reader.TokenType == JsonTokenType.Number && FitsInt64(ref reader);

            case GoKind.Double:
                return reader.TokenType == JsonTokenType.Number && FitsFloat64(ref reader);

            case GoKind.Raw:
                SkipValue(ref reader);
                return true;

            case GoKind.Struct:
                return reader.TokenType == JsonTokenType.StartObject
                    && ObjectMatches(ref reader, type.Struct!);

            case GoKind.Slice:
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    return false;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (!ValueMatches(ref reader, type.Element!))
                    {
                        return false;
                    }
                }

                return true;

            case GoKind.Map:
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    return false;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (!reader.Read() || !ValueMatches(ref reader, type.Value!))
                    {
                        return false;
                    }
                }

                return true;

            default:
                return false;
        }
    }

    private static string KindWord(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.StartObject => "object",
        JsonTokenType.StartArray => "array",
        JsonTokenType.String => "string",
        JsonTokenType.True or JsonTokenType.False => "bool",
        _ => "number",
    };

    private static string Literal(ref Utf8JsonReader reader)
        => Encoding.UTF8.GetString(reader.ValueSpan);

    /// <summary>Go: strconv.ParseInt(literal, 10, 64) for int and int64.</summary>
    private static bool FitsInt64(ref Utf8JsonReader reader)
        => long.TryParse(
            Literal(ref reader),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out _);

    /// <summary>
    /// Go: strconv.ParseFloat(literal, 64). Overflow reports ErrRange and is a
    /// type error; underflow silently rounds to zero and is accepted.
    /// </summary>
    private static bool FitsFloat64(ref Utf8JsonReader reader)
        => double.TryParse(
                Literal(ref reader),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            && !double.IsInfinity(value);

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

    private static GoType StructOf(Schema schema)
        => new(GoKind.Struct, "openai." + schema.GoName, Struct: schema);

    private static GoType SliceOf(GoType element)
        => new(GoKind.Slice, "[]" + element.Name, Element: element);

    private static GoType MapOf(GoType value)
        => new(GoKind.Map, "map[string]" + value.Name, Value: value);

    private sealed record GoField(string Name, GoType Type);

    private sealed record GoType(GoKind Kind, string Name, Schema? Struct = null, GoType? Element = null, GoType? Value = null);

    private sealed class Schema
    {
        private readonly Dictionary<string, GoField> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GoField> _fold = new(StringComparer.OrdinalIgnoreCase);

        public Schema(string goName, params GoField[] fields)
        {
            GoName = goName;
            foreach (var field in fields)
            {
                _exact.Add(field.Name, field);
                _fold.TryAdd(field.Name, field);
            }
        }

        public string GoName { get; }

        /// <summary>Go matches the tag exactly first, then case-insensitively.</summary>
        public GoField? Find(string jsonName)
        {
            if (_exact.TryGetValue(jsonName, out var field))
            {
                return field;
            }

            return _fold.TryGetValue(jsonName, out field) ? field : null;
        }
    }

    private enum GoKind
    {
        String,
        Bool,
        Int64,
        Int,
        Double,
        Raw,
        Struct,
        Slice,
        Map,
        Stop,
        Content,
        ToolChoice,
    }

    // Leaf Go types.
    private static readonly GoType StringType = new(GoKind.String, "string");
    private static readonly GoType BoolType = new(GoKind.Bool, "bool");
    private static readonly GoType Int64Type = new(GoKind.Int64, "int64");
    private static readonly GoType IntType = new(GoKind.Int, "int");
    private static readonly GoType DoubleType = new(GoKind.Double, "float64");
    private static readonly GoType RawType = new(GoKind.Raw, "json.RawMessage");
    private static readonly GoType StopType = new(GoKind.Stop, "openai.Stop");
    private static readonly GoType ContentType = new(GoKind.Content, "openai.MessageContent");
    private static readonly GoType ToolChoiceType = new(GoKind.ToolChoice, "openai.ToolChoice");

    // Schemas, declared leaf-first because static initializers run in order.
    private static readonly Schema ThinkingSchema = new(
        "Thinking",
        new GoField("type", StringType));

    private static readonly Schema StreamOptionsSchema = new(
        "StreamOptions",
        new GoField("include_usage", BoolType));

    private static readonly Schema ToolFunctionSchema = new(
        "ToolFunction",
        new GoField("name", StringType));

    private static readonly Schema UrlCitationSchema = new(
        "URLCitation",
        new GoField("url", StringType),
        new GoField("title", StringType));

    private static readonly Schema FileSchema = new(
        "File",
        new GoField("file_data", StringType),
        new GoField("file_id", StringType),
        new GoField("filename", StringType));

    private static readonly Schema ImageUrlSchema = new(
        "ImageURL",
        new GoField("url", StringType),
        new GoField("detail", StringType));

    private static readonly Schema VideoUrlSchema = new(
        "VideoURL",
        new GoField("url", StringType));

    private static readonly Schema InputAudioSchema = new(
        "InputAudio",
        new GoField("format", StringType),
        new GoField("data", StringType));

    private static readonly Schema OutputAudioSchema = new(
        "OutputAudio",
        new GoField("id", StringType),
        new GoField("data", StringType),
        new GoField("expires_at", Int64Type),
        new GoField("transcript", StringType));

    private static readonly Schema FunctionCallSchema = new(
        "FunctionCall",
        new GoField("name", StringType),
        new GoField("arguments", StringType));

    private static readonly Schema ToolCallGoogleExtraContentSchema = new(
        "ToolCallGoogleExtraContent",
        new GoField("thought_signature", StringType));

    private static readonly Schema ToolCallExtraContentSchema = new(
        "ToolCallExtraContent",
        new GoField("google", StructOf(ToolCallGoogleExtraContentSchema)));

    private static readonly Schema ToolCallExtraFieldsSchema = new(
        "ToolCallExtraFields",
        new GoField("extra_content", StructOf(ToolCallExtraContentSchema)));

    private static readonly Schema AnnotationSchema = new(
        "Annotation",
        new GoField("type", StringType),
        new GoField("start_index", Int64Type),
        new GoField("end_index", Int64Type),
        new GoField("url_citation", StructOf(UrlCitationSchema)));

    private static readonly Schema FunctionSchema = new(
        "Function",
        new GoField("name", StringType),
        new GoField("description", StringType),
        new GoField("parameters", RawType),
        new GoField("strict", BoolType));

    private static readonly Schema NamedToolChoiceSchema = new(
        "NamedToolChoice",
        new GoField("type", StringType),
        new GoField("function", StructOf(ToolFunctionSchema)));

    private static readonly Schema AllowedToolsSchema = new(
        "AllowedTools",
        new GoField("mode", StringType),
        new GoField("tools", SliceOf(StructOf(NamedToolChoiceSchema))));

    private static readonly Schema ContentPartSchema = new(
        "MessageContentPart",
        new GoField("type", StringType),
        new GoField("text", StringType),
        new GoField("image_url", StructOf(ImageUrlSchema)),
        new GoField("video_url", StructOf(VideoUrlSchema)),
        new GoField("input_audio", StructOf(InputAudioSchema)),
        new GoField("file", StructOf(FileSchema)));

    private static readonly Schema ToolSchema = new(
        "Tool",
        new GoField("type", StringType),
        new GoField("function", StructOf(FunctionSchema)));

    private static readonly Schema ToolCallSchema = new(
        "ToolCall",
        new GoField("id", StringType),
        new GoField("type", StringType),
        new GoField("function", StructOf(FunctionCallSchema)),
        new GoField("index", IntType),
        new GoField("extra_content", StructOf(ToolCallExtraContentSchema)),
        new GoField("extra_fields", StructOf(ToolCallExtraFieldsSchema)));

    private static readonly Schema MessageSchema = new(
        "Message",
        new GoField("role", StringType),
        new GoField("content", ContentType),
        new GoField("name", StringType),
        new GoField("refusal", StringType),
        new GoField("tool_call_id", StringType),
        new GoField("tool_calls", SliceOf(StructOf(ToolCallSchema))),
        new GoField("reasoning_content", StringType),
        new GoField("reasoning", StringType),
        new GoField("annotations", SliceOf(StructOf(AnnotationSchema))),
        new GoField("audio", StructOf(OutputAudioSchema)));

    private static readonly Schema ResponseFormatSchema = new(
        "ResponseFormat",
        new GoField("type", StringType),
        new GoField("json_schema", RawType));

    private static readonly Schema RequestSchema = new(
        "Request",
        new GoField("messages", SliceOf(StructOf(MessageSchema))),
        new GoField("model", StringType),
        new GoField("frequency_penalty", DoubleType),
        new GoField("logprobs", BoolType),
        new GoField("max_completion_tokens", Int64Type),
        new GoField("max_tokens", Int64Type),
        new GoField("presence_penalty", DoubleType),
        new GoField("seed", Int64Type),
        new GoField("store", BoolType),
        new GoField("temperature", DoubleType),
        new GoField("top_logprobs", Int64Type),
        new GoField("top_p", DoubleType),
        new GoField("prompt_cache_key", StringType),
        new GoField("safety_identifier", StringType),
        new GoField("user", StringType),
        new GoField("logit_bias", MapOf(Int64Type)),
        new GoField("metadata", MapOf(StringType)),
        new GoField("modalities", SliceOf(StringType)),
        new GoField("reasoning_effort", StringType),
        new GoField("reasoning_budget", Int64Type),
        new GoField("reasoning_summary", StringType),
        new GoField("service_tier", StringType),
        new GoField("stop", StopType),
        new GoField("stream", BoolType),
        new GoField("stream_options", StructOf(StreamOptionsSchema)),
        new GoField("parallel_tool_calls", BoolType),
        new GoField("tools", SliceOf(StructOf(ToolSchema))),
        new GoField("tool_choice", ToolChoiceType),
        new GoField("response_format", StructOf(ResponseFormatSchema)),
        new GoField("verbosity", StringType),
        new GoField("thinking", StructOf(ThinkingSchema)));

    /// <summary>Go: openai.ImageGenerationRequest (image_inbound.go:56-79).</summary>
    private static readonly Schema ImageGenerationRequestSchema = new(
        "ImageGenerationRequest",
        new GoField("prompt", StringType),
        new GoField("model", StringType),
        new GoField("n", Int64Type),
        new GoField("quality", StringType),
        new GoField("response_format", StringType),
        new GoField("size", StringType),
        new GoField("style", StringType),
        new GoField("user", StringType),
        new GoField("background", StringType),
        new GoField("output_format", StringType),
        new GoField("output_compression", Int64Type),
        new GoField("moderation", StringType),
        new GoField("partial_images", Int64Type),
        new GoField("stream", BoolType),
        new GoField("image", RawType),
        new GoField("subject_reference", RawType),
        new GoField("aspect_ratio", StringType),
        new GoField("width", Int64Type),
        new GoField("height", Int64Type),
        new GoField("seed", Int64Type),
        new GoField("prompt_optimizer", BoolType));

    /// <summary>Go: openai.ImageEditJSONRequest (image_inbound.go:361-378).</summary>
    private static readonly Schema ImageEditJsonRequestSchema = new(
        "ImageEditJSONRequest",
        new GoField("prompt", StringType),
        new GoField("model", StringType),
        new GoField("image", RawType),
        new GoField("images", RawType),
        new GoField("mask", StringType),
        new GoField("n", Int64Type),
        new GoField("size", StringType),
        new GoField("quality", StringType),
        new GoField("response_format", StringType),
        new GoField("user", StringType),
        new GoField("background", StringType),
        new GoField("output_format", StringType),
        new GoField("output_compression", Int64Type),
        new GoField("input_fidelity", StringType),
        new GoField("partial_images", Int64Type),
        new GoField("stream", BoolType));
}
