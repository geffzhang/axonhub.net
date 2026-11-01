using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Zai;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the ZAI image-generation route (llm/transformer/zai/
/// image.go and image_test.go): the sorted request body, the quality mapping and
/// the data-URL response conversion.
/// </summary>
public sealed class ZaiImageTests
{
    // Reference TestBuildImageGenerationAPIRequest.
    [Fact]
    public void TransformRequest_BuildsTheSortedImageBody()
    {
        var request = MakeImageRequest("cogview-4-250304", new ImageRequest { Prompt = "a cute cat" });

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal("https://api.example.com/v4/images/generations", httpRequest.Url);
        Assert.Equal("POST", httpRequest.Method);
        Assert.Equal(RequestTypes.Image, httpRequest.RequestType);
        Assert.Equal(ApiFormats.OpenAIImageGeneration, httpRequest.ApiFormat);
        Assert.Equal("application/json", httpRequest.Headers!["Content-Type"].Single());
        Assert.Equal("application/json", httpRequest.Headers["Accept"].Single());
        Assert.Equal("test-key", httpRequest.Auth!.ApiKey);

        // Go marshals map[string]any, so keys are sorted.
        Assert.Equal(
            """{"model":"cogview-4-250304","prompt":"a cute cat","quality":"standard","size":"1024x1024","watermark_enabled":false}""",
            Json(httpRequest));

        // The builder stores the model in TransformerMetadata.
        Assert.Equal(
            "cogview-4-250304",
            httpRequest.TransformerMetadata!["model"].GetString());
    }

    [Theory]
    [InlineData("high", "hd")]
    [InlineData("hd", "hd")]
    [InlineData("low", "standard")]
    [InlineData("standard", "standard")]
    [InlineData("", "standard")]
    public void TransformRequest_MapsQuality(string quality, string expected)
    {
        var request = MakeImageRequest("cogview-4", new ImageRequest
        {
            Prompt = "landscape",
            Quality = quality,
        });

        var body = Body(Transformer().TransformRequest(request));

        Assert.Equal(expected, body.GetProperty("quality").GetString());
    }

    [Fact]
    public void TransformRequest_KeepsSizeAndUser()
    {
        var request = MakeImageRequest("cogview-3-flash", new ImageRequest
        {
            Prompt = "a futuristic city",
            Size = "1280x720",
            User = "123",
        });

        var body = Body(Transformer().TransformRequest(request));

        Assert.Equal("1280x720", body.GetProperty("size").GetString());
        Assert.Equal("123", body.GetProperty("user_id").GetString());
        Assert.False(body.GetProperty("watermark_enabled").GetBoolean());
    }

    [Fact]
    public void TransformRequest_RejectsInvalidImageRequests()
    {
        var transformer = Transformer();

        var noImage = Assert.Throws<TransformerException>(
            () => transformer.TransformRequest(MakeImageRequest("cogview-4", null)));
        Assert.Equal("invalid request: image request is required", noImage.Message);

        var wrongFormat = Assert.Throws<TransformerException>(
            () => transformer.TransformRequest(MakeImageRequest(
                "cogview-4",
                new ImageRequest { Prompt = "cat" },
                ApiFormats.OpenAIImageEdit)));
        Assert.Equal("invalid request: ZAI only supports image generation", wrongFormat.Message);

        var withImages = Assert.Throws<TransformerException>(
            () => transformer.TransformRequest(MakeImageRequest(
                "cogview-4",
                new ImageRequest { Prompt = "cat", Images = [[1, 2, 3]] })));
        Assert.Equal(
            "invalid request: ZAI does not support image editing with input images",
            withImages.Message);

        var withMask = Assert.Throws<TransformerException>(
            () => transformer.TransformRequest(MakeImageRequest(
                "cogview-4",
                new ImageRequest { Prompt = "cat", Mask = [1, 2, 3] })));
        Assert.Equal(
            "invalid request: ZAI does not support image editing with input images",
            withMask.Message);

        var noPrompt = Assert.Throws<InvalidOperationException>(
            () => transformer.TransformRequest(MakeImageRequest("cogview-4", new ImageRequest())));
        Assert.Equal("prompt is required for image generation", noPrompt.Message);
    }

    // Reference TestTransformImageGenerationResponse.
    [Fact]
    public void TransformResponse_ConvertsDataUrlsToBase64()
    {
        const string dataUrl = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = ("""
                {"created":1234567890,"data":[{"url":"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="}]}
                """u8.ToArray()),
            Request = new HttpRequestData
            {
                ApiFormat = ApiFormats.OpenAIImageGeneration,
                Metadata = new Dictionary<string, string> { ["model"] = "cogview-4-250304" },
            },
        };

        var result = Transformer().TransformResponse(response);

        Assert.StartsWith("zai-img-", result.Id, StringComparison.Ordinal);
        Assert.Equal("chat.completion", result.Object);
        Assert.Equal(1234567890, result.Created);
        Assert.Equal("cogview-4-250304", result.Model);
        Assert.Equal(RequestTypes.Image, result.RequestType);
        Assert.Empty(result.Choices);

        Assert.NotNull(result.Image);
        Assert.Equal(1234567890, result.Image!.Created);
        var image = Assert.Single(result.Image.Data);
        Assert.Equal(dataUrl, image.URL);
        Assert.Equal(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==",
            image.B64JSON);
    }

    /// <summary>
    /// The reference builder writes <c>TransformerMetadata["model"]</c> but the
    /// response reader looks in <c>Request.Metadata</c>, so an in-process
    /// round-trip falls back to "image-generation" (image.go:137).
    /// </summary>
    [Fact]
    public void TransformResponse_WithoutRequestMetadataFallsBackToTheDefaultModel()
    {
        var httpRequest = Transformer().TransformRequest(
            MakeImageRequest("cogview-4", new ImageRequest { Prompt = "cat" }));

        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = """{"created":1,"data":[{"url":"data:image/png;base64,YWJj"}]}"""u8.ToArray(),
            Request = httpRequest,
        };

        var result = Transformer().TransformResponse(response);

        Assert.Equal("image-generation", result.Model);
        Assert.Equal("YWJj", Assert.Single(result.Image!.Data).B64JSON);
    }

    [Fact]
    public void TransformResponse_Errors()
    {
        var request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIImageGeneration };

        var malformed = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(new HttpResponseData
            {
                StatusCode = 200,
                Body = "not json"u8.ToArray(),
                Request = request,
            }));
        Assert.StartsWith(
            "failed to unmarshal images response: ",
            malformed.Message,
            StringComparison.Ordinal);

        var status = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(new HttpResponseData
            {
                StatusCode = 500,
                Body = "{}"u8.ToArray(),
                Request = request,
            }));
        Assert.Equal("HTTP error 500", status.Message);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ZaiOutboundTransformer Transformer()
        => new(new ZaiOutboundConfig
        {
            BaseUrl = "https://api.example.com/v4",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

    private static LlmRequest MakeImageRequest(string model, ImageRequest? image, string? apiFormat = null)
        => new()
        {
            Model = model,
            RequestType = RequestTypes.Image,
            ApiFormat = apiFormat ?? ApiFormats.OpenAIImageGeneration,
            Image = image,
        };

    private static string Json(HttpRequestData request)
        => System.Text.Encoding.UTF8.GetString(request.Body!);

    private static JsonElement Body(HttpRequestData request)
    {
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
