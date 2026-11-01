using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Zenmux;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for llm/transformer/zenmux: the native video task create /
/// poll / delete surface, the size inference table and the delegation of every
/// non-video request to the embedded OpenAI outbound. Ported from
/// zenmux/zenmux_test.go and zenmux/size_test.go; the reference spins an
/// httptest server and inspects the captured request, the port asserts the
/// same method/path/auth on the built <see cref="HttpRequestData"/>.
/// </summary>
public sealed class ZenmuxOutboundTransformerTests
{
    private const string ServerUrl = "https://zenmux.test";

    private static readonly int[] NonObjectExtraBody = [1, 2];

    private static ZenmuxOutboundTransformer Transformer()
        => ZenmuxOutboundTransformer.Create(ServerUrl, "zenmux-test-key");

    private static LlmRequest NativeVideoRequest(string? duration = "5")
        => new()
        {
            Model = "provider/video",
            RequestType = RequestTypes.Video,
            Video = new VideoRequest
            {
                Content =
                [
                    new VideoContent { Type = "text", Text = "a cat reading" },
                    new VideoContent
                    {
                        Type = "image_url",
                        Role = "first_frame",
                        ImageURL = new VideoImageURL { URL = "https://example.invalid/first.png" },
                    },
                    new VideoContent
                    {
                        Type = "video_url",
                        Role = "reference_video",
                        VideoURL = new VideoURL { URL = "https://example.invalid/reference.mp4" },
                    },
                    new VideoContent
                    {
                        Type = "audio_url",
                        Role = "reference_audio",
                        AudioURL = new AudioURL { URL = "https://example.invalid/reference.mp3" },
                    },
                ],
                Resolution = "720p",
                Ratio = "16:9",
                Duration = duration,
                Seed = -1,
                GenerateAudio = true,
                Frames = 120,
                CameraFixed = false,
                Watermark = true,
                Draft = false,
                CallbackURL = "https://example.invalid/callback",
                ReturnLastFrame = true,
                Tools = [JsonSerializer.SerializeToElement(new { type = "web_search" })],
            },
        };

    private static LlmRequest InvalidContentRequest(VideoContent content)
    {
        var request = NativeVideoRequest();
        request.Video!.Content = [content];
        return request;
    }

    private static JsonElement Body(HttpRequestData request)
    {
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }

    private static HttpResponseData PollResponse(string body)
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
        };

    // ------------------------------------------------------------- create

    [Fact]
    public void TransformRequest_BuildsTheNativeCreateRequest()
    {
        var request = Transformer().TransformRequest(NativeVideoRequest());

        Assert.Equal("POST", request.Method);
        Assert.Equal(ServerUrl + "/v1/videos", request.Url);
        Assert.Equal(RequestTypes.Video, request.RequestType);
        Assert.Equal(ApiFormats.ZenmuxVideo, request.ApiFormat);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal("zenmux-test-key", request.Auth.ApiKey);
        Assert.Equal("provider/video", request.TransformerMetadata!["model"].GetString());

        // Go struct declaration order of nativeCreateRequest (video_types.go:18-34).
        Assert.Equal(
            """{"model":"provider/video","content":[{"type":"text","text":"a cat reading"},{"type":"image_url","role":"first_frame","image_url":{"url":"https://example.invalid/first.png"}},{"type":"video_url","role":"reference_video","video_url":{"url":"https://example.invalid/reference.mp4"}},{"type":"audio_url","role":"reference_audio","audio_url":{"url":"https://example.invalid/reference.mp3"}}],"resolution":"720p","ratio":"16:9","duration":5,"seed":-1,"generate_audio":true,"frames":120,"camera_fixed":false,"watermark":true,"draft":false,"callback_url":"https://example.invalid/callback","return_last_frame":true,"tools":[{"type":"web_search"}]}""",
            Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public void TransformRequest_ReportsTheSelectedKeyForVideo()
    {
        var transformer = Transformer();

        _ = transformer.TransformRequest(NativeVideoRequest());

        Assert.Equal("zenmux-test-key", transformer.SelectedApiKey);
    }

    [Theory]
    [InlineData("https://zenmux.test/api", "", "https://zenmux.test/api/v1/videos")]
    [InlineData("https://zenmux.test/api", "/custom/tasks", "https://zenmux.test/api/custom/tasks")]
    [InlineData("https://zenmux.test/raw##", "", "https://zenmux.test/raw/videos")]
    public void TransformRequest_NormalizesTheBaseUrl(
        string baseUrl,
        string endpointPath,
        string expectedUrl)
    {
        var transformer = new ZenmuxOutboundTransformer(new ZenmuxOutboundConfig
        {
            BaseUrl = baseUrl,
            EndpointPath = endpointPath,
            ApiKeyProvider = new StaticApiKeyProvider("zenmux-test-key"),
        });

        var request = transformer.TransformRequest(NativeVideoRequest());

        Assert.Equal(expectedUrl, request.Url);
    }

    [Fact]
    public void TransformRequest_UsesACustomEndpointPathForCreateAndPoll()
    {
        var transformer = new ZenmuxOutboundTransformer(new ZenmuxOutboundConfig
        {
            BaseUrl = ServerUrl,
            EndpointPath = "/custom/videos",
            ApiKeyProvider = new StaticApiKeyProvider("zenmux-test-key"),
        });

        var createRequest = transformer.TransformRequest(NativeVideoRequest());
        var pollRequest = transformer.BuildGetVideoTaskRequest("task-123");

        Assert.Equal(ServerUrl + "/custom/videos", createRequest.Url);
        Assert.Equal(ServerUrl + "/custom/videos/task-123", pollRequest.Url);
    }

    // --------------------------------------------------------------- size

    [Fact]
    public void TransformRequest_MapsTheOpenAiSquareSizeToNativeFields()
    {
        var request = NativeVideoRequest();
        request.Video!.Size = "256x256";
        request.Video.Ratio = string.Empty;
        request.Video.Resolution = string.Empty;

        var body = Body(Transformer().TransformRequest(request));

        Assert.Equal("1:1", body.GetProperty("ratio").GetString());
        Assert.Equal("480p", body.GetProperty("resolution").GetString());
        Assert.False(body.TryGetProperty("size", out _));
    }

    [Fact]
    public void TransformRequest_PreservesExplicitNativeFieldsOverTheSize()
    {
        var request = NativeVideoRequest();
        request.Video!.Size = "1920x1080";

        var body = Body(Transformer().TransformRequest(request));

        Assert.Equal("16:9", body.GetProperty("ratio").GetString());
        Assert.Equal("720p", body.GetProperty("resolution").GetString());
        Assert.False(body.TryGetProperty("size", out _));
    }

    [Fact]
    public void TransformRequest_RejectsAnUnmappableSize()
    {
        var request = NativeVideoRequest();
        request.Video!.Size = "257x257";
        request.Video.Ratio = string.Empty;
        request.Video.Resolution = string.Empty;

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal(
            "invalid request: size \"257x257\" cannot be mapped to ratio/resolution, please set ratio and resolution",
            error.Message);
    }

    // --------------------------------------------------------- validation

    [Theory]
    [InlineData("", "invalid request: model is required")]
    [InlineData("0", "invalid request: duration must be a positive integer")]
    [InlineData("-1", "invalid request: duration must be a positive integer")]
    [InlineData("1.5", "invalid request: duration must be a positive integer")]
    [InlineData("five", "invalid request: duration must be a positive integer")]
    public void TransformRequest_RejectsInvalidScalarFields(string duration, string expected)
    {
        var request = NativeVideoRequest(duration == "" ? string.Empty : duration);
        if (duration == "")
        {
            request.Model = string.Empty;
        }

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal(expected, error.Message);
        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
    }

    [Fact]
    public void TransformRequest_RejectsAnEmptyContentList()
    {
        var request = NativeVideoRequest();
        request.Video!.Content = [];

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal("invalid request: content is required", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsAServiceTier()
    {
        var request = NativeVideoRequest();
        request.Video!.ServiceTier = "default";

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal(
            "invalid request: service_tier is unsupported for ZenMux video",
            error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsAnExecutionExpiry()
    {
        var request = NativeVideoRequest();
        request.Video!.ExecutionExpiresAfter = 60;

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal(
            "invalid request: execution_expires_after is unsupported for ZenMux video",
            error.Message);
    }

    [Theory]
    [InlineData("text-with-image", "invalid request: invalid text content at index 0")]
    [InlineData("text-empty", "invalid request: invalid text content at index 0")]
    [InlineData("image-with-text", "invalid request: invalid image_url content at index 0")]
    [InlineData("image-empty-url", "invalid request: invalid image_url content at index 0")]
    [InlineData("image-invalid-role", "invalid request: invalid image_url content at index 0")]
    [InlineData("video-missing-role", "invalid request: invalid video_url content at index 0")]
    [InlineData("video-empty-url", "invalid request: invalid video_url content at index 0")]
    [InlineData("video-invalid-role", "invalid request: invalid video_url content at index 0")]
    [InlineData("video-with-mime", "invalid request: invalid video_url content at index 0")]
    [InlineData("audio-missing-role", "invalid request: invalid audio_url content at index 0")]
    [InlineData("audio-empty-url", "invalid request: invalid audio_url content at index 0")]
    [InlineData("audio-invalid-role", "invalid request: invalid audio_url content at index 0")]
    [InlineData("audio-with-image", "invalid request: invalid audio_url content at index 0")]
    [InlineData("unknown-type", "invalid request: unsupported content type \"unknown\"")]
    public void TransformRequest_RejectsInvalidContent(string kind, string expected)
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(InvalidContentRequest(kind switch
            {
                "text-with-image" => new VideoContent
                {
                    Type = "text",
                    Text = "prompt",
                    ImageURL = new VideoImageURL { URL = "https://example.invalid/image.png" },
                },
                "text-empty" => new VideoContent { Type = "text" },
                "image-with-text" => new VideoContent
                {
                    Type = "image_url",
                    Text = "conflicting",
                    ImageURL = new VideoImageURL { URL = "https://example.invalid/image.png" },
                },
                "image-empty-url" => new VideoContent
                {
                    Type = "image_url",
                    ImageURL = new VideoImageURL(),
                },
                "image-invalid-role" => new VideoContent
                {
                    Type = "image_url",
                    Role = "reference_video",
                    ImageURL = new VideoImageURL { URL = "https://example.invalid/image.png" },
                },
                "video-missing-role" => new VideoContent
                {
                    Type = "video_url",
                    VideoURL = new VideoURL { URL = "https://example.invalid/video.mp4" },
                },
                "video-empty-url" => new VideoContent
                {
                    Type = "video_url",
                    Role = "reference_video",
                    VideoURL = new VideoURL(),
                },
                "video-invalid-role" => new VideoContent
                {
                    Type = "video_url",
                    Role = "reference_audio",
                    VideoURL = new VideoURL { URL = "https://example.invalid/video.mp4" },
                },
                "video-with-mime" => new VideoContent
                {
                    Type = "video_url",
                    Role = "reference_video",
                    VideoURL = new VideoURL
                    {
                        URL = "https://example.invalid/video.mp4",
                        MIMEType = "video/mp4",
                    },
                },
                "audio-missing-role" => new VideoContent
                {
                    Type = "audio_url",
                    AudioURL = new AudioURL { URL = "https://example.invalid/audio.mp3" },
                },
                "audio-empty-url" => new VideoContent
                {
                    Type = "audio_url",
                    Role = "reference_audio",
                    AudioURL = new AudioURL(),
                },
                "audio-invalid-role" => new VideoContent
                {
                    Type = "audio_url",
                    Role = "reference_video",
                    AudioURL = new AudioURL { URL = "https://example.invalid/audio.mp3" },
                },
                "audio-with-image" => new VideoContent
                {
                    Type = "audio_url",
                    Role = "reference_audio",
                    AudioURL = new AudioURL { URL = "https://example.invalid/audio.mp3" },
                    ImageURL = new VideoImageURL { URL = "https://example.invalid/image.png" },
                },
                _ => new VideoContent { Type = "unknown" },
            })));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsAVideoRequestWithoutVideo()
    {
        var request = new LlmRequest
        {
            Model = "provider/video",
            RequestType = RequestTypes.Video,
        };

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal("invalid request: video request is required", error.Message);
    }

    // ----------------------------------------------------------- extra body

    [Fact]
    public void TransformRequest_PreservesOpaqueExtraBodyFields()
    {
        var request = NativeVideoRequest();
        request.ExtraBody = JsonSerializer.SerializeToElement(new { camera_motion = "fixed" });

        var bodyBytes = Transformer().TransformRequest(request).Body!;

        // Go re-marshals the merged map: keys sorted, values compacted.
        Assert.Equal(
            """{"callback_url":"https://example.invalid/callback","camera_fixed":false,"camera_motion":"fixed","content":[{"type":"text","text":"a cat reading"},{"type":"image_url","role":"first_frame","image_url":{"url":"https://example.invalid/first.png"}},{"type":"video_url","role":"reference_video","video_url":{"url":"https://example.invalid/reference.mp4"}},{"type":"audio_url","role":"reference_audio","audio_url":{"url":"https://example.invalid/reference.mp3"}}],"draft":false,"duration":5,"frames":120,"generate_audio":true,"model":"provider/video","ratio":"16:9","resolution":"720p","return_last_frame":true,"seed":-1,"tools":[{"type":"web_search"}],"watermark":true}""",
            Encoding.UTF8.GetString(bodyBytes));
    }

    [Theory]
    [InlineData("""{"service_tier":"default"}""", "invalid request: service_tier is unsupported for ZenMux video")]
    [InlineData("""{"execution_expires_after":60}""", "invalid request: execution_expires_after is unsupported for ZenMux video")]
    public void TransformRequest_RejectsUnsupportedExtraBodyFields(string extraBody, string expected)
    {
        var request = NativeVideoRequest();
        using var document = JsonDocument.Parse(extraBody);
        request.ExtraBody = document.RootElement.Clone();

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsANonObjectExtraBody()
    {
        var request = NativeVideoRequest();
        request.ExtraBody = JsonSerializer.SerializeToElement(NonObjectExtraBody);

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal("invalid request: extra_body must be a JSON object", error.Message);
    }

    // ------------------------------------------------------------- polling

    [Fact]
    public void BuildGetVideoTaskRequest_BuildsTheNativePoll()
    {
        var request = Transformer().BuildGetVideoTaskRequest("task-123");

        Assert.Equal("GET", request.Method);
        Assert.Equal(ServerUrl + "/v1/videos/task-123", request.Url);
        Assert.Equal(ApiFormats.ZenmuxVideo, request.ApiFormat);
        Assert.Equal(RequestTypes.Video, request.RequestType);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal("zenmux-test-key", request.Auth.ApiKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildGetVideoTaskRequest_RejectsABlankTaskId(string taskId)
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().BuildGetVideoTaskRequest(taskId));

        Assert.Equal("invalid request: providerTaskID is required", error.Message);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("succeeded")]
    public void ParseGetVideoTaskResponse_NormalizesTheStatus(string status)
    {
        var response = Transformer().ParseGetVideoTaskResponse(
            PollResponse("""{"id":"task-123","status":"  """ + status.ToUpperInvariant()
                + """  ","model":"provider/video"}"""));

        Assert.Equal(status, response.Video!.Status);
        Assert.Equal("task-123", response.Video.ID);
        Assert.Equal("provider/video", response.Video.Model);
        Assert.Equal(RequestTypes.Video, response.RequestType);
        Assert.Equal(ApiFormats.ZenmuxVideo, response.ApiFormat);
        Assert.Empty(response.Choices!);
    }

    [Fact]
    public void ParseGetVideoTaskResponse_ExposesTheNestedContent()
    {
        var response = Transformer().ParseGetVideoTaskResponse(PollResponse(
            """{"id":"task-123","status":"succeeded","model":"provider/video","content":{"video_url":"https://example.invalid/video.mp4","last_frame_url":"https://example.invalid/last.jpg","provider_metadata":{"request_id":"native-1"}}}"""));

        var video = response.Video!;
        Assert.Equal("succeeded", video.Status);
        Assert.Equal("https://example.invalid/video.mp4", video.VideoURL);
        Assert.Equal("https://example.invalid/last.jpg", video.LastFrameURL);
        Assert.Equal(
            """{"video_url":"https://example.invalid/video.mp4","last_frame_url":"https://example.invalid/last.jpg","provider_metadata":{"request_id":"native-1"}}""",
            video.Content!.Value.GetRawText());
    }

    [Fact]
    public void ParseGetVideoTaskResponse_KeepsAFailedTaskStructured()
    {
        var response = Transformer().ParseGetVideoTaskResponse(PollResponse(
            """{"id":"task-123","status":"failed","model":"provider/video","error":{"code":"generation_failed","message":"provider rejected the model"}}"""));

        Assert.Equal("failed", response.Video!.Status);
        Assert.Equal("generation_failed", response.Video.Error!.Code);
        Assert.Equal("provider rejected the model", response.Video.Error.Message);
    }

    [Fact]
    public void ParseGetVideoTaskResponse_RejectsANilResponse()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().ParseGetVideoTaskResponse(null));

        Assert.Equal("invalid response: http response is nil", error.Message);
        Assert.Equal(TransformerErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public void ParseGetVideoTaskResponse_RejectsAMalformedBody()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().ParseGetVideoTaskResponse(PollResponse("""{"id":""")));

        Assert.StartsWith(
            "invalid response: failed to unmarshal ZenMux video poll response: ",
            error.Message,
            StringComparison.Ordinal);
        Assert.Equal(TransformerErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public void ParseGetVideoTaskResponse_RejectsAMissingId()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().ParseGetVideoTaskResponse(PollResponse("""{"status":"queued"}""")));

        Assert.Equal("invalid response: missing id in ZenMux video response", error.Message);
    }

    [Theory]
    [InlineData("""{"id":"task-123","status":"cancelled"}""", "invalid response: unsupported ZenMux video status \"cancelled\"")]
    [InlineData("""{"id":"task-123","status":" cancelled "}""", "invalid response: unsupported ZenMux video status \" cancelled \"")]
    public void ParseGetVideoTaskResponse_RejectsAnUnknownStatus(string body, string expected)
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().ParseGetVideoTaskResponse(PollResponse(body)));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void ParseGetVideoTaskResponse_RejectsANonObjectBody()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().ParseGetVideoTaskResponse(PollResponse("[]")));

        Assert.Equal(
            "invalid response: failed to unmarshal ZenMux video poll response: json: cannot unmarshal array into Go value of type zenmux.nativeVideoResponse",
            error.Message);
    }

    [Fact]
    public void ParseGetVideoTaskResponse_RejectsNonObjectContent()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().ParseGetVideoTaskResponse(
                PollResponse("""{"id":"task-123","status":"queued","content":5}""")));

        Assert.Equal(
            "invalid response: invalid ZenMux video content: json: cannot unmarshal number into Go value of type zenmux.nativeVideoContent",
            error.Message);
    }

    // -------------------------------------------------------------- delete

    [Fact]
    public void BuildDeleteVideoTaskRequest_IsUnsupported()
    {
        var error = Assert.Throws<ZenmuxVideoTaskDeleteUnsupportedException>(
            () => Transformer().BuildDeleteVideoTaskRequest("task-123"));

        Assert.Equal("zenmux video task deletion is unsupported", error.Message);
    }

    // ------------------------------------------------------ create response

    [Fact]
    public void TransformResponse_MapsTheCreateResponse()
    {
        var transformer = Transformer();
        var createRequest = transformer.TransformRequest(NativeVideoRequest());
        var response = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 202,
            Body = Encoding.UTF8.GetBytes(
                """{"id":"task-123","status":"queued","model":"provider/video"}"""),
            Request = createRequest,
        });

        Assert.Equal("task-123", response.Id);
        Assert.Equal("video", response.Object);
        Assert.Equal("provider/video", response.Model);
        Assert.Equal(RequestTypes.Video, response.RequestType);
        Assert.Equal(ApiFormats.ZenmuxVideo, response.ApiFormat);
        Assert.Empty(response.Choices!);

        var video = response.Video!;
        Assert.Equal("task-123", video.ID);
        Assert.Equal("queued", video.Status);
        Assert.Equal("provider/video", video.Model);
    }

    [Fact]
    public void TransformResponse_FallsBackToTheRequestModel()
    {
        var transformer = Transformer();
        var createRequest = transformer.TransformRequest(NativeVideoRequest());

        var response = transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 202,
            Body = Encoding.UTF8.GetBytes("""{"id":"task-123","status":"queued"}"""),
            Request = createRequest,
        });

        Assert.Equal("provider/video", response.Model);
        Assert.Equal("provider/video", response.Video!.Model);
    }

    [Fact]
    public void TransformResponse_DoesNotMapMissingId()
    {
        var transformer = Transformer();
        var createRequest = transformer.TransformRequest(NativeVideoRequest());

        var error = Assert.Throws<TransformerException>(
            () => transformer.TransformResponse(new HttpResponseData
            {
                StatusCode = 202,
                Body = Encoding.UTF8.GetBytes("""{"status":"queued"}"""),
                Request = createRequest,
            }));

        Assert.Equal(
            "invalid response: missing id in ZenMux video response",
            error.Message);
    }

    [Fact]
    public void TransformResponse_DelegatesNonZenmuxResponsesToOpenAi()
    {
        var response = Transformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """{"id":"chat-1","object":"chat.completion","created":1,"model":"provider/chat","choices":[]}"""),
            Request = new HttpRequestData
            {
                RequestType = RequestTypes.Chat,
                ApiFormat = ApiFormats.OpenAIChatCompletion,
            },
        });

        Assert.Equal("chat-1", response.Id);
        Assert.Equal("provider/chat", response.Model);
        Assert.Null(response.Video);
    }

    // ---------------------------------------------------------- delegation

    [Fact]
    public void TransformRequest_DelegatesNonVideoRequestsToOpenAi()
    {
        var request = new LlmRequest
        {
            Model = "provider/chat",
            RequestType = RequestTypes.Chat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hello") }],
        };

        var providerRequest = Transformer().TransformRequest(request);

        Assert.Equal("POST", providerRequest.Method);
        Assert.Equal(ServerUrl + "/v1/chat/completions", providerRequest.Url);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, providerRequest.ApiFormat);
    }

    [Fact]
    public void SelectedApiKeyFallsBackToTheDelegatedChatKey()
    {
        var transformer = Transformer();

        _ = transformer.TransformRequest(new LlmRequest
        {
            Model = "provider/chat",
            RequestType = RequestTypes.Chat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hello") }],
        });

        Assert.Equal("zenmux-test-key", transformer.SelectedApiKey);
    }

    // ---------------------------------------------------------- constructor

    [Fact]
    public void Constructor_RejectsANilConfig()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new ZenmuxOutboundTransformer(null));

        Assert.Equal(
            "invalid ZenMux transformer configuration: config is nil",
            error.Message);
    }

    [Fact]
    public void Constructor_RequiresAKeyProvider()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new ZenmuxOutboundTransformer(new ZenmuxOutboundConfig
            {
                BaseUrl = ServerUrl,
            }));

        Assert.Equal(
            "API key provider is required for ZenMux transformer",
            error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RequiresABaseUrl(string baseUrl)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new ZenmuxOutboundTransformer(new ZenmuxOutboundConfig
            {
                BaseUrl = baseUrl,
                ApiKeyProvider = new StaticApiKeyProvider("key"),
            }));

        Assert.Equal("base URL is required for ZenMux transformer", error.Message);
    }
}
