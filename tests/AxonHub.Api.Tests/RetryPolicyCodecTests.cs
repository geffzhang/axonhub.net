using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the retry-policy codec shared by the runtime provider and the admin
/// GraphQL surface: Go field order, omitempty rules, the TraceStickyMode
/// unmarshaler, legacy status migration and normalizeAutoDisableRules
/// (internal/server/biz/system.go:1042-1172, channel.go:759-846).
/// </summary>
public sealed class RetryPolicyCodecTests
{
    [Fact]
    public void DefaultPolicySerializesInGoFieldOrder()
    {
        var json = RetryPolicyCodec.Serialize(RetryPolicy.Default);

        Assert.Equal(
            """
            {"enabled":true,"max_channel_retries":3,"max_single_channel_retries":2,"retry_delay_ms":1000,"stream_first_event_timeout_seconds":0,"non_stream_response_timeout_seconds":0,"load_balancer_strategy":"adaptive","trace_sticky_mode":"prefer_previous_channel","auto_disable_channel":{"enabled":false,"rules":[]},"empty_response_detection":false,"upstream_error_policy":{"mode":"passthrough","custom_message":""}}
            """,
            json);
    }

    [Fact]
    public void RuleMembersUseGoOmitempty()
    {
        var policy = RetryPolicy.Default with
        {
            AutoDisableChannel = new AutoDisableChannel(
                true,
                [
                    new AutoDisableRule(
                        null,
                        null,
                        3,
                        RetryPolicy.AutoDisableActionPermanent,
                        null,
                        string.Empty,
                        string.Empty),
                ]),
        };

        var json = RetryPolicyCodec.Serialize(policy);

        Assert.Contains(
            "\"auto_disable_channel\":{\"enabled\":true,\"rules\":[{\"times\":3,\"action\":\"permanent_disable\"}]}",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyRuleListsStillSerializeWhenNonNil()
    {
        var policy = RetryPolicy.Default with
        {
            AutoDisableChannel = new AutoDisableChannel(
                false,
                [
                    new AutoDisableRule(
                        [],
                        [],
                        1,
                        RetryPolicy.AutoDisableActionTemporary,
                        5,
                        string.Empty,
                        string.Empty),
                ]),
        };

        var json = RetryPolicyCodec.Serialize(policy);

        // omitempty drops empty (but non-nil) slices; the duration pointer is
        // kept because only a nil pointer is omitted.
        Assert.Contains(
            "{\"times\":1,\"action\":\"temporary_disable\",\"disableDurationMinutes\":5}",
            json,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("disabled", RetryPolicy.TraceStickyDisabled)]
    [InlineData("DISABLED", RetryPolicy.TraceStickyDisabled)]
    [InlineData("prefer_previous_channel", RetryPolicy.TraceStickyPreferPreviousChannel)]
    [InlineData("PREFER_PREVIOUS_CHANNEL", RetryPolicy.TraceStickyPreferPreviousChannel)]
    public void TraceStickyModeAcceptsGoAndGraphQLNames(string stored, string expected)
    {
        var policy = RetryPolicyCodec.Deserialize(
            $$"""{"trace_sticky_mode":"{{stored}}"}""");

        Assert.Equal(expected, policy.TraceStickyMode);
    }

    [Fact]
    public void NullTraceStickyModeIsRejectedByTheCustomUnmarshaler()
    {
        var error = Assert.Throws<JsonException>(
            () => RetryPolicyCodec.Deserialize("""{"trace_sticky_mode":null}"""));

        Assert.Equal("invalid TraceStickyMode: \"\"", error.Message);
    }

    [Fact]
    public void UnknownTraceStickyModeQuotesTheRawValue()
    {
        var error = Assert.Throws<JsonException>(
            () => RetryPolicyCodec.Deserialize("""{"trace_sticky_mode":"BOGUS"}"""));

        Assert.Equal("invalid TraceStickyMode: \"BOGUS\"", error.Message);
    }

    [Fact]
    public void NonStringTraceStickyModeUsesGoUnmarshalText()
    {
        var error = Assert.Throws<JsonException>(
            () => RetryPolicyCodec.Deserialize("""{"trace_sticky_mode":5}"""));

        Assert.Equal(
            "invalid TraceStickyMode: json: cannot unmarshal number into Go value of type string",
            error.Message);
    }

    [Fact]
    public void TopLevelArraysNameTheBizType()
    {
        var error = Assert.Throws<JsonException>(() => RetryPolicyCodec.Deserialize("[1]"));

        Assert.Equal(
            "json: cannot unmarshal array into Go value of type biz.RetryPolicy",
            error.Message);
    }

    [Fact]
    public void LegacyStatusesBecomePermanentRulesOnlyWhenRulesAreEmpty()
    {
        var policy = RetryPolicyCodec.Deserialize(
            """
            {
              "auto_disable_channel": {
                "enabled": true,
                "statuses": [{"status": 401, "times": 3}]
              }
            }
            """);

        var rule = Assert.Single(policy.AutoDisableChannel.Rules);
        Assert.Equal([401], rule.StatusCodes);
        Assert.Null(rule.KeywordPatterns);
        Assert.Equal(3, rule.Times);
        Assert.Equal(RetryPolicy.AutoDisableActionPermanent, rule.Action);

        var existing = RetryPolicyCodec.Deserialize(
            """
            {
              "auto_disable_channel": {
                "rules": [{"statusCodes":[429],"times":1,"action":"permanent_disable"}],
                "statuses": [{"status":401,"times":3}]
              }
            }
            """);
        var kept = Assert.Single(existing.AutoDisableChannel.Rules);
        Assert.Equal([429], kept.StatusCodes);
    }

    [Fact]
    public void NormalizeAutoDisableRulesSortsAndDedupesStatusCodes()
    {
        var rules = RetryPolicyCodec.NormalizeAutoDisableRules(
            [
                new AutoDisableRule(
                    [429, 401, 429, 500],
                    [" quota ", "", "quota", " billing "],
                    2,
                    RetryPolicy.AutoDisableActionPermanent,
                    null,
                    string.Empty,
                    string.Empty),
            ],
            allowDelete: false);

        var rule = Assert.Single(rules);
        Assert.Equal([401, 429, 500], rule.StatusCodes);
        Assert.Equal(["quota", "billing"], rule.KeywordPatterns);
    }

    [Fact]
    public void NormalizeAutoDisableRulesClearsInapplicableScheduleFields()
    {
        var temporary = RetryPolicyCodec.NormalizeAutoDisableRules(
            [
                new AutoDisableRule(
                    null,
                    null,
                    1,
                    RetryPolicy.AutoDisableActionTemporary,
                    5,
                    "0 0 * * *",
                    "UTC"),
            ],
            allowDelete: false);

        Assert.Equal(string.Empty, Assert.Single(temporary).DisableUntilCron);
        Assert.Equal(string.Empty, temporary[0].DisableUntilTimezone);

        var cron = RetryPolicyCodec.NormalizeAutoDisableRules(
            [
                new AutoDisableRule(
                    null,
                    null,
                    1,
                    RetryPolicy.AutoDisableActionUntilCron,
                    5,
                    " 0 0 * * * ",
                    " UTC "),
            ],
            allowDelete: false);

        Assert.Null(cron[0].DisableDurationMinutes);
        Assert.Equal("0 0 * * *", cron[0].DisableUntilCron);
        Assert.Equal("UTC", cron[0].DisableUntilTimezone);

        var permanent = RetryPolicyCodec.NormalizeAutoDisableRules(
            [
                new AutoDisableRule(
                    null,
                    null,
                    1,
                    RetryPolicy.AutoDisableActionPermanent,
                    5,
                    "0 0 * * *",
                    "UTC"),
            ],
            allowDelete: false);

        Assert.Null(permanent[0].DisableDurationMinutes);
        Assert.Equal(string.Empty, permanent[0].DisableUntilCron);
        Assert.Equal(string.Empty, permanent[0].DisableUntilTimezone);
    }

    [Theory]
    [InlineData(0, "permanent_disable", null, "", "", "API key rule 1 consecutive error count must be at least 1")]
    [InlineData(1, "temporary_disable", null, "", "", "API key rule 1 requires a disable duration for temporary disable")]
    [InlineData(1, "temporary_disable", 0, "", "", "API key rule 1 disable duration must be at least 1 minute")]
    [InlineData(1, "disable_until_cron", null, "  ", "", "API key rule 1 requires a cron expression for scheduled recovery")]
    [InlineData(1, "disable_until_cron", null, "0 0 * * *", "Mars/Olympus", "API key rule 1 has invalid timezone \"Mars/Olympus\": unknown time zone Mars/Olympus")]
    [InlineData(1, "permanent_disable_delete", null, "", "", "API key rule 1 cannot use permanent_disable_delete in global auto-disable settings")]
    [InlineData(1, "bogus", null, "", "", "API key rule 1 has unsupported action \"bogus\"")]
    public void NormalizeAutoDisableRulesReportsTheReferenceMessage(
        int times,
        string action,
        int? duration,
        string cron,
        string timezone,
        string expected)
    {
        var rule = new AutoDisableRule(null, null, times, action, duration, cron, timezone);

        var error = Assert.Throws<AutoDisableRuleValidationException>(
            () => RetryPolicyCodec.NormalizeAutoDisableRules([rule], allowDelete: false));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void InvalidCronQuotesTheTrimmedExpressionAndAppendsTheParserError()
    {
        var rule = new AutoDisableRule(
            null,
            null,
            1,
            RetryPolicy.AutoDisableActionUntilCron,
            null,
            "  bad  ",
            string.Empty);

        var error = Assert.Throws<AutoDisableRuleValidationException>(
            () => RetryPolicyCodec.NormalizeAutoDisableRules([rule], allowDelete: false));

        Assert.StartsWith(
            "API key rule 1 has invalid cron expression \"bad\": ",
            error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(600)]
    public void StatusCodesMustBeHttpCodes(int statusCode)
    {
        var rule = new AutoDisableRule(
            [statusCode],
            null,
            1,
            RetryPolicy.AutoDisableActionPermanent,
            null,
            string.Empty,
            string.Empty);

        var error = Assert.Throws<AutoDisableRuleValidationException>(
            () => RetryPolicyCodec.NormalizeAutoDisableRules([rule], allowDelete: false));

        Assert.Equal(
            "API key rule 1 has invalid HTTP status code " + statusCode,
            error.Message);
    }

    [Fact]
    public void DeleteActionIsAllowedWhenTheCallerPermitsIt()
    {
        var rule = new AutoDisableRule(
            null,
            null,
            1,
            RetryPolicy.AutoDisableActionPermanentDelete,
            5,
            "0 0 * * *",
            "UTC");

        var normalized = RetryPolicyCodec.NormalizeAutoDisableRules([rule], allowDelete: true);

        Assert.Null(normalized[0].DisableDurationMinutes);
        Assert.Equal(string.Empty, normalized[0].DisableUntilCron);
    }

    [Fact]
    public void CronCorpusMatchesTheReferenceParser()
    {
        var path = FindCorpus();
        var checkedCases = 0;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var input = root.GetProperty("input").GetString()!;
            var expected = root.TryGetProperty("error", out var error) ? error.GetString() : null;

            Assert.True(
                expected == CronExpression.Validate(input),
                $"cron {GoCast.GoQuote(input)}: expected {expected ?? "<valid>"}, "
                + $"got {CronExpression.Validate(input) ?? "<valid>"}");
            checkedCases++;
        }

        Assert.Equal(86, checkedCases);
    }

    private static string FindCorpus()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "corpus",
                "cronexpr",
                "expected.jsonl");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("corpus/cronexpr/expected.jsonl was not found.");
    }
}
