using System.Text;
using AxonHub.Llm.Transformer.Anthropic;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Anthropic cache_control breakpoint optimizer
/// (llm/transformer/anthropic/ensure_cache_control.go and its tests).
/// </summary>
public sealed class AnthropicCacheControlOptimizerTests
{
    // ------------------------------------------------------------------
    // Counting
    // ------------------------------------------------------------------

    [Fact]
    public void CountCacheControls_CountsToolsSystemAndMessages()
    {
        var empty = Request(Message("user", "hello"));
        Assert.Equal(0, AnthropicCacheControlOptimizer.CountCacheControls(empty));

        var tools = Request(Message("user", "hello"));
        tools.Tools = [Tool("a"), Tool("b", Ephemeral())];
        Assert.Equal(1, AnthropicCacheControlOptimizer.CountCacheControls(tools));

        var system = Request(Message("user", "hello"));
        system.System = new AnthropicSystemPrompt
        {
            MultiplePrompts =
            [
                new AnthropicSystemPromptPart { Type = "text", Text = "a", CacheControl = Ephemeral() },
                new AnthropicSystemPromptPart { Type = "text", Text = "b", CacheControl = Ephemeral() },
            ],
        };
        Assert.Equal(2, AnthropicCacheControlOptimizer.CountCacheControls(system));

        var mixed = Request(
            Blocks("user", new AnthropicMessageContentBlock
            {
                Type = "text",
                Text = "msg",
                CacheControl = Ephemeral(),
            }),
            Blocks("user", new AnthropicMessageContentBlock
            {
                Type = "tool_result",
                ToolUseId = "id1",
                CacheControl = Ephemeral(),
            }));
        mixed.Tools = [Tool("t1", Ephemeral())];
        mixed.System = new AnthropicSystemPrompt
        {
            MultiplePrompts =
            [
                new AnthropicSystemPromptPart { Type = "text", Text = "sys", CacheControl = Ephemeral() },
            ],
        };

        Assert.Equal(4, AnthropicCacheControlOptimizer.CountCacheControls(mixed));
    }

    // ------------------------------------------------------------------
    // Zero breakpoints
    // ------------------------------------------------------------------

    [Fact]
    public void ZeroBreakpoints_InjectsStructuralAnchorsAndTailAnchor()
    {
        var request = Request(
            Message("user", "first user msg"),
            Message("assistant", "response"),
            Message("user", "second user msg"));
        request.Tools = [Tool("tool_a"), Tool("tool_b")];
        request.System = new AnthropicSystemPrompt
        {
            MultiplePrompts =
            [
                new AnthropicSystemPromptPart { Type = "text", Text = "prompt_1" },
                new AnthropicSystemPromptPart { Type = "text", Text = "prompt_2" },
            ],
        };

        AnthropicCacheControlOptimizer.Optimize(request);

        var tools = request.Tools!;
        Assert.Null(tools[0].CacheControl);
        Assert.NotNull(tools[1].CacheControl);
        Assert.Equal("ephemeral", tools[1].CacheControl!.Type);

        var systemParts = request.System!.MultiplePrompts!;
        Assert.Null(systemParts[0].CacheControl);
        Assert.NotNull(systemParts[1].CacheControl);
        Assert.Equal("ephemeral", systemParts[1].CacheControl!.Type);

        var messages = request.Messages!;
        Assert.NotNull(messages[2].Content!.MultipleContent);
        Assert.Single(messages[2].Content!.MultipleContent!);
        Assert.NotNull(messages[2].Content!.MultipleContent![0].CacheControl);
        Assert.Equal("ephemeral", messages[2].Content!.MultipleContent![0].CacheControl!.Type);

        Assert.Equal(3, AnthropicCacheControlOptimizer.CountCacheControls(request));
    }

    [Fact]
    public void ZeroBreakpoints_WithoutToolsAndSystem_OnlyInjectsMessageAnchor()
    {
        var request = Request(
            Message("user", "first"),
            Message("assistant", "resp"),
            Message("user", "second"));

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Equal(1, AnthropicCacheControlOptimizer.CountCacheControls(request));

        var messages = request.Messages!;
        Assert.NotNull(messages[2].Content!.MultipleContent![0].CacheControl);
    }

    [Fact]
    public void ZeroBreakpoints_SingleUserTurnWithTool_InjectsToolAndMessageAnchors()
    {
        var request = Request(Message("user", "only one"));
        request.Tools = [Tool("t1")];

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Equal(2, AnthropicCacheControlOptimizer.CountCacheControls(request));

        var tools = request.Tools!;
        Assert.NotNull(tools[0].CacheControl);
    }

    [Fact]
    public void ZeroBreakpoints_ArrayContent_InjectsOnLastMessage()
    {
        var request = Request(
            Blocks("user", Text("part1"), Text("part2")),
            Message("assistant", "resp"),
            Message("user", "latest"));

        AnthropicCacheControlOptimizer.Optimize(request);

        var messages = request.Messages!;
        var blocks = messages[2].Content!.MultipleContent!;
        Assert.Single(blocks);
        Assert.NotNull(blocks[0].CacheControl);
        Assert.Equal("ephemeral", blocks[0].CacheControl!.Type);
    }

    // ------------------------------------------------------------------
    // Existing breakpoints
    // ------------------------------------------------------------------

    [Fact]
    public void WithinLimit_FillsStrategicAnchors()
    {
        var request = Request(Message("user", "hello"));
        request.Tools = [Tool("t1", Ephemeral())];

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Equal(2, AnthropicCacheControlOptimizer.CountCacheControls(request));
    }

    [Fact]
    public void WithinLimit_KeepsFourClientBreakpointsInPlace()
    {
        var request = Request(Blocks(
            "user",
            new AnthropicMessageContentBlock { Type = "text", Text = "a", CacheControl = Ephemeral() },
            new AnthropicMessageContentBlock { Type = "text", Text = "b", CacheControl = Ephemeral() }));
        request.Tools = [Tool("t1", Ephemeral())];
        request.System = new AnthropicSystemPrompt
        {
            MultiplePrompts =
            [
                new AnthropicSystemPromptPart { Type = "text", Text = "s1", CacheControl = Ephemeral() },
            ],
        };

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Equal(4, AnthropicCacheControlOptimizer.CountCacheControls(request));

        var tools = request.Tools!;
        var systemParts = request.System!.MultiplePrompts!;
        var blocks = request.Messages![0].Content!.MultipleContent!;
        Assert.NotNull(tools[0].CacheControl);
        Assert.NotNull(systemParts[0].CacheControl);
        Assert.NotNull(blocks[0].CacheControl);
        Assert.NotNull(blocks[1].CacheControl);
    }

    [Fact]
    public void ExistingBreakpoints_WithoutToolsAndSystem_KeepSingleAnchor()
    {
        var request = Request(
            Message("user", "first"),
            Message("assistant", "resp"),
            Message("user", "second"));

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Equal(1, AnthropicCacheControlOptimizer.CountCacheControls(request));
    }

    // ------------------------------------------------------------------
    // Trimming
    // ------------------------------------------------------------------

    [Fact]
    public void ExceedsLimit_TrimsEarliestMessageBreakpoints()
    {
        var five = Request(
            Blocks(
                "user",
                new AnthropicMessageContentBlock { Type = "text", Text = "turn1-a", CacheControl = Ephemeral() },
                Text("turn1-b")),
            Message("assistant", "resp"),
            Blocks(
                "user",
                new AnthropicMessageContentBlock { Type = "text", Text = "turn2", CacheControl = Ephemeral() }));
        five.Tools = [Tool("tool_a", Ephemeral()), Tool("tool_b", Ephemeral())];
        five.System = new AnthropicSystemPrompt
        {
            MultiplePrompts =
            [
                new AnthropicSystemPromptPart { Type = "text", Text = "sys1", CacheControl = Ephemeral() },
                new AnthropicSystemPromptPart { Type = "text", Text = "sys2" },
            ],
        };

        AnthropicCacheControlOptimizer.Optimize(five);

        Assert.Equal(4, AnthropicCacheControlOptimizer.CountCacheControls(five));

        var fiveTools = five.Tools!;
        var fiveSystem = five.System!.MultiplePrompts!;
        var fiveMessages = five.Messages!;
        Assert.NotNull(fiveTools[0].CacheControl);
        Assert.NotNull(fiveTools[1].CacheControl);
        Assert.NotNull(fiveSystem[0].CacheControl);
        Assert.Null(fiveSystem[1].CacheControl);
        Assert.Null(fiveMessages[0].Content!.MultipleContent![0].CacheControl);
        Assert.Null(fiveMessages[0].Content!.MultipleContent![1].CacheControl);
        Assert.NotNull(fiveMessages[2].Content!.MultipleContent![0].CacheControl);

        var six = Request(
            Blocks(
                "user",
                new AnthropicMessageContentBlock { Type = "text", Text = "turn1", CacheControl = Ephemeral() },
                new AnthropicMessageContentBlock { Type = "text", Text = "turn1-b", CacheControl = Ephemeral() }),
            Message("assistant", "resp"),
            Message("user", "turn2"));
        six.Tools = [Tool("tool_a", Ephemeral()), Tool("tool_b", Ephemeral())];
        six.System = new AnthropicSystemPrompt
        {
            MultiplePrompts =
            [
                new AnthropicSystemPromptPart { Type = "text", Text = "sys1", CacheControl = Ephemeral() },
                new AnthropicSystemPromptPart { Type = "text", Text = "sys2", CacheControl = Ephemeral() },
            ],
        };

        AnthropicCacheControlOptimizer.Optimize(six);

        Assert.Equal(4, AnthropicCacheControlOptimizer.CountCacheControls(six));

        var sixTools = six.Tools!;
        var sixSystem = six.System!.MultiplePrompts!;
        var sixMessages = six.Messages!;
        Assert.NotNull(sixTools[0].CacheControl);
        Assert.NotNull(sixTools[1].CacheControl);
        Assert.NotNull(sixSystem[0].CacheControl);
        Assert.NotNull(sixSystem[1].CacheControl);
        Assert.Null(sixMessages[0].Content!.MultipleContent![0].CacheControl);
        Assert.Null(sixMessages[0].Content!.MultipleContent![1].CacheControl);
        Assert.Null(sixMessages[2].Content!.MultipleContent![0].CacheControl);
    }

    // ------------------------------------------------------------------
    // Adaptive density
    // ------------------------------------------------------------------

    [Fact]
    public void AdaptiveDensity_LongConversationGetsTwoMessageAnchors()
    {
        var request = Request(
            Blocks("user", [.. Enumerable.Range(0, 65).Select(_ => Text("chunk"))]),
            Message("assistant", "resp"),
            Message("user", "latest"));

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Equal(2, AnthropicCacheControlOptimizer.CountCacheControls(request));

        var firstBlocks = request.Messages![0].Content!.MultipleContent!;
        Assert.Contains(firstBlocks, static block => block.CacheControl is not null);
    }

    [Fact]
    public void AdaptiveDensity_SecondAnchorFallsOnWindowBoundary()
    {
        var request = Request(Blocks(
            "user",
            [.. Enumerable.Range(0, 50).Select(_ => Text("chunk"))]));

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Equal(2, AnthropicCacheControlOptimizer.CountCacheControls(request));

        var blocks = request.Messages![0].Content!.MultipleContent!;
        var marked = blocks
            .Select((block, index) => (block, index))
            .Where(static pair => pair.block.CacheControl is not null)
            .Select(static pair => pair.index)
            .ToList();

        Assert.Equal(2, marked.Count);
        Assert.Equal(29, marked[0]);
        Assert.Equal(49, marked[1]);
    }

    // ------------------------------------------------------------------
    // System prompt normalization
    // ------------------------------------------------------------------

    [Fact]
    public void SystemPromptStringForm_IsNormalizedAndInjected()
    {
        var request = Request(Message("user", "hello"));
        request.System = new AnthropicSystemPrompt { Prompt = "You are a helpful assistant." };

        AnthropicCacheControlOptimizer.Optimize(request);

        var systemParts = request.System!.MultiplePrompts;
        Assert.Null(request.System.Prompt);
        Assert.NotNull(systemParts);
        Assert.Single(systemParts!);
        Assert.Equal("You are a helpful assistant.", systemParts![0].Text);
        Assert.NotNull(systemParts[0].CacheControl);
        Assert.Equal("ephemeral", systemParts[0].CacheControl!.Type);
    }

    // ------------------------------------------------------------------
    // Structural anchors
    // ------------------------------------------------------------------

    [Fact]
    public void StructuralAnchors_FillToolsAndSystemWhenOnlyMessagesAreMarked()
    {
        var request = Request(
            Blocks("user", new AnthropicMessageContentBlock { Type = "text", Text = "context", CacheControl = Ephemeral() }),
            Message("assistant", "ok"),
            Blocks("user", new AnthropicMessageContentBlock { Type = "text", Text = "question", CacheControl = Ephemeral() }));
        request.Tools = [Tool("bash"), Tool("edit")];
        request.System = new AnthropicSystemPrompt
        {
            MultiplePrompts = [new AnthropicSystemPromptPart { Type = "text", Text = "You are helpful." }],
        };

        AnthropicCacheControlOptimizer.Optimize(request);

        var tools = request.Tools!;
        var systemParts = request.System!.MultiplePrompts!;
        Assert.NotNull(tools[1].CacheControl);
        Assert.Equal("ephemeral", tools[1].CacheControl!.Type);
        Assert.NotNull(systemParts[0].CacheControl);
        Assert.Equal("ephemeral", systemParts[0].CacheControl!.Type);
        Assert.Equal(4, AnthropicCacheControlOptimizer.CountCacheControls(request));
    }

    [Fact]
    public void StructuralAnchors_DoNotDuplicateClientToolBreakpoint()
    {
        var request = Request(Message("user", "hello"));
        request.Tools = [Tool("bash"), Tool("edit", Ephemeral())];

        AnthropicCacheControlOptimizer.Optimize(request);

        var tools = request.Tools!;
        Assert.Null(tools[0].CacheControl);
        Assert.Equal("ephemeral", tools[1].CacheControl!.Type);
    }

    [Fact]
    public void StructuralAnchors_AreSkippedWhenMessageBudgetIsFull()
    {
        var request = Request(Blocks(
            "user",
            new AnthropicMessageContentBlock { Type = "text", Text = "m1", CacheControl = Ephemeral() },
            new AnthropicMessageContentBlock { Type = "text", Text = "m2", CacheControl = Ephemeral() },
            new AnthropicMessageContentBlock { Type = "text", Text = "m3", CacheControl = Ephemeral() },
            new AnthropicMessageContentBlock { Type = "text", Text = "m4", CacheControl = Ephemeral() }));
        request.Tools = [Tool("bash"), Tool("edit")];
        request.System = new AnthropicSystemPrompt { Prompt = "You are helpful" };

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Null(request.Tools![1].CacheControl);

        var systemParts = request.System!.MultiplePrompts;
        Assert.True(systemParts is null || systemParts.Count == 0);
        Assert.Equal(4, AnthropicCacheControlOptimizer.CountCacheControls(request));
    }

    // ------------------------------------------------------------------
    // Thinking blocks
    // ------------------------------------------------------------------

    [Fact]
    public void ThinkingBlocks_AreSanitizedBeforePlanning()
    {
        var request = Request(Blocks(
            "assistant",
            new AnthropicMessageContentBlock
            {
                Type = "thinking",
                Thinking = "thought",
                CacheControl = Ephemeral(),
            },
            Text("answer")));

        AnthropicCacheControlOptimizer.Optimize(request);

        var blocks = request.Messages![0].Content!.MultipleContent!;
        Assert.Null(blocks[0].CacheControl);
        Assert.NotNull(blocks[1].CacheControl);
        Assert.Equal(1, AnthropicCacheControlOptimizer.CountCacheControls(request));
    }

    [Fact]
    public void ThinkingBlocks_AreNeverInjectedAndDoNotCountTowardsDensity()
    {
        var blocks = new List<AnthropicMessageContentBlock>();
        for (var i = 0; i < 40; i++)
        {
            blocks.Add(new AnthropicMessageContentBlock { Type = "thinking", Thinking = "thought" });
        }

        for (var i = 0; i < 30; i++)
        {
            blocks.Add(Text("chunk"));
        }

        var request = Request(Blocks("user", [.. blocks]));

        AnthropicCacheControlOptimizer.Optimize(request);

        var actual = request.Messages![0].Content!.MultipleContent!;
        foreach (var block in actual)
        {
            if (block.Type == "thinking")
            {
                Assert.Null(block.CacheControl);
            }
        }

        Assert.Equal(2, AnthropicCacheControlOptimizer.CountCacheControls(request));
    }

    [Fact]
    public void ThinkingBlocks_EmptyTextMarkersAreCleanedAndNotInjectable()
    {
        var request = Request(Blocks(
            "user",
            new AnthropicMessageContentBlock { Type = "text", Text = string.Empty, CacheControl = Ephemeral() },
            Text("valid")));

        AnthropicCacheControlOptimizer.Optimize(request);

        var blocks = request.Messages![0].Content!.MultipleContent!;
        Assert.Null(blocks[0].CacheControl);
        Assert.NotNull(blocks[1].CacheControl);
        Assert.Equal(1, AnthropicCacheControlOptimizer.CountCacheControls(request));
    }

    // ------------------------------------------------------------------
    // Regression (ensure_cache_control_regression_test.go)
    // ------------------------------------------------------------------

    [Fact]
    public void Regression_TrimsStructuralOnlyOverflow()
    {
        var request = new AnthropicMessageRequest
        {
            Tools =
            [
                Tool("tool-1", Ephemeral()),
                Tool("tool-2", Ephemeral()),
                Tool("tool-3", Ephemeral()),
            ],
            System = new AnthropicSystemPrompt
            {
                MultiplePrompts =
                [
                    new AnthropicSystemPromptPart { Type = "text", Text = "system-1", CacheControl = Ephemeral() },
                    new AnthropicSystemPromptPart { Type = "text", Text = "system-2", CacheControl = Ephemeral() },
                ],
            },
        };

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Equal(4, AnthropicCacheControlOptimizer.CountCacheControls(request));

        var tools = request.Tools!;
        var systemParts = request.System!.MultiplePrompts!;
        Assert.Null(tools[0].CacheControl);
        Assert.NotNull(tools[1].CacheControl);
        Assert.NotNull(tools[2].CacheControl);
        Assert.NotNull(systemParts[0].CacheControl);
        Assert.NotNull(systemParts[1].CacheControl);
    }

    [Fact]
    public void Regression_GeneratedAnchorsDoNotDisplaceClientBreakpoints()
    {
        var request = Request(Blocks(
            "user",
            new AnthropicMessageContentBlock { Type = "text", Text = "client-1", CacheControl = Ephemeral() },
            new AnthropicMessageContentBlock { Type = "text", Text = "client-2", CacheControl = Ephemeral() },
            new AnthropicMessageContentBlock { Type = "text", Text = "client-3", CacheControl = Ephemeral() },
            new AnthropicMessageContentBlock { Type = "text", Text = "client-4", CacheControl = Ephemeral() }));
        request.Tools = [Tool("tool")];
        request.System = new AnthropicSystemPrompt
        {
            MultiplePrompts = [new AnthropicSystemPromptPart { Type = "text", Text = "system" }],
        };

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Equal(4, AnthropicCacheControlOptimizer.CountCacheControls(request));
        Assert.Null(request.Tools![0].CacheControl);
        Assert.Null(request.System!.MultiplePrompts![0].CacheControl);
        foreach (var block in request.Messages![0].Content!.MultipleContent!)
        {
            Assert.NotNull(block.CacheControl);
        }
    }

    [Fact]
    public void Regression_GeneratedAnchorFollowsClientOneHourAnchor()
    {
        var request = Request(Message("user", "question"));
        request.Tools = [Tool("tool")];
        request.System = new AnthropicSystemPrompt
        {
            MultiplePrompts =
            [
                new AnthropicSystemPromptPart
                {
                    Type = "text",
                    Text = "system",
                    CacheControl = new AnthropicCacheControl { Type = "ephemeral", Ttl = "1h" },
                },
            ],
        };

        AnthropicCacheControlOptimizer.Optimize(request);

        var systemParts = request.System!.MultiplePrompts!;
        Assert.Null(request.Tools![0].CacheControl);
        Assert.Equal("1h", systemParts[0].CacheControl!.Ttl);

        var blocks = request.Messages![0].Content!.MultipleContent!;
        Assert.Single(blocks);
        Assert.NotNull(blocks[0].CacheControl);
        Assert.Null(blocks[0].CacheControl!.Ttl);
        Assert.Equal(2, AnthropicCacheControlOptimizer.CountCacheControls(request));
    }

    [Fact]
    public void Regression_PreservesClientAnchorsAsConversationGrows()
    {
        var first = Request(
            Blocks("user", new AnthropicMessageContentBlock { Type = "text", Text = "stable-1", CacheControl = Ephemeral() }),
            Message("assistant", "reply"),
            Blocks("user", new AnthropicMessageContentBlock { Type = "text", Text = "stable-2", CacheControl = Ephemeral() }));
        first.Tools = [Tool("tool", Ephemeral())];
        first.System = new AnthropicSystemPrompt
        {
            MultiplePrompts =
            [
                new AnthropicSystemPromptPart { Type = "text", Text = "system", CacheControl = Ephemeral() },
            ],
        };

        var secondMessages = first.Messages!
            .Concat([Message("assistant", "new reply"), Message("user", "new question")])
            .ToArray();
        var second = Request(secondMessages);
        second.Tools = first.Tools;
        second.System = first.System;

        AnthropicCacheControlOptimizer.Optimize(first);
        AnthropicCacheControlOptimizer.Optimize(second);

        Assert.Equal(4, AnthropicCacheControlOptimizer.CountCacheControls(first));
        Assert.Equal(4, AnthropicCacheControlOptimizer.CountCacheControls(second));

        var firstMessages = first.Messages!;
        var secondMessagesList = second.Messages!;
        Assert.NotNull(firstMessages[0].Content!.MultipleContent![0].CacheControl);
        Assert.NotNull(firstMessages[2].Content!.MultipleContent![0].CacheControl);
        Assert.NotNull(secondMessagesList[0].Content!.MultipleContent![0].CacheControl);
        Assert.NotNull(secondMessagesList[2].Content!.MultipleContent![0].CacheControl);
        Assert.Null(secondMessagesList[3].Content!.MultipleContent![0].CacheControl);
        Assert.Null(secondMessagesList[4].Content!.MultipleContent![0].CacheControl);
    }

    [Fact]
    public void Regression_CountsNestedRawBreakpoints()
    {
        var raw =
            """
            [
                {"type":"tool_result","tool_use_id":"tool-1","content":[
                    {"type":"text","text":"cached result","cache_control":{"type":"ephemeral","ttl":"1h"}},
                    {"type":"text","text":"more result","cache_control":{"type":"ephemeral"}}
                ]},
                {"type":"text","text":"client tail","cache_control":{"type":"ephemeral"}}
            ]
            """;

        var content = new AnthropicMessageContent { Raw = Encoding.UTF8.GetBytes(raw) };
        var request = Request(new AnthropicMessageParam { Role = "user", Content = content });
        request.Tools = [Tool("tool", Ephemeral())];

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Equal(4, AnthropicCacheControlOptimizer.CountCacheControls(request));
        Assert.Equal(raw, Encoding.UTF8.GetString(request.Messages![0].Content!.Raw!));
    }

    [Fact]
    public void Regression_DoesNotInjectBeforeNestedRawOneHourBreakpoint()
    {
        var raw =
            """
            [
                {"type":"tool_result","tool_use_id":"tool-1","content":[
                    {"type":"text","text":"cached result","cache_control":{"type":"ephemeral","ttl":"1h"}}
                ]}
            ]
            """;

        var content = new AnthropicMessageContent { Raw = Encoding.UTF8.GetBytes(raw) };
        var request = Request(new AnthropicMessageParam { Role = "user", Content = content });
        request.Tools = [Tool("tool")];
        request.System = new AnthropicSystemPrompt
        {
            MultiplePrompts = [new AnthropicSystemPromptPart { Type = "text", Text = "system" }],
        };

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Null(request.Tools![0].CacheControl);
        Assert.Null(request.System!.MultiplePrompts![0].CacheControl);
        Assert.Equal(1, AnthropicCacheControlOptimizer.CountCacheControls(request));
    }

    [Fact]
    public void Regression_TrimsRawBreakpointsToLimit()
    {
        var raw =
            """
            [
                {"type":"text","text":"one","cache_control":{"type":"ephemeral"}},
                {"type":"text","text":"two","cache_control":{"type":"ephemeral"}},
                {"type":"text","text":"three","cache_control":{"type":"ephemeral"}},
                {"type":"text","text":"four","cache_control":{"type":"ephemeral"}},
                {"type":"text","text":"five","cache_control":{"type":"ephemeral"}}
            ]
            """;

        var content = new AnthropicMessageContent { Raw = Encoding.UTF8.GetBytes(raw) };
        var request = Request(new AnthropicMessageParam { Role = "user", Content = content });

        AnthropicCacheControlOptimizer.Optimize(request);

        Assert.Equal(4, AnthropicCacheControlOptimizer.CountCacheControls(request));

        var blocks = request.Messages![0].Content!.MultipleContent;
        Assert.NotNull(blocks);
        Assert.Null(blocks![0].CacheControl);
        Assert.NotNull(blocks[4].CacheControl);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AnthropicMessageRequest Request(params AnthropicMessageParam[] messages)
        => new() { Messages = [.. messages] };

    private static AnthropicMessageParam Message(string role, string? text)
        => new()
        {
            Role = role,
            Content = text is null
                ? new AnthropicMessageContent()
                : new AnthropicMessageContent { Content = text },
        };

    private static AnthropicMessageParam Blocks(string role, params AnthropicMessageContentBlock[] blocks)
        => new()
        {
            Role = role,
            Content = new AnthropicMessageContent { MultipleContent = [.. blocks] },
        };

    private static AnthropicMessageContentBlock Text(string text)
        => new() { Type = "text", Text = text };

    private static AnthropicCacheControl Ephemeral()
        => new() { Type = "ephemeral" };

    private static AnthropicTool Tool(string name, AnthropicCacheControl? cacheControl = null)
        => new() { Name = name, CacheControl = cacheControl };
}
