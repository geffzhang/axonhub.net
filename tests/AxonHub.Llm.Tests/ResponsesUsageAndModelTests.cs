using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the Responses usage surface and the created_at
/// compatibility rules (llm/transformer/openai/responses/usage.go,
/// usage_json.go and model.go). Expectations mirror usage_test.go and
/// model_test.go's created_at table.
/// </summary>
public sealed class ResponsesUsageAndModelTests
{
    [Fact]
    public void ConvertsResponsesUsageToUnifiedUsage()
    {
        var usage = new ResponsesUsage
        {
            InputTokens = 100,
            OutputTokens = 20,
            TotalTokens = 120,
        };
        usage.InputTokensDetails.CacheWriteTokens = 80;
        usage.InputTokensDetails.CachedTokens = 10;
        usage.OutputTokensDetails.ReasoningTokens = 7;

        var unified = usage.ToUsage();

        Assert.Equal(100, unified.PromptTokens);
        Assert.Equal(20, unified.CompletionTokens);
        Assert.Equal(120, unified.TotalTokens);
        Assert.NotNull(unified.PromptTokensDetails);
        Assert.Equal(80, unified.PromptTokensDetails!.WriteCachedTokens);
        Assert.Equal(10, unified.PromptTokensDetails.CachedTokens);
        Assert.NotNull(unified.CompletionTokensDetails);
        Assert.Equal(7, unified.CompletionTokensDetails!.ReasoningTokens);

        // Go's Usage.ToUsage deliberately drops the cost.
        Assert.Null(unified.Cost);
    }

    [Fact]
    public void ConvertsUnifiedUsageToResponsesUsage()
    {
        var responsesUsage = ResponsesUsage.ConvertLlmUsageToResponsesUsage(new Usage
        {
            PromptTokens = 100,
            CompletionTokens = 20,
            TotalTokens = 120,
            PromptTokensDetails = new PromptTokensDetails
            {
                WriteCachedTokens = 80,
                CachedTokens = 10,
            },
        });

        Assert.NotNull(responsesUsage);
        Assert.Equal(80, responsesUsage!.InputTokensDetails.CacheWriteTokens);
        Assert.Equal(10, responsesUsage.InputTokensDetails.CachedTokens);

        // Field order and names match Go's encoding/json struct marshaling.
        Assert.Equal(
            "{\"input_tokens\":100,"
            + "\"input_tokens_details\":{\"cache_write_tokens\":80,\"cached_tokens\":10},"
            + "\"output_tokens\":20,"
            + "\"output_tokens_details\":{\"reasoning_tokens\":0},"
            + "\"total_tokens\":120}",
            GoJson.MarshalToString(responsesUsage));
    }

    [Fact]
    public void ConvertsNullUnifiedUsageToNull()
    {
        Assert.Null(ResponsesUsage.ConvertLlmUsageToResponsesUsage(null));
    }

    [Fact]
    public void ConvertsUnifiedUsageCarriesCost()
    {
        var responsesUsage = ResponsesUsage.ConvertLlmUsageToResponsesUsage(new Usage
        {
            PromptTokens = 100,
            CompletionTokens = 20,
            TotalTokens = 120,
            Cost = 0.000005,
        })!;

        Assert.NotNull(responsesUsage.Cost);
        Assert.Equal(0.000005, responsesUsage.Cost!.Value, 12);
        Assert.Contains("\"cost\":0.000005", GoJson.MarshalToString(responsesUsage), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.000005", 0.000005)]
    [InlineData("\"0.000005\"", 0.000005)]
    [InlineData("\"1e-6\"", 0.000001)]
    public void DecodesResponsesUsageCost(string costJson, double expected)
    {
        var usage = DeserializeUsageWithCost(costJson);

        Assert.NotNull(usage.Cost);
        Assert.Equal(expected, usage.Cost!.Value, 12);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("1e400")]
    [InlineData("\"\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\".5\"")]
    [InlineData("\"5.\"")]
    [InlineData("\" 1\"")]
    [InlineData("\"1e400\"")]
    [InlineData("\"0x10\"")]
    public void IgnoresCostThatIsNotAGoFloat(string costJson)
    {
        // xjson.ParseOptionalFloat64 decodes the raw value into json.Number
        // (rejecting any non-number literal, including quoted non-numbers) and
        // treats a strconv range error as "no cost".
        Assert.Null(DeserializeUsageWithCost(costJson).Cost);
    }

    [Theory]
    [InlineData(null, 0L)]
    [InlineData("null", 0L)]
    [InlineData("1786360449", 1786360449L)]
    [InlineData("1786360449.0", 1786360449L)]
    [InlineData("1786360449.000", 1786360449L)]
    [InlineData("0", 0L)]
    [InlineData("0.0", 0L)]
    [InlineData("0.000", 0L)]
    [InlineData("1.7e9", 1700000000L)]
    [InlineData("170000000000000000000000000000e-20", 1700000000L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void AcceptsCreatedAtCompatibilityForms(string? createdAtJson, long expected)
    {
        var response = DeserializeResponse(createdAtJson);

        Assert.Equal(expected, response.CreatedAt);
        Assert.Equal("resp_1", response.Id);
        Assert.Equal("gpt-5", response.Model);

        // Marshaling always emits the integer form, never 1786360449.0.
        var json = GoJson.MarshalToString(response);
        Assert.Contains(
            "\"created_at\":" + expected.ToString(CultureInfo.InvariantCulture),
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".0", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1786360449.5", "integer number of seconds")]
    [InlineData("\"1786360449\"", "must be a JSON number")]
    [InlineData("\"1786360449.0\"", "must be a JSON number")]
    [InlineData("1e1000000", "out of int64 range")]
    [InlineData("1e-9223372036854775808", "integer number of seconds")]
    [InlineData("1e9223372036854775807", "out of int64 range")]
    [InlineData("9223372036854775808", "created_at")]
    [InlineData("-9223372036854775809", "created_at")]
    public void RejectsCreatedAtThatIsNotAnIntegerNumberOfSeconds(string createdAtJson, string expectedFragment)
    {
        var ex = Assert.Throws<JsonException>(() =>
        {
            DeserializeResponse(createdAtJson);
        });

        Assert.Contains("created_at", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.000005, "0.000005")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(0.0000001, "1e-7")]
    [InlineData(1.5e-7, "1.5e-7")]
    [InlineData(-2.5e-7, "-2.5e-7")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(1e21, "1e+21")]
    [InlineData(100.0, "100")]
    [InlineData(0.1, "0.1")]
    [InlineData(1.0, "1")]
    public void FormatsFloatsLikeGoEncodingJson(double value, string expected)
    {
        // Go's encoding/json uses fixed notation for 1e-6 <= |v| < 1e21 and
        // trims the leading zero of single-digit negative exponents.
        Assert.Equal(expected, GoJsonFloat.Format(value));
    }

    private static ResponsesUsage DeserializeUsageWithCost(string costJson)
    {
        var payload = "{\"input_tokens\":1,\"output_tokens\":2,\"total_tokens\":3,\"cost\":" + costJson + "}";
        var usage = LlmJson.Deserialize<ResponsesUsage>(payload);
        return Assert.IsType<ResponsesUsage>(usage);
    }

    private static ResponsesResponse DeserializeResponse(string? createdAtJson)
    {
        var payload = createdAtJson is null
            ? "{\"id\":\"resp_1\",\"model\":\"gpt-5\",\"output\":[]}"
            : "{\"id\":\"resp_1\",\"created_at\":" + createdAtJson + ",\"model\":\"gpt-5\",\"output\":[]}";

        var response = LlmJson.Deserialize<ResponsesResponse>(Encoding.UTF8.GetBytes(payload));
        return Assert.IsType<ResponsesResponse>(response);
    }
}
