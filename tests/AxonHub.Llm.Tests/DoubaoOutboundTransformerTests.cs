using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Doubao;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for llm/transformer/doubao/outbound.go (outbound_test.go):
/// constructor validation, the Ark user_id/request_id chat fields, the URL
/// override conventions, the image generation shape and the delegation to the
/// embedded OpenAI outbound.
/// </summary>
public sealed class DoubaoOutboundTransformerTests
{
    private const string BaseUrl = "https://ark.cn-beijing.volces.com/api/v3";

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private static readonly string LongUserId =
        """{"device_id":"334251bf6b147d22d006e2e9cb990169a3ba5e77fecb52a03473e32eb1afe62e","account_uuid":"","session_id":"db0eef37-75a9-4288-8098-0557997cd879"}""";

    private static DoubaoOutboundTransformer Transformer(
        string baseUrl = BaseUrl,
        string apiKey = "test-api-key")
        => DoubaoOutboundTransformer.Create(baseUrl, apiKey);

    private static LlmRequest ChatRequest(
        string model = "ep-20241203072800-8f7f",
        Dictionary<string, string>? metadata = null)
        => new()
        {
            Model = model,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello, world!") }],
            Metadata = metadata,
        };

    private static JsonElement Body(HttpRequestData request)
    {
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }

    // Reference TestNewOutboundTransformer.
    [Fact]
    public void Create_RejectsAnEmptyBaseUrl()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => DoubaoOutboundTransformer.Create(string.Empty, "test-api-key"));

        Assert.Equal("base URL is required for Doubao transformer", error.Message);
    }

    [Fact]
    public void Create_RejectsAnEmptyApiKey()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => DoubaoOutboundTransformer.Create(BaseUrl, string.Empty));

        Assert.Equal("API key is required for Doubao transformer", error.Message);
    }

    // Reference TestNewOutboundTransformerWithConfig.
    [Fact]
    public void Constructor_RejectsANilConfig()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new DoubaoOutboundTransformer(null));

        Assert.Equal("invalid Doubao transformer configuration: config is nil", error.Message);
    }

    [Fact]
    public void Constructor_RejectsAnEmptyBaseUrl()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new DoubaoOutboundTransformer(new DoubaoOutboundConfig
            {
                ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
            }));

        Assert.Equal("base URL is required for Doubao transformer", error.Message);
    }

    [Fact]
    public void Constructor_RejectsAMissingKeyProvider()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new DoubaoOutboundTransformer(new DoubaoOutboundConfig
            {
                BaseUrl = BaseUrl,
            }));

        Assert.Equal("API key provider is required for Doubao transformer", error.Message);
    }

    [Fact]
    public void Constructor_NormalizesTheBaseUrlOnce()
    {
        var transformer = Transformer(BaseUrl + "/");

        Assert.Equal(BaseUrl, transformer.BaseUrl);
        Assert.Equal(
            BaseUrl + "/chat/completions",
            transformer.TransformRequest(ChatRequest()).Url);
    }

    [Fact]
    public void Constructor_LeavesTheRawUrlSuffixOffTheNormalizedUrl()
    {
        var transformer = Transformer(BaseUrl + "/chat/completions##");

        Assert.Equal(BaseUrl + "/chat/completions", transformer.BaseUrl);
    }

    // Reference TestOutboundTransformer_TransformRequest.
    [Fact]
    public void TransformRequest_BuildsTheChatCompletionRequest()
    {
        var httpRequest = Transformer().TransformRequest(ChatRequest());

        Assert.Equal("POST", httpRequest.Method);
        Assert.Equal(BaseUrl + "/chat/completions", httpRequest.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpRequest.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpRequest.Headers, "Accept"));
        Assert.Equal(ApiFormats.OpenAIChatCompletion, httpRequest.ApiFormat);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("test-api-key", httpRequest.Auth.ApiKey);

        var body = Body(httpRequest);
        Assert.Equal("ep-20241203072800-8f7f", body.GetProperty("model").GetString());
        var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("Hello, world!", message.GetProperty("content").GetString());

        // The Doubao request does not support metadata.
        Assert.False(body.TryGetProperty("metadata", out _));
    }

    // Reference TestOutboundTransformer_TransformRequest_UserIDLength.
    [Fact]
    public void TransformRequest_CarriesUserIdAndRequestIdFromMetadata()
    {
        var httpRequest = Transformer().TransformRequest(ChatRequest(
            metadata: new Dictionary<string, string>
            {
                ["user_id"] = "user123",
                ["request_id"] = "req456",
            }));

        var body = Body(httpRequest);
        Assert.Equal("user123", body.GetProperty("user_id").GetString());
        Assert.Equal("req456", body.GetProperty("request_id").GetString());
        Assert.False(body.TryGetProperty("metadata", out _));

        // doubao.Request embeds openai.Request and appends its own fields after
        // it, so Go emits user_id then request_id at the end of the object.
        var text = Encoding.UTF8.GetString(httpRequest.Body!);
        Assert.EndsWith(",\"user_id\":\"user123\",\"request_id\":\"req456\"}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformRequest_GeneratesARequestIdWhenMissing()
    {
        var body = Body(Transformer().TransformRequest(ChatRequest(
            metadata: new Dictionary<string, string> { ["user_id"] = "user123" })));

        Assert.Equal("user123", body.GetProperty("user_id").GetString());
        Assert.StartsWith("req_", body.GetProperty("request_id").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TransformRequest_TruncatesALongUserIdTo128Runes()
    {
        var body = Body(Transformer().TransformRequest(ChatRequest(
            metadata: new Dictionary<string, string> { ["user_id"] = LongUserId })));

        var userId = body.GetProperty("user_id").GetString();

        Assert.NotNull(userId);
        Assert.Equal(128, userId.Length);
        Assert.Equal(LongUserId[..128], userId);
    }

    [Fact]
    public void TransformRequest_OmitsAShortUserId()
    {
        var body = Body(Transformer().TransformRequest(ChatRequest(
            metadata: new Dictionary<string, string> { ["user_id"] = "abc" })));

        Assert.False(body.TryGetProperty("user_id", out _));
    }

    // Reference TestOutboundTransformer_TransformRequest_URLOverrides.
    [Fact]
    public void TransformRequest_AppendsV3ToAVersionlessBaseUrl()
    {
        var transformer = Transformer("https://ark.cn-beijing.volces.com/api");

        Assert.Equal(
            "https://ark.cn-beijing.volces.com/api/v3/chat/completions",
            transformer.TransformRequest(ChatRequest()).Url);
    }

    [Fact]
    public void TransformRequest_UsesTheEndpointPathVerbatim()
    {
        var transformer = new DoubaoOutboundTransformer(new DoubaoOutboundConfig
        {
            BaseUrl = BaseUrl,
            EndpointPath = "/chat/completions",
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
        });

        Assert.Equal(
            BaseUrl + "/chat/completions",
            transformer.TransformRequest(ChatRequest()).Url);
    }

    [Fact]
    public void TransformRequest_UsesARawUrlAsTheFullRequestUrl()
    {
        var transformer = Transformer(BaseUrl + "/chat/completions##");

        Assert.Equal(
            BaseUrl + "/chat/completions",
            transformer.TransformRequest(ChatRequest()).Url);
    }

    [Fact]
    public void TransformRequest_RejectsANilRequest()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(null));

        Assert.Equal("chat completion request is nil", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsAnEmptyModel()
    {
        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(ChatRequest(model: string.Empty)));

        Assert.Equal("invalid request: model is required", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsEmptyMessages()
    {
        var request = new LlmRequest { Model = "ep-20241203072800-8f7f", Messages = [] };

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal("invalid request: messages are required", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsAnUnsupportedRequestType()
    {
        var request = ChatRequest();
        request.RequestType = RequestTypes.Rerank;

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal("invalid request: rerank is not supported", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsCompactRequests()
    {
        var request = ChatRequest();
        request.RequestType = RequestTypes.Compact;

        var error = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal("invalid request: compact is only supported by OpenAI Responses API", error.Message);
    }

    // ------------------------------------------------------------ image path

    private static LlmRequest ImageRequest(
        string? prompt = "A beautiful sunset",
        string? size = "1024x1024",
        string? quality = null,
        string? user = null,
        string? apiFormat = ApiFormats.OpenAIImageGeneration,
        List<byte[]>? images = null)
        => new()
        {
            Model = "doubao-image-pro",
            RequestType = RequestTypes.Image,
            ApiFormat = apiFormat,
            Image = new ImageRequest
            {
                Prompt = prompt,
                Size = size,
                Quality = quality,
                User = user,
                Images = images,
            },
        };

    // Reference TestOutboundTransformer_buildImageGenerationAPIRequest.
    [Fact]
    public void TransformRequest_BuildsTheImageGenerationBody()
    {
        var request = Transformer().TransformRequest(ImageRequest(quality: "hd"));

        Assert.Equal("POST", request.Method);
        Assert.Equal(BaseUrl + "/images/generations", request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal(ApiFormats.OpenAIImageGeneration, request.ApiFormat);
        Assert.Equal(RequestTypes.Image, request.RequestType);

        // Go marshals the request from a map, so the keys come out sorted.
        Assert.Equal(
            """{"guidance_scale":7.5,"model":"doubao-image-pro","prompt":"A beautiful sunset","response_format":"b64_json","size":"1024x1024","stream":false}""",
            Encoding.UTF8.GetString(request.Body!));
        Assert.Equal(
            "doubao-image-pro",
            request.TransformerMetadata!["model"].GetString());
    }

    [Fact]
    public void TransformRequest_SendsEditImagesThroughTheGenerationsEndpoint()
    {
        var request = Transformer().TransformRequest(ImageRequest(
            prompt: "Modify this image",
            size: null,
            apiFormat: ApiFormats.OpenAIImageEdit,
            images: [PngBytes]));

        Assert.Equal(BaseUrl + "/images/generations", request.Url);

        var body = Body(request);
        var image = body.GetProperty("image").GetString()!;
        Assert.StartsWith("data:image/png;base64,", image, StringComparison.Ordinal);
        Assert.Equal(Convert.ToBase64String(PngBytes), image["data:image/png;base64,".Length..]);
        Assert.Equal("b64_json", body.GetProperty("response_format").GetString());
    }

    [Fact]
    public void TransformRequest_UsesThePngFallbackForUnknownImageBytes()
    {
        var body = Body(Transformer().TransformRequest(ImageRequest(
            size: null,
            apiFormat: ApiFormats.OpenAIImageEdit,
            images: [[1, 2, 3]])));

        Assert.StartsWith(
            "data:image/png;base64,",
            body.GetProperty("image").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransformRequest_MapsStandardQualityAndTheUserField()
    {
        var body = Body(Transformer().TransformRequest(ImageRequest(
            quality: "standard",
            user: "user123")));

        Assert.Equal(2.5, body.GetProperty("guidance_scale").GetDouble());
        Assert.Equal("user123", body.GetProperty("user").GetString());
    }

    [Fact]
    public void TransformRequest_OmitsTheUnsupportedWatermarkField()
    {
        var body = Body(Transformer().TransformRequest(ImageRequest()));

        Assert.False(body.TryGetProperty("watermark", out _));
    }

    [Fact]
    public void TransformRequest_RejectsAnImageRequestWithoutImage()
    {
        var request = new LlmRequest
        {
            Model = "doubao-image-pro",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(request));

        Assert.Equal("image request is required", error.Message);
    }

    [Fact]
    public void TransformRequest_RejectsAnEmptyImagePrompt()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(ImageRequest(prompt: string.Empty)));

        Assert.Equal("prompt is required for image generation", error.Message);
    }

    // ----------------------------------------------------------- delegation

    [Fact]
    public void TransformResponse_DelegatesChatResponsesToTheOpenAiOutbound()
    {
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"m","choices":[]}"""),
            Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIChatCompletion },
        };

        Assert.Equal("chatcmpl-1", Transformer().TransformResponse(response).Id);
    }

    [Fact]
    public void TransformResponse_DelegatesAResponseWithoutRequest()
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
    public void TransformError_DelegatesToTheOpenAiOutbound()
    {
        var response = Transformer().TransformError(
            new HttpClientError(
                "POST",
                BaseUrl + "/chat/completions",
                400,
                "400 Bad Request",
                Encoding.UTF8.GetBytes(
                    """{"error":{"message":"bad request","type":"invalid_request_error"}}""")));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("bad request", response.Detail.Message);
        Assert.Equal("invalid_request_error", response.Detail.Type);
    }
}
