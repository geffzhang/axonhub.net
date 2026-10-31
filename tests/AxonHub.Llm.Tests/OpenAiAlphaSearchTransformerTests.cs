using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the alpha search slice: llm/transformer/openai/
/// alpha_search_inbound.go, alpha_search_outbound.go and alpha_search_test.go.
/// </summary>
public sealed class OpenAiAlphaSearchTransformerTests
{
    private const string RawBody =
        """{"id":"session-123","model":"client-model","commands":{"search_query":[{"q":"golang"}]}}""";

    // Port of TestAlphaSearchInboundAndOutbound.
    [Fact]
    public void InboundAndOutbound_RoundTripsRawBody()
    {
        var raw = Encoding.UTF8.GetBytes(RawBody);
        var inbound = new AlphaSearchInboundTransformer();

        var request = inbound.TransformRequest(JsonRequest(raw));

        Assert.Equal(RequestTypes.AlphaSearch, request.RequestType);
        Assert.Equal(ApiFormats.OpenAIAlphaSearch, request.ApiFormat);
        Assert.Equal("client-model", request.Model);
        Assert.Empty(request.Messages);
        Assert.NotNull(request.AlphaSearch);
        Assert.Equal(raw, request.AlphaSearch!.Body);

        var outbound = OpenAiOutboundTransformer.Create("https://cpa.example/v1", "test-key");
        var providerRequest = outbound.TransformRequest(request);

        Assert.Equal("POST", providerRequest.Method);
        Assert.Equal("https://cpa.example/v1/alpha/search", providerRequest.Url);
        Assert.Equal(RequestTypes.AlphaSearch, providerRequest.RequestType);
        Assert.Equal(ApiFormats.OpenAIAlphaSearch, providerRequest.ApiFormat);
        AssertJsonEqual(RawBody, Encoding.UTF8.GetString(providerRequest.Body!));

        var providerResponse = outbound.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes("""{"results":[{"title":"Go"}]}"""),
            Request = providerRequest,
        });

        Assert.Equal(RequestTypes.AlphaSearch, providerResponse.RequestType);
        Assert.Equal(ApiFormats.OpenAIAlphaSearch, providerResponse.ApiFormat);

        var clientResponse = inbound.TransformResponse(providerResponse);

        Assert.Equal(200, clientResponse.StatusCode);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(clientResponse.Headers, "Content-Type"));
        AssertJsonEqual("""{"results":[{"title":"Go"}]}""", Encoding.UTF8.GetString(clientResponse.Body!));
    }

    // Port of TestAlphaSearchInboundRequiresModel.
    [Fact]
    public void Inbound_RequiresModel()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new AlphaSearchInboundTransformer().TransformRequest(
                RawRequest("""{"commands":{"search_query":[]}}""")));

        Assert.Contains("model is required for alpha search routing", ex.Message, StringComparison.Ordinal);
    }

    // Port of TestAlphaSearchOutboundTransformsUpstreamError.
    [Fact]
    public void Outbound_TransformsUpstreamError()
    {
        var outbound = OpenAiOutboundTransformer.Create("https://cpa.example/v1", "test-key");

        var ex = Assert.Throws<ResponseError>(() => outbound.TransformResponse(new HttpResponseData
        {
            StatusCode = 429,
            Body = Encoding.UTF8.GetBytes(
                """{"error":{"message":"search rate limit","type":"rate_limit_error","code":"rate_limit"}}"""),
            Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIAlphaSearch },
        }));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal("search rate limit", ex.Detail.Message);
        Assert.Equal("rate_limit_error", ex.Detail.Type);
        Assert.Equal("rate_limit", ex.Detail.Code);
    }

    [Fact]
    public void Inbound_AcceptsNestingDeeperThanTheJsonReaderDefault()
    {
        // Go's encoding/json accepts up to 10000 nesting levels; the port's
        // parser must not fall back to System.Text.Json's default of 64.
        var nested = new string('[', 65) + new string(']', 65);

        var request = new AlphaSearchInboundTransformer().TransformRequest(
            RawRequest("{\"model\":\"m\",\"payload\":" + nested + "}"));

        Assert.Equal("m", request.Model);
    }

    [Fact]
    public void Inbound_ModelIsTrimmedAndWhitespaceOnlyIsRejected()
    {
        var request = new AlphaSearchInboundTransformer().TransformRequest(
            RawRequest("""{"model":"  routed-model  "}"""));

        Assert.Equal("routed-model", request.Model);

        var ex = Assert.Throws<TransformerException>(() =>
            new AlphaSearchInboundTransformer().TransformRequest(
                RawRequest("""{"model":"   "}""")));

        Assert.Contains("model is required for alpha search routing", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[]""", "json: cannot unmarshal array into Go value of type map[string]json.RawMessage")]
    [InlineData("\"abc\"", "json: cannot unmarshal string into Go value of type map[string]json.RawMessage")]
    [InlineData("123", "json: cannot unmarshal number into Go value of type map[string]json.RawMessage")]
    [InlineData("null", "<nil>")]
    public void Inbound_RejectsNonObjectEnvelope(string body, string expectedDetail)
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new AlphaSearchInboundTransformer().TransformRequest(RawRequest(body)));

        Assert.Equal(
            "invalid request: failed to decode alpha search request: " + expectedDetail,
            ex.Message);
    }

    [Fact]
    public void Inbound_RejectsMalformedJson()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new AlphaSearchInboundTransformer().TransformRequest(RawRequest("""{"model":""")));

        Assert.Equal(
            "invalid request: failed to decode alpha search request: unexpected end of JSON input",
            ex.Message);
    }

    [Fact]
    public void Inbound_RejectsUnsupportedContentType()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new AlphaSearchInboundTransformer().TransformRequest(
                JsonRequest(Encoding.UTF8.GetBytes(RawBody), "text/plain")));

        Assert.Equal("invalid request: unsupported content type: text/plain", ex.Message);
    }

    [Fact]
    public void Inbound_RejectsEmptyBodyAndNilRequest()
    {
        var empty = Assert.Throws<TransformerException>(() =>
            new AlphaSearchInboundTransformer().TransformRequest(RawRequest(string.Empty)));
        Assert.Equal("invalid request: request body is empty", empty.Message);

        var nil = Assert.Throws<TransformerException>(() =>
            new AlphaSearchInboundTransformer().TransformRequest(null));
        Assert.Equal("invalid request: http request is nil", nil.Message);
    }

    [Fact]
    public void Outbound_ReplacesTheClientModelWithTheRoutedModel()
    {
        var outbound = OpenAiOutboundTransformer.Create("https://cpa.example/v1", "test-key");

        var providerRequest = outbound.TransformRequest(new LlmRequest
        {
            Model = "routed-model",
            RequestType = RequestTypes.AlphaSearch,
            AlphaSearch = new AlphaSearchRequest
            {
                Body = Encoding.UTF8.GetBytes("""{"model":"client-model","commands":{"q":"x"}}"""),
            },
        });

        AssertJsonEqual(
            """{"model":"routed-model","commands":{"q":"x"}}""",
            Encoding.UTF8.GetString(providerRequest.Body!));
    }

    [Fact]
    public void Outbound_AppendsTheRoutedModelWhenTheBodyHasNone()
    {
        var outbound = OpenAiOutboundTransformer.Create("https://cpa.example/v1", "test-key");

        var providerRequest = outbound.TransformRequest(new LlmRequest
        {
            Model = "routed-model",
            RequestType = RequestTypes.AlphaSearch,
            AlphaSearch = new AlphaSearchRequest
            {
                Body = Encoding.UTF8.GetBytes("""{"commands":{"q":"x"}}"""),
            },
        });

        AssertJsonEqual(
            """{"commands":{"q":"x"},"model":"routed-model"}""",
            Encoding.UTF8.GetString(providerRequest.Body!));
    }

    [Fact]
    public void Outbound_UsesConfiguredEndpointPath()
    {
        var outbound = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
        {
            BaseUrl = "https://cpa.example/v1",
            EndpointPath = "/custom/alpha",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

        var providerRequest = outbound.TransformRequest(new LlmRequest
        {
            Model = "routed-model",
            RequestType = RequestTypes.AlphaSearch,
            AlphaSearch = new AlphaSearchRequest
            {
                Body = Encoding.UTF8.GetBytes("""{"model":"m"}"""),
            },
        });

        Assert.Equal("https://cpa.example/v1/custom/alpha", providerRequest.Url);
    }

    [Fact]
    public void Outbound_RequiresTheAlphaRequestBody()
    {
        var outbound = OpenAiOutboundTransformer.Create("https://cpa.example/v1", "test-key");

        var ex = Assert.Throws<InvalidOperationException>(() => outbound.TransformRequest(new LlmRequest
        {
            Model = "routed-model",
            RequestType = RequestTypes.AlphaSearch,
        }));

        Assert.Equal("alpha search request is nil", ex.Message);
    }

    [Fact]
    public void Outbound_EmptySuccessfulBodyIsAnEmptyResponse()
    {
        var outbound = OpenAiOutboundTransformer.Create("https://cpa.example/v1", "test-key");

        var ex = Assert.Throws<InvalidOperationException>(() => outbound.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = [],
            Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIAlphaSearch },
        }));

        Assert.Equal("alpha search response body is empty", ex.Message);
    }

    [Fact]
    public void Inbound_EmptyResponseBodyThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new AlphaSearchInboundTransformer().TransformResponse(new LlmResponse
            {
                AlphaSearch = new AlphaSearchResponse(),
            }));

        Assert.Equal("alpha search response is empty", ex.Message);
    }

    [Fact]
    public void StreamingIsUnsupported()
    {
        var inbound = new AlphaSearchInboundTransformer();

        var stream = Assert.Throws<TransformerException>(() =>
            inbound.TransformStream(new SliceStream<LlmResponse>([])));
        Assert.Equal("invalid request: alpha search does not support streaming", stream.Message);

        var aggregate = Assert.Throws<InvalidOperationException>(() =>
            inbound.AggregateStreamChunks([], out _));
        Assert.Equal("alpha search does not support streaming", aggregate.Message);
    }

    // ------------------------------------------------------------- helpers

    private static HttpRequestData JsonRequest(byte[] body, string contentType = "application/json")
        => new()
        {
            Body = body,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = [contentType],
            },
        };

    private static HttpRequestData RawRequest(string body)
        => new() { Body = Encoding.UTF8.GetBytes(body) };

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement),
            "expected " + expected + " but got " + actual);
    }
}
