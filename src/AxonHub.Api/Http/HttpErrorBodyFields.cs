using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of the gjson lookups the reference performs on an upstream error body
/// (<c>error.type</c>, <c>error.code</c> and the top-level <c>request_id</c>):
/// only non-empty JSON strings count, everything else is reported as absent.
/// </summary>
internal readonly record struct HttpErrorBodyFields(string Type, string Code, string RequestId)
{
    private static readonly HttpErrorBodyFields Empty = new(string.Empty, string.Empty, string.Empty);

    public static HttpErrorBodyFields Parse(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return Empty;
        }

        using var document = TryParse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var root = document.RootElement;
        var type = string.Empty;
        var code = string.Empty;

        if (root.TryGetProperty("error", out var errorObject)
            && errorObject.ValueKind == JsonValueKind.Object)
        {
            type = ReadString(errorObject, "type");
            code = ReadString(errorObject, "code");
        }

        return new HttpErrorBodyFields(type, code, ReadString(root, "request_id"));
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(value.GetString()))
        {
            return value.GetString()!;
        }

        return string.Empty;
    }

    private static JsonDocument? TryParse(byte[] data)
    {
        try
        {
            // The reference decodes upstream payloads with gjson, which does not
            // require well-formed UTF-8; repair string literals the same way the
            // other lenient body readers in this assembly do.
            return JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
