using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Gemini;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for outbound_convert_parameters_test.go: function
/// declarations use parametersJsonSchema (new format) instead of parameters
/// (old format) so full JSON Schema constructs (const, enum) survive.
/// </summary>
public sealed class GeminiOutboundConvertParametersTests
{
    [Fact]
    public void ConvertLLMToGeminiRequest_UsesParametersJsonSchema()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "test" },
                },
            ],
            Tools =
            [
                new Tool
                {
                    Type = "function",
                    Function = new Function
                    {
                        Name = "test_tool",
                        Description = "A test tool",
                        ParametersJsonSchema = JsonDocument.Parse(
                            """
                            {
                                "type": "object",
                                "properties": {
                                    "mode": {
                                        "type": "string",
                                        "const": "strict",
                                        "description": "Must be strict"
                                    },
                                    "options": {
                                        "type": "string",
                                        "enum": ["opt1", "opt2"],
                                        "description": "Available options"
                                    }
                                },
                                "required": ["mode"]
                            }
                            """).RootElement,
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        // Verify tools were converted
        Assert.NotNull(result.Tools);
        var tool = Assert.Single(result.Tools);
        Assert.NotNull(tool.FunctionDeclarations);
        var fd = Assert.Single(tool.FunctionDeclarations);

        Assert.Equal("test_tool", fd.Name);
        Assert.Equal("A test tool", fd.Description);

        // CRITICAL: Must use ParametersJsonSchema (new format), not Parameters (old format)
        Assert.Null(fd.Parameters);
        Assert.NotNull(fd.ParametersJsonSchema);

        // Verify const and enum are preserved
        using var schema = JsonDocument.Parse(fd.ParametersJsonSchema.Value.GetRawText());
        var props = schema.RootElement.GetProperty("properties");
        Assert.Equal("strict", props.GetProperty("mode").GetProperty("const").GetString());
        Assert.Equal(
            ["opt1", "opt2"],
            props.GetProperty("options").GetProperty("enum")
                .EnumerateArray().Select(static e => e.GetString() ?? string.Empty).ToArray());
    }
}
