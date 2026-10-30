using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm.Json;

/// <summary>
/// Emits <c>Dictionary&lt;string, T&gt;</c> with keys sorted in ordinal order.
/// </summary>
/// <remarks>
/// Go's <c>encoding/json</c> always sorts map keys when marshaling, so any map
/// carried in a wire payload is ordered by key, not by insertion. Reproducing
/// that is required for byte-level equality: header maps in particular are built
/// in an order the client controls.
/// </remarks>
public sealed class GoSortedMapConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType
            && typeToConvert.GetGenericArguments().Length == 2
            && typeToConvert.GetGenericArguments()[0] == typeof(string)
            // SortedDictionary already emits in comparer order; only the
            // insertion-ordered Dictionary needs the conversion.
            && typeToConvert.GetGenericTypeDefinition() == typeof(Dictionary<,>);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[1];
        var converterType = typeof(SortedMapConverter<>).MakeGenericType(valueType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class SortedMapConverter<TValue> : JsonConverter<Dictionary<string, TValue>>
    {
        private static TValue ZeroValue
            => typeof(TValue) == typeof(string) ? (TValue)(object)string.Empty : default!;

        public override Dictionary<string, TValue> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return [];
            }

            var valueConverter = (JsonConverter<TValue>)options.GetConverter(typeof(TValue));
            var result = new Dictionary<string, TValue>(StringComparer.Ordinal);

            reader.Read();
            while (reader.TokenType != JsonTokenType.EndObject)
            {
                var key = reader.GetString()!;
                reader.Read();
                // Go decodes a null map value as the zero value: 0 for numbers,
                // "" for strings, nil for slices, pointers and interfaces.
                result[key] = reader.TokenType == JsonTokenType.Null
                    ? ZeroValue
                    : valueConverter.Read(ref reader, typeof(TValue), options)!;
                reader.Read();
            }

            return result;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, TValue> value, JsonSerializerOptions options)
        {
            var valueConverter = (JsonConverter<TValue>)options.GetConverter(typeof(TValue));

            writer.WriteStartObject();
            foreach (var key in value.Keys.OrderBy(static k => k, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                valueConverter.Write(writer, value[key], options);
            }

            writer.WriteEndObject();
        }
    }
}
