using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm.Json;

/// <summary>
/// Writes every <see cref="double"/> the way Go's <c>encoding/json</c> does
/// (difference ledger C067): <see cref="GoJsonFloat.Format"/> instead of
/// System.Text.Json's exponent choice.
/// </summary>
/// <remarks>
/// Registered on <see cref="LlmJson.Options"/>, so it covers the unified model
/// and every transformer payload serialized through <see cref="GoJson"/>.
/// System.Text.Json wraps it for <c>double?</c> automatically, and dictionary /
/// array elements resolve it through <see cref="JsonSerializerOptions.GetConverter"/>.
/// Property-level converters such as <see cref="GoOptionalNumberConverter"/>
/// still take precedence.
/// </remarks>
public sealed class GoDoubleJsonConverter : JsonConverter<double>
{
    public override double Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        => writer.WriteRawValue(GoJsonFloat.Format(value), skipInputValidation: true);
}
