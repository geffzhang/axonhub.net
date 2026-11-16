using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm.Transformer.Gemini.OpenAI;

/// <summary>
/// Port of geminioai.ThinkingBudget (llm/transformer/gemini/openai/outbound.go:37-75):
/// a thinking budget that is either an integer (Gemini 2.5: 1024 / 8192 / 24576)
/// or a string (Gemini 3: "low" / "high").
/// </summary>
/// <remarks>
/// Go's MarshalJSON prefers the string value when both pointers are set and
/// writes <c>null</c> when neither is. UnmarshalJSON tries an integer first and
/// a string second; everything else fails with
/// <c>thinking_budget must be an int or string</c>.
/// </remarks>
[JsonConverter(typeof(ThinkingBudgetJsonConverter))]
public sealed class ThinkingBudget
{
    public long? IntValue { get; set; }

    public string? StringValue { get; set; }
}

public sealed class ThinkingBudgetJsonConverter : JsonConverter<ThinkingBudget>
{
    public override ThinkingBudget? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            // Go's int attempt on a JSON null is a successful no-op, so a
            // non-pointer ThinkingBudget decodes as zero.
            return new ThinkingBudget { IntValue = 0 };
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            // Go decodes into int with strconv.ParseInt: fractions, exponents
            // and out-of-range literals all fail and fall through to the
            // string attempt, which also fails.
            if (reader.TryGetInt64(out var intValue))
            {
                return new ThinkingBudget { IntValue = intValue };
            }

            throw new JsonException("thinking_budget must be an int or string");
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new ThinkingBudget { StringValue = reader.GetString() ?? string.Empty };
        }

        throw new JsonException("thinking_budget must be an int or string");
    }

    public override void Write(Utf8JsonWriter writer, ThinkingBudget value, JsonSerializerOptions options)
    {
        if (value.StringValue is not null)
        {
            writer.WriteStringValue(value.StringValue);
            return;
        }

        if (value.IntValue is { } intValue)
        {
            writer.WriteNumberValue(intValue);
            return;
        }

        writer.WriteNullValue();
    }
}

/// <summary>
/// Port of geminioai.ThinkingConfig (llm/transformer/gemini/openai/outbound.go:78-88).
/// </summary>
public sealed class ThinkingConfig
{
    /// <summary>Token budget; Gemini 2.5 also accepts the string ladder.</summary>
    [JsonPropertyName("thinking_budget")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThinkingBudget? ThinkingBudget { get; set; }

    /// <summary>Gemini 3 thinking level ("low" / "high").</summary>
    [JsonPropertyName("thinking_level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ThinkingLevel { get; set; }

    /// <summary>Whether to include thought summaries in the response.</summary>
    [JsonPropertyName("include_thoughts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeThoughts { get; set; }
}

/// <summary>Port of geminioai.GoogleExtraBody.</summary>
public sealed class GoogleExtraBody
{
    [JsonPropertyName("thinking_config")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThinkingConfig? ThinkingConfig { get; set; }
}

/// <summary>Port of geminioai.ExtraBody.</summary>
public sealed class ExtraBody
{
    [JsonPropertyName("google")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoogleExtraBody? Google { get; set; }
}

/// <summary>
/// Port of geminioai.ParseExtraBody (outbound.go:214-225): a null, empty or
/// undecodable <c>extra_body</c> yields null; a decodable document yields the
/// struct even when it carries no <c>google</c> member.
/// </summary>
/// <remarks>
/// The reference parses a <c>json.RawMessage</c>; the port's unified request
/// stores a <see cref="JsonElement"/> because an invalid document cannot be
/// represented there (C108). Go's struct decode rules apply: field names match
/// case-insensitively, a later duplicate key wins, and a type error anywhere
/// fails the whole document.
/// </remarks>
public static class GeminiExtraBodyParser
{
    public static ExtraBody? ParseExtraBody(JsonElement? rawExtraBody)
    {
        if (rawExtraBody is not { } raw
            || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var extraBody = new ExtraBody();

        if (raw.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in raw.EnumerateObject())
        {
            if (!FieldEquals(property.Name, "google"))
            {
                continue;
            }

            var google = ParseGoogle(property.Value, out var valid);
            if (!valid)
            {
                return null;
            }

            extraBody.Google = google;
        }

        return extraBody;
    }

    private static GoogleExtraBody? ParseGoogle(JsonElement value, out bool valid)
    {
        valid = true;

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            valid = false;
            return null;
        }

        var google = new GoogleExtraBody();
        foreach (var property in value.EnumerateObject())
        {
            if (!FieldEquals(property.Name, "thinking_config"))
            {
                continue;
            }

            google.ThinkingConfig = ParseThinkingConfig(property.Value, out valid);
            if (!valid)
            {
                return null;
            }
        }

        return google;
    }

    private static ThinkingConfig? ParseThinkingConfig(JsonElement value, out bool valid)
    {
        valid = true;

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            valid = false;
            return null;
        }

        var config = new ThinkingConfig();
        foreach (var property in value.EnumerateObject())
        {
            if (FieldEquals(property.Name, "thinking_budget"))
            {
                config.ThinkingBudget = ParseThinkingBudget(property.Value, out valid);
                if (!valid)
                {
                    return null;
                }

                continue;
            }

            if (FieldEquals(property.Name, "thinking_level"))
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    valid = false;
                    return null;
                }

                // Go's string field omitempty drops the empty value on write.
                var level = property.Value.GetString() ?? string.Empty;
                config.ThinkingLevel = level.Length == 0 ? null : level;
                continue;
            }

            if (FieldEquals(property.Name, "include_thoughts"))
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Null:
                        continue;
                    case JsonValueKind.True:
                        config.IncludeThoughts = true;
                        continue;
                    case JsonValueKind.False:
                        config.IncludeThoughts = false;
                        continue;
                    default:
                        valid = false;
                        return null;
                }
            }
        }

        return config;
    }

    private static ThinkingBudget? ParseThinkingBudget(JsonElement value, out bool valid)
    {
        valid = true;

        // A null through a *ThinkingBudget field sets the pointer to nil
        // without invoking UnmarshalJSON.
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var intValue))
            {
                return new ThinkingBudget { IntValue = intValue };
            }

            valid = false;
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return new ThinkingBudget { StringValue = value.GetString() ?? string.Empty };
        }

        valid = false;
        return null;
    }

    /// <summary>Go's struct field lookup folds ASCII letters only.</summary>
    private static bool FieldEquals(string name, string tag)
    {
        if (name.Length != tag.Length)
        {
            return false;
        }

        for (var i = 0; i < name.Length; i++)
        {
            if (FoldAscii(name[i]) != FoldAscii(tag[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static char FoldAscii(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
}
