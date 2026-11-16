namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Google-native tool helpers (llm/tools.go:241-271):
/// the <c>google_*</c> naming convention, containment, and the filter that the
/// gemini_openai outbound applies as a graceful degradation.
/// </summary>
public sealed class GoogleToolFilterTests
{
    [Theory]
    [InlineData("function")]
    [InlineData("image_generation")]
    [InlineData("")]
    [InlineData("unknown_tool_type")]
    [InlineData("GoogleSearch")]
    [InlineData("google")]
    public void NonGoogleTypesAreNotGoogleNative(string type)
    {
        Assert.False(GoogleToolFilter.IsGoogleNativeTool(new Tool { Type = type }));
    }

    [Theory]
    [InlineData(ToolTypes.GoogleSearch)]
    [InlineData(ToolTypes.GoogleUrlContext)]
    [InlineData(ToolTypes.GoogleCodeExecution)]
    [InlineData("google_anything")]
    public void TheGooglePrefixMarksANativeTool(string type)
    {
        Assert.True(GoogleToolFilter.IsGoogleNativeTool(new Tool { Type = type }));
    }

    [Fact]
    public void NullAndEmptyListsContainNoGoogleNativeTools()
    {
        Assert.False(GoogleToolFilter.ContainsGoogleNativeTools(null));
        Assert.False(GoogleToolFilter.ContainsGoogleNativeTools([]));
    }

    [Fact]
    public void FunctionOnlyListsContainNoGoogleNativeTools()
    {
        Assert.False(GoogleToolFilter.ContainsGoogleNativeTools(
        [
            FunctionTool("get_weather"),
            FunctionTool("search"),
        ]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AGoogleNativeToolAnywhereIsDetected(int position)
    {
        var tools = new List<Tool>
        {
            FunctionTool("fn1"),
            FunctionTool("fn2"),
        };
        tools.Insert(position, GoogleTool(ToolTypes.GoogleSearch));

        Assert.True(GoogleToolFilter.ContainsGoogleNativeTools(tools));
    }

    [Fact]
    public void FilteringRemovesEveryGoogleNativeToolAndKeepsTheOrder()
    {
        var filtered = GoogleToolFilter.FilterGoogleNativeTools(
        [
            FunctionTool("fn1"),
            GoogleTool(ToolTypes.GoogleSearch),
            FunctionTool("fn2"),
            GoogleTool(ToolTypes.GoogleCodeExecution),
            FunctionTool("fn3"),
        ]);

        Assert.NotNull(filtered);
        Assert.Equal(["function", "function", "function"], filtered!.Select(t => t.Type));
        Assert.Equal(["fn1", "fn2", "fn3"], filtered.Select(t => t.Function.Name));
    }

    [Fact]
    public void FilteringAnAllGoogleListYieldsAnEmptyList()
    {
        var filtered = GoogleToolFilter.FilterGoogleNativeTools(
        [
            GoogleTool(ToolTypes.GoogleSearch),
            GoogleTool(ToolTypes.GoogleUrlContext),
        ]);

        Assert.NotNull(filtered);
        Assert.Empty(filtered!);
    }

    [Fact]
    public void FilteringFunctionOnlyListsKeepsEveryTool()
    {
        // Go rebuilds the slice unconditionally when len > 0
        // (make([]Tool, 0, len(tools))), so identity is not preserved.
        var filtered = GoogleToolFilter.FilterGoogleNativeTools(
            [FunctionTool("get_weather"), FunctionTool("search")]);

        Assert.NotNull(filtered);
        Assert.Equal(["get_weather", "search"], filtered!.Select(t => t.Function.Name));
    }

    [Fact]
    public void FilteringNullAndEmptyListsReturnsTheInput()
    {
        Assert.Null(GoogleToolFilter.FilterGoogleNativeTools(null));

        var empty = new List<Tool>();
        Assert.Same(empty, GoogleToolFilter.FilterGoogleNativeTools(empty));
    }

    private static Tool FunctionTool(string name)
        => new() { Type = "function", Function = new Function { Name = name } };

    private static Tool GoogleTool(string type)
        => new() { Type = type, Google = new GoogleTools() };
}
