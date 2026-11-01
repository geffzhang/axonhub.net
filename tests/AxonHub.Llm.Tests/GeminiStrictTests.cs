using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Gemini;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for Gemini strict tool handling
/// (llm/transformer/gemini/strict_test.go): strict function tools force the
/// VALIDATED/ANY/NONE calling modes, non-strict tool sets keep the default
/// mode, and required function names survive a ToolChoice round trip.
/// </summary>
public sealed class GeminiStrictTests
{
    private static JsonElement WeatherParameters()
        => JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;

    // ------------------------------------------------------------ TestStrictFunctionToolChoice (strict_test.go:14-52)

    public static TheoryData<ToolChoice?, string, List<string>?> StrictChoices()
    {
        var data = new TheoryData<ToolChoice?, string, List<string>?>();
        data.Add(null, "VALIDATED", null);
        data.Add(new ToolChoice { ToolChoiceMode = "auto" }, "VALIDATED", null);
        data.Add(new ToolChoice { ToolChoiceMode = "required" }, "ANY", null);
        data.Add(new ToolChoice { ToolChoiceMode = "any" }, "ANY", null);
        data.Add(new ToolChoice { ToolChoiceMode = "none" }, "NONE", null);
        data.Add(new ToolChoice
        {
            NamedToolChoice = new NamedToolChoice
            {
                Type = "function",
                Function = new ToolFunction { Name = "weather" },
            },
        }, "ANY", new List<string> { "weather" });
        return data;
    }

    private static readonly string[] GeminiModels =
    [
        "gemini-2.5-flash",
        "gemini-3.1-pro-preview",
        "custom-model-alias",
    ];

    [Theory]
    [MemberData(nameof(StrictChoices))]
    public void StrictFunctionToolChoice(ToolChoice? choice, string mode, List<string>? names)
    {
        foreach (var model in GeminiModels)
        {
            // Given a mixed request: strict validation is request-wide in Gemini.
            var req = new LlmRequest
            {
                Model = model,
                ToolChoice = choice,
                Tools =
                [
                    new Tool
                    {
                        Type = ToolTypes.Function,
                        Function = new Function { Name = "relaxed", Strict = false },
                    },
                    new Tool
                    {
                        Type = ToolTypes.Function,
                        Function = new Function
                        {
                            Name = "weather",
                            Strict = true,
                            Parameters = WeatherParameters(),
                        },
                    },
                ],
            };

            // When the request is converted for Gemini.
            var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

            // Then optional, required, disabled, and named selection remain distinct.
            Assert.NotNull(result.ToolConfig);
            Assert.NotNull(result.ToolConfig!.FunctionCallingConfig);
            Assert.Equal(mode, result.ToolConfig.FunctionCallingConfig!.Mode);
            Assert.Equal(names, result.ToolConfig.FunctionCallingConfig.AllowedFunctionNames);
            Assert.Equal(2, result.Tools![0].FunctionDeclarations!.Count);
        }
    }

    // ------------------------------------------------------------ TestNonStrictToolsPreserveDefaultMode (strict_test.go:54-79)

    [Fact]
    public void NonStrictToolsPreserveDefaultMode()
    {
        List<Tool>?[] toolSets =
        [
            null,
            [new Tool { Type = ToolTypes.Function, Function = new Function { Name = "weather" } }],
            [
                new Tool
                {
                    Type = ToolTypes.Function,
                    Function = new Function { Name = "weather", Strict = false },
                },
            ],
            [
                new Tool
                {
                    Type = ToolTypes.WebSearch,
                    WebSearch = new WebSearch { Strict = true },
                },
            ],
        ];

        foreach (var tools in toolSets)
        {
            foreach (var explicitAuto in new[] { false, true })
            {
                // Given no strict function tools, including a strict native tool.
                var req = new LlmRequest { Tools = tools };
                if (explicitAuto)
                {
                    req.ToolChoice = new ToolChoice { ToolChoiceMode = "auto" };
                }

                // When converting the request.
                var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

                // Then no schema-enforcing mode is introduced.
                if (explicitAuto)
                {
                    Assert.NotNull(result.ToolConfig);
                    Assert.Equal("AUTO", result.ToolConfig!.FunctionCallingConfig!.Mode);
                }
                else
                {
                    Assert.Null(result.ToolConfig);
                }
            }
        }
    }

    // ------------------------------------------------------------ TestRequiredFunctionNamesSurviveRoundTrip (strict_test.go:81-100)

    [Fact]
    public void RequiredFunctionNamesSurviveRoundTrip()
    {
        List<string>?[] nameSets = [null, ["weather"], ["weather", "forecast"]];

        foreach (var names in nameSets)
        {
            var choice = GeminiConvert.ConvertGeminiFunctionCallingConfigToToolChoice(
                new FunctionCallingConfig
                {
                    Mode = "ANY",
                    AllowedFunctionNames = names,
                });

            Assert.NotNull(choice);
            var body = GoJson.Marshal(choice!);
            var restored = LlmJson.Deserialize<ToolChoice>(body);
            Assert.NotNull(restored);

            foreach (var strict in new[] { false, true })
            {
                var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(new LlmRequest
                {
                    ToolChoice = restored,
                    Tools =
                    [
                        new Tool
                        {
                            Type = ToolTypes.Function,
                            Function = new Function { Name = "weather", Strict = strict },
                        },
                    ],
                });

                Assert.NotNull(result.ToolConfig);
                Assert.Equal("ANY", result.ToolConfig!.FunctionCallingConfig!.Mode);
                Assert.Equal(names, result.ToolConfig.FunctionCallingConfig.AllowedFunctionNames);
            }
        }
    }
}
