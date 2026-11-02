using System.Buffers.Text;
using System.Text;
using AxonHub.Llm.Transformer.Xai.Subscription;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for <c>PlanFromAccessToken</c>
/// (llm/transformer/xai/subscription/plan_test.go): the unsigned JWT payload's
/// tier claim maps to the display plan, numeric and named tiers both work, and
/// an opaque token yields no plan.
/// </summary>
public sealed class XaiSubscriptionPlanTests
{
    [Theory]
    // The reference table (plan_test.go).
    [InlineData("""{"tier":5}""", "SuperGrok Heavy")]
    [InlineData("""{"tier":"6"}""", "SuperGrok Lite")]
    [InlineData("""{"tier":"supergrok-plus"}""", "SuperGrok Plus")]
    [InlineData("""{"tier":"enterprise"}""", "")]
    // Port-added: the remaining tier encodings the Go decoder accepts.
    [InlineData("""{"tier":1}""", "SuperGrok")]
    [InlineData("""{"tier":"supergrok_heavy"}""", "SuperGrok Heavy")]
    [InlineData("""{"tier":" 7 "}""", "SuperGrok Plus")]
    [InlineData("""{"tier":5.5}""", "")]
    [InlineData("""{"tier":null}""", "")]
    [InlineData("""{"tier":true}""", "")]
    [InlineData("""{}""", "")]
    [InlineData("""{"tier":"x_premium+"}""", "X Premium Plus")]
    public void PlanFromAccessToken_reads_the_tier_claim(string payload, string expected)
    {
        var token = "header."
            + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))
            + ".signature";

        Assert.Equal(expected, SubscriptionPlan.PlanFromAccessToken(token));
    }

    [Fact]
    public void PlanFromAccessTokenRejectsOpaqueToken()
    {
        Assert.Empty(SubscriptionPlan.PlanFromAccessToken("opaque-access-token"));
    }

    [Fact]
    public void PlanFromAccessTokenRejectsUndecodablePayload()
    {
        // Go: base64.RawURLEncoding.DecodeString fails and the plan is empty.
        Assert.Empty(SubscriptionPlan.PlanFromAccessToken("header.!!!not-base64!!!.signature"));
    }
}
