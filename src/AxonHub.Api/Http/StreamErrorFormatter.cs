using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of api.FormatStreamError (internal/server/api/chat.go:781-836): the SSE
/// error envelope, plus the message extraction it shares with request logs.
/// </summary>
/// <remarks>
/// The reference builds <c>gin.H</c> maps, and Go sorts map keys when marching
/// JSON, so the envelope keys are <c>code, message, type</c> and the outer keys
/// <c>error, request_id</c>.
/// </remarks>
public static class StreamErrorFormatter
{
    public static byte[] Format(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        // chat.go:786-795: the quota branch returns its own envelope without a
        // request_id, unlike every other error path.
        if (GoErrors.As<QuotaExhaustedException>(error) is { } quotaError)
        {
            return QuotaEnvelope(quotaError.Message);
        }

        if (GoErrors.As<ResponseError>(error) is { } responseError)
        {
            return Envelope(
                responseError.Detail.Code ?? string.Empty,
                responseError.Detail.Message,
                EmptyTo(responseError.Detail.Type, "server_error"),
                responseError.Detail.RequestId ?? string.Empty);
        }

        var errorType = "server_error";
        var errorCode = string.Empty;
        var requestId = string.Empty;
        var message = ExtractErrorMessage(error);

        if (GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            var fields = HttpErrorBodyFields.Parse(httpError.Body);
            if (fields.Type.Length > 0)
            {
                errorType = fields.Type;
            }

            if (fields.Code.Length > 0)
            {
                errorCode = fields.Code;
            }

            if (fields.RequestId.Length > 0)
            {
                requestId = fields.RequestId;
            }
        }

        return Envelope(errorCode, message, errorType, requestId);
    }

    /// <summary>Port of orchestrator.ExtractErrorMessage (request_execution.go:296-327).</summary>
    public static string ExtractErrorMessage(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (GoErrors.As<HttpClientError>(error) is not { } httpError)
        {
            if (GoErrors.As<ResponseError>(error) is { } responseError
                && !string.IsNullOrEmpty(responseError.Detail.Message))
            {
                return responseError.Detail.Message;
            }

            return error.Message;
        }

        using var document = TryParse(httpError.Body);
        if (document is not null && document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var root = document.RootElement;

            // Anthropic && OpenAI error format.
            if (root.TryGetProperty("error", out var errorObject)
                && errorObject.ValueKind == JsonValueKind.Object
                && errorObject.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }

            // Other compatible error formats: errors.0.message, then errors.message.
            if (root.TryGetProperty("errors", out var errors))
            {
                if (errors.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in errors.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.Object
                            && element.TryGetProperty("message", out var first)
                            && first.ValueKind == JsonValueKind.String
                            && !string.IsNullOrEmpty(first.GetString()))
                        {
                            return first.GetString()!;
                        }

                        break;
                    }
                }
                else if (errors.ValueKind == JsonValueKind.Object
                    && errors.TryGetProperty("message", out var second)
                    && second.ValueKind == JsonValueKind.String)
                {
                    return second.GetString() ?? string.Empty;
                }
            }
        }

        return httpError.Message;
    }

    private static byte[] Envelope(string code, string message, string type, string requestId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteString("type", type);
            writer.WriteEndObject();
            writer.WriteString("request_id", requestId);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// chat.go:788-795: <c>{"error":{"code":"quota_exhausted","message":...,
    /// "type":"quota_exhausted"}}</c> — no outer request_id.
    /// </summary>
    private static byte[] QuotaEnvelope(string message)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("code", "quota_exhausted");
            writer.WriteString("message", message);
            writer.WriteString("type", "quota_exhausted");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string EmptyTo(string value, string fallback)
        => string.IsNullOrEmpty(value) ? fallback : value;

    private static JsonDocument? TryParse(byte[] data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        try
        {
            // The reference decodes upstream payloads with json.Unmarshal,
            // which repairs ill-formed UTF-8 inside strings.
            return JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
