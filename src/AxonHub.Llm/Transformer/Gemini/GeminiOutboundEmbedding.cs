using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Gemini;

/// <summary>Port of gemini.EmbedContentConfig (embedding.go:15-22).</summary>
public sealed class EmbedContentConfig
{
    /// <summary>Type of task for which the embedding will be used.</summary>
    [JsonPropertyName("taskType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string TaskType { get; set; } = string.Empty;

    /// <summary>Title for the text. Only applicable when TaskType is RETRIEVAL_DOCUMENT.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Reduced dimension for the output embedding.</summary>
    [JsonPropertyName("outputDimensionality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputDimensionality { get; set; }
}

/// <summary>Port of gemini.ContentEmbedding (embedding.go:25-28).</summary>
public sealed class ContentEmbedding
{
    /// <summary>A list of floats representing an embedding.</summary>
    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<float>? Values { get; set; }
}

/// <summary>Port of gemini.EmbedContentRequest (embedding.go:31-37).</summary>
public sealed class EmbedContentRequest
{
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Model { get; set; } = string.Empty;

    /// <summary>Go: <c>content</c> carries no omitempty tag, so it is always emitted.</summary>
    [JsonPropertyName("content")]
    public Content? Content { get; set; }

    [JsonPropertyName("taskType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string TaskType { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("outputDimensionality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputDimensionality { get; set; }
}

/// <summary>Port of gemini.EmbedContentResponse (embedding.go:40-42).</summary>
public sealed class EmbedContentResponse
{
    [JsonPropertyName("embedding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContentEmbedding? Embedding { get; set; }
}

/// <summary>Port of gemini.BatchEmbedContentsRequest (embedding.go:45-47).</summary>
public sealed class BatchEmbedContentsRequest
{
    /// <summary>Go: <c>requests</c> carries no omitempty tag, so it is always emitted.</summary>
    [JsonPropertyName("requests")]
    public List<EmbedContentRequest>? Requests { get; set; }
}

/// <summary>Port of gemini.BatchEmbedContentsResponse (embedding.go:50-52).</summary>
public sealed class BatchEmbedContentsResponse
{
    [JsonPropertyName("embeddings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ContentEmbedding>? Embeddings { get; set; }
}

public sealed partial class GeminiOutboundTransformer
{
    /// <summary>Port of transformEmbeddingRequest (embedding.go:55-162).</summary>
    public HttpRequestData TransformEmbeddingRequest(LlmRequest llmReq)
    {
        if (llmReq.Embedding is null)
        {
            throw new InvalidOperationException("embedding request is nil in llm.Request");
        }

        if (llmReq.Model.Length == 0)
        {
            throw new InvalidOperationException("model is required");
        }

        // Collect all input texts
        var texts = EmbeddingInputToTexts(llmReq.Embedding.Input) ?? [];
        if (texts.Count == 0)
        {
            throw new InvalidOperationException("embedding input is empty");
        }

        // Map task from llm.EmbeddingRequest to Gemini TaskType
        var taskType = MapEmbeddingTaskType(llmReq.Embedding.Task ?? "");

        // Map dimensions
        int? outputDim = llmReq.Embedding.Dimensions;

        var modelRef = "models/" + llmReq.Model;

        byte[] body;
        var url = string.Empty;

        if (texts.Count == 1)
        {
            // Single text: use embedContent
            var req = new EmbedContentRequest
            {
                Model = modelRef,
                Content = new Content
                {
                    Parts = [new Part { Text = texts[0] }],
                },
                TaskType = taskType,
                OutputDimensionality = outputDim,
            };

            try
            {
                body = GoJson.Marshal(req);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("failed to marshal embed content request: " + ex.Message, ex);
            }

            url = BuildEmbeddingUrl(llmReq.Model, false);
        }
        else
        {
            // Multiple texts: use batchEmbedContents
            var requests = new List<EmbedContentRequest>(texts.Count);
            foreach (var text in texts)
            {
                requests.Add(new EmbedContentRequest
                {
                    Model = modelRef,
                    Content = new Content
                    {
                        Parts = [new Part { Text = text }],
                    },
                    TaskType = taskType,
                    OutputDimensionality = outputDim,
                });
            }

            var batchReq = new BatchEmbedContentsRequest
            {
                Requests = requests,
            };

            try
            {
                body = GoJson.Marshal(batchReq);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("failed to marshal batch embed content request: " + ex.Message, ex);
            }

            url = BuildEmbeddingUrl(llmReq.Model, true);
        }

        // Prepare headers
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
            ["Accept"] = ["application/json"],
        };

        // Prepare authentication: unlike the chat and image paths, the
        // embedding path always sets the auth config, even for an empty key.
        var apiKey = _config.ApiKeyProvider?.Get() ?? "";
        var authConfig = new AuthConfig
        {
            Type = AuthTypes.ApiKey,
            ApiKey = apiKey,
            HeaderKey = "x-goog-api-key",
        };

        return new HttpRequestData
        {
            Method = "POST",
            Url = url,
            Headers = headers,
            Body = body,
            Auth = authConfig,
            RequestType = RequestTypes.Embedding,
            ApiFormat = ApiFormats.GeminiEmbedding,
            SkipInboundQueryMerge = true,
        };
    }

    /// <summary>Port of buildEmbeddingURL (embedding.go:165-187).</summary>
    public string BuildEmbeddingUrl(string model, bool isBatch)
    {
        var version = _config.ApiVersion ?? "";
        if (version.Length == 0)
        {
            version = GeminiConstants.DefaultAPIVersion;
        }

        var action = isBatch ? "batchEmbedContents" : "embedContent";

        // For Vertex AI platform
        if (_config.PlatformType == GeminiConstants.PlatformVertex)
        {
            var baseUrl = TrimSingleTrailingSlash(ResolveBaseUrl());
            if (baseUrl.Contains("/v1/", StringComparison.Ordinal))
            {
                return $"{baseUrl}/publishers/google/models/{model}:{action}";
            }

            return $"{baseUrl}/v1/publishers/google/models/{model}:{action}";
        }

        return $"{ResolveBaseUrl()}/{version}/models/{model}:{action}";
    }

    /// <summary>Port of transformEmbeddingResponse (embedding.go:190-222).</summary>
    public LlmResponse TransformEmbeddingResponse(HttpResponseData httpResp)
    {
        if (httpResp.StatusCode >= 400)
        {
            throw TransformError(new HttpClientError(
                string.Empty, string.Empty, httpResp.StatusCode, string.Empty, httpResp.Body));
        }

        if (httpResp.Body is not { Length: > 0 })
        {
            throw new InvalidOperationException("response body is empty");
        }

        // Try parsing as batch response first (has "embeddings" array). A JSON
        // "null" body decodes to null here and falls through to the single shape.
        try
        {
            var batchResp = LlmJson.Deserialize<BatchEmbedContentsResponse>(httpResp.Body);
            if (batchResp?.Embeddings is { Count: > 0 })
            {
                return ConvertBatchEmbeddingResponse(batchResp);
            }
        }
        catch (JsonException)
        {
            // Falls through to the single embedContent shape below.
        }

        EmbedContentResponse singleResp;
        try
        {
            singleResp = LlmJson.Deserialize<EmbedContentResponse>(httpResp.Body)
                ?? new EmbedContentResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal embed content response: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }

        return ConvertSingleEmbeddingResponse(singleResp);
    }

    /// <summary>Port of convertSingleEmbeddingResponse (embedding.go:225-245).</summary>
    public static LlmResponse ConvertSingleEmbeddingResponse(EmbedContentResponse resp)
    {
        List<EmbeddingData>? data = null;
        if (resp.Embedding is not null)
        {
            data =
            [
                new EmbeddingData
                {
                    Object = "embedding",
                    Embedding = new Embedding { Values = Float32sToFloat64s(resp.Embedding.Values) },
                    Index = 0,
                },
            ];
        }

        return new LlmResponse
        {
            RequestType = RequestTypes.Embedding,
            ApiFormat = ApiFormats.GeminiEmbedding,
            Embedding = new EmbeddingResponse
            {
                Object = "list",
                Data = data,
            },
        };
    }

    /// <summary>Port of convertBatchEmbeddingResponse (embedding.go:248-266).</summary>
    public static LlmResponse ConvertBatchEmbeddingResponse(BatchEmbedContentsResponse resp)
    {
        var embeddings = resp.Embeddings ?? [];
        var data = new List<EmbeddingData>(embeddings.Count);
        for (var i = 0; i < embeddings.Count; i++)
        {
            data.Add(new EmbeddingData
            {
                Object = "embedding",
                Embedding = new Embedding { Values = Float32sToFloat64s(embeddings[i].Values) },
                Index = i,
            });
        }

        return new LlmResponse
        {
            RequestType = RequestTypes.Embedding,
            ApiFormat = ApiFormats.GeminiEmbedding,
            Embedding = new EmbeddingResponse
            {
                Object = "list",
                Data = data,
            },
        };
    }

    /// <summary>Port of embeddingInputToTexts (embedding.go:269-280).</summary>
    public static List<string>? EmbeddingInputToTexts(EmbeddingInput input)
    {
        if (input.String.Length > 0)
        {
            return [input.String];
        }

        if (input.StringArray is { Count: > 0 })
        {
            return input.StringArray;
        }

        // Token arrays (IntArray, IntArrayArray) are not supported by Gemini embedding API.
        return null;
    }

    /// <summary>Port of mapEmbeddingTaskType (embedding.go:283-304).</summary>
    public static string MapEmbeddingTaskType(string task)
    {
        switch (task.ToLowerInvariant())
        {
            case "retrieval.query":
            case "retrieval_query":
                return "RETRIEVAL_QUERY";
            case "retrieval.passage":
            case "retrieval_document":
                return "RETRIEVAL_DOCUMENT";
            case "semantic_similarity":
            case "text-matching":
                return "SEMANTIC_SIMILARITY";
            case "classification":
                return "CLASSIFICATION";
            case "clustering":
                return "CLUSTERING";
            case "question_answering":
                return "QUESTION_ANSWERING";
            case "fact_verification":
                return "FACT_VERIFICATION";
            case "code_retrieval_query":
                return "CODE_RETRIEVAL_QUERY";
            default:
                return "";
        }
    }

    /// <summary>Port of float32sToFloat64s (embedding.go:307-314).</summary>
    public static List<double> Float32sToFloat64s(List<float>? values)
    {
        if (values is null)
        {
            return [];
        }

        var result = new List<double>(values.Count);
        foreach (var v in values)
        {
            result.Add(v);
        }

        return result;
    }
}
