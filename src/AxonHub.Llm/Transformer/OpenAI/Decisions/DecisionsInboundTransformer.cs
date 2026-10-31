using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;

namespace AxonHub.Llm.Transformer.OpenAI.Decisions;

/// <summary>
/// Port of openai/decisions.InboundTransformer
/// (llm/transformer/openai/decisions/inbound.go): decodes the raw decisions
/// envelope, validates its structure and preserves the original body for
/// transparent forwarding.
/// </summary>
public sealed class DecisionsInboundTransformer : IInboundTransformer
{
    /// <summary>Port of TransformRequest.</summary>
    public LlmRequest TransformRequest(HttpRequestData? request)
    {
        if (request is null)
        {
            throw TransformerErrors.InvalidRequest("http request is nil");
        }

        var body = request.Body;
        if (body is null || body.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("request body is empty");
        }

        var contentType = HttpClientUtils.HeaderGet(request.Headers, "Content-Type");
        if (contentType.Length > 0
            && !contentType.ToLowerInvariant().Contains("application/json", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("unsupported content type: " + contentType);
        }

        var wire = Decode(body);

        if (wire.Stream == true)
        {
            throw TransformerErrors.InvalidRequest("streaming is not supported for decisions requests");
        }

        if (wire.Model.Trim().Length == 0)
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        var inputError = DecisionsValidation.ValidateInput(wire.Input);
        if (inputError is not null)
        {
            throw TransformerErrors.InvalidRequest(inputError);
        }

        if (wire.Questions is not { Count: > 0 })
        {
            throw TransformerErrors.InvalidRequest("questions are required");
        }

        return new LlmRequest
        {
            Model = wire.Model,
            RawRequest = request,
            RequestType = RequestTypes.Decisions,
            ApiFormat = ApiFormats.OpenAIDecisions,
            Decisions = new DecisionsRequest
            {
                // append([]byte(nil), request.Body...)
                Body = (byte[])body.Clone(),
                Model = wire.Model,
                Input = wire.Input,
                Questions = wire.Questions,
            },
        };
    }

    /// <summary>Port of TransformResponse: the provider body is replayed verbatim.</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response?.Decisions?.Body is not { Length: > 0 } body)
        {
            throw new InvalidOperationException("decisions response is empty");
        }

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = (byte[])body.Clone(),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
        };
    }

    /// <summary>Go: InboundTransformer.TransformStream.</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => throw TransformerErrors.InvalidRequest("decisions does not support streaming");

    /// <summary>Go: InboundTransformer.AggregateStreamChunks.</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        throw new InvalidOperationException("decisions does not support streaming");
    }

    /// <summary>
    /// Port of InboundTransformer.TransformError (inbound.go:192-237). Unlike
    /// the chat mapper, this one returns a wrapped HTTP error untouched and
    /// never rewrites statuses outside 4xx/5xx.
    /// </summary>
    public HttpResponseData TransformError(Exception? error)
    {
        if (error is null)
        {
            // The reference uses a literal body, not a marshalled struct.
            return new HttpResponseData
            {
                StatusCode = 500,
                Body = Encoding.UTF8.GetBytes(
                    "{\"error\":{\"message\":\"Internal server error\",\"type\":\"api_error\"}}"),
            };
        }

        if (GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            return new HttpResponseData
            {
                StatusCode = httpError.StatusCode,
                Body = httpError.Body,
                Headers = httpError.Headers ?? [],
            };
        }

        var transformerError = GoErrors.As<TransformerException>(error);
        if (transformerError is { Kind: TransformerErrorKind.InvalidRequest })
        {
            return ErrorResponse(400, error.Message, "invalid_request_error");
        }

        if (GoErrors.As<ResponseError>(error) is { } responseError)
        {
            return new HttpResponseData
            {
                StatusCode = responseError.StatusCode,
                Body = GoJson.Marshal(new ResponseError { Detail = responseError.Detail }),
                Headers = JsonHeaders(),
            };
        }

        return ErrorResponse(500, error.Message, "internal_server_error");
    }

    /// <summary>
    /// Port of the <c>json.Decoder</c> sequence in TransformRequest: scan the
    /// first top-level value, decode it, then probe for trailing data.
    /// </summary>
    private static DecisionsWireRequest Decode(byte[] body)
    {
        var scanError = GoJsonSyntax.ScanDecoderValue(body, 0, out var end);
        if (scanError is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode decisions request: " + scanError);
        }

        DecisionsWireRequest wire;
        try
        {
            wire = DecisionsWire.DecodeRequest(body, end);
        }
        catch (DecisionsWireException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode decisions request: " + ex.Message, ex);
        }

        var trailingError = GoJsonSyntax.ScanDecoderValue(body, end, out _);
        if (trailingError is null)
        {
            throw TransformerErrors.InvalidRequest("trailing data after decisions request");
        }

        if (!string.Equals(trailingError, "EOF", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest(
                "invalid trailing data after decisions request: " + trailingError);
        }

        return wire;
    }

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
