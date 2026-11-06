using System.Globalization;
using System.Text.Json;
using HotChocolate.Features;
using HotChocolate.Language;
using HotChocolate.Text.Json;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of <c>objects.JSONRawMessage</c> (internal/objects/json.go): the
/// reference scalar emits <c>json.RawMessage</c> bytes verbatim into the
/// response, so an object/array/string/number stored by the server is embedded
/// as JSON rather than as a GraphQL String. The port carries a
/// <see cref="JsonElement"/> and writes it structurally; the wire bytes match
/// because the service already produced them with Go's encoder semantics.
/// </summary>
public class JsonRawMessageType : ScalarType<JsonElement>
{
    public JsonRawMessageType()
        : this("JSONRawMessage")
    {
    }

    /// <summary>
    /// Shared JSON-embedding behaviour for scalars that emit their runtime
    /// value as raw JSON rather than as a GraphQL String; gqlgen's <c>Map</c>
    /// scalar is the same wire shape under a different name
    /// (<see cref="MapScalarType"/>).
    /// </summary>
    protected JsonRawMessageType(string name)
        : base(name, BindingBehavior.Explicit)
    {
    }

    /// <summary>
    /// The scalar accepts any JSON value on output. AnyType uses the same
    /// serialization flags (63 = string | number | boolean | object | array |
    /// null).
    /// </summary>
    public override ScalarSerializationType SerializationType
        => (ScalarSerializationType)63;

    public override bool IsValueCompatible(IValueNode valueLiteral)
        => valueLiteral is ObjectValueNode or ListValueNode or StringValueNode
            or IntValueNode or FloatValueNode or BooleanValueNode or NullValueNode;

    public override bool IsValueCompatible(JsonElement inputValue)
        => inputValue.ValueKind != JsonValueKind.Undefined;

    public override object CoerceInputLiteral(IValueNode valueLiteral)
        => JsonFromLiteral(valueLiteral);

    public override object CoerceInputValue(JsonElement inputValue, IFeatureProvider context)
        => inputValue.Clone();

    protected override void OnCoerceOutputValue(
        JsonElement runtimeValue,
        ResultElement resultValue)
    {
        switch (runtimeValue.ValueKind)
        {
            case JsonValueKind.String:
                resultValue.SetStringValue(runtimeValue.GetString());
                break;
            case JsonValueKind.Number:
                resultValue.SetNumberValue(
                    System.Text.Encoding.UTF8.GetBytes(runtimeValue.GetRawText()));
                break;
            case JsonValueKind.True:
                resultValue.SetBooleanValue(true);
                break;
            case JsonValueKind.False:
                resultValue.SetBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                resultValue.SetNullValue();
                break;
            case JsonValueKind.Array:
            {
                resultValue.SetArrayValue(runtimeValue.GetArrayLength());
                using var enumerator = runtimeValue.EnumerateArray().GetEnumerator();
                foreach (var item in resultValue.EnumerateArray())
                {
                    enumerator.MoveNext();
                    OnCoerceOutputValue(enumerator.Current, item);
                }

                break;
            }

            case JsonValueKind.Object:
            {
                resultValue.SetObjectValue(runtimeValue.GetPropertyCount());
                using var enumerator = runtimeValue.EnumerateObject().GetEnumerator();
                foreach (var property in resultValue.EnumerateObject())
                {
                    enumerator.MoveNext();
                    property.Value.SetPropertyName(enumerator.Current.Name.AsSpan());
                    OnCoerceOutputValue(enumerator.Current.Value, property.Value);
                }

                break;
            }

            default:
                throw new InvalidOperationException(
                    "JSONRawMessage cannot serialize " + runtimeValue.ValueKind);
        }
    }

    protected override IValueNode OnValueToLiteral(JsonElement runtimeValue)
    {
        switch (runtimeValue.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var fields = new List<ObjectFieldNode>();
                foreach (var property in runtimeValue.EnumerateObject())
                {
                    fields.Add(new ObjectFieldNode(
                        property.Name,
                        OnValueToLiteral(property.Value)));
                }

                return new ObjectValueNode(fields);
            }

            case JsonValueKind.Array:
            {
                var items = new List<IValueNode>();
                foreach (var item in runtimeValue.EnumerateArray())
                {
                    items.Add(OnValueToLiteral(item));
                }

                return new ListValueNode(items);
            }

            case JsonValueKind.String:
                return new StringValueNode(runtimeValue.GetString()!);
            case JsonValueKind.Number:
            {
                var raw = runtimeValue.GetRawText();
                return long.TryParse(
                        raw,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var integer)
                    ? new IntValueNode(integer)
                    : new FloatValueNode(
                        double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
            }

            case JsonValueKind.True:
                return new BooleanValueNode(true);
            case JsonValueKind.False:
                return new BooleanValueNode(false);
            default:
                return NullValueNode.Default;
        }
    }

    /// <summary>
    /// Converts an input literal to a JSON element. The scalar is only exposed
    /// as an output type by the reference schema, so this path exists for
    /// schema completeness rather than parity.
    /// </summary>
    private static JsonElement JsonFromLiteral(IValueNode literal)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteLiteral(writer, literal);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteLiteral(Utf8JsonWriter writer, IValueNode literal)
    {
        switch (literal)
        {
            case NullValueNode:
                writer.WriteNullValue();
                break;
            case StringValueNode value:
                writer.WriteStringValue(value.Value);
                break;
            case IntValueNode value:
                writer.WriteRawValue(value.Value, skipInputValidation: true);
                break;
            case FloatValueNode value:
                writer.WriteRawValue(value.Value, skipInputValidation: true);
                break;
            case BooleanValueNode value:
                writer.WriteBooleanValue(value.Value);
                break;
            case EnumValueNode value:
                writer.WriteStringValue(value.Value);
                break;
            case ListValueNode value:
                writer.WriteStartArray();
                foreach (var item in value.Items)
                {
                    WriteLiteral(writer, item);
                }

                writer.WriteEndArray();
                break;
            case ObjectValueNode value:
                writer.WriteStartObject();
                foreach (var field in value.Fields)
                {
                    writer.WritePropertyName(field.Name.Value);
                    WriteLiteral(writer, field.Value);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException(
                    "JSONRawMessage cannot read literal " + literal.Kind);
        }
    }
}
