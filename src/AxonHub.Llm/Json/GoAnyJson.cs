using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm.Json;

/// <summary>
/// Emits a <see cref="JsonElement"/> the way Go's <c>encoding/json</c> re-marshals
/// a value decoded into <c>any</c>.
/// </summary>
/// <remarks>
/// <para>
/// Go decodes an <c>any</c> field into <c>map[string]any</c>, <c>[]any</c>,
/// <c>string</c>, <c>bool</c>, <c>nil</c> or (with <c>Decoder.UseNumber</c>)
/// <c>json.Number</c>. Marshal then sorts map keys, re-encodes strings with the
/// usual HTML escaping, and writes numbers from the literal it retained —
/// never through a float64, so 64-bit ids and long decimals survive.
/// </para>
/// <para>
/// A bare <see cref="JsonElement"/> serialized by System.Text.Json would instead
/// keep the original key order and whitespace and would not apply the HTML
/// escape pass. The reference's SystemOne <c>state</c>/<c>instructions</c>/
/// <c>criteria</c>/<c>legend</c>/<c>probabilities</c> fields all carry Go
/// <c>any</c>, so they are written through here.
/// </para>
/// </remarks>
public static class GoAnyJson
{
    /// <summary>
    /// Writes one Go <c>any</c> value. <see cref="JsonValueKind.Undefined"/>
    /// stands for a nil interface and is written as <c>null</c>.
    /// </summary>
    public static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        ArgumentNullException.ThrowIfNull(writer);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(writer, element);
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
                // GoJson.Escape (applied by GoJson.Marshal) supplies the HTML
                // escaping encoding/json adds on top of the string encoder.
                writer.WriteStringValue(element.GetString());
                return;
            case JsonValueKind.Number:
                // json.Number: the literal is preserved verbatim.
                element.WriteTo(writer);
                return;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;
            default:
                writer.WriteNullValue();
                return;
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonElement element)
    {
        // Go decodes into a map: duplicate keys keep the last value, and the
        // marshal side orders keys by ordinal comparison.
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            members[property.Name] = property.Value;
        }

        writer.WriteStartObject();
        foreach (var name in members.Keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(name);
            Write(writer, members[name]);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// Applies <see cref="GoAnyJson"/> to a property carried as
/// <see cref="JsonElement"/>. The JSON <c>null</c> token decodes to the default
/// element (<see cref="JsonValueKind.Undefined"/>), matching Go's nil interface.
/// </summary>
public sealed class GoAnyJsonConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.ValueKind == JsonValueKind.Null
            ? default
            : document.RootElement.Clone();
    }

    public override void Write(
        Utf8JsonWriter writer,
        JsonElement value,
        JsonSerializerOptions options)
        => GoAnyJson.Write(writer, value);
}
