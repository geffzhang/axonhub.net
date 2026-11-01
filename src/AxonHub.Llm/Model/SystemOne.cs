using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm;

/// <summary>
/// Port of llm.SystemOneRequest (llm/systemone.go): a System One decision
/// inference request.
/// </summary>
public sealed class SystemOneRequest
{
    /// <summary>
    /// Context or entity to evaluate. Go carries <c>any</c>, so a string, an
    /// object or an array are all valid; <see cref="JsonValueKind.Undefined"/>
    /// is the nil state the inbound transformer rejects.
    /// </summary>
    [JsonPropertyName("state")]
    [JsonConverter(typeof(GoAnyJsonConverter))]
    public JsonElement State { get; set; }

    /// <summary>Question identifiers mapped to their schema definitions.</summary>
    [JsonPropertyName("questions")]
    public Dictionary<string, SystemOneQuestion>? Questions { get; set; }
}

/// <summary>
/// Port of llm.SystemOneQuestion (llm/systemone.go). Go decodes this struct
/// without a custom unmarshaler, so unknown fields are dropped and a JSON
/// <c>null</c> string field becomes the zero value.
/// </summary>
[JsonConverter(typeof(SystemOneQuestionJsonConverter))]
public sealed class SystemOneQuestion
{
    /// <summary>Primitive type: <c>"choice"</c>, <c>"noul"</c> or <c>"score"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Instructions for the decision model (string, object or array).</summary>
    [JsonPropertyName("instructions")]
    [JsonConverter(typeof(GoAnyJsonConverter))]
    public JsonElement Instructions { get; set; }

    /// <summary>Optional evaluation criteria/rubric (<c>omitempty</c> in Go).</summary>
    [JsonPropertyName("criteria")]
    [JsonConverter(typeof(GoAnyJsonConverter))]
    public JsonElement Criteria { get; set; }
}

/// <summary>Port of llm.SystemOneResponse (llm/systemone.go).</summary>
public sealed class SystemOneResponse
{
    /// <summary>Question identifiers mapped to their evaluated results.</summary>
    [JsonPropertyName("answers")]
    public Dictionary<string, SystemOneAnswer>? Answers { get; set; }
}

/// <summary>
/// Port of llm.SystemOneAnswer (llm/systemone.go): the evaluated answer for one
/// question. Fields the reference does not know are preserved in
/// <see cref="Extra"/> and re-emitted on marshal.
/// </summary>
[JsonConverter(typeof(SystemOneAnswerJsonConverter))]
public sealed class SystemOneAnswer
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("choice")]
    public string Choice { get; set; } = string.Empty;

    [JsonPropertyName("noul")]
    public double? Noul { get; set; }

    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("legend")]
    [JsonConverter(typeof(GoAnyJsonConverter))]
    public JsonElement Legend { get; set; }

    [JsonPropertyName("probabilities")]
    [JsonConverter(typeof(GoAnyJsonConverter))]
    public JsonElement Probabilities { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    /// <summary>
    /// Every field the reference's custom unmarshaler did not recognize, keyed
    /// by its exact JSON spelling (matching is case-insensitive, the deletion
    /// from the extra map is exact — so <c>"Type"</c> lands here as well).
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// Port of SystemOneQuestion's plain struct decode (no custom unmarshaler in
/// Go): unknown fields are ignored, a null string becomes "", a null
/// <c>any</c> becomes nil.
/// </summary>
public sealed class SystemOneQuestionJsonConverter : JsonConverter<SystemOneQuestion>
{
    public override SystemOneQuestion? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new SystemOneQuestion();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                "json: cannot unmarshal " + GoKind(reader.TokenType)
                + " into Go value of type llm.SystemOneQuestion");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var question = new SystemOneQuestion();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            switch (property.Name.ToLowerInvariant())
            {
                case "type":
                    question.Type = ReadString(property.Value, "SystemOneQuestion.type");
                    break;
                case "instructions":
                    question.Instructions = ReadAny(property.Value);
                    break;
                case "criteria":
                    question.Criteria = ReadAny(property.Value);
                    break;
            }
        }

        return question;
    }

    public override void Write(
        Utf8JsonWriter writer,
        SystemOneQuestion value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type ?? string.Empty);
        writer.WritePropertyName("instructions");
        GoAnyJson.Write(writer, value.Instructions);
        if (value.Criteria.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            writer.WritePropertyName("criteria");
            GoAnyJson.Write(writer, value.Criteria);
        }

        writer.WriteEndObject();
    }

    private static string ReadString(JsonElement element, string field)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Null => string.Empty,
            _ => throw new JsonException(
                "json: cannot unmarshal " + GoKind(element.ValueKind)
                + " into Go struct field " + field + " of type string"),
        };

    private static JsonElement ReadAny(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? default : element.Clone();

    internal static string GoKind(JsonTokenType tokenType)
        => tokenType switch
        {
            JsonTokenType.StartObject => "object",
            JsonTokenType.StartArray => "array",
            JsonTokenType.String => "string",
            JsonTokenType.Number => "number",
            JsonTokenType.True or JsonTokenType.False => "bool",
            JsonTokenType.Null => "null",
            _ => "value",
        };

    internal static string GoKind(JsonValueKind kind)
        => kind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "bool",
            JsonValueKind.Null => "null",
            _ => "value",
        };
}

/// <summary>
/// Port of <c>SystemOneAnswer.UnmarshalJSON</c> / <c>MarshalJSON</c>
/// (llm/systemone.go): known fields decode case-insensitively with
/// <c>UseNumber</c>; every remaining key is kept as raw JSON under its exact
/// spelling. On marshal a non-empty extra map re-serializes the whole answer as
/// a Go map — keys sorted, extras merged — instead of the struct field order.
/// </summary>
public sealed class SystemOneAnswerJsonConverter : JsonConverter<SystemOneAnswer>
{
    private static readonly string[] StructFieldOrder =
    [
        "type",
        "choice",
        "noul",
        "score",
        "legend",
        "probabilities",
        "confidence",
    ];

    public override SystemOneAnswer? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            // Go decodes a null map value as the zero struct.
            return new SystemOneAnswer();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                "json: cannot unmarshal " + SystemOneQuestionJsonConverter.GoKind(reader.TokenType)
                + " into Go value of type llm.SystemOneAnswer");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var answer = new SystemOneAnswer();
        Dictionary<string, JsonElement>? extra = null;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var name = property.Name;
            if (name.Equals("type", StringComparison.OrdinalIgnoreCase))
            {
                answer.Type = ReadString(property.Value);
            }
            else if (name.Equals("choice", StringComparison.OrdinalIgnoreCase))
            {
                answer.Choice = ReadString(property.Value);
            }
            else if (name.Equals("noul", StringComparison.OrdinalIgnoreCase))
            {
                answer.Noul = ReadFloat(property.Value);
            }
            else if (name.Equals("score", StringComparison.OrdinalIgnoreCase))
            {
                answer.Score = ReadFloat(property.Value);
            }
            else if (name.Equals("legend", StringComparison.OrdinalIgnoreCase))
            {
                answer.Legend = ReadAny(property.Value);
            }
            else if (name.Equals("probabilities", StringComparison.OrdinalIgnoreCase))
            {
                answer.Probabilities = ReadAny(property.Value);
            }
            else if (name.Equals("confidence", StringComparison.OrdinalIgnoreCase))
            {
                answer.Confidence = ReadFloat(property.Value);
            }
            else
            {
                (extra ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal))[name] =
                    property.Value.Clone();
                continue;
            }

            // Go deletes only the exact lowercase names from the raw map, so a
            // differently-cased spelling of a known field sets the struct field
            // above *and* survives in Extra (for example "TYPE").
            if (!IsCanonicalName(name))
            {
                (extra ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal))[name] =
                    property.Value.Clone();
            }
        }

        answer.Extra = extra;
        return answer;
    }

    public override void Write(
        Utf8JsonWriter writer,
        SystemOneAnswer value,
        JsonSerializerOptions options)
    {
        if (value.Extra is not { Count: > 0 })
        {
            writer.WriteStartObject();
            foreach (var name in StructFieldOrder)
            {
                if (IsPresent(name, value))
                {
                    WriteKnown(writer, name, value);
                }
            }

            writer.WriteEndObject();
            return;
        }

        // MarshalJSON re-decodes the alias output into map[string]RawMessage,
        // merges the extras and marshals the map: ordinal key order.
        var names = new List<string>(StructFieldOrder.Length + value.Extra.Count);
        foreach (var name in StructFieldOrder)
        {
            if (IsPresent(name, value))
            {
                names.Add(name);
            }
        }

        names.AddRange(value.Extra.Keys);
        names.Sort(StringComparer.Ordinal);

        writer.WriteStartObject();
        foreach (var name in names)
        {
            if (value.Extra.TryGetValue(name, out var raw))
            {
                writer.WritePropertyName(name);
                GoJson.CompactElement(raw).WriteTo(writer);
            }
            else
            {
                WriteKnown(writer, name, value);
            }
        }

        writer.WriteEndObject();
    }

    private static bool IsCanonicalName(string name)
        => name is "type"
            or "choice"
            or "noul"
            or "score"
            or "legend"
            or "probabilities"
            or "confidence";

    private static void WriteKnown(Utf8JsonWriter writer, string name, SystemOneAnswer value)
    {
        switch (name)
        {
            case "type":
                writer.WriteString(name, value.Type);
                return;
            case "choice":
                writer.WriteString(name, value.Choice);
                return;
            case "noul":
                writer.WritePropertyName(name);
                writer.WriteRawValue(FormatFloat(value.Noul!.Value), skipInputValidation: true);
                return;
            case "score":
                writer.WritePropertyName(name);
                writer.WriteRawValue(FormatFloat(value.Score!.Value), skipInputValidation: true);
                return;
            case "legend":
                writer.WritePropertyName(name);
                GoAnyJson.Write(writer, value.Legend);
                return;
            case "probabilities":
                writer.WritePropertyName(name);
                GoAnyJson.Write(writer, value.Probabilities);
                return;
            case "confidence":
                writer.WritePropertyName(name);
                writer.WriteRawValue(FormatFloat(value.Confidence!.Value), skipInputValidation: true);
                return;
            default:
                throw new InvalidOperationException("unknown SystemOneAnswer field " + name);
        }
    }

    private static bool IsPresent(string name, SystemOneAnswer value)
        => name switch
        {
            "type" => !string.IsNullOrEmpty(value.Type),
            "choice" => !string.IsNullOrEmpty(value.Choice),
            "noul" => value.Noul is not null,
            "score" => value.Score is not null,
            "legend" => value.Legend.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null),
            "probabilities" => value.Probabilities.ValueKind
                is not (JsonValueKind.Undefined or JsonValueKind.Null),
            "confidence" => value.Confidence is not null,
            _ => false,
        };

    private static string FormatFloat(double value) => GoJsonFloat.Format(value);

    private static string ReadString(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Null => string.Empty,
            _ => throw new JsonException(
                "json: cannot unmarshal " + SystemOneQuestionJsonConverter.GoKind(element.ValueKind)
                + " into Go struct field SystemOneAnswer of type string"),
        };

    private static double? ReadFloat(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Number:
                var value = element.GetDouble();
                if (!double.IsFinite(value))
                {
                    // Go rejects out-of-range literals such as 1e400.
                    throw new JsonException(
                        "json: cannot unmarshal number "
                        + element.GetRawText()
                        + " into Go struct field SystemOneAnswer of type float64");
                }

                return value;
            default:
                throw new JsonException(
                    "json: cannot unmarshal " + SystemOneQuestionJsonConverter.GoKind(element.ValueKind)
                    + " into Go struct field SystemOneAnswer of type float64");
        }
    }

    private static JsonElement ReadAny(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? default : element.Clone();
}

/// <summary>
/// Go nil-map semantics for SystemOne's string-keyed maps: a JSON <c>null</c>
/// map member stays nil, but a null *value* inside the map decodes as the
/// zero struct (Go map values are not pointers).
/// </summary>
public static class SystemOneJson
{
    public static Dictionary<string, SystemOneQuestion>? CoalesceQuestions(
        Dictionary<string, SystemOneQuestion>? questions)
    {
        if (questions is null)
        {
            return null;
        }

        foreach (var key in questions.Keys.ToList())
        {
            questions[key] ??= new SystemOneQuestion();
        }

        return questions;
    }

    public static Dictionary<string, SystemOneAnswer>? CoalesceAnswers(
        Dictionary<string, SystemOneAnswer>? answers)
    {
        if (answers is null)
        {
            return null;
        }

        foreach (var key in answers.Keys.ToList())
        {
            answers[key] ??= new SystemOneAnswer();
        }

        return answers;
    }
}
