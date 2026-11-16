using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Jina;

/// <summary>
/// Port of jina.RerankInboundTransformer (llm/transformer/jina/rerank_inbound.go):
/// the /v1/rerank and /jina/v1/rerank surfaces. Validation errors take the
/// reference's 500 <c>api_error</c> envelope because the inbound TransformError
/// only special-cases <see cref="ResponseError"/>.
/// </summary>
public sealed class JinaRerankInboundTransformer : IInboundTransformer
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

        var body = request.Body;
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode rerank request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind is not null && rootKind != "null")
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode rerank request: " + GoStructJson.TypeMismatch(rootKind, "jina.RerankRequest"));
        }

        JinaRerankRequest jinaReq;
        try
        {
            jinaReq = LlmJson.Deserialize<JinaRerankRequest>(body) ?? new JinaRerankRequest();
        }
        catch (JsonException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode rerank request: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        if (jinaReq.Model.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (jinaReq.Query.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("query is required");
        }

        if (jinaReq.Documents.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("documents are required");
        }

        return new LlmRequest
        {
            Model = jinaReq.Model,
            RequestType = RequestTypes.Rerank,
            ApiFormat = ApiFormats.JinaRerank,
            Rerank = new RerankRequest
            {
                Query = jinaReq.Query,
                Documents = jinaReq.Documents,
                TopN = jinaReq.TopN,
                ReturnDocuments = jinaReq.ReturnDocuments,
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

        if (response.Rerank is null)
        {
            throw new InvalidOperationException("rerank response is nil");
        }

        var rerank = response.Rerank;

        // Go: make([]RerankResult, len(...)) — always a non-nil slice.
        var results = new List<JinaRerankResult>();
        foreach (var result in rerank.Results)
        {
            results.Add(new JinaRerankResult
            {
                Index = result.Index,
                RelevanceScore = result.RelevanceScore,
                Document = result.Document is { } doc
                    ? new JinaRerankDocument { Text = doc.Text }
                    : null,
            });
        }

        var jinaResp = new JinaRerankResponse
        {
            Model = response.Model,
            Object = rerank.Object,
            Results = results,
        };

        if (response.Usage is { } usage)
        {
            jinaResp.Usage = new JinaRerankUsage
            {
                PromptTokens = (int)usage.PromptTokens,
                TotalTokens = (int)usage.TotalTokens,
                Cost = usage.Cost,
            };
        }

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = GoJson.Marshal(jinaResp),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
        };
    }

    /// <summary>Port of TransformError: only ResponseError gets its own envelope.</summary>
    public HttpResponseData TransformError(Exception? error)
    {
        if (error is null)
        {
            return ApiErrorResponse();
        }

        if (GoErrors.As<ResponseError>(error) is { } respErr)
        {
            byte[] body;
            try
            {
                body = GoJson.Marshal(new Dictionary<string, object?> { ["error"] = respErr.Detail });
            }
            catch (Exception)
            {
                return new HttpResponseData
                {
                    StatusCode = 500,
                    Body = GoJson.Marshal(new Dictionary<string, object?>
                    {
                        ["error"] = new ErrorDetail
                        {
                            Message = "Failed to marshal error",
                            Type = "api_error",
                        },
                    }),
                };
            }

            return new HttpResponseData
            {
                StatusCode = respErr.StatusCode,
                Body = body,
            };
        }

        return ApiErrorResponse();
    }

    /// <summary>Port of TransformStream: rerank has no streaming form.</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => throw new InvalidOperationException("rerank does not support streaming");

    /// <summary>Port of AggregateStreamChunks: rerank has no streaming form.</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        throw new InvalidOperationException("rerank does not support streaming");
    }

    private static HttpResponseData ApiErrorResponse()
        => new()
        {
            StatusCode = 500,
            Body = GoJson.Marshal(new Dictionary<string, object?>
            {
                ["error"] = new ErrorDetail
                {
                    Message = "Internal server error",
                    Type = "api_error",
                },
            }),
        };
}
