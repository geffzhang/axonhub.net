using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the native Gemini outbound transformer
/// (llm/transformer/gemini/outbound.go, embedding.go, image.go and their
/// embedding_test.go / image_test.go). Every test mirrors a reference
/// behaviour one-for-one; the port-only extras (marked below) lock functions
/// the reference leaves untested.
/// </summary>
public sealed class GeminiOutboundTransformerTests
{
    // ------------------------------------------------------------ embedding TransformRequest

    [Fact]
    public void TransformEmbeddingRequest_SingleText()
    {
        var tr = NewTransformer();

        var httpReq = tr.TransformRequest(new LlmRequest
        {
            Model = "gemini-embedding-001",
            RequestType = RequestTypes.Embedding,
            Embedding = new EmbeddingRequest
            {
                Input = new EmbeddingInput { String = "Hello world" },
            },
        });

        Assert.Equal("POST", httpReq.Method);
        Assert.Contains("models/gemini-embedding-001:embedContent", httpReq.Url);
        Assert.Equal(RequestTypes.Embedding, httpReq.RequestType);
        Assert.Equal(ApiFormats.GeminiEmbedding, httpReq.ApiFormat);
        Assert.True(httpReq.SkipInboundQueryMerge);
        Assert.Equal(AuthTypes.ApiKey, httpReq.Auth!.Type);
        Assert.Equal("test-key", httpReq.Auth.ApiKey);
        Assert.Equal("x-goog-api-key", httpReq.Auth.HeaderKey);

        var geminiReq = LlmJson.Deserialize<EmbedContentRequest>(httpReq.Body!)!;
        Assert.Equal("models/gemini-embedding-001", geminiReq.Model);
        Assert.NotNull(geminiReq.Content);
        Assert.Single(geminiReq.Content!.Parts!);
        Assert.Equal("Hello world", geminiReq.Content.Parts![0].Text);
    }

    [Fact]
    public void TransformEmbeddingRequest_BatchTexts()
    {
        var tr = NewTransformer();

        var httpReq = tr.TransformRequest(new LlmRequest
        {
            Model = "gemini-embedding-001",
            RequestType = RequestTypes.Embedding,
            Embedding = new EmbeddingRequest
            {
                Input = new EmbeddingInput { StringArray = ["Hello", "World", "Test"] },
            },
        });

        Assert.Contains("models/gemini-embedding-001:batchEmbedContents", httpReq.Url);

        var batchReq = LlmJson.Deserialize<BatchEmbedContentsRequest>(httpReq.Body!)!;
        Assert.Equal(3, batchReq.Requests!.Count);
        Assert.Equal("Hello", batchReq.Requests[0].Content!.Parts![0].Text);
        Assert.Equal("World", batchReq.Requests[1].Content!.Parts![0].Text);
        Assert.Equal("Test", batchReq.Requests[2].Content!.Parts![0].Text);
    }

    [Fact]
    public void TransformEmbeddingRequest_WithDimensions()
    {
        var tr = NewTransformer();

        var httpReq = tr.TransformRequest(new LlmRequest
        {
            Model = "gemini-embedding-001",
            RequestType = RequestTypes.Embedding,
            Embedding = new EmbeddingRequest
            {
                Input = new EmbeddingInput { String = "Hello" },
                Dimensions = 256,
            },
        });

        var geminiReq = LlmJson.Deserialize<EmbedContentRequest>(httpReq.Body!)!;
        Assert.Equal(256, geminiReq.OutputDimensionality);
    }

    [Fact]
    public void TransformEmbeddingRequest_WithTaskType()
    {
        var tr = NewTransformer();

        var httpReq = tr.TransformRequest(new LlmRequest
        {
            Model = "gemini-embedding-001",
            RequestType = RequestTypes.Embedding,
            Embedding = new EmbeddingRequest
            {
                Input = new EmbeddingInput { String = "Hello" },
                Task = "retrieval.query",
            },
        });

        var geminiReq = LlmJson.Deserialize<EmbedContentRequest>(httpReq.Body!)!;
        Assert.Equal("RETRIEVAL_QUERY", geminiReq.TaskType);
    }

    [Fact]
    public void TransformEmbeddingRequest_NilEmbedding()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NewTransformer().TransformRequest(new LlmRequest
        {
            Model = "gemini-embedding-001",
            RequestType = RequestTypes.Embedding,
        }));

        Assert.Contains("embedding request is nil", ex.Message);
    }

    /// <summary>Port-only: the reference leaves the model guard untested.</summary>
    [Fact]
    public void TransformEmbeddingRequest_ModelRequired()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NewTransformer().TransformRequest(new LlmRequest
        {
            Model = "",
            RequestType = RequestTypes.Embedding,
            Embedding = new EmbeddingRequest
            {
                Input = new EmbeddingInput { String = "Hello" },
            },
        }));

        Assert.Equal("model is required", ex.Message);
    }

    /// <summary>Port-only: the reference leaves the empty-input guard untested.</summary>
    [Fact]
    public void TransformEmbeddingRequest_EmptyInput()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NewTransformer().TransformRequest(new LlmRequest
        {
            Model = "gemini-embedding-001",
            RequestType = RequestTypes.Embedding,
            Embedding = new EmbeddingRequest
            {
                Input = new EmbeddingInput(),
            },
        }));

        Assert.Equal("embedding input is empty", ex.Message);
    }

    // ------------------------------------------------------------ embedding TransformResponse

    [Fact]
    public void TransformEmbeddingResponse_Single()
    {
        var body = GoJson.Marshal(new EmbedContentResponse
        {
            Embedding = new ContentEmbedding
            {
                Values = [0.1f, 0.2f, 0.3f],
            },
        });

        var llmResp = NewTransformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = body,
            Request = EmbeddingRequest(),
        });

        Assert.Equal(RequestTypes.Embedding, llmResp.RequestType);
        Assert.Equal(ApiFormats.GeminiEmbedding, llmResp.ApiFormat);
        Assert.NotNull(llmResp.Embedding);
        var data = llmResp.Embedding!.Data;
        Assert.NotNull(data);
        Assert.Single(data!);
        Assert.Equal("embedding", data[0].Object);
        Assert.Equal(0, data[0].Index);
        var values = data[0].Embedding.Values;
        Assert.NotNull(values);
        Assert.InRange(values[0], 0.1 - 0.001, 0.1 + 0.001);
        Assert.InRange(values[1], 0.2 - 0.001, 0.2 + 0.001);
        Assert.InRange(values[2], 0.3 - 0.001, 0.3 + 0.001);
    }

    [Fact]
    public void TransformEmbeddingResponse_Batch()
    {
        var body = GoJson.Marshal(new BatchEmbedContentsResponse
        {
            Embeddings =
            [
                new ContentEmbedding { Values = [0.1f, 0.2f] },
                new ContentEmbedding { Values = [0.3f, 0.4f] },
            ],
        });

        var llmResp = NewTransformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = body,
            Request = EmbeddingRequest(),
        });

        Assert.NotNull(llmResp.Embedding);
        Assert.Equal(2, llmResp.Embedding!.Data!.Count);
        Assert.Equal(0, llmResp.Embedding.Data[0].Index);
        Assert.Equal(1, llmResp.Embedding.Data[1].Index);
        Assert.InRange(llmResp.Embedding.Data[0].Embedding.Values![0], 0.1 - 0.001, 0.1 + 0.001);
        Assert.InRange(llmResp.Embedding.Data[1].Embedding.Values![0], 0.3 - 0.001, 0.3 + 0.001);
    }

    [Fact]
    public void TransformEmbeddingResponse_Error()
    {
        var err = Assert.Throws<ResponseError>(() => NewTransformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 400,
            Body = """{"error":{"code":400,"message":"Invalid request","status":"INVALID_ARGUMENT"}}"""u8.ToArray(),
            Request = EmbeddingRequest(),
        }));

        Assert.Equal(400, err.StatusCode);
        Assert.Equal("Invalid request", err.Detail.Message);
        Assert.Equal("INVALID_ARGUMENT", err.Detail.Type);
        Assert.Equal("400", err.Detail.Code);
    }

    /// <summary>
    /// Port-only: a body without an embedding keeps Go's nil data slice (the
    /// reference leaves the nil branch untested).
    /// </summary>
    [Fact]
    public void TransformEmbeddingResponse_EmptyEmbedding()
    {
        var llmResp = NewTransformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = "{}"u8.ToArray(),
            Request = EmbeddingRequest(),
        });

        Assert.NotNull(llmResp.Embedding);
        Assert.Equal("list", llmResp.Embedding!.Object);
        Assert.Null(llmResp.Embedding.Data);
    }

    // ------------------------------------------------------------ embedding URL and helpers

    [Theory]
    [InlineData("", false, "https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent")]
    [InlineData("", true, "https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:batchEmbedContents")]
    [InlineData(GeminiConstants.PlatformVertex, false, "https://us-central1-aiplatform.googleapis.com/v1/publishers/google/models/gemini-embedding-001:embedContent")]
    public void BuildEmbeddingUrl(string platformType, bool isBatch, string expected)
    {
        var tr = NewTransformer(platformType);

        Assert.Equal(expected, tr.BuildEmbeddingUrl("gemini-embedding-001", isBatch));
    }

    [Theory]
    [InlineData("retrieval.query", "RETRIEVAL_QUERY")]
    [InlineData("retrieval.passage", "RETRIEVAL_DOCUMENT")]
    [InlineData("text-matching", "SEMANTIC_SIMILARITY")]
    [InlineData("classification", "CLASSIFICATION")]
    [InlineData("clustering", "CLUSTERING")]
    [InlineData("unknown", "")]
    [InlineData("", "")]
    public void MapEmbeddingTaskType(string input, string expected)
    {
        Assert.Equal(expected, GeminiOutboundTransformer.MapEmbeddingTaskType(input));
    }

    // ------------------------------------------------------------ image TransformRequest

    [Theory]
    // image generation with prompt
    [InlineData("gemini-2.5-flash-image", "Generate a beautiful sunset over mountains", true, false)]
    // image generation with size
    [InlineData("gemini-2.5-flash-image", "Generate a cat", true, false)]
    // missing model
    [InlineData("", "Generate something", true, true)]
    // missing image field
    [InlineData("gemini-2.5-flash-image", null, false, true)]
    // missing prompt
    [InlineData("gemini-2.5-flash-image", null, true, true)]
    public void TransformRequest_ImageGenerationRequest(
        string model, string? prompt, bool hasImage, bool wantErr)
    {
        var llmReq = new LlmRequest
        {
            Model = model,
            RequestType = RequestTypes.Image,
            Image = hasImage ? new ImageRequest { Prompt = prompt } : null,
        };

        var tr = NewTransformer();
        if (wantErr)
        {
            Assert.Throws<TransformerException>(() => tr.TransformRequest(llmReq));
            return;
        }

        var req = tr.TransformRequest(llmReq);

        Assert.Equal("POST", req.Method);
        Assert.Contains(":generateContent", req.Url);
        Assert.Equal(RequestTypes.Image, req.RequestType);

        var body = LlmJson.Deserialize<GenerateContentRequest>(req.Body!)!;
        Assert.NotNull(body.GenerationConfig);
        Assert.Equal(new List<string> { "TEXT", "IMAGE" }, body.GenerationConfig!.ResponseModalities);
        Assert.NotNull(req.TransformerMetadata);
        Assert.Equal(model, req.TransformerMetadata!["model"].GetString());
    }

    [Theory]
    [InlineData("image edit with single image", 1)]
    [InlineData("image edit with multiple images", 2)]
    public void TransformRequest_ImageEditRequest(string name, int expectedImgCount)
    {
        // PNG magic bytes / JPEG magic bytes (image_test.go:120-122).
        byte[] pngData = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        byte[] jpegData = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

        var images = name.Contains("multiple", StringComparison.Ordinal)
            ? new List<byte[]> { pngData, jpegData }
            : new List<byte[]> { pngData };

        var req = NewTransformer().TransformRequest(new LlmRequest
        {
            Model = "gemini-2.5-flash-image",
            RequestType = RequestTypes.Image,
            Image = new ImageRequest
            {
                Prompt = "Combine these two images",
                Images = images,
            },
        });

        var body = LlmJson.Deserialize<GenerateContentRequest>(req.Body!)!;
        Assert.NotNull(body.Contents);
        Assert.Single(body.Contents!);
        Assert.NotNull(body.Contents![0].Parts);

        var imageCount = 0;
        var textCount = 0;
        foreach (var part in body.Contents[0].Parts!)
        {
            if (part.InlineData is not null)
            {
                imageCount++;
                Assert.StartsWith("image/", part.InlineData.MIMEType);
            }

            if (part.Text is not null)
            {
                textCount++;
            }
        }

        Assert.Equal(expectedImgCount, imageCount);
        Assert.Equal(1, textCount);
    }

    // ------------------------------------------------------------ image TransformResponse

    [Theory]
    [InlineData("""
        {
            "candidates": [{
                "content": {
                    "parts": [
                        {"inlineData": {"mimeType": "image/png", "data": "iVBORw0KGgo"}}
                    ],
                    "role": "model"
                },
                "finishReason": "STOP"
            }],
            "usageMetadata": {
                "promptTokenCount": 10,
                "candidatesTokenCount": 100,
                "totalTokenCount": 110
            },
            "responseId": "resp-123"
        }
        """, "resp-123", "iVBORw0KGgo", 10, 100, 110)]
    [InlineData("""
        {
            "candidates": [{
                "content": {
                    "parts": [
                        {"inlineData": {"mimeType": "image/png", "data": "image1base64"}},
                        {"text": "Here are the images"},
                        {"inlineData": {"mimeType": "image/jpeg", "data": "image2base64"}}
                    ],
                    "role": "model"
                },
                "finishReason": "STOP"
            }],
            "usageMetadata": {
                "promptTokenCount": 20,
                "candidatesTokenCount": 200,
                "totalTokenCount": 220
            }
        }
        """, "", "image1base64,image2base64", 20, 200, 220)]
    public void TransformImageGenerationResponse(
        string response, string expectedId, string expectedB64, long promptTokens, long completionTokens, long totalTokens)
    {
        var got = NewTransformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = System.Text.Encoding.UTF8.GetBytes(response),
            Request = ImageRequest("test-model"),
        });

        Assert.Equal("chat.completion", got.Object);
        Assert.Equal("test-model", got.Model);
        Assert.Equal(RequestTypes.Image, got.RequestType);
        Assert.Equal(expectedId, got.Id);

        Assert.NotNull(got.Image);
        var expectedData = expectedB64.Split(',');
        Assert.Equal(expectedData.Length, got.Image!.Data.Count);
        for (var i = 0; i < expectedData.Length; i++)
        {
            Assert.Equal(expectedData[i], got.Image.Data[i].B64JSON);
        }

        Assert.NotNull(got.Usage);
        Assert.Equal(promptTokens, got.Usage!.PromptTokens);
        Assert.Equal(completionTokens, got.Usage.CompletionTokens);
        Assert.Equal(totalTokens, got.Usage.TotalTokens);
    }

    [Theory]
    [InlineData(400, """{"error": {"message": "Invalid request"}}""")]
    [InlineData(200, "")]
    [InlineData(200, "invalid json")]
    public void TransformImageGenerationResponse_Errors(int statusCode, string body)
    {
        Assert.Throws<InvalidOperationException>(() => NewTransformer().TransformResponse(new HttpResponseData
        {
            StatusCode = statusCode,
            Body = System.Text.Encoding.UTF8.GetBytes(body),
            Request = ImageRequest("test-model"),
        }));
    }

    // ------------------------------------------------------------ chat TransformRequest

    /// <summary>
    /// Port-only smoke: the chat path builds the generateContent wire request.
    /// The reference exercises this through outbound_test.go, ported with the
    /// stream slice.
    /// </summary>
    [Fact]
    public void TransformRequest_ChatPath()
    {
        var req = NewTransformer().TransformRequest(new LlmRequest
        {
            Model = "gemini-2.5-flash",
            RequestType = RequestTypes.Chat,
            Messages = [new Message { Role = "user", Content = new MessageContent { Content = "hello" } }],
        });

        Assert.Equal("POST", req.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", req.Url);
        Assert.Equal(ApiFormats.GeminiContents, req.ApiFormat);
        Assert.True(req.SkipInboundQueryMerge);
        Assert.Equal(AuthTypes.ApiKey, req.Auth!.Type);
        Assert.Equal("test-key", req.Auth.ApiKey);
        Assert.Equal("x-goog-api-key", req.Auth.HeaderKey);
        Assert.Equal("application/json", req.Headers!["Content-Type"][0]);
        Assert.Equal("application/json", req.Headers["Accept"][0]);
        Assert.NotNull(req.Body);
        Assert.Equal("", req.RequestType);
    }

    // ------------------------------------------------------------ image size mapping

    [Theory]
    [InlineData("1024x1024", "1:1")]
    [InlineData("512x512", "1:1")]
    [InlineData("1792x1024", "16:9")]
    [InlineData("1024x1792", "9:16")]
    [InlineData("1536x1024", "3:2")]
    [InlineData("1024x1536", "2:3")]
    [InlineData("1024x768", "4:3")]
    [InlineData("768x1024", "3:4")]
    [InlineData("16:9", "16:9")]
    [InlineData("unknown", "1:1")]
    public void MapSizeToAspectRatio(string size, string expected)
    {
        // The reference asserts through the full request path (image_test.go:392-440).
        var req = NewTransformer().TransformRequest(new LlmRequest
        {
            Model = "gemini-2.5-flash-image",
            RequestType = RequestTypes.Image,
            Image = new ImageRequest
            {
                Prompt = "test",
                Size = size,
            },
        });

        var body = LlmJson.Deserialize<GenerateContentRequest>(req.Body!)!;
        Assert.NotNull(body.GenerationConfig);
        Assert.NotNull(body.GenerationConfig!.ImageConfig);
        Assert.Equal(expected, body.GenerationConfig.ImageConfig!.AspectRatio);
    }

    /// <summary>Port-only: the reference leaves mapSizeToImageSize untested.</summary>
    [Theory]
    [InlineData("1024x1024", "1K")]
    [InlineData("512x512", "1K")]
    [InlineData("1792x1024", "1K")]
    [InlineData("1024x768", "1K")]
    [InlineData("2048x2048", "2K")]
    [InlineData("1152x2048", "2K")]
    [InlineData("4096x4096", "4K")]
    [InlineData("2304x4096", "4K")]
    [InlineData("800x600", "1K")]
    [InlineData("1600x1600", "2K")]
    [InlineData("3000x3000", "4K")]
    [InlineData("1024x768junk", "1K")]
    [InlineData(" 512x512", "1K")]
    [InlineData("99999999999x1", "1K")]
    [InlineData("abc", "1K")]
    [InlineData("16:9", "1K")]
    public void MapSizeToImageSize(string size, string expected)
    {
        Assert.Equal(expected, GeminiOutboundTransformer.MapSizeToImageSize(size));
    }

    /// <summary>Port-only: the reference leaves detectImageMIMEType untested.</summary>
    [Theory]
    [InlineData("three-bytes", "image/png")]
    [InlineData("png", "image/png")]
    [InlineData("png-exact-four", "image/png")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("gif87a", "image/gif")]
    [InlineData("gif89a", "image/gif")]
    [InlineData("webp", "image/webp")]
    [InlineData("riff-too-short", "image/png")]
    [InlineData("unknown", "image/png")]
    public void DetectImageMIMEType(string name, string expected)
    {
        byte[] data = name switch
        {
            "three-bytes" => [0x01, 0x02, 0x03],
            "png" => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            "png-exact-four" => [0x89, 0x50, 0x4E, 0x47],
            "jpeg" => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46],
            "gif87a" => [0x47, 0x49, 0x46, 0x38, 0x37, 0x61],
            "gif89a" => [0x47, 0x49, 0x46, 0x38, 0x39, 0x61],
            "webp" => [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50],
            "riff-too-short" => [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00],
            _ => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        };

        Assert.Equal(expected, GeminiOutboundTransformer.DetectImageMIMEType(data));
    }

    /// <summary>Port-only: the reference leaves buildImageRequestURL untested.</summary>
    [Theory]
    [InlineData("", "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-image:generateContent")]
    [InlineData(GeminiConstants.PlatformVertex, "https://us-central1-aiplatform.googleapis.com/v1/publishers/google/models/gemini-2.5-flash-image:generateContent")]
    public void BuildImageRequestUrl(string platformType, string expected)
    {
        Assert.Equal(expected, NewTransformer(platformType).BuildImageRequestUrl(new LlmRequest
        {
            Model = "gemini-2.5-flash-image",
        }));
    }

    // ------------------------------------------------------------ helpers

    private static GeminiOutboundTransformer NewTransformer(string platformType = "")
        => GeminiOutboundTransformer.CreateWithConfig(new GeminiOutboundConfig
        {
            BaseUrl = platformType == GeminiConstants.PlatformVertex
                ? "https://us-central1-aiplatform.googleapis.com"
                : "https://generativelanguage.googleapis.com",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
            PlatformType = platformType,
        });

    private static HttpRequestData EmbeddingRequest()
        => new()
        {
            RequestType = RequestTypes.Embedding,
            ApiFormat = ApiFormats.GeminiEmbedding,
        };

    private static HttpRequestData ImageRequest(string model)
        => new()
        {
            RequestType = RequestTypes.Image,
            TransformerMetadata = new Dictionary<string, JsonElement>
            {
                ["model"] = JsonSerializer.SerializeToElement(model, LlmJson.Options),
            },
        };
}
