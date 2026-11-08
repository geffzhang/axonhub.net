using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Jina;

/// <summary>
/// Port of jina.OutboundTransformer (llm/transformer/jina/outbound.go): the
/// Jina Rerank and Embedding provider protocols. Validation errors are plain
/// errors in the reference (no ErrInvalidRequest wrap), so they stay plain
/// exceptions here.
/// </summary>
public sealed class JinaOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly JinaOutboundConfig _config;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    public JinaOutboundTransformer(JinaOutboundConfig config)
    {
        ValidateConfig(config);
        _config = config;

        _config.BaseUrl = HttpClientUtils.NormalizeBaseUrl(
            _config.BaseUrl,
            _config.EndpointPath.Length > 0 ? string.Empty : "v1");
    }

    /// <summary>Port of NewOutboundTransformer(baseURL, apiKey).</summary>
    public static JinaOutboundTransformer Create(string baseUrl, string apiKey)
        => Create(baseUrl, new StaticApiKeyProvider(apiKey));

    /// <summary>
    /// Builds a transformer with a key provider and its audit index, mirroring
    /// the OpenAI outbound factory (getAPIKeyProvider + channel_api_key_index).
    /// </summary>
    public static JinaOutboundTransformer Create(
        string baseUrl,
        IApiKeyProvider apiKeyProvider,
        int? channelApiKeyIndex = null)
        => new(new JinaOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = channelApiKeyIndex,
        });

    /// <summary>Port of APIFormat: the primary format, routing happens per request type.</summary>
    public string ApiFormat => ApiFormats.JinaRerank;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>Port of TransformRequest.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("llm request is nil");
        }

        switch (request.RequestType)
        {
            case RequestTypes.Rerank:
                return TransformRerankRequest(request);
            case RequestTypes.Embedding:
                return TransformEmbeddingRequest(request);
            case RequestTypes.Compact:
                throw TransformerErrors.InvalidRequest("compact is only supported by OpenAI Responses API");
            default:
                throw TransformerErrors.InvalidRequest(request.RequestType + " is not supported");
        }
    }

    /// <summary>Port of transformRerankRequest.</summary>
    public HttpRequestData TransformRerankRequest(LlmRequest llmReq)
    {
        if (llmReq.Rerank is null)
        {
            throw new InvalidOperationException("rerank request is nil in llm.Request");
        }

        var rerankReq = llmReq.Rerank;

        if (llmReq.Model.Length == 0)
        {
            throw new InvalidOperationException("model is required");
        }

        if (rerankReq.Query.Length == 0)
        {
            throw new InvalidOperationException("query is required");
        }

        if (rerankReq.Documents.Count == 0)
        {
            throw new InvalidOperationException("documents are required");
        }

        var jinaReq = new JinaRerankRequest
        {
            Model = llmReq.Model,
            Query = rerankReq.Query,
            Documents = rerankReq.Documents,
            TopN = rerankReq.TopN,
            ReturnDocuments = rerankReq.ReturnDocuments,
        };

        byte[] body;
        try
        {
            body = GoJson.Marshal(jinaReq);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to marshal rerank request: " + ex.Message, ex);
        }

        var apiKey = _config.ApiKeyProvider.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "POST",
            Url = BuildRerankUrl(),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Accept"] = ["application/json"],
            },
            Body = body,
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            RequestType = RequestTypes.Rerank,
            ApiFormat = ApiFormats.JinaRerank,
        };
    }

    /// <summary>Port of transformEmbeddingRequest.</summary>
    public HttpRequestData TransformEmbeddingRequest(LlmRequest llmReq)
    {
        if (llmReq.Embedding is null)
        {
            throw new InvalidOperationException("embedding request is nil in llm.Request");
        }

        var embReq = llmReq.Embedding;

        if (llmReq.Model.Length == 0)
        {
            throw new InvalidOperationException("model is required");
        }

        var jinaReq = new JinaEmbeddingRequest
        {
            Input = embReq.Input,
            Model = llmReq.Model,
            Task = embReq.Task,
            EncodingFormat = string.IsNullOrEmpty(embReq.EncodingFormat) ? null : embReq.EncodingFormat,
            Dimensions = embReq.Dimensions,
            User = string.IsNullOrEmpty(embReq.User) ? null : embReq.User,
        };

        // Go's omitempty drops an empty task; the outbound defaults it to
        // text-matching before the wire body is built.
        if (string.IsNullOrEmpty(jinaReq.Task))
        {
            jinaReq.Task = "text-matching";
        }

        byte[] body;
        try
        {
            body = GoJson.Marshal(jinaReq);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to marshal embedding request: " + ex.Message, ex);
        }

        var apiKey = _config.ApiKeyProvider.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "POST",
            Url = BuildEmbeddingUrl(),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Accept"] = ["application/json"],
            },
            Body = body,
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey },
            RequestType = RequestTypes.Embedding,
            ApiFormat = ApiFormats.JinaEmbedding,
        };
    }

    /// <summary>Port of buildRerankURL.</summary>
    public string BuildRerankUrl()
        => _config.EndpointPath.Length > 0
            ? _config.BaseUrl + _config.EndpointPath
            : _config.BaseUrl + "/rerank";

    /// <summary>Port of buildEmbeddingURL.</summary>
    public string BuildEmbeddingUrl()
        => _config.EndpointPath.Length > 0
            ? _config.BaseUrl + _config.EndpointPath
            : _config.BaseUrl + "/embeddings";

    /// <summary>Port of TransformResponse.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        if (response.StatusCode >= 400)
        {
            // Go returns the *llm.ResponseError as the error value; the port's
            // ResponseError is itself an exception.
            throw TransformError(new HttpClientError(
                string.Empty, string.Empty, response.StatusCode, string.Empty, response.Body));
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        return response.Request?.ApiFormat == ApiFormats.JinaEmbedding
            ? TransformEmbeddingResponse(response)
            : TransformRerankResponse(response);
    }

    /// <summary>Port of transformRerankResponse.</summary>
    public LlmResponse TransformRerankResponse(HttpResponseData httpResp)
    {
        var syntaxError = GoJsonSyntax.Validate(httpResp.Body!);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException("failed to unmarshal rerank response: " + syntaxError);
        }

        JinaRerankResponse jinaResp;
        try
        {
            jinaResp = LlmJson.Deserialize<JinaRerankResponse>(httpResp.Body!)
                ?? throw new InvalidOperationException("failed to unmarshal rerank response: unexpected null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal rerank response: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        var llmRerankResp = new RerankResponse
        {
            Object = jinaResp.Object,
            Results = new List<RerankResult>(),
        };

        foreach (var rawResult in jinaResp.Results)
        {
            // Go decodes a null element as the zero RerankResult struct.
            var result = rawResult ?? new JinaRerankResult();
            llmRerankResp.Results.Add(new RerankResult
            {
                Index = result.Index,
                RelevanceScore = result.RelevanceScore,
                Document = result.Document is { } doc
                    ? new RerankDocument { Text = doc.Text }
                    : null,
            });
        }

        var llmResp = new LlmResponse
        {
            RequestType = RequestTypes.Rerank,
            ApiFormat = ApiFormats.JinaRerank,
            Rerank = llmRerankResp,
            Model = jinaResp.Model,
        };

        if (jinaResp.Usage is { } usage)
        {
            llmResp.Usage = new Usage
            {
                PromptTokens = usage.PromptTokens,
                TotalTokens = usage.TotalTokens,
            };
        }

        return llmResp;
    }

    /// <summary>Port of transformEmbeddingResponse.</summary>
    public LlmResponse TransformEmbeddingResponse(HttpResponseData httpResp)
    {
        var syntaxError = GoJsonSyntax.Validate(httpResp.Body!);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException("failed to unmarshal embedding response: " + syntaxError);
        }

        JinaEmbeddingResponse jinaResp;
        try
        {
            jinaResp = LlmJson.Deserialize<JinaEmbeddingResponse>(httpResp.Body!)
                ?? throw new InvalidOperationException("failed to unmarshal embedding response: unexpected null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal embedding response: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        var llmEmbeddingResp = new EmbeddingResponse
        {
            Object = jinaResp.Object,
            Data = new List<EmbeddingData>(),
        };

        foreach (var rawData in jinaResp.Data)
        {
            // Go decodes a null element as the zero EmbeddingData struct.
            var data = rawData ?? new JinaEmbeddingData();
            llmEmbeddingResp.Data.Add(new EmbeddingData
            {
                Object = data.Object,
                Embedding = data.Embedding,
                Index = data.Index,
            });
        }

        var llmResp = new LlmResponse
        {
            RequestType = RequestTypes.Embedding,
            ApiFormat = ApiFormats.JinaEmbedding,
            Embedding = llmEmbeddingResp,
            Model = jinaResp.Model,
        };

        if (jinaResp.Usage.PromptTokens > 0 || jinaResp.Usage.TotalTokens > 0)
        {
            llmResp.Usage = new Usage
            {
                PromptTokens = jinaResp.Usage.PromptTokens,
                TotalTokens = jinaResp.Usage.TotalTokens,
            };
        }

        return llmResp;
    }

    /// <summary>Port of TransformStream: rerank has no streaming form.</summary>
    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => throw new InvalidOperationException("rerank does not support streaming");

    /// <summary>Port of AggregateStreamChunks: rerank has no streaming form.</summary>
    public byte[] AggregateStreamChunks(HttpRequestData? request, IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        throw new InvalidOperationException("rerank does not support streaming");
    }

    /// <summary>Port of TransformError.</summary>
    public ResponseError TransformError(HttpClientError? error)
    {
        if (error is null)
        {
            return new ResponseError
            {
                StatusCode = 500,
                Detail = new ErrorDetail
                {
                    Message = GoHttpStatus.Text(500),
                    Type = "api_error",
                },
            };
        }

        var jinaError = new JinaErrorEnvelope();
        if (error.Body is { Length: > 0 } body)
        {
            try
            {
                jinaError = LlmJson.Deserialize<JinaErrorEnvelope>(body) ?? new JinaErrorEnvelope();
            }
            catch (JsonException)
            {
                jinaError = new JinaErrorEnvelope();
            }
        }

        if (jinaError.Error is { } detail && detail.Message.Length > 0)
        {
            return new ResponseError
            {
                StatusCode = error.StatusCode,
                Detail = detail,
            };
        }

        return new ResponseError
        {
            StatusCode = error.StatusCode,
            Detail = new ErrorDetail
            {
                Message = GoHttpStatus.Text(error.StatusCode),
                Type = "api_error",
            },
        };
    }

    private static void ValidateConfig(JinaOutboundConfig config)
    {
        if (config is null)
        {
            throw new InvalidOperationException("config cannot be nil");
        }

        if (config.ApiKeyProvider is null)
        {
            throw new InvalidOperationException("API key provider is required");
        }

        if (config.BaseUrl.Length == 0)
        {
            throw new InvalidOperationException("base URL is required");
        }
    }

    private sealed class JinaErrorEnvelope
    {
        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public ErrorDetail? Error { get; set; }
    }
}

/// <summary>Port of jina.Config (llm/transformer/jina/outbound.go:27-35).</summary>
public sealed class JinaOutboundConfig
{
    public string BaseUrl { get; set; } = string.Empty;

    public IApiKeyProvider ApiKeyProvider { get; set; } = null!;

    /// <summary>Optional custom path override, replacing "/rerank" or "/embeddings".</summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>1-based index over the channel's full key list, or null when unambiguous.</summary>
    public int? ChannelApiKeyIndex { get; set; }
}
