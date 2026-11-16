using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of openai/outbound_convert_test.go's tool_choice/no-tools cases: an
/// upstream rejects tool_choice without tools, while omitting it means auto.
/// </summary>
public sealed class OpenAiToolChoiceTests
{
    [Fact]
    public void DropsToolChoiceWhenNoToolsAreSent()
    {
        var request = new LlmRequest
        {
            Model = "deepseek-v3",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
            ToolChoice = new ToolChoice { ToolChoiceMode = "auto" },
        };

        var wire = request.RequestFromLlm(ReasoningField.None);

        Assert.NotNull(wire);
        Assert.Null(wire!.ToolChoice);
    }

    [Fact]
    public void KeepsToolChoiceWhenToolsAreSent()
    {
        var request = new LlmRequest
        {
            Model = "deepseek-v3",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
            Tools =
            [
                new Tool
                {
                    Type = ToolTypes.Function,
                    Function = new Function
                    {
                        Name = "lookup",
                        Parameters = System.Text.Json.JsonSerializer.SerializeToElement(
                            new { type = "object", properties = new { } }),
                    },
                },
            ],
            ToolChoice = new ToolChoice { ToolChoiceMode = "required" },
        };

        var wire = request.RequestFromLlm(ReasoningField.None);

        Assert.NotNull(wire);
        Assert.NotNull(wire!.ToolChoice);
        Assert.Equal("required", wire.ToolChoice!.ToolChoice);
    }
}
