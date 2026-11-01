using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Modelscope;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for llm/transformer/modelscope (outbound.go, image.go and
/// image_test.go): chat delegation, the async image task wire shape, polling,
/// key reuse, output-image parsing and base64 materialization.
/// </summary>
public sealed class ModelscopeOutboundTransformerTests
{
    private const string BaseUrl = "https://example.com/v1";

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private sealed class RotatingKeyProvider(params string[] keys) : IApiKeyProvider
    {
        private int _next;

        public string Get() => keys[_next++ % keys.Length];
    }

    private static ModelscopeOutboundTransformer Transformer(
        IApiKeyProvider? keys = null,
        IExecutor? httpClient = null,
        string baseUrl = BaseUrl,
        TimeSpan? pollInterval = null,
        TimeSpan? taskTimeout = null)
        => new(new ModelscopeOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = keys ?? new StaticApiKeyProvider("test-key"),
            HttpClient = httpClient,
            PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1),
            TaskTimeout = taskTimeout,
        });

    private static LlmRequest ImageRequest(
        string? prompt = "a fox",
        string? size = null,
        string? apiFormat = ApiFormats.OpenAIImageGeneration,
        List<byte[]>? images = null,
        byte[]? mask = null,
        string model = "Qwen/Qwen-Image-2.1")
        => new()
        {
            Model = model,
            RequestType = RequestTypes.Image,
            ApiFormat = apiFormat,
            Image = new ImageRequest
            {
                Prompt = prompt,
                Size = size,
                Images = images,
                Mask = mask,
            },
        };

    private static HttpResponseData TaskResponse(
        string body,
        HttpRequestData request,
        CancellationToken cancellationToken = default)
    {
        request.RequestCancellationToken = cancellationToken;
        return new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(body),
            Request = request,
        };
    }

    private static JsonElement Body(HttpRequestData request)
    {
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }

    // Reference TestBuildImageRequest_GenerationOmitsImageURL.
    [Fact]
    public void TransformRequest_BuildsTheImageGenerationSubmission()
    {
        var httpRequest = Transformer().TransformRequest(ImageRequest(size: "auto"));

        Assert.Equal("POST", httpRequest.Method);
        Assert.Equal("https://example.com/v1/images/generations", httpRequest.Url);
        Assert.Equal("true", HttpClientUtils.HeaderGet(httpRequest.Headers, "X-ModelScope-Async-Mode"));
        Assert.Equal(ApiFormats.ModelScopeImage, httpRequest.ApiFormat);
        Assert.Equal(RequestTypes.Image, httpRequest.RequestType);
        Assert.Equal("test-key", httpRequest.Auth!.ApiKey);

        Assert.Equal(
            """{"model":"Qwen/Qwen-Image-2.1","prompt":"a fox","size":"2048x2048"}""",
            Encoding.UTF8.GetString(httpRequest.Body!));

        Assert.Equal(
            "Qwen/Qwen-Image-2.1",
            httpRequest.TransformerMetadata!["model"].GetString());
        Assert.Equal("test-key", httpRequest.TransformerMetadata["api_key"].GetString());
    }

    // Reference TestBuildImageRequest_SizeMapping.
    [Theory]
    [InlineData("auto", "2048x2048")]
    [InlineData("", "2048x2048")]
    [InlineData("AUTO", "2048x2048")]
    [InlineData("1024x1024", "1024x1024")]
    [InlineData(" 1664x1664 ", "1664x1664")]
    public void TransformRequest_MapsTheImageSize(string size, string expected)
    {
        var body = Body(Transformer().TransformRequest(ImageRequest(size: size)));

        Assert.Equal(expected, body.GetProperty("size").GetString());
    }

    // Reference TestBuildImageRequest_EditSendsImageURL.
    [Fact]
    public void TransformRequest_SendsASingleEditImageAsADataUrlString()
    {
        var httpRequest = Transformer().TransformRequest(ImageRequest(
            prompt: "repaint the sky",
            apiFormat: ApiFormats.OpenAIImageEdit,
            images: [PngBytes]));

        var body = Body(httpRequest);
        var imageUrl = body.GetProperty("image_url").GetString()!;

        Assert.StartsWith("data:image/png;base64,", imageUrl, StringComparison.Ordinal);
        Assert.Equal(
            Convert.ToBase64String(PngBytes),
            imageUrl["data:image/png;base64,".Length..]);
    }

    [Fact]
    public void TransformRequest_DetectsTheReferenceImageMediaType()
    {
        // A non-image payload is labelled image/png (Go's sniff fallback).
        var httpRequest = Transformer().TransformRequest(ImageRequest(
            apiFormat: ApiFormats.OpenAIImageEdit,
            images: [[1, 2, 3]]));

        var imageUrl = Body(httpRequest).GetProperty("image_url").GetString()!;

        Assert.StartsWith("data:image/png;base64,", imageUrl, StringComparison.Ordinal);
    }

    // Reference TestBuildImageRequest_MultipleImagesSentAsArray.
    [Fact]
    public void TransformRequest_SendsMultipleEditImagesAsAnArray()
    {
        var body = Body(Transformer().TransformRequest(ImageRequest(
            prompt: "merge",
            apiFormat: ApiFormats.OpenAIImageEdit,
            images: [PngBytes, PngBytes])));

        var images = body.GetProperty("image_url");

        Assert.Equal(JsonValueKind.Array, images.ValueKind);
        Assert.Equal(2, images.GetArrayLength());
    }

    // Reference TestBuildImageRequest_RejectsMaskAndUnsupportedFormat.
    [Fact]
    public void TransformRequest_RejectsMasksAndUnsupportedFormats()
    {
        var withMask = Assert.Throws<TransformerException>(() => Transformer().TransformRequest(
            ImageRequest(
                prompt: "repaint",
                apiFormat: ApiFormats.OpenAIImageEdit,
                mask: [0x01])));

        Assert.Contains("do not support masks", withMask.Message, StringComparison.Ordinal);

        var unsupported = Assert.Throws<TransformerException>(() => Transformer().TransformRequest(
            ImageRequest(
                prompt: "x",
                apiFormat: ApiFormats.OpenAIImageVariation)));

        Assert.Contains("does not support image api format", unsupported.Message, StringComparison.Ordinal);
    }

    // Reference TestBuildImageRequest_RejectsEmptyPrompt.
    [Fact]
    public void TransformRequest_RejectsAnEmptyPrompt()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(ImageRequest(prompt: "   ")));

        Assert.Equal("invalid request: prompt is required for image requests", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsAnImageRequestWithoutImage()
    {
        var request = new LlmRequest
        {
            Model = "Qwen/Qwen-Image-2.1",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
        };

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal("invalid request: image request is required", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsAnEmptyResolvedKey()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer(keys: new StaticApiKeyProvider(string.Empty))
                .TransformRequest(ImageRequest()));

        Assert.Equal(
            "invalid request: no API key available for ModelScope image request",
            error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsANilRequest()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(null));

        Assert.Equal("request is nil", error.Message);
    }

    [Fact]
    public void TransformRequest_DelegatesChatAndStripsMetadata()
    {
        var request = new LlmRequest
        {
            Model = "Qwen/Qwen3",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
            Metadata = new Dictionary<string, string> { ["user_id"] = "u-1" },
        };

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal("https://example.com/v1/chat/completions", httpRequest.Url);
        Assert.Equal("test-key", httpRequest.Auth!.ApiKey);

        Assert.False(Body(httpRequest).TryGetProperty("metadata", out _));
        Assert.Equal("u-1", request.Metadata!["user_id"]);
    }

    // Reference TestTransformImageResponse_DelegatesNonImageFormats.
    [Fact]
    public void TransformResponse_DelegatesNonImageFormats()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"m","choices":[]}"""),
            Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIChatCompletion },
        };

        var result = Transformer().TransformResponse(response);

        Assert.Equal("chatcmpl-1", result.Id);
    }

    [Fact]
    public void TransformResponse_DelegatesWhenTheRequestIsMissing()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """{"id":"chatcmpl-2","object":"chat.completion","created":1,"model":"m","choices":[]}"""),
        };

        Assert.Equal("chatcmpl-2", Transformer().TransformResponse(response).Id);
    }

    [Fact]
    public void TransformResponse_RejectsANilResponse()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(null));

        Assert.Equal("http response is nil", error.Message);
    }

    [Fact]
    public void TransformResponse_RejectsAnEmptyBody()
    {
        var request = Transformer().TransformRequest(ImageRequest());
        var response = TaskResponse(string.Empty, request);
        response.Body = [];

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(response));

        Assert.Equal("ModelScope image response body is empty", error.Message);
    }

    [Fact]
    public void TransformResponse_RequiresDataOrATaskId()
    {
        var request = Transformer().TransformRequest(ImageRequest());
        var response = TaskResponse("""{"foo":1}""", request);

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(response));

        Assert.Equal(
            """ModelScope image response has neither image data nor task_id: {"foo":1}""",
            error.Message);
    }

    [Fact]
    public void TransformResponse_ReportsDecodeErrors()
    {
        var request = Transformer().TransformRequest(ImageRequest());
        var response = TaskResponse("not-json", request);

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(response));

        Assert.StartsWith("failed to decode ModelScope image response: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformResponse_ConvertsInlineDataUrlsToBase64()
    {
        var request = Transformer().TransformRequest(ImageRequest());
        var response = TaskResponse(
            """{"created":123,"data":[{"url":"data:image/png;base64,QUJD"}]}""",
            request);

        var result = Transformer().TransformResponse(response);

        Assert.Equal("modelscope-image-123", result.Id);
        Assert.Equal("chat.completion", result.Object);
        Assert.Equal(123, result.Created);
        Assert.Equal("Qwen/Qwen-Image-2.1", result.Model);
        Assert.Equal(RequestTypes.Image, result.RequestType);
        Assert.Equal(ApiFormats.ModelScopeImage, result.ApiFormat);

        var image = Assert.Single(result.Image!.Data);
        Assert.Equal("QUJD", image.B64JSON);
        Assert.Equal("data:image/png;base64,QUJD", image.URL);
        Assert.Null(image.RevisedPrompt);
    }

    [Fact]
    public void TransformResponse_KeepsInlineB64Json()
    {
        var request = Transformer().TransformRequest(ImageRequest());
        var response = TaskResponse(
            """{"created":7,"data":[{"b64_json":"XYZ","revised_prompt":"better"}]}""",
            request);

        var image = Assert.Single(Transformer().TransformResponse(response).Image!.Data);

        Assert.Equal("XYZ", image.B64JSON);
        Assert.Null(image.URL);
        Assert.Equal("better", image.RevisedPrompt);
    }

    [Fact]
    public void TransformResponse_DefaultsMissingCreatedAndModel()
    {
        var request = Transformer().TransformRequest(ImageRequest());
        request.TransformerMetadata = null;
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = Transformer().TransformResponse(
            TaskResponse("""{"data":[{"b64_json":"XYZ"}]}""", request));

        Assert.InRange(result.Created, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal("image-generation", result.Model);
        Assert.Equal("modelscope-image-" + result.Created, result.Id);
    }

    [Fact]
    public void TransformResponse_ParsesStringAndObjectOutputImages()
    {
        var request = Transformer().TransformRequest(ImageRequest());
        var response = TaskResponse(
            """{"created":9,"output_images":["data:image/png;base64,QUJD",{"url":"https://cdn.example.com/b.png","b64_json":"EFG"}]}""",
            request);

        var data = Transformer().TransformResponse(response).Image!.Data;

        Assert.Equal(2, data.Count);
        Assert.Equal("QUJD", data[0].B64JSON);
        Assert.Equal("data:image/png;base64,QUJD", data[0].URL);
        Assert.Equal("EFG", data[1].B64JSON);
        Assert.Equal("https://cdn.example.com/b.png", data[1].URL);
    }

    [Fact]
    public void TransformResponse_ErrorsWhenOutputImagesYieldNoImages()
    {
        var request = Transformer().TransformRequest(ImageRequest());
        var response = TaskResponse("""{"output_images":[{"other":1}]}""", request);

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(response));

        Assert.Equal("ModelScope image response contains no output images", error.Message);
    }

    [Fact]
    public void TransformResponse_ReportsMalformedOutputImages()
    {
        var request = Transformer().TransformRequest(ImageRequest());
        var response = TaskResponse("""{"output_images":[{"url":123}]}""", request);

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(response));

        Assert.StartsWith("failed to decode ModelScope output image: ", error.Message, StringComparison.Ordinal);
    }

    // Reference TestTransformImageResponse_PollsTaskAndReturnsBase64.
    [Fact]
    public void TransformResponse_PollsTheTaskAndDownloadsTheOutput()
    {
        var polls = 0;
        string? authorization = null;
        string? taskType = null;

        var executor = new MockExecutor
        {
            DoFn = (_, httpRequest) =>
            {
                polls++;
                authorization = httpRequest.Auth?.ApiKey;
                taskType = HttpClientUtils.HeaderGet(httpRequest.Headers, "X-ModelScope-Task-Type");
                Assert.Equal("https://example.com/v1/tasks/task-1", httpRequest.Url);

                var status = polls > 1 ? "SUCCEED" : "RUNNING";
                var output = "data:image/png;base64," + Convert.ToBase64String(PngBytes);
                return new HttpResponseData
                {
                    StatusCode = 200,
                    Body = Encoding.UTF8.GetBytes(
                        $$"""{"task_id":"task-1","task_status":"{{status}}","output_images":["{{output}}"]}"""),
                };
            },
        };

        var transformer = Transformer(httpClient: executor);
        var request = transformer.TransformRequest(ImageRequest());
        var response = TaskResponse(
            """{"task_id":"task-1","task_status":"PENDING"}""",
            request);

        var result = transformer.TransformResponse(response);

        Assert.Equal(2, polls);
        Assert.Equal("test-key", authorization);
        Assert.Equal("image_generation", taskType);

        var image = Assert.Single(result.Image!.Data);
        Assert.Equal(Convert.ToBase64String(PngBytes), image.B64JSON);
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(PngBytes), image.URL);
    }

    [Theory]
    [InlineData("SUCCEED")]
    [InlineData("SUCCESS")]
    [InlineData(" succeed ")]
    public void TransformResponse_AcceptsEverySuccessStatusSpelling(string status)
    {
        var executor = new MockExecutor
        {
            DoFn = (_, _) => new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes(
                    $$"""{"task_status":"{{status}}","output_images":["data:image/png;base64,QUJD"]}"""),
            },
        };

        var transformer = Transformer(httpClient: executor);
        var request = transformer.TransformRequest(ImageRequest());

        var result = transformer.TransformResponse(TaskResponse(
            """{"task_id":"task-1"}""",
            request));

        Assert.Single(result.Image!.Data);
    }

    // Reference TestTransformImageResponse_TaskFailureIsReported.
    [Fact]
    public void TransformResponse_ReportsTaskFailureMessage()
    {
        var executor = new MockExecutor
        {
            DoFn = (_, _) => new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes(
                    """{"task_id":"task-2","task_status":"FAILED","message":"prompt rejected"}"""),
            },
        };

        var transformer = Transformer(httpClient: executor);
        var request = transformer.TransformRequest(ImageRequest());

        var error = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            TaskResponse("""{"task_id":"task-2","task_status":"PENDING"}""", request)));

        Assert.Equal("ModelScope image task task-2 failed: prompt rejected", error.Message);
    }

    [Fact]
    public void TransformResponse_FallsBackToTheErrorsMessage()
    {
        var executor = new MockExecutor
        {
            DoFn = (_, _) => new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes(
                    """{"task_status":"FAILED","errors":{"message":"quota exceeded"}}"""),
            },
        };

        var transformer = Transformer(httpClient: executor);
        var request = transformer.TransformRequest(ImageRequest());

        var error = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            TaskResponse("""{"task_id":"task-3"}""", request)));

        Assert.Equal("ModelScope image task task-3 failed: quota exceeded", error.Message);
    }

    [Fact]
    public void TransformResponse_UsesUnknownErrorWhenTheTaskHasNoMessage()
    {
        var executor = new MockExecutor
        {
            DoFn = (_, _) => new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes("""{"task_status":"FAILED"}"""),
            },
        };

        var transformer = Transformer(httpClient: executor);
        var request = transformer.TransformRequest(ImageRequest());

        var error = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            TaskResponse("""{"task_id":"task-4"}""", request)));

        Assert.Equal("ModelScope image task task-4 failed: unknown error", error.Message);
    }

    // Reference TestTransformImageResponse_BoundsEachPollRequest.
    [Fact]
    public void TransformResponse_TimesOutWhenTheTaskNeverFinishes()
    {
        var executor = new MockExecutor
        {
            DoFn = (_, _) => new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes("""{"task_status":"RUNNING"}"""),
            },
        };

        var transformer = Transformer(
            httpClient: executor,
            pollInterval: TimeSpan.FromMilliseconds(5),
            taskTimeout: TimeSpan.FromMilliseconds(120));
        var request = transformer.TransformRequest(ImageRequest());

        var started = Environment.TickCount64;
        var error = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            TaskResponse("""{"task_id":"task-slow"}""", request)));
        var elapsed = Environment.TickCount64 - started;

        Assert.Equal("ModelScope image task task-slow timed out", error.Message);
        Assert.True(elapsed < 5000, "the poll loop must stop at the task deadline");
    }

    // Reference TestTransformImageResponse_HonoursCallerDeadline.
    [Fact]
    public void TransformResponse_StopsPollingWhenTheCallerCancels()
    {
        var executor = new MockExecutor
        {
            DoFn = (_, _) => new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes("""{"task_status":"RUNNING"}"""),
            },
        };

        var transformer = Transformer(
            httpClient: executor,
            taskTimeout: TimeSpan.FromHours(1));
        var request = transformer.TransformRequest(ImageRequest());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var started = Environment.TickCount64;
        Assert.Throws<OperationCanceledException>(() => transformer.TransformResponse(
            TaskResponse("""{"task_id":"task-cancel"}""", request, cancellation.Token)));
        var elapsed = Environment.TickCount64 - started;

        Assert.True(elapsed < 5000, "the poll loop must stop at the caller deadline");
    }

    // Reference TestImageTask_ReusesSubmissionKey.
    [Fact]
    public void TransformResponse_ReusesTheSubmittingKeyForPolling()
    {
        var pollKeys = new List<string>();
        var executor = new MockExecutor
        {
            DoFn = (_, httpRequest) =>
            {
                pollKeys.Add(httpRequest.Auth!.ApiKey!);
                return new HttpResponseData
                {
                    StatusCode = 200,
                    Body = Encoding.UTF8.GetBytes(
                        """{"task_status":"SUCCEED","output_images":[]}"""),
                };
            },
        };

        var transformer = Transformer(
            keys: new RotatingKeyProvider("key-a", "key-b", "key-c"),
            httpClient: executor);
        var built = transformer.TransformRequest(ImageRequest());

        Assert.Equal("key-a", built.Auth!.ApiKey);

        // The empty output list makes the call fail, but the key used for the
        // poll is the assertion that matters.
        var error = Assert.Throws<InvalidOperationException>(
            () => transformer.TransformResponse(TaskResponse("""{"task_id":"task-key"}""", built)));

        Assert.Equal("ModelScope image response contains no output images", error.Message);
        Assert.Equal("key-a", Assert.Single(pollKeys));
    }

    [Fact]
    public void TransformResponse_DownloadsUrlImagesThroughTheInjectedExecutor()
    {
        var downloads = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, httpRequest) =>
            {
                downloads++;
                Assert.Equal("https://cdn.example.com/a.png", httpRequest.Url);
                Assert.Equal("GET", httpRequest.Method);
                return new HttpResponseData { StatusCode = 200, Body = PngBytes };
            },
        };

        var transformer = Transformer(httpClient: executor);
        var request = transformer.TransformRequest(ImageRequest());

        var image = Assert.Single(transformer.TransformResponse(TaskResponse(
            """{"data":[{"url":"https://cdn.example.com/a.png"}]}""",
            request)).Image!.Data);

        Assert.Equal(1, downloads);
        Assert.Equal(Convert.ToBase64String(PngBytes), image.B64JSON);
    }

    [Fact]
    public void TransformResponse_RejectsEmptyDownloadedImages()
    {
        var executor = new MockExecutor
        {
            DoFn = (_, _) => new HttpResponseData { StatusCode = 200, Body = [] },
        };

        var transformer = Transformer(httpClient: executor);
        var request = transformer.TransformRequest(ImageRequest());

        var error = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            TaskResponse("""{"data":[{"url":"https://cdn.example.com/empty.png"}]}""", request)));

        Assert.Equal("downloaded ModelScope output image is empty", error.Message);
    }

    [Fact]
    public void TransformResponse_RejectsNonBase64DataUrls()
    {
        var transformer = Transformer();
        var request = transformer.TransformRequest(ImageRequest());

        var error = Assert.Throws<InvalidOperationException>(() => transformer.TransformResponse(
            TaskResponse("""{"data":[{"url":"data:image/png,plain"}]}""", request)));

        Assert.Equal("invalid image data URL", error.Message);
    }

    [Fact]
    public void TransformResponse_EscapesTheTaskIdAsAPathSegment()
    {
        string? taskUrl = null;
        var executor = new MockExecutor
        {
            DoFn = (_, httpRequest) =>
            {
                taskUrl = httpRequest.Url;
                return new HttpResponseData
                {
                    StatusCode = 200,
                    Body = Encoding.UTF8.GetBytes(
                        """{"task_status":"SUCCEED","output_images":["data:image/png;base64,QUJD"]}"""),
                };
            },
        };

        var transformer = Transformer(httpClient: executor);
        var request = transformer.TransformRequest(ImageRequest());
        transformer.TransformResponse(TaskResponse("""{"task_id":"task/1 x"}""", request));

        Assert.Equal("https://example.com/v1/tasks/task%2F1%20x", taskUrl);
    }

    [Fact]
    public void Constructor_RejectsANilConfiguration()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new ModelscopeOutboundTransformer(null));

        Assert.Equal("invalid ModelScope transformer configuration: config is nil", error.Message);
    }

    [Fact]
    public void Constructor_RejectsAMissingKeyProvider()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new ModelscopeOutboundTransformer(new ModelscopeOutboundConfig
            {
                BaseUrl = BaseUrl,
            }));

        Assert.StartsWith(
            "invalid ModelScope transformer configuration: ",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("API key provider is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsAMissingBaseUrl()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new ModelscopeOutboundTransformer(new ModelscopeOutboundConfig
            {
                ApiKeyProvider = new StaticApiKeyProvider("test-key"),
            }));

        Assert.Contains("base URL is required", error.Message, StringComparison.Ordinal);
    }
}
