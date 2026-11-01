using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of the error and aggregation halves of llm/transformer/openai/outbound.go
/// (outbound.go:499-574).
/// </summary>
public sealed partial class OpenAiOutboundTransformer
{
    /// <summary>
    /// Port of OutboundTransformer.AggregateStreamChunks. The reference routes
    /// speech/transcription/translation API formats to dedicated aggregators;
    /// everything else uses the chat aggregation.
    /// </summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
    {
        if (request is not null)
        {
            switch (request.ApiFormat)
            {
                case ApiFormats.OpenAISpeech:
                    return AggregateSpeechStreamChunks(chunks, out meta);
                case ApiFormats.OpenAITranscription:
                case ApiFormats.OpenAITranslation:
                    return AggregateTranscriptionStreamChunks(chunks, out meta);
            }
        }

        return OpenAiAggregator.AggregateStreamChunks(chunks, OpenAiAggregator.DefaultTransformChunk, out meta);
    }

    /// <summary>
    /// Port of OutboundTransformer.TransformError. The provider error body is
    /// parsed leniently: any decode failure falls back to the HTTP status text.
    /// </summary>
    public ResponseError TransformError(HttpClientError? error) => TransformOpenAiError(error);

    /// <summary>
    /// Port of TransformOpenAIError (error.go:14-63), shared by every OpenAI
    /// outbound transformer (the completion outbound calls it directly).
    /// </summary>
    internal static ResponseError TransformOpenAiError(HttpClientError? error)
    {
        if (error is null)
        {
            return new ResponseError
            {
                StatusCode = 500,
                Detail = new ErrorDetail
                {
                    Message = GoHttpStatus.Text(500),
                    Type = "api_error",
                },
            };
        }

        var detail = TryParseErrorDetail(error.Body);
        if (detail is not null)
        {
            return new ResponseError { StatusCode = error.StatusCode, Detail = detail };
        }

        return new ResponseError
        {
            StatusCode = error.StatusCode,
            Detail = new ErrorDetail
            {
                Message = GoHttpStatus.Text(error.StatusCode),
                Type = "api_error",
            },
        };
    }

    /// <summary>
    /// Port of the anonymous-struct decode at outbound.go:530-564: both
    /// <c>error</c> and <c>errors</c> are accepted, a type error in either one
    /// fails the whole <c>json.Unmarshal</c>, and the empty <c>error</c> message
    /// lets <c>errors</c> win. Null means "the reference would fall back".
    /// </summary>
    private static ErrorDetail? TryParseErrorDetail(byte[] body)
    {
        // Go validates the whole document before decoding, so malformed input
        // never reaches the field logic.
        if (GoJsonSyntax.Validate(body) is not null)
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            // Go's json.Unmarshal repairs ill-formed UTF-8 in string literals
            // before decoding; System.Text.Json refuses it outright.
            doc = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(body) ?? body);
        }
        catch (JsonException)
        {
            // Anything else the reference also fails to decode (fallback).
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                // Go: null leaves the zero struct (no message -> fallback);
                // every other root is a type error (also fallback).
                return null;
            }

            var fromError = DecodeErrorSection(root, "error", out var errorInvalid);
            var fromErrors = DecodeErrorSection(root, "errors", out var errorsInvalid);
            if (errorInvalid || errorsInvalid)
            {
                return null;
            }

            var section = fromError.Message.Length > 0 ? fromError : fromErrors;
            if (section.Message.Length == 0)
            {
                return null;
            }

            return new ErrorDetail
            {
                Message = section.Message,
                Type = section.Type,
                Param = NullIfEmpty(section.Param),
                Code = NullIfEmpty(section.Code),
                RequestId = NullIfEmpty(section.RequestId),
            };
        }
    }

    private static ErrorSection DecodeErrorSection(JsonElement root, string fieldName, out bool invalid)
    {
        var section = new ErrorSection();
        var anyInvalid = false;

        // encoding/json matches struct field names case-insensitively and lets a
        // later duplicate key overwrite an earlier one.
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var current = DecodeErrorObject(property.Value, out var currentInvalid);
            if (currentInvalid)
            {
                anyInvalid = true;
                continue;
            }

            section = current;
        }

        invalid = anyInvalid;
        return section;
    }

    private static ErrorSection DecodeErrorObject(JsonElement value, out bool invalid)
    {
        invalid = false;

        if (value.ValueKind == JsonValueKind.Null)
        {
            // Decoding null into a struct value is a no-op in Go.
            return new ErrorSection();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            invalid = true;
            return new ErrorSection();
        }

        var section = new ErrorSection();

        foreach (var property in value.EnumerateObject())
        {
            switch (GoFoldField(property.Name))
            {
                case "message":
                    section.Message = ReadString(property.Value, section.Message, ref invalid);
                    break;
                case "type":
                    section.Type = ReadString(property.Value, section.Type, ref invalid);
                    break;
                case "param":
                    section.Param = ReadString(property.Value, section.Param, ref invalid);
                    break;
                case "request_id":
                    section.RequestId = ReadString(property.Value, section.RequestId, ref invalid);
                    break;
                case "code":
                    section.Code = ReadAnyAsString(property.Value, ref invalid);
                    break;
            }
        }

        return section;
    }

    /// <summary>
    /// Decoding into <c>string</c>: null is a no-op, a string assigns, anything
    /// else is a type error that fails the whole unmarshal.
    /// </summary>
    private static string ReadString(JsonElement value, string current, ref bool invalid)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return current;
            case JsonValueKind.String:
                return value.GetString() ?? string.Empty;
            default:
                invalid = true;
                return current;
        }
    }

    /// <summary>
    /// Decoding into <c>any</c> accepts every JSON value (only an out-of-range
    /// number is a decode error), then <c>cast.ToString</c> maps it.
    /// </summary>
    private static string ReadAnyAsString(JsonElement value, ref bool invalid)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out var number))
            {
                // Go: "cannot unmarshal number ... into Go value of type float64".
                invalid = true;
                return string.Empty;
            }

            return GoCast.GoFloatToString(number);
        }

        return GoCast.ToString(value);
    }

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

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>Mutable mirror of the anonymous <c>{message,type,param,code,request_id}</c> struct.</summary>
    private sealed class ErrorSection
    {
        public string Message { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string Param { get; set; } = string.Empty;

        public string RequestId { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;
    }
}
