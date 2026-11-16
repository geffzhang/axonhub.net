using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Gemini outbound request/response conversions
/// (llm/transformer/gemini/outbound_convert_test.go, all test functions
/// except TestConvertLLMToGeminiResponse_Testdata which belongs to the
/// inbound slice, and TestExtractSafetySettingsFromMetadata which is already
/// locked in GeminiMetadataExtractionTests.cs).
/// </summary>
public sealed class GeminiOutboundConvertTests
{
    private static JsonElement Json(string raw) => LlmJson.Deserialize<JsonElement>(raw);

    private static GroundingMetadata? Grounding(Choice choice)
    {
        if (choice.TransformerMetadata is not { } metadata
            || !metadata.TryGetValue(GeminiOutboundConvert.TransformerMetadataKeyGroundingMetadata, out var raw))
        {
            return null;
        }

        return LlmJson.Deserialize<GroundingMetadata>(raw.GetRawText());
    }

    private static string GeminiTestData(string name)
        => Path.Combine(AppContext.BaseDirectory, "testdata", "gemini", name);

    // ------------------------------------------------------------------
    // TestConvertLLMToGeminiRequest_Basic (outbound_convert_test.go:22-537)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmToGeminiRequest_SimpleTextRequest_SetsMaxOutputTokensAndContent()
    {
        var req = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            MaxTokens = 1024,
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello, Gemini!" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.Equal(1024, result.GenerationConfig.MaxOutputTokens);
        Assert.Single(result.Contents!);
        Assert.Equal("user", result.Contents![0].Role);
        Assert.Single(result.Contents![0].Parts!);
        Assert.Equal("Hello, Gemini!", result.Contents![0].Parts![0].Text);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_SystemMessage_BecomesSystemInstruction()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "system",
                    Content = new MessageContent { Content = "You are a helpful assistant." },
                },
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello!" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.SystemInstruction);
        Assert.Single(result.SystemInstruction.Parts!);
        Assert.Equal("You are a helpful assistant.", result.SystemInstruction.Parts![0].Text);
        Assert.Single(result.Contents!);
        Assert.Equal("user", result.Contents![0].Role);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_MultipleSystemMessages_MergeIntoSystemInstruction()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "system",
                    Content = new MessageContent { Content = "First instruction." },
                },
                new Message
                {
                    Role = "system",
                    Content = new MessageContent { Content = "Second instruction." },
                },
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello!" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.SystemInstruction);
        Assert.Equal(2, result.SystemInstruction.Parts!.Count);
        Assert.Equal("First instruction.", result.SystemInstruction.Parts![0].Text);
        Assert.Equal("Second instruction.", result.SystemInstruction.Parts![1].Text);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_SystemMessageWithMultipleParts_MergesAllParts()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "system",
                    Content = new MessageContent
                    {
                        MultipleContent =
                        [
                            new MessageContentPart { Type = "text", Text = "Part A" },
                            new MessageContentPart { Type = "text", Text = "Part B" },
                            new MessageContentPart { Type = "text", Text = "Part C" },
                        ],
                    },
                },
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello!" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.SystemInstruction);
        Assert.Equal(3, result.SystemInstruction.Parts!.Count);
        Assert.Equal("Part A", result.SystemInstruction.Parts![0].Text);
        Assert.Equal("Part B", result.SystemInstruction.Parts![1].Text);
        Assert.Equal("Part C", result.SystemInstruction.Parts![2].Text);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_GenerationConfig_MapsSamplingAndStop()
    {
        var req = new LlmRequest
        {
            MaxTokens = 2048,
            Temperature = 0.7,
            TopP = 0.9,
            PresencePenalty = 0.5,
            FrequencyPenalty = 0.3,
            Seed = 42,
            Stop = new Stop { Multiple = ["END", "STOP"] },
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.Equal(2048, result.GenerationConfig.MaxOutputTokens);
        Assert.InRange(result.GenerationConfig.Temperature!.Value, 0.69, 0.71);
        Assert.InRange(result.GenerationConfig.TopP!.Value, 0.89, 0.91);
        Assert.InRange(result.GenerationConfig.PresencePenalty!.Value, 0.49, 0.51);
        Assert.InRange(result.GenerationConfig.FrequencyPenalty!.Value, 0.29, 0.31);
        Assert.Equal(42, result.GenerationConfig.Seed);
        Assert.Equal(new List<string> { "END", "STOP" }, result.GenerationConfig.StopSequences);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_SingleStopSequence_WrapsInList()
    {
        var req = new LlmRequest
        {
            Stop = new Stop { Single = "END" },
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.Equal(new List<string> { "END" }, result.GenerationConfig.StopSequences);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_MaxCompletionTokens_FallsBackToMaxOutputTokens()
    {
        var req = new LlmRequest
        {
            MaxCompletionTokens = 512,
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.Equal(512, result.GenerationConfig.MaxOutputTokens);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_AudioInput_BecomesInlineDataWithMimeFromFormat()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent
                    {
                        MultipleContent =
                        [
                            new MessageContentPart
                            {
                                Type = "input_audio",
                                InputAudio = new InputAudio
                                {
                                    Format = "wav",
                                    Data = "UklGRiQAAABXQVZF",
                                },
                            },
                        ],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Single(result.Contents!);
        Assert.Single(result.Contents![0].Parts!);
        Assert.NotNull(result.Contents![0].Parts![0].InlineData);
        Assert.Equal("audio/wav", result.Contents![0].Parts![0].InlineData!.MIMEType);
        Assert.Equal("UklGRiQAAABXQVZF", result.Contents![0].Parts![0].InlineData!.Data);
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "high")] // "xhigh" (from Anthropic "max") maps to Gemini "high"
    public void ConvertLlmToGeminiRequest_ReasoningEffort_SetsThinkingLevel(string effort, string expectedLevel)
    {
        var req = new LlmRequest
        {
            ReasoningEffort = effort,
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Question" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ThinkingConfig);
        Assert.True(result.GenerationConfig.ThinkingConfig.IncludeThoughts);
        Assert.Equal(expectedLevel, result.GenerationConfig.ThinkingConfig.ThinkingLevel);
        Assert.Null(result.GenerationConfig.ThinkingConfig.ThinkingBudget);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_ReasoningEffortWithBudget_PreservesBudget()
    {
        var req = new LlmRequest
        {
            ReasoningEffort = "medium",
            ReasoningBudget = 12000,
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Question with custom budget" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ThinkingConfig);
        Assert.True(result.GenerationConfig.ThinkingConfig.IncludeThoughts);
        Assert.Equal(12000, result.GenerationConfig.ThinkingConfig.ThinkingBudget);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_ReasoningEffortWithOversizedBudget_CapsAt24576()
    {
        var req = new LlmRequest
        {
            ReasoningEffort = "high",
            ReasoningBudget = 50000,
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Question with large budget" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ThinkingConfig);
        Assert.True(result.GenerationConfig.ThinkingConfig.IncludeThoughts);
        Assert.Equal(24576, result.GenerationConfig.ThinkingConfig.ThinkingBudget);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_OversizedBudget_CapsAt24576()
    {
        var req = new LlmRequest
        {
            ReasoningBudget = 50000,
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ThinkingConfig);
        Assert.Equal(24576, result.GenerationConfig.ThinkingConfig.ThinkingBudget);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_BudgetTakesPriorityOverEffort()
    {
        var req = new LlmRequest
        {
            ReasoningEffort = "low", // Would map to 1024
            ReasoningBudget = 20000, // Budget takes priority
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Complex task" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ThinkingConfig);
        Assert.Equal(20000, result.GenerationConfig.ThinkingConfig.ThinkingBudget);
        Assert.True(result.GenerationConfig.ThinkingConfig.IncludeThoughts);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_BudgetOnly_SetsThinkingBudget()
    {
        var req = new LlmRequest
        {
            ReasoningBudget = 15000,
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Task" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ThinkingConfig);
        Assert.Equal(15000, result.GenerationConfig.ThinkingConfig.ThinkingBudget);
        Assert.True(result.GenerationConfig.ThinkingConfig.IncludeThoughts);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_ExtraBodyThinkingLevel_TakesPriority()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
            ExtraBody = Json(
                """{"google":{"thinking_config":{"thinking_level":"high","thinking_budget":1024,"include_thoughts":true}}}"""),
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ThinkingConfig);
        // Level takes priority; budget is not set when level is present.
        Assert.Equal("high", result.GenerationConfig.ThinkingConfig.ThinkingLevel);
        Assert.Null(result.GenerationConfig.ThinkingConfig.ThinkingBudget);
        Assert.True(result.GenerationConfig.ThinkingConfig.IncludeThoughts);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_ExtraBodyMinimalLevel_MapsToLow()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
            ExtraBody = Json("""{"google":{"thinking_config":{"thinking_level":"minimal","include_thoughts":true}}}"""),
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ThinkingConfig);
        Assert.Equal("low", result.GenerationConfig.ThinkingConfig.ThinkingLevel);
        Assert.Null(result.GenerationConfig.ThinkingConfig.ThinkingBudget);
        Assert.True(result.GenerationConfig.ThinkingConfig.IncludeThoughts);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_ExtraBodyStringBudget_ConvertsToLevel()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
            ExtraBody = Json("""{"google":{"thinking_config":{"thinking_budget":"high","include_thoughts":true}}}"""),
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ThinkingConfig);
        Assert.Equal("high", result.GenerationConfig.ThinkingConfig.ThinkingLevel);
        Assert.Null(result.GenerationConfig.ThinkingConfig.ThinkingBudget);
        Assert.True(result.GenerationConfig.ThinkingConfig.IncludeThoughts);
    }

    // ------------------------------------------------------------------
    // TestConvertLLMToGeminiRequest_VideoURL (outbound_convert_test.go:539)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmToGeminiRequest_VideoUrl_UsesFileDataWithDeclaredMime()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent
                    {
                        MultipleContent =
                        [
                            new MessageContentPart
                            {
                                Type = "video_url",
                                VideoUrl = new VideoURL
                                {
                                    URL = "https://example.com/example.mp4",
                                    MIMEType = "video/quicktime",
                                },
                            },
                        ],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Single(result.Contents!);
        Assert.Single(result.Contents![0].Parts!);
        Assert.NotNull(result.Contents![0].Parts![0].FileData);
        Assert.Equal("https://example.com/example.mp4", result.Contents![0].Parts![0].FileData!.FileURI);
        Assert.Equal("video/quicktime", result.Contents![0].Parts![0].FileData!.MIMEType!);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_VideoUrlWithoutKnownMime_LeavesMimeEmpty()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent
                    {
                        MultipleContent =
                        [
                            new MessageContentPart
                            {
                                Type = "video_url",
                                VideoUrl = new VideoURL { URL = "https://assets.example.com/video/input" },
                            },
                        ],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Single(result.Contents!);
        Assert.Single(result.Contents![0].Parts!);
        Assert.NotNull(result.Contents![0].Parts![0].FileData);
        Assert.Empty(result.Contents![0].Parts![0].FileData!.MIMEType!);
    }

    // ------------------------------------------------------------------
    // TestConvertLLMToGeminiRequest_RemoteAudio (outbound_convert_test.go:585)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmToGeminiRequest_RemoteAudio_UsesFileData()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent
                    {
                        MultipleContent =
                        [
                            new MessageContentPart
                            {
                                Type = "input_audio",
                                InputAudio = new InputAudio
                                {
                                    Url = "https://assets.example.com/audio/input.mp3",
                                    MimeType = "audio/mpeg",
                                },
                            },
                        ],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Single(result.Contents!);
        Assert.Single(result.Contents![0].Parts!);
        Assert.NotNull(result.Contents![0].Parts![0].FileData);
        Assert.Equal("https://assets.example.com/audio/input.mp3", result.Contents![0].Parts![0].FileData!.FileURI);
        Assert.Equal("audio/mpeg", result.Contents![0].Parts![0].FileData!.MIMEType!);
    }

    // ------------------------------------------------------------------
    // TestConvertLLMToGeminiRequest_ResponseFormat (outbound_convert_test.go:607-766)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmToGeminiRequest_JsonSchema_SetsResponseJsonSchemaAndMimeType()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Generate JSON" },
                },
            ],
            ResponseFormat = new ResponseFormat
            {
                Type = "json_schema",
                JsonSchema = Json(
                    """{"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer"}}}"""),
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ResponseJsonSchema);
        Assert.Null(result.GenerationConfig.ResponseSchema);
        Assert.Equal("application/json", result.GenerationConfig.ResponseMIMEType);
        Assert.Contains("name", result.GenerationConfig.ResponseJsonSchema.Value.GetRawText());
        Assert.Contains("age", result.GenerationConfig.ResponseJsonSchema.Value.GetRawText());
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_JsonSchemaWithOpenAiWrapper_ExtractsInnerSchema()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Generate JSON" },
                },
            ],
            ResponseFormat = new ResponseFormat
            {
                Type = "json_schema",
                JsonSchema = Json(
                    """{"name":"ping_response","schema":{"additionalProperties":false,"properties":{"pong":{"type":"boolean"}},"required":["pong"],"type":"object"}}"""),
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ResponseJsonSchema);
        Assert.Null(result.GenerationConfig.ResponseSchema);
        Assert.Equal("application/json", result.GenerationConfig.ResponseMIMEType);
        var schema = result.GenerationConfig.ResponseJsonSchema.Value.GetRawText();
        // The inner schema fields, not the wrapper "name"/"schema" keys.
        Assert.Contains("\"pong\"", schema);
        Assert.Contains("\"boolean\"", schema);
        Assert.DoesNotContain("\"ping_response\"", schema);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_JsonObject_SetsMimeTypeOnly()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Generate JSON" },
                },
            ],
            ResponseFormat = new ResponseFormat { Type = "json_object" },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.Equal("application/json", result.GenerationConfig.ResponseMIMEType);
        Assert.Null(result.GenerationConfig.ResponseSchema);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_TextFormat_DoesNotSetResponseSchema()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello" },
                },
            ],
            ResponseFormat = new ResponseFormat { Type = "text" },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        // Go asserts only when GenerationConfig is non-nil; with no other
        // config fields it stays nil in both implementations.
        Assert.Null(result.GenerationConfig);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_NoResponseFormat_NoGenerationConfig()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Null(result.GenerationConfig);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_JsonSchemaWithoutSchema_NoGenerationConfig()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Generate JSON" },
                },
            ],
            ResponseFormat = new ResponseFormat { Type = "json_schema" },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        // Type is json_schema but JSONSchema is absent: nothing is set.
        Assert.Null(result.GenerationConfig);
    }

    // ------------------------------------------------------------------
    // TestConvertLLMToGeminiRequest_Tools (outbound_convert_test.go:768-1129)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmToGeminiRequest_SingleFunctionTool_DeclaresIt()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "What's the weather?" },
                },
            ],
            Tools =
            [
                new Tool
                {
                    Type = "function",
                    Function = new Function
                    {
                        Name = "get_weather",
                        Description = "Get weather information",
                        Parameters = Json("""{"type":"object","properties":{"location":{"type":"string"}}}"""),
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Single(result.Tools!);
        Assert.Single(result.Tools![0].FunctionDeclarations!);
        Assert.Equal("get_weather", result.Tools![0].FunctionDeclarations![0].Name);
        Assert.Equal("Get weather information", result.Tools![0].FunctionDeclarations![0].Description);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_MultipleFunctionTools_SingleToolWithDeclarations()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Help me" },
                },
            ],
            Tools =
            [
                new Tool
                {
                    Type = "function",
                    Function = new Function { Name = "tool1", Description = "First tool" },
                },
                new Tool
                {
                    Type = "function",
                    Function = new Function { Name = "tool2", Description = "Second tool" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Single(result.Tools!);
        Assert.Equal(2, result.Tools![0].FunctionDeclarations!.Count);
        Assert.Equal("tool1", result.Tools![0].FunctionDeclarations![0].Name);
        Assert.Equal("tool2", result.Tools![0].FunctionDeclarations![1].Name);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_GoogleSearchTool_BecomesGoogleSearch()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Search the web" },
                },
            ],
            Tools =
            [
                new Tool
                {
                    Type = ToolTypes.GoogleSearch,
                    Google = new GoogleTools { Search = new GoogleSearch() },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Single(result.Tools!);
        Assert.NotNull(result.Tools![0].GoogleSearch);
        Assert.Null(result.Tools![0].FunctionDeclarations!);
        Assert.Null(result.Tools![0].CodeExecution);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_WebSearchTool_MapsToGoogleSearch()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Search the web" },
                },
            ],
            Tools =
            [
                new Tool
                {
                    Type = ToolTypes.WebSearch,
                    WebSearch = new WebSearch(),
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Single(result.Tools!);
        Assert.NotNull(result.Tools![0].GoogleSearch);
        Assert.Null(result.Tools![0].FunctionDeclarations!);
        Assert.Null(result.Tools![0].CodeExecution);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_CodeExecutionTool_BecomesCodeExecution()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Run some code" },
                },
            ],
            Tools =
            [
                new Tool
                {
                    Type = ToolTypes.GoogleCodeExecution,
                    Google = new GoogleTools { CodeExecution = new GoogleCodeExecution() },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Single(result.Tools!);
        Assert.NotNull(result.Tools![0].CodeExecution);
        Assert.Null(result.Tools![0].FunctionDeclarations!);
        Assert.Null(result.Tools![0].GoogleSearch);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_UrlContextTool_BecomesUrlContext()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Fetch URL content" },
                },
            ],
            Tools =
            [
                new Tool
                {
                    Type = ToolTypes.GoogleUrlContext,
                    Google = new GoogleTools { UrlContext = new GoogleUrlContext() },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Single(result.Tools!);
        Assert.NotNull(result.Tools![0].UrlContext);
        Assert.Null(result.Tools![0].FunctionDeclarations!);
        Assert.Null(result.Tools![0].GoogleSearch);
        Assert.Null(result.Tools![0].CodeExecution);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_MixedTools_PreservesOrder()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Use all tools" },
                },
            ],
            Tools =
            [
                new Tool
                {
                    Type = "function",
                    Function = new Function
                    {
                        Name = "get_weather",
                        Description = "Get weather info",
                        Parameters = Json("""{"type":"object"}"""),
                    },
                },
                new Tool
                {
                    Type = ToolTypes.GoogleSearch,
                    Google = new GoogleTools { Search = new GoogleSearch() },
                },
                new Tool
                {
                    Type = ToolTypes.GoogleCodeExecution,
                    Google = new GoogleTools { CodeExecution = new GoogleCodeExecution() },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Equal(3, result.Tools!.Count);
        Assert.NotNull(result.Tools![0].FunctionDeclarations!);
        Assert.Single(result.Tools![0].FunctionDeclarations!);
        Assert.Equal("get_weather", result.Tools![0].FunctionDeclarations![0].Name);
        Assert.NotNull(result.Tools![1].GoogleSearch);
        Assert.NotNull(result.Tools![2].CodeExecution);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_ToolChoiceAuto_SetsAutoMode()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
            ToolChoice = new ToolChoice { ToolChoiceMode = "auto" },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.ToolConfig);
        Assert.NotNull(result.ToolConfig.FunctionCallingConfig);
        Assert.Equal("AUTO", result.ToolConfig.FunctionCallingConfig.Mode);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_StrictFunctionTool_SetsValidatedMode()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
            Tools =
            [
                new Tool
                {
                    Type = "function",
                    Function = new Function
                    {
                        Name = "get_weather",
                        Parameters = Json("""{"type":"object","properties":{}}"""),
                        Strict = true,
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.ToolConfig);
        Assert.NotNull(result.ToolConfig.FunctionCallingConfig);
        Assert.Equal("VALIDATED", result.ToolConfig.FunctionCallingConfig.Mode);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_ToolChoiceNone_SetsNoneMode()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
            ToolChoice = new ToolChoice { ToolChoiceMode = "none" },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.ToolConfig);
        Assert.NotNull(result.ToolConfig.FunctionCallingConfig);
        Assert.Equal("NONE", result.ToolConfig.FunctionCallingConfig.Mode);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_ToolChoiceRequired_SetsAnyMode()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
            ToolChoice = new ToolChoice { ToolChoiceMode = "required" },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.ToolConfig);
        Assert.NotNull(result.ToolConfig.FunctionCallingConfig);
        Assert.Equal("ANY", result.ToolConfig.FunctionCallingConfig.Mode);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_NamedToolChoice_SetsAnyWithAllowedName()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
            ToolChoice = new ToolChoice
            {
                NamedToolChoice = new NamedToolChoice
                {
                    Type = "function",
                    Function = new ToolFunction { Name = "specific_function" },
                },
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.ToolConfig);
        Assert.NotNull(result.ToolConfig.FunctionCallingConfig);
        Assert.Equal("ANY", result.ToolConfig.FunctionCallingConfig.Mode);
        Assert.Equal(new List<string> { "specific_function" }, result.ToolConfig.FunctionCallingConfig.AllowedFunctionNames);
    }

    // ------------------------------------------------------------------
    // TestConvertLLMMessageToGeminiContent (outbound_convert_test.go:1131-1294)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmMessageToGeminiContent_SimpleTextMessage_KeepsRoleAndText()
    {
        var msg = new Message
        {
            Role = "user",
            Content = new MessageContent { Content = "Hello" },
        };

        var result = GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg);

        Assert.NotNull(result);
        Assert.Equal("user", result.Role);
        Assert.Single(result.Parts!);
        Assert.Equal("Hello", result.Parts![0].Text);
    }

    [Fact]
    public void ConvertLlmMessageToGeminiContent_AssistantRole_MapsToModel()
    {
        var msg = new Message
        {
            Role = "assistant",
            Content = new MessageContent { Content = "Response" },
        };

        var result = GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg);

        Assert.NotNull(result);
        Assert.Equal("model", result.Role);
    }

    [Fact]
    public void ConvertLlmMessageToGeminiContent_ReasoningContent_BecomesThoughtPartFirst()
    {
        var msg = new Message
        {
            Role = "assistant",
            ReasoningContent = "Let me think...",
            Content = new MessageContent { Content = "The answer is 42" },
        };

        var result = GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg);

        Assert.NotNull(result);
        Assert.Equal(2, result.Parts!.Count);
        Assert.True(result.Parts![0].Thought);
        Assert.Equal("Let me think...", result.Parts![0].Text);
        Assert.False(result.Parts![1].Thought);
        Assert.Equal("The answer is 42", result.Parts![1].Text);
    }

    [Fact]
    public void ConvertLlmMessageToGeminiContent_MultipleTextParts_KeepOrder()
    {
        var msg = new Message
        {
            Role = "user",
            Content = new MessageContent
            {
                MultipleContent =
                [
                    new MessageContentPart { Type = "text", Text = "First part" },
                    new MessageContentPart { Type = "text", Text = "Second part" },
                ],
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg);

        Assert.NotNull(result);
        Assert.Equal(2, result.Parts!.Count);
        Assert.Equal("First part", result.Parts![0].Text);
        Assert.Equal("Second part", result.Parts![1].Text);
    }

    [Fact]
    public void ConvertLlmMessageToGeminiContent_ImageDataUrl_BecomesInlineData()
    {
        var msg = new Message
        {
            Role = "user",
            Content = new MessageContent
            {
                MultipleContent =
                [
                    new MessageContentPart
                    {
                        Type = "image_url",
                        ImageUrl = new ImageUrl { Url = "data:image/jpeg;base64,/9j/4AAQSkZJRg==" },
                    },
                ],
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg);

        Assert.NotNull(result);
        Assert.Single(result.Parts!);
        Assert.NotNull(result.Parts![0].InlineData);
        Assert.Equal("image/jpeg", result.Parts![0].InlineData!.MIMEType);
        Assert.Equal("/9j/4AAQSkZJRg==", result.Parts![0].InlineData!.Data);
    }

    [Fact]
    public void ConvertLlmMessageToGeminiContent_ImageRegularUrl_BecomesFileData()
    {
        var msg = new Message
        {
            Role = "user",
            Content = new MessageContent
            {
                MultipleContent =
                [
                    new MessageContentPart
                    {
                        Type = "image_url",
                        ImageUrl = new ImageUrl { Url = "https://example.com/image.jpg" },
                    },
                ],
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg);

        Assert.NotNull(result);
        Assert.Single(result.Parts!);
        Assert.NotNull(result.Parts![0].FileData);
        Assert.Equal("https://example.com/image.jpg", result.Parts![0].FileData!.FileURI);
    }

    [Fact]
    public void ConvertLlmMessageToGeminiContent_ToolCalls_BecomeFunctionCallParts()
    {
        var msg = new Message
        {
            Role = "assistant",
            Content = new MessageContent { Content = "I'll check the weather" },
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "call_001",
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = "get_weather",
                        Arguments = """{"location":"NYC"}""",
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg);

        Assert.NotNull(result);
        Assert.Equal(2, result.Parts!.Count);
        Assert.Equal("I'll check the weather", result.Parts![0].Text);
        Assert.NotNull(result.Parts![1].FunctionCall!);
        Assert.Equal("call_001", result.Parts![1].FunctionCall!.Id);
        Assert.Equal("get_weather", result.Parts![1].FunctionCall!.Name);
        Assert.Equal("NYC", result.Parts![1].FunctionCall!.Args!["location"].GetString());
    }

    [Fact]
    public void ConvertLlmMessageToGeminiContent_EmptyMessage_ReturnsNull()
    {
        var msg = new Message
        {
            Role = "user",
            Content = new MessageContent(),
        };

        Assert.Null(GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg));
    }

    // ------------------------------------------------------------------
    // TestConvertLLMToolMessageToGeminiContent (outbound_convert_test.go:1296-1456)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmToolResultToGeminiContent_JsonContent_BecomesResponseObject()
    {
        var msg = new Message
        {
            Role = "tool",
            ToolCallId = "call_123",
            Content = new MessageContent { Content = """{"temperature": 72, "unit": "F"}""" },
        };

        var result = GeminiOutboundConvert.ConvertLlmToolResultToGeminiContent(msg, []);

        Assert.Equal("user", result.Role);
        Assert.Single(result.Parts!);
        Assert.NotNull(result.Parts![0].FunctionResponse!);
        Assert.Equal("call_123", result.Parts![0].FunctionResponse!.Id);
        Assert.Equal(72, result.Parts![0].FunctionResponse!.Response!["temperature"].GetInt32());
    }

    [Fact]
    public void ConvertLlmToolResultToGeminiContent_PlainText_WrapsInResultKey()
    {
        var msg = new Message
        {
            Role = "tool",
            ToolCallId = "call_456",
            Content = new MessageContent { Content = "Plain text result" },
        };

        var result = GeminiOutboundConvert.ConvertLlmToolResultToGeminiContent(msg, []);

        Assert.Equal("user", result.Role);
        Assert.Single(result.Parts!);
        Assert.NotNull(result.Parts![0].FunctionResponse!);
        Assert.Equal("call_456", result.Parts![0].FunctionResponse!.Id);
        Assert.Equal("Plain text result", result.Parts![0].FunctionResponse!.Response!["result"].GetString());
    }

    [Fact]
    public void ConvertLlmToolResultToGeminiContent_MissingToolCallId_LeavesIdEmpty()
    {
        var msg = new Message
        {
            Role = "tool",
            Content = new MessageContent { Content = """{"result": "success"}""" },
        };

        var result = GeminiOutboundConvert.ConvertLlmToolResultToGeminiContent(msg, []);

        Assert.Equal("user", result.Role);
        Assert.Single(result.Parts!);
        Assert.NotNull(result.Parts![0].FunctionResponse!);
        Assert.Equal("", result.Parts![0].FunctionResponse!.Id);
    }

    [Fact]
    public void ConvertLlmToolResultToGeminiContent_ToolCallName_BecomesName()
    {
        var msg = new Message
        {
            Role = "tool",
            ToolCallId = "call_789",
            ToolCallName = "get_weather",
            Content = new MessageContent { Content = """{"temp": 25}""" },
        };

        var result = GeminiOutboundConvert.ConvertLlmToolResultToGeminiContent(msg, []);

        Assert.Equal("user", result.Role);
        Assert.Single(result.Parts!);
        Assert.NotNull(result.Parts![0].FunctionResponse!);
        Assert.Equal("call_789", result.Parts![0].FunctionResponse!.Id);
        Assert.Equal("get_weather", result.Parts![0].FunctionResponse!.Name);
    }

    [Fact]
    public void ConvertLlmToolResultToGeminiContent_MissingName_FindsByToolCallId()
    {
        var msg = new Message
        {
            Role = "tool",
            ToolCallId = "call_abc",
            Content = new MessageContent { Content = """{"result": "found"}""" },
        };
        var contents = new List<Content>
        {
            new()
            {
                Role = "model",
                Parts =
                [
                    new Part
                    {
                        FunctionCall = new Transformer.Gemini.FunctionCall
                        {
                            Id = "call_abc",
                            Name = "search_function",
                            Args = new Dictionary<string, JsonElement>
                            {
                                ["query"] = JsonSerializer.SerializeToElement("test"),
                            },
                        },
                    },
                ],
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToolResultToGeminiContent(msg, contents);

        Assert.Equal("user", result.Role);
        Assert.Single(result.Parts!);
        Assert.NotNull(result.Parts![0].FunctionResponse!);
        Assert.Equal("call_abc", result.Parts![0].FunctionResponse!.Id);
        Assert.Equal("search_function", result.Parts![0].FunctionResponse!.Name);
    }

    [Fact]
    public void ConvertLlmToolResultToGeminiContent_ToolCallNameWinsOverLookup()
    {
        var msg = new Message
        {
            Role = "tool",
            ToolCallId = "call_xyz",
            ToolCallName = "explicit_name",
            Content = new MessageContent { Content = """{"data": "value"}""" },
        };
        var contents = new List<Content>
        {
            new()
            {
                Role = "model",
                Parts =
                [
                    new Part
                    {
                        FunctionCall = new Transformer.Gemini.FunctionCall
                        {
                            Id = "call_xyz",
                            Name = "lookup_name",
                            Args = new Dictionary<string, JsonElement>(),
                        },
                    },
                ],
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToolResultToGeminiContent(msg, contents);

        Assert.Equal("user", result.Role);
        Assert.Single(result.Parts!);
        Assert.NotNull(result.Parts![0].FunctionResponse!);
        Assert.Equal("call_xyz", result.Parts![0].FunctionResponse!.Id);
        Assert.Equal("explicit_name", result.Parts![0].FunctionResponse!.Name);
    }

    // ------------------------------------------------------------------
    // TestConvertLLMToolResultToGeminiContent_VertexAI (outbound_convert_test.go:1458-1500)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmToolResultToGeminiContent_VertexAi_KeepsIdAndMarshalsIt()
    {
        var msg = new Message
        {
            Role = "tool",
            ToolCallId = "call_123",
            ToolCallName = "get_weather",
            Content = new MessageContent { Content = """{"temperature": 22}""" },
        };
        var contents = new List<Content>
        {
            new()
            {
                Role = "model",
                Parts =
                [
                    new Part
                    {
                        FunctionCall = new Transformer.Gemini.FunctionCall
                        {
                            Id = "call_123",
                            Name = "get_weather",
                            Args = new Dictionary<string, JsonElement>
                            {
                                ["city"] = JsonSerializer.SerializeToElement("Paris"),
                            },
                        },
                    },
                ],
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToolResultToGeminiContent(msg, contents);

        Assert.NotNull(result);
        Assert.Single(result.Parts!);
        Assert.NotNull(result.Parts![0].FunctionResponse!);
        Assert.Equal("call_123", result.Parts![0].FunctionResponse!.Id);
        Assert.Equal("get_weather", result.Parts![0].FunctionResponse!.Name);

        // json.Marshal of the FunctionResponse: ID present when not empty.
        var json = LlmJson.Serialize(result.Parts![0].FunctionResponse!);
        Assert.Contains("\"id\":", json);
        Assert.Contains("get_weather", json);
    }

    // ------------------------------------------------------------------
    // TestConvertLLMToGeminiRequest_MultipleToolMessages (outbound_convert_test.go:1502-1564)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmToGeminiRequest_ConsecutiveToolMessages_GroupIntoOneContent()
    {
        var req = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "What's the weather and news?" },
                },
                new Message
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Id = "call_weather",
                            Type = "function",
                            Function = new FunctionCall
                            {
                                Name = "get_weather",
                                Arguments = """{"city": "Paris"}""",
                            },
                        },
                        new ToolCall
                        {
                            Id = "call_news",
                            Type = "function",
                            Function = new FunctionCall
                            {
                                Name = "get_news",
                                Arguments = """{"category": "tech"}""",
                            },
                        },
                    ],
                },
                new Message
                {
                    Role = "tool",
                    ToolCallId = "call_weather",
                    ToolCallName = "get_weather",
                    Content = new MessageContent { Content = """{"temperature": 22}""" },
                },
                new Message
                {
                    Role = "tool",
                    ToolCallId = "call_news",
                    ToolCallName = "get_news",
                    Content = new MessageContent { Content = """{"headlines": ["AI breakthrough"]}""" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        // user message, assistant with function calls, user with grouped responses.
        Assert.Equal(3, result.Contents!.Count);

        var last = result.Contents![^1];
        Assert.Equal("user", last.Role);
        Assert.Equal(2, last.Parts!.Count);
        Assert.NotNull(last.Parts![0].FunctionResponse!);
        Assert.NotNull(last.Parts![1].FunctionResponse!);
        Assert.Equal("get_weather", last.Parts![0].FunctionResponse!.Name);
        Assert.Equal("get_news", last.Parts![1].FunctionResponse!.Name);
    }

    // ------------------------------------------------------------------
    // TestConvertGeminiToLLMResponse_Basic (outbound_convert_test.go:1570-1754)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertGeminiToLlmResponse_SimpleResponse_MapsFields()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_123",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Hello!" }],
                    },
                    FinishReason = "STOP",
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Equal("resp_123", result.Id);
        Assert.Equal("chat.completion", result.Object);
        Assert.Equal("gemini-2.5-flash", result.Model);
        Assert.Single(result.Choices!);
        Assert.Equal("assistant", result.Choices![0].Message!.Role);
        Assert.Equal("Hello!", result.Choices![0].Message!.Content!.Content);
        Assert.Equal("stop", result.Choices![0].FinishReason);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_MissingId_GeneratesChatCmplId()
    {
        var resp = new GenerateContentResponse
        {
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Response" }],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.NotEmpty(result.Id);
        Assert.StartsWith("chatcmpl-", result.Id);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_ThinkingContent_SplitsReasoningAndText()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_think",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part { Text = "Let me think...", Thought = true },
                            new Part { Text = "The answer is 42" },
                        ],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.NotNull(result.Choices![0].Message!.ReasoningContent);
        Assert.Equal("Let me think...", result.Choices![0].Message!.ReasoningContent);
        Assert.Equal("The answer is 42", result.Choices![0].Message!.Content!.Content);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_FunctionCall_BecomesToolCall()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_func",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part { Text = "I'll check the weather" },
                            new Part
                            {
                                FunctionCall = new Transformer.Gemini.FunctionCall
                                {
                                    Id = "call_001",
                                    Name = "get_weather",
                                    Args = new Dictionary<string, JsonElement>
                                    {
                                        ["location"] = JsonSerializer.SerializeToElement("NYC"),
                                    },
                                },
                            },
                        ],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Equal("I'll check the weather", result.Choices![0].Message!.Content!.Content);
        Assert.Single(result.Choices![0].Message!.ToolCalls!);
        Assert.Equal("call_001", result.Choices![0].Message!.ToolCalls![0].Id);
        Assert.Equal("function", result.Choices![0].Message!.ToolCalls![0].Type);
        Assert.Equal("get_weather", result.Choices![0].Message!.ToolCalls![0].Function.Name);
        Assert.Contains("NYC", result.Choices![0].Message!.ToolCalls![0].Function.Arguments);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_UsageMetadata_AddsThoughtsToCompletion()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_usage",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Response" }],
                    },
                },
            ],
            UsageMetadata = new UsageMetadata
            {
                PromptTokenCount = 100,
                CandidatesTokenCount = 50,
                TotalTokenCount = 150,
                CachedContentTokenCount = 20,
                ThoughtsTokenCount = 30,
            },
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.NotNull(result.Usage);
        Assert.Equal(100, result.Usage.PromptTokens);
        Assert.Equal(80, result.Usage.CompletionTokens); // 50 + 30 thoughts
        Assert.Equal(150, result.Usage.TotalTokens);
        Assert.NotNull(result.Usage.PromptTokensDetails);
        Assert.Equal(20, result.Usage.PromptTokensDetails.CachedTokens);
        Assert.NotNull(result.Usage.CompletionTokensDetails);
        Assert.Equal(30, result.Usage.CompletionTokensDetails.ReasoningTokens);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_MultipleTextParts_JoinWithoutSeparator()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_multi",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part { Text = "First " },
                            new Part { Text = "Second " },
                            new Part { Text = "Third" },
                        ],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Equal("First Second Third", result.Choices![0].Message!.Content!.Content);
    }

    // ------------------------------------------------------------------
    // TestConvertGeminiToLLMResponse_ThoughtSignature (outbound_convert_test.go:1756-1946)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertGeminiToLlmResponse_FunctionCallThoughtSignature_SetsBoth()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_sig",
            ModelVersion = "gemini-3-pro",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part
                            {
                                FunctionCall = new Transformer.Gemini.FunctionCall
                                {
                                    Id = "call_001",
                                    Name = "check_flight",
                                    Args = new Dictionary<string, JsonElement>
                                    {
                                        ["flight"] = JsonSerializer.SerializeToElement("AA100"),
                                    },
                                },
                                ThoughtSignature = "signature_A",
                            },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Single(result.Choices!);
        Assert.NotNull(result.Choices![0].Message);
        Assert.NotNull(result.Choices![0].Message!.ReasoningSignature);
        Assert.Equal("signature_A", result.Choices![0].Message!.ReasoningSignature);
        Assert.Single(result.Choices![0].Message!.ToolCalls!);
        var tc = result.Choices![0].Message!.ToolCalls![0];
        Assert.Equal("call_001", tc.Id);
        Assert.Equal("check_flight", tc.Function.Name);
        Assert.NotNull(tc.TransformerMetadata);
        Assert.Equal(
            "signature_A",
            tc.TransformerMetadata![GeminiSignatures.TransformerMetadataKeyGoogleThoughtSignature].GetString());
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_ParallelFunctionCalls_OnlyFirstHasSignature()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_parallel",
            ModelVersion = "gemini-3-pro",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part
                            {
                                FunctionCall = new Transformer.Gemini.FunctionCall
                                {
                                    Id = "call_paris",
                                    Name = "get_weather",
                                    Args = new Dictionary<string, JsonElement>
                                    {
                                        ["location"] = JsonSerializer.SerializeToElement("Paris"),
                                    },
                                },
                                ThoughtSignature = "signature_parallel",
                            },
                            new Part
                            {
                                FunctionCall = new Transformer.Gemini.FunctionCall
                                {
                                    Id = "call_london",
                                    Name = "get_weather",
                                    Args = new Dictionary<string, JsonElement>
                                    {
                                        ["location"] = JsonSerializer.SerializeToElement("London"),
                                    },
                                },
                            },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.NotNull(result.Choices![0].Message);
        Assert.NotNull(result.Choices![0].Message!.ReasoningSignature);
        Assert.Equal("signature_parallel", result.Choices![0].Message!.ReasoningSignature);
        Assert.Equal(2, result.Choices![0].Message!.ToolCalls!.Count);

        var tc1 = result.Choices![0].Message!.ToolCalls![0];
        Assert.Equal("call_paris", tc1.Id);
        Assert.NotNull(tc1.TransformerMetadata);
        Assert.Equal(
            "signature_parallel",
            tc1.TransformerMetadata![GeminiSignatures.TransformerMetadataKeyGoogleThoughtSignature].GetString());

        var tc2 = result.Choices![0].Message!.ToolCalls![1];
        Assert.Equal("call_london", tc2.Id);
        Assert.Null(tc2.TransformerMetadata);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_NonGeminiSignature_IsNotDecodedButKept()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_prefixed_sig",
            ModelVersion = "gemini-3-pro",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part
                            {
                                FunctionCall = new Transformer.Gemini.FunctionCall
                                {
                                    Id = "call_prefixed",
                                    Name = "check_weather",
                                    Args = new Dictionary<string, JsonElement>
                                    {
                                        ["city"] = JsonSerializer.SerializeToElement("Tokyo"),
                                    },
                                },
                                ThoughtSignature = "signature_prefixed",
                            },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.NotNull(result.Choices![0].Message);
        Assert.NotNull(result.Choices![0].Message!.ReasoningSignature);
        Assert.Equal("signature_prefixed", result.Choices![0].Message!.ReasoningSignature);
        // "signature_prefixed" is not recognized as a Gemini signature.
        Assert.Null(GeminiSignatures.DecodeGeminiThoughtSignature(result.Choices![0].Message!.ReasoningSignature));
        Assert.Single(result.Choices![0].Message!.ToolCalls!);
        Assert.NotNull(result.Choices![0].Message!.ToolCalls![0].TransformerMetadata);
        Assert.Equal(
            "signature_prefixed",
            result.Choices![0].Message!.ToolCalls![0]
                .TransformerMetadata![GeminiSignatures.TransformerMetadataKeyGoogleThoughtSignature].GetString());
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_FunctionCallWithoutSignature_NoMetadata()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_no_sig",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part
                            {
                                FunctionCall = new Transformer.Gemini.FunctionCall
                                {
                                    Id = "call_002",
                                    Name = "get_weather",
                                    Args = new Dictionary<string, JsonElement>
                                    {
                                        ["location"] = JsonSerializer.SerializeToElement("NYC"),
                                    },
                                },
                            },
                        ],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.NotNull(result.Choices![0].Message);
        Assert.Null(result.Choices![0].Message!.ReasoningSignature);
        Assert.Single(result.Choices![0].Message!.ToolCalls!);
        var tc = result.Choices![0].Message!.ToolCalls![0];
        Assert.Equal("call_002", tc.Id);
        Assert.Null(tc.TransformerMetadata);
    }

    // ------------------------------------------------------------------
    // TestConvertLLMMessageToGeminiContent_ThoughtSignature (outbound_convert_test.go:1948-2113)
    // ------------------------------------------------------------------

    private static readonly string ProtoSig = Convert.ToBase64String([0x0a, 0x02, 0x08, 0x01]);

    [Fact]
    public void ConvertLlmMessageToGeminiContent_ToolCallWithSignature_DecodesToPart()
    {
        var msg = new Message
        {
            Role = "assistant",
            ReasoningSignature = GeminiSignatures.EncodeGeminiThoughtSignature(ProtoSig),
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "call_001",
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = "check_flight",
                        Arguments = """{"flight":"AA100"}""",
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg);

        Assert.NotNull(result);
        Assert.Equal("model", result.Role);
        Assert.Single(result.Parts!);
        Assert.NotNull(result.Parts![0].FunctionCall!);
        Assert.Equal("check_flight", result.Parts![0].FunctionCall!.Name);
        Assert.Equal(ProtoSig, result.Parts![0].ThoughtSignature);
    }

    [Fact]
    public void ConvertLlmMessageToGeminiContent_MultipleToolCalls_OnlyFirstHasSignature()
    {
        var msg = new Message
        {
            Role = "assistant",
            ReasoningSignature = GeminiSignatures.EncodeGeminiThoughtSignature(ProtoSig),
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "call_001",
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = "check_flight",
                        Arguments = """{"flight":"AA100"}""",
                    },
                },
                new ToolCall
                {
                    Id = "call_002",
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = "book_taxi",
                        Arguments = """{"time":"10 AM"}""",
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg);

        Assert.NotNull(result);
        Assert.Equal(2, result.Parts!.Count);

        Assert.Equal("check_flight", result.Parts![0].FunctionCall!.Name);
        Assert.Equal(ProtoSig, result.Parts![0].ThoughtSignature);

        Assert.Equal("book_taxi", result.Parts![1].FunctionCall!.Name);
        Assert.Null(result.Parts![1].ThoughtSignature);
    }

    [Fact]
    public void ConvertLlmMessageToGeminiContent_PerToolSignatureMetadata_UsedDirectly()
    {
        var secondSig = Convert.ToBase64String([0x0a, 0x02, 0x41, 0x42]);
        var msg = new Message
        {
            Role = "assistant",
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "call_001",
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = "check_flight",
                        Arguments = """{"flight":"AA100"}""",
                    },
                },
                new ToolCall
                {
                    Id = "call_002",
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = "book_taxi",
                        Arguments = """{"time":"10 AM"}""",
                    },
                    TransformerMetadata = new Dictionary<string, JsonElement>
                    {
                        [GeminiSignatures.TransformerMetadataKeyGoogleThoughtSignature] =
                            JsonSerializer.SerializeToElement(secondSig),
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg);

        Assert.NotNull(result);
        Assert.Equal(2, result.Parts!.Count);
        Assert.Null(result.Parts![0].ThoughtSignature);
        Assert.Equal(secondSig, result.Parts![1].ThoughtSignature);
    }

    [Fact]
    public void ConvertLlmMessageToGeminiContent_UnrecognizedSignature_UsesContextDefault()
    {
        var msg = new Message
        {
            Role = "assistant",
            ReasoningSignature = "encrypted_data",
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "call_001",
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = "get_weather",
                        Arguments = """{"location":"NYC"}""",
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg);

        Assert.NotNull(result);
        Assert.Single(result.Parts!);
        Assert.NotNull(result.Parts![0].FunctionCall!);
        Assert.Equal(GeminiThoughtSignatures.ContextEngineeringThoughtSignature, result.Parts![0].ThoughtSignature);
    }

    [Fact]
    public void ConvertLlmMessageToGeminiContent_ParallelCallsWithoutSignature_FirstGetsDefault()
    {
        var msg = new Message
        {
            Role = "assistant",
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "call_paris",
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = "get_weather",
                        Arguments = """{"location":"Paris"}""",
                    },
                },
                new ToolCall
                {
                    Id = "call_london",
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = "get_weather",
                        Arguments = """{"location":"London"}""",
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmMessageToGeminiContent(msg);

        Assert.NotNull(result);
        Assert.Equal(2, result.Parts!.Count);
        Assert.Equal(GeminiThoughtSignatures.ContextEngineeringThoughtSignature, result.Parts![0].ThoughtSignature);
        Assert.Null(result.Parts![1].ThoughtSignature);
    }

    // ------------------------------------------------------------------
    // TestConvertGeminiToLLMResponse_FinishReasons (outbound_convert_test.go:2115-2172)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("STOP", "stop")]
    [InlineData("MAX_TOKENS", "length")]
    [InlineData("SAFETY", "content_filter")]
    [InlineData("RECITATION", "content_filter")]
    [InlineData("MALFORMED_FUNCTION_CALL", "stop")] // Error condition, maps to stop
    [InlineData("UNKNOWN", "stop")]
    public void ConvertGeminiToLlmResponse_FinishReason_MapsToLlm(string geminiReason, string expected)
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_finish",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Test" }],
                    },
                    FinishReason = geminiReason,
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.NotNull(result.Choices![0].FinishReason);
        Assert.Equal(expected, result.Choices![0].FinishReason);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_EmptyFinishReason_ReturnsNull()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_finish",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Test" }],
                    },
                    FinishReason = "",
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Null(result.Choices![0].FinishReason);
    }

    // ------------------------------------------------------------------
    // TestConvertLLMToGeminiRequest_Testdata (outbound_convert_test.go:2178-2250)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmToGeminiRequest_Testdata_SimpleRequest()
    {
        var llmReq = LlmJson.Deserialize<LlmRequest>(File.ReadAllText(GeminiTestData("llm-simple.request.json")))!;

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(llmReq);

        Assert.Single(result.Contents!);
        Assert.Equal("user", result.Contents![0].Role);
        Assert.Equal("Output 1-20, 5 each line", result.Contents![0].Parts![0].Text);
        Assert.NotNull(result.GenerationConfig);
        Assert.Equal(4096, result.GenerationConfig.MaxOutputTokens);
        Assert.NotNull(result.GenerationConfig.ThinkingConfig);
        Assert.True(result.GenerationConfig.ThinkingConfig.IncludeThoughts);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_Testdata_ToolsRequest()
    {
        var llmReq = LlmJson.Deserialize<LlmRequest>(File.ReadAllText(GeminiTestData("llm-tools.request.json")))!;

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(llmReq);

        Assert.Single(result.Contents!);
        Assert.Equal("What is the weather in San Francisco, CA?", result.Contents![0].Parts![0].Text);
        Assert.Single(result.Tools!);
        Assert.Equal(2, result.Tools![0].FunctionDeclarations!.Count);
        Assert.Equal("get_coordinates", result.Tools![0].FunctionDeclarations![0].Name);
        Assert.Equal("get_weather", result.Tools![0].FunctionDeclarations![1].Name);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_Testdata_ThinkingRequest()
    {
        var llmReq = LlmJson.Deserialize<LlmRequest>(File.ReadAllText(GeminiTestData("llm-thinking.request.json")))!;

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(llmReq);

        Assert.Equal(3, result.Contents!.Count);
        Assert.Equal("user", result.Contents![0].Role);
        Assert.Equal("model", result.Contents![1].Role);

        var hasThought = false;
        foreach (var part in result.Contents![1].Parts!)
        {
            if (part.Thought)
            {
                hasThought = true;
                Assert.Contains("25 * 47", part.Text);
            }
        }

        Assert.True(hasThought);
        Assert.Equal("user", result.Contents![2].Role);
    }

    // ------------------------------------------------------------------
    // TestConvertLLMToGeminiRequest_EdgeCases (outbound_convert_test.go:2351-2443)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmToGeminiRequest_EmptyMessages_NoContents()
    {
        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(new LlmRequest { Messages = [] });

        Assert.Empty(result.Contents!);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_NoConfigFields_NoGenerationConfig()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Null(result.GenerationConfig);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_EmptyToolList_NoTools()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
            Tools = [],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        // Go: len(empty) == 0; the port leaves Tools null for an empty list.
        Assert.Null(result.Tools!);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_NonFunctionToolType_Skipped()
    {
        var req = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Test" },
                },
            ],
            Tools =
            [
                new Tool
                {
                    Type = "not_function",
                    Function = new Function { Name = "should_be_skipped" },
                },
                new Tool
                {
                    Type = "function",
                    Function = new Function { Name = "should_be_included" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Single(result.Tools!);
        Assert.Single(result.Tools![0].FunctionDeclarations!);
        Assert.Equal("should_be_included", result.Tools![0].FunctionDeclarations![0].Name);
    }

    // ------------------------------------------------------------------
    // TestConvertGeminiToLLMResponse_EdgeCases (outbound_convert_test.go:2445-2544)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertGeminiToLlmResponse_EmptyCandidates_NoChoices()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_empty",
            ModelVersion = "gemini-2.5-flash",
            Candidates = [],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Empty(result.Choices!);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_NilCandidateContent_NoMessage()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_nil_content",
            ModelVersion = "gemini-2.5-flash",
            Candidates = [new Candidate { Index = 0, Content = null }],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Single(result.Choices!);
        Assert.Null(result.Choices![0].Message);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_NilUsageMetadata_NoUsage()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_no_usage",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Response" }],
                    },
                },
            ],
            UsageMetadata = null,
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Null(result.Usage);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_UsageWithoutCacheOrThoughts_NoDetails()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_basic_usage",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Response" }],
                    },
                },
            ],
            UsageMetadata = new UsageMetadata
            {
                PromptTokenCount = 100,
                CandidatesTokenCount = 50,
                TotalTokenCount = 150,
            },
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.NotNull(result.Usage);
        Assert.Equal(100, result.Usage.PromptTokens);
        Assert.Equal(50, result.Usage.CompletionTokens);
        Assert.Equal(150, result.Usage.TotalTokens);
        Assert.Null(result.Usage.PromptTokensDetails);
        Assert.Null(result.Usage.CompletionTokensDetails);
    }

    // ------------------------------------------------------------------
    // TestExtractPartsFromLLMMessage (outbound_convert_test.go:2550-2661)
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractPartsFromLlmMessage_SingleTextContent_OnePart()
    {
        var msg = new Message
        {
            Role = "system",
            Content = new MessageContent { Content = "Hello world" },
        };

        var result = GeminiConvert.ExtractPartsFromLlmMessage(msg);

        Assert.Single(result);
        Assert.Equal("Hello world", result[0].Text);
    }

    [Fact]
    public void ExtractPartsFromLlmMessage_EmptyContent_NoParts()
    {
        var msg = new Message
        {
            Role = "system",
            Content = new MessageContent { Content = "" },
        };

        Assert.Empty(GeminiConvert.ExtractPartsFromLlmMessage(msg));
    }

    [Fact]
    public void ExtractPartsFromLlmMessage_NilContent_NoParts()
    {
        var msg = new Message
        {
            Role = "system",
            Content = new MessageContent(),
        };

        Assert.Empty(GeminiConvert.ExtractPartsFromLlmMessage(msg));
    }

    [Fact]
    public void ExtractPartsFromLlmMessage_MultipleContentParts_AllTextParts()
    {
        var msg = new Message
        {
            Role = "system",
            Content = new MessageContent
            {
                MultipleContent =
                [
                    new MessageContentPart { Type = "text", Text = "First part" },
                    new MessageContentPart { Type = "text", Text = "Second part" },
                    new MessageContentPart { Type = "text", Text = "Third part" },
                ],
            },
        };

        var result = GeminiConvert.ExtractPartsFromLlmMessage(msg);

        Assert.Equal(3, result.Count);
        Assert.Equal("First part", result[0].Text);
        Assert.Equal("Second part", result[1].Text);
        Assert.Equal("Third part", result[2].Text);
    }

    [Fact]
    public void ExtractPartsFromLlmMessage_EmptyAndNilTexts_FilteredOut()
    {
        var msg = new Message
        {
            Role = "system",
            Content = new MessageContent
            {
                MultipleContent =
                [
                    new MessageContentPart { Type = "text", Text = "Valid part" },
                    new MessageContentPart { Type = "text", Text = "" },
                    new MessageContentPart { Type = "text", Text = null },
                    new MessageContentPart { Type = "text", Text = "Another valid" },
                ],
            },
        };

        var result = GeminiConvert.ExtractPartsFromLlmMessage(msg);

        Assert.Equal(2, result.Count);
        Assert.Equal("Valid part", result[0].Text);
        Assert.Equal("Another valid", result[1].Text);
    }

    [Fact]
    public void ExtractPartsFromLlmMessage_NonTextTypes_Ignored()
    {
        var msg = new Message
        {
            Role = "system",
            Content = new MessageContent
            {
                MultipleContent =
                [
                    new MessageContentPart { Type = "text", Text = "Text part" },
                    new MessageContentPart
                    {
                        Type = "image_url",
                        ImageUrl = new ImageUrl { Url = "http://example.com/img.jpg" },
                    },
                    new MessageContentPart { Type = "text", Text = "Another text" },
                ],
            },
        };

        var result = GeminiConvert.ExtractPartsFromLlmMessage(msg);

        Assert.Equal(2, result.Count);
        Assert.Equal("Text part", result[0].Text);
        Assert.Equal("Another text", result[1].Text);
    }

    // ------------------------------------------------------------------
    // TestConvertImageURLToGeminiPart (outbound_convert_test.go:2663-2719)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("data:image/jpeg;base64,/9j/4AAQSkZJRg==", "image/jpeg", "/9j/4AAQSkZJRg==")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", "image/png", "iVBORw0KGgo=")]
    public void ConvertImageUrlToGeminiPart_DataUrl_BecomesInlineData(string url, string mime, string data)
    {
        var result = GeminiConvert.ConvertImageUrlToGeminiPart(new ImageUrl { Url = url });

        Assert.NotNull(result);
        Assert.NotNull(result.InlineData);
        Assert.Equal(mime, result.InlineData.MIMEType);
        Assert.Equal(data, result.InlineData.Data);
    }

    [Theory]
    [InlineData("https://example.com/image.jpg")]
    [InlineData("gs://bucket/path/to/image.png")]
    public void ConvertImageUrlToGeminiPart_RegularUrl_BecomesFileData(string url)
    {
        var result = GeminiConvert.ConvertImageUrlToGeminiPart(new ImageUrl { Url = url });

        Assert.NotNull(result);
        Assert.NotNull(result.FileData);
        Assert.Equal(url, result.FileData.FileURI);
    }

    // ------------------------------------------------------------------
    // TestThinkingBudgetToReasoningEffort (outbound_convert_test.go:2721-2740)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(512, "low")]
    [InlineData(1024, "low")]
    [InlineData(2048, "medium")]
    [InlineData(8192, "medium")]
    [InlineData(16384, "high")]
    [InlineData(32768, "high")]
    public void ThinkingBudgetToReasoningEffort_MatchesLadder(long budget, string expected)
    {
        Assert.Equal(expected, GeminiConvert.ThinkingBudgetToReasoningEffort(budget));
    }

    // ------------------------------------------------------------------
    // TestReasoningEffortToThinkingBudget (outbound_convert_test.go:2742-2760)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("low", 1024)]
    [InlineData("medium", 8192)]
    [InlineData("high", 32768)]
    [InlineData("unknown", 8192)]
    [InlineData("", 8192)]
    public void ReasoningEffortToThinkingBudget_MatchesReference(string effort, long expected)
    {
        Assert.Equal(expected, GeminiConvert.ReasoningEffortToThinkingBudget(effort));
    }

    // ------------------------------------------------------------------
    // TestReasoningEffortToThinkingBudgetWithConfig (outbound_convert_test.go:2762-2862)
    // ------------------------------------------------------------------

    private static GeminiOutboundConfig ConfigWith(Dictionary<string, long> mapping)
        => new() { ReasoningEffortToBudget = mapping };

    [Theory]
    [InlineData("low", 1024)]
    [InlineData("medium", 8192)]
    [InlineData("high", 32768)]
    public void ReasoningEffortToThinkingBudgetWithConfig_NoConfig_UsesDefaults(string effort, long expected)
    {
        Assert.Equal(expected, GeminiConvert.ReasoningEffortToThinkingBudgetWithConfig(effort, null));
    }

    [Theory]
    [InlineData("low", 2000)]
    [InlineData("medium", 9000)]
    [InlineData("high", 35000)]
    public void ReasoningEffortToThinkingBudgetWithConfig_CustomConfig_Wins(string effort, long expected)
    {
        var config = ConfigWith(new Dictionary<string, long>
        {
            ["low"] = 2000,
            ["medium"] = 9000,
            ["high"] = 35000,
        });

        Assert.Equal(expected, GeminiConvert.ReasoningEffortToThinkingBudgetWithConfig(effort, config));
    }

    [Fact]
    public void ReasoningEffortToThinkingBudgetWithConfig_UnknownEffort_FallsBackToMedium()
    {
        var config = ConfigWith(new Dictionary<string, long>
        {
            ["low"] = 2000,
            ["medium"] = 9000,
            ["high"] = 35000,
        });

        Assert.Equal(8192, GeminiConvert.ReasoningEffortToThinkingBudgetWithConfig("unknown", config));
    }

    [Fact]
    public void ReasoningEffortToThinkingBudgetWithConfig_EffortMissingFromConfig_FallsBackToDefault()
    {
        var config = ConfigWith(new Dictionary<string, long>
        {
            ["low"] = 2000,
            ["high"] = 35000,
        });

        Assert.Equal(8192, GeminiConvert.ReasoningEffortToThinkingBudgetWithConfig("medium", config));
    }

    [Fact]
    public void ReasoningEffortToThinkingBudgetWithConfig_EmptyMapping_UsesDefault()
    {
        var config = ConfigWith(new Dictionary<string, long>());

        Assert.Equal(32768, GeminiConvert.ReasoningEffortToThinkingBudgetWithConfig("high", config));
    }

    // ------------------------------------------------------------------
    // TestConvertGeminiRoleToLLMRole (outbound_convert_test.go:2864-2881)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("model", "assistant")]
    [InlineData("user", "user")]
    [InlineData("", "user")]
    [InlineData("unknown", "unknown")]
    public void ConvertGeminiRoleToLlmRole_MatchesReference(string input, string expected)
    {
        Assert.Equal(expected, GeminiConvert.ConvertGeminiRoleToLlmRole(input));
    }

    // ------------------------------------------------------------------
    // TestConvertLLMRoleToGeminiRole (outbound_convert_test.go:2883-2900)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("assistant", "model")]
    [InlineData("user", "user")]
    [InlineData("system", "system")]
    [InlineData("unknown", "unknown")]
    public void ConvertLlmRoleToGeminiRole_MatchesReference(string input, string expected)
    {
        Assert.Equal(expected, GeminiConvert.ConvertLlmRoleToGeminiRole(input));
    }

    // ------------------------------------------------------------------
    // TestConvertGeminiToLLMResponse_CitationMetadata (outbound_convert_test.go:2902-2928)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertGeminiToLlmResponse_CitationMetadata_BecomesUrlCitationAnnotation()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_citation_meta",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Grounded answer" }],
                    },
                    CitationMetadata = new CitationMetadata
                    {
                        Citations =
                        [
                            new Citation
                            {
                                StartIndex = 0,
                                EndIndex = 8,
                                URI = "https://example.com/citation",
                                Title = "Citation Source",
                            },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Single(result.Choices!);
        Assert.NotNull(result.Choices![0].Message);
        Assert.Single(result.Choices![0].Message!.Annotations!);
        Assert.Equal("url_citation", result.Choices![0].Message!.Annotations![0].Type);
        Assert.NotNull(result.Choices![0].Message!.Annotations![0].UrlCitation!);
        Assert.Equal("https://example.com/citation", result.Choices![0].Message!.Annotations![0].UrlCitation!.Url);
        Assert.Equal("Citation Source", result.Choices![0].Message!.Annotations![0].UrlCitation!.Title);
        Assert.Equal(0, result.Choices![0].Message!.Annotations![0].StartIndex);
        Assert.Equal(8, result.Choices![0].Message!.Annotations![0].EndIndex);
    }

    // ------------------------------------------------------------------
    // TestConvertGeminiToLLMResponse_GroundingMetadataDerivesAnnotations
    // (outbound_convert_test.go:2930-2960)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertGeminiToLlmResponse_GroundingMetadata_DerivesAnnotations()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_grounding_annotations",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Grounded answer" }],
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk
                            {
                                Web = new GroundingChunkWeb
                                {
                                    URI = "https://example.com/chunk",
                                    Title = "Chunk Source",
                                },
                            },
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new Segment { StartIndex = 0, EndIndex = 8 },
                                GroundingChunkIndices = [0],
                            },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Single(result.Choices!);
        Assert.NotNull(result.Choices![0].Message);
        Assert.Single(result.Choices![0].Message!.Annotations!);
        Assert.Equal("url_citation", result.Choices![0].Message!.Annotations![0].Type);
        Assert.NotNull(result.Choices![0].Message!.Annotations![0].UrlCitation!);
        Assert.Equal("https://example.com/chunk", result.Choices![0].Message!.Annotations![0].UrlCitation!.Url);
        Assert.Equal("Chunk Source", result.Choices![0].Message!.Annotations![0].UrlCitation!.Title);
        Assert.Equal(0, result.Choices![0].Message!.Annotations![0].StartIndex);
        Assert.Equal(8, result.Choices![0].Message!.Annotations![0].EndIndex);
        Assert.NotNull(result.Choices![0].TransformerMetadata);
    }

    // ------------------------------------------------------------------
    // TestConvertGeminiToLLMResponse_GroundingMetadata (outbound_convert_test.go:2962-3267)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertGeminiToLlmResponse_GroundingMetadata_WebSearch_FullShape()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_grounding",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Based on my search, here is the answer." }],
                    },
                    FinishReason = "STOP",
                    GroundingMetadata = new GroundingMetadata
                    {
                        WebSearchQueries = ["latest news about AI"],
                        GroundingChunks =
                        [
                            new GroundingChunk
                            {
                                Web = new GroundingChunkWeb
                                {
                                    URI = "https://example.com/article1",
                                    Title = "AI News Article",
                                    Domain = "example.com",
                                },
                            },
                            new GroundingChunk
                            {
                                Web = new GroundingChunkWeb
                                {
                                    URI = "https://example.org/article2",
                                    Title = "Another AI Article",
                                    Domain = "example.org",
                                },
                            },
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new Segment
                                {
                                    StartIndex = 0,
                                    EndIndex = 38,
                                    Text = "Based on my search, here is the answer",
                                },
                                GroundingChunkIndices = [0, 1],
                                ConfidenceScores = [0.95f, 0.87f],
                            },
                        ],
                        SearchEntryPoint = new SearchEntryPoint
                        {
                            RenderedContent = "<div>Search results</div>",
                        },
                        RetrievalMetadata = new RetrievalMetadata
                        {
                            GoogleSearchDynamicRetrievalScore = 0.92f,
                        },
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Single(result.Choices!);
        Assert.NotNull(result.Choices![0].TransformerMetadata);

        var gm = Grounding(result.Choices![0]);
        Assert.NotNull(gm);
        Assert.Equal(new List<string> { "latest news about AI" }, gm.WebSearchQueries!);
        Assert.Equal(2, gm.GroundingChunks!.Count);
        Assert.Equal("https://example.com/article1", gm.GroundingChunks![0].Web!.URI);
        Assert.Equal("AI News Article", gm.GroundingChunks![0].Web!.Title);
        Assert.Equal("example.com", gm.GroundingChunks![0].Web!.Domain);
        Assert.Single(gm.GroundingSupports!);
        Assert.Equal(new List<int> { 0, 1 }, gm.GroundingSupports![0].GroundingChunkIndices);
        Assert.InRange(gm.GroundingSupports![0].ConfidenceScores![0], 0.94f, 0.96f);
        Assert.NotNull(gm.SearchEntryPoint);
        Assert.Equal("<div>Search results</div>", gm.SearchEntryPoint.RenderedContent);
        Assert.NotNull(gm.RetrievalMetadata);
        Assert.InRange(gm.RetrievalMetadata.GoogleSearchDynamicRetrievalScore, 0.91f, 0.93f);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_GroundingMetadata_RetrievedContext()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_retrieval",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "According to the document..." }],
                    },
                    FinishReason = "STOP",
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk
                            {
                                RetrievedContext = new GroundingChunkRetrievedContext
                                {
                                    URI = "gs://bucket/document.pdf",
                                    Title = "Important Document",
                                    Text = "Relevant excerpt from the document.",
                                },
                            },
                        ],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Single(result.Choices!);
        Assert.NotNull(result.Choices![0].TransformerMetadata);

        var gm = Grounding(result.Choices![0]);
        Assert.NotNull(gm);
        Assert.Single(gm.GroundingChunks!);
        Assert.NotNull(gm.GroundingChunks![0].RetrievedContext);
        Assert.Equal("gs://bucket/document.pdf", gm.GroundingChunks![0].RetrievedContext!.URI);
        Assert.Equal("Important Document", gm.GroundingChunks![0].RetrievedContext!.Title);
        Assert.Equal("Relevant excerpt from the document.", gm.GroundingChunks![0].RetrievedContext!.Text);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_NoGroundingMetadata_NoTransformerMetadata()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_no_grounding",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Simple response without grounding." }],
                    },
                    FinishReason = "STOP",
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Single(result.Choices!);
        Assert.Null(result.Choices![0].TransformerMetadata);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_GroundingMetadata_Streaming_SetsChunkObject()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_stream_grounding",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Streaming chunk with grounding." }],
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        WebSearchQueries = ["streaming search query"],
                        GroundingChunks =
                        [
                            new GroundingChunk
                            {
                                Web = new GroundingChunkWeb
                                {
                                    URI = "https://stream.example.com",
                                    Title = "Stream Source",
                                },
                            },
                        ],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, true);

        Assert.Equal("chat.completion.chunk", result.Object);
        Assert.Single(result.Choices!);
        Assert.NotNull(result.Choices![0].TransformerMetadata);

        var gm = Grounding(result.Choices![0]);
        Assert.NotNull(gm);
        Assert.Equal(new List<string> { "streaming search query" }, gm.WebSearchQueries!);
        Assert.Single(gm.GroundingChunks!);
        Assert.Equal("https://stream.example.com", gm.GroundingChunks![0].Web!.URI);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_GroundingMetadata_MultipleCandidates_PerChoice()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_multi_candidates",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "First candidate response." }],
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        WebSearchQueries = ["query for candidate 1"],
                    },
                },
                new Candidate
                {
                    Index = 1,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Second candidate response." }],
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        WebSearchQueries = ["query for candidate 2"],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Equal(2, result.Choices!.Count);

        var gm0 = Grounding(result.Choices![0]);
        Assert.NotNull(gm0);
        Assert.Equal(new List<string> { "query for candidate 1" }, gm0.WebSearchQueries!);

        var gm1 = Grounding(result.Choices![1]);
        Assert.NotNull(gm1);
        Assert.Equal(new List<string> { "query for candidate 2" }, gm1.WebSearchQueries!);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_GroundingMetadata_SegmentDetails()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_segment",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "The capital of France is Paris." }],
                    },
                    FinishReason = "STOP",
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk
                            {
                                Web = new GroundingChunkWeb
                                {
                                    URI = "https://example.com/france",
                                    Title = "France Facts",
                                },
                            },
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new Segment
                                {
                                    StartIndex = 0,
                                    EndIndex = 31,
                                    PartIndex = 0,
                                    Text = "The capital of France is Paris.",
                                },
                                GroundingChunkIndices = [0],
                                ConfidenceScores = [0.99f],
                            },
                        ],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Single(result.Choices!);
        Assert.NotNull(result.Choices![0].TransformerMetadata);

        var gm = Grounding(result.Choices![0]);
        Assert.NotNull(gm);
        Assert.Single(gm.GroundingSupports!);
        var seg = gm.GroundingSupports![0].Segment;
        Assert.NotNull(seg);
        Assert.Equal(0, seg.StartIndex);
        Assert.Equal(31, seg.EndIndex);
        Assert.Equal(0, seg.PartIndex);
        Assert.Equal("The capital of France is Paris.", seg.Text);
    }

    // ------------------------------------------------------------------
    // TestConvertGeminiToLLMResponse_ToolCallThoughtSignature (outbound_convert_test.go:3269-3308)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertGeminiToLlmResponse_ToolCallThoughtSignature_RoundTripsThroughDecode()
    {
        var protoB64 = Convert.ToBase64String([0x0a, 0x05, 0x73, 0x69, 0x67, 0x5f, 0x41]);

        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_fp_sig",
            ModelVersion = "gemini-3-pro",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts =
                        [
                            new Part
                            {
                                FunctionCall = new Transformer.Gemini.FunctionCall
                                {
                                    Id = "call_001",
                                    Name = "check_flight",
                                    Args = new Dictionary<string, JsonElement>
                                    {
                                        ["flight"] = JsonSerializer.SerializeToElement("AA100"),
                                    },
                                },
                                ThoughtSignature = protoB64,
                            },
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Single(result.Choices!);
        Assert.NotNull(result.Choices![0].Message);
        Assert.Single(result.Choices![0].Message!.ToolCalls!);
        Assert.NotNull(result.Choices![0].Message!.ToolCalls![0].TransformerMetadata);
        var raw = result.Choices![0].Message!.ToolCalls![0]
            .TransformerMetadata![GeminiSignatures.TransformerMetadataKeyGoogleThoughtSignature].GetString();

        var decoded = GeminiSignatures.DecodeGeminiThoughtSignature(raw);
        Assert.NotNull(decoded);
        Assert.Equal(protoB64, decoded);
    }

    // ------------------------------------------------------------------
    // TestConvertLLMToGeminiRequest_SafetySettings (outbound_convert_test.go:3314-3449)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmToGeminiRequest_SafetySettings_FromMetadata()
    {
        var req = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello, Gemini!" },
                },
            ],
            TransformerMetadata = new Dictionary<string, JsonElement>
            {
                [GeminiConvert.TransformerMetadataKeySafetySettings] = JsonSerializer.SerializeToElement(
                    new[]
                    {
                        new SafetySetting
                        {
                            Category = "HARM_CATEGORY_HARASSMENT",
                            Threshold = "BLOCK_LOW_AND_ABOVE",
                        },
                        new SafetySetting
                        {
                            Category = "HARM_CATEGORY_HATE_SPEECH",
                            Threshold = "BLOCK_MEDIUM_AND_ABOVE",
                        },
                    }),
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.SafetySettings);
        Assert.Equal(2, result.SafetySettings.Count);
        Assert.Equal("HARM_CATEGORY_HARASSMENT", result.SafetySettings[0].Category);
        Assert.Equal("BLOCK_LOW_AND_ABOVE", result.SafetySettings[0].Threshold);
        Assert.Equal("HARM_CATEGORY_HATE_SPEECH", result.SafetySettings[1].Category);
        Assert.Equal("BLOCK_MEDIUM_AND_ABOVE", result.SafetySettings[1].Threshold);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_NoSafetySettings_StaysNull()
    {
        var req = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello, Gemini!" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Null(result.SafetySettings);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_EmptySafetySettings_StaysNull()
    {
        var req = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello, Gemini!" },
                },
            ],
            TransformerMetadata = new Dictionary<string, JsonElement>
            {
                [GeminiConvert.TransformerMetadataKeySafetySettings] =
                    JsonSerializer.SerializeToElement(Array.Empty<SafetySetting>()),
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Null(result.SafetySettings);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_AllSafetyCategories_InOrder()
    {
        var req = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello, Gemini!" },
                },
            ],
            TransformerMetadata = new Dictionary<string, JsonElement>
            {
                [GeminiConvert.TransformerMetadataKeySafetySettings] = JsonSerializer.SerializeToElement(
                    new[]
                    {
                        new SafetySetting
                        {
                            Category = "HARM_CATEGORY_HARASSMENT",
                            Threshold = "BLOCK_NONE",
                        },
                        new SafetySetting
                        {
                            Category = "HARM_CATEGORY_HATE_SPEECH",
                            Threshold = "BLOCK_LOW_AND_ABOVE",
                        },
                        new SafetySetting
                        {
                            Category = "HARM_CATEGORY_SEXUALLY_EXPLICIT",
                            Threshold = "BLOCK_MEDIUM_AND_ABOVE",
                        },
                        new SafetySetting
                        {
                            Category = "HARM_CATEGORY_DANGEROUS_CONTENT",
                            Threshold = "BLOCK_HIGH_AND_ABOVE",
                        },
                    }),
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.SafetySettings);
        Assert.Equal(4, result.SafetySettings.Count);
        Assert.Equal("HARM_CATEGORY_HARASSMENT", result.SafetySettings[0].Category);
        Assert.Equal("BLOCK_NONE", result.SafetySettings[0].Threshold);
        Assert.Equal("HARM_CATEGORY_HATE_SPEECH", result.SafetySettings[1].Category);
        Assert.Equal("BLOCK_LOW_AND_ABOVE", result.SafetySettings[1].Threshold);
        Assert.Equal("HARM_CATEGORY_SEXUALLY_EXPLICIT", result.SafetySettings[2].Category);
        Assert.Equal("BLOCK_MEDIUM_AND_ABOVE", result.SafetySettings[2].Threshold);
        Assert.Equal("HARM_CATEGORY_DANGEROUS_CONTENT", result.SafetySettings[3].Category);
        Assert.Equal("BLOCK_HIGH_AND_ABOVE", result.SafetySettings[3].Threshold);
    }

    // ------------------------------------------------------------------
    // TestConvertLLMToGeminiRequest_ImageConfig (outbound_convert_test.go:3531-3667)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertLlmToGeminiRequest_ImageConfig_FromMetadata()
    {
        var req = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Generate an image" },
                },
            ],
            TransformerMetadata = new Dictionary<string, JsonElement>
            {
                [GeminiConvert.TransformerMetadataKeyImageConfig] = JsonSerializer.SerializeToElement(
                    new ImageConfig { AspectRatio = "16:9", ImageSize = "2K" }),
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ImageConfig);
        Assert.Equal("16:9", result.GenerationConfig.ImageConfig.AspectRatio);
        Assert.Equal("2K", result.GenerationConfig.ImageConfig.ImageSize);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_NoImageConfig_NoGenerationConfig()
    {
        var req = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello, Gemini!" },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.Null(result.GenerationConfig);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_NilImageConfig_NoGenerationConfig()
    {
        var req = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Hello, Gemini!" },
                },
            ],
            TransformerMetadata = new Dictionary<string, JsonElement>
            {
                [GeminiConvert.TransformerMetadataKeyImageConfig] = JsonSerializer.SerializeToElement<ImageConfig?>(null),
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        // Go: the nil type assertion yields nil; the port deserializes "null".
        Assert.Null(result.GenerationConfig);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_ImageConfigAspectRatioOnly_LeavesSizeEmpty()
    {
        var req = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Generate an image" },
                },
            ],
            TransformerMetadata = new Dictionary<string, JsonElement>
            {
                [GeminiConvert.TransformerMetadataKeyImageConfig] = JsonSerializer.SerializeToElement(
                    new ImageConfig { AspectRatio = "1:1" }),
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ImageConfig);
        Assert.Equal("1:1", result.GenerationConfig.ImageConfig.AspectRatio);
        Assert.Empty(result.GenerationConfig.ImageConfig.ImageSize);
    }

    [Fact]
    public void ConvertLlmToGeminiRequest_ImageConfigWithTemperature_KeepsBoth()
    {
        var req = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            Temperature = 0.7,
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = new MessageContent { Content = "Generate an image" },
                },
            ],
            TransformerMetadata = new Dictionary<string, JsonElement>
            {
                [GeminiConvert.TransformerMetadataKeyImageConfig] = JsonSerializer.SerializeToElement(
                    new ImageConfig { AspectRatio = "9:16", ImageSize = "4K" }),
            },
        };

        var result = GeminiOutboundConvert.ConvertLlmToGeminiRequest(req);

        Assert.NotNull(result.GenerationConfig);
        Assert.NotNull(result.GenerationConfig.ImageConfig);
        Assert.Equal("9:16", result.GenerationConfig.ImageConfig.AspectRatio);
        Assert.Equal("4K", result.GenerationConfig.ImageConfig.ImageSize);
        Assert.NotNull(result.GenerationConfig.Temperature);
        Assert.InRange(result.GenerationConfig.Temperature.Value, 0.69, 0.71);
    }

    // ------------------------------------------------------------------
    // TestConvertGeminiToLLMResponse_GroundingMetadata_Additional
    // (outbound_convert_test.go:3669-3924)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertGeminiToLlmResponse_GroundingMetadata_AllFieldsPopulated()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_all_fields",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Comprehensive response with all grounding fields." }],
                    },
                    FinishReason = "STOP",
                    GroundingMetadata = new GroundingMetadata
                    {
                        WebSearchQueries = ["comprehensive search"],
                        GroundingChunks =
                        [
                            new GroundingChunk
                            {
                                Web = new GroundingChunkWeb
                                {
                                    URI = "https://example.com/comprehensive",
                                    Title = "Comprehensive Article",
                                    Domain = "example.com",
                                },
                            },
                            new GroundingChunk
                            {
                                RetrievedContext = new GroundingChunkRetrievedContext
                                {
                                    URI = "gs://bucket/comprehensive.pdf",
                                    Title = "Comprehensive Document",
                                    Text = "Comprehensive document content.",
                                },
                            },
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new Segment
                                {
                                    StartIndex = 0,
                                    EndIndex = 45,
                                    PartIndex = 0,
                                    Text = "Comprehensive response with all grounding fields",
                                },
                                GroundingChunkIndices = [0, 1],
                                ConfidenceScores = [0.98f, 0.92f],
                            },
                        ],
                        SearchEntryPoint = new SearchEntryPoint
                        {
                            RenderedContent = "<div>Comprehensive search entry point</div>",
                        },
                        RetrievalMetadata = new RetrievalMetadata
                        {
                            GoogleSearchDynamicRetrievalScore = 0.95f,
                        },
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Single(result.Choices!);
        Assert.NotNull(result.Choices![0].TransformerMetadata);

        var gm = Grounding(result.Choices![0]);
        Assert.NotNull(gm);
        Assert.Equal(new List<string> { "comprehensive search" }, gm.WebSearchQueries!);
        Assert.Equal(2, gm.GroundingChunks!.Count);
        Assert.NotNull(gm.GroundingChunks![0].Web);
        Assert.NotNull(gm.GroundingChunks![1].RetrievedContext);
        Assert.Single(gm.GroundingSupports!);
        Assert.NotNull(gm.SearchEntryPoint);
        Assert.NotNull(gm.RetrievalMetadata);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_GroundingMetadata_EmptyArrays()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_empty_arrays",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Response with empty grounding arrays." }],
                    },
                    FinishReason = "STOP",
                    GroundingMetadata = new GroundingMetadata
                    {
                        WebSearchQueries = [],
                        GroundingChunks = [],
                        GroundingSupports = [],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Single(result.Choices!);
        Assert.NotNull(result.Choices![0].TransformerMetadata);

        var gm = Grounding(result.Choices![0]);
        Assert.NotNull(gm);
        Assert.Empty(gm.WebSearchQueries!);
        Assert.Empty(gm.GroundingChunks!);
        Assert.Empty(gm.GroundingSupports!);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_GroundingMetadata_MixedCandidates()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_mixed_grounding",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Response with grounding." }],
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        WebSearchQueries = ["candidate 1 search"],
                    },
                },
                new Candidate
                {
                    Index = 1,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Response without grounding." }],
                    },
                },
                new Candidate
                {
                    Index = 2,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Another response with grounding." }],
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        RetrievalMetadata = new RetrievalMetadata
                        {
                            GoogleSearchDynamicRetrievalScore = 0.88f,
                        },
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, false);

        Assert.Equal(3, result.Choices!.Count);

        Assert.NotNull(result.Choices![0].TransformerMetadata);
        var gm0 = Grounding(result.Choices![0]);
        Assert.NotNull(gm0);
        Assert.Equal(new List<string> { "candidate 1 search" }, gm0.WebSearchQueries!);

        Assert.Null(result.Choices![1].TransformerMetadata);

        Assert.NotNull(result.Choices![2].TransformerMetadata);
        var gm2 = Grounding(result.Choices![2]);
        Assert.NotNull(gm2);
        Assert.NotNull(gm2.RetrievalMetadata);
        Assert.InRange(gm2.RetrievalMetadata.GoogleSearchDynamicRetrievalScore, 0.87f, 0.89f);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_GroundingMetadata_StreamingMultipleChunks()
    {
        var resp = new GenerateContentResponse
        {
            ResponseId = "resp_stream_multi_chunks",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = "model",
                        Parts = [new Part { Text = "Streaming with multiple chunks." }],
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk
                            {
                                Web = new GroundingChunkWeb
                                {
                                    URI = "https://source1.example.com",
                                    Title = "Source 1",
                                },
                            },
                            new GroundingChunk
                            {
                                Web = new GroundingChunkWeb
                                {
                                    URI = "https://source2.example.com",
                                    Title = "Source 2",
                                },
                            },
                            new GroundingChunk
                            {
                                RetrievedContext = new GroundingChunkRetrievedContext
                                {
                                    URI = "gs://bucket/internal.pdf",
                                    Title = "Internal Document",
                                },
                            },
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new Segment
                                {
                                    StartIndex = 0,
                                    EndIndex = 31,
                                    Text = "Streaming with multiple chunks",
                                },
                                GroundingChunkIndices = [0, 1, 2],
                                ConfidenceScores = [0.95f, 0.87f, 0.91f],
                            },
                        ],
                    },
                },
            ],
        };

        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(resp, true);

        Assert.Equal("chat.completion.chunk", result.Object);
        Assert.Single(result.Choices!);
        Assert.NotNull(result.Choices![0].TransformerMetadata);

        var gm = Grounding(result.Choices![0]);
        Assert.NotNull(gm);
        Assert.Equal(3, gm.GroundingChunks!.Count);
        Assert.NotNull(gm.GroundingChunks![0].Web);
        Assert.NotNull(gm.GroundingChunks![1].Web);
        Assert.NotNull(gm.GroundingChunks![2].RetrievedContext);
        Assert.Single(gm.GroundingSupports!);
        Assert.Equal(new List<int> { 0, 1, 2 }, gm.GroundingSupports![0].GroundingChunkIndices);
    }
}
