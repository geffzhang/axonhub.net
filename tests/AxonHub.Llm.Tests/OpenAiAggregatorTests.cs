using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/openai/aggregator_test.go, starting with the cases
/// that lock the zero-response wire shape and the usage provenance the
/// orchestrator's accounting gate consumes.
/// </summary>
public sealed class OpenAiAggregatorTests
{
    // Port of TestAggregateStreamChunks_EmptyChunks. The reference asserts that
    // the bytes unmarshal into llm.Response{}; the port asserts the exact bytes
    // instead, which additionally locks field order and "choices":null.
    [Fact]
    public void AggregateStreamChunks_EmptyChunks()
    {
        var body = OpenAiAggregator.AggregateStreamChunks(
            Array.Empty<StreamEvent>(),
            OpenAiAggregator.DefaultTransformChunk,
            out _);

        Assert.Equal(
            """{"id":"","choices":null,"object":"","created":0,"model":""}""",
            Encoding.UTF8.GetString(body));
    }

    public static TheoryData<string, string[], AccountingEvidence, bool> AccountingProvenanceCases => new()
    {
        {
            "choices-less final usage is explicit including zero",
            [
                """{"id":"chat","model":"m","choices":[{"index":0,"finish_reason":"stop","delta":{}}]}""",
                """{"id":"chat","model":"m","choices":[],"usage":{"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}}""",
            ],
            AccountingEvidence.ExplicitFinal,
            true
        },
        {
            "finish usage is a clean end candidate",
            [
                """{"id":"chat","model":"m","choices":[{"index":0,"finish_reason":"stop","delta":{}}],"usage":{"prompt_tokens":12,"completion_tokens":34,"total_tokens":46}}""",
            ],
            AccountingEvidence.CleanEndCandidate,
            true
        },
        {
            "repeated zero finish placeholders are unavailable",
            [
                """{"id":"chat","model":"m","choices":[{"index":0,"finish_reason":"stop","delta":{}}],"usage":{"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}}""",
                """{"id":"chat","model":"m","choices":[{"index":0,"finish_reason":"stop","delta":{}}],"usage":{"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}}""",
            ],
            AccountingEvidence.Unavailable,
            true
        },
    };

    [Theory]
    [MemberData(nameof(AccountingProvenanceCases))]
    public void AggregateStreamChunks_AccountingProvenance(
        string name,
        string[] chunkJsons,
        AccountingEvidence want,
        bool wantUsage)
    {
        _ = name;
        _ = OpenAiAggregator.AggregateStreamChunks(
            chunkJsons.Select(Chunk).ToList(),
            OpenAiAggregator.DefaultTransformChunk,
            out var meta);

        Assert.Equal(want, meta.AccountingEvidence);
        Assert.Equal(wantUsage, meta.Usage is not null);
    }

    // Port of TestAggregateStreamChunks_CleanEOFUsageWithoutFinishReason.
    [Fact]
    public void AggregateStreamChunks_CleanEOFUsageWithoutFinishReason()
    {
        var chunks = new[]
        {
            Chunk("""{"id":"chat","model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"}}]}"""),
            Chunk("""{"id":"chat","model":"m","choices":[],"usage":{"prompt_tokens":12,"completion_tokens":34,"total_tokens":46}}"""),
        };

        _ = OpenAiAggregator.AggregateStreamChunks(
            chunks, OpenAiAggregator.DefaultTransformChunk, out var meta);

        Assert.Equal(AccountingEvidence.CleanEndCandidate, meta.AccountingEvidence);
        Assert.Equal(46, meta.Usage!.TotalTokens);
    }

    private static StreamEvent Chunk(string json) => new()
    {
        Data = Encoding.UTF8.GetBytes(json),
    };
}
