using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Cline;

/// <summary>
/// Port of llm/transformer/cline/error.go: Cline returns OpenAI-shaped bodies
/// inside a <c>{success,data,error,errors}</c> envelope, so every failure path
/// funnels through this decoder.
/// </summary>
internal static class ClineErrors
{
    /// <summary>Port of parseClineError (error.go:14-38).</summary>
    public static ResponseError Parse(int statusCode, byte[]? body)
    {
        if (body is { Length: > 0 } && TryDecodeErrorPayload(body, out var error, out var errors))
        {
            if (error is { } errorValue)
            {
                return FromRaw(statusCode, errorValue);
            }

            if (errors is { } errorsValue)
            {
                return FromRaw(statusCode, errorsValue);
            }
        }

        var message = GoHttpStatus.Text(statusCode);
        if (message.Length == 0)
        {
            message = GoHttpStatus.Text(502);
        }

        return FromRaw(statusCode, message);
    }

    /// <summary>
    /// Port of parseClineErrorEvent (error.go:40-99): Cline can fail mid-stream
    /// with either an SSE <c>event: error</c> or an in-band envelope. A payload
    /// the reference cannot decode is not an error event.
    /// </summary>
    public static ResponseError? ParseStreamErrorEvent(StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            return null;
        }

        if (streamEvent.Type == "error" && streamEvent.Data.Length == 0)
        {
            return FromRaw(502, "stream error");
        }

        if (streamEvent.Data.Length == 0)
        {
            return null;
        }

        if (!TryDecodeEventPayload(streamEvent.Data, out var payload))
        {
            return null;
        }

        if (payload.Success == false)
        {
            if (payload.Error is { } error)
            {
                return FromRaw(502, error);
            }

            if (payload.Errors is { } errors)
            {
                return FromRaw(502, errors);
            }

            return FromRaw(502, "stream error");
        }

        if (payload.Error is { } topLevelError)
        {
            return FromRaw(502, topLevelError);
        }

        if (payload.Errors is { } topLevelErrors)
        {
            return FromRaw(502, topLevelErrors);
        }

        if (streamEvent.Type == "error" || payload.Event == "error")
        {
            if (payload.DataError is { } dataError)
            {
                return FromRaw(502, dataError);
            }

            return FromRaw(502, "stream error");
        }

        return null;
    }

    /// <summary>Port of clineResponseError (error.go:113-160).</summary>
    public static ResponseError FromRaw(int statusCode, JsonElement raw)
    {
        if (statusCode == 0)
        {
            statusCode = 502;
        }

        var detail = new ErrorDetail { Type = "api_error" };

        switch (raw.ValueKind)
        {
            case JsonValueKind.Object:
                detail.Message = ExactString(raw, "message");
                detail.Type = ExactString(raw, "type");
                detail.Code = NullIfEmpty(ExactString(raw, "code"));
                detail.Param = NullIfEmpty(ExactString(raw, "param"));
                detail.RequestId = NullIfEmpty(ExactString(raw, "request_id"));
                break;
            case JsonValueKind.Array:
                var messages = new List<string>();
                foreach (var item in raw.EnumerateArray())
                {
                    var message = ErrorMessage(item);
                    if (message.Length > 0)
                    {
                        messages.Add(message);
                    }
                }

                detail.Message = string.Join("; ", messages);
                break;
            default:
                // any other JSON value (string, bool, number): cast.ToString.
                detail.Message = GoCast.ToString(raw);
                break;
        }

        return Finish(statusCode, detail);
    }

    /// <summary>Port of the string arm of clineResponseError.</summary>
    public static ResponseError FromRaw(int statusCode, string raw)
        => Finish(
            statusCode == 0 ? 502 : statusCode,
            new ErrorDetail { Type = "api_error", Message = raw });

    private static ResponseError Finish(int statusCode, ErrorDetail detail)
    {
        if (detail.Type.Length == 0)
        {
            detail.Type = "api_error";
        }

        if (detail.Message.Length == 0)
        {
            detail.Message = GoHttpStatus.Text(statusCode);
        }

        if (detail.Message.Length == 0)
        {
            detail.Message = "Cline API error";
        }

        return new ResponseError { StatusCode = statusCode, Detail = detail };
    }

    /// <summary>Port of clineErrorMessage (error.go:101-111).</summary>
    private static string ErrorMessage(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.String)
        {
            return raw.GetString() ?? string.Empty;
        }

        if (raw.ValueKind == JsonValueKind.Object)
        {
            var message = ExactString(raw, "message");
            if (message.Length > 0)
            {
                return message;
            }

            // cast.ToString(map) fails silently in the reference, so objects
            // without a usable message contribute nothing.
            return string.Empty;
        }

        return GoCast.ToString(raw);
    }

    /// <summary>
    /// <c>cast.ToString(v[key])</c> on a decoded <c>map[string]any</c>: Go map
    /// lookups are exact-case and a later duplicate key wins.
    /// </summary>
    private static string ExactString(JsonElement obj, string key)
    {
        var result = string.Empty;
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.Ordinal))
            {
                result = GoCast.ToString(property.Value);
            }
        }

        return result;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>
    /// Port of the <c>{success *bool; error any; errors any}</c> decode in
    /// parseClineError. Returns false when the reference's json.Unmarshal would
    /// fail, in which case the caller falls back to the status text.
    /// </summary>
    private static bool TryDecodeErrorPayload(
        byte[] body,
        out JsonElement? error,
        out JsonElement? errors)
    {
        error = null;
        errors = null;

        if (!TryParse(body, out var doc))
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                // Unmarshal(null) leaves the zero struct.
                return true;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var valid = true;
            foreach (var property in root.EnumerateObject())
            {
                switch (GoFoldField(property.Name))
                {
                    case "success":
                        // Decoding null into *bool clears it; any non-bool is a
                        // type error that fails the whole unmarshal.
                        if (property.Value.ValueKind != JsonValueKind.Null
                            && property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            valid = false;
                        }

                        break;
                    case "error":
                        if (!IsValidAny(property.Value))
                        {
                            valid = false;
                            break;
                        }

                        error = CloneUnlessNull(property.Value);
                        break;
                    case "errors":
                        if (!IsValidAny(property.Value))
                        {
                            valid = false;
                            break;
                        }

                        errors = CloneUnlessNull(property.Value);
                        break;
                    default:
                        break;
                }
            }

            return valid;
        }
    }

    /// <summary>
    /// Port of the event payload decode in parseClineErrorEvent. Returns false
    /// when the reference's json.Unmarshal would fail (the reference then
    /// ignores the event).
    /// </summary>
    private static bool TryDecodeEventPayload(byte[] data, out EventPayload payload)
    {
        payload = new EventPayload();

        if (!TryParse(data, out var doc))
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                switch (GoFoldField(property.Name))
                {
                    case "success":
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        {
                            payload.Success = null;
                            break;
                        }

                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            return false;
                        }

                        payload.Success = property.Value.GetBoolean();
                        break;
                    case "error":
                        if (!IsValidAny(property.Value))
                        {
                            return false;
                        }

                        payload.Error = CloneUnlessNull(property.Value);
                        break;
                    case "errors":
                        if (!IsValidAny(property.Value))
                        {
                            return false;
                        }

                        payload.Errors = CloneUnlessNull(property.Value);
                        break;
                    case "event":
                        // null into a string field is a no-op in Go.
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        {
                            break;
                        }

                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            return false;
                        }

                        payload.Event = property.Value.GetString() ?? string.Empty;
                        break;
                    case "data":
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        {
                            break;
                        }

                        if (property.Value.ValueKind != JsonValueKind.Object)
                        {
                            return false;
                        }

                        if (!TryDecodeDataError(property.Value, ref payload))
                        {
                            return false;
                        }

                        break;
                    default:
                        break;
                }
            }

            return true;
        }
    }

    private static bool TryDecodeDataError(JsonElement data, ref EventPayload payload)
    {
        foreach (var property in data.EnumerateObject())
        {
            if (GoFoldField(property.Name) != "error")
            {
                continue;
            }

            if (!IsValidAny(property.Value))
            {
                return false;
            }

            payload.DataError = CloneUnlessNull(property.Value);
        }

        return true;
    }

    private static bool TryParse(byte[] body, out JsonDocument doc)
    {
        try
        {
            // encoding/json repairs ill-formed UTF-8 before decoding.
            doc = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(body) ?? body);
            return true;
        }
        catch (JsonException)
        {
            doc = null!;
            return false;
        }
    }

    /// <summary>
    /// Decoding into <c>any</c> accepts every JSON value except a number that
    /// does not fit in float64; nested numbers are decoded too, so the failure
    /// applies recursively.
    /// </summary>
    private static bool IsValidAny(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetDouble(out _),
        JsonValueKind.Array => value.EnumerateArray().All(IsValidAny),
        JsonValueKind.Object => value.EnumerateObject().All(p => IsValidAny(p.Value)),
        _ => true,
    };

    private static JsonElement? CloneUnlessNull(JsonElement value)
        => value.ValueKind == JsonValueKind.Null ? null : value.Clone();

    /// <summary>Go's struct field lookup folds ASCII letters only.</summary>
    private static string GoFoldField(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'A' and <= 'Z')
            {
                chars[i] = (char)(chars[i] + ('a' - 'A'));
            }
        }

        return new string(chars);
    }

    /// <summary>Mutable mirror of the reference's anonymous event-payload struct.</summary>
    private sealed class EventPayload
    {
        public bool? Success { get; set; }

        public JsonElement? Error { get; set; }

        public JsonElement? Errors { get; set; }

        public string Event { get; set; } = string.Empty;

        public JsonElement? DataError { get; set; }
    }
}
