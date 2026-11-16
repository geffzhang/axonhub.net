using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the /v1/moderations surface and the OpenAI moderation
/// outbound path (llm/transformer/openai/moderation_test.go, moderation.go,
/// moderation_inbound.go). Every test mirrors a reference subtest one-for-one.
/// </summary>
public sealed class OpenAiModerationTransformerTests
{
    private static readonly string[] OneTwo = ["one", "two"];

    private static readonly string[] AB = ["a", "b"];

    // ------------------------------------------------ inbound TransformRequest

    [Fact]
    public void InboundTransformRequest_ValidStringInputWithModel()
    {
        var req = new ModerationInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"omni-moderation-latest","input":"hello world"}"""));

        Assert.Equal("omni-moderation-latest", req.Model);
        Assert.Equal(RequestTypes.Moderation, req.RequestType);
        Assert.Equal(ApiFormats.OpenAIModeration, req.ApiFormat);
        Assert.NotNull(req.Moderation);
        Assert.Equal("hello world", req.Moderation!.Input.String);
    }

    [Fact]
    public void InboundTransformRequest_DefaultsModelWhenOmitted()
    {
        var req = new ModerationInboundTransformer().TransformRequest(
            JsonRequest("""{"input":"I want to kill them."}"""));

        Assert.Equal("omni-moderation-latest", req.Model);
    }

    [Fact]
    public void InboundTransformRequest_StringArrayInput()
    {
        var req = new ModerationInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"omni-moderation-latest","input":["one","two"]}"""));

        Assert.Equal(OneTwo, req.Moderation!.Input.StringArray);
    }

    [Fact]
    public void InboundTransformRequest_MultimodalInput()
    {
        var req = new ModerationInboundTransformer().TransformRequest(JsonRequest(
            """{"model":"omni-moderation-latest","input":[{"type":"text","text":"classify this"},{"type":"image_url","image_url":{"url":"https://example.com/a.png"}}]}"""));

        var parts = req.Moderation!.Input.Parts!;
        Assert.Equal(2, parts.Count);
        Assert.Equal("text", parts[0].Type);
        Assert.Equal("image_url", parts[1].Type);
        Assert.Equal("https://example.com/a.png", parts[1].ImageURL!.Url);
    }

    [Fact]
    public void InboundTransformRequest_EmptyInputRejected()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new ModerationInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"omni-moderation-latest","input":""}""")));

        Assert.Contains("input cannot be empty string", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_EmptyArrayInputRejected()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new ModerationInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"omni-moderation-latest","input":[]}""")));

        Assert.Contains("input cannot be empty array", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_MultimodalMissingImageUrlRejected()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new ModerationInboundTransformer().TransformRequest(JsonRequest(
                """{"model":"omni-moderation-latest","input":[{"type":"image_url","image_url":{"url":""}}]}""")));

        Assert.Contains("image_url.url is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_UnsupportedContentTypeRejected()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new ModerationInboundTransformer().TransformRequest(
                JsonRequest("""{"input":"x"}""", "text/plain")));

        Assert.Contains("unsupported content type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_EmptyBodyRejected()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new ModerationInboundTransformer().TransformRequest(JsonRequest(string.Empty)));

        Assert.Contains("request body is empty", ex.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------- inbound TransformResponse

    [Fact]
    public void InboundTransformResponse_RoundTrip()
    {
        var httpResp = new ModerationInboundTransformer().TransformResponse(new LlmResponse
        {
            Id = "modr-test",
            Model = "omni-moderation-latest",
            Moderation = new ModerationResponse
            {
                Results =
                [
                    new ModerationClassification
                    {
                        Flagged = true,
                        Categories = new Dictionary<string, bool> { ["violence"] = true },
                        CategoryScores = new Dictionary<string, double> { ["violence"] = 0.9 },
                    },
                ],
            },
        });

        Assert.Equal(200, httpResp.StatusCode);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpResp.Headers, "Content-Type"));
        Assert.Equal("no-cache", HttpClientUtils.HeaderGet(httpResp.Headers, "Cache-Control"));

        var got = LlmJson.Deserialize<OpenAiModerationCreateResponse>(httpResp.Body!)!;
        Assert.Equal("modr-test", got.Id);
        Assert.Equal("omni-moderation-latest", got.Model);
        Assert.True(got.Results![0].Flagged);
        Assert.True(got.Results[0].Categories!["violence"]);
        Assert.Equal(0.9, got.Results[0].CategoryScores!["violence"]);
    }

    // ------------------------------------------------------- outbound round trip

    [Fact]
    public void OutboundTransformer_ModerationRoundTrip()
    {
        var transformer = NewTransformer();

        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "omni-moderation-latest",
            RequestType = RequestTypes.Moderation,
            Moderation = new ModerationRequest
            {
                Input = new ModerationInput { String = "hello" },
            },
        });

        Assert.Equal("https://api.openai.com/v1/moderations", httpReq.Url);
        Assert.Equal(RequestTypes.Moderation, httpReq.RequestType);
        Assert.Equal(ApiFormats.OpenAIModeration, httpReq.ApiFormat);

        using (var doc = JsonDocument.Parse(httpReq.Body!))
        {
            Assert.Equal("omni-moderation-latest", doc.RootElement.GetProperty("model").GetString());
            Assert.Equal("hello", doc.RootElement.GetProperty("input").GetString());
        }

        var upstreamBody = GoJson.Marshal(new OpenAiModerationCreateResponse
        {
            Id = "modr-1",
            Model = "omni-moderation-latest",
            Results =
            [
                new ModerationClassification
                {
                    Flagged = false,
                    Categories = new Dictionary<string, bool> { ["violence"] = false },
                    CategoryScores = new Dictionary<string, double> { ["violence"] = 0.01 },
                },
            ],
        });

        var llmResp = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = upstreamBody,
            Request = httpReq,
        });

        Assert.Equal("modr-1", llmResp.Id);
        Assert.NotNull(llmResp.Moderation);
        Assert.False(llmResp.Moderation!.Results![0].Flagged);
        Assert.Equal(RequestTypes.Moderation, llmResp.RequestType);
        Assert.Equal(ApiFormats.OpenAIModeration, llmResp.ApiFormat);
    }

    [Fact]
    public void OutboundTransformer_ModerationCustomEndpointPath()
    {
        var transformer = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
        {
            PlatformType = OpenAiPlatformType.OpenAi,
            BaseUrl = "https://gateway.example.com/v1",
            EndpointPath = "/custom/moderations",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "omni-moderation-latest",
            RequestType = RequestTypes.Moderation,
            Moderation = new ModerationRequest
            {
                Input = new ModerationInput { String = "hello" },
            },
        });

        Assert.Equal("https://gateway.example.com/v1/custom/moderations", httpReq.Url);
    }

    [Fact]
    public void OutboundTransformer_ModerationMissingPayload()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewTransformer().TransformRequest(new LlmRequest
            {
                Model = "omni-moderation-latest",
                RequestType = RequestTypes.Moderation,
            }));

        Assert.Contains("moderation request is nil", ex.Message, StringComparison.Ordinal);
    }

    // --------------------------------------------------------- moderation input

    [Fact]
    public void ModerationInputJson_String()
    {
        var input = LlmJson.Deserialize<ModerationInput>("\"hi\"")!;

        Assert.Equal("hi", input.String);
        Assert.Equal("\"hi\"", Encoding.UTF8.GetString(GoJson.Marshal(input)));
    }

    [Fact]
    public void ModerationInputJson_StringArray()
    {
        var input = LlmJson.Deserialize<ModerationInput>("[\"a\",\"b\"]")!;

        Assert.Equal(AB, input.StringArray);
    }

    [Fact]
    public void ModerationInputJson_Parts()
    {
        var input = LlmJson.Deserialize<ModerationInput>("[{\"type\":\"text\",\"text\":\"x\"}]")!;

        Assert.Single(input.Parts!);
        Assert.Equal("text", input.Parts![0].Type);
    }

    // ----------------------------------------------------------------- helpers

    private static OpenAiOutboundTransformer NewTransformer()
        => OpenAiOutboundTransformer.Create("https://api.openai.com/v1", "test-key");

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
