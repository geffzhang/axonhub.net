using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of xjson.ParseOptionalFloat64: accepts a JSON number or a numeric
/// string and yields null for anything else instead of failing the decode.
/// </summary>
public sealed class GoOptionalNumberConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.TryGetDouble(out var value) ? value : null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteNumberValue(value.Value);
    }
}
