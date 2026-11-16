using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.TypeSafe;

/// <summary>
/// Port of typesafe.SystemOneInboundTransformer
/// (llm/transformer/typesafe/inbound.go): the client-facing
/// <c>POST /v1/systemone</c> and <c>POST /typesafe/v1/systemone</c> surface.
/// The validation order and error text match the reference one-for-one.
/// </summary>
public sealed class SystemOneInboundTransformer : IInboundTransformer
{
    /// <summary>Port of TransformRequest.</summary>
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

        var wireReq = DecodeRequest(request.Body);
        SystemOneJson.CoalesceQuestions(wireReq.Questions);

        if (wireReq.Stream is true)
        {
            throw TransformerErrors.InvalidRequest("streaming is not supported for systemone requests");
        }

        // Go decodes a JSON null string as "", so either spelling is missing.
        if (string.IsNullOrEmpty(wireReq.Model))
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (wireReq.State.ValueKind == JsonValueKind.Undefined)
        {
            throw TransformerErrors.InvalidRequest("state is required");
        }

        if (wireReq.Questions is null || wireReq.Questions.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("questions are required");
        }

        return new LlmRequest
        {
            Model = wireReq.Model,
            Messages = [],
            RequestType = RequestTypes.SystemOne,
            ApiFormat = ApiFormats.TypeSafeSystemOne,
            SystemOne = new SystemOneRequest
            {
                State = wireReq.State,
                Questions = wireReq.Questions,
            },
            RawRequest = request,
        };
    }

    /// <summary>Port of TransformResponse.</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("llm response is nil");
        }

        if (response.SystemOne is null)
        {
            throw new InvalidOperationException("systemone response is nil");
        }

        var wireResp = new SystemOneWireResponse
        {
            Model = response.Model,
            Answers = response.SystemOne.Answers,
        };

        if (response.Usage is { } usage)
        {
            wireResp.Usage = new SystemOneWireUsage
            {
                InputTokens = usage.PromptTokens,
                OutputTokens = usage.CompletionTokens,
            };
        }

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = GoJson.Marshal(wireResp),
            Headers = JsonHeaders(),
        };
    }

    /// <summary>
    /// Port of TransformError. Errors carrying a <see cref="ResponseError"/>
    /// keep its status and detail; everything else becomes an <c>api_error</c>
    /// with the message quoted the way Go's <c>%q</c> quotes it.
    /// </summary>
    public HttpResponseData TransformError(Exception? error)
    {
        if (error is null)
        {
            return new HttpResponseData
            {
                StatusCode = 500,
                Body = Encoding.UTF8.GetBytes(
                    """{"error":{"message":"Internal server error","type":"api_error"}}"""),
            };
        }

        if (GoErrors.As<ResponseError>(error) is { } responseError)
        {
            return new HttpResponseData
            {
                StatusCode = responseError.StatusCode,
                Body = GoJson.Marshal(responseError),
            };
        }

        return new HttpResponseData
        {
            StatusCode = 500,
            Body = Encoding.UTF8.GetBytes(
                """{"error":{"message":""" + GoCast.GoQuote(error.Message)
                + ""","type":"api_error"}}"""),
        };
    }

    /// <summary>Port of TransformStream: systemone never streams.</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => throw new InvalidOperationException("systemone does not support streaming");

    /// <summary>Port of AggregateStreamChunks: systemone never streams.</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
        => throw new InvalidOperationException("systemone does not support streaming");

    private static Dictionary<string, List<string>> JsonHeaders()
        => new(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        };

    /// <summary>
    /// Decodes with Go's <c>json.Decoder</c> semantics: one value is read from
    /// the start of the body and trailing bytes are ignored. Empty input is
    /// <c>EOF</c>, a cut-off value is <c>unexpected EOF</c>; a JSON
    /// <c>null</c> leaves the zero struct in place (then fails on model).
    /// </summary>
    private static SystemOneWireRequest DecodeRequest(byte[] body)
    {
        var scanError = GoJsonSyntax.ScanDecoderValue(body, 0, out var end);
        if (scanError is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode systemone request: " + scanError);
        }

        var rootKind = GoStructJson.RootKind(body.AsSpan(0, end));
        if (rootKind == "null")
        {
            return new SystemOneWireRequest();
        }

        if (rootKind is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode systemone request: "
                + GoStructJson.TypeMismatch(rootKind, "typesafe.systemOneWireRequest"));
        }

        try
        {
            return LlmJson.Deserialize<SystemOneWireRequest>(body.AsSpan(0, end))
                ?? new SystemOneWireRequest();
        }
        catch (JsonException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode systemone request: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }
}
