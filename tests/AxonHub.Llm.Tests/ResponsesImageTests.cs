using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the Responses image request/response path
/// (llm/transformer/openai/responses/image_request.go, image_response.go).
/// Expectations mirror image_response_test.go, image_stream_test.go, and
/// outbound_test.go (TestOutboundTransformer_TransformImageEditResponse).
/// </summary>
public sealed class ResponsesImageTests
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

    [Fact]
    public void BuildImageResponseFromRequestPipeline()
    {
        var src = new LlmRequest
        {
            Model = "gpt-image-1",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Image = new ImageRequest
            {
                Prompt = "a cat",
                OutputFormat = "webp",
                Quality = "high",
                Size = "1024x1024",
                Background = "opaque",
            },
        };

        var toolRequest = ResponsesImageConvert.BuildImageToolRequest(src);

        var upstream = new ResponsesResponse
        {
            Id = "resp_img_pipeline",
            CreatedAt = 1760000000,
            Model = "gpt-image-1",
            Output =
            [
                new ResponsesItem { Type = "image_generation_call", Result = "iVBORw0KGgo=" },
            ],
        };

        var response = ResponsesImageConvert.BuildImageResponse(upstream, toolRequest.TransformerMetadata);

        // The request-side metadata keys feed the response-side echo.
        Assert.Equal(
            "generate",
            toolRequest.TransformerMetadata!["image_generation_action"].GetString());
        Assert.NotNull(response.Image);
        Assert.Equal("webp", response.Image!.OutputFormat);
        Assert.Equal("high", response.Image.Quality);
        Assert.Equal("1024x1024", response.Image.Size);
        Assert.Equal("opaque", response.Image.Background);
    }

    [Fact]
    public void BuildImageResponseSuccess()
    {
        var upstream = new ResponsesResponse
        {
            Id = "resp_img_001",
            CreatedAt = 1760000000,
            Model = "gpt-image-2",
            Output =
            [
                new ResponsesItem { Type = "image_generation_call", Result = "iVBORw0KGgo=" },
            ],
        };

        var response = ResponsesImageConvert.BuildImageResponse(upstream, Metadata(
            ("codex_image_output_format", "png"),
            ("codex_image_quality", "high"),
            ("codex_image_size", "1024x1024"),
            ("codex_image_background", "opaque")));

        Assert.Equal(RequestTypes.Image, response.RequestType);
        Assert.Equal("image.generation", response.Object);
        Assert.Equal("resp_img_001", response.Id);
        Assert.Equal("gpt-image-2", response.Model);
        Assert.Equal(1760000000L, response.Created);
        Assert.NotNull(response.Image);
        Assert.Equal("png", response.Image!.OutputFormat);
        Assert.Equal("high", response.Image.Quality);
        Assert.Equal("1024x1024", response.Image.Size);
        Assert.Equal("opaque", response.Image.Background);
        Assert.Equal(1760000000L, response.Image.Created);
        var data = Assert.Single(response.Image.Data);
        Assert.Equal("iVBORw0KGgo=", data.B64JSON);
    }

    [Fact]
    public void BuildImageResponseMultipleImages()
    {
        var upstream = new ResponsesResponse
        {
            Id = "resp_img_multi",
            CreatedAt = 1760000000,
            Model = "gpt-image-2",
            Output =
            [
                new ResponsesItem { Type = "image_generation_call", Result = "image1base64" },
                new ResponsesItem { Type = "image_generation_call", Result = "image2base64" },
            ],
        };

        var response = ResponsesImageConvert.BuildImageResponse(
            upstream, Metadata(("codex_image_output_format", "webp")));

        Assert.NotNull(response.Image);
        Assert.Equal(2, response.Image!.Data.Count);
        Assert.Equal("image1base64", response.Image.Data[0].B64JSON);
        Assert.Equal("image2base64", response.Image.Data[1].B64JSON);
        Assert.Equal("webp", response.Image.OutputFormat);
    }

    [Fact]
    public void BuildImageResponseSkipsNonImageItems()
    {
        var upstream = new ResponsesResponse
        {
            Id = "resp_mixed",
            CreatedAt = 1760000000,
            Model = "gpt-image-2",
            Output =
            [
                new ResponsesItem { Type = "message", Id = "msg_1" },
                new ResponsesItem { Type = "image_generation_call", Result = "base64only" },
                new ResponsesItem { Type = "function_call", Id = "fn_1" },
            ],
        };

        var response = ResponsesImageConvert.BuildImageResponse(
            upstream, Metadata(("codex_image_output_format", "png")));

        Assert.NotNull(response.Image);
        var data = Assert.Single(response.Image!.Data);
        Assert.Equal("base64only", data.B64JSON);
    }

    [Fact]
    public void BuildImageResponseRejectsNil()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ResponsesImageConvert.BuildImageResponse(null, null));

        Assert.Equal("response is nil", ex.Message);
    }

    [Fact]
    public void BuildImageResponseReportsUpstreamError()
    {
        var upstream = new ResponsesResponse
        {
            Id = "resp_err",
            Error = new ResponsesError { Message = "generation failed" },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ResponsesImageConvert.BuildImageResponse(upstream, null));

        Assert.Equal("codex image response error: generation failed", ex.Message);
    }

    [Fact]
    public void BuildImageResponseRequiresImageResult()
    {
        var upstream = new ResponsesResponse { Id = "resp_empty", Output = [] };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ResponsesImageConvert.BuildImageResponse(upstream, null));

        Assert.Equal(
            "codex image response did not contain image_generation_call result",
            ex.Message);
    }

    [Fact]
    public void BuildImageResponseUsesModelFromMetadata()
    {
        var upstream = new ResponsesResponse
        {
            Id = "resp_no_model",
            CreatedAt = 1760000000,
            Output =
            [
                new ResponsesItem { Type = "image_generation_call", Result = "base64data" },
            ],
        };

        var response = ResponsesImageConvert.BuildImageResponse(
            upstream, Metadata(("codex_image_model", "gpt-image-1")));

        Assert.Equal("gpt-image-1", response.Model);
    }

    [Fact]
    public void BuildImageResponseDefaultsOutputFormatToPng()
    {
        var upstream = new ResponsesResponse
        {
            Id = "resp_default",
            CreatedAt = 1760000000,
            Output =
            [
                new ResponsesItem { Type = "image_generation_call", Result = "data" },
            ],
        };

        var response = ResponsesImageConvert.BuildImageResponse(upstream, null);

        Assert.NotNull(response.Image);
        Assert.Equal("png", response.Image!.OutputFormat);
    }

    [Fact]
    public void BuildImageResponseExtractsBase64FromDataUrl()
    {
        var upstream = new ResponsesResponse
        {
            Id = "resp_dataurl",
            CreatedAt = 1760000000,
            Output =
            [
                new ResponsesItem
                {
                    Type = "image_generation_call",
                    Result = "data:image/png;base64,realb64data",
                },
            ],
        };

        var response = ResponsesImageConvert.BuildImageResponse(upstream, null);

        Assert.NotNull(response.Image);
        Assert.Equal("realb64data", response.Image!.Data[0].B64JSON);
    }

    [Fact]
    public void BuildImageResponseSkipsEmptyResults()
    {
        var upstream = new ResponsesResponse
        {
            Id = "resp_empty_result",
            CreatedAt = 1760000000,
            Output =
            [
                new ResponsesItem { Type = "image_generation_call", Result = "" },
                new ResponsesItem { Type = "image_generation_call", Result = "valid" },
            ],
        };

        var response = ResponsesImageConvert.BuildImageResponse(upstream, null);

        Assert.NotNull(response.Image);
        var data = Assert.Single(response.Image!.Data);
        Assert.Equal("valid", data.B64JSON);
    }

    [Fact]
    public void BuildImageToolRequestPreservesSystemMessages()
    {
        var src = new LlmRequest
        {
            Model = "gpt-image-1",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Messages =
            [
                new Message { Role = "system", Content = MessageContent.FromText("system instruction") },
            ],
            Image = new ImageRequest { Prompt = "a cat" },
        };

        var toolRequest = ResponsesImageConvert.BuildImageToolRequest(src);

        Assert.Equal(3, toolRequest.Messages.Count);
        Assert.Equal("system", toolRequest.Messages[0].Role);
        Assert.Equal(
            ResponsesImageConvert.DefaultImageSystemInstruction,
            toolRequest.Messages[0].Content!.Content);
        Assert.Equal("system", toolRequest.Messages[1].Role);
        Assert.Equal("system instruction", toolRequest.Messages[1].Content!.Content);
        Assert.Equal("user", toolRequest.Messages[2].Role);
    }

    [Fact]
    public void BuildImageToolRequestRejectsMissingImage()
    {
        var ex = Assert.Throws<TransformerException>(
            () => ResponsesImageConvert.BuildImageToolRequest(null));
        Assert.Equal("invalid request: image request is required", ex.Message);

        ex = Assert.Throws<TransformerException>(
            () => ResponsesImageConvert.BuildImageToolRequest(
                new LlmRequest { RequestType = RequestTypes.Image }));
        Assert.Equal("invalid request: image request is required", ex.Message);
    }

    [Fact]
    public void BuildImageToolRequestRejectsUnsupportedApiFormat()
    {
        var src = new LlmRequest
        {
            Model = "gpt-image-1",
            ApiFormat = ApiFormats.OpenAIImageVariation,
            Image = new ImageRequest { Prompt = "a cat" },
        };

        var ex = Assert.Throws<TransformerException>(
            () => ResponsesImageConvert.BuildImageToolRequest(src));

        Assert.Equal(
            "invalid request: responses image tool only supports generation and edit",
            ex.Message);
    }

    [Fact]
    public void BuildImageToolRequestRequiresPrompt()
    {
        var src = new LlmRequest
        {
            Model = "gpt-image-1",
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Image = new ImageRequest(),
        };

        var ex = Assert.Throws<TransformerException>(
            () => ResponsesImageConvert.BuildImageToolRequest(src));

        Assert.Equal("invalid request: prompt is required", ex.Message);
    }

    [Fact]
    public void BuildImageToolRequestRejectsMultipleImages()
    {
        var src = new LlmRequest
        {
            Model = "gpt-image-1",
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Image = new ImageRequest { Prompt = "a cat", N = 2 },
        };

        var ex = Assert.Throws<TransformerException>(
            () => ResponsesImageConvert.BuildImageToolRequest(src));

        Assert.Equal(
            "invalid request: responses image tool does not support n greater than 1",
            ex.Message);
    }

    [Fact]
    public void BuildImageToolRequestRequiresEditSourceImage()
    {
        var src = new LlmRequest
        {
            Model = "gpt-image-1",
            ApiFormat = ApiFormats.OpenAIImageEdit,
            Image = new ImageRequest { Prompt = "edit this image" },
        };

        var ex = Assert.Throws<TransformerException>(
            () => ResponsesImageConvert.BuildImageToolRequest(src));

        Assert.Equal("invalid request: at least one image is required for edits", ex.Message);
    }

    [Fact]
    public void BuildImageToolRequestUsesRawDataUrls()
    {
        var src = new LlmRequest
        {
            Model = "gpt-image-1",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageEdit,
            Image = new ImageRequest
            {
                Prompt = "edit this image",
                Images = [Encoding.UTF8.GetBytes("fallback-image")],
            },
            RawRequest = new HttpRequestData
            {
                JsonBody = Bytes(
                    """{"image":["data:image/webp;base64,QUJD"],"mask":"data:image/png;base64,TUFTSw=="}"""),
            },
        };

        var toolRequest = ResponsesImageConvert.BuildImageToolRequest(src);

        var userContent = toolRequest.Messages[^1].Content!.MultipleContent!;
        Assert.Equal(2, userContent.Count);
        Assert.Equal("text", userContent[0].Type);
        Assert.Equal("edit this image", userContent[0].Text);
        Assert.Equal("image_url", userContent[1].Type);
        Assert.Equal("data:image/webp;base64,QUJD", userContent[1].ImageUrl!.Url);

        var mask = toolRequest.Tools![0].ImageGeneration!.InputImageMask!;
        Assert.Equal("data:image/png;base64,TUFTSw==", mask["image_url"].GetString());
    }

    [Fact]
    public void BuildImageToolRequestUsesByteImagesAndMask()
    {
        var src = new LlmRequest
        {
            Model = "gpt-image-1",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageEdit,
            Image = new ImageRequest
            {
                Prompt = "edit this image",
                Images = [PngMagic, Encoding.UTF8.GetBytes("not-an-image")],
                Mask = JpegMagic,
            },
        };

        var toolRequest = ResponsesImageConvert.BuildImageToolRequest(src);

        var userContent = toolRequest.Messages[^1].Content!.MultipleContent!;
        Assert.Equal(3, userContent.Count);
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", userContent[1].ImageUrl!.Url);

        // Go's DetectContentType finds text/plain here; the caller collapses
        // every non-image result to image/png.
        Assert.Equal(
            "data:image/png;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("not-an-image")),
            userContent[2].ImageUrl!.Url);

        var mask = toolRequest.Tools![0].ImageGeneration!.InputImageMask!;
        Assert.Equal("data:image/jpeg;base64,/9j/", mask["image_url"].GetString());
        Assert.Equal(
            "edit",
            toolRequest.TransformerMetadata!["image_generation_action"].GetString());
    }

    [Fact]
    public void BuildImageToolRequestHonorsToolModelOverride()
    {
        var src = new LlmRequest
        {
            Model = "gpt-image-1",
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Image = new ImageRequest { Prompt = "a cat" },
            TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [ResponsesImageConvert.ImageGenerationToolModelMetadataKey] =
                    JsonSerializer.SerializeToElement("  gpt-image-2  "),
            },
        };

        var toolRequest = ResponsesImageConvert.BuildImageToolRequest(src);

        Assert.Equal("gpt-image-1", toolRequest.Model);
        Assert.Equal("gpt-image-2", toolRequest.Tools![0].ImageGeneration!.Model);
        Assert.Equal(
            "gpt-image-2",
            toolRequest.TransformerMetadata!["codex_image_model"].GetString());

        // Whitespace-only overrides fall back to the request model.
        src.TransformerMetadata[ResponsesImageConvert.ImageGenerationToolModelMetadataKey] =
            JsonSerializer.SerializeToElement("   ");
        toolRequest = ResponsesImageConvert.BuildImageToolRequest(src);
        Assert.Equal("gpt-image-1", toolRequest.Tools![0].ImageGeneration!.Model);
    }

    [Fact]
    public void TransformRequestRejectsImageRequestsWithoutNativeEndpoint()
    {
        var transformer = ResponsesOutboundTransformer.Create(
            "https://api.openai.com", "test-api-key");

        var ex = Assert.Throws<TransformerException>(() => transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-image-1",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Image = new ImageRequest { Prompt = "a cat" },
        }));

        Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
        Assert.Contains(
            "native Images endpoint is required for image requests",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CodexImageStreamAggregatesIntoImageResponse()
    {
        // The image_generation_call result only arrives in
        // response.output_item.done (status "generating", bare string action
        // "generate"); response.completed carries an empty output array.
        var chunks = new List<StreamEvent>
        {
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_x","object":"response","created_at":1781880909,"status":"in_progress","model":"gpt-5.4-mini","output":[]},"sequence_number":0}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.in_progress","response":{"id":"resp_x","object":"response","created_at":1781880909,"status":"in_progress","model":"gpt-5.4-mini","output":[]},"sequence_number":1}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","item":{"id":"ig_1","type":"image_generation_call","status":"in_progress"},"output_index":0,"sequence_number":2}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.image_generation_call.in_progress","item_id":"ig_1","output_index":0,"sequence_number":3}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.image_generation_call.generating","item_id":"ig_1","output_index":0,"sequence_number":4}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.image_generation_call.partial_image","item_id":"ig_1","output_index":0,"partial_image_b64":"UFART","partial_image_index":0,"sequence_number":5}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","item":{"id":"ig_1","type":"image_generation_call","status":"generating","action":"generate","background":"opaque","output_format":"png","quality":"low","result":"iVBORw0KGgo=","revised_prompt":"a puppy","size":"1254x1254"},"output_index":0,"sequence_number":6}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","item":{"id":"msg_1","type":"message","status":"in_progress","content":[],"role":"assistant"},"output_index":1,"sequence_number":7}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.content_part.added","content_index":0,"item_id":"msg_1","output_index":1,"part":{"type":"output_text","annotations":[],"text":""},"sequence_number":8}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_text.done","content_index":0,"item_id":"msg_1","output_index":1,"text":"","sequence_number":9}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.content_part.done","content_index":0,"item_id":"msg_1","output_index":1,"part":{"type":"output_text","annotations":[],"text":""},"sequence_number":10}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","item":{"id":"msg_1","type":"message","status":"completed","content":[{"type":"output_text","annotations":[],"text":""}],"role":"assistant"},"output_index":1,"sequence_number":11}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_x","object":"response","created_at":1781880909,"status":"completed","model":"gpt-5.4-mini","output":[]},"sequence_number":12}"""),
        };

        var body = ResponsesAggregate.AggregateStreamChunks(chunks, out _);
        var upstream = Assert.IsType<ResponsesResponse>(LlmJson.Deserialize<ResponsesResponse>(body));

        var response = ResponsesImageConvert.BuildImageResponse(
            upstream, new Dictionary<string, JsonElement>(StringComparer.Ordinal));

        Assert.NotNull(response.Image);
        var data = Assert.Single(response.Image!.Data);
        Assert.Equal("iVBORw0KGgo=", data.B64JSON);
    }

    [Fact]
    public void ItemActionAcceptsStringObjectAndNull()
    {
        var stringAction = Assert.IsType<ResponsesItemAction>(
            LlmJson.Deserialize<ResponsesItemAction>("\"generate\""));
        Assert.Equal("generate", stringAction.ImageGenerationAction);
        Assert.Null(stringAction.WebSearch);

        var objectAction = Assert.IsType<ResponsesItemAction>(
            LlmJson.Deserialize<ResponsesItemAction>("{\"type\":\"search\",\"query\":\"cats\"}"));
        Assert.NotNull(objectAction.WebSearch);
        Assert.Equal("search", objectAction.WebSearch!.Type);
        Assert.Equal("cats", objectAction.WebSearch.Query);
        Assert.Equal(string.Empty, objectAction.ImageGenerationAction);

        // Go's bare-value case (`var a ItemAction; json.Unmarshal(null, &a)`)
        // has no direct STJ analogue: null skips Read for reference types. On
        // the wire Item.Action is a pointer that Go also leaves nil, so the
        // observable behaviour is a null action field.
        var item = Assert.IsType<ResponsesItem>(LlmJson.Deserialize<ResponsesItem>(
            "{\"id\":\"ig_1\",\"type\":\"image_generation_call\",\"action\":null}"));
        Assert.Null(item.Action);
    }

    private static Dictionary<string, JsonElement> Metadata(params (string Key, string Value)[] pairs)
    {
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            metadata[key] = JsonSerializer.SerializeToElement(value);
        }

        return metadata;
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
