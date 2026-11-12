using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Modelscope;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the ModelScope branch of the channel transformer switch
/// (channel_llm.go:1096-1108, channel_endpoint.go:309-312): one outbound serves
/// both the OpenAI-compatible chat endpoint and the native asynchronous image
/// task protocol.
/// </summary>
public sealed class ModelscopeChannelWiringTests
{
    private const string BaseUrl = "https://api.modelscope.cn/v1";

    private static ChannelCandidate Candidate(string baseUrl = BaseUrl)
        => new()
        {
            ChannelId = 89,
            ChannelName = "modelscope-wiring",
            ActualModel = "Qwen/Qwen-Image-2.1",
            Type = ChannelOutboundFactory.ModelscopeChannelType,
            BaseUrl = baseUrl,
            Credentials = """{"apiKey":"provider-key"}""",
        };

    private static LlmRequest ChatRequest()
        => new()
        {
            Model = "Qwen/Qwen3",
            ApiFormat = ApiFormats.OpenAIChatCompletion,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static LlmRequest ImageRequest()
        => new()
        {
            Model = "Qwen/Qwen-Image-2.1",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Image = new ImageRequest { Prompt = "a fox", Size = "auto" },
        };

    private static IOutboundTransformer Create(LlmRequest request)
        => ChannelOutboundFactory.Create(Candidate(), request, DateTimeOffset.UnixEpoch);

    [Fact]
    public void ModelscopeChannelBuildsTheModelscopeTransformer()
    {
        var transformer = Assert.IsType<ModelscopeOutboundTransformer>(
            Create(ChatRequest()));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(ChatRequest());

        Assert.Equal("https://api.modelscope.cn/v1/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth?.Type);
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void ModelscopeChannelRoutesImageRequestsToTheTaskApi()
    {
        var transformer = Assert.IsType<ModelscopeOutboundTransformer>(
            Create(ImageRequest()));

        var httpRequest = transformer.TransformRequest(ImageRequest());

        Assert.Equal("https://api.modelscope.cn/v1/images/generations", httpRequest.Url);
        Assert.Equal(ApiFormats.ModelScopeImage, httpRequest.ApiFormat);
        Assert.Equal(RequestTypes.Image, httpRequest.RequestType);
        Assert.Equal(
            "true",
            HttpClientUtils.HeaderGet(httpRequest.Headers, "X-ModelScope-Async-Mode"));
        Assert.Equal("provider-key", httpRequest.Auth?.ApiKey);
        Assert.Equal("provider-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void ModelscopeChannelKeepsTheBaseUrlForImageTasks()
    {
        var candidate = Candidate("https://api.modelscope.cn");
        var transformer = Assert.IsType<ModelscopeOutboundTransformer>(
            ChannelOutboundFactory.Create(candidate, ChatRequest(), DateTimeOffset.UnixEpoch));

        Assert.Equal(
            "https://api.modelscope.cn/v1/chat/completions",
            transformer.TransformRequest(ChatRequest()).Url);
        Assert.Equal(
            "https://api.modelscope.cn/images/generations",
            transformer.TransformRequest(ImageRequest()).Url);
    }

    [Fact]
    public void TheChannelExecutorServesImageTaskPolling()
    {
        // channel_llm.go:1096-1108: the reference puts ch.HTTPClient on the
        // Modelscope config, so the async task poll follows the channel proxy.
        var channelExecutor = new ScriptedExecutor
        {
            OnDo = (_, request, _) =>
            {
                Assert.Equal("https://api.modelscope.cn/v1/tasks/task-1", request.Url);
                return new HttpResponseData
                {
                    StatusCode = 200,
                    Body = Encoding.UTF8.GetBytes(
                        """{"task_id":"task-1","task_status":"SUCCEED","output_images":["data:image/png;base64,QUJD"]}"""),
                };
            },
        };

        var transformer = ChannelOutboundFactory.Create(
            Candidate(),
            ImageRequest(),
            DateTimeOffset.UnixEpoch,
            channelExecutor);
        var httpRequest = transformer.TransformRequest(ImageRequest());
        var response = new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes("""{"task_id":"task-1","task_status":"PENDING"}"""),
            Request = httpRequest,
        };

        var result = transformer.TransformResponse(response);

        Assert.Equal("QUJD", Assert.Single(result.Image!.Data).B64JSON);
        Assert.Single(channelExecutor.Requests);
    }

    [Fact]
    public void ModelscopeChannelWithoutKeysIsRejected()
    {
        var candidate = Candidate();
        candidate.Credentials = "{}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                candidate,
                ChatRequest(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel modelscope-wiring", error.Message);
    }
}
