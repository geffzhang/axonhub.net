using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for openai.VideoInboundTransformer and the video routes on
/// the shared OpenAI outbound transformer. Every test mirrors a function in
/// video_inbound_test.go / video_outbound_test.go, plus boundary and
/// wire-shape cases for the unreferenced error paths.
/// </summary>
public sealed class OpenAiVideoTransformerTests
{
    // ---------------------------------------------------------------- inbound

    // Port of TestVideoInboundTransformer_TransformRequest_JSON.
    [Fact]
    public void VideoInboundTransformer_TransformRequest_JSON()
    {
        var inbound = new VideoInboundTransformer();

        var httpReq = JsonRequest(
            "http://localhost/v1/videos",
            Bytes(
                """
                {
                    "model":"sora-2",
                    "prompt":"a cat walking",
                    "input_reference":"https://example.com/a.png",
                    "seconds":"8",
                    "size":"1280x720",
                    "ratio":"16:9",
                    "resolution":"720p",
                    "callback_url":"http://example.com/callback",
                    "return_last_frame":true,
                    "tools":[{"type":"web_search"}],
                    "extra_body":{"provider_hint":{"mode":"fast"}},
                    "camera_fixed":true
                }
                """));

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.Equal(RequestTypes.Video, llmReq.RequestType);
        Assert.Equal(ApiFormats.OpenAIVideo, llmReq.ApiFormat);
        Assert.Equal("sora-2", llmReq.Model);
        Assert.NotNull(llmReq.Video);
        Assert.Equal("sora-2", llmReq.Video!.Model);
        Assert.Equal("8", llmReq.Video.Duration);
        Assert.Equal("1280x720", llmReq.Video.Size);
        Assert.Equal("16:9", llmReq.Video.Ratio);
        Assert.Equal("720p", llmReq.Video.Resolution);
        Assert.Equal("http://example.com/callback", llmReq.Video.CallbackURL);
        Assert.True(llmReq.Video.ReturnLastFrame!.Value);
        var tools = Assert.Single(llmReq.Video.Tools!);
        AssertJsonEq("""{"type":"web_search"}""", tools);
        Assert.NotNull(llmReq.ExtraBody);
        AssertJsonEq(
            """{"provider_hint":{"mode":"fast"},"camera_fixed":true}""",
            llmReq.ExtraBody!.Value);
        Assert.Equal("a cat walking", OpenAiOutboundTransformer.FirstVideoText(llmReq.Video.Content));
        Assert.Equal("https://example.com/a.png", OpenAiOutboundTransformer.FirstVideoImageURL(llmReq.Video.Content));
    }

    // Port of TestVideoInboundTransformer_TransformRequest_JSONPreservesContent.
    [Fact]
    public void VideoInboundTransformer_TransformRequest_JSONPreservesContent()
    {
        var inbound = new VideoInboundTransformer();
        var httpReq = JsonRequest(
            "http://localhost/v1/videos",
            Bytes(
                """
                {
                    "model":"zenmux-video",
                    "content":[
                        {"type":"text","text":"keep this prompt"},
                        {"type":"image_url","image_url":{"url":"https://example.com/last.png"},"role":"last_frame"},
                        {"type":"video_url","video_url":{"url":"https://example.com/reference.mp4"},"role":"reference_video"}
                    ]
                }
                """));

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.NotNull(llmReq.Video);
        Assert.Equal(3, llmReq.Video!.Content.Count);
        Assert.Equal("text", llmReq.Video.Content[0].Type);
        Assert.Equal("keep this prompt", llmReq.Video.Content[0].Text);
        Assert.Equal("image_url", llmReq.Video.Content[1].Type);
        Assert.Equal("https://example.com/last.png", llmReq.Video.Content[1].ImageURL!.URL);
        Assert.Equal("last_frame", llmReq.Video.Content[1].Role);
        Assert.Equal("video_url", llmReq.Video.Content[2].Type);
        Assert.Equal("https://example.com/reference.mp4", llmReq.Video.Content[2].VideoURL!.URL);
        Assert.Equal("reference_video", llmReq.Video.Content[2].Role);
    }

    // Port of TestVideoInboundTransformer_TransformRequest_Multipart_WithInputReferenceFile.
    [Fact]
    public void VideoInboundTransformer_TransformRequest_Multipart_WithInputReferenceFile()
    {
        var inbound = new VideoInboundTransformer();

        var writer = new GoMultipartWriter();
        writer.WriteField("model", "sora-2");
        writer.WriteField("prompt", "a cat walking");
        writer.WriteField("seconds", "8");
        writer.WriteField("size", "1280x720");
        writer.WriteField("callback_url", "http://example.com/callback");
        writer.WriteField("return_last_frame", "true");
        writer.WriteField("tools", """[{"type":"web_search"}]""");
        writer.WriteField("extra_body", """{"provider_hint":{"mode":"fast"}}""");
        writer.WriteField("camera_fixed", "true");
        writer.CreatePart("input_reference", "ref.png", "image/png", Encoding.UTF8.GetBytes("pngdata"));
        writer.Close();

        var httpReq = MultipartRequest(writer.FormDataContentType(), writer.ToArray());

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.Equal(RequestTypes.Video, llmReq.RequestType);
        Assert.Equal(ApiFormats.OpenAIVideo, llmReq.ApiFormat);
        Assert.NotNull(llmReq.Video);
        Assert.Equal("http://example.com/callback", llmReq.Video!.CallbackURL);
        Assert.True(llmReq.Video.ReturnLastFrame!.Value);
        var tools = Assert.Single(llmReq.Video.Tools!);
        AssertJsonEq("""{"type":"web_search"}""", tools);
        Assert.NotNull(llmReq.ExtraBody);
        AssertJsonEq(
            """{"provider_hint":{"mode":"fast"},"camera_fixed":true}""",
            llmReq.ExtraBody!.Value);

        var reference = OpenAiOutboundTransformer.FirstVideoImageURL(llmReq.Video.Content);
        Assert.False(string.IsNullOrEmpty(reference));
        Assert.Contains("data:image/png;base64,", reference, StringComparison.Ordinal);
    }

    // Port of TestVideoInboundTransformer_TransformRequest_Multipart_ShortUntypedReference.
    [Fact]
    public void VideoInboundTransformer_TransformRequest_Multipart_ShortUntypedReference()
    {
        var inbound = new VideoInboundTransformer();

        var writer = new GoMultipartWriter();
        writer.WriteField("model", "sora-2");
        writer.WriteField("prompt", "a cat walking");
        writer.CreatePart("input_reference", "ref.bin", null, Encoding.UTF8.GetBytes("short"));
        writer.Close();

        var httpReq = MultipartRequest(writer.FormDataContentType(), writer.ToArray());

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
        Assert.Contains("unsupported image type", ex.Message, StringComparison.Ordinal);
    }

    // Port of TestVideoInboundTransformer_TransformRequest_MultipartPreservesContent.
    [Fact]
    public void VideoInboundTransformer_TransformRequest_MultipartPreservesContent()
    {
        var inbound = new VideoInboundTransformer();

        var writer = new GoMultipartWriter();
        writer.WriteField("model", "zenmux-video");
        writer.WriteField(
            "content",
            """[{"type":"text","text":"multipart prompt"},{"type":"image_url","image_url":{"url":"https://example.com/first.png"},"role":"first_frame"}]""");
        writer.Close();

        var httpReq = MultipartRequest(writer.FormDataContentType(), writer.ToArray());

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.NotNull(llmReq.Video);
        Assert.Equal(2, llmReq.Video!.Content.Count);
        Assert.Equal("text", llmReq.Video.Content[0].Type);
        Assert.Equal("multipart prompt", llmReq.Video.Content[0].Text);
        Assert.Equal("image_url", llmReq.Video.Content[1].Type);
        Assert.Equal("https://example.com/first.png", llmReq.Video.Content[1].ImageURL!.URL);
        Assert.Equal("first_frame", llmReq.Video.Content[1].Role);
    }

    // Port of TestVideoInboundTransformer_TransformRequest_rejects_non_object_extra_body.
    [Fact]
    public void VideoInboundTransformer_TransformRequest_RejectsNonObjectExtraBody()
    {
        var inbound = new VideoInboundTransformer();
        var httpReq = JsonRequest(
            "http://localhost/v1/videos",
            Bytes("""{"model":"sora-2","prompt":"a cat walking","extra_body":[]}"""));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
        Assert.Contains("extra_body must be a JSON object", ex.Message, StringComparison.Ordinal);
    }

    // Port of TestVideoInboundTransformer_TransformResponse_JSON.
    [Fact]
    public void VideoInboundTransformer_TransformResponse_JSON()
    {
        var inbound = new VideoInboundTransformer();

        using var contentDoc = JsonDocument.Parse(
            """
            {"video_url":"https://example.com/video.mp4","last_frame_url":"https://example.com/last.jpg","provider_metadata":{"request_id":"native-1"}}
            """);

        var llmResp = new LlmResponse
        {
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.OpenAIVideo,
            Video = new VideoResponse
            {
                ID = "vid_1",
                Status = "running",
                Model = "sora-2",
                Prompt = "a cat",
                Duration = "8",
                Size = "1280x720",
                Progress = 50,
                LastFrameURL = "https://example.com/last.jpg",
                Content = contentDoc.RootElement.Clone(),
                CreatedAt = 1700000000,
            },
        };

        var httpResp = inbound.TransformResponse(llmResp);

        Assert.Equal(200, httpResp.StatusCode);
        Assert.Equal("application/json", httpResp.Headers["Content-Type"][0]);
        Assert.Equal("no-cache", httpResp.Headers["Cache-Control"][0]);

        using var doc = JsonDocument.Parse(httpResp.Body!);
        var root = doc.RootElement;
        Assert.Equal("vid_1", root.GetProperty("id").GetString());
        Assert.Equal("in_progress", root.GetProperty("status").GetString());
        Assert.Equal("sora-2", root.GetProperty("model").GetString());
        Assert.Equal("a cat", root.GetProperty("prompt").GetString());
        Assert.Equal("8", root.GetProperty("seconds").GetString());
        Assert.Equal("1280x720", root.GetProperty("size").GetString());
        Assert.Equal(50, root.GetProperty("progress").GetDouble());
        Assert.Equal("https://example.com/last.jpg", root.GetProperty("last_frame_url").GetString());
        Assert.Equal(1700000000L, root.GetProperty("created_at").GetInt64());
        Assert.False(root.TryGetProperty("completed_at", out _));
        Assert.False(root.TryGetProperty("expires_at", out _));
        AssertJsonEq(
            """{"video_url":"https://example.com/video.mp4","last_frame_url":"https://example.com/last.jpg","provider_metadata":{"request_id":"native-1"}}""",
            root.GetProperty("content"));
    }

    [Fact]
    public void VideoInboundTransformer_TransformResponse_UsageAndError()
    {
        var inbound = new VideoInboundTransformer();

        var httpResp = inbound.TransformResponse(new LlmResponse
        {
            Video = new VideoResponse
            {
                ID = "vid_1",
                Status = "failed",
                Error = new VideoError { Code = "err", Message = "boom" },
                CompletedAt = 1700000001,
                ExpiresAt = 1700000002,
            },
            Usage = new Usage { CompletionTokens = 3, TotalTokens = 7 },
        });

        using var doc = JsonDocument.Parse(httpResp.Body!);
        var root = doc.RootElement;
        Assert.Equal(1700000001L, root.GetProperty("completed_at").GetInt64());
        Assert.Equal(1700000002L, root.GetProperty("expires_at").GetInt64());
        Assert.Equal("err", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("boom", root.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(3L, root.GetProperty("usage").GetProperty("completion_tokens").GetInt64());
        Assert.Equal(7L, root.GetProperty("usage").GetProperty("total_tokens").GetInt64());
        Assert.False(root.GetProperty("usage").TryGetProperty("cost", out _));
    }

    // ---------------------------------------------------------------- inbound boundaries

    [Fact]
    public void VideoInboundTransformer_TransformRequest_NilRequest()
    {
        var ex = Assert.Throws<TransformerException>(() => new VideoInboundTransformer().TransformRequest(null));
        Assert.Contains("http request is nil", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_EmptyBody()
    {
        var httpReq = JsonRequest("http://localhost/v1/videos", []);
        var ex = Assert.Throws<TransformerException>(() => new VideoInboundTransformer().TransformRequest(httpReq));
        Assert.Contains("request body is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_DefaultContentTypeIsJson()
    {
        var inbound = new VideoInboundTransformer();
        var httpReq = new HttpRequestData
        {
            Method = "POST",
            Url = "http://localhost/v1/videos",
            Body = Bytes("""{"model":"sora-2","prompt":"a cat"}"""),
        };

        var llmReq = inbound.TransformRequest(httpReq);

        Assert.Equal("sora-2", llmReq.Model);
        Assert.NotNull(llmReq.Video);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_UnsupportedContentType()
    {
        var inbound = new VideoInboundTransformer();
        var httpReq = new HttpRequestData
        {
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["text/plain"],
            },
            Body = Bytes("model=1"),
        };

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("unsupported content type: text/plain", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_MissingModel()
    {
        var inbound = new VideoInboundTransformer();
        var httpReq = JsonRequest("http://localhost/v1/videos", Bytes("""{"prompt":"a cat"}"""));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("model is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_MissingPrompt()
    {
        var inbound = new VideoInboundTransformer();
        var httpReq = JsonRequest("http://localhost/v1/videos", Bytes("""{"model":"sora-2"}"""));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("prompt is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_MalformedJson()
    {
        var inbound = new VideoInboundTransformer();
        var httpReq = JsonRequest("http://localhost/v1/videos", Bytes("{bad"));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("failed to decode video request: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_ArrayRoot()
    {
        var inbound = new VideoInboundTransformer();
        var httpReq = JsonRequest("http://localhost/v1/videos", Bytes("[1,2]"));

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("failed to decode video request: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("openai.VideoCreateRequest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_Multipart_ExecutionExpiresAfterNotInteger()
    {
        var ex = Assert.Throws<TransformerException>(() => TransformMultipart(("execution_expires_after", "abc")));
        Assert.Contains("execution_expires_after must be an integer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_Multipart_ContentNotArray()
    {
        var ex = Assert.Throws<TransformerException>(() => TransformMultipart(("content", """{"a":1}""")));
        Assert.Contains("content must be a JSON array", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_Multipart_ReturnLastFrameNotBool()
    {
        var ex = Assert.Throws<TransformerException>(() => TransformMultipart(("return_last_frame", "yes")));
        Assert.Contains("return_last_frame must be a boolean", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_Multipart_ToolsNotArray()
    {
        var ex = Assert.Throws<TransformerException>(() => TransformMultipart(("tools", """{"a":1}""")));
        Assert.Contains("tools must be a JSON array", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_Multipart_NativeFieldInvalidJson()
    {
        var ex = Assert.Throws<TransformerException>(() => TransformMultipart(("native_field", "{broken")));
        Assert.Contains(
            "native field \"native_field\" must be valid JSON",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_Multipart_ExtraBodyNotObject()
    {
        var ex = Assert.Throws<TransformerException>(() => TransformMultipart(("extra_body", "[]")));
        Assert.Contains("extra_body must be a JSON object", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_Multipart_MultipleReferenceFiles()
    {
        var inbound = new VideoInboundTransformer();
        var writer = new GoMultipartWriter();
        writer.WriteField("model", "sora-2");
        writer.WriteField("prompt", "a cat");
        writer.CreatePart("input_reference", "a.png", "image/png", Encoding.UTF8.GetBytes("png"));
        writer.CreatePart("input_reference", "b.png", "image/png", Encoding.UTF8.GetBytes("png"));
        writer.Close();

        var httpReq = MultipartRequest(writer.FormDataContentType(), writer.ToArray());

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("multiple input_reference files are not supported", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformRequest_Multipart_MissingBoundary()
    {
        var inbound = new VideoInboundTransformer();
        var httpReq = new HttpRequestData
        {
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["multipart/form-data"],
            },
            Body = Bytes("--x"),
        };

        var ex = Assert.Throws<TransformerException>(() => inbound.TransformRequest(httpReq));
        Assert.Contains("missing boundary in content-type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformResponse_NilVideo()
    {
        var ex = Assert.Throws<TransformerException>(
            () => new VideoInboundTransformer().TransformResponse(new LlmResponse()));
        Assert.Equal(TransformerErrorKind.InvalidResponse, ex.Kind);
        Assert.Contains("video response is nil", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoInboundTransformer_TransformStream_Unsupported()
    {
        var ex = Assert.Throws<TransformerException>(
            () => new VideoInboundTransformer().TransformStream(new SliceStream<LlmResponse>([])));
        Assert.Contains("video request does not support streaming", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- outbound

    // Port of buildVideoGenerationAPIRequest plus wire-shape locks.
    [Fact]
    public void Outbound_BuildVideoGenerationApiRequest()
    {
        var outbound = VideoOutbound();
        var llmReq = new LlmRequest
        {
            RequestType = RequestTypes.Video,
            Model = "sora-2",
            Video = new VideoRequest
            {
                Content =
                [
                    new VideoContent { Type = "image_url", ImageURL = new VideoImageURL { URL = "https://example.com/a.png" } },
                    new VideoContent { Type = "text", Text = "a cat walking" },
                ],
                Duration = "8",
                Size = "1280x720",
            },
        };

        var outReq = outbound.TransformRequest(llmReq);

        Assert.Equal("POST", outReq.Method);
        Assert.Equal("https://apihub.agnes-ai.com/v1/videos", outReq.Url);
        Assert.Equal(
            """{"input_reference":"https://example.com/a.png","model":"sora-2","prompt":"a cat walking","seconds":"8","size":"1280x720"}""",
            Encoding.UTF8.GetString(outReq.Body!));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(outReq.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(outReq.Headers, "Accept"));
        Assert.Equal(AuthTypes.Bearer, outReq.Auth!.Type);
        Assert.Equal("sk-test", outReq.Auth.ApiKey);
        Assert.Equal("sk-test", outbound.SelectedApiKey);
        Assert.Equal(RequestTypes.Video, outReq.RequestType);
        Assert.Equal(ApiFormats.OpenAIVideo, outReq.ApiFormat);
        Assert.Equal("sora-2", outReq.TransformerMetadata!["model"].GetString());
        Assert.Equal("a cat walking", outReq.TransformerMetadata!["video_prompt"].GetString());
        Assert.Equal("a cat walking", llmReq.TransformerMetadata!["video_prompt"].GetString());
    }

    [Fact]
    public void Outbound_BuildVideoGenerationApiRequest_NoReferenceNoSize()
    {
        var outbound = VideoOutbound();
        var llmReq = new LlmRequest
        {
            RequestType = RequestTypes.Video,
            Model = "m",
            Video = new VideoRequest
            {
                Content = [new VideoContent { Type = "text", Text = "p" }],
            },
        };

        var outReq = outbound.TransformRequest(llmReq);

        Assert.Equal("""{"model":"m","prompt":"p"}""", Encoding.UTF8.GetString(outReq.Body!));
    }

    [Fact]
    public void Outbound_BuildVideoGenerationApiRequest_MissingVideo()
    {
        var llmReq = new LlmRequest { RequestType = RequestTypes.Video, Model = "m" };

        var ex = Assert.Throws<TransformerException>(() => VideoOutbound().TransformRequest(llmReq));
        Assert.Contains("video request is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Outbound_BuildVideoGenerationApiRequest_MissingModel()
    {
        // The reference's TransformRequest checks the model before dispatching
        // and returns a plain (non-sentinel) error, matching the port's chat
        // path: outbound.go:148.
        var llmReq = new LlmRequest
        {
            RequestType = RequestTypes.Video,
            Video = new VideoRequest { Content = [new VideoContent { Type = "text", Text = "p" }] },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => VideoOutbound().TransformRequest(llmReq));
        Assert.Contains("model is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Outbound_BuildVideoGenerationApiRequest_MissingPrompt()
    {
        var llmReq = new LlmRequest
        {
            RequestType = RequestTypes.Video,
            Model = "m",
            Video = new VideoRequest(),
        };

        var ex = Assert.Throws<TransformerException>(() => VideoOutbound().TransformRequest(llmReq));
        Assert.Contains("prompt is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Outbound_TransformVideoResponse()
    {
        var outbound = VideoOutbound();
        var httpResp = VideoResponse(
            """{"id":"vid_1","status":"in_progress"}""",
            [("model", "sora-2"), ("video_prompt", "a cat walking")]);

        var resp = outbound.TransformVideoResponse(httpResp);

        Assert.Equal("vid_1", resp.Id);
        Assert.Equal("video", resp.Object);
        Assert.Equal("sora-2", resp.Model);
        Assert.Equal(RequestTypes.Video, resp.RequestType);
        Assert.Equal(ApiFormats.OpenAIVideo, resp.ApiFormat);
        Assert.NotNull(resp.Video);
        Assert.Equal("vid_1", resp.Video!.ID);
        Assert.Equal("running", resp.Video.Status);
        Assert.Equal("sora-2", resp.Video.Model);
        Assert.Equal("a cat walking", resp.Video.Prompt);
        Assert.True(resp.Video.CreatedAt > 0);
        Assert.Empty(resp.Choices);
    }

    [Fact]
    public void Outbound_TransformVideoResponse_Defaults()
    {
        var outbound = VideoOutbound();
        var httpResp = VideoResponse("""{"id":"vid_1","status":"queued"}""", []);

        var resp = outbound.TransformVideoResponse(httpResp);

        Assert.Equal("video-generation", resp.Model);
        Assert.Equal("queued", resp.Video!.Status);
        Assert.Null(resp.Video.Prompt);
    }

    [Fact]
    public void Outbound_TransformVideoResponse_MissingId()
    {
        var outbound = VideoOutbound();

        var ex = Assert.Throws<TransformerException>(
            () => outbound.TransformVideoResponse(VideoResponse("""{"status":"queued"}""", [])));
        Assert.Equal(TransformerErrorKind.InvalidResponse, ex.Kind);
        Assert.Contains("missing id in openai video response", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Outbound_TransformVideoResponse_ProviderError()
    {
        var outbound = VideoOutbound();

        var ex = Assert.Throws<ResponseError>(
            () => outbound.TransformVideoResponse(
                VideoResponse("""{"id":"v1","error":{"code":"bad","message":"boom"}}""", [])));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("bad", ex.Detail.Code);
        Assert.Equal("boom", ex.Detail.Message);
        Assert.Equal("video_generation_error", ex.Detail.Type);
    }

    [Fact]
    public void Outbound_TransformVideoResponse_MalformedJson()
    {
        var outbound = VideoOutbound();

        var ex = Assert.Throws<InvalidOperationException>(
            () => outbound.TransformVideoResponse(VideoResponse("{oops", [])));
        Assert.Contains("failed to unmarshal openai video response: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Outbound_TransformVideoResponse_ArrayRoot()
    {
        var outbound = VideoOutbound();

        var ex = Assert.Throws<InvalidOperationException>(
            () => outbound.TransformVideoResponse(VideoResponse("[1,2]", [])));
        Assert.Contains("failed to unmarshal openai video response: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("openai.openAIVideoCreateResponse", ex.Message, StringComparison.Ordinal);
    }

    // Port of the outbound.go dispatch: APIFormat openai/video routes to the
    // video response transformer.
    [Fact]
    public void Outbound_TransformVideoResponse_DispatchViaApiFormat()
    {
        var outbound = VideoOutbound();
        var httpResp = VideoResponse("""{"id":"vid_1","status":"completed"}""", [("model", "sora-2")]);
        httpResp.Request!.ApiFormat = ApiFormats.OpenAIVideo;

        var resp = outbound.TransformResponse(httpResp);

        Assert.Equal("succeeded", resp.Video!.Status);
    }

    [Theory]
    [InlineData("queued", "queued")]
    [InlineData("in_progress", "running")]
    [InlineData("completed", "succeeded")]
    [InlineData("failed", "failed")]
    [InlineData("", "queued")]
    [InlineData("processing", "processing")]
    [InlineData("\tPROCESSING\t", "\tprocessing\t")]
    public void Outbound_NormalizeVideoStatusOpenAI(string status, string expected)
    {
        Assert.Equal(expected, OpenAiOutboundTransformer.NormalizeVideoStatusOpenAI(status));
    }

    // Port of TestOutbound_BuildGetVideoTaskRequest.
    [Fact]
    public void Outbound_BuildGetVideoTaskRequest()
    {
        var outbound = VideoOutbound();

        var req = outbound.BuildGetVideoTaskRequest("task_123");

        Assert.Equal("GET", req.Method);
        Assert.Equal("https://apihub.agnes-ai.com/v1/videos/task_123", req.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(req.Headers, "Accept"));
        Assert.Equal(AuthTypes.Bearer, req.Auth!.Type);
        Assert.Equal("sk-test", req.Auth.ApiKey);
        Assert.Equal(RequestTypes.Video, req.RequestType);
        Assert.Equal(ApiFormats.OpenAIVideo, req.ApiFormat);
    }

    [Fact]
    public void Outbound_BuildGetVideoTaskRequest_BlankId()
    {
        var ex = Assert.Throws<TransformerException>(() => VideoOutbound().BuildGetVideoTaskRequest(" "));
        Assert.Contains("providerTaskID is required", ex.Message, StringComparison.Ordinal);
    }

    // Port of TestOutbound_ParseGetVideoTaskResponse_AgnesSecondsString.
    [Fact]
    public void Outbound_ParseGetVideoTaskResponse_AgnesSecondsString()
    {
        var outbound = VideoOutbound();
        var httpResp = VideoResponse(
            """
            {
                "id": "video_xxx",
                "object": "video",
                "model": "agnes-video-v2.0",
                "status": "completed",
                "progress": 100,
                "seconds": "3.4",
                "size": "1088x832",
                "created_at": 1781604979,
                "completed_at": 1781605076,
                "video_url": "https://platform-outputs.agnes-ai.space/videos/agnes-video-v2.0/2026/06/16/video_xxx.mp4"
            }
            """,
            []);

        var resp = outbound.ParseGetVideoTaskResponse(httpResp);

        Assert.NotNull(resp.Video);
        Assert.Equal("video_xxx", resp.Video!.ID);
        Assert.Equal("succeeded", resp.Video.Status);
        Assert.Equal("3.4", resp.Video.Duration);
        Assert.Equal(
            "https://platform-outputs.agnes-ai.space/videos/agnes-video-v2.0/2026/06/16/video_xxx.mp4",
            resp.Video.VideoURL);
        Assert.Equal("agnes-video-v2.0", resp.Video.Model);
        Assert.Equal(100, resp.Video.Progress);
        Assert.Equal(1781604979L, resp.Video.CreatedAt);
        Assert.Equal(1781605076L, resp.Video.CompletedAt);
        Assert.Equal(0L, resp.Video.ExpiresAt);
    }

    // Port of TestOutbound_ParseGetVideoTaskResponse_SecondsStringInteger.
    [Fact]
    public void Outbound_ParseGetVideoTaskResponse_SecondsStringInteger()
    {
        var outbound = VideoOutbound();
        var httpResp = VideoResponse(
            """
            {
                "id": "video_yyy",
                "object": "video",
                "status": "queued",
                "progress": 0,
                "seconds": "8",
                "size": "1280x720",
                "created_at": 1781607020,
                "video_url": "https://example.com/video.mp4"
            }
            """,
            []);

        var resp = outbound.ParseGetVideoTaskResponse(httpResp);

        Assert.NotNull(resp.Video);
        Assert.Equal("video_yyy", resp.Video!.ID);
        Assert.Equal("queued", resp.Video.Status);
        Assert.Equal("8", resp.Video.Duration);
        Assert.Equal("https://example.com/video.mp4", resp.Video.VideoURL);
        Assert.Equal(0, resp.Video.Progress);
    }

    [Fact]
    public void Outbound_ParseGetVideoTaskResponse_MissingId()
    {
        var outbound = VideoOutbound();

        var ex = Assert.Throws<TransformerException>(
            () => outbound.ParseGetVideoTaskResponse(VideoResponse("""{"status":"queued"}""", [])));
        Assert.Equal(TransformerErrorKind.InvalidResponse, ex.Kind);
        Assert.Contains("missing id in openai video response", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Outbound_ParseGetVideoTaskResponse_ProviderError()
    {
        var outbound = VideoOutbound();

        var resp = outbound.ParseGetVideoTaskResponse(
            VideoResponse("""{"id":"v1","error":{"code":"x","message":"failed"}}""", []));

        Assert.NotNull(resp.Video!.Error);
        Assert.Equal("x", resp.Video.Error!.Code);
        Assert.Equal("failed", resp.Video.Error.Message);
    }

    [Fact]
    public void Outbound_ParseGetVideoTaskResponse_Usage()
    {
        var outbound = VideoOutbound();

        var resp = outbound.ParseGetVideoTaskResponse(
            VideoResponse("""{"id":"v1","usage":{"completion_tokens":5,"total_tokens":10}}""", []));

        Assert.NotNull(resp.Usage);
        Assert.Equal(5L, resp.Usage!.CompletionTokens);
        Assert.Equal(10L, resp.Usage.TotalTokens);
    }

    [Fact]
    public void Outbound_ParseGetVideoTaskResponse_MalformedJson()
    {
        var outbound = VideoOutbound();

        var ex = Assert.Throws<InvalidOperationException>(
            () => outbound.ParseGetVideoTaskResponse(VideoResponse("{oops", [])));
        Assert.Contains("failed to unmarshal openai video get response: ", ex.Message, StringComparison.Ordinal);
    }

    // Port of BuildDeleteVideoTaskRequest.
    [Fact]
    public void Outbound_BuildDeleteVideoTaskRequest()
    {
        var outbound = VideoOutbound();

        var req = outbound.BuildDeleteVideoTaskRequest("task_123");

        Assert.Equal("DELETE", req.Method);
        Assert.Equal("https://apihub.agnes-ai.com/v1/videos/task_123", req.Url);
        Assert.Equal(AuthTypes.Bearer, req.Auth!.Type);
        Assert.Equal(RequestTypes.Video, req.RequestType);
        Assert.Equal(ApiFormats.OpenAIVideo, req.ApiFormat);
    }

    [Fact]
    public void Outbound_BuildDeleteVideoTaskRequest_BlankId()
    {
        var ex = Assert.Throws<TransformerException>(() => VideoOutbound().BuildDeleteVideoTaskRequest(" "));
        Assert.Contains("providerTaskID is required", ex.Message, StringComparison.Ordinal);
    }

    // Port of firstVideoText / firstVideoImageURL.
    [Fact]
    public void Outbound_FirstVideoTextAndImageURL()
    {
        var content = new List<VideoContent>
        {
            new() { Type = "image_url", ImageURL = new VideoImageURL { URL = "https://example.com/a.png" } },
            new() { Type = "text", Text = "  prompt  " },
            new() { Type = "image_url", ImageURL = new VideoImageURL { URL = "   " } },
        };

        Assert.Equal("  prompt  ", OpenAiOutboundTransformer.FirstVideoText(content));
        Assert.Equal("https://example.com/a.png", OpenAiOutboundTransformer.FirstVideoImageURL(content));
        Assert.Equal(string.Empty, OpenAiOutboundTransformer.FirstVideoText([content[0]]));
        Assert.Equal(string.Empty, OpenAiOutboundTransformer.FirstVideoImageURL([content[1]]));
    }

    // ---------------------------------------------------------------- helpers

    private static OpenAiOutboundTransformer VideoOutbound()
        => OpenAiOutboundTransformer.Create("https://apihub.agnes-ai.com", "sk-test");

    private static LlmRequest TransformMultipart(params (string Name, string Value)[] fields)
    {
        var writer = new GoMultipartWriter();
        writer.WriteField("model", "sora-2");
        writer.WriteField("prompt", "a cat");
        foreach (var (name, value) in fields)
        {
            writer.WriteField(name, value);
        }

        writer.Close();

        var httpReq = MultipartRequest(writer.FormDataContentType(), writer.ToArray());
        return new VideoInboundTransformer().TransformRequest(httpReq);
    }

    private static HttpResponseData VideoResponse(
        string body,
        (string Key, string Value)[] metadata)
    {
        var transformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            transformerMetadata[key] = JsonSerializer.SerializeToElement(value);
        }

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = Bytes(body),
            Request = new HttpRequestData
            {
                TransformerMetadata = transformerMetadata,
            },
        };
    }

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

    private static HttpRequestData MultipartRequest(string contentType, byte[] body)
        => new()
        {
            Method = "POST",
            Url = "http://localhost/v1/videos",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = [contentType],
            },
            Body = body,
        };

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static void AssertJsonEq(string expected, JsonElement actual)
    {
        using var expectedDoc = JsonDocument.Parse(expected);
        Assert.True(
            JsonElement.DeepEquals(expectedDoc.RootElement, actual),
            $"expected {expected}, got {actual.GetRawText()}");
    }
}
