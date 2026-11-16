using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.OpenAI;
using GeminiFunctionCall = AxonHub.Llm.Transformer.Gemini.FunctionCall;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the Gemini outbound transformer core
/// (llm/transformer/gemini/outbound_test.go): config cleanup, URL building,
/// constructors, extra-body thinking config, reasoning-effort fallback,
/// multi-tool-call responses, and Vertex function-ID clearing. The extra-body
/// "invalid JSON" case is adapted to a non-object element because the port's
/// <see cref="LlmRequest.ExtraBody"/> is a parsed <see cref="JsonElement"/>
/// (C113).
/// </summary>
public sealed class GeminiOutboundTransformerCoreTests
{
    private static GeminiOutboundTransformer NewTransformer()
        => GeminiOutboundTransformer.CreateWithConfig(new GeminiOutboundConfig
        {
            BaseUrl = "https://generativelanguage.googleapis.com",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

    private static LlmRequest ChatRequest(
        string userText,
        string? reasoningEffort = null,
        JsonElement? extraBody = null) => new()
        {
            Model = "gemini-2.5-flash",
            Messages = [UserMessage(userText)],
            ReasoningEffort = reasoningEffort,
            ExtraBody = extraBody,
        };

    private static Message UserMessage(string text) => new()
    {
        Role = "user",
        Content = new MessageContent { Content = text },
    };

    // ------------------------------------------------------------ TestClenupConfig (outbound_test.go:16-110)

    [Fact]
    public void ClenupConfig()
    {
        (string Name, GeminiOutboundConfig Input, string ExpectedBase, string ExpectedVersion)[] cases =
        [
            ("empty config uses defaults",
                new GeminiOutboundConfig(),
                GeminiConstants.DefaultBaseURL, "v1beta"),
            ("config with base URL only",
                new GeminiOutboundConfig { BaseUrl = "https://custom.example.com" },
                "https://custom.example.com", "v1beta"),
            ("config with API version only",
                new GeminiOutboundConfig { ApiVersion = "v1" },
                GeminiConstants.DefaultBaseURL, "v1"),
            ("config with base URL containing v1beta suffix",
                new GeminiOutboundConfig { BaseUrl = "https://generativelanguage.googleapis.com/v1beta" },
                "https://generativelanguage.googleapis.com", "v1beta"),
            ("config with base URL containing v1 suffix",
                new GeminiOutboundConfig { BaseUrl = "https://generativelanguage.googleapis.com/v1" },
                "https://generativelanguage.googleapis.com", "v1"),
            ("config with API version and base URL with version suffix",
                new GeminiOutboundConfig
                {
                    BaseUrl = "https://example.com/v1beta",
                    ApiVersion = "v1",
                },
                "https://example.com/v1beta", "v1"),
            ("config with trailing slash in base URL",
                new GeminiOutboundConfig { BaseUrl = "https://generativelanguage.googleapis.com/" },
                "https://generativelanguage.googleapis.com/", "v1beta"),
            ("complete config",
                new GeminiOutboundConfig
                {
                    BaseUrl = "https://custom.api.com",
                    ApiVersion = "v1",
                },
                "https://custom.api.com", "v1"),
        ];

        foreach (var (_, input, expectedBase, expectedVersion) in cases)
        {
            var result = input.Cleanup();
            Assert.Equal(expectedBase, result.BaseUrl);
            Assert.Equal(expectedVersion, result.ApiVersion);
            Assert.Equal(input.PlatformType, result.PlatformType);
            Assert.Equal(input.EndpointPath, result.EndpointPath);
            Assert.Same(input.ApiKeyProvider, result.ApiKeyProvider);
        }
    }

    // ------------------------------------------------------------ TransformRequest metadata (outbound_test.go:112-148)

    [Fact]
    public void TransformRequest_AccountIdentity()
    {
        var outbound = GeminiOutboundTransformer.CreateWithConfig(new GeminiOutboundConfig
        {
            BaseUrl = "https://generativelanguage.googleapis.com",
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
        });

        var hreq = outbound.TransformRequest(ChatRequest("hi"));

        Assert.Null(hreq.Metadata);
    }

    [Fact]
    public void TransformRequest_OmitsMetadataWhenEmpty()
    {
        var outbound = GeminiOutboundTransformer.CreateWithConfig(new GeminiOutboundConfig
        {
            BaseUrl = "https://generativelanguage.googleapis.com",
            ApiKeyProvider = new StaticApiKeyProvider(""),
        });

        var hreq = outbound.TransformRequest(ChatRequest("hi"));

        Assert.Null(hreq.Metadata);
    }

    // ------------------------------------------------------------ buildFullRequestURL (outbound_test.go:150-253)

    [Theory]
    [InlineData("https://generativelanguage.googleapis.com", "v1beta", false,
        "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent")]
    [InlineData("https://generativelanguage.googleapis.com", "v1beta", true,
        "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse")]
    [InlineData("https://generativelanguage.googleapis.com", "v1", false,
        "https://generativelanguage.googleapis.com/v1/models/gemini-2.5-flash:generateContent")]
    [InlineData("https://generativelanguage.googleapis.com", "v1", true,
        "https://generativelanguage.googleapis.com/v1/models/gemini-2.5-flash:streamGenerateContent?alt=sse")]
    [InlineData("https://custom.api.com", "v1beta", false,
        "https://custom.api.com/v1beta/models/gemini-pro:generateContent")]
    public void BuildFullRequestUrl(string baseUrl, string apiVersion, bool stream, string expected)
    {
        var transformer = GeminiOutboundTransformer.CreateWithConfig(new GeminiOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiVersion = apiVersion,
        });

        Assert.Equal(expected, transformer.BuildFullRequestUrl(new LlmRequest
        {
            Model = baseUrl.Contains("custom.api.com", StringComparison.Ordinal) ? "gemini-pro" : "gemini-2.5-flash",
            Stream = stream,
        }));
    }

    [Fact]
    public void BuildFullRequestUrl_NilStreamDefaultsToNonStreaming()
    {
        var transformer = GeminiOutboundTransformer.CreateWithConfig(new GeminiOutboundConfig
        {
            BaseUrl = "https://generativelanguage.googleapis.com",
            ApiVersion = "v1beta",
        });

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
            transformer.BuildFullRequestUrl(new LlmRequest
            {
                Model = "gemini-2.5-flash",
                Stream = null,
            }));
    }

    [Fact]
    public void BuildFullRequestUrl_RawRequestWithoutVersionFallsBackToDefault()
    {
        var transformer = GeminiOutboundTransformer.CreateWithConfig(new GeminiOutboundConfig
        {
            BaseUrl = "https://generativelanguage.googleapis.com",
            ApiVersion = "", // Empty to trigger raw request lookup
        });

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
            transformer.BuildFullRequestUrl(new LlmRequest
            {
                Model = "gemini-2.5-flash",
                Stream = false,
                RawRequest = new HttpRequestData(),
            }));
    }

    // ------------------------------------------------------------ constructors (outbound_test.go:255-346)

    [Theory]
    [InlineData("https://generativelanguage.googleapis.com", "test-key")]
    [InlineData("", "test-key")]
    [InlineData("https://generativelanguage.googleapis.com", "")]
    [InlineData("", "")]
    public void NewOutboundTransformer(string baseUrl, string apiKey)
    {
        var transformer = GeminiOutboundTransformer.Create(baseUrl, apiKey);

        Assert.NotNull(transformer);
        Assert.Equal(ApiFormats.GeminiContents, transformer.ApiFormat);
    }

    [Fact]
    public void NewOutboundTransformerWithConfig()
    {
        GeminiOutboundConfig[] configs =
        [
            new()
            {
                BaseUrl = "https://generativelanguage.googleapis.com",
                ApiVersion = "v1beta",
            },
            new(),
            new()
            {
                BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            },
        ];

        foreach (var config in configs)
        {
            var transformer = GeminiOutboundTransformer.CreateWithConfig(config);

            Assert.NotNull(transformer);
            Assert.Equal(ApiFormats.GeminiContents, transformer.ApiFormat);
        }
    }

    // ------------------------------------------------------------ extra body thinking config (outbound_test.go:348-502)

    [Fact]
    public void TransformRequestWithExtraBody_IntegerBudget()
    {
        var httpReq = NewTransformer().TransformRequest(ChatRequest(
            "Hello",
            extraBody: JsonElement.Parse("""
                {
                    "google": {
                        "thinking_config": {
                            "thinking_budget": 8192,
                            "include_thoughts": true
                        }
                    }
                }
                """)));

        var geminiReq = LlmJson.Deserialize<GenerateContentRequest>(httpReq.Body!)!;
        var thinking = geminiReq.GenerationConfig!.ThinkingConfig!;

        Assert.Equal(8192, thinking.ThinkingBudget);
        Assert.Null(thinking.ThinkingLevel);
        Assert.True(thinking.IncludeThoughts);
    }

    [Fact]
    public void TransformRequestWithExtraBody_StringBudgetBecomesLevel()
    {
        var httpReq = NewTransformer().TransformRequest(ChatRequest(
            "Hello",
            extraBody: JsonElement.Parse("""
                {
                    "google": {
                        "thinking_config": {
                            "thinking_budget": "low",
                            "include_thoughts": true
                        }
                    }
                }
                """)));

        var geminiReq = LlmJson.Deserialize<GenerateContentRequest>(httpReq.Body!)!;
        var thinking = geminiReq.GenerationConfig!.ThinkingConfig!;

        Assert.Equal("low", thinking.ThinkingLevel);
        Assert.Null(thinking.ThinkingBudget);
        Assert.True(thinking.IncludeThoughts);
    }

    [Fact]
    public void TransformRequestWithExtraBody_BeatsReasoningEffort()
    {
        var httpReq = NewTransformer().TransformRequest(ChatRequest(
            "Hello",
            reasoningEffort: "high",
            extraBody: JsonElement.Parse("""
                {
                    "google": {
                        "thinking_config": {
                            "thinking_budget": 1024,
                            "include_thoughts": true
                        }
                    }
                }
                """)));

        var geminiReq = LlmJson.Deserialize<GenerateContentRequest>(httpReq.Body!)!;
        var thinking = geminiReq.GenerationConfig!.ThinkingConfig!;

        Assert.Equal(1024, thinking.ThinkingBudget);
        Assert.Null(thinking.ThinkingLevel);
        Assert.True(thinking.IncludeThoughts);
    }

    /// <summary>
    /// C113: Go feeds json.RawMessage(`{invalid`) straight through; the port's
    /// ExtraBody is a parsed JsonElement, so the graceful-no-error path is
    /// locked with a non-object element instead.
    /// </summary>
    [Fact]
    public void TransformRequestWithExtraBody_InvalidExtraBodyIsGraceful()
    {
        var httpReq = NewTransformer().TransformRequest(ChatRequest(
            "Hello",
            extraBody: JsonElement.Parse("42")));

        Assert.NotNull(httpReq);
        Assert.NotNull(httpReq.Body);
    }

    [Fact]
    public void TransformRequestWithExtraBody_EmptyIsGraceful()
    {
        var httpReq = NewTransformer().TransformRequest(ChatRequest(
            "Hello",
            extraBody: null));

        Assert.NotNull(httpReq);
        Assert.NotNull(httpReq.Body);
    }

    // ------------------------------------------------------------ reasoning effort fallback (outbound_test.go:504-538)

    [Fact]
    public void ReasoningEffortFallback()
    {
        var httpReq = NewTransformer().TransformRequest(ChatRequest(
            "Hello",
            reasoningEffort: "medium",
            extraBody: null));

        var geminiReq = LlmJson.Deserialize<GenerateContentRequest>(httpReq.Body!)!;
        var thinking = geminiReq.GenerationConfig!.ThinkingConfig!;

        Assert.NotNull(thinking);
        Assert.True(thinking.IncludeThoughts);
        Assert.Equal("medium", thinking.ThinkingLevel);
        Assert.Null(thinking.ThinkingBudget);
    }

    // ------------------------------------------------------------ TransformResponse multi tool calls (outbound_test.go:540-796)

    [Fact]
    public void TransformResponse_SingleFunctionCallHasIndex0()
    {
        var resp = TransformGeminiResponse(new GenerateContentResponse
        {
            ResponseId = "resp-single-tool",
            ModelVersion = "gemini-2.0-flash",
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
                            FunctionCallPart("call-1", "get_weather", new Dictionary<string, JsonElement>
                            {
                                ["location"] = JsonElement.Parse("\"Tokyo\""),
                            }),
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        });

        Assert.Single(resp.Choices);
        Assert.Single(resp.Choices[0].Message!.ToolCalls!);
        Assert.Equal(0, resp.Choices[0].Message!.ToolCalls![0].Index);
        Assert.Equal("call-1", resp.Choices[0].Message!.ToolCalls![0].Id);
        Assert.Equal("get_weather", resp.Choices[0].Message!.ToolCalls![0].Function.Name);
    }

    [Fact]
    public void TransformResponse_MultipleFunctionCallsHaveSequentialIndices()
    {
        var resp = TransformGeminiResponse(new GenerateContentResponse
        {
            ResponseId = "resp-multi-tool",
            ModelVersion = "gemini-2.0-flash",
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
                            FunctionCallPart("call-1", "get_weather", new Dictionary<string, JsonElement>
                            {
                                ["location"] = JsonElement.Parse("\"Tokyo\""),
                            }),
                            FunctionCallPart("call-2", "get_time", new Dictionary<string, JsonElement>
                            {
                                ["timezone"] = JsonElement.Parse("\"JST\""),
                            }),
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        });

        Assert.Single(resp.Choices);
        Assert.Equal(2, resp.Choices[0].Message!.ToolCalls!.Count);

        Assert.Equal(0, resp.Choices[0].Message!.ToolCalls![0].Index);
        Assert.Equal("call-1", resp.Choices[0].Message!.ToolCalls![0].Id);
        Assert.Equal("get_weather", resp.Choices[0].Message!.ToolCalls![0].Function.Name);

        Assert.Equal(1, resp.Choices[0].Message!.ToolCalls![1].Index);
        Assert.Equal("call-2", resp.Choices[0].Message!.ToolCalls![1].Id);
        Assert.Equal("get_time", resp.Choices[0].Message!.ToolCalls![1].Function.Name);
    }

    [Fact]
    public void TransformResponse_ThreeFunctionCallsHaveSequentialIndices()
    {
        var resp = TransformGeminiResponse(new GenerateContentResponse
        {
            ResponseId = "resp-three-tools",
            ModelVersion = "gemini-2.0-flash",
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
                            FunctionCallPart("call-a", "func_a", Args("param", "a")),
                            FunctionCallPart("call-b", "func_b", Args("param", "b")),
                            FunctionCallPart("call-c", "func_c", Args("param", "c")),
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        });

        Assert.Single(resp.Choices);
        Assert.Equal(3, resp.Choices[0].Message!.ToolCalls!.Count);

        Assert.Equal(0, resp.Choices[0].Message!.ToolCalls![0].Index);
        Assert.Equal("func_a", resp.Choices[0].Message!.ToolCalls![0].Function.Name);
        Assert.Equal(1, resp.Choices[0].Message!.ToolCalls![1].Index);
        Assert.Equal("func_b", resp.Choices[0].Message!.ToolCalls![1].Function.Name);
        Assert.Equal(2, resp.Choices[0].Message!.ToolCalls![2].Index);
        Assert.Equal("func_c", resp.Choices[0].Message!.ToolCalls![2].Function.Name);
    }

    [Fact]
    public void TransformResponse_FunctionCallsWithTextContentMixed()
    {
        var resp = TransformGeminiResponse(new GenerateContentResponse
        {
            ResponseId = "resp-mixed",
            ModelVersion = "gemini-2.0-flash",
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
                            new Part { Text = "I'll help you with that." },
                            FunctionCallPart("call-1", "search", Args("query", "weather")),
                            FunctionCallPart("call-2", "calculate", Args("expr", "1+1")),
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        });

        Assert.Single(resp.Choices);
        Assert.Equal("I'll help you with that.", resp.Choices[0].Message!.Content!.Content);

        Assert.Equal(2, resp.Choices[0].Message!.ToolCalls!.Count);
        Assert.Equal(0, resp.Choices[0].Message!.ToolCalls![0].Index);
        Assert.Equal("search", resp.Choices[0].Message!.ToolCalls![0].Function.Name);
        Assert.Equal(1, resp.Choices[0].Message!.ToolCalls![1].Index);
        Assert.Equal("calculate", resp.Choices[0].Message!.ToolCalls![1].Function.Name);
    }

    [Fact]
    public void TransformResponse_FunctionCallWithoutIdGetsGeneratedUuid()
    {
        var resp = TransformGeminiResponse(new GenerateContentResponse
        {
            ResponseId = "resp-no-id",
            ModelVersion = "gemini-2.0-flash",
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
                            FunctionCallPart(null, "get_data", Args("key", "value")),
                            FunctionCallPart(null, "process_data", Args("data", "test")),
                        ],
                    },
                    FinishReason = "STOP",
                },
            ],
        });

        Assert.Single(resp.Choices);
        Assert.Equal(2, resp.Choices[0].Message!.ToolCalls!.Count);

        Assert.NotEmpty(resp.Choices[0].Message!.ToolCalls![0].Id!);
        Assert.NotEmpty(resp.Choices[0].Message!.ToolCalls![1].Id!);

        Assert.Equal(0, resp.Choices[0].Message!.ToolCalls![0].Index);
        Assert.Equal(1, resp.Choices[0].Message!.ToolCalls![1].Index);
    }

    private static LlmResponse TransformGeminiResponse(GenerateContentResponse geminiResp)
    {
        var httpResp = new HttpResponseData
        {
            StatusCode = 200,
            Body = GoJson.Marshal(geminiResp),
        };

        return NewTransformer().TransformResponse(httpResp);
    }

    private static Part FunctionCallPart(string? id, string name, Dictionary<string, JsonElement> args) => new()
    {
        FunctionCall = new GeminiFunctionCall
        {
            Id = id,
            Name = name,
            Args = args,
        },
    };

    private static Dictionary<string, JsonElement> Args(string key, string value)
        => new() { [key] = JsonElement.Parse($"\"{value}\"") };

    // ------------------------------------------------------------ TestClearFunctionIDsForVertexAI (outbound_test.go:798-850)

    [Fact]
    public void ClearFunctionIDsForVertexAI()
    {
        var req = new GenerateContentRequest
        {
            Contents =
            [
                new Content
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
                                Args = new Dictionary<string, JsonElement>
                                {
                                    ["city"] = JsonElement.Parse("\"Paris\""),
                                },
                            },
                        },
                        new Part { Text = "Some text" },
                    ],
                },
                new Content
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
                                Response = new Dictionary<string, JsonElement>
                                {
                                    ["temperature"] = JsonElement.Parse("22"),
                                },
                            },
                        },
                    ],
                },
            ],
        };

        // Verify IDs are present before clearing
        Assert.Equal("call_123", req.Contents![0].Parts![0].FunctionCall!.Id);
        Assert.Equal("call_123", req.Contents[1].Parts![0].FunctionResponse!.Id);

        // Clear IDs
        GeminiOutboundTransformer.ClearFunctionIDsForVertexAI(req);

        // Verify IDs are cleared
        Assert.Null(req.Contents![0].Parts![0].FunctionCall!.Id);
        Assert.Null(req.Contents[1].Parts![0].FunctionResponse!.Id);

        // Verify other fields are preserved
        Assert.Equal("get_weather", req.Contents![0].Parts![0].FunctionCall!.Name);
        Assert.Equal("get_weather", req.Contents[1].Parts![0].FunctionResponse!.Name);

        // Verify JSON serialization omits ID fields
        var jsonBytes = GoJson.Marshal(req);
        Assert.DoesNotContain("\"id\":", Encoding.UTF8.GetString(jsonBytes));
    }
}
