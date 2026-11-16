using System.Net.Sockets;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of internal/server/orchestrator/upstream_transport_error.go: tells a
/// broken upstream connection apart from an HTTP error body and a client
/// cancellation, and gives the former a 502 classification.
/// </summary>
public static class UpstreamTransportErrors
{
    public const string ErrorType = "upstream_error";

    public const string StreamInterruptedCode = "upstream_stream_interrupted";

    /// <summary>Port of IsUpstreamTransportError.</summary>
    public static bool IsUpstreamTransportError(Exception? error)
    {
        if (error is null)
        {
            return false;
        }

        if (error is OperationCanceledException)
        {
            return false;
        }

        // Already carries a status code or structured detail: nothing to classify.
        if (GoErrors.As<HttpClientError>(error) is not null
            || GoErrors.As<ResponseError>(error) is not null)
        {
            return false;
        }

        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is StreamIncompleteException
                or HttpRequestException
                or IOException
                or SocketException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of ClassifyUpstreamTransportError: wraps transport failures as a 502 ResponseError.</summary>
    public static Exception Classify(Exception error)
    {
        if (!IsUpstreamTransportError(error))
        {
            return error;
        }

        return new ResponseError
        {
            StatusCode = 502,
            Detail = new ErrorDetail
            {
                Message = "Upstream provider closed the connection before the response completed: " + error.Message,
                Type = ErrorType,
                Code = StreamInterruptedCode,
            },
            CauseException = error,
        };
    }
}
