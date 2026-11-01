using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenRouter;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the OpenRouter outbound (llm/transformer/openrouter):
/// the OpenAI-compatible chat route, the dedicated /images router, the
/// reasoning/image response conversion and the provider error envelope.
/// </summary>
public sealed class OpenRouterOutboundTransformerTests
{
    // ------------------------------------------------------------------
    // Constructor / base URL
    // ------------------------------------------------------------------

    [Fact]
    public void ConstructorValidation()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => new OpenRouterOutboundTransformer(null!));
        Assert.Equal(
            "invalid OpenRouter transformer configuration: config cannot be nil",
            nil.Message);

        var missingKey = Assert.Throws<InvalidOperationException>(
            () => new OpenRouterOutboundTransformer(new OpenRouterOutboundConfig
            {
                BaseUrl = "https://openrouter.ai/api/v1",
            }));
        // The inner OpenAI validator throws ArgumentException; the port keeps
        // its parameter suffix, so only the Go-compatible prefix is pinned (C101).
        Assert.StartsWith(
            "invalid OpenRouter transformer configuration: "
            + "invalid OpenAI transformer configuration: ",
            missingKey.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "API key provider is required",
            missingKey.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BaseUrlTrimsExactlyOneTrailingSlash()
    {
        // The transformer keeps its own copy of the caller's URL: only one
        // trailing slash is trimmed and the OpenAI "v1" normalization is not
        // applied to the routes OpenRouter builds.
        var trailing = Transformer("https://openrouter.ai/api/v1/");
        Assert.Equal("https://openrouter.ai/api/v1", trailing.BaseUrl);
        Assert.Equal(
            "https://openrouter.ai/api/v1/chat/completions",
            trailing.TransformRequest(SimpleRequest()).Url);

        var doubleTrailing = Transformer("https://openrouter.ai/api/v1//");
        Assert.Equal("https://openrouter.ai/api/v1/", doubleTrailing.BaseUrl);
        Assert.Equal(
            "https://openrouter.ai/api/v1//chat/completions",
            doubleTrailing.TransformRequest(SimpleRequest()).Url);

        // The embedded OpenAI transformer normalizes /api to /api/v1, but the
        // OpenRouter chat URL is /api/chat/completions (no version suffix).
        var unversioned = Transformer("https://openrouter.ai/api");
        Assert.Equal(
            "https://openrouter.ai/api/chat/completions",
            unversioned.TransformRequest(SimpleRequest()).Url);
    }

    // ------------------------------------------------------------------
    // Chat request
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequest_ChatShape()
    {
        var transformer = Transformer();
        var httpRequest = transformer.TransformRequest(SimpleRequest());

        Assert.Equal("POST", httpRequest.Method);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", httpRequest.Url);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, httpRequest.ApiFormat);
        Assert.Equal("application/json", httpRequest.ContentType);
        Assert.Equal("application/json", httpRequest.Headers!["Content-Type"][0]);
        Assert.Equal("application/json", httpRequest.Headers!["Accept"][0]);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("test-api-key", httpRequest.Auth!.ApiKey);
        Assert.Equal("test-api-key", transformer.SelectedApiKey);

        var body = Body(httpRequest);
        Assert.Equal("z-ai/glm-4.5-air:free", body.GetProperty("model").GetString());
        Assert.Single(body.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public void TransformRequest_SendsReasoningThroughTheReasoningField()
    {
        // OpenRouter's Config pins ReasoningFieldReasoning: the unified
        // reasoning is sent as the top-level `reasoning` field, and the
        // reasoning_content field is left out.
        var request = SimpleRequest();
        request.Messages =
        [
            new Message
            {
                Role = "assistant",
                Content = MessageContent.FromText("Hi"),
                Reasoning = "trace",
            },
        ];

        var assistant = Body(Transformer().TransformRequest(request)).GetProperty("messages")[0];
        Assert.Equal("trace", assistant.GetProperty("reasoning").GetString());
        Assert.False(assistant.TryGetProperty("reasoning_content", out _));
    }

    [Fact]
    public void TransformRequest_RejectsMissingModelMessagesAndUnsupportedTypes()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(null));
        Assert.Equal("chat completion request is nil", nil.Message);

        var noModel = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(new LlmRequest
            {
                Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hi") }],
            }));
        Assert.Equal("invalid request: model is required", noModel.Message);

        var noMessages = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(new LlmRequest
            {
                Model = "z-ai/glm-4.5-air:free",
            }));
        Assert.Equal("invalid request: messages are required", noMessages.Message);

        var compact = SimpleRequest();
        compact.RequestType = RequestTypes.Compact;
        var compactError = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(compact));
        Assert.Equal(
            "invalid request: compact is only supported by OpenAI Responses API",
            compactError.Message);

        var video = SimpleRequest();
        video.RequestType = RequestTypes.Video;
        var videoError = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(video));
        Assert.Equal("invalid request: video is not supported", videoError.Message);
    }

    [Fact]
    public void TransformRequest_AudioDelegatesToTheOpenAiOutbound()
    {
        var transformer = Transformer();
        var request = new LlmRequest
        {
            Model = "openai/gpt-4o-mini-tts",
            RequestType = RequestTypes.Speech,
            Speech = new SpeechRequest { Input = "hello", Voice = "alloy" },
        };

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(ApiFormats.OpenAISpeech, httpRequest.ApiFormat);
        Assert.Equal(RequestTypes.Speech, httpRequest.RequestType);
        Assert.Equal("https://openrouter.ai/api/v1/audio/speech", httpRequest.Url);
        Assert.Equal("test-api-key", transformer.SelectedApiKey);
    }

    // ------------------------------------------------------------------
    // Image generation request
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequest_ImageGenerationShape()
    {
        var request = ImageRequest();
        var transformer = Transformer();
        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal("POST", httpRequest.Method);
        Assert.Equal("https://openrouter.ai/api/v1/images", httpRequest.Url);
        Assert.Equal(RequestTypes.Image, httpRequest.RequestType);
        Assert.Equal(ApiFormats.OpenAIImageGeneration, httpRequest.ApiFormat);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("test-api-key", httpRequest.Auth!.ApiKey);
        Assert.Equal(
            request.Model,
            httpRequest.TransformerMetadata!["model"].GetString());

        var body = Body(httpRequest);
        Assert.Equal(request.Model, body.GetProperty("model").GetString());
        Assert.Equal("Generate a sunset", body.GetProperty("prompt").GetString());
        Assert.False(body.TryGetProperty("stream", out _));
        Assert.False(body.TryGetProperty("response_format", out _));
    }

    [Fact]
    public void TransformRequest_ImageGenerationEncodesInputReferences()
    {
        // PNG and JPEG magic bytes; the data URL media type is sniffed from them.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

        var request = ImageRequest();
        request.Image!.Images = [png, jpeg];

        var references = Body(Transformer().TransformRequest(request))
            .GetProperty("input_references");

        Assert.Equal(2, references.GetArrayLength());
        Assert.Equal("image_url", references[0].GetProperty("type").GetString());
        Assert.Equal(
            "data:image/png;base64," + Convert.ToBase64String(png),
            references[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal(
            "data:image/jpeg;base64," + Convert.ToBase64String(jpeg),
            references[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void TransformRequest_ImageGenerationValidations()
    {
        var missingModel = ImageRequest();
        missingModel.Model = string.Empty;
        Assert.Equal(
            "invalid request: model is required",
            Assert.Throws<TransformerException>(
                () => Transformer().TransformRequest(missingModel)).Message);

        var missingImage = SimpleRequest();
        missingImage.RequestType = RequestTypes.Image;
        Assert.Equal(
            "invalid request: image request is required",
            Assert.Throws<TransformerException>(
                () => Transformer().TransformRequest(missingImage)).Message);

        var missingPrompt = ImageRequest();
        missingPrompt.Image!.Prompt = null;
        Assert.Equal(
            "invalid request: prompt is required for image generation",
            Assert.Throws<TransformerException>(
                () => Transformer().TransformRequest(missingPrompt)).Message);

        var tooMany = ImageRequest();
        tooMany.Image!.Images = Enumerable.Repeat(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, 17).ToList();
        Assert.Equal(
            "invalid request: at most 16 input references are supported",
            Assert.Throws<TransformerException>(
                () => Transformer().TransformRequest(tooMany)).Message);

        var mask = ImageRequest();
        mask.Image!.Mask = [0x89, 0x50, 0x4E, 0x47];
        Assert.Equal(
            "invalid request: masks are not supported by OpenRouter image generation",
            Assert.Throws<TransformerException>(
                () => Transformer().TransformRequest(mask)).Message);
    }

    // ------------------------------------------------------------------
    // Image generation response
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(
        """{"created":1759393520,"data":[{"b64_json":"iVBORw0KGgo","media_type":"image/png"}],"usage":{"prompt_tokens":7,"completion_tokens":1290,"total_tokens":1297}}""",
        "data:image/png;base64,iVBORw0KGgo",
        "png")]
    [InlineData(
        """{"created":1759393520,"data":[{"b64_json":"/9j/4AAQ","media_type":"image/jpeg"},{"b64_json":"R0lGODlh","media_type":"image/gif"}],"usage":{"prompt_tokens":10,"completion_tokens":100,"total_tokens":110}}""",
        "data:image/jpeg;base64,/9j/4AAQ",
        "jpeg")]
    public void TransformResponse_ImageGeneration(string body, string expectedUrl, string expectedFormat)
    {
        var response = ImageHttpResponse(body);

        var result = Transformer().TransformResponse(response);

        Assert.Equal("img-1759393520", result.Id);
        Assert.Equal("chat.completion", result.Object);
        Assert.Equal(1759393520, result.Created);
        Assert.Equal("test-model", result.Model);
        Assert.Equal(RequestTypes.Image, result.RequestType);
        Assert.Equal(ApiFormats.OpenAIImageGeneration, result.ApiFormat);
        Assert.Equal(expectedUrl, result.Image!.Data[0].URL);
        Assert.Equal(expectedFormat, result.Image.OutputFormat);
        Assert.NotNull(result.Usage);
    }

    [Fact]
    public void TransformResponse_ImageGenerationDataUrlsAndMissingMediaType()
    {
        var result = Transformer().TransformResponse(ImageHttpResponse(
            """{"created":1,"data":[{"b64_json":"data:image/webp;base64,AAAA","media_type":"image/png"},{"b64_json":"BBBB"},{"b64_json":""}]}"""));

        // An already-data URL is passed through untouched.
        Assert.Equal("data:image/webp;base64,AAAA", result.Image!.Data[0].URL);
        Assert.Equal("png", result.Image.OutputFormat);

        // A missing media type defaults to image/png.
        Assert.Equal("data:image/png;base64,BBBB", result.Image.Data[1].URL);

        // No payload means no derived URL or format.
        Assert.Null(result.Image.Data[2].URL);
        Assert.Null(result.Usage);
    }

    [Fact]
    public void TransformResponse_ImageGenerationFallsBackToTheDefaultModel()
    {
        var response = ImageHttpResponse("""{"created":1,"data":[]}""");
        response.Request!.TransformerMetadata = null;

        Assert.Equal("image-generation", Transformer().TransformResponse(response).Model);
    }

    [Theory]
    [InlineData(400, """{"error":{"message":"Invalid request"}}""")]
    [InlineData(200, "")]
    [InlineData(200, "invalid json")]
    public void TransformResponse_ImageGenerationErrors(int statusCode, string body)
    {
        var response = ImageHttpResponse(body);
        response.StatusCode = statusCode;

        Assert.Throws<InvalidOperationException>(() => Transformer().TransformResponse(response));
    }

    // ------------------------------------------------------------------
    // Model conversion (reference model_test.go + testdata)
    // ------------------------------------------------------------------

    [Fact]
    public void ModelConversion_ReasoningChunk()
    {
        var result = ConvertFixture("or-chunk.json");

        Assert.Equal("gen-1758295230-SiI5bLSgznz9dz6HO9XP", result.Id);
        Assert.Equal("chat.completion.chunk", result.Object);
        var delta = Assert.Single(result.Choices).Delta!;
        Assert.Equal("assistant", delta.Role);
        Assert.Equal(string.Empty, delta.Content!.Content);
        Assert.Equal("We", delta.ReasoningContent);
        // openai.Message.ToLLMMessage syncs the two reasoning fields.
        Assert.Equal("We", delta.Reasoning);
    }

    [Fact]
    public void ModelConversion_GeneratedImagesBecomeContentParts()
    {
        var result = ConvertFixture("or-image.json");

        var message = Assert.Single(result.Choices).Message!;
        Assert.Null(message.Content!.Content);
        var part = Assert.Single(message.Content.MultipleContent!);
        Assert.Equal("image_url", part.Type);
        Assert.Equal("data:image/png;base64,iVBORw0KGgo", part.ImageUrl!.Url);
        Assert.Equal(7, result.Usage!.PromptTokens);
        Assert.Equal(1290, result.Usage.CompletionTokens);
        Assert.Equal(1297, result.Usage.TotalTokens);
    }

    [Fact]
    public void ModelConversion_ToolCall()
    {
        var result = ConvertFixture("or-tool.json");

        var message = Assert.Single(result.Choices).Message!;
        Assert.Equal(string.Empty, message.Content!.Content);
        var toolCall = Assert.Single(message.ToolCalls!);
        Assert.Equal("call_function_2653208710_1", toolCall.Id);
        Assert.Equal("Read", toolCall.Function.Name);
        Assert.Equal(
            """{"file_path": "/axonhub/internal/server/middleware/trace_test.go"}""",
            toolCall.Function.Arguments);
    }

    [Fact]
    public void ModelConversion_ToolCallChunk()
    {
        var result = ConvertFixture("or-tool.chunk.json");

        var delta = Assert.Single(result.Choices).Delta!;
        Assert.Equal("tool_calls", result.Choices[0].FinishReason);
        Assert.Equal("assistant", delta.Role);
        Assert.Contains("</think>", delta.Content!.Content, StringComparison.Ordinal);
        Assert.Equal(
            """{"file_path": "/axonhub/internal/llm/transformer/openrouter/outbound.go"}""",
            Assert.Single(delta.ToolCalls!).Function.Arguments);
    }

    // ------------------------------------------------------------------
    // Chat response / errors
    // ------------------------------------------------------------------

    [Fact]
    public void TransformResponse_DecodesChatReasoning()
    {
        var result = Transformer().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = """{"id":"gen-1","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"Hello","reasoning":"because"},"finish_reason":"stop"}]}"""u8.ToArray(),
        });

        var message = Assert.Single(result.Choices).Message!;
        Assert.Equal("Hello", message.Content!.Content);
        Assert.Equal("because", message.ReasoningContent);
    }

    [Fact]
    public void TransformResponse_Errors()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(null));
        Assert.Equal("http response is nil", nil.Message);

        Assert.Equal(
            "HTTP error 502",
            Assert.Throws<InvalidOperationException>(
                () => Transformer().TransformResponse(new HttpResponseData
                {
                    StatusCode = 502,
                    Body = "{}"u8.ToArray(),
                })).Message);

        Assert.Equal(
            "response body is empty",
            Assert.Throws<InvalidOperationException>(
                () => Transformer().TransformResponse(new HttpResponseData
                {
                    StatusCode = 200,
                    Body = [],
                })).Message);

        Assert.StartsWith(
            "failed to unmarshal chat completion response: ",
            Assert.Throws<InvalidOperationException>(
                () => Transformer().TransformResponse(new HttpResponseData
                {
                    StatusCode = 200,
                    Body = "not json"u8.ToArray(),
                })).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransformError_ParsesTheOpenRouterEnvelope()
    {
        var response = Transformer().TransformError(Error(
            429,
            """{"error":{"message":"rate limited","code":429}}"""));

        Assert.Equal(429, response.StatusCode);
        Assert.Equal("rate limited", response.Detail.Message);
        Assert.Equal("429", response.Detail.Code);
        Assert.Equal("api_error", response.Detail.Type);
    }

    [Fact]
    public void TransformError_PrefersMetadataRawOverMessage()
    {
        var response = Transformer().TransformError(Error(
            400,
            """{"error":{"message":"upstream message","code":400,"metadata":{"raw":"raw provider failure"}}}"""));

        Assert.Equal("raw provider failure", response.Detail.Message);
        Assert.Equal("400", response.Detail.Code);
    }

    [Fact]
    public void TransformError_UsesTheStatusCodeTextWhenOnlyTheCodeIsPresent()
    {
        var response = Transformer().TransformError(Error(
            404,
            """{"error":{"code":404}}"""));

        Assert.Equal("Not Found", response.Detail.Message);
        Assert.Equal("404", response.Detail.Code);
    }

    [Fact]
    public void TransformError_KeepsTheCodeAsStringEvenWhenZero()
    {
        var response = Transformer().TransformError(Error(400, "{}"));

        Assert.Equal(string.Empty, response.Detail.Message);
        Assert.Equal("0", response.Detail.Code);
    }

    [Fact]
    public void TransformError_FallsBackToTheStatusText()
    {
        var invalidJson = Transformer().TransformError(Error(502, "not json"));
        Assert.Equal(502, invalidJson.StatusCode);
        Assert.Equal("Bad Gateway", invalidJson.Detail.Message);
        Assert.Equal("api_error", invalidJson.Detail.Type);
        Assert.Null(invalidJson.Detail.Code);

        var nonObjectError = Transformer().TransformError(
            Error(400, """{"error":"boom"}"""));
        Assert.Equal("Bad Request", nonObjectError.Detail.Message);

        var nil = Transformer().TransformError(null);
        Assert.Equal(500, nil.StatusCode);
        Assert.Equal("Internal Server Error", nil.Detail.Message);
    }

    // ------------------------------------------------------------------
    // Stream / aggregation
    // ------------------------------------------------------------------

    [Fact]
    public void TransformStream_FiltersDoneAndAppendsItsOwn()
    {
        var events = new List<StreamEvent>
        {
            new()
            {
                Data = """{"id":"gen-1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"Hello"}}]}"""u8.ToArray(),
            },
            new() { Data = "[DONE]"u8.ToArray() },
        };

        using var stream = Transformer().TransformStream(
            null,
            new SliceStream<StreamEvent>(events));

        var responses = GoStreams.All(stream, out var error);

        Assert.Null(error);
        Assert.Equal(2, responses.Count);
        Assert.Equal(
            "Hello",
            Assert.Single(responses[0].Choices).Delta!.Content!.Content);
        Assert.Same(LlmResponse.DoneResponse, responses[1]);
    }

    [Fact]
    public void TransformStream_SurfacesInStreamErrors()
    {
        using var stream = Transformer().TransformStream(
            null,
            new SliceStream<StreamEvent>(
                [new StreamEvent { Data = """{"error":"upstream exploded"}"""u8.ToArray() }]));

        GoStreams.All(stream, out var error);

        var responseError = Assert.IsType<ResponseError>(error);
        Assert.Equal("upstream exploded", responseError.Detail.Message);
    }

    [Fact]
    public void AggregateStreamChunks_MergesContent()
    {
        var chunks = new List<StreamEvent>
        {
            new() { Data = """{"id":"test","choices":[{"index":0,"delta":{"content":"Hello"}}]}"""u8.ToArray() },
            new() { Data = """{"id":"test","choices":[{"index":0,"delta":{"content":" World"}}]}"""u8.ToArray() },
        };

        var data = Transformer().AggregateStreamChunks(null, chunks, out var meta);

        Assert.Contains("Hello World", Encoding.UTF8.GetString(data), StringComparison.Ordinal);
        Assert.Equal("test", meta.Id);
    }

    // ------------------------------------------------------------------
    // Image helpers
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38 }, "gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "webp")]
    [InlineData(new byte[] { 0x01 }, "png")]
    public void DetectImageFormat(byte[] data, string expected)
        => Assert.Equal(expected, OpenRouterOutboundTransformer.DetectImageFormat(data));

    [Theory]
    [InlineData("", "")]
    [InlineData("image/svg+xml", "svg")]
    [InlineData(" image/JPEG ", "jpeg")]
    [InlineData("application/png", "")]
    public void ImageFormatFromMediaType(string mediaType, string expected)
        => Assert.Equal(
            expected,
            OpenRouterOutboundTransformer.ImageFormatFromMediaType(mediaType));

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static OpenRouterOutboundTransformer Transformer(
        string baseUrl = "https://openrouter.ai/api/v1")
        => new(new OpenRouterOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
        });

    private static LlmRequest SimpleRequest(string model = "z-ai/glm-4.5-air:free")
        => new()
        {
            Model = model,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static LlmRequest ImageRequest()
        => new()
        {
            Model = "google/gemini-2.5-flash-image-preview",
            RequestType = RequestTypes.Image,
            Image = new ImageRequest
            {
                Prompt = "Generate a sunset",
            },
        };

    private static HttpResponseData ImageHttpResponse(string body)
        => new()
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(body),
            Request = new HttpRequestData
            {
                RequestType = RequestTypes.Image,
                TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["model"] = JsonSerializer.SerializeToElement("test-model"),
                },
            },
        };

    private static LlmResponse ConvertFixture(string fixture)
    {
        var body = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "testdata", "openrouter", fixture));
        var response = LlmJson.Deserialize<OpenRouterResponse>(body)!;
        return response.ToOpenAiResponse().ToLlmResponse()!;
    }

    private static HttpClientError Error(int statusCode, string body)
        => new(
            "POST",
            "https://openrouter.ai/api/v1/chat/completions",
            statusCode,
            statusCode + " Error",
            Encoding.UTF8.GetBytes(body));

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
