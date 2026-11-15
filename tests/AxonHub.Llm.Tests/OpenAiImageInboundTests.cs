using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for openai.ImageInboundTransformer and the image outbound
/// routes (llm/transformer/openai/image_inbound.go). Every test mirrors a
/// function in image_inbound_test.go; limits are package-level in Go and
/// mutable statics here, so the class joins a serial collection.
/// </summary>
[Collection("openai-image-transformers")]
public sealed class OpenAiImageInboundTests
{
    // ---------------------------------------------------------------- generation

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Generation_JSON()
    {
        var inbound = ImageInboundTransformer.Generation();

        var httpReq = JsonRequest(
            "http://localhost/v1/images/generations",
            Bytes(
                """
                {
                    "prompt":"a cat",
                    "model":"dall-e-3",
                    "n":2,
                    "response_format":"url",
                    "size":"1024x1024",
                    "user":"u1"
                }
                """));

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.Equal(RequestTypes.Image, llmReq.RequestType);
        Assert.Equal(ApiFormats.OpenAIImageGeneration, llmReq.ApiFormat);
        Assert.Equal("dall-e-3", llmReq.Model);
        Assert.Contains("image", llmReq.Modalities!);
        Assert.False(llmReq.Stream!.Value);
        Assert.NotNull(llmReq.Image);
        Assert.Equal("a cat", llmReq.Image!.Prompt);
        Assert.Equal(2L, llmReq.Image.N);
        Assert.Equal("url", llmReq.Image.ResponseFormat);
        Assert.Equal("1024x1024", llmReq.Image.Size);

        var outbound = OpenAiOutboundTransformer.Create(
            "https://api.openai.com/v1", "test-key");

        var outReq = outbound.TransformRequest(llmReq);

        Assert.Equal("https://api.openai.com/v1/images/generations", outReq.Url);
        var body = Encoding.UTF8.GetString(outReq.Body!);
        Assert.Contains("\"response_format\":\"url\"", body, StringComparison.Ordinal);
        Assert.Contains("\"n\":2", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Generation_WithSingleImage()
    {
        var inbound = ImageInboundTransformer.Generation();

        // 1x1 red pixel PNG
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        var httpReq = JsonRequest(
            "http://localhost/v1/images/generations",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "make this image brighter",
                ["model"] = "gpt-image-1",
                ["image"] = dataUrl,
            }));

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.NotNull(llmReq.Image);
        var image = Assert.Single(llmReq.Image!.Images!);
        Assert.Equal(pngBytes, image);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Generation_WithMultipleImages()
    {
        var inbound = ImageInboundTransformer.Generation();

        byte[] pngBytes1 = [0x89, 0x50, 0x4E, 0x47];
        byte[] pngBytes2 = [0x89, 0x50, 0x4E, 0x47, 0x0D];
        var dataUrl1 = "data:image/png;base64," + Convert.ToBase64String(pngBytes1);
        var dataUrl2 = "data:image/png;base64," + Convert.ToBase64String(pngBytes2);

        var httpReq = JsonRequest(
            "http://localhost/v1/images/generations",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "combine these images",
                ["model"] = "gpt-image-1",
                ["image"] = new List<string> { dataUrl1, dataUrl2 },
            }));

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.NotNull(llmReq.Image);
        Assert.Equal(2, llmReq.Image!.Images!.Count);
        Assert.Equal(pngBytes1, llmReq.Image.Images[0]);
        Assert.Equal(pngBytes2, llmReq.Image.Images[1]);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Generation_WithInvalidImageField()
    {
        var inbound = ImageInboundTransformer.Generation();

        var httpReq = JsonRequest(
            "http://localhost/v1/images/generations",
            Bytes(
                """
                {
                    "prompt":"a cat",
                    "model":"gpt-image-1",
                    "image":123
                }
                """));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains(
            "image field must be a string or array of strings",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Generation_WithNonDataURLImage()
    {
        var inbound = ImageInboundTransformer.Generation();

        var httpReq = JsonRequest(
            "http://localhost/v1/images/generations",
            Bytes(
                """
                {
                    "prompt":"a cat",
                    "model":"gpt-image-1",
                    "image":"https://example.com/image.png"
                }
                """));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("image must be a data URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Generation_WithNonBase64DataURL()
    {
        var inbound = ImageInboundTransformer.Generation();

        var httpReq = JsonRequest(
            "http://localhost/v1/images/generations",
            Bytes(
                """
                {
                    "prompt":"a cat",
                    "model":"gpt-image-1",
                    "image":"data:image/png,raw-not-base64"
                }
                """));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("must be base64-encoded", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_Generation_RoundTrip_WithImage()
    {
        var inbound = ImageInboundTransformer.Generation();

        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        var httpReq = JsonRequest(
            "http://localhost/v1/images/generations",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "make this brighter",
                ["model"] = "gpt-image-1",
                ["image"] = dataUrl,
            }));

        var llmReq = inbound.TransformRequest(httpReq);

        var outbound = OpenAiOutboundTransformer.Create(
            "https://api.openai.com/v1", "test-key");

        var outReq = outbound.TransformRequest(llmReq);

        Assert.Equal("https://api.openai.com/v1/images/generations", outReq.Url);

        using var doc = JsonDocument.Parse(outReq.Body!);
        var imageField = doc.RootElement.GetProperty("image");
        Assert.Equal(JsonValueKind.String, imageField.ValueKind);
        Assert.Contains(
            "data:image/png;base64,",
            imageField.GetString()!,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- edit (multipart)

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_Multipart_WithMask()
    {
        var inbound = ImageInboundTransformer.Edit();

        var writer = new GoMultipartWriter();
        writer.WriteField("prompt", "make it blue");
        writer.WriteField("model", "dall-e-2");
        writer.WriteField("response_format", "b64_json");
        writer.CreatePart("image", "image.png", "image/png", Bytes("img"));
        writer.CreatePart("mask", "mask.png", "image/png", Bytes("msk"));
        writer.Close();

        var httpReq = MultipartRequest("http://localhost/v1/images/edits", writer);

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.Equal(RequestTypes.Image, llmReq.RequestType);
        Assert.Equal(ApiFormats.OpenAIImageEdit, llmReq.ApiFormat);
        Assert.Contains("image", llmReq.Modalities!);
        Assert.NotNull(llmReq.Image);
        Assert.Equal("make it blue", llmReq.Image!.Prompt);
        Assert.Equal("b64_json", llmReq.Image.ResponseFormat);
        Assert.Single(llmReq.Image.Images!);
        Assert.NotNull(llmReq.Image.Mask);

        var outbound = OpenAiOutboundTransformer.Create(
            "https://api.openai.com/v1", "test-key");

        var outReq = outbound.TransformRequest(llmReq);

        Assert.Equal("https://api.openai.com/v1/images/edits", outReq.Url);
        var body = Encoding.UTF8.GetString(outReq.Body!);
        Assert.Contains("name=\"mask\"", body, StringComparison.Ordinal);
        Assert.Contains("name=\"image\"", body, StringComparison.Ordinal);
        Assert.Contains("name=\"prompt\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Variation_Multipart()
    {
        var inbound = ImageInboundTransformer.Variation();

        var writer = new GoMultipartWriter();
        writer.WriteField("model", "dall-e-2");
        writer.WriteField("n", "2");
        writer.CreatePart("image", "image.png", "image/png", Bytes("img"));
        writer.Close();

        var httpReq = MultipartRequest("http://localhost/v1/images/variations", writer);

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.Equal(RequestTypes.Image, llmReq.RequestType);
        Assert.Equal(ApiFormats.OpenAIImageVariation, llmReq.ApiFormat);
        Assert.NotNull(llmReq.Image);
        Assert.Equal(2L, llmReq.Image!.N);
        Assert.Single(llmReq.Image.Images!);

        var outbound = OpenAiOutboundTransformer.Create(
            "https://api.openai.com/v1", "test-key");

        var outReq = outbound.TransformRequest(llmReq);

        Assert.Equal("https://api.openai.com/v1/images/variations", outReq.Url);
        Assert.DoesNotContain(
            "name=\"prompt\"",
            Encoding.UTF8.GetString(outReq.Body!),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_Multipart_ImageArrayFieldName()
    {
        var inbound = ImageInboundTransformer.Edit();

        var writer = new GoMultipartWriter();
        writer.WriteField("prompt", "change dog color to black");
        writer.WriteField("model", "gpt-image-1.5");
        writer.CreatePart("image[]", "dog.png", "image/png", Bytes("dogimg"));
        writer.Close();

        var httpReq = MultipartRequest("http://localhost/v1/images/edits", writer);

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.Equal(RequestTypes.Image, llmReq.RequestType);
        Assert.Equal(ApiFormats.OpenAIImageEdit, llmReq.ApiFormat);
        Assert.Equal("gpt-image-1.5", llmReq.Model);
        Assert.NotNull(llmReq.Image);
        Assert.Equal("change dog color to black", llmReq.Image!.Prompt);
        var image = Assert.Single(llmReq.Image.Images!);
        Assert.Equal(Bytes("dogimg"), image);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_Multipart_MultipleImagesWithArraySyntax()
    {
        var inbound = ImageInboundTransformer.Edit();

        var writer = new GoMultipartWriter();
        writer.WriteField("prompt", "combine these images");
        writer.WriteField("model", "gpt-image-1.5");
        writer.CreatePart("image[]", "img1.png", "image/png", Bytes("img1"));
        writer.CreatePart("image[]", "img2.png", "image/png", Bytes("img2"));
        writer.Close();

        var httpReq = MultipartRequest("http://localhost/v1/images/edits", writer);

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.NotNull(llmReq.Image);
        Assert.Equal(2, llmReq.Image!.Images!.Count);
        Assert.Equal(Bytes("img1"), llmReq.Image.Images[0]);
        Assert.Equal(Bytes("img2"), llmReq.Image.Images[1]);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_Multipart_MixedImageFieldNames()
    {
        var inbound = ImageInboundTransformer.Edit();

        var writer = new GoMultipartWriter();
        writer.WriteField("prompt", "edit images");
        writer.WriteField("model", "dall-e-2");
        writer.CreatePart("image", "img1.png", "image/png", Bytes("img1"));
        writer.CreatePart("image[]", "img2.png", "image/png", Bytes("img2"));
        writer.Close();

        var httpReq = MultipartRequest("http://localhost/v1/images/edits", writer);

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.NotNull(llmReq.Image);
        Assert.Equal(2, llmReq.Image!.Images!.Count);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_Multipart_AcceptsImageAboveLegacy4MBLimit()
    {
        var inbound = ImageInboundTransformer.Edit();

        var imageData = new byte[5 * 1024 * 1024];
        Array.Fill(imageData, (byte)0x89);

        var writer = new GoMultipartWriter();
        writer.WriteField("prompt", "edit image");
        writer.CreatePart("image", "large.png", "image/png", imageData);
        writer.Close();

        var httpReq = MultipartRequest("http://localhost/v1/images/edits", writer);

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.NotNull(llmReq.Image);
        var image = Assert.Single(llmReq.Image!.Images!);
        Assert.Equal(imageData.Length, image.Length);
    }

    // ---------------------------------------------------------------- edit (JSON)

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON()
    {
        var inbound = ImageInboundTransformer.Edit();

        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "make it blue",
                ["model"] = "sensenova-u1.5-lite",
                ["image"] = dataUrl,
                ["size"] = "1024x1024",
                ["n"] = 2L,
                ["response_format"] = "b64_json",
            }));

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.Equal(RequestTypes.Image, llmReq.RequestType);
        Assert.Equal(ApiFormats.OpenAIImageEdit, llmReq.ApiFormat);
        Assert.Equal("sensenova-u1.5-lite", llmReq.Model);
        Assert.Contains("image", llmReq.Modalities!);
        Assert.False(llmReq.Stream!.Value);
        Assert.NotNull(llmReq.Image);
        Assert.Equal("make it blue", llmReq.Image!.Prompt);
        Assert.Equal("1024x1024", llmReq.Image.Size);
        Assert.Equal(2L, llmReq.Image.N);
        Assert.Equal("b64_json", llmReq.Image.ResponseFormat);
        var image = Assert.Single(llmReq.Image.Images!);
        Assert.Equal(pngBytes, image);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_MultipleImagesAndMask()
    {
        var inbound = ImageInboundTransformer.Edit();

        byte[] pngBytes1 = [0x89, 0x50, 0x4E, 0x47];
        byte[] pngBytes2 = [0x89, 0x50, 0x4E, 0x47, 0x0D];
        byte[] maskBytes = [0x89, 0x4D, 0x53, 0x4B];
        var dataUrl1 = "data:image/png;base64," + Convert.ToBase64String(pngBytes1);
        var dataUrl2 = "data:image/png;base64," + Convert.ToBase64String(pngBytes2);
        var maskUrl = "data:image/png;base64," + Convert.ToBase64String(maskBytes);

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "combine these images",
                ["model"] = "gpt-image-1",
                ["image"] = new List<string> { dataUrl1, dataUrl2 },
                ["mask"] = maskUrl,
            }));

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.NotNull(llmReq.Image);
        Assert.Equal(2, llmReq.Image!.Images!.Count);
        Assert.Equal(pngBytes1, llmReq.Image.Images[0]);
        Assert.Equal(pngBytes2, llmReq.Image.Images[1]);
        Assert.Equal(maskBytes, llmReq.Image.Mask);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_RequiresPrompt()
    {
        var inbound = ImageInboundTransformer.Edit();

        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47];
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["model"] = "sensenova-u1.5-lite",
                ["image"] = dataUrl,
            }));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("prompt is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_RequiresImage()
    {
        var inbound = ImageInboundTransformer.Edit();

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Bytes("""{"prompt":"make it blue","model":"sensenova-u1.5-lite"}"""));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains(
            "at least one image is required",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_RejectsStream()
    {
        var inbound = ImageInboundTransformer.Edit();

        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47];
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "make it blue",
                ["model"] = "sensenova-u1.5-lite",
                ["image"] = dataUrl,
                ["stream"] = true,
            }));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("does not support streaming", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_RejectsNonDataURLImage()
    {
        var inbound = ImageInboundTransformer.Edit();

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Bytes(
                """
                {
                    "prompt":"make it blue",
                    "model":"sensenova-u1.5-lite",
                    "image":"https://example.com/image.png"
                }
                """));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("image must be a data URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_BodyTooLarge()
    {
        var inbound = ImageInboundTransformer.Edit();
        var originalMaxBodySize = ImageInboundTransformer.MaxImageBodySize;
        ImageInboundTransformer.MaxImageBodySize = 32;
        try
        {
            var httpReq = JsonRequest(
                "http://localhost/v1/images/edits",
                new byte[ImageInboundTransformer.MaxImageBodySize + 1]);

            var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
            Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
            Assert.Contains("request body too large", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            ImageInboundTransformer.MaxImageBodySize = originalMaxBodySize;
        }
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_RejectsTooManyImagesBeforeDecoding()
    {
        var inbound = ImageInboundTransformer.Edit();

        var images = new List<string>(ImageInboundTransformer.MaxImageCount + 1);
        for (var i = 0; i <= ImageInboundTransformer.MaxImageCount; i++)
        {
            images.Add("not-a-data-url");
        }

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "edit",
                ["image"] = images,
            }));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("too many images", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeDataURLToBytes_RejectsUnsupportedImageType()
    {
        var ex = Assert.Throws<TransformerException>(
            () => ImageInboundTransformer.DecodeDataUrlToBytes("data:text/plain;base64,aGVsbG8="));
        Assert.Contains(
            "unsupported image content type",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeDataURLToBytes_RejectsOversizedImage()
    {
        var originalMaxFileSize = ImageInboundTransformer.MaxImageFileSize;
        ImageInboundTransformer.MaxImageFileSize = 4;
        try
        {
            byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
            var ex = Assert.Throws<TransformerException>(
                () => ImageInboundTransformer.DecodeDataUrlToBytes(
                    "data:image/png;base64," + Convert.ToBase64String(png)));
            Assert.Contains("image file too large", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            ImageInboundTransformer.MaxImageFileSize = originalMaxFileSize;
        }
    }

    [Fact]
    public void DecodeDataURLToBytes_RejectsOversizedImageBeforeDecode()
    {
        var originalMaxFileSize = ImageInboundTransformer.MaxImageFileSize;
        ImageInboundTransformer.MaxImageFileSize = 3;
        try
        {
            var ex = Assert.Throws<TransformerException>(
                () => ImageInboundTransformer.DecodeDataUrlToBytes(
                    "data:image/png;base64,AAAAAA=="));
            Assert.Contains("image file too large", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            ImageInboundTransformer.MaxImageFileSize = originalMaxFileSize;
        }
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_TooManyImages()
    {
        var inbound = ImageInboundTransformer.Edit();

        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47];
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        var images = new List<string>(ImageInboundTransformer.MaxImageCount + 1);
        for (var i = 0; i <= ImageInboundTransformer.MaxImageCount; i++)
        {
            images.Add(dataUrl);
        }

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "make it blue",
                ["model"] = "sensenova-u1.5-lite",
                ["image"] = images,
            }));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
        Assert.Contains("too many images", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_ImagesAsObjects()
    {
        var inbound = ImageInboundTransformer.Edit();

        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "make it blue",
                ["model"] = "sensenova-u1.5-lite",
                ["images"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["image_url"] = dataUrl },
                },
            }));

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.Equal("sensenova-u1.5-lite", llmReq.Model);
        Assert.NotNull(llmReq.Image);
        Assert.Equal("make it blue", llmReq.Image!.Prompt);
        var image = Assert.Single(llmReq.Image.Images!);
        Assert.Equal(pngBytes, image);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_ImagesAsDataURLArray()
    {
        var inbound = ImageInboundTransformer.Edit();

        byte[] pngBytes1 = [0x89, 0x50, 0x4E, 0x47];
        byte[] pngBytes2 = [0x89, 0x50, 0x4E, 0x47, 0x0D];
        var dataUrl1 = "data:image/png;base64," + Convert.ToBase64String(pngBytes1);
        var dataUrl2 = "data:image/png;base64," + Convert.ToBase64String(pngBytes2);

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "combine these images",
                ["model"] = "gpt-image-1",
                ["images"] = new List<string> { dataUrl1, dataUrl2 },
            }));

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.NotNull(llmReq.Image);
        Assert.Equal(2, llmReq.Image!.Images!.Count);
        Assert.Equal(pngBytes1, llmReq.Image.Images[0]);
        Assert.Equal(pngBytes2, llmReq.Image.Images[1]);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_PrefersImageOverImages()
    {
        var inbound = ImageInboundTransformer.Edit();

        byte[] legacy = [0x89, 0x4C, 0x45, 0x47, 0x41, 0x43, 0x59];
        byte[] other = [0x89, 0x49, 0x47, 0x4E, 0x4F, 0x52, 0x45, 0x44];
        var legacyUrl = "data:image/png;base64," + Convert.ToBase64String(legacy);
        var otherUrl = "data:image/png;base64," + Convert.ToBase64String(other);

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "make it blue",
                ["model"] = "gpt-image-1",
                ["image"] = legacyUrl,
                ["images"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["image_url"] = otherUrl },
                },
            }));

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.NotNull(llmReq.Image);
        var image = Assert.Single(llmReq.Image!.Images!);
        Assert.Equal(legacy, image);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_FallsBackToImagesWhenImageIsEmpty()
    {
        var inbound = ImageInboundTransformer.Edit();

        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47];
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        var cases = new (string Name, object? Legacy)[]
        {
            ("empty array", new List<string>()),
            ("nested empty array", new List<List<string>>()),
        };

        foreach (var (name, legacy) in cases)
        {
            var body = new Dictionary<string, object?>
            {
                ["prompt"] = "make it blue",
                ["model"] = "gpt-image-1",
                ["images"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["image_url"] = dataUrl },
                },
            };
            if (legacy is not null)
            {
                body["image"] = legacy;
            }

            var llmReq = inbound.TransformRequest(JsonRequest(
                "http://localhost/v1/images/edits", Json(body)));

            Assert.NotNull(llmReq.Image);
            var image = Assert.Single(llmReq.Image!.Images!);
            Assert.Equal(pngBytes, image);
        }
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_ReportsInvalidImageWithoutFallingBack()
    {
        var inbound = ImageInboundTransformer.Edit();

        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47];
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        var cases = new (string Name, object? Legacy)[]
        {
            ("number", 42L),
            ("empty string", ""),
            ("null", null),
        };

        foreach (var (_, legacy) in cases)
        {
            var httpReq = JsonRequest(
                "http://localhost/v1/images/edits",
                Json(new Dictionary<string, object?>
                {
                    ["prompt"] = "make it blue",
                    ["model"] = "gpt-image-1",
                    ["image"] = legacy,
                    ["images"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["image_url"] = dataUrl },
                    },
                }));

            var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
            Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
        }
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_AcceptsRemoteImagesURL()
    {
        var inbound = ImageInboundTransformer.Edit();

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "make it blue",
                ["model"] = "gpt-image-1",
                ["images"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["image_url"] = "https://example.com/cat.png",
                    },
                },
            }));

        var request = inbound.TransformRequest(httpReq);

        var reference = Assert.Single(request.Image!.ImageReferences!);
        Assert.Equal("https://example.com/cat.png", reference.ImageUrl);
        Assert.Null(reference.FileId);
        Assert.Empty(request.Image.Images ?? []);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_RejectsInvalidImagesElement()
    {
        var inbound = ImageInboundTransformer.Edit();

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "make it blue",
                ["model"] = "gpt-image-1",
                ["images"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["url"] = "data:image/png;base64,AA==",
                    },
                },
            }));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
        Assert.Contains(
            "images[0]: invalid request: invalid image reference: json: unknown field \"url\"",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_TooManyImagesObjects()
    {
        var inbound = ImageInboundTransformer.Edit();

        var entries = new List<object?>(ImageInboundTransformer.MaxImageCount + 1);
        for (var i = 0; i <= ImageInboundTransformer.MaxImageCount; i++)
        {
            entries.Add(new Dictionary<string, object?> { ["image_url"] = "not-a-data-url" });
        }

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "make it blue",
                ["model"] = "gpt-image-1",
                ["images"] = entries,
            }));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("too many images", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_TransformRequest_Edit_JSON_RequiresImageWithEmptyImagesArray()
    {
        var inbound = ImageInboundTransformer.Edit();

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "make it blue",
                ["model"] = "gpt-image-1",
                ["images"] = new List<object?>(),
            }));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains(
            "at least one image is required for edits",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImageInboundTransformer_Edit_JSON_RoundTrip_ToMultipartOutbound()
    {
        var inbound = ImageInboundTransformer.Edit();

        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        var httpReq = JsonRequest(
            "http://localhost/v1/images/edits",
            Json(new Dictionary<string, object?>
            {
                ["prompt"] = "make this brighter",
                ["model"] = "dall-e-2",
                ["image"] = dataUrl,
            }));

        var llmReq = inbound.TransformRequest(httpReq);

        var outbound = OpenAiOutboundTransformer.Create(
            "https://api.openai.com/v1", "test-key");

        var outReq = outbound.TransformRequest(llmReq);

        Assert.Equal("https://api.openai.com/v1/images/edits", outReq.Url);
        Assert.Contains(
            "multipart/form-data",
            HttpClientUtils.HeaderGet(outReq.Headers, "Content-Type"),
            StringComparison.Ordinal);
        var body = Encoding.UTF8.GetString(outReq.Body!);
        Assert.Contains("name=\"image\"", body, StringComparison.Ordinal);
        Assert.Contains("name=\"prompt\"", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- response

    [Fact]
    public void ImageInboundTransformer_TransformResponse_ToImagesResponse()
    {
        var inbound = ImageInboundTransformer.Generation();

        var resp = new LlmResponse
        {
            Created = 123,
            Image = new ImageResponse
            {
                Created = 123,
                Data =
                [
                    new ImageData
                    {
                        B64JSON = "AAA",
                        URL = "data:image/png;base64,AAA",
                    },
                    new ImageData
                    {
                        URL = "https://example.com/a.png",
                    },
                ],
            },
        };

        var httpResp = inbound.TransformResponse(resp);

        Assert.Equal(200, httpResp.StatusCode);

        var oaiResp = LlmJson.Deserialize<ImagesResponse>(httpResp.Body!)
            ?? throw new InvalidOperationException("decode failed");

        Assert.Equal(123L, oaiResp.Created);
        Assert.NotNull(oaiResp.Data);
        Assert.Equal(2, oaiResp.Data!.Count);
        Assert.Equal("AAA", oaiResp.Data[0].B64JSON);
        Assert.Equal("https://example.com/a.png", oaiResp.Data[1].URL);
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] Json(Dictionary<string, object?> body) => GoJson.Marshal(body);

    private static HttpRequestData JsonRequest(string url, byte[] body)
        => new()
        {
            Method = "POST",
            Url = url,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = body,
        };

    private static HttpRequestData MultipartRequest(string url, GoMultipartWriter writer)
        => new()
        {
            Method = "POST",
            Url = url,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = [writer.FormDataContentType()],
            },
            Body = writer.ToArray(),
        };
}
