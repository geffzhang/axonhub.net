using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Jina;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the jina rerank/embedding transformers
/// (llm/transformer/jina/rerank_inbound.go, embedding_inbound.go, outbound.go,
/// model.go, usage_json.go, and embedding_test.go). Every test mirrors a
/// reference behaviour one-for-one.
/// </summary>
public sealed class JinaRerankTransformerTests
{
    private static readonly string[] CapitalCandidates = ["Paris", "London"];

    private static readonly string[] HelloWorldInputs = ["Hello", "World"];

    private static readonly double[] SampleVector = [0.1, 0.2, 0.3];

    // ------------------------------------------------ inbound rerank TransformRequest

    [Fact]
    public void InboundTransformRequest_ValidRequestWithAllFields()
    {
        var req = new JinaRerankInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"jina-reranker-v2-base-multilingual","query":"What is the capital of France?","documents":["Paris","London"],"top_n":3,"return_documents":true}"""));

        Assert.Equal("jina-reranker-v2-base-multilingual", req.Model);
        Assert.Equal(RequestTypes.Rerank, req.RequestType);
        Assert.Equal(ApiFormats.JinaRerank, req.ApiFormat);
        Assert.NotNull(req.Rerank);
        Assert.Equal("What is the capital of France?", req.Rerank!.Query);
        Assert.Equal(CapitalCandidates, req.Rerank.Documents);
        Assert.Equal(3, req.Rerank.TopN);
        Assert.True(req.Rerank.ReturnDocuments);
        Assert.NotNull(req.RawRequest);
    }

    [Fact]
    public void InboundTransformRequest_OptionalFieldsDefaultToNull()
    {
        var req = new JinaRerankInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"jina-reranker-v2-base-multilingual","query":"q","documents":["d"]}"""));

        Assert.Null(req.Rerank!.TopN);
        Assert.Null(req.Rerank.ReturnDocuments);
    }

    [Fact]
    public void InboundTransformRequest_NilHttpRequest()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaRerankInboundTransformer().TransformRequest(null));

        Assert.Equal("invalid request: http request is nil", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_EmptyBody()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaRerankInboundTransformer().TransformRequest(JsonRequest(string.Empty)));

        Assert.Equal("invalid request: request body is empty", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_MissingModel()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaRerankInboundTransformer().TransformRequest(
                JsonRequest("""{"query":"q","documents":["d"]}""")));

        Assert.Equal("invalid request: model is required", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_MissingQuery()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaRerankInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"m","documents":["d"]}""")));

        Assert.Equal("invalid request: query is required", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_MissingDocuments()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaRerankInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"m","query":"q","documents":[]}""")));

        Assert.Equal("invalid request: documents are required", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_InvalidJson()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaRerankInboundTransformer().TransformRequest(JsonRequest("{not json")));

        Assert.StartsWith("invalid request: failed to decode rerank request: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_ArrayRoot()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaRerankInboundTransformer().TransformRequest(JsonRequest("[]")));

        Assert.Equal(
            "invalid request: failed to decode rerank request: "
            + "json: cannot unmarshal array into Go value of type jina.RerankRequest",
            ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_StringRoot()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaRerankInboundTransformer().TransformRequest(JsonRequest("\"x\"")));

        Assert.Equal(
            "invalid request: failed to decode rerank request: "
            + "json: cannot unmarshal string into Go value of type jina.RerankRequest",
            ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_FieldTypeErrorKeepsGoPrefix()
    {
        // C080: field-level type mismatches report System.Text.Json wording
        // inside the Go-faithful prefix (no C003 walker on this path).
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaRerankInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"m","query":"q","documents":"not-an-array"}""")));

        Assert.StartsWith("invalid request: failed to decode rerank request: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_NullRootFailsOnModel()
    {
        // Go's json.Unmarshal of "null" leaves the zero request in place.
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaRerankInboundTransformer().TransformRequest(JsonRequest("null")));

        Assert.Equal("invalid request: model is required", ex.Message);
    }

    // ----------------------------------------------- inbound rerank TransformResponse

    [Fact]
    public void InboundTransformResponse_ValidWithDocumentAndUsage()
    {
        var resp = new JinaRerankInboundTransformer().TransformResponse(new LlmResponse
        {
            Model = "jina-reranker-v2-base-multilingual",
            Rerank = new RerankResponse
            {
                Object = "list",
                Results =
                [
                    new RerankResult
                    {
                        Index = 0,
                        RelevanceScore = 0.8050369,
                        Document = new RerankDocument { Text = "Paris" },
                    },
                ],
            },
            Usage = new Usage { PromptTokens = 5, TotalTokens = 5 },
        });

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(resp.Headers, "Content-Type"));
        Assert.Equal(
            "{\"model\":\"jina-reranker-v2-base-multilingual\",\"object\":\"list\","
            + "\"results\":[{\"index\":0,\"relevance_score\":0.8050369,"
            + "\"document\":{\"text\":\"Paris\"}}],"
            + "\"usage\":{\"prompt_tokens\":5,\"total_tokens\":5}}",
            Encoding.UTF8.GetString(resp.Body!));
    }

    [Fact]
    public void InboundTransformResponse_ResultWithoutDocumentOmitsKey()
    {
        var resp = new JinaRerankInboundTransformer().TransformResponse(new LlmResponse
        {
            Model = "m",
            Rerank = new RerankResponse
            {
                Object = "list",
                Results = [new RerankResult { Index = 1, RelevanceScore = 0.5 }],
            },
        });

        Assert.Equal(
            "{\"model\":\"m\",\"object\":\"list\",\"results\":[{\"index\":1,\"relevance_score\":0.5}]}",
            Encoding.UTF8.GetString(resp.Body!));
    }

    [Fact]
    public void InboundTransformResponse_EmptyResultsStaysAnArray()
    {
        var resp = new JinaRerankInboundTransformer().TransformResponse(new LlmResponse
        {
            Model = "m",
            Rerank = new RerankResponse { Object = "list" },
        });

        // Go: make([]RerankResult, 0) marshals as [] — never null.
        Assert.Equal(
            "{\"model\":\"m\",\"object\":\"list\",\"results\":[]}",
            Encoding.UTF8.GetString(resp.Body!));
    }

    [Fact]
    public void InboundTransformResponse_UsageCostRoundTrips()
    {
        var resp = new JinaRerankInboundTransformer().TransformResponse(new LlmResponse
        {
            Model = "m",
            Rerank = new RerankResponse
            {
                Object = "list",
                Results = [new RerankResult { Index = 0, RelevanceScore = 0.5 }],
            },
            Usage = new Usage { PromptTokens = 5, TotalTokens = 5, Cost = 0.000005 },
        });

        var returned = LlmJson.Deserialize<JinaRerankResponse>(resp.Body!)!;
        Assert.NotNull(returned.Usage!.Cost);
        Assert.Equal(0.000005, returned.Usage.Cost!.Value, 12);
    }

    [Fact]
    public void InboundTransformResponse_NilLlmResponse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new JinaRerankInboundTransformer().TransformResponse(null));

        Assert.Equal("llm response is nil", ex.Message);
    }

    [Fact]
    public void InboundTransformResponse_NilRerankResponse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new JinaRerankInboundTransformer().TransformResponse(new LlmResponse()));

        Assert.Equal("rerank response is nil", ex.Message);
    }

    // ------------------------------------------------------- inbound TransformError

    [Fact]
    public void InboundTransformError_NilErrorReturnsGenericEnvelope()
    {
        var resp = new JinaRerankInboundTransformer().TransformError(null);

        Assert.Equal(500, resp.StatusCode);
        Assert.Equal(
            "{\"error\":{\"message\":\"Internal server error\",\"type\":\"api_error\"}}",
            Encoding.UTF8.GetString(resp.Body!));
    }

    [Fact]
    public void InboundTransformError_NonResponseErrorReturnsGenericEnvelope()
    {
        var resp = new JinaRerankInboundTransformer().TransformError(
            new InvalidOperationException("boom"));

        Assert.Equal(500, resp.StatusCode);
        Assert.Equal(
            "{\"error\":{\"message\":\"Internal server error\",\"type\":\"api_error\"}}",
            Encoding.UTF8.GetString(resp.Body!));
    }

    [Fact]
    public void InboundTransformError_ResponseErrorKeepsStatusAndDetail()
    {
        var resp = new JinaRerankInboundTransformer().TransformError(new ResponseError
        {
            StatusCode = 429,
            Detail = new ErrorDetail { Message = "rate limited", Type = "rate_limit_error" },
        });

        Assert.Equal(429, resp.StatusCode);
        Assert.Equal(
            "{\"error\":{\"message\":\"rate limited\",\"type\":\"rate_limit_error\"}}",
            Encoding.UTF8.GetString(resp.Body!));
    }

    [Fact]
    public void InboundTransformStream_NotSupported()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new JinaRerankInboundTransformer().TransformStream(new SliceStream<LlmResponse>([])));

        Assert.Equal("rerank does not support streaming", ex.Message);
    }

    [Fact]
    public void InboundAggregateStreamChunks_NotSupported()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new JinaRerankInboundTransformer().AggregateStreamChunks([], out _));

        Assert.Equal("rerank does not support streaming", ex.Message);
    }

    // --------------------------------------------- outbound config and URL building

    public static TheoryData<string, string> BaseUrlNormalizations => new()
    {
        { "https://api.jina.ai/v1", "https://api.jina.ai/v1" },
        { "https://api.jina.ai", "https://api.jina.ai/v1" },
        { "https://api.jina.ai/", "https://api.jina.ai/v1" },
    };

    [Theory]
    [MemberData(nameof(BaseUrlNormalizations))]
    public void OutboundCreate_NormalizesBaseUrl(string baseUrl, string expected)
    {
        var httpReq = JinaOutboundTransformer.Create(baseUrl, "test-key").TransformRequest(
            RerankRequest("m", "q", ["d"]));

        Assert.Equal(expected + "/rerank", httpReq.Url);
    }

    [Theory]
    [MemberData(nameof(BaseUrlNormalizations))]
    public void OutboundCreate_NormalizesEmbeddingUrl(string baseUrl, string expected)
    {
        var httpReq = JinaOutboundTransformer.Create(baseUrl, "test-key").TransformRequest(
            new LlmRequest
            {
                Model = "jina-embeddings-v3",
                RequestType = RequestTypes.Embedding,
                Embedding = new EmbeddingRequest { Input = new EmbeddingInput { String = "Hello world" } },
            });

        Assert.Equal(expected + "/embeddings", httpReq.Url);
    }

    [Fact]
    public void OutboundCreate_ConfigValidation()
    {
        Assert.Throws<InvalidOperationException>(() => new JinaOutboundTransformer(null!));
        Assert.Throws<InvalidOperationException>(() => JinaOutboundTransformer.Create(string.Empty, "key"));
        Assert.Throws<InvalidOperationException>(() =>
            new JinaOutboundTransformer(new JinaOutboundConfig { BaseUrl = "http://x" }));
    }

    [Fact]
    public void OutboundApiFormat_IsJinaRerank()
    {
        Assert.Equal(ApiFormats.JinaRerank, JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").ApiFormat);
    }

    // ------------------------------------------------- outbound rerank TransformRequest

    [Fact]
    public void OutboundTransformRerankRequest_WireShape()
    {
        var transformer = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key");

        var httpReq = transformer.TransformRequest(RerankRequest(
            "jina-reranker-v2-base-multilingual",
            "What is the capital of France?",
            ["Paris", "London"],
            topN: 3,
            returnDocuments: true));

        Assert.Equal("POST", httpReq.Method);
        Assert.Equal("https://api.jina.ai/v1/rerank", httpReq.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpReq.Headers, "Accept"));
        Assert.NotNull(httpReq.Auth);
        Assert.Equal(AuthTypes.Bearer, httpReq.Auth!.Type);
        Assert.Equal("test-key", httpReq.Auth.ApiKey);
        Assert.Equal(RequestTypes.Rerank, httpReq.RequestType);
        Assert.Equal(ApiFormats.JinaRerank, httpReq.ApiFormat);
        Assert.Equal("test-key", transformer.SelectedApiKey);

        // Byte-exact: the wire model's declaration order matches Go's struct
        // field order, and the optional fields are omitempty.
        Assert.Equal(
            "{\"model\":\"jina-reranker-v2-base-multilingual\","
            + "\"query\":\"What is the capital of France?\","
            + "\"documents\":[\"Paris\",\"London\"],\"top_n\":3,\"return_documents\":true}",
            Encoding.UTF8.GetString(httpReq.Body!));
    }

    [Fact]
    public void OutboundTransformRerankRequest_OmitsOptionalFields()
    {
        var httpReq = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
            .TransformRequest(RerankRequest("m", "q", ["d"]));

        Assert.Equal(
            "{\"model\":\"m\",\"query\":\"q\",\"documents\":[\"d\"]}",
            Encoding.UTF8.GetString(httpReq.Body!));
    }

    [Fact]
    public void OutboundTransformRequest_NilLlmRequest()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformRequest(null));

        Assert.Equal("llm request is nil", ex.Message);
    }

    [Fact]
    public void OutboundTransformRerankRequest_NilRerankRequest()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformRequest(
                new LlmRequest { Model = "m", RequestType = RequestTypes.Rerank }));

        Assert.Equal("rerank request is nil in llm.Request", ex.Message);
    }

    [Fact]
    public void OutboundTransformRerankRequest_MissingModel()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformRequest(
                RerankRequest(string.Empty, "q", ["d"])));

        Assert.Equal("model is required", ex.Message);
    }

    [Fact]
    public void OutboundTransformRerankRequest_MissingQuery()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformRequest(
                RerankRequest("m", string.Empty, ["d"])));

        Assert.Equal("query is required", ex.Message);
    }

    [Fact]
    public void OutboundTransformRerankRequest_MissingDocuments()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformRequest(
                RerankRequest("m", "q", [])));

        Assert.Equal("documents are required", ex.Message);
    }

    [Fact]
    public void OutboundTransformRequest_CompactRequestType()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformRequest(
                new LlmRequest { RequestType = RequestTypes.Compact }));

        Assert.Equal("invalid request: compact is only supported by OpenAI Responses API", ex.Message);
    }

    [Fact]
    public void OutboundTransformRequest_UnknownRequestType()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformRequest(
                new LlmRequest { RequestType = RequestTypes.Chat }));

        Assert.Equal("invalid request: chat is not supported", ex.Message);
    }

    // ------------------------------------------------ outbound rerank TransformResponse

    [Fact]
    public void OutboundTransformRerankResponse_Valid()
    {
        var body = GoJson.Marshal(new JinaRerankResponse
        {
            Model = "jina-reranker-v2-base-multilingual",
            Object = "list",
            Results =
            [
                new JinaRerankResult
                {
                    Index = 0,
                    RelevanceScore = 0.9,
                    Document = new JinaRerankDocument { Text = "Paris" },
                },
            ],
            Usage = new JinaRerankUsage { PromptTokens = 5, TotalTokens = 5, Cost = 0.000005 },
        });

        var resp = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
            .TransformResponse(new HttpResponseData
            {
                StatusCode = 200,
                Body = body,
                Request = new HttpRequestData { ApiFormat = ApiFormats.JinaRerank },
            });

        Assert.NotNull(resp.Rerank);
        Assert.Equal("list", resp.Rerank!.Object);
        Assert.Equal("jina-reranker-v2-base-multilingual", resp.Model);
        Assert.Single(resp.Rerank.Results);
        Assert.Equal(0, resp.Rerank.Results[0].Index);
        Assert.Equal(0.9, resp.Rerank.Results[0].RelevanceScore, 9);
        Assert.Equal("Paris", resp.Rerank.Results[0].Document!.Text);
        Assert.Equal(RequestTypes.Rerank, resp.RequestType);
        Assert.Equal(ApiFormats.JinaRerank, resp.ApiFormat);
        Assert.NotNull(resp.Usage);
        Assert.Equal(5L, resp.Usage!.PromptTokens);

        // The unified usage carries only the token counters; cost is dropped
        // (outbound.go maps PromptTokens/TotalTokens and nothing else).
        Assert.Null(resp.Usage.Cost);
    }

    [Fact]
    public void OutboundTransformRerankResponse_NullResultElementDecodesAsZeroResult()
    {
        var body = Encoding.UTF8.GetBytes(
            "{\"model\":\"m\",\"object\":\"list\",\"results\":[null]}");

        var resp = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
            .TransformResponse(new HttpResponseData
            {
                StatusCode = 200,
                Body = body,
                Request = new HttpRequestData { ApiFormat = ApiFormats.JinaRerank },
            });

        var result = Assert.Single(resp.Rerank!.Results);
        Assert.Equal(0L, result.Index);
        Assert.Equal(0d, result.RelevanceScore);
        Assert.Null(result.Document);
    }

    [Fact]
    public void OutboundTransformRerankResponse_InvalidJson()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
                .TransformResponse(new HttpResponseData
                {
                    StatusCode = 200,
                    Body = Encoding.UTF8.GetBytes("not valid json"),
                    Request = new HttpRequestData { ApiFormat = ApiFormats.JinaRerank },
                }));

        Assert.StartsWith("failed to unmarshal rerank response: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutboundTransformResponse_NilHttpResponse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformResponse(null));

        Assert.Equal("http response is nil", ex.Message);
    }

    [Fact]
    public void OutboundTransformResponse_NilRequestDefaultsToRerankDecoder()
    {
        // C080: Go's httpResp.Request.APIFormat panics on a nil Request; the
        // port's null-safe dispatch falls back to the rerank decoder (the
        // reference always attaches the request, so this is unreachable there).
        var body = GoJson.Marshal(new JinaRerankResponse
        {
            Model = "m",
            Object = "list",
            Results = [new JinaRerankResult { Index = 0, RelevanceScore = 0.9 }],
        });

        var resp = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
            .TransformResponse(new HttpResponseData { StatusCode = 200, Body = body });

        Assert.NotNull(resp.Rerank);
        Assert.Equal("list", resp.Rerank!.Object);
    }

    [Fact]
    public void OutboundTransformResponse_EmptyBody()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
                .TransformResponse(new HttpResponseData
                {
                    StatusCode = 200,
                    Body = [],
                    Request = new HttpRequestData { ApiFormat = ApiFormats.JinaRerank },
                }));

        Assert.Equal("response body is empty", ex.Message);
    }

    [Fact]
    public void OutboundTransformResponse_HttpErrorKeepsJinaEnvelope()
    {
        var ex = Assert.Throws<ResponseError>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
                .TransformResponse(new HttpResponseData
                {
                    StatusCode = 400,
                    Body = Encoding.UTF8.GetBytes(
                        "{\"error\":{\"message\":\"Invalid model\",\"type\":\"invalid_request_error\"}}"),
                    Request = new HttpRequestData { ApiFormat = ApiFormats.JinaRerank },
                }));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Invalid model", ex.Detail.Message);
        Assert.Equal("invalid_request_error", ex.Detail.Type);
    }

    [Fact]
    public void OutboundTransformResponse_HttpErrorNonJsonFallsBackToStatusText()
    {
        var ex = Assert.Throws<ResponseError>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
                .TransformResponse(new HttpResponseData
                {
                    StatusCode = 502,
                    Body = Encoding.UTF8.GetBytes("Bad gateway body"),
                    Request = new HttpRequestData { ApiFormat = ApiFormats.JinaRerank },
                }));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("Bad Gateway", ex.Detail.Message);
        Assert.Equal("api_error", ex.Detail.Type);
    }

    // --------------------------------------------------- outbound TransformError

    [Fact]
    public void OutboundTransformError_NilError()
    {
        var err = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformError(null);

        Assert.Equal(500, err.StatusCode);
        Assert.Equal("Internal Server Error", err.Detail.Message);
        Assert.Equal("api_error", err.Detail.Type);
    }

    [Fact]
    public void OutboundTransformError_JinaEnvelope()
    {
        var err = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformError(
            new HttpClientError(
                "POST",
                "https://api.jina.ai/v1/rerank",
                400,
                "400 Bad Request",
                Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"Invalid model\",\"type\":\"invalid_request_error\"}}")));

        Assert.Equal(400, err.StatusCode);
        Assert.Equal("Invalid model", err.Detail.Message);
        Assert.Equal("invalid_request_error", err.Detail.Type);
    }

    [Fact]
    public void OutboundTransformError_NonJsonBody()
    {
        var err = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformError(
            new HttpClientError(
                "POST",
                "https://api.jina.ai/v1/rerank",
                503,
                "503 Service Unavailable",
                Encoding.UTF8.GetBytes("Service unavailable")));

        Assert.Equal(503, err.StatusCode);
        Assert.Equal("Service Unavailable", err.Detail.Message);
        Assert.Equal("api_error", err.Detail.Type);
    }

    // ---------------------------------------------- outbound embedding TransformRequest

    [Fact]
    public void OutboundTransformEmbeddingRequest_DefaultsTaskToTextMatching()
    {
        var httpReq = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
            .TransformRequest(new LlmRequest
            {
                Model = "jina-embeddings-v3",
                RequestType = RequestTypes.Embedding,
                Embedding = new EmbeddingRequest { Input = new EmbeddingInput { String = "Hello world" } },
            });

        Assert.Equal("POST", httpReq.Method);
        Assert.Equal("https://api.jina.ai/v1/embeddings", httpReq.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpReq.Headers, "Accept"));
        Assert.Equal(AuthTypes.Bearer, httpReq.Auth!.Type);
        Assert.Equal(RequestTypes.Embedding, httpReq.RequestType);
        Assert.Equal(ApiFormats.JinaEmbedding, httpReq.ApiFormat);

        // Byte-exact: input, model, task follow Go's struct field order;
        // encoding_format, dimensions and user are omitempty.
        Assert.Equal(
            "{\"input\":\"Hello world\",\"model\":\"jina-embeddings-v3\",\"task\":\"text-matching\"}",
            Encoding.UTF8.GetString(httpReq.Body!));
    }

    [Fact]
    public void OutboundTransformEmbeddingRequest_ExplicitTask()
    {
        var httpReq = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
            .TransformRequest(new LlmRequest
            {
                Model = "jina-embeddings-v3",
                RequestType = RequestTypes.Embedding,
                Embedding = new EmbeddingRequest
                {
                    Input = new EmbeddingInput { String = "Hello world" },
                    Task = "retrieval.query",
                },
            });

        Assert.Equal(
            "{\"input\":\"Hello world\",\"model\":\"jina-embeddings-v3\",\"task\":\"retrieval.query\"}",
            Encoding.UTF8.GetString(httpReq.Body!));
    }

    [Fact]
    public void OutboundTransformEmbeddingRequest_NilEmbeddingRequest()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformRequest(
                new LlmRequest { Model = "m", RequestType = RequestTypes.Embedding }));

        Assert.Equal("embedding request is nil in llm.Request", ex.Message);
    }

    [Fact]
    public void OutboundTransformEmbeddingRequest_MissingModel()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key").TransformRequest(
                new LlmRequest
                {
                    RequestType = RequestTypes.Embedding,
                    Embedding = new EmbeddingRequest { Input = new EmbeddingInput { String = "x" } },
                }));

        Assert.Equal("model is required", ex.Message);
    }

    // ----------------------------------------------- outbound embedding TransformResponse

    [Fact]
    public void OutboundTransformEmbeddingResponse_Valid()
    {
        var body = GoJson.Marshal(new JinaEmbeddingResponse
        {
            Object = "list",
            Model = "jina-embeddings-v3",
            Data =
            [
                new JinaEmbeddingData
                {
                    Object = "embedding",
                    Index = 0,
                    Embedding = new Embedding { Values = [0.1, 0.2, 0.3] },
                },
            ],
            Usage = new JinaEmbeddingUsage { PromptTokens = 5, TotalTokens = 5 },
        });

        var resp = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
            .TransformResponse(new HttpResponseData
            {
                StatusCode = 200,
                Body = body,
                Request = new HttpRequestData { ApiFormat = ApiFormats.JinaEmbedding },
            });

        Assert.NotNull(resp.Embedding);
        Assert.Equal("list", resp.Embedding!.Object);
        Assert.Equal("jina-embeddings-v3", resp.Model);
        Assert.Equal(RequestTypes.Embedding, resp.RequestType);
        Assert.Equal(ApiFormats.JinaEmbedding, resp.ApiFormat);
        Assert.NotNull(resp.Usage);
        Assert.Equal(5L, resp.Usage!.PromptTokens);
        Assert.Equal(5L, resp.Usage.TotalTokens);
    }

    [Fact]
    public void OutboundTransformEmbeddingResponse_NullDataElementDecodesAsZeroData()
    {
        var body = Encoding.UTF8.GetBytes(
            "{\"object\":\"list\",\"model\":\"m\",\"data\":[null],"
            + "\"usage\":{\"prompt_tokens\":5,\"total_tokens\":5}}");

        var resp = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
            .TransformResponse(new HttpResponseData
            {
                StatusCode = 200,
                Body = body,
                Request = new HttpRequestData { ApiFormat = ApiFormats.JinaEmbedding },
            });

        var data = Assert.Single(resp.Embedding!.Data!);
        Assert.Equal(string.Empty, data.Object);
        Assert.Equal(0L, data.Index);
        Assert.Null(data.Embedding.Values);
        Assert.Equal(string.Empty, data.Embedding.Base64);
    }

    [Fact]
    public void OutboundTransformEmbeddingResponse_ZeroUsageIsDropped()
    {
        var body = GoJson.Marshal(new JinaEmbeddingResponse
        {
            Object = "list",
            Data = [],
            Usage = new JinaEmbeddingUsage(),
        });

        var resp = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
            .TransformResponse(new HttpResponseData
            {
                StatusCode = 200,
                Body = body,
                Request = new HttpRequestData { ApiFormat = ApiFormats.JinaEmbedding },
            });

        // outbound.go: usage is only carried when a token counter is positive.
        Assert.Null(resp.Usage);
    }

    [Fact]
    public void OutboundTransformEmbeddingResponse_InvalidJson()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key")
                .TransformResponse(new HttpResponseData
                {
                    StatusCode = 200,
                    Body = Encoding.UTF8.GetBytes("not valid json"),
                    Request = new HttpRequestData { ApiFormat = ApiFormats.JinaEmbedding },
                }));

        Assert.StartsWith("failed to unmarshal embedding response: ", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- outbound streaming

    [Fact]
    public void OutboundTransformStream_NotSupported()
    {
        var transformer = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            transformer.TransformStream(null, new SliceStream<StreamEvent>([])));

        Assert.Equal("rerank does not support streaming", ex.Message);
    }

    [Fact]
    public void OutboundAggregateStreamChunks_NotSupported()
    {
        var transformer = JinaOutboundTransformer.Create("https://api.jina.ai/v1", "test-key");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            transformer.AggregateStreamChunks(null, [], out _));

        Assert.Equal("rerank does not support streaming", ex.Message);
    }

    // ------------------------------------------- jina embedding inbound TransformRequest

    [Fact]
    public void JinaEmbeddingInboundTransformRequest_ValidStringInput()
    {
        var req = new JinaEmbeddingInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"jina-embeddings-v3","input":"The quick brown fox"}"""));

        Assert.Equal("jina-embeddings-v3", req.Model);
        Assert.Equal(RequestTypes.Embedding, req.RequestType);
        Assert.Equal(ApiFormats.JinaEmbedding, req.ApiFormat);
        Assert.Null(req.Stream);
        Assert.NotNull(req.Embedding);
        Assert.Equal(EmbeddingInputTypes.String, req.Embedding!.Input.GetType());
        Assert.Equal("The quick brown fox", req.Embedding.Input.String);

        // The nullable wire model normalizes an absent task to null; the
        // outbound still defaults it to text-matching (identical wire output).
        Assert.Null(req.Embedding.Task);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformRequest_WithTask()
    {
        var req = new JinaEmbeddingInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"jina-embeddings-v3","input":"The quick brown fox","task":"retrieval.query"}"""));

        Assert.Equal("retrieval.query", req.Embedding!.Task);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformRequest_ValidArrayInput()
    {
        var req = new JinaEmbeddingInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"jina-embeddings-v3","input":["Hello","World"]}"""));

        Assert.Equal(EmbeddingInputTypes.StringArray, req.Embedding!.Input.GetType());
        Assert.Equal(HelloWorldInputs, req.Embedding.Input.StringArray);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformRequest_MissingModel()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaEmbeddingInboundTransformer().TransformRequest(JsonRequest("""{"input":"test"}""")));

        Assert.Equal("invalid request: model is required", ex.Message);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformRequest_MissingInput()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaEmbeddingInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"jina-embeddings-v3"}""")));

        Assert.Equal("invalid request: input cannot be empty string", ex.Message);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformRequest_EmptyStringInput()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaEmbeddingInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"jina-embeddings-v3","input":""}""")));

        Assert.Equal("invalid request: input cannot be empty string", ex.Message);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformRequest_EmptyArrayInput()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaEmbeddingInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"jina-embeddings-v3","input":[]}""")));

        Assert.Equal("invalid request: input cannot be empty array", ex.Message);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformRequest_NilHttpRequest()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaEmbeddingInboundTransformer().TransformRequest(null));

        Assert.Equal("invalid request: http request is nil", ex.Message);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformRequest_EmptyBody()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaEmbeddingInboundTransformer().TransformRequest(JsonRequest(string.Empty)));

        Assert.Equal("invalid request: request body is empty", ex.Message);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformRequest_UnsupportedContentType()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaEmbeddingInboundTransformer().TransformRequest(JsonRequest("test", "text/plain")));

        Assert.Equal("invalid request: unsupported content type: text/plain", ex.Message);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformRequest_InvalidJson()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaEmbeddingInboundTransformer().TransformRequest(JsonRequest("not valid json")));

        Assert.StartsWith(
            "invalid request: failed to decode embedding request: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformRequest_ArrayRoot()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaEmbeddingInboundTransformer().TransformRequest(JsonRequest("[]")));

        Assert.Equal(
            "invalid request: failed to decode embedding request: "
            + "json: cannot unmarshal array into Go value of type jina.EmbeddingRequest",
            ex.Message);
    }

    // ------------------------------------------- jina embedding inbound TransformResponse

    [Fact]
    public void JinaEmbeddingInboundTransformResponse_Valid()
    {
        var resp = new JinaEmbeddingInboundTransformer().TransformResponse(new LlmResponse
        {
            Object = "list",
            Model = "jina-embeddings-v3",
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

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(resp.Headers, "Content-Type"));
        Assert.Equal("no-cache", HttpClientUtils.HeaderGet(resp.Headers, "Cache-Control"));

        var returned = LlmJson.Deserialize<JinaEmbeddingResponse>(resp.Body!)!;
        Assert.Equal("list", returned.Object);
        Assert.Equal("jina-embeddings-v3", returned.Model);
        Assert.Single(returned.Data);
        Assert.Equal(SampleVector, returned.Data[0].Embedding.Values);
        Assert.Equal(5L, returned.Usage.PromptTokens);
        Assert.Equal(5L, returned.Usage.TotalTokens);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformResponse_WithoutUsageStillEmitsZeroCounters()
    {
        var resp = new JinaEmbeddingInboundTransformer().TransformResponse(new LlmResponse
        {
            Model = "jina-embeddings-v3",
            Embedding = new EmbeddingResponse { Object = "list", Data = [] },
        });

        // Go's EmbeddingUsage is a struct, so the zero counters are still emitted.
        var returned = LlmJson.Deserialize<JinaEmbeddingResponse>(resp.Body!)!;
        Assert.Equal(0L, returned.Usage.PromptTokens);
        Assert.Equal(0L, returned.Usage.TotalTokens);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformResponse_UsageCostRoundTrips()
    {
        var resp = new JinaEmbeddingInboundTransformer().TransformResponse(new LlmResponse
        {
            Model = "m",
            Embedding = new EmbeddingResponse { Object = "list", Data = [] },
            Usage = new Usage { PromptTokens = 5, TotalTokens = 5, Cost = 0.000005 },
        });

        var returned = LlmJson.Deserialize<JinaEmbeddingResponse>(resp.Body!)!;
        Assert.NotNull(returned.Usage.Cost);
        Assert.Equal(0.000005, returned.Usage.Cost!.Value, 12);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformResponse_NilResponse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new JinaEmbeddingInboundTransformer().TransformResponse(null));

        Assert.Equal("embedding response is nil", ex.Message);
    }

    [Fact]
    public void JinaEmbeddingInboundTransformResponse_MissingEmbeddingData()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new JinaEmbeddingInboundTransformer().TransformResponse(new LlmResponse()));

        Assert.Equal("embedding response missing embedding data", ex.Message);
    }

    // -------------------------------------------- jina embedding inbound error/stream

    [Fact]
    public void JinaEmbeddingInboundTransformError_DelegatesToRerankTransformer()
    {
        Assert.Equal(500, new JinaEmbeddingInboundTransformer().TransformError(null).StatusCode);

        var resp = new JinaEmbeddingInboundTransformer().TransformError(new ResponseError
        {
            StatusCode = 400,
            Detail = new ErrorDetail { Message = "bad model", Type = "invalid_request_error" },
        });
        Assert.Equal(400, resp.StatusCode);
        Assert.Equal(
            "{\"error\":{\"message\":\"bad model\",\"type\":\"invalid_request_error\"}}",
            Encoding.UTF8.GetString(resp.Body!));
    }

    [Fact]
    public void JinaEmbeddingInboundTransformStream_InvalidRequest()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new JinaEmbeddingInboundTransformer().TransformStream(new SliceStream<LlmResponse>([])));

        Assert.Equal("invalid request: embeddings do not support streaming", ex.Message);
    }

    [Fact]
    public void JinaEmbeddingInboundAggregateStreamChunks_PlainError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new JinaEmbeddingInboundTransformer().AggregateStreamChunks([], out _));

        Assert.Equal("embeddings do not support streaming", ex.Message);
    }

    // ------------------------------------------------------------------ helpers

    private static LlmRequest RerankRequest(
        string model,
        string query,
        List<string> documents,
        int? topN = null,
        bool? returnDocuments = null)
        => new()
        {
            Model = model,
            RequestType = RequestTypes.Rerank,
            Rerank = new RerankRequest
            {
                Query = query,
                Documents = documents,
                TopN = topN,
                ReturnDocuments = returnDocuments,
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
