using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Doubao;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for llm/transformer/doubao/video_inbound.go: the Seedance
/// create request parsing and the two client-facing response shapes.
/// </summary>
public sealed class DoubaoVideoInboundTransformerTests
{
    private static readonly DoubaoVideoInboundTransformer Transformer = new();

    private static HttpRequestData JsonRequest(string body, string? contentType = "application/json")
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (contentType is not null)
        {
            headers["Content-Type"] = [contentType];
        }

        return new HttpRequestData
        {
            Method = "POST",
            Url = "https://gateway.test/doubao/v3/contents/generations/tasks",
            Headers = headers,
            Body = Encoding.UTF8.GetBytes(body),
        };
    }

    private static JsonElement Body(HttpResponseData response)
    {
        using var document = JsonDocument.Parse(response.Body!);
        return document.RootElement.Clone();
    }

    [Fact]
    public void TransformRequest_ParsesTheSeedanceCreateRequest()
    {
        var httpRequest = JsonRequest(
            """
            {
                "model":"doubao-seedance-1-0-pro",
                "content":[
                    {"type":"text","text":"a cat walking"},
                    {"type":"image_url","image_url":{"url":"https://example.com/a.png"},"role":"first_frame"}
                ],
                "duration":8,
                "ratio":"16:9",
                "resolution":"720p",
                "frames":121,
                "seed":7,
                "generate_audio":true,
                "camera_fixed":true,
                "watermark":false,
                "draft":true,
                "service_tier":"flex",
                "execution_expires_after":600
            }
            """);

        var request = Transformer.TransformRequest(httpRequest);

        Assert.Equal("doubao-seedance-1-0-pro", request.Model);
        Assert.Equal(RequestTypes.Video, request.RequestType);
        Assert.Equal(ApiFormats.SeedanceVideo, request.ApiFormat);
        Assert.Equal("video", Assert.Single(request.Modalities!));
        Assert.Empty(request.Messages!);
        Assert.False(request.Stream!.Value);
        Assert.Same(httpRequest, request.RawRequest);

        var video = request.Video!;
        Assert.Equal("doubao-seedance-1-0-pro", video.Model);
        Assert.Equal("8", video.Duration);
        Assert.Equal("16:9", video.Ratio);
        Assert.Equal("720p", video.Resolution);
        Assert.Equal(121L, video.Frames);
        Assert.Equal(7L, video.Seed);
        Assert.True(video.GenerateAudio);
        Assert.True(video.CameraFixed);
        Assert.False(video.Watermark);
        Assert.True(video.Draft);
        Assert.Equal("flex", video.ServiceTier);
        Assert.Equal(600L, video.ExecutionExpiresAfter);
        Assert.Equal(2, video.Content.Count);
        Assert.Equal("text", video.Content[0].Type);
        Assert.Equal("a cat walking", video.Content[0].Text);
        Assert.Equal("image_url", video.Content[1].Type);
        Assert.Equal("https://example.com/a.png", video.Content[1].ImageURL!.URL);
        Assert.Equal("first_frame", video.Content[1].Role);
    }

    [Fact]
    public void TransformRequest_DefaultsTheContentTypeToJson()
    {
        var request = Transformer.TransformRequest(JsonRequest(
            """{"model":"m","content":[{"type":"text","text":"x"}]}""",
            contentType: null));

        Assert.Equal("m", request.Model);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("multipart/form-data")]
    public void TransformRequest_RejectsANonJsonContentType(string contentType)
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer.TransformRequest(JsonRequest(
                """{"model":"m","content":[]}""",
                contentType)));

        Assert.Equal("invalid request: unsupported content type: " + contentType, error.Message);
    }

    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("Application/JSON")]
    public void TransformRequest_AcceptsJsonContentTypeVariants(string contentType)
    {
        var request = Transformer.TransformRequest(JsonRequest(
            """{"model":"m","content":[{"type":"text","text":"x"}]}""",
            contentType));

        Assert.Equal("m", request.Model);
    }

    [Fact]
    public void TransformRequest_RejectsANilRequest()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer.TransformRequest(null));

        Assert.Equal("invalid request: http request is nil", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsAnEmptyBody()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer.TransformRequest(JsonRequest(string.Empty)));

        Assert.Equal("invalid request: request body is empty", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsAMissingModel()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer.TransformRequest(JsonRequest(
                """{"content":[{"type":"text","text":"x"}]}""")));

        Assert.Equal("invalid request: model is required", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsEmptyContent()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer.TransformRequest(JsonRequest(
                """{"model":"m","content":[]}""")));

        Assert.Equal("invalid request: content is required", error.Message);
    }

    [Fact]
    public void TransformRequest_ReportsMalformedBodies()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer.TransformRequest(JsonRequest("not-json")));

        Assert.StartsWith(
            "invalid request: failed to decode seedance video request: ",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransformRequest_ReportsARootTypeMismatch()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer.TransformRequest(JsonRequest("[]")));

        Assert.Equal(
            "invalid request: failed to decode seedance video request: "
            + "json: cannot unmarshal array into Go value of type doubao.seedanceCreateRequest",
            error.Message);
    }

    [Fact]
    public void TransformResponse_RendersTheCreateResponseAsOnlyTheId()
    {
        var response = Transformer.TransformResponse(new LlmResponse
        {
            Object = "video.create",
            RequestType = RequestTypes.Video,
            Video = new VideoResponse { ID = "task-1", Status = "queued" },
        });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("""{"id":"task-1"}""", Encoding.UTF8.GetString(response.Body!));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(response.Headers, "Content-Type"));
        Assert.Equal("no-cache", HttpClientUtils.HeaderGet(response.Headers, "Cache-Control"));
    }

    [Fact]
    public void TransformResponse_RendersTheSeedanceTask()
    {
        var response = Transformer.TransformResponse(new LlmResponse
        {
            RequestType = RequestTypes.Video,
            Usage = new Usage { CompletionTokens = 12, TotalTokens = 34 },
            Video = new VideoResponse
            {
                ID = "task-1",
                Model = "doubao-seedance-1-0-pro",
                Status = "succeeded",
                CreatedAt = 100,
                CompletedAt = 200,
                Seed = 7,
                Resolution = "720p",
                Ratio = "16:9",
                Duration = "4.6",
                VideoURL = "https://cdn.example.com/v.mp4",
                FPS = 24L,
            },
        });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("no-cache", HttpClientUtils.HeaderGet(response.Headers, "Cache-Control"));

        // Go struct declaration order is preserved; duration is rounded half
        // away from zero and service_tier is always omitted.
        Assert.Equal(
            """{"id":"task-1","model":"doubao-seedance-1-0-pro","status":"succeeded","content":{"video_url":"https://cdn.example.com/v.mp4"},"usage":{"completion_tokens":12,"total_tokens":34},"created_at":100,"updated_at":200,"seed":7,"resolution":"720p","ratio":"16:9","duration":5,"framespersecond":24}""",
            Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public void TransformResponse_DefaultsUpdatedAtToNow()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var response = Transformer.TransformResponse(new LlmResponse
        {
            RequestType = RequestTypes.Video,
            Video = new VideoResponse { ID = "task-1", Status = "queued" },
        });

        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = Body(response);

        Assert.InRange(body.GetProperty("updated_at").GetInt64(), before, after);
        Assert.False(body.TryGetProperty("created_at", out _));
        Assert.False(body.TryGetProperty("service_tier", out _));
    }

    [Fact]
    public void TransformResponse_RejectsANilVideo()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer.TransformResponse(new LlmResponse()));

        Assert.Equal("invalid response: video response is nil", error.Message);
    }

    [Fact]
    public void TransformResponse_RejectsANilResponse()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer.TransformResponse(null));

        Assert.Equal("invalid response: video response is nil", error.Message);
    }

    [Fact]
    public void TransformStream_RejectsStreaming()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer.TransformStream(null!));

        Assert.Equal("invalid request: video request does not support streaming", error.Message);
    }

    [Fact]
    public void AggregateStreamChunks_RejectsStreaming()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer.AggregateStreamChunks([], out _));

        Assert.Equal("invalid request: video request does not support streaming", error.Message);
    }

    [Fact]
    public void TransformError_DelegatesToTheOpenAiInbound()
    {
        var response = Transformer.TransformError(TransformerErrors.InvalidRequest("bad"));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal(
            """{"error":{"message":"invalid request: bad","type":"invalid_request_error"}}""",
            Encoding.UTF8.GetString(response.Body!));
    }
}
