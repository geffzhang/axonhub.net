using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.EmbeddingInboundTransformer
/// (llm/transformer/openai/embedding_inbound.go): the /v1/embeddings surface.
/// The error text, the content-type default and the validation order match the
/// reference one-for-one.
/// </summary>
public sealed class EmbeddingInboundTransformer : IInboundTransformer
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

        var llmRequest = new LlmRequest
        {
            Model = embeddingRequest.Model,
            Messages = [],
            RawRequest = request,
            RequestType = RequestTypes.Embedding,
            ApiFormat = ApiFormats.OpenAIEmbedding,
            Stream = null,
            Embedding = new EmbeddingRequest
            {
                Input = embeddingRequest.Input,
                EncodingFormat = NullIfEmpty(embeddingRequest.EncodingFormat),
                Dimensions = embeddingRequest.Dimensions,
                User = NullIfEmpty(embeddingRequest.User),
            },
        };

        if (!string.IsNullOrEmpty(embeddingRequest.User))
        {
            llmRequest.User = embeddingRequest.User;
        }

        return llmRequest;
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
        var data = new List<OpenAiEmbeddingData>();
        foreach (var item in embedding.Data ?? [])
        {
            data.Add(new OpenAiEmbeddingData
            {
                Object = item.Object,
                Embedding = item.Embedding,
                Index = item.Index,
            });
        }

        var apiResponse = new OpenAiEmbeddingResponse
        {
            Object = embedding.Object,
            Data = data,
            Model = response.Model,
        };

        // Go's EmbeddingUsage is a struct, so an absent unified usage still
        // emits the zero token counters.
        apiResponse.Usage = response.Usage is { } usage
            ? new OpenAiEmbeddingUsage
            {
                PromptTokens = usage.PromptTokens,
                TotalTokens = usage.TotalTokens,
                Cost = usage.Cost,
            }
            : new OpenAiEmbeddingUsage();

        var body = GoJson.Marshal(apiResponse);

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

    /// <summary>Go: EmbeddingInboundTransformer.TransformStream.</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => throw TransformerErrors.InvalidRequest("embeddings do not support streaming");

    /// <summary>Go: EmbeddingInboundTransformer.AggregateStreamChunks.</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        throw new InvalidOperationException("embeddings do not support streaming");
    }

    /// <summary>Go: TransformError delegates to the chat transformer.</summary>
    public HttpResponseData TransformError(Exception? error)
        => new OpenAiInboundTransformer().TransformError(error);

    /// <summary>
    /// Decodes the client body the way Go's json.Unmarshal does: syntax errors
    /// and root-type mismatches keep encoding/json's wording, and a JSON
    /// <c>null</c> leaves the zero request in place (then fails on model).
    /// </summary>
    private static OpenAiEmbeddingRequest DecodeRequest(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode embedding request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind == "null")
        {
            return new OpenAiEmbeddingRequest();
        }

        if (rootKind is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode embedding request: "
                + GoStructJson.TypeMismatch(rootKind, "openai.EmbeddingRequest"));
        }

        try
        {
            return LlmJson.Deserialize<OpenAiEmbeddingRequest>(body) ?? new OpenAiEmbeddingRequest();
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

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
