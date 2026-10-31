using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of the embedding and moderation halves of
/// llm/transformer/openai/embedding.go and moderation.go: construction of the
/// upstream request and decoding of the upstream response.
/// </summary>
public sealed partial class OpenAiOutboundTransformer
{
    /// <summary>Port of transformEmbeddingRequest (embedding.go:42-97).</summary>
    private HttpRequestData BuildEmbeddingApiRequest(LlmRequest request)
    {
        if (request.Embedding is null)
        {
            throw new InvalidOperationException("embedding request is nil in llm.Request");
        }

        var embedding = request.Embedding;

        var apiRequest = new OpenAiEmbeddingRequest
        {
            Input = embedding.Input,
            Model = request.Model,
            EncodingFormat = NullIfEmpty(embedding.EncodingFormat ?? string.Empty),
            Dimensions = embedding.Dimensions,
            User = NullIfEmpty(embedding.User ?? string.Empty),
        };

        var body = GoJson.Marshal(apiRequest);

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        return new HttpRequestData
        {
            Method = "POST",
            Url = BuildEmbeddingUrl(),
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            RequestType = RequestTypes.Embedding,
            ApiFormat = ApiFormats.OpenAIEmbedding,
        };
    }

    /// <summary>Port of buildEmbeddingURL (embedding.go:99-106).</summary>
    private string BuildEmbeddingUrl()
        => _config.EndpointPath.Length > 0
            ? _config.BaseUrl + _config.EndpointPath
            : _config.BaseUrl + "/embeddings";

    /// <summary>
    /// Port of transformEmbeddingResponse (embedding.go:108-169). The caller
    /// already rejected status &gt;= 400 and empty bodies (outbound.go's generic
    /// checks run before the API-format dispatch), matching the reference.
    /// </summary>
    private static LlmResponse TransformEmbeddingResponse(HttpResponseData response)
    {
        var body = response.Body ?? [];

        var embeddingResponse = DecodeEmbeddingResponse(body);

        // Go: make([]llm.EmbeddingData, len(embResp.Data)).
        var data = new List<EmbeddingData>();
        foreach (var item in embeddingResponse.Data ?? [])
        {
            data.Add(new EmbeddingData
            {
                Object = item.Object,
                Embedding = item.Embedding,
                Index = item.Index,
            });
        }

        var llmResponse = new LlmResponse
        {
            RequestType = RequestTypes.Embedding,
            ApiFormat = ApiFormats.OpenAIEmbedding,
            Embedding = new EmbeddingResponse
            {
                Object = embeddingResponse.Object,
                Data = data,
            },
            Model = embeddingResponse.Model,
        };

        if (embeddingResponse.Usage is { } usage
            && (usage.PromptTokens > 0 || usage.TotalTokens > 0))
        {
            llmResponse.Usage = new Usage
            {
                PromptTokens = usage.PromptTokens,
                TotalTokens = usage.TotalTokens,
            };
        }

        return llmResponse;
    }

    /// <summary>Port of transformModerationRequest (moderation.go:25-64).</summary>
    private HttpRequestData BuildModerationApiRequest(LlmRequest request)
    {
        if (request.Moderation is null)
        {
            throw new InvalidOperationException("moderation request is nil in llm.Request");
        }

        var apiRequest = new OpenAiModerationCreateRequest
        {
            Input = request.Moderation.Input,
            Model = request.Model,
        };

        var body = GoJson.Marshal(apiRequest);

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        return new HttpRequestData
        {
            Method = "POST",
            Url = BuildModerationUrl(),
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            RequestType = RequestTypes.Moderation,
            ApiFormat = ApiFormats.OpenAIModeration,
        };
    }

    /// <summary>Port of buildModerationURL (moderation.go:66-72).</summary>
    private string BuildModerationUrl()
        => _config.EndpointPath.Length > 0
            ? _config.BaseUrl + _config.EndpointPath
            : _config.BaseUrl + "/moderations";

    /// <summary>
    /// Port of transformModerationResponse (moderation.go:74-116). Status and
    /// empty-body checks run before dispatch, as in the reference.
    /// </summary>
    private static LlmResponse TransformModerationResponse(HttpResponseData response)
    {
        var body = response.Body ?? [];

        var moderationResponse = DecodeModerationResponse(body);

        return new LlmResponse
        {
            Id = moderationResponse.Id,
            Model = moderationResponse.Model,
            RequestType = RequestTypes.Moderation,
            ApiFormat = ApiFormats.OpenAIModeration,
            Moderation = new ModerationResponse
            {
                Results = moderationResponse.Results,
            },
        };
    }

    private static OpenAiEmbeddingResponse DecodeEmbeddingResponse(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal embedding response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind == "null")
        {
            return new OpenAiEmbeddingResponse();
        }

        if (rootKind is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal embedding response: "
                + GoStructJson.TypeMismatch(rootKind, "openai.EmbeddingResponse"));
        }

        try
        {
            return LlmJson.Deserialize<OpenAiEmbeddingResponse>(body)
                ?? new OpenAiEmbeddingResponse();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal embedding response: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }

    private static OpenAiModerationCreateResponse DecodeModerationResponse(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal moderation response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind == "null")
        {
            return new OpenAiModerationCreateResponse();
        }

        if (rootKind is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal moderation response: "
                + GoStructJson.TypeMismatch(rootKind, "openai.ModerationCreateResponse"));
        }

        try
        {
            return LlmJson.Deserialize<OpenAiModerationCreateResponse>(body)
                ?? new OpenAiModerationCreateResponse();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal moderation response: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }
}
