using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Doubao;

/// <summary>
/// Port of llm/transformer/doubao/embedding.go: the Ark multimodal embedding
/// API (text inputs only) and its response conversion.
/// </summary>
public sealed partial class DoubaoOutboundTransformer
{
    /// <summary>Port of transformEmbeddingRequest (embedding.go:34-78).</summary>
    private HttpRequestData TransformEmbeddingRequest(LlmRequest request)
    {
        if (request.Embedding is null)
        {
            throw new InvalidOperationException("embedding request is nil in llm.Request");
        }

        var embedding = request.Embedding;

        var apiRequest = new MultimodalEmbeddingRequest
        {
            Model = request.Model,
            Input = BuildMultimodalInput(embedding.Input),
            EncodingFormat = EmptyToNull(embedding.EncodingFormat ?? string.Empty),
            Dimensions = embedding.Dimensions,
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
            Url = _baseUrl + "/embeddings/multimodal",
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

    /// <summary>Port of transformEmbeddingResponse (embedding.go:107-165).</summary>
    private LlmResponse TransformEmbeddingResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        if (response.StatusCode >= 400)
        {
            // Delegate to the OpenAI outbound so provider error bodies keep the
            // standard conversion; a body it accepts is still an HTTP failure.
            _inner.TransformResponse(response);

            throw new InvalidOperationException(
                "HTTP error " + response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var body = response.Body ?? [];
        if (body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        var embeddingResponse = DecodeMultimodalEmbeddingResponse(body);

        var data = embeddingResponse.Data ?? new MultimodalEmbeddingData();

        var llmResponse = new LlmResponse
        {
            RequestType = RequestTypes.Embedding,
            ApiFormat = ApiFormats.OpenAIEmbedding,
            Embedding = new EmbeddingResponse
            {
                Id = EmptyToNull(embeddingResponse.ID),
                Object = embeddingResponse.Object,
                Data =
                [
                    new EmbeddingData
                    {
                        Object = data.Object,
                        Embedding = data.Embedding ?? new Embedding(),
                        Index = 0,
                    },
                ],
            },
            Model = embeddingResponse.Model,
        };

        var usage = embeddingResponse.Usage ?? new MultimodalEmbeddingUsage();
        if (usage.PromptTokens > 0 || usage.TotalTokens > 0)
        {
            llmResponse.Usage = new Usage
            {
                PromptTokens = usage.PromptTokens,
                TotalTokens = usage.TotalTokens,
            };

            if (usage.PromptTokensDetails is { } details)
            {
                llmResponse.Usage.PromptTokensDetails = new PromptTokensDetails
                {
                    TextTokens = details.TextTokens,
                    ImageTokens = details.ImageTokens,
                };
            }
        }

        return llmResponse;
    }

    /// <summary>Port of buildMultimodalInput (embedding.go:169-188).</summary>
    internal static List<MultimodalEmbeddingInputItem> BuildMultimodalInput(EmbeddingInput input)
    {
        List<string> texts;
        if (input.String.Length > 0)
        {
            texts = [input.String];
        }
        else if (input.StringArray is { Count: > 0 } strings)
        {
            texts = strings;
        }
        else
        {
            texts = [];
        }

        var items = new List<MultimodalEmbeddingInputItem>(texts.Count);
        foreach (var text in texts)
        {
            items.Add(new MultimodalEmbeddingInputItem
            {
                Type = "text",
                Text = text,
            });
        }

        return items;
    }

    private static MultimodalEmbeddingResponse DecodeMultimodalEmbeddingResponse(byte[] body)
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
            return new MultimodalEmbeddingResponse();
        }

        if (rootKind is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal embedding response: "
                + GoStructJson.TypeMismatch(rootKind, "doubao.multimodalEmbeddingResponse"));
        }

        try
        {
            return LlmJson.Deserialize<MultimodalEmbeddingResponse>(body)
                ?? new MultimodalEmbeddingResponse();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal embedding response: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }
}

/// <summary>Port of doubao.multimodalEmbeddingRequest (embedding.go:15-20).</summary>
public sealed class MultimodalEmbeddingRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("input")]
    public List<MultimodalEmbeddingInputItem> Input { get; set; } = [];

    [System.Text.Json.Serialization.JsonPropertyName("encoding_format")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? EncodingFormat { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("dimensions")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Dimensions { get; set; }
}

/// <summary>Port of doubao.multimodalEmbeddingInputItem (embedding.go:23-27).</summary>
public sealed class MultimodalEmbeddingInputItem
{
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("text")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("image_url")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MultimodalEmbeddingImageUrl? ImageUrl { get; set; }
}

/// <summary>Port of doubao.multimodalEmbeddingImageURL (embedding.go:29-31).</summary>
public sealed class MultimodalEmbeddingImageUrl
{
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>Port of doubao.multimodalEmbeddingResponse (embedding.go:81-87).</summary>
public sealed class MultimodalEmbeddingResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string ID { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public MultimodalEmbeddingData? Data { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("usage")]
    public MultimodalEmbeddingUsage? Usage { get; set; }
}

/// <summary>Port of doubao.multimodalEmbeddingData (embedding.go:89-93).</summary>
public sealed class MultimodalEmbeddingData
{
    [System.Text.Json.Serialization.JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("embedding")]
    public Embedding? Embedding { get; set; }

    /// <summary>Opaque sparse vector; decoded but not surfaced by the reference.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sparse_embedding")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.JsonElement? SparseEmbedding { get; set; }
}

/// <summary>Port of doubao.multimodalEmbeddingUsage (embedding.go:95-99).</summary>
public sealed class MultimodalEmbeddingUsage
{
    [System.Text.Json.Serialization.JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("prompt_tokens_details")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MultimodalEmbeddingPromptTokensDetails? PromptTokensDetails { get; set; }
}

/// <summary>Port of doubao.multimodalEmbeddingPromptTokensDetails (embedding.go:101-104).</summary>
public sealed class MultimodalEmbeddingPromptTokensDetails
{
    [System.Text.Json.Serialization.JsonPropertyName("text_tokens")]
    public long TextTokens { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("image_tokens")]
    public long ImageTokens { get; set; }
}
