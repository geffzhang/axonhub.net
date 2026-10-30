using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of encoding/json's decode errors for the Anthropic Messages request wire
/// model (difference ledger C003).
/// </summary>
/// <remarks>
/// <para>
/// Go saves the first field type mismatch in document order and keeps decoding,
/// but a failing <c>UnmarshalJSON</c> aborts the decode and overrides the saved
/// error. Two fields carry custom unmarshalers here — <c>content</c> and
/// <c>system</c> — so those two produce hard errors with the reference's own
/// sentinel text; everything nested inside them is folded into that sentinel,
/// exactly like the nested <c>json.Unmarshal</c> calls that swallow their
/// errors. Syntax and root-value errors are reported by
/// <see cref="GoJsonSyntax"/> and <see cref="GoStructJson"/> before this runs.
/// </para>
/// <para>
/// Wording was recorded from Go 1.26 with the reference structs: the struct
/// name is the innermost struct being decoded while the field path is the full
/// path from the document root (e.g. <c>Thinking.thinking.budget_tokens</c>,
/// <c>Tool.tools.name</c>).
/// </para>
/// </remarks>
public static class AnthropicRequestTypeCheck
{
    private const string ContentCannotBeNull = "content cannot be null";
    private const string InvalidContentType = "invalid content type";
    private const string InvalidSystemPromptFormat = "invalid system prompt format";

    /// <summary>
    /// Returns Go's decode error text for the request body, or
    /// <see langword="null"/> when every field decodes.
    /// </summary>
    public static string? Validate(ReadOnlySpan<byte> utf8Json)
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

            return new Check().CheckRoot(ref reader);
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

        public string? CheckRoot(ref Utf8JsonReader reader)
        {
            var hard = CheckObject(MessageRequestSchema, path: string.Empty, ref reader);
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
                // MessageParam.Content is a value field, so its UnmarshalJSON
                // runs for null and rejects it. MessageContentBlock.Content is
                // a pointer, so null simply leaves it nil.
                return type.Kind == GoKind.Content ? ContentCannotBeNull : null;
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
                    // json.RawMessage accepts any value, including null's
                    // neighbours; a literal null was handled above (pointer).
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

                case GoKind.System:
                    return SystemAttempt(ref reader) ? null : InvalidSystemPromptFormat;

                case GoKind.Content:
                case GoKind.ContentPtr:
                    return ContentAttempt(ref reader) ? null : InvalidContentType;

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

    /// <summary>
    /// Go: SystemPrompt.UnmarshalJSON — try a string, then []SystemPromptPart;
    /// both failures collapse into the sentinel.
    /// </summary>
    private static bool SystemAttempt(ref Utf8JsonReader reader)
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
            // A null element decodes as the zero SystemPromptPart, which the
            // inbound validation later rejects (its type is not "text").
            if (reader.TokenType == JsonTokenType.Null)
            {
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartObject
                || !ObjectMatches(ref reader, SystemPromptPartSchema))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Go: MessageContent.UnmarshalJSON — try []MessageContentBlock, then a
    /// string; any failure collapses into the sentinel.
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
            // A null element decodes as the zero block.
            if (reader.TokenType == JsonTokenType.Null)
            {
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartObject
                || !ObjectMatches(ref reader, ContentBlockSchema))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validation-only object walk used by the two UnmarshalJSON attempts: it
    /// mirrors json.Unmarshal (unknown fields ignored, null accepted, number
    /// conversion rules) without recording errors.
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
            // Null is a no-op for every field shape inside these attempts: the
            // pointer target of a nested content field stays nil.
            return true;
        }

        switch (type.Kind)
        {
            case GoKind.String:
                return reader.TokenType == JsonTokenType.String;

            case GoKind.Bool:
                return reader.TokenType is JsonTokenType.True or JsonTokenType.False;

            case GoKind.Int64:
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

            case GoKind.System:
                return SystemAttempt(ref reader);

            case GoKind.Content:
            case GoKind.ContentPtr:
                return ContentAttempt(ref reader);

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
        => new(GoKind.Struct, "anthropic." + schema.GoName, Struct: schema);

    private static GoType SliceOf(GoType element)
        => new(GoKind.Slice, "[]" + element.Name, Element: element);

    private sealed record GoField(string Name, GoType Type);

    private sealed record GoType(
        GoKind Kind,
        string Name,
        Schema? Struct = null,
        GoType? Element = null);

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
        Double,
        Raw,
        Struct,
        Slice,
        System,
        Content,
        ContentPtr,
    }

    // Leaf Go types (pointer targets keep their element type name in errors).
    private static readonly GoType StringType = new(GoKind.String, "string");
    private static readonly GoType BoolType = new(GoKind.Bool, "bool");
    private static readonly GoType Int64Type = new(GoKind.Int64, "int64");
    private static readonly GoType DoubleType = new(GoKind.Double, "float64");
    private static readonly GoType RawType = new(GoKind.Raw, "json.RawMessage");
    private static readonly GoType SystemType = new(GoKind.System, "anthropic.SystemPrompt");
    private static readonly GoType ContentType = new(GoKind.Content, "anthropic.MessageContent");

    /// <summary>*MessageContent: null is accepted and leaves the pointer nil.</summary>
    private static readonly GoType ContentPtrType = new(GoKind.ContentPtr, "anthropic.MessageContent");

    // Schemas, declared leaf-first because static initializers run in order.
    private static readonly Schema CacheControlSchema = new(
        "CacheControl",
        new GoField("type", StringType),
        new GoField("ttl", StringType));

    private static readonly Schema SystemPromptPartSchema = new(
        "SystemPromptPart",
        new GoField("type", StringType),
        new GoField("text", StringType),
        new GoField("cache_control", StructOf(CacheControlSchema)));

    private static readonly Schema ImageSourceSchema = new(
        "ImageSource",
        new GoField("type", StringType),
        new GoField("media_type", StringType),
        new GoField("data", StringType),
        new GoField("url", StringType));

    private static readonly Schema TextCitationSchema = new(
        "TextCitation",
        new GoField("type", StringType),
        new GoField("url", StringType),
        new GoField("title", StringType),
        new GoField("encrypted_index", StringType),
        new GoField("cited_text", StringType));

    private static readonly Schema ContentBlockSchema = new(
        "MessageContentBlock",
        new GoField("type", StringType),
        new GoField("text", StringType),
        new GoField("citations", SliceOf(StructOf(TextCitationSchema))),
        new GoField("thinking", StringType),
        new GoField("signature", StringType),
        new GoField("data", StringType),
        new GoField("source", StructOf(ImageSourceSchema)),
        new GoField("id", StringType),
        new GoField("name", StringType),
        new GoField("input", RawType),
        new GoField("cache_control", StructOf(CacheControlSchema)),
        new GoField("tool_use_id", StringType),
        // Nested content lives behind a pointer, so a JSON null leaves it nil.
        new GoField("content", ContentPtrType),
        new GoField("is_error", BoolType),
        new GoField("url", StringType),
        new GoField("title", StringType),
        new GoField("encrypted_content", StringType),
        new GoField("page_age", StringType),
        new GoField("caller", RawType));

    private static readonly Schema AnthropicMetadataSchema = new(
        "AnthropicMetadata",
        new GoField("user_id", StringType));

    private static readonly Schema ThinkingSchema = new(
        "Thinking",
        new GoField("type", StringType),
        new GoField("budget_tokens", Int64Type),
        new GoField("display", StringType));

    private static readonly Schema OutputConfigSchema = new(
        "OutputConfig",
        new GoField("effort", StringType));

    private static readonly Schema ToolChoiceSchema = new(
        "ToolChoice",
        new GoField("type", StringType),
        new GoField("disable_parallel_tool_use", BoolType),
        new GoField("name", StringType));

    private static readonly Schema UserLocationSchema = new(
        "WebSearchToolUserLocation",
        new GoField("city", StringType),
        new GoField("country", StringType),
        new GoField("region", StringType),
        new GoField("timezone", StringType),
        new GoField("type", StringType));

    private static readonly Schema ToolSchema = new(
        "Tool",
        new GoField("type", StringType),
        new GoField("name", StringType),
        new GoField("description", StringType),
        new GoField("input_schema", RawType),
        new GoField("cache_control", StructOf(CacheControlSchema)),
        new GoField("max_uses", Int64Type),
        new GoField("strict", BoolType),
        new GoField("allowed_domains", SliceOf(StringType)),
        new GoField("blocked_domains", SliceOf(StringType)),
        new GoField("user_location", StructOf(UserLocationSchema)));

    private static readonly Schema MessageParamSchema = new(
        "MessageParam",
        new GoField("role", StringType),
        new GoField("content", ContentType));

    private static readonly Schema MessageRequestSchema = new(
        "MessageRequest",
        new GoField("max_tokens", Int64Type),
        new GoField("messages", SliceOf(StructOf(MessageParamSchema))),
        new GoField("model", StringType),
        new GoField("anthropic_version", StringType),
        new GoField("anthropic_beta", SliceOf(StringType)),
        new GoField("temperature", DoubleType),
        new GoField("top_k", Int64Type),
        new GoField("top_p", DoubleType),
        new GoField("metadata", StructOf(AnthropicMetadataSchema)),
        new GoField("service_tier", StringType),
        new GoField("stop_sequences", SliceOf(StringType)),
        new GoField("system", SystemType),
        new GoField("thinking", StructOf(ThinkingSchema)),
        new GoField("output_config", StructOf(OutputConfigSchema)),
        new GoField("tools", SliceOf(StructOf(ToolSchema))),
        new GoField("tool_choice", StructOf(ToolChoiceSchema)),
        new GoField("stream", BoolType),
        new GoField("cache_control", StructOf(CacheControlSchema)));
}
