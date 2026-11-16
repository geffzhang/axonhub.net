using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/task5_accounting_test.go: a usage log is
/// written only when the aggregator's provenance proves the usage final
/// (accountingMayBeFinal), and the reason for refusing it is reported
/// (accountingUnavailableReason).
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class AccountingProvenanceTests(GrainClusterFixture fixture)
{
    // ------------------------------------------------------------------
    // Accounting gate helpers (accountingMayBeFinal / accountingUnavailableReason)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(AccountingEvidence.ExplicitFinal, ApiFormats.OpenAIChatCompletion, SourceEndReason.Error, true, true)]
    [InlineData(AccountingEvidence.CleanEndCandidate, ApiFormats.OpenAIChatCompletion, SourceEndReason.CleanEof, true, true)]
    [InlineData(AccountingEvidence.CleanEndCandidate, ApiFormats.OpenAIChatCompletion, SourceEndReason.Semantic, true, true)]
    [InlineData(AccountingEvidence.CleanEndCandidate, ApiFormats.OpenAIChatCompletion, SourceEndReason.Error, true, false)]
    [InlineData(AccountingEvidence.Unavailable, ApiFormats.OpenAIChatCompletion, SourceEndReason.CleanEof, true, false)]
    [InlineData(AccountingEvidence.Unavailable, ApiFormats.AnthropicMessage, SourceEndReason.CleanEof, true, false)]
    [InlineData(AccountingEvidence.Unavailable, ApiFormats.OpenAIResponse, SourceEndReason.CleanEof, true, false)]
    // Other adapters keep their established usage behavior until they report
    // provenance: the gate falls back to "usage is present".
    [InlineData(AccountingEvidence.Unavailable, ApiFormats.OpenAICompletion, SourceEndReason.CleanEof, true, true)]
    [InlineData(AccountingEvidence.Unavailable, ApiFormats.OpenAICompletion, SourceEndReason.CleanEof, false, false)]
    public void AccountingMayBeFinalFollowsProvenance(
        AccountingEvidence evidence,
        string format,
        SourceEndReason sourceEnd,
        bool hasUsage,
        bool expected)
    {
        var meta = new ResponseMeta
        {
            Usage = hasUsage ? new Usage { TotalTokens = 1 } : null,
            AccountingEvidence = evidence,
        };

        Assert.Equal(expected, RequestChainPersistenceMiddleware.AccountingMayBeFinal(format, meta, sourceEnd));
    }

    [Fact]
    public void BudgetSourceEndRejectsCleanCandidateZeroUsage()
    {
        var meta = new ResponseMeta
        {
            Usage = new Usage(),
            AccountingEvidence = AccountingEvidence.CleanEndCandidate,
        };

        Assert.False(RequestChainPersistenceMiddleware.AccountingMayBeFinal(
            ApiFormats.OpenAIChatCompletion, meta, SourceEndReason.Budget));
        Assert.Equal("budget", RequestChainPersistenceMiddleware.AccountingUnavailableReason(
            meta, SourceEndReason.Budget));
    }

    // Port of TestStreamDeliveryAccounting_UnavailableReasons.
    [Theory]
    [InlineData(AccountingEvidence.Unavailable, SourceEndReason.CleanEof, "absent")]
    [InlineData(AccountingEvidence.CleanEndCandidate, SourceEndReason.Canceled, "truncated")]
    [InlineData(AccountingEvidence.Unavailable, SourceEndReason.Error, "provider-error")]
    [InlineData(AccountingEvidence.Unavailable, SourceEndReason.Budget, "budget")]
    public void AccountingUnavailableReasonMatchesSourceEnd(
        AccountingEvidence evidence,
        SourceEndReason sourceEnd,
        string expected)
    {
        Assert.Equal(
            expected,
            RequestChainPersistenceMiddleware.AccountingUnavailableReason(
                new ResponseMeta { AccountingEvidence = evidence },
                sourceEnd));
    }

    // ------------------------------------------------------------------
    // Chat clean EOF (TestStreamDeliveryAccounting_ChatCleanEOFAndAmbiguousPlaceholders)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ChatFinishOnlyNonzeroCleanEofWritesUsage()
    {
        fixture.Channels.Set(TestChannels.OpenAi(7101, "accounting-chat-clean"));
        var recorder = await RunChatStreamAsync(
            "accounting-chat-clean",
            TestChannels.Data(
                "{\"id\":\"chat\",\"model\":\"accounting-chat-clean\",\"choices\":[{\"index\":0,"
                + "\"finish_reason\":\"stop\",\"delta\":{}}],"
                + "\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":34,\"total_tokens\":46}}"));

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(12, usage.PromptTokens);
        Assert.Equal(34, usage.CompletionTokens);
        Assert.Equal(46, usage.TotalTokens);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, usage.Format);

        Assert.Single(recorder.ExecutionResults);
    }

    [Fact]
    public async Task ChatFinishOnlyZeroCleanEofWritesUsage()
    {
        fixture.Channels.Set(TestChannels.OpenAi(7102, "accounting-chat-clean-zero"));
        var recorder = await RunChatStreamAsync(
            "accounting-chat-clean-zero",
            TestChannels.Data(
                "{\"id\":\"chat\",\"model\":\"accounting-chat-clean-zero\",\"choices\":[{\"index\":0,"
                + "\"finish_reason\":\"stop\",\"delta\":{}}],"
                + "\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}}"));

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(0, usage.TotalTokens);
        Assert.Single(recorder.ExecutionResults);
    }

    [Fact]
    public async Task ChatChoicesLessFinalUsageIsExplicitIncludingZero()
    {
        fixture.Channels.Set(TestChannels.OpenAi(7103, "accounting-chat-final"));
        var recorder = await RunChatStreamAsync(
            "accounting-chat-final",
            TestChannels.Data(
                """{"id":"chat","model":"accounting-chat-final","choices":[{"index":0,"finish_reason":"stop","delta":{}}]}"""),
            TestChannels.Data(
                """{"id":"chat","model":"accounting-chat-final","choices":[],"usage":{"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}}"""));

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(0, usage.TotalTokens);
    }

    [Fact]
    public async Task ChatRepeatedZeroPlaceholdersCleanEofSkipsUsage()
    {
        fixture.Channels.Set(TestChannels.OpenAi(7104, "accounting-chat-placeholder"));
        var recorder = await RunChatStreamAsync(
            "accounting-chat-placeholder",
            TestChannels.Data(
                "{\"id\":\"chat\",\"model\":\"accounting-chat-placeholder\",\"choices\":[{\"index\":0,"
                + "\"finish_reason\":\"stop\",\"delta\":{}}],"
                + "\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}}"),
            TestChannels.Data(
                "{\"id\":\"chat\",\"model\":\"accounting-chat-placeholder\",\"choices\":[{\"index\":0,"
                + "\"finish_reason\":\"stop\",\"delta\":{}}],"
                + "\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}}"));

        Assert.Empty(recorder.UsageLogs);

        // The execution still finalizes; only the fabricated usage is withheld.
        Assert.Single(recorder.ExecutionResults);
        Assert.Equal(
            "absent",
            RequestChainPersistenceMiddleware.AccountingUnavailableReason(
                new ResponseMeta { AccountingEvidence = AccountingEvidence.Unavailable },
                SourceEndReason.CleanEof));
    }

    [Fact]
    public async Task ChatRepeatedZeroPlaceholdersStreamErrorSkipsUsage()
    {
        fixture.Channels.Set(TestChannels.OpenAi(7105, "accounting-chat-truncated"));
        var recorder = await RunChatStreamAsync(
            "accounting-chat-truncated",
            new IOException("provider stream interrupted"),
            TestChannels.Data(
                "{\"id\":\"chat\",\"model\":\"accounting-chat-truncated\",\"choices\":[{\"index\":0,"
                + "\"finish_reason\":\"stop\",\"delta\":{}}],"
                + "\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}}"),
            TestChannels.Data(
                "{\"id\":\"chat\",\"model\":\"accounting-chat-truncated\",\"choices\":[{\"index\":0,"
                + "\"finish_reason\":\"stop\",\"delta\":{}}],"
                + "\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}}"));

        Assert.Empty(recorder.UsageLogs);
        Assert.Equal(
            "provider-error",
            RequestChainPersistenceMiddleware.AccountingUnavailableReason(
                new ResponseMeta { AccountingEvidence = AccountingEvidence.Unavailable },
                SourceEndReason.Error));
    }

    // ------------------------------------------------------------------
    // Responses terminal usage (TestStreamDeliveryAccounting_ResponseFailedTerminalRetainsUsage)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(12, 34)]
    [InlineData(0, 0)]
    public async Task ResponsesFailedTerminalRetainsUsage(long input, long output)
    {
        var recorder = new RecordingRequestChainRecorder();
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(
                [
                    TestChannels.Data(
                        "{\"type\":\"response.failed\",\"response\":{\"id\":\"resp-failed\",\"status\":\"failed\","
                        + "\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output
                        + ",\"total_tokens\":" + (input + output) + "}}}",
                        "response.failed"),
                ]),
        };
        var orchestrator = NewOrchestrator(
            new ResponsesInboundTransformer(),
            executor,
            recorder,
            new FixedSelector(CandidateFor("accounting-responses", 7201, "openai", ApiFormats.OpenAIResponse)));

        var result = await orchestrator.ProcessAsync(
            ResponsesRequest(
                """{"model":"accounting-responses","input":"hello","stream":true}"""),
            RequestIdentity.Default,
            onRequestCreated: null);

        ConsumeStream(result.Stream);

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(input, usage.PromptTokens);
        Assert.Equal(output, usage.CompletionTokens);
        Assert.Equal(input + output, usage.TotalTokens);
        Assert.Equal(ApiFormats.OpenAIResponse, usage.Format);

        var executionResult = Assert.Single(recorder.ExecutionResults);
        Assert.Equal("failed", executionResult.Status);
    }

    // ------------------------------------------------------------------
    // Anthropic message_stop (TestStreamDeliveryAccounting_RealAggregatorsPersistTerminalUsage)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(12, 34)]
    [InlineData(0, 0)]
    public async Task AnthropicMessageStopPersistsTerminalUsage(long input, long output)
    {
        var recorder = new RecordingRequestChainRecorder();
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(
                [
                    TestChannels.Data(
                        "{\"type\":\"message_start\",\"message\":{\"id\":\"msg\",\"type\":\"message\","
                        + "\"role\":\"assistant\",\"content\":[],\"model\":\"accounting-anthropic\","
                        + "\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":0}}}",
                        "message_start"),
                    TestChannels.Data(
                        "{\"type\":\"message_delta\",\"usage\":{\"input_tokens\":" + input
                        + ",\"output_tokens\":" + output + "},\"delta\":{\"stop_reason\":\"end_turn\"}}",
                        "message_delta"),
                    TestChannels.Data("""{"type":"message_stop"}""", "message_stop"),
                ]),
        };
        var orchestrator = NewOrchestrator(
            new AnthropicInboundTransformer(),
            executor,
            recorder,
            new FixedSelector(CandidateFor("accounting-anthropic", 7301, "anthropic", ApiFormats.AnthropicMessage)));

        var result = await orchestrator.ProcessAsync(
            AnthropicRequest(
                """{"model":"accounting-anthropic","max_tokens":64,"messages":[{"role":"user","content":"hi"}],"stream":true}"""),
            RequestIdentity.Default,
            onRequestCreated: null);

        ConsumeStream(result.Stream);

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(input, usage.PromptTokens);
        Assert.Equal(output, usage.CompletionTokens);
        Assert.Equal(input + output, usage.TotalTokens);
        Assert.Equal(ApiFormats.AnthropicMessage, usage.Format);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private ChatCompletionOrchestrator NewOrchestrator(
        IInboundTransformer inbound,
        ScriptedExecutor executor,
        RecordingRequestChainRecorder recorder,
        IModelCandidateSelector selector)
        => new(
            fixture.Cluster.GrainFactory,
            inbound,
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy { Enabled = false }),
            selector,
            TimeProvider.System,
            recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

    private async Task<RecordingRequestChainRecorder> RunChatStreamAsync(
        string model,
        params StreamEvent[] chunks)
        => await RunChatStreamAsync(model, null, chunks);

    private async Task<RecordingRequestChainRecorder> RunChatStreamAsync(
        string model,
        Exception? streamError,
        params StreamEvent[] chunks)
    {
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(chunks, streamError),
        };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = new ChatCompletionOrchestrator(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy { Enabled = false }),
            fixture.CandidateSelector,
            TimeProvider.System,
            recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

        var request = TestChannels.ChatRequest(model, stream: true);
        var result = await orchestrator.ProcessAsync(request);

        ConsumeStream(result.Stream);
        return recorder;
    }

    private static void ConsumeStream(IStream<StreamEvent>? stream)
    {
        Assert.NotNull(stream);
        while (stream.Next())
        {
            _ = stream.Current;
        }

        // The delivery acknowledgement is what lets the request row finalize as
        // completed; every transport writer acknowledges the terminal event.
        stream.AcknowledgeDelivery();
        stream.Dispose();
    }

    private static ChannelCandidate CandidateFor(
        string model,
        long channelId,
        string type,
        string apiFormat)
        => new()
        {
            ChannelId = channelId,
            ChannelName = "channel-" + channelId,
            ActualModel = model,
            Type = type,
            ApiFormat = apiFormat,
            BaseUrl = "http://channel-" + channelId + ".test",
            Credentials = "{\"apiKey\":\"sk-" + channelId + "\"}",
            Models =
            [
                new ChannelCandidateModel
                {
                    RequestModel = model,
                    ActualModel = model,
                    Source = "direct",
                },
            ],
        };

    private static HttpRequestData ResponsesRequest(string body)
        => new()
        {
            Method = "POST",
            Url = "http://gateway.test/v1/responses",
            Path = "/v1/responses",
            Query = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
        };

    private static HttpRequestData AnthropicRequest(string body)
        => new()
        {
            Method = "POST",
            Url = "http://gateway.test/v1/messages",
            Path = "/v1/messages",
            Query = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
        };

    private sealed class FixedSelector(params ChannelCandidate[] candidates) : IModelCandidateSelector
    {
        public Task<CandidateSelection> SelectAsync(
            LlmRequest request,
            RequestIdentity identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CandidateSelection(candidates, RoutingPolicy: null));
    }
}
