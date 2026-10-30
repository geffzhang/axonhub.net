using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/openai/aggregator_test.go, starting with the case
/// that locks the zero-response wire shape.
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
}
