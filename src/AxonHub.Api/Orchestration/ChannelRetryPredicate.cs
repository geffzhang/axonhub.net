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
