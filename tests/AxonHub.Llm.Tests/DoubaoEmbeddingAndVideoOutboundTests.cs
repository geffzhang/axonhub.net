using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Doubao;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for llm/transformer/doubao/embedding.go and video_outbound.go:
/// the multimodal embedding wire shape, the Seedance task create/get/delete
/// requests and the asynchronous task response conversion.
/// </summary>
public sealed class DoubaoEmbeddingAndVideoOutboundTests
{
    private const string BaseUrl = "https://ark.cn-beijing.volces.com/api/v3";

    private static readonly double[] ExpectedEmbeddingValues = [0.1, 0.2];

    private static DoubaoOutboundTransformer Transformer()
        => DoubaoOutboundTransformer.Create(BaseUrl, "test-api-key");

    private static JsonElement Body(HttpRequestData request)
    {
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }

    private static LlmRequest EmbeddingRequest(EmbeddingInput input)
        => new()
        {
            Model = "doubao-embedding-vision",
            RequestType = RequestTypes.Embedding,
            ApiFormat = ApiFormats.OpenAIEmbedding,
            Embedding = new EmbeddingRequest
            {
                Input = input,
                EncodingFormat = "float",
                Dimensions = 1024,
            },
        };

    // -------------------------------------------------------------- embedding

    [Fact]
    public void TransformRequest_BuildsTheMultimodalEmbeddingRequest()
    {
        var request = Transformer().TransformRequest(
            EmbeddingRequest(new EmbeddingInput { String = "hello" }));

        Assert.Equal("POST", request.Method);
        Assert.Equal(BaseUrl + "/embeddings/multimodal", request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal(RequestTypes.Embedding, request.RequestType);
        Assert.Equal(ApiFormats.OpenAIEmbedding, request.ApiFormat);
        Assert.Equal("test-api-key", request.Auth!.ApiKey);

        Assert.Equal(
            """{"model":"doubao-embedding-vision","input":[{"type":"text","text":"hello"}],"encoding_format":"float","dimensions":1024}""",
            Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public void TransformRequest_MapsAStringArrayToTextItems()
    {
        var request = Transformer().TransformRequest(
            EmbeddingRequest(new EmbeddingInput { StringArray = ["a", "b"] }));

        var input = Body(request).GetProperty("input");

        Assert.Equal(2, input.GetArrayLength());
        Assert.Equal("text", input[0].GetProperty("type").GetString());
        Assert.Equal("a", input[0].GetProperty("text").GetString());
        Assert.Equal("b", input[1].GetProperty("text").GetString());
    }

    [Fact]
    public void TransformRequest_SendsNoInputItemsForTokenInput()
    {
        // buildMultimodalInput only understands the string shapes.
        var request = Transformer().TransformRequest(
            EmbeddingRequest(new EmbeddingInput { IntArray = [1, 2, 3] }));

        Assert.Equal(0, Body(request).GetProperty("input").GetArrayLength());
    }

    [Fact]
    public void TransformRequest_RejectsAMissingEmbeddingRequest()
    {
        var request = new LlmRequest
        {
            Model = "doubao-embedding-vision",
            RequestType = RequestTypes.Embedding,
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal("embedding request is nil in llm.Request", error.Message);
    }

    [Fact]
    public void TransformResponse_MapsTheMultimodalEmbeddingResponse()
    {
        var response = EmbeddingResponse(
            """{"id":"emb-1","object":"list","data":{"object":"embedding","embedding":[0.1,0.2]},"model":"doubao-embedding-vision","usage":{"prompt_tokens":3,"total_tokens":5,"prompt_tokens_details":{"text_tokens":1,"image_tokens":2}}}""");

        var result = Transformer().TransformResponse(response);

        Assert.Equal(RequestTypes.Embedding, result.RequestType);
        Assert.Equal(ApiFormats.OpenAIEmbedding, result.ApiFormat);
        Assert.Equal("doubao-embedding-vision", result.Model);

        var embedding = result.Embedding!;
        Assert.Equal("emb-1", embedding.Id);
        Assert.Equal("list", embedding.Object);
        var data = Assert.Single(embedding.Data!);
        Assert.Equal("embedding", data.Object);
        Assert.Equal(0, data.Index);
        Assert.Equal(ExpectedEmbeddingValues, data.Embedding.Values!);

        Assert.Equal(3, result.Usage!.PromptTokens);
        Assert.Equal(5, result.Usage.TotalTokens);
        Assert.Equal(1, result.Usage.PromptTokensDetails!.TextTokens);
        Assert.Equal(2, result.Usage.PromptTokensDetails.ImageTokens);
    }

    [Fact]
    public void TransformResponse_KeepsABase64Embedding()
    {
        var response = EmbeddingResponse(
            """{"id":"emb-2","object":"list","data":{"object":"embedding","embedding":"QUJD"},"model":"m"}""");

        var data = Assert.Single(Transformer().TransformResponse(response).Embedding!.Data!);

        Assert.Equal("QUJD", data.Embedding.Base64);
        Assert.Null(data.Embedding.Values);
    }

    [Fact]
    public void TransformResponse_OmitsTheIdAndUsageWhenAbsent()
    {
        var response = EmbeddingResponse(
            """{"object":"list","data":{"object":"embedding","embedding":[1]}}""");

        var result = Transformer().TransformResponse(response);

        Assert.Null(result.Embedding!.Id);
        Assert.Null(result.Usage);
    }

    [Fact]
    public void TransformResponse_RejectsAnEmptyEmbeddingBody()
    {
        var response = EmbeddingResponse(string.Empty);
        response.Body = [];

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(response));

        Assert.Equal("response body is empty", error.Message);
    }

    [Fact]
    public void TransformResponse_ReportsHttpEmbeddingErrors()
    {
        var response = EmbeddingResponse("""{"error":{"message":"bad"}}""");
        response.StatusCode = 400;

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(response));

        Assert.Equal("HTTP error 400", error.Message);
    }

    [Fact]
    public void TransformResponse_ReportsMalformedEmbeddingBodies()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(EmbeddingResponse("not-json")));

        Assert.StartsWith("failed to unmarshal embedding response: ", error.Message, StringComparison.Ordinal);
    }

    private static HttpResponseData EmbeddingResponse(string body)
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
            Request = new HttpRequestData
            {
                RequestType = RequestTypes.Embedding,
                ApiFormat = ApiFormats.OpenAIEmbedding,
            },
        };

    // ------------------------------------------------------------------ video

    private static LlmRequest VideoRequest(
        string model = "doubao-seedance-1-0-pro",
        string? ratio = "16:9",
        string? resolution = "720p",
        string? size = null,
        string? duration = "3.6",
        string? serviceTier = "flex")
        => new()
        {
            Model = model,
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.SeedanceVideo,
            Video = new VideoRequest
            {
                Content = [new VideoContent { Type = "text", Text = "a cat" }],
                Ratio = ratio,
                Resolution = resolution,
                Size = size,
                Duration = duration,
                Frames = 121,
                Seed = 7,
                GenerateAudio = false,
                CameraFixed = true,
                Watermark = true,
                Draft = true,
                ServiceTier = serviceTier,
                ExecutionExpiresAfter = 600,
            },
        };

    [Fact]
    public void TransformRequest_BuildsTheSeedanceTaskBody()
    {
        var request = Transformer().TransformRequest(VideoRequest());

        Assert.Equal("POST", request.Method);
        Assert.Equal(BaseUrl + "/contents/generations/tasks", request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));
        Assert.Equal(RequestTypes.Video, request.RequestType);
        Assert.Equal(ApiFormats.SeedanceVideo, request.ApiFormat);
        Assert.Equal("test-api-key", request.Auth!.ApiKey);
        Assert.Equal(
            "doubao-seedance-1-0-pro",
            request.TransformerMetadata!["model"].GetString());

        // A Go map marshals with sorted keys; duration is rounded half away
        // from zero (math.Round).
        Assert.Equal(
            """{"camera_fixed":true,"content":[{"type":"text","text":"a cat"}],"draft":true,"duration":4,"execution_expires_after":600,"frames":121,"generate_audio":false,"model":"doubao-seedance-1-0-pro","ratio":"16:9","resolution":"720p","seed":7,"service_tier":"flex","watermark":true}""",
            Encoding.UTF8.GetString(request.Body!));
    }

    [Theory]
    [InlineData("3.4", 3)]
    [InlineData("3.5", 4)]
    [InlineData(" 8 ", 8)]
    [InlineData("not-a-number", null)]
    public void TransformRequest_RoundsTheDuration(string duration, int? expected)
    {
        var body = Body(Transformer().TransformRequest(VideoRequest(duration: duration)));

        if (expected is null)
        {
            Assert.False(body.TryGetProperty("duration", out _));
            return;
        }

        Assert.Equal(expected.Value, body.GetProperty("duration").GetInt64());
    }

    [Theory]
    [InlineData("1280x720", "16:9", "720p")]
    [InlineData("720x1280", "9:16", "720p")]
    [InlineData("1920x1080", "16:9", "1080p")]
    [InlineData("1080x1920", "9:16", "1080p")]
    [InlineData("640x480", "4:3", "480p")]
    [InlineData("480x640", "3:4", "480p")]
    [InlineData(" 1280X720 ", "16:9", "720p")]
    public void TransformRequest_InfersRatioAndResolutionFromSize(
        string size,
        string expectedRatio,
        string expectedResolution)
    {
        var body = Body(Transformer().TransformRequest(
            VideoRequest(ratio: null, resolution: null, size: size)));

        Assert.Equal(expectedRatio, body.GetProperty("ratio").GetString());
        Assert.Equal(expectedResolution, body.GetProperty("resolution").GetString());
    }

    [Fact]
    public void TransformRequest_RejectsAnUnmappableSize()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(
                VideoRequest(ratio: null, resolution: null, size: "1024x1024")));

        Assert.Equal(
            "invalid request: size \"1024x1024\" cannot be mapped to ratio/resolution, please set ratio and resolution",
            error.Message);
    }

    [Fact]
    public void TransformRequest_LeavesAnExplicitResolutionAlone()
    {
        var body = Body(Transformer().TransformRequest(
            VideoRequest(ratio: null, resolution: "1080p", size: "1024x1024")));

        Assert.False(body.TryGetProperty("ratio", out _));
        Assert.Equal("1080p", body.GetProperty("resolution").GetString());
    }

    [Fact]
    public void TransformRequest_TrimsRatioResolutionAndServiceTierPresence()
    {
        var body = Body(Transformer().TransformRequest(
            VideoRequest(ratio: " 16:9 ", resolution: " 720p ", serviceTier: "  ")));

        Assert.Equal("16:9", body.GetProperty("ratio").GetString());
        Assert.Equal("720p", body.GetProperty("resolution").GetString());
        Assert.False(body.TryGetProperty("service_tier", out _));
    }

    [Fact]
    public void TransformRequest_RejectsAVideoRequestWithoutVideo()
    {
        var request = new LlmRequest
        {
            Model = "doubao-seedance-1-0-pro",
            RequestType = RequestTypes.Video,
        };

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal("invalid request: video request is required", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsABlankVideoModel()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(VideoRequest(model: "  ")));

        Assert.Equal("invalid request: model is required", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsEmptyVideoContent()
    {
        var request = VideoRequest();
        request.Video!.Content = [];

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal("invalid request: content is required", error.Message);
    }

    [Fact]
    public void BuildGetVideoTaskRequest_BuildsTheTaskQuery()
    {
        var request = Transformer().BuildGetVideoTaskRequest("task-1");

        Assert.Equal("GET", request.Method);
        Assert.Equal(BaseUrl + "/contents/generations/tasks/task-1", request.Url);
        Assert.Equal(ApiFormats.SeedanceVideo, request.ApiFormat);
        Assert.Equal(RequestTypes.Video, request.RequestType);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal("test-api-key", request.Auth.ApiKey);
    }

    [Fact]
    public void BuildDeleteVideoTaskRequest_BuildsTheTaskDelete()
    {
        var request = Transformer().BuildDeleteVideoTaskRequest("task-1");

        Assert.Equal("DELETE", request.Method);
        Assert.Equal(BaseUrl + "/contents/generations/tasks/task-1", request.Url);
        Assert.Equal(ApiFormats.SeedanceVideo, request.ApiFormat);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void VideoTaskRequests_RejectABlankProviderTaskId(string taskId)
    {
        var getError = Assert.Throws<TransformerException>(
            () => Transformer().BuildGetVideoTaskRequest(taskId));
        Assert.Equal("invalid request: providerTaskID is required", getError.Message);

        var deleteError = Assert.Throws<TransformerException>(
            () => Transformer().BuildDeleteVideoTaskRequest(taskId));
        Assert.Equal("invalid request: providerTaskID is required", deleteError.Message);
    }

    [Fact]
    public void ParseGetVideoTaskResponse_MapsTheSeedanceTask()
    {
        var result = Transformer().ParseGetVideoTaskResponse(TaskResponse(
            """{"id":"task-1","model":"doubao-seedance-1-0-pro","status":"Succeeded","content":{"video_url":"https://cdn.example.com/v.mp4"},"usage":{"completion_tokens":12,"total_tokens":34},"created_at":100,"updated_at":200,"seed":7,"resolution":"720p","ratio":"16:9","duration":5,"framespersecond":24}"""));

        Assert.Equal(RequestTypes.Video, result.RequestType);
        var video = result.Video!;
        Assert.Equal("task-1", video.ID);
        Assert.Equal("succeeded", video.Status);
        Assert.Equal("doubao-seedance-1-0-pro", video.Model);
        Assert.Equal("https://cdn.example.com/v.mp4", video.VideoURL);
        Assert.Equal(100, video.CreatedAt);
        Assert.Equal(200, video.CompletedAt);
        Assert.Equal(7, video.Seed);
        Assert.Equal("720p", video.Resolution);
        Assert.Equal("16:9", video.Ratio);
        Assert.Equal("5", video.Duration);
        Assert.Equal(24, video.FPS);

        Assert.Equal(12, result.Usage!.CompletionTokens);
        Assert.Equal(34, result.Usage.TotalTokens);
    }

    [Fact]
    public void ParseGetVideoTaskResponse_DefaultsTheStatusAndCompletionTime()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var succeeded = Transformer().ParseGetVideoTaskResponse(TaskResponse(
            """{"id":"task-1","status":" succeeded "}""")).Video!;

        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Equal("succeeded", succeeded.Status);
        Assert.InRange(succeeded.CompletedAt, before, after);

        var queued = Transformer().ParseGetVideoTaskResponse(TaskResponse(
            """{"id":"task-2"}""")).Video!;

        Assert.Equal("queued", queued.Status);
        Assert.Equal(0, queued.CompletedAt);
    }

    [Fact]
    public void ParseGetVideoTaskResponse_OmitsEmptyOptionalFields()
    {
        var video = Transformer().ParseGetVideoTaskResponse(TaskResponse(
            """{"id":"task-1","status":"running"}""")).Video!;

        Assert.Null(video.Model);
        Assert.Null(video.VideoURL);
        Assert.Null(video.Duration);
        Assert.Null(video.Ratio);
        Assert.Null(video.Resolution);
    }

    [Fact]
    public void ParseGetVideoTaskResponse_RejectsAMissingId()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().ParseGetVideoTaskResponse(TaskResponse("""{"status":"queued"}""")));

        Assert.Equal("invalid response: missing id in seedance response", error.Message);
    }

    [Fact]
    public void ParseGetVideoTaskResponse_RejectsANilResponse()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().ParseGetVideoTaskResponse(null));

        Assert.Equal("invalid response: http response is nil", error.Message);
    }

    [Fact]
    public void ParseGetVideoTaskResponse_ReportsMalformedBodies()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().ParseGetVideoTaskResponse(TaskResponse("not-json")));

        Assert.StartsWith(
            "failed to unmarshal seedance video response: ",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransformResponse_MapsTheSeedanceCreateResponse()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes("""{"id":"task-1"}"""),
            Request = new HttpRequestData
            {
                RequestType = RequestTypes.Video,
                ApiFormat = ApiFormats.SeedanceVideo,
                TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["model"] = JsonSerializer.SerializeToElement("doubao-seedance-1-0-pro"),
                },
            },
        };

        var result = Transformer().TransformResponse(response);

        Assert.Equal("task-1", result.Id);
        Assert.Equal("video.create", result.Object);
        Assert.Equal(RequestTypes.Video, result.RequestType);
        Assert.Equal(ApiFormats.SeedanceVideo, result.ApiFormat);
        Assert.Empty(result.Choices!);
        Assert.Equal("doubao-seedance-1-0-pro", result.Model);

        var video = result.Video!;
        Assert.Equal("task-1", video.ID);
        Assert.Equal("queued", video.Status);
        Assert.Equal("doubao-seedance-1-0-pro", video.Model);
        Assert.NotEqual(0, video.CreatedAt);
    }

    [Fact]
    public void TransformResponse_OmitsTheCreateModelWhenTheMetadataIsMissing()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes("""{"id":"task-1"}"""),
            Request = new HttpRequestData { RequestType = RequestTypes.Video },
        };

        var result = Transformer().TransformResponse(response);

        Assert.Equal(string.Empty, result.Model);
        Assert.Null(result.Video!.Model);
    }

    [Fact]
    public void TransformResponse_RejectsACreateResponseWithoutId()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes("""{"id":"  "}"""),
            Request = new HttpRequestData { RequestType = RequestTypes.Video },
        };

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformResponse(response));

        Assert.Equal("invalid response: missing id in seedance create response", error.Message);
    }

    [Fact]
    public void TransformResponse_ReportsMalformedCreateBodies()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes("not-json"),
            Request = new HttpRequestData { RequestType = RequestTypes.Video },
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(response));

        Assert.StartsWith(
            "failed to unmarshal seedance create response: ",
            error.Message,
            StringComparison.Ordinal);
    }

    private static HttpResponseData TaskResponse(string body)
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
        };
}
