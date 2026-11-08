using AxonHub.Llm.Json;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Locks difference-ledger C067: every <c>double</c> serialized through
/// <see cref="LlmJson.Options"/> uses Go <c>encoding/json</c>'s formatting
/// (fixed notation for 1e-6 ≤ |v| &lt; 1e21, trimmed negative exponents),
/// including nullable fields, dictionary values and embedding vectors.
/// </summary>
public sealed class GoDoubleConverterTests
{
    [Theory]
    [InlineData(0.000005, "0.000005")]
    [InlineData(0.0000001, "1e-7")]
    [InlineData(1.5e-7, "1.5e-7")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(1e21, "1e+21")]
    [InlineData(0.7, "0.7")]
    public void RequestScalarsUseGoFloatFormatting(double value, string expected)
    {
        var json = GoJson.MarshalToString(new LlmRequest
        {
            Model = "gpt-5",
            Temperature = value,
            TopP = value,
            FrequencyPenalty = value,
            PresencePenalty = value,
        });

        Assert.Contains("\"temperature\":" + expected, json, StringComparison.Ordinal);
        Assert.Contains("\"top_p\":" + expected, json, StringComparison.Ordinal);
        Assert.Contains("\"frequency_penalty\":" + expected, json, StringComparison.Ordinal);
        Assert.Contains("\"presence_penalty\":" + expected, json, StringComparison.Ordinal);
    }

    [Fact]
    public void NullableDoublesStayOmitted()
    {
        var json = GoJson.MarshalToString(new LlmRequest { Model = "gpt-5" });

        Assert.DoesNotContain("\"temperature\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"top_p\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.5e-7, "1.5e-7")]
    [InlineData(-2.5e-7, "-2.5e-7")]
    [InlineData(1e-7, "1e-7")]
    public void LogprobFieldsUseGoFloatFormatting(double value, string expected)
    {
        var json = GoJson.MarshalToString(new LlmResponse
        {
            Id = "r1",
            Model = "gpt-5",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Logprobs = new LogprobsContent
                    {
                        Content =
                        [
                            new TokenLogprob
                            {
                                Token = "x",
                                Logprob = value,
                                TopLogprobs = [new TopLogprob { Token = "y", Logprob = value }],
                            },
                        ],
                    },
                },
            ],
        });

        Assert.Contains("\"logprob\":" + expected, json, StringComparison.Ordinal);
        Assert.Contains(
            "\"top_logprobs\":[{\"token\":\"y\",\"logprob\":" + expected + "}]",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddingVectorElementsUseGoFloatFormatting()
    {
        var json = GoJson.MarshalToString(new EmbeddingResponse
        {
            Object = "list",
            Data =
            [
                new EmbeddingData
                {
                    Object = "embedding",
                    Index = 0,
                    Embedding = new Embedding { Values = [5e-7, 1e-7, 0.1, 1e21] },
                },
            ],
        });

        Assert.Contains("\"embedding\":[5e-7,1e-7,0.1,1e+21]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleMapValuesUseGoFloatFormatting()
    {
        var json = GoJson.MarshalToString(new ModerationClassification
        {
            CategoryScores = new Dictionary<string, double>
            {
                ["hate"] = 5e-7,
                ["violence"] = 1e21,
            },
        });

        Assert.Contains(
            "\"category_scores\":{\"hate\":5e-7,\"violence\":1e+21}",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsBothGoAndSystemTextJsonExponentSpellings()
    {
        var request = LlmJson.Deserialize<LlmRequest>(
            "{\"model\":\"gpt-5\",\"temperature\":1E-7,\"top_p\":1e-7}");

        Assert.NotNull(request);
        Assert.Equal(1e-7, request.Temperature);
        Assert.Equal(1e-7, request.TopP);
        Assert.Contains(
            "\"temperature\":1e-7",
            GoJson.MarshalToString(request),
            StringComparison.Ordinal);
    }
}
