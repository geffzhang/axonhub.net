using System.Globalization;
using System.Text;
using System.Text.Json;
using HotChocolate;
using HotChocolate.Features;
using HotChocolate.Language;
using HotChocolate.Text.Json;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of objects.GUID (internal/objects/GUID.go). The reference ID scalar is
/// gqlgen's built-in ID bound to objects.GUID; the port keeps HotChocolate's ID
/// scalar and parses/renders the same wire strings at the resolver boundary.
/// </summary>
public readonly record struct GidValue(string Type, long Id)
{
    public const string Prefix = "gid://axonhub/";

    public override string ToString() => Prefix + Type + "/" + Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Port of GUID.UnmarshalGQL with the exact reference messages.</summary>
    public static GidValue Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException("guid is empty");
        }

        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("guid must start with gid://axonhub/");
        }

        var remainder = value[Prefix.Length..];
        var slash = remainder.IndexOf('/');
        if (slash < 0)
        {
            throw new InvalidOperationException("guid must contain type and id");
        }

        var type = remainder[..slash];
        var idText = remainder[(slash + 1)..];
        if (!long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            // strconv.Atoi reports ErrSyntax for non-numeric text and ErrRange
            // for values outside the machine int.
            throw new InvalidOperationException(
                "strconv.Atoi: parsing \"" + idText + "\": "
                + (IsDecimal(idText) ? "value out of range" : "invalid syntax"));
        }

        return new GidValue(type, id);
    }

    private static bool IsDecimal(string text)
    {
        var start = text.Length > 0 && (text[0] == '+' || text[0] == '-') ? 1 : 0;
        if (start == text.Length)
        {
            return false;
        }

        for (var i = start; i < text.Length; i++)
        {
            if (text[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The reference ent.Cursor value carried by the Cursor scalar.</summary>
public readonly record struct CursorValue(long Id, DateTime? Value);

/// <summary>
/// Port of gqlgen's Cursor marshaler (internal/server/gql/cursor.go). The
/// reference wraps a msgpack payload in base64.RawStdEncoding; the port uses a
/// JSON payload because the msgpack dependency is unavailable offline. The
/// wire shape (opaque, unpadded base64) is preserved (C-range difference).
/// </summary>
public static class CursorCodec
{
    public sealed record Payload(long Id, string? Value);

    public static string Encode(CursorValue cursor)
    {
        var payload = new Payload(
            cursor.Id,
            cursor.Value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(payload),
                Base64FormattingOptions.None)
            .TrimEnd('=');
    }

    public static CursorValue Decode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("cannot decode cursor: empty cursor");
        }

        try
        {
            var padded = text.PadRight(
                text.Length + ((4 - (text.Length % 4)) % 4),
                '=');
            var payload = JsonSerializer.Deserialize<Payload>(
                Convert.FromBase64String(padded));
            if (payload is null)
            {
                throw new InvalidOperationException("cannot decode cursor: empty payload");
            }

            var value = payload.Value is null
                ? (DateTime?)null
                : DateTime.Parse(
                    payload.Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind).ToUniversalTime();

            return new CursorValue(payload.Id, value);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new InvalidOperationException("cannot decode cursor: " + ex.Message, ex);
        }
    }
}

/// <summary>Port of the relay Cursor scalar (Cursor in ent.graphql).</summary>
public sealed class CursorType : ScalarType<CursorValue, StringValueNode>
{
    public CursorType()
        : base("Cursor", BindingBehavior.Explicit)
    {
    }

    protected override CursorValue OnCoerceInputLiteral(StringValueNode valueLiteral)
        => CursorCodec.Decode(valueLiteral.Value);

    protected override CursorValue OnCoerceInputValue(
        JsonElement inputValue,
        IFeatureProvider context)
        => CursorCodec.Decode(inputValue.GetString());

    protected override void OnCoerceOutputValue(
        CursorValue runtimeValue,
        ResultElement resultValue)
        => resultValue.SetStringValue(CursorCodec.Encode(runtimeValue), isEncoded: false);

    protected override StringValueNode OnValueToLiteral(CursorValue runtimeValue)
        => new(CursorCodec.Encode(runtimeValue));
}

/// <summary>
/// Port of gqlgen's Time scalar (graphql.Time): time.Format(time.RFC3339Nano),
/// i.e. UTC timestamps with trailing fractional zeroes trimmed.
/// </summary>
public sealed class GoTimeType : ScalarType<DateTime, StringValueNode>
{
    public GoTimeType()
        : base("Time", BindingBehavior.Explicit)
    {
    }

    public static string Format(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }

    public static DateTime Parse(string text)
    {
        var parsed = DateTime.Parse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        return parsed.Kind switch
        {
            DateTimeKind.Utc => parsed,
            DateTimeKind.Local => parsed.ToUniversalTime(),
            _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
        };
    }

    protected override DateTime OnCoerceInputLiteral(StringValueNode valueLiteral)
        => Parse(valueLiteral.Value);

    protected override DateTime OnCoerceInputValue(
        JsonElement inputValue,
        IFeatureProvider context)
        => Parse(inputValue.GetString()
            ?? throw new InvalidOperationException("Time cannot be null"));

    protected override void OnCoerceOutputValue(DateTime runtimeValue, ResultElement resultValue)
        => resultValue.SetStringValue(Format(runtimeValue), isEncoded: false);

    protected override StringValueNode OnValueToLiteral(DateTime runtimeValue)
        => new(Format(runtimeValue));
}
