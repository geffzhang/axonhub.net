using System.Text;
using System.Text.Json;
using AxonHub.Api.AnthropicUsage;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/api/anthropic_input_usage_test.go's estimator
/// coverage: only textual evidence counts, and concurrent estimates are stable.
/// </summary>
public sealed class AnthropicInputUsageTests
{
    [Fact]
    public void EstimateCountsTextualEvidenceOnly()
    {
        var baseline = """
            {"system":"Policy","messages":[{"role":"user","content":[{"type":"text","text":"Hello"}]}]}
            """;
        var count = AnthropicInputUsageEstimator.Estimate(Encoding.UTF8.GetBytes(baseline));
        Assert.True(count > 0);

        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(baseline)!;
        var mutable = JsonSerializer.SerializeToNode(body)!.AsObject();
        mutable["metadata"] = new System.Text.Json.Nodes.JsonObject
        {
            ["user_id"] = string.Concat(Enumerable.Repeat("secret", 1000)),
        };
        mutable["max_tokens"] = 100000;
        var messages = mutable["messages"]!.AsArray();
        var message = messages[0]!.AsObject();
        message["content"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "image",
            ["source"] = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "base64",
                ["data"] = string.Concat(Enumerable.Repeat("aaaa", 10000)),
            },
        });

        var withIgnoredFields = AnthropicInputUsageEstimator.Estimate(
            Encoding.UTF8.GetBytes(mutable.ToJsonString()));
        Assert.Equal(count, withIgnoredFields);

        mutable["tools"] = new System.Text.Json.Nodes.JsonArray
        {
            new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "Read",
                ["description"] = string.Concat(Enumerable.Repeat("Read a file. ", 20)),
                ["input_schema"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "object" },
            },
        };
        var larger = AnthropicInputUsageEstimator.Estimate(
            Encoding.UTF8.GetBytes(mutable.ToJsonString()));
        Assert.True(larger > count);

        var nested = AnthropicInputUsageEstimator.Estimate(
            Encoding.UTF8.GetBytes(
                """{"messages":[{"role":"user","content":[{"type":"tool_result","content":[{"type":"text","text":"a detailed tool result with several words"}]}]}]}"""));
        Assert.True(nested > count);

        Assert.ThrowsAny<JsonException>(
            () => AnthropicInputUsageEstimator.Estimate(Encoding.UTF8.GetBytes("{")));
    }

    [Fact]
    public void EstimateIsStableUnderConcurrency()
    {
        var body = JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = string.Concat(
                        Enumerable.Repeat("Concurrent counting: Debian 日本語 1234567. ", 200)),
                },
            },
        });
        var bytes = Encoding.UTF8.GetBytes(body);
        var want = AnthropicInputUsageEstimator.Estimate(bytes);
        Assert.True(want > 0);

        Parallel.For(0, 16 * 5, _ =>
        {
            Assert.Equal(want, AnthropicInputUsageEstimator.Estimate(bytes));
        });
    }
}
