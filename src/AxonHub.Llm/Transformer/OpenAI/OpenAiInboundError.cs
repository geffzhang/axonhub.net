using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of InboundTransformer.TransformError (llm/transformer/openai/inbound.go:190-257):
/// maps orchestrator failures back to the OpenAI error envelope the client sees.
/// </summary>
public sealed partial class OpenAiInboundTransformer
{
    public HttpResponseData TransformError(Exception? error)
    {
        if (error is null)
        {
            return Unexpected();
        }

        // The reference checks the invalid-model sentinel before both the HTTP
        // error and the invalid-request sentinel; the port carries the sentinel
        // identity in TransformerException.Kind.
        var transformerError = GoErrors.As<TransformerException>(error);
        if (transformerError is { Kind: TransformerErrorKind.InvalidModel })
        {
            return ErrorResponse(
                422,
                error.Message,
                "invalid_model_error");
        }

        if (GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            // normalizeHTTPErrorStatus: statuses outside 400..599 become 502.
            var statusCode = httpError.StatusCode is >= 400 and <= 599
                ? httpError.StatusCode
                : 502;

            return new HttpResponseData
            {
                StatusCode = statusCode,
                Body = httpError.Body,
                Headers = JsonHeaders(),
            };
        }

        if (transformerError is { Kind: TransformerErrorKind.InvalidRequest })
        {
            return ErrorResponse(400, error.Message, "invalid_request_error");
        }

        if (GoErrors.As<ResponseError>(error) is { } llmError)
        {
            var statusCode = llmError.StatusCode;
            if (statusCode == 0 && llmError.Detail.Code == "context_length_exceeded")
            {
                statusCode = 400;
            }
            else if (statusCode is < 400 or > 599)
            {
                statusCode = 502;
            }

            return new HttpResponseData
            {
                StatusCode = statusCode,
                Body = GoJson.Marshal(llmError),
                Headers = JsonHeaders(),
            };
        }

        return ErrorResponse(500, error.Message, "internal_server_error");
    }

    private static HttpResponseData Unexpected()
        => ErrorResponse(
            500,
            "An unexpected error occurred",
            "unexpected_error");

    private static HttpResponseData ErrorResponse(int statusCode, string message, string type)
        => new()
        {
            StatusCode = statusCode,
            Body = GoJson.Marshal(new ResponseError
            {
                Detail = new ErrorDetail
                {
                    Message = message,
                    Type = type,
                },
            }),
            Headers = JsonHeaders(),
        };

    private static Dictionary<string, List<string>> JsonHeaders()
        => new(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        };
}
