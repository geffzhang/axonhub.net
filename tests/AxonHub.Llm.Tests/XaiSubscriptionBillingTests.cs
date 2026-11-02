using System.Text;
using System.Text.Json;
using AxonHub.Llm.Transformer.Xai.Subscription;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for <c>ParseBillingResponses</c>
/// (llm/transformer/xai/subscription/billing_test.go): the weekly credit window
/// merges with the monthly plan payload, a response without any config is
/// rejected, and empty configs preserve absent windows. The port-added cases
/// pin the Go decoder rules the reference relies on: the
/// <c>{"val": ...}</c> wrapper, raw numbers and trimmed strings, key matching
/// case-insensitivity, and non-object documents.
/// </summary>
public sealed class XaiSubscriptionBillingTests
{
    [Fact]
    public void ParseBillingResponses_merges_weekly_usage_and_monthly_plan()
    {
        var weekly = Encoding.UTF8.GetBytes(
            """
            {
                "config": {
                    "currentPeriod": {"type":"WEEKLY","start":"2026-07-09T03:25:00Z","end":"2026-07-16T03:25:00Z"},
                    "creditUsagePercent": 82.5,
                    "productUsage": [{"product":"Api","usagePercent":82.5}],
                    "prepaidBalance": {"val": 12},
                    "onDemandCap": {"val": 100},
                    "onDemandUsed": {"val": 5},
                    "isUnifiedBillingUser": true
                }
            }
            """);
        var monthly = Encoding.UTF8.GetBytes(
            """
            {
                "config": {
                    "monthlyLimit": {"val": 15000},
                    "used": {"val": 7500},
                    "billingPeriodStart": "2026-07-01T00:00:00Z",
                    "billingPeriodEnd": "2026-08-01T00:00:00Z"
                }
            }
            """);

        var result = SubscriptionBilling.ParseBillingResponses(weekly, monthly);

        Assert.Equal("SuperGrok", result.Plan);
        Assert.Equal(82.5, result.Weekly!.UsagePercent);
        Assert.Equal("2026-07-16T03:25:00Z", result.Weekly.ResetAt);
        Assert.Equal(50.0, result.Monthly!.UsagePercent);
        Assert.Equal(150.0, result.Monthly.LimitUsd);
        Assert.Equal(75.0, result.Monthly.UsedUsd);
        Assert.Equal("2026-08-01T00:00:00Z", result.Monthly.ResetAt);
    }

    [Fact]
    public void ParseBillingResponses_identifies_supergrok_heavy()
    {
        var result = SubscriptionBilling.ParseBillingResponses(
            """{"config":{"creditUsagePercent":2}}"""u8.ToArray(),
            """{"config":{"monthlyLimit":{"val":"150000"},"used":{"val":"30000"}}}"""u8.ToArray());

        Assert.Equal("SuperGrok Heavy", result.Plan);
        Assert.Equal(20.0, result.Monthly!.UsagePercent);
    }

    [Fact]
    public void ParseBillingResponses_rejects_missing_billing_config()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SubscriptionBilling.ParseBillingResponses(
                "{}"u8.ToArray(),
                "{}"u8.ToArray()));

        Assert.Equal("xAI billing responses contain no config", error.Message);
    }

    [Fact]
    public void ParseBillingResponses_preserves_missing_windows()
    {
        var result = SubscriptionBilling.ParseBillingResponses(
            """{"config":{}}"""u8.ToArray(),
            """{"config":{}}"""u8.ToArray());

        Assert.Null(result.Weekly);
        Assert.Null(result.Monthly);
        Assert.Null(result.Plan);
    }

    // ------------------------------------------------------------------
    // Port-added: Go decoder semantics the reference tests do not cover.
    // ------------------------------------------------------------------

    [Fact]
    public void ParseBillingResponses_treats_null_config_as_absent()
    {
        // Go: a null config pointer stays nil, so two null configs still fail.
        var error = Assert.Throws<InvalidOperationException>(
            () => SubscriptionBilling.ParseBillingResponses(
                """{"config":null}"""u8.ToArray(),
                """{"config":null}"""u8.ToArray()));

        Assert.Equal("xAI billing responses contain no config", error.Message);
    }

    [Fact]
    public void ParseBillingResponses_null_wrapper_value_yields_no_amount()
    {
        // Go: the wrapper is taken whenever "val" is present; {"val": null}
        // parses to a nil amount rather than falling back to the object.
        var result = SubscriptionBilling.ParseBillingResponses(
            """{"config":{}}"""u8.ToArray(),
            """{"config":{"monthlyLimit":{"val":null},"used":{"val":5}}}"""u8.ToArray());

        Assert.NotNull(result.Monthly);
        Assert.Equal(0.0, result.Monthly!.LimitUsd);
        Assert.Equal(0.05, result.Monthly.UsedUsd);
        Assert.Equal(0.0, result.Monthly.UsagePercent);
        Assert.Null(result.Monthly.ResetAt);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void ParseBillingResponses_accepts_raw_numbers_and_trimmed_strings()
    {
        var result = SubscriptionBilling.ParseBillingResponses(
            """{"config":{"creditUsagePercent":0}}"""u8.ToArray(),
            """{"config":{"monthlyLimit":15000,"used":" 7500 "}}"""u8.ToArray());

        Assert.Equal(50.0, result.Monthly!.UsagePercent);
        Assert.Equal(150.0, result.Monthly.LimitUsd);
        Assert.Equal(75.0, result.Monthly.UsedUsd);
        Assert.Equal("SuperGrok", result.Plan);
    }

    [Fact]
    public void ParseBillingResponses_matches_keys_case_insensitively()
    {
        var result = SubscriptionBilling.ParseBillingResponses(
            """{"CONFIG":{"CreditUsagePercent":10,"CURRENTPERIOD":{"END":"2026-01-01T00:00:00Z"}}}"""u8.ToArray(),
            """{"Config":{"MonthlyLimit":{"VAL":100},"Used":{"Val":25}}}"""u8.ToArray());

        Assert.Equal(10.0, result.Weekly!.UsagePercent);
        Assert.Equal("2026-01-01T00:00:00Z", result.Weekly.ResetAt);
        Assert.Equal(25.0, result.Monthly!.UsagePercent);
        Assert.Equal(1.0, result.Monthly.LimitUsd);
        Assert.Equal(0.25, result.Monthly.UsedUsd);
    }

    [Fact]
    public void ParseBillingResponses_rejects_wrapper_without_value()
    {
        // Go: an object without "val" is not a wrapper, so the whole object is
        // parsed as a number and fails.
        Assert.Throws<JsonException>(
            () => SubscriptionBilling.ParseBillingResponses(
                """{"config":{}}"""u8.ToArray(),
                """{"config":{"monthlyLimit":{"amount":15000}}}"""u8.ToArray()));
    }

    [Fact]
    public void ParseBillingResponses_rejects_non_object_documents()
    {
        Assert.Throws<JsonException>(
            () => SubscriptionBilling.ParseBillingResponses(
                "[1]"u8.ToArray(),
                "{}"u8.ToArray()));

        Assert.Throws<JsonException>(
            () => SubscriptionBilling.ParseBillingResponses(
                "{}"u8.ToArray(),
                "1"u8.ToArray()));
    }
}
