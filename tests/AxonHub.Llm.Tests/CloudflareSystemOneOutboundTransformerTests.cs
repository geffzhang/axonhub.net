using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Cloudflare;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.TypeSafe;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/cloudflare/outbound_test.go: the Workers AI System
/// One outbound selects native /run/{model} routes, rewrites the model to the
/// short selector, and unwraps the Cloudflare v4 envelope.
/// </summary>
public sealed class CloudflareSystemOneOutboundTransformerTests
{
    private const string TestBaseUrl =
        "https://api.cloudflare.com/client/v4/accounts/account-id/ai";

    [Fact]
    public void ConfigValidationMatchesTheReference()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new CloudflareSystemOneOutboundTransformer(null!));
        Assert.Throws<InvalidOperationException>(() =>
            new CloudflareSystemOneOutboundTransformer(new CloudflareSystemOneConfig
            {
                BaseUrl = TestBaseUrl,
                ApiKeyProvider = null,
            }));
        Assert.Throws<InvalidOperationException>(() =>
            Outbound(baseUrl: string.Empty));
    }

    [Theory]
    [InlineData(TestBaseUrl, "", "@cf/cloudflare/clef", TestBaseUrl + "/run/@cf/cloudflare/clef")]
    [InlineData(TestBaseUrl + "/", "", "@cf/cloudflare/clef-flash", TestBaseUrl + "/run/@cf/cloudflare/clef-flash")]
    [InlineData(TestBaseUrl + "/v1", "", "@cf/cloudflare/clef-omni", TestBaseUrl + "/run/@cf/cloudflare/clef-omni")]
    [InlineData(TestBaseUrl + "/v1/", "", "@cf/cloudflare/clef", TestBaseUrl + "/run/@cf/cloudflare/clef")]
    [InlineData(TestBaseUrl, "/custom/run/", "@cf/cloudflare/clef", TestBaseUrl + "/custom/run/@cf/cloudflare/clef")]
    [InlineData(TestBaseUrl, "custom/run", "@cf/cloudflare/clef", TestBaseUrl + "/custom/run/@cf/cloudflare/clef")]
    [InlineData(TestBaseUrl, "/", "@cf/cloudflare/clef", TestBaseUrl + "/@cf/cloudflare/clef")]
    public void TransformRequestBuildsTheNativeRunUrl(
        string baseUrl,
        string endpointPath,
        string model,
        string expectedUrl)
    {
        var request = Outbound(baseUrl, endpointPath: endpointPath)
            .TransformRequest(Request(model));

        Assert.Equal("POST", request.Method);
        Assert.Equal(expectedUrl, request.Url);
        Assert.Equal("bearer", request.Auth!.Type);
        Assert.Equal("test-key", request.Auth.ApiKey);
        Assert.Equal(ApiFormats.TypeSafeSystemOne, request.ApiFormat);
    }

    [Theory]
    [InlineData("@cf/cloudflare/clef", "clef")]
    [InlineData("@cf/cloudflare/clef-flash", "clef-flash")]
    [InlineData("@cf/cloudflare/clef-omni", "clef-omni")]
    [InlineData("clef", "clef")]
    public void TransformRequestRewritesTheModelSelector(string model, string expected)
    {
        var request = Outbound().TransformRequest(Request(model));

        using var document = JsonDocument.Parse(request.Body!);
        var root = document.RootElement;
        Assert.Equal(expected, root.GetProperty("model").GetString());
        Assert.Equal(
            """{"ticket":"Refund request"}""",
            root.GetProperty("state").GetRawText());
        Assert.Equal(
            """{"check":{"type":"noul","instructions":"Is this a refund?"}}""",
            root.GetProperty("questions").GetRawText());
        Assert.False(root.TryGetProperty("input", out _));
    }

    [Fact]
    public void TransformRequestRejectsInvalidRequests()
    {
        var outbound = Outbound();

        Assert.Throws<InvalidOperationException>(() => outbound.TransformRequest(null));
        Assert.Throws<InvalidOperationException>(() =>
            outbound.TransformRequest(new LlmRequest { Model = "@cf/cloudflare/clef" }));
        Assert.Throws<InvalidOperationException>(() =>
            outbound.TransformRequest(Request(string.Empty)));
    }

    [Fact]
    public void PassThroughIsVetoed()
    {
        var outbound = Outbound();
        Assert.False(outbound.AllowPassThroughBody(Request("@cf/cloudflare/clef"), null));
        Assert.False(outbound.AllowPassThroughResponse(Request("@cf/cloudflare/clef"), null));
    }

    [Theory]
    [InlineData(
        """
        {"success":true,"result":{"model":"clef","answers":{"check":{"type":"noul","noul":0.945}},"usage":{"input_tokens":120,"output_tokens":3}},"errors":[],"messages":[]}
        """)]
    [InlineData(
        """
        {"model":"clef","answers":{"check":{"type":"noul","noul":0.945}},"usage":{"input_tokens":120,"output_tokens":3}}
        """)]
    public void TransformResponseReadsEnvelopeAndFlatBodies(string body)
    {
        var response = Outbound().TransformResponse(JsonResponse(body));

        Assert.Equal("clef", response.Model);
        Assert.Equal(RequestTypes.SystemOne, response.RequestType);
        Assert.Equal(ApiFormats.TypeSafeSystemOne, response.ApiFormat);
        var answer = Assert.Single(response.SystemOne!.Answers!);
        Assert.Equal("noul", answer.Value.Type);
        Assert.Equal(0.945, answer.Value.Noul!.Value, 9);
        Assert.Equal(120L, response.Usage!.PromptTokens);
        Assert.Equal(3L, response.Usage.CompletionTokens);
        Assert.Equal(123L, response.Usage.TotalTokens);
    }

    [Fact]
    public void TransformResponseSurfacesEnvelopeFailures()
    {
        var successFalse = Assert.Throws<ResponseError>(() =>
            Outbound().TransformResponse(JsonResponse(
                """{"success":false,"errors":[{"code":5006,"message":"AiError: Bad input"}],"messages":[],"result":null}""")));
        Assert.Equal(502, successFalse.StatusCode);
        Assert.Equal("AiError: Bad input", successFalse.Detail.Message);
        Assert.Equal("5006", successFalse.Detail.Code);

        var statusKept = Assert.Throws<ResponseError>(() =>
            Outbound().TransformResponse(new HttpResponseData
            {
                StatusCode = 429,
                Body = Encoding.UTF8.GetBytes(
                    """{"success":false,"errors":[{"code":3040,"message":"Capacity temporarily exceeded"}],"messages":[],"result":null}"""),
            }));
        Assert.Equal(429, statusKept.StatusCode);
        Assert.Equal("Capacity temporarily exceeded", statusKept.Detail.Message);
        Assert.Equal("3040", statusKept.Detail.Code);

        var errorsWithoutSuccess = Assert.Throws<ResponseError>(() =>
            Outbound().TransformResponse(JsonResponse(
                """{"errors":[{"code":7001,"message":"GET not supported for requested URI."}],"result":null}""")));
        Assert.Equal(502, errorsWithoutSuccess.StatusCode);

        var missingResult = Assert.Throws<InvalidOperationException>(() =>
            Outbound().TransformResponse(JsonResponse(
                """{"success":true,"errors":[],"messages":[]}""")));
        Assert.Contains("envelope has no result", missingResult.Message, StringComparison.Ordinal);

        var nullResult = Assert.Throws<InvalidOperationException>(() =>
            Outbound().TransformResponse(JsonResponse(
                """{"success":true,"result":null,"errors":[],"messages":[]}""")));
        Assert.Contains("unexpected result", nullResult.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"success":true,"result":{"model":"clef"}}""")]
    [InlineData("""{"success":true,"result":{"model":"clef","answers":{}}}""")]
    [InlineData("""{"model":"clef","usage":{"input_tokens":120,"output_tokens":3}}""")]
    public void TransformResponseRejectsBodiesWithoutAnswers(string body)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Outbound().TransformResponse(JsonResponse(body)));
        Assert.Contains("systemone response has no answers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformResponseRejectsInvalidBodies()
    {
        var outbound = Outbound();
        Assert.Throws<InvalidOperationException>(() => outbound.TransformResponse(null));
        Assert.Throws<InvalidOperationException>(() =>
            outbound.TransformResponse(new HttpResponseData { StatusCode = 200 }));
        Assert.Throws<InvalidOperationException>(() =>
            outbound.TransformResponse(JsonResponse("""{"result":""")));
    }

    [Fact]
    public void TransformErrorUsesEnvelopeMessagesThenRawBody()
    {
        var outbound = Outbound();

        var envelope = outbound.TransformError(new HttpClientError(
            "POST",
            string.Empty,
            400,
            "400 Bad Request",
            Encoding.UTF8.GetBytes(
                """{"success":false,"errors":[{"code":7003,"message":"Could not route"}],"messages":[],"result":null}""")));
        Assert.Equal(400, envelope.StatusCode);
        Assert.Equal("Could not route", envelope.Detail.Message);
        Assert.Equal("7003", envelope.Detail.Code);
        Assert.Equal("api_error", envelope.Detail.Type);

        var multiple = outbound.TransformError(new HttpClientError(
            "POST",
            string.Empty,
            401,
            "401 Unauthorized",
            Encoding.UTF8.GetBytes(
                """{"success":false,"errors":[{"code":10000,"message":"Authentication error"},{"code":"9109","message":"Invalid access token"}]}""")));
        Assert.Equal("Authentication error; Invalid access token", multiple.Detail.Message);
        Assert.Equal("10000", multiple.Detail.Code);

        var raw = outbound.TransformError(new HttpClientError(
            "POST",
            string.Empty,
            502,
            "502 Bad Gateway",
            Encoding.UTF8.GetBytes("upstream unavailable")));
        Assert.Equal("upstream unavailable", raw.Detail.Message);
        Assert.Equal("api_error", raw.Detail.Type);

        var openAiStyle = outbound.TransformError(new HttpClientError(
            "POST",
            string.Empty,
            400,
            "400 Bad Request",
            Encoding.UTF8.GetBytes("""{"error":{"message":"upstream boom","type":"invalid_request_error"}}""")));
        Assert.Contains("upstream boom", openAiStyle.Detail.Message, StringComparison.Ordinal);

        var nil = outbound.TransformError(null);
        Assert.Equal(500, nil.StatusCode);
    }

    private static CloudflareSystemOneOutboundTransformer Outbound(
        string baseUrl = TestBaseUrl,
        string endpointPath = "",
        IApiKeyProvider? apiKeyProvider = null)
        => new(new CloudflareSystemOneConfig
        {
            BaseUrl = baseUrl,
            EndpointPath = endpointPath,
            ApiKeyProvider = apiKeyProvider ?? new StaticApiKeyProvider("test-key"),
        });

    private static LlmRequest Request(string model) => new()
    {
        Model = model,
        RequestType = RequestTypes.SystemOne,
        ApiFormat = ApiFormats.TypeSafeSystemOne,
        SystemOne = new SystemOneRequest
        {
            State = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["ticket"] = "Refund request",
            }),
            Questions = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal)
            {
                ["check"] = new()
                {
                    Type = "noul",
                    Instructions = JsonSerializer.SerializeToElement("Is this a refund?"),
                },
            },
        },
    };

    private static HttpResponseData JsonResponse(string json) => new()
    {
        StatusCode = 200,
        Body = Encoding.UTF8.GetBytes(json),
    };
}
