using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the OpenAI images outbound path
/// (llm/transformer/openai/image_outbound.go). Every test mirrors a function in
/// image_outbound_test.go; image_edit_outbound_test.go's two ordering tests
/// live in <see cref="OpenAiImageEditOutboundTests"/>.
/// </summary>
[Collection("openai-image-transformers")]
public sealed class OpenAiImageOutboundTests
{
    /// <summary>1x1 red pixel PNG, the fixture the reference uses.</summary>
    private const string RedPixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    private static readonly byte[] RedPixelPng = Convert.FromBase64String(RedPixelPngBase64);

    // ---------------------------------------------------------------- routing

    [Fact]
    public void TransformRequest_RoutesToImageGenerationAPI_WhenImageRequestPresent()
    {
        var ot = NewTransformer();

        var req = new LlmRequest
        {
            Model = "gpt-4o-mini",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Image = new ImageRequest
            {
                Prompt = "Generate a beautiful sunset over mountains",
                OutputFormat = "png",
                Size = "1024x1024",
            },
        };

        var hreq = ot.TransformRequest(req);

        Assert.Equal("https://api.openai.com/v1/images/generations", hreq.Url);
    }

    [Fact]
    public void TransformRequest_RoutesToImageGenerationAPI_WhenModelIsImageCapable()
    {
        var ot = NewTransformer();

        var req = new LlmRequest
        {
            Model = "gpt-image-1",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Image = new ImageRequest
            {
                Prompt = "Create an image of a futuristic city",
            },
        };

        var hreq = ot.TransformRequest(req);

        Assert.Equal("https://api.openai.com/v1/images/generations", hreq.Url);
    }

    [Fact]
    public void TransformRequest_RoutesToChatCompletions_WhenTextOnly()
    {
        var ot = NewTransformer();

        var req = new LlmRequest
        {
            Model = "gpt-4o-mini",
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("Hello") },
            ],
        };

        var hreq = ot.TransformRequest(req);

        Assert.Equal("https://api.openai.com/v1/chat/completions", hreq.Url);
    }

    // ---------------------------------------------------------------- generations

    [Fact]
    public void BuildImageGenerateRequest_BasicPrompt()
    {
        var ot = NewTransformer();
        var req = new LlmRequest
        {
            Model = "dall-e-3",
            Image = new ImageRequest
            {
                Prompt = "A cute baby sea otter",
            },
        };

        var httpReq = ot.BuildImageGenerateRequest(req, "test-key");

        Assert.Equal("https://api.openai.com/v1/images/generations", httpReq.Url);
        Assert.Equal("POST", httpReq.Method);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type"));

        using var doc = JsonDocument.Parse(httpReq.Body!);
        var body = doc.RootElement;
        Assert.Equal("A cute baby sea otter", body.GetProperty("prompt").GetString());
        Assert.Equal("dall-e-3", body.GetProperty("model").GetString());
        Assert.Equal("b64_json", body.GetProperty("response_format").GetString());
    }

    [Fact]
    public void BuildImageGenerateRequest_WithParameters()
    {
        var ot = NewTransformer();
        var req = new LlmRequest
        {
            Model = "gpt-image-1",
            Modalities = ["image"],
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Image = new ImageRequest
            {
                Prompt = "A futuristic city",
                OutputFormat = "png",
                Size = "1024x1024",
                Quality = "high",
                Background = "transparent",
            },
        };

        var httpReq = ot.BuildImageGenerateRequest(req, "test-key");

        using var doc = JsonDocument.Parse(httpReq.Body!);
        var body = doc.RootElement;
        Assert.Equal("A futuristic city", body.GetProperty("prompt").GetString());
        Assert.Equal("gpt-image-1", body.GetProperty("model").GetString());
        Assert.Equal("png", body.GetProperty("output_format").GetString());
        Assert.Equal("1024x1024", body.GetProperty("size").GetString());
        Assert.Equal("high", body.GetProperty("quality").GetString());
        Assert.Equal("transparent", body.GetProperty("background").GetString());
    }

    [Fact]
    public void BuildImageGenerateRequest_WithSingleImage()
    {
        var ot = NewTransformer();

        var req = new LlmRequest
        {
            Model = "gpt-image-1",
            Image = new ImageRequest
            {
                Prompt = "Make this image brighter",
                Images = [RedPixelPng],
            },
        };

        var httpReq = ot.BuildImageGenerateRequest(req, "test-key");

        using var doc = JsonDocument.Parse(httpReq.Body!);
        var body = doc.RootElement;

        Assert.Equal("Make this image brighter", body.GetProperty("prompt").GetString());
        Assert.Equal("gpt-image-1", body.GetProperty("model").GetString());

        // image should be a single data URL string
        var imageField = body.GetProperty("image");
        Assert.Equal(JsonValueKind.String, imageField.ValueKind);
        Assert.Contains("data:image/png;base64,", imageField.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildImageGenerateRequest_WithMultipleImages()
    {
        var ot = NewTransformer();

        var imageData2 = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        var req = new LlmRequest
        {
            Model = "gpt-image-1",
            Image = new ImageRequest
            {
                Prompt = "Combine these images",
                Images = [RedPixelPng, imageData2],
            },
        };

        var httpReq = ot.BuildImageGenerateRequest(req, "test-key");

        using var doc = JsonDocument.Parse(httpReq.Body!);
        var imageField = doc.RootElement.GetProperty("image");
        Assert.Equal(JsonValueKind.Array, imageField.ValueKind);
        Assert.Equal(2, imageField.GetArrayLength());

        foreach (var img in imageField.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, img.ValueKind);
            Assert.Contains("data:image/", img.GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildImageGenerateRequest_WithoutImage()
    {
        var ot = NewTransformer();

        var req = new LlmRequest
        {
            Model = "dall-e-3",
            Image = new ImageRequest
            {
                Prompt = "A beautiful sunset",
            },
        };

        var httpReq = ot.BuildImageGenerateRequest(req, "test-key");

        using var doc = JsonDocument.Parse(httpReq.Body!);

        // image field should not be present when no input images
        Assert.False(doc.RootElement.TryGetProperty("image", out _));
    }

    [Fact]
    public void BuildImageGenerateRequest_GPTImageModelOmitsResponseFormat()
    {
        var ot = NewTransformer();
        var req = new LlmRequest
        {
            Model = "gpt-image-2",
            Image = new ImageRequest
            {
                Prompt = "A futuristic city",
                ResponseFormat = "b64_json",
            },
        };

        var httpReq = ot.BuildImageGenerateRequest(req, "test-key");

        using var doc = JsonDocument.Parse(httpReq.Body!);
        Assert.False(doc.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public void BuildImageGenerateRequest_NoPrompt()
    {
        var ot = NewTransformer();
        var req = new LlmRequest
        {
            Model = "dall-e-3",
            Image = new ImageRequest
            {
                Prompt = string.Empty,
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ot.BuildImageGenerateRequest(req, "test-key"));
        Assert.Contains("prompt is required", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- edits

    [Fact]
    public void BuildImageEditRequest_WithImage()
    {
        var ot = NewTransformer();

        var req = new LlmRequest
        {
            Model = "gpt-image-1",
            ApiFormat = ApiFormats.OpenAIImageEdit,
            Image = new ImageRequest
            {
                Prompt = "Make this image brighter",
                Images = [RedPixelPng],
            },
        };

        var httpReq = ot.BuildImageEditRequest(req, "test-key");

        Assert.Equal("https://api.openai.com/v1/images/edits", httpReq.Url);
        Assert.Equal("POST", httpReq.Method);
        Assert.Contains(
            "multipart/form-data",
            HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildImageEditRequest_GPTImage2OmitsResponseFormat()
    {
        var ot = NewTransformer();

        var req = new LlmRequest
        {
            Model = "gpt-image-2",
            ApiFormat = ApiFormats.OpenAIImageEdit,
            Image = new ImageRequest
            {
                Prompt = "Make this image brighter",
                Images = [RedPixelPng],
                ResponseFormat = "b64_json",
            },
        };

        var httpReq = ot.BuildImageEditRequest(req, "test-key");

        using var doc = JsonDocument.Parse(httpReq.JsonBody!);
        Assert.False(doc.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public void BuildImageEditRequest_MultipleImagesUsesArrayField()
    {
        var ot = NewTransformer();
        var req = new LlmRequest
        {
            Model = "gpt-image-2",
            ApiFormat = ApiFormats.OpenAIImageEdit,
            Image = new ImageRequest
            {
                Prompt = "Add the logo from the second image to the first image",
                Images = [Encoding.UTF8.GetBytes("image1"), Encoding.UTF8.GetBytes("image2")],
            },
        };

        var httpReq = ot.BuildImageEditRequest(req, "test-key");

        var parts = ReadMultipart(httpReq);

        Assert.DoesNotContain(parts, p => p.FileName.Length > 0 && p.Name == "image");
        Assert.Equal(2, parts.Count(p => p.FileName.Length > 0 && p.Name == "image[]"));
    }

    [Fact]
    public void BuildImageEditRequest_NoImage()
    {
        var ot = NewTransformer();
        var req = new LlmRequest
        {
            Model = "gpt-image-1",
            ApiFormat = ApiFormats.OpenAIImageEdit,
            Image = new ImageRequest
            {
                Prompt = "Make this image brighter",
                Images = [],
            },
        };

        var ex = Assert.Throws<TransformerException>(
            () => ot.BuildImageEditRequest(req, "test-key"));
        Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
        Assert.Contains("at least one image is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildImageEditRequest_NoPrompt()
    {
        var ot = NewTransformer();

        var req = new LlmRequest
        {
            Model = "gpt-image-1",
            ApiFormat = ApiFormats.OpenAIImageEdit,
            Image = new ImageRequest
            {
                Prompt = string.Empty,
                Images = [RedPixelPng],
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ot.BuildImageEditRequest(req, "test-key"));
        Assert.Contains("prompt is required", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- API request routing

    [Fact]
    public void BuildImageGenerationAPIRequest_RoutesToGenerate()
    {
        var ot = NewTransformer();
        var req = new LlmRequest
        {
            Model = "dall-e-3",
            Image = new ImageRequest
            {
                Prompt = "A sunset",
            },
        };

        var httpReq = ot.BuildImageGenerationApiRequest(req);

        Assert.Equal("https://api.openai.com/v1/images/generations", httpReq.Url);
        Assert.Equal(ApiFormats.OpenAIImageGeneration, httpReq.ApiFormat);
        Assert.Equal(
            "dall-e-3",
            httpReq.TransformerMetadata!["model"].GetString());
    }

    [Theory]
    [InlineData(ApiFormats.OpenAIImageGeneration, "/custom/images/generations", "Generate a skyline", false)]
    [InlineData(ApiFormats.OpenAIImageEdit, "/custom/images/edits", "Edit the image", true)]
    [InlineData(ApiFormats.OpenAIImageVariation, "/custom/images/variations", "", true)]
    public void ImageRequests_UseCustomEndpointPath(
        string apiFormat,
        string endpoint,
        string prompt,
        bool withImages)
    {
        var transformer = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
        {
            PlatformType = OpenAiPlatformType.OpenAi,
            BaseUrl = "https://custom.api.com",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
            EndpointPath = endpoint,
        });

        var image = new ImageRequest { Prompt = prompt };
        if (withImages)
        {
            image.Images = [RedPixelPng];
        }

        var httpReq = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-image-1",
            RequestType = RequestTypes.Image,
            ApiFormat = apiFormat,
            Image = image,
        });

        Assert.Equal("https://custom.api.com" + endpoint, httpReq.Url);
    }

    [Fact]
    public void BuildImageGenerationAPIRequest_RoutesToGeneration()
    {
        var ot = NewTransformer();

        var req = new LlmRequest
        {
            Model = "gpt-image-1",
            Image = new ImageRequest
            {
                Prompt = "Generate an image of a cat",
            },
        };

        var httpReq = ot.BuildImageGenerationApiRequest(req);

        Assert.Equal("https://api.openai.com/v1/images/generations", httpReq.Url);
        Assert.Equal(ApiFormats.OpenAIImageGeneration, httpReq.ApiFormat);
        Assert.Equal(
            "gpt-image-1",
            httpReq.TransformerMetadata!["model"].GetString());
    }

    // ---------------------------------------------------------------- response transformation

    [Fact]
    public void TransformImageGenerationResponse_BasicResponse()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            {
            	"created": 1730000000,
            	"data": [
            		{
            			"b64_json": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
            		}
            	]
            }
            """);

        var resp = OpenAiOutboundTransformer.TransformImageGenerationResponse(
            new HttpResponseData { StatusCode = 200, Body = body });

        Assert.Equal("img-1730000000", resp.Id);
        Assert.Equal("chat.completion", resp.Object);
        Assert.Equal(1730000000L, resp.Created);
        // Image response should not have Choices
        Assert.Empty(resp.Choices);
        // Image response should have Image field
        Assert.NotNull(resp.Image);
        var image = Assert.Single(resp.Image!.Data);
        Assert.False(string.IsNullOrEmpty(image.B64JSON));
    }

    [Fact]
    public void TransformImageGenerationResponse_NullDataElementDecodesAsZeroImage()
    {
        var body = Encoding.UTF8.GetBytes("{\"created\":1730000000,\"data\":[null]}");

        var resp = OpenAiOutboundTransformer.TransformImageGenerationResponse(
            new HttpResponseData { StatusCode = 200, Body = body });

        var image = Assert.Single(resp.Image!.Data);
        Assert.Null(image.B64JSON);
        Assert.Null(image.URL);
        Assert.Null(image.RevisedPrompt);
    }

    [Fact]
    public void TransformImageGenerationResponse_WithUsage()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            {
            	"created": 1730000000,
            	"data": [
            		{
            			"b64_json": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
            		}
            	],
            	"usage": {
            		"input_tokens": 10,
            		"output_tokens": 256,
            		"total_tokens": 266,
            		"input_tokens_details": {
            			"image_tokens": 0,
            			"text_tokens": 10
            		}
            	}
            }
            """);

        var resp = OpenAiOutboundTransformer.TransformImageGenerationResponse(
            new HttpResponseData { StatusCode = 200, Body = body });

        Assert.NotNull(resp.Usage);
        Assert.Equal(10L, resp.Usage!.PromptTokens);
        Assert.Equal(256L, resp.Usage.CompletionTokens);
        Assert.Equal(266L, resp.Usage.TotalTokens);
        Assert.NotNull(resp.Usage.PromptTokensDetails);
        Assert.Equal(0L, resp.Usage.PromptTokensDetails!.ImageTokens);
        Assert.Equal(10L, resp.Usage.PromptTokensDetails.TextTokens);
    }

    [Fact]
    public void TransformImageGenerationResponse_WithCachedTokens()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            {
            	"created": 1730000000,
            	"data": [
            		{
            			"b64_json": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
            		}
            	],
            	"usage": {
            		"input_tokens": 100,
            		"output_tokens": 256,
            		"total_tokens": 356,
            		"input_tokens_details": {
            			"image_tokens": 80,
            			"text_tokens": 10,
            			"cached_tokens": 10
            		}
            	}
            }
            """);

        var resp = OpenAiOutboundTransformer.TransformImageGenerationResponse(
            new HttpResponseData { StatusCode = 200, Body = body });

        Assert.NotNull(resp.Usage);
        Assert.Equal(100L, resp.Usage!.PromptTokens);
        Assert.Equal(256L, resp.Usage.CompletionTokens);
        Assert.Equal(356L, resp.Usage.TotalTokens);
        Assert.NotNull(resp.Usage.PromptTokensDetails);
        Assert.Equal(80L, resp.Usage.PromptTokensDetails!.ImageTokens);
        Assert.Equal(10L, resp.Usage.PromptTokensDetails.TextTokens);
        Assert.Equal(10L, resp.Usage.PromptTokensDetails.CachedTokens);
    }

    [Fact]
    public void TransformImageGenerationResponse_WithOutputTokensDetails()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            {
            	"created": 1730000000,
            	"data": [
            		{
            			"b64_json": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
            		}
            	],
            	"usage": {
            		"input_tokens": 100,
            		"output_tokens": 256,
            		"total_tokens": 356,
            		"input_tokens_details": {
            			"image_tokens": 80,
            			"text_tokens": 20
            		},
            		"output_tokens_details": {
            			"reasoning_tokens": 50
            		}
            	}
            }
            """);

        var resp = OpenAiOutboundTransformer.TransformImageGenerationResponse(
            new HttpResponseData { StatusCode = 200, Body = body });

        Assert.NotNull(resp.Usage);
        Assert.Equal(100L, resp.Usage!.PromptTokens);
        Assert.Equal(256L, resp.Usage.CompletionTokens);
        Assert.Equal(356L, resp.Usage.TotalTokens);
        Assert.NotNull(resp.Usage.PromptTokensDetails);
        Assert.Equal(80L, resp.Usage.PromptTokensDetails!.ImageTokens);
        Assert.Equal(20L, resp.Usage.PromptTokensDetails.TextTokens);
        Assert.NotNull(resp.Usage.CompletionTokensDetails);
        Assert.Equal(50L, resp.Usage.CompletionTokensDetails!.ReasoningTokens);
    }

    [Fact]
    public void TransformImageGenerationResponse_MultipleImages()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            {
            	"created": 1730000000,
            	"data": [
            		{
            			"b64_json": "image1data"
            		},
            		{
            			"b64_json": "image2data"
            		}
            	],
            	"output_format": "webp"
            }
            """);

        var resp = OpenAiOutboundTransformer.TransformImageGenerationResponse(
            new HttpResponseData { StatusCode = 200, Body = body });

        // Image response should not have Choices
        Assert.Empty(resp.Choices);
        // Verify Image field
        Assert.NotNull(resp.Image);
        Assert.Equal(2, resp.Image!.Data.Count);
        Assert.Equal("image1data", resp.Image.Data[0].B64JSON);
        Assert.Equal("image2data", resp.Image.Data[1].B64JSON);
    }

    [Fact]
    public void TransformImageGenerationResponse_WithModelInTransformerMetadata()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            {
            	"created": 1730000000,
            	"data": [
            		{
            			"b64_json": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
            		}
            	]
            }
            """);

        var resp = OpenAiOutboundTransformer.TransformImageGenerationResponse(
            new HttpResponseData
            {
                StatusCode = 200,
                Body = body,
                Request = new HttpRequestData
                {
                    TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["model"] = JsonSerializer.SerializeToElement("dall-e-3"),
                    },
                },
            });

        Assert.Equal("dall-e-3", resp.Model);
    }

    [Fact]
    public void TransformImageGenerationResponse_WithoutModelInTransformerMetadata()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            {
            	"created": 1730000000,
            	"data": [
            		{
            			"b64_json": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
            		}
            	]
            }
            """);

        var resp = OpenAiOutboundTransformer.TransformImageGenerationResponse(
            new HttpResponseData { StatusCode = 200, Body = body });

        Assert.Equal("image-generation", resp.Model);
    }

    // ---------------------------------------------------------------- extractFile

    [Fact]
    public void ExtractImageData_ValidDataURL()
    {
        var dataUrl =
            "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

        var formFile = OpenAiOutboundTransformer.ExtractFile(dataUrl);

        Assert.NotEmpty(formFile.Data);
        Assert.Equal("png", formFile.Format);
        Assert.Equal("image/png", formFile.ContentType);
    }

    [Fact]
    public void ExtractImageData_InvalidDataURL()
    {
        var dataUrl = "data:image/png;base64";

        var ex = Assert.Throws<TransformerException>(
            () => OpenAiOutboundTransformer.ExtractFile(dataUrl));
        // This will fail because there's no comma separator
        Assert.Contains("invalid data URL format", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractImageData_NonDataURL()
    {
        var url = "https://example.com/image.png";

        var ex = Assert.Throws<TransformerException>(
            () => OpenAiOutboundTransformer.ExtractFile(url));
        Assert.Contains("only data URLs are supported", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractImageData_JPEGFormat()
    {
        var dataUrl =
            "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/2wBDAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAv/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAAX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwA/8A8A";

        var formFile = OpenAiOutboundTransformer.ExtractFile(dataUrl);

        Assert.NotEmpty(formFile.Data);
        Assert.Equal("jpeg", formFile.Format);
        Assert.Equal("image/jpeg", formFile.ContentType);
    }

    // ---------------------------------------------------------------- integration

    [Fact]
    public void TransformRequest_ImageGeneration_Integration()
    {
        var ot = NewTransformer();
        var req = new LlmRequest
        {
            Model = "dall-e-3",
            RequestType = RequestTypes.Image,
            Image = new ImageRequest
            {
                Prompt = "A beautiful landscape",
            },
        };

        var httpReq = ot.TransformRequest(req);

        Assert.Equal("https://api.openai.com/v1/images/generations", httpReq.Url);
        Assert.Equal(ApiFormats.OpenAIImageGeneration, httpReq.ApiFormat);
    }

    [Fact]
    public void TransformResponse_ImageGeneration_Integration()
    {
        var ot = NewTransformer();
        var body = Encoding.UTF8.GetBytes(
            """
            {
            	"created": 1730000000,
            	"data": [
            		{
            			"b64_json": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
            		}
            	]
            }
            """);

        var resp = ot.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = body,
            Request = new HttpRequestData
            {
                ApiFormat = ApiFormats.OpenAIImageGeneration,
            },
        });

        // Image generation responses now use resp.Image instead of resp.Choices
        Assert.Empty(resp.Choices);
        Assert.NotNull(resp.Image);
        var image = Assert.Single(resp.Image!.Data);
        Assert.False(string.IsNullOrEmpty(image.B64JSON));
    }

    // ---------------------------------------------------------------- helpers

    private static OpenAiOutboundTransformer NewTransformer()
        => OpenAiOutboundTransformer.Create("https://api.openai.com/v1", "test-key");

    private static List<GoMultipartPart> ReadMultipart(HttpRequestData httpReq)
    {
        var contentType = HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type");
        Assert.True(
            GoMimeTypes.TryParseMediaType(contentType, out _, out var parameters),
            "Content-Type should parse as a media type");
        Assert.True(parameters.TryGetValue("boundary", out var boundary), "boundary is required");

        var reader = new GoMultipartReader(httpReq.Body!, boundary);
        var parts = new List<GoMultipartPart>();
        while (reader.NextPart() is { } part)
        {
            parts.Add(part);
        }

        return parts;
    }
}
