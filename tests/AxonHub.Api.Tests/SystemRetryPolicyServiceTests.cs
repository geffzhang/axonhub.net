using AxonHub.Api.Orchestration;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.SystemService.RetryPolicyOrDefault and normalizeRetryPolicy
/// (internal/server/biz/system.go:1042-1172): defaults, lookup failures,
/// Go's JSON null semantics, clamps and value normalization.
/// </summary>
public sealed class SystemRetryPolicyServiceTests
{
    [Fact]
    public async Task MissingRowReturnsReferenceDefaults()
    {
        var policy = await LoadAsync(null);

        AssertDefaults(policy);
    }

    [Fact]
    public async Task StoreFailureFallsBackToDefaults()
    {
        var policy = await LoadAsync(
            """{"enabled":false,"max_channel_retries":9}""",
            new InvalidOperationException("database is down"));

        AssertDefaults(policy);
    }

    [Fact]
    public async Task SoftDeletedRowFallsBackToDefaults()
    {
        var store = new FakeSystemStore();
        store.Add(
            SystemRetryPolicyService.RetryPolicyKey,
            """{"enabled":false,"max_channel_retries":9}""",
            static row => row.DeletedAt = 1);

        var policy = await new SystemRetryPolicyService(store).GetRetryPolicyAsync();

        AssertDefaults(policy);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public async Task MalformedDocumentFallsBackToDefaults(string value)
    {
        var policy = await LoadAsync(value);

        AssertDefaults(policy);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task NullOrEmptyDocumentYieldsTheZeroPolicy(string value)
    {
        var policy = await LoadAsync(value);

        // Go unmarshals into a zero struct and then normalizes only the
        // strategy, sticky mode and upstream mode.
        Assert.False(policy.Enabled);
        Assert.Equal(0, policy.MaxChannelRetries);
        Assert.Equal(0, policy.MaxSingleChannelRetries);
        Assert.Equal(0, policy.RetryDelayMs);
        Assert.Equal(0, policy.StreamFirstEventTimeoutSeconds);
        Assert.Equal(0, policy.NonStreamResponseTimeoutSeconds);
        Assert.False(policy.EmptyResponseDetection);
        Assert.Equal(RetryPolicy.AdaptiveStrategy, policy.LoadBalancerStrategy);
        Assert.Equal(RetryPolicy.TraceStickyPreferPreviousChannel, policy.TraceStickyMode);
        Assert.Equal(RetryPolicy.UpstreamErrorModePassthrough, policy.UpstreamErrorPolicy.Mode);
        Assert.Empty(policy.AutoDisableChannel.Rules);
    }

    [Fact]
    public async Task EveryFieldIsDecodedFromSnakeCase()
    {
        var policy = await LoadAsync(
            """
            {
              "enabled": true,
              "max_channel_retries": 7,
              "max_single_channel_retries": 5,
              "retry_delay_ms": 4242,
              "stream_first_event_timeout_seconds": 12,
              "non_stream_response_timeout_seconds": 34,
              "load_balancer_strategy": "round-robin",
              "trace_sticky_mode": "disabled",
              "empty_response_detection": true,
              "upstream_error_policy": {"mode": "custom", "custom_message": "try later"},
              "auto_disable_channel": {
                "enabled": true,
                "rules": [{
                  "statusCodes": [401, 403],
                  "keywordPatterns": ["quota"],
                  "times": 3,
                  "action": "temporary_disable",
                  "disableDurationMinutes": 5,
                  "disableUntilCron": "0 0 * * *",
                  "disableUntilTimezone": "UTC"
                }]
              }
            }
            """);

        Assert.True(policy.Enabled);
        Assert.Equal(7, policy.MaxChannelRetries);
        Assert.Equal(5, policy.MaxSingleChannelRetries);
        Assert.Equal(4242, policy.RetryDelayMs);
        Assert.Equal(12, policy.StreamFirstEventTimeoutSeconds);
        Assert.Equal(34, policy.NonStreamResponseTimeoutSeconds);
        Assert.Equal(RetryPolicy.RoundRobinStrategy, policy.LoadBalancerStrategy);
        Assert.Equal(RetryPolicy.TraceStickyDisabled, policy.TraceStickyMode);
        Assert.True(policy.EmptyResponseDetection);
        Assert.Equal(RetryPolicy.UpstreamErrorModeCustom, policy.UpstreamErrorPolicy.Mode);
        Assert.Equal("try later", policy.UpstreamErrorPolicy.CustomMessage);
        Assert.True(policy.AutoDisableChannel.Enabled);

        var rule = Assert.Single(policy.AutoDisableChannel.Rules);
        Assert.Equal([401, 403], rule.StatusCodes);
        Assert.Equal(["quota"], rule.KeywordPatterns);
        Assert.Equal(3, rule.Times);
        Assert.Equal("temporary_disable", rule.Action);
        Assert.Equal(5, rule.DisableDurationMinutes);
        Assert.Equal("0 0 * * *", rule.DisableUntilCron);
        Assert.Equal("UTC", rule.DisableUntilTimezone);
    }

    [Fact]
    public async Task MissingMembersKeepGoZeroValuesRatherThanDefaults()
    {
        var policy = await LoadAsync("""{"max_channel_retries":7}""");

        Assert.False(policy.Enabled);
        Assert.Equal(7, policy.MaxChannelRetries);
        Assert.Equal(0, policy.MaxSingleChannelRetries);
        Assert.Equal(0, policy.RetryDelayMs);
        Assert.False(policy.EmptyResponseDetection);
    }

    [Fact]
    public async Task JsonNullScalarsAreIgnoredLikeGo()
    {
        var policy = await LoadAsync(
            """
            {
              "enabled": null,
              "max_channel_retries": null,
              "max_single_channel_retries": null,
              "retry_delay_ms": null,
              "stream_first_event_timeout_seconds": null,
              "non_stream_response_timeout_seconds": null,
              "load_balancer_strategy": null,
              "empty_response_detection": null,
              "auto_disable_channel": null,
              "upstream_error_policy": null
            }
            """);

        Assert.False(policy.Enabled);
        Assert.Equal(0, policy.MaxChannelRetries);
        Assert.Equal(0, policy.RetryDelayMs);
        Assert.Equal(RetryPolicy.AdaptiveStrategy, policy.LoadBalancerStrategy);
        Assert.Equal(RetryPolicy.TraceStickyPreferPreviousChannel, policy.TraceStickyMode);
        Assert.Equal(RetryPolicy.UpstreamErrorModePassthrough, policy.UpstreamErrorPolicy.Mode);
        Assert.Empty(policy.AutoDisableChannel.Rules);
    }

    [Theory]
    [InlineData("adaptive", "adaptive")]
    [InlineData("failover", "failover")]
    [InlineData("circuit-breaker", "circuit-breaker")]
    [InlineData("round-robin", "round-robin")]
    [InlineData("weighted", "failover")]
    [InlineData("unknown", "adaptive")]
    [InlineData("", "adaptive")]
    public async Task LoadBalancerStrategyIsNormalized(string stored, string expected)
    {
        var policy = await LoadAsync(
            $$"""{"load_balancer_strategy":"{{stored}}"}""");

        Assert.Equal(expected, policy.LoadBalancerStrategy);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(600, 600, 600, 600)]
    [InlineData(601, 600, 601, 600)]
    [InlineData(9999, 600, 9999, 600)]
    public async Task ResponseTimeoutsAreClamped(
        int streamSeconds,
        int expectedStream,
        int nonStreamSeconds,
        int expectedNonStream)
    {
        var policy = await LoadAsync(
            $$"""
            {
              "stream_first_event_timeout_seconds": {{streamSeconds}},
              "non_stream_response_timeout_seconds": {{nonStreamSeconds}}
            }
            """);

        Assert.Equal(expectedStream, policy.StreamFirstEventTimeoutSeconds);
        Assert.Equal(expectedNonStream, policy.NonStreamResponseTimeoutSeconds);
    }

    [Theory]
    [InlineData("disabled", "disabled")]
    [InlineData("prefer_previous_channel", "prefer_previous_channel")]
    [InlineData("unknown", "prefer_previous_channel")]
    [InlineData("", "prefer_previous_channel")]
    public async Task TraceStickyModeIsNormalized(string stored, string expected)
    {
        var policy = await LoadAsync(
            $$"""{"trace_sticky_mode":"{{stored}}"}""");

        Assert.Equal(expected, policy.TraceStickyMode);
    }

    [Fact]
    public async Task KeysAreMatchedCaseInsensitively()
    {
        var policy = await LoadAsync(
            """{"ENABLED":true,"Max_Channel_Retries":9,"Load_Balancer_Strategy":"round-robin"}""");

        Assert.True(policy.Enabled);
        Assert.Equal(9, policy.MaxChannelRetries);
        Assert.Equal(RetryPolicy.RoundRobinStrategy, policy.LoadBalancerStrategy);
    }

    [Fact]
    public async Task UnknownMembersAreIgnored()
    {
        var policy = await LoadAsync(
            """{"enabled":true,"future_setting":{"nested":1}}""");

        Assert.True(policy.Enabled);
        Assert.Equal(0, policy.MaxChannelRetries);
    }

    [Fact]
    public async Task DuplicateKeysUseTheLastValue()
    {
        var policy = await LoadAsync(
            """{"max_channel_retries":2,"max_channel_retries":5}""");

        Assert.Equal(5, policy.MaxChannelRetries);
    }

    [Fact]
    public async Task DuplicateKeyFollowedByNullUsesDotNetSemantics()
    {
        // C032: Go ignores JSON null for scalars, so the earlier values survive;
        // System.Text.Json replaces them with null. The settings writer always
        // emits complete objects, so only hand-edited rows can reach this.
        var policy = await LoadAsync(
            """
            {
              "max_channel_retries": 7,
              "max_channel_retries": null,
              "load_balancer_strategy": "round-robin",
              "load_balancer_strategy": null
            }
            """);

        Assert.Equal(0, policy.MaxChannelRetries);
        Assert.Equal(RetryPolicy.AdaptiveStrategy, policy.LoadBalancerStrategy);
    }

    [Theory]
    [InlineData("""{"enabled":"yes"}""")]
    [InlineData("""{"enabled":1}""")]
    [InlineData("""{"max_channel_retries":1.5}""")]
    [InlineData("""{"load_balancer_strategy":7}""")]
    [InlineData("""{"upstream_error_policy":"hidden"}""")]
    [InlineData("""{"auto_disable_channel":{"rules":{"statusCodes":[401]}}}""")]
    [InlineData("""{"auto_disable_channel":{"statuses":[{"status":"401"}]}}""")]
    public async Task WrongMemberTypeFallsBackToDefaults(string value)
    {
        var policy = await LoadAsync(value);

        AssertDefaults(policy);
    }

    [Fact]
    public async Task LegacyStatusesMigrateToPermanentRules()
    {
        var policy = await LoadAsync(
            """
            {
              "auto_disable_channel": {
                "enabled": true,
                "statuses": [{"status": 401, "times": 3}]
              }
            }
            """);

        Assert.True(policy.AutoDisableChannel.Enabled);
        var rule = Assert.Single(policy.AutoDisableChannel.Rules);
        Assert.Equal([401], rule.StatusCodes);
        Assert.Equal(3, rule.Times);
        Assert.Equal(RetryPolicy.AutoDisableActionPermanent, rule.Action);
        Assert.Null(rule.KeywordPatterns);
        Assert.Null(rule.DisableDurationMinutes);
    }

    [Fact]
    public async Task ExistingRulesWinOverLegacyStatuses()
    {
        var policy = await LoadAsync(
            """
            {
              "auto_disable_channel": {
                "rules": [{"statusCodes": [429], "times": 1, "action": "permanent_disable"}],
                "statuses": [{"status": 401, "times": 3}]
              }
            }
            """);

        var rule = Assert.Single(policy.AutoDisableChannel.Rules);
        Assert.Equal([429], rule.StatusCodes);
    }

    [Fact]
    public async Task CustomUpstreamMessageBlankFallsBackToHidden()
    {
        var policy = await LoadAsync(
            """{"upstream_error_policy":{"mode":"custom","custom_message":"   "}}""");

        Assert.Equal(RetryPolicy.UpstreamErrorModeHidden, policy.UpstreamErrorPolicy.Mode);
        Assert.Equal("   ", policy.UpstreamErrorPolicy.CustomMessage);
    }

    [Theory]
    [InlineData("passthrough", "passthrough")]
    [InlineData("hidden", "hidden")]
    [InlineData("custom", "custom")]
    [InlineData("unknown", "passthrough")]
    [InlineData("", "passthrough")]
    public async Task UpstreamErrorModeIsNormalized(string stored, string expected)
    {
        var policy = await LoadAsync(
            $$$"""{"upstream_error_policy":{"mode":"{{{stored}}}","custom_message":"nope"}}""");

        Assert.Equal(expected, policy.UpstreamErrorPolicy.Mode);
    }

    [Fact]
    public async Task RetryCountersAndDelayAreNotClamped()
    {
        var policy = await LoadAsync(
            """{"max_channel_retries":-2,"max_single_channel_retries":-3,"retry_delay_ms":-5}""");

        Assert.Equal(-2, policy.MaxChannelRetries);
        Assert.Equal(-3, policy.MaxSingleChannelRetries);
        Assert.Equal(-5, policy.RetryDelayMs);
    }

    [Fact]
    public async Task NullSliceElementsBecomeGoZeroValues()
    {
        var policy = await LoadAsync(
            """
            {
              "auto_disable_channel": {
                "rules": [{
                  "statusCodes": [401, null],
                  "keywordPatterns": [null, "quota"]
                }],
                "statuses": [null]
              }
            }
            """);

        var rule = Assert.Single(policy.AutoDisableChannel.Rules);
        Assert.Equal([401, 0], rule.StatusCodes);
        Assert.Equal(["", "quota"], rule.KeywordPatterns);
        Assert.Equal(0, rule.Times);
        Assert.Equal("", rule.Action);
    }

    private static void AssertDefaults(RetryPolicy policy)
    {
        Assert.True(policy.Enabled);
        Assert.Equal(3, policy.MaxChannelRetries);
        Assert.Equal(2, policy.MaxSingleChannelRetries);
        Assert.Equal(1000, policy.RetryDelayMs);
        Assert.Equal(0, policy.StreamFirstEventTimeoutSeconds);
        Assert.Equal(0, policy.NonStreamResponseTimeoutSeconds);
        Assert.False(policy.EmptyResponseDetection);
        Assert.Equal(RetryPolicy.AdaptiveStrategy, policy.LoadBalancerStrategy);
        Assert.Equal(RetryPolicy.TraceStickyPreferPreviousChannel, policy.TraceStickyMode);
        Assert.False(policy.AutoDisableChannel.Enabled);
        Assert.Empty(policy.AutoDisableChannel.Rules);
        Assert.Equal(RetryPolicy.UpstreamErrorModePassthrough, policy.UpstreamErrorPolicy.Mode);
        Assert.Equal("", policy.UpstreamErrorPolicy.CustomMessage);
    }

    private static async Task<RetryPolicy> LoadAsync(string? value, Exception? failure = null)
    {
        var store = new FakeSystemStore();
        if (failure is not null)
        {
            store.GetFailure = failure;
        }

        if (value is not null)
        {
            store.Add(SystemRetryPolicyService.RetryPolicyKey, value);
        }

        return await new SystemRetryPolicyService(store).GetRetryPolicyAsync();
    }
}
