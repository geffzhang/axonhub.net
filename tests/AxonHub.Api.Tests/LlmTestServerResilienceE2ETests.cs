using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Billing;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

[Collection(OrleansCollection.Name)]
public sealed class LlmTestServerResilienceE2ETests(GrainClusterFixture fixture)
{
    private const long PrimaryChannelId = 9981;
    private const long FallbackChannelId = 9982;
    private const string ChatPath = "/v1/chat/completions";

    private static string ServerUrl => LlmTestServerE2ETests.ServerUrl;

    private static string ApiKey => LlmTestServerE2ETests.ApiKey;

    private readonly RecordingRequestChainRecorder _recorder = new();
    private readonly FixedUsageCostService _usageCostService = new();

    [LlmTestServerFact]
    public async Task Transient429FallsBackToBAndBillsOnlyFinalUsage()
    {
        const string model = "resilience-transient-429";
        const string answer = "Recovered on provider B.";
        await ConfigureAsync(
            model,
            RateLimitError("temporary rate limit"),
            new { content = answer });

        var context = await SendAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1),
            PrimaryChannel(model),
            FallbackChannel(model));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var response = JsonDocument.Parse(ResponseBody(context));
        var root = response.RootElement;
        Assert.Equal(
            answer,
            root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 429));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq", 200));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
        Assert.Equal(new[] { PrimaryChannelId, FallbackChannelId }, _recorder.RequestChannels);

        var usage = Assert.Single(_recorder.UsageLogs);
        Assert.Equal(FallbackChannelId, usage.ChannelId);
        Assert.Equal(root.GetProperty("usage").GetProperty("prompt_tokens").GetInt64(), usage.PromptTokens);
        Assert.Equal(root.GetProperty("usage").GetProperty("completion_tokens").GetInt64(), usage.CompletionTokens);
        Assert.Equal(root.GetProperty("usage").GetProperty("total_tokens").GetInt64(), usage.TotalTokens);
        Assert.Equal(0.00006, usage.TotalCost!.Value, 12);
        Assert.Equal("retry-final", usage.CostPriceReferenceId);
        Assert.Equal(0.00006, root.GetProperty("usage").GetProperty("cost").GetDouble(), 12);

        Assert.Equal(2, _usageCostService.Computed.Count);
        Assert.All(
            _usageCostService.Computed,
            cost =>
            {
                Assert.Equal(FallbackChannelId, cost.ChannelId);
                Assert.Equal(model, cost.ModelId);
                Assert.Equal(usage.PromptTokens, cost.Usage.PromptTokens);
            });
    }

    [LlmTestServerFact]
    public async Task Transient429NeverExceedsConfiguredAttemptBudget()
    {
        const string model = "resilience-429-budget";
        await ConfigureAsync(
            model,
            RateLimitError("temporary rate limit 1"),
            RateLimitError("temporary rate limit 2"),
            RateLimitError("temporary rate limit 3"),
            RateLimitError("must not be requested"));

        var context = await SendAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1),
            PrimaryChannel(model),
            FallbackChannel(model));

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Contains("rate_limit_exceeded", ResponseBody(context), StringComparison.Ordinal);
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 429));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq", 429));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
    }

    [LlmTestServerFact]
    public async Task InsufficientQuota429StopsWithoutFallback()
    {
        const string model = "resilience-insufficient-quota";
        await ConfigureAsync(
            model,
            ProviderError(429, "insufficient_quota", "account quota exhausted"),
            new { content = "Fallback must not be used." });

        var context = await SendAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1),
            PrimaryChannel(model),
            FallbackChannel(model));

        AssertPublicError(context, StatusCodes.Status429TooManyRequests, "insufficient_quota");
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 429));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(0, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
    }

    [LlmTestServerFact]
    public async Task InsufficientBalance429StopsWithoutFallback()
    {
        const string model = "resilience-insufficient-balance";
        await ConfigureAsync(
            model,
            ProviderError(429, "insufficient_balance", "account balance exhausted"),
            new { content = "Fallback must not be used." });

        var context = await SendAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1),
            PrimaryChannel(model),
            FallbackChannel(model));

        AssertPublicError(context, StatusCodes.Status429TooManyRequests, "insufficient_balance");
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 429));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(0, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
    }

    [LlmTestServerTheory]
    [InlineData(StatusCodes.Status401Unauthorized, "invalid_api_key")]
    [InlineData(StatusCodes.Status403Forbidden, "permission_denied")]
    public async Task UnauthorizedAndForbiddenAreNotRetriedOrWrapped(int status, string code)
    {
        const string model = "resilience-auth-case";
        await ConfigureAsync(
            model,
            ProviderError(status, code, "provider rejected credentials"),
            new { content = "Fallback must not be used." });

        var context = await SendAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1),
            PrimaryChannel(model),
            FallbackChannel(model));

        AssertPublicError(context, status, code);
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", status));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(0, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
    }

    [LlmTestServerFact]
    public async Task NonStreamContentFilterIsNotRetried()
    {
        const string model = "resilience-filter-non-stream";
        await ConfigureAsync(
            model,
            ProviderError(400, "content_filter", "blocked by content filter"),
            new { content = "Filtered fallback must not be used." });

        var context = await SendAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1),
            PrimaryChannel(model),
            FallbackChannel(model));

        AssertPublicError(context, StatusCodes.Status400BadRequest, "content_filter");
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 400));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(0, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
    }

    [LlmTestServerFact]
    public async Task StreamContentFilterIsNotRetried()
    {
        const string model = "resilience-filter-stream";
        await ConfigureAsync(
            model,
            ProviderError(400, "content_filter", "blocked by content filter"),
            new { content = "Filtered fallback must not be used." });

        var context = await SendAsync(
            RequestBody(model, stream: true),
            CreateRetryPolicy(maxChannelRetries: 1),
            PrimaryChannel(model),
            FallbackChannel(model));

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = ResponseBody(context);
        Assert.Contains("content_filter", body, StringComparison.Ordinal);
        Assert.DoesNotContain("data:[DONE]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Filtered fallback must not be used.", body, StringComparison.Ordinal);
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 400));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(0, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
    }

    private async Task<DefaultHttpContext> SendAsync(
        string body,
        RetryPolicy policy,
        params ChannelDescriptor[] channels)
    {
        fixture.Channels.Set(channels);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var context = NewContext(body);
        var orchestrator = new ChatCompletionOrchestrator(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            new HttpExecutor(http),
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(policy),
            fixture.CandidateSelector,
            TimeProvider.System,
            _recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            usageCostService: _usageCostService);

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);
        return context;
    }

    private static RetryPolicy CreateRetryPolicy(int maxChannelRetries, int maxSingleChannelRetries = 0)
        => new()
        {
            Enabled = true,
            MaxChannelRetries = maxChannelRetries,
            MaxSingleChannelRetries = maxSingleChannelRetries,
            RetryDelayMs = 0,
            LoadBalancerStrategy = RetryPolicy.FailoverStrategy,
        };

    private static ChannelDescriptor PrimaryChannel(string model)
        => NewChannel(PrimaryChannelId, model, "openai", ServerUrl + "/openai/v1", orderingWeight: 100);

    private static ChannelDescriptor FallbackChannel(string model)
        => NewChannel(FallbackChannelId, model, "openai", ServerUrl + "/groq/openai/v1", orderingWeight: 10);

    private static ChannelDescriptor NewChannel(
        long id,
        string model,
        string type,
        string baseUrl,
        long orderingWeight)
        => new()
        {
            Id = id,
            Name = "resilience-channel-" + id,
            OrderingWeight = orderingWeight,
            Type = type,
            BaseUrl = baseUrl,
            Credentials = "{\"apiKey\":\"" + ApiKey + "\"}",
            SupportedModels = "[\"" + model + "\"]",
        };

    private static async Task ConfigureAsync(string model, params object[] responses)
    {
        await LlmTestServerE2ETests.AdminClient.ResetAsync();
        await LlmTestServerE2ETests.AdminClient.ConfigureAsync(new
        {
            requiredBearerToken = ApiKey,
            models = new[] { new { id = model, kind = "chat" } },
            chatScenarios = new[]
            {
                new
                {
                    id = "resilience-" + model,
                    modelId = model,
                    responses,
                },
            },
        });
    }

    private static object RateLimitError(string message)
        => ProviderError(StatusCodes.Status429TooManyRequests, "rate_limit_exceeded", message);

    private static object ProviderError(int statusCode, string code, string message)
        => new
        {
            error = new
            {
                statusCode,
                code,
                message,
            },
        };

    private static string RequestBody(string model, bool stream = false)
        => "{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]"
            + (stream ? ",\"stream\":true}" : "}");

    private static DefaultHttpContext NewContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = ChatPath;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseBody(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static void AssertPublicError(DefaultHttpContext context, int expectedStatus, string expectedCode)
    {
        Assert.True(
            expectedStatus == context.Response.StatusCode,
            $"Expected status {expectedStatus}, got {context.Response.StatusCode}. "
                + $"Response body: {ResponseBody(context)}");
        Assert.Contains(expectedCode, ResponseBody(context), StringComparison.Ordinal);
    }

    private static UsageCost FinalUsageCost()
        => new(
            [new CostItem { ItemCode = PriceItemCodes.PromptTokens, Quantity = 1, Subtotal = 0.00006m }],
            0.00006m,
            "retry-final");

    private sealed class FixedUsageCostService : IUsageCostService
    {
        public List<(long ChannelId, string ModelId, Usage Usage)> Computed { get; } = [];

        public UsageCost? Compute(long channelId, string modelId, Usage usage)
        {
            Computed.Add((channelId, modelId, usage));
            return FinalUsageCost();
        }

        public bool InjectUsageCostEnabled() => true;
    }
}

public sealed class LlmTestServerTheoryAttribute : TheoryAttribute
{
    public LlmTestServerTheoryAttribute()
    {
        if (LlmTestServerE2ETests.ServerUrl.Length == 0)
        {
            Skip = "set AXONHUB_E2E_LLM_TEST_SERVER_URL (e.g. http://localhost:8080) "
                + "and start E:\\GitHub\\LlmTestServer to run the real-provider E2E";
        }
    }
}
