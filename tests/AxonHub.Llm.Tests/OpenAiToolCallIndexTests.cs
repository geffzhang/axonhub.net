using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of openai/tool_call_index.go coverage: Gemini's OpenAI-compatible
/// endpoint sends each parallel tool call whole without an index, so every call
/// decoded as index 0 and downstream merged them into one.
/// </summary>
public sealed class OpenAiToolCallIndexTests
{
    [Fact]
    public void StreamAssignsIndexesToUnindexedToolCalls()
    {
        var results = Run(
            Chunk("""{"id":"c","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"id":"call-1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Paris\"}"}}]}}]}"""),
            Chunk("""{"id":"c","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"id":"call-2","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Tokyo\"}"}}]}}]}"""));

        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0].Choices[0].Delta!.ToolCalls![0].Index);
        Assert.Equal(1, results[1].Choices[0].Delta!.ToolCalls![0].Index);
    }

    [Fact]
    public void StreamContinuesABareToolCallFragment()
    {
        var results = Run(
            Chunk("""{"id":"c","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"id":"call-1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":"}}]}}]}"""),
            Chunk("""{"id":"c","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"function":{"arguments":"\"Paris\"}"}}]}}]}"""));

        Assert.Equal(0, results[0].Choices[0].Delta!.ToolCalls![0].Index);
        Assert.Equal(0, results[1].Choices[0].Delta!.ToolCalls![0].Index);
    }

    [Fact]
    public void StreamKeepsExplicitIndexesAndAdvancesTheNextUnindexedCall()
    {
        var results = Run(
            Chunk("""{"id":"c","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":5,"id":"call-5","type":"function","function":{"name":"a","arguments":"{}"}}]}}]}"""),
            Chunk("""{"id":"c","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"id":"call-6","type":"function","function":{"name":"b","arguments":"{}"}}]}}]}"""));

        Assert.Equal(5, results[0].Choices[0].Delta!.ToolCalls![0].Index);
        Assert.Equal(6, results[1].Choices[0].Delta!.ToolCalls![0].Index);
    }

    [Fact]
    public void AggregatorMergesUnindexedCallsByTheirAssignedIndex()
    {
        var chunks = new[]
        {
            Chunk("""{"id":"c","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"id":"call-1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Paris\"}"}}]}}]}"""),
            Chunk("""{"id":"c","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"id":"call-2","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Tokyo\"}"}}]}}]}"""),
            Chunk("""{"id":"c","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""),
        };

        var body = OpenAiAggregator.AggregateStreamChunks(
            chunks,
            static chunk => LlmJson.Deserialize<OpenAiResponse>(chunk.Data)!,
            out _);
        var response = LlmJson.Deserialize<OpenAiResponse>(body)!;
        var toolCalls = response.Choices![0].Message!.ToolCalls!;

        Assert.Equal(2, toolCalls.Count);
        Assert.Equal(0, toolCalls[0].Index);
        Assert.Equal(1, toolCalls[1].Index);
        Assert.Equal("call-1", toolCalls[0].Id);
        Assert.Equal("call-2", toolCalls[1].Id);
        Assert.Equal("""{"city":"Paris"}""", toolCalls[0].Function.Arguments);
        Assert.Equal("""{"city":"Tokyo"}""", toolCalls[1].Function.Arguments);
    }

    private static List<LlmResponse> Run(params StreamEvent[] chunks)
    {
        var transformer = OpenAiOutboundTransformer.Create(
            "https://api.openai.com/v1",
            "test-key");
        var output = transformer.TransformStream(null, new SliceStream<StreamEvent>(chunks));
        var results = GoStreams.All(output, out var error);
        Assert.Null(error);
        return results
            .Where(static response => response.Object != "[DONE]")
            .ToList();
    }

    private static StreamEvent Chunk(string json) => new()
    {
        Data = Encoding.UTF8.GetBytes(json),
    };
}
