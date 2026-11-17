using System.Text.Json;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of internal/server/orchestrator/retry.go: decides whether a failed
/// attempt may be retried on the same channel, including the channel's
/// configured extra status codes and error text matchers (C016).
/// </summary>
public static class ChannelRetryPredicate
{
    private static readonly HashSet<string> TerminalErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "content_filter",
        "insufficient_balance",
        "insufficient_quota",
        "invalid_api_key",
        "permission_denied",
    };

    /// <summary>Port of isRetryableErrorForChannel.</summary>
    public static bool IsRetryable(Exception? error, ChannelRetrySettings? settings)
    {
        if (error is null)
        {
            return false;
        }

        if (UpstreamTransportErrors.IsUpstreamTransportError(error))
        {
            return true;
        }

        var statusCode = ExtractStatusCode(error);

        // httpclient.IsHTTPStatusCodeRetryable: 429 and all 5xx.
        if (statusCode == 429 || statusCode is >= 500 and <= 599)
        {
            return true;
        }

        return settings is not null && settings.Matches(statusCode, ErrorText(error));
    }

    public static bool IsRetryableByErrorText(Exception? error, ChannelRetrySettings? settings)
        => error is not null
            && settings is not null
            && settings.MatchesErrorText(ErrorText(error));

    /// <summary>
    /// Provider errors that cannot be repaired by switching to another
    /// channel: authentication/permission statuses, permanent quota codes,
    /// and content-filter decisions.
    /// </summary>
    public static bool IsTerminalUpstreamError(Exception? error)
    {
        if (error is null)
        {
            return false;
        }

        var statusCode = ExtractStatusCode(error);
        if (statusCode is 401 or 403)
        {
            return true;
        }

        var responseError = GoErrors.As<ResponseError>(error);
        var code = responseError?.Detail.Code;
        if (string.IsNullOrEmpty(code)
            && GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            code = ExtractErrorCode(httpError.Body);
        }

        return code is not null && TerminalErrorCodes.Contains(code);
    }

    /// <summary>Port of ExtractStatusCodeFromError.</summary>
    public static int ExtractStatusCode(Exception? error)
    {
        if (error is null)
        {
            return 0;
        }

        if (GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            return httpError.StatusCode;
        }

        return GoErrors.As<ResponseError>(error)?.StatusCode ?? 0;
    }

    private static string? ExtractErrorCode(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryGetCode(root, "error", out var code)
                || TryGetCode(root, "errors", out code))
            {
                return code;
            }
        }
        catch (JsonException)
        {
            // Malformed provider errors cannot be classified by their code.
        }

        return null;
    }

    private static bool TryGetCode(JsonElement root, string propertyName, out string? code)
    {
        code = null;
        if (!root.TryGetProperty(propertyName, out var error)
            || error.ValueKind != JsonValueKind.Object
            || !error.TryGetProperty("code", out var errorCode)
            || errorCode.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        code = errorCode.GetString();
        return !string.IsNullOrEmpty(code);
    }

    /// <summary>
    /// The text Go's <c>err.Error()</c> would produce for the upstream failure:
    /// <see cref="ResponseError"/> renders from its detail fields, while other
    /// exceptions already carry Go's wording in <see cref="Exception.Message"/>.
    /// </summary>
    private static string ErrorText(Exception error)
    {
        if (GoErrors.As<ResponseError>(error) is { } responseError)
        {
            return responseError.ToString();
        }

        return error.Message;
    }
}
