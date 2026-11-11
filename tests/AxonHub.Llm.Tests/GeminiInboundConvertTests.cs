using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.Gemini.OpenAI;
using AxonHub.Llm.Transformer.Shared;
using GeminiFunctionCall = AxonHub.Llm.Transformer.Gemini.FunctionCall;
using GeminiGoogleSearch = AxonHub.Llm.Transformer.Gemini.GoogleSearch;
using GeminiThinkingConfig = AxonHub.Llm.Transformer.Gemini.ThinkingConfig;
using GeminiTool = AxonHub.Llm.Transformer.Gemini.Tool;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the native Gemini inbound conversions
/// (llm/transformer/gemini/inbound_convert_test.go). One fact per Go test
/// case; TestSafetySettingsStoredInMetadata and TestExtractImageConfigFromMetadata
/// are already ported in GeminiMetadataExtractionTests.cs.
/// </summary>
public sealed class GeminiInboundConvertTests
{
    private static JsonElement Json(string raw) => LlmJson.Deserialize<JsonElement>(raw);

    private static string GeminiTestData(string name)
        => Path.Combine(AppContext.BaseDirectory, "testdata", "gemini", name);

    private static GenerateContentRequest LoadGeminiRequest(string name)
        => LlmJson.Deserialize<GenerateContentRequest>(File.ReadAllText(GeminiTestData(name)))!;

    private static GenerateContentResponse LoadGeminiResponse(string name)
        => LlmJson.Deserialize<GenerateContentResponse>(File.ReadAllText(GeminiTestData(name)))!;

    private static void AssertJsonEqual(string expected, JsonElement actual)
    {
        using var expectedDoc = JsonDocument.Parse(expected);
        Assert.True(
            JsonElement.DeepEquals(expectedDoc.RootElement, actual),
            $"expected JSON {expected}, got {actual.GetRawText()}");
    }

    // ==================================================================
    // TestConvertGeminiToLLMRequest_Basic (inbound_convert_test.go:22-371)
    // ==================================================================

    [Fact]
    public void ConvertGeminiToLlmRequest_Basic_SimpleTextRequest()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Hello, Gemini!" }] },
            ],
            GenerationConfig = new GenerationConfig { MaxOutputTokens = 1024 },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result);
        Assert.Equal(ApiFormats.GeminiContents, result.ApiFormat);
        Assert.Single(result.Messages);
        Assert.Equal("user", result.Messages[0].Role);
        Assert.Equal("Hello, Gemini!", result.Messages[0].Content!.Content);
        Assert.Equal(1024L, result.MaxTokens);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Basic_SystemInstruction()
    {
        var input = new GenerateContentRequest
        {
            SystemInstruction = new Content
            {
                Parts = [new Part { Text = "You are a helpful assistant." }],
            },
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Hello!" }] },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("system", result.Messages[0].Role);
        Assert.Equal("You are a helpful assistant.", result.Messages[0].Content!.Content);
        Assert.Equal("user", result.Messages[1].Role);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Basic_GenerationConfig()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Test" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                MaxOutputTokens = 2048,
                Temperature = 0.7,
                TopP = 0.9,
                PresencePenalty = 0.5,
                FrequencyPenalty = 0.3,
                Seed = 42,
                StopSequences = ["END", "STOP"],
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal(2048L, result.MaxTokens);
        Assert.InRange(result.Temperature!.Value, 0.69, 0.71);
        Assert.InRange(result.TopP!.Value, 0.89, 0.91);
        Assert.InRange(result.PresencePenalty!.Value, 0.49, 0.51);
        Assert.InRange(result.FrequencyPenalty!.Value, 0.29, 0.31);
        Assert.Equal(42L, result.Seed);
        Assert.NotNull(result.Stop);
        Assert.Equal(["END", "STOP"], result.Stop.Multiple);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Basic_SingleStopSequence()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Test" }] },
            ],
            GenerationConfig = new GenerationConfig { StopSequences = ["END"] },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result.Stop);
        Assert.Equal("END", result.Stop.Single);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Basic_ThinkingConfig()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Solve this problem" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                MaxOutputTokens = 4096,
                ThinkingConfig = new GeminiThinkingConfig { IncludeThoughts = true, ThinkingBudget = 8192 },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal("medium", result.ReasoningEffort);
        Assert.NotNull(result.ExtraBody);

        var extra = GeminiExtraBodyParser.ParseExtraBody(result.ExtraBody);
        Assert.NotNull(extra);
        Assert.NotNull(extra.Google);
        Assert.NotNull(extra.Google.ThinkingConfig);
        Assert.True(extra.Google.ThinkingConfig.IncludeThoughts);
        Assert.Null(extra.Google.ThinkingConfig.ThinkingLevel);
        Assert.NotNull(extra.Google.ThinkingConfig.ThinkingBudget);
        Assert.Equal(8192L, extra.Google.ThinkingConfig.ThinkingBudget.IntValue);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Basic_ThinkingConfigLowBudget()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Quick question" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                ThinkingConfig = new GeminiThinkingConfig { IncludeThoughts = true, ThinkingBudget = 512 },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal("low", result.ReasoningEffort);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Basic_ThinkingConfigHighBudget()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Complex problem" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                ThinkingConfig = new GeminiThinkingConfig { IncludeThoughts = true, ThinkingBudget = 32768 },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal("high", result.ReasoningEffort);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Basic_ThinkingConfigNoBudget()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Question" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                ThinkingConfig = new GeminiThinkingConfig { IncludeThoughts = true },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal("medium", result.ReasoningEffort);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Basic_ThinkingConfigBudgetPreservation()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Question" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                ThinkingConfig = new GeminiThinkingConfig { IncludeThoughts = true, ThinkingBudget = 5000 },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal("medium", result.ReasoningEffort);
        Assert.Equal(5000L, result.ReasoningBudget);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Basic_ThinkingLevelPriorityHighLevel()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Complex question" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                ThinkingConfig = new GeminiThinkingConfig
                {
                    IncludeThoughts = true,
                    ThinkingLevel = "high",
                    ThinkingBudget = 1024, // Budget is low, but level should take priority
                },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal("high", result.ReasoningEffort); // Should use level, not budget
        Assert.Equal(1024L, result.ReasoningBudget); // Budget should be preserved
        Assert.NotNull(result.ExtraBody);

        var extra = GeminiExtraBodyParser.ParseExtraBody(result.ExtraBody);
        Assert.NotNull(extra);
        Assert.NotNull(extra.Google);
        Assert.NotNull(extra.Google.ThinkingConfig);
        Assert.True(extra.Google.ThinkingConfig.IncludeThoughts);
        Assert.Equal("high", extra.Google.ThinkingConfig.ThinkingLevel);
        Assert.NotNull(extra.Google.ThinkingConfig.ThinkingBudget);
        Assert.Equal(1024L, extra.Google.ThinkingConfig.ThinkingBudget.IntValue);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Basic_ThinkingLevelPriorityLowLevel()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Simple question" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                ThinkingConfig = new GeminiThinkingConfig
                {
                    IncludeThoughts = true,
                    ThinkingLevel = "low",
                    ThinkingBudget = 32768, // Budget is high, but level should take priority
                },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal("low", result.ReasoningEffort); // Should use level, not budget
        Assert.Equal(32768L, result.ReasoningBudget); // Budget should be preserved
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Basic_ThinkingLevelOnlyNoBudget()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Question" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                ThinkingConfig = new GeminiThinkingConfig { IncludeThoughts = true, ThinkingLevel = "high" },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal("high", result.ReasoningEffort);
        Assert.Null(result.ReasoningBudget); // No budget provided
        Assert.NotNull(result.ExtraBody);

        var extra = GeminiExtraBodyParser.ParseExtraBody(result.ExtraBody);
        Assert.NotNull(extra);
        Assert.NotNull(extra.Google);
        Assert.NotNull(extra.Google.ThinkingConfig);
        Assert.True(extra.Google.ThinkingConfig.IncludeThoughts);
        Assert.Equal("high", extra.Google.ThinkingConfig.ThinkingLevel);
        Assert.Null(extra.Google.ThinkingConfig.ThinkingBudget);
    }

    // ==================================================================
    // TestConvertGeminiToLLMRequest_AudioInput (inbound_convert_test.go:373-398)
    // ==================================================================

    [Fact]
    public void ConvertGeminiToLlmRequest_AudioInput_UsesInputAudioPart()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content
                {
                    Role = "user",
                    Parts =
                    [
                        new Part
                        {
                            InlineData = new Blob
                            {
                                MIMEType = "audio/wav",
                                Data = "UklGRiQAAABXQVZF",
                            },
                        },
                    ],
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Single(result.Messages);
        Assert.Single(result.Messages[0].Content!.MultipleContent!);
        Assert.Equal("input_audio", result.Messages[0].Content!.MultipleContent![0].Type);
        Assert.NotNull(result.Messages[0].Content!.MultipleContent![0].InputAudio);
        Assert.Equal("wav", result.Messages[0].Content!.MultipleContent![0].InputAudio!.Format);
        Assert.Equal("UklGRiQAAABXQVZF", result.Messages[0].Content!.MultipleContent![0].InputAudio!.Data);
    }

    // ==================================================================
    // TestConvertGeminiContentToLLMMessage_VideoFileData (inbound_convert_test.go:400-420)
    // ==================================================================

    [Fact]
    public void ConvertGeminiContentToLlmMessage_VideoFileData_UsesVideoUrl()
    {
        var content = new Content
        {
            Role = "user",
            Parts =
            [
                new Part
                {
                    FileData = new FileData
                    {
                        FileURI = "https://example.com/example.mp4",
                        MIMEType = "video/mp4",
                    },
                },
            ],
        };

        var msg = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(content, null);

        Assert.NotNull(msg);
        Assert.Single(msg.Content!.MultipleContent!);
        Assert.Equal("video_url", msg.Content.MultipleContent![0].Type);
        Assert.NotNull(msg.Content.MultipleContent![0].VideoUrl);
        Assert.Equal("https://example.com/example.mp4", msg.Content.MultipleContent![0].VideoUrl!.URL);
    }

    // ==================================================================
    // TestConvertGeminiToLLMRequest_ResponseFormat (inbound_convert_test.go:422-576)
    // ==================================================================

    [Fact]
    public void ConvertGeminiToLlmRequest_ResponseFormat_ResponseSchemaBecomesJsonSchema()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Generate JSON" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                ResponseSchema = Json("""{"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer"}}}"""),
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result.ResponseFormat);
        Assert.Equal("json_schema", result.ResponseFormat!.Type);
        Assert.NotNull(result.ResponseFormat.JsonSchema);

        var wrapper = JsonElement.Parse(result.ResponseFormat.JsonSchema!.Value.GetRawText());
        Assert.Equal("gemini_response", wrapper.GetProperty("name").GetString());
        AssertJsonEqual(
            """{"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer"}}}""",
            wrapper.GetProperty("schema"));
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_ResponseFormat_ResponseJsonSchemaAddsName()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Generate JSON" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                ResponseJsonSchema = Json("""{"type":"object","properties":{"answer":{"type":"string"}}}"""),
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result.ResponseFormat);
        var wrapper = JsonElement.Parse(result.ResponseFormat!.JsonSchema!.Value.GetRawText());
        Assert.Equal("gemini_response", wrapper.GetProperty("name").GetString());
        AssertJsonEqual(
            """{"type":"object","properties":{"answer":{"type":"string"}}}""",
            wrapper.GetProperty("schema"));
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_ResponseFormat_JsonMimeTypeBecomesJsonObject()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Generate JSON" }] },
            ],
            GenerationConfig = new GenerationConfig { ResponseMIMEType = "application/json" },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result.ResponseFormat);
        Assert.Equal("json_object", result.ResponseFormat!.Type);
        Assert.Null(result.ResponseFormat.JsonSchema);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_ResponseFormat_ResponseSchemaBeatsMimeType()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Generate JSON" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                ResponseMIMEType = "application/json",
                ResponseSchema = Json("""{"type":"object"}"""),
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result.ResponseFormat);
        Assert.Equal("json_schema", result.ResponseFormat!.Type);
        Assert.NotNull(result.ResponseFormat.JsonSchema);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_ResponseFormat_NoSchemaNoJsonMime()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Hello" }] },
            ],
            GenerationConfig = new GenerationConfig { MaxOutputTokens = 1024 },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Null(result.ResponseFormat);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_ResponseFormat_TextPlainMimeHasNoFormat()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Hello" }] },
            ],
            GenerationConfig = new GenerationConfig { ResponseMIMEType = "text/plain" },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Null(result.ResponseFormat);
    }

    // ==================================================================
    // TestConvertGeminiToLLMRequest_Tools (inbound_convert_test.go:578-827)
    // ==================================================================

    [Fact]
    public void ConvertGeminiToLlmRequest_Tools_FunctionDeclarations()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "What's the weather?" }] },
            ],
            Tools =
            [
                new GeminiTool
                {
                    FunctionDeclarations =
                    [
                        new FunctionDeclaration
                        {
                            Name = "get_weather",
                            Description = "Get weather information",
                            Parameters = Json("""{"type":"object","properties":{"location":{"type":"string"}}}"""),
                        },
                    ],
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Single(result.Tools!);
        Assert.Equal("function", result.Tools![0].Type);
        Assert.Equal("get_weather", result.Tools![0].Function.Name);
        Assert.Equal("Get weather information", result.Tools![0].Function.Description);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Tools_MultipleFunctionDeclarations()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Help me" }] },
            ],
            Tools =
            [
                new GeminiTool
                {
                    FunctionDeclarations =
                    [
                        new FunctionDeclaration { Name = "tool1", Description = "First tool" },
                        new FunctionDeclaration { Name = "tool2", Description = "Second tool" },
                    ],
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal(2, result.Tools!.Count);
        Assert.Equal("tool1", result.Tools![0].Function.Name);
        Assert.Equal("tool2", result.Tools![1].Function.Name);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Tools_GoogleSearchAndCodeExecution()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Search and run" }] },
            ],
            Tools =
            [
                new GeminiTool { GoogleSearch = new GeminiGoogleSearch() },
                new GeminiTool { CodeExecution = new CodeExecution() },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal(2, result.Tools!.Count);
        Assert.Equal(ToolTypes.GoogleSearch, result.Tools![0].Type);
        Assert.NotNull(result.Tools![0].Google);
        Assert.NotNull(result.Tools![0].Google!.Search);
        Assert.Equal(ToolTypes.GoogleCodeExecution, result.Tools![1].Type);
        Assert.NotNull(result.Tools![1].Google);
        Assert.NotNull(result.Tools![1].Google!.CodeExecution);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Tools_UrlContext()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Fetch URL content" }] },
            ],
            Tools = [new GeminiTool { UrlContext = new UrlContext() }],
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Single(result.Tools!);
        Assert.Equal(ToolTypes.GoogleUrlContext, result.Tools![0].Type);
        Assert.NotNull(result.Tools![0].Google);
        Assert.NotNull(result.Tools![0].Google!.UrlContext);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Tools_AllGroundingTools()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Use all tools" }] },
            ],
            Tools =
            [
                new GeminiTool { GoogleSearch = new GeminiGoogleSearch() },
                new GeminiTool { CodeExecution = new CodeExecution() },
                new GeminiTool { UrlContext = new UrlContext() },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.Equal(3, result.Tools!.Count);
        Assert.Equal(ToolTypes.GoogleSearch, result.Tools![0].Type);
        Assert.Equal(ToolTypes.GoogleCodeExecution, result.Tools![1].Type);
        Assert.Equal(ToolTypes.GoogleUrlContext, result.Tools[2].Type);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Tools_ToolConfigAuto()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Test" }] },
            ],
            ToolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig { Mode = "AUTO" },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result.ToolChoice);
        Assert.Equal("auto", result.ToolChoice!.ToolChoiceMode);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Tools_ToolConfigNone()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Test" }] },
            ],
            ToolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig { Mode = "NONE" },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result.ToolChoice);
        Assert.Equal("none", result.ToolChoice!.ToolChoiceMode);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Tools_ToolConfigAny()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Test" }] },
            ],
            ToolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig { Mode = "ANY" },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result.ToolChoice);
        Assert.Equal("required", result.ToolChoice!.ToolChoiceMode);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Tools_ToolConfigAnyWithSpecificFunction()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Test" }] },
            ],
            ToolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig
                {
                    Mode = "ANY",
                    AllowedFunctionNames = ["specific_function"],
                },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result.ToolChoice);
        Assert.NotNull(result.ToolChoice!.NamedToolChoice);
        Assert.Equal("function", result.ToolChoice.NamedToolChoice!.Type);
        Assert.Equal("specific_function", result.ToolChoice.NamedToolChoice.Function.Name);
    }

    // ==================================================================
    // TestConvertGeminiContentToLLMMessage (inbound_convert_test.go:829-1008)
    // ==================================================================

    [Fact]
    public void ConvertGeminiContentToLlmMessage_NilContent_ReturnsNull()
    {
        Assert.Null(GeminiInboundConvert.ConvertGeminiContentToLLMMessage(null, null));
    }

    [Fact]
    public void ConvertGeminiContentToLlmMessage_EmptyParts_ReturnsNull()
    {
        var input = new Content { Role = "user", Parts = [] };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.Null(result);
    }

    [Fact]
    public void ConvertGeminiContentToLlmMessage_TextContent()
    {
        var input = new Content { Role = "user", Parts = [new Part { Text = "Hello" }] };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.NotNull(result);
        Assert.Equal("user", result.Role);
        Assert.Equal("Hello", result.Content!.Content);
    }

    [Fact]
    public void ConvertGeminiContentToLlmMessage_ModelRoleConversion()
    {
        var input = new Content { Role = "model", Parts = [new Part { Text = "Response" }] };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.NotNull(result);
        Assert.Equal("assistant", result.Role);
    }

    [Fact]
    public void ConvertGeminiContentToLlmMessage_ThinkingContent()
    {
        var input = new Content
        {
            Role = "model",
            Parts =
            [
                new Part { Text = "Let me think...", Thought = true },
                new Part { Text = "The answer is 42" },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.NotNull(result);
        Assert.Equal("Let me think...", result.ReasoningContent);
        Assert.Equal("The answer is 42", result.Content!.Content);
    }

    [Fact]
    public void ConvertGeminiContentToLlmMessage_InlineImageData()
    {
        var input = new Content
        {
            Role = "user",
            Parts =
            [
                new Part
                {
                    InlineData = new Blob { MIMEType = "image/jpeg", Data = "base64data" },
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.NotNull(result);
        Assert.Single(result.Content!.MultipleContent!);
        Assert.Equal("image_url", result.Content.MultipleContent![0].Type);
        Assert.Equal("data:image/jpeg;base64,base64data", result.Content.MultipleContent![0].ImageUrl!.Url);
    }

    [Fact]
    public void ConvertGeminiContentToLlmMessage_FileData()
    {
        var input = new Content
        {
            Role = "user",
            Parts =
            [
                new Part
                {
                    FileData = new FileData { MIMEType = "image/png", FileURI = "gs://bucket/file.png" },
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.NotNull(result);
        Assert.Single(result.Content!.MultipleContent!);
        Assert.Equal("image_url", result.Content.MultipleContent![0].Type);
        Assert.Equal("gs://bucket/file.png", result.Content.MultipleContent![0].ImageUrl!.Url);
    }

    [Fact]
    public void ConvertGeminiContentToLlmMessage_FunctionCall()
    {
        var input = new Content
        {
            Role = "model",
            Parts =
            [
                new Part
                {
                    FunctionCall = new GeminiFunctionCall
                    {
                        Id = "call_123",
                        Name = "get_weather",
                        Args = LlmJson.Deserialize<Dictionary<string, JsonElement>>("""{"location":"NYC"}"""),
                    },
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.NotNull(result);
        Assert.Single(result.ToolCalls!);
        Assert.Equal("call_123", result.ToolCalls![0].Id);
        Assert.Equal("function", result.ToolCalls[0].Type);
        Assert.Equal("get_weather", result.ToolCalls[0].Function.Name);
        Assert.Contains("NYC", result.ToolCalls[0].Function.Arguments);
    }

    [Fact]
    public void ConvertGeminiContentToLlmMessage_FunctionResponse()
    {
        var input = new Content
        {
            Role = "user",
            Parts =
            [
                new Part
                {
                    FunctionResponse = new FunctionResponse
                    {
                        Id = "call_123",
                        Name = "get_weather",
                        Response = LlmJson.Deserialize<Dictionary<string, JsonElement>>("""{"temperature":72}"""),
                    },
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.NotNull(result);
        Assert.Equal("tool", result.Role);
        Assert.Equal("call_123", result.ToolCallId);
        Assert.Contains("72", result.Content!.Content);
    }

    [Fact]
    public void ConvertGeminiContentToLlmMessage_MultipleTextParts()
    {
        var input = new Content
        {
            Role = "user",
            Parts = [new Part { Text = "First part" }, new Part { Text = "Second part" }],
        };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.NotNull(result);
        Assert.Equal(2, result.Content!.MultipleContent!.Count);
        Assert.Equal("text", result.Content.MultipleContent![0].Type);
        Assert.Equal("First part", result.Content.MultipleContent![0].Text);
        Assert.Equal("Second part", result.Content.MultipleContent![1].Text);
    }

    // ==================================================================
    // TestConvertGeminiContentToLLMMessages_MultipleFunctionResponses
    // (inbound_convert_test.go:1010-1041)
    // ==================================================================

    [Fact]
    public void ConvertGeminiContentToLlmMessages_MultipleFunctionResponses_SplitsPerResponse()
    {
        var content = new Content
        {
            Role = "user",
            Parts =
            [
                new Part { Text = "Tool results:" },
                new Part
                {
                    FunctionResponse = new FunctionResponse
                    {
                        Id = "call_alpha",
                        Name = "tool_alpha",
                        Response = LlmJson.Deserialize<Dictionary<string, JsonElement>>("""{"value":1}"""),
                    },
                },
                new Part
                {
                    FunctionResponse = new FunctionResponse
                    {
                        Id = "call_beta",
                        Name = "tool_beta",
                        Response = LlmJson.Deserialize<Dictionary<string, JsonElement>>("""{"value":2}"""),
                    },
                },
                new Part { Text = "Continue." },
            ],
        };

        var messages = GeminiInboundConvert.ConvertGeminiContentToLLMMessages(content, null);

        Assert.NotNull(messages);
        Assert.Equal(4, messages.Count);

        Assert.Equal("user", messages[0].Role);
        Assert.Equal("Tool results:", messages[0].Content!.Content);
        Assert.Equal("tool", messages[1].Role);
        Assert.Equal("call_alpha", messages[1].ToolCallId);
        AssertJsonEqual("""{"value":1}""", Json(messages[1].Content!.Content!));
        Assert.Equal("tool", messages[2].Role);
        Assert.Equal("call_beta", messages[2].ToolCallId);
        AssertJsonEqual("""{"value":2}""", Json(messages[2].Content!.Content!));
        Assert.Equal("user", messages[3].Role);
        Assert.Equal("Continue.", messages[3].Content!.Content);
    }

    // ==================================================================
    // TestConvertGeminiContentToLLMMessage_ThoughtSignature
    // (inbound_convert_test.go:1043-1189)
    // ==================================================================

    [Fact]
    public void ConvertGeminiContentToLlmMessage_ThoughtSignature_FunctionCallWithSignature()
    {
        var input = new Content
        {
            Role = "model",
            Parts =
            [
                new Part
                {
                    FunctionCall = new GeminiFunctionCall
                    {
                        Id = "call_001",
                        Name = "check_flight",
                        Args = LlmJson.Deserialize<Dictionary<string, JsonElement>>("""{"flight":"AA100"}"""),
                    },
                    ThoughtSignature = "signature_A",
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.NotNull(result);
        Assert.Equal("signature_A", result.ReasoningSignature);
        Assert.Single(result.ToolCalls!);
        var tc = result.ToolCalls![0];
        Assert.Equal("call_001", tc.Id);
        Assert.Equal("check_flight", tc.Function.Name);
        Assert.NotNull(tc.TransformerMetadata);
        Assert.Equal(
            "signature_A",
            tc.TransformerMetadata![GeminiSignatures.TransformerMetadataKeyGoogleThoughtSignature].GetString());
    }

    [Fact]
    public void ConvertGeminiContentToLlmMessage_ThoughtSignature_ParallelCallsOnlyFirstSigned()
    {
        var input = new Content
        {
            Role = "model",
            Parts =
            [
                new Part
                {
                    FunctionCall = new GeminiFunctionCall
                    {
                        Id = "call_paris",
                        Name = "get_weather",
                        Args = LlmJson.Deserialize<Dictionary<string, JsonElement>>("""{"location":"Paris"}"""),
                    },
                    ThoughtSignature = "signature_parallel",
                },
                new Part
                {
                    FunctionCall = new GeminiFunctionCall
                    {
                        Id = "call_london",
                        Name = "get_weather",
                        Args = LlmJson.Deserialize<Dictionary<string, JsonElement>>("""{"location":"London"}"""),
                    },
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.NotNull(result);
        Assert.Equal("signature_parallel", result.ReasoningSignature);
        Assert.Equal(2, result.ToolCalls!.Count);

        // First call should have signature.
        var tc1 = result.ToolCalls[0];
        Assert.Equal("call_paris", tc1.Id);
        Assert.NotNull(tc1.TransformerMetadata);
        Assert.Equal(
            "signature_parallel",
            tc1.TransformerMetadata![GeminiSignatures.TransformerMetadataKeyGoogleThoughtSignature].GetString());

        // Second call should not have signature.
        var tc2 = result.ToolCalls[1];
        Assert.Equal("call_london", tc2.Id);
        Assert.Null(tc2.TransformerMetadata);
    }

    [Fact]
    public void ConvertGeminiContentToLlmMessage_ThoughtSignature_PrefixedSignatureNotDecoded()
    {
        var input = new Content
        {
            Role = "model",
            Parts =
            [
                new Part
                {
                    FunctionCall = new GeminiFunctionCall
                    {
                        Id = "call_003",
                        Name = "check_weather",
                        Args = LlmJson.Deserialize<Dictionary<string, JsonElement>>("""{"city":"Tokyo"}"""),
                    },
                    ThoughtSignature = "signature_prefixed",
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.NotNull(result);
        Assert.Equal("signature_prefixed", result.ReasoningSignature);
        var decoded = GeminiSignatures.DecodeGeminiThoughtSignature(result.ReasoningSignature);
        Assert.Null(decoded);
        Assert.Single(result.ToolCalls!);
        Assert.Equal("call_003", result.ToolCalls![0].Id);
        Assert.NotNull(result.ToolCalls[0].TransformerMetadata);
        Assert.Equal(
            "signature_prefixed",
            result.ToolCalls[0].TransformerMetadata![GeminiSignatures.TransformerMetadataKeyGoogleThoughtSignature].GetString());
    }

    [Fact]
    public void ConvertGeminiContentToLlmMessage_ThoughtSignature_FunctionCallWithoutSignature()
    {
        var input = new Content
        {
            Role = "model",
            Parts =
            [
                new Part
                {
                    FunctionCall = new GeminiFunctionCall
                    {
                        Id = "call_002",
                        Name = "get_weather",
                        Args = LlmJson.Deserialize<Dictionary<string, JsonElement>>("""{"location":"NYC"}"""),
                    },
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiContentToLLMMessage(input, null);

        Assert.NotNull(result);
        Assert.Null(result.ReasoningSignature);
        Assert.Single(result.ToolCalls!);
        var tc = result.ToolCalls![0];
        Assert.Equal("call_002", tc.Id);
        Assert.Null(tc.TransformerMetadata);
    }

    // ==================================================================
    // TestConvertLLMChoiceToGeminiCandidate_ThoughtSignature
    // (inbound_convert_test.go:1191-1439)
    // ==================================================================

    [Fact]
    public void ConvertLlmChoiceToGeminiCandidate_ThoughtSignature_PrefixedReasoningSignature()
    {
        var input = new Choice
        {
            Index = 0,
            Message = new Message
            {
                Role = "assistant",
                ReasoningSignature = GeminiSignatures.EncodeGeminiThoughtSignature("signature_prefixed"),
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_001",
                        Type = "function",
                        Function = new AxonHub.Llm.FunctionCall
                        {
                            Name = "check_flight",
                            Arguments = """{"flight":"AA100"}""",
                        },
                    },
                ],
            },
        };

        var result = GeminiInboundConvert.ConvertLLMChoiceToGeminiCandidate(input, false);

        Assert.NotNull(result);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content.Parts!);
        Assert.Equal("signature_prefixed", result.Content.Parts![0].ThoughtSignature);
    }

    [Fact]
    public void ConvertLlmChoiceToGeminiCandidate_ThoughtSignature_ToolCallWithReasoningSignature()
    {
        var input = new Choice
        {
            Index = 0,
            Message = new Message
            {
                Role = "assistant",
                ReasoningSignature = GeminiSignatures.EncodeGeminiThoughtSignature("signature_A"),
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_001",
                        Type = "function",
                        Function = new AxonHub.Llm.FunctionCall
                        {
                            Name = "check_flight",
                            Arguments = """{"flight":"AA100"}""",
                        },
                    },
                ],
            },
        };

        var result = GeminiInboundConvert.ConvertLLMChoiceToGeminiCandidate(input, false);

        Assert.NotNull(result);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content.Parts!);
        Assert.NotNull(result.Content.Parts![0].FunctionCall);
        Assert.Equal("check_flight", result.Content.Parts![0].FunctionCall!.Name);
        Assert.Equal("signature_A", result.Content.Parts![0].ThoughtSignature);
    }

    [Fact]
    public void ConvertLlmChoiceToGeminiCandidate_ThoughtSignature_MultipleToolCallsOnlyFirstSigned()
    {
        var input = new Choice
        {
            Index = 0,
            Message = new Message
            {
                Role = "assistant",
                ReasoningSignature = GeminiSignatures.EncodeGeminiThoughtSignature("signature_A"),
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_001",
                        Type = "function",
                        Function = new AxonHub.Llm.FunctionCall
                        {
                            Name = "check_flight",
                            Arguments = """{"flight":"AA100"}""",
                        },
                    },
                    new ToolCall
                    {
                        Id = "call_002",
                        Type = "function",
                        Function = new AxonHub.Llm.FunctionCall
                        {
                            Name = "book_taxi",
                            Arguments = """{"time":"10 AM"}""",
                        },
                    },
                ],
            },
        };

        var result = GeminiInboundConvert.ConvertLLMChoiceToGeminiCandidate(input, false);

        Assert.NotNull(result);
        Assert.Equal(2, result.Content!.Parts!.Count);

        Assert.Equal("check_flight", result.Content.Parts![0].FunctionCall!.Name);
        Assert.Equal("signature_A", result.Content.Parts![0].ThoughtSignature);

        Assert.Equal("book_taxi", result.Content.Parts![1].FunctionCall!.Name);
        Assert.Null(result.Content.Parts![1].ThoughtSignature);
    }

    [Fact]
    public void ConvertLlmChoiceToGeminiCandidate_ThoughtSignature_PerToolMetadata()
    {
        var input = new Choice
        {
            Index = 0,
            Message = new Message
            {
                Role = "assistant",
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_001",
                        Type = "function",
                        Function = new AxonHub.Llm.FunctionCall
                        {
                            Name = "check_flight",
                            Arguments = """{"flight":"AA100"}""",
                        },
                    },
                    new ToolCall
                    {
                        Id = "call_002",
                        Type = "function",
                        Function = new AxonHub.Llm.FunctionCall
                        {
                            Name = "book_taxi",
                            Arguments = """{"time":"10 AM"}""",
                        },
                        TransformerMetadata = new Dictionary<string, JsonElement>
                        {
                            [GeminiSignatures.TransformerMetadataKeyGoogleThoughtSignature] =
                                Json("\"signature_tool_2\""),
                        },
                    },
                ],
            },
        };

        var result = GeminiInboundConvert.ConvertLLMChoiceToGeminiCandidate(input, false);

        Assert.NotNull(result);
        Assert.Equal(2, result.Content!.Parts!.Count);
        Assert.Null(result.Content.Parts![0].ThoughtSignature);
        Assert.Equal("signature_tool_2", result.Content.Parts![1].ThoughtSignature);
    }

    [Fact]
    public void ConvertLlmChoiceToGeminiCandidate_ThoughtSignature_NonGeminiSignaturePassesThrough()
    {
        var input = new Choice
        {
            Index = 0,
            Message = new Message
            {
                Role = "assistant",
                ReasoningSignature = "encrypted_data",
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_001",
                        Type = "function",
                        Function = new AxonHub.Llm.FunctionCall
                        {
                            Name = "get_weather",
                            Arguments = """{"location":"NYC"}""",
                        },
                    },
                ],
            },
        };

        var result = GeminiInboundConvert.ConvertLLMChoiceToGeminiCandidate(input, false);

        Assert.NotNull(result);
        Assert.Single(result.Content!.Parts!);
        Assert.Equal("encrypted_data", result.Content.Parts![0].ThoughtSignature);
    }

    [Fact]
    public void ConvertLlmChoiceToGeminiCandidate_ThoughtSignature_ToolCallWithoutSignature()
    {
        var input = new Choice
        {
            Index = 0,
            Message = new Message
            {
                Role = "assistant",
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_001",
                        Type = "function",
                        Function = new AxonHub.Llm.FunctionCall
                        {
                            Name = "get_weather",
                            Arguments = """{"location":"NYC"}""",
                        },
                    },
                ],
            },
        };

        var result = GeminiInboundConvert.ConvertLLMChoiceToGeminiCandidate(input, false);

        Assert.NotNull(result);
        Assert.Single(result.Content!.Parts!);
        Assert.NotNull(result.Content.Parts![0].FunctionCall);
        Assert.Null(result.Content.Parts![0].ThoughtSignature);
    }

    [Fact]
    public void ConvertLlmChoiceToGeminiCandidate_ThoughtSignature_ParallelToolCallsStayEmpty()
    {
        var input = new Choice
        {
            Index = 0,
            Message = new Message
            {
                Role = "assistant",
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_paris",
                        Type = "function",
                        Function = new AxonHub.Llm.FunctionCall
                        {
                            Name = "get_weather",
                            Arguments = """{"location":"Paris"}""",
                        },
                    },
                    new ToolCall
                    {
                        Id = "call_london",
                        Type = "function",
                        Function = new AxonHub.Llm.FunctionCall
                        {
                            Name = "get_weather",
                            Arguments = """{"location":"London"}""",
                        },
                    },
                ],
            },
        };

        var result = GeminiInboundConvert.ConvertLLMChoiceToGeminiCandidate(input, false);

        Assert.NotNull(result);
        Assert.Equal(2, result.Content!.Parts!.Count);
        Assert.Null(result.Content.Parts![0].ThoughtSignature);
        Assert.Null(result.Content.Parts![1].ThoughtSignature);
    }

    [Fact]
    public void ConvertLlmChoiceToGeminiCandidate_ThoughtSignature_ReasoningSignatureWithoutParts()
    {
        var input = new Choice
        {
            Index = 0,
            Message = new Message
            {
                Role = "assistant",
                ReasoningSignature = GeminiSignatures.EncodeGeminiThoughtSignature("signature_without_parts"),
            },
        };

        var result = GeminiInboundConvert.ConvertLLMChoiceToGeminiCandidate(input, false);

        Assert.NotNull(result);
        Assert.NotNull(result.Content);
        // Go: omitempty drops the empty slice on the wire; the port nulls it.
        Assert.Null(result.Content.Parts);
    }

    // ==================================================================
    // TestConvertLLMToGeminiResponse_Basic (inbound_convert_test.go:1445-1640)
    // ==================================================================

    [Fact]
    public void ConvertLlmToGeminiResponse_Basic_SimpleResponse()
    {
        var input = new LlmResponse
        {
            Id = "resp_123",
            Model = "gemini-2.5-flash",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Message = new Message
                    {
                        Role = "assistant",
                        Content = new MessageContent { Content = "Hello!" },
                    },
                    FinishReason = "stop",
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertLLMToGeminiResponse(input, false);

        Assert.Equal("resp_123", result.ResponseId);
        Assert.Equal("gemini-2.5-flash", result.ModelVersion);
        Assert.Single(result.Candidates!);
        Assert.Equal("model", result.Candidates![0].Content!.Role);
        Assert.Single(result.Candidates![0].Content!.Parts!);
        Assert.Equal("Hello!", result.Candidates![0].Content!.Parts![0].Text);
        Assert.Equal("STOP", result.Candidates[0].FinishReason);
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_Basic_ResponseWithThinking()
    {
        var input = new LlmResponse
        {
            Id = "resp_think",
            Model = "gemini-2.5-flash",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Message = new Message
                    {
                        Role = "assistant",
                        ReasoningContent = "Let me think...",
                        Content = new MessageContent { Content = "The answer is 42" },
                    },
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertLLMToGeminiResponse(input, false);

        Assert.Equal(2, result.Candidates![0].Content!.Parts!.Count);
        Assert.True(result.Candidates![0].Content!.Parts![0].Thought);
        Assert.Equal("Let me think...", result.Candidates![0].Content!.Parts![0].Text);
        Assert.False(result.Candidates![0].Content!.Parts![1].Thought);
        Assert.Equal("The answer is 42", result.Candidates![0].Content!.Parts![1].Text);
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_Basic_ResponseWithToolCalls()
    {
        var input = new LlmResponse
        {
            Id = "resp_tool",
            Model = "gemini-2.5-flash",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Message = new Message
                    {
                        Role = "assistant",
                        Content = new MessageContent { Content = "I'll check the weather" },
                        ToolCalls =
                        [
                            new ToolCall
                            {
                                Id = "call_001",
                                Type = "function",
                                Function = new AxonHub.Llm.FunctionCall
                                {
                                    Name = "get_weather",
                                    Arguments = """{"location":"NYC"}""",
                                },
                            },
                        ],
                    },
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertLLMToGeminiResponse(input, false);

        Assert.Equal(2, result.Candidates![0].Content!.Parts!.Count);
        Assert.Equal("I'll check the weather", result.Candidates![0].Content!.Parts![0].Text);
        Assert.NotNull(result.Candidates![0].Content!.Parts![1].FunctionCall);
        Assert.Equal("call_001", result.Candidates![0].Content!.Parts![1].FunctionCall!.Id);
        Assert.Equal("get_weather", result.Candidates![0].Content!.Parts![1].FunctionCall!.Name);
        Assert.Equal("NYC", result.Candidates![0].Content!.Parts![1].FunctionCall!.Args!["location"].GetString());
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_Basic_ResponseWithUsage()
    {
        var input = new LlmResponse
        {
            Id = "resp_usage",
            Model = "gemini-2.5-flash",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Message = new Message
                    {
                        Role = "assistant",
                        Content = new MessageContent { Content = "Response" },
                    },
                },
            ],
            Usage = new Usage
            {
                PromptTokens = 100,
                CompletionTokens = 50,
                TotalTokens = 150,
                PromptTokensDetails = new PromptTokensDetails { CachedTokens = 20 },
                CompletionTokensDetails = new CompletionTokensDetails { ReasoningTokens = 30 },
            },
        };

        var result = GeminiInboundConvert.ConvertLLMToGeminiResponse(input, false);

        Assert.NotNull(result.UsageMetadata);
        Assert.Equal(100, result.UsageMetadata!.PromptTokenCount);
        Assert.Equal(20, result.UsageMetadata.CandidatesTokenCount);
        Assert.Equal(150, result.UsageMetadata.TotalTokenCount);
        Assert.Equal(20, result.UsageMetadata.CachedContentTokenCount);
        Assert.Equal(30, result.UsageMetadata.ThoughtsTokenCount);
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_Basic_MultipleContentParts()
    {
        var input = new LlmResponse
        {
            Id = "resp_multi",
            Model = "gemini-2.5-flash",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Message = new Message
                    {
                        Role = "assistant",
                        Content = new MessageContent
                        {
                            MultipleContent =
                            [
                                new MessageContentPart { Type = "text", Text = "First part" },
                                new MessageContentPart { Type = "text", Text = "Second part" },
                            ],
                        },
                    },
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertLLMToGeminiResponse(input, false);

        Assert.Equal(2, result.Candidates![0].Content!.Parts!.Count);
        Assert.Equal("First part", result.Candidates![0].Content!.Parts![0].Text);
        Assert.Equal("Second part", result.Candidates![0].Content!.Parts![1].Text);
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_Basic_ResponseWithDelta()
    {
        var input = new LlmResponse
        {
            Id = "resp_delta",
            Model = "gemini-2.5-flash",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Delta = new Message
                    {
                        Role = "assistant",
                        Content = new MessageContent { Content = "Streaming content" },
                    },
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertLLMToGeminiResponse(input, false);

        Assert.Single(result.Candidates![0].Content!.Parts!);
        Assert.Equal("Streaming content", result.Candidates![0].Content!.Parts![0].Text);
    }

    // ==================================================================
    // TestConvertLLMToGeminiResponse_FinishReasons (inbound_convert_test.go:1642-1674)
    // ==================================================================

    [Theory]
    [InlineData("stop", "STOP")]
    [InlineData("length", "MAX_TOKENS")]
    [InlineData("content_filter", "SAFETY")]
    [InlineData("tool_calls", "STOP")]
    [InlineData("unknown", "STOP")]
    public void ConvertLlmToGeminiResponse_FinishReasons(string llmReason, string expectedGeminiReason)
    {
        var input = new LlmResponse
        {
            Id = "resp_finish",
            Model = "gemini-2.5-flash",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Message = new Message
                    {
                        Role = "assistant",
                        Content = new MessageContent { Content = "Test" },
                    },
                    FinishReason = llmReason,
                },
            ],
        };

        var result = GeminiInboundConvert.ConvertLLMToGeminiResponse(input, false);

        Assert.Equal(expectedGeminiReason, result.Candidates![0].FinishReason);
    }

    // ==================================================================
    // TestConvertGeminiToLLMRequest_Testdata (inbound_convert_test.go:1680-1775)
    // ==================================================================

    [Fact]
    public void ConvertGeminiToLlmRequest_Testdata_SimpleRequest()
    {
        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(
            LoadGeminiRequest("gemini-simple.request.json"));

        Assert.Single(result.Messages);
        Assert.Equal("user", result.Messages[0].Role);
        Assert.Equal("Output 1-20, 5 each line", result.Messages[0].Content!.Content);
        Assert.Equal(4096L, result.MaxTokens);
        Assert.Equal("low", result.ReasoningEffort);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Testdata_ToolsRequest()
    {
        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(
            LoadGeminiRequest("gemini-tools.request.json"));

        Assert.Single(result.Messages);
        Assert.Equal("What is the weather in San Francisco, CA?", result.Messages[0].Content!.Content);
        Assert.Equal(2, result.Tools!.Count);
        Assert.Equal("get_coordinates", result.Tools![0].Function.Name);
        Assert.Equal("get_weather", result.Tools![1].Function.Name);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Testdata_ThinkingRequest()
    {
        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(
            LoadGeminiRequest("gemini-thinking.request.json"));

        Assert.Equal(3, result.Messages.Count);
        Assert.Equal("user", result.Messages[0].Role);
        Assert.Equal("assistant", result.Messages[1].Role);
        Assert.NotNull(result.Messages[1].ReasoningContent);
        Assert.Contains("25 * 47", result.Messages[1].ReasoningContent);
        Assert.Equal("user", result.Messages[2].Role);
        Assert.Equal("high", result.ReasoningEffort); // ThinkingLevel "high" takes priority
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_Testdata_ToolResultRequest()
    {
        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(
            LoadGeminiRequest("gemini-tool-result.request.json"));

        Assert.Equal(3, result.Messages.Count);
        Assert.Equal("user", result.Messages[0].Role);
        Assert.Equal(
            "I need help with some calculations and weather information for my trip planning. What's 100 / 4 and what's the weather in Tokyo?",
            result.Messages[0].Content!.Content);

        // Check assistant message with tool calls.
        Assert.Equal("assistant", result.Messages[1].Role);
        Assert.Equal(
            "I'll help you with both calculations and weather information for your trip planning.",
            result.Messages[1].Content!.Content);
        Assert.Equal(2, result.Messages[1].ToolCalls!.Count);
        Assert.Equal("call_00_IMEgeiAgajAZ47qX9hzSnjBP", result.Messages[1].ToolCalls![0].Id);
        Assert.Equal("calculate", result.Messages[1].ToolCalls![0].Function.Name);
        Assert.Equal("call_01_nyJz54P3fg9880GPr8O2QvER", result.Messages[1].ToolCalls![1].Id);
        Assert.Equal("get_current_weather", result.Messages[1].ToolCalls![1].Function.Name);

        // Check tool response message with ID completion.
        Assert.Equal("tool", result.Messages[2].Role);
        Assert.Equal("call_00_IMEgeiAgajAZ47qX9hzSnjBP", result.Messages[2].ToolCallId);
        Assert.Equal("calculate", result.Messages[2].ToolCallName);
        Assert.Contains("25", result.Messages[2].Content!.Content);

        // Check tools.
        Assert.Equal(2, result.Tools!.Count);
        Assert.Equal("calculate", result.Tools![0].Function.Name);
        Assert.Equal("get_current_weather", result.Tools![1].Function.Name);

        // Check temperature.
        Assert.InRange(result.Temperature!.Value, 0.69, 0.71);
    }

    // ==================================================================
    // TestConvertGeminiToLLMResponse_Testdata (inbound_convert_test.go:1777-1835)
    // ==================================================================

    [Fact]
    public void ConvertGeminiToLlmResponse_Testdata_SimpleResponse()
    {
        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(
            LoadGeminiResponse("gemini-simple.response.json"), false);

        Assert.Equal("G34qaY30KYSk0-kPkIX5UA", result.Id);
        Assert.Equal("gemini-2.5-flash", result.Model);
        Assert.Single(result.Choices);
        Assert.NotNull(result.Choices[0].Message!.ReasoningContent);
        Assert.Contains("Organizing Numbers", result.Choices![0].Message!.ReasoningContent);
        Assert.Contains("1 2 3 4 5", result.Choices![0].Message!.Content!.Content);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_Testdata_ToolsResponse()
    {
        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(
            LoadGeminiResponse("gemini-tools.response.json"), false);

        Assert.Equal("tools-response-001", result.Id);
        Assert.Single(result.Choices);
        Assert.Single(result.Choices[0].Message!.ToolCalls!);
        Assert.Equal("get_coordinates", result.Choices![0].Message!.ToolCalls![0].Function.Name);
    }

    [Fact]
    public void ConvertGeminiToLlmResponse_Testdata_ThinkingResponse()
    {
        var result = GeminiOutboundConvert.ConvertGeminiToLlmResponse(
            LoadGeminiResponse("gemini-thinking.response.json"), false);

        Assert.Equal("thinking-response-001", result.Id);
        Assert.Single(result.Choices);
        Assert.NotNull(result.Choices[0].Message!.ReasoningContent);
        Assert.Contains("1175 by 3", result.Choices![0].Message!.ReasoningContent);
        Assert.Contains("3525", result.Choices![0].Message!.Content!.Content);
        Assert.NotNull(result.Usage);
        Assert.NotNull(result.Usage!.CompletionTokensDetails);
        Assert.Equal(100, result.Usage.CompletionTokensDetails!.ReasoningTokens);
    }

    // ==================================================================
    // Round-trip Tests (inbound_convert_test.go:1837-2102)
    // ==================================================================

    [Theory]
    [InlineData("gemini-simple.request.json")]
    [InlineData("gemini-tools.request.json")]
    [InlineData("gemini-thinking.request.json")]
    public void RoundTrip_GeminiRequest_ToLlm_BackToGemini(string geminiFile)
    {
        var originalGemini = LoadGeminiRequest(geminiFile);

        // Convert Gemini -> LLM.
        var llmReq = GeminiInboundConvert.ConvertGeminiToLlmRequest(originalGemini);

        // Convert LLM -> Gemini.
        var convertedGemini = GeminiOutboundConvert.ConvertLlmToGeminiRequest(llmReq);

        // Verify key fields are preserved.
        Assert.Equal(originalGemini.Contents!.Count, convertedGemini.Contents!.Count);

        // Verify system instruction.
        if (originalGemini.SystemInstruction is not null)
        {
            Assert.NotNull(convertedGemini.SystemInstruction);
        }

        // Verify tools.
        if (originalGemini.Tools is { Count: > 0 })
        {
            Assert.NotEmpty(convertedGemini.Tools!);

            var originalToolCount = originalGemini.Tools.Sum(tool => tool.FunctionDeclarations?.Count ?? 0);
            var convertedToolCount = convertedGemini.Tools!.Sum(tool => tool.FunctionDeclarations?.Count ?? 0);

            Assert.Equal(originalToolCount, convertedToolCount);
        }

        // Verify generation config.
        if (originalGemini.GenerationConfig is not null)
        {
            Assert.NotNull(convertedGemini.GenerationConfig);

            if (originalGemini.GenerationConfig.MaxOutputTokens > 0)
            {
                Assert.Equal(
                    originalGemini.GenerationConfig.MaxOutputTokens,
                    convertedGemini.GenerationConfig!.MaxOutputTokens);
            }
        }
    }

    [Theory]
    [InlineData("gemini-simple.response.json")]
    [InlineData("gemini-tools.response.json")]
    [InlineData("gemini-thinking.response.json")]
    public void RoundTrip_GeminiResponse_ToLlm_BackToGemini(string geminiFile)
    {
        var originalGemini = LoadGeminiResponse(geminiFile);

        // Convert Gemini -> LLM (non-streaming).
        var llmResp = GeminiOutboundConvert.ConvertGeminiToLlmResponse(originalGemini, false);

        // Convert LLM -> Gemini (non-streaming).
        var convertedGemini = GeminiInboundConvert.ConvertLLMToGeminiResponse(llmResp, false);

        // Verify key fields are preserved.
        Assert.Equal(originalGemini.ResponseId, convertedGemini.ResponseId);
        Assert.Equal(originalGemini.ModelVersion, convertedGemini.ModelVersion);
        Assert.Equal(originalGemini.Candidates!.Count, convertedGemini.Candidates!.Count);

        // Verify candidate content.
        for (var i = 0; i < originalGemini.Candidates.Count; i++)
        {
            var originalCandidate = originalGemini.Candidates[i];
            var convertedCandidate = convertedGemini.Candidates[i];
            Assert.Equal(originalCandidate.Index, convertedCandidate.Index);

            if (originalCandidate.Content is not null)
            {
                Assert.NotNull(convertedCandidate.Content);
                Assert.Equal("model", convertedCandidate.Content!.Role);
            }
        }

        // Verify usage metadata.
        if (originalGemini.UsageMetadata is not null)
        {
            Assert.NotNull(convertedGemini.UsageMetadata);
            Assert.Equal(originalGemini.UsageMetadata.PromptTokenCount, convertedGemini.UsageMetadata!.PromptTokenCount);
            Assert.Equal(originalGemini.UsageMetadata.TotalTokenCount, convertedGemini.UsageMetadata.TotalTokenCount);
        }
    }

    [Theory]
    [InlineData("llm-simple.request.json")]
    [InlineData("llm-tools.request.json")]
    [InlineData("llm-thinking.request.json")]
    public void RoundTrip_LlmRequest_ToGemini_BackToLlm(string llmFile)
    {
        var originalLLM = LlmJson.Deserialize<LlmRequest>(File.ReadAllText(GeminiTestData(llmFile)))!;

        // Convert LLM -> Gemini.
        var geminiReq = GeminiOutboundConvert.ConvertLlmToGeminiRequest(originalLLM);

        // Convert Gemini -> LLM.
        var convertedLLM = GeminiInboundConvert.ConvertGeminiToLlmRequest(geminiReq);

        // Verify key fields are preserved.
        Assert.Equal(originalLLM.Messages.Count, convertedLLM.Messages.Count);

        // Verify max tokens.
        if (originalLLM.MaxTokens is not null)
        {
            Assert.Equal(originalLLM.MaxTokens, convertedLLM.MaxTokens);
        }

        // Verify tools (Go: len(nil) == 0 on both sides for tool-less requests).
        Assert.Equal(originalLLM.Tools?.Count ?? 0, convertedLLM.Tools?.Count ?? 0);

        if (originalLLM.Tools is { } originalTools)
        {
            for (var i = 0; i < originalTools.Count; i++)
            {
                Assert.Equal(originalTools[i].Function.Name, convertedLLM.Tools![i].Function.Name);
                Assert.Equal(originalTools[i].Function.Description, convertedLLM.Tools![i].Function.Description);
            }
        }

        // Verify message roles.
        for (var i = 0; i < originalLLM.Messages.Count; i++)
        {
            Assert.Equal(originalLLM.Messages[i].Role, convertedLLM.Messages[i].Role);
        }
    }

    [Theory]
    [InlineData("llm-simple.response.json")]
    [InlineData("llm-tools.response.json")]
    [InlineData("llm-thinking.response.json")]
    public void RoundTrip_LlmResponse_ToGemini_BackToLlm(string llmFile)
    {
        var originalLLM = LlmJson.Deserialize<LlmResponse>(File.ReadAllText(GeminiTestData(llmFile)))!;

        // Convert LLM -> Gemini (non-streaming).
        var geminiResp = GeminiInboundConvert.ConvertLLMToGeminiResponse(originalLLM, false);

        // Convert Gemini -> LLM (non-streaming).
        var convertedLLM = GeminiOutboundConvert.ConvertGeminiToLlmResponse(geminiResp, false);

        // Verify key fields are preserved.
        Assert.Equal(originalLLM.Id, convertedLLM.Id);
        Assert.Equal(originalLLM.Model, convertedLLM.Model);
        Assert.Equal(originalLLM.Choices.Count, convertedLLM.Choices.Count);

        // Verify choice content.
        for (var i = 0; i < originalLLM.Choices.Count; i++)
        {
            var originalChoice = originalLLM.Choices[i];
            var convertedChoice = convertedLLM.Choices[i];
            Assert.Equal(originalChoice.Index, convertedChoice.Index);

            if (originalChoice.Message is not null)
            {
                Assert.NotNull(convertedChoice.Message);
                Assert.Equal("assistant", convertedChoice.Message!.Role);

                // Verify tool calls (Go: len(nil) == 0 for plain responses).
                Assert.Equal(
                    originalChoice.Message.ToolCalls?.Count ?? 0,
                    convertedChoice.Message.ToolCalls?.Count ?? 0);

                if (originalChoice.Message.ToolCalls is { } originalToolCalls)
                {
                    for (var j = 0; j < originalToolCalls.Count; j++)
                    {
                        Assert.Equal(
                            originalToolCalls[j].Function.Name,
                            convertedChoice.Message.ToolCalls![j].Function.Name);
                    }
                }
            }
        }

        // Verify usage.
        if (originalLLM.Usage is not null)
        {
            Assert.NotNull(convertedLLM.Usage);
            Assert.Equal(originalLLM.Usage.PromptTokens, convertedLLM.Usage!.PromptTokens);
            Assert.Equal(originalLLM.Usage.TotalTokens, convertedLLM.Usage.TotalTokens);
        }
    }

    // ==================================================================
    // TestConvertLLMToGeminiResponse_CitationMetadataFallback
    // (inbound_convert_test.go:2104-2130)
    // ==================================================================

    [Fact]
    public void ConvertLlmToGeminiResponse_CitationMetadataFallback()
    {
        var resp = GeminiInboundConvert.ConvertLLMToGeminiResponse(
            new LlmResponse
            {
                Id = "resp_gemini_fallback",
                Model = "gemini-2.5-flash",
                Choices =
                [
                    new Choice
                    {
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Grounded answer" },
                            Annotations =
                            [
                                new Annotation
                                {
                                    Type = "url_citation",
                                    StartIndex = 0,
                                    EndIndex = 8,
                                    UrlCitation = new UrlCitation
                                    {
                                        Url = "https://example.com/fallback",
                                        Title = "Fallback Source",
                                    },
                                },
                            ],
                        },
                    },
                ],
            },
            false);

        Assert.Single(resp.Candidates!);
        Assert.Null(resp.Candidates![0].GroundingMetadata);
        Assert.NotNull(resp.Candidates![0].CitationMetadata);
        Assert.Single(resp.Candidates![0].CitationMetadata!.Citations!);
        Assert.Equal("https://example.com/fallback", resp.Candidates![0].CitationMetadata!.Citations![0].URI);
        Assert.Equal("Fallback Source", resp.Candidates![0].CitationMetadata!.Citations![0].Title);
        Assert.Equal(0, resp.Candidates![0].CitationMetadata!.Citations![0].StartIndex);
        Assert.Equal(8, resp.Candidates![0].CitationMetadata!.Citations![0].EndIndex);
    }

    // ==================================================================
    // TestConvertLLMToGeminiResponse_GroundingMetadataTakesPrecedenceOverAnnotations
    // (inbound_convert_test.go:2132-2155)
    // ==================================================================

    [Fact]
    public void ConvertLlmToGeminiResponse_GroundingMetadataTakesPrecedenceOverAnnotations()
    {
        var resp = GeminiInboundConvert.ConvertLLMToGeminiResponse(
            new LlmResponse
            {
                Id = "resp_gemini_priority",
                Model = "gemini-2.5-flash",
                Choices =
                [
                    new Choice
                    {
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Grounded answer" },
                            Annotations =
                            [
                                new Annotation
                                {
                                    Type = "url_citation",
                                    UrlCitation = new UrlCitation
                                    {
                                        Url = "https://example.com/ignore",
                                        Title = "Ignore Me",
                                    },
                                },
                            ],
                        },
                        TransformerMetadata = new Dictionary<string, JsonElement>
                        {
                            [GeminiOutboundConvert.TransformerMetadataKeyGroundingMetadata] =
                                JsonSerializer.SerializeToElement(
                                    new GroundingMetadata { WebSearchQueries = ["authoritative query"] },
                                    LlmJson.Options),
                        },
                    },
                ],
            },
            false);

        Assert.Single(resp.Candidates!);
        Assert.NotNull(resp.Candidates![0].GroundingMetadata);
        Assert.Equal(["authoritative query"], resp.Candidates![0].GroundingMetadata!.WebSearchQueries);
        Assert.Null(resp.Candidates![0].CitationMetadata);
    }

    // ==================================================================
    // TestConvertLLMToGeminiResponse_GroundingMetadata (inbound_convert_test.go:2157-2392)
    // ==================================================================

    private static Dictionary<string, JsonElement> Grounding(GroundingMetadata metadata)
        => new()
        {
            [GeminiOutboundConvert.TransformerMetadataKeyGroundingMetadata] =
                JsonSerializer.SerializeToElement(metadata, LlmJson.Options),
        };

    [Fact]
    public void ConvertLlmToGeminiResponse_GroundingMetadata_WebSearch()
    {
        var resp = GeminiInboundConvert.ConvertLLMToGeminiResponse(
            new LlmResponse
            {
                Id = "resp_grounding",
                Model = "gemini-2.5-flash",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Based on my search, here is the answer." },
                        },
                        FinishReason = "stop",
                        TransformerMetadata = Grounding(new GroundingMetadata
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
                                    GroundingChunkIndices = [0],
                                    ConfidenceScores = [0.95f],
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
                        }),
                    },
                ],
            },
            false);

        Assert.Single(resp.Candidates!);
        Assert.NotNull(resp.Candidates![0].GroundingMetadata);

        var gm = resp.Candidates![0].GroundingMetadata!;
        Assert.Equal(["latest news about AI"], gm.WebSearchQueries);
        Assert.Single(gm.GroundingChunks!);
        Assert.Equal("https://example.com/article1", gm.GroundingChunks![0].Web!.URI);
        Assert.Equal("AI News Article", gm.GroundingChunks[0].Web!.Title);
        Assert.Single(gm.GroundingSupports!);
        Assert.Equal([0], gm.GroundingSupports![0].GroundingChunkIndices);
        Assert.NotNull(gm.SearchEntryPoint);
        Assert.Equal("<div>Search results</div>", gm.SearchEntryPoint!.RenderedContent);
        Assert.NotNull(gm.RetrievalMetadata);
        Assert.InRange(gm.RetrievalMetadata!.GoogleSearchDynamicRetrievalScore, 0.91f, 0.93f);
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_GroundingMetadata_RetrievedContext()
    {
        var resp = GeminiInboundConvert.ConvertLLMToGeminiResponse(
            new LlmResponse
            {
                Id = "resp_retrieval",
                Model = "gemini-2.5-flash",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "According to the document..." },
                        },
                        FinishReason = "stop",
                        TransformerMetadata = Grounding(new GroundingMetadata
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
                        }),
                    },
                ],
            },
            false);

        Assert.NotNull(resp.Candidates![0].GroundingMetadata);
        var gm = resp.Candidates![0].GroundingMetadata!;
        Assert.Single(gm.GroundingChunks!);
        Assert.NotNull(gm.GroundingChunks![0].RetrievedContext);
        Assert.Equal("gs://bucket/document.pdf", gm.GroundingChunks[0].RetrievedContext!.URI);
        Assert.Equal("Important Document", gm.GroundingChunks[0].RetrievedContext!.Title);
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_GroundingMetadata_WithoutGrounding()
    {
        var resp = GeminiInboundConvert.ConvertLLMToGeminiResponse(
            new LlmResponse
            {
                Id = "resp_no_grounding",
                Model = "gemini-2.5-flash",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Simple response without grounding." },
                        },
                        FinishReason = "stop",
                    },
                ],
            },
            false);

        Assert.Null(resp.Candidates![0].GroundingMetadata);
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_GroundingMetadata_Streaming()
    {
        var resp = GeminiInboundConvert.ConvertLLMToGeminiResponse(
            new LlmResponse
            {
                Id = "resp_stream_grounding",
                Model = "gemini-2.5-flash",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Streaming chunk with grounding." },
                        },
                        TransformerMetadata = Grounding(new GroundingMetadata
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
                        }),
                    },
                ],
            },
            true);

        Assert.NotNull(resp.Candidates![0].GroundingMetadata);
        var gm = resp.Candidates![0].GroundingMetadata!;
        Assert.Equal(["streaming search query"], gm.WebSearchQueries);
        Assert.Single(gm.GroundingChunks!);
        Assert.Equal("https://stream.example.com", gm.GroundingChunks![0].Web!.URI);
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_GroundingMetadata_MultipleCandidates()
    {
        var resp = GeminiInboundConvert.ConvertLLMToGeminiResponse(
            new LlmResponse
            {
                Id = "resp_multi_candidates",
                Model = "gemini-2.5-flash",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "First candidate response." },
                        },
                        TransformerMetadata = Grounding(
                            new GroundingMetadata { WebSearchQueries = ["query for candidate 1"] }),
                    },
                    new Choice
                    {
                        Index = 1,
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Second candidate response." },
                        },
                        TransformerMetadata = Grounding(
                            new GroundingMetadata { WebSearchQueries = ["query for candidate 2"] }),
                    },
                ],
            },
            false);

        Assert.Equal(2, resp.Candidates!.Count);
        Assert.NotNull(resp.Candidates![0].GroundingMetadata);
        Assert.NotNull(resp.Candidates![1].GroundingMetadata);
        Assert.Equal(["query for candidate 1"], resp.Candidates![0].GroundingMetadata!.WebSearchQueries);
        Assert.Equal(["query for candidate 2"], resp.Candidates![1].GroundingMetadata!.WebSearchQueries);
    }

    // ==================================================================
    // TestConvertGeminiToLLMRequest_SafetySettings (inbound_convert_test.go:2398-2530)
    // ==================================================================

    [Fact]
    public void ConvertGeminiToLlmRequest_SafetySettings_StoredInMetadata()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Hello, Gemini!" }] },
            ],
            SafetySettings =
            [
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
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result);
        Assert.NotNull(result.TransformerMetadata);
        var safetySettings = Xmap.GetPtr<List<SafetySetting>>(
            result.TransformerMetadata, GeminiConvert.TransformerMetadataKeySafetySettings);
        Assert.Equal(2, safetySettings!.Count);
        Assert.Equal("HARM_CATEGORY_HARASSMENT", safetySettings[0].Category);
        Assert.Equal("BLOCK_LOW_AND_ABOVE", safetySettings[0].Threshold);
        Assert.Equal("HARM_CATEGORY_HATE_SPEECH", safetySettings[1].Category);
        Assert.Equal("BLOCK_MEDIUM_AND_ABOVE", safetySettings[1].Threshold);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_SafetySettings_WithoutSettings()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Hello, Gemini!" }] },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result);
        Assert.Null(result.TransformerMetadata);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_SafetySettings_EmptySettings()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Hello, Gemini!" }] },
            ],
            SafetySettings = [],
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result);
        Assert.Null(result.TransformerMetadata);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_SafetySettings_AllCategories()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Hello, Gemini!" }] },
            ],
            SafetySettings =
            [
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
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result);
        Assert.NotNull(result.TransformerMetadata);
        var safetySettings = Xmap.GetPtr<List<SafetySetting>>(
            result.TransformerMetadata, GeminiConvert.TransformerMetadataKeySafetySettings);
        Assert.Equal(4, safetySettings!.Count);
        Assert.Equal("HARM_CATEGORY_HARASSMENT", safetySettings[0].Category);
        Assert.Equal("BLOCK_NONE", safetySettings[0].Threshold);
        Assert.Equal("HARM_CATEGORY_HATE_SPEECH", safetySettings[1].Category);
        Assert.Equal("BLOCK_LOW_AND_ABOVE", safetySettings[1].Threshold);
        Assert.Equal("HARM_CATEGORY_SEXUALLY_EXPLICIT", safetySettings[2].Category);
        Assert.Equal("BLOCK_MEDIUM_AND_ABOVE", safetySettings[2].Threshold);
        Assert.Equal("HARM_CATEGORY_DANGEROUS_CONTENT", safetySettings[3].Category);
        Assert.Equal("BLOCK_HIGH_AND_ABOVE", safetySettings[3].Threshold);
    }

    // ==================================================================
    // TestConvertGeminiToLLMRequest_ImageConfig (inbound_convert_test.go:2614-2724)
    // ==================================================================

    [Fact]
    public void ConvertGeminiToLlmRequest_ImageConfig_StoredInMetadata()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Generate an image" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                ImageConfig = new ImageConfig { AspectRatio = "16:9", ImageSize = "2K" },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result);
        Assert.NotNull(result.TransformerMetadata);
        var imageConfig = Xmap.GetPtr<ImageConfig>(
            result.TransformerMetadata, GeminiConvert.TransformerMetadataKeyImageConfig);
        Assert.NotNull(imageConfig);
        Assert.Equal("16:9", imageConfig.AspectRatio);
        Assert.Equal("2K", imageConfig.ImageSize);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_ImageConfig_WithoutImageConfig()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Hello, Gemini!" }] },
            ],
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result);
        Assert.Null(result.TransformerMetadata);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_ImageConfig_NilImageConfig()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Hello, Gemini!" }] },
            ],
            GenerationConfig = new GenerationConfig { Temperature = 0.7 },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result);
        // TransformerMetadata should be null since ImageConfig is nil.
        Assert.Null(result.TransformerMetadata);
    }

    [Fact]
    public void ConvertGeminiToLlmRequest_ImageConfig_OnlyAspectRatio()
    {
        var input = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = "user", Parts = [new Part { Text = "Generate an image" }] },
            ],
            GenerationConfig = new GenerationConfig
            {
                ImageConfig = new ImageConfig { AspectRatio = "1:1" },
            },
        };

        var result = GeminiInboundConvert.ConvertGeminiToLlmRequest(input);

        Assert.NotNull(result);
        Assert.NotNull(result.TransformerMetadata);
        var imageConfig = Xmap.GetPtr<ImageConfig>(
            result.TransformerMetadata, GeminiConvert.TransformerMetadataKeyImageConfig);
        Assert.NotNull(imageConfig);
        Assert.Equal("1:1", imageConfig.AspectRatio);
        Assert.Empty(imageConfig.ImageSize);
    }

    // ==================================================================
    // TestConvertLLMToGeminiResponse_GroundingMetadata_Additional
    // (inbound_convert_test.go:2774-3103)
    // ==================================================================

    [Fact]
    public void ConvertLlmToGeminiResponse_GroundingMetadata_AllFieldsPopulated()
    {
        var resp = GeminiInboundConvert.ConvertLLMToGeminiResponse(
            new LlmResponse
            {
                Id = "resp_all_fields",
                Model = "gemini-2.5-flash",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent
                            {
                                Content = "Comprehensive response with all grounding fields.",
                            },
                        },
                        FinishReason = "stop",
                        TransformerMetadata = Grounding(new GroundingMetadata
                        {
                            WebSearchQueries = ["comprehensive inbound search"],
                            GroundingChunks =
                            [
                                new GroundingChunk
                                {
                                    Web = new GroundingChunkWeb
                                    {
                                        URI = "https://inbound.example.com/comprehensive",
                                        Title = "Comprehensive Inbound Article",
                                        Domain = "inbound.example.com",
                                    },
                                },
                                new GroundingChunk
                                {
                                    RetrievedContext = new GroundingChunkRetrievedContext
                                    {
                                        URI = "gs://inbound-bucket/comprehensive.pdf",
                                        Title = "Comprehensive Inbound Document",
                                        Text = "Comprehensive inbound document content.",
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
                                    ConfidenceScores = [0.97f, 0.91f],
                                },
                            ],
                            SearchEntryPoint = new SearchEntryPoint
                            {
                                RenderedContent = "<div>Comprehensive inbound search entry point</div>",
                            },
                            RetrievalMetadata = new RetrievalMetadata
                            {
                                GoogleSearchDynamicRetrievalScore = 0.94f,
                            },
                        }),
                    },
                ],
            },
            false);

        Assert.Single(resp.Candidates!);
        Assert.NotNull(resp.Candidates![0].GroundingMetadata);

        var gm = resp.Candidates![0].GroundingMetadata!;
        Assert.Equal(["comprehensive inbound search"], gm.WebSearchQueries);
        Assert.Equal(2, gm.GroundingChunks!.Count);
        Assert.NotNull(gm.GroundingChunks![0].Web);
        Assert.NotNull(gm.GroundingChunks[1].RetrievedContext);
        Assert.Single(gm.GroundingSupports!);
        Assert.NotNull(gm.SearchEntryPoint);
        Assert.NotNull(gm.RetrievalMetadata);
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_GroundingMetadata_EmptyArrays()
    {
        var resp = GeminiInboundConvert.ConvertLLMToGeminiResponse(
            new LlmResponse
            {
                Id = "resp_empty_arrays",
                Model = "gemini-2.5-flash",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Response with empty grounding arrays." },
                        },
                        FinishReason = "stop",
                        TransformerMetadata = Grounding(new GroundingMetadata
                        {
                            WebSearchQueries = [],
                            GroundingChunks = [],
                            GroundingSupports = [],
                        }),
                    },
                ],
            },
            false);

        Assert.Single(resp.Candidates!);
        Assert.NotNull(resp.Candidates![0].GroundingMetadata);

        var gm = resp.Candidates![0].GroundingMetadata!;
        Assert.Empty(gm.WebSearchQueries!);
        Assert.Empty(gm.GroundingChunks!);
        Assert.Empty(gm.GroundingSupports!);
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_GroundingMetadata_MixedCandidates()
    {
        var resp = GeminiInboundConvert.ConvertLLMToGeminiResponse(
            new LlmResponse
            {
                Id = "resp_mixed_grounding",
                Model = "gemini-2.5-flash",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Response with grounding." },
                        },
                        TransformerMetadata = Grounding(
                            new GroundingMetadata { WebSearchQueries = ["inbound candidate 1 search"] }),
                    },
                    new Choice
                    {
                        Index = 1,
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Response without grounding." },
                        },
                    },
                    new Choice
                    {
                        Index = 2,
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Another response with grounding." },
                        },
                        TransformerMetadata = Grounding(new GroundingMetadata
                        {
                            RetrievalMetadata = new RetrievalMetadata
                            {
                                GoogleSearchDynamicRetrievalScore = 0.85f,
                            },
                        }),
                    },
                ],
            },
            false);

        Assert.Equal(3, resp.Candidates!.Count);

        // First choice has grounding metadata.
        Assert.NotNull(resp.Candidates![0].GroundingMetadata);
        Assert.Equal(["inbound candidate 1 search"], resp.Candidates![0].GroundingMetadata!.WebSearchQueries);

        // Second choice has no grounding metadata.
        Assert.Null(resp.Candidates![1].GroundingMetadata);

        // Third choice has grounding metadata.
        Assert.NotNull(resp.Candidates![2].GroundingMetadata);
        Assert.NotNull(resp.Candidates![2].GroundingMetadata!.RetrievalMetadata);
        Assert.InRange(
            resp.Candidates![2].GroundingMetadata!.RetrievalMetadata!.GoogleSearchDynamicRetrievalScore, 0.84f, 0.86f);
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_GroundingMetadata_StreamingMultipleChunks()
    {
        var resp = GeminiInboundConvert.ConvertLLMToGeminiResponse(
            new LlmResponse
            {
                Id = "resp_stream_multi_chunks",
                Model = "gemini-2.5-flash",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Streaming with multiple chunks." },
                        },
                        TransformerMetadata = Grounding(new GroundingMetadata
                        {
                            GroundingChunks =
                            [
                                new GroundingChunk
                                {
                                    Web = new GroundingChunkWeb
                                    {
                                        URI = "https://inbound-source1.example.com",
                                        Title = "Inbound Source 1",
                                    },
                                },
                                new GroundingChunk
                                {
                                    Web = new GroundingChunkWeb
                                    {
                                        URI = "https://inbound-source2.example.com",
                                        Title = "Inbound Source 2",
                                    },
                                },
                                new GroundingChunk
                                {
                                    RetrievedContext = new GroundingChunkRetrievedContext
                                    {
                                        URI = "gs://inbound-bucket/internal.pdf",
                                        Title = "Inbound Internal Document",
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
                                    ConfidenceScores = [0.94f, 0.86f, 0.90f],
                                },
                            ],
                        }),
                    },
                ],
            },
            true);

        Assert.Single(resp.Candidates!);
        Assert.NotNull(resp.Candidates![0].GroundingMetadata);

        var gm = resp.Candidates![0].GroundingMetadata!;
        Assert.Equal(3, gm.GroundingChunks!.Count);
        Assert.NotNull(gm.GroundingChunks![0].Web);
        Assert.NotNull(gm.GroundingChunks[1].Web);
        Assert.NotNull(gm.GroundingChunks[2].RetrievedContext);
        Assert.Single(gm.GroundingSupports!);
        Assert.Equal([0, 1, 2], gm.GroundingSupports![0].GroundingChunkIndices);
    }

    [Fact]
    public void ConvertLlmToGeminiResponse_GroundingMetadata_ComplexSegmentDetails()
    {
        var resp = GeminiInboundConvert.ConvertLLMToGeminiResponse(
            new LlmResponse
            {
                Id = "resp_complex_segments",
                Model = "gemini-2.5-flash",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Message = new Message
                        {
                            Role = "assistant",
                            Content = new MessageContent { Content = "Complex response with detailed segments." },
                        },
                        FinishReason = "stop",
                        TransformerMetadata = Grounding(new GroundingMetadata
                        {
                            GroundingChunks =
                            [
                                new GroundingChunk
                                {
                                    Web = new GroundingChunkWeb
                                    {
                                        URI = "https://complex.example.com",
                                        Title = "Complex Source",
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
                                        EndIndex = 15,
                                        PartIndex = 0,
                                        Text = "Complex response",
                                    },
                                    GroundingChunkIndices = [0],
                                    ConfidenceScores = [0.96f],
                                },
                                new GroundingSupport
                                {
                                    Segment = new Segment
                                    {
                                        StartIndex = 16,
                                        EndIndex = 41,
                                        PartIndex = 0,
                                        Text = "with detailed segments",
                                    },
                                    GroundingChunkIndices = [0],
                                    ConfidenceScores = [0.88f],
                                },
                            ],
                        }),
                    },
                ],
            },
            false);

        Assert.Single(resp.Candidates!);
        Assert.NotNull(resp.Candidates![0].GroundingMetadata);

        var gm = resp.Candidates![0].GroundingMetadata!;
        Assert.Equal(2, gm.GroundingSupports!.Count);

        var seg1 = gm.GroundingSupports[0].Segment!;
        Assert.Equal(0, seg1.StartIndex);
        Assert.Equal(15, seg1.EndIndex);
        Assert.Equal("Complex response", seg1.Text);

        var seg2 = gm.GroundingSupports[1].Segment!;
        Assert.Equal(16, seg2.StartIndex);
        Assert.Equal(41, seg2.EndIndex);
        Assert.Equal("with detailed segments", seg2.Text);
    }
}
