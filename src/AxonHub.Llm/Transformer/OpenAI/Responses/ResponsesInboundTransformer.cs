using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>Port of responses.InboundTransformer (llm/transformer/openai/responses/inbound.go).</summary>
public sealed class ResponsesInboundTransformer : IInboundTransformer
{
    /// <summary>Port of TransformRequest: client HTTP request to unified request.</summary>
    public LlmRequest TransformRequest(HttpRequestData? request)
    {
        if (request is null)
        {
            throw TransformerErrors.InvalidRequest("http request is nil");
        }

        if (request.Body is null || request.Body.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("request body is empty");
        }

        // Go probes the raw body with gjson before decoding. The probe runs
        // first so stream_id wins over a malformed body (see C060 for the
        // gjson-vs-JsonDocument leniency difference).
        if (HasStreamId(request.Body))
        {
            throw new ResponseError
            {
                StatusCode = 400,
                Detail = new ErrorDetail
                {
                    Message = "Unsupported parameter: stream_id",
                    Type = "invalid_request_error",
                    Param = "stream_id",
                },
            };
        }

        var contentType = HttpClientUtils.HeaderGet(request.Headers, "Content-Type");
        if (contentType.Length > 0
            && !contentType.ToLowerInvariant().Contains("application/json", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("unsupported content type: " + contentType);
        }

        // Go validates the whole document before decoding, so syntax errors are
        // always reported with encoding/json's own wording.
        var syntaxError = GoJsonSyntax.Validate(request.Body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode responses api request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(request.Body);
        if (rootKind is not null && rootKind != "null")
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode responses api request: "
                + GoStructJson.TypeMismatch(rootKind, "responses.Request"));
        }

        ResponsesRequest? req;
        try
        {
            // A JSON null leaves the zero struct in place, as in Go.
            req = rootKind == "null" ? new ResponsesRequest() : LlmJson.Deserialize<ResponsesRequest>(request.Body);
        }
        catch (JsonException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode responses api request: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }

        if (req is null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode responses api request: unexpected null");
        }

        if (string.IsNullOrEmpty(req.Model))
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        return ResponsesInboundConvert.ConvertToLlmRequest(req, request.Body);
    }

    /// <summary>Port of TransformResponse: unified response to the client HTTP response.</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("chat completion response is nil");
        }

        var resp = ResponsesInboundConvert.ConvertToResponsesApiResponse(
            ResponsesNamespace.MapResponseFunctionNames(response, false)!);

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = GoJson.Marshal(resp),
            Headers = ResponseHeaders(),
        };
    }

    /// <summary>
    /// The reference's inbound stream transformer (inbound_stream.go) is not
    /// ported in this slice; tracked as a real gap in the difference ledger.
    /// </summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => throw new NotSupportedException("responses streaming is not ported");

    /// <summary>Not ported in this slice (see TransformStream).</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        throw new NotSupportedException("responses stream aggregation is not ported");
    }

    /// <summary>Port of TransformError: LLM error to the Responses API error envelope.</summary>
    public HttpResponseData TransformError(Exception? error)
    {
        if (error is null)
        {
            return ErrorResponse(500, "internal server error", "internal_error");
        }

        // The reference checks the invalid-model sentinel before the response
        // error, and the invalid-request sentinel only after it.
        var transformerError = GoErrors.As<TransformerException>(error);
        if (transformerError is { Kind: TransformerErrorKind.InvalidModel })
        {
            return ErrorResponse(422, error.Message, "invalid_model_error");
        }

        if (GoErrors.As<ResponseError>(error) is { } llmError)
        {
            return Envelope(
                llmError.StatusCode,
                ResponsesApiErrorDetail.FromLlm(llmError.Detail));
        }

        if (GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            // The reference returns the HTTP error unchanged (no status
            // normalization); status, body and headers travel to the client.
            return new HttpResponseData
            {
                StatusCode = httpError.StatusCode,
                Body = httpError.Body,
                Headers = JsonHeaders(),
            };
        }

        if (transformerError is { Kind: TransformerErrorKind.InvalidRequest })
        {
            return ErrorResponse(400, error.Message, "invalid_request_error");
        }

        return ErrorResponse(500, error.Message, "internal_error");
    }

    /// <summary>gjson.GetBytes(body, "stream_id").Exists() for well-formed bodies.</summary>
    private static bool HasStreamId(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("stream_id", out _);
        }
        catch (JsonException)
        {
            // The reference still finds a top-level stream_id in some malformed
            // bodies; recorded as a difference rather than reimplementing gjson.
            return false;
        }
    }

    private static HttpResponseData Envelope(int statusCode, ResponsesApiErrorDetail detail)
        => new()
        {
            StatusCode = statusCode,
            Body = GoJson.Marshal(new ResponsesApiError { Error = detail }),
            Headers = JsonHeaders(),
        };

    private static HttpResponseData ErrorResponse(int statusCode, string message, string type)
        => Envelope(statusCode, new ResponsesApiErrorDetail { Message = message, Type = type });

    private static Dictionary<string, List<string>> JsonHeaders()
        => new(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        };

    private static Dictionary<string, List<string>> ResponseHeaders()
        => new(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
            ["Cache-Control"] = ["no-cache"],
        };
}

/// <summary>Port of responses.ResponseError: the envelope the client sees.</summary>
public sealed class ResponsesApiError
{
    [JsonPropertyName("error")]
    public ResponsesApiErrorDetail Error { get; set; } = new();
}

/// <summary>Port of responses.ResponseErrorDetail (field order matches Go).</summary>
public sealed class ResponsesApiErrorDetail
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    [JsonPropertyName("param")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Param { get; set; }

    /// <summary>Re-shapes an <see cref="ErrorDetail"/> the way TransformError does.</summary>
    internal static ResponsesApiErrorDetail FromLlm(ErrorDetail detail)
        => new()
        {
            Message = detail.Message,
            Type = detail.Type,
            Code = string.IsNullOrEmpty(detail.Code) ? null : detail.Code,
            Param = string.IsNullOrEmpty(detail.Param) ? null : detail.Param,
        };
}
