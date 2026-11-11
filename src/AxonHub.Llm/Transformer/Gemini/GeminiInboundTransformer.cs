using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Gemini;

/// <summary>
/// Port of llm/transformer/gemini/inbound.go: the native Gemini inbound
/// transformer (<c>POST /v1beta/models/{model}:generateContent</c> and
/// <c>:streamGenerateContent</c>), including the streaming half
/// (inbound_stream.go) and the shared aggregator (aggregator.go).
/// </summary>
public sealed class GeminiInboundTransformer : IInboundTransformer
{
    /// <summary>Port of ErrInvalidRequestURL (inbound.go:18).</summary>
    public static readonly Exception ErrInvalidRequestURL = new PipelineSentinelException("invalid request URL");

    /// <summary>
    /// Port of extractRequestParams (inbound.go:29-50): the model is the last
    /// path segment up to the first colon, the action after it.
    /// </summary>
    private static (string Model, bool Stream) ExtractRequestParams(HttpRequestData httpReq)
    {
        var urlParts = httpReq.Path.Split('/');
        if (urlParts.Length < 1)
        {
            throw InvalidRequestUrl(httpReq.Path);
        }

        var suffix = urlParts[^1];
        var suffixParts = suffix.Split(':');
        if (suffixParts.Length < 2)
        {
            throw InvalidRequestUrl(httpReq.Path);
        }

        switch (suffixParts[1])
        {
            case "generateContent":
                return (suffixParts[0], false);
            case "streamGenerateContent":
                return (suffixParts[0], true);
            default:
                throw InvalidRequestUrl(httpReq.Path);
        }
    }

    /// <summary>Go: <c>fmt.Errorf("%w: invalid request path: %s", ErrInvalidRequestURL, path)</c>.</summary>
    private static WrappedException InvalidRequestUrl(string path)
        => new("invalid request URL: invalid request path: " + path, ErrInvalidRequestURL);

    /// <summary>Port of TransformRequest (inbound.go:53-88).</summary>
    public LlmRequest TransformRequest(HttpRequestData? request)
    {
        if (request is null)
        {
            throw TransformerErrors.InvalidRequest("http request is nil");
        }

        if (request.Body is not { Length: > 0 })
        {
            throw TransformerErrors.InvalidRequest("request body is empty");
        }

        var (model, stream) = ExtractRequestParams(request);

        GenerateContentRequest geminiReq;
        try
        {
            geminiReq = LlmJson.Deserialize<GenerateContentRequest>(request.Body)
                ?? new GenerateContentRequest();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode gemini request: " + OpenAiInboundTransformerJsonErrorText(ex), ex);
        }

        // Validate required fields
        if (geminiReq.Contents is not { Count: > 0 })
        {
            throw TransformerErrors.InvalidRequest("contents are required");
        }

        var req = GeminiInboundConvert.ConvertGeminiToLlmRequest(geminiReq);

        req.Stream = stream;
        req.Model = model;

        return req;
    }

    /// <summary>Port of TransformResponse (inbound.go:91-112).</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("chat completion response is nil");
        }

        // Convert to Gemini response format (non-streaming)
        var geminiResp = GeminiInboundConvert.ConvertLLMToGeminiResponse(response, false);
        var body = GoJson.Marshal(geminiResp);

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = body,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Cache-Control"] = ["no-cache"],
            },
        };
    }

    /// <summary>
    /// Port of TransformError (inbound.go:115-190): maps orchestrator failures
    /// back to the Gemini error envelope. The reference checks the sentinels in
    /// this order: invalid request URL, ResponseError, transport error, invalid
    /// request/model.
    /// </summary>
    public HttpResponseData TransformError(Exception? error)
    {
        if (error is null)
        {
            return GeminiErrorResponse(500, GoHttpStatus.Text(500), MapHttpStatusToGeminiStatus(500));
        }

        if (GoErrors.Is(error, ErrInvalidRequestURL))
        {
            return GeminiErrorResponse(404, error.Message, MapHttpStatusToGeminiStatus(404));
        }

        if (GoErrors.As<ResponseError>(error) is { } llmError)
        {
            return GeminiErrorResponse(
                llmError.StatusCode,
                llmError.Detail.Message,
                MapHttpStatusToGeminiStatus(llmError.StatusCode));
        }

        if (GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            // The reference returns the transport error untouched; the API layer
            // writes its status and raw body.
            return new HttpResponseData
            {
                StatusCode = httpError.StatusCode,
                Body = httpError.Body,
                Headers = JsonHeaders(),
            };
        }

        // Handle validation errors
        if (GoErrors.As<TransformerException>(error) is
        { Kind: TransformerErrorKind.InvalidRequest } or
        { Kind: TransformerErrorKind.InvalidModel })
        {
            return GeminiErrorResponse(400, error.Message, MapHttpStatusToGeminiStatus(400));
        }

        return GeminiErrorResponse(500, GoHttpStatus.Text(500), MapHttpStatusToGeminiStatus(500));
    }

    /// <summary>Port of mapHTTPStatusToGeminiStatus (inbound.go:193-216).</summary>
    public static string MapHttpStatusToGeminiStatus(int statusCode) => statusCode switch
    {
        400 => "INVALID_ARGUMENT",
        401 => "UNAUTHENTICATED",
        403 => "PERMISSION_DENIED",
        404 => "NOT_FOUND",
        409 => "ALREADY_EXISTS",
        429 => "RESOURCE_EXHAUSTED",
        500 => "INTERNAL",
        501 => "UNIMPLEMENTED",
        503 => "UNAVAILABLE",
        _ => "UNKNOWN",
    };

    /// <summary>Port of TransformStream (inbound_stream.go:18-28).</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => new GeminiInboundStream(this, stream);

    /// <summary>
    /// Port of TransformStreamChunk (inbound_stream.go:329-355): one unified
    /// chunk to one Gemini StreamEvent. Null means "emit nothing" — Go's
    /// <c>(nil, nil)</c> for a nil response or the [DONE] marker.
    /// </summary>
    public StreamEvent? TransformStreamChunk(LlmResponse? chatResp)
    {
        if (chatResp is null)
        {
            return null;
        }

        // Handle [DONE] marker
        if (chatResp.Object == "[DONE]")
        {
            // Gemini doesn't use [DONE] marker, but we can return null to signal the end of the stream.
            return null;
        }

        // Convert to Gemini response format (streaming)
        var geminiResp = GeminiInboundConvert.ConvertLLMToGeminiResponse(chatResp, true);
        var eventData = GoJson.Marshal(geminiResp);

        return new StreamEvent
        {
            Data = eventData,
        };
    }

    /// <summary>Port of AggregateStreamChunks (inbound_stream.go:358-363).</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
        => GeminiAggregator.AggregateStreamChunks(chunks, out meta);

    private static HttpResponseData GeminiErrorResponse(int statusCode, string message, string geminiStatus)
        => new()
        {
            StatusCode = statusCode,
            Body = GoJson.Marshal(new GeminiError
            {
                Error = new GeminiErrorDetail
                {
                    Code = statusCode,
                    Message = message,
                    Status = geminiStatus,
                },
            }),
            Headers = JsonHeaders(),
        };

    private static Dictionary<string, List<string>> JsonHeaders()
        => new(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        };

    /// <summary>
    /// Strips the decoder's position suffix so messages read like Go's
    /// (shared with the outbound transformer, see GeminiOutboundTransformer).
    /// </summary>
    private static string OpenAiInboundTransformerJsonErrorText(System.Text.Json.JsonException ex)
        => OpenAiInboundTransformer.JsonErrorText(ex);
}
