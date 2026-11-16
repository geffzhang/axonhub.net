using System.Text;
using System.Text.Json;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Claude Code request utilities
/// (llm/transformer/anthropic/claudecode/utils.go and the cases in
/// outbound_test.go that exercise them).
/// </summary>
public sealed class ClaudeCodeUtilsTests
{
    // ------------------------------------------------------------------
    // RemoveBillingSystemMessages (reference TestRemoveBillingSystemMessages)
    // ------------------------------------------------------------------

    [Fact]
    public void TestRemoveBillingSystemMessages()
    {
        const string Billing = "  X-Anthropic-Billing-Header: cc_version=2.1.42;";
        const string Keep = "keep";
        const string UserBilling = "x-anthropic-billing-header: user text";

        var request = new LlmRequest
        {
            Messages =
            [
                new Message { Role = "system", Content = new MessageContent { Content = Billing } },
                new Message { Role = "user", Content = new MessageContent { Content = UserBilling } },
                new Message
                {
                    Role = "system",
                    Content = new MessageContent
                    {
                        MultipleContent =
                        [
                            new MessageContentPart { Type = "text", Text = Billing },
                            new MessageContentPart { Type = "text", Text = Keep },
                        ],
                    },
                },
            ],
        };

        var result = ClaudeCodeUtils.RemoveBillingSystemMessages(request);

        Assert.NotSame(request, result);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("user", result.Messages[0].Role);
        Assert.Single(result.Messages[1].Content!.MultipleContent!);
        Assert.Equal(Keep, result.Messages[1].Content!.MultipleContent![0].Text);

        // The shared inbound request must not be mutated (Go copies the struct).
        Assert.Equal(3, request.Messages.Count);
        Assert.Equal(2, request.Messages[2].Content!.MultipleContent!.Count);
    }

    [Fact]
    public void TestRemoveBillingSystemMessages_NoBillingReturnsSameRequest()
    {
        var request = new LlmRequest
        {
            Messages =
            [
                new Message { Role = "system", Content = new MessageContent { Content = "keep" } },
                new Message { Role = "user", Content = new MessageContent { Content = "hi" } },
            ],
        };

        Assert.Same(request, ClaudeCodeUtils.RemoveBillingSystemMessages(request));

        var empty = new LlmRequest();
        Assert.Same(empty, ClaudeCodeUtils.RemoveBillingSystemMessages(empty));
    }

    [Theory]
    [InlineData("x-anthropic-billing-header: x", true)]
    [InlineData("  X-Anthropic-Billing-Header: cc_version=2.1.42;", true)]
    [InlineData("\tX-ANTHROPIC-BILLING-HEADER:", true)]
    [InlineData("keep", false)]
    [InlineData("", false)]
    public void TestIsBillingHeaderText(string text, bool expected)
        => Assert.Equal(expected, ClaudeCodeUtils.IsBillingHeaderText(text));

    // ------------------------------------------------------------------
    // MergeBetasIntoHeader
    // ------------------------------------------------------------------

    [Fact]
    public void TestMergeBetasIntoHeader()
    {
        Assert.Equal(
            "a,b",
            ClaudeCodeUtils.MergeBetasIntoHeader(string.Empty, ["a", "b"]));
        Assert.Equal(
            "a,b,c",
            ClaudeCodeUtils.MergeBetasIntoHeader("a,b", ["b", "c"]));
        Assert.Equal(
            "a,b",
            ClaudeCodeUtils.MergeBetasIntoHeader(" a , b ", [" b ", string.Empty]));
        Assert.Equal(
            string.Empty,
            ClaudeCodeUtils.MergeBetasIntoHeader("  ", [string.Empty]));
    }

    [Fact]
    public void TestMergeBetasIntoHeader_DoesNotDeduplicateBaseEntries()
    {
        // The reference tracks base entries in a set but appends them all.
        Assert.Equal(
            "a,a,b",
            ClaudeCodeUtils.MergeBetasIntoHeader("a,a", ["a", "b"]));
    }

    // ------------------------------------------------------------------
    // DisableThinkingIfToolChoiceForced
    // ------------------------------------------------------------------

    [Fact]
    public void TestDisableThinkingIfToolChoiceForced()
    {
        var request = new LlmRequest
        {
            ReasoningEffort = "high",
            ReasoningBudget = 10_000,
            ToolChoice = new ToolChoice { ToolChoiceMode = "any" },
        };

        var result = ClaudeCodeUtils.DisableThinkingIfToolChoiceForced(request);

        Assert.NotSame(request, result);
        Assert.Null(result.ReasoningEffort);
        Assert.Null(result.ReasoningBudget);

        // The shared request keeps its thinking configuration.
        Assert.Equal("high", request.ReasoningEffort);
        Assert.Equal(10_000L, request.ReasoningBudget);
    }

    [Fact]
    public void TestDisableThinkingIfToolChoiceForced_AllowedToolsEnvelope()
    {
        var request = new LlmRequest
        {
            ReasoningEffort = "medium",
            ToolChoice = new ToolChoice
            {
                ToolChoiceMode = "any",
                Tools = [new ToolOption { Type = "function", Name = "bash" }],
            },
        };

        Assert.Null(
            ClaudeCodeUtils.DisableThinkingIfToolChoiceForced(request).ReasoningEffort);
    }

    [Fact]
    public void TestDisableThinkingIfToolChoiceForced_NamedTool()
    {
        var request = new LlmRequest
        {
            ReasoningEffort = "high",
            ToolChoice = new ToolChoice
            {
                NamedToolChoice = new NamedToolChoice
                {
                    Type = "tool",
                    Function = new ToolFunction { Name = "bash" },
                },
            },
        };

        Assert.Null(
            ClaudeCodeUtils.DisableThinkingIfToolChoiceForced(request).ReasoningEffort);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("auto", null)]
    [InlineData("none", null)]
    [InlineData("", null)]
    [InlineData(null, "function")]
    public void TestDisableThinkingIfToolChoiceForced_Passthrough(string? mode, string? namedType)
    {
        var request = new LlmRequest
        {
            ReasoningEffort = "high",
            ToolChoice = new ToolChoice
            {
                ToolChoiceMode = mode,
                NamedToolChoice = namedType is null
                    ? null
                    : new NamedToolChoice
                    {
                        Type = namedType,
                        Function = new ToolFunction { Name = "bash" },
                    },
            },
        };

        Assert.Same(request, ClaudeCodeUtils.DisableThinkingIfToolChoiceForced(request));
        Assert.Equal("high", request.ReasoningEffort);
    }

    [Fact]
    public void TestDisableThinkingIfToolChoiceForced_WithoutReasoningIsPassthrough()
    {
        var request = new LlmRequest
        {
            ToolChoice = new ToolChoice { ToolChoiceMode = "any" },
        };

        Assert.Same(request, ClaudeCodeUtils.DisableThinkingIfToolChoiceForced(request));
    }

    [Fact]
    public void TestDisableThinkingIfToolChoiceForced_WithoutToolChoiceIsPassthrough()
    {
        var request = new LlmRequest { ReasoningEffort = "high" };

        Assert.Same(request, ClaudeCodeUtils.DisableThinkingIfToolChoiceForced(request));
    }

    // ------------------------------------------------------------------
    // ApplyClaudeToolPrefix
    // ------------------------------------------------------------------

    [Fact]
    public void TestApplyClaudeToolPrefix()
    {
        var request = new LlmRequest
        {
            Tools =
            [
                new Tool
                {
                    Type = ToolTypes.Function,
                    Function = new Function { Name = "bash", Description = "Execute bash" },
                },
            ],
            ToolChoice = new ToolChoice
            {
                ToolChoiceMode = "any",
                NamedToolChoice = new NamedToolChoice
                {
                    Type = "tool",
                    Function = new ToolFunction { Name = "bash" },
                },
            },
        };

        var result = ClaudeCodeUtils.ApplyClaudeToolPrefix(request, "proxy_");

        Assert.NotSame(request, result);
        Assert.Equal("proxy_bash", result.Tools![0].Function.Name);
        Assert.Equal("proxy_bash", result.ToolChoice!.NamedToolChoice!.Function.Name);

        // The caller's request and its nested objects stay untouched.
        Assert.Equal("bash", request.Tools![0].Function.Name);
        Assert.Equal("bash", request.ToolChoice!.NamedToolChoice!.Function.Name);
        Assert.NotSame(request.Tools, result.Tools);
    }

    [Fact]
    public void TestApplyClaudeToolPrefix_AlreadyPrefixedIsKept()
    {
        var request = new LlmRequest
        {
            Tools =
            [
                new Tool { Type = ToolTypes.Function, Function = new Function { Name = "proxy_bash" } },
            ],
        };

        var result = ClaudeCodeUtils.ApplyClaudeToolPrefix(request, "proxy_");

        Assert.Equal("proxy_bash", result.Tools![0].Function.Name);
    }

    [Fact]
    public void TestApplyClaudeToolPrefix_FunctionToolChoice()
    {
        // Go prefixes both "tool" and "function" named tool choices.
        var request = new LlmRequest
        {
            ToolChoice = new ToolChoice
            {
                NamedToolChoice = new NamedToolChoice
                {
                    Type = "function",
                    Function = new ToolFunction { Name = "bash" },
                },
            },
        };

        var result = ClaudeCodeUtils.ApplyClaudeToolPrefix(request, "proxy_");

        Assert.Equal("proxy_bash", result.ToolChoice!.NamedToolChoice!.Function.Name);
    }

    [Fact]
    public void TestApplyClaudeToolPrefix_EmptyNameIsNotPrefixed()
    {
        var request = new LlmRequest
        {
            ToolChoice = new ToolChoice
            {
                NamedToolChoice = new NamedToolChoice
                {
                    Type = "tool",
                    Function = new ToolFunction { Name = string.Empty },
                },
            },
        };

        var result = ClaudeCodeUtils.ApplyClaudeToolPrefix(request, "proxy_");

        Assert.Equal(string.Empty, result.ToolChoice!.NamedToolChoice!.Function.Name);
    }

    [Fact]
    public void TestApplyClaudeToolPrefix_OtherNamedTypesAreNotPrefixed()
    {
        var request = new LlmRequest
        {
            ToolChoice = new ToolChoice
            {
                NamedToolChoice = new NamedToolChoice
                {
                    Type = "auto",
                    Function = new ToolFunction { Name = "bash" },
                },
            },
        };

        var result = ClaudeCodeUtils.ApplyClaudeToolPrefix(request, "proxy_");

        Assert.Equal("bash", result.ToolChoice!.NamedToolChoice!.Function.Name);
    }

    [Fact]
    public void TestApplyClaudeToolPrefix_EmptyPrefixReturnsSameRequest()
    {
        var request = new LlmRequest
        {
            Tools = [new Tool { Type = ToolTypes.Function, Function = new Function { Name = "bash" } }],
        };

        Assert.Same(request, ClaudeCodeUtils.ApplyClaudeToolPrefix(request, string.Empty));
    }

    // ------------------------------------------------------------------
    // StripClaudeToolPrefixFromResponse
    // ------------------------------------------------------------------

    [Fact]
    public void TestStripClaudeToolPrefixFromResponse()
    {
        const string Input =
            """{"id":"msg_1","content":[{"type":"text","text":"hi"},{"type":"tool_use","id":"toolu_1","name":"proxy_bash","input":{"command":"ls"}}],"stop_reason":"tool_use"}""";
        const string Expected =
            """{"id":"msg_1","content":[{"type":"text","text":"hi"},{"type":"tool_use","id":"toolu_1","name":"bash","input":{"command":"ls"}}],"stop_reason":"tool_use"}""";

        var result = ClaudeCodeUtils.StripClaudeToolPrefixFromResponse(
            Encoding.UTF8.GetBytes(Input),
            "proxy_");

        // sjson edits only the name value, so every other byte is preserved.
        Assert.Equal(Expected, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void TestStripClaudeToolPrefixFromResponse_MultipleToolUseBlocks()
    {
        const string Input =
            """{"content":[{"type":"tool_use","name":"proxy_a","input":{}},{"type":"tool_use","name":"proxy_b"}]}""";
        const string Expected =
            """{"content":[{"type":"tool_use","name":"a","input":{}},{"type":"tool_use","name":"b"}]}""";

        Assert.Equal(
            Expected,
            Encoding.UTF8.GetString(
                ClaudeCodeUtils.StripClaudeToolPrefixFromResponse(
                    Encoding.UTF8.GetBytes(Input),
                    "proxy_")));
    }

    [Theory]
    [InlineData("""{"content":[{"type":"text","text":"hi"}]}""")]
    [InlineData("""{"content":[{"type":"tool_use","name":"bash"}]}""")]
    [InlineData("""{"content":"hi"}""")]
    [InlineData("""{"id":"msg_1"}""")]
    [InlineData("""{"content":[""")]
    [InlineData("""[1,2]""")]
    [InlineData("not json")]
    public void TestStripClaudeToolPrefixFromResponse_Unchanged(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);

        Assert.Same(body, ClaudeCodeUtils.StripClaudeToolPrefixFromResponse(body, "proxy_"));
    }

    [Fact]
    public void TestStripClaudeToolPrefixFromResponse_EmptyInputs()
    {
        var body = Encoding.UTF8.GetBytes("""{"content":[{"type":"tool_use","name":"proxy_bash"}]}""");

        Assert.Same(body, ClaudeCodeUtils.StripClaudeToolPrefixFromResponse(body, string.Empty));

        var empty = Array.Empty<byte>();
        Assert.Same(empty, ClaudeCodeUtils.StripClaudeToolPrefixFromResponse(empty, "proxy_"));
    }

    // ------------------------------------------------------------------
    // StripClaudeToolPrefixFromStreamLine
    // ------------------------------------------------------------------

    [Fact]
    public void TestStripClaudeToolPrefixFromStreamLine()
    {
        var line = Encoding.UTF8.GetBytes(
            """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"proxy_bash"}}""");

        var result = ClaudeCodeUtils.StripClaudeToolPrefixFromStreamLine(line, "proxy_");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        Assert.Equal("content_block_start", root.GetProperty("type").GetString());
        Assert.Equal(0, root.GetProperty("index").GetInt32());
        Assert.Equal(
            "bash",
            root.GetProperty("content_block").GetProperty("name").GetString());
        Assert.Equal(
            "toolu_1",
            root.GetProperty("content_block").GetProperty("id").GetString());
        Assert.Equal(
            "tool_use",
            root.GetProperty("content_block").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("""{"type":"content_block_start","content_block":{"type":"text","text":"proxy_x"}}""")]
    [InlineData("""{"type":"content_block_start","content_block":{"type":"tool_use","name":"bash"}}""")]
    [InlineData("""{"type":"message_start"}""")]
    [InlineData("""{"content_block":{"type":"tool_use"}}""")]
    [InlineData("""not json""")]
    public void TestStripClaudeToolPrefixFromStreamLine_Unchanged(string json)
    {
        var line = Encoding.UTF8.GetBytes(json);

        Assert.Same(line, ClaudeCodeUtils.StripClaudeToolPrefixFromStreamLine(line, "proxy_"));
    }

    [Fact]
    public void TestStripClaudeToolPrefixFromStreamLine_EmptyPrefix()
    {
        var line = Encoding.UTF8.GetBytes(
            """{"type":"content_block_start","content_block":{"type":"tool_use","name":"proxy_bash"}}""");

        Assert.Same(line, ClaudeCodeUtils.StripClaudeToolPrefixFromStreamLine(line, string.Empty));
    }

    // ------------------------------------------------------------------
    // InjectClaudeCodeSystemMessage
    // ------------------------------------------------------------------

    [Fact]
    public void TestInjectClaudeCodeSystemMessage()
    {
        var request = new LlmRequest
        {
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };
        var callerMessages = request.Messages;

        var result = ClaudeCodeUtils.InjectClaudeCodeSystemMessage(request);

        Assert.Same(request, result);
        Assert.Equal(2, request.Messages.Count);
        Assert.NotSame(callerMessages, request.Messages);

        // Go rebuilds the slice; the caller's list must not be prepended to.
        Assert.Single(callerMessages);

        var first = request.Messages[0];
        Assert.Equal("system", first.Role);
        Assert.Equal(ClaudeCodeConstants.SystemMessage, first.Content!.Content);
        Assert.Equal("ephemeral", first.CacheControl!.Type);

        // Array format is required for cache_control.
        Assert.True(request.TransformOptions!.ArrayInstructions);
    }

    [Fact]
    public void TestInjectClaudeCodeSystemMessage_IsIdempotent()
    {
        var request = new LlmRequest
        {
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

        ClaudeCodeUtils.InjectClaudeCodeSystemMessage(request);
        var result = ClaudeCodeUtils.InjectClaudeCodeSystemMessage(request);

        Assert.Same(request, result);
        Assert.Equal(2, request.Messages.Count);
    }

    [Fact]
    public void TestInjectClaudeCodeSystemMessage_ClonesTransformOptions()
    {
        var options = new TransformOptions { DefaultMaxTokens = 1024 };
        var request = new LlmRequest
        {
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
            TransformOptions = options,
        };

        ClaudeCodeUtils.InjectClaudeCodeSystemMessage(request);

        Assert.NotSame(options, request.TransformOptions);
        Assert.Null(options.ArrayInstructions);
        Assert.True(request.TransformOptions!.ArrayInstructions);
        Assert.Equal(1024, request.TransformOptions.DefaultMaxTokens);
    }

    [Fact]
    public void TestInjectClaudeCodeSystemMessage_KeepsExplicitArrayInstructions()
    {
        var options = new TransformOptions { ArrayInstructions = false };
        var request = new LlmRequest
        {
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
            TransformOptions = options,
        };

        ClaudeCodeUtils.InjectClaudeCodeSystemMessage(request);

        Assert.Same(options, request.TransformOptions);
        Assert.False(request.TransformOptions!.ArrayInstructions);
    }

    [Fact]
    public void TestInjectClaudeCodeSystemMessage_OtherSystemMessageIsPrefixed()
    {
        var request = new LlmRequest
        {
            Messages =
            [
                new Message { Role = "system", Content = MessageContent.FromText("You are helpful.") },
                new Message { Role = "user", Content = MessageContent.FromText("Hello") },
            ],
        };

        ClaudeCodeUtils.InjectClaudeCodeSystemMessage(request);

        Assert.Equal(3, request.Messages.Count);
        Assert.Equal(ClaudeCodeConstants.SystemMessage, request.Messages[0].Content!.Content);
        Assert.Equal("You are helpful.", request.Messages[1].Content!.Content);
    }

    // ------------------------------------------------------------------
    // InjectFakeUserId
    // ------------------------------------------------------------------

    [Fact]
    public void TestInjectFakeUserId()
    {
        var request = new LlmRequest();

        var result = ClaudeCodeUtils.InjectFakeUserId(request, "42", "sess");

        Assert.Same(request, result);
        Assert.Equal(
            """{"device_id":"73475cb40a568e8da8a045ced110137e159f890ac4da883b6b17dc651b3a8049","account_uuid":"5c2b23de-4bad-58ee-a4b3-f22f3b9cfd7d","session_id":"sess"}""",
            request.Metadata!["user_id"]);
    }

    [Fact]
    public void TestInjectFakeUserId_KeepsValidExistingId()
    {
        const string Existing =
            """{"device_id":"abc","account_uuid":"acc","session_id":"s"}""";
        var request = new LlmRequest
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["user_id"] = Existing,
            },
        };

        ClaudeCodeUtils.InjectFakeUserId(request, "42", null);

        Assert.Equal(Existing, request.Metadata["user_id"]);
    }

    [Fact]
    public void TestInjectFakeUserId_ReplacesInvalidId()
    {
        var request = new LlmRequest
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["user_id"] = "random-string",
            },
        };

        ClaudeCodeUtils.InjectFakeUserId(request, "42", null);

        var uid = ClaudeCodeUserID.ParseUserId(request.Metadata["user_id"]);
        Assert.NotNull(uid);
        Assert.Equal(
            "73475cb40a568e8da8a045ced110137e159f890ac4da883b6b17dc651b3a8049",
            uid!.DeviceId);
        Assert.True(Guid.TryParse(uid.SessionId, out _));
    }

    // ------------------------------------------------------------------
    // IsClaudeCliUserAgent
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("claude-cli/1.0.83", true)]
    [InlineData("claude-cli/", true)]
    [InlineData("Claude-CLI/1.0", false)]
    [InlineData("curl/8.0", false)]
    [InlineData("", false)]
    public void TestIsClaudeCliUserAgent(string value, bool expected)
        => Assert.Equal(expected, ClaudeCodeUtils.IsClaudeCliUserAgent(value));
}
