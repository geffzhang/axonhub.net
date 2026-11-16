using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the /v1/embeddings surface and the OpenAI embedding
/// outbound path (llm/transformer/openai/embedding_test.go, embedding.go,
/// embedding_inbound.go). Every test mirrors a reference subtest one-for-one.
/// </summary>
public sealed class OpenAiEmbeddingTransformerTests
{
    private static readonly string[] HelloWorldInputs = ["Hello", "World"];

    private static readonly double[] SampleVector = [0.1, 0.2, 0.3];

    // ------------------------------------------------ inbound TransformRequest

    [Fact]
    public void InboundTransformRequest_ValidStringInput()
    {
        var req = new EmbeddingInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"text-embedding-ada-002","input":"The quick brown fox"}"""));

        Assert.Equal("text-embedding-ada-002", req.Model);
        Assert.Equal(ApiFormats.OpenAIEmbedding, req.ApiFormat);
        Assert.Null(req.Stream);
        Assert.NotNull(req.Embedding);
        Assert.Equal(EmbeddingInputTypes.String, req.Embedding!.Input.GetType());
        Assert.Equal("The quick brown fox", req.Embedding.Input.String);
    }

    [Fact]
    public void InboundTransformRequest_ValidArrayInput()
    {
        var req = new EmbeddingInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"text-embedding-ada-002","input":["Hello","World"]}"""));

        Assert.NotNull(req.Embedding);
        Assert.Equal(EmbeddingInputTypes.StringArray, req.Embedding!.Input.GetType());
        Assert.Equal(HelloWorldInputs, req.Embedding.Input.StringArray);
    }

    [Fact]
    public void InboundTransformRequest_MissingModel()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new EmbeddingInboundTransformer().TransformRequest(JsonRequest("""{"input":"test"}""")));

        Assert.Contains("model is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_MissingInput()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new EmbeddingInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"text-embedding-ada-002"}""")));

        Assert.Contains("input cannot be empty string", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_EmptyStringInput()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new EmbeddingInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"text-embedding-ada-002","input":""}""")));

        Assert.Contains("input cannot be empty string", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_EmptyArrayInput()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new EmbeddingInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"text-embedding-ada-002","input":[]}""")));

        Assert.Contains("input cannot be empty array", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_WhitespaceOnlyInput()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new EmbeddingInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"text-embedding-ada-002","input":"   "}""")));

        Assert.Contains("input cannot be empty string", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_NilHttpRequest()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new EmbeddingInboundTransformer().TransformRequest(null));

        Assert.Contains("http request is nil", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_EmptyBody()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new EmbeddingInboundTransformer().TransformRequest(JsonRequest(string.Empty)));

        Assert.Contains("request body is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_UnsupportedContentType()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new EmbeddingInboundTransformer().TransformRequest(JsonRequest("test", "text/plain")));

        Assert.Contains("unsupported content type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_ValidTokenIdsInput()
    {
        var req = new EmbeddingInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"text-embedding-ada-002","input":[1234,5678,9012]}"""));

        Assert.NotNull(req.Embedding);
        Assert.Equal(EmbeddingInputTypes.IntArray, req.Embedding!.Input.GetType());
        Assert.Equal(new long[] { 1234, 5678, 9012 }, req.Embedding.Input.IntArray);
    }

    [Fact]
    public void InboundTransformRequest_ValidNestedTokenIdsInput()
    {
        var req = new EmbeddingInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"text-embedding-ada-002","input":[[1234,5678],[9012,3456]]}"""));

        Assert.NotNull(req.Embedding);
        Assert.Equal(EmbeddingInputTypes.IntArrayArray, req.Embedding!.Input.GetType());
        Assert.Equal(2, req.Embedding.Input.IntArrayArray!.Count);
        Assert.Equal(new long[] { 1234, 5678 }, req.Embedding.Input.IntArrayArray[0]);
        Assert.Equal(new long[] { 9012, 3456 }, req.Embedding.Input.IntArrayArray[1]);
    }

    [Fact]
    public void InboundTransformRequest_EmptyNestedArrayInput()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new EmbeddingInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"text-embedding-ada-002","input":[[],[1234]]}""")));

        Assert.Contains("input[0] cannot be empty array", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_InvalidJsonBody()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new EmbeddingInboundTransformer().TransformRequest(JsonRequest("not valid json")));

        Assert.Contains("failed to decode embedding request", ex.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------- outbound TransformRequest

    [Fact]
    public void OutboundTransformRequest_ValidRequestWithV1Suffix()
    {
        var transformer = NewTransformer();

        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "text-embedding-ada-002",
            RequestType = RequestTypes.Embedding,
            Embedding = new EmbeddingRequest
            {
                Input = new EmbeddingInput { String = "Hello world" },
            },
        });

        Assert.Equal("POST", httpReq.Method);
        Assert.Equal("https://api.openai.com/v1/embeddings", httpReq.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type"));
        Assert.NotNull(httpReq.Auth);
        Assert.Equal("bearer", httpReq.Auth!.Type);
        Assert.Equal(RequestTypes.Embedding, httpReq.RequestType);
        Assert.Equal(ApiFormats.OpenAIEmbedding, httpReq.ApiFormat);

        using var doc = JsonDocument.Parse(httpReq.Body!);
        Assert.Equal("Hello world", doc.RootElement.GetProperty("input").GetString());
        Assert.Equal("text-embedding-ada-002", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void OutboundTransformRequest_NilLlmRequest()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewTransformer().TransformRequest(null));

        Assert.Contains("request is nil", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutboundTransformRequest_MissingEmbeddingRequest()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewTransformer().TransformRequest(new LlmRequest
            {
                Model = "text-embedding-ada-002",
                RequestType = RequestTypes.Embedding,
            }));

        Assert.Contains("embedding request is nil in llm.Request", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------- outbound TransformResponse

    [Fact]
    public void OutboundTransformResponse_ValidResponse()
    {
        var body = GoJson.Marshal(new OpenAiEmbeddingResponse
        {
            Object = "list",
            Model = "text-embedding-ada-002",
            Data =
            [
                new OpenAiEmbeddingData
                {
                    Object = "embedding",
                    Index = 0,
                    Embedding = new Embedding { Values = [0.1, 0.2, 0.3] },
                },
            ],
            Usage = new OpenAiEmbeddingUsage { PromptTokens = 5, TotalTokens = 5 },
        });

        var resp = NewTransformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = body,
            Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIEmbedding },
        });

        Assert.NotNull(resp.Embedding);
        Assert.Equal("list", resp.Embedding!.Object);
        Assert.Equal("text-embedding-ada-002", resp.Model);
        Assert.NotNull(resp.Usage);
        Assert.Equal(5L, resp.Usage!.PromptTokens);
        Assert.Equal(5L, resp.Usage.TotalTokens);
        Assert.Equal(RequestTypes.Embedding, resp.RequestType);
        Assert.Equal(ApiFormats.OpenAIEmbedding, resp.ApiFormat);
    }

    [Fact]
    public void OutboundTransformResponse_ResponseWithUpstreamId()
    {
        var body = GoJson.Marshal(new OpenAiEmbeddingResponse
        {
            Object = "list",
            Data =
            [
                new OpenAiEmbeddingData
                {
                    Object = "embedding",
                    Index = 0,
                    Embedding = new Embedding { Values = [0.1, 0.2, 0.3] },
                },
            ],
            Usage = new OpenAiEmbeddingUsage(),
        });

        var resp = NewTransformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = body,
            Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIEmbedding },
        });

        // The OpenAI embedding response has no id field; the reference leaves
        // the unified response ID empty.
        Assert.Equal(string.Empty, resp.Id);
    }

    [Fact]
    public void OutboundTransformResponse_NilHttpResponse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewTransformer().TransformResponse(null));

        Assert.Contains("http response is nil", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutboundTransformResponse_HttpError400()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewTransformer().TransformResponse(new HttpResponseData
            {
                StatusCode = 400,
                Body = Encoding.UTF8.GetBytes("""{"error": {"message": "Invalid request"}}"""),
                Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIEmbedding },
            }));

        Assert.Contains("400", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutboundTransformResponse_HttpError500()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewTransformer().TransformResponse(new HttpResponseData
            {
                StatusCode = 500,
                Body = Encoding.UTF8.GetBytes("""{"error": {"message": "Internal server error"}}"""),
                Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIEmbedding },
            }));

        Assert.Contains("500", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutboundTransformResponse_EmptyResponseBody()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewTransformer().TransformResponse(new HttpResponseData
            {
                StatusCode = 200,
                Body = [],
                Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIEmbedding },
            }));

        Assert.Contains("response body is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutboundTransformResponse_InvalidJsonResponse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewTransformer().TransformResponse(new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes("not valid json"),
                Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIEmbedding },
            }));

        Assert.Contains("failed to unmarshal embedding response", ex.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------- inbound TransformResponse

    [Fact]
    public void InboundTransformResponse_ValidResponse()
    {
        var httpResp = new EmbeddingInboundTransformer().TransformResponse(new LlmResponse
        {
            Object = "list",
            Model = "text-embedding-ada-002",
            Embedding = new EmbeddingResponse
            {
                Object = "list",
                Data =
                [
                    new EmbeddingData
                    {
                        Object = "embedding",
                        Index = 0,
                        Embedding = new Embedding { Values = [0.1, 0.2, 0.3] },
                    },
                ],
            },
            Usage = new Usage { PromptTokens = 5, TotalTokens = 5 },
        });

        Assert.Equal(200, httpResp.StatusCode);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpResp.Headers, "Content-Type"));
        Assert.Equal("no-cache", HttpClientUtils.HeaderGet(httpResp.Headers, "Cache-Control"));

        var returned = LlmJson.Deserialize<OpenAiEmbeddingResponse>(httpResp.Body!)!;
        Assert.Equal("list", returned.Object);
        Assert.Equal("text-embedding-ada-002", returned.Model);
        Assert.Single(returned.Data!);
        Assert.Equal(SampleVector, returned.Data![0].Embedding.Values);
    }

    [Fact]
    public void InboundTransformResponse_IncludesUsageCost()
    {
        var httpResp = new EmbeddingInboundTransformer().TransformResponse(new LlmResponse
        {
            Object = "list",
            Model = "text-embedding-ada-002",
            Embedding = new EmbeddingResponse
            {
                Object = "list",
                Data =
                [
                    new EmbeddingData
                    {
                        Object = "embedding",
                        Index = 0,
                        Embedding = new Embedding { Values = [0.1, 0.2, 0.3] },
                    },
                ],
            },
            Usage = new Usage { PromptTokens = 5, TotalTokens = 5, Cost = 0.000005 },
        });

        var returned = LlmJson.Deserialize<OpenAiEmbeddingResponse>(httpResp.Body!)!;
        Assert.NotNull(returned.Usage!.Cost);
        Assert.Equal(0.000005, returned.Usage.Cost!.Value, 12);
    }

    [Fact]
    public void InboundTransformResponse_ValidResponseWithoutUsage()
    {
        var httpResp = new EmbeddingInboundTransformer().TransformResponse(new LlmResponse
        {
            Object = "list",
            Model = "text-embedding-ada-002",
            Embedding = new EmbeddingResponse
            {
                Object = "list",
                Data =
                [
                    new EmbeddingData
                    {
                        Object = "embedding",
                        Index = 0,
                        Embedding = new Embedding { Values = [0.1, 0.2, 0.3] },
                    },
                ],
            },
        });

        Assert.Equal(200, httpResp.StatusCode);

        // Go's EmbeddingUsage is a struct, so the zero counters are still emitted.
        var returned = LlmJson.Deserialize<OpenAiEmbeddingResponse>(httpResp.Body!)!;
        Assert.NotNull(returned.Usage);
        Assert.Equal(0L, returned.Usage!.PromptTokens);
        Assert.Equal(0L, returned.Usage.TotalTokens);
    }

    [Fact]
    public void InboundTransformResponse_NilLlmResponse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new EmbeddingInboundTransformer().TransformResponse(null));

        Assert.Contains("embedding response is nil", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- API format

    [Fact]
    public void ApiFormat_IsChatCompletion()
    {
        Assert.Equal(ApiFormats.OpenAIChatCompletion, NewTransformer().ApiFormat);
    }

    // --------------------------------------------------------- TransformError

    [Fact]
    public void TransformError_NilError()
    {
        var resp = NewTransformer().TransformError(null);

        Assert.Equal(500, resp.StatusCode);
    }

    [Fact]
    public void TransformError_OpenAiFormatError()
    {
        var resp = NewTransformer().TransformError(new HttpClientError(
            "POST",
            "https://api.openai.com/v1/embeddings",
            400,
            "400 Bad Request",
            Encoding.UTF8.GetBytes(
                """{"error": {"message": "Invalid model", "type": "invalid_request_error"}}""")));

        Assert.Equal(400, resp.StatusCode);
        Assert.Equal("Invalid model", resp.Detail.Message);
    }

    [Fact]
    public void TransformError_NonJsonErrorBody()
    {
        var resp = NewTransformer().TransformError(new HttpClientError(
            "POST",
            "https://api.openai.com/v1/embeddings",
            503,
            "503 Service Unavailable",
            Encoding.UTF8.GetBytes("Service unavailable")));

        Assert.Equal(503, resp.StatusCode);
    }

    // -------------------------------------------------- stream is unsupported

    [Fact]
    public void TransformStream_ReturnsError()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new EmbeddingInboundTransformer().TransformStream(
                new SliceStream<LlmResponse>(new List<LlmResponse>())));

        Assert.Contains("do not support streaming", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateStreamChunks_ReturnsError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new EmbeddingInboundTransformer().AggregateStreamChunks([], out _));

        Assert.Contains("do not support streaming", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ URL building

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/embeddings")]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1/embeddings")]
    [InlineData("https://api.openai.com/", "https://api.openai.com/v1/embeddings")]
    [InlineData("https://api.siliconflow.cn/v1", "https://api.siliconflow.cn/v1/embeddings")]
    [InlineData("https://api.siliconflow.cn", "https://api.siliconflow.cn/v1/embeddings")]
    public void UrlBuilding(string baseUrl, string expectedUrl)
    {
        var transformer = OpenAiOutboundTransformer.Create(baseUrl, "test-key");

        var httpReq = transformer.TransformRequest(HelloWorldRequest());

        Assert.Equal(expectedUrl, httpReq.Url);
    }

    [Theory]
    [InlineData("https://custom.api.com/v100#", false, "https://custom.api.com/v100/embeddings")]
    [InlineData("https://custom.api.com/v20#", false, "https://custom.api.com/v20/embeddings")]
    [InlineData("https://api.openai.com", false, "https://api.openai.com/v1/embeddings")]
    public void RawUrl(string baseUrl, bool rawUrl, string expectedUrl)
    {
        var transformer = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
        {
            PlatformType = OpenAiPlatformType.OpenAi,
            BaseUrl = baseUrl,
            RawUrl = rawUrl,
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

        var httpReq = transformer.TransformRequest(HelloWorldRequest());

        Assert.Equal(expectedUrl, httpReq.Url);
    }

    [Fact]
    public void CustomEndpointPath()
    {
        var transformer = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
        {
            PlatformType = OpenAiPlatformType.OpenAi,
            BaseUrl = "https://custom.api.com",
            EndpointPath = "/custom/embeddings",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "text-embedding-3-large",
            RequestType = RequestTypes.Embedding,
            Embedding = new EmbeddingRequest
            {
                Input = new EmbeddingInput { String = "Hello world" },
            },
        });

        Assert.Equal("https://custom.api.com/custom/embeddings", httpReq.Url);
    }

    // ----------------------------------------------------------------- helpers

    private static OpenAiOutboundTransformer NewTransformer()
        => OpenAiOutboundTransformer.Create("https://api.openai.com/v1", "test-key");

    private static LlmRequest HelloWorldRequest() => new()
    {
        Model = "text-embedding-ada-002",
        RequestType = RequestTypes.Embedding,
        Embedding = new EmbeddingRequest
        {
            Input = new EmbeddingInput { String = "Hello world" },
        },
    };

    private static HttpRequestData JsonRequest(string body, string contentType = "application/json")
        => new()
        {
            Body = Encoding.UTF8.GetBytes(body),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = [contentType],
            },
        };
}
