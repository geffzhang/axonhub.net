using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Jina;

/// <summary>
/// Port of jina.EmbeddingInboundTransformer
/// (llm/transformer/jina/embedding_inbound.go): the /jina/v1/embeddings
/// surface. Differs from the OpenAI embedding inbound only in the wire models
/// and the jina Task field; the error text and validation order match the
/// reference one-for-one.
/// </summary>
public sealed class JinaEmbeddingInboundTransformer : IInboundTransformer
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

        var contentType = HttpClientUtils.HeaderGet(request.Headers, "Content-Type");
        if (contentType.Length == 0)
        {
            contentType = "application/json";
        }

        if (!contentType.ToLowerInvariant().Contains("application/json", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("unsupported content type: " + contentType);
        }

        var embeddingRequest = DecodeRequest(request.Body);

        if (string.IsNullOrEmpty(embeddingRequest.Model))
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        ValidateEmbeddingInput(embeddingRequest.Input);

        return new LlmRequest
        {
            Model = embeddingRequest.Model,
            Messages = [],
            RawRequest = request,
            RequestType = RequestTypes.Embedding,
            ApiFormat = ApiFormats.JinaEmbedding,
            Stream = null,
            Embedding = new EmbeddingRequest
            {
                Input = embeddingRequest.Input,
                Task = embeddingRequest.Task,
                EncodingFormat = embeddingRequest.EncodingFormat,
                Dimensions = embeddingRequest.Dimensions,
                User = embeddingRequest.User,
            },
        };
    }

    /// <summary>Port of TransformResponse.</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("embedding response is nil");
        }

        if (response.Embedding is null)
        {
            throw new InvalidOperationException("embedding response missing embedding data");
        }

        var embedding = response.Embedding;

        // Go: make([]EmbeddingData, len(...)) — always a non-nil slice.
        var data = new List<JinaEmbeddingData>();
        foreach (var item in embedding.Data ?? [])
        {
            data.Add(new JinaEmbeddingData
            {
                Object = item.Object,
                Embedding = item.Embedding,
                Index = item.Index,
            });
        }

        var jinaResp = new JinaEmbeddingResponse
        {
            Object = embedding.Object,
            Data = data,
            Model = response.Model,
        };

        // Go's EmbeddingUsage is a struct, so an absent unified usage still
        // emits the zero token counters.
        jinaResp.Usage = response.Usage is { } usage
            ? new JinaEmbeddingUsage
            {
                PromptTokens = usage.PromptTokens,
                TotalTokens = usage.TotalTokens,
                Cost = usage.Cost,
            }
            : new JinaEmbeddingUsage();

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = GoJson.Marshal(jinaResp),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Cache-Control"] = ["no-cache"],
            },
        };
    }

    /// <summary>
    /// Go: TransformStream is wrapped in ErrInvalidRequest (unlike the rerank
    /// transformer's plain error).
    /// </summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => throw TransformerErrors.InvalidRequest("embeddings do not support streaming");

    /// <summary>Go: EmbeddingInboundTransformer.AggregateStreamChunks.</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        throw new InvalidOperationException("embeddings do not support streaming");
    }

    /// <summary>Go: TransformError delegates to the rerank inbound transformer.</summary>
    public HttpResponseData TransformError(Exception? error)
        => new JinaRerankInboundTransformer().TransformError(error);

    /// <summary>
    /// Decodes the client body the way Go's json.Unmarshal does: syntax errors
    /// and root-type mismatches keep encoding/json's wording, and a JSON
    /// <c>null</c> leaves the zero request in place (then fails on model).
    /// </summary>
    private static JinaEmbeddingRequest DecodeRequest(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode embedding request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind == "null")
        {
            return new JinaEmbeddingRequest();
        }

        if (rootKind is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode embedding request: "
                + GoStructJson.TypeMismatch(rootKind, "jina.EmbeddingRequest"));
        }

        try
        {
            return LlmJson.Deserialize<JinaEmbeddingRequest>(body) ?? new JinaEmbeddingRequest();
        }
        catch (JsonException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode embedding request: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }

    /// <summary>Port of validateEmbeddingInput (the branch order is observable).</summary>
    private static void ValidateEmbeddingInput(EmbeddingInput input)
    {
        if (input.StringArray is { } strings)
        {
            if (strings.Count == 0)
            {
                throw TransformerErrors.InvalidRequest("input cannot be empty array");
            }

            for (var i = 0; i < strings.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(strings[i]))
                {
                    throw TransformerErrors.InvalidRequest(
                        "input[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "] cannot be empty string");
                }
            }

            return;
        }

        if (input.IntArray is { } ids)
        {
            if (ids.Count == 0)
            {
                throw TransformerErrors.InvalidRequest("input cannot be empty array");
            }

            return;
        }

        if (input.IntArrayArray is { } batches)
        {
            if (batches.Count == 0)
            {
                throw TransformerErrors.InvalidRequest("input cannot be empty array");
            }

            for (var i = 0; i < batches.Count; i++)
            {
                if (batches[i] is null || batches[i].Count == 0)
                {
                    throw TransformerErrors.InvalidRequest(
                        "input[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "] cannot be empty array");
                }
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(input.String))
        {
            throw TransformerErrors.InvalidRequest("input cannot be empty string");
        }
    }
}
