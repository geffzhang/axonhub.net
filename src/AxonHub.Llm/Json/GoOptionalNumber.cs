using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of xjson.ParseOptionalFloat64: accepts a JSON number, or a JSON string
/// that is itself a bare number literal, and yields null for anything else
/// instead of failing the decode. Go validates quoted input with
/// encoding/json's <c>isValidNumber</c> before <c>strconv.ParseFloat</c>, and
/// treats out-of-range values as an error (nil result); both rules are
/// reproduced here.
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
            return reader.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (text is null || !IsValidNumber(text))
            {
                return null;
            }

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed)
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

        // Go encodes float64 through encoding/json's own formatter; the
        // reference test locks `"cost":0.000005` byte-for-byte.
        writer.WriteRawValue(GoJsonFloat.Format(value.Value), skipInputValidation: true);
    }

    /// <summary>
    /// Go encoding/json <c>isValidNumber</c>: a bare JSON number literal with
    /// no surrounding whitespace or leading '+', at most one leading zero, and
    /// mandatory fraction/exponent digits when those parts are present.
    /// </summary>
    private static bool IsValidNumber(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        var i = 0;
        if (s[0] == '-')
        {
            i++;
            if (i == s.Length)
            {
                return false;
            }
        }

        if (s[i] == '0')
        {
            i++;
        }
        else if (s[i] >= '1' && s[i] <= '9')
        {
            i++;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
            {
                i++;
            }
        }
        else
        {
            return false;
        }

        if (i < s.Length && s[i] == '.')
        {
            i++;
            if (i == s.Length || s[i] < '0' || s[i] > '9')
            {
                return false;
            }

            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
            {
                i++;
            }
        }

        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                i++;
            }

            if (i == s.Length || s[i] < '0' || s[i] > '9')
            {
                return false;
            }

            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
            {
                i++;
            }
        }

        return i == s.Length;
    }
}
