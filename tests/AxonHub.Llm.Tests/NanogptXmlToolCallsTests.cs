using AxonHub.Llm.Transformer.Nanogpt;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/nanogpt/xml_parser_test.go: the regex-based
/// extractor for tool calls embedded in NanoGPT message content.
/// </summary>
public sealed class NanogptXmlToolCallsTests
{
    // Reference TestMaybeHasXMLToolCalls.
    [Theory]
    [InlineData("", false)]
    [InlineData("Hello, this is just text", false)]
    [InlineData("""<Write file_path="x">content</Write>""", true)]
    [InlineData("""<use_tool name="write">content</use_tool>""", true)]
    [InlineData("Running <Bash>ls</Bash> command", true)]
    [InlineData("""<Read file_path="x"/>""", true)]
    [InlineData("<div>not a tool</div>", true)]
    public void MaybeHasXmlToolCalls(string content, bool expected)
        => Assert.Equal(expected, NanogptXmlToolCalls.MaybeHasXmlToolCalls(content));

    // Reference TestParseXMLToolCalls_SelfClosing.
    [Fact]
    public void ParseXmlToolCalls_SelfClosing()
    {
        var tools = NanogptXmlToolCalls.ParseXmlToolCalls(
            """<Write file_path="/test/file.txt" content="hello world"/>""",
            out var remaining);

        var tool = Assert.Single(tools!);
        Assert.Equal("write", tool.Function.Name);
        Assert.Equal(
            """{"content":"hello world","file_path":"/test/file.txt"}""",
            tool.Function.Arguments);
        Assert.Equal(string.Empty, remaining);
    }

    // Reference TestParseXMLToolCalls_SimpleContent.
    [Fact]
    public void ParseXmlToolCalls_SimpleContent()
    {
        var tools = NanogptXmlToolCalls.ParseXmlToolCalls(
            """<Write file_path="/test/file.txt">file contents here</Write>""",
            out _);

        var tool = Assert.Single(tools!);
        Assert.Equal("write", tool.Function.Name);
        Assert.Equal(
            """{"content":"file contents here","file_path":"/test/file.txt"}""",
            tool.Function.Arguments);
    }

    // Reference TestParseXMLToolCalls_JSONInContent.
    [Fact]
    public void ParseXmlToolCalls_JsonInContent()
    {
        var tools = NanogptXmlToolCalls.ParseXmlToolCalls(
            """<Write>{"file_path": "/test/file.txt", "content": "hello"}</Write>""",
            out _);

        var tool = Assert.Single(tools!);
        Assert.Equal("write", tool.Function.Name);
        Assert.Equal(
            """{"content":"hello","file_path":"/test/file.txt"}""",
            tool.Function.Arguments);
    }

    // Reference TestParseXMLToolCalls_MismatchedClosingTag.
    [Fact]
    public void ParseXmlToolCalls_MismatchedClosingTag()
    {
        var tools = NanogptXmlToolCalls.ParseXmlToolCalls(
            """<Write file_path="/test/file.txt">content</use_tool>""",
            out _);

        var tool = Assert.Single(tools!);
        Assert.Equal("write", tool.Function.Name);
        Assert.Equal(
            """{"content":"content","file_path":"/test/file.txt"}""",
            tool.Function.Arguments);
    }

    // Reference TestParseXMLToolCalls_UnclosedOpeningTag.
    [Fact]
    public void ParseXmlToolCalls_UnclosedOpeningTag()
    {
        var tools = NanogptXmlToolCalls.ParseXmlToolCalls(
            "<Write file_path=\"/test/file.txt\" content=\"hello\"\n}\n</use_tool>",
            out _);

        var tool = Assert.Single(tools!);
        Assert.Equal("write", tool.Function.Name);
        // The greedy attribute group backtracks to the last newline before
        // </use_tool>, so the `}` line is captured by the attrs group and the
        // inner content is empty (the reference test only asserts the name).
        Assert.Equal(
            """{"content":"hello","file_path":"/test/file.txt"}""",
            tool.Function.Arguments);
    }

    // Reference TestParseXMLToolCalls_NestedXMLElements.
    [Fact]
    public void ParseXmlToolCalls_NestedXmlElements()
    {
        var tools = NanogptXmlToolCalls.ParseXmlToolCalls(
            """
            <Write>
              <file_path>/test/file.txt</file_path>
              <content>hello world</content>
            </Write>
            """,
            out _);

        var tool = Assert.Single(tools!);
        Assert.Equal("write", tool.Function.Name);
        Assert.Equal(
            """{"content":"hello world","file_path":"/test/file.txt"}""",
            tool.Function.Arguments);
    }

    // Reference TestParseXMLToolCases_NoSpaceAfterTag.
    [Fact]
    public void ParseXmlToolCalls_NoSpaceAfterTag()
    {
        var tools = NanogptXmlToolCalls.ParseXmlToolCalls(
            """<Write_File>{"path": "/test/file.txt", "content": "hello"}</Write_File>""",
            out _);

        var tool = Assert.Single(tools!);
        Assert.Equal("write", tool.Function.Name);
        Assert.Equal("""{"content":"hello","path":"/test/file.txt"}""", tool.Function.Arguments);
    }

    // Reference TestParseXMLToolCalls_MultipleToolCalls.
    [Fact]
    public void ParseXmlToolCalls_MultipleToolCalls()
    {
        var content = """
            <Write file_path="/file1.txt">content1</Write>
            Some text in between
            <Read file_path="/file2.txt"/>
            """;

        var tools = NanogptXmlToolCalls.ParseXmlToolCalls(content, out var remaining);

        Assert.Equal(2, tools!.Count);
        Assert.Equal("write", tools[0].Function.Name);
        Assert.Equal("read", tools[1].Function.Name);
        Assert.Contains("Some text in between", remaining, StringComparison.Ordinal);
    }

    // Reference TestParseXMLToolCalls_NoToolCalls.
    [Fact]
    public void ParseXmlToolCalls_NoToolCalls()
    {
        const string content = "This is just plain text without any tool calls";

        var tools = NanogptXmlToolCalls.ParseXmlToolCalls(content, out var remaining);

        Assert.Null(tools);
        Assert.Equal(content, remaining);
    }

    [Fact]
    public void ParseXmlToolCalls_UnrecognizedTagsKeepTheWholeContent()
    {
        const string content = "<div>not a tool</div>";

        var tools = NanogptXmlToolCalls.ParseXmlToolCalls(content, out var remaining);

        Assert.Null(tools);
        Assert.Equal(content, remaining);
    }

    // Reference TestExtractToolName.
    [Theory]
    [InlineData("Write", "", "write")]
    [InlineData("Write_FILE", "", "write")]
    [InlineData("Write_file", "", "write")]
    [InlineData("Read", "", "read")]
    [InlineData("Read_FILE", "", "read")]
    [InlineData("Bash", "", "bash")]
    [InlineData("Python", "", "python")]
    [InlineData("Search", "", "search")]
    [InlineData("Glob", "", "glob")]
    [InlineData("use_tool", "name=\"write\"", "write")]
    [InlineData("use_tool", "name=\"Read\"", "read")]
    [InlineData("Unknown", "", "")]
    [InlineData("", "", "")]
    public void ExtractToolName(string tagName, string attrs, string expected)
        => Assert.Equal(expected, NanogptXmlToolCalls.ExtractToolName(tagName, attrs));

    // Reference TestGenerateToolCallID.
    [Fact]
    public void GenerateToolCallId_IsDeterministicSha256()
    {
        var first = NanogptXmlToolCalls.GenerateToolCallId(
            "write",
            """{"file_path":"/test.txt"}""");
        var second = NanogptXmlToolCalls.GenerateToolCallId(
            "write",
            """{"file_path":"/test.txt"}""");

        Assert.Equal("nanogpt_9da63d92eab917b3", first);
        Assert.Equal(first, second);
        Assert.NotEqual(
            first,
            NanogptXmlToolCalls.GenerateToolCallId(
                "read",
                """{"file_path":"/test.txt"}"""));
    }

    // Reference TestToOpenAIToolCalls.
    [Fact]
    public void ToOpenAiToolCalls_MapsAndDropsEmptyInput()
    {
        var result = NanogptXmlToolCalls.ToOpenAiToolCalls(
        [
            new ToolCall
            {
                Index = 0,
                Id = "test-id-1",
                Type = "function",
                Function = new FunctionCall
                {
                    Name = "write",
                    Arguments = """{"file_path":"/test.txt"}""",
                },
            },
        ]);

        var toolCall = Assert.Single(result!);
        Assert.Equal("test-id-1", toolCall.Id);
        Assert.Equal("write", toolCall.Function!.Name);

        Assert.Null(NanogptXmlToolCalls.ToOpenAiToolCalls([]));
    }

    [Fact]
    public void ParseXmlToolCalls_MergesNumbersAndNestedJsonWithGoFormatting()
    {
        var tools = NanogptXmlToolCalls.ParseXmlToolCalls(
            """<Write>{"file_path":"/x","count":1.0,"nested":{"b":true,"a":[1,2]}}</Write>""",
            out _);

        // Go re-marshals float64 values without a trailing .0 and sorts map
        // keys by their UTF-8 bytes.
        Assert.Equal(
            """{"count":1,"file_path":"/x","nested":{"a":[1,2],"b":true}}""",
            Assert.Single(tools!).Function.Arguments);
    }
}
