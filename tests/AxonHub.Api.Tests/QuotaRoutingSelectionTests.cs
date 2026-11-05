using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Http;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the quota routing gate
/// (internal/server/orchestrator/candidates_quota.go and
/// internal/server/biz/provider_quota/routing.go) plus the quota exhaustion
/// error mapping: two-phase filtering, channel overrides, request modality,
/// settings defaulting, and the 503 <c>quota_exhausted</c> envelope.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class QuotaRoutingSelectionTests(GrainClusterFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset WindowStart = Now.AddMinutes(-30);
    private static readonly DateTimeOffset WindowReset = Now.AddMinutes(30);

    private static ChannelCandidate Candidate(
        long channelId,
        string? quotaRoutingMode = null,
        string model = "quota-model")
        => new()
        {
            ChannelId = channelId,
            ChannelName = "channel-" + channelId,
            ActualModel = model,
            Type = "openai",
            BaseUrl = "http://channel-" + channelId + ".test",
            Credentials = "{\"apiKey\":\"sk-" + channelId + "\"}",
            Settings = quotaRoutingMode is null
                ? null
                : "{\"quotaRoutingMode\":\"" + quotaRoutingMode + "\"}",
        };

    private static QuotaChannelStatus Status(string status, params QuotaLimitStatus[] limits)
        => new()
        {
            Status = status,
            Ready = status != "exhausted",
            Limits = limits,
        };

    private static QuotaChannelStatus Available()
        => Status("available");

    private static QuotaChannelStatus Exhausted()
        => Status("exhausted", new QuotaLimitStatus
        {
            Type = QuotaLimitTypes.Token,
            Status = "exhausted",
            UsageRatio = 1,
        });

    private static QuotaChannelStatus StickyOnly()
        => Status(
            "available",
            new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.Token,
                Status = "available",
                UsageRatio = 0.9,
                Window = QuotaWindows.FiveHours,
                PeriodStart = WindowStart,
                NextResetAt = WindowReset,
            });

    private static QuotaRoutingGate Gate(
        IReadOnlyDictionary<long, QuotaChannelStatus> statuses,
        string defaultMode = QuotaRoutingModes.RemoveOnExhausted)
        => new(
            new FakeQuotaStatusProvider(statuses),
            defaultMode,
            new TestTimeProvider(Now));

    private static long[] Ids(IEnumerable<ChannelCandidate> candidates)
        => [.. candidates.Select(static candidate => candidate.ChannelId)];

    // ---------------------------------------------------------------- routing

    [Fact]
    public void EvaluateQuotaRoutingMatchesTheDecisionTable()
    {
        var cases = new (string Name, QuotaLimitStatus[] Limits, string Overall, string Type, string State, string Reason)[]
        {
            ("open window under pace", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "available",
                    UsageRatio = 0.4,
                    Window = QuotaWindows.FiveHours,
                    PeriodStart = WindowStart,
                    NextResetAt = WindowReset,
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.Open, ""),

            ("window over pace and balance positive", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "available",
                    UsageRatio = 0.8,
                    Window = QuotaWindows.FiveHours,
                    PeriodStart = WindowStart,
                    NextResetAt = WindowReset,
                    AvailabilityGroup = "funding",
                },
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "available",
                    Window = QuotaWindows.PayAsYouGo,
                    AvailabilityGroup = "funding",
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.StickyOnly, "window_pressure"),

            ("window exhausted and payg available in same group", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "exhausted",
                    UsageRatio = 1,
                    Window = QuotaWindows.FiveHours,
                    AvailabilityGroup = "funding",
                },
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "available",
                    Window = QuotaWindows.PayAsYouGo,
                    AvailabilityGroup = "funding",
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.StickyOnly, "window_exhausted_balance_fallback"),

            ("window exhausted and another window available in same group", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "exhausted",
                    UsageRatio = 1,
                    Window = QuotaWindows.FiveHours,
                    AvailabilityGroup = "funding",
                },
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "available",
                    UsageRatio = 0.2,
                    Window = QuotaWindows.SevenDays,
                    AvailabilityGroup = "funding",
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.Open, ""),

            ("window group exhausted and separate balance group available", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "exhausted",
                    UsageRatio = 1,
                    Window = QuotaWindows.FiveHours,
                    AvailabilityGroup = "window",
                },
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "available",
                    Window = QuotaWindows.Credits,
                    AvailabilityGroup = "balance",
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.StickyOnly, "window_exhausted_balance_fallback"),

            ("window exhausted and balance exhausted", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "exhausted",
                    UsageRatio = 1,
                    Window = QuotaWindows.FiveHours,
                    AvailabilityGroup = "funding",
                },
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "exhausted",
                    UsageRatio = 1,
                    Window = QuotaWindows.PayAsYouGo,
                    AvailabilityGroup = "funding",
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.Exhausted, ""),

            ("balance-only available", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "available",
                    Window = QuotaWindows.Credits,
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.Open, ""),

            ("balance-only exhausted", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "exhausted",
                    UsageRatio = 1,
                    Window = QuotaWindows.Credits,
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.Exhausted, ""),

            ("window without time information", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "available",
                    UsageRatio = 0.9,
                    Window = QuotaWindows.FiveHours,
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.Open, ""),

            ("stale window is not balance", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "exhausted",
                    Window = QuotaWindows.FiveHours,
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.Exhausted, ""),

            ("ratio one is exhausted", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "available",
                    UsageRatio = 1,
                    Window = QuotaWindows.FiveHours,
                    PeriodStart = WindowStart,
                    NextResetAt = WindowReset,
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.Exhausted, ""),

            ("ungrouped windows require all windows", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "available",
                    UsageRatio = 0.2,
                    Window = QuotaWindows.FiveHours,
                },
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "exhausted",
                    UsageRatio = 1,
                    Window = QuotaWindows.SevenDays,
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.Exhausted, ""),

            ("channel exhausted short-circuits limits", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "available",
                    Window = QuotaWindows.FiveHours,
                },
            ], "exhausted", QuotaLimitTypes.Token, QuotaRoutingStates.Exhausted, ""),

            ("limit type isolation without shared group", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Image,
                    Status = "exhausted",
                    UsageRatio = 1,
                    Window = QuotaWindows.FiveHours,
                },
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "available",
                    Window = QuotaWindows.FiveHours,
                },
            ], "available", QuotaLimitTypes.Image, QuotaRoutingStates.Exhausted, ""),

            ("cross type or group inclusion", [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Status = "exhausted",
                    UsageRatio = 1,
                    Window = QuotaWindows.FiveHours,
                    AvailabilityGroup = "funding",
                },
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.SubscriptionCycle,
                    Status = "available",
                    Window = QuotaWindows.Cycle,
                    AvailabilityGroup = "funding",
                },
            ], "available", QuotaLimitTypes.Token, QuotaRoutingStates.Open, ""),

            ("unknown and no data is neutral", [], "unknown", QuotaLimitTypes.Token, QuotaRoutingStates.Unknown, ""),
        };

        foreach (var testCase in cases)
        {
            var (state, reason) = ProviderQuotaRouting.EvaluateQuotaRouting(
                testCase.Limits,
                testCase.Overall,
                testCase.Type,
                Now);

            Assert.Equal(testCase.State, state);
            Assert.Equal(testCase.Reason, reason);
        }
    }

    [Theory]
    [InlineData(QuotaWindows.PayAsYouGo, true)]
    [InlineData(QuotaWindows.Credits, true)]
    [InlineData("payg", false)]
    [InlineData("", false)]
    [InlineData(QuotaWindows.FiveHours, false)]
    public void IsBalanceLimitUsesTheWindowLabel(string window, bool expected)
        => Assert.Equal(expected, ProviderQuotaRouting.IsBalanceLimit(new QuotaLimitStatus { Window = window }));

    [Fact]
    public void ElapsedRatioRequiresAnActiveWindow()
    {
        Assert.Equal((0.5, true), ProviderQuotaRouting.ElapsedRatio(
            new QuotaLimitStatus { PeriodStart = WindowStart, NextResetAt = WindowReset }, Now));
        Assert.False(ProviderQuotaRouting.ElapsedRatio(
            new QuotaLimitStatus { NextResetAt = WindowReset }, Now).Ok);
        Assert.False(ProviderQuotaRouting.ElapsedRatio(
            new QuotaLimitStatus { PeriodStart = WindowStart }, Now).Ok);
        Assert.False(ProviderQuotaRouting.ElapsedRatio(
            new QuotaLimitStatus { PeriodStart = Now, NextResetAt = WindowReset }, Now).Ok);
        Assert.False(ProviderQuotaRouting.ElapsedRatio(
            new QuotaLimitStatus { PeriodStart = WindowStart, NextResetAt = Now }, Now).Ok);
    }

    // ------------------------------------------------------------------- gate

    [Fact]
    public void BackpressureExcludesNonPinnedStickyOnlyWhileOpenExists()
    {
        var gate = Gate(
            new Dictionary<long, QuotaChannelStatus> { [1] = Available(), [2] = StickyOnly() },
            QuotaRoutingModes.Backpressure);

        var filtered = gate.Filter([Candidate(1), Candidate(2)], new LlmRequest(), stickyId: 0);

        Assert.Equal([1L], Ids(filtered));
        Assert.Equal(1, gate.DroppedCount);
    }

    [Fact]
    public void BackpressureKeepsThePinnedStickyOnlyCandidate()
    {
        var gate = Gate(
            new Dictionary<long, QuotaChannelStatus> { [1] = Available(), [2] = StickyOnly() },
            QuotaRoutingModes.Backpressure);

        var filtered = gate.Filter([Candidate(1), Candidate(2)], new LlmRequest(), stickyId: 2);

        Assert.Equal([1L, 2L], Ids(filtered));
        Assert.Equal(0, gate.DroppedCount);
    }

    [Fact]
    public void PhaseTwoReturnsSingleStickyOnlyCandidate()
    {
        var gate = Gate(
            new Dictionary<long, QuotaChannelStatus> { [1] = StickyOnly() },
            QuotaRoutingModes.Backpressure);

        var filtered = gate.Filter([Candidate(1)], new LlmRequest(), stickyId: 0);

        Assert.Equal([1L], Ids(filtered));
        Assert.Equal(0, gate.DroppedCount);
    }

    [Fact]
    public void PhaseTwoIsNotAppliedWhenAnOpenCandidateExists()
    {
        var gate = Gate(
            new Dictionary<long, QuotaChannelStatus> { [1] = Available(), [2] = StickyOnly() },
            QuotaRoutingModes.Backpressure);

        var filtered = gate.Filter([Candidate(1), Candidate(2)], new LlmRequest(), stickyId: 0);

        Assert.Equal([1L], Ids(filtered));
    }

    [Fact]
    public void IgnoreQuotaKeepsExhaustedChannel()
    {
        var gate = Gate(
            new Dictionary<long, QuotaChannelStatus> { [1] = Exhausted() },
            QuotaRoutingModes.RemoveOnExhausted);

        var filtered = gate.Filter(
            [Candidate(1, QuotaRoutingModes.IgnoreQuota)],
            new LlmRequest(),
            stickyId: 0);

        Assert.Equal([1L], Ids(filtered));
        Assert.Equal(0, gate.DroppedCount);
    }

    [Fact]
    public void UnknownChannelModeFailsClosedToRemoveOnExhausted()
    {
        var gate = Gate(
            new Dictionary<long, QuotaChannelStatus> { [1] = Exhausted() },
            QuotaRoutingModes.IgnoreQuota);

        var filtered = gate.Filter(
            [Candidate(1, "unknown")],
            new LlmRequest(),
            stickyId: 0);

        Assert.Empty(filtered);
        Assert.Equal(1, gate.DroppedCount);
    }

    [Fact]
    public void ChannelOverrideBeatsGlobalAndInheritUsesGlobal()
    {
        var gate = Gate(
            new Dictionary<long, QuotaChannelStatus> { [1] = Exhausted(), [2] = Exhausted() },
            QuotaRoutingModes.RemoveOnExhausted);

        var filtered = gate.Filter(
            [Candidate(1, QuotaRoutingModes.IgnoreQuota), Candidate(2)],
            new LlmRequest(),
            stickyId: 0);

        Assert.Equal([1L], Ids(filtered));
        Assert.Equal(1, gate.DroppedCount);
    }

    [Fact]
    public void RequestModalitySelectsTheLimitTypeAndUnknownStaysNeutral()
    {
        var warning = Status(
            "warning",
            new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.Image,
                Status = "exhausted",
                UsageRatio = 1,
            },
            new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.Token,
                Status = "available",
                UsageRatio = 0.2,
            });
        var gate = Gate(
            new Dictionary<long, QuotaChannelStatus>
            {
                [1] = warning,
                [2] = Status("unknown"),
            });

        var text = gate.Filter([Candidate(1), Candidate(2)], new LlmRequest(), stickyId: 0);
        Assert.Equal([1L, 2L], Ids(text));

        var image = gate.Filter(
            [Candidate(1), Candidate(2)],
            new LlmRequest { Image = new ImageRequest() },
            stickyId: 0);
        Assert.Equal([2L], Ids(image));
    }

    [Fact]
    public void NilProviderLeavesCandidatesUntouched()
    {
        var gate = new QuotaRoutingGate(null, QuotaRoutingModes.RemoveOnExhausted);

        var filtered = gate.Filter([Candidate(1), Candidate(2)], new LlmRequest(), stickyId: 0);

        Assert.Equal([1L, 2L], Ids(filtered));
        Assert.Equal(0, gate.DroppedCount);
    }

    [Fact]
    public void EmptyCandidateListStaysEmpty()
    {
        var gate = Gate(new Dictionary<long, QuotaChannelStatus>());

        Assert.Empty(gate.Filter([], new LlmRequest(), stickyId: 0));
        Assert.Equal(0, gate.DroppedCount);
    }

    [Fact]
    public void RemoveOnExhaustedKeepsStickyOnlyChannels()
    {
        var gate = Gate(
            new Dictionary<long, QuotaChannelStatus> { [1] = StickyOnly() },
            QuotaRoutingModes.RemoveOnExhausted);

        var filtered = gate.Filter([Candidate(1)], new LlmRequest(), stickyId: 0);

        Assert.Equal([1L], Ids(filtered));
    }

    // --------------------------------------------------------------- settings

    [Fact]
    public async Task MissingSettingsRowFallsBackToRemoveOnExhausted()
    {
        var service = new SystemQuotaRoutingSettingsService(new FakeSystemStore());

        var settings = await service.GetQuotaRoutingSettingsAsync();

        Assert.Equal(QuotaRoutingModes.RemoveOnExhausted, settings.DefaultMode);
    }

    [Fact]
    public async Task CurrentSettingsWinOverTheLegacyKey()
    {
        var store = new FakeSystemStore();
        store.Add(
            SystemQuotaRoutingSettingsService.LegacyQuotaEnforcementSettingsKey,
            "{\"enabled\":false}");
        store.Add(
            SystemQuotaRoutingSettingsService.QuotaRoutingSettingsKey,
            "{\"defaultMode\":\"backpressure\"}");

        var settings = await new SystemQuotaRoutingSettingsService(store)
            .GetQuotaRoutingSettingsAsync();

        Assert.Equal(QuotaRoutingModes.Backpressure, settings.DefaultMode);
    }

    [Fact]
    public async Task EmptyDefaultModeFallsBackToTheDefault()
    {
        var store = new FakeSystemStore();
        store.Add(
            SystemQuotaRoutingSettingsService.QuotaRoutingSettingsKey,
            "{\"defaultMode\":\"\"}");

        var settings = await new SystemQuotaRoutingSettingsService(store)
            .GetQuotaRoutingSettingsAsync();

        Assert.Equal(QuotaRoutingModes.RemoveOnExhausted, settings.DefaultMode);
    }

    [Theory]
    [InlineData("{\"defaultMode\":\"not-a-mode\"}")]
    [InlineData("{not json")]
    public async Task InvalidSettingsFallBackToTheDefault(string value)
    {
        var store = new FakeSystemStore();
        store.Add(SystemQuotaRoutingSettingsService.QuotaRoutingSettingsKey, value);

        var settings = await new SystemQuotaRoutingSettingsService(store)
            .GetQuotaRoutingSettingsAsync();

        Assert.Equal(QuotaRoutingModes.RemoveOnExhausted, settings.DefaultMode);
    }

    [Fact]
    public async Task StoreLookupFailureFallsBackToTheDefault()
    {
        var store = new FakeSystemStore { GetFailure = new InvalidOperationException("boom") };

        var settings = await new SystemQuotaRoutingSettingsService(store)
            .GetQuotaRoutingSettingsAsync();

        Assert.Equal(QuotaRoutingModes.RemoveOnExhausted, settings.DefaultMode);
    }

    [Theory]
    [InlineData(false, false, false, null, QuotaRoutingModes.IgnoreQuota)]
    [InlineData(true, true, false, null, QuotaRoutingModes.RemoveOnExhausted)]
    [InlineData(true, false, true, null, QuotaRoutingModes.Backpressure)]
    [InlineData(true, false, false, "EXHAUSTED_ONLY", QuotaRoutingModes.RemoveOnExhausted)]
    [InlineData(true, false, false, "exhausted_only", QuotaRoutingModes.RemoveOnExhausted)]
    [InlineData(true, false, false, "DE_PRIORITIZE", QuotaRoutingModes.Backpressure)]
    [InlineData(true, false, false, "de_prioritize", QuotaRoutingModes.Backpressure)]
    [InlineData(true, false, false, "unknown", QuotaRoutingModes.RemoveOnExhausted)]
    public void LegacyMappingMatchesTheReference(
        bool enabled,
        bool exhaustedOnly,
        bool dePrioritize,
        string? mode,
        string expected)
        => Assert.Equal(
            expected,
            SystemQuotaRoutingSettingsService.QuotaRoutingModeFromLegacy(
                enabled,
                exhaustedOnly,
                dePrioritize,
                mode));

    [Fact]
    public async Task LegacySettingsAreMappedWhenTheNewKeyIsAbsent()
    {
        var store = new FakeSystemStore();
        store.Add(
            SystemQuotaRoutingSettingsService.LegacyQuotaEnforcementSettingsKey,
            "{\"enabled\":true,\"dePrioritize\":true}");

        var settings = await new SystemQuotaRoutingSettingsService(store)
            .GetQuotaRoutingSettingsAsync();

        Assert.Equal(QuotaRoutingModes.Backpressure, settings.DefaultMode);
    }

    [Fact]
    public async Task MalformedLegacySettingsFallBackToTheDefault()
    {
        var store = new FakeSystemStore();
        store.Add(
            SystemQuotaRoutingSettingsService.LegacyQuotaEnforcementSettingsKey,
            "{not json");

        var settings = await new SystemQuotaRoutingSettingsService(store)
            .GetQuotaRoutingSettingsAsync();

        Assert.Equal(QuotaRoutingModes.RemoveOnExhausted, settings.DefaultMode);
    }

    // ------------------------------------------------------------ orchestrator

    [Fact]
    public async Task OrchestratorRaisesQuotaExhaustedWhenTheGateDropsEverything()
    {
        fixture.Channels.Set();
        var executor = new ScriptedExecutor();
        var orchestrator = NewOrchestrator(
            executor,
            new FixedSelector(Candidate(5861)),
            new FakeQuotaStatusProvider(new Dictionary<long, QuotaChannelStatus>
            {
                [5861] = Exhausted(),
            }));

        var exception = await Assert.ThrowsAsync<QuotaExhaustedException>(() =>
            orchestrator.ProcessAsync(
                TestChannels.ChatRequest("quota-model"),
                RequestIdentity.Default,
                onRequestCreated: null));

        Assert.Equal("quota-model", exception.ModelName);
        Assert.Equal(
            "all channels quota exhausted for model quota-model",
            exception.Message);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task OrchestratorKeepsUnknownQuotaChannels()
    {
        fixture.Channels.Set();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("quota-model"),
        };
        var orchestrator = NewOrchestrator(
            executor,
            new FixedSelector(Candidate(5862)),
            new FakeQuotaStatusProvider(new Dictionary<long, QuotaChannelStatus>()));

        var result = await orchestrator.ProcessAsync(
            TestChannels.ChatRequest("quota-model"),
            RequestIdentity.Default,
            onRequestCreated: null);

        Assert.Equal(200, result.Response!.StatusCode);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task OrchestratorPinsTheStickyOnlyChannelUnderBackpressure()
    {
        fixture.Channels.Set();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw TestChannels.HttpClient(500),
        };
        var previous = new FakePreviousChannelProvider();
        previous.TraceChannels[77] = 5822;
        var orchestrator = NewOrchestrator(
            executor,
            new FixedSelector(Candidate(5821), Candidate(5822)),
            new FakeQuotaStatusProvider(new Dictionary<long, QuotaChannelStatus>
            {
                [5821] = Available(),
                [5822] = StickyOnly(),
            }),
            previous,
            defaultMode: QuotaRoutingModes.Backpressure);

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest("quota-model"),
            new RequestIdentity(1, null) { TraceId = 77 },
            onRequestCreated: null));

        Assert.Equal(
            [
                "http://channel-5822.test/v1/chat/completions",
                "http://channel-5821.test/v1/chat/completions",
            ],
            executor.Requests.Select(static request => request.Url));
    }

    [Fact]
    public async Task OrchestratorAbandonsAnExhaustedStickyChannel()
    {
        fixture.Channels.Set();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw TestChannels.HttpClient(500),
        };
        var previous = new FakePreviousChannelProvider();
        previous.TraceChannels[77] = 5823;
        var orchestrator = NewOrchestrator(
            executor,
            new FixedSelector(Candidate(5823), Candidate(5824)),
            new FakeQuotaStatusProvider(new Dictionary<long, QuotaChannelStatus>
            {
                [5823] = Exhausted(),
                [5824] = Available(),
            }),
            previous,
            defaultMode: QuotaRoutingModes.Backpressure);

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest("quota-model"),
            new RequestIdentity(1, null) { TraceId = 77 },
            onRequestCreated: null));

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://channel-5824.test/v1/chat/completions", request.Url);
    }

    [Fact]
    public async Task ConcurrentRequestsHaveIndependentDropCounts()
    {
        fixture.Channels.Set();
        var executor = new ScriptedExecutor();
        var orchestrator = NewOrchestrator(
            executor,
            new ModelFixedSelector(new Dictionary<string, ChannelCandidate[]>
            {
                ["exhausted"] = [Candidate(5863, model: "exhausted")],
                ["empty"] = [],
            }),
            new FakeQuotaStatusProvider(new Dictionary<long, QuotaChannelStatus>
            {
                [5863] = Exhausted(),
            }));

        var results = await Task.WhenAll(
            Capture(() => orchestrator.ProcessAsync(
                TestChannels.ChatRequest("exhausted"),
                RequestIdentity.Default,
                onRequestCreated: null)),
            Capture(() => orchestrator.ProcessAsync(
                TestChannels.ChatRequest("empty"),
                RequestIdentity.Default,
                onRequestCreated: null)));

        Assert.IsType<QuotaExhaustedException>(results[0]);
        var invalid = Assert.IsAssignableFrom<AxonHub.Llm.Transformer.TransformerException>(results[1]);
        Assert.Equal(AxonHub.Llm.Transformer.TransformerErrorKind.InvalidModel, invalid.Kind);
    }

    [Fact]
    public async Task EmptyCandidatesWithoutDropsRaiseInvalidModel()
    {
        fixture.Channels.Set();
        var executor = new ScriptedExecutor();
        var orchestrator = NewOrchestrator(
            executor,
            new FixedSelector(),
            new FakeQuotaStatusProvider(new Dictionary<long, QuotaChannelStatus>()));

        var exception = await Assert.ThrowsAsync<AxonHub.Llm.Transformer.TransformerException>(() =>
            orchestrator.ProcessAsync(
                TestChannels.ChatRequest("quota-model"),
                RequestIdentity.Default,
                onRequestCreated: null));

        Assert.Equal(AxonHub.Llm.Transformer.TransformerErrorKind.InvalidModel, exception.Kind);
        Assert.Equal("model not found: quota-model", exception.Message);
    }

    // ------------------------------------------------------------ error mapping

    [Fact]
    public async Task TransformErrorMapsQuotaExhaustionTo503()
    {
        fixture.Channels.Set();
        var orchestrator = NewOrchestrator(
            new ScriptedExecutor(),
            new FixedSelector(Candidate(5864)),
            new FakeQuotaStatusProvider(new Dictionary<long, QuotaChannelStatus>
            {
                [5864] = Exhausted(),
            }));

        var response = await orchestrator.TransformErrorAsync(
            new QuotaExhaustedException("quota-model"));

        Assert.Equal(503, response.StatusCode);
        var body = Encoding.UTF8.GetString(response.Body!);
        Assert.Equal(
            "{\"error\":{\"code\":\"quota_exhausted\","
            + "\"message\":\"all channels quota exhausted for model quota-model\","
            + "\"type\":\"quota_exhausted\"}}",
            body);
    }

    [Fact]
    public void StreamErrorFormatterUsesTheQuotaEnvelope()
    {
        var body = StreamErrorFormatter.Format(
            new QuotaExhaustedException("quota-model"));

        Assert.Equal(
            "{\"error\":{\"code\":\"quota_exhausted\","
            + "\"message\":\"all channels quota exhausted for model quota-model\","
            + "\"type\":\"quota_exhausted\"}}",
            Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task BinaryStreamWriterMapsQuotaExhaustionTo503()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        var stream = new ListStream<StreamEvent>(
            [],
            terminalError: new QuotaExhaustedException("quota-model"));

        await BinaryStreamWriter.WriteAsync(
            context,
            stream,
            expectedChoices: 1,
            CancellationToken.None,
            transformStreamError: null);

        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(
            "{\"error\":{\"code\":\"quota_exhausted\","
            + "\"message\":\"all channels quota exhausted for model quota-model\","
            + "\"type\":\"quota_exhausted\"}}",
            Encoding.UTF8.GetString(body.ToArray()));
    }

    // ------------------------------------------------------------------ helpers

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        IModelCandidateSelector selector,
        IProviderQuotaStatusProvider quotaProvider,
        IPreviousChannelProvider? previousChannels = null,
        string defaultMode = QuotaRoutingModes.RemoveOnExhausted)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 1,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
                TraceStickyMode = RetryPolicy.TraceStickyPreferPreviousChannel,
            }),
            selector,
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            previousChannels: previousChannels,
            quotaStatusProvider: quotaProvider,
            quotaRoutingSettings: new StaticQuotaRoutingSettingsProvider(defaultMode));

    private static async Task<Exception?> Capture(Func<Task<ChatCompletionResult>> action)
    {
        try
        {
            _ = await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private sealed class FakeQuotaStatusProvider(IReadOnlyDictionary<long, QuotaChannelStatus> statuses)
        : IProviderQuotaStatusProvider
    {
        public QuotaChannelStatus? GetQuotaStatus(long channelId)
            => statuses.GetValueOrDefault(channelId);
    }

    private sealed class StaticQuotaRoutingSettingsProvider(string defaultMode)
        : IQuotaRoutingSettingsProvider
    {
        public Task<QuotaRoutingSettings> GetQuotaRoutingSettingsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new QuotaRoutingSettings(defaultMode));
    }

    private sealed class FixedSelector(params ChannelCandidate[] candidates) : IModelCandidateSelector
    {
        public Task<CandidateSelection> SelectAsync(
            LlmRequest request,
            RequestIdentity identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CandidateSelection(candidates, RoutingPolicy: null));
    }

    private sealed class ModelFixedSelector(
        IReadOnlyDictionary<string, ChannelCandidate[]> candidatesByModel)
        : IModelCandidateSelector
    {
        public Task<CandidateSelection> SelectAsync(
            LlmRequest request,
            RequestIdentity identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CandidateSelection(
                candidatesByModel.GetValueOrDefault(request.Model) ?? [],
                RoutingPolicy: null));
    }
}
