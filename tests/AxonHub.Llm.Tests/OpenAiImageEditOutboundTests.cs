using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Wire-shape lock for the images/edits multipart body
/// (llm/transformer/openai/image_edit_outbound_test.go): the model field must
/// precede the image parts, and the JPEG metadata must agree between JSONBody
/// and the multipart headers.
/// </summary>
[Collection("openai-image-transformers")]
public sealed class OpenAiImageEditOutboundTests
{
    [Fact]
    public void ImageEditRequestWritesModelBeforeImageFiles()
    {
        // Given
        var outbound = OpenAiOutboundTransformer.Create(
            "https://api.openai.com/v1", "test-key");

        var request = new LlmRequest
        {
            Model = "gpt-image-2",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageEdit,
            Image = new ImageRequest
            {
                Prompt = "Make this image brighter",
                Images = [Encoding.UTF8.GetBytes("image-data")],
            },
        };

        // When
        var httpRequest = outbound.TransformRequest(request);
        var parts = ReadMultipart(httpRequest);

        // Then
        var part = parts[0];
        Assert.Equal("model", part.Name);
        Assert.Equal("gpt-image-2", Encoding.UTF8.GetString(part.Data));
    }

    [Fact]
    public void ImageEditRequestKeepsJPEGMetadataConsistent()
    {
        var outbound = OpenAiOutboundTransformer.Create(
            "https://api.openai.com/v1", "test-key");

        var request = new LlmRequest
        {
            Model = "gpt-image-2",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageEdit,
            Image = new ImageRequest
            {
                Prompt = "Make this image brighter",
                Images = [[0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F']],
            },
        };

        var httpRequest = outbound.TransformRequest(request);

        using (var logged = JsonDocument.Parse(httpRequest.JsonBody!))
        {
            var formFiles = logged.RootElement.GetProperty("formFiles");
            Assert.Equal(1, formFiles.GetArrayLength());

            var file = formFiles[0];
            Assert.Equal("image_1.jpg", file.GetProperty("filename").GetString());
            Assert.Equal("image/jpeg", file.GetProperty("content_type").GetString());
            Assert.Equal("jpg", file.GetProperty("format").GetString());
        }

        var parts = ReadMultipart(httpRequest);
        var imagePart = parts.First(part => part.FileName.Length > 0);

        Assert.Equal("image_1.jpg", imagePart.FileName);
        Assert.Equal("image/jpeg", imagePart.ContentType);
    }

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
