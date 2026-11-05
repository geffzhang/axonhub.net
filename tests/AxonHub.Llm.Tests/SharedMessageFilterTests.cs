using AxonHub.Llm;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the shared response-custom-tool message filter
/// (llm/transformer/shared/messages.go): the orchestrator removes Responses
/// custom-tool calls before a non-Responses outbound renders the conversation.
/// </summary>
public sealed class SharedMessageFilterTests
{
    [Fact]
    public void EmptyInputReturnsAnEmptyList()
    {
        Assert.Empty(SharedMessageFilter.FilterOutResponseCustomToolMessages([]));
    }

    [Fact]
    public void RemovesCustomToolCallsAndTheirResultMessages()
    {
        var messages = new List<Message>
        {
            new()
            {
                Role = "assistant",
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_custom_1",
                        Type = ToolTypes.ResponsesCustomTool,
                        ResponseCustomToolCall = new ResponseCustomToolCall
                        {
                            CallId = "call_custom_1",
                            Name = "apply_patch",
                            Input = "*** Begin Patch\n*** End Patch\n",
                        },
                    },
                    new ToolCall
                    {
                        Id = "call_function_1",
                        Type = ToolTypes.Function,
                        Function = new FunctionCall { Name = "get_weather", Arguments = "{}" },
                    },
                ],
            },
            new() { Role = "tool", ToolCallId = "call_custom_1", Content = MessageContent.FromText("custom") },
            new() { Role = "tool", ToolCallId = "call_function_1", Content = MessageContent.FromText("function") },
        };

        var filtered = SharedMessageFilter.FilterOutResponseCustomToolMessages(messages);

        Assert.Equal(2, filtered.Count);
        var assistant = Assert.Single(filtered[0].ToolCalls!);
        Assert.Equal(ToolTypes.Function, assistant.Type);
        Assert.Equal("call_function_1", filtered[1].ToolCallId);
        Assert.Equal("function", filtered[1].Content!.Content);

        // Go copies the message before rewriting its tool calls: the caller's
        // slice keeps both calls.
        Assert.Equal(2, messages[0].ToolCalls!.Count);
    }

    [Fact]
    public void RemovesToolResultsByResponseCustomToolCallIdWhenTheCallIdIsEmpty()
    {
        var messages = new List<Message>
        {
            new()
            {
                Role = "assistant",
                ToolCalls =
                [
                    new ToolCall
                    {
                        Type = ToolTypes.ResponsesCustomTool,
                        ResponseCustomToolCall = new ResponseCustomToolCall { CallId = "call_x" },
                    },
                ],
            },
            new() { Role = "tool", ToolCallId = "call_x", Content = MessageContent.FromText("x") },
        };

        var filtered = SharedMessageFilter.FilterOutResponseCustomToolMessages(messages);

        // The assistant message carried nothing but the custom call and is
        // dropped too (shouldDropMessageAfterToolFiltering).
        Assert.Empty(filtered);
    }

    [Fact]
    public void DropsAMessageWhoseOnlyToolCallsWereCustomTools()
    {
        var messages = new List<Message>
        {
            new()
            {
                Role = "assistant",
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_custom_1",
                        Type = ToolTypes.ResponsesCustomTool,
                        ResponseCustomToolCall = new ResponseCustomToolCall { CallId = "call_custom_1" },
                    },
                ],
            },
        };

        Assert.Empty(SharedMessageFilter.FilterOutResponseCustomToolMessages(messages));
    }

    [Fact]
    public void KeepsAMessageWithContentAfterItsToolCallsAreRemoved()
    {
        var messages = new List<Message>
        {
            new()
            {
                Role = "assistant",
                Content = MessageContent.FromText("visible"),
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_custom_1",
                        Type = ToolTypes.ResponsesCustomTool,
                        ResponseCustomToolCall = new ResponseCustomToolCall { CallId = "call_custom_1" },
                    },
                ],
            },
        };

        var filtered = SharedMessageFilter.FilterOutResponseCustomToolMessages(messages);

        var message = Assert.Single(filtered);
        Assert.Equal("visible", message.Content!.Content);
        Assert.NotNull(message.ToolCalls);
        Assert.Empty(message.ToolCalls!);
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("refusal")]
    [InlineData("audio")]
    public void KeepsMessagesWithNonContentPayloads(string payload)
    {
        var message = new Message
        {
            Role = "assistant",
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "call_custom_1",
                    Type = ToolTypes.ResponsesCustomTool,
                    ResponseCustomToolCall = new ResponseCustomToolCall { CallId = "call_custom_1" },
                },
            ],
        };

        switch (payload)
        {
            case "reasoning":
                message.ReasoningContent = "thinking";
                break;
            case "refusal":
                message.Refusal = "no";
                break;
            default:
                message.Audio = new OutputAudio();
                break;
        }

        var filtered = SharedMessageFilter.FilterOutResponseCustomToolMessages([message]);

        Assert.Single(filtered);
    }

    [Fact]
    public void DoesNotRemoveNonToolMessagesSharingARemovedCallId()
    {
        var messages = new List<Message>
        {
            new()
            {
                Role = "assistant",
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_custom_1",
                        Type = ToolTypes.ResponsesCustomTool,
                        ResponseCustomToolCall = new ResponseCustomToolCall { CallId = "call_custom_1" },
                    },
                ],
            },
            new() { Role = "user", ToolCallId = "call_custom_1", Content = MessageContent.FromText("kept") },
        };

        var filtered = SharedMessageFilter.FilterOutResponseCustomToolMessages(messages);

        var kept = Assert.Single(filtered);
        Assert.Equal("user", kept.Role);
    }
}
