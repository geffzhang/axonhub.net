using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>Port of responses.CompactInboundTransformer (compact_inbound.go).</summary>
public sealed class ResponsesCompactInboundTransformer : IInboundTransformer
{
    private const string StreamingNotSupported = "compact does not support streaming";

    /// <summary>Port of TransformRequest: HTTP compact request to unified request.</summary>
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

        var contentType = HttpClientUtils.HeaderGet(request.Headers, "Content-Type");
        if (contentType.Length > 0
            && !contentType.ToLowerInvariant().Contains("application/json", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("unsupported content type: " + contentType);
        }

        var syntaxError = GoJsonSyntax.Validate(request.Body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode compact request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(request.Body);
        if (rootKind is not null && rootKind != "null")
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode compact request: "
                + GoStructJson.TypeMismatch(rootKind, "responses.CompactAPIRequest"));
        }

        CompactApiRequest? req;
        try
        {
            req = rootKind == "null"
                ? new CompactApiRequest()
                : LlmJson.Deserialize<CompactApiRequest>(request.Body);
        }
        catch (JsonException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode compact request: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }

        if (req is null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode compact request: unexpected null");
        }

        if (string.IsNullOrEmpty(req.Model))
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        List<Message> inputMessages;
        try
        {
            inputMessages = ResponsesCompactConvert.ConvertCompactInputToMessages(
                req.Input ?? new ResponsesInput());
        }
        catch (TransformerException ex)
        {
            // fmt.Errorf("%w: failed to convert input: %w", ErrInvalidRequest, err)
            throw TransformerErrors.InvalidRequest("failed to convert input: " + ex.Message, ex);
        }

        var llmReq = new LlmRequest
        {
            Model = req.Model,
            Messages = [],
            RequestType = RequestTypes.Compact,
            ApiFormat = ApiFormats.OpenAIResponseCompact,
            Stream = false,
            Compact = new CompactRequest
            {
                Input = inputMessages,
                Instructions = ResponsesConvertUtil.NullIfEmpty(req.Instructions),
                PromptCacheKey = ResponsesConvertUtil.NullIfEmpty(req.PromptCacheKey),
            },
        };
        ResponsesRequestExtensions.AttachOpenAIResponsesRawRequestFields(
            llmReq, request.Body, ResponsesRequestExtensions.RawCompactRequestFields);

        return llmReq;
    }

    /// <summary>Port of TransformResponse: unified response to HTTP compact response.</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("compact response is nil");
        }

        if (response.Compact is null)
        {
            throw new InvalidOperationException("compact response missing compact data");
        }

        var outputItems = ResponsesCompactConvert.ConvertCompactMessagesToItems(
            response.Compact.Output);

        ResponsesUsage? usage = null;
        if (response.Usage is not null)
        {
            usage = ResponsesUsage.ConvertLlmUsageToResponsesUsage(response.Usage);
        }

        var body = GoJson.Marshal(new CompactApiResponse
        {
            Id = response.Compact.Id,
            CreatedAt = response.Compact.CreatedAt,
            Object = response.Compact.Object,
            Model = ResponsesConvertUtil.NullIfEmpty(response.Model),
            Instructions = response.Compact.Instructions,
            Output = outputItems,
            Usage = usage,
        });

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

    /// <summary>Go: ErrInvalidRequest wrapped around the same wording.</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => throw TransformerErrors.InvalidRequest(StreamingNotSupported);

    /// <summary>Go returns a plain (unwrapped) error here.</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        throw new InvalidOperationException(StreamingNotSupported);
    }

    /// <summary>Port of TransformError: delegates to the Responses error mapping.</summary>
    public HttpResponseData TransformError(Exception? error)
        => new ResponsesInboundTransformer().TransformError(error);
}
