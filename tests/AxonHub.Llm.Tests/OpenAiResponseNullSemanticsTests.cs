using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of Go's zero-value rules for JSON nulls (difference ledger C022):
/// encoding/json turns a null slice element into the zero struct, while
/// System.Text.Json keeps null. The conversions must substitute the zero value, and
/// index fields keep Go's 64-bit <c>int</c> range.
/// </summary>
public sealed class OpenAiResponseNullSemanticsTests
{
    private static readonly OpenAiOutboundTransformer Transformer =
        OpenAiOutboundTransformer.Create("https://upstream.test", "sk-test");

    [Fact]
    public void NullChoicesDecodeAsZeroChoices()
    {
        var response = TransformResponse(
            "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"m\","
            + "\"choices\":[null,"
            + "{\"index\":3000000000,\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},"
            + "\"finish_reason\":\"stop\"}]}");

        Assert.Equal(2, response.Choices.Count);

        var zero = response.Choices[0];
        Assert.Equal(0L, zero.Index);
        Assert.Null(zero.Message);
        Assert.Null(zero.Delta);
        Assert.Null(zero.FinishReason);
        Assert.Null(zero.Logprobs);

        Assert.Equal(3000000000L, response.Choices[1].Index);
        Assert.Equal("hi", response.Choices[1].Message!.Content!.Content);
    }

    [Fact]
    public void NullContentPartsDecodeAsZeroParts()
    {
        var response = TransformResponse(
            "{\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\","
            + "\"content\":[null,{\"type\":\"text\",\"text\":\"hi\"}]}}]}");

        var parts = response.Choices[0].Message!.Content!.MultipleContent!;

        Assert.Equal(2, parts.Count);
        Assert.Equal(string.Empty, parts[0].Type);
        Assert.Null(parts[0].Text);
        Assert.Equal("text", parts[1].Type);
        Assert.Equal("hi", parts[1].Text);
    }

    [Fact]
    public void NullLogprobsDecodeAsZeroValues()
    {
        var response = TransformResponse(
            "{\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,"
            + "\"logprobs\":{\"content\":[null,"
            + "{\"token\":\"a\",\"logprob\":-0.5,\"top_logprobs\":[null]}]}}]}");

        var tokens = response.Choices[0].Logprobs!.Content;

        Assert.Equal(2, tokens.Count);
        Assert.Equal(string.Empty, tokens[0].Token);
        Assert.Equal(0d, tokens[0].Logprob);
        Assert.Null(tokens[0].Bytes);
        Assert.Null(tokens[0].TopLogprobs);

        Assert.Equal("a", tokens[1].Token);
        Assert.Equal(-0.5, tokens[1].Logprob);
        var top = Assert.Single(tokens[1].TopLogprobs!);
        Assert.Equal(string.Empty, top.Token);
        Assert.Equal(0d, top.Logprob);
        Assert.Null(top.Bytes);
    }

    [Fact]
    public void NullEmbeddingDataDecodesAsZeroData()
    {
        var response = Transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes("{\"object\":\"list\",\"data\":[null],\"model\":\"m\"}"),
            Request = new HttpRequestData { ApiFormat = ApiFormats.OpenAIEmbedding },
        });

        var data = Assert.Single(response.Embedding!.Data!);
        Assert.Equal(0L, data.Index);
        Assert.Equal(string.Empty, data.Object);
        Assert.Null(data.Embedding.Values);
        Assert.Equal(string.Empty, data.Embedding.Base64);
    }

    [Fact]
    public void NullChoiceChunksAggregateWithoutDroppingTheRealDelta()
    {
        var chunks = new List<StreamEvent>
        {
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                    + "\"model\":\"m\",\"choices\":[{\"index\":0,"
                    + "\"delta\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}"),
            },
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                    + "\"model\":\"m\",\"choices\":[null]}"),
            },
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                    + "\"model\":\"m\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\"}]}"),
            },
        };

        var body = OpenAiAggregator.AggregateStreamChunks(
            chunks,
            OpenAiAggregator.DefaultTransformChunk,
            out _);
        var aggregated = LlmJson.Deserialize<OpenAiResponse>(body)!;

        var choice = Assert.Single(aggregated.Choices!);
        Assert.Equal("hi", choice.Message!.Content!.Content);
        Assert.Equal("stop", choice.FinishReason);
    }

    private static LlmResponse TransformResponse(string body)
        => Transformer.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(body),
        });
}
