using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.Billing;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Http;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Anthropic;
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
    private const int LlmTestServerEarlyCleanEndMode = 1;

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

    [LlmTestServerFact]
    public async Task EarlyCleanEndAfterFirstFrameDoesNotSpliceFallback()
    {
        const string model = "resilience-early-clean-end-prefix";
        const string primaryModel = "resilience-early-clean-end-prefix-primary";
        const string fallbackModel = "resilience-early-clean-end-prefix-fallback";
        const string prefix = "A visible prefix.";
        const string remainder = " A-only remainder.";
        const string fallbackText = "B fallback marker.";
        await ConfigureChannelsAsync(
            model,
            primaryModel,
            fallbackModel,
            new
            {
                content = prefix + remainder,
                streamChunks = new[] { prefix, remainder },
                streamDelivery = new { mode = LlmTestServerEarlyCleanEndMode, afterContentFrames = 1 },
            },
            new { content = fallbackText });

        var primary = PrimaryChannel(model);
        var fallback = FallbackChannel(model);
        var context = await SendSelectedAsync(
            RequestBody(model, stream: true),
            CreateRetryPolicy(maxChannelRetries: 1),
            [primary, fallback],
            new FixedCandidateSelector(
                Candidate(primary, priority: 0, actualModel: primaryModel),
                Candidate(fallback, priority: 1, actualModel: fallbackModel)));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var stream = InspectOpenAiStream(ResponseBody(context));
        Assert.Equal(prefix, stream.Text);
        Assert.False(stream.HasDoneMarker);
        Assert.False(stream.HasFinishReason);
        Assert.True(stream.HasErrorEvent);
        Assert.DoesNotContain(fallbackText, stream.Text, StringComparison.Ordinal);
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(0, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));

        using var requests = await LlmTestServerE2ETests.AdminClient.GetRequestsAsync("provider=openai");
        var request = Assert.Single(requests.RootElement.EnumerateArray());
        Assert.Equal(primaryModel, request.GetProperty("modelId").GetString());
        Assert.Equal(LlmTestServerEarlyCleanEndMode, request.GetProperty("streamDelivery").GetProperty("mode").GetInt32());
        Assert.True(request.GetProperty("streamDeliveryApplied").GetBoolean());
        Assert.Equal(1, request.GetProperty("emittedContentFrames").GetInt32());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("terminalMarkerObserved").ValueKind);
    }

    [LlmTestServerFact]
    public async Task EarlyCleanEndAfterAllContentStillFailsTerminalCheck()
    {
        const string model = "resilience-early-clean-end-all-content";
        const string primaryModel = "resilience-early-clean-end-all-content-primary";
        const string fallbackModel = "resilience-early-clean-end-all-content-fallback";
        const string content = "All text arrived before the stream ended.";
        await ConfigureChannelsAsync(
            model,
            primaryModel,
            fallbackModel,
            new
            {
                content,
                streamChunks = new[] { content },
                streamDelivery = new { mode = LlmTestServerEarlyCleanEndMode, afterContentFrames = 1 },
            },
            new { content = "B fallback marker." });

        var primary = PrimaryChannel(model);
        var fallback = FallbackChannel(model);
        var context = await SendSelectedAsync(
            RequestBody(model, stream: true),
            CreateRetryPolicy(maxChannelRetries: 1),
            [primary, fallback],
            new FixedCandidateSelector(
                Candidate(primary, priority: 0, actualModel: primaryModel),
                Candidate(fallback, priority: 1, actualModel: fallbackModel)));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var stream = InspectOpenAiStream(ResponseBody(context));
        Assert.Equal(content, stream.Text);
        Assert.False(stream.HasDoneMarker);
        Assert.False(stream.HasFinishReason);
        Assert.True(stream.HasErrorEvent);
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(0, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));

        using var requests = await LlmTestServerE2ETests.AdminClient.GetRequestsAsync("provider=openai");
        var request = Assert.Single(requests.RootElement.EnumerateArray());
        Assert.Equal(primaryModel, request.GetProperty("modelId").GetString());
        Assert.Equal(LlmTestServerEarlyCleanEndMode, request.GetProperty("streamDelivery").GetProperty("mode").GetInt32());
        Assert.True(request.GetProperty("streamDeliveryApplied").GetBoolean());
        Assert.Equal(1, request.GetProperty("emittedContentFrames").GetInt32());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("terminalMarkerObserved").ValueKind);
    }

    [LlmTestServerFact]
    public async Task AutoDisableThresholdMinusOneKeepsChannelEnabled()
    {
        const string model = "resilience-auto-disable-minus-one";
        const int threshold = 2;
        await ConfigureAsync(
            model,
            ProviderError(StatusCodes.Status503ServiceUnavailable, "service_unavailable", "temporary outage"));

        var primary = PrimaryChannel(model) with { Policies = AutoDisablePolicies(threshold) };
        var autoDisable = new AutoDisableHarness(fixture.Channels);
        var queue = new CapturingAutoDisableQueue(autoDisable.Service);
        var context = await SendCoreAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 0),
            [primary, FallbackChannel(model)],
            autoDisableQueue: queue);
        var evaluated = await queue.WaitForNextEvaluationAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal(PrimaryChannelId, evaluated.ChannelId);
        Assert.Empty(autoDisable.Credentials.DisableCalls);
        Assert.Contains(
            PrimaryChannelId,
            (await fixture.Channels.ListEnabledAsync()).Select(channel => channel.Id));
    }

    [LlmTestServerFact]
    public async Task AutoDisableAtThresholdSkipsAOnNextRequest()
    {
        const string model = "resilience-auto-disable-at-threshold";
        const int threshold = 2;
        await ConfigureAsync(
            model,
            ProviderError(StatusCodes.Status503ServiceUnavailable, "service_unavailable", "first outage"),
            ProviderError(StatusCodes.Status503ServiceUnavailable, "service_unavailable", "second outage"),
            new { content = "Fallback remains available." });

        var primary = PrimaryChannel(model) with { Policies = AutoDisablePolicies(threshold) };
        var fallback = FallbackChannel(model);
        var autoDisable = new AutoDisableHarness(fixture.Channels);
        var queue = new CapturingAutoDisableQueue(autoDisable.Service);

        for (var failure = 0; failure < threshold; failure++)
        {
            var context = await SendCoreAsync(
                RequestBody(model),
                CreateRetryPolicy(maxChannelRetries: 0),
                [primary, fallback],
                autoDisableQueue: queue);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
            await queue.WaitForNextEvaluationAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        var disableCall = Assert.Single(autoDisable.Credentials.DisableCalls);
        Assert.Equal(PrimaryChannelId, disableCall.ChannelId);
        Assert.Equal(ApiKey, disableCall.Key);
        Assert.DoesNotContain(
            PrimaryChannelId,
            (await fixture.Channels.ListEnabledAsync()).Select(channel => channel.Id));

        var fallbackContext = await SendCoreAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 0),
            [],
            setEnabledChannels: false,
            autoDisableQueue: queue);
        await queue.WaitForNextEvaluationAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StatusCodes.Status200OK, fallbackContext.Response.StatusCode);
        Assert.Equal(
            new[] { PrimaryChannelId, PrimaryChannelId, FallbackChannelId },
            _recorder.RequestChannels);
        Assert.Equal(2, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 503));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq", 200));
    }

    [LlmTestServerFact]
    public async Task ThreeFailuresEnterHalfOpen()
    {
        const string model = "resilience-circuit-half-open";
        await ConfigureAsync(
            model,
            RepeatedProviderErrors(ModelCircuitBreaker.HalfOpenThreshold));

        var clock = NewManualTimeProvider();
        var breaker = new ModelCircuitBreaker(clock);
        for (var failure = 0; failure < ModelCircuitBreaker.HalfOpenThreshold; failure++)
        {
            var context = await SendCoreAsync(
                RequestBody(model),
                CreateCircuitBreakerRetryPolicy(),
                [PrimaryChannel(model)],
                modelCircuitBreaker: breaker,
                timeProvider: clock);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        }

        var stats = breaker.GetStats(PrimaryChannelId, model);
        Assert.Equal(ModelCircuitBreakerState.HalfOpen, stats.State);
        Assert.Equal(ModelCircuitBreaker.HalfOpenThreshold, stats.ConsecutiveFailures);
    }

    [LlmTestServerFact]
    public async Task FifthFailureOpensCircuit()
    {
        const string model = "resilience-circuit-open";
        await ConfigureAsync(
            model,
            RepeatedProviderErrors(ModelCircuitBreaker.OpenThreshold));

        var clock = NewManualTimeProvider();
        var breaker = new ModelCircuitBreaker(clock);
        for (var failure = 0; failure < ModelCircuitBreaker.OpenThreshold; failure++)
        {
            var context = await SendCoreAsync(
                RequestBody(model),
                CreateCircuitBreakerRetryPolicy(),
                [PrimaryChannel(model)],
                modelCircuitBreaker: breaker,
                timeProvider: clock);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        }

        var stats = breaker.GetStats(PrimaryChannelId, model);
        Assert.Equal(ModelCircuitBreakerState.Open, stats.State);
        Assert.Equal(ModelCircuitBreaker.OpenThreshold, stats.ConsecutiveFailures);
        Assert.Equal(clock.GetUtcNow() + ModelCircuitBreaker.ProbeInterval, stats.NextProbeAt);
    }

    [LlmTestServerFact]
    public async Task SuccessfulHalfOpenProbeClosesCircuit()
    {
        const string model = "resilience-circuit-successful-probe";
        await ConfigureAsync(
            model,
            RepeatedProviderErrors(ModelCircuitBreaker.OpenThreshold)
                .Append(new { content = "Probe recovered." })
                .Cast<object>()
                .ToArray());

        var clock = NewManualTimeProvider();
        var breaker = new ModelCircuitBreaker(clock);
        for (var failure = 0; failure < ModelCircuitBreaker.OpenThreshold; failure++)
        {
            await SendCoreAsync(
                RequestBody(model),
                CreateCircuitBreakerRetryPolicy(),
                [PrimaryChannel(model)],
                modelCircuitBreaker: breaker,
                timeProvider: clock);
        }

        var nextProbeAt = breaker.GetStats(PrimaryChannelId, model).NextProbeAt;
        var blockedRequest = await SendCoreAsync(
            RequestBody(model),
            CreateCircuitBreakerRetryPolicy(),
            [],
            setEnabledChannels: false,
            modelCircuitBreaker: breaker,
            timeProvider: clock);

        Assert.Equal(
            ModelCircuitBreaker.OpenThreshold,
            await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 503));
        Assert.True(blockedRequest.Response.StatusCode >= StatusCodes.Status400BadRequest);
        Assert.Equal(nextProbeAt, breaker.GetStats(PrimaryChannelId, model).NextProbeAt);

        clock.Advance(nextProbeAt - clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        var probe = await SendCoreAsync(
            RequestBody(model),
            CreateCircuitBreakerRetryPolicy(),
            [],
            setEnabledChannels: false,
            modelCircuitBreaker: breaker,
            timeProvider: clock);

        Assert.Equal(StatusCodes.Status200OK, probe.Response.StatusCode);
        Assert.Equal(
            ModelCircuitBreakerState.Closed,
            breaker.GetStats(PrimaryChannelId, model).State);
        Assert.Equal(0, breaker.GetStats(PrimaryChannelId, model).ConsecutiveFailures);
        Assert.Equal(default, breaker.GetStats(PrimaryChannelId, model).NextProbeAt);
        Assert.False(breaker.GetStats(PrimaryChannelId, model).ProbingInProgress);
    }

    [LlmTestServerFact]
    public async Task FailedHalfOpenProbeUsesBackoff()
    {
        const string model = "resilience-circuit-failed-probe";
        await ConfigureAsync(
            model,
            RepeatedProviderErrors(ModelCircuitBreaker.OpenThreshold + 2));

        var clock = NewManualTimeProvider();
        var breaker = new ModelCircuitBreaker(clock);
        for (var failure = 0; failure < ModelCircuitBreaker.OpenThreshold; failure++)
        {
            await SendCoreAsync(
                RequestBody(model),
                CreateCircuitBreakerRetryPolicy(),
                [PrimaryChannel(model)],
                modelCircuitBreaker: breaker,
                timeProvider: clock);
        }

        clock.Advance(
            breaker.GetStats(PrimaryChannelId, model).NextProbeAt - clock.GetUtcNow()
                + TimeSpan.FromSeconds(1));
        var firstProbe = await SendCoreAsync(
            RequestBody(model),
            CreateCircuitBreakerRetryPolicy(),
            [],
            setEnabledChannels: false,
            modelCircuitBreaker: breaker,
            timeProvider: clock);
        var firstBackoff = breaker.GetStats(PrimaryChannelId, model);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, firstProbe.Response.StatusCode);
        Assert.Equal(ModelCircuitBreakerState.Open, firstBackoff.State);
        Assert.Equal(1, firstBackoff.ProbeAttempts);
        Assert.Equal(clock.GetUtcNow() + ModelCircuitBreaker.ProbeInterval, firstBackoff.NextProbeAt);

        var requestsBeforeBlockedProbe =
            await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 503);
        await SendCoreAsync(
            RequestBody(model),
            CreateCircuitBreakerRetryPolicy(),
            [],
            setEnabledChannels: false,
            modelCircuitBreaker: breaker,
            timeProvider: clock);
        Assert.Equal(
            requestsBeforeBlockedProbe,
            await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 503));

        clock.Advance(
            firstBackoff.NextProbeAt - clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        var secondProbe = await SendCoreAsync(
            RequestBody(model),
            CreateCircuitBreakerRetryPolicy(),
            [],
            setEnabledChannels: false,
            modelCircuitBreaker: breaker,
            timeProvider: clock);
        var secondBackoff = breaker.GetStats(PrimaryChannelId, model);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, secondProbe.Response.StatusCode);
        Assert.Equal(ModelCircuitBreakerState.Open, secondBackoff.State);
        Assert.Equal(2, secondBackoff.ProbeAttempts);
        Assert.Equal(
            clock.GetUtcNow() + ModelCircuitBreaker.ProbeInterval + ModelCircuitBreaker.ProbeInterval,
            secondBackoff.NextProbeAt);
    }

    [LlmTestServerFact]
    public async Task NativeToolRequestKeepsCompatibleFallbackOnly()
    {
        const string model = "claude-sonnet-5-5";
        await ConfigureAsync(model, new { content = "Compatible channel handled the request." });

        var incompatible = NewChannel(
            PrimaryChannelId,
            model,
            "openai",
            ServerUrl + "/openai/v1",
            orderingWeight: 100);
        var compatible = NewChannel(
            FallbackChannelId,
            model,
            "anthropic",
            ServerUrl + "/anthropic",
            orderingWeight: 10);
        var context = await SendSelectedAsync(
            AnthropicNativeToolRequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1),
            [incompatible, compatible],
            new FixedCandidateSelector(
                Candidate(incompatible, priority: 0),
                Candidate(compatible, priority: 1, apiFormat: ApiFormats.AnthropicMessage)),
            anthropic: true);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("Compatible channel handled the request.", ResponseBody(context), StringComparison.Ordinal);
        Assert.Equal(0, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("anthropic", 200));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("anthropic"));
    }

    [LlmTestServerFact]
    public async Task NoCompatibleNativeToolChannelPreservesExistingCandidateFallback()
    {
        const string model = "claude-sonnet-5-5";
        await ConfigureAsync(
            model,
            ProviderError(StatusCodes.Status503ServiceUnavailable, "service_unavailable", "first candidate failed"),
            new { content = "Original candidate fallback remains available." });

        var first = NewChannel(
            PrimaryChannelId,
            model,
            "openai",
            ServerUrl + "/openai/v1",
            orderingWeight: 100);
        var second = NewChannel(
            FallbackChannelId,
            model,
            "openai",
            ServerUrl + "/groq/openai/v1",
            orderingWeight: 10);
        var context = await SendSelectedAsync(
            AnthropicNativeToolRequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1),
            [first, second],
            new FixedCandidateSelector(
                Candidate(first, priority: 0),
                Candidate(second, priority: 1)),
            anthropic: true);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("Original candidate fallback remains available.", ResponseBody(context), StringComparison.Ordinal);
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 503));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq", 200));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
    }

    [LlmTestServerFact]
    public async Task TimedOutPrimaryFallsBackWithinDeadline()
    {
        const string model = "resilience-timeout-primary-fallback";
        const string primaryModel = "resilience-timeout-primary-slow";
        const string fallbackModel = "resilience-timeout-fallback-fast";
        await ConfigureChannelsAsync(
            model,
            primaryModel,
            fallbackModel,
            new { content = "Primary response arrived too late.", delayMilliseconds = 1800 },
            new { content = "Fallback completed within the overall deadline." });

        var started = System.Diagnostics.Stopwatch.StartNew();
        var primary = PrimaryChannel(model);
        var fallback = FallbackChannel(model);
        var context = await SendSelectedAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1, nonStreamResponseTimeoutSeconds: 1),
            [primary, fallback],
            new FixedCandidateSelector(
                Candidate(primary, priority: 0, actualModel: primaryModel),
                Candidate(fallback, priority: 1, actualModel: fallbackModel)),
            requestDeadline: TimeSpan.FromSeconds(6));
        started.Stop();

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("Fallback completed within the overall deadline.", ResponseBody(context), StringComparison.Ordinal);
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq", 200));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(7), $"Fallback exceeded its deadline budget: {started.Elapsed}.");
    }

    [LlmTestServerFact]
    public async Task ExpiredOverallDeadlinePreventsNextAttempt()
    {
        const string model = "resilience-overall-deadline-expired";
        const string primaryModel = "resilience-deadline-primary-slow";
        const string fallbackModel = "resilience-deadline-fallback-fast";
        await ConfigureChannelsAsync(
            model,
            primaryModel,
            fallbackModel,
            new { content = "Primary response arrived after the request deadline.", delayMilliseconds = 1800 },
            new { content = "Fallback must not be requested after the deadline." });

        var started = System.Diagnostics.Stopwatch.StartNew();
        var primary = PrimaryChannel(model);
        var fallback = FallbackChannel(model);
        var context = await SendSelectedAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1, nonStreamResponseTimeoutSeconds: 5),
            [primary, fallback],
            new FixedCandidateSelector(
                Candidate(primary, priority: 0, actualModel: primaryModel),
                Candidate(fallback, priority: 1, actualModel: fallbackModel)),
            requestDeadline: TimeSpan.FromMilliseconds(300));
        started.Stop();

        Assert.NotEqual(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(0, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"Expired request exceeded its deadline budget: {started.Elapsed}.");
    }

    [LlmTestServerFact]
    public async Task RepeatedTraceIdSticksToPreviouslySelectedChannel()
    {
        const string model = "resilience-repeated-trace-sticky";
        const string traceHeader = "resilience-sticky-trace";
        await ConfigureAsync(
            model,
            new { content = "First request." },
            new { content = "Second request." });

        var primary = PrimaryChannel(model);
        var fallback = FallbackChannel(model);
        var previousChannels = new PreviousChannelCache(TimeProvider.System);
        var traceStore = new FixedTraceStore();

        var first = await SendSelectedAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1),
            [primary, fallback],
            new FixedCandidateSelector(
                Candidate(primary, priority: 0),
                Candidate(fallback, priority: 1)),
            previousChannels: previousChannels,
            traceHeader: traceHeader,
            traceStore: traceStore);

        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 200));
        Assert.Equal(0, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
        Assert.Equal(
            PrimaryChannelId,
            previousChannels.GetPreviousChannelId(traceStore.GetTrace(traceHeader).Id));

        var second = await SendSelectedAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1),
            [fallback, primary],
            new FixedCandidateSelector(
                Candidate(fallback, priority: 0),
                Candidate(primary, priority: 1)),
            previousChannels: previousChannels,
            traceHeader: traceHeader,
            traceStore: traceStore);

        Assert.Equal(StatusCodes.Status200OK, second.Response.StatusCode);
        Assert.Equal(2, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 200));
        Assert.Equal(0, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
    }

    [LlmTestServerFact]
    public async Task DifferentTraceIdUsesNormalCandidateOrder()
    {
        const string model = "resilience-distinct-trace-order";
        await ConfigureAsync(model, new { content = "Normal candidate order was used." });

        var primary = PrimaryChannel(model);
        var fallback = FallbackChannel(model);
        var previousChannels = new PreviousChannelCache(TimeProvider.System);
        var traceStore = new FixedTraceStore();
        var seededTrace = await traceStore.GetOrCreateAsync(1, "seeded-trace", threadId: null);
        previousChannels.Remember(seededTrace.Id, threadId: null, FallbackChannelId);

        var context = await SendSelectedAsync(
            RequestBody(model),
            CreateRetryPolicy(maxChannelRetries: 1),
            [primary, fallback],
            new FixedCandidateSelector(
                Candidate(primary, priority: 0),
                Candidate(fallback, priority: 1)),
            previousChannels: previousChannels,
            traceHeader: "unseeded-trace",
            traceStore: traceStore);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai", 200));
        Assert.Equal(1, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("openai"));
        Assert.Equal(0, await LlmTestServerE2ETests.AdminClient.CountRequestsAsync("groq"));
        Assert.Equal(FallbackChannelId, previousChannels.GetPreviousChannelId(seededTrace.Id));
        Assert.NotEqual(seededTrace.Id, traceStore.GetTrace("unseeded-trace").Id);
        Assert.Equal(
            PrimaryChannelId,
            previousChannels.GetPreviousChannelId(traceStore.GetTrace("unseeded-trace").Id));
    }

    private async Task<DefaultHttpContext> SendAsync(
        string body,
        RetryPolicy policy,
        params ChannelDescriptor[] channels)
        => await SendCoreAsync(body, policy, channels);

    private async Task<DefaultHttpContext> SendCoreAsync(
        string body,
        RetryPolicy policy,
        ChannelDescriptor[] channels,
        bool setEnabledChannels = true,
        IChannelAutoDisableEvaluationQueue? autoDisableQueue = null,
        ModelCircuitBreaker? modelCircuitBreaker = null,
        TimeProvider? timeProvider = null)
    {
        if (setEnabledChannels)
        {
            fixture.Channels.Set(channels);
        }

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
            timeProvider ?? TimeProvider.System,
            _recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            usageCostService: _usageCostService,
            autoDisableQueue: autoDisableQueue,
            modelCircuitBreaker: modelCircuitBreaker);

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);
        return context;
    }

    private async Task<DefaultHttpContext> SendSelectedAsync(
        string body,
        RetryPolicy policy,
        ChannelDescriptor[] channels,
        IModelCandidateSelector selector,
        bool anthropic = false,
        IPreviousChannelProvider? previousChannels = null,
        string? traceHeader = null,
        FixedTraceStore? traceStore = null,
        TimeSpan? requestDeadline = null)
    {
        fixture.Channels.Set(channels);

        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var orchestrator = new ChatCompletionOrchestrator(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            new HttpExecutor(http),
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(policy),
            selector,
            TimeProvider.System,
            _recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            usageCostService: _usageCostService,
            previousChannels: previousChannels);
        if (anthropic)
        {
            orchestrator = orchestrator.WithInbound(new AnthropicInboundTransformer());
        }

        var path = anthropic ? "/v1/messages" : ChatPath;
        var context = NewContext(body, path);
        if (traceHeader is not null)
        {
            context.Items[RequestIdentity.HttpContextItemKey] =
                new RequestIdentity(1, null);
            traceStore ??= new FixedTraceStore();
        }

        RequestDelegate endpoint = requestContext =>
        {
            if (traceHeader is not null)
            {
                Assert.Equal(traceHeader, TraceContext.GetTraceId(requestContext));
                Assert.Equal(
                    traceStore!.GetTrace(traceHeader).Id,
                    RequestIdentity.FromHttpContext(requestContext).TraceId);
            }

            return ChatCompletionEndpoint.HandleAsync(requestContext, orchestrator);
        };

        if (traceHeader is not null)
        {
            context.Request.Headers[TracingOptions.DefaultTraceHeader] = traceHeader;
            var persistedTracing = new TraceMiddleware(endpoint, TracingOptions.Default);
            RequestDelegate persistTrace = requestContext =>
                persistedTracing.InvokeAsync(requestContext, traceStore!);
            var tracing = new RequestTracingMiddleware(persistTrace, TracingOptions.Default);
            endpoint = tracing.InvokeAsync;
        }

        if (requestDeadline is { } timeout)
        {
            var deadline = new RequestTimeoutMiddleware(endpoint, timeout, TimeProvider.System);
            endpoint = deadline.InvokeAsync;
        }

        await endpoint(context);
        return context;
    }

    private static ChannelCandidate Candidate(
        ChannelDescriptor channel,
        int priority,
        string? actualModel = null,
        string? apiFormat = null)
        => new()
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            ActualModel = actualModel ?? channel.SupportedModels.Trim('[', ']', '"'),
            Type = channel.Type,
            BaseUrl = channel.BaseUrl,
            Credentials = channel.Credentials,
            ApiFormat = apiFormat ?? ApiFormats.OpenAIChatCompletion,
            OrderingWeight = channel.OrderingWeight,
            Priority = priority,
        };

    private static string AnthropicNativeToolRequestBody(string model)
        => "{\"model\":\"" + model + "\",\"max_tokens\":32,"
            + "\"messages\":[{\"role\":\"user\",\"content\":\"search the web\"}],"
            + "\"tools\":[{\"type\":\"web_search_20250305\",\"name\":\"web_search\"}]}";

    private static async Task ConfigureChannelsAsync(
        string requestedModel,
        string primaryModel,
        string fallbackModel,
        object primaryResponse,
        object fallbackResponse)
    {
        await LlmTestServerE2ETests.AdminClient.ConfigureAsync(new
        {
            requiredBearerToken = ApiKey,
            models = new[]
            {
                new { id = requestedModel, kind = "chat" },
                new { id = primaryModel, kind = "chat" },
                new { id = fallbackModel, kind = "chat" },
            },
            chatScenarios = new[]
            {
                new
                {
                    id = "resilience-" + primaryModel,
                    modelId = primaryModel,
                    responses = new[] { primaryResponse },
                },
                new
                {
                    id = "resilience-" + fallbackModel,
                    modelId = fallbackModel,
                    responses = new[] { fallbackResponse },
                },
            },
        });
    }

    private static RetryPolicy CreateCircuitBreakerRetryPolicy()
        => new()
        {
            Enabled = true,
            MaxChannelRetries = 0,
            MaxSingleChannelRetries = 0,
            RetryDelayMs = 0,
            LoadBalancerStrategy = LoadBalancerStrategies.CircuitBreaker,
        };

    private static RetryPolicy CreateRetryPolicy(
        int maxChannelRetries,
        int maxSingleChannelRetries = 0,
        int nonStreamResponseTimeoutSeconds = 0)
        => new()
        {
            Enabled = true,
            MaxChannelRetries = maxChannelRetries,
            MaxSingleChannelRetries = maxSingleChannelRetries,
            RetryDelayMs = 0,
            LoadBalancerStrategy = RetryPolicy.FailoverStrategy,
            NonStreamResponseTimeoutSeconds = nonStreamResponseTimeoutSeconds,
        };

    private static string AutoDisablePolicies(int threshold)
        => JsonSerializer.Serialize(new
        {
            apiKeyAutoDisableMode = "custom",
            apiKeyAutoDisableRules = new[]
            {
                new
                {
                    statusCodes = new[] { StatusCodes.Status503ServiceUnavailable },
                    times = threshold,
                    action = RetryPolicy.AutoDisableActionPermanent,
                },
            },
        });

    private static object[] RepeatedProviderErrors(int count)
        => Enumerable
            .Repeat(ProviderError(
                StatusCodes.Status503ServiceUnavailable,
                "service_unavailable",
                "temporary outage"), count)
            .ToArray();

    private static ManualTimeProvider NewManualTimeProvider()
        => new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

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

    private static DefaultHttpContext NewContext(string body, string path = ChatPath)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseBody(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static OpenAiStreamEvidence InspectOpenAiStream(string sse)
    {
        var text = new StringBuilder();
        var hasDoneMarker = false;
        var hasFinishReason = false;
        var hasErrorEvent = false;
        var eventType = string.Empty;

        foreach (var raw in sse.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                eventType = string.Empty;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line["event:".Length..].Trim();
                hasErrorEvent |= eventType == "error";
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                hasDoneMarker = true;
                continue;
            }

            if (payload.Length == 0)
            {
                continue;
            }

            using var chunk = JsonDocument.Parse(payload);
            if (!chunk.RootElement.TryGetProperty("choices", out var choices))
            {
                continue;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    text.Append(content.GetString());
                }

                if (choice.TryGetProperty("finish_reason", out var finishReason)
                    && finishReason.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                {
                    hasFinishReason = true;
                }
            }
        }

        return new OpenAiStreamEvidence(text.ToString(), hasDoneMarker, hasFinishReason, hasErrorEvent);
    }

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

    private sealed class FixedCandidateSelector(params ChannelCandidate[] candidates)
        : IModelCandidateSelector
    {
        public Task<CandidateSelection> SelectAsync(
            LlmRequest request,
            RequestIdentity identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CandidateSelection(candidates, RoutingPolicy: null));
    }

    private sealed class FixedTraceStore : ITraceStore
    {
        private readonly Dictionary<string, TraceRecord> _traces = new(StringComparer.Ordinal);

        public Task<TraceRecord> GetOrCreateAsync(
            long projectId,
            string traceId,
            long? threadId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_traces.TryGetValue(traceId, out var trace))
            {
                var now = DateTime.UtcNow;
                trace = new TraceRecord
                {
                    Id = 88000 + _traces.Count + 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                    TraceId = traceId,
                    Status = "active",
                    ProjectId = projectId,
                    ThreadId = threadId,
                };
                _traces.Add(traceId, trace);
            }

            return Task.FromResult(trace);
        }

        public TraceRecord GetTrace(string traceId) => _traces[traceId];
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record OpenAiStreamEvidence(
        string Text,
        bool HasDoneMarker,
        bool HasFinishReason,
        bool HasErrorEvent);

    private sealed class AutoDisableHarness
    {
        public FakeChannelCredentialMutations Credentials { get; } = new();

        public ChannelAutoDisableService Service { get; }

        public AutoDisableHarness(FakeEnabledChannelProvider enabled)
        {
            Service = new ChannelAutoDisableService(
                enabled,
                new FakeGcSettingsService(),
                new FilteringCredentialMutations(enabled, Credentials),
                new FakeChannelWriteStore(),
                new AutoDisableCounters(),
                new RecordingEnabledChannelsRefresher(),
                new FakeWebhookSink(),
                TimeProvider.System);
        }
    }

    private sealed class FilteringCredentialMutations(
        FakeEnabledChannelProvider enabled,
        FakeChannelCredentialMutations mutations) : IChannelCredentialMutations
    {
        public async Task DisableApiKeyAsync(
            long channelId,
            string key,
            int errorCode,
            string reason,
            DateTime? expiresAt = null,
            CancellationToken cancellationToken = default)
        {
            await mutations.DisableApiKeyAsync(
                channelId,
                key,
                errorCode,
                reason,
                expiresAt,
                cancellationToken);

            var remaining = (await enabled.ListEnabledAsync(cancellationToken))
                .Where(channel => channel.Id != channelId)
                .ToArray();
            enabled.Set(remaining);
        }

        public Task<DeleteDisabledApiKeysResult> DeleteDisabledApiKeysAsync(
            long channelId,
            IReadOnlyList<string> keys,
            CancellationToken cancellationToken = default)
            => mutations.DeleteDisabledApiKeysAsync(channelId, keys, cancellationToken);
    }

    private sealed class CapturingAutoDisableQueue(ChannelAutoDisableService service)
        : IChannelAutoDisableEvaluationQueue
    {
        private readonly Channel<(ChannelPerformanceRecord Record, Task Evaluation)> _evaluations =
            Channel.CreateUnbounded<(ChannelPerformanceRecord, Task)>();

        public ConcurrentQueue<ChannelPerformanceRecord> Records { get; } = new();

        public void Enqueue(ChannelPerformanceRecord performance)
        {
            Records.Enqueue(performance);
            _evaluations.Writer.TryWrite((performance, service.EvaluateAsync(performance)));
        }

        public async Task<ChannelPerformanceRecord> WaitForNextEvaluationAsync()
        {
            var (record, evaluation) = await _evaluations.Reader.ReadAsync();
            await evaluation;
            return record;
        }
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
