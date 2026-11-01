using System.Globalization;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Minimax;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the MiniMax outbound (llm/transformer/minimax and its
/// outbound_test.go): the /image_generation body, the size/dimension rules and
/// the business-error envelope, with chat delegated to the OpenAI outbound.
/// </summary>
public sealed class MinimaxOutboundTransformerTests
{
    [Fact]
    public void ConstructorValidation()
    {
        Assert.Equal(
            "base URL is required for MiniMax transformer",
            Assert.Throws<InvalidOperationException>(
                () => new MinimaxOutboundTransformer(null)).Message);

        Assert.Equal(
            "base URL is required for MiniMax transformer",
            Assert.Throws<InvalidOperationException>(
                () => new MinimaxOutboundTransformer(new MinimaxOutboundConfig
                {
                    BaseUrl = "   ",
                    ApiKeyProvider = new StaticApiKeyProvider("key"),
                })).Message);

        Assert.Equal(
            "API key provider is required for MiniMax transformer",
            Assert.Throws<InvalidOperationException>(
                () => new MinimaxOutboundTransformer(new MinimaxOutboundConfig
                {
                    BaseUrl = "https://api.minimax.io",
                })).Message);
    }

    // Reference TestImageGeneration_roundTrip_whenModelIsOmitted (request half).
    [Fact]
    public void TransformRequest_DefaultsTheModelAndResponseFormat()
    {
        var request = new LlmRequest
        {
            RequestType = RequestTypes.Image,
            Image = new ImageRequest { Prompt = "cat", ResponseFormat = "b64_json" },
        };

        var httpRequest = Transformer().TransformRequest(request);
        var body = Body(httpRequest);

        Assert.Equal("https://api.minimax.io/v1/image_generation", httpRequest.Url);
        Assert.Equal(RequestTypes.Image, httpRequest.RequestType);
        Assert.Equal(ApiFormats.OpenAIImageGeneration, httpRequest.ApiFormat);
        Assert.Equal("key", httpRequest.Auth!.ApiKey);
        Assert.Equal("image-01", body.GetProperty("model").GetString());
        Assert.Equal("base64", body.GetProperty("response_format").GetString());
        Assert.Equal("image-01", httpRequest.TransformerMetadata!["model"].GetString());
    }

    // Reference TestImageGeneration_preservesModel_whenExplicitOrMapped.
    [Theory]
    [InlineData("", false, "image-01")]
    [InlineData("dall-e-2", false, "image-01")]
    [InlineData("dall-e-2", true, "dall-e-2")]
    [InlineData("image-01-live", false, "image-01-live")]
    public void TransformRequest_ModelDefaultRules(string model, bool modelSpecified, string expected)
    {
        var request = new LlmRequest
        {
            Model = model,
            RequestType = RequestTypes.Image,
            Image = new ImageRequest { Prompt = "cat", ModelSpecified = modelSpecified },
        };

        var body = Body(Transformer().TransformRequest(request));

        Assert.Equal(expected, body.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData("b64_json", "base64")]
    [InlineData("url", "url")]
    [InlineData("", "url")]
    public void TransformRequest_MapsResponseFormat(string format, string expected)
    {
        var request = new LlmRequest
        {
            RequestType = RequestTypes.Image,
            Image = new ImageRequest { Prompt = "cat", ResponseFormat = format },
        };

        var body = Body(Transformer().TransformRequest(request));

        Assert.Equal(expected, body.GetProperty("response_format").GetString());
    }

    // Reference TestImageGeneration_mapsSize_whenDimensionsAreAbsent.
    [Theory]
    [InlineData("1536x1024", null, null, 1536, 1024)]
    [InlineData("1536x1024", 768L, null, 768, 1024)]
    [InlineData("1536x1024", 768L, 1280L, 768, 1280)]
    public void TransformRequest_ResolvesDimensions(
        string size,
        long? width,
        long? height,
        long expectedWidth,
        long expectedHeight)
    {
        var request = new LlmRequest
        {
            RequestType = RequestTypes.Image,
            Image = new ImageRequest
            {
                Prompt = "cat",
                Size = size,
                Width = width,
                Height = height,
            },
        };

        var body = Body(Transformer().TransformRequest(request));

        Assert.Equal(expectedWidth, body.GetProperty("width").GetInt64());
        Assert.Equal(expectedHeight, body.GetProperty("height").GetInt64());
    }

    // Reference TestImageGeneration_rejectsUnsupportedSize_whenDimensionsAreAbsent.
    [Theory]
    [InlineData("auto")]
    [InlineData("bad")]
    [InlineData("511x1024")]
    [InlineData("1025x1024")]
    [InlineData("2048x2056")]
    [InlineData("1024")]
    public void TransformRequest_RejectsUnsupportedSizes(string size)
    {
        var request = new LlmRequest
        {
            RequestType = RequestTypes.Image,
            Image = new ImageRequest { Prompt = "cat", Size = size },
        };

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal(
            "invalid request: unsupported MiniMax image size " + GoQuote(size),
            error.Message);
        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
    }

    // Reference TestImageGeneration_usesVersionedCustomPath_whenBaseIsHost.
    [Theory]
    [InlineData("https://api.minimax.io", "/v1/image_generation", "https://api.minimax.io/v1/image_generation")]
    [InlineData("https://api.minimax.io", "/image_generation", "https://api.minimax.io/v1/image_generation")]
    [InlineData("https://api.minimax.io/v1", "/image_generation", "https://api.minimax.io/v1/image_generation")]
    [InlineData("https://api.minimax.io/v1", "/v1/image_generation", "https://api.minimax.io/v1/image_generation")]
    [InlineData("https://api.minimax.io", "", "https://api.minimax.io/v1/image_generation")]
    public void TransformRequest_UsesVersionedPaths(string baseUrl, string path, string expected)
    {
        var transformer = new MinimaxOutboundTransformer(new MinimaxOutboundConfig
        {
            BaseUrl = baseUrl,
            EndpointPath = path,
            ApiKeyProvider = new StaticApiKeyProvider("key"),
        });

        var request = transformer.TransformRequest(new LlmRequest
        {
            Model = "image-01",
            RequestType = RequestTypes.Image,
            Image = new ImageRequest { Prompt = "cat" },
        });

        Assert.Equal(expected, request.Url);
    }

    // The provider body is a Go map, so keys are sorted and the
    // subject_reference value is re-marshaled the same way.
    [Fact]
    public void TransformRequest_EmitsTheSortedBody()
    {
        var request = new LlmRequest
        {
            Model = "image-01",
            Seed = 42,
            RequestType = RequestTypes.Image,
            Image = new ImageRequest
            {
                Prompt = "cat",
                Size = "1024x1024",
                AspectRatio = "1:1",
                N = 2,
                PromptOptimizer = true,
                SubjectReference = JsonDocument.Parse(
                    """{"type":"character","image_url":"https://img/ref.png"}""")
                    .RootElement.Clone(),
            },
        };

        var json = Json(Transformer().TransformRequest(request));

        Assert.Equal(
            """{"aspect_ratio":"1:1","height":1024,"model":"image-01","n":2,"prompt":"cat","prompt_optimizer":true,"response_format":"url","seed":42,"subject_reference":{"image_url":"https://img/ref.png","type":"character"},"width":1024}""",
            json);
    }

    [Fact]
    public void TransformRequest_RequiresImageAndPrompt()
    {
        var noImage = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(new LlmRequest
            {
                RequestType = RequestTypes.Image,
            }));
        Assert.Equal("invalid request: image request is required", noImage.Message);

        var noPrompt = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(new LlmRequest
            {
                RequestType = RequestTypes.Image,
                Image = new ImageRequest { Prompt = "   " },
            }));
        Assert.Equal("invalid request: prompt is required for image generation", noPrompt.Message);
    }

    // Reference TestImageGeneration_rejectsBusinessFailure_whenHTTPIsOK.
    [Fact]
    public void TransformResponse_ReportsBusinessFailures()
    {
        var response = MakeImageResponse(
            """{"base_resp":{"status_code":1008,"status_msg":"insufficient balance"},"data":{"image_urls":["https://image.example/test.png"]}}""");

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformResponse(response));

        Assert.Equal(
            "invalid response: MiniMax image API status 1008: insufficient balance",
            error.Message);
        Assert.Equal(TransformerErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public void TransformResponse_MapsUrlsThenBase64()
    {
        var response = MakeImageResponse(
            """{"id":"minimax-trace","created":123,"base_resp":{"status_code":0},"data":{"image_urls":["https://image.example/1.png"],"image_base64":["YWJj"]}}""");
        response.Request!.TransformerMetadata = new Dictionary<string, JsonElement>
        {
            ["model"] = JsonSerializer.SerializeToElement("image-01-live"),
        };

        var result = Transformer().TransformResponse(response);

        Assert.Equal("minimax-trace", result.Id);
        Assert.Equal("image.generation", result.Object);
        Assert.Equal(123, result.Created);
        Assert.Equal("image-01-live", result.Model);
        Assert.Equal(RequestTypes.Image, result.RequestType);
        Assert.Equal(ApiFormats.OpenAIImageGeneration, result.ApiFormat);

        Assert.NotNull(result.Image);
        Assert.Equal(123, result.Image!.Created);
        Assert.Equal(2, result.Image.Data.Count);
        Assert.Equal("https://image.example/1.png", result.Image.Data[0].URL);
        Assert.Equal("YWJj", result.Image.Data[1].B64JSON);
    }

    [Fact]
    public void TransformResponse_FallsBackToNowAndTheDefaultModel()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = Transformer().TransformResponse(
            MakeImageResponse("""{"base_resp":{"status_code":0},"data":{"image_base64":["YWJj"]}}"""));
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.InRange(result.Created, before, after);
        Assert.Equal("image-01", result.Model);
        Assert.Equal(
            "minimax-img-" + result.Created.ToString(CultureInfo.InvariantCulture),
            result.Id);
    }

    [Fact]
    public void TransformResponse_Errors()
    {
        var httpError = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(new HttpResponseData
            {
                StatusCode = 400,
                Body = "{}"u8.ToArray(),
                Request = new HttpRequestData { RequestType = RequestTypes.Image },
            }));
        Assert.Equal("MiniMax image API returned HTTP 400", httpError.Message);

        var noImages = Assert.Throws<TransformerException>(
            () => Transformer().TransformResponse(
                MakeImageResponse("""{"base_resp":{"status_code":0},"data":{}}""")));
        Assert.Equal(
            "invalid response: MiniMax image response contained no images",
            noImages.Message);

        var malformed = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(
                MakeImageResponse("not json")));
        Assert.StartsWith(
            "failed to decode MiniMax image response: ",
            malformed.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransformRequest_ChatDelegatesToTheOpenAiOutbound()
    {
        var request = new LlmRequest
        {
            Model = "abab6.5",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal("https://api.minimax.io/v1/chat/completions", httpRequest.Url);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, httpRequest.ApiFormat);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("key", httpRequest.Auth.ApiKey);
    }

    [Fact]
    public void TransformResponse_ChatDelegatesToTheOpenAiOutbound()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = """
                {"id":"chat-1","object":"chat.completion","created":1,"model":"abab6.5",
                 "choices":[{"index":0,"message":{"role":"assistant","content":"Hi"},"finish_reason":"stop"}]}
                """u8.ToArray(),
            Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIChatCompletion },
        };

        var result = Transformer().TransformResponse(response);

        Assert.Equal("chat-1", result.Id);
        Assert.Equal("Hi", Assert.Single(result.Choices).Message!.Content!.Content);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static MinimaxOutboundTransformer Transformer()
        => new(new MinimaxOutboundConfig
        {
            BaseUrl = "https://api.minimax.io",
            ApiKeyProvider = new StaticApiKeyProvider("key"),
        });

    private static HttpResponseData MakeImageResponse(string body)
        => new()
        {
            StatusCode = 200,
            Body = System.Text.Encoding.UTF8.GetBytes(body),
            Request = new HttpRequestData { RequestType = RequestTypes.Image },
        };

    private static string Json(HttpRequestData request)
        => System.Text.Encoding.UTF8.GetString(request.Body!);

    private static JsonElement Body(HttpRequestData request)
    {
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }

    private static string GoQuote(string value) => AxonHub.Llm.Json.GoCast.GoQuote(value);
}
