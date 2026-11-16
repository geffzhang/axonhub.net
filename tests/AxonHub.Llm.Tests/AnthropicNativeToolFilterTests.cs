namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Anthropic-native tool helpers
/// (llm/transformer/anthropic/tools.go:11-56): only the transformed
/// <c>web_search</c> / <c>web_search_20250305</c> tool types are native; a
/// function merely named <c>web_search</c> is not.
/// </summary>
public sealed class AnthropicNativeToolFilterTests
{
    [Fact]
    public void OnlyNativeWebSearchTypesCountAsAnthropicNative()
    {
        var cases = new (Tool Tool, bool Expected)[]
        {
            (new Tool { Type = ToolTypes.Function, Function = new Function { Name = "web_search" } }, false),
            (new Tool { Type = ToolTypes.Function, Function = new Function { Name = "get_weather" } }, false),
            (new Tool { Type = "other", Function = new Function { Name = "web_search" } }, false),
            (new Tool { Type = ToolTypes.GoogleSearch }, false),
            (new Tool(), false),
            (new Tool { Type = AnthropicToolFilter.ToolTypeWebSearch20250305 }, true),
            (new Tool
            {
                Type = AnthropicToolFilter.ToolTypeWebSearch20250305,
                Function = new Function { Name = "web_search" },
            }, true),
            (new Tool { Type = ToolTypes.WebSearch }, true),
        };

        foreach (var (tool, expected) in cases)
        {
            Assert.Equal(expected, AnthropicToolFilter.IsAnthropicNativeTool(tool));
            Assert.Equal(expected, AnthropicToolFilter.ContainsAnthropicNativeTools([tool]));
        }
    }

    [Fact]
    public void ContainsHandlesNullEmptyAndMixedLists()
    {
        Assert.False(AnthropicToolFilter.ContainsAnthropicNativeTools(null));
        Assert.False(AnthropicToolFilter.ContainsAnthropicNativeTools([]));
        Assert.False(AnthropicToolFilter.ContainsAnthropicNativeTools(
        [
            new Tool { Type = ToolTypes.Function, Function = new Function { Name = "web_search" } },
        ]));
        Assert.True(AnthropicToolFilter.ContainsAnthropicNativeTools(
        [
            new Tool { Type = ToolTypes.Function, Function = new Function { Name = "get_weather" } },
            new Tool { Type = AnthropicToolFilter.ToolTypeWebSearch20250305 },
        ]));
    }

    [Fact]
    public void FilterOutKeepsNullEmptyAndFunctionOnlyListsUnchanged()
    {
        Assert.Null(AnthropicToolFilter.FilterOutAnthropicNativeTools(null));

        var empty = new List<Tool>();
        Assert.Same(empty, AnthropicToolFilter.FilterOutAnthropicNativeTools(empty));

        var functions = new List<Tool>
        {
            new() { Type = ToolTypes.Function, Function = new Function { Name = "get_weather" } },
            new() { Type = ToolTypes.Function, Function = new Function { Name = "web_search" } },
        };
        Assert.Equal(
            [ToolTypes.Function, ToolTypes.Function],
            AnthropicToolFilter.FilterOutAnthropicNativeTools(functions)!.Select(tool => tool.Type).ToArray());

        var mixed = new List<Tool>
        {
            new() { Type = ToolTypes.Function, Function = new Function { Name = "fn1" } },
            new() { Type = AnthropicToolFilter.ToolTypeWebSearch20250305 },
            new() { Type = ToolTypes.WebSearch },
        };
        var filtered = AnthropicToolFilter.FilterOutAnthropicNativeTools(mixed);
        var tool = Assert.Single(filtered!);
        Assert.Equal(ToolTypes.Function, tool.Type);
    }
}
