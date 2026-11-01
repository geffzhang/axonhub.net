using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Bailian;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Bailian stream filter (stream_filter_test.go): text
/// before the first tool call streams through, text after it is buffered until
/// the finish chunk, and repeated empty <c>{}</c> arguments are dropped.
/// </summary>
public sealed class BailianStreamFilterTests
{
    private static readonly string[] HelloWorld = ["Hello ", "world"];

    // Reference TestBailianStreamFilter_DropsTextAfterToolCalls.
    [Fact]
    public void DropsTextAfterToolCalls()
    {
        var responses = new List<LlmResponse>
        {
            Chunk("resp_1", 0, TextDelta("hello ")),
            Chunk("resp_1", 0, TextDelta("world ")),
            Chunk("resp_1", 0, TextDelta("this is ")),
            Chunk("resp_1", 0, ToolDelta(Call(0, """{"path":"/"}""", "list_dir"))),
            Chunk("resp_1", 0, TextDelta("after tool calls")),
            Chunk("resp_1", 0, finishReason: "tool_calls"),
        };

        var output = Collect(responses);

        Assert.Equal(7, output.Count);

        var textChunks = TextChunks(output);
        Assert.Equal(4, textChunks.Count);
        Assert.Equal("hello ", textChunks[0]);
        Assert.Equal("world ", textChunks[1]);
        Assert.Equal("this is ", textChunks[2]);
        Assert.Equal("after tool calls", textChunks[3]);

        // The buffered text is emitted in its own chunk right before the
        // finish chunk.
        Assert.Equal("tool_calls", output[^1].Choices[0].FinishReason);
        Assert.Equal("after tool calls", output[^2].Choices[0].Delta!.Content!.Content);
        Assert.Null(output[^3].Choices[0].Delta!.Content!.Content);
    }

    // Reference TestBailianStreamFilter_IgnoresRedundantEmptyToolArgs.
    [Fact]
    public void IgnoresRedundantEmptyToolArgs()
    {
        var responses = new List<LlmResponse>
        {
            Chunk("resp_2", 0, ToolDelta(Call(0, """{"loc":"SF"}""", "get_weather"))),
            Chunk("resp_2", 0, ToolDelta(Call(0, "{}", "get_weather"))),
        };

        var output = Collect(responses);

        Assert.Equal(2, output.Count);
        Assert.Empty(output[1].Choices[0].Delta!.ToolCalls![0].Function.Arguments);
    }

    // Reference TestBailianStreamFilter_PassesThroughTextWhenNoToolCalls.
    [Fact]
    public void PassesThroughTextWhenNoToolCalls()
    {
        var responses = new List<LlmResponse>
        {
            Chunk("resp_3", 0, TextDelta("Hello ")),
            Chunk("resp_3", 0, TextDelta("world")),
            Chunk("resp_3", 0, finishReason: "stop"),
        };

        var output = Collect(responses);

        Assert.Equal(3, output.Count);
        Assert.Equal(HelloWorld, TextChunks(output));
    }

    [Fact]
    public void BuffersTextFromEveryChoiceAndKeepsNonTextParts()
    {
        var imagePart = new MessageContentPart
        {
            Type = "image_url",
            ImageUrl = new ImageUrl { Url = "https://img/part.png" },
        };
        var emptyTextPart = new MessageContentPart { Type = "text", Text = string.Empty };

        var responses = new List<LlmResponse>
        {
            Chunk("resp_4", 0, ToolDelta(Call(0, """{"a":1}"""))),
            Chunk("resp_4", 0, new Message
            {
                Content = MessageContent.FromParts(
                [
                    new MessageContentPart { Type = "text", Text = "a" },
                    imagePart,
                    emptyTextPart,
                    new MessageContentPart { Type = "text", Text = "b" },
                ]),
            }),
            Chunk("resp_4", 0, finishReason: "tool_calls"),
        };

        var output = Collect(responses);

        // The text-bearing chunk passes through with the text removed and the
        // non-text parts kept; the buffered text is released at the finish.
        var stripped = output[1].Choices[0].Delta!.Content;
        Assert.Null(stripped!.Content);
        Assert.Equal(2, stripped.MultipleContent!.Count);
        Assert.Same(imagePart, stripped.MultipleContent[0]);
        Assert.Same(emptyTextPart, stripped.MultipleContent[1]);

        Assert.Equal("ab", output[^2].Choices[0].Delta!.Content!.Content);
    }

    [Fact]
    public void FlushesWithTheLastTextChoiceIndexAcrossChoices()
    {
        var responses = new List<LlmResponse>
        {
            Chunk("resp_5", 0, ToolDelta(Call(0, """{"a":1}"""))),
            Chunk("resp_5", 0, TextDelta("left")),
            Chunk("resp_5", 1, TextDelta("right"), finishReason: "tool_calls"),
        };

        var output = Collect(responses);

        Assert.Equal(4, output.Count);
        Assert.Equal(1, output[^2].Choices[0].Index);
        Assert.Equal("leftright", output[^2].Choices[0].Delta!.Content!.Content);
    }

    [Fact]
    public void TracksEmptyToolArgumentsPerChoiceAndCallIndex()
    {
        var responses = new List<LlmResponse>
        {
            Chunk("resp_6", 0, ToolDelta(Call(0, """{"a":1}"""))),
            Chunk(
                "resp_6",
                0,
                ToolDelta(Call(0, "{}"), Call(1, " {} "))),
        };

        var output = Collect(responses);

        var toolCalls = output[1].Choices[0].Delta!.ToolCalls!;
        Assert.Empty(toolCalls[0].Function.Arguments);
        Assert.Equal(" {} ", toolCalls[1].Function.Arguments);
    }

    [Fact]
    public void PassesTheDoneResponseThrough()
    {
        var responses = new List<LlmResponse>
        {
            Chunk("resp_7", 0, ToolDelta(Call(0, """{"a":1}"""))),
            LlmResponse.DoneResponse,
        };

        var output = Collect(responses);

        Assert.Equal(2, output.Count);
        Assert.Same(LlmResponse.DoneResponse, output[1]);
    }

    [Fact]
    public void SkipsNilSourceItems()
    {
        var text = Chunk("resp_8", 0, TextDelta("hi"));
        var responses = new List<LlmResponse> { null!, text };

        var output = Collect(responses);

        Assert.Single(output);
        Assert.Same(text, output[0]);
    }

    [Fact]
    public void PassesUntouchedResponsesThroughByReference()
    {
        var response = Chunk("resp_9", 0, TextDelta("hi"));
        response.Usage = new Usage { TotalTokens = 3 };

        var output = Collect([response]);

        Assert.Same(response, Assert.Single(output));
        Assert.Equal(3, output[0].Usage!.TotalTokens);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static List<LlmResponse> Collect(IEnumerable<LlmResponse> responses)
    {
        using var stream = new BailianStreamFilter(new SliceStream<LlmResponse>(responses));
        var output = GoStreams.All(stream, out var error);
        Assert.Null(error);
        return output;
    }

    private static List<string> TextChunks(IEnumerable<LlmResponse> responses)
    {
        var texts = new List<string>();
        foreach (var response in responses)
        {
            foreach (var choice in response.Choices)
            {
                if (choice.Delta?.Content?.Content is { Length: > 0 } text)
                {
                    texts.Add(text);
                }
            }
        }

        return texts;
    }

    private static LlmResponse Chunk(
        string id,
        int choiceIndex,
        Message? delta = null,
        string? finishReason = null)
        => new()
        {
            Id = id,
            Object = "chat.completion.chunk",
            Choices =
            [
                new Choice
                {
                    Index = choiceIndex,
                    Delta = delta,
                    FinishReason = finishReason,
                },
            ],
        };

    private static Message TextDelta(string text)
        => new() { Content = MessageContent.FromText(text) };

    private static Message ToolDelta(params ToolCall[] toolCalls)
        => new() { ToolCalls = [.. toolCalls] };

    private static ToolCall Call(int index, string arguments, string name = "tool")
        => new()
        {
            Index = index,
            Type = "function",
            Function = new FunctionCall { Name = name, Arguments = arguments },
        };
}
