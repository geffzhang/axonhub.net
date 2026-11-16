using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/auto_reasoning_effort_test.go,
/// model_access_test.go and quota_minute_test.go: the two inbound middlewares
/// the orchestrator mounts before model mapping and channel selection, their
/// ordering, and the enforceQuota 403 envelope.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class InboundMiddlewareTests(GrainClusterFixture fixture)
{
    // ------------------------------------------------------------------
    // splitAutoReasoningEffortModel (auto_reasoning_effort_test.go:17-81)
    // ------------------------------------------------------------------

    [Fact]
    public void SplitModelMovesSupportedSuffixesIntoTheEffort()
    {
        (string Name, string Model, string BaseModel, string Effort, bool Normalized)[] cases =
        [
            ("supports xhigh", "gpt-5.4-xhigh", "gpt-5.4", "xhigh", true),
            ("supports max", "gpt-5.4-max", "gpt-5.4", "max", true),
            ("ignores qwen max model", "qwen3.7-max", "", "", false),
            ("ignores namespaced qwen max model", "qwen/qwen3-max", "", "", false),
            ("supports uppercase suffix", "gpt-5.4-HIGH", "gpt-5.4", "high", true),
            ("ignores unsupported suffix", "gpt-5.4-ultra", "", "", false),
            ("ignores missing base model", "-high", "", "", false),
            ("ignores model without suffix", "gpt-5.4", "", "", false),
        ];

        foreach (var one in cases)
        {
            var normalized = AutoReasoningEffort.TrySplit(
                one.Model,
                out var baseModel,
                out var effort);

            Assert.True(one.Normalized == normalized, one.Name + ": normalized");
            Assert.True(
                string.Equals(one.BaseModel, baseModel, StringComparison.Ordinal),
                one.Name + ": expected base " + one.BaseModel + ", got " + baseModel);
            Assert.True(
                string.Equals(one.Effort, effort, StringComparison.Ordinal),
                one.Name + ": expected effort " + one.Effort + ", got " + effort);
        }
    }

    [Theory]
    [InlineData("gpt-5.4-medium", "gpt-5.4", "medium")]
    [InlineData("gpt-5.4-low", "gpt-5.4", "low")]
    [InlineData("openrouter/gpt-5.4-xhigh", "openrouter/gpt-5.4", "xhigh")]
    [InlineData("QWEN3-MAX", "", "")]
    public void SplitModelHandlesTheRemainingEffortSpellings(
        string model,
        string expectedBase,
        string expectedEffort)
    {
        var normalized = AutoReasoningEffort.TrySplit(model, out var baseModel, out var effort);

        Assert.Equal(expectedBase.Length > 0, normalized);
        Assert.Equal(expectedBase, baseModel);
        Assert.Equal(expectedEffort, effort);
    }

    [Fact]
    public void QwenMaxDetectionLooksOnlyAtTheLastPathSegment()
    {
        Assert.True(AutoReasoningEffort.IsQwenMaxModel("qwen3.7-max"));
        Assert.True(AutoReasoningEffort.IsQwenMaxModel(" QWEN/qwen3-MAX "));
        Assert.True(AutoReasoningEffort.IsQwenMaxModel("other/qwen3-max"));
        Assert.False(AutoReasoningEffort.IsQwenMaxModel("qwen3.7-high"));
        Assert.False(AutoReasoningEffort.IsQwenMaxModel("not-qwen/max"));
    }

    // ------------------------------------------------------------------
    // autoReasoningEffortMiddleware (auto_reasoning_effort_test.go:83-181)
    // ------------------------------------------------------------------

    [Fact]
    public void MiddlewareAppliesTheSuffixOnlyWhenTheSettingIsOn()
    {
        (string Name, bool Enabled, string Model, string Effort, string ExpectedModel, string ExpectedEffort)[] cases =
        [
            ("disabled setting leaves request unchanged", false, "gpt-5.4-xhigh", "", "gpt-5.4-xhigh", ""),
            ("enabled setting applies xhigh suffix", true, "gpt-5.4-xhigh", "", "gpt-5.4", "xhigh"),
            ("enabled setting applies max suffix", true, "gpt-5.4-max", "", "gpt-5.4", "max"),
            ("enabled setting keeps qwen max model unchanged", true, "qwen3.7-max", "", "qwen3.7-max", ""),
            (
                "suffix reasoning effort overrides explicit request value",
                true,
                "gpt-5.4-xhigh",
                "high",
                "gpt-5.4",
                "xhigh"),
            ("unsupported suffix is ignored", true, "gpt-5.4-ultra", "", "gpt-5.4-ultra", ""),
        ];

        foreach (var one in cases)
        {
            var middleware = new AutoReasoningEffortMiddleware(
                ModelSettings.Default with { AutoReasoningEffort = one.Enabled });
            var request = new LlmRequest { Model = one.Model, ReasoningEffort = one.Effort };

            var result = middleware.TransformInboundLlmRequest(request);

            Assert.Same(request, result);
            Assert.True(
                string.Equals(one.ExpectedModel, result.Model, StringComparison.Ordinal),
                one.Name + ": expected model " + one.ExpectedModel + ", got " + result.Model);
            Assert.True(
                string.Equals(one.ExpectedEffort, result.ReasoningEffort, StringComparison.Ordinal),
                one.Name + ": expected effort " + one.ExpectedEffort + ", got "
                    + result.ReasoningEffort);
        }
    }

    // ------------------------------------------------------------------
    // checkApiKeyModelAccess (model_access_test.go:19-208)
    // ------------------------------------------------------------------

    [Fact]
    public void ModelAccessFollowsTheActiveProfileAllowList()
    {
        (string Name, string? Profiles, string Model, bool Denied)[] cases =
        [
            ("nil api key", null, "gpt-4", false),
            ("no profiles", null, "gpt-4", false),
            ("no active profile", Profiles(string.Empty, null, []), "gpt-4", false),
            ("active profile with no model restrictions", Profiles("profile1", "profile1", []), "gpt-4", false),
            (
                "active profile with exact match allowed",
                Profiles("profile1", "profile1", ["gpt-4"]),
                "gpt-4",
                false),
            (
                "active profile with exact match denied",
                Profiles("profile1", "profile1", ["gpt-4"]),
                "gpt-3.5-turbo",
                true),
            (
                "active profile with multiple exact matches",
                Profiles("profile1", "profile1", ["gpt-4", "claude-3-opus"]),
                "claude-3-opus",
                false),
            (
                "active profile denies a case variant",
                Profiles("profile1", "profile1", ["GPT-4"]),
                "gpt-4",
                true),
        ];

        foreach (var one in cases)
        {
            var apiKey = one.Profiles is null
                ? (one.Name == "nil api key" ? null : new ApiKeyRecord { Id = 2, ProjectId = 1 })
                : new ApiKeyRecord { Id = 2, ProjectId = 1, Profiles = one.Profiles };
            var request = new LlmRequest { Model = one.Model };

            if (one.Denied)
            {
                var error = Assert.Throws<TransformerException>(
                    () => ApiKeyModelAccess.Check(apiKey, request));
                Assert.Equal(TransformerErrorKind.InvalidModel, error.Kind);
                Assert.Equal("model not found: " + one.Model, error.Message);
            }
            else
            {
                ApiKeyModelAccess.Check(apiKey, request);
                Assert.Equal(one.Model, request.Model);
            }
        }
    }

    [Fact]
    public void ModelAccessRejectsAnEmptyModel()
    {
        var error = Assert.Throws<TransformerException>(
            () => ApiKeyModelAccess.Check(null, new LlmRequest()));

        Assert.Equal(TransformerErrorKind.InvalidModel, error.Kind);
        Assert.Equal("model not found: request model is empty", error.Message);
    }

    [Fact]
    public void AutoReasoningEffortRunsBeforeModelAccess()
    {
        // model_access_test.go:159-207: the allow-list names the base model, so
        // the request only passes when the suffix has already been split off.
        var apiKey = new ApiKeyRecord
        {
            Id = 2,
            ProjectId = 1,
            Profiles = Profiles("profile1", "profile1", ["gpt-5.4"]),
        };
        var request = new LlmRequest { Model = "gpt-5.4-xhigh", ReasoningEffort = "low" };

        AutoReasoningEffort.Apply(request, ModelSettings.Default with { AutoReasoningEffort = true });
        Assert.Equal("gpt-5.4", request.Model);
        Assert.Equal("xhigh", request.ReasoningEffort);

        ApiKeyModelAccess.Check(apiKey, request);
        Assert.Equal("gpt-5.4", request.Model);
        Assert.Equal("xhigh", request.ReasoningEffort);
    }

    // ------------------------------------------------------------------
    // Pipeline integration
    // ------------------------------------------------------------------

    [Fact]
    public async Task AutoReasoningEffortRewritesTheProviderBody()
    {
        const string providerModel = "auto-effort-model";

        fixture.Channels.Set(TestChannels.OpenAi(5201, providerModel));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion(providerModel),
        };
        var orchestrator = NewOrchestrator(
            executor,
            new RecordingRequestChainRecorder(),
            modelSettings: new StaticModelSettingsProvider(
                ModelSettings.Default with { AutoReasoningEffort = true }));

        var result = await orchestrator.ProcessAsync(
            TestChannels.ChatRequest(providerModel + "-xhigh"),
            new RequestIdentity(1, null),
            onRequestCreated: null);

        Assert.Equal(200, result.Response!.StatusCode);

        // The selector routed on the split model, and the provider body carries
        // the base model plus the effort taken from the client suffix.
        var request = Assert.Single(executor.Requests);
        using var body = System.Text.Json.JsonDocument.Parse(request.Body!);
        Assert.Equal(providerModel, body.RootElement.GetProperty("model").GetString());
        Assert.Equal("xhigh", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task ModelAccessDenialStopsBeforeSelectionAndPersistence()
    {
        fixture.Channels.Set(TestChannels.OpenAi(5202, "access-allowed-model"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("access-allowed-model"),
        };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = NewOrchestrator(executor, recorder);

        var identity = new RequestIdentity(1, 2)
        {
            ApiKey = new ApiKeyRecord
            {
                Id = 2,
                ProjectId = 1,
                Profiles = Profiles("active", "active", ["access-allowed-model"]),
            },
        };

        var error = await Assert.ThrowsAsync<TransformerException>(
            () => orchestrator.ProcessAsync(
                TestChannels.ChatRequest("access-denied-model"),
                identity,
                onRequestCreated: null));

        Assert.Equal(TransformerErrorKind.InvalidModel, error.Kind);
        Assert.Equal("model not found: access-denied-model", error.Message);
        Assert.Empty(executor.Requests);
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task QuotaRejectionIsTheReferenceForbiddenEnvelope()
    {
        fixture.Channels.Set(TestChannels.OpenAi(5203, "quota-envelope-model"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("quota-envelope-model"),
        };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = NewOrchestrator(
            executor,
            recorder,
            quotaChecker: new ScriptedQuotaChecker(new QuotaCheckResult(
                false,
                "requests quota exceeded: 3/3",
                new QuotaWindow(null, null, EndInclusive: true))));

        var identity = new RequestIdentity(1, 2)
        {
            RequestId = "ar-quota-test",
            ApiKey = QuotaKey(requests: 3),
        };

        var error = await Assert.ThrowsAsync<ResponseError>(
            () => orchestrator.ProcessAsync(
                TestChannels.ChatRequest("quota-envelope-model"),
                identity,
                onRequestCreated: null));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("quota_exceeded", error.Detail.Code);
        Assert.Equal("quota_exceeded_error", error.Detail.Type);
        Assert.Equal("requests quota exceeded: 3/3", error.Detail.Message);
        Assert.Equal("ar-quota-test", error.Detail.RequestId);

        Assert.Empty(executor.Requests);
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task QuotaEnvelopeOmitsAnUnknownRequestId()
    {
        fixture.Channels.Set(TestChannels.OpenAi(5204, "quota-no-request-id-model"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("quota-no-request-id-model"),
        };
        var orchestrator = NewOrchestrator(
            executor,
            new RecordingRequestChainRecorder(),
            quotaChecker: new ScriptedQuotaChecker(new QuotaCheckResult(
                false,
                "cost quota exceeded: 2/2",
                new QuotaWindow(null, null, EndInclusive: true))));

        var identity = new RequestIdentity(1, 2) { ApiKey = QuotaKey(requests: 3) };

        var error = await Assert.ThrowsAsync<ResponseError>(
            () => orchestrator.ProcessAsync(
                TestChannels.ChatRequest("quota-no-request-id-model"),
                identity,
                onRequestCreated: null));

        Assert.Null(error.Detail.RequestId);
    }

    [Fact]
    public async Task MinuteRequestQuotaAllowsTheFirstCallAndRejectsTheSecond()
    {
        // quota_minute_test.go:24-123: a one-request-per-minute quota passes the
        // first call, then the usage log written by that call trips the second
        // request before any request row or upstream call exists.
        const string model = "quota-minute-model";

        fixture.Channels.Set(TestChannels.OpenAi(5205, model));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion(model),
        };
        var recorder = new RecordingRequestChainRecorder();
        var quota = new ApiKeyQuotaService(
            new RecorderUsageLogStore(recorder),
            new FakeSystemStore(),
            TimeProvider.System);
        var orchestrator = NewOrchestrator(executor, recorder, quotaChecker: quota);

        var identity = new RequestIdentity(1, 2) { ApiKey = QuotaKey(requests: 1) };

        var first = await orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            identity,
            onRequestCreated: null);
        Assert.Equal(200, first.Response!.StatusCode);
        Assert.Single(executor.Requests);
        Assert.Single(recorder.UsageLogs);

        var error = await Assert.ThrowsAsync<ResponseError>(
            () => orchestrator.ProcessAsync(
                TestChannels.ChatRequest(model),
                identity,
                onRequestCreated: null));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("quota_exceeded", error.Detail.Code);
        Assert.Equal("quota_exceeded_error", error.Detail.Type);
        Assert.Equal("requests quota exceeded: 1/1", error.Detail.Message);

        // The rejection happens before persistRequest, so the second call left
        // no request row and never reached the upstream.
        Assert.Single(executor.Requests);
        Assert.Single(recorder.Requests);
    }

    // -- Helpers -------------------------------------------------------------

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        IRequestChainRecorder recorder,
        IModelSettingsProvider? modelSettings = null,
        IApiKeyQuotaChecker? quotaChecker = null)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 0,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            fixture.CandidateSelector,
            TimeProvider.System,
            recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            modelSettings: modelSettings,
            quotaChecker: quotaChecker);

    private static string Profiles(
        string activeProfile,
        string? profileName,
        string[] modelIds)
    {
        List<ApiKeyProfileModel> profiles = profileName is null
            ? []
            :
            [
                new ApiKeyProfileModel
                {
                    Name = profileName,
                    ModelIDs = modelIds,
                },
            ];

        return ApiKeyProfilesCodec.Serialize(new ApiKeyProfilesModel
        {
            ActiveProfile = activeProfile,
            Profiles = profiles,
        });
    }

    private static ApiKeyRecord QuotaKey(long requests) => new()
    {
        Id = 2,
        ProjectId = 1,
        Name = "quota-key",
        Profiles = ApiKeyProfilesCodec.Serialize(new ApiKeyProfilesModel
        {
            ActiveProfile = "default",
            Profiles =
            [
                new ApiKeyProfileModel
                {
                    Name = "default",
                    ModelMappings = [],
                    Quota = new ApiKeyQuotaModel
                    {
                        Requests = requests,
                        Period = new ApiKeyQuotaPeriodModel
                        {
                            Type = "past_duration",
                            PastDuration = new ApiKeyQuotaPastDurationModel
                            {
                                Value = 1,
                                Unit = "minute",
                            },
                        },
                    },
                },
            ],
        }),
    };

    private sealed class ScriptedQuotaChecker(QuotaCheckResult result) : IApiKeyQuotaChecker
    {
        public int Calls { get; private set; }

        public Task<QuotaCheckResult> CheckApiKeyQuotaAsync(
            long apiKeyId,
            ApiKeyQuotaModel quota,
            CancellationToken cancellationToken = default)
        {
            _ = apiKeyId;
            _ = quota;
            _ = cancellationToken;
            Calls++;
            return Task.FromResult(result);
        }
    }

    /// <summary>Reads the usage rows the persistence middleware wrote.</summary>
    private sealed class RecorderUsageLogStore(RecordingRequestChainRecorder recorder) : IUsageLogStore
    {
        public Task<ListPage<UsageLogRecord>> ListAsync(
            UsageLogListQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("not used by the quota path");

        public Task<long> CreateAsync(
            UsageLogRecord usageLog,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("the orchestrator writes through the recorder");

        public Task<UsageLogRecord?> GetByIdAsync(
            long id,
            long? projectScope,
            long? visibilityUserId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("not used by the quota path");

        public Task<IReadOnlyList<UsageLogRecord>> ListByRequestIdAsync(
            long requestId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("not used by the quota path");

        public Task<long> CountByApiKeyAsync(
            long apiKeyId,
            DateTime? start,
            DateTime? end,
            bool endInclusive,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult((long)recorder.UsageLogs.Count(
                row => Matches(row, apiKeyId, start, end, endInclusive)));
        }

        public Task<UsageAggregate> AggregateByApiKeyAsync(
            long apiKeyId,
            DateTime? start,
            DateTime? end,
            bool endInclusive,
            bool needTokens,
            bool needCost,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            var rows = recorder.UsageLogs
                .Where(row => Matches(row, apiKeyId, start, end, endInclusive))
                .ToList();
            return Task.FromResult(new UsageAggregate(
                needTokens ? rows.Sum(row => row.TotalTokens) : 0,
                needCost ? rows.Sum(row => row.TotalCost ?? 0) : 0));
        }

        public Task<double> SumCostByChannelAsync(
            long channelId,
            DateTime since,
            DateTime until,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(recorder.UsageLogs
                .Where(row => row.ChannelId == channelId
                    && row.CreatedAt >= since
                    && row.CreatedAt < until)
                .Sum(row => row.TotalCost ?? 0));
        }

        private static bool Matches(
            UsageLogRecord row,
            long apiKeyId,
            DateTime? start,
            DateTime? end,
            bool endInclusive)
        {
            if (row.ApiKeyId != apiKeyId)
            {
                return false;
            }

            if (start is not null && row.CreatedAt < start.Value)
            {
                return false;
            }

            return end is null
                || (endInclusive ? row.CreatedAt <= end.Value : row.CreatedAt < end.Value);
        }
    }
}
