using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Billing;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Behavioural tests for the cost half of the request-chain persistence port
/// (internal/server/orchestrator/request.go: injectUsageCost and the
/// CreateUsageLog cost columns). Injection is gated by
/// system_inject_usage_cost; the usage-log cost is not.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class UsageCostInjectionTests(GrainClusterFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        IRequestChainRecorder recorder,
        IUsageCostService costService)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 3,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            fixture.CandidateSelector,
            new TestTimeProvider(Now),
            recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            usageCostService: costService);

    private static UsageCost SampleCost()
        => new(
            [new CostItem { ItemCode = PriceItemCodes.PromptTokens, Quantity = 1, Subtotal = 0.00006m }],
            0.00006m,
            "ref-1");

    private static double ResponseCost(HttpResponseData response)
    {
        using var document = JsonDocument.Parse(response.Body);
        return document.RootElement.GetProperty("usage").GetProperty("cost").GetDouble();
    }

    private static bool ResponseHasCost(HttpResponseData response)
    {
        using var document = JsonDocument.Parse(response.Body);
        return document.RootElement.GetProperty("usage").TryGetProperty("cost", out _);
    }

    [Fact]
    public async Task EnabledInjectionWritesCostOntoResponseAndUsageLog()
    {
        fixture.Channels.Set(TestChannels.OpenAi(6101, "cost-inject"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("cost-inject"),
        };
        var recorder = new RecordingRequestChainRecorder();
        var costService = new StubUsageCostService { Enabled = true, Cost = SampleCost() };

        var result = await NewOrchestrator(executor, recorder, costService)
            .ProcessAsync(TestChannels.ChatRequest("cost-inject"));

        Assert.Equal(0.00006, ResponseCost(result.Response!), 12);

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(0.00006, usage.TotalCost!.Value, 12);
        Assert.Equal(
            "[{\"itemCode\":\"prompt_tokens\",\"quantity\":1,\"subtotal\":\"0.00006\"}]",
            usage.CostItems);
        Assert.Equal("ref-1", usage.CostPriceReferenceId);
    }

    [Fact]
    public async Task DisabledInjectionStillPricesTheUsageLog()
    {
        fixture.Channels.Set(TestChannels.OpenAi(6102, "cost-disabled"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("cost-disabled"),
        };
        var recorder = new RecordingRequestChainRecorder();
        var costService = new StubUsageCostService { Enabled = false, Cost = SampleCost() };

        var result = await NewOrchestrator(executor, recorder, costService)
            .ProcessAsync(TestChannels.ChatRequest("cost-disabled"));

        Assert.False(ResponseHasCost(result.Response!));

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(0.00006, usage.TotalCost!.Value, 12);
        Assert.Equal("ref-1", usage.CostPriceReferenceId);
    }

    [Fact]
    public async Task MissingPriceClearsResponseCostAndWritesNullCostItems()
    {
        fixture.Channels.Set(TestChannels.OpenAi(6103, "cost-missing"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("cost-missing"),
        };
        var recorder = new RecordingRequestChainRecorder();
        var costService = new StubUsageCostService { Enabled = true, Cost = null };

        var result = await NewOrchestrator(executor, recorder, costService)
            .ProcessAsync(TestChannels.ChatRequest("cost-missing"));

        Assert.False(ResponseHasCost(result.Response!));

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Null(usage.TotalCost);
        Assert.Equal("null", usage.CostItems);
        Assert.Null(usage.CostPriceReferenceId);
    }

    [Fact]
    public async Task StreamChunksCarryInjectedCost()
    {
        fixture.Channels.Set(TestChannels.OpenAi(6104, "cost-stream"));
        var chunks = new List<StreamEvent>
        {
            TestChannels.Data("{\"id\":\"chatcmpl-cost\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"cost-stream\",\"choices\":[{\"index\":0,"
                + "\"delta\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":null}]}"),
            TestChannels.Data("{\"id\":\"chatcmpl-cost\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"cost-stream\",\"choices\":[{\"index\":0,\"delta\":{},"
                + "\"finish_reason\":\"stop\"}],"
                + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}"),
            TestChannels.Data("[DONE]"),
        };
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(chunks),
        };
        var recorder = new RecordingRequestChainRecorder();
        var costService = new StubUsageCostService { Enabled = true, Cost = SampleCost() };

        var result = await NewOrchestrator(executor, recorder, costService)
            .ProcessAsync(TestChannels.ChatRequest("cost-stream", stream: true));

        var events = new List<string>();
        while (result.Stream!.Next())
        {
            if (result.Stream.Current is { } streamEvent)
            {
                events.Add(Encoding.UTF8.GetString(streamEvent.Data));
            }
        }

        // A normal consumer read completes the delivery; without the
        // acknowledgement the lifecycle treats the close as a downstream
        // cancel and refuses the usage as non-final (C292/C294).
        result.Stream.AcknowledgeDelivery();
        result.Stream.Dispose();

        var usageChunk = Assert.Single(
            events,
            data =>
            {
                if (!data.StartsWith('{'))
                {
                    return false;
                }

                using var chunkDocument = JsonDocument.Parse(data);
                return chunkDocument.RootElement.TryGetProperty("usage", out var chunkUsage)
                    && chunkUsage.ValueKind == JsonValueKind.Object
                    && chunkUsage.TryGetProperty("cost", out _);
            });
        using var document = JsonDocument.Parse(usageChunk);
        Assert.Equal(0.00006, document.RootElement.GetProperty("usage").GetProperty("cost").GetDouble(), 12);

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(0.00006, usage.TotalCost!.Value, 12);
    }

    [Fact]
    public async Task RealCostServiceUsesTheStoredPriceAndSetting()
    {
        fixture.Channels.Set(TestChannels.OpenAi(6105, "cost-real"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("cost-real"),
        };
        var recorder = new RecordingRequestChainRecorder();
        var pricing = new StubChannelPricingSource();
        pricing.Prices.Add(new ChannelModelPriceRecord
        {
            ChannelId = 6105,
            ModelId = "cost-real",
            ReferenceId = "ref-real",
            Price = """
                {"items":[{"itemCode":"prompt_tokens",
                  "pricing":{"mode":"usage_per_unit","usagePerUnit":"1"}}]}
                """,
        });
        pricing.Values[UsageCostService.InjectUsageCostKey] = "true";
        var costService = new UsageCostService(pricing, new TestTimeProvider(Now));

        var result = await NewOrchestrator(executor, recorder, costService)
            .ProcessAsync(TestChannels.ChatRequest("cost-real"));

        // ScriptedExecutor returns one prompt token at $1/1M => 0.000001.
        Assert.Equal(0.000001, ResponseCost(result.Response!), 12);

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(0.000001, usage.TotalCost!.Value, 12);
        Assert.Equal(
            "[{\"itemCode\":\"prompt_tokens\",\"quantity\":1,\"subtotal\":\"0.000001\"}]",
            usage.CostItems);
        Assert.Equal("ref-real", usage.CostPriceReferenceId);
    }

    private sealed class StubUsageCostService : IUsageCostService
    {
        public bool Enabled { get; set; }

        public UsageCost? Cost { get; set; }

        public UsageCost? Compute(long channelId, string modelId, Usage usage)
        {
            _ = channelId;
            _ = modelId;
            _ = usage;
            return Cost;
        }

        public bool InjectUsageCostEnabled() => Enabled;
    }

    private sealed class StubChannelPricingSource : IChannelPricingSource
    {
        public List<ChannelModelPriceRecord> Prices { get; } = [];

        public Dictionary<string, string?> Values { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<ChannelModelPriceRecord> ListEnabledChannelPrices(long channelId)
            => Prices.Where(price => price.ChannelId == channelId).ToList();

        public string? GetSystemValue(string key)
            => Values.TryGetValue(key, out var value) ? value : null;
    }
}
